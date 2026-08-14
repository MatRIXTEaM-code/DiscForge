// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;

namespace DiscForge.Core.Forensics;

/// <summary>A guess at what produced a dump, with the evidence for it.</summary>
public sealed record ToolGuess(string Tool, ProtectionConfidence Confidence, string Evidence);

/// <summary>What the shape of a dump says about how it was made.</summary>
public sealed record ProvenanceReport
{
    public required string GeometryNote { get; init; }
    public required IReadOnlyList<ToolGuess> Candidates { get; init; }

    public ToolGuess? Best => Candidates.Count == 0 ? null
        : Candidates.OrderByDescending(c => c.Confidence).First();

    public string Summary() => Best is { } b
        ? $"Likely produced by {b.Tool} ({b.Confidence}). {GeometryNote}"
        : $"Origin unclear from the fileset. {GeometryNote}";
}

/// <summary>
/// Dump-tool &amp; drive fingerprinting — infer how an image was made from the shape it left behind.
/// The container format is a strong tell (a <c>.ccd/.img/.sub</c> trio is CloneCD, <c>.mds/.mdf</c> is
/// Alcohol 120%, <c>.cdi</c> is DiscJuggler, <c>.chd</c> is MAME, a Redump set carries a
/// <c>submission-info</c> and per-track subchannel), and the main image's sector geometry says whether
/// it is a cooked 2048-byte ISO, a raw 2352-byte rip, or a raw rip carrying sub-channel. Together they
/// place an image's provenance — "this is a CloneCD dump," "this is a mastered ISO, not a disc rip" —
/// which is useful both for cataloguing and as an authenticity signal alongside the genome. Inference
/// from public structure only.
/// </summary>
public static class DumpProvenance
{
    // Ordered so the more specific / stronger signatures come first.
    private static readonly (string[] Need, string Tool, ProtectionConfidence Conf, string Why)[] Rules =
    {
        (new[] { ".ccd", ".img", ".sub" }, "CloneCD", ProtectionConfidence.Confirmed, ".ccd + .img + .sub set"),
        (new[] { ".ccd", ".img" }, "CloneCD", ProtectionConfidence.Likely, ".ccd + .img (no subchannel)"),
        (new[] { ".mds", ".mdf" }, "Alcohol 120%", ProtectionConfidence.Confirmed, ".mds + .mdf pair"),
        (new[] { ".cdi" }, "DiscJuggler", ProtectionConfidence.Confirmed, ".cdi image"),
        (new[] { ".nrg" }, "Nero Burning ROM", ProtectionConfidence.Confirmed, ".nrg image"),
        (new[] { ".gdi" }, "Dreamcast GDI dump", ProtectionConfidence.Confirmed, ".gdi track index"),
        (new[] { ".chd" }, "MAME / chdman", ProtectionConfidence.Confirmed, ".chd container"),
        (new[] { ".cue", ".bin", "submission-info" }, "Redump (DiscImageCreator)", ProtectionConfidence.Confirmed,
            ".cue/.bin with a submission-info report"),
        (new[] { ".cue", ".bin", ".sub" }, "DiscImageCreator (subchannel rip)", ProtectionConfidence.Likely,
            ".cue/.bin with per-disc .sub"),
        (new[] { ".cue", ".bin" }, "generic bin/cue", ProtectionConfidence.Possible, ".cue + .bin (tool-agnostic)"),
        (new[] { ".iso" }, "cooked ISO (mastered or extracted)", ProtectionConfidence.Possible, ".iso only"),
    };

    /// <summary>Infer provenance from the set of files that make up the dump, plus (optionally) the
    /// main image's bytes-per-sector.</summary>
    public static ProvenanceReport Infer(IReadOnlyList<string> fileNames, int? mainSectorSize = null)
    {
        ArgumentNullException.ThrowIfNull(fileNames);

        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasSubmission = false;
        foreach (var raw in fileNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string name = raw.Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (name.Contains("submission-info", StringComparison.OrdinalIgnoreCase)) { hasSubmission = true; continue; }
            string ext = System.IO.Path.GetExtension(name);
            if (ext.Length > 0) exts.Add(ext);
        }
        if (hasSubmission) exts.Add("submission-info");

        var candidates = new List<ToolGuess>();
        foreach (var (need, tool, conf, why) in Rules)
        {
            if (need.All(exts.Contains) && !candidates.Any(c => c.Tool == tool))
                candidates.Add(new ToolGuess(tool, conf, why));
        }

        return new ProvenanceReport
        {
            GeometryNote = GeometryNote(mainSectorSize),
            Candidates = candidates,
        };
    }

    public static string Render(ProvenanceReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var c in r.Candidates)
            sb.AppendLine($"  {c.Tool} [{c.Confidence}] — {c.Evidence}");
        return sb.ToString().TrimEnd();
    }

    private static string GeometryNote(int? sectorSize) => sectorSize switch
    {
        null => "Sector geometry not supplied.",
        2048 => "2048-byte sectors: a cooked image (ISO / mastered), not a raw disc rip.",
        2352 => "2352-byte sectors: a raw rip (main channel only).",
        2448 => "2448-byte sectors: a raw rip carrying the sub-channel — a subchannel-capable ripper.",
        2368 => "2368-byte sectors: a raw rip with PQ sub-channel.",
        _ => $"{sectorSize}-byte sectors: an unusual geometry.",
    };
}
