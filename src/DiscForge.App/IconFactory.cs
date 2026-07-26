// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Drawing.Drawing2D;

namespace DiscForge.App;

/// <summary>
/// Draws small 16×16 toolbar glyphs in code — no binary image assets in the
/// source tree, so the whole UI stays reviewable as text. Simple, recognizable
/// shapes in the app accent colour — no bitmaps to ship, lose, or scale badly.
/// </summary>
internal static class IconFactory
{
    private static readonly Color Ink = Theme.Accent;

    private static Image Draw(Action<Graphics> paint)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        paint(g);
        return bmp;
    }

    public static Image Home() => Draw(g =>
    {
        using var pen = new Pen(Ink, 1.4f);
        using var fill = new SolidBrush(Color.FromArgb(60, Ink));
        var body = new Rectangle(4, 7, 8, 6);
        g.FillRectangle(fill, body);
        g.DrawRectangle(pen, body);
        g.DrawLines(pen, new[] { new Point(3, 8), new Point(8, 3), new Point(13, 8) });
    });

    public static Image Inspect() => Draw(g =>
    {
        using var pen = new Pen(Ink, 1.6f);
        g.DrawEllipse(pen, 3, 3, 7, 7);
        g.DrawLine(pen, 9, 9, 13, 13);
    });

    public static Image Create() => Draw(g =>
    {
        using var pen = new Pen(Ink, 1.4f);
        using var fill = new SolidBrush(Color.FromArgb(50, Ink));
        // disc
        g.FillEllipse(fill, 2, 3, 10, 10);
        g.DrawEllipse(pen, 2, 3, 10, 10);
        g.DrawEllipse(pen, 6, 7, 2, 2);
        // plus badge
        using var green = new Pen(Color.FromArgb(0x2E, 0x8B, 0x57), 1.8f);
        g.DrawLine(green, 12, 11, 12, 15);
        g.DrawLine(green, 10, 13, 14, 13);
    });

    public static Image Drives() => Draw(g =>
    {
        using var pen = new Pen(Ink, 1.3f);
        using var fill = new SolidBrush(Color.FromArgb(40, Ink));
        var body = new Rectangle(2, 5, 12, 6);
        g.FillRectangle(fill, body);
        g.DrawRectangle(pen, body);
        g.DrawLine(pen, 4, 8, 9, 8);       // slot
        using var dot = new SolidBrush(Color.FromArgb(0x2E, 0x8B, 0x57));
        g.FillEllipse(dot, 11, 7, 2, 2);   // activity LED
    });

    public static Image Burn() => Draw(g =>
    {
        using var pen = new Pen(Ink, 1.3f);
        g.DrawEllipse(pen, 2, 3, 10, 10);
        g.DrawEllipse(pen, 6, 7, 2, 2);
        // flame
        using var flame = new SolidBrush(Color.FromArgb(0xE2, 0x55, 0x22));
        var path = new GraphicsPath();
        path.AddPolygon(new[] { new Point(12, 2), new Point(15, 7), new Point(12, 9), new Point(10, 6) });
        g.FillPath(flame, path);
    });

    public static Image Verify() => Draw(g =>
    {
        using var green = new Pen(Color.FromArgb(0x2E, 0x8B, 0x57), 2f);
        g.DrawLines(green, new[] { new Point(3, 8), new Point(7, 12), new Point(13, 4) });
    });

    /// <summary>Two discs with an arrow between — copying one to the other.</summary>
    public static Image Copy() => Draw(g =>
    {
        using var pen = new Pen(Ink, 1.2f);
        using var fill = new SolidBrush(Color.FromArgb(45, Ink));
        g.FillEllipse(fill, 0, 4, 8, 8);
        g.DrawEllipse(pen, 0, 4, 8, 8);
        g.FillEllipse(fill, 8, 4, 8, 8);
        g.DrawEllipse(pen, 8, 4, 8, 8);
        using var arrow = new Pen(Color.FromArgb(0x2E, 0x8B, 0x57), 1.5f)
        {
            EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor,
        };
        g.DrawLine(arrow, 5, 8, 11, 8);
    });

    /// <summary>Disc with an arrow pulling off it — reading a disc to a file.</summary>
    public static Image Read() => Draw(g =>
    {
        using var pen = new Pen(Ink, 1.3f);
        using var fill = new SolidBrush(Color.FromArgb(50, Ink));
        g.FillEllipse(fill, 1, 3, 10, 10);
        g.DrawEllipse(pen, 1, 3, 10, 10);
        g.DrawEllipse(pen, 5, 7, 2, 2);
        // arrow off to the right
        using var arrow = new Pen(Color.FromArgb(0x2E, 0x8B, 0x57), 1.6f)
        {
            EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor,
        };
        g.DrawLine(arrow, 11, 8, 15, 8);
    });
}
