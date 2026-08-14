// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.GameCube;
using DiscForge.Core.Util;

namespace DiscForge.App.Views;

/// <summary>
/// Opens a GameCube/Wii TPL texture container, lists the textures inside it (dimensions and GX pixel
/// format), previews the selected one, and decodes any or all of them to PNG. The heavy lifting — de-tiling
/// and decoding every GX format (I4…RGBA8, the CI palettes, CMPR) — is <see cref="Tpl"/> in the core; this is
/// just the picker, the preview and the save.
/// </summary>
internal sealed class TextureView : UserControl
{
    private byte[]? _data;
    private IReadOnlyList<TplTexture> _textures = Array.Empty<TplTexture>();

    private readonly ListBox _list = new()
    {
        Location = new Point(12, 60), Size = new Size(260, 380),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom,
        Font = Theme.Mono, IntegralHeight = false,
    };
    private readonly PictureBox _preview = new()
    {
        Location = new Point(284, 60), Size = new Size(440, 350),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Gainsboro, SizeMode = PictureBoxSizeMode.Zoom,
    };
    private readonly Label _status = new()
    {
        Location = new Point(284, 416), Size = new Size(440, 24),
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, Font = Theme.Ui, ForeColor = Color.Gray,
    };

    public TextureView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) LoadTexture(f[0]); };

        var open = new Button { Text = "Open TPL…", Location = new Point(12, 20), Width = 110, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => Choose();
        var save = new Button { Text = "Extract all to PNG…", Location = new Point(130, 20), Width = 160, FlatStyle = FlatStyle.System };
        save.Click += (_, _) => ExtractAll();
        var saveOne = new Button { Text = "Save selected…", Location = new Point(298, 20), Width = 130, FlatStyle = FlatStyle.System };
        saveOne.Click += (_, _) => SaveSelected();

        _list.SelectedIndexChanged += (_, _) => ShowSelected();

        Controls.Add(open);
        Controls.Add(save);
        Controls.Add(saveOne);
        Controls.Add(_list);
        Controls.Add(_preview);
        Controls.Add(_status);
        _status.Text = "Open or drop a .tpl file.";
    }

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void Choose()
    {
        using var dlg = new OpenFileDialog { Filter = "TPL textures (*.tpl)|*.tpl|All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (dlg.ShowDialog() == DialogResult.OK) LoadTexture(dlg.FileName);
    }

    private void LoadTexture(string path)
    {
        AppSettings.LastImageDirectory = Path.GetDirectoryName(path);
        _list.Items.Clear();
        _preview.Image = null;
        try
        {
            _data = File.ReadAllBytes(path);
            _textures = Tpl.Read(_data);
            foreach (var t in _textures)
                _list.Items.Add($"#{t.Index,-3} {t.Width}×{t.Height} {t.FormatName}");
            _status.Text = $"{_textures.Count} texture(s) in {Path.GetFileName(path)}";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            StatusBus.Report($"{Path.GetFileName(path)}: {_textures.Count} texture(s)");
        }
        catch (Exception ex)
        {
            _status.Text = $"Not a readable TPL: {ex.Message}";
            _status.ForeColor = Color.FromArgb(0xA0, 0x30, 0x30);
            AppLog.WriteException("tpl-load", ex);
        }
    }

    private void ShowSelected()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _textures.Count) return;
        try { _preview.Image = ToBitmap(_textures[i]); }
        catch (Exception ex) { AppLog.WriteException("tpl-preview", ex); }
    }

    private static Bitmap ToBitmap(TplTexture t)
    {
        // The core already produced straight RGBA; hand it to GDI+ as a 32-bit bitmap.
        var bmp = new Bitmap(t.Width, t.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, t.Width, t.Height);
        var bits = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            // GDI+ 32bppArgb is BGRA in memory; convert from our RGBA.
            var bgra = new byte[t.Rgba.Length];
            for (int p = 0; p < t.Rgba.Length; p += 4)
            {
                bgra[p] = t.Rgba[p + 2];     // B
                bgra[p + 1] = t.Rgba[p + 1]; // G
                bgra[p + 2] = t.Rgba[p];     // R
                bgra[p + 3] = t.Rgba[p + 3]; // A
            }
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bits.Scan0, bgra.Length);
        }
        finally { bmp.UnlockBits(bits); }
        return bmp;
    }

    private void SaveSelected()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _textures.Count) { _status.Text = "Select a texture first."; return; }
        var t = _textures[i];
        using var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = $"tex{t.Index:D3}.png" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, PngWriter.EncodeRgba(t.Rgba, t.Width, t.Height));
            _status.Text = $"Saved #{t.Index} → {Path.GetFileName(dlg.FileName)}";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
        }
        catch (Exception ex) { _status.Text = $"Save failed: {ex.Message}"; _status.ForeColor = Color.FromArgb(0xA0, 0x30, 0x30); }
    }

    private void ExtractAll()
    {
        if (_textures.Count == 0) { _status.Text = "Open a TPL first."; return; }
        using var folder = new FolderBrowserDialog
        {
            Description = "Extract all textures (PNG) to…",
            SelectedPath = AppSettings.LastExtractDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastExtractDirectory = folder.SelectedPath;
        try
        {
            foreach (var t in _textures)
            {
                string outPng = Path.Combine(folder.SelectedPath, $"tex{t.Index:D3}.png");
                File.WriteAllBytes(outPng, PngWriter.EncodeRgba(t.Rgba, t.Width, t.Height));
            }
            _status.Text = $"Wrote {_textures.Count} PNG(s) to {folder.SelectedPath}";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
        }
        catch (Exception ex) { _status.Text = $"Extract failed: {ex.Message}"; _status.ForeColor = Color.FromArgb(0xA0, 0x30, 0x30); }
    }
}
