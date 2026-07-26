// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;

namespace DiscForge.Core.Reading;

/// <summary>Raised when a disc can't be read faithfully with the given drive.</summary>
public sealed class ReadNotSupportedException(string message) : Exception(message);

/// <summary>
/// What a track's sectors actually are, as read from a sector header rather than
/// inferred from the TOC. The TOC's control nibble distinguishes audio from data
/// and nothing else, so this is the only way to know.
/// </summary>
public enum TrackSectorMode
{
    /// <summary>Not probed, or the sector didn't classify. Plan conservatively.</summary>
    Unknown,
    Audio,
    Mode1,
    /// <summary>2048 bytes of user data plus ECC — cooked reads are fine.</summary>
    Mode2Form1,
    /// <summary>2324 bytes of user data, NO ECC and no 2048-byte field. Cooked
    /// reads of these are impossible, not merely lossy.</summary>
    Mode2Form2,
}

/// <summary>How to pull one track off the disc.</summary>
public sealed record ReadTrackPlan
{
    public required int Number { get; init; }
    public required uint StartLba { get; init; }
    public required uint LengthSectors { get; init; }
    /// <summary>Mode to record in the resulting image.</summary>
    public required CdiTrackMode Mode { get; init; }
    /// <summary>Bytes per sector to request from the drive and store.</summary>
    public required CdiSectorSize SectorSize { get; init; }
    public required bool IsAudio { get; init; }
    /// <summary>What the sector header said, if it was probed.</summary>
    public TrackSectorMode Detected { get; init; } = TrackSectorMode.Unknown;

    public long StoredBytes => (long)LengthSectors * (int)SectorSize;
}

