// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiscForge.Core.Burning;

namespace DiscForge.Devices.Burning;

/// <summary>
/// Media housekeeping via IMAPI2: erasing rewritable discs and asking a drive
/// which write speeds it supports for the media currently loaded. Both use the
/// same late-bound COM style as <see cref="Imapi2BurnEngine"/> — ProgID +
/// dynamic, no compile-time interop assembly.
///
/// Erase uses IMAPI2's MsftDiscFormat2Erase, which handles CD-RW, DVD-RW,
/// DVD+RW and BD-RE. Quick erase blanks the lead-in/TOC so the disc reads as
/// empty (seconds); full erase overwrites the whole surface (tens of minutes,
/// but the honest option for media that's been struggling, and for privacy).
///
/// Speeds come from MsftDiscFormat2Data.SupportedWriteSpeeds and are ONLY
/// meaningful for the disc in the drive right now: the same burner reports
/// different speed sets for a CD-R and a DVD+R, and reports nothing without
/// media. IMAPI2's unit is sectors per second (CD 1x = 75, DVD 1x ≈ 677,
/// BD 1x ≈ 2195).
/// </summary>
[SupportedOSPlatform("windows")]
public static class Imapi2MediaTools
{
    // 1x rates in 2048-byte sectors per second, per media family. These are
    // the display divisors for turning IMAPI2's sectors/sec into the familiar
    // "8x" labels; the drive itself only ever sees sectors/sec.
    private const double CdOneX = 75.0;      // 150 KB/s
    private const double DvdOneX = 676.7;    // 1,385 KB/s
    private const double BdOneX = 2195.3;    // ~4.5 MB/s

    // ---------------------------------------------------------------- erase

    /// <summary>
    /// Erase the rewritable disc in the drive. Blocks until done.
    /// Throws IOException with a plain-language message on anything IMAPI2
    /// refuses — non-rewritable media, no disc, drive busy.
    /// </summary>
    /// <param name="full">
    /// False = quick erase (blank the TOC, seconds). True = full erase
    /// (overwrite everything, can take as long as a burn).
    /// </param>
    public static void Erase(string devicePath, bool full,
                             IProgress<BurnProgress>? progress = null)
    {
        progress?.Report(new BurnProgress("prepare", 0.0,
            full ? "Full erase — this overwrites the whole disc and takes a while"
                 : "Quick erase"));

        Type masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2")
            ?? throw new InvalidOperationException("IMAPI2 not available on this system.");
        dynamic master = Activator.CreateInstance(masterType)!;
        try
        {
            string? uniqueId = FindRecorderId(master, devicePath)
                ?? throw new InvalidOperationException($"No IMAPI2 recorder matches {devicePath}.");

            Type recType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!;
            dynamic recorder = Activator.CreateInstance(recType)!;
            try
            {
                recorder.InitializeDiscRecorder(uniqueId);

                Type eraseType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Erase")
                    ?? throw new InvalidOperationException("IMAPI2 erase interface unavailable.");
                dynamic erase = Activator.CreateInstance(eraseType)!;
                try
                {
                    erase.Recorder = recorder;
                    erase.ClientName = "DiscForge";

                    bool supported;
                    try { supported = erase.IsCurrentMediaSupported(recorder); }
                    catch (COMException ex)
                    {
                        throw new IOException(
                            "Could not read the disc in the drive. " + Describe(ex), ex);
                    }
                    if (!supported)
                        throw new IOException(
                            "The disc in the drive can't be erased. Only rewritable media " +
                            "(CD-RW, DVD-RW, DVD+RW, BD-RE) can be blanked — write-once " +
                            "discs (CD-R, DVD±R, BD-R) can't be undone.");

                    erase.FullErase = full;

                    progress?.Report(new BurnProgress("erase", 0.1,
                        full ? "Erasing (full)" : "Erasing (quick)"));
                    try
                    {
                        erase.EraseMedia();   // blocks until done or throws
                    }
                    catch (COMException ex)
                    {
                        throw new IOException("Erase failed. " + Describe(ex), ex);
                    }
                    progress?.Report(new BurnProgress("erase", 1.0, "Erase complete — the disc is blank"));
                }
                finally { Marshal.FinalReleaseComObject(erase); }
            }
            finally { Marshal.FinalReleaseComObject(recorder); }
        }
        finally { Marshal.FinalReleaseComObject(master); }
    }

    // ---------------------------------------------------------------- speeds

