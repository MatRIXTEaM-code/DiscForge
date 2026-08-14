// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Cdi;
using DiscForge.Core.Cue;
using DiscForge.Core.Raw;

namespace DiscForge.App.Views;

/// <summary>
/// The Raw Lab: the CLI's inspect-raw and build-raw, with buttons. Analyse
/// any raw image (or bare BIN) — TOC, Q health, CD-TEXT, MCN/ISRC, scramble
/// state, EDC/ECC — and compose DAO images from CUE sheets or CDIs offline,
/// exactly what the burner would write, for inspection before a disc is
/// ever risked.
/// </summary>
internal sealed class RawLabView : UserControl
{
    // ---- analyse ------------------------------------------------------------
    private readonly TextBox _inPath = new() { ReadOnly = true, Width = 420, Font = Theme.Ui };
    private readonly CheckBox _deep = new() { Text = "Deep (every sector)", AutoSize = true, Font = Theme.Ui };
    private readonly Button _analyse = new() { Text = "Analyse", Width = 90, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _report = new()
    {
        Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both,
        Font = Theme.Mono, Size = new Size(668, 200), BackColor = Color.White,
    };

    // ---- compose ------------------------------------------------------------
    private readonly TextBox _srcPath = new() { ReadOnly = true, Width = 420, Font = Theme.Ui };
    private readonly ComboBox _form = new()
    {
        Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
    };
    private readonly Button _compose = new() { Text = "Compose…", Width = 90, FlatStyle = FlatStyle.System, Enabled = false };

    private readonly ProgressBar _progress = new() { Size = new Size(668, 14), Style = ProgressBarStyle.Continuous };
    private readonly EventLogView _log = new() { Size = new Size(668, 110) };
    private bool _busy;

    public RawLabView()
    {
        Size = new Size(736, 620);
        BackColor = Color.White;
        Padding = new Padding(12);
        AutoScroll = true;

        var analyse = new GroupBox
        {
            Text = "Analyse raw image (inspect-raw)", Font = Theme.UiBold, ForeColor = Theme.Text,
            Location = new Point(12, 12), Size = new Size(700, 292),
        };
        var browseIn = new Button { Text = "Browse…", Width = 80, FlatStyle = FlatStyle.System, Location = new Point(438, 24), Font = Theme.Ui };
        _inPath.Location = new Point(12, 26);
        _deep.Location = new Point(524, 27);
        _analyse.Location = new Point(12, 54);
        _report.Location = new Point(12, 84);

        browseIn.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Raw images / BINs (*.img;*.bin;*.raw)|*.img;*.bin;*.raw|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            _inPath.Text = dlg.FileName;
            _analyse.Enabled = !_busy;
            _report.Text = "";
        };
        _analyse.Click += async (_, _) => await AnalyseAsync();

        foreach (Control c in new Control[] { _inPath, browseIn, _deep, _analyse, _report })
            analyse.Controls.Add(c);

        var compose = new GroupBox
        {
            Text = "Compose raw image (build-raw)", Font = Theme.UiBold, ForeColor = Theme.Text,
            Location = new Point(12, 314), Size = new Size(700, 100),
        };
        var browseSrc = new Button { Text = "Browse…", Width = 80, FlatStyle = FlatStyle.System, Location = new Point(438, 24), Font = Theme.Ui };
        _srcPath.Location = new Point(12, 26);
        _form.Location = new Point(12, 58);
        _form.Items.AddRange(new object[]
        {
            "Packed 96 (cooked, 2448)", "PQ-16 (2368)", "Interleaved 96 (2448)",
        });
        _form.SelectedIndex = 0;
        _compose.Location = new Point(174, 56);
        var hint = new Label
        {
            Text = "From a .cue (full semantics: gaps, indexes, FLAGS, ISRC/MCN, CD-TEXT, .sub sidecars) or a single-session .cdi.",
            AutoSize = true, Location = new Point(276, 60), Font = Theme.Small, ForeColor = Theme.TextMuted,
        };

        browseSrc.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "CUE sheets / CDI images (*.cue;*.cdi)|*.cue;*.cdi|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            _srcPath.Text = dlg.FileName;
            _compose.Enabled = !_busy;
        };
        _compose.Click += async (_, _) => await ComposeAsync();

        foreach (Control c in new Control[] { _srcPath, browseSrc, _form, _compose, hint })
            compose.Controls.Add(c);

        _progress.Location = new Point(24, 424);
        _log.Location = new Point(24, 448);

