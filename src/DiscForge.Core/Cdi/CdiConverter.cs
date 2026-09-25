// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
using DiscForge.Core.Cue;

namespace DiscForge.Core.Convert;

/// <summary>
/// Converts between CDI and BIN/CUE. Design choice: preserve each track's stored
/// sector size and emit the matching CUE token (MODE1/2048, MODE2/2336, etc.),
/// one BIN per track (multi-FILE CUE). This is faithful and needs no ECC/EDC
/// synthesis — we never fabricate sync/header/parity bytes we don't have.
///
/// Known limitation: BIN/CUE has no clean multisession model, so session
/// boundaries and absolute LBAs (e.g. a Dreamcast Audio/Data gap at LBA 45000)
/// are NOT preserved through a CUE round-trip. Track content, mode, sector size,
/// pregap and ordering are preserved. This mirrors why CDI existed in the first
/// place — round-trip within a single session is lossless; across sessions, use
/// CDI as the container. Multisession round-trips emit a warning list.
/// </summary>
public static class CdiConverter
{
    public sealed record BinCueResult(string CueText, IReadOnlyList<string> BinFilenames,
                                      IReadOnlyList<string> Warnings);

    /// <summary>
    /// CDI -> BIN/CUE. Writes one BIN per track into <paramref name="outputDir"/>
    /// using <paramref name="baseName"/>, returns the CUE text (also written to
    /// baseName.cue) and any warnings.
    /// </summary>
    public static BinCueResult CdiToBinCue(Stream cdi, CdiImage image, string outputDir, string baseName)
    {
        Directory.CreateDirectory(outputDir);
        var cueTracks = new List<CueTrack>();
        var binNames = new List<string>();
        var warnings = new List<string>();

        if (image.Sessions.Count > 1)
            warnings.Add($"Image has {image.Sessions.Count} sessions; BIN/CUE cannot " +
                         "represent multisession layout. Absolute LBAs/session gaps " +
                         "will not survive the round-trip. Track data is preserved.");

        foreach (var t in image.AllTracks)
        {
            string binName = $"{baseName}_track{t.Number:D2}.bin";
            binNames.Add(binName);
            var binPath = Path.Combine(outputDir, binName);

            // Write the track's content sectors (pregap excluded) at stored size.
            using (var bin = File.Create(binPath))
            {
                var contentTrack = t with { PregapSectors = 0, TotalSectors = t.LengthSectors,
                                            FileOffset = t.FileOffset + (long)t.PregapSectors * (int)t.SectorSize };
                CdiExtractor.ExtractRaw(cdi, contentTrack, bin);
            }

            var type = TrackType(t.Mode, t.SectorSize);
            var indices = new List<CueIndex> { new(1, Msf.FromSectors(0)) };
            Msf? pregap = t.PregapSectors > 0 ? Msf.FromSectors(t.PregapSectors) : null;

            cueTracks.Add(new CueTrack
            {
                Number = t.Number, Type = type, File = binName,
                Pregap = pregap, Indices = indices,
            });
        }

        var sheet = new CueSheet { Tracks = cueTracks };
        var cueText = sheet.Write();
        File.WriteAllText(Path.Combine(outputDir, $"{baseName}.cue"), cueText);
        return new BinCueResult(cueText, binNames, warnings);
    }

