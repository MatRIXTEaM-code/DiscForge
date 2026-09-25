// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Identify;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the universal format identifier. Each format is presented as the
/// minimal bytes that carry its signature and must be named correctly, and the
/// fixed-offset signatures (ISO, XISO, VMU) and the NRG tail footer are exercised
/// at their real positions.
/// </summary>
public class FormatIdentifierTests
{
    private static byte[] WithAscii(string s, int size = 64)
    {
        var b = new byte[Math.Max(size, s.Length)];
        Encoding.ASCII.GetBytes(s).CopyTo(b, 0);
        return b;
    }

    [Theory]
    [InlineData("MComprHD", "CHD")]
    [InlineData("CISO", "CSO")]
    [InlineData("ZISO", "ZSO")]
    [InlineData("Sony PS2 Memory Card Format ", "PS2 memory card")]
    [InlineData("PS-X EXE", "PS-EXE")]
    [InlineData("VAGp", "VAG")]
    [InlineData("MEDIA DESCRIPTOR", "MDS")]
    [InlineData("WBFS", "WBFS")]
    [InlineData("DVDVIDEO-VMG", "IFO")]
    [InlineData("VIDEO_CD", "VCD INFO")]
    [InlineData("ENTRYVCD", "VCD ENTRIES")]
    [InlineData("PPF30", "PPF")]
    public void Offset_zero_magics_are_named(string magic, string expected)
    {
        Assert.Equal(expected, FormatIdentifier.Identify(WithAscii(magic)).Name);
    }

    [Fact]
    public void Tim_and_tmd_binary_magics_are_named()
    {
        Assert.Equal("TIM", FormatIdentifier.Identify(new byte[] { 0x10, 0, 0, 0, 0, 0, 0, 0 }).Name);
        Assert.Equal("TMD", FormatIdentifier.Identify(new byte[] { 0x41, 0, 0, 0, 0, 0, 0, 0 }).Name);
    }

    [Fact]
    public void An_iso9660_is_identified_by_its_cd001_at_sector_16()
    {
        var img = new byte[0x8010];
        Encoding.ASCII.GetBytes("CD001").CopyTo(img, 0x8001);
        Assert.Equal("ISO 9660", FormatIdentifier.Identify(img).Name);
    }

    [Fact]
    public void An_xiso_is_identified_by_the_media_signature_at_sector_32()
    {
        var img = new byte[0x10000 + 32];
        Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA").CopyTo(img, 0x10000);
        Assert.Equal("XISO", FormatIdentifier.Identify(img).Name);
    }

    [Fact]
    public void A_vmu_is_identified_by_the_root_block_marker()
    {
        var img = new byte[128 * 1024];
        for (int i = 0; i < 16; i++) img[0x1FE00 + i] = 0x55;
        Assert.Equal("VMU", FormatIdentifier.Identify(img).Name);
    }

    [Fact]
    public void A_saturn_backup_is_identified_by_its_signature()
    {
        var img = new byte[32 * 1024];
        Encoding.ASCII.GetBytes("BackUpRam Format").CopyTo(img, 0);
        Assert.Equal("Saturn backup", FormatIdentifier.Identify(img).Name);
    }

    [Fact]
    public void An_n64_controller_pak_is_identified_by_its_id_block()
    {
        var pak = new byte[32 * 1024];
        int off = 0x20;
        for (int i = 0; i < 0x1C; i++) pak[off + i] = (byte)(i + 1);
        uint sum = 0;
        for (int i = 0; i < 14; i++)
            sum += (uint)((pak[off + i * 2] << 8) | pak[off + i * 2 + 1]);
        pak[off + 0x1C] = (byte)((sum >> 8) & 0xFF); pak[off + 0x1D] = (byte)(sum & 0xFF);
        ushort b = (ushort)((0xFFF2 - sum) & 0xFFFF);
        pak[off + 0x1E] = (byte)(b >> 8); pak[off + 0x1F] = (byte)(b & 0xFF);
        Assert.Equal("N64 Controller Pak", FormatIdentifier.Identify(pak).Name);
    }

    [Fact]
    public void An_nrg_is_identified_by_its_tail_footer()
    {
        var img = new byte[10000];
        Encoding.ASCII.GetBytes("NER5").CopyTo(img, img.Length - 12);
        Assert.Equal("NRG", FormatIdentifier.Identify(img).Name);
    }

    [Fact]
    public void A_cue_sheet_is_identified_as_text()
    {
        var cue = Encoding.ASCII.GetBytes("FILE \"game.bin\" BINARY\n  TRACK 01 MODE2/2352\n");
        Assert.Equal("CUE", FormatIdentifier.Identify(cue).Name);
    }

