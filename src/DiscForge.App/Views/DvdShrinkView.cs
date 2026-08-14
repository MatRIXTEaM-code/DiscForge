// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.DvdVideo;

namespace DiscForge.App.Views;

/// <summary>
/// DVD-Video structure + shrink planning: point it at a VIDEO_TS folder and it
/// shows the title sets, titles and streams, then computes a DVD-5/DVD-9 fit
/// plan with the compression ratio needed. The same <see cref="IfoReader"/> and
/// <see cref="BitBudget"/> the CLI uses.
///
/// This view plans; the actual re-encode (FFmpeg) and VIDEO_TS rebuild
/// (dvdauthor) are separate steps surfaced as guidance, since they need those
/// external tools installed. CSS-encrypted video is never processed.
/// </summary>
internal sealed class DvdShrinkView : UserControl
{
    private readonly TextBox _path = new() { ReadOnly = true, Width = 440, Font = Theme.Ui };
    private readonly Button _browse = new() { Text = "Browse…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly Button _analyse = new() { Text = "Analyse", Width = 90, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly ComboBox _target = new()
    {
        Width = 160, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
    };
    private readonly TextBox _out = new()
    {
        Multiline = true, ReadOnly = true, Font = Theme.Mono, ScrollBars = ScrollBars.Vertical,
        Size = new Size(660, 300),
    };

    private IfoReader.DvdStructure? _dvd;

    public DvdShrinkView()
    {
        Size = new Size(720, 480);
        BackColor = Color.White;

        var title = new Label
        {
            Text = "DVD-Video shrink", Font = Theme.UiBold, AutoSize = true, Location = new Point(16, 12),
        };
        var hint = new Label
        {
            Text = "Reads a VIDEO_TS folder, shows its structure, and plans a fit to a target disc. " +
                   "Unprotected / personally-authored DVD-Video only.",
            Font = Theme.Ui, AutoSize = false, Size = new Size(680, 32), Location = new Point(16, 34),
        };

        _path.Location = new Point(16, 72);
        _browse.Location = new Point(464, 70);
        _analyse.Location = new Point(552, 70);

        var targetLabel = new Label { Text = "Target:", AutoSize = true, Font = Theme.Ui, Location = new Point(16, 106) };
        _target.Location = new Point(70, 103);
        _target.Items.AddRange(new object[] { "DVD-5 (4.7 GB)", "DVD-9 (8.5 GB)" });
        _target.SelectedIndex = 0;
        _target.SelectedIndexChanged += (_, _) => Replan();

        _out.Location = new Point(16, 136);

        _browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Select a VIDEO_TS folder or disc root" };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _path.Text = dlg.SelectedPath;
                _analyse.Enabled = true;
            }
        };

        _analyse.Click += (_, _) => Analyse();

        Controls.Add(title);
        Controls.Add(hint);
        Controls.Add(_path);
        Controls.Add(_browse);
        Controls.Add(_analyse);
        Controls.Add(targetLabel);
        Controls.Add(_target);
        Controls.Add(_out);
    }

    private long TargetBytes => _target.SelectedIndex == 1 ? BitBudget.Dvd9 : BitBudget.Dvd5;

    private void Analyse()
    {
        _analyse.Enabled = false;
        StatusBus.Report("Reading DVD-Video structure…");
        try
        {
            var src = new VideoTsSources.Folder(_path.Text);
            _dvd = IfoReader.Read(src);
            Replan();
            StatusBus.Report("DVD-Video analysis complete.");
        }
        catch (IfoFormatException ex)
        {
            _out.Text = "Not a DVD-Video volume: " + ex.Message;
            StatusBus.Report("Analysis failed.");
        }
        catch (Exception ex)
        {
            _out.Text = "Analysis failed: " + ex.Message;
            StatusBus.Report("Analysis failed.");
        }
        finally
        {
            _analyse.Enabled = true;
        }
    }

    private void Replan()
    {
        if (_dvd is null) return;
        var dvd = _dvd;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(dvd.Summary);
        sb.AppendLine();

        foreach (var ts in dvd.TitleSets)
        {
            sb.AppendLine($"Title set {ts.Number}:  video {ts.TitleVobBytes:N0} B, menu {ts.MenuVobBytes:N0} B");
            foreach (var t in ts.Titles)
            {
                sb.AppendLine($"  Title {t.TitleNumber}: {t.Chapters} chapter(s), {t.AngleCount} angle(s)");
                foreach (var a in t.Audio)
                    sb.AppendLine($"    audio {a.Index}: {a.Codec} {a.Channels}ch " +
                                  $"[{(string.IsNullOrWhiteSpace(a.Language) ? "und" : a.Language)}]");
                foreach (var s in t.Subtitles)
                    sb.AppendLine($"    sub   {s.Index}: [{(string.IsNullOrWhiteSpace(s.Language) ? "und" : s.Language)}]");
            }
        }

        var reqs = dvd.TitleSets.Select(ts => new BitBudget.TitlePlanRequest
        {
            Title = new BitBudget.TitleSizes
            {
                Name = $"VTS {ts.Number}", VideoBytes = ts.TitleVobBytes, OverheadBytes = ts.MenuVobBytes,
            },
            Mode = BitBudget.Mode.Automatic,
        }).ToList();

        var plan = BitBudget.Compute(reqs, TargetBytes);
        sb.AppendLine();
        sb.AppendLine($"Shrink plan ({(_target.SelectedIndex == 1 ? "DVD-9" : "DVD-5")}, full disc, automatic):");
        sb.AppendLine("  " + plan.Summary);
        if (plan.AutomaticRatio < 1.0)
            sb.AppendLine($"  Video would be compressed to {plan.AutomaticRatio:P0} of original.");
        sb.AppendLine();
        sb.AppendLine("The re-encode (FFmpeg) and VIDEO_TS rebuild (dvdauthor) run as separate");
        sb.AppendLine("steps and need those tools installed. CSS-encrypted video is never processed.");

        _out.Text = sb.ToString();
    }
}
