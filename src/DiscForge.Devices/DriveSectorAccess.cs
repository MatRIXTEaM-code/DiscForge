// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Cue;
using DiscForge.Core.Files;
using DiscForge.Core.Mmc;
using DiscForge.Core.Raw;
using DiscForge.Devices.Reading;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices;

/// <summary>
/// The sector viewer pointed at a LIVE DISC: same <see cref="ISectorSource"/>
/// contract as image files, but each read is a fresh SCSI command, so what's
/// shown is what the drive returns right now — including sub-channel Q,
/// which images can only have if they were ripped with it.
///
/// Read strategy, negotiated once and remembered:
///   1. READ CD raw 2352 + formatted Q (2368/sector) — CDs, the full picture;
///   2. READ CD raw 2352 without subcode — drives that refuse sub-channel;
///   3. READ(10) cooked 2048 — DVDs and BDs, which have no raw CD form.
/// A sector that fails all three throws with the sense information, because
/// a viewer that silently shows zeros would be worse than one that says
/// "the drive couldn't read this".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriveSectorAccess : ISectorSource
{
    private enum Strategy { Undecided, RawWithQ, Raw, Cooked }

    private readonly SptiDevice _dev;
    private readonly char _letter;
    private Strategy _strategy = Strategy.Undecided;

    public string Description { get; }
    public long TotalSectors { get; }

    public DriveSectorAccess(char driveLetter, string driveDisplayName)
    {
        _letter = driveLetter;
        var toc = DiscReader.ReadToc(driveLetter);
        TotalSectors = toc.LeadOutLba;
        Description = $"{driveDisplayName} ({driveLetter}:), {TotalSectors:N0} sectors on disc";
        _dev = new SptiDevice(driveLetter);
    }

    public void Dispose() => _dev.Dispose();

    public long Resolve(string address)
    {
        address = address.Trim();
        if (address.StartsWith('+')) return long.Parse(address[1..]);
        if (address.Contains(':')) return Msf.Parse(address).ToSectors() - 150;
        return long.Parse(address);
    }

    public SectorAccess.SectorData Read(long fileIndex)
    {
        if (fileIndex < 0 || fileIndex >= TotalSectors)
            throw new ArgumentOutOfRangeException(nameof(fileIndex),
                $"LBA {fileIndex:N0} is outside the disc (0..{TotalSectors - 1:N0}).");

        uint lba = (uint)fileIndex;

        // Try in preference order, remembering what the drive accepts. A
        // strategy that worked once is retried from scratch if it later
        // fails, in case a mixed disc changes the answer per region.
        foreach (var attempt in Order())
        {
            var (ok, main, sub) = Try(attempt, lba, out var sense);
            if (ok)
            {
                _strategy = attempt;
                return new SectorAccess.SectorData
                {
                    FileIndex = fileIndex,
                    Lba = fileIndex,
                    Msf = Msf.FromSectors(fileIndex + 150),
                    Stored = main,
                    Subcode = sub,
                    SubcodeForm = sub is null ? null : RawSubcodeForm.Pq16,
                };
            }
            if (attempt == Strategy.Cooked)
                throw new IOException(
                    $"The drive could not read LBA {lba:N0} in any form " +
                    $"(sense {sense}). The sector may be unreadable, or past " +
                    "the readable area.");
        }
        throw new InvalidOperationException("unreachable");
    }

    private IEnumerable<Strategy> Order()
    {
        // Start from the remembered strategy, then everything below it.
        if (_strategy is Strategy.Undecided or Strategy.RawWithQ) yield return Strategy.RawWithQ;
        if (_strategy is Strategy.Undecided or Strategy.RawWithQ or Strategy.Raw) yield return Strategy.Raw;
        yield return Strategy.Cooked;
    }

    private (bool ok, byte[] main, byte[]? sub) Try(Strategy s, uint lba, out string sense)
    {
        switch (s)
        {
            case Strategy.RawWithQ:
            {
                var buf = new byte[2368];
                var cdb = MmcCommands.ReadCd(lba, 1,
                    MmcCommands.ExpectedSectorType.Any,
                    MmcCommands.SectorFields.Raw,
                    MmcCommands.SubChannel.FormattedQ);
                var r = _dev.SendCommand(cdb, buf, SptiDataDirection.In);
                sense = Sense(r);
                return r.Success
                    ? (true, buf[..2352], buf[2352..])
                    : (false, Array.Empty<byte>(), null);
            }
            case Strategy.Raw:
            {
                var buf = new byte[2352];
                var cdb = MmcCommands.ReadCd(lba, 1,
                    MmcCommands.ExpectedSectorType.Any,
                    MmcCommands.SectorFields.Raw);
                var r = _dev.SendCommand(cdb, buf, SptiDataDirection.In);
                sense = Sense(r);
                return r.Success ? (true, buf, null) : (false, Array.Empty<byte>(), null);
            }
            default:
            {
                var buf = new byte[2048];
                var cdb = MmcCommands.Read10(lba, 1);
                var r = _dev.SendCommand(cdb, buf, SptiDataDirection.In);
                sense = Sense(r);
                return r.Success ? (true, buf, null) : (false, Array.Empty<byte>(), null);
            }
        }
    }

    private static string Sense(SptiResult r)
        => $"{r.SenseKey:X}/{r.Asc:X2}/{r.Ascq:X2}";
}
