using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

public class PsxMemoryCardFormatEdgeTests
{
    [Fact]
    public void Format_is_deterministic()
    {
        Assert.Equal(PsxMemoryCard.Format(), PsxMemoryCard.Format());
    }

    [Fact]
    public void Broken_sector_frames_mark_no_broken_sectors()
    {
        var card = PsxMemoryCard.Format();
        for (int i = 16; i <= 35; i++)
        {
            int off = i * 128;
            Assert.Equal(0xFF, card[off + 0]);
            Assert.Equal(0xFF, card[off + 1]);
            Assert.Equal(0xFF, card[off + 2]);
            Assert.Equal(0xFF, card[off + 3]);
        }
    }

    [Fact]
    public void Data_blocks_1_to_15_are_zeroed()
    {
        var card = PsxMemoryCard.Format();
        bool allZero = true;
        for (int b = 8192; b < card.Length; b++)
            if (card[b] != 0) { allZero = false; break; }
        Assert.True(allZero);
    }

    [Fact]
    public void The_write_test_frame_duplicates_the_header()
    {
        var card = PsxMemoryCard.Format();
        int off = 63 * 128;
        Assert.Equal((byte)'M', card[off + 0]);
        Assert.Equal((byte)'C', card[off + 1]);
        Assert.Equal(0x0E, card[off + 127]);
    }

    [Fact]
    public void Formatted_card_wrapped_to_vgs_round_trips()
    {
        var raw = PsxMemoryCard.Format();
        var vgs = Ps1CardConvert.Convert(raw, Ps1CardFormat.Vgs);
        Assert.Equal(Ps1CardFormat.Vgs, Ps1CardConvert.Detect(vgs));
        Assert.Equal(raw, Ps1CardConvert.ToRaw(vgs));
    }
}
