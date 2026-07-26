// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiscForge.Core.Burning;
using DiscForge.Core.Cdi;

namespace DiscForge.Devices.Burning;

/// <summary>
/// Standard data-disc burning via IMAPI2 (Image Mastering API v2), the built-in
/// Windows optical-write stack. Handles CD/DVD/BD data burns with NO kernel
/// driver and NO RAW/subchannel work — the everyday path that "just works" on
/// modern drives. Byte-faithful RAW/mixed/multisession burns are the separate
/// SPTI engine (Phase 4b).
///
/// This engine burns a single data track's cooked user data as a raw disc image
/// stream via IMAPI2's RawCD/Data writer. It drives COM objects by ProgID, so it
/// needs no compile-time IMAPI2 interop assembly.
///
/// Status: structural implementation complete; the COM call sequence is the
/// documented IMAPI2 flow. Must be exercised on real Windows hardware — like the
/// SPTI layer, it cannot be run in CI. Progress and error propagation are wired
/// so a failed burn surfaces as an exception, never a false success.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Imapi2BurnEngine : IBurnEngine
{
    public bool Supports(BurnMethod method) => method == BurnMethod.Imapi2Data;

    public void Burn(Stream cdi, CdiImage image, BurnPlan plan,
                     IProgress<BurnProgress>? progress = null)
    {
        if (plan.Method != BurnMethod.Imapi2Data)
            throw new NotSupportedException("Imapi2BurnEngine handles only Imapi2Data plans.");
        if (image.TrackCount != 1)
            throw new NotSupportedException("IMAPI2 data path expects a single data track.");

        var track = image.AllTracks.Single();

        progress?.Report(new BurnProgress("prepare", 0.0, "Cooking user data"));

        // Extract the cooked ISO/user data to a temp file (IMAPI2 streams from it).
        var tmp = Path.Combine(Path.GetTempPath(), "ojug_burn_" + Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            using (var iso = File.Create(tmp))
                CdiExtractor.ExtractUserData(cdi, track, iso);

            BurnDataImage(plan, tmp, progress);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Burn a plain .iso straight to disc.
    ///
    /// An ISO already IS the cooked user data IMAPI2 wants, so unlike the CDI
    /// path there's nothing to extract — no temp copy, no second 4.7 GB on disk,
    /// and no wait before the burn starts.
    /// </summary>
    public void BurnIso(string isoPath, BurnPlan plan, IProgress<BurnProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(isoPath);
        if (plan.Method != BurnMethod.Imapi2Data)
            throw new NotSupportedException(
                "A plain ISO is a single data track and is written by the IMAPI2 data path.");

        var info = new FileInfo(isoPath);
        if (!info.Exists)
            throw new FileNotFoundException("ISO not found.", isoPath);
        if (info.Length == 0)
            throw new InvalidDataException($"'{info.Name}' is empty.");
        if (info.Length % 2048 != 0)
            throw new InvalidDataException(
                $"'{info.Name}' is {info.Length:N0} bytes, which is not a whole number of " +
                "2048-byte sectors. It may be truncated, or it may be a raw BIN rather than " +
                "an ISO.");

        progress?.Report(new BurnProgress("prepare", 0.0,
            $"{info.Name}, {info.Length / (1024.0 * 1024.0):N1} MB"));

        BurnDataImage(plan, isoPath, progress);
    }

    private static void BurnDataImage(BurnPlan plan, string isoPath,
                                      IProgress<BurnProgress>? progress)
    {
        string devicePath = plan.DevicePath;
        // --- IMAPI2 COM flow (by ProgID; late-bound) ---
        // 1. MsftDiscMaster2       -> enumerate recorders, find the one matching devicePath
        // 2. MsftDiscRecorder2     -> InitializeDiscRecorder(uniqueId)
        // 3. MsftDiscFormat2Data   -> Recorder = recorder; write the image stream
        //
        // We resolve the recorder whose VolumePathNames contains our drive letter.
        Type masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2")
            ?? throw new InvalidOperationException("IMAPI2 not available on this system.");
        dynamic master = Activator.CreateInstance(masterType)!;

        try
        {
            if (master.Count == 0)
                throw new InvalidOperationException("No optical recorders found by IMAPI2.");

            string wantLetter = ExtractDriveLetter(devicePath);
            string? uniqueId = null;

            Type recType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!;
            for (int i = 0; i < master.Count; i++)
            {
                string id = master.Item(i);
                dynamic rec = Activator.CreateInstance(recType)!;
                rec.InitializeDiscRecorder(id);
                foreach (string vol in rec.VolumePathNames)
                {
                    if (!string.IsNullOrEmpty(vol) &&
                        char.ToUpperInvariant(vol[0]) == wantLetter[0])
                    {
                        uniqueId = id;
                        break;
                    }
                }
                Marshal.FinalReleaseComObject(rec);
                if (uniqueId is not null) break;
            }

            if (uniqueId is null)
                throw new InvalidOperationException(
                    $"No IMAPI2 recorder matches {devicePath}.");

            dynamic recorder = Activator.CreateInstance(recType)!;
            recorder.InitializeDiscRecorder(uniqueId);

            Type dataType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data")!;
            dynamic format = Activator.CreateInstance(dataType)!;
            format.Recorder = recorder;
            format.ClientName = "DiscForge";

            // Check the media BEFORE writing. IMAPI2 will otherwise throw a bare
            // "The requested operation is only valid with supported media"
            // (0xC0AA0402) that says nothing about which of several very
            // different problems you have: wrong media type, a disc that isn't
            // blank, or one that's too small.
            CheckMedia(format, isoPath, progress);

            // Apply a requested write speed. SetWriteSpeed wants IMAPI2's
            // sectors-per-second unit; the drive snaps to its closest supported
            // speed. A refusal here must not kill the burn — worst case the
            // disc writes at max, which is what would have happened anyway.
            if (plan.WriteSpeedSectorsPerSecond is int sps)
            {
                try
                {
                    format.SetWriteSpeed(sps, false);   // false = not pure CAV
                    progress?.Report(new BurnProgress("prepare", 0.09,
                        $"Write speed requested: {sps} sectors/s " +
                        $"(drive chose {(int)format.CurrentWriteSpeed})"));
                }
                catch (COMException)
                {
                    progress?.Report(new BurnProgress("prepare", 0.09,
                        "The drive rejected the speed request — burning at its default (max)"));
                }
            }

            // Progress via the Update event would require a sink; we report phase
            // boundaries synchronously to keep this dependency-free.
            progress?.Report(new BurnProgress("burn", 0.1, "Opening data stream"));

            // Wrap the ISO file as an IStream for IMAPI2.
            dynamic stream = CreateComStreamFromFile(isoPath);

            progress?.Report(new BurnProgress("burn", 0.2, "Writing to disc"));
            try
            {
                format.Write(stream);   // blocks until done or throws on failure
            }
            catch (COMException ex)
            {
                throw new IOException(DescribeImapiError(ex), ex);
            }
            progress?.Report(new BurnProgress("finalize", 1.0, "Burn complete"));

            Marshal.FinalReleaseComObject(format);
            Marshal.FinalReleaseComObject(recorder);
        }
        finally
        {
            Marshal.FinalReleaseComObject(master);
        }
    }

    /// <summary>
    /// Interrogate the disc before writing a byte, and say plainly what's wrong.
    ///
    /// Every one of these conditions otherwise surfaces as the same opaque
    /// 0xC0AA0402 "only valid with supported media" — which sends people hunting
    /// a software bug when the actual answer is "that disc already has data on it".
    /// </summary>
    private static void CheckMedia(dynamic format, string isoPath, IProgress<BurnProgress>? progress)
    {
        progress?.Report(new BurnProgress("prepare", 0.05, "Checking the disc"));

        bool supported;
        try { supported = format.IsCurrentMediaSupported(format.Recorder); }
        catch (COMException ex)
        {
            throw new IOException(
                "Could not read the disc in the drive. " + DescribeImapiError(ex), ex);
        }

        if (!supported)
            throw new IOException(
                "The disc in the drive can't be written by this drive via the data path. " +
                "It may be a non-writable disc (a pressed CD/DVD, or a finalised write-once " +
                "disc that already has data), or a media type this drive doesn't support. " +
                "Insert a blank disc.");

        // MediaHeuristicallyBlank is IMAPI2's own judgement, and the one it acts on.
        bool blank = true;
        try { blank = format.MediaHeuristicallyBlank; }
        catch { /* older stacks may not expose it — fall through to the write */ }

        if (!blank)
        {
            string kind = "";
            try { kind = " (" + DescribeMediaType((int)format.CurrentPhysicalMediaType) + ")"; }
            catch { /* best effort */ }

            throw new IOException(
                $"The disc in the drive is not blank{kind}. DiscForge will not overwrite it. " +
                "Insert a blank disc — or, for a rewritable one (CD-RW / DVD-RW / DVD+RW / " +
                "BD-RE), erase it first.");
        }

        // Capacity: better a refusal now than a coaster three minutes in.
        try
        {
            long sectorsFree = (long)(int)format.FreeSectorsOnMedia;
            long needed = new FileInfo(isoPath).Length / 2048;
            if (sectorsFree > 0 && needed > sectorsFree)
                throw new IOException(
                    $"The image needs {needed:N0} sectors ({needed * 2048.0 / 1024 / 1024 / 1024:N2} GB) " +
                    $"but the disc holds {sectorsFree:N0} ({sectorsFree * 2048.0 / 1024 / 1024 / 1024:N2} GB). " +
                    "Use larger media.");
        }
        catch (IOException) { throw; }
        catch { /* property unavailable: let the burn find out */ }

        progress?.Report(new BurnProgress("prepare", 0.08, "Disc is blank and writable"));
    }

    /// <summary>Turn IMAPI2's HRESULTs into something actionable.</summary>
    private static string DescribeImapiError(COMException ex) => (uint)ex.HResult switch
    {
        0xC0AA0402 => "The drive rejected this media. It's usually a disc that isn't blank, " +
                      "is write-once and already finalised, or is a type this drive can't write. " +
                      "Insert a blank disc.",
        0xC0AA0403 => "The disc is not blank. Erase it (if rewritable) or use a blank one.",
        0xC0AA0404 => "The media is write-protected.",
        0xC0AA0405 => "There isn't enough space on the disc for this image.",
        0xC0AA0202 => "The drive is in use by another program. Close anything else using it.",
        0xC0AA0203 => "The drive reported a hardware failure.",
        0xC0AA0301 => "No disc in the drive.",
        0xC0AA0302 => "The drive tray is open.",
        _ => $"IMAPI2 error 0x{(uint)ex.HResult:X8}: {ex.Message.Trim()}",
    };

    private static string DescribeMediaType(int type) => type switch
    {
        1 => "CD-ROM", 2 => "CD-R", 3 => "CD-RW",
        4 => "DVD-ROM", 5 => "DVD-RAM", 6 => "DVD+R", 7 => "DVD+RW",
        8 => "DVD+R DL", 9 => "DVD-R", 10 => "DVD-RW", 11 => "DVD-R DL",
        12 => "DVD-RW DL", 13 => "HD DVD-ROM", 14 => "HD DVD-R", 15 => "HD DVD-RAM",
        16 => "BD-ROM", 17 => "BD-R", 18 => "BD-RE",
        _ => $"media type {type}",
    };

    private static string ExtractDriveLetter(string devicePath)
    {
        // Accept "\\.\E:" or "E:" or "E".
        foreach (char c in devicePath)
            if (char.IsLetter(c)) return char.ToUpperInvariant(c).ToString();
        throw new ArgumentException($"No drive letter in '{devicePath}'.");
    }

    private static object CreateComStreamFromFile(string path)
    {
        // SHCreateStreamOnFileEx gives an IStream over the file for IMAPI2.Write.
        const uint STGM_READ = 0x0;
        int hr = SHCreateStreamOnFileEx(path, STGM_READ, 0, false, IntPtr.Zero, out object stream);
        if (hr != 0) throw Marshal.GetExceptionForHR(hr)!;
        return stream;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(
        string pszFile, uint grfMode, uint dwAttributes, bool fCreate,
        IntPtr pstmTemplate, [MarshalAs(UnmanagedType.Interface)] out object ppstm);
}
