// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Dat;
using DiscForge.Core.Library;

namespace DiscForge.App.Views;

/// <summary>
/// Collection set tools: build a 1G1R ("one game, one ROM") subset of a DAT by region
/// priority and save it as a filtered DAT, and rebuild a messy folder into a clean,
/// DAT-named set. Thin shells over <see cref="OneGameOneRom"/> / <see cref="DatWriter"/>
/// and <see cref="SetRebuilder"/> — selection and file placement only, never game data.
/// </summary>
internal sealed class SetsView : UserControl
{
    // 1G1R
    private readonly TextBox _dat = new() { ReadOnly = true, Location = new Point(70, 40), Width = 380, Font = Theme.Ui };
    private readonly TextBox _regions = new() { Location = new Point(120, 70), Width = 330, Font = Theme.Mono, Text = "USA,World,Europe,Japan" };
    private readonly CheckBox _keepProto = new() { Text = "Keep proto/beta", AutoSize = true, Location = new Point(120, 98), Font = Theme.Ui };
    private readonly Label _1g1rStatus = new() { AutoSize = true, Location = new Point(120, 122), Font = Theme.Ui, ForeColor = Color.Gray };
    private string? _datPath;
    private OneGameOneRomReport? _report;
    private string? _datName;

    // Rebuild
    private readonly TextBox _src = new() { ReadOnly = true, Location = new Point(70, 40), Width = 380, Font = Theme.Ui };
    private readonly TextBox _dest = new() { ReadOnly = true, Location = new Point(70, 70), Width = 380, Font = Theme.Ui };
    private readonly TextBox _rbDat = new() { ReadOnly = true, Location = new Point(70, 100), Width = 380, Font = Theme.Ui };
    private readonly CheckBox _perGame = new() { Text = "Folder per game", AutoSize = true, Location = new Point(70, 130), Font = Theme.Ui };
    private readonly CheckBox _move = new() { Text = "Move (not copy)", AutoSize = true, Location = new Point(210, 130), Font = Theme.Ui };
    private readonly Label _rbStatus = new() { AutoSize = true, Location = new Point(70, 158), Font = Theme.Ui, ForeColor = Color.Gray };
    private string? _srcPath, _destPath, _rbDatPath;

    public SetsView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        // --- 1G1R box ---
        var g = new GroupBox { Text = "1G1R — one game, one ROM (from a DAT)", Location = new Point(12, 6), Size = new Size(710, 158), Font = Theme.UiBold };
        g.Controls.Add(new Label { Text = "DAT:", AutoSize = true, Location = new Point(12, 43), Font = Theme.Ui });
        g.Controls.Add(new Label { Text = "Region priority:", AutoSize = true, Location = new Point(12, 73), Font = Theme.Ui });
        var pickDat = new Button { Text = "…", Location = new Point(456, 39), Width = 30, FlatStyle = FlatStyle.System };
        pickDat.Click += (_, _) => ChooseDat();
        var analyze = new Button { Text = "Analyze", Location = new Point(492, 38), Width = 90, FlatStyle = FlatStyle.System };
        analyze.Click += (_, _) => Analyze();
        var saveDat = new Button { Text = "Save filtered DAT…", Location = new Point(456, 94), Width = 130, FlatStyle = FlatStyle.System };
        saveDat.Click += (_, _) => SaveFilteredDat();
        g.Controls.AddRange(new Control[] { _dat, pickDat, analyze, _regions, _keepProto, saveDat, _1g1rStatus });

        // --- Rebuild box ---
        var rb = new GroupBox { Text = "Rebuild a clean, DAT-named set", Location = new Point(12, 172), Size = new Size(710, 218), Font = Theme.UiBold };
        rb.Controls.Add(new Label { Text = "Source:", AutoSize = true, Location = new Point(12, 43), Font = Theme.Ui });
        rb.Controls.Add(new Label { Text = "Dest:", AutoSize = true, Location = new Point(12, 73), Font = Theme.Ui });
        rb.Controls.Add(new Label { Text = "DAT:", AutoSize = true, Location = new Point(12, 103), Font = Theme.Ui });
        var pickSrc = new Button { Text = "…", Location = new Point(456, 39), Width = 30, FlatStyle = FlatStyle.System };
        pickSrc.Click += (_, _) => { if (Folder() is { } p) { _srcPath = p; _src.Text = p; } };
        var pickDest = new Button { Text = "…", Location = new Point(456, 69), Width = 30, FlatStyle = FlatStyle.System };
        pickDest.Click += (_, _) => { if (Folder() is { } p) { _destPath = p; _dest.Text = p; } };
        var pickRbDat = new Button { Text = "…", Location = new Point(456, 99), Width = 30, FlatStyle = FlatStyle.System };
        pickRbDat.Click += (_, _) => { if (ChooseDatPath() is { } p) { _rbDatPath = p; _rbDat.Text = p; } };
        var plan = new Button { Text = "Plan", Location = new Point(500, 128), Width = 80, FlatStyle = FlatStyle.System };
        plan.Click += async (_, _) => await Rebuild(false);
        var apply = new Button { Text = "Apply", Location = new Point(588, 128), Width = 80, FlatStyle = FlatStyle.System };
        apply.Click += async (_, _) => await Rebuild(true);
        rb.Controls.AddRange(new Control[] { _src, _dest, _rbDat, pickSrc, pickDest, pickRbDat, _perGame, _move, plan, apply, _rbStatus });

