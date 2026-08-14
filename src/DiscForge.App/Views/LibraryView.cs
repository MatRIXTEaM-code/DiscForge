// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Dat;
using DiscForge.Core.Library;

namespace DiscForge.App.Views;

/// <summary>
/// The collection/library manager: point it at a folder (optionally with a Redump/
/// No-Intro DAT) and it identifies, hashes and verifies every file, reporting what is
/// confirmed-good, mis-named, duplicated, unrecognised or missing from the set — then
/// renames the verified-but-mis-named files to their canonical names. A thin shell over
/// <see cref="LibraryScanner"/>.
/// </summary>
internal sealed class LibraryView : UserControl
{
    private readonly TextBox _folder = new() { ReadOnly = true, Width = 360, Font = Theme.Ui, Location = new Point(90, 14) };
    private readonly TextBox _dat = new() { ReadOnly = true, Width = 360, Font = Theme.Ui, Location = new Point(90, 44) };
    private readonly Label _summary = new() { AutoSize = true, Font = Theme.UiBold, Location = new Point(12, 78) };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.Mono,
        Location = new Point(12, 104), Size = new Size(712, 300),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Color.White,
    };
    private readonly Button _scan = new() { Text = "Scan", Location = new Point(560, 12), Width = 74, FlatStyle = FlatStyle.System };
    private readonly Button _rename = new() { Text = "Rename mis-named…", Location = new Point(560, 44), Width = 164, FlatStyle = FlatStyle.System, Enabled = false };

    private string? _folderPath;
    private string? _datPath;
    private LibraryReport? _report;

    public LibraryView()
    {
        Size = new Size(736, 416);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Folder:", AutoSize = true, Location = new Point(12, 17), Font = Theme.Ui });
        Controls.Add(new Label { Text = "DAT (opt):", AutoSize = true, Location = new Point(12, 47), Font = Theme.Ui });
        var pickFolder = new Button { Text = "…", Location = new Point(456, 13), Width = 30, FlatStyle = FlatStyle.System };
        pickFolder.Click += (_, _) => ChooseFolder();
        var pickDat = new Button { Text = "…", Location = new Point(456, 43), Width = 30, FlatStyle = FlatStyle.System };
        pickDat.Click += (_, _) => ChooseDat();
        _scan.Click += async (_, _) => await ScanAsync();
        _rename.Click += (_, _) => DoRename();

        Controls.AddRange(new Control[] { _folder, _dat, pickFolder, pickDat, _scan, _rename, _summary, _log });
        _summary.Text = "Choose a folder, then Scan.";
        _summary.ForeColor = Color.Gray;
    }

    private void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose the folder to scan" };
        if (dlg.ShowDialog() == DialogResult.OK) { _folderPath = dlg.SelectedPath; _folder.Text = dlg.SelectedPath; }
    }

    private void ChooseDat()
    {
        using var dlg = new OpenFileDialog { Filter = "DAT files (*.dat;*.xml)|*.dat;*.xml|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == DialogResult.OK) { _datPath = dlg.FileName; _dat.Text = dlg.FileName; }
    }

    private async Task ScanAsync()
    {
        if (_folderPath is null) { RetroMessageBox.Show("Choose a folder to scan first."); return; }
        _scan.Enabled = false; _rename.Enabled = false;
        _summary.Text = "Scanning…"; _summary.ForeColor = Color.Black; _log.Clear();
        try
        {
            string folder = _folderPath; string? datPath = _datPath;
            var report = await Task.Run(() =>
            {
                DatFile? dat = null;
                if (datPath is not null) { using var ds = File.OpenRead(datPath); dat = DatFile.Parse(ds); }
                return LibraryScanner.Scan(folder, dat);
            });
            _report = report;
            ShowReport(report);
            _rename.Enabled = report.Misnamed > 0;
        }
        catch (Exception ex)
        {
            _summary.Text = "Scan failed."; _summary.ForeColor = Color.FromArgb(0xA0, 0x30, 0x30);
            _log.AppendText("Error: " + ex.Message + Environment.NewLine);
            AppLog.WriteException("library scan", ex);
        }
        finally { _scan.Enabled = true; }
    }

    private void ShowReport(LibraryReport r)
    {
        var sb = new StringBuilder();
        int shown = 0;
        foreach (var e in r.Entries)
        {
            if (shown++ >= 1000) { sb.AppendLine("  … (more)"); break; }
            string tag = e.Status.ToString().ToUpperInvariant();
            string extra = e.Match is not null ? $"  = {e.Match.Game}"
                         : e.RomPlatform.Length > 0 ? $"  [{e.RomPlatform}]"
                         : e.Format.Length > 0 ? $"  [{e.Format}]" : "";
            string sug = e.SuggestedName is not null ? $"  -> {e.SuggestedName}" : "";
            sb.AppendLine($"{tag,-9} {e.FileName}{extra}{sug}");
        }
        if (r.Missing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Missing from set ({r.Missing.Count}):");
            foreach (var m in r.Missing.Take(200)) sb.AppendLine($"  - {m.Name}  ({m.Game})");
            if (r.Missing.Count > 200) sb.AppendLine($"  … and {r.Missing.Count - 200} more");
        }
        _log.Text = sb.ToString();
        _summary.Text = $"{r.Total} file(s)   Verified {r.Verified}   Mis-named {r.Misnamed}   " +
                        $"Dupes {r.Duplicates}   Unknown {r.Unknown}" +
                        (r.DatName is not null ? $"   (DAT: {r.DatName})" : "");
        _summary.ForeColor = Color.FromArgb(0x20, 0x50, 0x20);
        StatusBus.Report($"Scanned {r.Total} file(s) — {r.Verified} verified");
    }

    private void DoRename()
    {
        if (_report is null) return;
        var plan = _report.RenamePlan();
        if (plan.Count == 0) { RetroMessageBox.Show("Nothing to rename."); return; }
        if (RetroMessageBox.Show($"Rename {plan.Count} mis-named file(s) to their canonical names?",
                "DiscForge", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        try
        {
            int n = LibraryScanner.ApplyRenames(plan);
            _log.AppendText(Environment.NewLine + $"Renamed {n} file(s)." + Environment.NewLine);
            StatusBus.Report($"Renamed {n} file(s)");
            AppLog.Write($"Library rename: {n} file(s) in {_folderPath}");
            _ = ScanAsync();   // refresh
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("library rename", ex);
        }
    }
}
