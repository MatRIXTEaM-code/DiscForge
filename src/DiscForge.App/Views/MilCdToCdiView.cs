// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Cdi;
using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using DiscForge.Core.Gdi;

namespace DiscForge.App.Views;

/// <summary>
/// Convert a Dreamcast MIL-CD (a self-boot CD-ROM, the kind Redump distributes as
/// bin/cue) into a DiscJuggler CDI. A MIL-CD is a two-session disc — a low-density
/// first session then a high-density session that opens the bootable game area —
/// and this preserves that split in the CDI (a plain bin/cue→CDI would flatten it
/// to one session). It reads the cue's "REM SESSION" markers to find the boundary.
/// Faithful container conversion using the same Core code the CLI carries; it does
/// not synthesise self-boot capability a source disc did not already have.
/// </summary>
internal sealed class MilCdToCdiView : UserControl
{
    private readonly TextBox _cuePath = new()
    {
        ReadOnly = true, Width = 400, Font = Theme.Ui, Location = new Point(70, 13),
    };
    private readonly Label _summary = new()
    {
        AutoSize = false, Location = new Point(12, 46), Size = new Size(712, 50),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly ComboBox _version = new()
    {
        Width = 100, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 104),
    };
    private readonly Button _convert = new()
    {
        Text = "Convert to CDI…", Location = new Point(184, 102), Width = 130, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly EventLogView _log = new()
    {
        Location = new Point(12, 140), Size = new Size(712, 300),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private string? _cueText, _cueDir;

    public MilCdToCdiView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Cue:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        var open = new Button { Text = "Open…", Location = new Point(478, 12), Width = 80, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => OpenCue();

        Controls.Add(new Label { Text = "Version:", AutoSize = true, Location = new Point(12, 107), Font = Theme.Ui });
        _version.Items.AddRange(new object[] { "CDI V3.5", "CDI V3", "CDI V2" });
        _version.SelectedIndex = 0;
        _convert.Click += (_, _) => ConvertToCdi();

        Controls.Add(_cuePath); Controls.Add(open);
        Controls.Add(_summary);
        Controls.Add(_version); Controls.Add(_convert);
        Controls.Add(_log);

        _summary.Text = "Open a Redump MIL-CD bin/cue rip (its .cue). The two-session self-boot layout " +
                        "is preserved in the CDI.";
    }

    private void OpenCue()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Cue sheet (*.cue)|*.cue|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _cuePath.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        try
        {
            _cueText = File.ReadAllText(dlg.FileName);
            _cueDir = Path.GetDirectoryName(Path.GetFullPath(dlg.FileName));
            var sheet = CueSheet.Parse(_cueText);
            var sessions = sheet.Tracks.Select(t => t.Session).Distinct().OrderBy(n => n).ToList();

            if (sessions.Count >= 2)
            {
                int hi = sessions[^1];
                var hiTracks = string.Join(", ", sheet.Tracks.Where(t => t.Session == hi).Select(t => t.Number));
                _summary.Text = $"{sheet.Tracks.Count} track(s), {sessions.Count} session(s). " +
                                $"High-density session {hi}: track(s) {hiTracks}.";
                _summary.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            }
            else
            {
                _summary.Text = $"{sheet.Tracks.Count} track(s), single session — no \"REM SESSION\" markers. " +
                                "A MIL-CD self-boot disc is two-session; the CDI will be single-session. " +
                                "Confirm this is a Redump MIL-CD rip.";
                _summary.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
            }
            _convert.Enabled = true;
            _log.Add($"Loaded {Path.GetFileName(dlg.FileName)}: {sheet.Tracks.Count} track(s), {sessions.Count} session(s).",
                EventLogView.Level.Info);

            // Identify the disc from its IP.BIN boot header, if it carries one.
            try
            {
                var ip = IpBin.ReadFromBinCue(dlg.FileName);
                if (ip is not null)
                    _log.Add($"IP.BIN: {ip.Title}  ·  {ip.ProductNumber} {ip.Version}  ·  " +
                             (ip.Regions.Count > 0 ? string.Join("/", ip.Regions) : "no region"),
                        EventLogView.Level.Good);
            }
            catch (IpBinFormatException) { /* not a Dreamcast disc — that's fine */ }
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not read the cue: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            _convert.Enabled = false;
            AppLog.WriteException("milcd load", ex);
        }
    }

    private void ConvertToCdi()
    {
        if (_cueText is null || _cueDir is null) return;
        var version = _version.SelectedItem?.ToString() switch
        {
            "CDI V2" => CdiVersion.V2,
            "CDI V3" => CdiVersion.V3,
            _ => CdiVersion.V35,
        };

        using var save = new SaveFileDialog
        {
            Filter = "CDI image (*.cdi)|*.cdi",
            FileName = Path.GetFileNameWithoutExtension(_cuePath.Text) + ".cdi",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (save.ShowDialog() != DialogResult.OK) return;

        try
        {
            using (var os = File.Create(save.FileName))
                CdiConverter.BinCueToCdi(_cueText, _cueDir, version, os);
            AppSettings.LastImageDirectory = Path.GetDirectoryName(save.FileName);
            _log.Add($"Wrote {Path.GetFileName(save.FileName)} ({version}).", EventLogView.Level.Good);
            StatusBus.Report($"MIL-CD converted to {Path.GetFileName(save.FileName)}.");
        }
        catch (Exception ex)
        {
            _log.Add("Convert failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("milcd convert", ex);
        }
    }
}
