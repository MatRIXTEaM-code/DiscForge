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
/// A CDRWIN-style task window: the dialog a launcher button opens. It hosts
/// one of the existing views (Read, Burn, Copy, …) inside period chrome — a
/// navy title bar, a sunken content well, a status strip fed by the view's own
/// progress reports, and a Close button — and runs the <see cref="RetroStyler"/>
/// over the hosted view so its controls take the classic look. The views do
/// the real work unchanged; this is their frame.
///
/// Each task gets its own window, exactly as CDRWIN did — several can be open
/// at once, and closing one leaves the launcher and the others untouched.
/// </summary>
internal sealed class CdrwinTaskWindow : Form
{
    private readonly Panel _well = new()
    {
        Dock = DockStyle.Fill,
        Padding = new Padding(3),
        BackColor = RetroTheme.Face,
    };

    private readonly Label _status;

    private void OnStatus(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => { if (!IsDisposed) _status.Text = message; }); return; }
        _status.Text = message;
    }

    public CdrwinTaskWindow(string title, Control view)
    {
        Text = "DiscForge — " + title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        BackColor = RetroTheme.Face;
        Font = RetroTheme.Ui;

        // Size to the view's own preferred size (plus chrome) so nothing is
        // clipped or forced to scroll; views declare a sensible Size in their
        // constructors. Clamp to something reasonable and to the screen.
        var pref = view.Size;
        int w = Math.Clamp(pref.Width + 40, 560, 1100);
        int h = Math.Clamp(pref.Height + 96, 460, 820);
        MinimumSize = new Size(560, 460);
        ClientSize = new Size(w, h);
        if (RetroTheme.AppIcon is { } ic) Icon = ic;

        view.Dock = DockStyle.Fill;
        _well.Controls.Add(view);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = RetroTheme.Face };
        var close = new Button
        {
            Text = "Close", FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
            Size = new Size(80, 26), Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        close.Location = new Point(bottom.Width - 92, 7);
        close.Click += (_, _) => Close();
        _status = new Label
        {
            AutoSize = false, Font = RetroTheme.Ui, ForeColor = RetroTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft, Text = "Ready.",
            Location = new Point(4, 8), Size = new Size(bottom.Width - 104, 24),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            BorderStyle = BorderStyle.Fixed3D,
        };
        bottom.Controls.Add(_status);
        bottom.Controls.Add(close);
        bottom.Resize += (_, _) =>
        {
            close.Location = new Point(bottom.Width - 92, 7);
            _status.Size = new Size(bottom.Width - 104, 24);
        };

        // Surface the hosted view's status reports while this window lives.
        StatusBus.Changed += OnStatus;
        FormClosed += (_, _) => StatusBus.Changed -= OnStatus;

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 4), BackColor = RetroTheme.Face };
        host.Controls.Add(_well);

        // Add the docked children so the Fill panel is added LAST. WinForms lays
        // docked siblings out in reverse add-order; when the Fill panel is added
        // before the Bottom strip it can be flowed against the raw client and end
        // up offset by a few pixels, leaving an unpainted sliver at the client
        // edge that shows stale desktop content behind the window. Adding Fill
        // last makes it dock first and cover the whole client cleanly.
        Controls.Add(bottom);
        Controls.Add(host);

        // Skin the hosted view once it's parented.
        RetroTheme.Enabled = true;
        RetroStyler.Apply(view);

        _well.Paint += (_, e) =>
            RetroTheme.DrawBevel(e.Graphics,
                new Rectangle(0, 0, _well.Width, _well.Height), RetroTheme.Bevel.Sunken);
    }

    // Belt-and-braces against edge seams: fill the whole client in the theme
    // face before children paint. WinForms clips this to the regions not covered
    // by an opaque docked child, so in the normal case it is a no-op, but if a
    // one- or two-pixel gap ever survives at the client edge it reads as the
    // window face instead of whatever was on screen behind the window.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (var b = new SolidBrush(RetroTheme.Face))
            e.Graphics.FillRectangle(b, ClientRectangle);
        base.OnPaintBackground(e);
    }

    /// <summary>The Settings task: a small preferences + diagnostics window.</summary>
    public static void ShowSettings(IWin32Window owner)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = RetroTheme.Face, Padding = new Padding(16) };

        var diag = new Button
        {
            Text = "Save Diagnostics…", FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
            Location = new Point(16, 20), Size = new Size(140, 26),
        };
        diag.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "Text file (*.txt)|*.txt",
                FileName = $"discforge-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                File.WriteAllText(dlg.FileName, AppLog.BuildReport());
                RetroMessageBox.Show("Diagnostics saved.", "DiscForge",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        var openLog = new Button
        {
            Text = "Open Log Folder", FlatStyle = FlatStyle.System, Font = RetroTheme.Ui,
            Location = new Point(16, 56), Size = new Size(140, 26),
        };
        openLog.Click += (_, _) =>
        {
            try
            {
                var dir = Path.GetDirectoryName(AppLog.FilePath);
                if (dir is not null)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
                    { UseShellExecute = true });
            }
            catch { /* best effort */ }
        };

        var note = new Label
        {
            Text = "DiscForge stores no other preferences yet.\r\n" +
                   "Drive and media settings live in each task window.",
            AutoSize = true, Font = RetroTheme.Ui, ForeColor = RetroTheme.Text,
            Location = new Point(16, 100),
        };

        panel.Controls.Add(diag);
        panel.Controls.Add(openLog);
        panel.Controls.Add(note);

        using var win = new CdrwinTaskWindow("Settings", panel) { ClientSize = new Size(420, 260) };
        win.ShowDialog(owner);
    }
}
