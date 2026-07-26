// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// A wholly uncompressed CHD v5 (chdman <c>--compression none</c>) does not use the
/// Huffman-coded compressed map; its map is a flat array of big-endian 4-byte hunk
/// offsets (in hunk-size units), and it stores no SHA-1. These pin the flat-map
/// decode against a real chdman-produced fixture, and the sparse (zero) hunk mapping
/// directly — chdman never emits a sparse hunk, so it is exercised by hand.
/// </summary>
public class ChdUncompressedTests
{
    // A real chdman `createraw --hunksize 512 --unitsize 512 --compression none`
    // image: two 512-byte hunks of random data. Its raw SHA-1 field is all zeros.
    private const string ChdB64 =
        "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";

    private const string ImgB64 =
        "eUK98iEG8IR3YvDzy012TccHIFEVmg+J8sbayuNEuzESRf1vhN+a18Wz0HasDo9TpzVsiJE/IPb3LbAi0k0KltrUPBYXwamOeBKeAyc3EGXQlYZPFa2guEbBwOvFNIrceZrfhJutBdShCsBEHqrutLSO+gsfCr2A6ZijWrpeoL2HmcE1DUOecYl6p1/eMTSkqnLgViisb+aKcz0RYaFdjq4rsELXlYrtsdWU1tES009mAvTecRDpk650IpI9fRcRZdwZBvY9V5l6CtMbOq5AgfQftHFlPj1XeoxBA/nMGYp/idga8qUAHEAXPxkj9xAs+qFQoSSzxcebuIdhqNs/QQHCKFsVv+vCFtwbvv6h19brCX1viiTZctpCDqa/hj7tP8A3ozQC8kl4xxYvMsBbDK4+DTr2kZktEno2Mx+mXCd7XH/oyYG8y7PWKsB401LU90/NTFMx/vfiX0WIZUuhdpfTiG+dC4n1w2ZYuHqk90nW9WnvDvYlzBfvdXgjb4J7YYRGXxKCVhegXdguKzwvh5UStuesAw+rqd/C+Cdr+shAoz2MJ9054IAxv7zml4c2rTr8tB6WXUxbveg/N0ip15lf6vafWiM2XMi3M4iKxBtFFfWKfrWqzuUjtP45TYozOTleYNXIQUrLY1dbZ4C9lg/j0MShnv6Z9w9hATd3+1jrZWNsEuM5kU5F7y0ZDbh3J/8JraWosEQpESivaSBm33H4oTcV0SdmUsj+8iLYavqbC+3qzeBc6RODu73luc1yAWuEvUnrY1FrC1fOVg5HOFbi+14eC87lotAQGnrOFMv8DXB7MMfyYVSqO7E/GpSM7pn6f4gPrLCiLx3eLQE1Dy4JVxL2G2CpZvSu9bMRw5zJLJZe0zrHq85ZxbdeudTgdeP2sIlWxvkVTlcL7y8xo3kcGObuqr0AJGPMNa2fOOYpa3sYTkkFOXWTanDWo2DvWigVOQwzZoIrN+7Mcjf4sc7kOJXjwmk7A+2ZJ66xYvgkutgibX+zH6t43OArgG+lVGlv7da8YdH30PARlQleMQ5Nlh/xFGNqjfvdE7DvZJNJNOOZ0uMnaU75kcC+Utyf7fJxuJOSD+2/t5h8BQdDTApUGQBo7rW5EfpeegaN3a0aMOafhn7/1oWtFg/bE1R+RdOsRI8IVhcI+B7r79S9V5ZdJUc00LTj6I6C55BPoUcT0vh26osPojv5QI+JNN4mvhH55WOfsVzEy6EZim0ToqLIkBJD1YDTKP11Zig6PwKQI9yJ9+yJlBhZePlWSUxb78sESMgbXFqfYUJLGE1twzbdx10NjzVDOkqXQMS0J2IDvUj0fSC1+DWg8gqw4tDtnOJM6cRml1uZVaKIZ0IbHQ==";

    [Fact]
    public void An_uncompressed_hard_disk_chd_extracts_byte_for_byte()
    {
        byte[] chd = System.Convert.FromBase64String(ChdB64);
        byte[] expected = System.Convert.FromBase64String(ImgB64);
        byte[] got = ChdHdExtractor.Extract(chd);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void The_flat_map_maps_nonzero_entries_to_None_and_zero_entries_to_Zero()
    {
        // A minimal buffer: three flat 4-byte big-endian entries at offset 16, values
        // 1, 0, 3 — hunk-size units of 512 bytes.
        const int hunkBytes = 512, unitBytes = 512;
        var buf = new byte[16 + 3 * 4];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24), 3);

        var noParent = ChdMap.DecodeUncompressed(buf, 16, 3, hunkBytes, unitBytes, hasParent: false);
        Assert.Equal(ChdHunkType.None, noParent[0].Type);
        Assert.Equal(1L * hunkBytes, noParent[0].Offset);   // byte offset = unit * hunkBytes
        Assert.Equal(ChdHunkType.Zero, noParent[1].Type);   // sparse hunk, no parent -> zeros
        Assert.Equal(ChdHunkType.None, noParent[2].Type);
        Assert.Equal(3L * hunkBytes, noParent[2].Offset);

        // With a parent, the same sparse entry resolves against the parent instead.
        var withParent = ChdMap.DecodeUncompressed(buf, 16, 3, hunkBytes, unitBytes, hasParent: true);
        Assert.Equal(ChdHunkType.Parent, withParent[1].Type);
        Assert.Equal(1L * hunkBytes / unitBytes, withParent[1].Offset);
    }

    [Fact]
    public void A_flat_map_that_runs_past_the_file_is_declined()
    {
        var buf = new byte[16 + 4];   // room for one entry
        Assert.Throws<ChdFormatException>(() =>
            ChdMap.DecodeUncompressed(buf, 16, 8, 512, 512, hasParent: false));
    }
}
