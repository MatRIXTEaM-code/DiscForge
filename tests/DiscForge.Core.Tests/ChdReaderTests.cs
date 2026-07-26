// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the CHD inspector. A CHD v5 header plus one CD-track metadata entry is
/// built by hand and read back; the checks pin the big-endian header decode, the
/// FourCC compressor names, and the ASCII CD-track descriptor parse.
/// </summary>
public class ChdReaderTests
{
    private static uint Cc(string fourcc) =>
        (uint)((fourcc[0] << 24) | (fourcc[1] << 16) | (fourcc[2] << 8) | fourcc[3]);

    private static byte[] BuildChd(string trackText)
    {
        int headerLen = 124;
        var payload = Encoding.ASCII.GetBytes(trackText + "\0");
        var d = new byte[headerLen + 16 + payload.Length];

        Encoding.ASCII.GetBytes("MComprHD").CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x08), (uint)headerLen);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x0C), 5);            // version
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x10), Cc("cdzl"));  // compressor 0
        // compressors 1-3 left zero ("none")
        BinaryPrimitives.WriteUInt64BigEndian(d.AsSpan(0x20), 12345UL * 2448); // logical bytes
        BinaryPrimitives.WriteUInt64BigEndian(d.AsSpan(0x30), (ulong)headerLen); // meta offset
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x38), 2448 * 8);     // hunk bytes
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x3C), 2448);         // unit bytes

        // One metadata entry at the header end: "CHT2", flags+length, next=0, payload.
        int m = headerLen;
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(m), Cc("CHT2"));
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(m + 4), (0x01u << 24) | (uint)payload.Length);
        BinaryPrimitives.WriteUInt64BigEndian(d.AsSpan(m + 8), 0);           // no next entry
        payload.CopyTo(d, m + 16);
        return d;
    }

    [Fact]
    public void The_header_decodes_version_codecs_and_sizes()
    {
        var info = ChdReader.Read(BuildChd("TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:12345 PREGAP:150 POSTGAP:0"));

        Assert.Equal(5, info.Version);
        Assert.Equal("cdzl", info.Compressors[0]);
        Assert.Equal("none", info.Compressors[1]);
        Assert.Equal(12345L * 2448, info.LogicalBytes);
        Assert.Equal(2448, info.UnitBytes);
        Assert.True(info.IsCd);
    }

    [Fact]
    public void A_cd_track_descriptor_is_parsed()
    {
        var info = ChdReader.Read(BuildChd("TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:99999 PREGAP:150 POSTGAP:0"));
        var track = Assert.Single(info.Tracks);

        Assert.Equal(1, track.Number);
        Assert.Equal("MODE2_RAW", track.Type);
        Assert.Equal("NONE", track.SubType);
        Assert.Equal(99999, track.Frames);
        Assert.Equal(150, track.Pregap);
    }

    [Fact]
    public void A_non_chd_is_refused()
    {
        Assert.False(ChdReader.IsChd(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Throws<ChdFormatException>(() => ChdReader.Read(new byte[16]));
    }

    [Fact]
    public void An_older_version_is_reported_as_unsupported()
    {
        var d = new byte[124];
        Encoding.ASCII.GetBytes("MComprHD").CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0x0C), 4);   // v4
        var ex = Assert.Throws<ChdFormatException>(() => ChdReader.Read(d));
        Assert.Contains("v4", ex.Message);
    }
}
