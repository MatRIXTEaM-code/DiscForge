// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Vmu;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the VMU writer, validated by round trip against the reader: format a
/// blank card, add saves, read them back and extract them. A save that survives
/// create → add → read → extract with its bytes intact proves the FAT chain,
/// directory entry and allocation are all right.
/// </summary>
public class VmuBuilderTests
{
    private static byte[] Save(string header, int totalBytes)
    {
        var b = new byte[totalBytes];
        Encoding.ASCII.GetBytes(header).CopyTo(b, 0);          // 16-char short description
        for (int i = 0x30; i < totalBytes; i++) b[i] = (byte)(i & 0xFF);
        return b;
    }

    [Fact]
    public void A_freshly_formatted_card_is_empty_and_valid()
    {
        var vmu = VmuImage.Read(VmuBuilder.CreateFormatted());
        Assert.True(vmu.Formatted);
        Assert.Empty(vmu.Files);
        Assert.Equal(200, vmu.FreeBlocks);
    }

    [Fact]
    public void An_added_save_reads_back_and_extracts_intact()
    {
        var save = Save("MY GAME SAVE", 1024);   // two blocks
        var image = VmuBuilder.Add(VmuBuilder.CreateFormatted(), "SAVE01", save);

        var vmu = VmuImage.Read(image);
        var file = Assert.Single(vmu.Files);
        Assert.Equal("SAVE01", file.Name);
        Assert.Equal(2, file.SizeBlocks);
        Assert.Equal("MY GAME SAVE", file.ShortDescription);
        Assert.Equal(198, vmu.FreeBlocks);

        var extracted = VmuImage.Extract(image, file);
        Assert.Equal(save, extracted[..save.Length]);
    }

    [Fact]
    public void Two_saves_coexist()
    {
        var image = VmuBuilder.Add(VmuBuilder.CreateFormatted(), "FIRST", Save("first save", 512));
        image = VmuBuilder.Add(image, "SECOND", Save("second save", 1536));

        var vmu = VmuImage.Read(image);
        Assert.Equal(2, vmu.Files.Count);
        Assert.Contains(vmu.Files, f => f.Name == "FIRST" && f.SizeBlocks == 1);
        Assert.Contains(vmu.Files, f => f.Name == "SECOND" && f.SizeBlocks == 3);
        Assert.Equal(200 - 1 - 3, vmu.FreeBlocks);
    }

    [Fact]
    public void Adding_a_save_too_big_for_the_free_space_is_refused()
    {
        var huge = new byte[201 * 512];   // 201 blocks, only 200 user blocks exist
        Assert.Throws<InvalidOperationException>(() =>
            VmuBuilder.Add(VmuBuilder.CreateFormatted(), "TOOBIG", huge));
    }
}

/// <summary>Tests for the VMI descriptor writer/reader.</summary>
public class VmiTests
{
    [Fact]
    public void A_vmi_round_trips_its_fields()
    {
        var vmi = Vmi.Create("SONICADV", "SONIC.SAV", "Sonic Adventure", fileSize: 2048,
                             isGame: false, copyProtected: true);
        var info = Vmi.Read(vmi);

        Assert.Equal(Vmi.Size, vmi.Length);
        Assert.Equal("SONICADV", info.ResourceName);
        Assert.Equal("SONIC.SAV", info.VmuFileName);
        Assert.Equal("Sonic Adventure", info.Description);
        Assert.Equal(2048, info.FileSize);
        Assert.True(info.CopyProtected);
        Assert.False(info.IsGame);
    }

    [Fact]
    public void The_checksum_is_the_resource_name_anded_with_sega()
    {
        var vmi = Vmi.Create("SEGA", "F", "d", 100);
        // "SEGA" & "SEGA" = "SEGA" -> bytes 'S','E','G','A' little-endian.
        uint expected = (uint)('S' | ('E' << 8) | ('G' << 16) | ('A' << 24));
        Assert.Equal(expected, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(vmi.AsSpan(0, 4)));
        Assert.Equal(expected, Vmi.Checksum(vmi));
    }
}
