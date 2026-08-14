// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Util;

namespace DiscForge.Core.Raw;

/// <summary>Album- and per-track title/performer (and other text) decoded from CD-TEXT.</summary>
public sealed record CdTextTrack(string? Title, string? Performer, string? Songwriter, string? Composer);

/// <summary>The result of reading a disc's CD-TEXT packs.</summary>
public sealed record CdTextInfo
{
    public string? AlbumTitle { get; init; }
    public string? AlbumPerformer { get; init; }
    public string? AlbumSongwriter { get; init; }
    public string? AlbumComposer { get; init; }
    public IReadOnlyList<CdTextTrack> Tracks { get; init; } = Array.Empty<CdTextTrack>();

    public int FirstTrack { get; init; }
    public int LastTrack { get; init; }
    /// <summary>Language code of block 0 (0x09 = English), from the size-info pack, or -1 if absent.</summary>
    public int LanguageCode { get; init; } = -1;
    /// <summary>Character code (0 = ISO 8859-1), from the size-info pack, or -1 if absent.</summary>
    public int CharacterCode { get; init; } = -1;

    public int PacksParsed { get; init; }
    public int PacksBadCrc { get; init; }

    public bool HasText =>
        !string.IsNullOrEmpty(AlbumTitle) || !string.IsNullOrEmpty(AlbumPerformer) ||
        Tracks.Any(t => !string.IsNullOrEmpty(t.Title) || !string.IsNullOrEmpty(t.Performer));

    public string Summary()
    {
        if (!HasText && PacksParsed == 0) return "No CD-TEXT packs found.";
        if (!HasText) return $"CD-TEXT: {PacksParsed} pack(s), no readable text ({PacksBadCrc} bad CRC).";
        string alb = AlbumPerformer is { Length: > 0 } ? $"{AlbumTitle} — {AlbumPerformer}" : AlbumTitle ?? "";
        return $"CD-TEXT: \"{alb}\", {Tracks.Count} track(s)" +
               (LanguageCode >= 0 ? $", language 0x{LanguageCode:X2}" : "") +
               (PacksBadCrc > 0 ? $" ({PacksBadCrc} bad-CRC pack(s) dropped)" : "") + ".";
    }
}

/// <summary>
/// cdtext — the reader that decodes CD-TEXT back into album and track metadata, the counterpart to
/// <see cref="CdTextBuilder"/>. CD-TEXT rides in the R–W sub-channels of the lead-in as 18-byte packs; each
/// pack names its type (title, performer, songwriter, …), the track it belongs to, its running sequence,
/// and carries twelve bytes of text plus a CRC. The strings of a given type flow NUL-separated across many
/// packs — the album string first, then one per track. This validates each pack's CRC, discards the
/// repeats the lead-in loops through, reassembles the size-information pack (first/last track, language),
/// and stitches the fields back into per-track text. It can read a flat pack stream or reverse the six-bit
/// R–W symbol packing to read a captured lead-in. Read-only; it parses and reports.
/// </summary>
public static class CdTextReader
{
    public const int PackSize = 18;

    private const byte TypeTitle = 0x80, TypePerformer = 0x81, TypeSongwriter = 0x82, TypeComposer = 0x83;
    private const byte TypeSizeInfo = 0x8F;

    /// <summary>Read CD-TEXT from a set of 18-byte packs.</summary>
    public static CdTextInfo Read(IReadOnlyList<byte[]> packs, bool requireValidCrc = true)
    {
        ArgumentNullException.ThrowIfNull(packs);
        int parsed = packs.Count, bad = 0;

        // Keep the first pack for each (block, sequence) — the lead-in loops the same packs repeatedly.
        var unique = new List<byte[]>();
        var seen = new HashSet<(int block, int seq)>();
        foreach (var p in packs)
        {
            if (p.Length < PackSize) continue;
            ushort crc = Crc16.ComputeInverted(p.AsSpan(0, 16));
            ushort stored = (ushort)((p[16] << 8) | p[17]);
            if (crc != stored) { bad++; if (requireValidCrc) continue; }

            int block = (p[3] >> 4) & 0x07;
            int seq = p[2];
            int type = p[0];
            // Size-info packs (0x8F) use byte 1 as a part index, not a sequence — key them separately.
            var key = type == TypeSizeInfo ? (block, 0x1000 | p[1]) : (block, seq);
            if (seen.Add(key)) unique.Add(p);
        }

        // ---- size information (0x8F, block 0) ------------------------------
        int firstTrack = 0, lastTrack = 0, language = -1, charCode = -1;
        var sizeParts = unique.Where(p => p[0] == TypeSizeInfo && ((p[3] >> 4) & 7) == 0)
                              .OrderBy(p => p[1]).ToList();
        if (sizeParts.Count > 0)
        {
            var info = new byte[36];
            foreach (var p in sizeParts)
            {
                int part = p[1];
                if (part is >= 0 and < 3) Array.Copy(p, 4, info, part * 12, 12);
            }
            charCode = info[0];
            firstTrack = info[1];
            lastTrack = info[2];
            language = info[28];
        }

        // ---- text types (block 0) ------------------------------------------
        var titles = ReassembleFields(unique, TypeTitle);
        var performers = ReassembleFields(unique, TypePerformer);
        var songwriters = ReassembleFields(unique, TypeSongwriter);
        var composers = ReassembleFields(unique, TypeComposer);

        int trackCount = lastTrack >= firstTrack && firstTrack >= 1
            ? lastTrack - firstTrack + 1
            : Math.Max(0, new[] { titles.Count, performers.Count, songwriters.Count, composers.Count }.Max() - 1);

        var tracks = new List<CdTextTrack>(trackCount);
        for (int i = 1; i <= trackCount; i++)
            tracks.Add(new CdTextTrack(Field(titles, i), Field(performers, i), Field(songwriters, i), Field(composers, i)));

        return new CdTextInfo
        {
            AlbumTitle = Field(titles, 0),
            AlbumPerformer = Field(performers, 0),
            AlbumSongwriter = Field(songwriters, 0),
            AlbumComposer = Field(composers, 0),
            Tracks = tracks,
            FirstTrack = firstTrack,
            LastTrack = lastTrack,
            LanguageCode = language,
            CharacterCode = charCode,
            PacksParsed = parsed,
            PacksBadCrc = bad,
        };
    }