    /// <summary>
    /// What one drive can do with the media it currently holds: the media name
    /// ("DVD+R") and the supported write speeds, fastest first, in IMAPI2's
    /// sectors-per-second unit. <see cref="DescribeSpeed"/> renders one as the
    /// familiar "8x (11.1 MB/s)".
    /// </summary>
    public sealed record WriteSpeedReport(
        string MediaName,
        int MediaType,
        IReadOnlyList<int> SectorsPerSecond)
    {
        /// <summary>"16x (22.2 MB/s)" — the X factor is media-relative.</summary>
        public string DescribeSpeed(int sectorsPerSecond)
        {
            double oneX = MediaType switch
            {
                >= 16 => BdOneX,             // BD-ROM/R/RE
                >= 4  => DvdOneX,            // DVD/HD DVD families
                _     => CdOneX,             // CD families
            };
            double x = sectorsPerSecond / oneX;
            double mbps = sectorsPerSecond * 2048.0 / (1000.0 * 1000.0);
            return $"{x:0.#}x ({mbps:0.#} MB/s)";
        }
    }

    /// <summary>
    /// Ask the drive which write speeds it supports for the loaded media.
    /// Returns null when there's no usable answer (no disc, non-writable disc,
    /// or a stack that doesn't expose speeds) — callers treat null as "offer
    /// only Max", never as an error: speed choice is a nicety, not a gate.
    /// </summary>
    public static WriteSpeedReport? GetWriteSpeeds(string devicePath)
    {
        Type? masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2");
        if (masterType is null) return null;
        dynamic master = Activator.CreateInstance(masterType)!;
        try
        {
            string? uniqueId = FindRecorderId(master, devicePath);
            if (uniqueId is null) return null;

            Type recType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!;
            dynamic recorder = Activator.CreateInstance(recType)!;
            try
            {
                recorder.InitializeDiscRecorder(uniqueId);

                Type dataType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data")!;
                dynamic format = Activator.CreateInstance(dataType)!;
                try
                {
                    format.Recorder = recorder;
                    format.ClientName = "DiscForge";

                    int mediaType;
                    try { mediaType = (int)format.CurrentPhysicalMediaType; }
                    catch { return null; }                      // no disc

                    var speeds = new List<int>();
                    try
                    {
                        foreach (object s in format.SupportedWriteSpeeds)
                        {
                            int sps = System.Convert.ToInt32(s);
                            if (sps > 0 && !speeds.Contains(sps)) speeds.Add(sps);
                        }
                    }
                    catch { /* property missing or empty — fall through */ }

                    if (speeds.Count == 0) return null;
                    speeds.Sort((a, b) => b.CompareTo(a));      // fastest first

                    return new WriteSpeedReport(
                        MediaName(mediaType), mediaType, speeds);
                }
                finally { Marshal.FinalReleaseComObject(format); }
            }
            finally { Marshal.FinalReleaseComObject(recorder); }
        }
        catch
        {
            // Speed listing must never break drive detection or burning.
            return null;
        }
        finally { Marshal.FinalReleaseComObject(master); }
    }

    // ---------------------------------------------------------------- shared

    /// <summary>Resolve the IMAPI2 recorder unique ID for a drive path/letter.</summary>
    internal static string? FindRecorderId(dynamic master, string devicePath)
    {
        char want = char.ToUpperInvariant(devicePath.FirstOrDefault(char.IsLetter));
        if (want == default) return null;

        Type recType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!;
        for (int i = 0; i < master.Count; i++)
        {
            string id = master.Item(i);
            dynamic probe = Activator.CreateInstance(recType)!;
            try
            {
                probe.InitializeDiscRecorder(id);
                foreach (string vol in probe.VolumePathNames)
                    if (!string.IsNullOrEmpty(vol) && char.ToUpperInvariant(vol[0]) == want)
                        return id;
            }
            catch { /* an uninitialisable recorder is not ours */ }
            finally { Marshal.FinalReleaseComObject(probe); }
        }
        return null;
    }

    private static string MediaName(int type) => type switch
    {
        1 => "CD-ROM", 2 => "CD-R", 3 => "CD-RW",
        4 => "DVD-ROM", 5 => "DVD-RAM", 6 => "DVD+R", 7 => "DVD+RW",
        8 => "DVD+R DL", 9 => "DVD-R", 10 => "DVD-RW", 11 => "DVD-R DL",
        12 => "DVD-RW DL", 13 => "HD DVD-ROM", 14 => "HD DVD-R", 15 => "HD DVD-RAM",
        16 => "BD-ROM", 17 => "BD-R", 18 => "BD-RE",
        _ => $"media type {type}",
    };

    private static string Describe(COMException ex) => (uint)ex.HResult switch
    {
        0xC0AA0402 => "The disc in the drive isn't a type this operation supports.",
        0xC0AA0404 => "The media is write-protected.",
        0xC0AA0202 => "The drive is in use by another program. Close anything else using it.",
        0xC0AA0203 => "The drive reported a hardware failure.",
        0xC0AA0301 => "No disc in the drive.",
        0xC0AA0302 => "The drive tray is open.",
        _ => $"IMAPI2 error 0x{(uint)ex.HResult:X8}: {ex.Message.Trim()}",
    };
}
