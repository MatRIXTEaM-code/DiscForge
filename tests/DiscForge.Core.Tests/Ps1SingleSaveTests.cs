// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System;
using DiscForge.Core.PlayStation;
using Xunit;

namespace DiscForge.Core.Tests;

public class Ps1SingleSaveTests
{
    private static byte[] SaveBlock(byte fill = 0x77)
    {
        var block = new byte[8192];
        block[0] = (byte)'S'; block[1] = (byte)'C';   // the "SC" save-header a real block opens with
        for (int i = 2; i < block.Length; i++) block[i] = (byte)((i * 3 + fill) & 0xFF);
        return block;
    }

    [Fact]
    public void A_wrapped_save_round_trips_its_product_code_and_payload()
    {
        var block = SaveBlock();
        var psv = Ps1SingleSave.ToPsv("BASCUS-94163FF7-S01", block);

        Assert.True(Ps1SingleSave.IsPsv(psv));
        var read = Ps1SingleSave.Read(psv);
        Assert.Equal("BASCUS-94163FF7-S01", read.ProductCode);
        Assert.Equal(block, read.SaveBlock);
    }

    [Fact]
    public void The_signature_fields_are_honestly_zeroed_not_fabricated()
    {
        var psv = Ps1SingleSave.ToPsv("BASLUS-00000GAMESAVE01", SaveBlock());
        // Key seed (0x08, 20 bytes) and HMAC (0x1C, 20 bytes) — see the class doc-comment: DiscForge
        // has no Sony signing key material, so these stay zero rather than being faked.
        for (int i = 0x08; i < 0x08 + 40; i++)
            Assert.Equal(0, psv[i]);
    }

    [Fact]
    public void A_missing_magic_is_rejected()
    {
        Assert.False(Ps1SingleSave.IsPsv(new byte[9000]));
        Assert.Throws<Ps1PsvFormatException>(() => Ps1SingleSave.Read(new byte[9000]));
    }

    [Fact]
    public void A_non_ps1_platform_indicator_is_rejected()
    {
        var psv = Ps1SingleSave.ToPsv("TEST-00000SAVE", SaveBlock());
        psv[0x38] = 0x02;   // corrupt the platform indicator away from 0x14 (PS1)
        var ex = Assert.Throws<Ps1PsvFormatException>(() => Ps1SingleSave.Read(psv));
        Assert.Contains("not PS1", ex.Message);
    }

    [Fact]
    public void A_wrong_sized_block_is_rejected_rather_than_silently_truncated_or_padded()
    {
        Assert.Throws<Ps1PsvFormatException>(() => Ps1SingleSave.ToPsv("X", new byte[100]));
    }

    [Fact]
    public void ProductCode_longer_than_the_field_is_truncated_not_overrun()
    {
        var longCode = new string('A', 40);
        var psv = Ps1SingleSave.ToPsv(longCode, SaveBlock());
        var read = Ps1SingleSave.Read(psv);
        Assert.Equal(20, read.ProductCode.Length);
    }
}
