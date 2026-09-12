// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using DiscForge.Core.Audio;

namespace DiscForge.App.Views;

/// <summary>
/// Grades a rip's per-sector evidence (EAC-style secure-rip depth, offline) and plans the targeted
/// re-read of anything that isn't clean — a thin shell over <see cref="SecureRip"/>, mirroring the
/// CLI's <c>secure-rip-plan</c> command. Takes the same evidence JSON the rip layer records: per
/// track, a base64 string of one state byte per sector (clean / C2-flagged / pass-mismatch /
/// unreadable), the pass count, and the AccurateRip verdict where known.
/// </summary>
internal sealed class SecureRipPlanView : UserControl
{
    private readonly TextBox _evidence = new()
    {
        ReadOnly = true, Location = new Point(90, 40), Width = 522, Font = Theme.Ui,
    };
    private readonly Button _pick = new()
    {
        Text = "…", Location = new Point(618, 38), Width = 30, FlatStyle = FlatStyle.System,
    };
    private readonly Button _grade = new()
    {
        Text = "Grade", Location = new Point(90, 70), Width = 104, Height = 28, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Label _verdictSummary = new()
    {
        AutoSize = true, Location = new Point(210, 76), Font = Theme.UiBold,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 112), Size = new Size(712, 340),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _evidencePath;

    public SecureRipPlanView()
    {
        Size = new Size(736, 464);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Evidence:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "the per-sector-state JSON a rip records — see the log panel below for the exact shape",
            AutoSize = true, Location = new Point(90, 18), Font = Theme.Small, ForeColor = Color.Gray,
        });

        _pick.Click += (_, _) => ChooseEvidence();
        _grade.Click += (_, _) => DoGrade();

        Controls.AddRange(new Control[] { _evidence, _pick, _grade, _verdictSummary, _log });

        _log.Text =
            "Pick an evidence.json and press Grade." + "\r\n\r\n" +
            "The evidence file is what the rip layer records: per track, a base64 string of one state" + "\r\n" +
            "byte per sector (0 clean, 1 C2-flagged, 2 pass-mismatch, 3 unreadable), the pass count," + "\r\n" +
            "and the AccurateRip verdict where known:" + "\r\n\r\n" +
            "  { \"tracks\": [ { \"number\":1, \"passes\":2, \"accurateRipMatch\":true," + "\r\n" +
            "                   \"accurateRipConfidence\":7, \"sectorStates\":\"AAAA...\" } ] }" + "\r\n\r\n" +
            "Grades: VERIFIED (independent AccurateRip match) / CONSISTENT (self-agreement only — the" + "\r\n" +
            "honest ceiling without corroboration) / SUSPECT / FAILED.";
    }

    private void ChooseEvidence()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open secure-rip evidence",
            Filter = "Evidence JSON (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _evidencePath = dlg.FileName;
        _evidence.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        _grade.Enabled = true;
    }

    private void DoGrade()
    {
        if (_evidencePath is null) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_evidencePath));
            if (!doc.RootElement.TryGetProperty("tracks", out var tracksEl) || tracksEl.ValueKind != JsonValueKind.Array)
                throw new FormatException("evidence file has no \"tracks\" array.");

            var verdicts = new List<SecureRip.TrackVerdict>();
            var plans = new List<SecureRip.RereadPlan>();
            int trackIdx = 0;
            foreach (var tEl in tracksEl.EnumerateArray())
            {
                trackIdx++;
                if (!tEl.TryGetProperty("number", out var numEl) || !numEl.TryGetInt32(out int number))
                    throw new FormatException($"track #{trackIdx} has no integer \"number\" field.");
                if (!tEl.TryGetProperty("passes", out var passEl) || !passEl.TryGetInt32(out int passes))
                    throw new FormatException($"track {number}: missing integer \"passes\" field.");
                if (!tEl.TryGetProperty("sectorStates", out var statesEl) || statesEl.GetString() is not { } statesB64)
                    throw new FormatException($"track {number}: missing \"sectorStates\" (base64, one state byte per sector).");
                byte[] states;
                try { states = Convert.FromBase64String(statesB64); }
                catch (FormatException) { throw new FormatException($"track {number}: \"sectorStates\" is not valid base64."); }

                bool? ar = null;
                if (tEl.TryGetProperty("accurateRipMatch", out var arEl) &&
                    arEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    ar = arEl.GetBoolean();

                var t = new SecureRip.TrackEvidence
                {
                    Number = number,
                    Passes = passes,
                    Sectors = states,
                    AccurateRipMatch = ar,
                    AccurateRipConfidence = tEl.TryGetProperty("accurateRipConfidence", out var c) ? c.GetInt32() : 0,
                };
                verdicts.Add(SecureRip.Grade(t));
                plans.Add(SecureRip.PlanReread(t));
            }

            var sb = new StringBuilder();
            foreach (var (v, p) in verdicts.Zip(plans))
            {
                sb.AppendLine($"track {v.Number:00}: {v.Grade.ToString().ToUpperInvariant()} — {v.Reason}");
                if (!p.Nothing)
                {
                    sb.AppendLine($"  re-read {p.Ranges.Count} range(s), {p.SuggestedPasses} passes: {p.Strategy}");
                    foreach (var r in p.Ranges.Take(20))
                        sb.AppendLine($"    sectors {r.StartSector}..{r.StartSector + r.Count - 1}  (worst: {r.Worst})");
                    if (p.Ranges.Count > 20) sb.AppendLine($"    … and {p.Ranges.Count - 20} more");
                }
            }
            _log.Text = sb.ToString();

            bool allGood = verdicts.All(v => v.Grade is SecureRip.TrackGrade.Verified or SecureRip.TrackGrade.Consistent);
            _verdictSummary.Text = allGood
                ? $"{verdicts.Count} track(s) — all VERIFIED/CONSISTENT"
                : $"{verdicts.Count} track(s) — {verdicts.Count(v => v.Grade is SecureRip.TrackGrade.Suspect or SecureRip.TrackGrade.Failed)} need attention";
            _verdictSummary.ForeColor = allGood ? Color.DarkGreen : Color.DarkRed;

            StatusBus.Report($"Graded {verdicts.Count} track(s) from {Path.GetFileName(_evidencePath)}");
            AppLog.Write($"secure-rip-plan {Path.GetFileName(_evidencePath)}: {verdicts.Count} track(s), all-good={allGood}");
        }
        catch (Exception ex)
        {
            _log.Text = "Grading failed: " + ex.Message;
            _verdictSummary.Text = "";
            AppLog.WriteException("secure-rip-plan", ex);
        }
    }
}
