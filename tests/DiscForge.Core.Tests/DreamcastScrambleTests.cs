// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Gdi;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Dreamcast 1ST_READ.BIN scramble. The core guarantee is that
/// scramble and descramble are exact inverses (round-trip identity) across sizes
/// that exercise several chunk sizes and a sub-32-byte tail. The permutation must
/// also be a true rearrangement — same bytes, different order — and deterministic.
/// </summary>
public class DreamcastScrambleTests
{
    private static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(1024)]
    [InlineData(70000)]      // spans 64 KB + smaller chunks
    [InlineData(32 * 100 + 5)] // a 5-byte tail
    public void Scramble_then_descramble_is_the_identity(int size)
    {
        var plain = Pattern(size);
        var round = DreamcastScramble.Descramble(DreamcastScramble.Scramble(plain));
        Assert.Equal(plain, round);
    }

    [Fact]
    public void Scrambling_actually_reorders_slices_but_keeps_the_bytes()
    {
        var plain = Pattern(1024);
        var scrambled = DreamcastScramble.Scramble(plain);

        Assert.NotEqual(plain, scrambled);                       // order changed
        Assert.Equal(plain.OrderBy(x => x), scrambled.OrderBy(x => x));  // multiset unchanged
    }

    [Fact]
    public void The_tail_under_32_bytes_is_copied_straight()
    {
        // 20 bytes: entirely tail, so scramble is a no-op.
        var plain = Pattern(20);
        Assert.Equal(plain, DreamcastScramble.Scramble(plain));
    }

    [Fact]
    public void The_transform_is_deterministic()
    {
        var plain = Pattern(2048);
        Assert.Equal(DreamcastScramble.Scramble(plain), DreamcastScramble.Scramble(plain));
    }
}
