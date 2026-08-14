// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Devices;
using DiscForge.Devices;

namespace DiscForge.App.Views;

/// <summary>
/// A period-correct copy panel in the DUP-DVD / DiscJuggler idiom: raised
/// "Origen"/"Destino" plates with drop-downs, an etched options frame, and a
/// classic sunken "Reading…" progress trough. It's a retro-skinned face over
/// the same drive detection the modern views use — affectionate pastiche, not
/// a reskin of any one product.
///
/// This view opts fully into <see cref="RetroTheme"/>: it paints its own
/// bevels rather than using WinForms chrome, which is the only way to get the
/// two-tone raised-button edges that define the era.
/// </summary>
internal sealed class RetroCopyView : UserControl
{
    private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = RetroTheme.Ui };
    private readonly ComboBox _dest = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = RetroTheme.Ui };
    private readonly Button _detect = new() { Text = "Detect", Font = RetroTheme.Ui, FlatStyle = FlatStyle.System };
    private readonly Button _copy = new() { Text = "Copiar", Font = RetroTheme.UiBold, FlatStyle = FlatStyle.System };
    private readonly Button _options = new() { Text = "Opciones >", Font = RetroTheme.Ui, FlatStyle = FlatStyle.System };
    private readonly Label _status = new()
    {
        Text = "Insert a disc and press Detect.", Font = RetroTheme.Ui,
        ForeColor = RetroTheme.Text, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
    };
    private readonly ClassicProgress _progress = new();
    private readonly Action<string> _navigate;

    public RetroCopyView(Action<string> navigate)
    {
        _navigate = navigate;
        DoubleBuffered = true;
        BackColor = RetroTheme.Face;
        Size = new Size(520, 340);

        // Origen / Destino plates.
        _source.SetBounds(120, 40, 300, 22);
        _dest.SetBounds(120, 96, 300, 22);
        _detect.SetBounds(430, 39, 70, 24);
        _dest.Items.Add("Image  (C:\\…\\disc.cdi)");
        _dest.SelectedIndex = 0;

        // Action row.
        _copy.SetBounds(16, 150, 80, 26);
        _options.SetBounds(104, 150, 90, 26);

        // Progress trough + status.
        _progress.SetBounds(56, 200, 448, 22);
        _status.SetBounds(56, 226, 448, 18);

        _detect.Click += async (_, _) => await DetectAsync();
        _options.Click += (_, _) => _navigate("copy");   // hand off to the full copy view
        _copy.Click += (_, _) =>
        {
            _status.Text = "For the full copy workflow, use the modern Copy view (Opciones ▸).";
            _navigate("copy");
        };

        Controls.Add(_source);
        Controls.Add(_dest);
        Controls.Add(_detect);
        Controls.Add(_copy);
        Controls.Add(_options);
        Controls.Add(_progress);
        Controls.Add(_status);

        _ = DetectAsync();
    }

    private async Task DetectAsync()
    {
        _status.Text = "Detecting drives…";
        try
        {
            var drives = await Task.Run(() => DriveDetector.DetectAll());
            _source.Items.Clear();
            foreach (var d in drives)
                _source.Items.Add($"{d.DevicePath}  {d.Vendor} {d.Model}");
            if (_source.Items.Count > 0) _source.SelectedIndex = 0;
            _status.Text = drives.Count == 0
                ? "No optical drives detected."
                : $"{drives.Count} drive(s) detected.";
        }
        catch (Exception ex)
        {
            _status.Text = "Detect failed: " + ex.Message;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(RetroTheme.Face);

        // Title bar.
        RetroTheme.PaintTitleBar(g, new Rectangle(8, 8, Width - 24, 22),
            "DiscForge Copy  —  Classic");

        // Origen / Destino outer frame (etched).
        RetroTheme.DrawBevel(g, new Rectangle(8, 34, Width - 24, 96), RetroTheme.Bevel.Etched);

        // Labels with little disc emblems.
        DrawDisc(g, 24, 42, RetroTheme.TitleActive2);
        TextRenderer.DrawText(g, "Origen", RetroTheme.UiBold, new Point(56, 44), RetroTheme.Text);
        DrawDisc(g, 24, 98, Color.FromArgb(0xC0, 0x80, 0x20));
        TextRenderer.DrawText(g, "Destino", RetroTheme.UiBold, new Point(56, 100), RetroTheme.Text);

        // Options frame (etched), mimicking DUP-DVD's lower panel.
        RetroTheme.DrawBevel(g, new Rectangle(8, 190, Width - 24, 118), RetroTheme.Bevel.Etched);
        TextRenderer.DrawText(g, "Grabación", RetroTheme.UiBold, new Point(16, 184), RetroTheme.Text);
    }

    private static void DrawDisc(Graphics g, int x, int y, Color tint)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var body = new SolidBrush(tint);
        g.FillEllipse(body, x, y, 24, 24);
        using var hole = new SolidBrush(RetroTheme.Face);
        g.FillEllipse(hole, x + 9, y + 9, 6, 6);
        using var sheen = new Pen(Color.FromArgb(120, 255, 255, 255), 2);
        g.DrawArc(sheen, x + 3, y + 3, 18, 18, 200, 80);
    }

    /// <summary>The classic sunken progress trough with segmented blue fill.</summary>
    private sealed class ClassicProgress : Control
    {
        private int _value;
        public int Value { get => _value; set { _value = Math.Clamp(value, 0, 100); Invalidate(); } }

        public ClassicProgress() { DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(RetroTheme.Face);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            RetroTheme.DrawBevel(g, r, RetroTheme.Bevel.Sunken);

            // Segmented fill, like the Win9x progress control.
            int inner = r.Width - 6;
            int filled = inner * _value / 100;
            using var brush = new SolidBrush(RetroTheme.TitleActive2);
            int seg = 8, x = 3;
            while (x - 3 < filled)
            {
                g.FillRectangle(brush, x, 3, seg - 2, r.Height - 6);
                x += seg;
            }
        }
    }
}