/// <summary>A validated plan for reading a whole disc.</summary>
public sealed record ReadPlan
{
    public required IReadOnlyList<ReadTrackPlan> Tracks { get; init; }
    public required bool RawMode { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>True when the disc cannot be read cooked at all, so the UI should
    /// not offer it. Set when a track is audio or Mode 2 Form 2.</summary>
    public bool RawRequired { get; init; }

    public long TotalBytes => Tracks.Sum(t => t.StoredBytes);
    public uint TotalSectors => (uint)Tracks.Sum(t => (long)t.LengthSectors);
}

/// <summary>
/// Decides how to read a disc, from its TOC plus what the drive can do. Pure —
/// no hardware, fully unit-testable, which is the whole point: the risky part of
/// ripping is deciding what to ask for, not the asking. Sector modes are probed
/// by the caller (see TrackModeProber) and passed in, so this stays testable.
///
/// Two strategies:
///  - Cooked (default): data tracks at 2048 bytes/sector via a normal read.
///    Simple, fast, works on every drive — but discards subchannel and any
///    non-standard sector content.
///  - Raw: every track at 2352 bytes/sector. Preserves audio exactly and keeps
///    Mode 2 form data intact. Needs a drive that will hand over raw sectors.
///
/// Two things force raw, for the same underlying reason — there is no 2048-byte
/// user-data field to serve:
///  - Audio. CD-DA sectors have no header or ECC at all.
///  - Mode 2 Form 2. Its 2324-byte payload has no 2048-byte form, so a cooked
///    read isn't lossy, it's rejected outright ("illegal mode for this track").
///    SVCD and Video CD are built this way, as are many CD-i and PSX titles.
/// </summary>
public static class ReadPlanner
{
    public static ReadPlan Plan(DiscToc toc, DriveCapabilities drive, bool preferRaw = false,
                                IReadOnlyDictionary<int, TrackSectorMode>? detected = null)
    {
        ArgumentNullException.ThrowIfNull(toc);
        ArgumentNullException.ThrowIfNull(drive);

        if (toc.Tracks.Count == 0)
            throw new ReadNotSupportedException("The disc reports no tracks.");

        // Check the capability that matches the media actually present — a
        // DVD-only drive reading a DVD shouldn't be refused for lacking CD read.
        bool canRead = drive.MediaProfile switch
        {
            MmcProfile.None => drive.CdRead || drive.DvdRead || drive.BdRead,
            MmcProfile.CdRom or MmcProfile.CdR or MmcProfile.CdRw => drive.CdRead,
            MmcProfile.BdRom or MmcProfile.BdRSrm or MmcProfile.BdRRrm or MmcProfile.BdRe => drive.BdRead,
            _ => drive.DvdRead,
        };
        if (!canRead)
            throw new ReadNotSupportedException(
                $"{drive.Vendor} {drive.Model} does not report the read capability needed for " +
                $"{(drive.MediaProfile == MmcProfile.None ? "this media" : drive.MediaProfile.ToString())}.");

        var warnings = new List<string>();

        // Raw 2352-byte sectors are a CD concept. DVD and BD sectors are always
        // 2048 bytes with no raw form, so a raw read is rejected by the drive
        // ("Illegal request: invalid field in CDB"). Catch that here rather than
        // letting the user discover it at LBA 0.
        if (drive.MediaIsDvdOrBd)
        {
            if (preferRaw)
                throw new ReadNotSupportedException(
                    $"Raw reading is a CD-only concept, and the media in the drive is " +
                    $"{drive.MediaProfile}. DVD and Blu-ray sectors are always 2048 bytes — " +
                    "untick raw to read this disc.");

            if (toc.HasAudio)
                warnings.Add("The TOC flags an audio track on non-CD media, which is unusual; " +
                             "reading everything as 2048-byte data.");

            var dvdTracks = new List<ReadTrackPlan>();
            foreach (var t in toc.Tracks)
            {
                if (t.LengthSectors == 0)
                {
                    warnings.Add($"Track {t.Number} has zero length in the TOC; skipped.");
                    continue;
                }
                dvdTracks.Add(new ReadTrackPlan
                {
                    Number = t.Number,
                    StartLba = t.StartLba,
                    LengthSectors = t.LengthSectors,
                    Mode = CdiTrackMode.Mode1,
                    SectorSize = CdiSectorSize.S2048,
                    IsAudio = false,
                });
            }
            if (dvdTracks.Count == 0)
                throw new ReadNotSupportedException("No readable tracks on the disc.");

            return new ReadPlan { Tracks = dvdTracks, RawMode = false, Warnings = warnings };
        }

        TrackSectorMode DetectedFor(int number) =>
            detected is not null && detected.TryGetValue(number, out var m)
                ? m : TrackSectorMode.Unknown;

        // Which tracks physically cannot be served as 2048-byte user data?
        var form2Tracks = new List<int>();
        foreach (var t in toc.Tracks)
            if (!t.IsAudio && t.LengthSectors > 0 && DetectedFor(t.Number) == TrackSectorMode.Mode2Form2)
                form2Tracks.Add(t.Number);

        // Audio can only be read raw: CD-DA sectors have no header/EDC to strip.
        // Mode 2 Form 2 is the same problem wearing a different hat.
        bool needRaw = toc.HasAudio || form2Tracks.Count > 0;
        bool raw = needRaw || preferRaw;

        if (toc.HasAudio && !preferRaw)
            warnings.Add("Disc contains audio tracks, so every track is read raw (2352 bytes/sector).");

        if (form2Tracks.Count > 0)
        {
            string which = form2Tracks.Count == 1
                ? $"Track {form2Tracks[0]} is"
                : $"Tracks {string.Join(", ", form2Tracks)} are";
            warnings.Add($"{which} Mode 2 Form 2, which has no 2048-byte user-data field — " +
                         "the disc is read raw (2352 bytes/sector). This is normal for " +
                         "SVCD, Video CD and CD-i titles.");
        }

        if (toc.IsMixedMode)
            warnings.Add("Mixed-mode disc (audio + data): writing this back needs a RAW-capable burner.");

        // A Mode 2 disc read cooked loses the sub-header, so form and channel
        // information doesn't survive. Fine for plain data, worth saying out loud.
        if (!raw)
        {
            foreach (var t in toc.Tracks)
                if (DetectedFor(t.Number) == TrackSectorMode.Mode2Form1)
                {
                    warnings.Add("Mode 2 tracks are being read cooked: the 8-byte sub-header " +
                                 "is discarded. Read raw to preserve it.");
                    break;
                }
        }

        var tracks = new List<ReadTrackPlan>();
        foreach (var t in toc.Tracks)
        {
            if (t.LengthSectors == 0)
            {
                warnings.Add($"Track {t.Number} has zero length in the TOC; skipped.");
                continue;
            }

            var det = DetectedFor(t.Number);

            // Record what the sectors actually are, so the image's metadata is
            // true rather than merely plausible. Unknown falls back to Mode 1,
            // which is what this did for every disc before probing existed.
            var cdiMode = t.IsAudio
                ? CdiTrackMode.Audio
                : det switch
                {
                    TrackSectorMode.Mode2Form1 or TrackSectorMode.Mode2Form2 => CdiTrackMode.Mode2,
                    _ => CdiTrackMode.Mode1,
                };

            var size = (t.IsAudio, raw) switch
            {
                (true, _) => CdiSectorSize.S2352,        // audio is always raw
                (false, true) => CdiSectorSize.S2352,    // data, raw
                (false, false) => CdiSectorSize.S2048,   // data, cooked
            };

            if (t.PreEmphasis)
                warnings.Add($"Track {t.Number} is flagged pre-emphasis; the flag is recorded, audio is unchanged.");

            tracks.Add(new ReadTrackPlan
            {
                Number = t.Number,
                StartLba = t.StartLba,
                LengthSectors = t.LengthSectors,
                Mode = cdiMode,
                SectorSize = size,
                IsAudio = t.IsAudio,
                Detected = det,
            });
        }

        if (tracks.Count == 0)
            throw new ReadNotSupportedException("No readable tracks on the disc.");

        return new ReadPlan
        {
            Tracks = tracks,
            RawMode = raw,
            Warnings = warnings,
            RawRequired = needRaw,
        };
    }
}