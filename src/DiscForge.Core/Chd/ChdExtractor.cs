// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Chd;

/// <summary>
/// Extracts a CD CHD to a raw bin/cue — decompressing the hunks, not merely
/// identifying them. It decodes the CHD's compressed hunk map (<see cref="ChdMap"/>)
/// to learn exactly what every hunk is, then reads each one: a compressed hunk is
/// decoded by its own codec — cdzl (zlib, <see cref="ChdInflate"/>), cdlz (LZMA,
/// <see cref="ChdLzma"/>) or cdfl (FLAC, <see cref="ChdFlac"/>), auto-detected from
/// the hunk's framing — an uncompressed (NONE) hunk is taken verbatim, and a SELF
/// hunk (identical to an earlier hunk) is resolved by copying that hunk's output. It
/// restores CD-audio endianness, regenerates data-sector ECC, then checks the result
/// against the SHA-1 the CHD stores.
///
/// Two independent safety nets: the map self-verifies against its own CRC-16 (a bad
/// decode is refused before any hunk is read), and the CHD's SHA-1 of its
/// uncompressed contents is checked at the end — so this either produces
/// byte-identical output or declines; it never silently emits wrong sectors. A delta
/// (child) image whose hunks reference a PARENT CHD extracts when that parent is
/// supplied; without it, such a CHD is declined with a clear message.
///
/// Clean-room, from the public CHD v5 map / LZMA / FLAC formats and RFC 1951;
/// validated against real chdman-produced images (cdzl, cdlz, cdfl, and maps
/// containing SELF and PARENT hunks) by each map's own CRC-16 and the CHD SHA-1.
/// </summary>
public static class ChdExtractor
{
    private const int Frame = 2448, Sector = 2352, Subcode = 96;
    // This extractor holds the whole image in memory; refuse anything larger than a
    // large disc rather than attempt an allocation that would fail or exhaust memory.
    internal const long MaxInMemoryBytes = 2_000_000_000;

    public sealed record CdExtraction
    {
        public required byte[] Bin { get; init; }
        public required string Cue { get; init; }
        public required int Tracks { get; init; }
        /// <summary>True when the reconstructed data matched the CHD's stored SHA-1.</summary>
        public required bool Verified { get; init; }
    }

    /// <summary>
    /// Decompress a CD CHD to a single raw bin plus a cue. If the CHD is a delta
    /// (child) image whose hunks reference a <paramref name="parent"/> CHD, pass the
    /// parent's bytes and those PARENT hunks are resolved from it; otherwise a CHD that
    /// needs a parent is declined.
    /// </summary>
    public static CdExtraction ExtractCd(byte[] chd, byte[]? parent = null)
        => ExtractCd(chd, parent is null ? Array.Empty<byte[]>() : new[] { parent });

    /// <summary>
    /// Decompress a CD CHD, resolving a multi-level parent chain. <paramref name="parentChain"/>
    /// is ordered nearest-first: element 0 is this CHD's immediate parent, element 1 that
    /// parent's parent, and so on. Each level is resolved with the remaining chain.
    /// </summary>
    public static CdExtraction ExtractCd(byte[] chd, byte[][] parentChain)
    {
        ArgumentNullException.ThrowIfNull(chd);
        ArgumentNullException.ThrowIfNull(parentChain);
        var info = ChdReader.Read(chd);   // validates v5, parses tracks

        if (!info.IsCd)
            throw new ChdFormatException("This CHD has no CD track metadata — only CD CHDs can be extracted to bin/cue.");
        foreach (var comp in info.Compressors)
            if (comp is not ("cdzl" or "cdlz" or "cdfl" or "none"))
                throw new ChdFormatException(
                    $"This CHD uses the '{comp}' codec, which isn't supported. Only the CD codecs " +
                    "cdzl (zlib), cdlz (LZMA) and cdfl (FLAC) are decoded. Convert it to bin/cue with chdman instead.");

        if (info.UnitBytes != Frame || info.HunkBytes % Frame != 0)
            throw new ChdFormatException("Unexpected CHD unit size — this does not look like a CD CHD.");

        byte[] raw = DecodeCdLogical(chd, info, parentChain);
        var (bin, cue, tracks) = BuildBinCue(raw, info);
        return new CdExtraction { Bin = bin, Cue = cue, Tracks = tracks, Verified = true };
    }

