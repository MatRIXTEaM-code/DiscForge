// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;

namespace DiscForge.Core.Cdi;

/// <summary>What a track's descriptor claims versus what its sectors actually are.</summary>
public sealed record TrackModeFinding
{
    public required int TrackNumber { get; init; }
    public required CdiTrackMode Declared { get; init; }
    /// <summary>Null when the sectors could not be classified — an audio track,
    /// a cooked 2048 track, or damage. Not an error: it means "no opinion".</summary>
    public CdiTrackMode? Actual { get; init; }
    /// <summary>Offset in the file of the descriptor's 32-bit mode field.</summary>
    public required long ModeFieldOffset { get; init; }
    public required string Detail { get; init; }

    public bool NeedsRepair => Actual is not null && Actual != Declared;
}

public sealed record CdiModeRepairReport
{
    public required IReadOnlyList<TrackModeFinding> Findings { get; init; }
    public required bool DescriptorLayoutVerified { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public int RepairsNeeded => Findings.Count(f => f.NeedsRepair);
    public bool AnyRepairNeeded => RepairsNeeded > 0;
}

/// <summary>
/// Repairs the track mode recorded in a CDI descriptor by comparing it against
/// what the stored sectors actually contain.
///
/// Why this exists: a CDI's descriptor states each track's mode, and every
/// consumer trusts it — <see cref="CdiExtractor.UserDataWindow"/> uses it to
/// decide whether user data starts at offset 16 (Mode 1) or 24 (Mode 2, after
/// the 8-byte sub-header). An image whose descriptor says Mode 1 for what are
/// really Mode 2 sectors therefore reads every sector eight bytes early: the
/// filesystem won't parse, extraction produces rubbish, and nothing announces
/// the problem. DiscForge wrote exactly such images before track modes were
/// probed from the disc, and the media on them is often no longer to hand.
///
/// The fix is small and surgical: each mode field is a single 32-bit value in
/// the descriptor, so a wrong one is four bytes to correct in place. Track data
/// is never touched, and neither is anything else in the descriptor.
///
/// Safety: the descriptor's track entries are variable-length (they embed a
/// filename), so the mode field's position has to be found by walking. Before
/// any write, the value found at the computed offset is checked against what
/// <see cref="CdiParser"/> reported for that track. If they disagree the layout
/// assumption is wrong for this file and the repair refuses to proceed rather
/// than writing four bytes into the middle of something else.
/// </summary>
public static class CdiModeRepair
{
    /// <summary>Sectors sampled per track before deciding its true mode. One
    /// sector could be an anomaly; a handful agreeing is evidence.</summary>
    private const int SamplesPerTrack = 8;

    /// <summary>
    /// Examine an image without modifying it. Safe to run on anything.
    /// </summary>
    public static CdiModeRepairReport Analyse(Stream cdi)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        if (!cdi.CanSeek) throw new ArgumentException("A seekable stream is required.", nameof(cdi));

        var image = CdiParser.Parse(cdi);
        var notes = new List<string>();

        var offsets = FindModeFieldOffsets(cdi, image, notes, out bool verified);
        var findings = new List<TrackModeFinding>();

        var tracks = image.AllTracks.ToList();
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            long fieldOffset = i < offsets.Count ? offsets[i] : -1;

            var (actual, detail) = ClassifyTrack(cdi, t);
            findings.Add(new TrackModeFinding
            {
                TrackNumber = i + 1,
                Declared = t.Mode,
                Actual = actual,
                ModeFieldOffset = fieldOffset,
                Detail = detail,
            });
        }

