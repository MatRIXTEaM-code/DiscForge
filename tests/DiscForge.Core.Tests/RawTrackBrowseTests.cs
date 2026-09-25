// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Files;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Browsing a raw Mode 2/2352 bin — the psxrip case. An ISO 9660 filesystem
/// wrapped into 2352-byte sectors (user data at +24) must be listable and
/// extractable straight out of the .bin, with the extracted bytes identical to
/// what went in. This proves the sector-layout probe and the user-data view.
/// </summary>
public class RawTrackBrowseTests
{
    private sealed class Temp : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "df-rawbin-" + Guid.NewGuid().ToString("N")[..8]);
        public Temp() => Directory.CreateDirectory(Dir);
        public string P(string n) => Path.Combine(Dir, n);
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    /// <summary>Inflate a cooked 2048-per-sector ISO into a raw Mode 2/2352 image
    /// by placing each sector's user bytes at offset 24.</summary>
    private static byte[] InflateToMode2_2352(byte[] iso2048)
    {
        int sectors = iso2048.Length / 2048;
        var raw = new byte[sectors * 2352];
        for (int k = 0; k < sectors; k++)
            Array.Copy(iso2048, k * 2048, raw, k * 2352 + 24, 2048);
        return raw;
    }

    [Fact]
    public void RawBin_ListsAndExtracts_LikePsxrip()
    {
        using var t = new Temp();

        var payload = Encoding.ASCII.GetBytes("PlayStation preservation test — the bytes must round-trip exactly.");
        var built = IsoBuilder.Build("PSXTEST", new[]
        {
            new IsoBuilder.FileEntry("HELLO.TXT", payload),
        });

        var raw = InflateToMode2_2352(built.Image);
        string binPath = t.P("game.bin");
        File.WriteAllBytes(binPath, raw);

        // List straight out of the raw bin.
        var listing = ImageBrowser.List(binPath);
        Assert.Null(listing.Error);
        Assert.Contains(listing.Files, f => f.Path.EndsWith("HELLO.TXT", StringComparison.OrdinalIgnoreCase));

        // Extract everything and confirm the content survived.
        string outDir = t.P("out");
        var result = ImageBrowser.Extract(binPath, listing.Files, outDir);
        Assert.Equal(0, result.Failed);
        Assert.True(result.Extracted >= 1);

        var extracted = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
            .First(p => p.EndsWith("HELLO.TXT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(payload, File.ReadAllBytes(extracted));
    }

    [Fact]
    public void RawUserDataStream_DeinterleavesSectors()
    {
        // Two sectors of 2352; user data at +24. Reading the view should return
        // just the concatenated 2048-byte user regions.
        var raw = new byte[2 * 2352];
        for (int i = 0; i < 2048; i++) { raw[24 + i] = (byte)i; raw[2352 + 24 + i] = (byte)(i ^ 0xFF); }

        using var ms = new MemoryStream(raw);
        using var view = new RawTrackUserDataStream(ms, 0, 2352, 24, 2048, 2);
        Assert.Equal(4096, view.Length);

        var got = new byte[4096];
        view.Position = 0;
        view.ReadExactly(got, 0, 4096);
        for (int i = 0; i < 2048; i++)
        {
            Assert.Equal((byte)i, got[i]);
            Assert.Equal((byte)(i ^ 0xFF), got[2048 + i]);
        }
    }
}
