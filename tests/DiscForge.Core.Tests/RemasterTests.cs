using System;
using System.Collections.Generic;
using System.Linq;
using DiscForge.Core.Iso;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

public class RemasterTests
{
    private static byte[] Resolve(IReadOnlyDictionary<string, byte[]> store, string sha) => store[sha];

    [Fact]
    public void A_synthetic_image_rebuilds_byte_for_byte()
    {
        // [0..20) structural non-zero, [20..40) file A, [40..64) zeros, [64..84) file B, [84..100) zeros.
        var image = new byte[100];
        for (int i = 0; i < 20; i++) image[i] = (byte)(0x80 + i);      // structure
        for (int i = 20; i < 40; i++) image[i] = (byte)('A');          // file A
        for (int i = 64; i < 84; i++) image[i] = (byte)('B');          // file B

        var (recipe, store) = Remaster.Build(image, new[] { ((long)20, (long)20), ((long)64, (long)20) });
        var rebuilt = Remaster.Rebuild(recipe, sha => Resolve(store, sha));
        Assert.Equal(image, rebuilt);

        var v = Remaster.Verify(recipe, sha => Resolve(store, sha));
        Assert.True(v.Match);
        Assert.Equal(2, recipe.FileRegions);
    }

    [Fact]
    public void Identical_files_are_stored_once()
    {
        var image = new byte[80];
        for (int i = 0; i < 20; i++) image[i] = (byte)('X');           // file 1
        for (int i = 40; i < 60; i++) image[i] = (byte)('X');          // file 2 — identical content
        var (recipe, store) = Remaster.Build(image, new[] { ((long)0, (long)20), ((long)40, (long)20) });
        Assert.Equal(2, recipe.FileRegions);
        Assert.Single(store);                                          // deduped to one blob
        Assert.Equal(image, Remaster.Rebuild(recipe, sha => Resolve(store, sha)));
    }

    [Fact]
    public void Long_zero_padding_compresses_to_a_zero_region()
    {
        var image = new byte[2048];
        for (int i = 0; i < 10; i++) image[i] = (byte)(i + 1);         // a little structure, then all zeros
        var (recipe, store) = Remaster.Build(image, Array.Empty<(long, long)>());
        Assert.Contains(recipe.Regions, r => r.Kind == "zero");
        Assert.Equal(image, Remaster.Rebuild(recipe, sha => Resolve(store, sha)));
    }

    [Fact]
    public void Recipe_survives_a_json_round_trip()
    {
        var image = new byte[64];
        for (int i = 16; i < 48; i++) image[i] = (byte)('F');
        var (recipe, store) = Remaster.Build(image, new[] { ((long)16, (long)32) });
        var back = Remaster.FromJson(Remaster.ToJson(recipe));
        Assert.Equal(recipe.ImageSha256, back.ImageSha256);
        Assert.Equal(image, Remaster.Rebuild(back, sha => Resolve(store, sha)));
    }

    [Fact]
    public void A_wrong_blob_fails_verification()
    {
        var image = new byte[40];
        for (int i = 0; i < 20; i++) image[i] = (byte)('G');
        var (recipe, store) = Remaster.Build(image, new[] { ((long)0, (long)20) });
        var tampered = new Dictionary<string, byte[]>(store);
        foreach (var k in tampered.Keys.ToList()) tampered[k] = new byte[20];   // zero it out, same length
        var v = Remaster.Verify(recipe, sha => tampered[sha]);
        Assert.False(v.Match);
    }

    [Fact]
    public void A_real_iso_rebuilds_byte_for_byte()
    {
        // Build a genuine ISO 9660 image, remaster it, and prove it regenerates exactly.
        var files = new List<IsoBuilder.FileEntry>
        {
            new("HELLO.TXT", System.Text.Encoding.ASCII.GetBytes(new string('H', 5000))),
            new("DATA.BIN", Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray()),
            new("EMPTY.DAT", Array.Empty<byte>()),
        };
        byte[] iso = IsoBuilder.Build("TESTVOL", files).Image;

        var (recipe, store) = Remaster.FromIso(iso);
        Assert.True(recipe.FileRegions >= 2);                          // the two non-empty files

        var rebuilt = Remaster.Rebuild(recipe, sha => store[sha]);
        Assert.Equal(iso, rebuilt);                                    // byte-exact reconstruction

        var v = Remaster.Verify(recipe, sha => store[sha]);
        Assert.True(v.Match);
    }
}
