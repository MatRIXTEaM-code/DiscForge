// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Ciso;
using DiscForge.Core.Iso;
using DiscForge.Core.Psp;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for PSP UMD reading. The priority is the PARAM.SFO parser, pinned with a
/// hand-built buffer laid out exactly per the format (header, index table, key
/// table, data table) covering strings, a trailing-NUL string, and a uint32 —
/// plus malformed cases. The reader is exercised end to end against a real ISO
/// 9660 image built with <see cref="IsoBuilder"/> holding /PSP_GAME/PARAM.SFO,
/// both plain and CSO-compressed, and the "no PARAM.SFO" failure path.
/// </summary>
public class PspTests
{
    // ---- a PARAM.SFO builder that mirrors the format the parser reads --------

    private enum Fmt { StringZ, StringSpecial, Uint32 }

    private sealed record Kv(string Key, Fmt Format, string? Text = null, uint Number = 0);

    /// <summary>Hand-build a valid PARAM.SFO blob from the given entries, laying out
    /// the header, 16-byte index records, NUL-terminated key table, and data table
    /// with correct relative offsets — exactly as the parser expects to read.</summary>
    private static byte[] BuildSfo(params Kv[] entries)
    {
        // Encode each value's bytes.
        var valueBytes = new List<byte[]>();
        var maxLens = new List<int>();
        foreach (var e in entries)
        {
            if (e.Format == Fmt.Uint32)
            {
                var b = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(b, e.Number);
                valueBytes.Add(b);
                maxLens.Add(4);
            }
            else
            {
                var raw = Encoding.UTF8.GetBytes(e.Text ?? "");
                // NUL-terminated string carries a trailing NUL within its used length.
                var used = e.Format == Fmt.StringZ ? raw.Concat(new byte[] { 0 }).ToArray() : raw;
                valueBytes.Add(used);
                // Give a slightly rounded-up max length to prove trimming is by used length.
                int max = ((used.Length + 3) / 4) * 4;
                if (max < used.Length) max = used.Length;
                maxLens.Add(max);
            }
        }

        // Key table: NUL-terminated ASCII keys, back to back. Record each offset.
        var keyTable = new List<byte>();
        var keyOffsets = new List<int>();
        foreach (var e in entries)
        {
            keyOffsets.Add(keyTable.Count);
            keyTable.AddRange(Encoding.ASCII.GetBytes(e.Key));
            keyTable.Add(0);
        }
        // Pad the key table to a 4-byte boundary (as real SFOs do).
        while (keyTable.Count % 4 != 0) keyTable.Add(0);

        // Data table: each value at its (reserved) max length. Record offsets.
        var dataTable = new List<byte>();
        var dataOffsets = new List<int>();
        for (int i = 0; i < entries.Length; i++)
        {
            dataOffsets.Add(dataTable.Count);
            dataTable.AddRange(valueBytes[i]);
            for (int pad = valueBytes[i].Length; pad < maxLens[i]; pad++) dataTable.Add(0);
        }

        int headerLen = 20;
        int indexLen = entries.Length * 16;
        int keyTableStart = headerLen + indexLen;
        int dataTableStart = keyTableStart + keyTable.Count;

        var buf = new byte[dataTableStart + dataTable.Count];
        var span = buf.AsSpan();

        // Header.
        buf[0] = 0x00; buf[1] = (byte)'P'; buf[2] = (byte)'S'; buf[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x04, 4), 0x00000101);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x08, 4), (uint)keyTableStart);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x0C, 4), (uint)dataTableStart);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x10, 4), (uint)entries.Length);

        // Index records.
        for (int i = 0; i < entries.Length; i++)
        {
            var idx = span.Slice(20 + i * 16, 16);
            ushort fmt = entries[i].Format switch
            {
                Fmt.StringSpecial => 0x0004,
                Fmt.StringZ => 0x0204,
                Fmt.Uint32 => 0x0404,
                _ => 0,
            };
            BinaryPrimitives.WriteUInt16LittleEndian(idx.Slice(0, 2), (ushort)keyOffsets[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(idx.Slice(2, 2), fmt);
            BinaryPrimitives.WriteUInt32LittleEndian(idx.Slice(4, 4), (uint)valueBytes[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(idx.Slice(8, 4), (uint)maxLens[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(idx.Slice(12, 4), (uint)dataOffsets[i]);
        }

        // Key + data tables.
        keyTable.CopyTo(0, buf, keyTableStart, keyTable.Count);
        dataTable.CopyTo(0, buf, dataTableStart, dataTable.Count);
        return buf;
    }

    private static byte[] SampleSfo() => BuildSfo(
        new Kv("CATEGORY", Fmt.StringZ, Text: "UG"),
        new Kv("DISC_ID", Fmt.StringZ, Text: "ULUS12345"),
        new Kv("DISC_VERSION", Fmt.StringZ, Text: "1.00"),
        new Kv("PARENTAL_LEVEL", Fmt.Uint32, Number: 5),
        new Kv("TITLE", Fmt.StringZ, Text: "Test Game"));

    // ---- ParamSfo parser ----------------------------------------------------

    [Fact]
    public void Parses_string_entries_with_values_and_types()
    {
        var sfo = ParamSfo.Parse(SampleSfo());

        Assert.Equal("UG", sfo.GetString("CATEGORY"));
        Assert.Equal("ULUS12345", sfo.GetString("DISC_ID"));
        Assert.Equal("1.00", sfo.GetString("DISC_VERSION"));
        Assert.Equal("Test Game", sfo.GetString("TITLE"));

        Assert.False(sfo.Entries["TITLE"].IsInt);
        Assert.Equal("Test Game", sfo.Entries["TITLE"].Text);
    }

    [Fact]
    public void Parses_uint32_entry_as_int_not_string()
    {
        var sfo = ParamSfo.Parse(SampleSfo());

        Assert.Equal((uint?)5, sfo.GetInt("PARENTAL_LEVEL"));
        Assert.True(sfo.Entries["PARENTAL_LEVEL"].IsInt);
        Assert.Equal((uint?)5, sfo.Entries["PARENTAL_LEVEL"].Number);

        // A string getter on an int returns "", an int getter on a string returns null.
        Assert.Equal("", sfo.GetString("PARENTAL_LEVEL"));
        Assert.Null(sfo.GetInt("TITLE"));
    }

    [Fact]
    public void Trims_trailing_nul_from_null_terminated_string()
    {
        // Used length includes the terminator; the parser must not keep it.
        var sfo = ParamSfo.Parse(BuildSfo(new Kv("DISC_ID", Fmt.StringZ, Text: "ULUS00001")));
        var v = sfo.Entries["DISC_ID"];
        Assert.Equal("ULUS00001", v.Text);
        Assert.Equal(9, v.Text!.Length);   // no stray NUL
    }

    [Fact]
    public void Reads_special_not_null_terminated_utf8_string()
    {
        // 0x0004: no terminator; the whole used length is the text.
        var sfo = ParamSfo.Parse(BuildSfo(new Kv("APP_VER", Fmt.StringSpecial, Text: "01.50")));
        Assert.Equal("01.50", sfo.GetString("APP_VER"));
        Assert.False(sfo.Entries["APP_VER"].IsInt);
    }

    [Fact]
    public void Reports_entry_count_and_header_version()
    {
        var sfo = ParamSfo.Parse(SampleSfo());
        Assert.Equal(5, sfo.Entries.Count);
        Assert.Equal((uint)0x00000101, sfo.Version);
        Assert.True(sfo.Contains("CATEGORY"));
        Assert.False(sfo.Contains("NOPE"));
    }

    [Fact]
    public void Missing_key_getters_are_safe()
    {
        var sfo = ParamSfo.Parse(SampleSfo());
        Assert.Equal("", sfo.GetString("NOT_THERE"));
        Assert.Null(sfo.GetInt("NOT_THERE"));
    }

    [Fact]
    public void Parse_accepts_a_stream()
    {
        using var ms = new MemoryStream(SampleSfo());
        var sfo = ParamSfo.Parse(ms);
        Assert.Equal("Test Game", sfo.GetString("TITLE"));
    }

    [Fact]
    public void Bad_magic_throws()
    {
        var bytes = SampleSfo();
        bytes[1] = (byte)'X';   // corrupt the 'P'
        Assert.Throws<ParamSfoFormatException>(() => ParamSfo.Parse(bytes));
    }

    [Fact]
    public void Too_short_throws()
    {
        Assert.Throws<ParamSfoFormatException>(() => ParamSfo.Parse(new byte[] { 0x00, 0x50, 0x53, 0x46 }));
    }

    [Fact]
    public void Entry_count_past_end_throws()
    {
        var bytes = SampleSfo();
        // Claim a huge entry count so the index table overruns the buffer.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10, 4), 100000);
        Assert.Throws<ParamSfoFormatException>(() => ParamSfo.Parse(bytes));
    }

    [Fact]
    public void Data_offset_past_end_throws()
    {
        var bytes = SampleSfo();
        // Point the very first entry's data_offset far past the end of the file.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + 12, 4), 0x0100_0000);
        Assert.Throws<ParamSfoFormatException>(() => ParamSfo.Parse(bytes));
    }

    // ---- end-to-end reader over a real ISO ----------------------------------

    private static byte[] BuildUmdIso(byte[] sfo, bool withEboot = true)
    {
        var gameChildren = new List<IsoBuilder.Node> { IsoBuilder.Node.File("PARAM.SFO", sfo) };
        if (withEboot)
            gameChildren.Add(IsoBuilder.Node.File("EBOOT.BIN", Encoding.ASCII.GetBytes("EBOOTDATA")));

        var tree = new List<IsoBuilder.Node>
        {
            IsoBuilder.Node.Dir("PSP_GAME", gameChildren),
        };
        return IsoBuilder.BuildTree("UMD_TEST", tree).Image;
    }

    [Fact]
    public void Reads_metadata_from_a_plain_umd_iso()
    {
        var iso = BuildUmdIso(SampleSfo());
        using var ms = new MemoryStream(iso);
        var game = PspUmdReader.Read(ms);

        Assert.Equal("ULUS12345", game.DiscId);
        Assert.Equal("Test Game", game.Title);
        Assert.Equal("UG", game.Category);
        Assert.Equal("1.00", game.DiscVersion);
        Assert.Equal("Americas", game.Region);
        Assert.Contains(game.Filesystem.Files, f => f.Name.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsPspUmd_is_true_for_a_umd_iso_and_false_otherwise()
    {
        using var umd = new MemoryStream(BuildUmdIso(SampleSfo()));
        Assert.True(PspUmdReader.IsPspUmd(umd));

        // An ISO with no PSP_GAME/PARAM.SFO is not a UMD.
        var plain = IsoBuilder.BuildTree("DATA", new List<IsoBuilder.Node>
        {
            IsoBuilder.Node.File("README.TXT", Encoding.ASCII.GetBytes("hello")),
        }).Image;
        using var plainMs = new MemoryStream(plain);
        Assert.False(PspUmdReader.IsPspUmd(plainMs));
    }

    [Fact]
    public void Iso_without_param_sfo_throws_psp_format_exception()
    {
        var plain = IsoBuilder.BuildTree("DATA", new List<IsoBuilder.Node>
        {
            IsoBuilder.Node.File("README.TXT", Encoding.ASCII.GetBytes("hello")),
        }).Image;
        using var ms = new MemoryStream(plain);
        Assert.Throws<PspFormatException>(() => PspUmdReader.Read(ms));
    }

    [Fact]
    public void Reads_metadata_from_a_cso_compressed_umd()
    {
        // Compress the UMD ISO to CSO and confirm the reader dispatches to
        // CisoImage.Decompress and reads the same metadata back.
        var iso = BuildUmdIso(SampleSfo());
        var cso = CisoImage.Compress(iso);
        Assert.True(CisoImage.IsCiso(cso));   // it really is a CSO

        using var ms = new MemoryStream(cso);
        var game = PspUmdReader.Read(ms);
        Assert.Equal("ULUS12345", game.DiscId);
        Assert.Equal("Test Game", game.Title);
    }
}
