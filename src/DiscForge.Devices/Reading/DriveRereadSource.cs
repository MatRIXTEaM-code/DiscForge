// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Core.Recovery;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// Tier B: wires the (already-proven, hardware-free) <see cref="AdaptiveReread"/> decision logic to
/// REAL drive reads of one stubborn sector, so the controller's escalation ladder — read again, try
/// harder, give up — runs against an actual disc instead of a scripted fixture.
///
/// Three strategies, each strictly more work than the last (so escalation always means "try harder",
/// never "try differently at random"):
///   0. Plain re-read at the drive's current speed, C2 pointers not requested.
///   1. Re-read with C2 pointers requested — the drive's own per-byte error flags now contribute to
///      the uncertain-byte count directly, instead of relying only on cross-read disagreement.
///   2. Re-read at a deliberately slow, careful speed (4x) WITH C2 requested — the classic "last
///      resort" a human ripper reaches for on a marginal sector: give the drive more time per bit.
///
/// Correctness signal per read:
///   - Data sectors (Mode 1 / Mode 2 Form 1): the sector's own EDC+ECC is authoritative — a valid
///     EDC means the sector is provably correct regardless of what any other read said.
///   - Audio (CD-DA) sectors carry no EDC at all, so "valid" can only come from byte-level agreement
///     across every read taken so far (<see cref="AdaptiveReread"/> already handles this: it accepts
///     the moment UncertainBytes reaches 0), reinforced by the drive's own C2 flags when available —
///     a byte a C2 pointer marks bad is never trusted just because two reads happened to agree.
///
/// This is intentionally scoped to ONE sector, exercised through a dedicated CLI diagnostic
/// (`reread-probe`) rather than rewired into the main dump pipeline — proving the real-hardware path
/// works, and letting it be validated against real marginal media, without touching the read paths
/// (<c>RawDiscReader</c>, <c>SectorExtraction</c>) that existing dumps already depend on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DriveRereadSource : IRereadSource
{
    private const int RawSectorBytes = 2352;
    private const int C2Bytes = 294;   // 1 bit per main-channel byte, per MMC

    private readonly SptiDevice _dev;
    private readonly uint _lba;
    private readonly bool _isAudio;
    private readonly List<byte[]> _mainReads = new();
    private byte[]? _lastC2;

    /// <summary>The raw main-channel bytes from the most recent read — for a caller that wants to
    /// inspect (or save) what was actually recovered once the controller accepts.</summary>
    public byte[]? LastMain => _mainReads.Count == 0 ? null : _mainReads[^1];

    public DriveRereadSource(SptiDevice dev, uint lba, bool isAudio)
    {
        _dev = dev ?? throw new ArgumentNullException(nameof(dev));
        _lba = lba;
        _isAudio = isAudio;
    }

    public ReadAttempt Read(int strategy)
    {
        bool wantC2 = strategy >= 1;
        if (strategy >= 2)
        {
            // Best-effort: a drive that rejects SET CD SPEED just keeps reading at whatever speed
            // it was already at — that's a strictly safe fallback, not a failure of this strategy.
            try { _dev.SendCommand(SetCdSpeed.BuildCdb(SetCdSpeed.KbsForMultiplier(4), SetCdSpeed.KbsForMultiplier(4)), Array.Empty<byte>(), SptiDataDirection.None, 20); }
            catch { /* ignore — speed is an optimization here, not a correctness requirement */ }
        }

        var fields = _isAudio ? MmcCommands.SectorFields.UserData : MmcCommands.SectorFields.Raw;
        if (wantC2) fields |= (MmcCommands.SectorFields)0x02;

        int perSector = RawSectorBytes + (wantC2 ? C2Bytes : 0);
        var buf = new byte[perSector];
        var cdb = MmcCommands.ReadCd(_lba, 1, MmcCommands.ExpectedSectorType.Any, fields,
            MmcCommands.SubChannel.None);
        var r = _dev.SendCommand(cdb, buf, SptiDataDirection.In, 20);

        if (!r.Success)
        {
            // A failed transfer contributes nothing new — but it must still count as a read for the
            // controller's read-cap/backstop bookkeeping, and it certainly proves nothing is "valid".
            int uncertainOnFailure = RereadEvidence.CountUncertain(_mainReads, _lastC2, RawSectorBytes);
            return new ReadAttempt(strategy, EdcValid: false, UncertainBytes: uncertainOnFailure);
        }

        byte[] main = buf[..RawSectorBytes];
        _lastC2 = wantC2 ? buf[RawSectorBytes..(RawSectorBytes + C2Bytes)] : null;
        _mainReads.Add(main);

        // Correctness signals live in DiscForge.Core.Recovery.RereadEvidence (pure, unit-tested) —
        // this class only supplies the real reads and C2 bitmaps.
        bool edcValid = !_isAudio && RereadEvidence.CheckDataEdc(main);
        int uncertain = edcValid ? 0 : RereadEvidence.CountUncertain(_mainReads, _lastC2, RawSectorBytes);
        return new ReadAttempt(strategy, edcValid, uncertain);
    }
}
