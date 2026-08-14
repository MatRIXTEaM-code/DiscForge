// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Globalization;

namespace DiscForge.Core.Convert;

/// <summary>
/// Reads the CloneCD control file (<c>.ccd</c>) — the inverse of
/// <see cref="CloneCdWriter"/> — so DiscForge can consume CloneCD images
/// (<c>.ccd</c> + <c>.img</c> + optional <c>.sub</c>) as well as produce them.
/// This broadens DiscForge's format coverage into CloneCD's ecosystem and lets
/// its own <c>.ccd</c> output round-trip.
///
/// The <c>.ccd</c> is an INI-style text file: a [CloneCD] version stanza, a
/// [Disc] block with the TOC entry count and session count, [Session N] blocks,
/// and one [Entry N] block per TOC point (A0 first-track, A1 last-track,
/// A2 lead-out, plus each track) carrying its ADR/Control and MSF/PLBA. This
/// reader parses that structure into a <see cref="CcdToc"/> — a plain,
/// validated description of the disc's table of contents.
///
/// It reads a table of contents; it is a data format, not a protection
/// mechanism, and nothing here decrypts anything.
/// </summary>
public static class CloneCdReader
{
    public sealed record CcdEntry
    {
        public required int Session { get; init; }
        public required int Point { get; init; }        // 0x01..0x63 track, 0xA0/A1/A2 special
        public required int Adr { get; init; }
        public required int Control { get; init; }
        public required int PMin { get; init; }
        public required int PSec { get; init; }
        public required int PFrame { get; init; }
        public required int PLba { get; init; }
        public bool IsTrack => Point is >= 0x01 and <= 0x63;
        public bool IsLeadOut => Point == 0xA2;
    }

    public sealed record CcdTrack
    {
        public required int Number { get; init; }
        public required int StartLba { get; init; }
        public required int Control { get; init; }
        public bool IsData => (Control & 0x4) != 0;
        public string? Isrc { get; init; }
    }

    public sealed record CcdToc
    {
        public required int Version { get; init; }
        public required int SessionCount { get; init; }
        public required IReadOnlyList<CcdEntry> Entries { get; init; }
        public required IReadOnlyList<CcdTrack> Tracks { get; init; }
        public int FirstTrack { get; init; }
        public int LastTrack { get; init; }
        public int LeadOutLba { get; init; }
        public string? Catalog { get; init; }

        public string Summary =>
            $"CloneCD v{Version}: {SessionCount} session(s), {Tracks.Count} track(s), " +
            $"lead-out at LBA {LeadOutLba}.";
    }

    /// <summary>Parse a .ccd control file's text into a structured TOC.</summary>
    public static CcdToc Parse(string ccdText)
    {
        // Group the INI into sections: section name → key/value map. CloneCD keys
        // are case-insensitive; values are decimal or 0x-prefixed hex.
        var sections = new List<(string Name, Dictionary<string, string> Kv)>();
        Dictionary<string, string>? current = null;

        foreach (var rawLine in ccdText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add((line[1..^1], current));
            }
            else if (current is not null)
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                    current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        if (sections.Count == 0)
            throw new CcdFormatException("Not a CloneCD control file — no INI sections found.");

        var disc = Find(sections, "Disc");
        var clone = Find(sections, "CloneCD");
        if (disc is null)
            throw new CcdFormatException("CloneCD control file has no [Disc] section.");

        int version = clone is not null && clone.TryGetValue("Version", out var v) ? ParseInt(v) : 0;
        int sessionCount = disc.TryGetValue("Sessions", out var sc) ? ParseInt(sc) : 1;
        string? catalog = disc.TryGetValue("CATALOG", out var cat) ? cat : null;

        // Collect [Entry N] sections and [TRACK N] ISRC sections.
        var entries = new List<CcdEntry>();
        var isrcByTrack = new Dictionary<int, string>();

        foreach (var (name, kv) in sections)
        {
            if (name.StartsWith("Entry", StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new CcdEntry
                {
                    Session = GetInt(kv, "Session", 1),
                    Point = GetInt(kv, "Point", 0),
                    Adr = GetInt(kv, "ADR", 0),
                    Control = GetInt(kv, "Control", 0),
                    PMin = GetInt(kv, "PMin", 0),
                    PSec = GetInt(kv, "PSec", 0),
                    PFrame = GetInt(kv, "PFrame", 0),
                    PLba = GetInt(kv, "PLBA", 0),
                });
            }
            else if (name.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                // [TRACK n] blocks carry ISRC and per-track MODE in some writers.
                int num = ParseTrailingInt(name);
                if (kv.TryGetValue("ISRC", out var isrc) && !string.IsNullOrWhiteSpace(isrc))
                    isrcByTrack[num] = isrc;
            }
        }

        // Derive the track list from the track-point entries.
        var tracks = new List<CcdTrack>();
        int firstTrack = 0, lastTrack = 0, leadOut = 0;
        foreach (var e in entries.OrderBy(e => e.Point))
        {
            switch (e.Point)
            {
                case 0xA0: firstTrack = e.PMin; break;
                case 0xA1: lastTrack = e.PMin; break;
                case 0xA2: leadOut = e.PLba != 0 ? e.PLba : MsfToLba(e.PMin, e.PSec, e.PFrame); break;
                default:
                    if (e.IsTrack)
                    {
                        int startLba = e.PLba != 0 || (e.PMin == 0 && e.PSec == 0 && e.PFrame == 0)
                            ? e.PLba
                            : MsfToLba(e.PMin, e.PSec, e.PFrame);
                        tracks.Add(new CcdTrack
                        {
                            Number = e.Point,
                            StartLba = startLba,
                            Control = e.Control,
                            Isrc = isrcByTrack.TryGetValue(e.Point, out var isrc) ? isrc : null,
                        });
                    }
                    break;
            }
        }

        if (tracks.Count == 0)
            throw new CcdFormatException("CloneCD control file contains no track entries.");

        return new CcdToc
        {
            Version = version,
            SessionCount = sessionCount,
            Entries = entries,
            Tracks = tracks.OrderBy(t => t.Number).ToList(),
            FirstTrack = firstTrack != 0 ? firstTrack : tracks.Min(t => t.Number),
            LastTrack = lastTrack != 0 ? lastTrack : tracks.Max(t => t.Number),
            LeadOutLba = leadOut,
            Catalog = catalog,
        };
    }

