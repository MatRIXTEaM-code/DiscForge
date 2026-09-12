// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Cue;
using DiscForge.Core.Forensics;
using DiscForge.Core.Preservation;

namespace DiscForge.App.Views;

/// <summary>
/// GUI mirror of <c>dforge disc-mri</c>: renders per-sector evidence as a polar map of the PHYSICAL
/// disc (real Red Book spiral geometry), so damage shows its true shape — a radial streak is a
/// scratch, a ring is a pressing defect, a bloom from the hub is rot, a solid outer band is a
/// muted/failed read region. A cue supplies per-track audio/data knowledge; a dump's
/// <c>.badsectors.json</c> sidecar (auto-detected, or picked explicitly) overlays recorded holes and
/// track-boundary sectors. Worst evidence wins per pixel — damage never hides.
///
/// The last of the six GUI-parity candidates, and the only one that renders an image rather than
/// reporting text — otherwise the same lower-risk shape as <see cref="DumpCertView"/>,
/// <see cref="PressingDnaView"/>, <see cref="DriveDossierView"/>, and <see cref="DiscActuaryView"/>:
/// pure offline analysis of local files, no live drive at all. WinForms has no native SVG renderer,
/// so the on-screen preview always uses <see cref="DiscMri.RenderPng"/> (the CLI's own PNG path);
/// Save As offers both <c>.svg</c> (map + legend, matching <c>dforge disc-mri</c>'s default) and
/// <c>.png</c> (bare map), calling whichever renderer the chosen extension implies — exactly the
/// same branch the CLI takes.
/// </summary>
internal sealed class DiscMriView : UserControl
{
    private readonly TextBox _inputPath = new() { ReadOnly = true, Location = new Point(90, 14), Width = 430, Font = Theme.Ui };
    private readonly Button _inputPick = new() { Text = "Open…", Location = new Point(528, 12), Width = 80, FlatStyle = FlatStyle.System };

    private readonly TextBox _mapPath = new() { ReadOnly = true, Location = new Point(90, 44), Width = 430, Font = Theme.Ui };
    private readonly Button _mapPick = new() { Text = "…", Location = new Point(528, 42), Width = 30, FlatStyle = FlatStyle.System };
    private readonly Button _mapClear = new() { Text = "Clear", Location = new Point(564, 42), Width = 60, FlatStyle = FlatStyle.System, Enabled = false };

