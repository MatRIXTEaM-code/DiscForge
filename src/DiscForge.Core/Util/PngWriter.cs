// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.IO.Compression;

namespace DiscForge.Core.Util;

/// <summary>
/// A minimal, dependency-free PNG encoder for 8-bit RGBA images — enough to write
/// out a decoded texture (a TIM, say) as a portable file, without pulling in
/// System.Drawing (absent on non-Windows). It emits a single IDAT with the "None"
/// scanline filter, deflated through <see cref="DeflateStream"/> inside a proper
/// zlib wrapper (2-byte header + Adler-32 trailer), and CRC-32s each chunk with
/// DiscForge's own zlib-compatible <see cref="Crc32"/>.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Encode <paramref name="rgba"/> (row-major, 4 bytes/pixel, top-left
    /// origin) as a PNG. Length must be width*height*4.</summary>
    public static byte[] EncodeRgba(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0) throw new ArgumentException("Width and height must be positive.");
        if ((long)width * height * 4 != rgba.LongLength)
            throw new ArgumentException($"Expected {(long)width * height * 4} bytes, got {rgba.LongLength}.");

        using var ms = new MemoryStream();
        ms.Write(Signature);

        // IHDR: width, height, bit depth 8, colour type 6 (RGBA), no interlace.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);

        WriteChunk(ms, "IDAT", ZlibCompress(FilteredScanlines(rgba, width, height)));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    // Prefix each scanline with a filter byte 0 ("None").
    private static byte[] FilteredScanlines(byte[] rgba, int width, int height)
    {
        int stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (stride + 1);
            raw[dst] = 0;   // filter: None
            Array.Copy(rgba, y * stride, raw, dst + 1, stride);
        }
        return raw;
    }

    // zlib stream: 0x78 0x01, raw DEFLATE, then Adler-32 of the uncompressed data.
    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x01);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(data));
        ms.Write(adler);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);

        var typeBytes = new byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        s.Write(typeBytes);
        s.Write(data);

        var crc = new Crc32();
        crc.Update(typeBytes);
        crc.Update(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.Value);
        s.Write(crcBytes);
    }

    private static uint Adler32(byte[] data)
    {
        const uint mod = 65521;
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % mod;
            b = (b + a) % mod;
        }
        return (b << 16) | a;
    }
}
