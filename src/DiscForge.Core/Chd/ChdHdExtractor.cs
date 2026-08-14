// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DiscForge.Core.Chd;

/// <summary>
/// Extracts a hard-disk (non-CD) CHD v5 to its raw disk image. Where
/// <see cref="ChdExtractor"/> handles CD CHDs (frames, sub-channel, ECC), a hard-disk
/// CHD's logical data <em>is</em> the flat image, so this simply decodes every hunk in
/// order: a compressed hunk through its codec (zlib via <see cref="ChdInflate"/>, LZMA
/// via <see cref="ChdLzma"/>, huff via <see cref="ChdHuff"/>, or FLAC via
/// <see cref="ChdFlac"/> — the hard-disk flac hunk carries a leading byte-order marker
/// before its frames), an uncompressed (NONE) hunk verbatim, a SELF hunk by copying an
/// earlier hunk, and a PARENT hunk by copying the same range from a supplied parent CHD
/// (a multi-level parent chain is resolved level by level). It reads the compressed
/// hunk map (<see cref="ChdMap"/>), which self-verifies against its own CRC-16, and
/// checks the whole result against the CHD's stored SHA-1 — so extraction is either
/// byte-exact or declined.
///
/// Clean-room, from the public CHD v5 map / LZMA formats and RFC 1951; validated
/// against chdman-produced hard-disk and parent/child images by each map's CRC-16 and
/// the CHD SHA-1.
/// </summary>
public static class ChdHdExtractor
{
    /// <summary>
    /// Decode a hard-disk CHD to its raw image bytes. Pass <paramref name="parent"/>
    /// when the CHD is a delta (child) image; otherwise a CHD that needs a parent is
    /// declined.
    /// </summary>
    public static byte[] Extract(byte[] chd, byte[]? parent = null)
        => Extract(chd, parent is null ? Array.Empty<byte[]>() : new[] { parent });

    /// <summary>
    /// Decode a hard-disk CHD, resolving a multi-level parent chain. <paramref name="parentChain"/>
    /// is ordered nearest-first (element 0 is the immediate parent).
    /// </summary>
    public static byte[] Extract(byte[] chd, byte[][] parentChain)
    {
        ArgumentNullException.ThrowIfNull(chd);
        ArgumentNullException.ThrowIfNull(parentChain);
        var info = ChdReader.Read(chd);

        if (info.IsCd)
            throw new ChdFormatException("This is a CD CHD — use ExtractCd, which produces a bin/cue.");

        // A CHD may *list* codecs (e.g. huff) it never actually uses; only the hunks
        // that need an unsupported codec are declined, during decode, with a clear
        // message — so a child image that merely lists huff still extracts.
        return DecodeHdLogical(chd, info, parentChain);
    }

