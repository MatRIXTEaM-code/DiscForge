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
/// The escape hatch for flux-level floppy capture. DiscForge already images an ordinary floppy
/// from a standard drive itself (the CLI's <c>floppy-image</c>, reading flat sectors), and it
/// already reads and inspects a KryoFlux raw stream or a SuperCard Pro flux file once one exists
/// (<c>kryoflux-info</c>, <c>scp-info</c>, and the flux container in <c>DiscForge.Core</c>). What
/// it has never done, and has no reason to reimplement, is talk to a KryoFlux or Greaseweazle
/// board over USB to pull that flux off a real disk in the first place — that is exactly what
/// each board's own vendor software is for. This view exists purely to launch that vendor
/// software, the same way every other external-tool button in DiscForge does; it does not attempt
/// to give either tool a DiscForge-native front end.
/// </summary>
internal sealed class FloppyView : UserControl
{
    private readonly Label _info = new()
    {
        AutoSize = false, Location = new Point(12, 12), Size = new Size(712, 60),
        Font = Theme.Ui, ForeColor = Color.Black,
        Text = "DiscForge images an ordinary floppy from a standard drive itself (see the CLI's " +
               "floppy-image command), and it can already read a KryoFlux or SuperCard Pro flux " +
               "file once you have one. Capturing that flux from real KryoFlux or Greaseweazle " +
               "hardware over USB is the vendor's own tool's job — launch it here.",
    };
    // KryoFlux's own capture program (DTC). DiscForge never bundles, inspects, or knows anything
    // else about it — same launch-what-you-point-it-at contract as every external-tool button in
    // the app.
    private readonly Button _kryoFlux = new()
    {
        Text = "KryoFlux DTC…", Location = new Point(12, 80), Width = 150, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // Same idea, for Greaseweazle's host software (the "gw" client). Own remembered path so
    // configuring one doesn't disturb the other.
    private readonly Button _greaseweazle = new()
    {
        Text = "Greaseweazle (gw)…", Location = new Point(174, 80), Width = 160, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Location = new Point(12, 118), Size = new Size(712, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    public FloppyView()
    {
        Size = new Size(736, 160);
        BackColor = Color.White;
        Padding = new Padding(12);

        _kryoFlux.Click += (_, _) => LaunchExternalKryoFlux();
        _greaseweazle.Click += (_, _) => LaunchExternalGreaseweazle();

        Controls.Add(_info); Controls.Add(_kryoFlux); Controls.Add(_greaseweazle); Controls.Add(_status);

        _status.Text = "Once you've captured a .raw (KryoFlux) or .scp (SuperCard Pro) file, open it " +
                       "with kryoflux-info / scp-info, or bring it into Examine.";
    }

    /// <summary>
    /// Launch a user-supplied KryoFlux DTC (asked for once, then remembered) — DiscForge has no
    /// USB device driver for KryoFlux's capture hardware and has no reason to grow one; DTC is
    /// the vendor's own tool for that. Delegates to <see cref="ExternalToolLauncher"/>, the same
    /// shared logic every other external-tool button in the app uses; this view has no event
    /// log, so it reports through <see cref="_status"/> instead.
    /// </summary>
    private void LaunchExternalKryoFlux() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathKryoFlux,
        p => Settings.ExternalDumperPathKryoFlux = p,
        "Locate KryoFlux's DTC",
        "Capture the flux there; bring the resulting .raw stream into DiscForge afterward.",
        ReportToStatus);

    /// <summary>Same idea as <see cref="LaunchExternalKryoFlux"/>, for Greaseweazle's host
    /// software. Own remembered path so configuring it doesn't disturb KryoFlux's.</summary>
    private void LaunchExternalGreaseweazle() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathGreaseweazle,
        p => Settings.ExternalDumperPathGreaseweazle = p,
        "Locate Greaseweazle's host software (gw)",
        "Capture the flux there; bring the resulting .scp file into DiscForge afterward.",
        ReportToStatus);

    /// <summary>Shared report sink for this view's external-tool buttons: this screen has no
    /// event log the way Read/Burn do, so both buttons report through the same status label.</summary>
    private void ReportToStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.ForeColor = isError ? Color.FromArgb(0xA0, 0x20, 0x20) : Color.FromArgb(0x20, 0x70, 0x20);
        if (!isError) StatusBus.Report(message);
    }
}
