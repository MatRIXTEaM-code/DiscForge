// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Saves;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>Tests for the save's own banner/icon decoder (as opposed to the disc-level opening.bnr).</summary>
public class GcSaveBannerTests
{
    private static byte[] MakeEntry(byte flags, uint bannerOffset, ushort iconFormat)
    {
        var e = new byte[GcLayout.EntrySize];
        Encoding.ASCII.GetBytes("GALE").CopyTo(e, 0);
        Encoding.ASCII.GetBytes("01").CopyTo(e, 4);
        e[6] = 0xFF;
        e[7] = flags;
        Encoding.ASCII.GetBytes("test").CopyTo(e, 8);
        BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(0x2C), bannerOffset);
        BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(0x30), iconFormat);
        return e;
    }

    private static GcSave MakeSave(byte[] entry) =>
        GcLayout.ParseEntry(entry, comment: "", firstBlock: 5, blockCount: 1);

    private static byte[] Rgb5A3Bytes(byte r, byte g, byte b)
    {
        // Opaque RGB555, top bit set.
        ushort v = (ushort)(0x8000 | ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
        return new byte[] { (byte)(v >> 8), (byte)v };
    }

    [Fact]
    public void BannerFormat_reads_bit0_of_the_flags_byte()
    {
        Assert.Equal(GcSaveImageFormat.Ci8, GcSaveBannerReader.BannerFormat(MakeSave(MakeEntry(0x00, 0, 0))));
        Assert.Equal(GcSaveImageFormat.Rgb5A3, GcSaveBannerReader.BannerFormat(MakeSave(MakeEntry(0x01, 0, 0))));
    }

    [Fact]
    public void IconFormat_reads_the_low_2_bits_of_the_0x30_field()
    {
        Assert.Equal(GcSaveImageFormat.None, GcSaveBannerReader.IconFormat(MakeSave(MakeEntry(0x01, 0, 0))));
        Assert.Equal(GcSaveImageFormat.Rgb5A3, GcSaveBannerReader.IconFormat(MakeSave(MakeEntry(0x01, 0, 2))));
        Assert.Equal(GcSaveImageFormat.Ci8, GcSaveBannerReader.IconFormat(MakeSave(MakeEntry(0x01, 0, 1))));
        Assert.Equal(GcSaveImageFormat.Ci8, GcSaveBannerReader.IconFormat(MakeSave(MakeEntry(0x01, 0, 3))));
    }

    [Fact]
    public void DecodeBanner_decodes_a_solid_color_RGB5A3_banner()
    {
        var entry = MakeEntry(flags: 0x01, bannerOffset: 0, iconFormat: 0);
        var save = MakeSave(entry);

        var payload = new byte[GcSaveBannerReader.BannerWidth * GcSaveBannerReader.BannerHeight * 2];
        var texel = Rgb5A3Bytes(0xF8, 0x08, 0x08);   // ~red, snapped to 5-bit precision
        for (int i = 0; i < payload.Length; i += 2) { payload[i] = texel[0]; payload[i + 1] = texel[1]; }

        var img = GcSaveBannerReader.DecodeBanner(save, payload);
        Assert.NotNull(img);
        Assert.Equal(GcSaveImageFormat.Rgb5A3, img!.Format);
        Assert.Equal(96, img.Width);
        Assert.Equal(32, img.Height);
        Assert.Equal(255, img.Rgba[0]);   // 5-bit 0x1F round-trips to a full 255, not the original 0xF8
        Assert.Equal(255, img.Rgba[3]);   // opaque
    }

    [Fact]
    public void DecodeBanner_decodes_a_solid_color_CI8_banner_via_its_trailing_palette()
    {
        var entry = MakeEntry(flags: 0x00, bannerOffset: 0, iconFormat: 0);
        var save = MakeSave(entry);

        int w = GcSaveBannerReader.BannerWidth, h = GcSaveBannerReader.BannerHeight;
        var payload = new byte[w * h + 256 * 2];
        // Every pixel indexes palette entry 7; palette entry 7 is solid blue.
        for (int i = 0; i < w * h; i++) payload[i] = 7;
        var texel = Rgb5A3Bytes(0x08, 0x08, 0xF8);
        int palOff = w * h + 7 * 2;
        payload[palOff] = texel[0]; payload[palOff + 1] = texel[1];

        var img = GcSaveBannerReader.DecodeBanner(save, payload);
        Assert.NotNull(img);
        Assert.Equal(GcSaveImageFormat.Ci8, img!.Format);
        Assert.Equal(255, img.Rgba[2]);   // blue channel (5-bit 0x1F round-trips to a full 255)
        Assert.Equal(255, img.Rgba[3]);
    }

    [Fact]
    public void DecodeIcon_decodes_the_first_frame_immediately_after_the_banner()
    {
        // RGB5A3 banner (no palette) followed immediately by an RGB5A3 icon frame.
        var entry = MakeEntry(flags: 0x01, bannerOffset: 0, iconFormat: 2);
        var save = MakeSave(entry);

        int bannerBytes = GcSaveBannerReader.BannerWidth * GcSaveBannerReader.BannerHeight * 2;
        int iconBytes = GcSaveBannerReader.IconWidth * GcSaveBannerReader.IconHeight * 2;
        var payload = new byte[bannerBytes + iconBytes];
        var texel = Rgb5A3Bytes(0x08, 0xF8, 0x08);   // green
        for (int i = bannerBytes; i < payload.Length; i += 2) { payload[i] = texel[0]; payload[i + 1] = texel[1]; }

        var icon = GcSaveBannerReader.DecodeIcon(save, payload);
        Assert.NotNull(icon);
        Assert.Equal(32, icon!.Width);
        Assert.Equal(32, icon.Height);
        Assert.Equal(255, icon.Rgba[1]);   // green channel (5-bit 0x1F round-trips to a full 255, not 0xF8)
    }

    [Fact]
    public void DecodeIcon_returns_null_when_the_entry_declares_no_icon()
    {
        var entry = MakeEntry(flags: 0x01, bannerOffset: 0, iconFormat: 0);
        var save = MakeSave(entry);
        var payload = new byte[GcSaveBannerReader.BannerWidth * GcSaveBannerReader.BannerHeight * 2];
        Assert.Null(GcSaveBannerReader.DecodeIcon(save, payload));
    }

    [Fact]
    public void DecodeBanner_returns_null_when_the_offset_does_not_fit_the_payload()
    {
        var entry = MakeEntry(flags: 0x01, bannerOffset: 0, iconFormat: 0);
        var save = MakeSave(entry);
        Assert.Null(GcSaveBannerReader.DecodeBanner(save, Array.Empty<byte>()));
    }
}
