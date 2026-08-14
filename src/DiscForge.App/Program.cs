// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        AppLog.Start();

        // Catch what would otherwise be a silent death or a bare .NET crash box,
        // so a failure leaves evidence behind.
        Application.ThreadException += (_, e) => Fatal("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Fatal("background thread", ex);
        };

        // Per-monitor DPI awareness. Modern WinForms requires this in code
        // rather than the manifest (WFAC010); must precede window creation.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new CdrwinLauncher());
    }

    private static void Fatal(string where, Exception ex)
    {
        AppLog.WriteException(where, ex);
        MessageBox.Show(
            $"An unexpected error occurred.\r\n\r\n{ex.GetType().Name}: {ex.Message}\r\n\r\n" +
            $"The details have been written to:\r\n{AppLog.FilePath}\r\n\r\n" +
            "Help \u25B8 Save Diagnostics… will package this for a bug report.",
            "DiscForge", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

/// <summary>
/// The product's visual language.
///
/// This began as a deliberate early-2000s homage — Tahoma, steel-blue gradients,
/// the DiscJuggler era. DiscForge is a product people pay for, so it now reads as
/// a current Windows application: Segoe UI, flat surfaces, one accent colour used
/// sparingly, and space to breathe. The restraint is the point — a disc tool
/// should feel like an instrument, not a toy.
/// </summary>
internal static class Theme
{
    // --- type ---------------------------------------------------------------
    // Segoe UI Variable is the Windows 11 face; Segoe UI is the fallback that
    // every supported version has.
    private static readonly string Face =
        IsAvailable("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    public static readonly Font Ui = new(Face, 9f);
    public static readonly Font UiBold = new(Face, 9f, FontStyle.Bold);
    public static readonly Font Heading = new(
        IsAvailable("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI",
        16f, FontStyle.Regular);
    public static readonly Font Title = new(
        IsAvailable("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI",
        20f, FontStyle.Bold);
    public static readonly Font Small = new(Face, 8.25f);
    public static readonly Font Mono = new(IsAvailable("Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9f);

    // --- colour -------------------------------------------------------------
    // One accent, a neutral scale, and semantic colours. Nothing decorative.

    /// <summary>The accent. Deep enough for white text, calm enough to live with.</summary>
    public static readonly Color Accent = Color.FromArgb(0x1F, 0x4F, 0x8B);
    public static readonly Color AccentHover = Color.FromArgb(0x2A, 0x66, 0xB0);
    public static readonly Color AccentSubtle = Color.FromArgb(0xEA, 0xF1, 0xFA);

    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceAlt = Color.FromArgb(0xF7, 0xF8, 0xFA);
    public static readonly Color Sidebar = Color.FromArgb(0xF3, 0xF4, 0xF7);
    public static readonly Color Border = Color.FromArgb(0xE1, 0xE4, 0xEA);

    public static readonly Color Text = Color.FromArgb(0x1B, 0x1D, 0x22);
    public static readonly Color TextMuted = Color.FromArgb(0x6A, 0x70, 0x7C);
    public static readonly Color TextOnAccent = Color.White;

    public static readonly Color Good = Color.FromArgb(0x1E, 0x7A, 0x46);
    public static readonly Color Warn = Color.FromArgb(0xA1, 0x62, 0x07);
    public static readonly Color Bad = Color.FromArgb(0xB4, 0x25, 0x25);

    // Kept so existing header painting still compiles; both now the same flat accent.
    public static readonly Color HeaderTop = Accent;
    public static readonly Color HeaderBottom = Accent;
    public static readonly Color HeaderText = Color.White;
    public static readonly Color TaskPane = Sidebar;

    private static bool IsAvailable(string family)
    {
        try
        {
            using var f = new Font(family, 9f);
            return string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// The product header: flat, with the name set properly and the strapline
    /// underneath. No gradient — gradients date a UI faster than anything.
    /// </summary>
    public static void PaintHeaderGradient(Control c, PaintEventArgs e, string title, string? subtitle = null)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var bg = new SolidBrush(Accent);
        g.FillRectangle(bg, c.ClientRectangle);

        // A small mark: a disc, drawn rather than shipped as an asset.
        int cy = c.Height / 2;
        using var ring = new Pen(Color.FromArgb(90, Color.White), 1.6f);
        using var hub = new SolidBrush(Color.FromArgb(60, Color.White));
        g.DrawEllipse(ring, 18, cy - 13, 26, 26);
        g.FillEllipse(hub, 27, cy - 4, 8, 8);

        g.DrawString(title, Title, Brushes.White, 56, cy - 20);
        if (subtitle is not null)
        {
            using var sub = new SolidBrush(Color.FromArgb(190, Color.White));
            g.DrawString(subtitle, Small, sub, 58, cy + 4);
        }
    }

    /// <summary>A flat primary button — the one action a screen wants you to take.</summary>
    public static void StylePrimary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = Accent;
        b.ForeColor = TextOnAccent;
        b.Font = UiBold;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.EnabledChanged += (_, _) =>
        {
            b.BackColor = b.Enabled ? Accent : Border;
            b.ForeColor = b.Enabled ? TextOnAccent : TextMuted;
        };
    }

    /// <summary>A quieter button for everything else.</summary>
    public static void StyleSecondary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = Surface;
        b.ForeColor = Text;
        b.Font = Ui;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.MouseOverBackColor = SurfaceAlt;
    }
}

/// <summary>
/// A flat renderer for menus, toolbars and the status bar.
///
/// WinForms' default ToolStripProfessionalRenderer paints Office-2003 gradients
/// and blue selection washes. Overriding the colour table is the cheapest way to
/// make a WinForms app read as current rather than as a relic.
/// </summary>
internal sealed class FlatRenderer : ToolStripProfessionalRenderer
{
    public FlatRenderer() : base(new FlatColours()) { RoundedEdges = false; }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // A single hairline under the strip, instead of a 3D edge.
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, 0, e.AffectedBounds.Height - 1,
                            e.AffectedBounds.Width, e.AffectedBounds.Height - 1);
    }

    private sealed class FlatColours : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Theme.Surface;
        public override Color ToolStripGradientMiddle => Theme.Surface;
        public override Color ToolStripGradientEnd => Theme.Surface;
        public override Color MenuStripGradientBegin => Theme.Surface;
        public override Color MenuStripGradientEnd => Theme.Surface;
        public override Color StatusStripGradientBegin => Theme.SurfaceAlt;
        public override Color StatusStripGradientEnd => Theme.SurfaceAlt;

        public override Color ButtonSelectedHighlight => Theme.AccentSubtle;
        public override Color ButtonSelectedHighlightBorder => Theme.Accent;
        public override Color ButtonPressedHighlight => Theme.AccentSubtle;
        public override Color ButtonPressedHighlightBorder => Theme.Accent;
        public override Color ButtonSelectedGradientBegin => Theme.AccentSubtle;
        public override Color ButtonSelectedGradientMiddle => Theme.AccentSubtle;
        public override Color ButtonSelectedGradientEnd => Theme.AccentSubtle;
        public override Color ButtonSelectedBorder => Theme.Accent;
        public override Color ButtonPressedGradientBegin => Theme.AccentSubtle;
        public override Color ButtonPressedGradientMiddle => Theme.AccentSubtle;
        public override Color ButtonPressedGradientEnd => Theme.AccentSubtle;

        public override Color MenuItemSelected => Theme.AccentSubtle;
        public override Color MenuItemSelectedGradientBegin => Theme.AccentSubtle;
        public override Color MenuItemSelectedGradientEnd => Theme.AccentSubtle;
        public override Color MenuItemBorder => Theme.Accent;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemPressedGradientBegin => Theme.Surface;
        public override Color MenuItemPressedGradientMiddle => Theme.Surface;
        public override Color MenuItemPressedGradientEnd => Theme.Surface;

        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;

        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Surface;
    }
}