    private readonly NumericUpDown _size = new()
    {
        Minimum = 200, Maximum = 4000, Value = 1200, Increment = 100,
        Location = new Point(90, 74), Width = 80, Font = Theme.Ui,
    };
    private readonly Button _render = new()
    {
        Text = "Render", Location = new Point(12, 104), Width = 100, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _saveAs = new()
    {
        Text = "Save As…", Location = new Point(118, 104), Width = 100, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _planReread = new()
    {
        Text = "Plan Re-read…", Location = new Point(224, 104), Width = 120, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _savePlan = new()
    {
        Text = "Save Plan…", Location = new Point(350, 104), Width = 100, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly PictureBox _preview = new()
    {
        Location = new Point(12, 140), Size = new Size(420, 420),
        BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(444, 140), Size = new Size(280, 420),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _binPath;
    private string? _mapOverride;
    private DiscMri.Evidence[]? _evidence;
    private string _title = "";
    private DiscMri.RereadPlan? _plan;

    public DiscMriView()
    {
        Size = new Size(736, 574);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 18), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Sidecar map:", AutoSize = true, Location = new Point(12, 48), Font = Theme.Ui, ForeColor = Color.Gray });
        Controls.Add(new Label { Text = "Map pixels:", AutoSize = true, Location = new Point(12, 76), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "A .cue supplies per-track audio/data knowledge (needs a single-file cue).\n" +
                   "Sidecar map is optional — auto-detected next to the image if present.",
            AutoSize = true, Location = new Point(444, 76), Font = Theme.Ui, ForeColor = Color.Gray,
        });

        _inputPick.Click += (_, _) => PickInput();
        _mapPick.Click += (_, _) => PickMap();
        _mapClear.Click += (_, _) => { _mapOverride = null; _mapPath.Text = ""; _mapClear.Enabled = false; };
        _render.Click += (_, _) => DoRender();
        _saveAs.Click += (_, _) => DoSaveAs();
        _planReread.Click += (_, _) => DoPlanReread();
        _savePlan.Click += (_, _) => DoSavePlan();

        Controls.Add(_inputPath); Controls.Add(_inputPick);
        Controls.Add(_mapPath); Controls.Add(_mapPick); Controls.Add(_mapClear);
        Controls.Add(_size);
        Controls.Add(_render); Controls.Add(_saveAs); Controls.Add(_planReread); Controls.Add(_savePlan);
        Controls.Add(_preview);
        Controls.Add(_log);

        _log.Text = "Open a raw image (.bin) or a single-file .cue, then Render.";
    }

    private void PickInput()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Disc images / cues (*.bin;*.cue)|*.bin;*.cue|Raw images (*.bin)|*.bin|CUE sheets (*.cue)|*.cue|All files (*.*)|*.*",
        };
        if (AppSettings.LastImageDirectory is { } dir) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _binPath = dlg.FileName;
        _inputPath.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        _render.Enabled = true;
        _log.Text = $"Image: {Path.GetFileName(dlg.FileName)}";
    }

    private void PickMap()
    {
        using var dlg = new OpenFileDialog { Filter = "Bad-sector sidecar (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _mapOverride = dlg.FileName;
        _mapPath.Text = dlg.FileName;
        _mapClear.Enabled = true;
    }

    private void DoRender()
    {
        if (_binPath is null) return;
        try
        {
            // Resolve bin + spans. A cue names the bin and tells us which ranges are audio — same
            // logic as DiscMriCmd in Program.cs, ported verbatim (CLI-internal, not a public API).
            string binPath = _binPath;
            List<(long Start, long End, bool Audio)>? spans = null;
            if (Path.GetExtension(_binPath).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            {
                var cue = CueSheet.Parse(File.ReadAllText(_binPath));
                if (cue.Tracks.Count == 0) throw new InvalidDataException("The cue lists no tracks.");
                var files = cue.Tracks.Select(t => t.File).Distinct().ToList();
                if (files.Count != 1)
                    throw new InvalidDataException(
                        "disc-mri needs a single-file cue (one FILE, INDEX-split tracks). " +
                        "For one-file-per-track sets, run it on the individual bins.");
                string dir = Path.GetDirectoryName(Path.GetFullPath(_binPath))!;
                binPath = Path.Combine(dir, files[0]);
                if (!File.Exists(binPath))
                {
                    string alt = Path.Combine(dir, Path.GetFileNameWithoutExtension(_binPath) + Path.GetExtension(files[0]));
                    if (File.Exists(alt)) binPath = alt;
                    else throw new FileNotFoundException($"Bin referenced by the cue not found: {files[0]}");
                }

                long totalSectors = new FileInfo(binPath).Length / 2352;
                var ordered = cue.Tracks.OrderBy(t => t.Number).ToList();
                spans = new List<(long, long, bool)>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    long start = ordered[i].Indices.Min(x => x.Time.ToSectors());
                    long end = (i + 1 < ordered.Count
                        ? ordered[i + 1].Indices.Min(x => x.Time.ToSectors()) : totalSectors) - 1;
                    if (end >= start)
                        spans.Add((start, end, ordered[i].Type == CueTrackType.Audio));
                }
            }

            long imgSectors = new FileInfo(binPath).Length / 2352;
            if (imgSectors == 0) throw new InvalidDataException($"'{binPath}' holds no 2352-byte sectors.");
            bool truncatedWarning = new FileInfo(binPath).Length % 2352 != 0;

            BadSectorMap? map = null;
            string sidecar = _mapOverride ?? BadSectorMap.SidecarPath(binPath);
            if (File.Exists(sidecar)) map = BadSectorMap.Load(sidecar);
            else if (_mapOverride is not null) throw new FileNotFoundException($"Sidecar not found: {_mapOverride}");

            using (var fs = File.OpenRead(binPath))
                _evidence = DiscMri.Classify(fs, spans, map);

            _title = $"Disc MRI — {Path.GetFileName(binPath)}" +
                     (map is not null ? " (+ dump sidecar)" : "") +
                     (spans is not null ? $" · {spans.Count} track span(s)" : " · no cue: audio/void ambiguous");

            int sizePx = (int)_size.Value;
            var pngBytes = DiscMri.RenderPng(_evidence, sizePx);
            using (var ms = new MemoryStream(pngBytes))
            using (var raw = Image.FromStream(ms))
            {
                var bmp = new Bitmap(raw);        // fully detach from the MemoryStream before it's disposed
                var old = _preview.Image;
                _preview.Image = bmp;
                old?.Dispose();
            }

            var totals = _evidence.GroupBy(e => e).ToDictionary(g => g.Key, g => g.LongCount());
            long damage = totals.Where(kv => kv.Key >= DiscMri.Evidence.EdcFailed).Sum(kv => kv.Value);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(_title);
            sb.AppendLine();
            sb.AppendLine($"{_evidence.Length:N0} sectors mapped to the physical spiral.");
            sb.AppendLine(damage > 0 ? $"{damage:N0} sector(s) carry damage evidence." : "No damage evidence.");
            if (truncatedWarning)
                sb.AppendLine("warning: file length is not a whole number of 2352-byte sectors — trailing bytes ignored.");
            if (spans is null)
                sb.AppendLine("note: no cue given — sync-less sectors could be audio OR voids; " +
                              "run on the .cue (or a --disc dump) for an unambiguous map.");
            sb.AppendLine();
            foreach (var kv in totals.OrderByDescending(kv => (byte)kv.Key))
                sb.AppendLine($"  {kv.Key,-14} {kv.Value,10:N0}");
            _log.Text = sb.ToString();

            _saveAs.Enabled = true;
            _planReread.Enabled = true;
            _plan = null;
            _savePlan.Enabled = false;
            StatusBus.Report($"Disc MRI: mapped {Path.GetFileName(binPath)}" + (damage > 0 ? $", {damage:N0} damaged" : ""));
            AppLog.Write($"disc-mri {Path.GetFileName(_binPath)}");
        }
        catch (Exception ex)
        {
            _log.Text = "Render failed: " + ex.Message;
            _saveAs.Enabled = false;
            _planReread.Enabled = false;
            _savePlan.Enabled = false;
            AppLog.WriteException("disc-mri", ex);
        }
    }

    /// <summary>
    /// GUI mirror of <c>dforge disc-mri --plan-reread</c>: turns the same evidence just rendered into a
    /// targeted, escalating re-read plan (coalesced ranges + suggested pass count), offline — this
    /// button plans, it never touches a live drive. Save Plan writes the identical JSON shape the CLI
    /// writes, so it can be fed straight into <c>dforge disc-mri-reread</c> for the actual live re-read.
    /// </summary>
    private void DoPlanReread()
    {
        if (_evidence is null) return;
        try
        {
            _plan = DiscMri.PlanReread(_evidence);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(_title);
            sb.AppendLine();
            if (_plan.Nothing)
            {
                sb.AppendLine("plan-reread: nothing to re-read — no EDC-failed, void or unreadable sectors.");
            }
            else
            {
                sb.AppendLine($"plan-reread: {_plan.Ranges.Count} range(s), {_plan.Ranges.Sum(r => r.Count):N0} sector(s) total, " +
                              $"{_plan.SuggestedPasses} suggested pass(es).");
                sb.AppendLine($"  {_plan.Strategy}");
                foreach (var r in _plan.Ranges.Take(50))
                    sb.AppendLine($"    [{r.StartSector:N0} .. {r.StartSector + r.Count - 1:N0}]  {r.Count:N0} sector(s)  worst={r.Worst}");
                if (_plan.Ranges.Count > 50) sb.AppendLine($"    ... and {_plan.Ranges.Count - 50} more range(s).");
                sb.AppendLine();
                sb.AppendLine("Save Plan, then feed it to `dforge disc-mri-reread <drive> <plan.json> <target.bin>` to actually");
                sb.AppendLine("drive these ranges through a real drive's Tier-B adaptive re-read and patch what's recovered.");
                sb.AppendLine("This view never touches a live drive — planning stays purely offline, same as the CLI.");
            }
            _log.Text = sb.ToString();
            _savePlan.Enabled = !_plan.Nothing;
            StatusBus.Report(_plan.Nothing ? "Disc MRI: plan-reread — nothing to re-read"
                : $"Disc MRI: plan-reread — {_plan.Ranges.Count} range(s), {_plan.Ranges.Sum(r => r.Count):N0} sector(s)");
            AppLog.Write($"disc-mri --plan-reread {Path.GetFileName(_binPath)}");
        }
        catch (Exception ex)
        {
            _log.Text += Environment.NewLine + "Plan re-read failed: " + ex.Message;
            _savePlan.Enabled = false;
            AppLog.WriteException("disc-mri --plan-reread", ex);
        }
    }

    private void DoSavePlan()
    {
        if (_plan is null || _plan.Nothing) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "Re-read plan JSON (*.json)|*.json|All files (*.*)|*.*",
            FileName = Path.ChangeExtension(Path.GetFileName(_binPath), ".reread-plan.json"),
        };
        if (_binPath is not null) dlg.InitialDirectory = Path.GetDirectoryName(_binPath);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            // Same options the CLI uses (`dforge disc-mri --plan-reread out.json`), so a plan saved here
            // is byte-for-byte interchangeable with one the CLI wrote — either can feed `disc-mri-reread`.
            var jsonOpts = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            };
            File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(_plan, jsonOpts));
            _log.Text += Environment.NewLine + $"Wrote {Path.GetFileName(dlg.FileName)}.";
            AppLog.Write($"disc-mri --plan-reread saved {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            _log.Text += Environment.NewLine + "Save failed: " + ex.Message;
            AppLog.WriteException("disc-mri --plan-reread save", ex);
        }
    }

    private void DoSaveAs()
    {
        if (_evidence is null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "SVG map + legend (*.svg)|*.svg|PNG bare map (*.png)|*.png",
            FileName = Path.ChangeExtension(Path.GetFileName(_binPath), ".mri.svg"),
        };
        if (_binPath is not null) dlg.InitialDirectory = Path.GetDirectoryName(_binPath);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            bool png = Path.GetExtension(dlg.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase);
            int sizePx = (int)_size.Value;
            if (png)
                File.WriteAllBytes(dlg.FileName, DiscMri.RenderPng(_evidence, sizePx));
            else
                File.WriteAllText(dlg.FileName, DiscMri.RenderSvg(_evidence, _title, sizePx));

            _log.Text += Environment.NewLine + $"Wrote {Path.GetFileName(dlg.FileName)}.";
            AppLog.Write($"disc-mri saved {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            _log.Text += Environment.NewLine + "Save failed: " + ex.Message;
            AppLog.WriteException("disc-mri save", ex);
        }
    }
}
