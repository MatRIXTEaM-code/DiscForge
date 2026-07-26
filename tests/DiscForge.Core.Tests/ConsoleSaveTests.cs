// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Saves;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the GameCube, N64 and Saturn console-save readers. Each format is
/// hand-built with known values — GameCube directory entries + BAT chains, an N64
/// Controller Pak with a checksummed ID block and note table, and a Saturn backup
/// with the "BackUpRam Format" signature and directory entries — and read back.
/// </summary>
public class ConsoleSaveTests
{
    // ---- GameCube ----------------------------------------------------------

    private const int GcBlock = 0x2000;
    private const int GcEntry = 0x40;

    private static void WriteEntry(byte[] buf, int at, string game, string maker, string name,
                                   int firstBlock, int blockCount, uint commentOffset)
    {
        Encoding.ASCII.GetBytes(game).CopyTo(buf, at + 0x00);
        Encoding.ASCII.GetBytes(maker).CopyTo(buf, at + 0x04);
        buf[at + 0x06] = 0xFF;
        Encoding.ASCII.GetBytes(name).CopyTo(buf, at + 0x08);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(at + 0x36), (ushort)firstBlock);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(at + 0x38), (ushort)blockCount);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 0x3C), commentOffset);
    }

    private static byte[] BuildGci(string game, string maker, string name)
    {
        var gci = new byte[GcEntry + GcBlock];
        WriteEntry(gci, 0, game, maker, name, firstBlock: 0, blockCount: 1, commentOffset: 0);
        Encoding.ASCII.GetBytes("HELLO WORLD").CopyTo(gci, GcEntry);   // comment line 1 at payload[0]
        gci[GcEntry + 0x100] = 0x5A;                                   // a payload marker
        return gci;
    }

    // A 64-block (512 KB) card: header(0) directory(1) backup(2) BAT(3) BAT-backup(4) data(5+).
    private static byte[] BuildCard()
    {
        var card = new byte[64 * GcBlock];
        // Blank the directory to the 0xFF "unused" pattern, then fill 2 entries.
        for (int i = 0; i < GcBlock; i++) card[GcBlock + i] = 0xFF;

        WriteEntry(card, GcBlock + 0 * GcEntry, "GAEA", "01", "SAVE-ALPHA", firstBlock: 5, blockCount: 1, commentOffset: 0);
        WriteEntry(card, GcBlock + 1 * GcEntry, "GBEB", "8P", "SAVE-BETA", firstBlock: 6, blockCount: 2, commentOffset: 0);

        // BAT map (block 3, offset 0x0A): one u16 per data block, 5-based.
        int map = GcBlock * 3 + 0x0A;
        BinaryPrimitives.WriteUInt16BigEndian(card.AsSpan(map + 0 * 2), 0xFFFF);   // block 5 -> end (save A)
        BinaryPrimitives.WriteUInt16BigEndian(card.AsSpan(map + 1 * 2), 7);        // block 6 -> block 7 (save B)
        BinaryPrimitives.WriteUInt16BigEndian(card.AsSpan(map + 2 * 2), 0xFFFF);   // block 7 -> end

        // Payloads.
        Encoding.ASCII.GetBytes("ALPHA COMMENT").CopyTo(card, 5 * GcBlock);
        card[5 * GcBlock + 0x40] = 0xA1;
        Encoding.ASCII.GetBytes("BETA COMMENT").CopyTo(card, 6 * GcBlock);
        card[6 * GcBlock + 0x40] = 0xB1;
        card[7 * GcBlock + 0x50] = 0xB2;
        return card;
    }

    [Fact]
    public void Gci_single_save_reads_its_fields()
    {
        var save = GciReader.Read(BuildGci("GALE", "01", "MELEE-DATA"));
        Assert.Equal("GALE", save.GameCode);
        Assert.Equal("01", save.Maker);
        Assert.Equal("MELEE-DATA", save.FileName);
        Assert.Equal(1, save.BlockCount);
        Assert.Equal("HELLO WORLD", save.Comment);
    }

    [Fact]
    public void Gci_payload_extracts_intact()
    {
        var gci = BuildGci("GALE", "01", "MELEE-DATA");
        var payload = GciReader.Payload(gci);
        Assert.Equal(GcBlock, payload.Length);
        Assert.Equal(0x5A, payload[0x100]);
    }

    [Fact]
    public void Card_enumerates_two_saves_with_names_and_block_counts()
    {
        var card = GcMemoryCardReader.Read(BuildCard());
        Assert.Equal(2, card.Saves.Count);

        var a = card.Saves[0];
        Assert.Equal("GAEA", a.GameCode);
        Assert.Equal("SAVE-ALPHA", a.FileName);
        Assert.Equal(1, a.BlockCount);
        Assert.Equal("ALPHA COMMENT", a.Comment);

        var b = card.Saves[1];
        Assert.Equal("SAVE-BETA", b.FileName);
        Assert.Equal(2, b.BlockCount);
    }

    [Fact]
    public void Card_extract_to_gci_reproduces_a_valid_gci_with_matching_payload()
    {
        var raw = BuildCard();
        var card = GcMemoryCardReader.Read(raw);
        var b = card.Saves[1];   // the 2-block save

        var gci = GcMemoryCardReader.ExtractSaveToGci(raw, b);
        Assert.True(GciReader.IsGci(gci));

        var reread = GciReader.Read(gci);
        Assert.Equal("SAVE-BETA", reread.FileName);
        Assert.Equal(2, reread.BlockCount);

        var payload = GciReader.Payload(gci);
        Assert.Equal(2 * GcBlock, payload.Length);
        Assert.Equal(0xB1, payload[0x40]);            // from data block 6
        Assert.Equal(0xB2, payload[GcBlock + 0x50]);  // from data block 7
    }

    [Fact]
    public void IsGci_is_true_for_a_gci_and_false_for_a_card()
    {
        Assert.True(GciReader.IsGci(BuildGci("GALE", "01", "X")));
        Assert.False(GciReader.IsGci(BuildCard()));
        Assert.False(GciReader.IsGci(new byte[10]));
    }

    [Fact]
    public void IsGcMemoryCard_accepts_a_card_and_rejects_odd_sizes()
    {
        Assert.True(GcMemoryCardReader.IsGcMemoryCard(BuildCard()));
        Assert.False(GcMemoryCardReader.IsGcMemoryCard(new byte[GcBlock * 3]));   // not power of two
        Assert.False(GcMemoryCardReader.IsGcMemoryCard(new byte[1234]));
    }

    [Fact]
    public void Card_read_rejects_a_bad_size()
    {
        Assert.Throws<GcSaveFormatException>(() => GcMemoryCardReader.Read(new byte[GcBlock * 3]));
    }

    // ---- N64 ---------------------------------------------------------------

    [Theory]
    [InlineData(512, N64SaveType.Eeprom4k)]
    [InlineData(2048, N64SaveType.Eeprom16k)]
    [InlineData(32768, N64SaveType.Sram)]
    [InlineData(131072, N64SaveType.FlashRam)]
    [InlineData(999, N64SaveType.Unknown)]
    public void N64_identify_by_size(long length, N64SaveType expected)
    {
        Assert.Equal(expected, N64SaveReader.IdentifyBySize(length));
    }

    private static byte ToFont(char ch) => ch switch
    {
        ' ' => 0x0F,
        >= '0' and <= '9' => (byte)(0x10 + (ch - '0')),
        >= 'A' and <= 'Z' => (byte)(0x1A + (ch - 'A')),
        >= 'a' and <= 'z' => (byte)(0x34 + (ch - 'a')),
        _ => 0x00,
    };

    private static void WriteIdBlock(byte[] pak, int off)
    {
        for (int i = 0; i < 0x1C; i++) pak[off + i] = (byte)(i + 1);   // arbitrary serial
        uint sum = 0;
        for (int i = 0; i < 14; i++) sum += BinaryPrimitives.ReadUInt16BigEndian(pak.AsSpan(off + i * 2));
        BinaryPrimitives.WriteUInt16BigEndian(pak.AsSpan(off + 0x1C), (ushort)(sum & 0xFFFF));
        BinaryPrimitives.WriteUInt16BigEndian(pak.AsSpan(off + 0x1E), (ushort)((0xFFF2 - sum) & 0xFFFF));
    }

    private static void WriteNote(byte[] pak, int index, string game, string pub, int startPage, string name)
    {
        int at = 0x300 + index * 0x20;
        Encoding.ASCII.GetBytes(game).CopyTo(pak, at + 0x00);
        Encoding.ASCII.GetBytes(pub).CopyTo(pak, at + 0x04);
        BinaryPrimitives.WriteUInt16BigEndian(pak.AsSpan(at + 0x06), (ushort)startPage);
        for (int i = 0; i < name.Length && i < 16; i++) pak[at + 0x10 + i] = ToFont(name[i]);
    }

    private static byte[] BuildControllerPak()
    {
        var pak = new byte[32 * 1024];
        WriteIdBlock(pak, 0x20);
        WriteNote(pak, 0, "NSME", "01", startPage: 5, name: "MARIO 64");
        WriteNote(pak, 1, "NZLE", "01", startPage: 10, name: "ZELDA OOT");
        return pak;
    }

    [Fact]
    public void ControllerPak_enumerates_notes_with_game_codes_and_names()
    {
        var pak = N64ControllerPak.Read(BuildControllerPak());
        Assert.Equal(2, pak.Notes.Count);

        Assert.Equal("NSME", pak.Notes[0].GameCode);
        Assert.Equal("MARIO 64", pak.Notes[0].Name);
        Assert.Equal(5, pak.Notes[0].StartPage);

        Assert.Equal("NZLE", pak.Notes[1].GameCode);
        Assert.Equal("ZELDA OOT", pak.Notes[1].Name);
    }

    [Fact]
    public void IsControllerPak_is_true_for_a_valid_pak_and_false_otherwise()
    {
        Assert.True(N64ControllerPak.IsControllerPak(BuildControllerPak()));
        Assert.False(N64ControllerPak.IsControllerPak(new byte[32 * 1024]));   // no valid ID block
        Assert.False(N64ControllerPak.IsControllerPak(new byte[512]));         // wrong size
    }

    [Fact]
    public void ControllerPak_read_rejects_a_non_pak()
    {
        Assert.Throws<N64SaveFormatException>(() => N64ControllerPak.Read(new byte[32 * 1024]));
        Assert.Throws<N64SaveFormatException>(() => N64ControllerPak.Read(new byte[10]));
    }

    // ---- Saturn ------------------------------------------------------------

    private static void WriteSaturnEntry(byte[] buf, int block, string name, string comment,
                                         byte lang, uint dataSize)
    {
        int at = block * SaturnSaveReader.BlockSize;
        buf[at] = 0x80;   // occupied tag
        Encoding.ASCII.GetBytes(name).CopyTo(buf, at + 0x04);
        buf[at + 0x0F] = lang;
        Encoding.ASCII.GetBytes(comment).CopyTo(buf, at + 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 0x1E), dataSize);
    }

    private static byte[] BuildSaturnBackup()
    {
        var buf = new byte[32 * 1024];
        var sig = Encoding.ASCII.GetBytes(SaturnSaveReader.Signature);
        for (int copy = 0; copy * sig.Length + sig.Length <= SaturnSaveReader.BlockSize; copy++)
            sig.CopyTo(buf, copy * sig.Length);   // signature repeated across block 0

        WriteSaturnEntry(buf, block: 1, "SONIC_R1", "Round 1", lang: 1, dataSize: 2048);
        WriteSaturnEntry(buf, block: 2, "NIGHTS01", "Dream", lang: 0, dataSize: 4096);
        return buf;
    }

    [Fact]
    public void Saturn_signature_is_detected()
    {
        Assert.True(SaturnSaveReader.IsSaturnBackup(BuildSaturnBackup()));
        Assert.False(SaturnSaveReader.IsSaturnBackup(new byte[32 * 1024]));
    }

    [Fact]
    public void Saturn_enumerates_saves_with_names_comments_and_sizes()
    {
        var backup = SaturnSaveReader.Read(BuildSaturnBackup());
        Assert.Equal(2, backup.Saves.Count);

        var s0 = backup.Saves[0];
        Assert.Equal("SONIC_R1", s0.Name);
        Assert.Equal("Round 1", s0.Comment);
        Assert.Equal("English", s0.Language);
        Assert.Equal(2048, s0.DataSize);

        var s1 = backup.Saves[1];
        Assert.Equal("NIGHTS01", s1.Name);
        Assert.Equal("Japanese", s1.Language);
        Assert.Equal(4096, s1.DataSize);
    }

    [Fact]
    public void Saturn_read_rejects_missing_signature_and_short_data()
    {
        Assert.Throws<SaturnSaveFormatException>(() => SaturnSaveReader.Read(new byte[32 * 1024]));
        Assert.Throws<SaturnSaveFormatException>(() => SaturnSaveReader.Read(new byte[8]));
    }
}
