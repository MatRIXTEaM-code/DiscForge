// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Media;
using DiscForge.Core.PlayStation;

namespace DiscForge.App.Views;

/// <summary>
/// Convert PlayStation asset files pulled from a game: TIM textures to PNG, VAG
/// audio to WAV, TMD models to DXF, and inspect PS-EXE executables. The file type
/// is detected from its signature, so one view handles them all. Plain format
/// work on a person's own extracted files.
/// </summary>
internal sealed class PsxAssetView : UserControl
{
    private enum AssetKind { None, Tim, Vag, Tmd, PsExe }

    private readonly TextBox _path = new()
    {
        ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(70, 13),
    };
    private readonly Label _summary = new()
    {
        AutoSize = false, Location = new Point(12, 48), Size = new Size(712, 18), Font = Theme.UiBold,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Button _save = new()
    {
        Text = "Save output…", Location = new Point(604, 12), Width = 120, FlatStyle = FlatStyle.System,
        Enabled = false, Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 78), Size = new Size(712, 362),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        BackColor = Color.White,
    };

    private string? _path0;
    private byte[]? _data;
    private AssetKind _kind = AssetKind.None;

    public PsxAssetView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "File:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        var open = new Button { Text = "Open…", Location = new Point(548, 12), Width = 48, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => Open();
        _save.Click += (_, _) => Save();

        Controls.Add(_path); Controls.Add(open); Controls.Add(_summary); Controls.Add(_save); Controls.Add(_log);

        _summary.Text = "Open a TIM, VAG, TMD, TOD or PS-EXE file.";
        _summary.ForeColor = Color.Gray;
    }

    private void Open()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "PSX assets (*.tim;*.vag;*.tmd;*.tod;*.exe)|*.tim;*.vag;*.tmd;*.tod;*.exe|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _path0 = dlg.FileName;
        _path.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        _save.Enabled = false;
        _log.Clear();

        try
        {
            _data = File.ReadAllBytes(dlg.FileName);
            Detect();
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not read the file: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("psx asset open", ex);
        }
    }

    private void Detect()
    {
        var d = _data!;
        if (Tim.IsTim(d))
        {
            var t = Tim.Parse(d);
            _kind = AssetKind.Tim;
            _summary.Text = $"TIM texture — {t.Mode.ToString().Replace("Bpp", "")}bpp, {t.Width}x{t.Height}, {t.PaletteCount} palette(s)";
            Log("Save output writes a PNG.");
        }
        else if (Vag.IsVag(d))
        {
            var info = Vag.ReadInfo(d);
            _kind = AssetKind.Vag;
            _summary.Text = $"VAG audio — {(info.SampleRate > 0 ? info.SampleRate : 44100)} Hz" +
                            (info.Name.Length > 0 ? $" (\"{info.Name}\")" : "");
            Log("Save output writes a mono WAV.");
        }
        else if (Tmd.IsTmd(d))
        {
            var m = Tmd.Parse(d);
            _kind = AssetKind.Tmd;
            int faces = m.Objects.Sum(o => o.Faces.Count);
            _summary.Text = $"TMD model — {m.Objects.Count} object(s), {m.VertexTotal:N0} vertices, {faces:N0} faces";
            Log("Save output writes a DXF (3DFACE polygons, point cloud where no faces).");
        }
        else if (PsExe.IsPsExe(d))
        {
            var h = PsExe.ReadHeader(d);
            _kind = AssetKind.PsExe;
            _summary.Text = h.Summary;
            Log($"entry 0x{h.EntryPoint:X8}, load 0x{h.LoadAddress:X8}, t_size {h.TextSize:N0}");
            if (h.RegionMarker.Length > 0) Log("marker: " + h.RegionMarker);
        }
        else
        {
            _kind = AssetKind.None;
            _summary.Text = "Not a recognised PSX asset (TIM / VAG / TMD / TOD / PS-EXE).";
            _summary.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
            return;
        }

        _summary.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
        _save.Enabled = _kind is AssetKind.Tim or AssetKind.Vag or AssetKind.Tmd;
    }

    private void Save()
    {
        if (_data is null || _kind == AssetKind.None) return;
        var (filter, ext) = _kind switch
        {
            AssetKind.Tim => ("PNG image (*.png)|*.png", ".png"),
            AssetKind.Vag => ("WAV audio (*.wav)|*.wav", ".wav"),
            AssetKind.Tmd => ("DXF model (*.dxf)|*.dxf", ".dxf"),
            _ => ("All files (*.*)|*.*", ".out"),
        };
        using var dlg = new SaveFileDialog
        {
            Filter = filter,
            FileName = Path.GetFileNameWithoutExtension(_path0 ?? "output") + ext,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            switch (_kind)
            {
                case AssetKind.Tim: File.WriteAllBytes(dlg.FileName, Tim.ToPng(Tim.Parse(_data))); break;
                case AssetKind.Vag: File.WriteAllBytes(dlg.FileName, Vag.ToWav(_data)); break;
                case AssetKind.Tmd: File.WriteAllText(dlg.FileName, Tmd.ToDxf(Tmd.Parse(_data))); break;
            }
            Log($"Wrote {Path.GetFileName(dlg.FileName)}.");
            StatusBus.Report($"Wrote {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("psx asset save", ex);
        }
    }

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);
}
