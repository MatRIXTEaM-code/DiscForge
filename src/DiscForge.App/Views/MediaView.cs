// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Cdg;
using DiscForge.Core.GameAudio;

namespace DiscForge.App.Views;

/// <summary>
/// Game-media decoders that turn a console asset into something a PC can open:
/// a CRI ADX ADPCM stream into a 16-bit PCM WAV, and a CD+G graphics stream into
/// a rendered PNG frame (with a live preview). Thin shells over
/// <see cref="AdxDecoder"/> / <see cref="AdxReader"/> and <see cref="CdgRenderer"/>.
/// </summary>
internal sealed class MediaView : UserControl
{
    // ---- ADX ----
    private readonly TextBox _adxIn = new() { ReadOnly = true, Location = new Point(70, 40), Width = 470, Font = Theme.Ui };
    private readonly Label _adxInfo = new() { AutoSize = true, Location = new Point(70, 70), Font = Theme.Ui, ForeColor = Color.Gray };
    private string? _adxPath;

    // ---- CD+G ----
    private readonly TextBox _cdgIn = new() { ReadOnly = true, Location = new Point(70, 40), Width = 340, Font = Theme.Ui };
    private readonly TextBox _at = new() { Location = new Point(494, 40), Width = 46, Font = Theme.Mono };
    private readonly PictureBox _preview = new()
    {
        Location = new Point(70, 100), Size = new Size(300, 192), BorderStyle = BorderStyle.FixedSingle,
        SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black,
    };
    private string? _cdgPath;
    private byte[]? _cdgPng;

    public MediaView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        // ADX → WAV
        var adxBox = new GroupBox { Text = "ADX → WAV", Location = new Point(12, 6), Size = new Size(710, 108), Font = Theme.UiBold };
        adxBox.Controls.Add(new Label { Text = "Input:", AutoSize = true, Location = new Point(12, 43), Font = Theme.Ui });
        var adxPick = new Button { Text = "…", Location = new Point(546, 39), Width = 30, FlatStyle = FlatStyle.System };
        adxPick.Click += (_, _) => ChooseAdx();
        var adxGo = new Button { Text = "Decode to WAV…", Location = new Point(586, 38), Width = 110, FlatStyle = FlatStyle.System };
        adxGo.Click += (_, _) => DecodeAdx();
        adxBox.Controls.AddRange(new Control[] { _adxIn, adxPick, adxGo, _adxInfo });

        // CD+G → PNG
        var cdgBox = new GroupBox { Text = "CD+G → PNG", Location = new Point(12, 122), Size = new Size(710, 306), Font = Theme.UiBold };
        cdgBox.Controls.Add(new Label { Text = "Input:", AutoSize = true, Location = new Point(12, 43), Font = Theme.Ui });
        cdgBox.Controls.Add(new Label { Text = "At (MM:SS):", AutoSize = true, Location = new Point(416, 43), Font = Theme.Ui });
        cdgBox.Controls.Add(new Label { Text = "blank = final frame", AutoSize = true, Location = new Point(548, 43), Font = Theme.Ui, ForeColor = Color.Gray });
        var cdgPick = new Button { Text = "…", Location = new Point(586, 66), Width = 30, FlatStyle = FlatStyle.System };
        cdgPick.Click += (_, _) => ChooseCdg();
        var cdgRender = new Button { Text = "Render", Location = new Point(626, 65), Width = 70, FlatStyle = FlatStyle.System };
        cdgRender.Click += (_, _) => RenderCdg();
        var cdgSave = new Button { Text = "Save PNG…", Location = new Point(400, 270), Width = 100, FlatStyle = FlatStyle.System };
        cdgSave.Click += (_, _) => SaveCdg();
        cdgBox.Controls.AddRange(new Control[] { _cdgIn, _at, cdgPick, cdgRender, _preview, cdgSave });

        Controls.Add(adxBox);
        Controls.Add(cdgBox);
    }

    // ---- ADX ----

    private void ChooseAdx()
    {
        using var dlg = new OpenFileDialog { Filter = "CRI ADX (*.adx)|*.adx|All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _adxPath = dlg.FileName; _adxIn.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        try
        {
            var info = AdxReader.ReadInfo(File.ReadAllBytes(dlg.FileName));
            double secs = info.SampleRate > 0 ? info.TotalSamples / (double)info.SampleRate : 0;
            _adxInfo.Text = $"{info.Channels} ch, {info.SampleRate} Hz, {info.TotalSamples:N0} samples ({secs:0.0}s), encoding 0x{info.Encoding:X2}";
        }
        catch (Exception ex) { _adxInfo.Text = "Not a readable ADX: " + ex.Message; }
    }

    private void DecodeAdx()
    {
        if (_adxPath is null) { RetroMessageBox.Show("Choose an ADX file first."); return; }
        using var dlg = new SaveFileDialog { Filter = "WAV (*.wav)|*.wav", FileName = Path.GetFileNameWithoutExtension(_adxPath) + ".wav" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var data = File.ReadAllBytes(_adxPath);
            using (var os = File.Create(dlg.FileName))
                AdxDecoder.DecodeToWav(new MemoryStream(data), os);
            StatusBus.Report($"Wrote {Path.GetFileName(dlg.FileName)}");
            AppLog.Write($"ADX -> WAV {Path.GetFileName(_adxPath)}");
        }
        catch (Exception ex) { RetroMessageBox.Show(ex.Message); AppLog.WriteException("adx-decode", ex); }
    }

    // ---- CD+G ----

    private void ChooseCdg()
    {
        using var dlg = new OpenFileDialog { Filter = "CD+G (*.cdg)|*.cdg|All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _cdgPath = dlg.FileName; _cdgIn.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        RenderCdg();
    }

    private void RenderCdg()
    {
        if (_cdgPath is null) { RetroMessageBox.Show("Choose a CD+G file first."); return; }
        try
        {
            var cdg = File.ReadAllBytes(_cdgPath);
            CdgImage image;
            string at = _at.Text.Trim();
            if (at.Length > 0)
            {
                var parts = at.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int mm) || !int.TryParse(parts[1], out int ss))
                { RetroMessageBox.Show("Time must be MM:SS."); return; }
                image = CdgRenderer.RenderFrameAt(cdg, new TimeSpan(0, mm, ss));
            }
            else image = CdgRenderer.RenderFinalFrame(cdg);

            _cdgPng = CdgRenderer.RenderToPng(image);
            using var ms = new MemoryStream(_cdgPng);
            using var loaded = Image.FromStream(ms);
            _preview.Image?.Dispose();
            _preview.Image = new Bitmap(loaded);   // detach from the stream so it can be disposed safely
            StatusBus.Report($"Rendered {image.Width}x{image.Height} CD+G frame");
        }
        catch (Exception ex) { RetroMessageBox.Show(ex.Message); AppLog.WriteException("cdg-render", ex); }
    }

    private void SaveCdg()
    {
        if (_cdgPng is null) { RetroMessageBox.Show("Render a frame first."); return; }
        using var dlg = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = (_cdgPath is not null ? Path.GetFileNameWithoutExtension(_cdgPath) : "frame") + ".png" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try { File.WriteAllBytes(dlg.FileName, _cdgPng); StatusBus.Report($"Saved {Path.GetFileName(dlg.FileName)}"); }
        catch (Exception ex) { RetroMessageBox.Show(ex.Message); }
    }
}
