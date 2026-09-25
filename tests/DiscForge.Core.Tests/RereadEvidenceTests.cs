// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Raw;
using DiscForge.Core.Recovery;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The pure correctness signals behind Tier B (real-hardware) adaptive re-read: byte-level consensus
/// across repeated reads plus C2 reinforcement, and the EDC/ECC check for data sectors. Proven here,
/// independent of any drive, so the hardware wiring (<c>DriveRereadSource</c>) can trust it.
/// </summary>
public class RereadEvidenceTests
{
    private static byte[] ValidMode1Sector()
    {
        var s = new byte[2352];
        // Sync pattern isn't checked by VerifyMode1, but header/mode is expected in place.
        s[15] = 1; // Mode 1
        for (int i = 16; i < 2064; i++) s[i] = (byte)(i * 7);
        EdcEcc.FillMode1(s);
        return s;
    }

    [Fact]
    public void CountUncertain_with_zero_or_one_read_reports_everything_uncertain()
    {
        Assert.Equal(2352, RereadEvidence.CountUncertain(Array.Empty<byte[]>(), null, 2352));

        var single = new byte[][] { new byte[2352] };
        Assert.Equal(2352, RereadEvidence.CountUncertain(single, null, 2352));
    }

    [Fact]
    public void CountUncertain_two_agreeing_reads_with_no_c2_are_fully_certain()
    {
        var a = new byte[100];
        var b = new byte[100];
        for (int i = 0; i < 100; i++) a[i] = b[i] = (byte)i;
        Assert.Equal(0, RereadEvidence.CountUncertain(new[] { a, b }, null, 100));
    }

    [Fact]
    public void CountUncertain_disagreeing_bytes_are_uncertain()
    {
        var a = new byte[10];
        var b = new byte[10];
        for (int i = 0; i < 10; i++) a[i] = b[i] = (byte)i;
        b[3] = 0xFF; // one disagreement
        b[7] = 0xAB; // another
        Assert.Equal(2, RereadEvidence.CountUncertain(new[] { a, b }, null, 10));
    }

    [Fact]
    public void CountUncertain_c2_flag_overrides_agreement()
    {
        // Two reads agree on every byte, but C2 flags byte 5 as bad anyway — agreement between two
        // equally-wrong reads must not be mistaken for correctness.
        var a = new byte[10];
        var b = new byte[10];
        for (int i = 0; i < 10; i++) a[i] = b[i] = 0x42;

        var c2 = new byte[2]; // 16 bits available, byte 5 -> bit index 5 -> byte 0, bit (7-5)=2
        c2[0] |= 1 << (7 - 5);

        Assert.Equal(1, RereadEvidence.CountUncertain(new[] { a, b }, c2, 10));
    }

    [Fact]
    public void IsC2Bad_bit_layout_is_msb_first_per_byte()
    {
        var c2 = new byte[] { 0b1000_0000, 0b0000_0001 };
        Assert.True(RereadEvidence.IsC2Bad(c2, 0));   // byte 0, bit 7 (MSB)
        Assert.False(RereadEvidence.IsC2Bad(c2, 1));  // byte 0, bit 6
        Assert.True(RereadEvidence.IsC2Bad(c2, 15));  // byte 1, bit 0 (LSB)
        Assert.False(RereadEvidence.IsC2Bad(c2, 8));  // byte 1, bit 7
    }

    [Fact]
    public void CheckDataEdc_valid_mode1_sector_passes()
    {
        Assert.True(RereadEvidence.CheckDataEdc(ValidMode1Sector()));
    }

    [Fact]
    public void CheckDataEdc_corrupted_mode1_sector_fails()
    {
        var s = ValidMode1Sector();
        s[100] ^= 0xFF; // flip a data byte after EDC/ECC were computed
        Assert.False(RereadEvidence.CheckDataEdc(s));
    }

    [Fact]
    public void CheckDataEdc_unrecognized_mode_never_fabricates_valid()
    {
        // Claims Mode 2 but is genuinely non-zero, unfilled garbage — not a valid Form 1 sector,
        // so its EDC/ECC must not verify. (An untouched all-zero buffer is a degenerate case where
        // a linear parity check is trivially satisfied by all-zero data — not exercised here, since
        // it says nothing about whether real, non-trivial sector content is checked correctly.)
        var s = new byte[2352];
        s[15] = 2;
        for (int i = 16; i < 2072; i++) s[i] = (byte)(i * 3 + 1);
        Assert.False(RereadEvidence.CheckDataEdc(s));

        var unknown = new byte[2352];
        unknown[15] = 9; // not a real mode
        for (int i = 16; i < 2064; i++) unknown[i] = (byte)(i * 5 + 2);
        Assert.False(RereadEvidence.CheckDataEdc(unknown));
    }

    [Fact]
    public void CheckDataEdc_too_short_span_fails_cleanly()
    {
        Assert.False(RereadEvidence.CheckDataEdc(new byte[10]));
    }
}
