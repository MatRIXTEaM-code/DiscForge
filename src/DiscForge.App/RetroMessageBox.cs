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
/// A drop-in replacement for <see cref="MessageBox"/> that draws in the grey
/// retro chrome instead of the OS dialog style. Windows renders its own
/// message boxes with the current system theme, which no amount of app-side
/// styling can touch — so to keep confirmations and alerts consistent with
/// the CDRWIN look, DiscForge shows its own. The <c>Show</c> overloads mirror
/// the <see cref="MessageBox"/> ones the app uses, and return the same
/// <see cref="DialogResult"/>, so call sites change only in name.
/// </summary>
internal static class RetroMessageBox
{
    public static DialogResult Show(string text, string caption = "DiscForge",
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
        => Show(null, text, caption, buttons, icon, defaultButton);

    public static DialogResult Show(IWin32Window? owner, string text,
        string caption = "DiscForge",
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        using var dlg = new RetroMessageForm(text, caption, buttons, icon, defaultButton);
        return owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
    }

    private sealed class RetroMessageForm : Form
    {
        private readonly string _text;
        private readonly MessageBoxIcon _icon;

        public RetroMessageForm(string text, string caption, MessageBoxButtons buttons,
                                MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            _text = text;
            _icon = icon;

            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            BackColor = RetroTheme.Face;
            Font = RetroTheme.Ui;

            // Measure the message to size the window (wrapping at ~360px).
            int iconW = icon == MessageBoxIcon.None ? 0 : 48;
            int textLeft = 16 + iconW;
            var proposed = new Size(360 - iconW, 1000);
            var textSize = TextRenderer.MeasureText(text, RetroTheme.Ui, proposed,
                TextFormatFlags.WordBreak);
            int contentW = textLeft + textSize.Width + 16;
            int width = Math.Max(320, Math.Min(contentW, 480));
            int textH = Math.Max(icon == MessageBoxIcon.None ? 0 : 40, textSize.Height);

            var (labels, results, defaultIndex) = ButtonSpec(buttons, defaultButton);
            int buttonRowTop = 24 + textH + 20;
            ClientSize = new Size(width, buttonRowTop + 26 + 12);

            var msg = new Label
            {
                Text = text, Font = RetroTheme.Ui, ForeColor = RetroTheme.Text,
                Location = new Point(textLeft, 22),
                Size = new Size(width - textLeft - 12, textH),
                AutoSize = false, BackColor = Color.Transparent,
            };
            Controls.Add(msg);

            // Right-aligned button row, classic order.
            int bw = 80, gap = 8;
            int totalW = labels.Length * bw + (labels.Length - 1) * gap;
            int x = width - 12 - totalW;
            for (int i = 0; i < labels.Length; i++)
            {
                var b = new Button
                {
                    Text = labels[i], FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
                    Size = new Size(bw, 26), Location = new Point(x, buttonRowTop),
                    DialogResult = results[i],
                };
                x += bw + gap;
                Controls.Add(b);
                if (i == defaultIndex) AcceptButton = b;
            }

            // Cancel/close mapping.
            CancelButton = null;
            foreach (Control c in Controls)
                if (c is Button cb && cb.DialogResult is DialogResult.Cancel or DialogResult.No)
                    { CancelButton = cb; break; }
        }

        private static (string[] labels, DialogResult[] results, int defaultIndex) ButtonSpec(
            MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton)
        {
            var (labels, results) = buttons switch
            {
                MessageBoxButtons.OKCancel =>
                    (new[] { "OK", "Cancel" }, new[] { DialogResult.OK, DialogResult.Cancel }),
                MessageBoxButtons.YesNo =>
                    (new[] { "Yes", "No" }, new[] { DialogResult.Yes, DialogResult.No }),
                MessageBoxButtons.YesNoCancel =>
                    (new[] { "Yes", "No", "Cancel" },
                     new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel }),
                MessageBoxButtons.RetryCancel =>
                    (new[] { "Retry", "Cancel" }, new[] { DialogResult.Retry, DialogResult.Cancel }),
                _ => (new[] { "OK" }, new[] { DialogResult.OK }),
            };
            int def = defaultButton switch
            {
                MessageBoxDefaultButton.Button2 => Math.Min(1, labels.Length - 1),
                MessageBoxDefaultButton.Button3 => Math.Min(2, labels.Length - 1),
                _ => 0,
            };
            return (labels, results, def);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_icon == MessageBoxIcon.None) return;

            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var box = new Rectangle(16, 22, 32, 32);

            switch (_icon)
            {
                case MessageBoxIcon.Warning:   // yellow triangle, "!"
                {
                    using (var tri = new SolidBrush(Color.FromArgb(0xF0, 0xC0, 0x10)))
                    using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path.AddPolygon(new[]
                        {
                            new Point(box.Left + 16, box.Top),
                            new Point(box.Right, box.Bottom),
                            new Point(box.Left, box.Bottom),
                        });
                        g.FillPath(tri, path);
                        using var edge = new Pen(Color.FromArgb(0x80, 0x60, 0x00), 1.5f);
                        g.DrawPath(edge, path);
                    }
                    using (var bang = new Font("MS Sans Serif", 14f, FontStyle.Bold))
                        TextRenderer.DrawText(g, "!", bang,
                            new Rectangle(box.Left, box.Top + 8, box.Width, box.Height),
                            Color.Black, TextFormatFlags.HorizontalCenter);
                    break;
                }

                case MessageBoxIcon.Error:     // red circle, "×"
                {
                    using (var circ = new SolidBrush(Color.FromArgb(0xC0, 0x20, 0x20)))
                        g.FillEllipse(circ, box);
                    using (var mark = new Font("MS Sans Serif", 14f, FontStyle.Bold))
                        TextRenderer.DrawText(g, "\u00D7", mark, box, Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    break;
                }

                default:                        // Information: blue circle, "i"
                {
                    using (var circ = new SolidBrush(RetroTheme.TitleActive))
                        g.FillEllipse(circ, box);
                    using (var ci = new Font("Times New Roman", 15f, FontStyle.Bold | FontStyle.Italic))
                        TextRenderer.DrawText(g, "i", ci, box, Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    break;
                }
            }
        }
    }
}
