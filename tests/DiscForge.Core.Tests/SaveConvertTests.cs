using System;
using DiscForge.Core.Saves;
using Xunit;

namespace DiscForge.Core.Tests;

public class SaveConvertTests
{
    [Fact]
    public void Word_swap_16_reverses_each_pair_and_is_its_own_inverse()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6 };
        var swapped = SaveConvert.WordSwap(data, 2);
        Assert.Equal(new byte[] { 2, 1, 4, 3, 6, 5 }, swapped);
        Assert.Equal(data, SaveConvert.WordSwap(swapped, 2));
    }

    [Fact]
    public void Word_swap_32_reverses_each_dword_and_round_trips()
    {
        var data = new byte[] { 1, 2, 3, 4, 10, 20, 30, 40 };
        var swapped = SaveConvert.WordSwap(data, 4);
        Assert.Equal(new byte[] { 4, 3, 2, 1, 40, 30, 20, 10 }, swapped);
        Assert.Equal(data, SaveConvert.WordSwap(swapped, 4));
    }

    [Fact]
    public void Word_swap_leaves_a_trailing_partial_word_untouched()
    {
        var data = new byte[] { 1, 2, 3, 4, 99 };   // 5 bytes, one leftover for width 4
        var swapped = SaveConvert.WordSwap(data, 4);
        Assert.Equal(new byte[] { 4, 3, 2, 1, 99 }, swapped);
    }

    [Fact]
    public void Resize_pads_shorter_saves_and_truncates_longer_ones()
    {
        var data = new byte[] { 1, 2, 3 };
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0 }, SaveConvert.Resize(data, 5));
        Assert.Equal(new byte[] { 1, 2, 3, 0xFF }, SaveConvert.Resize(data, 4, 0xFF));
        Assert.Equal(new byte[] { 1, 2 }, SaveConvert.Resize(data, 2));
    }

    [Fact]
    public void Resize_to_the_n64_sram_size_gives_exactly_32k()
    {
        var save = new byte[100];
        Assert.Equal(SaveConvert.Sram, SaveConvert.Resize(save, SaveConvert.Sram).Length);
        Assert.Equal(32 * 1024, SaveConvert.Sram);
    }

    [Fact]
    public void Trim_trailing_removes_only_the_padding_run()
    {
        Assert.Equal(new byte[] { 1, 2, 3 }, SaveConvert.TrimTrailing(new byte[] { 1, 2, 3, 0, 0, 0 }));
        Assert.Equal(new byte[] { 1, 2, 3 }, SaveConvert.TrimTrailing(new byte[] { 1, 2, 3, 0xFF, 0xFF }, 0xFF));
        // Interior fill bytes are kept.
        Assert.Equal(new byte[] { 1, 0, 2 }, SaveConvert.TrimTrailing(new byte[] { 1, 0, 2, 0, 0 }));
    }

    [Fact]
    public void Pad_then_trim_restores_a_save_with_no_trailing_fill()
    {
        var original = new byte[] { 5, 6, 7, 8 };            // no trailing zeros
        var padded = SaveConvert.Resize(original, 64);
        Assert.Equal(original, SaveConvert.TrimTrailing(padded));
    }
}
