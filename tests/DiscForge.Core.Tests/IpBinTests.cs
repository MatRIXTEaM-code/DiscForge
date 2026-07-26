// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Gdi;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the Dreamcast boot header (IP.BIN) reader — the descriptive read
/// that names a GD-ROM: region, product number, title. Built from a header
/// laid out byte for byte, so the fixed field offsets are pinned, and read back
/// through a synthetic raw (2352) and cooked (2048) track so the sector-cooking
/// offset is exercised too.
/// </summary>
public class IpBinTests
{
    private static void PutAscii(byte[] buf, int at, int width, string value)
    {
        // Space-pad, as the real header does.
        for (int i = 0; i < width; i++) buf[at + i] = (byte)' ';
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, buf, at, Math.Min(bytes.Length, width));
    }

    private static byte[] BuildMeta(
        string hardware = "SEGA SEGAKATANA",
        string areas = "  E     ",
        string product = "T-8101N",
        string version = "V1.001",
        string date = "20000630",
        string boot = "1ST_READ.BIN",
        string maker = "SEGA ENTERPRISES",
        string title = "SONIC ADVENTURE")
    {
        var m = new byte[0x100];
        PutAscii(m, 0x00, 16, hardware);
        PutAscii(m, 0x10, 16, "SEGA ENTERPRISES");
        PutAscii(m, 0x20, 16, "GD-ROM1/1");
        PutAscii(m, 0x30, 8, areas);
        PutAscii(m, 0x38, 8, "E000F10");
        PutAscii(m, 0x40, 10, product);
        PutAscii(m, 0x4A, 6, version);
        PutAscii(m, 0x50, 16, date);
        PutAscii(m, 0x60, 16, boot);
        PutAscii(m, 0x70, 16, maker);
        PutAscii(m, 0x80, 128, title);
        return m;
    }

    // ---- parsing ------------------------------------------------------------

    [Fact]
    public void The_fixed_fields_are_read_at_their_offsets()
    {
        var header = IpBin.Parse(BuildMeta());

        Assert.StartsWith("SEGA SEGAKATANA", header.HardwareId);
        Assert.Equal("T-8101N", header.ProductNumber);
        Assert.Equal("V1.001", header.Version);
        Assert.Equal("20000630", header.ReleaseDate);
        Assert.Equal("1ST_READ.BIN", header.BootFile);
        Assert.Equal("SONIC ADVENTURE", header.Title);
    }

    [Fact]
    public void The_region_is_decoded_from_the_area_symbols()
    {
        Assert.Equal(new[] { "Europe" }, IpBin.Parse(BuildMeta(areas: "  E     ")).Regions);
        Assert.Equal(new[] { "Japan" }, IpBin.Parse(BuildMeta(areas: "J       ")).Regions);
        Assert.Equal(new[] { "Japan", "USA", "Europe" }, IpBin.Parse(BuildMeta(areas: "JUE     ")).Regions);
    }

    [Fact]
    public void A_multi_region_disc_reports_a_compact_region_code()
    {
        Assert.Equal("JUE", IpBin.Parse(BuildMeta(areas: "JUE     ")).RegionCode);
    }

    [Fact]
    public void A_disc_with_no_area_symbols_declares_no_region()
    {
        Assert.Empty(IpBin.Parse(BuildMeta(areas: "        ")).Regions);
    }

    [Fact]
    public void Fields_are_trimmed_of_padding()
    {
        var header = IpBin.Parse(BuildMeta(title: "CRAZY TAXI"));
        Assert.Equal("CRAZY TAXI", header.Title);   // no trailing spaces
    }

    // ---- refusals -----------------------------------------------------------

    [Fact]
    public void A_track_without_the_signature_is_refused()
    {
        var meta = BuildMeta(hardware: "NOT A DREAMCAST");
        var ex = Assert.Throws<IpBinFormatException>(() => IpBin.Parse(meta));
        Assert.Contains("Not a Dreamcast", ex.Message);
    }

    [Fact]
    public void A_buffer_too_short_is_refused()
    {
        Assert.Throws<IpBinFormatException>(() => IpBin.Parse(new byte[64]));
    }

    [Fact]
    public void IsBootHeader_recognises_the_signature()
    {
        Assert.True(IpBin.IsBootHeader(BuildMeta()));
        Assert.False(IpBin.IsBootHeader(BuildMeta(hardware: "SOMETHING ELSE")));
    }

    // ---- reading from a track ----------------------------------------------

    [Fact]
    public void The_header_reads_from_a_raw_2352_track_skipping_sync_and_header()
    {
        var meta = BuildMeta(title: "JET SET RADIO");
        // A raw Mode 1 sector: 16 bytes sync+header, then the user data.
        var sector = new byte[2352];
        Array.Copy(meta, 0, sector, 16, meta.Length);
        using var stream = new MemoryStream(sector);

        var track = new GdiTrack
        {
            Number = 3, StartLba = 45000, Type = GdiTrackType.Data,
            SectorSize = 2352, FileName = "track03.bin", Offset = 0,
        };

        var header = IpBin.ReadFromTrack(stream, track);
        Assert.Equal("JET SET RADIO", header.Title);
    }

    [Fact]
    public void The_header_reads_from_a_cooked_2048_track_at_offset_zero()
    {
        var meta = BuildMeta(title: "SHENMUE");
        var sector = new byte[2048];
        Array.Copy(meta, 0, sector, 0, meta.Length);
        using var stream = new MemoryStream(sector);

        var track = new GdiTrack
        {
            Number = 3, StartLba = 45000, Type = GdiTrackType.Data,
            SectorSize = 2048, FileName = "track03.iso", Offset = 0,
        };

        var header = IpBin.ReadFromTrack(stream, track);
        Assert.Equal("SHENMUE", header.Title);
    }

    [Fact]
    public void A_track_offset_is_honoured_when_reading()
    {
        var meta = BuildMeta(title: "POWER STONE");
        var sector = new byte[2048];
        Array.Copy(meta, 0, sector, 0, meta.Length);
        // Prepend 300 bytes of junk; the track's real data begins at offset 300.
        var file = new byte[300 + 2048];
        Array.Copy(sector, 0, file, 300, sector.Length);
        using var stream = new MemoryStream(file);

        var track = new GdiTrack
        {
            Number = 3, StartLba = 45000, Type = GdiTrackType.Data,
            SectorSize = 2048, FileName = "t.iso", Offset = 300,
        };

        Assert.Equal("POWER STONE", IpBin.ReadFromTrack(stream, track).Title);
    }
}
