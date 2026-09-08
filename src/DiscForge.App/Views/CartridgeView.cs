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
/// The escape hatch for cartridge dumping. DiscForge reads N64, SNES, Genesis, GB/GBC, GBA and
/// NES ROM dumps once they exist, but has no code that talks to cartridge-reading hardware at
/// all — every one of those consoles is read through a flashcart's own USB device, not a drive
/// DiscForge issues commands to. Unlike optical discs, there is no single dominant tool here:
/// dumping splits across more than one piece of hardware, so this screen offers the two most
/// widely used community options rather than picking one brand over another —
/// <c>GBxCart RW</c>/<c>FlashGBX</c> for the Game Boy family, and <c>Cart Reader</c> (sanni's
/// open-source Arduino-based dumper) for N64/SNES/Genesis/NES, the families GBxCart RW doesn't
/// cover. DiscForge never bundles, inspects, or knows anything else about either tool — the same
/// launch-what-you-point-it-at contract as every external-tool button in the app.
/// </summary>
internal sealed class CartridgeView : UserControl
{
    private readonly Label _info = new()
    {
        AutoSize = false, Location = new Point(12, 12), Size = new Size(712, 60),
        Font = Theme.Ui, ForeColor = Color.Black,
        Text = "DiscForge reads N64, SNES, Genesis, GB/GBC, GBA and NES ROM dumps once you have " +
               "them, but has no cartridge-reading hardware of its own — that always means a " +
               "flashcart's own software. There's no single dominant tool the way there is for " +
               "optical discs, so both major community options are offered below.",
    };
    // GBxCart RW / FlashGBX — the community's de facto standard for the Game Boy family
    // (GB/GBC/GBA). Own remembered path so configuring it doesn't disturb Cart Reader's.
    private readonly Button _gbxCart = new()
    {
        Text = "GBxCart RW / FlashGBX…", Location = new Point(12, 80), Width = 220, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // Cart Reader (sanni's open-source Arduino cart dumper) — covers N64, SNES, Genesis and NES,
    // the families GBxCart RW doesn't. A companion tool, not a replacement, so it gets its own
    // button and remembered path.
    private readonly Button _cartReader = new()
    {
        Text = "Cart Reader (N64/SNES/Genesis/NES)…", Location = new Point(240, 80), Width = 280, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Location = new Point(12, 118), Size = new Size(712, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    public CartridgeView()
    {
        Size = new Size(736, 160);
        BackColor = Color.White;
        Padding = new Padding(12);

        _gbxCart.Click += (_, _) => LaunchExternalGbxCart();
        _cartReader.Click += (_, _) => LaunchExternalCartReader();

        Controls.Add(_info); Controls.Add(_gbxCart); Controls.Add(_cartReader); Controls.Add(_status);

        _status.Text = "Dump the cartridge in its own window; bring the resulting ROM into DiscForge afterward.";
    }

    /// <summary>
    /// Launch a user-supplied GBxCart RW / FlashGBX (asked for once, then remembered) — the
    /// escape hatch for the Game Boy family, since DiscForge has no cartridge-reading hardware of
    /// its own. Delegates to <see cref="ExternalToolLauncher"/>, the same shared logic every
    /// other external-tool button in the app uses; this view has no event log, so it reports
    /// through <see cref="_status"/> instead.
    /// </summary>
    private void LaunchExternalGbxCart() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathGbxCart,
        p => Settings.ExternalDumperPathGbxCart = p,
        "Locate GBxCart RW / FlashGBX",
        "Dump the GB/GBC/GBA cartridge there; bring the resulting ROM into DiscForge afterward.",
        ReportToStatus);

    /// <summary>Same idea as <see cref="LaunchExternalGbxCart"/>, for Cart Reader — a companion
    /// tool covering N64/SNES/Genesis/NES, not a replacement. Own remembered path.</summary>
    private void LaunchExternalCartReader() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathCartReader,
        p => Settings.ExternalDumperPathCartReader = p,
        "Locate Cart Reader's host software",
        "Dump the cartridge there; bring the resulting ROM into DiscForge afterward.",
        ReportToStatus);

    /// <summary>Shared report sink for this view's external-tool buttons: this screen has no
    /// event log the way Read/Burn do, so both buttons report through the status label.</summary>
    private void ReportToStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.ForeColor = isError ? Color.FromArgb(0xA0, 0x20, 0x20) : Color.FromArgb(0x20, 0x70, 0x20);
        if (!isError) StatusBus.Report(message);
    }
}
