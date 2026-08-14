// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Core.Raw;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>What became of one sector after C2-guided recovery.</summary>
public sealed record C2SectorResult
{
    public required uint Lba { get; init; }
    public required byte[] Sector { get; init; }
    /// <summary>Byte offsets no read could vouch for. Empty means the sector is
    /// as good as this drive can attest.</summary>
    public required IReadOnlyList<int> UncertainBytes { get; init; }
    public required int ReadsUsed { get; init; }
    /// <summary>C2 errors the first successful read reported — what a plain
    /// reader would have been up against.</summary>
    public required int InitialBadBytes { get; init; }

    /// <summary>
    /// True when the drive rejected every attempt, so nothing was read at all.
    ///
    /// This is emphatically not the same as a damaged sector, and conflating the
    /// two produces the worst kind of diagnostic: "2352 bytes uncertain, damage
    /// that never moves is physical" reads as a ruined disc when the truth may
    /// be that READ CD was sent to a DVD, where it has never been valid. The
    /// distinction is kept because the remedies are opposite — one calls for a
    /// different command, the other for a cloth.
    /// </summary>
    public required bool AllReadsRefused { get; init; }

    /// <summary>Why the drive refused, when it did.</summary>
    public string? RefusalReason { get; init; }

    /// <summary>True when Reed-Solomon parity repaired what re-reading could not.</summary>
    public bool EccRepaired { get; init; }
    public int EccBytesCorrected { get; init; }
    public string? EccDetail { get; init; }

    public bool Complete => !AllReadsRefused && UncertainBytes.Count == 0;
    public bool NeededRecovery => !AllReadsRefused && ReadsUsed > 1;

    public string Describe()
    {
        if (AllReadsRefused)
            return $"LBA {Lba:N0}: the drive refused every read" +
                   (RefusalReason is not null ? $" — {RefusalReason}" : "");
        if (EccRepaired)
            return $"LBA {Lba:N0}: {EccBytesCorrected} byte(s) rebuilt from the sector's own " +
                   $"parity after {ReadsUsed} read(s) — EDC confirms it";
        if (Complete)
            return NeededRecovery
                ? $"LBA {Lba:N0}: recovered from {InitialBadBytes} bad byte(s) using {ReadsUsed} reads"
                : $"LBA {Lba:N0}: clean";
        return $"LBA {Lba:N0}: {UncertainBytes.Count} byte(s) UNCERTAIN after {ReadsUsed} read(s)" +
               (EccDetail is not null ? $" — {EccDetail}" : "");
    }
}

public sealed record C2ReadOptions
{
    /// <summary>
    /// How many times to read a damaged sector before accepting what's there.
    /// Each pass costs a seek and a revolution; beyond about eight the returns
    /// vanish because a byte no read has managed by then is usually physically
    /// gone rather than marginal.
    /// </summary>
    public int MaxReads { get; init; } = 8;

    /// <summary>
    /// Bit ordering of the C2 block. MMC specifies MSB-first; this exists so a
    /// drive that disagrees can be accommodated without a rebuild, and so the
    /// ordering can be proven rather than assumed.
    /// </summary>
    public bool C2MsbFirst { get; init; } = true;

    /// <summary>
    /// A sector the drive refuses outright is retried once — a transient
    /// not-ready is worth a second chance — but not eight times. Eight
    /// rejections take as long as eight reads and tell you nothing the first
    /// two didn't.
    /// </summary>
    public int MaxReadsWhenRefused { get; init; } = 2;

    /// <summary>
    /// After re-reading has done all it can, hand the remaining bad bytes to the
    /// sector's own Reed-Solomon parity. On by default: it costs no further
    /// reading, and the EDC check means a bad repair is rejected rather than
    /// accepted.
    /// </summary>
    public bool UseEccCorrection { get; init; } = true;
}

/// <summary>
/// Reads sectors using C2 error pointers to combine several attempts into one
/// good sector, then repairs what remains from the parity on the disc.
///
/// The ordinary retry loop — read, fail, read again, hope — throws away every
/// failed attempt. But a failed read is mostly correct: the drive names the few
/// bytes it couldn't correct, and those bytes move between attempts because the
/// failure is marginal rather than absolute. Keeping each attempt and taking
/// every byte from a read that vouched for it recovers sectors that no single
/// read can produce.
///
/// What re-reading cannot fix is damage that doesn't move — and that is where
/// the second stage earns its place. Every Mode 1 sector carries 276 bytes of
/// Reed-Solomon parity, and the code corrects twice as many ERASURES (errors at
/// known positions) as it does errors at unknown ones. C2 supplies exactly those
/// positions. So the two stages are complementary: voting handles damage that
/// varies between reads, parity handles damage that is permanent.
///
/// A caveat that cost real time to learn: READ CD (0xBE) is a CD command. Point
/// it at a DVD and every call is rejected, which — reported carelessly — looks
/// exactly like catastrophic disc damage. Refusals are therefore tracked apart
/// from damage throughout, and the caller is expected to check the media type
/// before starting.
/// </summary>
[SupportedOSPlatform("windows")]
public static class C2SectorReader
{
    /// <summary>
    /// Read one sector, re-reading and combining while C2 reports damage, then
    /// correcting what's left from the sector's own parity.
    ///
    /// Stops reading as soon as a read comes back clean or the accumulated reads
    /// cover every byte — there is nothing to gain from further passes at that
    /// point, and on a badly damaged disc the time saved is substantial. Also
    /// stops early when the drive is refusing outright, since repetition won't
    /// change its mind.
    /// </summary>
    public static C2SectorResult ReadSector(SptiDevice dev, uint lba,
                                            C2ReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dev);
        var opts = options ?? new C2ReadOptions();

