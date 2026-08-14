// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App.Views;

/// <summary>
/// A DVD-Shrink-style progress window for a video conversion: a live preview of
/// the frame being encoded, the percent-complete bar, and the encode rate, frames
/// per second and estimated time remaining — with a Cancel button. It is display
/// only: the caller owns the encode and pushes updates in (from any thread, marshalled
/// here), and raises <see cref="CancelRequested"/> when the user presses Cancel.
///
/// This mirrors the classic "Analysing" dialog's layout, but for DiscForge's
/// unprotected-DVD / video re-encode — there is no decryption step, so the status
/// line reports the encode, not a key.
/// </summary>
internal sealed class ShrinkProgressDialog : Form
{
    private readonly PictureBox _preview = new()
    {
        Location = new Point(14, 14), Size = new Size(220, 160),
        BorderStyle = BorderStyle.Fixed3D, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black,
    };
    private readonly CheckBox _enablePreview = new()
    {
        Text = "Enable video preview", Checked = true, AutoSize = true,
        Location = new Point(250, 16), Font = Theme.Ui,
    };
    private readonly Label _statusVal = new() { AutoSize = true, Location = new Point(360, 52), Font = Theme.Ui };
    private readonly Label _rateVal = new() { AutoSize = true, Location = new Point(360, 82), Font = Theme.Ui };
    private readonly Label _fpsVal = new() { AutoSize = true, Location = new Point(360, 112), Font = Theme.Ui };
    private readonly Label _timeVal = new() { AutoSize = true, Location = new Point(360, 142), Font = Theme.Ui };
    private readonly ProgressBar _bar = new()
    {
        Location = new Point(14, 186), Size = new Size(560, 18), Minimum = 0, Maximum = 100,
    };
    private readonly Button _cancel = new()
    {
        Text = "Cancel", Location = new Point(494, 214), Width = 80, Height = 26, FlatStyle = FlatStyle.System,
    };

    private bool _done;
    // Cached so the encode thread can read it without a cross-thread control access.
    private volatile bool _previewEnabled = true;

    /// <summary>Raised (on the UI thread) when the user presses Cancel.</summary>
    public event Action? CancelRequested;

    /// <summary>True while the user wants a live preview.</summary>
    public bool PreviewEnabled => !IsDisposed && _previewEnabled;

    public ShrinkProgressDialog(string caption)
    {
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(590, 252);
        BackColor = RetroTheme.Face;
        Font = Theme.Ui;
        ShowInTaskbar = false;

        Controls.Add(_preview);
        Controls.Add(_enablePreview);
        AddRow("Status:", 52, _statusVal);
        AddRow("Rate:", 82, _rateVal);
        AddRow("Frames/sec:", 112, _fpsVal);
        AddRow("Time remaining:", 142, _timeVal);
        Controls.Add(_bar);
        Controls.Add(_cancel);

        _statusVal.Text = "starting…";
        _enablePreview.CheckedChanged += (_, _) => _previewEnabled = _enablePreview.Checked;
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _cancel.Text = "Cancelling…";
            CancelRequested?.Invoke();
        };
        // Closing the window while running is the same as pressing Cancel.
        FormClosing += (_, e) =>
        {
            if (!_done && _cancel.Enabled) { _cancel.Enabled = false; CancelRequested?.Invoke(); }
        };
    }

    private void AddRow(string caption, int y, Label value)
    {
        Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(250, y), Font = Theme.Ui });
        Controls.Add(value);
    }

    /// <summary>Update the percent, and reflect it in the title like the original.</summary>
    public void SetPercent(int percent)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            int p = Math.Clamp(percent, 0, 100);
            _bar.Value = p;
            Text = _done ? Text : $"{p}% Encoding";
        });
    }

    public void SetStats(string status, string rate, string fps, string timeRemaining)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _statusVal.Text = status;
            _rateVal.Text = rate;
            _fpsVal.Text = fps;
            _timeVal.Text = timeRemaining;
        });
    }

    /// <summary>Show a preview frame; the dialog takes ownership of the image.</summary>
    public void SetPreview(Image image)
    {
        if (IsDisposed) { image.Dispose(); return; }
        BeginInvoke(() =>
        {
            var old = _preview.Image;
            _preview.Image = image;
            old?.Dispose();
        });
    }

    /// <summary>Mark the job finished and close shortly after.</summary>
    public void Finish(bool success)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _done = true;
            _bar.Value = success ? 100 : _bar.Value;
            Text = success ? "Done" : "Stopped";
            Close();
        });
    }
}
