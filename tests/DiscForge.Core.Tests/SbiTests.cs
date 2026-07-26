// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Cue;
using DiscForge.Core.PlayStation;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// SBI generation and round-trip — the portable LibCrypt sidecar. It must
/// capture exactly the anomalous sectors (wrong CRC, or a valid-CRC frame whose
/// stored position is wrong), key each to the sector's true M:S:F, and survive
/// serialise/parse byte-for-byte.
/// </summary>
public class SbiTests
{
    private const int N = 2000;

    private static byte[] AuthorSub(Func<int, byte[]> qFor)
    {
        var sub = new byte[N * 96];
        for (int s = 0; s < N; s++)
        {
            var q = qFor(s);
            for (int i = 0; i < 96; i++)
                if ((q[i >> 3] & (0x80 >> (i & 7))) != 0)
                    sub[s * 96 + i] |= 0x40;
        }
        return sub;
    }

    private static byte[] GoodQ(int s) =>
        SubQ.Position(QControl.Data, 1, 1, Msf.FromSectors(s), Msf.FromSectors(s + 150));

    [Fact]
    public void FromSubchannel_CapturesCorruptCrcSectors_WithTrueMsf()
    {
        int[] corrupt = { 600, 601, 1300, 1301 };
        var sub = AuthorSub(GoodQ);
        foreach (var s in corrupt) sub[s * 96 + 30] ^= 0x40;   // wreck the Q-CRC

        var doc = Sbi.FromSubchannel(sub, startLba: 0);
        Assert.Equal(corrupt.Length, doc.Entries.Count);

        // Sector 600 sits at absolute 750 = 00:10:00.
        var first = doc.Entries[0];
        Assert.Equal(0, first.Minute);
        Assert.Equal(10, first.Second);
        Assert.Equal(0, first.Frame);
        Assert.Equal(Sbi.TypeQ10, first.Type);
        Assert.Equal(10, first.Data.Length);
    }

    [Fact]
    public void FromSubchannel_DetectsValidCrcButWrongPosition()
    {
        // One sector carries a perfectly valid Q whose stored absolute address is
        // wrong — the recomputed-CRC LibCrypt variant a CRC-only check misses.
        const int bad = 900;
        var sub = AuthorSub(s => s == bad
            ? SubQ.Position(QControl.Data, 1, 1, Msf.FromSectors(s), Msf.FromSectors(s + 150 + 3000))
            : GoodQ(s));

        var doc = Sbi.FromSubchannel(sub, startLba: 0);
        Assert.Single(doc.Entries);
        Assert.Equal(bad + 150, doc.Entries[0].AbsSectors);
    }

    [Fact]
    public void CleanDisc_YieldsEmptySbi()
    {
        var sub = AuthorSub(GoodQ);
        var doc = Sbi.FromSubchannel(sub, startLba: 0);
        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public void Write_Then_Parse_RoundTrips()
    {
        int[] corrupt = { 600, 601 };
        var sub = AuthorSub(GoodQ);
        foreach (var s in corrupt) sub[s * 96 + 30] ^= 0x40;

        var doc = Sbi.FromSubchannel(sub, startLba: 0);
        var bytes = Sbi.Write(doc);

        Assert.True(bytes.AsSpan(0, 4).SequenceEqual(Sbi.Magic));

        var round = Sbi.Parse(bytes);
        Assert.Equal(doc.Entries.Count, round.Entries.Count);
        for (int i = 0; i < doc.Entries.Count; i++)
        {
            Assert.Equal(doc.Entries[i].Minute, round.Entries[i].Minute);
            Assert.Equal(doc.Entries[i].Second, round.Entries[i].Second);
            Assert.Equal(doc.Entries[i].Frame, round.Entries[i].Frame);
            Assert.Equal(doc.Entries[i].Type, round.Entries[i].Type);
            Assert.Equal(doc.Entries[i].Data, round.Entries[i].Data);
        }
    }

    [Fact]
    public void FloodOfErrors_IsRefusedAsDamage()
    {
        var sub = AuthorSub(GoodQ);
        for (int s = 0; s < N; s += 5) sub[s * 96 + 30] ^= 0x40;   // hundreds of bad frames
        Assert.Throws<InvalidDataException>(() => Sbi.FromSubchannel(sub, startLba: 0));
    }
}
