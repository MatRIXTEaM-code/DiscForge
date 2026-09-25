// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the PS1 memory-card reader. A card is built by hand — the "MC" header,
/// directory entries, and save blocks with "SC" title frames — and read back. The
/// checks pin the directory decode, the block-link chain (single- and multi-block
/// saves), the title read, and extraction.
/// </summary>
public class PsxMemoryCardTests
{
    private const int Block = 8192;
    private const int Frame = 128;

    private static int DirFrame(int idx) => (idx + 1) * Frame;

    private static void SetDir(byte[] d, int idx, uint state, uint size, ushort next, string name)
    {
        int at = DirFrame(idx);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), state);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at + 4), size);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at + 8), next);
        Encoding.ASCII.GetBytes(name).CopyTo(d, at + 0x0A);
    }

    private static void SetTitle(byte[] d, int block, string title)
    {
        int at = block * Block;
        d[at] = (byte)'S'; d[at + 1] = (byte)'C';
        Encoding.ASCII.GetBytes(title).CopyTo(d, at + 0x04);
    }

    private static byte[] BlankCard()
    {
        var d = new byte[PsxMemoryCard.ImageSize];
        d[0] = (byte)'M'; d[1] = (byte)'C';
        for (int i = 0; i < 15; i++) SetDir(d, i, 0xA0, 0, 0xFFFF, "");   // all free
        return d;
    }

    [Fact]
    public void A_single_block_save_reads_back_with_its_name_and_title()
    {
        var d = BlankCard();
        SetDir(d, 0, 0x51, Block, 0xFFFF, "BASLUS-00000TESTSAVE");   // 20-byte name field
        SetTitle(d, 1, "TEST SAVE");

        var vol = PsxMemoryCard.Read(d);
        var save = Assert.Single(vol.Saves);
        Assert.Equal("BASLUS-00000TESTSAVE", save.Name);
        Assert.Equal("TEST SAVE", save.Title);
        Assert.Equal(new[] { 1 }, save.Blocks.ToArray());
        Assert.Equal(14, vol.FreeBlocks);
    }

    [Fact]
    public void A_multi_block_save_follows_the_link_chain()
    {
        var d = BlankCard();
        SetDir(d, 0, 0x51, Block * 2, next: 1, "BASLUS-11111BIGSAVE");   // first -> block index 1
        SetDir(d, 1, 0x53, 0, 0xFFFF, "");                               // last
        SetTitle(d, 1, "BIG SAVE");

        var save = Assert.Single(PsxMemoryCard.Read(d).Saves);
        Assert.Equal(new[] { 1, 2 }, save.Blocks.ToArray());             // physical blocks 1 and 2
        Assert.Equal(Block * 2, save.Size);
    }

    [Fact]
    public void Extraction_joins_the_linked_blocks()
    {
        var d = BlankCard();
        SetDir(d, 0, 0x51, Block * 2, next: 1, "SAVE");
        SetDir(d, 1, 0x53, 0, 0xFFFF, "");
        SetTitle(d, 1, "T");
        d[1 * Block + 100] = 0xAB;   // a byte in the first block
        d[2 * Block + 200] = 0xCD;   // a byte in the second block

        var save = PsxMemoryCard.Read(d).Saves.Single();
        var bytes = PsxMemoryCard.Extract(d, save);

        Assert.Equal(Block * 2, bytes.Length);
        Assert.Equal(0xAB, bytes[100]);
        Assert.Equal(0xCD, bytes[Block + 200]);
    }

    [Fact]
    public void Two_independent_saves_are_both_listed()
    {
        var d = BlankCard();
        SetDir(d, 0, 0x51, Block, 0xFFFF, "GAME-A");
        SetDir(d, 5, 0x51, Block, 0xFFFF, "GAME-B");
        SetTitle(d, 1, "A"); SetTitle(d, 6, "B");

        Assert.Equal(2, PsxMemoryCard.Read(d).Saves.Count);
    }

    [Fact]
    public void A_non_ps1_card_is_refused()
    {
        Assert.False(PsxMemoryCard.IsPsxMemoryCard(new byte[PsxMemoryCard.ImageSize]));
        Assert.Throws<PsxMcFormatException>(() => PsxMemoryCard.Read(new byte[100]));
    }
}