    [Fact]
    public void A_gdi_index_is_identified_as_text()
    {
        var gdi = Encoding.ASCII.GetBytes("3\n1 0 4 2352 track01.bin 0\n2 756 0 2352 track02.raw 0\n");
        Assert.Equal("GDI", FormatIdentifier.Identify(gdi).Name);
    }

    [Fact]
    public void An_unknown_file_is_reported_as_unknown()
    {
        var junk = new byte[500];
        for (int i = 0; i < junk.Length; i++) junk[i] = (byte)(i * 91 + 3);
        Assert.False(FormatIdentifier.Identify(junk).Recognised);
    }

    [Theory]
    [InlineData(0x80000004u, "2")]
    [InlineData(0x80000005u, "3")]
    [InlineData(0x80000006u, "3.5")]
    public void A_discjuggler_cdi_is_identified_by_its_eof_trailer(uint magic, string version)
    {
        // A CDI has no header magic; it ends with an 8-byte trailer: version dword +
        // a descriptor locator that points inside the file.
        var img = new byte[4096];
        uint locator = 1000;
        int t = img.Length - 8;
        img[t] = (byte)magic; img[t + 1] = (byte)(magic >> 8); img[t + 2] = (byte)(magic >> 16); img[t + 3] = (byte)(magic >> 24);
        img[t + 4] = (byte)locator; img[t + 5] = (byte)(locator >> 8); img[t + 6] = (byte)(locator >> 16); img[t + 7] = (byte)(locator >> 24);

        var id = FormatIdentifier.Identify(img);
        Assert.Equal("CDI", id.Name);
        Assert.Contains(version, id.Detail);
    }

    [Fact]
    public void A_bad_cdi_locator_is_not_claimed()
    {
        // Right version magic but a locator past the end of the file — not a valid CDI.
        var img = new byte[1024];
        int t = img.Length - 8;
        img[t] = 0x06; img[t + 1] = 0x00; img[t + 2] = 0x00; img[t + 3] = 0x80;   // 0x80000006
        uint locator = 999999;
        img[t + 4] = (byte)locator; img[t + 5] = (byte)(locator >> 8); img[t + 6] = (byte)(locator >> 16); img[t + 7] = (byte)(locator >> 24);
        Assert.False(FormatIdentifier.Identify(img).Recognised);
    }

    // ---- common (non-disc) formats: named rather than left "unknown" ----

    [Fact]
    public void An_mp4_video_is_named()
    {
        var d = new byte[64];
        d[4] = (byte)'f'; d[5] = (byte)'t'; d[6] = (byte)'y'; d[7] = (byte)'p';
        Assert.Equal("MP4 / MOV", FormatIdentifier.Identify(d).Name);
    }

    [Theory]
    [InlineData("PNG", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("JPEG", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("ZIP", new byte[] { 0x50, 0x4B, 0x03, 0x04 })]
    [InlineData("gzip", new byte[] { 0x1F, 0x8B, 0x08, 0x00 })]
    [InlineData("PDF", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })]
    public void Common_container_magics_are_named(string expected, byte[] magic)
    {
        var d = new byte[64];
        System.Array.Copy(magic, d, magic.Length);
        Assert.Equal(expected, FormatIdentifier.Identify(d).Name);
    }

    [Fact]
    public void Plain_text_is_named_text_not_unknown()
    {
        var txt = Encoding.ASCII.GetBytes("this is a sample dat file\nit is actually just plain text\n");
        Assert.Equal("Text", FormatIdentifier.Identify(txt).Name);
    }

    [Fact]
    public void A_logiqx_dat_is_named_dat()
    {
        var dat = Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?>\n<datafile>\n<game name=\"x\"/>\n</datafile>");
        Assert.Equal("DAT", FormatIdentifier.Identify(dat).Name);
    }

    [Fact]
    public void A_raw_cd_data_track_is_named_by_its_sync()
    {
        var trk = new byte[2352 * 2];
        trk[0] = 0x00; for (int i = 1; i <= 10; i++) trk[i] = 0xFF; trk[11] = 0x00;   // 12-byte sync
        Assert.Equal("BIN", FormatIdentifier.Identify(trk).Name);
    }

    [Fact]
    public void A_bare_2352_multiple_is_named_a_raw_cd_track()
    {
        // A CD-DA audio track has no sync — just N sectors of 2352 bytes (this is the
        // Resident Evil 2 (Track 2).bin case, 15,900 sectors).
        var trk = new byte[2352 * 8];
        for (int i = 0; i < trk.Length; i++) trk[i] = 0xAA;   // non-text, no magic
        Assert.Equal("BIN", FormatIdentifier.Identify(trk).Name);
    }
}