    /// <summary>Read and parse a .ccd file from disk.</summary>
    public static CcdToc ReadFile(string ccdPath) => Parse(File.ReadAllText(ccdPath));

    /// <summary>The .img/.sub paths that accompany a .ccd (same stem).</summary>
    public static (string Img, string Sub) SidecarsFor(string ccdPath)
    {
        var dir = Path.GetDirectoryName(ccdPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(ccdPath);
        return (Path.Combine(dir, stem + ".img"), Path.Combine(dir, stem + ".sub"));
    }

    // ---- image / subchannel extraction --------------------------------------
    //
    // A CloneCD .img holds the raw 2352-byte main channel, one sector per LBA,
    // starting at LBA 0. The optional .sub holds the raw 96-byte sub-channel in
    // the same sector order. Track data therefore lives at a fixed byte offset —
    // this reads it out using the layout the .ccd already gives us. It copies
    // bytes; nothing here decodes or decrypts anything.

    /// <summary>Bytes per sector in a CloneCD .img (raw main channel).</summary>
    public const int ImgSectorBytes = 2352;

    /// <summary>Bytes per sector in a CloneCD .sub (raw P-W sub-channel).</summary>
    public const int SubSectorBytes = 96;

    /// <summary>
    /// How many sectors a track occupies: from its start LBA to the next track's
    /// start (or the lead-out for the last track).
    /// </summary>
    public static int TrackSectorCount(CcdToc toc, CcdTrack track)
    {
        ArgumentNullException.ThrowIfNull(toc);
        ArgumentNullException.ThrowIfNull(track);

        int next = toc.LeadOutLba;
        foreach (var t in toc.Tracks)
            if (t.StartLba > track.StartLba && t.StartLba < next)
                next = t.StartLba;

        int count = next - track.StartLba;
        if (count < 0)
            throw new CcdFormatException(
                $"Track {track.Number} starts at LBA {track.StartLba}, after the lead-out {toc.LeadOutLba}.");
        return count;
    }

    /// <summary>
    /// Copy a track's raw sectors from the .img into <paramref name="output"/>.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public static long ExtractTrack(CcdToc toc, CcdTrack track, Stream img, Stream output)
        => CopySectors(img, output, track.StartLba, TrackSectorCount(toc, track), ImgSectorBytes,
                       track.Number, "image (.img)");

    /// <summary>
    /// Copy a track's raw 96-byte sub-channel sectors from the .sub into
    /// <paramref name="output"/>.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public static long ReadSubchannel(CcdToc toc, CcdTrack track, Stream sub, Stream output)
        => CopySectors(sub, output, track.StartLba, TrackSectorCount(toc, track), SubSectorBytes,
                       track.Number, "sub-channel (.sub)");

    private static long CopySectors(Stream src, Stream dst, int startLba, int sectorCount,
                                    int sectorBytes, int trackNumber, string what)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);

        long offset = (long)startLba * sectorBytes;
        long total = (long)sectorCount * sectorBytes;
        if (offset < 0 || (src.CanSeek && offset + total > src.Length))
            throw new CcdFormatException(
                $"Track {trackNumber} needs {total:N0} bytes at offset {offset:N0}, but the " +
                $"{what} is only {(src.CanSeek ? src.Length : 0):N0} bytes — the sidecar may not match the .ccd.");

        src.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[1 << 16];
        long remaining = total;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int n = src.Read(buffer, 0, want);
            if (n <= 0)
                throw new CcdFormatException(
                    $"The {what} ended {remaining:N0} bytes early while reading track {trackNumber}.");
            dst.Write(buffer, 0, n);
            remaining -= n;
        }
        return total;
    }

    // ---- helpers ------------------------------------------------------------

    private static Dictionary<string, string>? Find(
        List<(string Name, Dictionary<string, string> Kv)> sections, string name)
        => sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Kv;

    private static int GetInt(Dictionary<string, string> kv, string key, int dflt)
        => kv.TryGetValue(key, out var s) ? ParseInt(s) : dflt;

    private static int ParseInt(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static int ParseTrailingInt(string name)
    {
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        return int.TryParse(name[i..], out var v) ? v : 0;
    }

    private static int MsfToLba(int m, int s, int f) => (m * 60 + s) * 75 + f - 150;

    public sealed class CcdFormatException(string message) : Exception(message);
}
