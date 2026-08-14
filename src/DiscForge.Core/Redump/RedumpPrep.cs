// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using DiscForge.Core.Dat;
using DiscForge.Core.Forensics;
using DiscForge.Core.Preservation;

namespace DiscForge.Core.Redump;

/// <summary>One line of the submission conformance report: a named check with a status and the detail behind it.</summary>
public sealed record PrepCheck(string Name, PrepStatus Status, string Detail);

public enum PrepStatus { Pass, Warn, Fail, Info }

public sealed record RedumpPrepOptions
{
    /// <summary>A captured subchannel sidecar; when given, the tracks are re-cut at its INDEX 00 boundaries (Redump split).</summary>
    public string? SubPath { get; init; }
    public bool SnapPregap { get; init; }
    /// <summary>A Redump DAT to diff the prepared set against, closing the loop with a match verdict.</summary>
    public string? DatPath { get; init; }
    public string? Game { get; init; }
    /// <summary>The drive's combined read offset in samples, if known — reported for the submission, not applied here.</summary>
    public int? ReadOffsetSamples { get; init; }
}

public sealed record RedumpPrepResult
{
    public required string OutCue { get; init; }
    public required IReadOnlyList<string> OutputFiles { get; init; }
    public required bool ReSplit { get; init; }
    public required IReadOnlyList<PrepCheck> Checks { get; init; }
    public required string SubmissionInfoPath { get; init; }
    public required bool SubmissionReady { get; init; }

    public string Summary()
    {
        int fail = Checks.Count(c => c.Status == PrepStatus.Fail);
        int warn = Checks.Count(c => c.Status == PrepStatus.Warn);
        return SubmissionReady
            ? $"SUBMISSION-READY — {Checks.Count} check(s) passed{(warn > 0 ? $", {warn} warning(s)" : "")}."
            : $"NOT READY — {fail} blocking issue(s){(warn > 0 ? $", {warn} warning(s)" : "")}. See the checklist.";
    }
}

