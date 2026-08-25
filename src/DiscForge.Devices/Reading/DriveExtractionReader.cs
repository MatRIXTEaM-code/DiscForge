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
/// <see cref="IExtractionReader"/> over a live drive. On CD media, each call is
/// one fresh READ CD (0xBE), raw 2352 bytes, with C2 pointers and/or formatted Q
/// appended when asked for. The field selection self-negotiates the way MMC
/// demands: data sectors want Raw (0xF8), CD-DA only accepts UserData (0x10) —
/// the reader tries the hinted form first, falls over to the other on rejection,
/// and remembers what the drive accepted. It reports exactly what the drive said;
/// retries and recovery policy belong to the engine.
///
/// On DVD/BD media there is no raw/CD-sync sector form and no C2 or subchannel —
/// READ CD (0xBE) is a CD-only command and either fails outright or returns bytes
/// with no CD structure to check. <see cref="IsDvdOrBd"/> (detected once via GET
/// CONFIGURATION at construction) switches the reader to plain READ(10) reads of
/// the 2048-byte user data block instead.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriveExtractionReader : IExtractionReader, IDisposable
{
    private const int C2Bytes = 294;
    private const int DvdSectorSize = DiscForge.Core.Dumping.SectorExtraction.DvdSectorSize;

    private readonly SptiDevice _dev;
    private bool _preferUserData;   // CD-DA form first?

    public DiscToc Toc { get; }
    public long TotalSectors { get; }

    /// <summary>True when GET CONFIGURATION reported the loaded media as DVD or BD
    /// (anything that isn't CD-ROM/CD-R/CD-RW). Read() switches to READ(10) 2048-byte
    /// user-data reads in that case; false (including "the drive didn't say") keeps
    /// the CD READ CD path, which was always this reader's default.</summary>
    public bool IsDvdOrBd { get; }

    /// <param name="audioHint">True when the span being extracted is expected to be
    /// audio, so the CD-DA field selection is tried first.</param>
    public DriveExtractionReader(char driveLetter, bool audioHint = false)
    {
        Toc = DiscReader.ReadToc(driveLetter);
        TotalSectors = Toc.LeadOutLba;
        _preferUserData = audioHint;
        _dev = new SptiDevice(driveLetter);

        try
        {
            var cfgBuf = new byte[512];
            var r = _dev.SendCommand(MmcCommands.GetConfiguration(), cfgBuf, SptiDataDirection.In);
            if (r.Success)
            {
                var profile = ConfigurationInfo.Parse(cfgBuf).CurrentProfile;
                IsDvdOrBd = profile is not MmcProfile.None
                            and not MmcProfile.CdRom and not MmcProfile.CdR and not MmcProfile.CdRw;
            }
        }
        catch
        {
            // A drive that won't answer GET CONFIGURATION: fall back to the CD path,
            // which is what every prior release assumed unconditionally.
        }
    }

    public void Dispose() => _dev.Dispose();

    /// <summary>Point the sector-type negotiation at a new span (e.g. the next track).
    /// Discards any prefetched sectors — they were read in the old form.</summary>
    public void SetAudioHint(bool audio)
    {
        _preferUserData = audio;
        _prefetched.Clear();
        _lastServed = -2;
    }

    // ---- batched read-ahead --------------------------------------------------
    //
    // One sector per SCSI command is clean but slow: the per-command overhead
    // dominates a sequential dump. The reader therefore prefetches a run of
    // sectors in ONE command and serves them from a cache — with two rules that
    // keep the engine's semantics EXACTLY as before:
    //
    //   1. Each cached sector is served at most once. The engine only re-asks
    //      for an LBA when it is retrying (a failed check, a jitter consensus
    //      pass, a Q re-read) — and a retry must hit the disc again, never the
    //      cache. Single-use entries make that automatic.
    //   2. A batch that FAILS proves nothing about any single sector (one bad
    //      sector fails the whole transfer). Failed batches are discarded and
    //      the sector is read individually, so error attribution stays per-sector.
    private readonly Dictionary<long, SectorReadAttempt> _prefetched = new();
    private (bool c2, bool sub, bool userData) _prefetchShape;
    private long _lastServed = -2;   // prefetch only fires on sequential forward progress

    /// <summary>Max sectors per prefetch command, sized to stay comfortably under
    /// the conventional 64 KiB SPTI transfer ceiling at the largest per-sector size
    /// (2352 + 294 C2 + 16 Q = 2662 bytes → 24 sectors ≈ 63.9 KiB).</summary>
    public const int MaxBatchSectors = 24;

    /// <summary>Shape marker reserved for DVD prefetch — c2/sub/userData are all
    /// meaningless for DVD reads, so this tuple can never collide with a real CD
    /// shape request (a CD span never asks for c2 AND sub AND non-userData together
    /// while also being on a reader with IsDvdOrBd true — the two paths never mix
    /// within one reader's lifetime, since media doesn't change mid-extraction).</summary>
    private static readonly (bool c2, bool sub, bool userData) DvdShape = (false, false, false);

    public SectorReadAttempt Read(long lba, bool wantC2, bool wantSubcode)
    {
        if (lba < 0 || lba > uint.MaxValue)
            return new SectorReadAttempt { Ok = false, Main = [], Error = $"LBA {lba} is not addressable." };

        if (IsDvdOrBd)
        {
            if (_prefetchShape == DvdShape && _prefetched.Remove(lba, out var dvdCached))
            {
                _lastServed = lba;
                return dvdCached;
            }
            bool dvdSequential = lba == _lastServed + 1;
            if (dvdSequential && TryPrefetchDvd((uint)lba) && _prefetched.Remove(lba, out var fresh))
            {
                _lastServed = lba;
                return fresh;
            }
            var (dvdOk, dvdAttempt, dvdError) = TryReadDvd((uint)lba, 1, out _);
            if (dvdOk) { _lastServed = lba; return dvdAttempt!; }
            return new SectorReadAttempt { Ok = false, Main = [], Error = dvdError };
        }

        // A single-use cache hit — only valid if it was fetched with the exact
        // shape (C2/subcode/field form) the engine wants now.
        if (_prefetchShape.c2 == wantC2 && _prefetchShape.sub == wantSubcode
            && _prefetchShape.userData == _preferUserData
            && _prefetched.Remove(lba, out var cached))
        {
            _lastServed = lba;
            return cached;
        }

        // Prefetch only on sequential forward progress: a retry (same LBA) or a
        // Q-only re-read must go to the disc individually, and a seek/jump gets
        // one probing read before batching resumes.
        bool sequential = lba == _lastServed + 1;

        string? firstError = null;
        foreach (bool userData in _preferUserData ? new[] { true, false } : new[] { false, true })
        {
            if (sequential && TryPrefetch((uint)lba, userData, wantC2, wantSubcode)
                && _prefetched.Remove(lba, out var fresh))
            {
                _preferUserData = userData;
                _lastServed = lba;
                return fresh;
            }
            var (ok, attempt, error) = TryRead((uint)lba, 1, userData, wantC2, wantSubcode, out _);
            if (ok)
            {
                _preferUserData = userData;   // remember what the drive accepted
                _lastServed = lba;
                return attempt!;
            }
            firstError ??= error;
        }
        return new SectorReadAttempt { Ok = false, Main = [], Error = firstError };
    }

    /// <summary>Read a run of sectors in one command into the single-use cache.
    /// Returns false (cache untouched) when the batch transfer fails — the caller
    /// then reads individually so blame lands on the right sector.</summary>
    private bool TryPrefetch(uint lba, bool userData, bool wantC2, bool wantSubcode)
    {
        long remaining = TotalSectors - lba;
        int count = (int)Math.Min(MaxBatchSectors, remaining);
        if (count < 2) return false;

        _prefetched.Clear();
        _prefetchShape = (wantC2, wantSubcode, userData);

        var (ok, _, _) = TryRead(lba, count, userData, wantC2, wantSubcode, out var batch);
        if (!ok || batch is null) return false;
        for (int i = 0; i < count; i++)
            _prefetched[lba + i] = batch[i];
        return true;
    }

    private (bool ok, SectorReadAttempt? attempt, string? error) TryRead(
        uint lba, int count, bool userData, bool wantC2, bool wantSubcode,
        out SectorReadAttempt[]? batch)
    {
        batch = null;
        int perSector = SectorExtraction.RawSectorSize
                      + (wantC2 ? C2Bytes : 0)
                      + (wantSubcode ? SectorExtraction.QBytesPerSector : 0);
        var buf = new byte[perSector * count];

        // Base field selection, plus the C2 request bit (CDB byte 9 bit 1).
        var fields = userData ? MmcCommands.SectorFields.UserData : MmcCommands.SectorFields.Raw;
        if (wantC2) fields |= (MmcCommands.SectorFields)0x02;

        var cdb = MmcCommands.ReadCd(lba, (uint)count,
            MmcCommands.ExpectedSectorType.Any, fields,
            wantSubcode ? MmcCommands.SubChannel.FormattedQ : MmcCommands.SubChannel.None);

        var r = _dev.SendCommand(cdb, buf, SptiDataDirection.In);
        if (!r.Success)
            return (false, null, $"sense {r.SenseKey:X}/{r.Asc:X2}/{r.Ascq:X2}");

        // Per MMC, each sector's transfer is main | C2 | sub-channel, in that order.
        batch = new SectorReadAttempt[count];
        for (int i = 0; i < count; i++)
        {
            int off = i * perSector;
            var main = buf[off..(off + SectorExtraction.RawSectorSize)];
            off += SectorExtraction.RawSectorSize;
            byte[]? c2 = null, q16 = null;
            if (wantC2) { c2 = buf[off..(off + C2Bytes)]; off += C2Bytes; }
            if (wantSubcode) q16 = buf[off..(off + SectorExtraction.QBytesPerSector)];
            batch[i] = new SectorReadAttempt { Ok = true, Main = main, C2 = c2, Q16 = q16 };
        }
        return (true, batch[0], null);
    }

    // ---- DVD/BD path ----------------------------------------------------------
    //
    // No raw sector form, no C2, no subchannel — READ(10) returns exactly the
    // 2048-byte user data block the drive's own ECC has already proven (or given
    // up on). Batched the same way as the CD path, for the same reason: one
    // command per sector would make a multi-gigabyte DVD dump take hours longer
    // than it needs to.

    private bool TryPrefetchDvd(uint lba)
    {
        long remaining = TotalSectors - lba;
        int count = (int)Math.Min(MaxBatchSectors, remaining);
        if (count < 2) return false;

        _prefetched.Clear();
        _prefetchShape = DvdShape;

        var (ok, _, _) = TryReadDvd(lba, count, out var batch);
        if (!ok || batch is null) return false;
        for (int i = 0; i < count; i++)
            _prefetched[lba + i] = batch[i];
        return true;
    }

    private (bool ok, SectorReadAttempt? attempt, string? error) TryReadDvd(
        uint lba, int count, out SectorReadAttempt[]? batch)
    {
        batch = null;
        var buf = new byte[DvdSectorSize * count];
        var cdb = MmcCommands.Read10(lba, (ushort)count);

        var r = _dev.SendCommand(cdb, buf, SptiDataDirection.In);
        if (!r.Success)
            return (false, null, $"sense {r.SenseKey:X}/{r.Asc:X2}/{r.Ascq:X2}");

        batch = new SectorReadAttempt[count];
        for (int i = 0; i < count; i++)
            batch[i] = new SectorReadAttempt { Ok = true, Main = buf[(i * DvdSectorSize)..((i + 1) * DvdSectorSize)] };
        return (true, batch[0], null);
    }
}