    /// <summary>Read CD-TEXT from a flat pack stream (length a multiple of 18; a 4-byte header, as some
    /// .cdt files carry, is skipped automatically).</summary>
    public static CdTextInfo ReadPackStream(ReadOnlySpan<byte> bytes, bool requireValidCrc = true)
    {
        int offset = bytes.Length % PackSize == 4 ? 4 : 0;   // some .cdt files prepend a 4-byte header
        var packs = new List<byte[]>();
        for (int o = offset; o + PackSize <= bytes.Length; o += PackSize)
            packs.Add(bytes.Slice(o, PackSize).ToArray());
        return Read(packs, requireValidCrc);
    }

    /// <summary>Reverse the six-bit R–W symbol packing (see <see cref="CdTextBuilder.FillSectorRw"/>): every
    /// four 6-bit symbols become three bytes. Returns the recovered 18-byte packs.</summary>
    public static IReadOnlyList<byte[]> DecodeRwSymbols(ReadOnlySpan<byte> symbols)
    {
        int groups = symbols.Length / 4;
        var bytes = new byte[groups * 3];
        for (int g = 0; g < groups; g++)
        {
            int s0 = symbols[g * 4] & 0x3F, s1 = symbols[g * 4 + 1] & 0x3F;
            int s2 = symbols[g * 4 + 2] & 0x3F, s3 = symbols[g * 4 + 3] & 0x3F;
            bytes[g * 3 + 0] = (byte)((s0 << 2) | (s1 >> 4));
            bytes[g * 3 + 1] = (byte)(((s1 & 0x0F) << 4) | (s2 >> 2));
            bytes[g * 3 + 2] = (byte)(((s2 & 0x03) << 6) | s3);
        }
        var packs = new List<byte[]>();
        for (int o = 0; o + PackSize <= bytes.Length; o += PackSize)
            packs.Add(bytes.AsSpan(o, PackSize).ToArray());
        return packs;
    }

    public static string Render(CdTextInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var sb = new StringBuilder();
        sb.AppendLine(info.Summary());
        for (int i = 0; i < info.Tracks.Count; i++)
        {
            var t = info.Tracks[i];
            if (string.IsNullOrEmpty(t.Title) && string.IsNullOrEmpty(t.Performer)) continue;
            int trackNo = (info.FirstTrack >= 1 ? info.FirstTrack : 1) + i;
            string perf = t.Performer is { Length: > 0 } ? $" — {t.Performer}" : "";
            sb.AppendLine($"  {trackNo,2}. {t.Title}{perf}");
        }
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    /// <summary>Concatenate a text type's payloads in sequence order and split on NUL: field 0 is the
    /// album string, field n the nth track.</summary>
    private static List<string> ReassembleFields(List<byte[]> packs, byte type)
    {
        var ordered = packs.Where(p => p[0] == type && ((p[3] >> 4) & 7) == 0)
                           .OrderBy(p => p[2]).ToList();
        if (ordered.Count == 0) return new List<string>();

        var stream = new List<byte>(ordered.Count * 12);
        foreach (var p in ordered) for (int j = 0; j < 12; j++) stream.Add(p[4 + j]);

        var fields = new List<string>();
        int start = 0;
        for (int i = 0; i < stream.Count; i++)
        {
            if (stream[i] != 0) continue;
            fields.Add(Encoding.Latin1.GetString(stream.ToArray(), start, i - start));
            start = i + 1;
        }
        return fields;
    }

    private static string? Field(List<string> fields, int index)
    {
        if (index < 0 || index >= fields.Count) return null;
        return fields[index].Length == 0 ? null : fields[index];
    }
}
