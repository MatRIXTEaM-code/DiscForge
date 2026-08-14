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
/// Applies the retro skin to an arbitrary control tree, so the whole app —
/// not just the purpose-built retro views — takes the Win9x/2000 look when
/// the skin is on. The modern views hardcode <c>Theme</c> fonts and colours
/// at construction; rather than rewrite all of them, this walks the tree once
/// after a view is shown and remaps each control to its classic equivalent:
/// MS Sans Serif type, the grey button face, black text on white fields, and
/// the "System" flat style that gives buttons their period chrome.
///
/// It's deliberately conservative: it never changes layout, only appearance,
/// and it leaves controls that already opted into the retro theme alone.
/// </summary>
internal static class RetroStyler
{
    public static void Apply(Control root)
    {
        Walk(root);
    }

    private static void Walk(Control c)
    {
        Style(c);
        foreach (Control child in c.Controls)
            Walk(child);
    }

    private static void Style(Control c)
    {
        switch (c)
        {
            case Button b:
                b.FlatStyle = FlatStyle.System;
                b.BackColor = RetroTheme.Face;
                b.ForeColor = RetroTheme.Text;
                b.Font = RetroTheme.Ui;
                b.UseVisualStyleBackColor = false;
                break;

            case CheckBox cb:
                cb.FlatStyle = FlatStyle.System;
                cb.BackColor = RetroTheme.Face;
                cb.ForeColor = RetroTheme.Text;
                cb.Font = RetroTheme.Ui;
                break;

            case RadioButton rb:
                rb.FlatStyle = FlatStyle.System;
                rb.BackColor = RetroTheme.Face;
                rb.ForeColor = RetroTheme.Text;
                rb.Font = RetroTheme.Ui;
                break;

            case TextBox tb:
                // Editable single-line fields stay white (the Image path, disc
                // name, etc. — as CDRWIN's own edit fields do). Read-only and
                // multiline output wells go grey, so large panes don't read as
                // a white expanse.
                tb.BorderStyle = BorderStyle.Fixed3D;
                tb.BackColor = (tb.ReadOnly || tb.Multiline) ? RetroTheme.Face : RetroTheme.Window;
                tb.ForeColor = RetroTheme.Text;
                tb.Font = IsMono(tb.Font) ? RetroTheme.Mono : RetroTheme.Ui;
                break;

            case ComboBox combo:
                // Drop-downs: classic sunken white field with a system button.
                combo.FlatStyle = FlatStyle.System;
                combo.BackColor = RetroTheme.Window;
                combo.ForeColor = RetroTheme.Text;
                combo.Font = RetroTheme.Ui;
                break;

            case ListBox lb:
                lb.BorderStyle = BorderStyle.Fixed3D;
                lb.BackColor = RetroTheme.Face;
                lb.ForeColor = RetroTheme.Text;
                lb.Font = IsMono(lb.Font) ? RetroTheme.Mono : RetroTheme.Ui;
                break;

            case ListView lv:
                lv.BorderStyle = BorderStyle.Fixed3D;
                lv.BackColor = RetroTheme.Face;
                lv.ForeColor = RetroTheme.Text;
                lv.Font = RetroTheme.Ui;
                break;

            case DataGridView dgv:
                dgv.BackgroundColor = RetroTheme.Face;
                dgv.BorderStyle = BorderStyle.Fixed3D;
                dgv.Font = RetroTheme.Ui;
                break;

            case ProgressBar pbar:
                pbar.BackColor = RetroTheme.Face;
                break;

            case GroupBox gb:
                gb.BackColor = RetroTheme.Face;
                gb.ForeColor = RetroTheme.Text;
                gb.Font = RetroTheme.Ui;
                break;

            case Label lbl:
                lbl.BackColor = Color.Transparent;
                lbl.ForeColor = RetroTheme.Text;
                lbl.Font = IsMono(lbl.Font) ? RetroTheme.Mono : RetroTheme.Ui;
                break;

            case TabControl tab:
                tab.BackColor = RetroTheme.Face;
                tab.Font = RetroTheme.Ui;
                break;

            case TabPage page:
                page.BackColor = RetroTheme.Face;
                page.Font = RetroTheme.Ui;
                break;

            case Panel or FlowLayoutPanel or TableLayoutPanel or SplitContainer or SplitterPanel:
                c.BackColor = RetroTheme.Face;
                if (c.Font is { } pf && !IsMono(pf)) c.Font = RetroTheme.Ui;
                break;

            case UserControl uc:
                // Don't fight views that paint their own retro chrome.
                if (uc is Views.RetroCopyView) break;
                uc.BackColor = RetroTheme.Face;
                uc.Font = RetroTheme.Ui;
                break;

            default:
                // Everything else: grey face, classic font, black text —
                // harmless where it doesn't apply, and it kills stray white.
                if (c.GetType().Name is not ("TextBox" or "RichTextBox"))
                {
                    if (c.BackColor == Color.White ||
                        c.BackColor == SystemColors.Window ||
                        c.BackColor == SystemColors.Control)
                        c.BackColor = RetroTheme.Face;
                }
                if (c.Font is { } f && !IsMono(f)) c.Font = RetroTheme.Ui;
                break;
        }
    }

    private static bool IsMono(Font f)
        => f.FontFamily.Name.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
        || f.FontFamily.Name.Contains("Cascadia", StringComparison.OrdinalIgnoreCase)
        || f.FontFamily.Name.Contains("Courier", StringComparison.OrdinalIgnoreCase)
        || f.FontFamily.Name.Contains("Mono", StringComparison.OrdinalIgnoreCase);
}
