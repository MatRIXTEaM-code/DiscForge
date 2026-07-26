// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Media;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Byte-offset tests for the media-identification parsers.
///
/// These matter more than most: the offsets were arrived at from the MMC spec
/// and then confirmed against one real disc (a Mitsubishi CD-RW reporting
/// 97m34s23f on a MATSHITA UJ8E2). References disagree by a byte or two in
/// places, so once a layout is known to work on hardware it needs pinning —
/// otherwise a later "tidy-up" can silently move a field and the only symptom
/// is a wrong manufacturer name nobody checks.
/// </summary>
public class MediaIdentityTests
{
    /// <summary>
    /// Build an ATIP response the way a drive returns one: a 4-byte header, then
    /// the descriptor. Lead-in MSF sits at absolute 8/9/10, lead-out at 12/13/14,
    /// and the disc-type bit is bit 6 of byte 6.
    /// </summary>
    private static byte[] AtipResponse(int inMin, int inSec, int inFrame,
                                       int outMin, int outSec, int outFrame,
                                       bool rewritable)
    {
        var r = new byte[32];
        r[0] = 0x00; r[1] = 0x1E;          // data length
        r[2] = 0x00; r[3] = 0x00;          // reserved

        r[4] = 0x80;                        // ITWP valid
        r[6] = (byte)(rewritable ? 0x40 : 0x00);

        r[8] = (byte)inMin; r[9] = (byte)inSec; r[10] = (byte)inFrame;
        r[12] = (byte)outMin; r[13] = (byte)outSec; r[14] = (byte)outFrame;
        return r;
    }

    [Fact]
    public void Atip_reads_the_manufacturer_code_from_the_lead_in_time()
    {
        // The exact response shape that produced "Mitsubishi Chemical / Verbatim"
        // on real hardware. If this fails, the offsets have moved.
        var response = AtipResponse(97, 34, 23, 79, 59, 74, rewritable: true);

        var id = MediaIdentityParser.ParseAtip(response);

        Assert.NotNull(id);
        Assert.Equal("97m34s23f", id!.AtipCode);
        Assert.Equal("Mitsubishi Chemical / Verbatim", id.Manufacturer);
        Assert.True(id.IsRewritable);
    }

    [Fact]
    public void Atip_disc_type_bit_distinguishes_cd_r_from_cd_rw()
    {
        var cdr = MediaIdentityParser.ParseAtip(
            AtipResponse(97, 26, 66, 79, 59, 74, rewritable: false));
        var cdrw = MediaIdentityParser.ParseAtip(
            AtipResponse(97, 26, 66, 79, 59, 74, rewritable: true));

        Assert.False(cdr!.IsRewritable);
        Assert.True(cdrw!.IsRewritable);
    }

    [Fact]
    public void Atip_capacity_comes_from_the_lead_out_less_the_pregap()
    {
        // 79:59:74 is the usual lead-out on an 80-minute disc.
        var id = MediaIdentityParser.ParseAtip(
            AtipResponse(97, 34, 23, 79, 59, 74, rewritable: false));

        // ((79*60 + 59) * 75 + 74) - 150 pregap = 359,699 sectors of 2048 bytes.
        Assert.NotNull(id!.CapacityMb);
        Assert.InRange(id.CapacityMb!.Value, 700, 705);
        Assert.Equal((79, 59, 74), id.LeadOut);
    }

    [Fact]
    public void Atip_reports_an_unknown_code_rather_than_failing()
    {
        // No public list of dye codes is complete. An unrecognised one is a gap
        // in the table, not a bad disc — the code must still come back.
        var id = MediaIdentityParser.ParseAtip(
            AtipResponse(97, 1, 1, 79, 59, 74, rewritable: false));

        Assert.NotNull(id);
        Assert.Equal("97m01s01f", id!.AtipCode);
        Assert.Null(id.Manufacturer);
        Assert.NotEmpty(id.Notes);
    }

    [Fact]
    public void Atip_returns_null_for_a_pressed_disc()
    {
        // Pressed discs have no ATIP. Some drives answer with zeros instead of a
        // check condition, and that must read as "not recordable", not as a disc
        // whose manufacturer code happens to be 00m00s00f.
        var allZero = new byte[32];
        Assert.Null(MediaIdentityParser.ParseAtip(allZero));
    }

