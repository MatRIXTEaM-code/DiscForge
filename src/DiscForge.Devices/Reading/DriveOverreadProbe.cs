// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// Empirical lead-out OVERREAD probe: can the drive read the first sector PAST the program area
/// (the start of the lead-out)? A drive that returns data there can overread — the property a
/// Redump-grade audio dump needs to fill the guard band a read-offset slide exposes; a drive that
/// refuses with "address out of range" cannot. This is a single non-destructive READ CD at the
/// TOC's lead-out LBA — it writes nothing and needs no special disc, only one loaded so a lead-out
/// boundary exists. It reports what it OBSERVED (returned / refused), not a guess: with no disc
/// there is nothing to probe and it says so.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriveOverreadProbe
{
    private const int RawSectorBytes = 2448;   // 2352 main + 96 sub, matching RawDiscReader

    public sealed record Result(bool DiscPresent, bool Overread, uint LeadOutLba, string Detail);

    /// <summary>Attempt to read the first lead-out sector on the disc currently loaded in
    /// <paramref name="dev"/>.</summary>
    public static Result Probe(SptiDevice dev)
    {
        ArgumentNullException.ThrowIfNull(dev);

        var toc = DiscReader.ReadToc(dev);
        if (toc.Tracks.Count == 0)
            return new Result(false, false, 0, "no readable disc / TOC — load a disc to probe overread");

        uint leadOut = toc.LeadOutLba;
        var buf = new byte[RawSectorBytes];

        // Data discs read raw; a pure-audio disc rejects Raw (illegal field combination), so fall
        // back to UserData — exactly as RawDiscReader auto-probes the field mode.
        var fields = toc.HasData ? MmcCommands.SectorFields.Raw : MmcCommands.SectorFields.UserData;
        var r = dev.SendCommand(
            MmcCommands.ReadCd(leadOut, 1, MmcCommands.ExpectedSectorType.Any, fields, MmcCommands.SubChannel.RawPw),
            buf, SptiDataDirection.In, 20);

        if (!r.Success && fields == MmcCommands.SectorFields.Raw)
            r = dev.SendCommand(
                MmcCommands.ReadCd(leadOut, 1, MmcCommands.ExpectedSectorType.Any,
                                   MmcCommands.SectorFields.UserData, MmcCommands.SubChannel.RawPw),
                buf, SptiDataDirection.In, 20);

        return r.Success
            ? new Result(true, true, leadOut, $"lead-out sector at LBA {leadOut} returned data — drive overreads")
            : new Result(true, false, leadOut, $"lead-out sector at LBA {leadOut} refused ({r.Describe()}) — no overread");
    }
}