        var voter = new C2SectorVoter();
        var buffer = new byte[MmcCommands.SectorBytesWithC2];
        int initialBad = -1;
        int refusals = 0, successes = 0;
        string? refusalReason = null;

        int maxReads = Math.Max(1, opts.MaxReads);
        for (int attempt = 0; attempt < maxReads; attempt++)
        {
            Array.Clear(buffer);
            var r = dev.SendCommand(
                MmcCommands.ReadCdWithC2(lba, 1), buffer, SptiDataDirection.In,
                timeoutSeconds: 30);

            // Every attempt is recorded exactly once, at the bottom of the loop.
            // An earlier version also added the buffer inside the give-up branch,
            // which counted refused attempts twice and made ReadsUsed report more
            // reads than were actually issued.
            bool giveUp = false;
            C2ErrorMap map;

            if (!r.Success)
            {
                refusals++;
                refusalReason ??= r.Describe();

                // Nothing in the buffer is trustworthy. Flag it all so a later
                // read can supply the bytes — a refused attempt is still a data
                // point about what's reachable.
                map = C2ErrorMap.All();

                // A drive that won't serve this sector won't change its mind on
                // the eighth ask. Two attempts covers a transient not-ready.
                giveUp = refusals >= Math.Max(1, opts.MaxReadsWhenRefused) && successes == 0;
            }
            else
            {
                successes++;
                map = C2ErrorMap.Parse(
                    buffer.AsSpan(C2ErrorMap.SectorBytes, C2ErrorMap.C2Bytes),
                    opts.C2MsbFirst);
                if (initialBad < 0) initialBad = map.BadByteCount;
            }

            voter.Add(buffer.AsSpan(0, C2ErrorMap.SectorBytes), map);

            if (giveUp) break;
            if (map.Clean) break;                 // nothing to improve on
            if (voter.FullyCovered()) break;      // every byte vouched for by someone
        }

        var vote = voter.Vote();
        var sector = vote.Sector;
        IReadOnlyList<int> uncertain = vote.UncertainBytes;
        bool eccRepaired = false;
        int eccBytes = 0;
        string? eccDetail = null;

        // Re-reading has done all it can. What remains is damage that doesn't
        // move — but the sector carries its own Reed-Solomon parity, and the
        // positions of the bad bytes are precisely what C2 just told us. Known
        // positions are erasures rather than errors, and the code corrects twice
        // as many of those. Damage that is physically permanent is often still
        // repairable from bytes already in hand.
        //
        // Mode 1 and Mode 2 Form 1 both carry the parity: byte 15 says the mode
        // and, for Mode 2, the sub-header says the form. Mode 2 Form 2 has no
        // parity and audio none at all, so those are left to the vote alone.
        bool mode1 = IsMode1(sector);
        bool mode2Form1 = !mode1 && IsMode2Form1(sector);
        if (opts.UseEccCorrection && uncertain.Count > 0 && successes > 0 && (mode1 || mode2Form1))
        {
            var repaired = (byte[])sector.Clone();
            var result = mode1 ? EccCorrector.CorrectMode1(repaired, uncertain)
                               : EccCorrector.CorrectMode2Form1(repaired, uncertain);

            // Accept the repair only if the EDC agrees. That check is
            // independent of the correction — computed over the whole sector by
            // a different polynomial — so a decode that produced plausible
            // nonsense fails it, and the unrepaired sector stands.
            if (result.Success)
            {
                sector = repaired;
                uncertain = result.StillUncertain;
                eccRepaired = true;
                eccBytes = result.BytesCorrected;
            }
            eccDetail = result.Detail;
        }

        return new C2SectorResult
        {
            Lba = lba,
            Sector = sector,
            UncertainBytes = uncertain,
            ReadsUsed = vote.ReadsUsed,
            InitialBadBytes = Math.Max(0, initialBad),
            AllReadsRefused = successes == 0,
            RefusalReason = successes == 0 ? refusalReason : null,
            EccRepaired = eccRepaired,
            EccBytesCorrected = eccBytes,
            EccDetail = eccDetail,
        };
    }

    /// <summary>True when the sector's own header says Mode 1.</summary>
    private static bool IsMode1(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 16) return false;
        if (sector[0] != 0x00 || sector[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (sector[i] != 0xFF) return false;
        return (sector[15] & 0x03) == 1;
    }

    /// <summary>True when the header says Mode 2 and the sub-header says Form 1
    /// (sub-mode bit 5 clear) — the CD-XA / PlayStation data form that carries ECC.</summary>
    private static bool IsMode2Form1(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 24) return false;
        if (sector[0] != 0x00 || sector[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (sector[i] != 0xFF) return false;
        if ((sector[15] & 0x03) != 2) return false;
        return (sector[18] & 0x20) == 0;
    }

    /// <summary>
    /// Check whether a drive will actually return C2 pointers, by asking it for
    /// one sector and seeing whether the command is accepted.
    ///
    /// Worth doing before a long recovery run: the mode page says what a drive
    /// claims, this says what it does — and on DVD media the answer is always no,
    /// because READ CD does not apply there whatever the mode page reports.
    /// </summary>
    public static bool SupportsC2(SptiDevice dev, uint testLba = 0)
    {
        var buffer = new byte[MmcCommands.SectorBytesWithC2];
        var r = dev.SendCommand(
            MmcCommands.ReadCdWithC2(testLba, 1), buffer, SptiDataDirection.In,
            timeoutSeconds: 20);
        return r.Success;
    }
}