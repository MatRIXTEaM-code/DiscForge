// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using System.Text.RegularExpressions;

namespace DiscForge.Core.Forensics;

/// <summary>An IFPI Source Identification Code from a disc's inner ring. A <b>mastering</b> SID
/// (<c>IFPI Lxxx</c>) identifies the glass-master/LBR that cut the stamper; a <b>mould</b> SID
/// (<c>IFPI xxxx</c>, no leading L) identifies the replication plant that pressed the disc.</summary>
public sealed record SidCode(string Raw, string Code, bool IsMastering, bool Valid)
{
    public string Kind => IsMastering ? "mastering (LBR)" : "mould (plant)";
    public override string ToString() => $"IFPI {Code} — {Kind}{(Valid ? "" : " (malformed)")}";
}

/// <summary>The decoded contents of a disc's ring / runout: the matrix string plus the IFPI codes
/// that pin down the mastering facility and the pressing plant.</summary>
public sealed record RingCode
{
    public string? Matrix { get; init; }
    public SidCode? MasteringSid { get; init; }
    public SidCode? MouldSid { get; init; }
    public string? Toolstamp { get; init; }
    public required IReadOnlyList<string> RawLines { get; init; }

    public bool HasAny => Matrix is { Length: > 0 } || MasteringSid is not null || MouldSid is not null;

    public string Summary()
    {
        var parts = new List<string>();
        if (Matrix is { Length: > 0 }) parts.Add($"matrix \"{Matrix}\"");
        if (MasteringSid is not null) parts.Add(MasteringSid.ToString());
        if (MouldSid is not null) parts.Add(MouldSid.ToString());
        if (Toolstamp is { Length: > 0 }) parts.Add($"toolstamp \"{Toolstamp}\"");
        return parts.Count == 0 ? "no ring-code data" : string.Join("; ", parts);
    }
}

/// <summary>A ring code bound to a disc identity — the link between the physical pressing marks and
/// the digital genome.</summary>
public sealed record RingCodeRecord
{
    public required string GenomeId { get; init; }
    public string? VolumeId { get; init; }
    public required RingCode Ring { get; init; }
    public string? Source { get; init; }
}

/// <summary>A set of discs that share a pressing origin (same plant or same master).</summary>
public sealed record PressingGroup(string Kind, string Key, IReadOnlyList<string> Members);

/// <summary>
/// Ring-code parsing and pressing linkage — decode the mastering/mould SID codes and matrix string
/// stamped in a disc's inner ring (the gold standard for identifying the exact pressing plant and
/// stamper, and today all typed by hand) and link them to the disc's genome, so physical pressing
/// variants get connected automatically. Give it the runout text — typed, or OCR'd from a photo of the
/// ring by the app layer — and it separates the IFPI mastering code (the glass master) from the mould
/// code (the plant) and validates their format; a registry then groups a collection by shared plant or
/// shared master. Identification and cataloguing only.
/// </summary>
public static class RingCodeParser
{
    private static readonly Regex IfpiRx = new(@"IFPI\s+([A-Za-z0-9]{2,6})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parse a block of ring / runout text into structured fields.</summary>
    public static RingCode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.TrimEntries)
                        .Where(l => l.Length > 0).ToList();

        SidCode? mastering = null, mould = null;
        foreach (Match m in IfpiRx.Matches(text))
        {
            var sid = ClassifySid(m.Value, m.Groups[1].Value);
            if (sid.IsMastering) mastering ??= sid;
            else mould ??= sid;
        }

        // The matrix is whatever runout text is left once the IFPI codes are removed.
        string stripped = IfpiRx.Replace(text, " ");
        string matrix = CollapseWhitespace(stripped);

        return new RingCode
        {
            Matrix = matrix.Length > 0 ? matrix : null,
            MasteringSid = mastering,
            MouldSid = mould,
            RawLines = lines,
        };
    }

    /// <summary>Parse a single SID token like "IFPI L553" or bare "L553" / "94D7".</summary>
    public static SidCode? ParseSid(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = IfpiRx.Match(s);
        if (m.Success) return ClassifySid(m.Value, m.Groups[1].Value);

        string code = s.Trim();
        if (Regex.IsMatch(code, @"^[A-Za-z0-9]{2,6}$"))
            return ClassifySid("IFPI " + code, code);
        return null;
    }

    /// <summary>Whether an IFPI SID code is well-formed for its kind.</summary>
    public static bool IsValidSid(string code, bool mastering)
    {
        if (string.IsNullOrEmpty(code)) return false;
        if (!Regex.IsMatch(code, @"^[A-Za-z0-9]{3,6}$")) return false;
        bool startsL = char.ToUpperInvariant(code[0]) == 'L';
        return mastering ? startsL : !startsL;
    }

    /// <summary>Group records by pressing plant (shared mould SID).</summary>
    public static IReadOnlyList<PressingGroup> GroupByPlant(IEnumerable<RingCodeRecord> records)
        => GroupBy(records, r => r.Ring.MouldSid?.Code, "plant");

    /// <summary>Group records by glass master (shared mastering SID).</summary>
    public static IReadOnlyList<PressingGroup> GroupByMaster(IEnumerable<RingCodeRecord> records)
        => GroupBy(records, r => r.Ring.MasteringSid?.Code, "master");

    public static string Render(RingCode ring)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ring.Summary());
        if (ring.MasteringSid is { Valid: false })
            sb.AppendLine("  ! mastering SID is malformed");
        if (ring.MouldSid is { Valid: false })
            sb.AppendLine("  ! mould SID is malformed");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static SidCode ClassifySid(string raw, string code)
    {
        code = code.ToUpperInvariant();
        bool mastering = code.Length > 0 && code[0] == 'L';
        bool valid = IsValidSid(code, mastering);
        return new SidCode(raw.Trim(), code, mastering, valid);
    }

    private static IReadOnlyList<PressingGroup> GroupBy(
        IEnumerable<RingCodeRecord> records, Func<RingCodeRecord, string?> keyOf, string kind)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .Where(r => keyOf(r) is { Length: > 0 })
            .GroupBy(r => keyOf(r)!.ToUpperInvariant())
            .Select(g => new PressingGroup(kind, g.Key,
                g.Select(r => r.VolumeId ?? r.GenomeId).ToList()))
            .OrderByDescending(g => g.Members.Count)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CollapseWhitespace(string s)
        => Regex.Replace(s, @"\s+", " ").Trim();
}