    [Fact]
    public void Atip_returns_null_when_the_response_is_too_short()
    {
        Assert.Null(MediaIdentityParser.ParseAtip(new byte[8]));
    }

    /// <summary>
    /// A READ DISC STRUCTURE format 0x00 response: 4-byte header, then the
    /// physical format block. Book type is the high nibble of byte 4.
    /// </summary>
    private static byte[] PhysicalFormat(int bookType, int layers,
                                         uint dataStart, uint dataEnd)
    {
        var r = new byte[68];
        r[0] = 0x00; r[1] = 0x42;
        r[4] = (byte)((bookType << 4) | 0x01);        // book type + part version
        r[6] = (byte)(((layers - 1) & 0x03) << 5);

        r[9] = (byte)(dataStart >> 16);
        r[10] = (byte)(dataStart >> 8);
        r[11] = (byte)dataStart;

        r[13] = (byte)(dataEnd >> 16);
        r[14] = (byte)(dataEnd >> 8);
        r[15] = (byte)dataEnd;
        return r;
    }

    [Theory]
    [InlineData(0x0, "DVD-ROM")]
    [InlineData(0x1, "DVD-RAM")]
    [InlineData(0x2, "DVD-R")]
    [InlineData(0x3, "DVD-RW")]
    [InlineData(0x9, "DVD+RW")]
    [InlineData(0xA, "DVD+R")]
    [InlineData(0xE, "DVD+R DL")]
    public void Physical_format_names_the_book_type(int book, string expected)
    {
        var id = MediaIdentityParser.ParsePhysicalFormat(
            PhysicalFormat(book, layers: 1, dataStart: 0x30000, dataEnd: 0x26053F));

        Assert.NotNull(id);
        Assert.Equal(expected, id!.BookTypeName);
        Assert.Equal(book, id.BookType);
    }

    [Fact]
    public void Physical_format_derives_capacity_from_the_data_area()
    {
        // A single-layer DVD: data from 0x30000 to 0x26053F is 4.7 GB.
        var id = MediaIdentityParser.ParsePhysicalFormat(
            PhysicalFormat(0x0, layers: 1, dataStart: 0x30000, dataEnd: 0x26053F));

        Assert.NotNull(id!.CapacityMb);
        Assert.InRange(id.CapacityMb!.Value, 4400, 4500);
        Assert.Equal(1, id.Layers);
    }

    [Fact]
    public void Physical_format_reads_the_layer_count()
    {
        var dl = MediaIdentityParser.ParsePhysicalFormat(
            PhysicalFormat(0xE, layers: 2, dataStart: 0x30000, dataEnd: 0x26053F));

        Assert.Equal(2, dl!.Layers);
    }

    [Theory]
    [InlineData("TYG03", "Taiyo Yuden 16× DVD-R")]
    [InlineData("MCC 03RG20", "Mitsubishi / Verbatim 16× DVD-R")]
    [InlineData("RITEKG05", "Ritek 8× DVD-R")]
    public void Dvd_media_ids_resolve_to_manufacturers(string id, string expected)
    {
        Assert.Equal(expected, DvdMediaIds.Lookup(id));
    }

    [Fact]
    public void Dvd_media_id_lookup_is_case_and_whitespace_tolerant()
    {
        // Drives pad these strings, and case varies between firmware revisions.
        Assert.Equal("Taiyo Yuden 16× DVD-R", DvdMediaIds.Lookup("  tyg03  "));
    }

    [Fact]
    public void Unknown_media_id_returns_null()
    {
        Assert.Null(DvdMediaIds.Lookup("NOTAREALMEDIAID"));
    }

    [Fact]
    public void Atip_table_is_populated()
    {
        // Guards against the table being emptied by a bad merge — every lookup
        // would then silently return "unknown" and nothing would look broken.
        Assert.True(AtipManufacturers.KnownCodes >= 20);
        Assert.True(DvdMediaIds.KnownIds >= 20);
    }
}