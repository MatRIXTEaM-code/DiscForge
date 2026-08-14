// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Udf;

namespace DiscForge.App.Views;

/// <summary>
/// Build a UDF 1.02 filesystem image from a folder — the GUI face of
/// <see cref="UdfBuilder"/> and the <c>create-udf</c> command. Pick a folder, set
/// a volume label, and write a <c>.udf</c>; the format work and its round-trip
/// validation live in Core, so this view only gathers the folder, the label and
/// the destination, and reports what Core built (including any warnings).
///
/// The in-memory builder has a ~2 GB ceiling; the view surfaces Core's own error
/// if a tree exceeds it rather than pretending otherwise.
/// </summary>
internal sealed class UdfCreateView : UserControl
{
    private readonly TextBox _folder = new()
    {
        ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(120, 14),
    };
    private readonly TextBox _volume = new()
    {
        Width = 200, Font = Theme.Ui, Location = new Point(120, 46), Text = "DISCFORGE", MaxLength = 30,
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Location = new Point(12, 82), Size = new Size(712, 18),
        Font = Theme.UiBold,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 110), Size = new Size(712, 330),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private string? _folderPath;

    public UdfCreateView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Folder:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Volume:", AutoSize = true, Location = new Point(12, 48), Font = Theme.Ui });

        var choose = new Button { Text = "Choose folder…", Location = new Point(600, 12), Width = 124, FlatStyle = FlatStyle.System };
        choose.Click += (_, _) => ChooseFolder();
        var build = new Button
        {
            Text = "Build UDF…", Location = new Point(600, 44), Width = 124, FlatStyle = FlatStyle.System,
        };
        build.Click += (_, _) => BuildUdf();

        Controls.AddRange(new Control[] { _folder, _volume, choose, build, _status, _log });

        _status.Text = "Choose a folder and press Build UDF to write a UDF 1.02 image.";
        _status.ForeColor = Color.Gray;
    }

    private void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Folder to build the UDF image from…",
            SelectedPath = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _folderPath = dlg.SelectedPath;
        _folder.Text = dlg.SelectedPath;
        AppSettings.LastImageDirectory = dlg.SelectedPath;

        // Offer the folder's own name as a sensible default volume label.
        var name = Path.GetFileName(dlg.SelectedPath.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(name))
            _volume.Text = name.Length > 30 ? name[..30] : name;

        Log($"Folder: {dlg.SelectedPath}");
    }

    private void BuildUdf()
    {
        if (_folderPath is null || !Directory.Exists(_folderPath))
        {
            RetroMessageBox.Show("Choose a folder to build from first.");
            return;
        }

        string volumeId = string.IsNullOrWhiteSpace(_volume.Text) ? "DISCFORGE" : _volume.Text.Trim();

        using var save = new SaveFileDialog
        {
            Filter = "UDF image (*.udf)|*.udf|All files (*.*)|*.*",
            FileName = volumeId + ".udf",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (save.ShowDialog() != DialogResult.OK) return;

        try
        {
            _status.Text = "Building…";
            _status.ForeColor = Color.Gray;

            var children = WalkFolder(_folderPath);
            IReadOnlyList<string> warnings;
            using (var output = File.Create(save.FileName))
                warnings = UdfBuilder.BuildToStream(volumeId, output, children);
            long size = new FileInfo(save.FileName).Length;

            foreach (var w in warnings) Log("  warning: " + w);
            _status.Text = $"Built {Path.GetFileName(save.FileName)}: UDF 1.02, " +
                           $"{size:N0} bytes ({size / 2048:N0} sectors), " +
                           $"volume \"{volumeId}\".";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            Log(_status.Text);
            StatusBus.Report(_status.Text);
            AppLog.Write($"UDF create {_folderPath} -> {Path.GetFileName(save.FileName)} " +
                         $"({size:N0} bytes)");
        }
        catch (Exception ex)
        {
            _status.Text = "Could not build the UDF image: " + ex.Message;
            _status.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            Log("Error: " + ex.Message);
            AppLog.WriteException("udf create", ex);
        }
    }

    private static IReadOnlyList<UdfBuilder.Node> WalkFolder(string folder)
    {
        var nodes = new List<UdfBuilder.Node>();
        foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.Ordinal))
            nodes.Add(UdfBuilder.Node.Dir(Path.GetFileName(dir), WalkFolder(dir)));
        foreach (var file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
            nodes.Add(UdfBuilder.Node.FileFromPath(Path.GetFileName(file), file));
        return nodes;
    }

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);
}
