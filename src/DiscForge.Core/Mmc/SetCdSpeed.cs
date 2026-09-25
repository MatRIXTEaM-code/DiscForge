// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Mmc;

/// <summary>
/// Builds the MMC <b>SET CD SPEED</b> command (opcode 0xBB) that tells an optical
/// drive how fast to read (and, optionally, write). Slowing a drive down is the key
/// to rescuing a scratched or marginal disc: a lower rotational speed lets the
/// pickup track a damaged surface it would skip over at full speed — the classic
/// "CD Bremse" use case, and a genuine preservation tool.
///
/// The 12-byte CDB is:
///   0  opcode        0xBB
///   1  rotation ctrl  bits 1:0 (0 = CLV / default)
///   2-3 read speed    uint16 BE, in KB/s  (0xFFFF = drive maximum)
///   4-5 write speed   uint16 BE, in KB/s  (0xFFFF = drive maximum)
///   6-11 reserved     0
///
/// Speeds are in kilobytes per second; one CD "x" is 176 KB/s (75 sectors/s ×
/// 2352 bytes ≈ 176.4 kB/s, rounded to the value drives expect). This class is the
/// pure, unit-testable CDB builder; the actual SPTI pass-through lives in the
/// Windows device layer.
/// </summary>
public static class SetCdSpeed
{
    public const byte Opcode = 0xBB;

    /// <summary>KB/s of a single-speed (1x) CD, as used by SET CD SPEED.</summary>
    public const int CdSpeed1x = 176;

    /// <summary>The "let the drive pick its maximum" sentinel.</summary>
    public const ushort Max = 0xFFFF;

    /// <summary>KB/s for an integer CD multiplier (e.g. 4 → 704). Clamped to a
    /// 16-bit value; 0 or negative means "maximum".</summary>
    public static ushort KbsForMultiplier(int multiplier)
    {
        if (multiplier <= 0) return Max;
        long kbs = (long)multiplier * CdSpeed1x;
        return kbs >= Max ? (ushort)(Max - 1) : (ushort)kbs;
    }

    /// <summary>Build the 12-byte CDB. <paramref name="readKbs"/>/<paramref name="writeKbs"/>
    /// are KB/s; pass <see cref="Max"/> for drive maximum.</summary>
    public static byte[] BuildCdb(ushort readKbs, ushort writeKbs = Max)
    {
        var cdb = new byte[12];
        cdb[0] = Opcode;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(2), readKbs);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(4), writeKbs);
        return cdb;
    }

    /// <summary>Build a CDB that sets the read speed to an integer CD multiplier and
    /// leaves the write speed at the drive maximum.</summary>
    public static byte[] BuildReadMultiplier(int multiplier)
        => BuildCdb(KbsForMultiplier(multiplier), Max);
}
