// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Dumping;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// <see cref="IExtractionReader"/> over a live drive: each call is one fresh
/// READ CD (0xBE), raw 2352 bytes, with C2 pointers and/or formatted Q appended
/// when asked for. The field selection self-negotiates the way MMC demands:
/// data sectors want Raw (0xF8), CD-DA only accepts UserData (0x10) — the reader
/// tries the hinted form first, falls over to the other on rejection, and
/// remembers what the drive accepted. It reports exactly what the drive said;
/// retries and recovery policy belong to the engine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriveExtractionReader : IExtractionReader, IDisposable
{
    private const int C2Bytes = 294;

    private readonly SptiDevice _dev;
    private bool _preferUserData;   // CD-DA form first?

    public DiscToc Toc { get; }
    public long TotalSectors { get; }

    /// <param name="audioHint">True when the span being extracted is expected to be
    /// audio, so the CD-DA field selection is tried first.</param>
    public DriveExtractionReader(char driveLetter, bool audioHint = false)
    {
        Toc = DiscReader.ReadToc(driveLetter);
        TotalSectors = Toc.LeadOutLba;
        _preferUserData = audioHint;
        _dev = new SptiDevice(driveLetter);
    }

    public void Dispose() => _dev.Dispose();

    /// <summary>Point the sector-type negotiation at a new span (e.g. the next track).</summary>
    public void SetAudioHint(bool audio) => _preferUserData = audio;

    public SectorReadAttempt Read(long lba, bool wantC2, bool wantSubcode)
    {
        if (lba < 0 || lba > uint.MaxValue)
            return new SectorReadAttempt { Ok = false, Main = [], Error = $"LBA {lba} is not addressable." };

        string? firstError = null;
        foreach (bool userData in _preferUserData ? new[] { true, false } : new[] { false, true })
        {
            var (ok, attempt, error) = TryRead((uint)lba, userData, wantC2, wantSubcode);
            if (ok)
            {
                _preferUserData = userData;   // remember what the drive accepted
                return attempt!;
            }
            firstError ??= error;
        }
        return new SectorReadAttempt { Ok = false, Main = [], Error = firstError };
    }

    private (bool ok, SectorReadAttempt? attempt, string? error) TryRead(
        uint lba, bool userData, bool wantC2, bool wantSubcode)
    {
        int size = SectorExtraction.RawSectorSize
                 + (wantC2 ? C2Bytes : 0)
                 + (wantSubcode ? SectorExtraction.QBytesPerSector : 0);
        var buf = new byte[size];

        // Base field selection, plus the C2 request bit (CDB byte 9 bit 1).
        var fields = userData ? MmcCommands.SectorFields.UserData : MmcCommands.SectorFields.Raw;
        if (wantC2) fields |= (MmcCommands.SectorFields)0x02;

        var cdb = MmcCommands.ReadCd(lba, 1,
            MmcCommands.ExpectedSectorType.Any, fields,
            wantSubcode ? MmcCommands.SubChannel.FormattedQ : MmcCommands.SubChannel.None);

        var r = _dev.SendCommand(cdb, buf, SptiDataDirection.In);
        if (!r.Success)
            return (false, null, $"sense {r.SenseKey:X}/{r.Asc:X2}/{r.Ascq:X2}");

        int off = SectorExtraction.RawSectorSize;
        byte[]? c2 = null, q16 = null;
        if (wantC2) { c2 = buf[off..(off + C2Bytes)]; off += C2Bytes; }
        if (wantSubcode) q16 = buf[off..(off + SectorExtraction.QBytesPerSector)];

        return (true, new SectorReadAttempt
        {
            Ok = true,
            Main = buf[..SectorExtraction.RawSectorSize],
            C2 = c2,
            Q16 = q16,
        }, null);
    }
}