/// <summary>
/// redump-prep — take a raw capture and hand back a submission-ready set plus an honest conformance checklist,
/// in one step. It composes the pieces that used to be run by hand: re-cut the tracks at the subchannel's real
/// boundaries (redump-cue), carry the unreadable-sector map forward, check pregap conformance and dump
/// completeness, write the redump.org submission text, and — given a DAT — diff the result and report whether it
/// matches. Nothing else does end-to-end clean-room Redump prep in one honest step.
///
/// It deliberately does NOT rewrite audio to correct a read offset: an offset is a capture-time property (the
/// samples that slide off one track belong to its neighbour), so redump-prep reports the offset to record on the
/// submission and leaves the payload byte-for-byte as dumped. It prepares and verifies; it fabricates nothing.
/// </summary>
public static class RedumpPrep
{
    public static RedumpPrepResult Prepare(string inCue, string outDir, RedumpPrepOptions opt)
    {
        ArgumentNullException.ThrowIfNull(inCue);
        ArgumentNullException.ThrowIfNull(outDir);
        opt ??= new RedumpPrepOptions();
        Directory.CreateDirectory(outDir);

        string baseName = Path.GetFileNameWithoutExtension(inCue);
        string inDir = Path.GetDirectoryName(Path.GetFullPath(inCue)) ?? ".";
        var checks = new List<PrepCheck>();
        var outputs = new List<string>();
        string outCue = Path.Combine(outDir, baseName + ".cue");
        bool reSplit = false;

        // Load the capture's unreadable-sector map if present; its absolute LBAs survive a re-split unchanged.
        BadSectorMap? bad = null;
        var inSidecar = BadSectorMap.SidecarPath(inCue);
        if (File.Exists(inSidecar)) { try { bad = BadSectorMap.Load(inSidecar); } catch { } }

        // 1) Track boundaries: re-cut at the subchannel when we have one, else copy the set through untouched.
        IReadOnlyList<RedumpTrackReport>? split = null;
        if (opt.SubPath is not null && File.Exists(opt.SubPath))
        {
            var sub = File.ReadAllBytes(opt.SubPath);
            var r = RedumpCueBuilder.Build(inCue, sub, outDir, baseName, opt.SnapPregap);
            split = r.Tracks;
            reSplit = true;
            outputs.Add(Path.GetFileName(outCue));
            outputs.AddRange(r.BinFilenames);
            int gaps = r.Tracks.Count(t => t.PregapSectors > 0);
            checks.Add(new PrepCheck("track split", PrepStatus.Pass,
                $"re-cut at the subchannel's INDEX 00 boundaries — {gaps} track(s) carry a pregap (Redump convention)."));
        }
        else
        {
            CopyThrough(inCue, inDir, outDir, outputs);
            checks.Add(new PrepCheck("track split", PrepStatus.Info, opt.SubPath is null
                ? "no subchannel given — tracks copied as-is; pass --sub to re-cut to Redump's boundaries."
                : $"'{opt.SubPath}' not found — tracks copied as-is."));
        }

        // 2) Carry the unreadable-sector map to the prepared set (re-expressed against the new split when re-cut).
        if (bad is not null)
        {
            BadSectorMap carried = reSplit && split is not null
                ? bad.RemapToTracks(SpansFrom(split, baseName), Path.GetFileName(outCue))
                : bad with { Image = Path.GetFileName(outCue) };
            carried.Save(BadSectorMap.SidecarPath(outCue));
            outputs.Add(Path.GetFileName(BadSectorMap.SidecarPath(outCue)));
            checks.Add(new PrepCheck("unreadable sectors",
                bad.DamagePresent ? PrepStatus.Fail : PrepStatus.Warn,
                bad.DamagePresent
                    ? $"{bad.DamageCount} genuine unreadable sector(s) — the dump is INCOMPLETE and cannot match Redump; re-read the disc."
                    : $"{bad.Count} boundary hole(s) only — payload intact; recorded in the sidecar."));
        }
        else
        {
            checks.Add(new PrepCheck("unreadable sectors", PrepStatus.Info,
                "no bad-sector map beside the capture — no read holes recorded (not proof of a clean read)."));
        }

        // 3) Pregap conformance on the prepared cue.
        try
        {
            var pg = PregapConformance.Check(CueSheet.Parse(File.ReadAllText(outCue)));
            checks.Add(new PrepCheck("pregap conformance", pg.Conformant ? PrepStatus.Pass : PrepStatus.Warn,
                pg.Conformant ? "pregaps follow PlayStation/Redump convention."
                              : "pregaps deviate from convention — " + string.Join("; ", pg.Issues.Take(3))));
        }
        catch (Exception ex) { checks.Add(new PrepCheck("pregap conformance", PrepStatus.Warn, ex.Message)); }

        // 4) Completeness of the bin/cue set.
        try
        {
            var comp = DumpCompleteness.Check(outCue);
            bool ok = comp.AllBinsPresent && comp.WholeSector;
            checks.Add(new PrepCheck("completeness", ok ? PrepStatus.Pass : PrepStatus.Warn,
                $"{comp.TrackCount} track(s), {comp.TotalSectors:N0} sectors; " +
                (ok ? "all bins present, whole-sector." : "structural gaps present.")));
        }
        catch (Exception ex) { checks.Add(new PrepCheck("completeness", PrepStatus.Warn, ex.Message)); }

        // 5) Submission text.
        string subInfoPath = outCue + ".submission.txt";
        try
        {
            var info = SubmissionInfoGenerator.Generate(outCue);
            string text = info.ToRedumpText();
            if (opt.ReadOffsetSamples is { } off)
                text = $"# Combined read offset (record on the submission): {off:+#;-#;0} sample(s)\n" + text;
            File.WriteAllText(subInfoPath, text);
            outputs.Add(Path.GetFileName(subInfoPath));
            checks.Add(new PrepCheck("submission info", PrepStatus.Pass, $"written to {Path.GetFileName(subInfoPath)}."));
        }
        catch (Exception ex) { checks.Add(new PrepCheck("submission info", PrepStatus.Warn, ex.Message)); }

        // 6) Read offset (reported, never applied).
        if (opt.ReadOffsetSamples is { } o)
            checks.Add(new PrepCheck("read offset", o == 0 ? PrepStatus.Pass : PrepStatus.Info,
                o == 0 ? "drive read offset is 0 — no correction needed."
                       : $"record a combined offset of {o:+#;-#;0} sample(s); apply at capture, not here (payload left as dumped)."));

        // 7) DAT diff, closing the loop.
        if (opt.DatPath is not null && File.Exists(opt.DatPath))
        {
            try
            {
                var dat = DatFile.ParseText(File.ReadAllText(opt.DatPath));
                var carried = File.Exists(BadSectorMap.SidecarPath(outCue))
                    ? BadSectorMap.Load(BadSectorMap.SidecarPath(outCue)) : null;
                var diff = RedumpDiffer.Diff(outCue, dat, opt.Game, carried);
                checks.Add(new PrepCheck("Redump match", diff.Match ? PrepStatus.Pass : PrepStatus.Fail,
                    diff.Match ? $"matches {diff.Game}." : diff.Summary() + " — run redump-diff for the full diagnosis."));
            }
            catch (Exception ex) { checks.Add(new PrepCheck("Redump match", PrepStatus.Warn, ex.Message)); }
        }

        bool ready = checks.All(c => c.Status != PrepStatus.Fail);
        return new RedumpPrepResult
        {
            OutCue = outCue, OutputFiles = outputs, ReSplit = reSplit,
            Checks = checks, SubmissionInfoPath = subInfoPath, SubmissionReady = ready,
        };
    }

    /// <summary>Track spans (absolute) for the freshly re-cut set, so the bad-sector map can be re-expressed against it.</summary>
    private static IReadOnlyList<BadSectorMap.TrackSpan> SpansFrom(IReadOnlyList<RedumpTrackReport> split, string baseName)
    {
        var spans = new List<BadSectorMap.TrackSpan>();
        long start = 0;
        foreach (var t in split)
        {
            long length = t.NewLengthSectors - t.PregapSectors;
            spans.Add(new BadSectorMap.TrackSpan(t.Track, $"{baseName}_track{t.Track:D2}.bin", start, t.PregapSectors, length));
            start += t.NewLengthSectors;
        }
        return spans;
    }

    private static void CopyThrough(string inCue, string inDir, string outDir, List<string> outputs)
    {
        var sheet = CueSheet.Parse(File.ReadAllText(inCue));
        string outCue = Path.Combine(outDir, Path.GetFileName(inCue));
        File.Copy(inCue, outCue, overwrite: true);
        outputs.Add(Path.GetFileName(outCue));
        foreach (var f in sheet.Tracks.Select(t => t.File).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
        {
            var src = Path.Combine(inDir, f);
            if (!File.Exists(src)) continue;
            File.Copy(src, Path.Combine(outDir, f), overwrite: true);
            outputs.Add(f);
        }
    }
}
