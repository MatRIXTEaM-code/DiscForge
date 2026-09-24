// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Files;
using DiscForge.Core.Mount;

namespace DiscForge.App.Views;

/// <summary>
/// Virtual mounting: describes how an image would mount as a drive and, for
/// ISO-compatible images, offers Windows' native mount (no driver needed).
/// Rich formats (audio / subchannel / multi-track) are reported honestly as
/// needing a virtual-drive driver DiscForge does not yet ship.
/// </summary>
internal sealed class MountView : UserControl
{
    private readonly TextBox _path = new() { ReadOnly = true, Width = 440, Font = Theme.Ui };
    private readonly Button _browse = new() { Text = "Browse…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly Button _describe = new() { Text = "Describe", Width = 90, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly Button _mount = new() { Text = "Mount (Windows)", Width = 130, FlatStyle = FlatStyle.System, Enabled = false, Visible = false };
    private readonly TextBox _out = new()
    {
        Multiline = true, ReadOnly = true, Font = Theme.Mono, ScrollBars = ScrollBars.Vertical,
        Size = new Size(660, 260),
    };

    private string? _nativeIsoToMount;

    // For what Windows' own mount can't take (BIN/CUE, audio, mixed-mode, CCD/MDS/NRG): WinCDEmu, the
    // open-source virtual drive. Its batchmnt.exe mounts the image passed to it; any other mount tool
    // that takes an image path works the same way. Never bundled — see ExternalToolLauncher.
    private readonly Button _winCdEmu = new() { Text = "Mount with WinCDEmu…", Width = 170, FlatStyle = FlatStyle.System };

    public MountView()
    {
        Size = new Size(720, 440);
        BackColor = Color.White;

        var title = new Label { Text = "Mount image", Font = Theme.UiBold, AutoSize = true, Location = new Point(16, 12) };
        var hint = new Label
        {
            Text = "Mounts ISO-compatible images via Windows' native mount (no driver). " +
                   "Audio / subchannel images report that a virtual-drive driver is needed.",
            Font = Theme.Ui, AutoSize = false, Size = new Size(680, 32), Location = new Point(16, 34),
        };

        _path.Location = new Point(16, 72);
        _browse.Location = new Point(464, 70);
        _describe.Location = new Point(552, 70);
        _mount.Location = new Point(16, 104);
        _winCdEmu.Location = new Point(152, 104);
        _out.Location = new Point(16, 140);

        _browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Disc images (*.iso;*.cdi;*.cue;*.bin;*.img)|*.iso;*.cdi;*.cue;*.bin;*.img|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() == DialogResult.OK) { _path.Text = dlg.FileName; _describe.Enabled = true; }
        };
        _describe.Click += (_, _) => Describe();
        _mount.Click += (_, _) => MountNative();
        _winCdEmu.Click += (_, _) => ExternalToolLauncher.Launch(
            () => Settings.ExternalDumperPathWinCdEmu,
            p => Settings.ExternalDumperPathWinCdEmu = p,
            "Locate WinCDEmu's batchmnt.exe (or your virtual-drive tool)",
            string.IsNullOrEmpty(_path.Text) ? "Pick an image first to have it mounted directly."
                                             : $"Asked it to mount {Path.GetFileName(_path.Text)}.",
            (msg, _) => _out.Text = msg,
            ExternalToolLauncher.OpeningFile(_path.Text));

        Controls.AddRange(new Control[] { title, hint, _path, _browse, _describe, _mount, _winCdEmu, _out });
    }

    private void Describe()
    {
        _describe.Enabled = false;
        _mount.Visible = false;
        _nativeIsoToMount = null;
        StatusBus.Report("Describing image for mount…");
        try
        {
            string path = _path.Text;
            var ext = Path.GetExtension(path).ToLowerInvariant();

            long totalSectors = 0;
            int trackCount = 1;
            bool hasAudio = false, hasSubchannel = false, isPlainData = true;

            try { using var access = SectorAccess.Open(path); totalSectors = access.TotalSectors; }
            catch { totalSectors = new FileInfo(path).Length / 2048; }

            if (File.Exists(Path.ChangeExtension(path, ".sub"))) hasSubchannel = true;

            var media = VirtualDisc.MediaFromSectors(totalSectors);
            var disc = VirtualDisc.Describe(path, media, totalSectors, trackCount, hasAudio, hasSubchannel, isPlainData);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(disc.Summary);
            sb.AppendLine();
            switch (disc.Strategy)
            {
                case VirtualDisc.MountStrategy.NativeIso:
                    sb.AppendLine("This image can be mounted now on Windows (no driver needed).");
                    sb.AppendLine("Click 'Mount (Windows)', or run:");
                    sb.AppendLine("  " + VirtualDisc.NativeMountCommand(Path.GetFullPath(path)));
                    _nativeIsoToMount = Path.GetFullPath(path);
                    _mount.Visible = true; _mount.Enabled = true;
                    break;
                case VirtualDisc.MountStrategy.ConvertThenNativeIso:
                    sb.AppendLine("A single data track in a non-ISO container. Convert to .iso first");
                    sb.AppendLine("(Tools ▸ Convert), then mount the .iso here.");
                    break;
                case VirtualDisc.MountStrategy.NeedsVirtualDriveDriver:
                    sb.AppendLine("A faithful mount of this image (audio / subchannel / multi-track)");
                    sb.AppendLine("needs a virtual optical drive — a kernel-mode driver DiscForge does");
                    sb.AppendLine("not yet ship. Inspect, verify, extract or convert it instead.");
                    break;
            }
            _out.Text = sb.ToString();
            StatusBus.Report("Mount description ready.");
        }
        catch (Exception ex)
        {
            _out.Text = "Failed: " + ex.Message;
            StatusBus.Report("Mount description failed.");
        }
        finally { _describe.Enabled = true; }
    }

    private void MountNative()
    {
        if (_nativeIsoToMount is null) return;
        try
        {
            // Mount-DiskImage via PowerShell — the native, driver-free path.
            var psi = new System.Diagnostics.ProcessStartInfo("powershell")
            {
                Arguments = $"-NoProfile -Command \"{VirtualDisc.NativeMountCommand(_nativeIsoToMount)}\"",
                UseShellExecute = true, CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
            StatusBus.Report("Requested native mount of the image.");
            _out.AppendText("\r\nRequested Windows to mount the image. Check This PC for the new drive.\r\n");
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show("Could not start the mount: " + ex.Message, "DiscForge",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
