// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiscForge.Core.Burning;
using DiscForge.Core.Cdi;

namespace DiscForge.Devices.Burning;

/// <summary>
/// Burns an audio CD track-at-once through IMAPI2's
/// <c>MsftDiscFormat2TrackAtOnce</c> — the same path Windows itself uses, which
/// means it works on ANY CD writer. No RAW DAO, no vintage Plextor required.
///
/// TAO's limitation is honest and specific: the recorder places the standard
/// two-second gap between tracks. It cannot reproduce a gapless mix or a source
/// disc's exact gaps — those need RAW DAO. The planner refuses such images here
/// rather than letting TAO silently standardise them.
///
/// Each audio track is handed over as raw 2352-byte PCM (44.1 kHz, 16-bit,
/// stereo), streamed from the image so a full CD never lands in memory.
///
/// NOTE: never run against real hardware in development. The COM sequence
/// follows the documented IMAPI2 interfaces, but the first real burn is the real
/// test — and a bad burn costs a disc.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Imapi2TrackAtOnceBurnEngine : IBurnEngine
{
    public bool Supports(BurnMethod method) => method == BurnMethod.Imapi2TrackAtOnce;

    public void Burn(Stream cdi, CdiImage image, BurnPlan plan,
                     IProgress<BurnProgress>? progress = null)
    {
        if (plan.Method != BurnMethod.Imapi2TrackAtOnce)
            throw new NotSupportedException(
                "Imapi2TrackAtOnceBurnEngine handles only Imapi2TrackAtOnce plans.");

        var tracks = image.AllTracks.ToList();
        if (tracks.Count == 0)
            throw new NotSupportedException("The image has no tracks.");
        if (tracks.Any(t => t.Mode != CdiTrackMode.Audio))
            throw new NotSupportedException(
                "The track-at-once path writes audio CDs; this image contains data tracks.");
        if (tracks.Count > 99)
            throw new NotSupportedException("A CD holds at most 99 tracks.");

        progress?.Report(new BurnProgress("prepare", 0.0, $"{tracks.Count} audio track(s)"));

        // Stage each track's PCM to a temp file. IMAPI2 wants an IStream per
        // track, and staging keeps memory flat for a full 800 MB disc.
        var staged = new List<string>(tracks.Count);
        try
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                var tmp = Path.Combine(Path.GetTempPath(),
                    $"ojug_tao_{Guid.NewGuid():N}_{i + 1:D2}.pcm");
                staged.Add(tmp);

                using var os = File.Create(tmp);
                // Audio is stored raw at 2352 bytes/sector; the pregap is not
                // written here — TAO's recorder lays the gaps down itself.
                WriteTrackAudio(cdi, track, os);

                progress?.Report(new BurnProgress("prepare",
                    (i + 1) / (double)tracks.Count * 0.2,
                    $"staged track {i + 1}/{tracks.Count}"));
            }

            BurnTracks(plan, staged, progress);
        }
        finally
        {
            foreach (var f in staged)
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Copy a track's raw 2352-byte audio (skipping any stored pregap).</summary>
    private static void WriteTrackAudio(Stream cdi, CdiTrack track, Stream output)
    {
        int sectorBytes = (int)track.SectorSize;
        long start = track.FileOffset + (long)track.PregapSectors * sectorBytes;
        long remaining = (long)track.LengthSectors * sectorBytes;

        cdi.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[1 << 16];
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int n = cdi.Read(buffer, 0, want);
            if (n <= 0)
                throw new EndOfStreamException(
                    $"Track {track.Number} ends {remaining:N0} bytes early in the image.");
            output.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    private static void BurnTracks(BurnPlan plan, List<string> pcmFiles,
                                   IProgress<BurnProgress>? progress)
    {
        string devicePath = plan.DevicePath;
        // COM sequence (all late-bound so the project needs no IMAPI2 interop
        // assembly, matching Imapi2BurnEngine):
        //   1. MsftDiscMaster2                -> find the recorder for devicePath
        //   2. MsftDiscRecorder2              -> InitializeDiscRecorder
        //   3. MsftDiscFormat2TrackAtOnce     -> Recorder, PrepareMedia,
        //                                        AddAudioTrack per track, ReleaseMedia
        object? master = null, recorder = null, format = null;
        try
        {
            Type masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2")
                ?? throw new NotSupportedException("IMAPI2 is not available on this system.");
            master = Activator.CreateInstance(masterType)!;

            string? uniqueId = FindRecorderId(master, devicePath)
                ?? throw new IOException($"No IMAPI2 recorder matches '{devicePath}'.");

            Type recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!;
            recorder = Activator.CreateInstance(recorderType)!;
            Invoke(recorder, "InitializeDiscRecorder", uniqueId);

            Type taoType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2TrackAtOnce")
                ?? throw new NotSupportedException(
                    "IMAPI2 track-at-once (audio) is not available on this system.");
            format = Activator.CreateInstance(taoType)!;

            SetProperty(format, "Recorder", recorder);
            SetProperty(format, "ClientName", "DiscForge");

            progress?.Report(new BurnProgress("burn", 0.2, "preparing media"));
            Invoke(format, "PrepareMedia");

            // IDiscFormat2TrackAtOnce::SetWriteSpeed is only valid AFTER
            // PrepareMedia — hence the ordering here differs from the data
            // engine. As there, a speed refusal downgrades to max, never fails.
            if (plan.WriteSpeedSectorsPerSecond is int sps)
            {
                try
                {
                    Invoke(format, "SetWriteSpeed", sps, false);
                    progress?.Report(new BurnProgress("burn", 0.2,
                        $"write speed requested: {sps} sectors/s"));
                }
                catch
                {
                    progress?.Report(new BurnProgress("burn", 0.2,
                        "the drive rejected the speed request — burning at its default (max)"));
                }
            }

            try
            {
                for (int i = 0; i < pcmFiles.Count; i++)
                {
                    progress?.Report(new BurnProgress("burn",
                        0.2 + (i / (double)pcmFiles.Count) * 0.8,
                        $"writing track {i + 1}/{pcmFiles.Count}"));

                    var stream = CreateComStream(pcmFiles[i]);
                    try
                    {
                        // The recorder inserts the standard two-second gap.
                        Invoke(format, "AddAudioTrack", stream);
                    }
                    finally
                    {
                        if (stream is not null && Marshal.IsComObject(stream))
                            Marshal.ReleaseComObject(stream);
                    }
                }
            }
            finally
            {
                // Always release the media: leaving a disc open mid-burn is how
                // coasters are made.
                try { Invoke(format, "ReleaseMedia"); } catch { /* already failing */ }
            }

            progress?.Report(new BurnProgress("burn", 1.0, "audio CD written"));
        }
        finally
        {
            Release(format);
            Release(recorder);
            Release(master);
        }
    }

    private static string? FindRecorderId(object master, string devicePath)
    {
        int count = (int)GetProperty(master, "Count")!;
        for (int i = 0; i < count; i++)
        {
            var id = (string)Invoke(master, "get_Item", i)!;

            Type recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!;
            object? probe = Activator.CreateInstance(recorderType);
            try
            {
                Invoke(probe!, "InitializeDiscRecorder", id);
                if (GetProperty(probe!, "VolumePathNames") is string[] paths)
                {
                    // devicePath looks like \\.\D: ; volume paths look like D:\
                    foreach (var p in paths)
                    {
                        if (p.Length >= 2 && devicePath.Contains(p[0] + ":",
                                StringComparison.OrdinalIgnoreCase))
                            return id;
                    }
                }
            }
            catch { /* try the next recorder */ }
            finally { Release(probe); }
        }
        return null;
    }

    /// <summary>Wrap a file as a COM IStream for IMAPI2.</summary>
    private static object CreateComStream(string path)
    {
        // SHCreateStreamOnFileEx gives IMAPI2 a real IStream over the staged PCM.
        int hr = SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_WRITE,
                                        0, false, IntPtr.Zero, out var stream);
        if (hr != 0 || stream is null)
            throw new IOException($"Could not open '{Path.GetFileName(path)}' as a stream (0x{hr:X8}).");
        return stream;
    }

    private const int STGM_READ = 0x00000000;
    private const int STGM_SHARE_DENY_WRITE = 0x00000020;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(
        string pszFile, int grfMode, int dwAttributes, bool fCreate,
        IntPtr pstmTemplate, [MarshalAs(UnmanagedType.Interface)] out object ppstm);

    // ---- late-bound COM helpers -------------------------------------------

    private static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(method,
            System.Reflection.BindingFlags.InvokeMethod, null, target, args);

    private static object? GetProperty(object target, string name) =>
        target.GetType().InvokeMember(name,
            System.Reflection.BindingFlags.GetProperty, null, target, null);

    private static void SetProperty(object target, string name, object? value) =>
        target.GetType().InvokeMember(name,
            System.Reflection.BindingFlags.SetProperty, null, target, new[] { value });

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.ReleaseComObject(comObject); } catch { /* best effort */ }
        }
    }
}
