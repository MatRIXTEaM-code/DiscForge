// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Cdi;
using DiscForge.Core.Create;
using DiscForge.Core.Iso;

namespace DiscForge.App.Views;

/// <summary>Build a data CDI from a folder of files (self-contained ISO builder).</summary>
internal sealed class CreateView : UserControl
{
    private readonly TextBox _folder = new() { Width = 460, Font = Theme.Ui, ReadOnly = true };
    private readonly TextBox _volume = new() { Width = 200, Font = Theme.Ui, Text = "OPENJUGGLER" };
    private readonly ComboBox _version = new()
    {
        Width = 120, Font = Theme.Ui, DropDownStyle = ComboBoxStyle.DropDownList,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 174), Size = new Size(712, 280),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };
    private readonly Button _create = new() { Text = "Create CDI…", Location = new Point(12, 112), Width = 110, FlatStyle = FlatStyle.System };

    private readonly CheckBox _rockRidge = new()
    {
        Text = "Rock Ridge (POSIX names for Linux/macOS)", AutoSize = true,
        Location = new Point(12, 82), Font = Theme.Ui,
    };
    private readonly CheckBox _bootable = new()
    {
        Text = "Bootable (El Torito):", AutoSize = true,
        Location = new Point(330, 82), Font = Theme.Ui,
    };
    private readonly TextBox _bootPath = new()
    {
        Width = 210, Font = Theme.Ui, ReadOnly = true, Enabled = false,
        Location = new Point(470, 79),
    };
    private readonly Button _bootBrowse = new()
    {
        Text = "…", Location = new Point(686, 78), Width = 28, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private string? _folderPath;
    private List<IsoBuilder.Node>? _droppedFiles;
    private string? _bootImagePath;

    public CreateView()
    {
        // Establish a realistic size before adding anchored children (see InspectView).
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasDroppable(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => AcceptDrop(e);

        Controls.Add(new Label { Text = "Source folder:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        _folder.Location = new Point(110, 13);
        var browse = new Button { Text = "Browse…", Location = new Point(578, 12), Width = 84, FlatStyle = FlatStyle.System };
        browse.Click += (_, _) => BrowseFolder();

        Controls.Add(new Label { Text = "Volume name:", AutoSize = true, Location = new Point(12, 48), Font = Theme.Ui });
        _volume.Location = new Point(110, 45);

        Controls.Add(new Label { Text = "Format:", AutoSize = true, Location = new Point(330, 48), Font = Theme.Ui });
        _version.Items.AddRange(new object[] { "v3.5 (recommended)", "v3", "v2" });
        _version.SelectedIndex = 0;
        _version.Location = new Point(380, 45);

        // Options row.
        _bootable.CheckedChanged += (_, _) =>
        {
            _bootPath.Enabled = _bootable.Checked;
            _bootBrowse.Enabled = _bootable.Checked;
            if (!_bootable.Checked) { _bootImagePath = null; _bootPath.Clear(); }
        };
        _bootBrowse.Click += (_, _) => BrowseBootImage();

        Controls.Add(new Label
        {
            Text = "Joliet long names are always included. Rock Ridge adds POSIX names/permissions; " +
                   "together they make a cross-platform disc.",
            AutoSize = false, Size = new Size(700, 30), Location = new Point(12, 106),
            Font = Theme.Ui, ForeColor = Color.Gray,
        });

        _create.Location = new Point(12, 140);
        _create.Click += async (_, _) => await CreateAsync();


        Controls.Add(_folder);
        Controls.Add(browse);
        Controls.Add(_volume);
        Controls.Add(_version);
        Controls.Add(_rockRidge);
        Controls.Add(_bootable);
        Controls.Add(_bootPath);
        Controls.Add(_bootBrowse);
        Controls.Add(_create);
        Controls.Add(_log);
    }

    private void BrowseBootImage()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose a boot image (you supply this — DiscForge embeds no boot code)",
            Filter = "Boot images (*.img;*.bin)|*.img;*.bin|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _bootImagePath = dlg.FileName;
        _bootPath.Text = dlg.FileName;
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose a folder to image" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        SetFolder(dlg.SelectedPath);
    }

    /// <summary>Set the source folder (used by menu/drag-drop routing).</summary>
    public void SetFolder(string path)
    {
        _folderPath = path;
        _droppedFiles = null;
        _folder.Text = path;
        StatusBus.Report($"Source: {path}");
    }

    private static bool HasDroppable(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void AcceptDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        // A single dropped folder -> treat as source folder.
        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            SetFolder(paths[0]);
            return;
        }

        // Otherwise gather dropped files (ignoring folders) as an explicit set.
        // Reference dropped files by path — streamed at write time, so a large
        // file doesn't have to fit in memory.
        var files = new List<IsoBuilder.Node>();
        foreach (var p in paths)
            if (File.Exists(p))
                files.Add(IsoBuilder.Node.FromPath(p));

        if (files.Count == 0) return;
        _droppedFiles = files;
        _folderPath = null;
        _folder.Text = $"{files.Count} dropped file(s)";
        StatusBus.Report($"{files.Count} file(s) ready to image");
    }

    private CdiVersion SelectedVersion() => _version.SelectedIndex switch
    {
        1 => CdiVersion.V3, 2 => CdiVersion.V2, _ => CdiVersion.V35,
    };

    private async Task CreateAsync()
    {
        if (_folderPath is null && _droppedFiles is null)
        {
            RetroMessageBox.Show("Choose a source folder or drop files first.");
            return;
        }
        if (_bootable.Checked && _bootImagePath is null)
        {
            RetroMessageBox.Show("Choose a boot image, or untick Bootable.");
            return;
        }
        using var save = new SaveFileDialog { Filter = "CDI image (*.cdi)|*.cdi", FileName = _volume.Text + ".cdi" };
        if (save.ShowDialog() != DialogResult.OK) return;

        _create.Enabled = false;
        _log.Text = "Building…\r\n";
        StatusBus.Report("Building image…");
        try
        {
            var version = SelectedVersion();
            var vol = _volume.Text;
            var folder = _folderPath;
            var files = _droppedFiles;
            bool rr = _rockRidge.Checked;
            var bootPath = _bootable.Checked ? _bootImagePath : null;

            var (sectors, bytes, warnings) = await Task.Run(() =>
            {
                IsoBuilder.BootImage? boot = bootPath is null
                    ? null
                    : new IsoBuilder.BootImage(File.ReadAllBytes(bootPath), IsoBuilder.BootMediaType.NoEmulation);

                using var os = File.Create(save.FileName);
                CdiCreator.CreateResult r;
                if (boot is not null)
                {
                    // El Torito: the tree API takes nodes, so adapt dropped files.
                    r = files is not null
                        ? CdiCreator.CreateBootableImage(vol, files, boot, version, os, rr)
                        : CdiCreator.CreateBootableFromDirectory(vol, folder!, boot, version, os, rr);
                }
                else
                {
                    r = files is not null
                        ? CdiCreator.CreateDataImageTree(vol, files, version, os, rr)
                        : CdiCreator.CreateFromDirectory(vol, folder!, version, os, rr);
                }
                return (r.IsoSectors, r.CdiBytes, r.Warnings);
            });

            foreach (var w in warnings)
            {
                _log.AppendText($"warning: {w}\r\n");
                AppLog.Write("  warning: " + w);
            }
            var extras = new List<string> { "Joliet" };
            if (rr) extras.Add("Rock Ridge");
            if (bootPath is not null) extras.Add("El Torito");
            _log.AppendText($"\r\nDone ({string.Join(" + ", extras)}). " +
                            $"{sectors} ISO sectors, {bytes:N0} bytes → {Path.GetFileName(save.FileName)}\r\n");
            AppLog.Write($"Created {Path.GetFileName(save.FileName)}: {string.Join(" + ", extras)}, " +
                         $"{sectors} ISO sectors, {bytes:N0} bytes");
            StatusBus.Report($"Created {Path.GetFileName(save.FileName)}");
        }
        catch (Exception ex)
        {
            _log.AppendText($"\r\nError: {ex.Message}\r\n");
            AppLog.WriteException("create image", ex);
            StatusBus.Report("Create failed");
        }
        finally { _create.Enabled = true; }
    }
}
