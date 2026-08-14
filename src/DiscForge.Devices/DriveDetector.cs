// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices;

/// <summary>
/// Finds optical drives and detects what each can do. The transport and OS calls
/// live here (Windows-only); all the actual reasoning is delegated to the pure,
/// tested parsers/mapper in DiscForge.Core.Mmc / .Devices.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriveDetector
{
    /// <summary>Enumerate optical drive letters (DRIVE_CDROM == 5).</summary>
    public static IReadOnlyList<char> EnumerateOpticalDrives()
    {
        var result = new List<char>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            // DriveInfo.DriveType.CDRom covers CD/DVD/BD optical units.
            if (drive.DriveType == DriveType.CDRom && drive.Name.Length >= 1)
                result.Add(char.ToUpperInvariant(drive.Name[0]));
        }
        return result;
    }

    /// <summary>
    /// Interrogate one drive: INQUIRY + GET CONFIGURATION + mode page 2A, then
    /// compose <see cref="DriveCapabilities"/> via the pure mapper. Mode page 2A
    /// is best-effort — some drives omit it; capabilities degrade gracefully.
    /// </summary>
    public static DriveCapabilities Detect(char driveLetter)
    {
        using var dev = new SptiDevice(driveLetter);

        var inquiry = InquiryData.Parse(Run(dev, MmcCommands.Inquiry(), 36));
        var config = ConfigurationInfo.Parse(Run(dev, MmcCommands.GetConfiguration(), 512));

        MmCapabilities? page2a = null;
        try
        {
            var raw = Run(dev, MmcCommands.ModeSense10(0x2A), 512);
            page2a = MmCapabilities.ParseFromModeSense10(raw);
        }
        catch
        {
            // Drive without a usable 2A page: proceed with profile data only.
        }

        // Is there a disc, and is it blank? GET CONFIGURATION only reports the
        // media TYPE — this is what distinguishes a blank DVD+R DL from one
        // that's already full.
        DiscInformation? disc = null;
        try
        {
            disc = DiscInformation.Parse(Run(dev, MmcCommands.ReadDiscInformation(), 34));
        }
        catch
        {
            // No disc, or a drive that won't say: leave it unknown rather than
            // guessing. The burn engine checks again before writing.
        }

        return DriveCapabilities.Build(dev.DevicePath, inquiry, config, page2a, disc);
    }

    /// <summary>Detect every optical drive; skips ones that error (busy/no media
    /// for some commands is tolerated where possible).</summary>
    public static IReadOnlyList<DriveCapabilities> DetectAll()
    {
        var caps = new List<DriveCapabilities>();
        foreach (var letter in EnumerateOpticalDrives())
        {
            try { caps.Add(Detect(letter)); }
            catch (IOException) { /* drive not accessible right now */ }
        }
        return caps;
    }

    private static byte[] Run(SptiDevice dev, byte[] cdb, int allocLen)
    {
        var buf = new byte[allocLen];
        var r = dev.SendCommand(cdb, buf, SptiDataDirection.In);
        if (!r.Success)
            throw new IOException(
                $"SCSI command 0x{cdb[0]:X2} failed (status {r.ScsiStatus}).");
        return buf;
    }
}
