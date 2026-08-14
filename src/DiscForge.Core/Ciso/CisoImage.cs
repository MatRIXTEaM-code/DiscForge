// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DiscForge.Core.Ciso;

public sealed class CisoFormatException(string message) : Exception(message);

/// <summary>CSO uses zlib (deflate) per block; ZSO uses LZ4.</summary>
public enum CisoKind { Ciso, Ziso }

public sealed record CisoInfo
{
    public required CisoKind Kind { get; init; }
    public required long UncompressedSize { get; init; }
    public required int BlockSize { get; init; }
    public required int Blocks { get; init; }
}

/// <summary>
/// Reads and writes CSO/ZSO — the block-compressed ISO container the emulation
/// world (OPL, PSP/PS2 loaders, ROM libraries) uses to store disc images smaller.
/// DiscForge decompresses a CSO/ZSO back to a plain ISO it can inspect and patch,
/// and compresses an ISO to CSO for storage. Plain container work — nothing here
/// is protection-related.
///
/// Clean-room, from the public CISO/ZISO description:
///   Header (24 bytes, little-endian):
///     0x00  4  magic "CISO" (zlib) or "ZISO" (LZ4)
///     0x04  4  header size (0x18)
///     0x08  8  uncompressed (original ISO) size
///     0x10  4  block size (2048)
///     0x14  1  version
///     0x15  1  index alignment shift
///     0x16  2  reserved
///   Index: (blocks+1) little-endian u32. Bit 31 set = the block is stored raw
///   (uncompressed); the low 31 bits, shifted left by the alignment, are the block's
///   file offset. Block N spans index[N]..index[N+1]; compressed blocks are raw
///   deflate (CSO) or an LZ4 block (ZSO).
/// </summary>
public static class CisoImage
{
    public const int DefaultBlockSize = 2048;
    private const int HeaderSize = 0x18;
    private const uint UncompressedFlag = 0x8000_0000;

    private static readonly byte[] CisoMagic = Encoding.ASCII.GetBytes("CISO");
    private static readonly byte[] ZisoMagic = Encoding.ASCII.GetBytes("ZISO");

    public static bool IsCiso(byte[] header) =>
        header.Length >= 4 &&
        (header.AsSpan(0, 4).SequenceEqual(CisoMagic) || header.AsSpan(0, 4).SequenceEqual(ZisoMagic));

    public static CisoInfo ReadInfo(Stream ciso)
    {
        ArgumentNullException.ThrowIfNull(ciso);
        var header = new byte[HeaderSize];
        ciso.Seek(0, SeekOrigin.Begin);
        ciso.ReadExactly(header, 0, HeaderSize);
        return ParseHeader(header);
    }

