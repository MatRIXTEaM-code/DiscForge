// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

public enum LintSeverity : byte { Info, Warning, Error }

/// <summary>One conformance finding against the ISO 9660 on-disc grammar.</summary>
public sealed record LintFinding(LintSeverity Severity, string Where, string Message);

/// <summary>The result of linting a disc image.</summary>
public sealed record IsoLintReport
{
    public required IReadOnlyList<LintFinding> Findings { get; init; }
    public int Errors => Findings.Count(f => f.Severity == LintSeverity.Error);
    public int Warnings => Findings.Count(f => f.Severity == LintSeverity.Warning);
    public bool Ok => Errors == 0;

    public string Summary() => Findings.Count == 0
        ? "ISO 9660: clean — no conformance issues."
        : $"ISO 9660: {Errors} error(s), {Warnings} warning(s).";
}

/// <summary>
/// image-lint — a strict conformance checker for disc images: hold an image up against the formal
/// ISO 9660 on-disc grammar and report every place it deviates, with a severity and where it is. It
/// checks the primary volume descriptor's magic and version, that the both-endian fields agree with
/// themselves (ISO stores every number twice, little- and big-endian — a mismatch is a real bug), the
/// 2048-byte logical block size, the volume-descriptor-set terminator, and that the recorded volume
/// size and root directory fit inside the image. A linter for disc images: it validates and reports,
/// and changes nothing.
/// </summary>
public static class IsoLint
{
    private const int SS = 2048;

    public static IsoLintReport Check(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var f = new List<LintFinding>();

        if (image.Length % SS != 0)
            f.Add(new(LintSeverity.Warning, "image",
                $"length {image.Length:N0} is not a whole number of {SS}-byte sectors."));

        long totalSectors = image.Length / SS;
        if (totalSectors < 17)
        {
            f.Add(new(LintSeverity.Error, "image", "too small to hold an ISO 9660 volume descriptor set."));
            return new IsoLintReport { Findings = f };
        }

        var pvd = image.AsSpan(16 * SS, SS);
        if (pvd[0] != 1) f.Add(new(LintSeverity.Error, "PVD", $"descriptor type at sector 16 is {pvd[0]}, expected 1 (primary)."));
        if (!Magic(pvd)) f.Add(new(LintSeverity.Error, "PVD", "standard identifier is not \"CD001\"."));
        if (pvd[6] != 1) f.Add(new(LintSeverity.Error, "PVD", $"volume descriptor version is {pvd[6]}, expected 1."));

        // Logical block size (both-endian 16-bit at 128).
        int lbsLe = U16Le(pvd, 128), lbsBe = U16Be(pvd, 130);
        if (lbsLe != lbsBe)
            f.Add(new(LintSeverity.Error, "PVD", $"logical block size both-endian mismatch (LE {lbsLe} vs BE {lbsBe})."));
        else if (lbsLe != SS)
            f.Add(new(LintSeverity.Warning, "PVD", $"logical block size is {lbsLe}, not the usual {SS}."));

        // Volume space size (both-endian 32-bit at 80).
        uint vssLe = U32Le(pvd, 80), vssBe = U32Be(pvd, 84);
        if (vssLe != vssBe)
            f.Add(new(LintSeverity.Error, "PVD", $"volume space size both-endian mismatch (LE {vssLe} vs BE {vssBe})."));
        else if (vssLe > totalSectors)
            f.Add(new(LintSeverity.Warning, "PVD",
                $"volume declares {vssLe:N0} sectors but the image holds only {totalSectors:N0} — truncated."));

        // Path table size (both-endian 32-bit at 132).
        uint ptLe = U32Le(pvd, 132), ptBe = U32Be(pvd, 136);
        if (ptLe != ptBe)
            f.Add(new(LintSeverity.Error, "PVD", $"path table size both-endian mismatch (LE {ptLe} vs BE {ptBe})."));

        // Root directory record (34 bytes at 156): extent must fit in the image.
        int rootLen = pvd[156];
        if (rootLen < 34)
            f.Add(new(LintSeverity.Error, "PVD", $"root directory record length is {rootLen}, expected >= 34."));
        else
        {
            uint rootExtent = U32Le(pvd, 156 + 2);
            uint rootSize = U32Le(pvd, 156 + 10);
            if ((long)rootExtent * SS >= image.Length)
                f.Add(new(LintSeverity.Error, "root", $"root directory extent {rootExtent} lies past the end of the image."));
            else if ((long)rootExtent * SS + rootSize > image.Length)
                f.Add(new(LintSeverity.Warning, "root", "root directory runs past the end of the image."));
        }

        // Volume-descriptor-set terminator (type 255) must appear after the PVD.
        bool terminator = false;
        for (long s = 17; s < Math.Min(totalSectors, 17 + 32); s++)
        {
            var d = image.AsSpan((int)(s * SS), SS);
            if (!Magic(d)) break;                 // descriptor set ended without a terminator
            if (d[0] == 255) { terminator = true; break; }
        }
        if (!terminator)
            f.Add(new(LintSeverity.Error, "VDS", "no volume-descriptor-set terminator (type 255) found after the PVD."));

        return new IsoLintReport { Findings = f };
    }

    public static string Render(IsoLintReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var x in r.Findings)
            sb.AppendLine($"  [{x.Severity}] {x.Where}: {x.Message}");
        return sb.ToString().TrimEnd();
    }

    // ---- helpers ------------------------------------------------------------

    private static bool Magic(ReadOnlySpan<byte> d) =>
        d[1] == (byte)'C' && d[2] == (byte)'D' && d[3] == (byte)'0' && d[4] == (byte)'0' && d[5] == (byte)'1';

    private static int U16Le(ReadOnlySpan<byte> b, int o) => b[o] | (b[o + 1] << 8);
    private static int U16Be(ReadOnlySpan<byte> b, int o) => (b[o] << 8) | b[o + 1];
    private static uint U32Le(ReadOnlySpan<byte> b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static uint U32Be(ReadOnlySpan<byte> b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
}
