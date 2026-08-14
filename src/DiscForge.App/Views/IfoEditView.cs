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
/// Dump a DVD-Video disc's structure to editable JSON (chapters, angles, audio and
/// subtitle languages), edit it, and rebuild the VIDEO_TS IFO files. IFO files are
/// unencrypted even on a CSS disc, so this stays inside the clean-room line. Shell
/// over <see cref="IfoReader"/>, <see cref="IfoPlanJson"/> and <see cref="IfoWriter"/>.
/// </summary>
internal sealed class IfoEditView : UserControl
{
    private readonly TextBox _in = new() { ReadOnly = true, Location = new Point(90, 13), Width = 450, Font = Theme.Ui };
    private readonly Button _inPick = new() { Text = "…", Location = new Point(546, 11), Width = 30, FlatStyle = FlatStyle.System };
    private readonly Button _dump = new() { Text = "Dump to JSON", Location = new Point(584, 11), Width = 140, Height = 26, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _json = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Location = new Point(12, 72), Size = new Size(712, 300),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };
    private readonly Button _rebuild = new()
    {
        Text = "Rebuild IFOs…", Location = new Point(600, 380), Width = 124, Height = 28, FlatStyle = FlatStyle.System, Enabled = false,
        Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
    };
    private readonly Label _status = new()
    {
        AutoSize = true, Location = new Point(12, 386), Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
    };

    private string? _folder;

    public IfoEditView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "VIDEO_TS:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "Edit chapters, angles and audio/subtitle languages below, then Rebuild:",
            AutoSize = true, Location = new Point(12, 50), Font = Theme.Ui, ForeColor = Color.Gray,
        });

        _inPick.Click += (_, _) => ChooseFolder();
        _dump.Click += (_, _) => Dump();
        _rebuild.Click += (_, _) => Rebuild();

        Controls.AddRange(new Control[] { _in, _inPick, _dump, _json, _rebuild, _status });
    }

    private void ChooseFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Pick a VIDEO_TS folder (or disc root)" };
        if (d.ShowDialog() != DialogResult.OK) return;
        _folder = d.SelectedPath; _in.Text = d.SelectedPath;
        _dump.Enabled = true;
    }

    private void Dump()
    {
        if (_folder is null) return;
        try
        {
            var src = new VideoTsSources.Folder(_folder);
            var dvd = IfoReader.Read(src);
            var dto = IfoPlanJson.FromStructure(dvd);
            _json.Text = IfoPlanJson.ToJson(dto).Replace("\n", "\r\n");
            _rebuild.Enabled = true;
            _status.Text = $"Loaded {dto.TitleSets.Count} title set(s). Edit above, then Rebuild.";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
        }
        catch (IfoFormatException ex)
        {
            _status.Text = "Not a readable DVD: " + ex.Message;
            _status.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
            _status.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("dvd-ifo dump", ex);
        }
    }

    private void Rebuild()
    {
        try
        {
            var dto = IfoPlanJson.FromJson(_json.Text);
            var plan = IfoPlanJson.ToPlan(dto);
            var files = IfoWriter.Write(plan);

            using var d = new FolderBrowserDialog { Description = "Write the rebuilt VIDEO_TS to…" };
            if (d.ShowDialog() != DialogResult.OK) return;
            string videoTs = Path.Combine(d.SelectedPath, "VIDEO_TS");
            Directory.CreateDirectory(videoTs);
            foreach (var kv in files)
            {
                File.WriteAllBytes(Path.Combine(videoTs, kv.Key), kv.Value);
                File.WriteAllBytes(Path.Combine(videoTs, Path.ChangeExtension(kv.Key, ".BUP")), kv.Value);
            }
            _status.Text = $"Rebuilt {files.Count} IFO file(s) (+ .BUP) to {videoTs}.";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report(_status.Text);
            AppLog.Write($"dvd-ifo build -> {videoTs} ({files.Count} IFOs)");
        }
        catch (Exception ex)
        {
            _status.Text = "Rebuild failed: " + ex.Message;
            _status.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("dvd-ifo build", ex);
        }
    }
}
