// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Raw;

namespace DiscForge.Core.Recovery;

/// <summary>
/// The pure correctness signals behind Tier B (real-hardware) adaptive re-read: byte-level consensus
/// across repeated reads of one sector, reinforced by the drive's own C2 error-pointer bits, plus the
/// sector's own EDC/ECC check for data sectors. Kept here — separate from the hardware I/O in
/// <c>DiscForge.Devices.Reading.DriveRereadSource</c> — so this reasoning is unit-testable without a
/// drive, in the same spirit as <see cref="AdaptiveReread"/> itself.
/// </summary>
public static class RereadEvidence
{
    /// <summary>
    /// How many bytes of <paramref name="sectorLength"/> are still NOT vouched for, given every main-
    /// channel read taken so far plus the most recent C2 error-pointer bitmap (one bit per byte, MSB
    /// first, may be null when C2 wasn't requested on this read).
    ///
    /// A single read proves nothing — with fewer than two reads, every byte is reported uncertain,
    /// full stop, regardless of what C2 says. Only once at least two independent reads exist can
    /// agreement mean anything; a byte then counts as certain only when EVERY read agrees on it AND
    /// the current C2 bitmap doesn't flag it — agreement between two equally-wrong reads is never
    /// mistaken for correctness just because a C2 pointer happened not to fire on this particular pass.
    /// </summary>
    public static int CountUncertain(IReadOnlyList<byte[]> reads, byte[]? c2, int sectorLength)
    {
        ArgumentNullException.ThrowIfNull(reads);
        if (reads.Count < 2) return sectorLength;

        int uncertain = 0;
        for (int i = 0; i < sectorLength; i++)
        {
            byte first = reads[0][i];
            bool agree = true;
            for (int j = 1; j < reads.Count; j++)
            {
                if (i >= reads[j].Length || reads[j][i] != first) { agree = false; break; }
            }
            bool c2Bad = c2 is not null && IsC2Bad(c2, i);
            if (!agree || c2Bad) uncertain++;
        }
        return uncertain;
    }

    /// <summary>Is the C2 error-pointer bit for main-channel byte <paramref name="byteIndex"/> set?
    /// MMC's C2 block is one bit per main-channel byte, most-significant-bit first within each byte.</summary>
    public static bool IsC2Bad(byte[] c2, int byteIndex)
    {
        ArgumentNullException.ThrowIfNull(c2);
        if (byteIndex < 0) return false;
        int b = byteIndex / 8, bit = 7 - (byteIndex % 8);
        return b < c2.Length && (c2[b] & (1 << bit)) != 0;
    }

    /// <summary>
    /// EDC+ECC check on a raw 2352-byte sector for the modes that carry one: Mode 1 and Mode 2 Form 1.
    /// The mode is read from the sector header (byte 15, per Yellow/XA Book). Mode 2 Form 2 (no ECC,
    /// only a weaker EDC over its own smaller payload) and anything unrecognized fall back to false —
    /// this never fabricates a "valid" verdict for a mode it can't actually check; correctness for
    /// those must come from byte-level consensus instead (see <see cref="CountUncertain"/>).
    /// </summary>
    public static bool CheckDataEdc(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 16) return false;
        byte mode = sector[15];
        try
        {
            if (mode == 1)
            {
                var (edcOk, eccOk) = EdcEcc.VerifyMode1(sector);
                return edcOk && eccOk;
            }
            if (mode == 2)
            {
                var (edcOk, eccOk) = EdcEcc.VerifyMode2Form1(sector);
                return edcOk && eccOk;
            }
        }
        catch
        {
            // A sector that doesn't parse under the mode it claims is not a valid sector.
        }
        return false;
    }
}
