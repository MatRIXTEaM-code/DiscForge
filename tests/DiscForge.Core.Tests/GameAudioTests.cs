// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DiscForge.Core.GameAudio;
using DiscForge.Core.Identify;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Hand-built fixtures for the game-audio metadata/structure readers (PSF/SPC/
/// VGM/NSF). Every file is assembled byte-by-byte with known values; the readers
/// parse headers and tags only — no audio is decoded, so the "program" payloads
/// here are arbitrary compressed bytes that are never inflated.
/// </summary>
public class GameAudioTests
{
    // ---- PSF -------------------------------------------------------------

    private static byte[] BuildPsf(byte version, byte[] reserved, byte[] program, string? tagBlock)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)'P'); ms.WriteByte((byte)'S'); ms.WriteByte((byte)'F');
        ms.WriteByte(version);
        var u32 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)reserved.Length); ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)program.Length); ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0xDEADBEEF); ms.Write(u32);   // program CRC32
        ms.Write(reserved);
        ms.Write(program);
        if (tagBlock is not null)
        {
            ms.Write(Encoding.ASCII.GetBytes("[TAG]"));
            ms.Write(Encoding.UTF8.GetBytes(tagBlock));
        }
        return ms.ToArray();
    }

    private static byte[] TinyZlib(string text)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(Encoding.ASCII.GetBytes(text));
        return ms.ToArray();
    }

    [Fact]
    public void Psf1_parses_system_crc_and_tags()
    {
        var reserved = new byte[] { 1, 2, 3, 4 };
        var program = TinyZlib("fake exe payload — never decoded");
        var tags = "title=Aeris Theme\ngame=Final Fantasy VII\nartist=Nobuo Uematsu\nlength=2:53\n";
        var psf = BuildPsf(0x01, reserved, program, tags);

        var file = PsfReader.Read(psf);
        Assert.Equal("PlayStation", file.SystemName);
        Assert.Equal((byte)0x01, file.PsfVersion);
        Assert.Equal(0xDEADBEEFu, file.ProgramCrc32);
        Assert.True(file.HasProgram);
        Assert.Equal("Aeris Theme", file.Title);
        Assert.Equal("Final Fantasy VII", file.Game);
        Assert.Equal("Nobuo Uematsu", file.Artist);
        Assert.Equal("2:53", file.Length);
    }

    [Fact]
    public void Psf2_version_maps_to_playstation_2()
    {
        var psf = BuildPsf(0x02, new byte[2], TinyZlib("x"), "title=Demo\n");
        var file = PsfReader.Read(psf);
        Assert.Equal("PlayStation 2", file.SystemName);
        Assert.Equal("Demo", file.Title);
    }

    [Fact]
    public void Psf_tag_keys_are_case_insensitive_and_trimmed()
    {
        var psf = BuildPsf(0x22, new byte[0], new byte[0], "  TITLE  =  Spaced Out  \n");
        var file = PsfReader.Read(psf);
        Assert.Equal("Game Boy Advance", file.SystemName);
        Assert.Equal("Spaced Out", file.Title);
    }

    [Fact]
    public void Psf_without_tag_block_has_empty_tags()
    {
        var psf = BuildPsf(0x01, new byte[4], TinyZlib("y"), null);
        var file = PsfReader.Read(psf);
        Assert.Empty(file.Tags);
        Assert.Null(file.Title);
    }

    [Fact]
    public void IsPsf_positive_and_negative()
    {
        var psf = BuildPsf(0x01, new byte[1], new byte[1], null);
        Assert.True(PsfReader.IsPsf(psf));
        Assert.False(PsfReader.IsPsf(new byte[] { (byte)'P', (byte)'S', (byte)'F', 0x99 })); // unknown version
        Assert.False(PsfReader.IsPsf(new byte[] { (byte)'N', (byte)'O', (byte)'P', 0x01 }));
    }

    [Fact]
    public void Psf_short_file_throws()
    {
        Assert.Throws<GameAudioFormatException>(() => PsfReader.Read(new byte[] { (byte)'P', (byte)'S' }));
    }

    // ---- SPC -------------------------------------------------------------

    private const string SpcMagic = "SNES-SPC700 Sound File Data v0.30";

    private static byte[] BuildSpc(bool hasTag, string song, string game, string artist,
                                   string date, bool textFormat)
    {
        var d = new byte[0x100];
        Encoding.ASCII.GetBytes(SpcMagic).CopyTo(d, 0);
        d[0x21] = 0x1A; d[0x22] = 0x1A;
        d[0x23] = (byte)(hasTag ? 0x1A : 0x1B);
        d[0x24] = 30;   // version minor

        void Put(int at, string s, int max)
        {
            var b = Encoding.Latin1.GetBytes(s);
            Array.Copy(b, 0, d, at, Math.Min(b.Length, max));
        }

        Put(0x2E, song, 32);
        Put(0x4E, game, 32);
        Put(0x7E, "dumped for test", 32);   // comments
        Put(0x6E, "TestDumper", 16);
        if (textFormat)
        {
            Put(0x9E, date, 11);            // ASCII date -> text sub-format
            Put(0xB1, artist, 32);
        }
        else
        {
            // Binary sub-format: raw date bytes at 0x9E, artist at 0xB0.
            d[0x9E] = 23; d[0x9F] = 5; d[0xA0] = 0xD0; d[0xA1] = 0x07;
            Put(0xB0, artist, 32);
        }
        return d;
    }

    [Fact]
    public void Spc_text_id666_strings_parse()
    {
        var spc = BuildSpc(hasTag: true, "Corridor", "Chrono Trigger", "Yasunori Mitsuda",
                           "05/23/1995", textFormat: true);
        var file = SpcReader.Read(spc);
        Assert.True(file.HasId666);
        Assert.True(file.TextFormatTag);
        Assert.Equal("Corridor", file.SongTitle);
        Assert.Equal("Chrono Trigger", file.GameTitle);
        Assert.Equal("Yasunori Mitsuda", file.Artist);
        Assert.Equal("dumped for test", file.Comments);
        Assert.Equal("TestDumper", file.DumperName);
        Assert.Equal("05/23/1995", file.DumpDate);
    }

    [Fact]
    public void Spc_binary_id666_reads_artist_at_alternate_offset()
    {
        var spc = BuildSpc(hasTag: true, "Boss", "Contra", "Konami Kukeiha Club",
                           "", textFormat: false);
        var file = SpcReader.Read(spc);
        Assert.True(file.HasId666);
        Assert.False(file.TextFormatTag);
        Assert.Equal("Boss", file.SongTitle);
        Assert.Equal("Contra", file.GameTitle);
        Assert.Equal("Konami Kukeiha Club", file.Artist);
    }

    [Fact]
    public void Spc_no_tag_flag_is_reported()
    {
        var spc = BuildSpc(hasTag: false, "X", "Y", "Z", "01/01/2000", textFormat: true);
        var file = SpcReader.Read(spc);
        Assert.False(file.HasId666);
    }

    [Fact]
    public void IsSpc_positive_and_negative()
    {
        var spc = BuildSpc(true, "a", "b", "c", "01/01/2000", true);
        Assert.True(SpcReader.IsSpc(spc));
        Assert.False(SpcReader.IsSpc(Encoding.ASCII.GetBytes("NOT-AN-SPC-FILE................")));
    }

    [Fact]
    public void Spc_short_file_throws()
    {
        Assert.Throws<GameAudioFormatException>(() => SpcReader.Read(new byte[4]));
    }

    // ---- VGM -------------------------------------------------------------

    private static byte[] BuildVgm(uint versionBcd, uint totalSamples,
                                   (int off, uint clock)[] chips, byte[]? gd3)
    {
        // Header 0x40 + optional GD3 appended after the (empty) data stream.
        int headerLen = 0x40;
        int dataLen = 4;   // a tiny placeholder "data" region (never executed)
        int gd3Len = gd3?.Length ?? 0;
        var d = new byte[headerLen + dataLen + gd3Len];

        Encoding.ASCII.GetBytes("Vgm ").CopyTo(d, 0);
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at, 4), v);

        U32(0x08, versionBcd);
        U32(0x18, totalSamples);
        // VGM data offset (relative to 0x34) points just past the header.
        U32(0x34, (uint)(headerLen - 0x34));
        foreach (var (off, clock) in chips) U32(off, clock);

        if (gd3 is not null)
        {
            int gd3At = headerLen + dataLen;
            U32(0x14, (uint)(gd3At - 0x14));   // gd3_offset relative to 0x14
            gd3.CopyTo(d, gd3At);
        }
        return d;
    }

    private static byte[] BuildGd3(params string[] fields)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("Gd3 "));
        var u32 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0x00000100); ms.Write(u32);   // version
        using var body = new MemoryStream();
        foreach (var f in fields)
        {
            body.Write(Encoding.Unicode.GetBytes(f));
            body.WriteByte(0); body.WriteByte(0);   // UTF-16 NUL terminator
        }
        var bodyBytes = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)bodyBytes.Length); ms.Write(u32);   // length
        ms.Write(bodyBytes);
        return ms.ToArray();
    }

    [Fact]
    public void Vgm_version_samples_and_duration()
    {
        var vgm = BuildVgm(0x00000161, 88200, new[] { (0x2C, 7670454u) }, null);
        var file = VgmReader.Read(vgm);
        Assert.Equal("1.61", file.Version);
        Assert.Equal(88200u, file.TotalSamples);
        Assert.Equal(2.0, file.DurationSeconds, 3);
    }

    [Fact]
    public void Vgm_reports_chips_by_nonzero_clock()
    {
        var vgm = BuildVgm(0x00000161, 44100,
            new[] { (0x0C, 3579545u), (0x2C, 7670454u), (0x30, 3579545u) }, null);
        var file = VgmReader.Read(vgm);
        Assert.Contains("SN76489", file.Chips);
        Assert.Contains("YM2612", file.Chips);
        Assert.Contains("YM2151", file.Chips);
        Assert.DoesNotContain("YM2413", file.Chips);
    }

    [Fact]
    public void Vgm_gd3_utf16_strings_parse_english_preferred()
    {
        var gd3 = BuildGd3(
            "Green Hill Zone", "グリーンヒル",   // track EN/JP
            "Sonic the Hedgehog", "ソニック",     // game EN/JP
            "Sega Mega Drive", "",                 // system EN/JP
            "Masato Nakamura", "",                 // author EN/JP
            "1991", "DiscForge", "test notes");    // date, vgm-by, notes
        var vgm = BuildVgm(0x00000150, 44100, new[] { (0x0C, 3579545u) }, gd3);
        var file = VgmReader.Read(vgm);
        Assert.Equal("Green Hill Zone", file.Tags.TrackName);
        Assert.Equal("Sonic the Hedgehog", file.Tags.GameName);
        Assert.Equal("Sega Mega Drive", file.Tags.System);
        Assert.Equal("Masato Nakamura", file.Tags.Author);
        Assert.Equal("1991", file.Tags.Date);
        Assert.Equal("test notes", file.Tags.Notes);
    }

    [Fact]
    public void IsVgm_positive_and_negative()
    {
        var vgm = BuildVgm(0x00000161, 1, Array.Empty<(int, uint)>(), null);
        Assert.True(VgmReader.IsVgm(vgm));
        Assert.False(VgmReader.IsVgm(new byte[] { (byte)'V', (byte)'G', (byte)'M', (byte)'!' }));
    }

    [Fact]
    public void Vgm_short_file_throws()
    {
        Assert.Throws<GameAudioFormatException>(() => VgmReader.Read(new byte[16]));
    }

    // ---- NSF -------------------------------------------------------------

    private static byte[] BuildNsf(int songs, int startingSong, string name, string artist,
                                   string copyright, bool pal, byte expansion)
    {
        var d = new byte[0x80];
        Encoding.ASCII.GetBytes("NESM").CopyTo(d, 0);
        d[0x04] = 0x1A;
        d[0x05] = 1;                    // version
        d[0x06] = (byte)songs;
        d[0x07] = (byte)startingSong;
        Encoding.Latin1.GetBytes(name).CopyTo(d, 0x0E);
        Encoding.Latin1.GetBytes(artist).CopyTo(d, 0x2E);
        Encoding.Latin1.GetBytes(copyright).CopyTo(d, 0x4E);
        d[0x7A] = (byte)(pal ? 0x01 : 0x00);
        d[0x7B] = expansion;
        return d;
    }

    [Fact]
    public void Nsf_header_fields_and_expansion_chip()
    {
        var nsf = BuildNsf(2, 1, "Mega Man 2", "Takashi Tateishi", "1988 Capcom",
                           pal: false, expansion: 0x01 /* VRC6 */);
        var file = NsfReader.Read(nsf);
        Assert.Equal(1, file.Version);
        Assert.Equal(2, file.TotalSongs);
        Assert.Equal(1, file.StartingSong);
        Assert.Equal("Mega Man 2", file.SongName);
        Assert.Equal("Takashi Tateishi", file.Artist);
        Assert.Equal("1988 Capcom", file.Copyright);
        Assert.False(file.IsPal);
        Assert.Contains("VRC6", file.ExpansionChips);
        Assert.DoesNotContain("FDS", file.ExpansionChips);
    }

    [Fact]
    public void Nsf_pal_flag_and_multiple_expansion_chips()
    {
        var nsf = BuildNsf(1, 1, "Tune", "Someone", "2020",
                           pal: true, expansion: 0b0010_1100 /* FDS | MMC5 | Sunsoft 5B */);
        var file = NsfReader.Read(nsf);
        Assert.True(file.IsPal);
        Assert.Contains("FDS", file.ExpansionChips);
        Assert.Contains("MMC5", file.ExpansionChips);
        Assert.Contains("Sunsoft 5B", file.ExpansionChips);
        Assert.DoesNotContain("VRC6", file.ExpansionChips);
    }

    [Fact]
    public void IsNsf_positive_negative_and_nsfe_detection()
    {
        var nsf = BuildNsf(1, 1, "a", "b", "c", false, 0);
        Assert.True(NsfReader.IsNsf(nsf));
        Assert.False(NsfReader.IsNsf(Encoding.ASCII.GetBytes("NESX\x1a...")));
        Assert.True(NsfReader.IsNsfe(Encoding.ASCII.GetBytes("NSFE....")));
        Assert.Throws<GameAudioFormatException>(() => NsfReader.Read(Encoding.ASCII.GetBytes("NSFE....")));
    }

    [Fact]
    public void Nsf_short_file_throws()
    {
        Assert.Throws<GameAudioFormatException>(() => NsfReader.Read(new byte[] { (byte)'N', (byte)'E', (byte)'S', (byte)'M', 0x1A }));
    }

    // ---- FormatIdentifier hooks -----------------------------------------

    [Fact]
    public void FormatIdentifier_names_the_four_game_audio_formats()
    {
        var psf = BuildPsf(0x01, new byte[1], new byte[1], null);
        var spc = BuildSpc(true, "a", "b", "c", "01/01/2000", true);
        var vgm = BuildVgm(0x00000161, 1, Array.Empty<(int, uint)>(), null);
        var nsf = BuildNsf(1, 1, "a", "b", "c", false, 0);

        Assert.Equal("PSF", FormatIdentifier.Identify(psf).Name);
        Assert.Equal("SPC", FormatIdentifier.Identify(spc).Name);
        Assert.Equal("VGM", FormatIdentifier.Identify(vgm).Name);
        Assert.Equal("NSF", FormatIdentifier.Identify(nsf).Name);
    }

    [Fact]
    public void FormatIdentifier_does_not_confuse_nsf_with_nes_cartridge()
    {
        // "NES\x1A" is an iNES cartridge, not the "NESM\x1A" NSF sound format.
        var ines = new byte[16];
        Encoding.ASCII.GetBytes("NES").CopyTo(ines, 0);
        ines[3] = 0x1A;
        Assert.Equal("NES", FormatIdentifier.Identify(ines).Name);
    }
}
