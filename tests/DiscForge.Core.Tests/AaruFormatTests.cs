// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Aaru;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The AaruFormat reader. The round-trip test builds a minimal UNCOMPRESSED AaruFormat by hand —
/// header, one data block, a deduplication table (including a zero-filled sector that isn't stored),
/// and the index — then reads the sectors back through the DDT, proving the header/index/DDT walk and
/// the shift math. The second test pins the honest decline: an LZMA-compressed image is refused, not
/// guessed.
/// </summary>
public class AaruFormatTests
{
    private const uint BtDataBlock = 0x4B4C4244, BtDdt = 0x2A544444, BtIndex = 0x58444E49;
    private const uint DtUserData = 1;
    private const int SS = 512;

    private static byte[] BuildImage(uint dataBlockCompression, int n, out byte[] blockData)
    {
        byte shift = 8;
        blockData = new byte[n * SS];
        for (int slot = 0; slot < n; slot++)
            for (int j = 0; j < SS; j++)
                blockData[slot * SS + j] = (byte)((slot * 7 + j) & 0xFF);

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        // ---- header (104 bytes) ----
        w.Write(System.Text.Encoding.ASCII.GetBytes("AARUFRMT"));   // identifier (8)
        w.Write(new byte[64]);                                       // application (32 UTF-16 chars)
        w.Write((byte)5); w.Write((byte)0);                          // image version
        w.Write((byte)1); w.Write((byte)0);                          // application version
        w.Write((uint)0);                                            // media type
        long indexOffsetPos = ms.Position; w.Write((ulong)0);        // indexOffset (patched later)
        w.Write((long)0); w.Write((long)0);                          // creation / last-written

        // ---- data block (BlockHeader is pack=1: u32,u16,u16,u32,u32,u32,u64,u64 = 36 bytes) ----
        long dataBlockOffset = ms.Position;
        w.Write(BtDataBlock);                                         // identifier u32
        w.Write((ushort)DtUserData);                                 // type u16
        w.Write((ushort)dataBlockCompression);                      // compression u16
        w.Write((uint)SS); w.Write((uint)(n * SS)); w.Write((uint)(n * SS));   // sectorSize, cmpLength, length
        w.Write((ulong)0); w.Write((ulong)0);                        // cmpCrc64, crc64
        w.Write(blockData);

        // ---- deduplication table (DdtHeader is pack=1: u32,u16,u16,u8,u64,u64,u64,u64,u64 = 49 bytes) ----
        long ddtOffset = ms.Position;
        w.Write(BtDdt);                                              // identifier u32
        w.Write((ushort)DtUserData);                                // type u16
        w.Write((ushort)0);                                         // compression u16 (None)
        w.Write(shift);                                             // shift u8
        w.Write((ulong)n); w.Write((ulong)(n * 8)); w.Write((ulong)(n * 8));  // entries, cmpLen, len
        w.Write((ulong)0); w.Write((ulong)0);                       // cmpCrc64, crc64
        for (int i = 0; i < n; i++)
        {
            ulong entry = i == 2 ? 0UL                                        // sector 2 is a zero sector
                                 : ((ulong)dataBlockOffset << shift) | (uint)i;
            w.Write(entry);
        }

        // ---- index (IndexHeader u32,u16,u64 = 14; IndexEntry u32,u16,u64 = 14) ----
        long indexOffset = ms.Position;
        w.Write(BtIndex); w.Write((ushort)2); w.Write((ulong)0);
        w.Write(BtDataBlock); w.Write((ushort)DtUserData); w.Write((ulong)dataBlockOffset);
        w.Write(BtDdt); w.Write((ushort)DtUserData); w.Write((ulong)ddtOffset);

        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.GetBytes((ulong)indexOffset).CopyTo(bytes, (int)indexOffsetPos);
        return bytes;
    }

    [Fact]
    public void Reads_an_uncompressed_image_through_the_dedup_table()
    {
        var bytes = BuildImage(dataBlockCompression: 0, n: 4, out var blockData);

        var info = AaruFormat.ReadInfo(new MemoryStream(bytes));
        Assert.True(info.Recognized);
        Assert.Equal("AARUFRMT", info.Magic);
        Assert.Equal(4, info.Sectors);
        Assert.Equal((uint)SS, info.SectorSize);
        Assert.True(info.UserDataExtractable);

        using var outMs = new MemoryStream();
        long written = AaruFormat.ExtractUserData(new MemoryStream(bytes), outMs);
        Assert.Equal(4, written);

        var outp = outMs.ToArray();
        Assert.Equal(4 * SS, outp.Length);
        Assert.Equal(blockData.AsSpan(0, SS).ToArray(), outp.AsSpan(0, SS).ToArray());          // sector 0
        Assert.Equal(blockData.AsSpan(SS, SS).ToArray(), outp.AsSpan(SS, SS).ToArray());        // sector 1
        Assert.All(outp.AsSpan(2 * SS, SS).ToArray(), b => Assert.Equal(0, b));                 // sector 2 = zeros
        Assert.Equal(blockData.AsSpan(3 * SS, SS).ToArray(), outp.AsSpan(3 * SS, SS).ToArray());// sector 3
    }