        Controls.Add(g);
        Controls.Add(rb);
    }

    // ---- 1G1R ----

    private void ChooseDat()
    {
        if (ChooseDatPath() is { } p) { _datPath = p; _dat.Text = p; _report = null; _1g1rStatus.Text = "Ready — click Analyze."; _1g1rStatus.ForeColor = Color.Gray; }
    }

    private OneGameOneRomOptions Options() => new()
    {
        RegionPriority = _regions.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        ExcludePrerelease = !_keepProto.Checked,
    };

    private void Analyze()
    {
        if (_datPath is null) { RetroMessageBox.Show("Choose a DAT first."); return; }
        try
        {
            using var fs = File.OpenRead(_datPath);
            var dat = DatFile.Parse(fs);
            _datName = dat.Name;
            _report = OneGameOneRom.Build(dat, Options());
            _1g1rStatus.Text = $"{_report.TotalGames:N0} games → {_report.Families:N0} kept.";
            _1g1rStatus.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
        }
        catch (Exception ex) { RetroMessageBox.Show(ex.Message); AppLog.WriteException("1g1r", ex); }
    }

    private void SaveFilteredDat()
    {
        if (_report is null) { RetroMessageBox.Show("Analyze a DAT first."); return; }
        using var dlg = new SaveFileDialog { Filter = "DAT (*.dat)|*.dat|XML (*.xml)|*.xml", FileName = "1g1r.dat" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, DatWriter.WriteLogiqx((_datName ?? "DAT") + " (1G1R)", _report.ChosenGames));
            StatusBus.Report($"Wrote {_report.Families} game(s) to {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) { RetroMessageBox.Show(ex.Message); }
    }

    // ---- Rebuild ----

    private async Task Rebuild(bool apply)
    {
        if (_srcPath is null || _destPath is null || _rbDatPath is null)
        { RetroMessageBox.Show("Choose a source, destination and DAT."); return; }

        _rbStatus.Text = apply ? "Rebuilding…" : "Planning…"; _rbStatus.ForeColor = Color.Black;
        try
        {
            string src = _srcPath, dest = _destPath, datPath = _rbDatPath;
            bool perGame = _perGame.Checked, move = _move.Checked;
            var (plan, placed) = await Task.Run(() =>
            {
                using var fs = File.OpenRead(datPath);
                var dat = DatFile.Parse(fs);
                var report = LibraryScanner.Scan(src, dat);
                var p = SetRebuilder.Plan(report, dest, perGame ? RebuildLayout.PerGameFolder : RebuildLayout.Flat);
                int n = apply ? SetRebuilder.Apply(p, move) : 0;
                return (p, n);
            });

            _rbStatus.Text = apply
                ? $"{(move ? "Moved" : "Copied")} {placed:N0} — missing {plan.Missing:N0}, unknown {plan.Unknown:N0}."
                : $"To place {plan.ToPlace:N0}, already in place {plan.AlreadyInPlace:N0}, missing {plan.Missing:N0}, unknown {plan.Unknown:N0}.";
            _rbStatus.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report(_rbStatus.Text);
        }
        catch (Exception ex)
        {
            _rbStatus.Text = "Failed."; _rbStatus.ForeColor = Color.FromArgb(0xA0, 0x30, 0x30);
            RetroMessageBox.Show(ex.Message); AppLog.WriteException("rebuild", ex);
        }
    }

    // ---- helpers ----

    private static string? ChooseDatPath()
    {
        using var dlg = new OpenFileDialog { Filter = "DAT files (*.dat;*.xml)|*.dat;*.xml|All files (*.*)|*.*" };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
    }

    private static string? Folder()
    {
        using var dlg = new FolderBrowserDialog { UseDescriptionForTitle = true };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
    }
}