    private static byte[] DecodeHdLogical(byte[] chd, ChdInfo info, byte[][] parentChain)
    {
        long logical = info.LogicalBytes;
        int hunkbytes = info.HunkBytes;
        if (hunkbytes <= 0)
            throw new ChdFormatException("CHD hunk size is invalid — the header is corrupt.");
        if (logical < 0 || logical > ChdExtractor.MaxInMemoryBytes)
            throw new ChdFormatException(
                $"This CHD's logical size ({logical:N0} bytes) is too large to extract in memory. " +
                "Extract it with chdman instead.");
        int numHunks = (int)((logical + hunkbytes - 1) / hunkbytes);
        long mapOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(chd.AsSpan(0x28));
        byte[] rawSha1 = chd.AsSpan(0x40, 20).ToArray();

        // Decode the (size-bounded) map first, so a corrupt header claiming a huge image
        // is refused before the large output buffer is allocated. An uncompressed CHD
        // (all four compressors "none") uses a flat 4-byte-per-hunk map, not the
        // compressed bitstream.
        bool uncompressed = info.Compressors.All(c => c is "none" or "");
        ChdMapEntry[] map = uncompressed
            ? ChdMap.DecodeUncompressed(chd, mapOffset, numHunks, hunkbytes, info.UnitBytes, parentChain.Length > 0)
            : ChdMap.Decode(chd, mapOffset, numHunks, hunkbytes, info.UnitBytes);
        var raw = new byte[logical];
        byte[]? parentRaw = null;

        for (int h = 0; h < numHunks; h++)
        {
            var entry = map[h];
            long hunkOffset = (long)h * hunkbytes;
            int copy = (int)Math.Min(hunkbytes, logical - hunkOffset);
            switch (entry.Type)
            {
                case ChdHunkType.Codec0:
                case ChdHunkType.Codec1:
                case ChdHunkType.Codec2:
                case ChdHunkType.Codec3:
                {
                    int codecIndex = (int)entry.Type;   // Codec0..3 == 0..3
                    string codec = codecIndex < info.Compressors.Count ? info.Compressors[codecIndex] : "";
                    byte[] data = DecodeCodecHunk(chd, (int)entry.Offset, (int)entry.Length, hunkbytes, codec, h);
                    Array.Copy(data, 0, raw, hunkOffset, copy);
                    break;
                }

                case ChdHunkType.None:
                    if (entry.Offset + copy > chd.Length)
                        throw new ChdFormatException("CHD hunk stream ended early — the file is truncated.");
                    Array.Copy(chd, (int)entry.Offset, raw, hunkOffset, copy);
                    break;

                case ChdHunkType.Self:
                {
                    long src = entry.Offset * hunkbytes;
                    if (entry.Offset >= h || src + copy > raw.Length)
                        throw new ChdFormatException($"Hunk {h} references hunk {entry.Offset}, which is not available.");
                    Array.Copy(raw, src, raw, hunkOffset, copy);
                    break;
                }

                case ChdHunkType.Zero:
                    // Unwritten hunk in an uncompressed CHD with no parent: the output
                    // buffer is already zero-initialised, so there is nothing to copy.
                    break;

                default:  // Parent
                {
                    if (parentChain.Length == 0)
                        throw new ChdFormatException(
                            "This CHD references a parent CHD (delta/child image), which needs the parent file to " +
                            "resolve. Supply the parent, or recombine them with chdman.");
                    if (parentRaw is null)
                    {
                        var parentBytes = parentChain[0];
                        var parentInfo = ChdReader.Read(parentBytes);
                        if (parentInfo.HunkBytes != hunkbytes || parentInfo.UnitBytes != info.UnitBytes)
                            throw new ChdFormatException("The supplied parent CHD does not match this image's hunk geometry.");
                        parentRaw = DecodeHdLogical(parentBytes, parentInfo, parentChain[1..]);
                    }
                    long src = entry.Offset * info.UnitBytes;
                    if (src < 0 || src + copy > parentRaw.Length)
                        throw new ChdFormatException($"Hunk {h} references parent data outside the parent image.");
                    Array.Copy(parentRaw, src, raw, hunkOffset, copy);
                    break;
                }
            }
        }

        // An uncompressed CHD records no SHA-1 (chdman: "no verification to be done"),
        // leaving the field all-zero — there is nothing to check it against, so only
        // gate on the SHA-1 when the CHD actually stored one.
        bool hasStoredSha1 = rawSha1.Any(b => b != 0);
        if (hasStoredSha1 && !SHA1.HashData(raw).AsSpan().SequenceEqual(rawSha1))
            throw new ChdFormatException(
                "The decompressed data did not match the CHD's stored SHA-1, so extraction was declined rather than " +
                "risk writing an incorrect image.");
        return raw;
    }

    private static byte[] DecodeCodecHunk(byte[] chd, int offset, int length, int hunkbytes, string codec, int hunk)
    {
        if (offset < 0 || offset > chd.Length)
            throw new ChdFormatException($"Hunk {hunk} points outside the file.");
        try
        {
            return DecodeCodecHunkCore(chd, offset, length, hunkbytes, codec, hunk);
        }
        catch (ChdFormatException) { throw; }
        catch (Exception ex)   // a malformed codec stream must decline, not crash the caller
        {
            throw new ChdFormatException($"Hunk {hunk} ('{codec}') could not be decoded: {ex.Message}");
        }
    }

    private static byte[] DecodeCodecHunkCore(byte[] chd, int offset, int length, int hunkbytes, string codec, int hunk)
    {
        byte[] data;
        switch (codec)
        {
            case "zlib":
                data = new ChdInflate(chd, offset).Run();
                break;
            case "lzma":
                data = ChdLzma.Decode(chd, offset, hunkbytes);
                break;
            case "zstd":
                data = Compression.ZstdDecoder.Decompress(chd.AsSpan(offset, length));
                break;
            case "huff":
                data = ChdHuff.Decode(chd, offset, length, hunkbytes);
                break;
            case "flac":
            {
                // A hard-disk flac hunk begins with a 1-byte sample byte-order marker
                // ('L' little-endian, 'B' big-endian) then raw FLAC frames. ChdFlac
                // yields big-endian interleaved samples; swap to little-endian if asked.
                byte order = chd[offset];
                data = ChdFlac.Decode(chd, offset + 1, hunkbytes).Bytes;
                if (order == (byte)'L')
                {
                    for (int i = 0; i + 1 < data.Length; i += 2) (data[i], data[i + 1]) = (data[i + 1], data[i]);
                }
                else if (order != (byte)'B')
                    throw new ChdFormatException($"Hunk {hunk} has an unrecognised flac byte-order marker 0x{order:X2}.");
                break;
            }
            case "none":
            case "":
                data = chd.AsSpan(offset, hunkbytes).ToArray();
                break;
            default:
                throw new ChdFormatException($"Hunk {hunk} uses the unsupported '{codec}' codec.");
        }
        if (data.Length != hunkbytes)
            throw new ChdFormatException($"Hunk {hunk} decoded to {data.Length} bytes, expected {hunkbytes}.");
        return data;
    }
}
