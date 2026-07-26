// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App.Views;

/// <summary>
/// A DVD-Shrink-style progress window for a disc operation — a read (rip) or a
/// copy. It mirrors <see cref="ShrinkProgressDialog"/>'s look, but a disc read
/// has no video frame to preview, so in its place it shows the readout that
/// matters here: what stage the job is in, the transfer rate, how far through
/// the sectors it is, and the estimated time remaining — with a Cancel button.
///
/// It is display only: the caller owns the operation and pushes updates in (from
/// any thread, marshalled here), and raises <see cref="CancelRequested"/> when
/// the user presses Cancel. The caller decides what Cancel means — a read can be
/// stopped safely; once a burn is underway the caller disables cancelling via
/// <see cref="SetCancellable"/>, because stopping a burn only makes a coaster.
/// </summary>
internal sealed class RipProgressDialog : Form
{
    private readonly Label _statusVal = new() { AutoSize = true, Location = new Point(140, 20), Font = Theme.Ui };
    private readonly Label _rateVal = new() { AutoSize = true, Location = new Point(140, 50), Font = Theme.Ui };
    private readonly Label _sectorsVal = new() { AutoSize = true, Location = new Point(140, 80), Font = Theme.Ui };
    private readonly Label _timeVal = new() { AutoSize = true, Location = new Point(140, 110), Font = Theme.Ui };
    private readonly ProgressBar _bar = new()
    {
        Location = new Point(16, 146), Size = new Size(388, 18), Minimum = 0, Maximum = 100,
    };
    private readonly Button _cancel = new()
    {
        Text = "Cancel", Location = new Point(324, 172), Width = 80, Height = 26, FlatStyle = FlatStyle.System,
    };

    private bool _done;

    /// <summary>Raised (on the UI thread) when the user presses Cancel.</summary>
    public event Action? CancelRequested;

    public RipProgressDialog(string caption)
    {
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 210);
        BackColor = RetroTheme.Face;
        Font = Theme.Ui;
        ShowInTaskbar = false;

        AddRow("Status:", 20, _statusVal);
        AddRow("Rate:", 50, _rateVal);
        AddRow("Sectors:", 80, _sectorsVal);
        AddRow("Time remaining:", 110, _timeVal);
        Controls.Add(_bar);
        Controls.Add(_cancel);

        _statusVal.Text = "starting…";
        _rateVal.Text = "—";
        _sectorsVal.Text = "—";
        _timeVal.Text = "—";

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
        Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(16, y), Font = Theme.Ui });
        Controls.Add(value);
    }

    private string _titleBase = "Working";

    // Marshal a UI update onto the dialog's thread — but only once its window handle
    // exists. Callers may push updates before the dialog is shown (or after it has
    // closed); those are dropped rather than throwing, since they are cosmetic and
    // the next update repaints the current state anyway. This is what stops the
    // "Invoke cannot be called until the window handle has been created" crash.
    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Update the percent, and reflect it in the title like the original.</summary>
    public void SetPercent(int percent) => Ui(() =>
    {
        int p = Math.Clamp(percent, 0, 100);
        _bar.Value = p;
        if (!_done) Text = $"{p}% — {_titleBase}";
    });

    /// <summary>Set the base title shown alongside the percent (e.g. "Reading disc").</summary>
    public void SetTitleBase(string title)
    {
        _titleBase = title;   // kept even if the handle isn't up yet, so the next repaint uses it
        Ui(() => { if (!_done) Text = $"{_bar.Value}% — {_titleBase}"; });
    }

    public void SetStats(string status, string rate, string sectors, string timeRemaining) => Ui(() =>
    {
        _statusVal.Text = status;
        _rateVal.Text = rate;
        _sectorsVal.Text = sectors;
        _timeVal.Text = timeRemaining;
    });

    /// <summary>Enable or disable the Cancel button (a burn in progress can't be
    /// stopped safely, so the caller disables it once burning starts).</summary>
    public void SetCancellable(bool can, string? disabledText = null) => Ui(() =>
    {
        _cancel.Enabled = can;
        _cancel.Text = can ? "Cancel" : (disabledText ?? "Cancel");
    });

    /// <summary>Mark the job finished and close shortly after.</summary>
    public void Finish(bool success) => Ui(() =>
    {
        _done = true;
        _bar.Value = success ? 100 : _bar.Value;
        Text = success ? "Done" : "Stopped";
        Close();
    });
}