    private static CisoInfo ParseHeader(byte[] header)
    {
        CisoKind kind;
        if (header.AsSpan(0, 4).SequenceEqual(CisoMagic)) kind = CisoKind.Ciso;
        else if (header.AsSpan(0, 4).SequenceEqual(ZisoMagic)) kind = CisoKind.Ziso;
        else throw new CisoFormatException("Missing the \"CISO\"/\"ZISO\" signature — not a compressed ISO.");

        long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x08));
        int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x10));
        if (blockSize <= 0) throw new CisoFormatException($"Invalid block size {blockSize}.");
        int blocks = (int)((size + blockSize - 1) / blockSize);
        return new CisoInfo { Kind = kind, UncompressedSize = size, BlockSize = blockSize, Blocks = blocks };
    }

    // ---- decompress (CSO/ZSO -> ISO) ----------------------------------------

    /// <summary>Decompress a CSO/ZSO to a plain ISO written to
    /// <paramref name="isoOut"/>. The input must be seekable.</summary>
    public static void Decompress(Stream ciso, Stream isoOut)
    {
        ArgumentNullException.ThrowIfNull(ciso);
        ArgumentNullException.ThrowIfNull(isoOut);
        if (!ciso.CanSeek) throw new ArgumentException("Decompressing a CSO needs a seekable input.", nameof(ciso));

        var header = new byte[HeaderSize];
        ciso.Seek(0, SeekOrigin.Begin);
        ciso.ReadExactly(header, 0, HeaderSize);
        var info = ParseHeader(header);
        int align = header[0x15];

        // Read the (blocks+1) index entries.
        var indexBytes = new byte[(info.Blocks + 1) * 4];
        ciso.ReadExactly(indexBytes, 0, indexBytes.Length);
        var index = new uint[info.Blocks + 1];
        for (int i = 0; i <= info.Blocks; i++)
            index[i] = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan(i * 4));

        long written = 0;
        var block = new byte[info.BlockSize];
        for (int i = 0; i < info.Blocks; i++)
        {
            bool raw = (index[i] & UncompressedFlag) != 0;
            long off = (long)(index[i] & ~UncompressedFlag) << align;
            long nextOff = (long)(index[i + 1] & ~UncompressedFlag) << align;
            int compLen = (int)(nextOff - off);
            if (compLen <= 0) throw new CisoFormatException($"Block {i} has a non-positive length.");

            var comp = new byte[compLen];
            ciso.Seek(off, SeekOrigin.Begin);
            ciso.ReadExactly(comp, 0, compLen);

            int produced;
            if (raw)
            {
                Array.Copy(comp, block, Math.Min(compLen, info.BlockSize));
                produced = info.BlockSize;
            }
            else if (info.Kind == CisoKind.Ciso)
                produced = Inflate(comp, block);
            else
                produced = Lz4DecompressBlock(comp, block);

            int toWrite = (int)Math.Min(produced, info.UncompressedSize - written);
            isoOut.Write(block, 0, toWrite);
            written += toWrite;
        }
        isoOut.Flush();
    }

    public static byte[] Decompress(byte[] ciso)
    {
        using var input = new MemoryStream(ciso);
        using var output = new MemoryStream();
        Decompress(input, output);
        return output.ToArray();
    }

    // ---- compress (ISO -> CSO) ----------------------------------------------

    /// <summary>Compress a plain ISO to CSO (zlib blocks). Output must be seekable
    /// (the index is back-patched once block offsets are known).</summary>
    public static void Compress(Stream iso, long isoSize, Stream cisoOut, int blockSize = DefaultBlockSize)
    {
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(cisoOut);
        if (!cisoOut.CanSeek) throw new ArgumentException("Writing a CSO needs a seekable output.", nameof(cisoOut));
        if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));

        int blocks = (int)((isoSize + blockSize - 1) / blockSize);

        var header = new byte[HeaderSize];
        CisoMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x04), HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x08), (ulong)isoSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), (uint)blockSize);
        header[0x14] = 1;   // version
        cisoOut.Seek(0, SeekOrigin.Begin);
        cisoOut.Write(header, 0, HeaderSize);

        long indexAt = HeaderSize;
        var index = new uint[blocks + 1];
        long dataAt = HeaderSize + (long)(blocks + 1) * 4;
        cisoOut.Seek(dataAt, SeekOrigin.Begin);

        var raw = new byte[blockSize];
        long offset = dataAt;
        for (int i = 0; i < blocks; i++)
        {
            Array.Clear(raw);
            int want = (int)Math.Min(blockSize, isoSize - (long)i * blockSize);
            iso.ReadExactly(raw, 0, want);   // last block zero-padded

            var comp = Deflate(raw);
            if (comp.Length >= blockSize)
            {
                index[i] = (uint)offset | UncompressedFlag;
                cisoOut.Write(raw, 0, blockSize);
                offset += blockSize;
            }
            else
            {
                index[i] = (uint)offset;
                cisoOut.Write(comp, 0, comp.Length);
                offset += comp.Length;
            }
        }
        index[blocks] = (uint)offset;

        // Back-patch the index.
        var indexBytes = new byte[(blocks + 1) * 4];
        for (int i = 0; i <= blocks; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(i * 4), index[i]);
        cisoOut.Seek(indexAt, SeekOrigin.Begin);
        cisoOut.Write(indexBytes, 0, indexBytes.Length);
        cisoOut.Flush();
    }

    public static byte[] Compress(byte[] iso)
    {
        using var input = new MemoryStream(iso);
        using var output = new MemoryStream();
        Compress(input, iso.Length, output);
        return output.ToArray();
    }

    // ---- codecs -------------------------------------------------------------

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static int Inflate(byte[] comp, byte[] into)
    {
        using var ds = new DeflateStream(new MemoryStream(comp), CompressionMode.Decompress);
        int total = 0;
        int n;
        while (total < into.Length && (n = ds.Read(into, total, into.Length - total)) > 0) total += n;
        return total;
    }

    /// <summary>Decompress one LZ4 block (clean-room, from the public LZ4 block
    /// format: token = literal-length high nibble / match-length low nibble, then
    /// literals, a 2-byte little-endian back-offset, and the match).</summary>
    internal static int Lz4DecompressBlock(byte[] src, byte[] dst)
    {
        int sp = 0, dp = 0;
        while (sp < src.Length)
        {
            byte token = src[sp++];
            int litLen = token >> 4;
            if (litLen == 15)
            {
                byte b;
                do { b = src[sp++]; litLen += b; } while (b == 255);
            }
            for (int i = 0; i < litLen && dp < dst.Length; i++) dst[dp++] = src[sp++];

            if (sp >= src.Length) break;   // final sequence: literals only

            int offset = src[sp++] | (src[sp++] << 8);
            int matchLen = token & 0x0F;
            if (matchLen == 15)
            {
                byte b;
                do { b = src[sp++]; matchLen += b; } while (b == 255);
            }
            matchLen += 4;   // LZ4 minimum match

            int mp = dp - offset;
            if (mp < 0) throw new CisoFormatException("Corrupt LZ4 block (back-reference before start).");
            for (int i = 0; i < matchLen && dp < dst.Length; i++) dst[dp++] = dst[mp++];
        }
        return dp;
    }
}
