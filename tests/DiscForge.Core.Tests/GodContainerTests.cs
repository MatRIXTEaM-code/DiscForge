// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using DiscForge.Core.Xbox;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// GOD (Xbox 360 Games on Demand) package identification: the header magic, the
/// content-type word and content size, and the inventory of Data#### payload files.
/// Built against a hand-constructed header + .data directory. (Full GOD → ISO
/// reconstruction is intentionally not implemented — see GodContainer's remarks and
/// docs/XBOX.md — so there is nothing else to assert here.)
/// </summary>
public class GodContainerTests
{
    private static byte[] Header(string magic, uint contentType, long contentSize)
    {
        var h = new byte[0x360];
        for (int i = 0; i < 4; i++) h[i] = (byte)magic[i];
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x344), contentType);
        BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(0x34C), (ulong)contentSize);
        return h;
    }

    [Fact]
    public void Reads_a_games_on_demand_header_and_inventories_its_data_files()
    {
        var dir = Directory.CreateTempSubdirectory("dforge_god_").FullName;
        try
        {
            string header = Path.Combine(dir, "ABCD1234");
            File.WriteAllBytes(header, Header("LIVE", GodInfo.GamesOnDemandType, 0x1_2345_6789));

            var dataDir = header + ".data";
            Directory.CreateDirectory(dataDir);
            File.WriteAllBytes(Path.Combine(dataDir, "Data0001"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(dataDir, "Data0000"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(dataDir, "notdata.txt"), new byte[10]);

            var info = GodContainer.Read(header);

            Assert.Equal(GodPackageKind.Live, info.Kind);
            Assert.True(info.LooksLikeGamesOnDemand);
            Assert.Equal(0x7000u, info.ContentType);
            Assert.Equal(0x1_2345_6789, info.ContentSize);
            Assert.Equal(2, info.DataFiles.Count);                 // the .txt is ignored
            Assert.Equal("Data0000", Path.GetFileName(info.DataFiles[0].Path));   // sorted
            Assert.Equal(4096 + 2048, info.DataFilesTotal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Sniffs_the_stfs_magics()
    {
        Assert.True(GodContainer.IsStfsHeader(new byte[] { (byte)'C', (byte)'O', (byte)'N', (byte)' ' }));
        Assert.True(GodContainer.IsStfsHeader(new byte[] { (byte)'P', (byte)'I', (byte)'R', (byte)'S' }));
        Assert.False(GodContainer.IsStfsHeader(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void A_non_stfs_file_is_rejected()
    {
        var dir = Directory.CreateTempSubdirectory("dforge_god2_").FullName;
        try
        {
            string p = Path.Combine(dir, "junk");
            File.WriteAllBytes(p, new byte[0x360]);       // all zero, no magic
            Assert.Throws<InvalidDataException>(() => GodContainer.Read(p));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