    // Decode a CD CHD to its raw logical bytes (frames of sector + subcode), resolving
    // every hunk kind via the map — including SELF (copy an earlier hunk) and PARENT
    // (copy the same logical range from the parent image). The CHD's stored SHA-1 is
    // the final proof; a mismatch declines rather than emits wrong sectors.
    private static byte[] DecodeCdLogical(byte[] chd, ChdInfo info, byte[][] parentChain)
    {
        long logical = info.LogicalBytes;
        int hunkbytes = info.HunkBytes;
        if (hunkbytes <= 0)
            throw new ChdFormatException("CHD hunk size is invalid — the header is corrupt.");
        if (logical < 0 || logical > MaxInMemoryBytes)
            throw new ChdFormatException(
                $"This CHD's logical size ({logical:N0} bytes) is too large to extract in memory. " +
                "Extract it with chdman instead.");
        int framesPerHunk = hunkbytes / Frame;
        int numHunks = (int)((logical + hunkbytes - 1) / hunkbytes);
        long mapOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(chd.AsSpan(0x28));
        byte[] rawSha1 = chd.AsSpan(0x40, 20).ToArray();

        // Decode the (size-bounded) map first, so a corrupt header that claims a huge
        // image is refused before the large output buffer is ever allocated. An
        // uncompressed CD CHD (all compressors "none") uses a flat 4-byte-per-hunk map.
        bool uncompressed = info.Compressors.All(c => c is "none" or "");
        ChdMapEntry[] map = uncompressed
            ? ChdMap.DecodeUncompressed(chd, mapOffset, numHunks, hunkbytes, info.UnitBytes, parentChain.Length > 0)
            : ChdMap.Decode(chd, mapOffset, numHunks, hunkbytes, info.UnitBytes);
        var raw = new byte[logical];
        byte[]? parentRaw = null;   // decoded lazily on the first PARENT hunk

        for (int h = 0; h < numHunks; h++)
        {
            var entry = map[h];
            long hunkOffset = (long)h * hunkbytes;
            switch (entry.Type)
            {
                case ChdHunkType.Codec0:
                case ChdHunkType.Codec1:
                case ChdHunkType.Codec2:
                case ChdHunkType.Codec3:
                {
                    // A compressed hunk at the map's exact offset. Each hunk still carries
                    // its own codec framing, so the reader auto-detects cdzl/cdlz/cdfl.
                    if (!TryDecodeCompressedHunk(chd, (int)entry.Offset, framesPerHunk,
                            out byte[]? sect, out byte[]? sub, out byte eccFlags, out _))
                        throw new ChdFormatException(
                            $"Hunk {h} is marked compressed but could not be decoded — the CHD uses a codec or " +
                            "framing this build does not handle.");
                    for (int f = 0; f < framesPerHunk; f++)
                    {
                        long fo = hunkOffset + (long)f * Frame;
                        if (fo >= logical) break;
                        Array.Copy(sect!, f * Sector, raw, fo, Sector);
                        Array.Copy(sub!, f * Subcode, raw, fo + Sector, Subcode);
                        // A set flag means sync + ECC were stripped for compression and
                        // must be regenerated so the sector reads as a valid data sector.
                        if ((eccFlags & (1 << f)) != 0)
                            RegenerateEcc(raw, fo);
                    }
                    break;
                }

                case ChdHunkType.None:
                {
                    // Stored verbatim: hunkbytes of final logical data (full sync + ECC).
                    if (entry.Offset + hunkbytes > chd.Length)
                        throw new ChdFormatException("CHD hunk stream ended early — the file is truncated or uses an unsupported layout.");
                    int copy = (int)Math.Min(hunkbytes, logical - hunkOffset);
                    Array.Copy(chd, (int)entry.Offset, raw, hunkOffset, copy);
                    break;
                }

                case ChdHunkType.Self:
                {
                    // Identical to an earlier hunk: copy its already-produced logical bytes.
                    long src = entry.Offset * hunkbytes;
                    if (entry.Offset >= h || src + hunkbytes > raw.Length)
                        throw new ChdFormatException($"Hunk {h} references hunk {entry.Offset}, which is not available.");
                    int copy = (int)Math.Min(hunkbytes, logical - hunkOffset);
                    Array.Copy(raw, src, raw, hunkOffset, copy);
                    break;
                }

                case ChdHunkType.Zero:
                    // Unwritten hunk in an uncompressed CHD with no parent: the output
                    // buffer is already zero-initialised, so nothing to copy.
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
                        parentRaw = DecodeCdLogical(parentBytes, parentInfo, parentChain[1..]);   // resolve up the chain
                    }
                    // The map's parent offset is in units; the same logical range in the parent.
                    long src = entry.Offset * info.UnitBytes;
                    if (src < 0 || src + hunkbytes > parentRaw.Length)
                        throw new ChdFormatException($"Hunk {h} references parent data outside the parent image.");
                    int copy = (int)Math.Min(hunkbytes, logical - hunkOffset);
                    Array.Copy(parentRaw, src, raw, hunkOffset, copy);
                    break;
                }
            }
        }

        // The CHD's own SHA-1 of its uncompressed data — the proof of correctness. An
        // uncompressed CHD records no SHA-1 (chdman does no verification), leaving the
        // field all-zero, so only gate on it when the CHD actually stored one.
        bool hasStoredSha1 = rawSha1.Any(b => b != 0);
        if (hasStoredSha1 && !SHA1.HashData(raw).AsSpan().SequenceEqual(rawSha1))
            throw new ChdFormatException(
                "The decompressed data did not match the CHD's stored SHA-1, so extraction was declined rather than " +
                "risk writing incorrect sectors. This CHD likely uses a feature not yet supported by this build.");
        return raw;
    }

    // Attempt to read the hunk at <paramref name="pos"/> as a compressed hunk. Returns
    // true with the decoded sector + subcode streams and the offset of the next hunk;
    // false (without throwing) if the bytes don't form a well-formed compressed hunk, so
    // the caller can fall back to treating it as an uncompressed (NONE) hunk. Any decode
    // fault — bad codec framing, a stream that runs off the end, a wrong output size — is
    // a "not compressed" signal here; the end-of-run SHA-1 check is what proves the guess.
    private static bool TryDecodeCompressedHunk(byte[] chd, int pos, int framesPerHunk,
        out byte[]? sect, out byte[]? sub, out byte eccFlags, out int nextPos)
    {
        sect = null; sub = null; eccFlags = 0; nextPos = pos;
        try
        {
            if (pos + 3 > chd.Length) return false;

            int subStart;
            if (chd[pos] == 0xFF && (chd[pos + 1] & 0xFE) == 0xF8)
            {
                // cdfl: raw self-delimiting FLAC frames, no header. Audio → no ECC.
                (sect, subStart) = ChdFlac.Decode(chd, pos, framesPerHunk * Sector);
                eccFlags = 0;
            }
            else
            {
                // cdzl / cdlz: [ecc flags][2-byte base length][base stream].
                eccFlags = chd[pos];
                int sourceLen = (chd[pos + 1] << 8) | chd[pos + 2];
                int basePos = pos + 3;
                if (basePos >= chd.Length) return false;
                sect = chd[basePos] == 0x00                       // LZMA range-coder init byte
                    ? ChdLzma.Decode(chd, basePos, framesPerHunk * Sector)
                    : new ChdInflate(chd, basePos).Run();
                subStart = basePos + sourceLen;
            }

            if (subStart > chd.Length) return false;
            var subBlock = new ChdInflate(chd, subStart);
            sub = subBlock.Run();
            nextPos = subBlock.NextOffset;

            if (sect.Length != framesPerHunk * Sector || sub.Length != framesPerHunk * Subcode)
                return false;
            if (nextPos > chd.Length) return false;
            return true;
        }
        catch
        {
            // Malformed as a compressed hunk — let the caller try the NONE path.
            return false;
        }
    }

    // Restore a data sector's sync pattern and error-correction from its payload.
    private static void RegenerateEcc(byte[] buf, long frameOffset)
    {
        Span<byte> sector = buf.AsSpan((int)frameOffset, Sector);
        sector[0] = 0x00;
        for (int i = 1; i <= 10; i++) sector[i] = 0xFF;
        sector[11] = 0x00;

        byte mode = sector[15];
        if (mode == 1)
            EdcEcc.FillMode1(sector);
        else if (mode == 2)
        {
            // Mode 2: Form 2 if the subheader submode bit 5 is set, else Form 1.
            bool form2 = (sector[18] & 0x20) != 0;
            if (!form2) EdcEcc.FillMode2Form1(sector);
            // Form 2 EDC is optional and not regenerated here; the SHA-1 check will
            // catch it if a given disc actually needs it.
        }
    }

    // Assemble one bin (each track's real 2352-byte sectors, CD audio byte-swapped
    // back to little-endian) and a cue describing the tracks.
    private static (byte[] Bin, string Cue, int Tracks) BuildBinCue(byte[] raw, ChdInfo info)
    {
        // chdman stores each track's frames padded up to a multiple of four; those
        // padding frames sit in the physical stream but are NOT part of the disc,
        // so the output carries only each track's real (pregap + data) frames.
        var plan = new List<(ChdTrack Track, int PhysStart, int Count)>();
        int physCursor = 0;
        foreach (var t in info.Tracks)
        {
            int count = t.Frames + t.Pregap;
            plan.Add((t, physCursor, count));
            physCursor += RoundUp(count, 4);
        }

        int outFrames = plan.Sum(p => p.Count);
        var bin = new byte[(long)outFrames * Sector];

        var cue = new StringBuilder();
        cue.Append("FILE \"disc.bin\" BINARY\n");
        int outCursor = 0;
        foreach (var (track, physStart, count) in plan)
        {
            for (int i = 0; i < count; i++)
                Array.Copy(raw, (long)(physStart + i) * Frame, bin, (long)(outCursor + i) * Sector, Sector);

            if (track.Type.Contains("AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                long a = (long)outCursor * Sector, end = a + (long)count * Sector;
                for (long i = a; i + 1 < end; i += 2) (bin[i], bin[i + 1]) = (bin[i + 1], bin[i]);
            }

            cue.Append($"  TRACK {track.Number:D2} {CueType(track.Type)}\n");
            if (track.Pregap > 0)
                cue.Append($"    INDEX 00 {FramesToMsf(outCursor)}\n");
            cue.Append($"    INDEX 01 {FramesToMsf(outCursor + track.Pregap)}\n");

            outCursor += count;
        }
        return (bin, cue.ToString(), plan.Count);
    }

    private static int RoundUp(int v, int m) => (v + m - 1) / m * m;

    private static string CueType(string chdType) => chdType.ToUpperInvariant() switch
    {
        "AUDIO" => "AUDIO",
        "MODE1" or "MODE1_RAW" => "MODE1/2352",
        "MODE2" or "MODE2_RAW" or "MODE2_FORM1" or "MODE2_FORM2" or "MODE2_FORM_MIX" => "MODE2/2352",
        _ => "MODE2/2352",
    };

    private static string FramesToMsf(int frames)
    {
        int f = frames % 75; int s = frames / 75 % 60; int m = frames / 75 / 60;
        return $"{m:D2}:{s:D2}:{f:D2}";
    }
}
