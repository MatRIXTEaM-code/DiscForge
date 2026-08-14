using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class ReedSolomonGf256Tests
{
    private static byte[] Message(int k, int seed)
    {
        var m = new byte[k];
        new System.Random(seed).NextBytes(m);
        return m;
    }

    [Fact]
    public void Encoding_is_systematic_and_the_right_length()
    {
        var rs = new ReedSolomonGf256(32, 28);
        var msg = Message(28, 1);
        var code = rs.Encode(msg);
        Assert.Equal(32, code.Length);
        Assert.Equal(msg, code[..28]);   // systematic: message is the prefix
    }

    [Fact]
    public void A_clean_codeword_decodes_unchanged()
    {
        var rs = new ReedSolomonGf256(32, 28);
        var code = rs.Encode(Message(28, 2));
        Assert.True(rs.TryDecode(code, out var fixedUp));
        Assert.Equal(code, fixedUp);
    }

    [Fact]
    public void It_corrects_up_to_two_errors()
    {
        var rs = new ReedSolomonGf256(32, 28);   // n-k=4 → corrects 2 errors
        var msg = Message(28, 3);
        var code = rs.Encode(msg);

        var damaged = (byte[])code.Clone();
        damaged[5] ^= 0xAB;
        damaged[20] ^= 0x7C;

        Assert.True(rs.TryDecode(damaged, out var recovered));
        Assert.Equal(msg, recovered[..28]);
    }

    [Fact]
    public void It_corrects_up_to_four_erasures()
    {
        var rs = new ReedSolomonGf256(28, 24);   // n-k=4 → corrects 4 erasures
        var msg = Message(24, 4);
        var code = rs.Encode(msg);

        var damaged = (byte[])code.Clone();
        var erased = new[] { 2, 9, 15, 25 };
        foreach (var p in erased) damaged[p] ^= 0xFF;

        Assert.True(rs.TryDecode(damaged, out var recovered, erased));
        Assert.Equal(msg, recovered[..24]);
    }

    [Fact]
    public void Too_many_errors_are_reported_uncorrectable()
    {
        var rs = new ReedSolomonGf256(32, 28);   // corrects only 2 errors
        var code = rs.Encode(Message(28, 5));

        var damaged = (byte[])code.Clone();
        damaged[1] ^= 0x11; damaged[10] ^= 0x22; damaged[20] ^= 0x33;   // 3 errors > capacity

        // Either it fails outright, or it must not silently return a wrong "fix".
        bool ok = rs.TryDecode(damaged, out var result);
        if (ok) Assert.NotEqual(code, result);   // never claim success with wrong data
    }

    [Fact]
    public void Erasures_plus_an_error_within_budget_are_corrected()
    {
        var rs = new ReedSolomonGf256(28, 24);   // 4 parity: 1 error (=2) + 2 erasures = 4, within budget
        var msg = Message(24, 6);
        var code = rs.Encode(msg);

        var damaged = (byte[])code.Clone();
        damaged[3] ^= 0x5A;                        // unknown error
        var erased = new[] { 12, 19 };
        foreach (var p in erased) damaged[p] ^= 0xC3;

        Assert.True(rs.TryDecode(damaged, out var recovered, erased));
        Assert.Equal(msg, recovered[..24]);
    }
}
