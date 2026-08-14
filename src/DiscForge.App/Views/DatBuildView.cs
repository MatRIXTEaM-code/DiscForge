// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Dat;
using DiscForge.Core.Files;

namespace DiscForge.App.Views;

/// <summary>
/// Builds a Redump / No-Intro–style Logiqx DAT from a folder of dumps: hashes each file (size +
/// CRC-32 / MD5 / SHA-1, the same <see cref="ImageChecksums"/> the verifier uses) and writes a datfile that
/// catalogues them — one game per file. The write-side companion to the Submit tile; a thin shell over
/// <see cref="DatBuilder"/>. The folder becomes its own reference set that <c>dat-verify</c> (or any DAT tool)
/// can check against.
/// </summary>
internal sealed class DatBuildView : UserControl
{
    private readonly TextBox _folder = new() { ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(90, 14) };
    private readonly TextBox _name = new() { Width = 300, Font = Theme.Ui, Location = new Point(90, 44) };
    private readonly CheckBox _recursive = new() { Text = "Include subfolders", AutoSize = true, Location = new Point(400, 46), Font = Theme.Ui };
    private readonly Button _build = new() { Text = "Build DAT…", Location = new Point(12, 74), Width = 120, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.Mono,
        Location = new Point(12, 108), Size = new Size(712, 296),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Color.White,
    };

    private string? _dir;

    public DatBuildView()
    {
        Size = new Size(736, 416);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Folder:", AutoSize = true, Location = new Point(12, 17), Font = Theme.Ui });
        Controls.Add(new Label { Text = "DAT name:", AutoSize = true, Location = new Point(12, 47), Font = Theme.Ui });
        var pick = new Button { Text = "…", Location = new Point(566, 13), Width = 30, FlatStyle = FlatStyle.System };
        pick.Click += (_, _) => Choose();
        _build.Click += (_, _) => Build();

        Controls.AddRange(new Control[] { _folder, _name, _recursive, pick, _build, _log });
    }

    private void Choose()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose the folder of dumps to catalogue",
            UseDescriptionForTitle = true,
            SelectedPath = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _dir = dlg.SelectedPath;
        _folder.Text = _dir;
        if (_name.Text.Length == 0) _name.Text = new DirectoryInfo(_dir).Name;
        AppSettings.LastImageDirectory = _dir;
        _build.Enabled = true;
    }

    private void Build()
    {
        if (_dir is null) return;
        string datName = _name.Text.Length > 0 ? _name.Text : "Collection";
        using var save = new SaveFileDialog
        {
            Filter = "DAT (*.dat)|*.dat|All files (*.*)|*.*",
            FileName = datName + ".dat",
        };
        if (save.ShowDialog() != DialogResult.OK) return;

        _log.Clear();
        Cursor = Cursors.WaitCursor;
        try
        {
            var opt = _recursive.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(_dir, "*", opt)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) { Log("No files to catalogue in that folder."); return; }

            var roms = new List<DatBuildRom>(files.Count);
            foreach (var f in files)
            {
                var s = ImageChecksums.ComputeFile(f);
                roms.Add(new DatBuildRom(Path.GetFileNameWithoutExtension(f), Path.GetFileName(f),
                                         s.Length, s.Crc32, s.Md5, s.Sha1));
                Log($"{s.Crc32}  {s.Length,12:N0}  {Path.GetFileName(f)}");
            }

            string dat = DatBuilder.Build(datName, roms);
            File.WriteAllText(save.FileName, dat);
            Log("");
            Log($"Wrote {roms.Count} entr{(roms.Count == 1 ? "y" : "ies")} → {save.FileName}");
            StatusBus.Report($"Built DAT \"{datName}\" — {roms.Count} entr{(roms.Count == 1 ? "y" : "ies")}");
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            AppLog.WriteException("dat-build", ex);
        }
        finally { Cursor = Cursors.Default; }
    }

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);
}
