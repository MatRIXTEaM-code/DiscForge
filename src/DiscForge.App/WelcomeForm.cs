// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App;

/// <summary>
/// Shown once, on first run. This is the first thing a customer sees, so it
/// orients them in four lines and gets out of the way.
/// </summary>
internal sealed class WelcomeForm : Form
{
    private readonly CheckBox _dontShow = new()
    {
        Text = "Don't show this again", Checked = true, AutoSize = true,
        Location = new Point(24, 300), Font = Theme.Ui, ForeColor = Theme.TextMuted,
    };

    public WelcomeForm()
    {
        Text = "Welcome to DiscForge";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(540, 350);
        Font = Theme.Ui;
        BackColor = Theme.Surface;

        var banner = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Theme.Accent };
        banner.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var bg = new SolidBrush(Theme.Accent);
            g.FillRectangle(bg, banner.ClientRectangle);

            using var ring = new Pen(Color.FromArgb(110, Color.White), 2f);
            using var hub = new SolidBrush(Color.FromArgb(70, Color.White));
            g.DrawEllipse(ring, 24, 24, 48, 48);
            g.FillEllipse(hub, 40, 40, 16, 16);

            g.DrawString("Welcome to DiscForge", Theme.Title, Brushes.White, 90, 28);
            using var sub = new SolidBrush(Color.FromArgb(195, Color.White));
            g.DrawString("CD / DVD / Blu-ray imaging", Theme.Ui, sub, 92, 60);
        };

        var intro = new Label
        {
            Location = new Point(24, 116), Size = new Size(492, 20),
            Font = Theme.UiBold, ForeColor = Theme.Text,
            Text = "Everything here works from the left-hand rail:",
        };

        var body = new Label
        {
            Location = new Point(24, 142), Size = new Size(492, 148),
            Font = Theme.Ui, ForeColor = Theme.Text,
            Text =
                "Read Disc      Rip a CD, DVD or Blu-ray to an image.\r\n\r\n" +
                "Create Image   Build a data image from a folder of files.\r\n\r\n" +
                "Copy Disc      Duplicate a disc — checked before it starts.\r\n\r\n" +
                "Burn           Write an image or ISO to disc, and verify it.\r\n\r\n" +
                "Inspect        Open an image, see its tracks, check its integrity.\r\n\r\n" +
                "Tip: drag a .cdi onto the window to open it, or drop files onto Create.",
        };

        var start = new Button
        {
            Text = "Get started", DialogResult = DialogResult.OK,
            Size = new Size(110, 32), Location = new Point(406, 296),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        Theme.StylePrimary(start);

        Controls.Add(intro);
        Controls.Add(body);
        Controls.Add(_dontShow);
        Controls.Add(start);
        Controls.Add(banner);          // added last so it docks to the very top

        AcceptButton = start;
    }

    /// <summary>True if the user asked not to see this again.</summary>
    public bool DoNotShowAgain => _dontShow.Checked;
}