        return new CdiModeRepairReport
        {
            Findings = findings,
            DescriptorLayoutVerified = verified,
            Notes = notes,
        };
    }

    /// <summary>
    /// Correct any mismatched mode fields. The stream must be writable. Returns
    /// the number of tracks patched. Refuses to write anything unless the
    /// descriptor layout was verified against the parser first.
    /// </summary>
    public static int Repair(Stream cdi, out CdiModeRepairReport report)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        if (!cdi.CanWrite) throw new ArgumentException("A writable stream is required.", nameof(cdi));

        report = Analyse(cdi);

        if (!report.DescriptorLayoutVerified)
            throw new InvalidOperationException(
                "The descriptor's layout could not be verified against the parsed tracks, so " +
                "the mode fields cannot be located with confidence. Nothing has been changed. " +
                "This image was probably not written by DiscForge — re-read the disc instead.");

        int patched = 0;
        var buf = new byte[4];

        foreach (var f in report.Findings)
        {
            if (!f.NeedsRepair || f.ModeFieldOffset < 0) continue;

            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)f.Actual!.Value);
            cdi.Seek(f.ModeFieldOffset, SeekOrigin.Begin);
            cdi.Write(buf, 0, 4);
            patched++;
        }

        cdi.Flush();
        return patched;
    }

    /// <summary>
    /// Read a track's sectors and say what mode they really are.
    ///
    /// Only raw 2352-byte tracks can be classified: they carry a sync pattern and
    /// a header byte that state the mode outright. A 2336 track is Mode 2 by
    /// definition of its size, and a 2048 track has been cooked — its sectors no
    /// longer contain the evidence. Both are left alone.
    /// </summary>
    private static (CdiTrackMode? Mode, string Detail) ClassifyTrack(Stream cdi, CdiTrack track)
    {
        if (track.SectorSize != CdiSectorSize.S2352)
            return (null, $"stored as {(int)track.SectorSize}-byte sectors — the mode cannot be " +
                          "read back from cooked data, so it is left as declared");

        if (track.Mode == CdiTrackMode.Audio)
            return (null, "audio track — no sector header to classify");

        int sectorBytes = (int)track.SectorSize;
        long contentStart = track.FileOffset + (long)track.PregapSectors * sectorBytes;

        var sector = new byte[sectorBytes];
        var votes = new Dictionary<CdiTrackMode, int>();
        int classified = 0, unreadable = 0;

        // Spread the samples across the track: the first sector of a track can be
        // atypical, and a single bad read shouldn't decide the verdict.
        uint step = Math.Max(1u, track.LengthSectors / SamplesPerTrack);

        for (int i = 0; i < SamplesPerTrack; i++)
        {
            uint lba = (uint)i * step;
            if (lba >= track.LengthSectors) break;

            long pos = contentStart + (long)lba * sectorBytes;
            if (pos + sectorBytes > cdi.Length) break;

            cdi.Seek(pos, SeekOrigin.Begin);
            try { cdi.ReadExactly(sector, 0, sectorBytes); }
            catch (EndOfStreamException) { unreadable++; continue; }

            var m = ClassifySector(sector);
            if (m is null) { unreadable++; continue; }

            votes[m.Value] = votes.GetValueOrDefault(m.Value) + 1;
            classified++;
        }

        if (classified == 0)
            return (null, unreadable > 0
                ? "no sector carried a readable sync pattern — the track may be audio or damaged"
                : "no sectors available to sample");

        var best = votes.OrderByDescending(kv => kv.Value).First();

        // Disagreement within a track is worth reporting rather than silently
        // taking the majority: a genuinely mixed track is unusual and the user
        // should know before four bytes get rewritten on that basis.
        if (votes.Count > 1)
        {
            var breakdown = string.Join(", ", votes.Select(kv => $"{kv.Key}x{kv.Value}"));
            return (best.Key, $"sectors disagree ({breakdown}) — taking the majority, " +
                              "but inspect this image before trusting it");
        }

        return (best.Key, $"{classified} sector(s) sampled, all {best.Key}");
    }

    /// <summary>
    /// Classify one raw 2352-byte sector from its own header. The sync pattern
    /// must be present or the bytes at offset 15 mean nothing.
    /// </summary>
    public static CdiTrackMode? ClassifySector(ReadOnlySpan<byte> raw2352)
    {
        if (raw2352.Length < 16) return null;

        // Sync: 00 FF FF FF FF FF FF FF FF FF FF 00
        if (raw2352[0] != 0x00 || raw2352[11] != 0x00) return null;
        for (int i = 1; i <= 10; i++)
            if (raw2352[i] != 0xFF) return null;

        return (raw2352[15] & 0x03) switch
        {
            1 => CdiTrackMode.Mode1,
            2 => CdiTrackMode.Mode2,
            _ => null,          // Mode 0 (empty) or nonsense: no opinion
        };
    }

    /// <summary>
    /// Walk the descriptor to find each track's mode field, mirroring the layout
    /// CdiWriter produces. Every computed offset is then checked against the mode
    /// the parser reported; if any disagrees, the whole set is rejected.
    /// </summary>
    private static List<long> FindModeFieldOffsets(Stream cdi, CdiImage image,
                                                   List<string> notes, out bool verified)
    {
        verified = false;
        var offsets = new List<long>();

        long descriptorStart = FindDescriptorStart(cdi, notes);
        if (descriptorStart < 0) return offsets;

        try
        {
            cdi.Seek(descriptorStart, SeekOrigin.Begin);
            using var reader = new BinaryReader(cdi, System.Text.Encoding.ASCII, leaveOpen: true);

            ushort sessionCount = reader.ReadUInt16();
            if (sessionCount == 0 || sessionCount > 99)
            {
                notes.Add($"Descriptor declares {sessionCount} session(s), which is implausible — " +
                          "the layout was not recognised.");
                return [];
            }

            for (int s = 0; s < sessionCount; s++)
            {
                ushort trackCount = reader.ReadUInt16();
                if (trackCount > 99)
                {
                    notes.Add($"Session {s + 1} declares {trackCount} track(s) — layout not recognised.");
                    return [];
                }

                for (int t = 0; t < trackCount; t++)
                {
                    cdi.Seek(4, SeekOrigin.Current);        // lead-in
                    cdi.Seek(20, SeekOrigin.Current);       // two 10-byte marks
                    cdi.Seek(4, SeekOrigin.Current);        // reserved0

                    byte fnLen = reader.ReadByte();
                    cdi.Seek(fnLen, SeekOrigin.Current);    // filename

                    cdi.Seek(8, SeekOrigin.Current);        // pregap + length sectors

                    offsets.Add(cdi.Position);              // the mode field
                    cdi.Seek(4, SeekOrigin.Current);        // mode

                    cdi.Seek(16, SeekOrigin.Current);       // lba, total, size code, reserved1
                }
                cdi.Seek(4, SeekOrigin.Current);            // session tail
            }
        }
        catch (EndOfStreamException)
        {
            notes.Add("The descriptor ended unexpectedly while walking track entries.");
            return [];
        }

        // The decisive check: does each computed offset actually hold the mode
        // the parser reported? If not, this file's layout differs from what the
        // walk assumes and nothing may be written.
        var tracks = image.AllTracks.ToList();
        if (offsets.Count != tracks.Count)
        {
            notes.Add($"Found {offsets.Count} mode field(s) but the parser reported " +
                      $"{tracks.Count} track(s) — the layout was not recognised.");
            return [];
        }

        var buf = new byte[4];
        for (int i = 0; i < offsets.Count; i++)
        {
            cdi.Seek(offsets[i], SeekOrigin.Begin);
            cdi.ReadExactly(buf, 0, 4);
            uint atOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            if (atOffset != (uint)tracks[i].Mode)
            {
                notes.Add($"Track {i + 1}: the value at the computed mode offset " +
                          $"({atOffset}) does not match the parsed mode ({(uint)tracks[i].Mode}). " +
                          "The descriptor layout is not what was assumed.");
                return [];
            }
        }

        verified = true;
        return offsets;
    }

    /// <summary>
    /// Locate the descriptor from the 8-byte trailer. V3.5 stores the distance
    /// back from EOF; earlier versions store an absolute offset.
    /// </summary>
    private static long FindDescriptorStart(Stream cdi, List<string> notes)
    {
        if (cdi.Length < 8) { notes.Add("File is too short to be a CDI image."); return -1; }

        var trailer = new byte[8];
        cdi.Seek(-8, SeekOrigin.End);
        cdi.ReadExactly(trailer, 0, 8);

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(0, 4));
        uint locator = BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4, 4));

        long start = version == (uint)CdiVersion.V35
            ? cdi.Length - locator
            : locator;

        if (start < 0 || start >= cdi.Length)
        {
            notes.Add($"The trailer points to offset {start}, which is outside the file.");
            return -1;
        }
        return start;
    }
}