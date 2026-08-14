// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class ShannonEntropyTests
{
    [Fact]
    public void All_one_byte_value_is_zero_entropy()
        => Assert.Equal(0.0, ShannonEntropy.Compute(new byte[1000]).BitsPerByte, 6);

    [Fact]
    public void A_uniform_distribution_is_eight_bits_per_byte()
    {
        var data = new byte[256 * 8];                 // every value 0..255, eight times each
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        Assert.Equal(8.0, ShannonEntropy.Compute(data).BitsPerByte, 6);
    }

    [Fact]
    public void Two_equally_likely_symbols_is_one_bit()
    {
        var data = new byte[1000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 2);
        Assert.Equal(1.0, ShannonEntropy.Compute(data).BitsPerByte, 6);
    }

    [Fact]
    public void Stream_and_buffer_agree()
    {
        var data = new byte[5000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);
        var buf = ShannonEntropy.Compute(data);
        var str = ShannonEntropy.Compute(new MemoryStream(data));
        Assert.Equal(buf.Bytes, str.Bytes);
        Assert.Equal(buf.BitsPerByte, str.BitsPerByte, 9);
    }
}

public class SpamSumTests
{
    private static byte[] Pattern(int n, int seed)
    {
        // A pseudo-random LCG stream — realistic bytes so the rolling hash triggers normally.
        var d = new byte[n];
        uint x = (uint)(seed * 2654435761u + 1);
        for (int i = 0; i < n; i++) { x = x * 1664525u + 1013904223u; d[i] = (byte)(x >> 24); }
        return d;
    }

    [Fact]
    public void Hash_is_deterministic_and_well_formed()
    {
        var d = Pattern(8000, 1);
        string h = SpamSum.Hash(d);
        Assert.Equal(h, SpamSum.Hash(d));
        var parts = h.Split(':');
        Assert.Equal(3, parts.Length);
        Assert.True(uint.TryParse(parts[0], out _));
    }

    [Fact]
    public void Identical_inputs_score_100()
    {
        var d = Pattern(8000, 1);
        Assert.Equal(100, SpamSum.Compare(SpamSum.Hash(d), SpamSum.Hash(d)));
    }

    [Fact]
    public void Similar_inputs_score_higher_than_unrelated_ones()
    {
        var baseData = Pattern(16000, 1);
        var similar = (byte[])baseData.Clone();
        for (int i = 0; i < 8; i++) similar[i * 1500] ^= 0xFF;   // a handful of scattered flips
        var different = Pattern(16000, 99);

        int sim = SpamSum.Compare(SpamSum.Hash(baseData), SpamSum.Hash(similar));
        int diff = SpamSum.Compare(SpamSum.Hash(baseData), SpamSum.Hash(different));

        Assert.True(sim > diff, $"near-identical ({sim}) should score above unrelated ({diff})");
    }

    // ---- reference vectors (generated with an ssdeep-compatible reference implementation) ----
    // The generators below are mirrored byte-for-byte in the vector-producing script, so a signature
    // match here proves cross-tool interchange, not just self-consistency.

    private static byte[] RefLcg(int n, long seed = 12345)
    {
        var outb = new byte[n];
        long x = seed;
        for (int i = 0; i < n; i++)
        {
            x = (x * 1103515245 + 12345) & 0x7fffffff;
            outb[i] = (byte)((x >> 16) & 0xFF);
        }
        return outb;
    }

    private static byte[] RefMixed(int n)
    {
        var a = RefLcg(n / 4);
        var s = System.Text.Encoding.ASCII.GetBytes("DiscForge preserves optical media provably or declines. ");
        var b = new byte[n / 4];
        for (int i = 0; i < b.Length; i++) b[i] = s[i % s.Length];
        var outb = new byte[n];
        int p = 0;
        foreach (var part in new[] { a, b, a, b })
            foreach (var by in part) { if (p >= n) return outb; outb[p++] = by; }
        return outb;
    }

    [Fact]
    public void Signatures_match_the_ssdeep_reference_byte_for_byte()
    {
        Assert.Equal("192:i3ekwunQmc0Ham/8wDfXQ3ekwunQmc0Ham/8wDfp:i3CzmxamnbXQ3Czmxamnbp",
            SpamSum.Hash(RefMixed(20000)));
        Assert.Equal("192:i3ekwunQmc0Ham/8wDfhJ8d1HQ6ibFI9TDznn1/lfvysRAPTZ:i3Czmxamnbhed1HQ6NTX1Z9RkTZ",
            SpamSum.Hash(RefLcg(8000)));

        // The same 20 KB with a 512-byte patch in the middle — a different but related signature.
        var patched = RefMixed(20000);
        System.Array.Copy(RefLcg(512, seed: 999), 0, patched, 10000, 512);
        Assert.Equal("192:i3ekwunQmc0Ham/8wDfjplwunQmc0Ham/8wDfp:i3CzmxamnbYzmxamnbp",
            SpamSum.Hash(patched));
    }

    [Fact]
    public void Compare_scores_match_the_ssdeep_reference()
    {
        var whole = SpamSum.Hash(RefMixed(20000));
        var patched = RefMixed(20000);
        System.Array.Copy(RefLcg(512, seed: 999), 0, patched, 10000, 512);

        // Scores pinned against the REAL ssdeep 2.14.1 binary (`ssdeep -d`), not a port.
        Assert.Equal(91, SpamSum.Compare(whole, SpamSum.Hash(patched)));            // ssdeep 2.14.1: 91
        Assert.Equal(54, SpamSum.Compare(whole, SpamSum.Hash(RefLcg(8000))));       // ssdeep 2.14.1: 54
        Assert.Equal(100, SpamSum.Compare(whole, whole));
    }

    [Fact]
    public void Compare_is_symmetric()
    {
        var a = SpamSum.Hash(RefMixed(20000));
        var b = SpamSum.Hash(RefLcg(8000));
        Assert.Equal(SpamSum.Compare(a, b), SpamSum.Compare(b, a));
    }

    [Fact]
    public void Sequence_stripping_collapses_runs_to_three()
    {
        Assert.Equal("aaabc", SpamSum.StripSequences("aaaaaabc"));
        Assert.Equal("abc", SpamSum.StripSequences("abc"));
    }
}
