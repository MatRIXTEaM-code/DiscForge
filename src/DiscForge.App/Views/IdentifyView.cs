// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Identify;

namespace DiscForge.App.Views;

/// <summary>
/// Drop any file in and DiscForge says what it is — one front door over every
/// format it understands: disc images, compressed images, console memory cards,
/// PlayStation asset and executable files, and patches. It reads only the small
/// regions where each signature lives, so even a multi-gigabyte image is named at
/// once. A thin shell over <see cref="FormatIdentifier"/>.
/// </summary>
internal sealed class IdentifyView : UserControl
{
    private readonly Label _result = new()
    {
        AutoSize = false, Location = new Point(12, 60), Size = new Size(712, 28),
        Font = Theme.UiBold,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 96), Size = new Size(712, 344),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        BackColor = Color.White,
    };

    public IdentifyView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f) IdentifyMany(f); };

        Controls.Add(new Label { Text = "File:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        var open = new Button { Text = "Choose file(s)…", Location = new Point(12, 34), Width = 130, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => Choose();

        Controls.Add(open);
        Controls.Add(_result);
        Controls.Add(_log);

        _result.Text = "Choose or drop a file to identify it.";
        _result.ForeColor = Color.Gray;
    }

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void Choose()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() == DialogResult.OK) IdentifyMany(dlg.FileNames);
    }

    private void IdentifyMany(string[] files)
    {
        if (files.Length == 0) return;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(files[0]);

        foreach (var f in files)
        {
            try
            {
                using var fs = File.OpenRead(f);
                var id = FormatIdentifier.Identify(fs);
                string line = $"{Path.GetFileName(f)}  ->  {id.Name}" +
                              (id.Detail.Length > 0 ? $" ({id.Detail})" : "") + $"  [{id.Category}]";
                Log(line);
                if (files.Length == 1)
                {
                    _result.Text = id.Recognised ? $"{id.Name} — {id.Detail}" : "Unrecognised format";
                    _result.ForeColor = id.Recognised
                        ? Color.FromArgb(0x20, 0x70, 0x20)
                        : Color.FromArgb(0xA0, 0x60, 0x00);
                    StatusBus.Report($"{Path.GetFileName(f)}: {id.Name}");
                }
            }
            catch (Exception ex)
            {
                Log($"{Path.GetFileName(f)}  ->  error: {ex.Message}");
                AppLog.WriteException("identify", ex);
            }
        }
        if (files.Length > 1) { _result.Text = $"Identified {files.Length} file(s)."; _result.ForeColor = Color.Black; }
    }

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);
}
