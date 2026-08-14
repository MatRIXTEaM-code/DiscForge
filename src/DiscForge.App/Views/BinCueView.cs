// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Cue;

namespace DiscForge.App.Views;

/// <summary>
/// Merge a multi-track bin/cue set (one .bin per track — the Redump shape) into a
/// single .bin with a rewritten .cue, and split a single-file image back into
/// per-track files. The binmerge job: many emulators, burners and older tools
/// only accept a single-file image, while the canonical preservation set is one
/// file per track. Plain container work — the bytes are moved verbatim, only the
/// cue's INDEX arithmetic changes.
/// </summary>
internal sealed class BinCueView : UserControl
{
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 96), Size = new Size(712, 344),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        BackColor = Color.White,
    };

    public BinCueView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label
        {
            Text = "Merge per-track bin/cue into one .bin, or split one .bin back into tracks.",
            AutoSize = true, Location = new Point(12, 14), Font = Theme.Ui,
        });

        var merge = new Button { Text = "Merge → single .bin…", Location = new Point(12, 44), Width = 170, FlatStyle = FlatStyle.System };
        merge.Click += (_, _) => Merge();
        var split = new Button { Text = "Split → per-track…", Location = new Point(192, 44), Width = 150, FlatStyle = FlatStyle.System };
        split.Click += (_, _) => Split();

        Controls.Add(merge); Controls.Add(split); Controls.Add(_log);
        Log("Point Merge at a multi-file cue; point Split at a single-file cue. Bytes are copied unchanged.");
    }

    private void Merge()
    {
        using var open = new OpenFileDialog
        {
            Filter = "Cue sheet (*.cue)|*.cue|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (open.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(open.FileName);

        using var save = new SaveFileDialog
        {
            Filter = "Bin image (*.bin)|*.bin",
            FileName = Path.GetFileNameWithoutExtension(open.FileName) + " (merged).bin",
        };
        if (save.ShowDialog() != DialogResult.OK) return;

        string outCue = Path.ChangeExtension(save.FileName, ".cue");
        try
        {
            var r = BinCueMerge.Merge(open.FileName, save.FileName, outCue);
            Log($"Merged {r.Tracks} track(s) → {Path.GetFileName(r.BinPath)} ({r.Bytes:N0} bytes) " +
                $"+ {Path.GetFileName(r.CuePath)}.");
            StatusBus.Report($"Merged {Path.GetFileName(r.BinPath)}");
        }
        catch (Exception ex) { Log("Error: " + ex.Message); AppLog.WriteException("bincue merge", ex); }
    }

    private void Split()
    {
        using var open = new OpenFileDialog
        {
            Filter = "Cue sheet (*.cue)|*.cue|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (open.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(open.FileName);

        using var folder = new FolderBrowserDialog { Description = "Choose an output folder for the per-track files." };
        if (folder.ShowDialog() != DialogResult.OK) return;

        string baseName = Path.GetFileNameWithoutExtension(open.FileName);
        string outCue = Path.Combine(folder.SelectedPath, baseName + " (split).cue");
        try
        {
            var r = BinCueMerge.Split(open.FileName, folder.SelectedPath, baseName, outCue);
            Log($"Split into {r.Tracks} file(s) in {folder.SelectedPath}:");
            foreach (var b in r.BinPaths) Log("  " + b);
            Log("Cue: " + Path.GetFileName(r.CuePath));
            StatusBus.Report($"Split into {r.Tracks} track(s)");
        }
        catch (Exception ex) { Log("Error: " + ex.Message); AppLog.WriteException("bincue split", ex); }
    }

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);
}
