// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.PlayStation;
using DiscForge.Core.Util;
using Xunit;

namespace DiscForge.Core.Tests;

public class PsExeTests
{
    private static byte[] BuildExe(uint entry, uint loadAddr, uint textSize, string marker, int payload)
    {
        var exe = new byte[PsExe.HeaderSize + payload];
        "PS-X EXE"u8.ToArray().CopyTo(exe, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x10), entry);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x18), loadAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x1C), textSize);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x30), 0x801FFF00);
        Encoding.ASCII.GetBytes(marker).CopyTo(exe, 0x4C);
        return exe;
    }

    [Fact]
    public void The_header_fields_are_read_little_endian()
    {
        var exe = BuildExe(0x80010000, 0x80010000, 0x800, "Sony Computer Entertainment Inc. for Europe area", 0x800);
        var h = PsExe.ReadHeader(exe);

        Assert.Equal(0x80010000u, h.EntryPoint);
        Assert.Equal(0x80010000u, h.LoadAddress);
        Assert.Equal(0x800u, h.TextSize);
        Assert.Equal(0x801FFF00u, h.StackBase);
        Assert.Contains("Europe", h.RegionMarker);
    }

    [Fact]
    public void A_file_without_the_signature_is_refused()
    {
        var notExe = new byte[PsExe.HeaderSize];
        Assert.False(PsExe.IsPsExe(notExe));
        Assert.Throws<PsExeFormatException>(() => PsExe.ReadHeader(notExe));
    }

    [Fact]
    public void A_short_file_is_refused()
    {
        Assert.Throws<PsExeFormatException>(() => PsExe.ReadHeader("PS-X EXE"u8.ToArray()));
    }
}

public class PsxPaddingTests
{
    [Fact]
    public void Padding_rounds_up_to_the_next_multiple()
    {
        var padded = PsxPadding.PadToMultiple(new byte[100], 2048);
        Assert.Equal(2048, padded.Length);
    }

    [Fact]
    public void Already_aligned_data_keeps_its_length()
    {
        var padded = PsxPadding.PadToMultiple(new byte[4096], 2048);
        Assert.Equal(4096, padded.Length);
    }

    [Fact]
    public void The_original_bytes_are_preserved_and_the_tail_is_the_fill()
    {
        var data = new byte[] { 1, 2, 3 };
        var padded = PsxPadding.PadToMultiple(data, 8, fill: 0xFF);
        Assert.Equal(8, padded.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, padded);
    }

    [Fact]
    public void Padding_a_ps_exe_updates_t_size_to_the_padded_payload()
    {
        var exe = new byte[PsExe.HeaderSize + 0x500];   // payload not a 0x800 multiple
        "PS-X EXE"u8.ToArray().CopyTo(exe, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x1C), 0x500);

        var padded = PsxPadding.PadPsExe(exe);

        Assert.Equal(PsExe.HeaderSize + 0x800, padded.Length);       // payload padded to 0x800
        Assert.Equal(0x800u, BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(0x1C, 4)));
    }
}

public class BinToSourceTests
{
    [Fact]
    public void A_c_array_names_sizes_and_hex_encodes_the_bytes()
    {
        string c = BinToSource.ToCArray(new byte[] { 0x00, 0xFF, 0x10 }, "boot", perLine: 8);
        Assert.Contains("unsigned char boot[3]", c);
        Assert.Contains("0x00", c);
        Assert.Contains("0xff", c);
        Assert.Contains("0x10", c);
    }

    [Fact]
    public void An_invalid_identifier_is_sanitised()
    {
        string c = BinToSource.ToCArray(new byte[] { 1 }, "9 bad-name");
        Assert.Contains("_9_bad_name", c);
    }

    [Fact]
    public void The_asm_form_emits_byte_rows_under_a_label()
    {
        string a = BinToSource.ToAsm(new byte[] { 1, 2, 3 }, "tbl", perLine: 2);
        Assert.Contains("tbl:", a);
        Assert.Contains(".byte 0x01, 0x02", a);
        Assert.Contains(".byte 0x03", a);
    }
}

public class ByteSearchTests
{
    [Fact]
    public void All_offsets_of_a_pattern_are_found()
    {
        var hay = Encoding.ASCII.GetBytes("abXYabXYab");
        var hits = ByteSearch.FindAll(hay, Encoding.ASCII.GetBytes("ab"));
        Assert.Equal(new long[] { 0, 4, 8 }, hits.ToArray());
    }

    [Fact]
    public void Overlapping_matches_are_reported()
    {
        var hits = ByteSearch.FindAll(new byte[] { 0xAA, 0xAA, 0xAA }, new byte[] { 0xAA, 0xAA });
        Assert.Equal(new long[] { 0, 1 }, hits.ToArray());
    }

    [Fact]
    public void Hex_patterns_parse_with_or_without_spaces()
    {
        Assert.Equal(new byte[] { 0x4D, 0x5A }, ByteSearch.ParseHex("4d5a"));
        Assert.Equal(new byte[] { 0x4D, 0x5A }, ByteSearch.ParseHex("4D 5A"));
        Assert.Throws<FormatException>(() => ByteSearch.ParseHex("4d5"));
    }

    [Fact]
    public void A_match_straddling_a_chunk_boundary_is_found()
    {
        // Needle spans the seam between two 4096-byte chunks.
        var data = new byte[9000];
        var needle = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        int at = 4094;   // straddles the first boundary at 4096
        needle.CopyTo(data, at);
        needle.CopyTo(data, 8000);

        using var ms = new MemoryStream(data);
        var hits = ByteSearch.FindAll(ms, needle, chunkSize: 4096);
        Assert.Equal(new long[] { at, 8000 }, hits.ToArray());
    }
}