    /// <summary>
    /// BIN/CUE -> CDI. Reads the CUE (and its BIN files, resolved relative to
    /// <paramref name="cueDir"/>) and writes a CDI of the given version.
    ///
    /// Multisession-aware: when the cue carries Redump-style "REM SESSION n"
    /// markers (a Dreamcast MIL-CD self-boot CD-ROM is a two-session disc — a
    /// low-density first session then a high-density session that opens the
    /// bootable game area), the tracks are grouped into those sessions and the
    /// absolute LBAs step across the standard inter-session gap. A cue with no
    /// session markers produces one session exactly as before.
    ///
    /// This faithfully preserves a disc's *existing* session layout in the CDI
    /// container. It does not synthesise self-boot capability (IP.BIN / bootstrap)
    /// a source disc did not already have.
    /// </summary>
    public static void BinCueToCdi(string cueText, string cueDir, CdiVersion version, Stream output,
                                   uint interSessionGap = MultisessionGap)
    {
        var sheet = CueSheet.Parse(cueText);
        var sessions = new List<IReadOnlyList<CdiWriter.TrackInput>>();
        uint lba = 0;

        // Sessions in ascending order; tracks keep their cue order within a session.
        var sessionNumbers = sheet.Tracks.Select(t => t.Session).Distinct().OrderBy(n => n).ToList();
        bool firstSession = true;
        foreach (var sessionNo in sessionNumbers)
        {
            // Between sessions the disc has a lead-out + lead-in the program area
            // skips over, so the next session's absolute LBAs jump by that gap.
            if (!firstSession) lba += interSessionGap;
            firstSession = false;

            var inputs = new List<CdiWriter.TrackInput>();
            foreach (var ct in sheet.Tracks.Where(t => t.Session == sessionNo))
            {
                var (mode, size) = TypeToModeSize(ct.Type);
                int sectorBytes = (int)size;

                var binPath = Path.Combine(cueDir, ct.File);
                var info = new FileInfo(binPath);
                if (!info.Exists)
                    throw new FileNotFoundException($"{ct.File}: referenced by the cue sheet but not found.", binPath);

                long fileLength = info.Length;
                if (fileLength % sectorBytes != 0)
                    throw new InvalidDataException(
                        $"{ct.File}: length {fileLength} not a multiple of sector size {sectorBytes}.");

                uint lengthSectors = (uint)(fileLength / sectorBytes);
                uint pregap = ct.Pregap is { } pg ? (uint)pg.ToSectors() : 0;

                // CDI stores the pregap, so emit zero pregap sectors then stream the
                // BIN through. Streaming keeps memory flat regardless of track size —
                // the old path read the whole BIN and then copied it again to prepend
                // the pregap, needing twice the track's size in RAM.
                long pregapBytes = (long)pregap * sectorBytes;
                string capturedPath = binPath;

                inputs.Add(new CdiWriter.TrackInput
                {
                    Mode = mode, SectorSize = size,
                    PregapSectors = pregap, LengthSectors = lengthSectors,
                    StartLba = lba, Filename = ct.File,
                    DataWriter = os =>
                    {
                        WriteZeros(os, pregapBytes);
                        using var src = new FileStream(capturedPath, FileMode.Open, FileAccess.Read,
                                                       FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                        src.CopyTo(os, 1 << 16);
                    },
                });
                lba += pregap + lengthSectors;
            }
            sessions.Add(inputs);
        }

        CdiWriter.Write(output, version, sessions);
    }

    /// <summary>
    /// Standard CD inter-session gap in sectors — the step from one session's last
    /// track to the next session's first-track data: first-session lead-out (6750,
    /// 01:30:00) + next-session lead-in (4500, 01:00:00) + the 150-sector (00:02:00)
    /// track pregap = 11400. This matches how DiscImageCreator/Redump lay out a
    /// multisession CD (lead-out + lead-in are 11250, and the data begins 150 sectors
    /// further in). It is added to the running LBA before a new session, so a
    /// session-2 track that carries no explicit PREGAP starts at exactly this offset;
    /// overridable via the gap parameter when a specific rip needs a different value.
    /// </summary>
    public const uint MultisessionGap = 11400;

    private static CueTrackType TrackType(CdiTrackMode mode, CdiSectorSize size) =>
        (mode, size) switch
        {
            (CdiTrackMode.Audio, _) => CueTrackType.Audio,
            (CdiTrackMode.Mode1, CdiSectorSize.S2048) => CueTrackType.Mode1_2048,
            (CdiTrackMode.Mode1, CdiSectorSize.S2352) => CueTrackType.Mode1_2352,
            (CdiTrackMode.Mode2, CdiSectorSize.S2336) => CueTrackType.Mode2_2336,
            (CdiTrackMode.Mode2, CdiSectorSize.S2352) => CueTrackType.Mode2_2352,
            _ => throw new NotSupportedException($"No CUE mapping for {mode}/{size}."),
        };

    private static (CdiTrackMode mode, CdiSectorSize size) TypeToModeSize(CueTrackType t) =>
        t switch
        {
            CueTrackType.Audio => (CdiTrackMode.Audio, CdiSectorSize.S2352),
            CueTrackType.Mode1_2048 => (CdiTrackMode.Mode1, CdiSectorSize.S2048),
            CueTrackType.Mode1_2352 => (CdiTrackMode.Mode1, CdiSectorSize.S2352),
            CueTrackType.Mode2_2336 => (CdiTrackMode.Mode2, CdiSectorSize.S2336),
            CueTrackType.Mode2_2352 => (CdiTrackMode.Mode2, CdiSectorSize.S2352),
            _ => throw new ArgumentOutOfRangeException(nameof(t)),
        };

    private static void WriteZeros(Stream s, long count)
    {
        if (count <= 0) return;
        var chunk = new byte[Math.Min(count, 1 << 16)];
        while (count > 0)
        {
            int n = (int)Math.Min(count, chunk.Length);
            s.Write(chunk, 0, n);
            count -= n;
        }
    }
}
