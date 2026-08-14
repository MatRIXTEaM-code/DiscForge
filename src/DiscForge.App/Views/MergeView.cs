// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Recovery;

namespace DiscForge.App.Views;

/// <summary>
/// Multi-read recovery: merge several imperfect rips of the SAME disc into one
/// best-possible image. Keeps sectors the copies agree on, uses any copy whose EDC
/// validates, and majority-votes the rest — reassembling sectors no single copy
/// held whole. A thin shell over <see cref="DumpMerge"/>.
/// </summary>
internal sealed class MergeView : UserControl
{
    private readonly ListBox _sources = new()
    {
        Location = new Point(70, 40), Size = new Size(542, 122), Font = Theme.Ui,
        HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended,
    };
    private readonly Button _add = new()
    {
        Text = "Add…", Location = new Point(620, 40), Width = 104, Height = 26, FlatStyle = FlatStyle.System,
    };
    private readonly Button _remove = new()
    {
        Text = "Remove", Location = new Point(620, 72), Width = 104, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly TextBox _out = new()
    {
        ReadOnly = true, Location = new Point(70, 172), Width = 542, Font = Theme.Ui,
    };
    private readonly Button _outPick = new()
    {
        Text = "…", Location = new Point(618, 170), Width = 30, FlatStyle = FlatStyle.System,
    };
    private readonly ComboBox _sectorSize = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(70, 202), Width = 240, Font = Theme.Ui,
    };
    private readonly Button _merge = new()
    {
        Text = "Merge", Location = new Point(620, 200), Width = 104, Height = 28, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 240), Size = new Size(712, 200),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _outPath;

    public MergeView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Sources:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "two or more rips of the SAME disc", AutoSize = true, Location = new Point(70, 18),
            Font = Theme.Ui, ForeColor = Color.Gray,
        });
        Controls.Add(new Label { Text = "Output:", AutoSize = true, Location = new Point(12, 175), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Sectors:", AutoSize = true, Location = new Point(12, 205), Font = Theme.Ui });

        _sectorSize.Items.AddRange(new object[] { "2352 raw (EDC-verified)", "2048 cooked (voting only)" });
        _sectorSize.SelectedIndex = 0;

        _add.Click += (_, _) => AddSources();
        _remove.Click += (_, _) => RemoveSelected();
        _outPick.Click += (_, _) => ChooseOutput();
        _merge.Click += (_, _) => DoMerge();
        _sources.SelectedIndexChanged += (_, _) => _remove.Enabled = _sources.SelectedItems.Count > 0;

        Controls.AddRange(new Control[] { _sources, _add, _remove, _out, _outPick, _sectorSize, _merge, _log });

        _log.Text =
            "Add two or more rips of the same disc, choose an output file, and press Merge." + "\r\n\r\n" +
            "Sectors the copies agree on are kept; any copy whose EDC validates is used as-is;" + "\r\n" +
            "the rest are majority-voted and re-checked against their EDC. A sector no single" + "\r\n" +
            "copy had whole can be reassembled from the good bytes of several. Unrecoverable" + "\r\n" +
            "sectors are reported so you know exactly what is still missing.";
    }

    private void AddSources()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add rip(s) of the same disc",
            Filter = "Disc images (*.bin;*.iso;*.img)|*.bin;*.iso;*.img|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) _sources.Items.Add(f);
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileNames[0]);
        UpdateReady();
    }

    private void RemoveSelected()
    {
        for (int i = _sources.SelectedIndices.Count - 1; i >= 0; i--)
            _sources.Items.RemoveAt(_sources.SelectedIndices[i]);
        UpdateReady();
    }

    private void ChooseOutput()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save the merged image",
            Filter = "Disc image (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = "merged.bin",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _outPath = dlg.FileName;
        _out.Text = dlg.FileName;
        UpdateReady();
    }

    private void UpdateReady() => _merge.Enabled = _sources.Items.Count >= 2 && _outPath is not null;

    private void DoMerge()
    {
        if (_outPath is null || _sources.Items.Count < 2) return;
        try
        {
            var images = _sources.Items.Cast<string>().Select(File.ReadAllBytes).ToList();
            int ss = _sectorSize.SelectedIndex == 1 ? 2048 : 2352;
            var result = DumpMerge.Merge(images, ss);
            File.WriteAllBytes(_outPath, result.Image);

            var r = result.Report;
            var sb = new StringBuilder();
            sb.AppendLine(r.Summary());
            sb.AppendLine($"Repaired {r.Repaired:N0} disagreeing sector(s); wrote {Path.GetFileName(_outPath)}.");
            if (r.FullyRecovered)
                sb.AppendLine("Fully recovered — every sector is either agreed or EDC-verified.");
            else
            {
                sb.AppendLine($"{r.Unrecovered:N0} sector(s) could not be recovered from these copies.");
                sb.AppendLine("First unrecovered: " + string.Join(", ", r.UnrecoveredSectors.Take(24))
                              + (r.UnrecoveredSectors.Count > 24 ? " …" : ""));
            }
            _log.Text = sb.ToString();
            StatusBus.Report($"Merged {images.Count} rip(s) → {Path.GetFileName(_outPath)}");
            AppLog.Write($"dump-merge {images.Count} sources -> {Path.GetFileName(_outPath)} ({r.Summary()})");
        }
        catch (Exception ex)
        {
            _log.Text = "Merge failed: " + ex.Message;
            AppLog.WriteException("dump-merge", ex);
        }
    }
}
