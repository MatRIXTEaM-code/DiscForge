using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

public class PsxMemoryCardFormatTests
{
    [Fact]
    public void Formatted_card_is_128kb_with_the_mc_header()
    {
        var card = PsxMemoryCard.Format();
        Assert.Equal(PsxMemoryCard.ImageSize, card.Length);
        Assert.Equal((byte)'M', card[0]);
        Assert.Equal((byte)'C', card[1]);
        Assert.Equal(0x0E, card[127]);              // 'M' ^ 'C'
        Assert.True(PsxMemoryCard.IsPsxMemoryCard(card));
    }

    [Fact]
    public void Formatted_card_reads_as_empty_with_all_blocks_free()
    {
        var vol = PsxMemoryCard.Read(PsxMemoryCard.Format());
        Assert.Empty(vol.Saves);
        Assert.Equal(0, vol.UsedBlocks);
        Assert.Equal(15, vol.FreeBlocks);
    }

    [Fact]
    public void Directory_frames_are_marked_free_with_the_correct_checksum()
    {
        var card = PsxMemoryCard.Format();
        for (int i = 1; i <= 15; i++)
        {
            int off = i * 128;
            Assert.Equal(0xA0, card[off + 0x00]);   // allocation state: free
            Assert.Equal(0xFF, card[off + 0x08]);   // next-block link = 0xFFFF
            Assert.Equal(0xFF, card[off + 0x09]);
            Assert.Equal(0xA0, card[off + 127]);    // XOR checksum 0xA0 ^ 0xFF ^ 0xFF = 0xA0
        }
    }

    [Fact]
    public void Every_directory_frame_checksum_actually_validates()
    {
        var card = PsxMemoryCard.Format();
        // Header frame + 15 directory frames: byte 127 must equal the XOR of 0..126.
        for (int frame = 0; frame <= 15; frame++)
        {
            int off = frame * 128;
            byte xor = 0;
            for (int b = 0; b < 127; b++) xor ^= card[off + b];
            Assert.Equal(card[off + 127], xor);
        }
    }

    [Fact]
    public void A_formatted_card_wrapped_to_dexdrive_round_trips_back_to_the_same_bytes()
    {
        var raw = PsxMemoryCard.Format();
        var gme = Ps1CardConvert.Convert(raw, Ps1CardFormat.DexDrive);
        Assert.Equal(Ps1CardFormat.DexDrive, Ps1CardConvert.Detect(gme));
        Assert.Equal(raw, Ps1CardConvert.ToRaw(gme));
    }
}
