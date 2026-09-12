// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.RegularExpressions;

namespace DiscForge.Core.GameCube;

/// <summary>
/// A GameCube disc's three inner-ring codes, decoded — distinct from the generic IFPI mastering/mould
/// SID codes in <c>DiscForge.Core.Forensics.RingCodeParser</c> (which apply to optical media broadly).
/// GameCube discs additionally carry three short codes, conventionally printed/etched in red, blue and
/// green near the hub:
///
///   RED   — AYYMDDBB: a manufacturing date. A = a region flag (community-observed: a LETTER here means
///           the disc was manufactured in the USA, a DIGIT means Japan — Nintendo has not published this
///           mapping, so it is taken on collector-community authority, not an official spec). YY/M/DD are
///           a two-digit year, a month letter (A=January … L=December), and a two-digit day. The trailing
///           BB is two more characters whose meaning is NOT documented anywhere this project could find
///           (even the community source flagging it says "not sure what this is either") — parsed and
///           kept raw, never interpreted.
///   BLUE  — AAA-BCCD-E-FF GGG: the disc's own identity, printed in a form that (once the hyphens are
///           dropped from the middle group) is exactly the disc's 4-character game code preceded by its
///           3-character console code and followed by disc number, ROM revision, and a region name.
///           AAA = console (DOL for GameCube), BCCD = the 4-char game code (matches the disc header's
///           GameCode field byte-for-byte when the pressing is genuine), E = disc number (0-based),
///           FF = ROM revision (0-based), GGG = a manufacturing-region name (e.g. USA/JPN/EUR).
///   GREEN — almost always the literal "S0"; kept as an anomaly flag rather than parsed further, since
///           the only documented exception on record is a single disc reading "S1" with no explanation
///           (possibly a pressing variant, possibly a transcription error).
///
/// Sourced from collector/community documentation of Nintendo optical disc date codes (a public forum
/// thread cataloguing them, cross-checked against the format's own internal consistency: the "Wind Waker"
/// example code C03B2606 decodes to 2003-02-26, and B = February matches A=January..L=December). This is
/// NOT an official Nintendo specification — Nintendo has never published what these fields mean — so this
/// class treats every field as "provably self-consistent" (the date parses, the game code matches the
/// disc's own header) rather than "confirmed correct against a manufacturer spec." The BB field and any
/// green code other than "S0" are surfaced raw, undecoded, exactly for that reason.
///
/// Cross-checking a ring's blue code against a specific Redump revision entry is left to the caller (see
/// <see cref="GcRingCodeCheck"/>): this project bundles no Redump database, so it can only confirm a
/// disc's ring code against a game code / disc number / revision the caller already has in hand.
/// </summary>
public sealed record GcRingCode
{
    public string? RedRaw { get; init; }
    public string? BlueRaw { get; init; }
    public string? GreenRaw { get; init; }

    // ---- red: AYYMDDBB ----
    public char? ManufactureRegionFlag { get; init; }
    /// <summary>Community-observed, not Nintendo-documented: true when the region flag is a letter (USA),
    /// false when it's a digit (Japan), null if the red code didn't parse.</summary>
    public bool? ManufacturedInUsa { get; init; }
    public DateOnly? ManufactureDate { get; init; }
    /// <summary>The trailing two characters of the red code — undocumented, kept raw and uninterpreted.</summary>
    public string? RedTrailer { get; init; }

    // ---- blue: AAA-BCCD-E-FF GGG ----
    public string? ConsoleCode { get; init; }     // "DOL" for GameCube
    /// <summary>The 4-character game code (B+CC+D) — should match the disc header's GameCode field.</summary>
    public string? GameCode { get; init; }
    public int? DiscNumber { get; init; }
    public int? RomRevision { get; init; }
    public string? ManufacturingRegionName { get; init; }   // e.g. "USA", "JPN", "EUR"

    // ---- green ----
    public bool? GreenIsStandard { get; init; }   // true when GreenRaw == "S0"

    public bool HasAny => RedRaw is { Length: > 0 } || BlueRaw is { Length: > 0 } || GreenRaw is { Length: > 0 };

    public string Summary()
    {
        var parts = new List<string>();
        if (ManufactureDate is { } d) parts.Add($"mastered {d:yyyy-MM-dd}");
        else if (RedRaw is { Length: > 0 }) parts.Add($"red \"{RedRaw}\" (unparsed)");
        if (ConsoleCode is { Length: > 0 } && GameCode is { Length: > 0 })
            parts.Add($"{ConsoleCode}-{GameCode} disc {DiscNumber ?? 0} rev {RomRevision ?? 0}" +
                      (ManufacturingRegionName is { Length: > 0 } ? $" ({ManufacturingRegionName})" : ""));
        else if (BlueRaw is { Length: > 0 }) parts.Add($"blue \"{BlueRaw}\" (unparsed)");
        if (GreenRaw is { Length: > 0 })
            parts.Add(GreenIsStandard == true ? "green S0" : $"green \"{GreenRaw}\" (non-standard)");
        return parts.Count == 0 ? "no GameCube ring-code data" : string.Join("; ", parts);
    }
}

/// <summary>Parses the three GameCube-specific ring codes. Every field is best-effort: a code that
/// doesn't match the expected shape is left unparsed (raw text kept, structured fields null) rather than
/// guessed at.</summary>
public static class GcRingCodeParser
{
    private static readonly Regex RedRx = new(
        @"^(?<flag>[A-Za-z0-9])(?<yy>\d{2})(?<month>[A-La-l])(?<dd>\d{2})(?<bb>.{2})$", RegexOptions.Compiled);

