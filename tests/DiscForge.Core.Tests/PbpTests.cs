// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Psp;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the PBP (EBOOT.PBP) container parser, pinned with hand-built buffers
/// laid out exactly per the format: a header (magic + version + eight u32 offsets)
/// followed by the delimited sub-files. The main fixture embeds a real small
/// PARAM.SFO, a fake ICON0.PNG, an empty ICON1.PMF, and a DATA.PSAR that runs to
/// EOF, and asserts section geometry, byte-exact extraction, and PARAM.SFO reuse.
/// Malformed cases cover bad magic, non-monotonic offsets, and an offset past EOF.
/// </summary>
public class PbpTests
{
    // ---- a minimal PARAM.SFO builder (mirrors the format ParamSfo reads) -----

    private static byte[] BuildSfo(params (string Key, string Text)[] entries)
    {
        var valueBytes = new List<byte[]>();
        var maxLens = new List<int>();
        foreach (var (_, text) in entries)
        {
            var raw = Encoding.UTF8.GetBytes(text).Concat(new byte[] { 0 }).ToArray();
            valueBytes.Add(raw);
            int max = ((raw.Length + 3) / 4) * 4;
            if (max < raw.Length) max = raw.Length;
            maxLens.Add(max);
        }

        var keyTable = new List<byte>();
        var keyOffsets = new List<int>();
        foreach (var (key, _) in entries)
        {
            keyOffsets.Add(keyTable.Count);
            keyTable.AddRange(Encoding.ASCII.GetBytes(key));
            keyTable.Add(0);
        }
        while (keyTable.Count % 4 != 0) keyTable.Add(0);

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

        buf[0] = 0x00; buf[1] = (byte)'P'; buf[2] = (byte)'S'; buf[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x04, 4), 0x00000101);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x08, 4), (uint)keyTableStart);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x0C, 4), (uint)dataTableStart);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x10, 4), (uint)entries.Length);

        for (int i = 0; i < entries.Length; i++)
        {
            var idx = span.Slice(20 + i * 16, 16);
            BinaryPrimitives.WriteUInt16LittleEndian(idx.Slice(0, 2), (ushort)keyOffsets[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(idx.Slice(2, 2), 0x0204); // NUL-terminated UTF-8
            BinaryPrimitives.WriteUInt32LittleEndian(idx.Slice(4, 4), (uint)valueBytes[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(idx.Slice(8, 4), (uint)maxLens[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(idx.Slice(12, 4), (uint)dataOffsets[i]);
        }

        keyTable.CopyTo(0, buf, keyTableStart, keyTable.Count);
        dataTable.CopyTo(0, buf, dataTableStart, dataTable.Count);
        return buf;
    }

    private static byte[] SampleSfo() => BuildSfo(
        ("CATEGORY", "MG"),
        ("DISC_ID", "ULUS12345"),
        ("TITLE", "Test Game"));

    // ---- a PBP builder -------------------------------------------------------

    private static readonly byte[] PngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] PsarBytes = Encoding.ASCII.GetBytes("PSARDATA_TAIL");

    /// <summary>Hand-build a PBP: header (magic + version + eight offsets) then the
    /// sub-file payloads concatenated in order. ICON1.PMF is empty (its offset equals
    /// the next), and DATA.PSAR runs to EOF.</summary>
    private static byte[] BuildPbp(
        uint version, byte[] sfo, byte[] icon0, byte[] dataPsp, byte[] dataPsar)
    {
        const int header = 0x28;

        // Payloads in header order. ICON1.PMF, PIC0.PNG, PIC1.PNG, SND0.AT3 are empty.
        long oSfo = header;
        long oIcon0 = oSfo + sfo.Length;
        long oIcon1 = oIcon0 + icon0.Length; // empty: next offset equals this one
        long oPic0 = oIcon1;                  // empty
        long oPic1 = oPic0;                   // empty
        long oSnd0 = oPic1;                   // empty
        long oDataPsp = oSnd0;                // SND0.AT3 empty -> DATA.PSP starts here
        long oDataPsar = oDataPsp + dataPsp.Length;
        long total = oDataPsar + dataPsar.Length;

        var buf = new byte[total];
        var span = buf.AsSpan();

        buf[0] = 0x00; buf[1] = (byte)'P'; buf[2] = (byte)'B'; buf[3] = (byte)'P';
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x04, 4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x08, 4), (uint)oSfo);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x0C, 4), (uint)oIcon0);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x10, 4), (uint)oIcon1);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x14, 4), (uint)oPic0);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x18, 4), (uint)oPic1);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x1C, 4), (uint)oSnd0);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x20, 4), (uint)oDataPsp);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x24, 4), (uint)oDataPsar);

        Array.Copy(sfo, 0, buf, oSfo, sfo.Length);
        Array.Copy(icon0, 0, buf, oIcon0, icon0.Length);
        Array.Copy(dataPsp, 0, buf, oDataPsp, dataPsp.Length);
        Array.Copy(dataPsar, 0, buf, oDataPsar, dataPsar.Length);
        return buf;
    }

    private static byte[] SamplePbp() => BuildPbp(
        version: 0x00010000,
        sfo: SampleSfo(),
        icon0: PngSig,
        dataPsp: Encoding.ASCII.GetBytes("~PSP-ENCRYPTED-BLOB"),
        dataPsar: PsarBytes);

    // ---- parsing -------------------------------------------------------------

    [Fact]
    public void IsPbp_true_for_magic_false_otherwise()
    {
        Assert.True(PbpFile.IsPbp(SamplePbp()));
        Assert.False(PbpFile.IsPbp(new byte[] { 0x00, 0x50, 0x53, 0x46 })); // SFO magic, not PBP
        Assert.False(PbpFile.IsPbp(new byte[] { 0x00, 0x50 }));             // too short
    }

    [Fact]
    public void Parses_version_and_eight_sections_in_order()
    {
        var pbp = PbpFile.Parse(SamplePbp());
        Assert.Equal((uint)0x00010000, pbp.Version);
        Assert.Equal(8, pbp.Sections.Count);

        string[] expected =
        {
            "PARAM.SFO", "ICON0.PNG", "ICON1.PMF", "PIC0.PNG",
            "PIC1.PNG", "SND0.AT3", "DATA.PSP", "DATA.PSAR",
        };
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], pbp.Sections[i].Name);
    }

    [Fact]
    public void Section_offsets_and_sizes_are_derived_from_neighbours()
    {
        var sfo = SampleSfo();
        var pbp = PbpFile.Parse(SamplePbp());

        var param = pbp.GetSection("PARAM.SFO");
        Assert.Equal(0x28, param.Offset);
        Assert.Equal(sfo.Length, param.Size);

        var icon0 = pbp.GetSection("ICON0.PNG");
        Assert.Equal(0x28 + sfo.Length, icon0.Offset);
        Assert.Equal(PngSig.Length, icon0.Size);
    }

    [Fact]
    public void Empty_section_has_size_zero()
    {
        var pbp = PbpFile.Parse(SamplePbp());
        var icon1 = pbp.GetSection("ICON1.PMF");
        Assert.Equal(0, icon1.Size);
        Assert.True(icon1.IsEmpty);

        // The other unused middle sections are empty too.
        Assert.True(pbp.GetSection("PIC0.PNG").IsEmpty);
        Assert.True(pbp.GetSection("SND0.AT3").IsEmpty);
    }

    [Fact]
    public void Last_section_runs_to_end_of_file()
    {
        var raw = SamplePbp();
        var pbp = PbpFile.Parse(raw);
        var psar = pbp.GetSection("DATA.PSAR");
        Assert.Equal(PsarBytes.Length, psar.Size);
        Assert.Equal(raw.Length, psar.Offset + psar.Size);
    }

    [Fact]
    public void GetSection_returns_exact_bytes()
    {
        var raw = SamplePbp();

        Assert.Equal(SampleSfo(), PbpFile.GetSection(raw, "PARAM.SFO"));
        Assert.Equal(PngSig, PbpFile.GetSection(raw, "ICON0.PNG"));
        Assert.Equal(Encoding.ASCII.GetBytes("~PSP-ENCRYPTED-BLOB"), PbpFile.GetSection(raw, "DATA.PSP"));
        Assert.Equal(PsarBytes, PbpFile.GetSection(raw, "DATA.PSAR"));

        // An empty section yields an empty (non-null) array.
        Assert.Empty(PbpFile.GetSection(raw, "ICON1.PMF"));
    }

    [Fact]
    public void ExtractSection_copies_raw_bytes_via_stream()
    {
        var raw = SamplePbp();
        var pbp = PbpFile.Parse(raw);
        var psp = pbp.GetSection("DATA.PSP");

        using var src = new MemoryStream(raw);
        using var dst = new MemoryStream();
        PbpFile.ExtractSection(src, psp, dst);

        // DATA.PSP is copied verbatim — never decrypted.
        Assert.Equal(Encoding.ASCII.GetBytes("~PSP-ENCRYPTED-BLOB"), dst.ToArray());
    }

    [Fact]
    public void GetParamSfo_reuses_the_sfo_parser()
    {
        var sfo = PbpFile.GetParamSfo(SamplePbp());
        Assert.NotNull(sfo);
        Assert.Equal("Test Game", sfo!.GetString("TITLE"));
        Assert.Equal("ULUS12345", sfo.GetString("DISC_ID"));
        Assert.Equal("MG", sfo.GetString("CATEGORY"));
    }

    [Fact]
    public void GetParamSfo_returns_null_when_section_empty()
    {
        // A PBP with an empty PARAM.SFO section (offset equals ICON0's offset).
        var pbp = BuildPbp(
            version: 1,
            sfo: Array.Empty<byte>(),
            icon0: PngSig,
            dataPsp: Array.Empty<byte>(),
            dataPsar: PsarBytes);

        Assert.True(PbpFile.Parse(pbp).GetSection("PARAM.SFO").IsEmpty);
        Assert.Null(PbpFile.GetParamSfo(pbp));
    }

    [Fact]
    public void Parse_accepts_a_stream()
    {
        using var ms = new MemoryStream(SamplePbp());
        var pbp = PbpFile.Parse(ms);
        Assert.Equal((uint)0x00010000, pbp.Version);
        Assert.Equal("DATA.PSAR", pbp.Sections[^1].Name);
    }

    // ---- malformed cases -----------------------------------------------------

    [Fact]
    public void Bad_magic_throws()
    {
        var raw = SamplePbp();
        raw[1] = (byte)'X'; // corrupt the 'P'
        Assert.Throws<PbpFormatException>(() => PbpFile.Parse(raw));
    }

    [Fact]
    public void Too_short_throws()
    {
        Assert.Throws<PbpFormatException>(() => PbpFile.Parse(new byte[] { 0x00, 0x50, 0x42, 0x50 }));
    }

    [Fact]
    public void Non_monotonic_offsets_throw()
    {
        var raw = SamplePbp();
        // Make ICON0.PNG's offset smaller than PARAM.SFO's offset.
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x0C, 4), 0x08);
        Assert.Throws<PbpFormatException>(() => PbpFile.Parse(raw));
    }

    [Fact]
    public void Offset_past_end_of_file_throws()
    {
        var raw = SamplePbp();
        // Push DATA.PSP's offset far past the end of the buffer.
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x20, 4), 0x0100_0000);
        Assert.Throws<PbpFormatException>(() => PbpFile.Parse(raw));
    }
}
