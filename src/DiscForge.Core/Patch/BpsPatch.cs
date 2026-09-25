// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Patch;

/// <summary>Thrown when a file is not a valid/parseable BPS patch, or a checksum fails.</summary>
public sealed class BpsFormatException(string message) : Exception(message);

/// <summary>Parsed BPS header/footer: the sizes, metadata and the three CRC-32s.</summary>
public sealed record BpsPatchFile
{
    public required long SourceSize { get; init; }
    public required long TargetSize { get; init; }
    public required string Metadata { get; init; }
    public required uint SourceChecksum { get; init; }
    public required uint TargetChecksum { get; init; }
    public required uint PatchChecksum { get; init; }
    /// <summary>The raw patch bytes, needed to actually apply it.</summary>
    public required byte[] Raw { get; init; }
    /// <summary>Byte offset in <see cref="Raw"/> where the action stream begins.</summary>
    public required int ActionStart { get; init; }
}

/// <summary>
/// Applies and builds BPS ("Beat Patch System") patches — the modern delta format that
/// superseded IPS: 64-bit sizes (no 16 MiB limit), embedded source/target CRC-32s that
/// verify the patch is being applied to the right file, and a copy model that expresses
/// insertions and moved data compactly.
///
/// Layout: the magic "BPS1", three variable-length integers (source size, target size,
/// metadata size) and the metadata, then an action stream, then a footer of three
/// little-endian CRC-32s (source, target, patch). Each action is a variable-length
/// integer whose low two bits select SourceRead / TargetRead / SourceCopy / TargetCopy
/// and whose upper bits carry the length; SourceCopy/TargetCopy are followed by a
/// signed relative offset. Numbers use Beat's endian-independent varint (each byte
/// holds seven bits; the high bit ends the number; a bias of one is added per continue).
///
/// Clean-room from the public BPS format description; round-trip validated and guarded
/// by the format's own CRC-32s (a patch applied to the wrong source is refused).
/// </summary>
public static class BpsPatch
{
    private static readonly byte[] Magic = "BPS1"u8.ToArray();

    // ---- parse --------------------------------------------------------------

    public static BpsPatchFile Parse(byte[] bps)
    {
        ArgumentNullException.ThrowIfNull(bps);
        if (bps.Length < 4 + 12 || !bps.AsSpan(0, 4).SequenceEqual(Magic))
            throw new BpsFormatException("Not a BPS patch: missing the \"BPS1\" magic.");

        int p = 4;
        long sourceSize = DecodeNumber(bps, ref p);
        long targetSize = DecodeNumber(bps, ref p);
        long metadataSize = DecodeNumber(bps, ref p);
        if (metadataSize < 0 || p + metadataSize > bps.Length - 12)
            throw new BpsFormatException("BPS metadata length is invalid.");
        string metadata = System.Text.Encoding.UTF8.GetString(bps, p, (int)metadataSize);
        p += (int)metadataSize;

        // The last 12 bytes are the three CRC-32s.
        int footer = bps.Length - 12;
        uint sourceCrc = BinaryPrimitives.ReadUInt32LittleEndian(bps.AsSpan(footer, 4));
        uint targetCrc = BinaryPrimitives.ReadUInt32LittleEndian(bps.AsSpan(footer + 4, 4));
        uint patchCrc = BinaryPrimitives.ReadUInt32LittleEndian(bps.AsSpan(footer + 8, 4));

        return new BpsPatchFile
        {
            SourceSize = sourceSize,
            TargetSize = targetSize,
            Metadata = metadata,
            SourceChecksum = sourceCrc,
            TargetChecksum = targetCrc,
            PatchChecksum = patchCrc,
            Raw = bps,
            ActionStart = p,
        };
    }

