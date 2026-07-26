// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Ciso;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for CSO/ZSO. The CSO path is validated by round trip — an ISO compressed
/// to CSO and decompressed back must be byte-identical — across compressible,
/// incompressible and non-block-aligned sizes. The ZSO/LZ4 decoder is pinned with a
/// hand-built LZ4 block whose literals-and-match output is known.
/// </summary>
public class CisoImageTests
{
    private static byte[] Compressible(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i / 64) & 0xFF);   // long runs → compresses well
        return b;
    }

    private static byte[] Incompressible(int n)
    {
        var b = new byte[n];
        uint s = 0x12345678;
        for (int i = 0; i < n; i++) { s = s * 1103515245 + 12345; b[i] = (byte)(s >> 24); }
        return b;
    }

    [Theory]
    [InlineData(2048)]        // one block
    [InlineData(2048 * 4)]    // several blocks
    [InlineData(2048 * 3 + 500)] // a partial final block
    public void Cso_round_trips_compressible_data(int size)
    {
        var iso = Compressible(size);
        var cso = CisoImage.Compress(iso);
        Assert.True(cso.Length < iso.Length);           // it actually compressed
        Assert.Equal(iso, CisoImage.Decompress(cso));
    }

    [Fact]
    public void Cso_round_trips_incompressible_data_by_storing_it_raw()
    {
        var iso = Incompressible(2048 * 3);
        var cso = CisoImage.Compress(iso);
        Assert.Equal(iso, CisoImage.Decompress(cso));   // raw-stored blocks still reproduce exactly
    }

    [Fact]
    public void The_header_reports_the_kind_and_size()
    {
        var cso = CisoImage.Compress(Compressible(4096));
        using var ms = new MemoryStream(cso);
        var info = CisoImage.ReadInfo(ms);

        Assert.Equal(CisoKind.Ciso, info.Kind);
        Assert.Equal(4096, info.UncompressedSize);
        Assert.Equal(2048, info.BlockSize);
        Assert.Equal(2, info.Blocks);
    }

    [Fact]
    public void A_non_ciso_is_refused()
    {
        Assert.False(CisoImage.IsCiso(new byte[] { 1, 2, 3, 4 }));
        using var ms = new MemoryStream(new byte[24]);
        Assert.Throws<CisoFormatException>(() => CisoImage.ReadInfo(ms));
    }

    [Fact]
    public void The_lz4_decoder_expands_literals_and_a_back_match()
    {
        // LZ4 block for "ABCABCA": token 0x30 (3 literals, match code 0 -> len 4),
        // literals "ABC", offset 3 (little-endian), match copies 4 bytes from -3.
        var block = new byte[] { 0x30, (byte)'A', (byte)'B', (byte)'C', 0x03, 0x00 };
        var dst = new byte[7];
        int n = CisoImage.Lz4DecompressBlock(block, dst);

        Assert.Equal(7, n);
        Assert.Equal("ABCABCA", Encoding.ASCII.GetString(dst));
    }

    [Fact]
    public void The_lz4_decoder_handles_a_literals_only_block()
    {
        var block = new byte[] { 0x40, (byte)'D', (byte)'A', (byte)'T', (byte)'A' };  // 4 literals, no match
        var dst = new byte[4];
        Assert.Equal(4, CisoImage.Lz4DecompressBlock(block, dst));
        Assert.Equal("DATA", Encoding.ASCII.GetString(dst));
    }
}