    private static readonly Regex BlueRx = new(
        @"^(?<console>[A-Za-z]{3})-(?<media>[A-Za-z])(?<game>[A-Za-z0-9]{2})(?<lang>[A-Za-z])-(?<disc>\d+)-(?<rev>\d+)(?:\s+(?<region>[A-Za-z]{2,4}))?$",
        RegexOptions.Compiled);

    public static GcRingCode Parse(string? red, string? blue, string? green)
    {
        var result = new GcRingCode { RedRaw = Clean(red), BlueRaw = Clean(blue), GreenRaw = Clean(green) };

        if (result.RedRaw is { Length: 8 } r)
        {
            var m = RedRx.Match(r);
            if (m.Success)
            {
                char flag = char.ToUpperInvariant(m.Groups["flag"].Value[0]);
                bool usa = char.IsLetter(flag);
                int yy = int.Parse(m.Groups["yy"].Value);
                int month = char.ToUpperInvariant(m.Groups["month"].Value[0]) - 'A' + 1;
                int dd = int.Parse(m.Groups["dd"].Value);
                DateOnly? date = null;
                if (month is >= 1 and <= 12)
                {
                    try { date = new DateOnly(2000 + yy, month, dd); }
                    catch (ArgumentOutOfRangeException) { date = null; }   // an invalid day — leave undated, don't guess
                }
                result = result with
                {
                    ManufactureRegionFlag = flag,
                    ManufacturedInUsa = usa,
                    ManufactureDate = date,
                    RedTrailer = m.Groups["bb"].Value,
                };
            }
        }

        if (result.BlueRaw is { Length: > 0 } b)
        {
            var m = BlueRx.Match(b);
            if (m.Success)
            {
                string game = (m.Groups["media"].Value + m.Groups["game"].Value + m.Groups["lang"].Value).ToUpperInvariant();
                result = result with
                {
                    ConsoleCode = m.Groups["console"].Value.ToUpperInvariant(),
                    GameCode = game,
                    DiscNumber = int.Parse(m.Groups["disc"].Value),
                    RomRevision = int.Parse(m.Groups["rev"].Value),
                    ManufacturingRegionName = m.Groups["region"].Success ? m.Groups["region"].Value.ToUpperInvariant() : null,
                };
            }
        }

        if (result.GreenRaw is { Length: > 0 } g)
            result = result with { GreenIsStandard = g.Equals("S0", StringComparison.OrdinalIgnoreCase) };

        return result;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Cross-checks a parsed GameCube ring code against values the caller already has — the disc's
/// own header game code (always available), and optionally a specific Redump entry's disc number /
/// revision. This project bundles no Redump database, so a revision/disc-number match is only ever
/// checked when the caller supplies the expected value themselves, exactly as
/// <c>GcJunkReconstructor.Reconstruct</c>'s <c>expectedCrc32</c> parameter works.</summary>
public static class GcRingCodeCheck
{
    public sealed record Result
    {
        public required bool? GameCodeMatches { get; init; }
        public required bool? DiscNumberMatches { get; init; }
        public required bool? RevisionMatches { get; init; }
        public required IReadOnlyList<string> Issues { get; init; }
    }

    public static Result CrossCheck(GcRingCode ring, string? discHeaderGameCode,
        int? expectedDiscNumber = null, int? expectedRomRevision = null)
    {
        ArgumentNullException.ThrowIfNull(ring);
        var issues = new List<string>();

        bool? gameCodeMatches = null;
        if (ring.GameCode is { Length: > 0 })
        {
            if (string.IsNullOrEmpty(discHeaderGameCode))
                issues.Add("ring code parsed a game code but no disc header game code was supplied to compare it to.");
            else
            {
                gameCodeMatches = ring.GameCode.Equals(discHeaderGameCode, StringComparison.OrdinalIgnoreCase);
                if (gameCodeMatches == false)
                    issues.Add($"ring code's blue field says \"{ring.GameCode}\" but the disc header says \"{discHeaderGameCode}\".");
            }
        }

        bool? discNumberMatches = null;
        if (expectedDiscNumber is { } exd)
        {
            if (ring.DiscNumber is { } actualD)
            {
                discNumberMatches = actualD == exd;
                if (!discNumberMatches.Value)
                    issues.Add($"ring code disc number {actualD} does not match the expected {exd}.");
            }
            else issues.Add("no disc number could be parsed from the ring code's blue field to compare.");
        }

        bool? revisionMatches = null;
        if (expectedRomRevision is { } exr)
        {
            if (ring.RomRevision is { } actualR)
            {
                revisionMatches = actualR == exr;
                if (!revisionMatches.Value)
                    issues.Add($"ring code ROM revision {actualR} does not match the expected {exr}.");
            }
            else issues.Add("no ROM revision could be parsed from the ring code's blue field to compare.");
        }

        if (ring.GreenIsStandard == false)
            issues.Add($"green ring code is \"{ring.GreenRaw}\", not the standard \"S0\" — undocumented, flagged as an anomaly only.");

        return new Result
        {
            GameCodeMatches = gameCodeMatches,
            DiscNumberMatches = discNumberMatches,
            RevisionMatches = revisionMatches,
            Issues = issues,
        };
    }
}
