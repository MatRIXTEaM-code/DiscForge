// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices;

/// <summary>
/// Opens and closes a drive tray.
///
/// Small, but its absence is felt: working through a stack of discs means
/// reaching for the drive's own button between every one, and on a slot-loading
/// or awkwardly-sited drive that is genuinely annoying. START STOP UNIT (0x1B)
/// does it in one command.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriveTray
{
    /// <summary>Open the tray. Fails harmlessly on slot-loading drives that
    /// have nothing to open.</summary>
    public static void Eject(char driveLetter)
    {
        using var dev = new SptiDevice(driveLetter);

        // START STOP UNIT: byte 4 bit 1 selects LoEj (load/eject), bit 0
        // selects Start. LoEj set with Start clear means eject.
        var cdb = new byte[6];
        cdb[0] = 0x1B;
        cdb[4] = 0x02;

        var r = dev.SendCommand(cdb, Array.Empty<byte>(), SptiDataDirection.None,
                                timeoutSeconds: 20);
        if (!r.Success)
            throw new IOException($"The drive refused to eject: {r.Describe()}");
    }

    /// <summary>Close the tray. Many drives ignore this — a tray closed by
    /// software is a feature the mechanism has to support.</summary>
    public static void Load(char driveLetter)
    {
        using var dev = new SptiDevice(driveLetter);

        var cdb = new byte[6];
        cdb[0] = 0x1B;
        cdb[4] = 0x03;              // LoEj + Start: load

        var r = dev.SendCommand(cdb, Array.Empty<byte>(), SptiDataDirection.None,
                                timeoutSeconds: 20);
        if (!r.Success)
            throw new IOException($"The drive refused to close its tray: {r.Describe()}");
    }
}