    [Fact]
    public void Writer_round_trips_through_the_reader()
    {
        const int ss = 2048;
        const int n = 5;
        var data = new byte[n * ss];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 31 + 7) & 0xFF);
        // Make sector 3 all-zero to exercise a zero sector on the round trip.
        Array.Clear(data, 3 * ss, ss);

        using var img = new MemoryStream();
        AaruFormat.WriteUncompressed(img, data, ss, mediaType: 0);
        var bytes = img.ToArray();

        var info = AaruFormat.ReadInfo(new MemoryStream(bytes));
        Assert.True(info.Recognized);
        Assert.Equal("AARUFRMT", info.Magic);
        Assert.Equal(n, info.Sectors);
        Assert.Equal((uint)ss, info.SectorSize);
        Assert.True(info.UserDataExtractable);

        using var outMs = new MemoryStream();
        long written = AaruFormat.ExtractUserData(new MemoryStream(bytes), outMs);
        Assert.Equal(n, written);
        Assert.Equal(data, outMs.ToArray());
    }

    [Fact]
    public void A_corrupted_uncompressed_block_is_declined_by_its_crc64()
    {
        // The writer stamps a real CRC-64; flip one payload byte and extraction must refuse.
        var data = new byte[4 * 512];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 11 + 3);
        using var img = new MemoryStream();
        AaruFormat.WriteUncompressed(img, data, 512);
        var bytes = img.ToArray();
        bytes[104 + 36 + 100] ^= 0xFF;               // header(104) + BlockHeader(36) + offset into payload

        Assert.Throws<InvalidDataException>(() =>
            AaruFormat.ExtractUserData(new MemoryStream(bytes), new MemoryStream()));
    }

    [Fact]
    public void Declines_a_subchannel_transform_image_rather_than_guess()
    {
        var bytes = BuildImage(dataBlockCompression: 3 /* LzmaSubchannelTransform */, n: 2, out _);

        // Identify still works and reports the compression honestly.
        var info = AaruFormat.ReadInfo(new MemoryStream(bytes));
        Assert.True(info.Recognized);
        Assert.False(info.UserDataExtractable);
        Assert.Equal("LzmaSubchannelTransform", info.UserDataCompression);

        // Extraction refuses rather than emit a corrupt image.
        Assert.Throws<NotSupportedException>(() =>
            AaruFormat.ExtractUserData(new MemoryStream(bytes), new MemoryStream()));
    }

    // ---- LZMA-compressed image: payload produced by liblzma (the reference implementation) ----

    // 2048 bytes of the repeated sentence below, LZMA1-compressed: [5-byte props][raw stream].
    private const string LzmaPayloadB64 =
        "XQAAgAAAIhpKZkPjClVABErjnAQTFaydsEDX8rMvkSLj1SEfzOBrp3RPM+9kf8IQlwOvfAnHtpKTHgbvwSQ1cLPKvaAA7Z5ApjZ5uYP//umaAA==";

    private static byte[] LzmaBlockData()
    {
        var s = System.Text.Encoding.ASCII.GetBytes("DiscForge preserves optical media provably or declines. ");
        var data = new byte[2048];
        for (int i = 0; i < data.Length; i++) data[i] = s[i % s.Length];
        return data;
    }

    private static byte[] BuildLzmaImage(byte[] blockData, ulong crc64) =>
        BuildCompressedImage(1 /* Lzma */, System.Convert.FromBase64String(LzmaPayloadB64),
                             blockData.Length, (uint)SS, crc64);

    /// <summary>A minimal AaruFormat image whose single user-data block stores <paramref name="dataLen"/>
    /// bytes compressed as <paramref name="payload"/> under the given compression id.</summary>
    private static byte[] BuildCompressedImage(ushort compression, byte[] payload, int dataLen,
                                               uint sectorSize, ulong crc64)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        w.Write(System.Text.Encoding.ASCII.GetBytes("AARUFRMT"));
        w.Write(new byte[64]);
        w.Write((byte)5); w.Write((byte)0); w.Write((byte)1); w.Write((byte)0);
        w.Write((uint)0);
        long indexOffsetPos = ms.Position; w.Write((ulong)0);
        w.Write((long)0); w.Write((long)0);

        long dataBlockOffset = ms.Position;
        w.Write(BtDataBlock);
        w.Write((ushort)DtUserData);
        w.Write(compression);
        w.Write(sectorSize);
        w.Write((uint)payload.Length);                       // cmpLength
        w.Write((uint)dataLen);                              // length = uncompressed
        w.Write((ulong)0);                                   // cmpCrc64 (unchecked)
        w.Write(crc64);                                      // crc64 of the UNCOMPRESSED data — the proof gate
        w.Write(payload);

        byte shift = 8;
        long ddtOffset = ms.Position;
        int n = dataLen / (int)sectorSize;
        w.Write(BtDdt); w.Write((ushort)DtUserData); w.Write((ushort)0); w.Write(shift);
        w.Write((ulong)n); w.Write((ulong)(n * 8)); w.Write((ulong)(n * 8));
        w.Write((ulong)0); w.Write((ulong)0);
        for (int i = 0; i < n; i++)
            w.Write(((ulong)dataBlockOffset << shift) | (uint)i);

        long indexOffset = ms.Position;
        w.Write(BtIndex); w.Write((ushort)2); w.Write((ulong)0);
        w.Write(BtDataBlock); w.Write((ushort)DtUserData); w.Write((ulong)dataBlockOffset);
        w.Write(BtDdt); w.Write((ushort)DtUserData); w.Write((ulong)ddtOffset);

        w.Flush();
        var bytes = ms.ToArray();
        BitConverter.GetBytes((ulong)indexOffset).CopyTo(bytes, (int)indexOffsetPos);
        return bytes;
    }

    [Fact]
    public void Extracts_an_lzma_compressed_image_and_proves_it_by_crc64()
    {
        var blockData = LzmaBlockData();
        var bytes = BuildLzmaImage(blockData, AaruFormat.Crc64Ecma182(blockData));

        var info = AaruFormat.ReadInfo(new MemoryStream(bytes));
        Assert.True(info.Recognized);
        Assert.Equal("Lzma", info.UserDataCompression);
        Assert.True(info.UserDataExtractable);

        using var outMs = new MemoryStream();
        long written = AaruFormat.ExtractUserData(new MemoryStream(bytes), outMs);
        Assert.Equal(blockData.Length / SS, written);
        Assert.Equal(blockData, outMs.ToArray());
    }

    [Fact]
    public void An_lzma_block_whose_crc_does_not_match_is_declined_not_emitted()
    {
        var blockData = LzmaBlockData();
        var bytes = BuildLzmaImage(blockData, AaruFormat.Crc64Ecma182(blockData) ^ 0xDEAD);

        Assert.Throws<InvalidDataException>(() =>
            AaruFormat.ExtractUserData(new MemoryStream(bytes), new MemoryStream()));
    }

    // ---- FLAC-compressed image: payload produced by ffmpeg (a reference FLAC encoder), with the ----
    // ---- full fLaC container (STREAMINFO + vorbis-comment + padding) exercising the skip logic  ----

    private static string FlacVectorPath([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "assets", "aaru-flac-vector.flac");

    /// <summary>4 audio sectors (9,408 bytes) of 16-bit stereo — integer triangle sweeps plus LCG
    /// dither, exactly reproducible here and in the vector-producing script (SHA-256 pinned).</summary>
    private static byte[] FlacBlockData()
    {
        var data = new byte[4 * 2352];
        long x = 12345;
        int p = 0;
        for (int i = 0; i < data.Length / 4; i++)
        {
            int tri = Math.Abs((i * 37) % 4000 - 2000) - 1000;
            x = (x * 1103515245 + 12345) & 0x7fffffff;
            int dith = (int)((x >> 16) % 64) - 32;
            int l = (tri * 12 + dith) & 0xFFFF;
            int r = (-tri * 9 + dith * 2) & 0xFFFF;
            data[p++] = (byte)(l & 0xFF); data[p++] = (byte)(l >> 8);
            data[p++] = (byte)(r & 0xFF); data[p++] = (byte)(r >> 8);
        }
        Assert.Equal("a0746b7665d04693c1177f11ffd20cdce00b9fda54695266934a0c05df8c1669",
            System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant());
        return data;
    }

    [Fact]
    public void Extracts_a_flac_compressed_image_and_proves_it_by_crc64()
    {
        var blockData = FlacBlockData();
        var payload = File.ReadAllBytes(FlacVectorPath());
        var bytes = BuildCompressedImage(2 /* Flac */, payload, blockData.Length, 2352,
                                         AaruFormat.Crc64Ecma182(blockData));

        var info = AaruFormat.ReadInfo(new MemoryStream(bytes));
        Assert.True(info.Recognized);
        Assert.Equal("Flac", info.UserDataCompression);
        Assert.True(info.UserDataExtractable);

        using var outMs = new MemoryStream();
        long written = AaruFormat.ExtractUserData(new MemoryStream(bytes), outMs);
        Assert.Equal(4, written);
        Assert.Equal(blockData, outMs.ToArray());
    }

    [Fact]
    public void A_flac_block_whose_crc_does_not_match_is_declined_not_emitted()
    {
        var blockData = FlacBlockData();
        var payload = File.ReadAllBytes(FlacVectorPath());
        var bytes = BuildCompressedImage(2, payload, blockData.Length, 2352,
                                         AaruFormat.Crc64Ecma182(blockData) ^ 0xBEEF);

        Assert.Throws<InvalidDataException>(() =>
            AaruFormat.ExtractUserData(new MemoryStream(bytes), new MemoryStream()));
    }
}