    public static BpsPatchFile ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path));
    }

    // ---- apply --------------------------------------------------------------

    /// <summary>Apply a BPS patch to <paramref name="source"/>, returning the target
    /// bytes. Verifies the source and target CRC-32s the patch carries; a mismatch
    /// (wrong source file, or a corrupt patch) is refused. Pass
    /// <paramref name="verifySource"/> = false to patch a source whose CRC differs
    /// (e.g. an over-dumped ROM) at your own risk.</summary>
    public static byte[] Apply(BpsPatchFile patch, byte[] source, bool verifySource = true)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length != patch.SourceSize)
            throw new BpsFormatException(
                $"This patch expects a {patch.SourceSize:N0}-byte source, but the file is {source.Length:N0}.");
        if (verifySource && Crc32(source) != patch.SourceChecksum)
            throw new BpsFormatException(
                "The source file's CRC-32 does not match what the patch expects — this patch is for a different file.");

        var bps = patch.Raw;
        var output = new byte[patch.TargetSize];
        int outPos = 0;
        int p = patch.ActionStart;
        int actionEnd = bps.Length - 12;
        long sourceRel = 0, targetRel = 0;

        while (p < actionEnd)
        {
            long value = DecodeNumber(bps, ref p);
            int command = (int)(value & 3);
            long length = (value >> 2) + 1;
            if (outPos + length > output.Length)
                throw new BpsFormatException("BPS action writes past the target size — the patch is corrupt.");

            switch (command)
            {
                case 0:   // SourceRead: copy from source at the current output position
                    for (long k = 0; k < length; k++, outPos++)
                    {
                        if (outPos >= source.Length) throw new BpsFormatException("BPS SourceRead reads past the source.");
                        output[outPos] = source[outPos];
                    }
                    break;

                case 1:   // TargetRead: literal bytes from the patch
                    if (p + length > actionEnd) throw new BpsFormatException("BPS TargetRead runs past the patch.");
                    for (long k = 0; k < length; k++) output[outPos++] = bps[p++];
                    break;

                case 2:   // SourceCopy: seek in the source, then copy
                {
                    long data = DecodeNumber(bps, ref p);
                    sourceRel += (data & 1) != 0 ? -(data >> 1) : (data >> 1);
                    for (long k = 0; k < length; k++)
                    {
                        if (sourceRel < 0 || sourceRel >= source.Length)
                            throw new BpsFormatException("BPS SourceCopy references outside the source.");
                        output[outPos++] = source[sourceRel++];
                    }
                    break;
                }

                default:  // 3 TargetCopy: seek in the already-written output, then copy
                {
                    long data = DecodeNumber(bps, ref p);
                    targetRel += (data & 1) != 0 ? -(data >> 1) : (data >> 1);
                    for (long k = 0; k < length; k++)
                    {
                        if (targetRel < 0 || targetRel >= outPos)
                            throw new BpsFormatException("BPS TargetCopy references output not yet written.");
                        output[outPos++] = output[targetRel++];
                    }
                    break;
                }
            }
        }

        if (outPos != output.Length)
            throw new BpsFormatException("BPS patch produced fewer bytes than the target size.");
        if (Crc32(output) != patch.TargetChecksum)
            throw new BpsFormatException("The patched output's CRC-32 does not match the patch's target — the patch or source is corrupt.");
        return output;
    }

    // ---- create -------------------------------------------------------------

    /// <summary>Build a BPS patch that turns <paramref name="source"/> into
    /// <paramref name="target"/>. Uses SourceRead where the two files already agree and
    /// TargetRead literals elsewhere — a correct, self-verifying patch (not the smallest
    /// possible; the SourceCopy/TargetCopy move-detection an optimiser would add is a
    /// later refinement). <paramref name="metadata"/> is embedded verbatim.</summary>
    public static byte[] Create(byte[] source, byte[] target, string metadata = "")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        using var ms = new MemoryStream();
        ms.Write(Magic);
        EncodeNumber(ms, source.Length);
        EncodeNumber(ms, target.Length);
        var meta = System.Text.Encoding.UTF8.GetBytes(metadata ?? "");
        EncodeNumber(ms, meta.Length);
        ms.Write(meta);

        int i = 0;
        while (i < target.Length)
        {
            // A run the source already matches -> SourceRead.
            int runStart = i;
            while (i < target.Length && i < source.Length && source[i] == target[i]) i++;
            if (i > runStart)
            {
                EncodeAction(ms, command: 0, length: i - runStart);
                continue;
            }

            // Otherwise a run of literal bytes -> TargetRead, up to the next match.
            int litStart = i;
            while (i < target.Length && !(i < source.Length && source[i] == target[i])) i++;
            int litLen = i - litStart;
            EncodeAction(ms, command: 1, length: litLen);
            ms.Write(target, litStart, litLen);
        }

        // Footer: source CRC, target CRC, then the CRC of everything written so far.
        var body = ms.ToArray();
        var withFooter = new byte[body.Length + 12];
        Array.Copy(body, withFooter, body.Length);
        int f = body.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(withFooter.AsSpan(f, 4), Crc32(source));
        BinaryPrimitives.WriteUInt32LittleEndian(withFooter.AsSpan(f + 4, 4), Crc32(target));
        BinaryPrimitives.WriteUInt32LittleEndian(withFooter.AsSpan(f + 8, 4), Crc32(withFooter.AsSpan(0, f + 8)));
        return withFooter;
    }

    private static void EncodeAction(Stream ms, int command, long length)
    {
        if (length <= 0) return;
        EncodeNumber(ms, ((length - 1) << 2) | (uint)command);
    }

    // ---- Beat variable-length integer ---------------------------------------

    private static long DecodeNumber(byte[] data, ref int p)
    {
        long result = 0;
        long shift = 1;
        while (true)
        {
            if (p >= data.Length) throw new BpsFormatException("BPS number ran past the end of the patch.");
            byte x = data[p++];
            result += (x & 0x7FL) * shift;
            if ((x & 0x80) != 0) break;
            shift <<= 7;
            result += shift;
        }
        return result;
    }

    private static void EncodeNumber(Stream ms, long number)
    {
        while (true)
        {
            byte x = (byte)(number & 0x7F);
            number >>= 7;
            if (number == 0) { ms.WriteByte((byte)(0x80 | x)); break; }
            ms.WriteByte(x);
            number -= 1;
        }
    }

    // ---- CRC-32 (zlib polynomial) -------------------------------------------

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>Standard CRC-32 (zlib/PNG polynomial) — the checksum BPS uses.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
