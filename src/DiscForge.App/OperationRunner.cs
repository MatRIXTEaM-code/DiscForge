// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Diagnostics;
using System.Windows.Forms;

namespace DiscForge.App;

/// <summary>
/// Runs a long operation with a cancel button and a live elapsed clock.
///
/// Every one of these operations can take twenty minutes on a damaged disc, and
/// until now the only way to stop one was to close the window — which leaves the
/// drive spinning and a half-written file behind. The cancellation token was
/// already plumbed through the readers; nothing was passing one.
///
/// The elapsed clock matters for the same reason. A progress bar that hasn't
/// moved for two minutes could mean a stalled drive or a sector being retried
/// eight times; a clock ticking beside it at least tells you the application
/// hasn't hung.
/// </summary>
internal sealed class OperationRunner : IDisposable
{
    private readonly Button _startButton;
    private readonly Button _cancelButton;
    private readonly Label _elapsed;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 500 };
    private readonly Stopwatch _watch = new();
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Wires a start button, a cancel button and an elapsed label together.
    /// The cancel button starts hidden and appears only while work is running.
    /// </summary>
    public OperationRunner(Button startButton, Button cancelButton, Label elapsed)
    {
        _startButton = startButton;
        _cancelButton = cancelButton;
        _elapsed = elapsed;

        _cancelButton.Visible = false;
        _cancelButton.Click += (_, _) => Cancel();
        _clock.Tick += (_, _) => _elapsed.Text = Format(_watch.Elapsed);
    }

    public bool IsRunning => _cts is not null;

    /// <summary>
    /// Run <paramref name="work"/> on a background thread with the buttons and
    /// clock managed. Returns the result, or default if cancelled.
    /// </summary>
    public async Task<T?> RunAsync<T>(Func<CancellationToken, T> work,
                                      Action<Exception>? onError = null)
    {
        if (IsRunning) return default;

        _cts = new CancellationTokenSource();
        _startButton.Enabled = false;
        _cancelButton.Visible = true;
        _cancelButton.Enabled = true;
        _cancelButton.Text = "Cancel";
        _watch.Restart();
        _clock.Start();
        _elapsed.Text = "0:00";

        try
        {
            var token = _cts.Token;
            return await Task.Run(() => work(token), token);
        }
        catch (OperationCanceledException)
        {
            // Not an error: the user asked for this. Callers distinguish it from
            // a failure by the default return value.
            return default;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return default;
        }
        finally
        {
            _clock.Stop();
            _watch.Stop();
            _elapsed.Text = Format(_watch.Elapsed);
            _cancelButton.Visible = false;
            _startButton.Enabled = true;
            _cts.Dispose();
            _cts = null;
        }
    }

    public void Cancel()
    {
        if (_cts is null) return;
        _cts.Cancel();

        // Cancellation is cooperative: the reader checks the token between
        // sectors, so a request during a thirty-second timeout won't take effect
        // until that read returns. Saying so beats a button that looks broken.
        _cancelButton.Enabled = false;
        _cancelButton.Text = "Stopping…";
    }

    public TimeSpan Elapsed => _watch.Elapsed;

    /// <summary>Was the last run stopped by the user rather than finishing?</summary>
    public bool WasCancelled { get; private set; }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    public void Dispose()
    {
        _clock.Dispose();
        _cts?.Dispose();
    }
}