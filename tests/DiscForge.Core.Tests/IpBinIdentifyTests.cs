// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;
using DiscForge.Core.Gdi;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Identifying a Dreamcast disc beyond a GD-ROM: decoding the IP.BIN peripherals
/// bitfield into human-readable capabilities, and reading the boot header out of a
/// MIL-CD BIN/CUE rip (skipping audio tracks, honouring the data track's sector
/// geometry). These exercise the parts that make "what disc is this" work for the
/// CD-ROM Dreamcast titles DiscForge now converts to CDI.
/// </summary>
public class IpBinIdentifyTests
{
    private static void PutAscii(byte[] buf, int at, int width, string value)
    {
        for (int i = 0; i < width; i++) buf[at + i] = (byte)' ';
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, buf, at, Math.Min(bytes.Length, width));
    }

    private static byte[] BuildMeta(string title, string peripherals = "0000000", string areas = "JUE     ")
    {
        var m = new byte[0x100];
        PutAscii(m, 0x00, 16, "SEGA SEGAKATANA");
        PutAscii(m, 0x10, 16, "SEGA ENTERPRISES");
        PutAscii(m, 0x20, 16, "GD-ROM1/1");
        PutAscii(m, 0x30, 8, areas);
        PutAscii(m, 0x38, 8, peripherals);
        PutAscii(m, 0x40, 10, "T-0000N");
        PutAscii(m, 0x4A, 6, "V1.000");
        PutAscii(m, 0x50, 16, "20000101");
        PutAscii(m, 0x60, 16, "1ST_READ.BIN");
        PutAscii(m, 0x70, 16, "SEGA ENTERPRISES");
        PutAscii(m, 0x80, 128, title);
        return m;
    }

    // ---- peripherals decode -------------------------------------------------

    [Fact]
    public void Peripherals_decode_the_set_bits_most_significant_first()
    {
        // bit 24 (standard controller), 8 (VMU), 6 (vibration), 1 (VGA), 0 (WinCE).
        uint bits = (1u << 24) | (1u << 8) | (1u << 6) | (1u << 1) | (1u << 0);
        var list = IpBin.DecodePeripherals(bits.ToString("X7"));

        Assert.Equal(new[]
        {
            "Standard controller (Start + A + B + directions)",
            "Memory card (VMU)",
            "Vibration pack",
            "VGA box",
            "Windows CE",
        }, list);
    }

    [Fact]
    public void Peripherals_from_the_header_property_match_the_field()
    {
        var header = IpBin.Parse(BuildMeta("IKARUGA", peripherals: "1000000"));   // bit 24 only
        Assert.Equal(new[] { "Standard controller (Start + A + B + directions)" }, header.SupportedPeripherals);
    }

    [Fact]
    public void An_empty_or_bad_peripherals_field_yields_no_capabilities()
    {
        Assert.Empty(IpBin.DecodePeripherals(""));
        Assert.Empty(IpBin.DecodePeripherals("   "));
        Assert.Empty(IpBin.DecodePeripherals("ZZZZ"));
    }

    // ---- reading from a MIL-CD bin/cue -------------------------------------

    [Fact]
    public void ReadFromBinCue_skips_audio_and_reads_the_data_track_header()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_ipbin_").FullName;
        try
        {
            // Track 1: two audio sectors. Track 2: a MODE1/2352 data track whose
            // first sector carries the boot header at the +16 user-data offset.
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[2 * 2352]);
            var meta = BuildMeta("REZ", peripherals: "1000100");   // controller + VMU
            var dataSector = new byte[2352];
            Array.Copy(meta, 0, dataSector, 16, meta.Length);
            File.WriteAllBytes(Path.Combine(dir, "d.bin"), dataSector);

            var cue =
                "FILE \"a.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n" +
                "FILE \"d.bin\" BINARY\n  TRACK 02 MODE1/2352\n    INDEX 01 00:00:00\n";
            File.WriteAllText(Path.Combine(dir, "game.cue"), cue);

            var header = IpBin.ReadFromBinCue(Path.Combine(dir, "game.cue"));
            Assert.NotNull(header);
            Assert.Equal("REZ", header!.Title);
            Assert.Equal(new[] { "Japan", "USA", "Europe" }, header.Regions);
            Assert.Contains("Memory card (VMU)", header.SupportedPeripherals);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadFromBinCue_reads_a_mode2_2352_data_track_at_offset_24()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_ipbin_").FullName;
        try
        {
            var meta = BuildMeta("CHUCHU ROCKET");
            var sector = new byte[2352];
            Array.Copy(meta, 0, sector, 24, meta.Length);   // Mode 2 form 1 user data at +24
            File.WriteAllBytes(Path.Combine(dir, "d.bin"), sector);
            File.WriteAllText(Path.Combine(dir, "g.cue"),
                "FILE \"d.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");

            Assert.Equal("CHUCHU ROCKET", IpBin.ReadFromBinCue(Path.Combine(dir, "g.cue"))?.Title);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadFromBinCue_returns_null_when_no_dreamcast_track_is_present()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_ipbin_").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "d.bin"), new byte[2352]);   // no signature
            File.WriteAllText(Path.Combine(dir, "g.cue"),
                "FILE \"d.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n");
            Assert.Null(IpBin.ReadFromBinCue(Path.Combine(dir, "g.cue")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- Identify: the shared any-format entry point ------------------------

    [Fact]
    public void Identify_reads_a_raw_cooked_data_track()
    {
        // A cooked 2048-byte image begins with the boot header at offset 0.
        string dir = Directory.CreateTempSubdirectory("dforge_ipbin_").FullName;
        try
        {
            string file = Path.Combine(dir, "game.iso");
            var img = new byte[4096];
            Array.Copy(BuildMeta("SONIC ADVENTURE"), img, 0x100);
            File.WriteAllBytes(file, img);

            var header = IpBin.Identify(file);
            Assert.NotNull(header);
            Assert.Equal("SONIC ADVENTURE", header!.Title);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Identify_returns_null_for_a_non_dreamcast_image()
    {
        string dir = Directory.CreateTempSubdirectory("dforge_ipbin_").FullName;
        try
        {
            string file = Path.Combine(dir, "not-dc.bin");
            File.WriteAllBytes(file, new byte[4096]);       // all zero — no signature
            Assert.Null(IpBin.Identify(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Identify_throws_for_a_missing_file()
    {
        Assert.Throws<FileNotFoundException>(() => IpBin.Identify("/no/such/dreamcast/image.gdi"));
    }
}
