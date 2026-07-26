using System;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

public class Ps1CardConvertTests
{
    private static byte[] RawCard(byte fill = 0xAB)
    {
        var card = new byte[Ps1CardConvert.CardSize];
        card[0] = (byte)'M'; card[1] = (byte)'C';        // PS1 cards start with "MC"
        for (int i = 2; i < card.Length; i++) card[i] = (byte)((i * 7 + fill) & 0xFF);
        return card;
    }

    [Fact]
    public void A_raw_card_is_detected_and_returned_unchanged()
    {
        var raw = RawCard();
        Assert.Equal(Ps1CardFormat.Raw, Ps1CardConvert.Detect(raw));
        Assert.Equal(raw, Ps1CardConvert.ToRaw(raw));
    }

    [Fact]
    public void Raw_to_dexdrive_adds_a_header_and_round_trips()
    {
        var raw = RawCard();
        var gme = Ps1CardConvert.Convert(raw, Ps1CardFormat.DexDrive);

        Assert.Equal(Ps1CardFormat.DexDrive, Ps1CardConvert.Detect(gme));
        Assert.Equal(3904 + Ps1CardConvert.CardSize, gme.Length);
        Assert.Equal(raw, Ps1CardConvert.ToRaw(gme));       // card data preserved byte-for-byte
    }

    [Fact]
    public void Raw_to_vgs_adds_a_header_and_round_trips()
    {
        var raw = RawCard();
        var vgs = Ps1CardConvert.Convert(raw, Ps1CardFormat.Vgs);

        Assert.Equal(Ps1CardFormat.Vgs, Ps1CardConvert.Detect(vgs));
        Assert.Equal(64 + Ps1CardConvert.CardSize, vgs.Length);
        Assert.Equal(raw, Ps1CardConvert.ToRaw(vgs));
    }

    [Fact]
    public void Dexdrive_to_vgs_converts_directly_and_keeps_the_card()
    {
        var raw = RawCard(0x5C);
        var gme = Ps1CardConvert.Convert(raw, Ps1CardFormat.DexDrive);
        var vgs = Ps1CardConvert.Convert(gme, Ps1CardFormat.Vgs);

        Assert.Equal(Ps1CardFormat.Vgs, Ps1CardConvert.Detect(vgs));
        Assert.Equal(raw, Ps1CardConvert.ToRaw(vgs));
    }

    [Fact]
    public void A_short_or_unknown_file_is_rejected()
    {
        Assert.Equal(Ps1CardFormat.Unknown, Ps1CardConvert.Detect(new byte[100]));
        Assert.Throws<Ps1CardConvert.Ps1CardFormatException>(() => Ps1CardConvert.ToRaw(new byte[100]));
    }
}