        Controls.Add(analyse);
        Controls.Add(compose);
        Controls.Add(_progress);
        Controls.Add(_log);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _analyse.Enabled = !busy && _inPath.Text.Length > 0;
        _compose.Enabled = !busy && _srcPath.Text.Length > 0;
        if (!busy) _progress.Value = 0;
    }

    private RawSubcodeForm SelectedForm() => _form.SelectedIndex switch
    {
        1 => RawSubcodeForm.Pq16,
        2 => RawSubcodeForm.Interleaved96,
        _ => RawSubcodeForm.Packed96,
    };

    private async Task AnalyseAsync()
    {
        if (_busy || _inPath.Text.Length == 0) return;
        var path = _inPath.Text;
        bool deep = _deep.Checked;
        SetBusy(true);
        _report.Text = deep ? "Analysing every sector — this reads the whole file…" : "Analysing…";
        try
        {
            var r = await Task.Run(() =>
            {
                using var fs = File.OpenRead(path);
                return RawImageInspector.Inspect(fs, deep);
            });
            _report.Text = RenderReport(Path.GetFileName(path), new FileInfo(path).Length, r, deep);
            bool clean = r.QCrcErrors == 0 && r.Tracks.All(t => t.EdcErrors == 0 && t.EccErrors == 0);
            _log.Add(clean ? $"{Path.GetFileName(path)}: clean."
                           : $"{Path.GetFileName(path)}: problems found — see the report.",
                clean ? EventLogView.Level.Good : EventLogView.Level.Error);
        }
        catch (Exception ex)
        {
            _report.Text = ex.Message;
            _log.Add("Analysis failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("rawlab analyse", ex);
        }
        finally { SetBusy(false); }
    }

    private static string RenderReport(string name, long bytes,
                                       RawImageInspector.Report r, bool deep)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File:        {name} ({bytes:N0} bytes)");
        sb.AppendLine($"Format:      {r.SectorSize} bytes/sector — " +
            (r.Form is null ? "main channel only (no subcode)" : r.Form.ToString()));
        sb.AppendLine($"Sectors:     {r.TotalSectors:N0}" +
            (r.HasLeadIn ? $" ({r.LeadInSectors:N0} lead-in + {r.TotalSectors - r.LeadInSectors:N0} program)"
                         : r.Form is null ? "" : " (no lead-in — program-only rip)"));
        if (r.Form is not null)
        {
            sb.AppendLine($"Q integrity: {r.QFramesChecked - r.QCrcErrors}/{r.QFramesChecked} " +
                $"frames CRC-valid{(deep ? "" : " (sampled)")}" +
                (r.QCrcErrors > 0 ? $"   <-- {r.QCrcErrors} BAD" : ""));
            if (r.LeadOutStartSector > 0)
                sb.AppendLine($"Lead-out:    {Msf.FromSectors(r.LeadOutStartSector)} " +
                              $"({r.LeadOutStartSector:N0} sectors)");
            if (r.Mcn is not null) sb.AppendLine($"MCN:         {r.Mcn}");
            if (r.AlbumTitle is not null || r.AlbumPerformer is not null)
            {
                sb.AppendLine($"CD-TEXT:     \"{r.AlbumTitle}\"" +
                    (r.AlbumPerformer is not null ? $" — {r.AlbumPerformer}" : "") +
                    $"  ({r.CdTextPacksValid} packs valid" +
                    (r.CdTextPacksBad > 0 ? $", {r.CdTextPacksBad} bad" : "") + ")");
                for (int i = 0; i < r.TrackTitles.Count; i++)
                    sb.AppendLine($"             {i + 1:D2}. {r.TrackTitles[i]}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(" #  Type   Start MSF    ISRC          Data checks");
        sb.AppendLine(" -- -----  -----------  ------------  --------------------------------");
        foreach (var t in r.Tracks)
        {
            string kind = t.IsData ? $"Data{(t.Mode is { } m ? $"{m}" : "")}" : "Audio";
            string data = "";
            if (t.IsData)
            {
                data = t.Scrambled switch
                {
                    true => "scrambled", false => "unscrambled", null => "undetermined",
                };
                if (t.DataSectorsChecked > 0)
                    data += t.EdcErrors == 0 && t.EccErrors == 0
                        ? $"; {t.CheckKind} OK ({t.DataSectorsChecked} checked)"
                        : $"; {t.CheckKind}: EDC errs {t.EdcErrors}, ECC errs {t.EccErrors} " +
                          $"of {t.DataSectorsChecked}  <-- BAD";
                else if (t.CheckKind is not null) data += $"; checks: {t.CheckKind}";
            }
            sb.AppendLine($" {t.Number:D2} {kind,-6} {Msf.FromSectors(t.StartSector),-12} " +
                          $"{t.Isrc ?? "-",-13} {data}");
        }
        foreach (var n in r.Notes) sb.AppendLine($"note: {n}");
        return sb.ToString();
    }

    private async Task ComposeAsync()
    {
        if (_busy || _srcPath.Text.Length == 0) return;
        var src = _srcPath.Text;
        var form = SelectedForm();

        using var save = new SaveFileDialog
        {
            Filter = "Raw images (*.img)|*.img|All files (*.*)|*.*",
            FileName = Path.GetFileNameWithoutExtension(src) + ".img",
        };
        if (save.ShowDialog() != DialogResult.OK) return;
        var outPath = save.FileName;

        SetBusy(true);
        try
        {
            var bar = new Progress<double>(f =>
                _progress.Value = Math.Clamp((int)(f * 100), 0, 100));

            var summary = await Task.Run(() =>
            {
                using Stream? cdiStream = src.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)
                    ? null : File.OpenRead(src);
                using var layout = cdiStream is null
                    ? DiscLayout.FromCueFile(src)
                    : DiscLayout.FromCdi(CdiParser.Parse(cdiStream), cdiStream);

                long total = RawImageGenerator.TotalSectors(layout);
                using (var output = File.Create(outPath))
                    RawImageGenerator.Generate(layout, form, output, bar);
                return $"{layout.Tracks.Count} track(s), {total:N0} sectors × " +
                       $"{RawImageGenerator.SectorSize(form)} bytes" +
                       (layout.Mcn is not null ? ", MCN" : "") +
                       (!layout.CdText.IsEmpty ? ", CD-TEXT" : "") +
                       (layout.HasProgramRw ? ", CD+G sub-channels" : "");
            });

            _log.Add($"Composed {Path.GetFileName(outPath)}: {summary}.", EventLogView.Level.Good);
            _log.Add("Tip: point Analyse (or the Sector Viewer) at it before burning.");
            _inPath.Text = outPath;
            _analyse.Enabled = true;
        }
        catch (Exception ex)
        {
            _log.Add("Compose failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("rawlab compose", ex);
        }
        finally { SetBusy(false); }
    }
}
