// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DiscForge.App;

/// <summary>
/// A period-correct skin for people who miss CDRWIN and DiscJuggler — the
/// turn-of-the-millennium look: raised/sunken bevels, "Classic" grey system
/// colours, MS Sans Serif type, and a tile launcher like CDRWIN 4's glossy
/// grid. Toggled from View ▸ Retro skin; purely cosmetic, no behaviour change.
///
/// The bevels are drawn with the classic two-tone edge (white/light on the
/// top-left, dark/shadow on the bottom-right) rather than the modern flat
/// 1px border, because that raised-button chrome is the single strongest
/// visual cue of the era.
/// </summary>
internal static class RetroTheme
{
    public static bool Enabled { get; set; }

    private static Icon? _appIcon;
    /// <summary>The application icon, loaded once from the executable's own
    /// embedded resource. Null only if extraction somehow fails.</summary>
    public static Icon? AppIcon
    {
        get
        {
            if (_appIcon is not null) return _appIcon;
            try
            {
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (exe.Length > 0)
                    _appIcon = Icon.ExtractAssociatedIcon(exe);
            }
            catch { /* leave null */ }
            return _appIcon;
        }
    }

    // The Windows 9x/2000 "Classic" palette.
    public static readonly Color Face = Color.FromArgb(0xC0, 0xC0, 0xC0);       // ButtonFace
    public static readonly Color FaceLit = Color.FromArgb(0xDF, 0xDF, 0xDF);
    public static readonly Color Highlight = Color.White;                       // top-left edge
    public static readonly Color Light = Color.FromArgb(0xDF, 0xDF, 0xDF);
    public static readonly Color Shadow = Color.FromArgb(0x80, 0x80, 0x80);     // bottom-right edge
    public static readonly Color DarkShadow = Color.FromArgb(0x40, 0x40, 0x40);
    public static readonly Color Text = Color.Black;
    public static readonly Color Window = Color.White;
    public static readonly Color TitleActive = Color.FromArgb(0x0A, 0x24, 0x6A);  // classic navy
    public static readonly Color TitleActive2 = Color.FromArgb(0x16, 0x6E, 0xD6); // gradient end

    public static readonly Font Ui = MakeFont("MS Sans Serif", 8.25f);
    public static readonly Font UiBold = MakeFont("MS Sans Serif", 8.25f, FontStyle.Bold);
    public static readonly Font Mono = MakeFont("Courier New", 9f);

    private static Font MakeFont(string family, float size, FontStyle style = FontStyle.Regular)
    {
        try
        {
            using var probe = new Font(family, size, style);
            if (string.Equals(probe.Name, family, StringComparison.OrdinalIgnoreCase))
                return new Font(family, size, style);
        }
        catch { /* fall through */ }
        return new Font("Tahoma", size, style);   // universally present fallback
    }

    // ---- bevel painting ----------------------------------------------------

    public enum Bevel { Raised, Sunken, RaisedThin, SunkenThin, Etched, Group }

    /// <summary>Draw a classic two-tone bevel around a rectangle.</summary>
    public static void DrawBevel(Graphics g, Rectangle r, Bevel style)
    {
        r = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        switch (style)
        {
            case Bevel.Raised:
                Edge(g, r, Highlight, Light, Shadow, DarkShadow);
                break;
            case Bevel.Sunken:
                Edge(g, r, DarkShadow, Shadow, Highlight, Light);
                break;
            case Bevel.RaisedThin:
                Line2(g, r, Highlight, Shadow);
                break;
            case Bevel.SunkenThin:
                Line2(g, r, Shadow, Highlight);
                break;
            case Bevel.Etched:
                Line2(g, r, Shadow, Highlight);
                Line2(g, new Rectangle(r.X + 1, r.Y + 1, r.Width - 1, r.Height - 1), Highlight, Shadow);
                break;
            case Bevel.Group:   // group-box frame: sunken thin, etched look
                Line2(g, r, Shadow, Highlight);
                break;
        }
    }

    // Outer + inner two-tone edge (the raised-button look).
    private static void Edge(Graphics g, Rectangle r, Color tlOuter, Color tlInner,
                             Color brInner, Color brOuter)
    {
        using var pTlO = new Pen(tlOuter);
        using var pTlI = new Pen(tlInner);
        using var pBrI = new Pen(brInner);
        using var pBrO = new Pen(brOuter);
        // outer
        g.DrawLine(pTlO, r.Left, r.Top, r.Right, r.Top);
        g.DrawLine(pTlO, r.Left, r.Top, r.Left, r.Bottom);
        g.DrawLine(pBrO, r.Left, r.Bottom, r.Right, r.Bottom);
        g.DrawLine(pBrO, r.Right, r.Top, r.Right, r.Bottom);
        // inner
        var i = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
        g.DrawLine(pTlI, i.Left, i.Top, i.Right, i.Top);
        g.DrawLine(pTlI, i.Left, i.Top, i.Left, i.Bottom);
        g.DrawLine(pBrI, i.Left, i.Bottom, i.Right, i.Bottom);
        g.DrawLine(pBrI, i.Right, i.Top, i.Right, i.Bottom);
    }

    private static void Line2(Graphics g, Rectangle r, Color tl, Color br)
    {
        using var pTl = new Pen(tl);
        using var pBr = new Pen(br);
        g.DrawLine(pTl, r.Left, r.Top, r.Right, r.Top);
        g.DrawLine(pTl, r.Left, r.Top, r.Left, r.Bottom);
        g.DrawLine(pBr, r.Left, r.Bottom, r.Right, r.Bottom);
        g.DrawLine(pBr, r.Right, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Paint a classic title-bar gradient (active-window navy).</summary>
    public static void PaintTitleBar(Graphics g, Rectangle r, string text)
    {
        using var brush = new LinearGradientBrush(r, TitleActive, TitleActive2, LinearGradientMode.Horizontal);
        g.FillRectangle(brush, r);
        using var f = new Font("MS Sans Serif", 8.25f, FontStyle.Bold);
        TextRenderer.DrawText(g, text, f, new Rectangle(r.X + 4, r.Y, r.Width - 8, r.Height),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}
