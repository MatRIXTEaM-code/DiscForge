// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Cdi;

namespace DiscForge.App.Views;

/// <summary>Open a .cdi, display its structure in a track grid, and verify it.</summary>
internal sealed class InspectView : UserControl
{
    private readonly TextBox _path = new() { ReadOnly = true, Width = 460, Font = Theme.Ui };
    private readonly Label _summary = new() { AutoSize = true, Font = Theme.UiBold, Location = new Point(12, 52) };
    private readonly DataGridView _grid = new()
    {
        Location = new Point(12, 78), Size = new Size(712, 300),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        ReadOnly = true, AllowUserToAddRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        Font = Theme.Ui, BackgroundColor = Color.White,
    };
    private readonly TextBox _issues = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 390), Size = new Size(712, 72),
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private string? _openPath;

    public InspectView()
    {
        // A UserControl defaults to 150x150; children anchored Left|Right would
        // then grow by the delta when docked, running off-screen. Establish a
        // realistic size FIRST so the anchor baseline is sane.
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = FirstCdi(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { var p = FirstCdi(e); if (p is not null) Open(p); };

        var open = new Button { Text = "Open CDI…", Location = new Point(12, 12), Width = 96, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => OpenFile();
        var verify = new Button { Text = "Verify (CRC-32)", Location = new Point(590, 12), Width = 120, FlatStyle = FlatStyle.System };
        verify.Click += async (_, _) => await VerifyAsync();

        _path.Location = new Point(116, 14);

        _grid.Columns.Add(Col("Num", "#", 40));
        _grid.Columns.Add(Col("Session", "Ses", 50));
        _grid.Columns.Add(Col("Mode", "Mode", 70));
        _grid.Columns.Add(Col("Sector", "Sector", 70));
        _grid.Columns.Add(Col("Pregap", "Pregap", 70));
        _grid.Columns.Add(Col("Length", "Length", 90));
        _grid.Columns.Add(Col("Lba", "Start LBA", 90));
        _grid.Columns.Add(Col("Offset", "File offset", 120));
        _grid.Columns.Add(Col("File", "Filename", 140));

        Controls.Add(open);
        Controls.Add(verify);
        Controls.Add(_path);
        Controls.Add(_summary);
        Controls.Add(_grid);
        Controls.Add(_issues);
    }

    private static DataGridViewTextBoxColumn Col(string name, string header, int width) =>
        new() { Name = name, HeaderText = header, FillWeight = width };

    private void OpenFile()
    {
        using var dlg = new OpenFileDialog { Filter = "CDI images (*.cdi)|*.cdi|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        Open(dlg.FileName);
    }

    /// <summary>Open a specific CDI path (used by menu/drag-drop routing).</summary>
    public void Open(string path)
    {
        _openPath = path;
        _path.Text = path;
        if (File.Exists(path)) Settings.AddRecent(path);
        LoadImage();
    }

    private static string? FirstCdi(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files)
                if (Path.GetExtension(f).Equals(".cdi", StringComparison.OrdinalIgnoreCase))
                    return f;
        return null;
    }

    private void LoadImage()
    {
        _grid.Rows.Clear();
        _issues.Clear();
        if (_openPath is null) return;
        try
        {
            using var fs = File.OpenRead(_openPath);
            var image = CdiParser.Parse(fs);
            _summary.Text = $"{Label(image.Version)}   {image.Sessions.Count} session(s)   {image.TrackCount} track(s)";

            // Mirror the structure into diagnostics: a bug report about an image
            // is useless without knowing what the image actually is.
            AppLog.Write($"Inspect {Path.GetFileName(_openPath)}: {Label(image.Version)}, " +
                         $"{image.Sessions.Count} session(s), {image.TrackCount} track(s)");
            foreach (var t in image.AllTracks)
                AppLog.Write($"  track {t.Number}: session {t.SessionIndex + 1} {t.Mode} " +
                             $"{(int)t.SectorSize}b/sector pregap={t.PregapSectors} " +
                             $"length={t.LengthSectors} lba={t.StartLba} offset={t.FileOffset}");
            foreach (var t in image.AllTracks)
                _grid.Rows.Add(t.Number, t.SessionIndex + 1, t.Mode, (int)t.SectorSize,
                    t.PregapSectors, t.LengthSectors, t.StartLba, t.FileOffset.ToString("N0"),
                    t.SourceFilename ?? "");
            StatusBus.Report($"Loaded {Path.GetFileName(_openPath)} — {image.TrackCount} track(s)");
        }
        catch (CdiFormatException ex)
        {
            _summary.Text = "Not a valid CDI image.";
            _issues.Text = ex.Message;
            AppLog.WriteException($"inspect {Path.GetFileName(_openPath)}", ex);
        }
    }

    private async Task VerifyAsync()
    {
        if (_openPath is null) { RetroMessageBox.Show("Open a CDI first."); return; }
        _issues.Text = "Verifying…";
        AppLog.Write($"Verify {Path.GetFileName(_openPath)} (with CRC-32)…");
        try
        {
            var (passed, lines) = await Task.Run(() =>
            {
                using var fs = File.OpenRead(_openPath);
                var image = CdiParser.Parse(fs);
                var report = CdiVerifier.Verify(fs, image, computeUserChecksums: true);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(report.Passed
                    ? (report.HasWarnings ? "PASS (with warnings)" : "PASS")
                    : "FAIL");
                foreach (var c in report.Checksums)
                    sb.AppendLine($"  track {c.TrackNumber}: {c.StoredBytes:N0} bytes  CRC32 {c.StoredCrc32:X8}");
                foreach (var i in report.Issues)
                    sb.AppendLine($"  [{i.Severity}] {i.Message}");
                return (report.Passed, sb.ToString());
            });
            _issues.Text = lines;

            // The whole point of a verify is its result — capture it verbatim,
            // CRCs and all, so it can be pasted into a report or compared later.
            foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                AppLog.Write("  " + line.TrimEnd('\r'));
        }
        catch (Exception ex)
        {
            _issues.Text = ex.Message;
            AppLog.WriteException($"verify {Path.GetFileName(_openPath)}", ex);
        }
    }

    private static string Label(CdiVersion v) => v switch
    {
        CdiVersion.V2 => "CDI v2", CdiVersion.V3 => "CDI v3",
        CdiVersion.V35 => "CDI v3.5/4", _ => "CDI",
    };
}
