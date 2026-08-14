// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.VideoCd;

namespace DiscForge.App.Views;

/// <summary>
/// Writes the two Video CD control sectors — INFO.VCD (album/disc identity) and
/// ENTRIES.VCD (the play-point list) — into a VCD/ folder. A thin shell over
/// <see cref="VideoCdControl"/>. Assembling a full player-verified VCD image is a
/// separate, sample-gated step and is not done here.
/// </summary>
internal sealed class VcdView : UserControl
{
    private readonly TextBox _outDir = new() { ReadOnly = true, Location = new Point(70, 13), Width = 470, Font = Theme.Ui };
    private readonly Button _outPick = new() { Text = "…", Location = new Point(546, 11), Width = 30, FlatStyle = FlatStyle.System };
    private readonly TextBox _album = new() { Location = new Point(70, 43), Width = 300, Font = Theme.Ui };
    private readonly CheckBox _svcd = new() { Text = "SVCD", AutoSize = true, Location = new Point(388, 45), Font = Theme.Ui };
    private readonly TextBox _entries = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
        Location = new Point(70, 96), Size = new Size(470, 168), Font = Theme.Mono,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Button _go = new()
    {
        Text = "Write VCD control", Location = new Point(560, 96), Width = 164, Height = 28, FlatStyle = FlatStyle.System, Enabled = false,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Location = new Point(12, 276), Size = new Size(712, 20), Font = Theme.UiBold,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 300), Size = new Size(712, 140),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _outPath;

    public VcdView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Out dir:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Album:", AutoSize = true, Location = new Point(12, 46), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Entries:", AutoSize = true, Location = new Point(12, 96), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "one per line:  track  mm:ss:ff   (e.g.  1  0:02:00)", AutoSize = true,
            Location = new Point(70, 76), Font = Theme.Ui, ForeColor = Color.Gray,
        });

        _outPick.Click += (_, _) => ChooseOut();
        _go.Click += (_, _) => Build();

        Controls.AddRange(new Control[] { _outDir, _outPick, _album, _svcd, _entries, _go, _status, _log });

        _entries.Text = "1  0:02:00";
        _log.Text =
            "Writes INFO.VCD and ENTRIES.VCD (the VCD control sectors) into <out>/VCD/." + "\r\n\r\n" +
            "A full player-verified VCD image (the MPEG track in Mode 2/Form 2 plus the ISO tree)" + "\r\n" +
            "needs a reference VCD to validate against and is not produced here.";
    }

    private void ChooseOut()
    {
        using var d = new FolderBrowserDialog { Description = "Write the VCD/ control files under…" };
        if (d.ShowDialog() != DialogResult.OK) return;
        _outPath = d.SelectedPath; _outDir.Text = d.SelectedPath;
        _go.Enabled = true;
    }

    private void Build()
    {
        if (_outPath is null) return;
        try
        {
            var entries = new List<VideoCdEntry>();
            foreach (var raw in _entries.Lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) throw new FormatException($"Bad entry '{raw}' (expected: track mm:ss:ff).");
                int track = int.Parse(parts[0]);
                var t = parts[1].Split(':');
                if (t.Length != 3) throw new FormatException($"Bad time in '{raw}' (expected mm:ss:ff).");
                entries.Add(new VideoCdEntry
                {
                    TrackNumber = track,
                    Minute = int.Parse(t[0]), Second = int.Parse(t[1]), Frame = int.Parse(t[2]),
                });
            }
            if (entries.Count == 0) throw new FormatException("Add at least one entry.");

            bool svcd = _svcd.Checked;
            var info = VideoCdControl.BuildInfo(new VideoCdInfoPlan { AlbumId = _album.Text, SuperVcd = svcd });
            var ents = VideoCdControl.BuildEntries(entries, superVcd: svcd);
            string vcdDir = Path.Combine(_outPath, "VCD");
            Directory.CreateDirectory(vcdDir);
            File.WriteAllBytes(Path.Combine(vcdDir, "INFO.VCD"), info);
            File.WriteAllBytes(Path.Combine(vcdDir, "ENTRIES.VCD"), ents);

            _status.Text = $"Wrote VCD/INFO.VCD and VCD/ENTRIES.VCD ({entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}).";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report(_status.Text);
            AppLog.Write($"vcd-control -> {vcdDir} ({entries.Count} entries)");
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
            _status.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("vcd-control", ex);
        }
    }
}
