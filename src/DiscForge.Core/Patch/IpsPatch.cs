// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Patch;

/// <summary>Thrown when a file is not a valid/parseable IPS patch.</summary>
public sealed class IpsFormatException(string message) : Exception(message);

/// <summary>One IPS change: write <see cref="Data"/> at <see cref="Offset"/>. An RLE
/// record is expanded to its literal bytes on parse, so consumers see one uniform shape.</summary>
public sealed record IpsRecord
{
    public required int Offset { get; init; }
    public required byte[] Data { get; init; }
    public bool WasRle { get; init; }
}

/// <summary>Parsed IPS patch: its records and the optional truncate length.</summary>
public sealed record IpsPatchFile
{
    public required IReadOnlyList<IpsRecord> Records { get; init; }
    /// <summary>The IPS "truncate" extension: if present, the output is cut to this
    /// length after applying. Null when the patch does not truncate.</summary>
    public int? TruncateLength { get; init; }

    /// <summary>The highest byte offset any record touches (0 if empty).</summary>
    public int MaxTouchedOffset =>
        Records.Count == 0 ? 0 : Records.Max(r => r.Offset + r.Data.Length);
}

/// <summary>
/// Applies and builds IPS ("International Patching System") patches — the oldest and
/// simplest ROM/image patch format, still ubiquitous for translations and hacks.
///
/// Layout: the ASCII magic "PATCH", then a sequence of records terminated by "EOF".
/// Each record is a 24-bit big-endian offset and a 16-bit big-endian size; a size of
/// zero marks an RLE record — a 16-bit run length and one byte to repeat. An optional
/// 24-bit big-endian value after "EOF" is the truncate extension (cut the output to
/// that length). Offsets are 24-bit, so IPS cannot address past 16 MiB — a real limit
/// of the format, surfaced here rather than silently wrapping.
///
/// Clean-room from the public IPS format description; round-trip validated (build a
/// patch from two files, apply it, get the second file back).
/// </summary>
public static class IpsPatch
{
    private static readonly byte[] Magic = "PATCH"u8.ToArray();
    private static readonly byte[] Eof = "EOF"u8.ToArray();

    /// <summary>The largest offset IPS can address (24-bit).</summary>
    public const int MaxAddressable = 0xFFFFFF;

    public static IpsPatchFile Parse(byte[] ips)
    {
        ArgumentNullException.ThrowIfNull(ips);
        if (ips.Length < 5 + 3 || !ips.AsSpan(0, 5).SequenceEqual(Magic))
            throw new IpsFormatException("Not an IPS patch: missing the \"PATCH\" magic.");

        var records = new List<IpsRecord>();
        int p = 5;
        while (true)
        {
            if (p + 3 > ips.Length)
                throw new IpsFormatException("IPS patch ended before the \"EOF\" marker.");

            // The record's offset — or the "EOF" terminator in the same three bytes.
            if (ips.AsSpan(p, 3).SequenceEqual(Eof))
            {
                p += 3;
                int? truncate = null;
                if (p + 3 <= ips.Length)               // optional truncate extension
                    truncate = (ips[p] << 16) | (ips[p + 1] << 8) | ips[p + 2];
                return new IpsPatchFile { Records = records, TruncateLength = truncate };
            }

            int offset = (ips[p] << 16) | (ips[p + 1] << 8) | ips[p + 2];
            p += 3;
            if (p + 2 > ips.Length) throw new IpsFormatException("IPS record is truncated (no size).");
            int size = (ips[p] << 8) | ips[p + 1];
            p += 2;

            if (size == 0)
            {
                // RLE: a 16-bit run length and the byte to repeat.
                if (p + 3 > ips.Length) throw new IpsFormatException("IPS RLE record is truncated.");
                int runLen = (ips[p] << 8) | ips[p + 1];
                byte value = ips[p + 2];
                p += 3;
                if (runLen == 0) throw new IpsFormatException("IPS RLE record has a zero run length.");
                var data = new byte[runLen];
                Array.Fill(data, value);
                records.Add(new IpsRecord { Offset = offset, Data = data, WasRle = true });
            }
            else
            {
                if (p + size > ips.Length) throw new IpsFormatException("IPS record data runs past the end of the patch.");
                records.Add(new IpsRecord { Offset = offset, Data = ips.AsSpan(p, size).ToArray() });
                p += size;
            }
        }
    }

    public static IpsPatchFile ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path));
    }

    /// <summary>Apply an IPS patch to <paramref name="source"/>, returning the patched
    /// bytes. The output grows to cover any record that writes past the current end,
    /// then is truncated if the patch carries a truncate length.</summary>
    public static byte[] Apply(IpsPatchFile patch, byte[] source)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(source);

        int needed = source.Length;
        foreach (var r in patch.Records) needed = Math.Max(needed, r.Offset + r.Data.Length);

        var output = new byte[needed];
        Array.Copy(source, output, source.Length);
        foreach (var r in patch.Records)
            Array.Copy(r.Data, 0, output, r.Offset, r.Data.Length);

        if (patch.TruncateLength is { } t && t < output.Length)
        {
            var cut = new byte[t];
            Array.Copy(output, cut, t);
            return cut;
        }
        return output;
    }

    /// <summary>Build an IPS patch that turns <paramref name="original"/> into
    /// <paramref name="modified"/>. Differing spans become records; long identical runs
    /// within a record are emitted as RLE. Both files must fit IPS's 16 MiB addressing.</summary>
    public static byte[] Create(byte[] original, byte[] modified)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        if (modified.Length > MaxAddressable + 1)
            throw new IpsFormatException(
                $"IPS cannot address past {MaxAddressable + 1:N0} bytes; the modified file is {modified.Length:N0}. " +
                "Use BPS for larger images.");

        using var ms = new MemoryStream();
        ms.Write(Magic);

        int i = 0;
        while (i < modified.Length)
        {
            // Skip spans that are unchanged (and within the original's length).
            bool same = i < original.Length && original[i] == modified[i];
            if (same) { i++; continue; }

            int start = i;
            // The offset 0x454F46 ("EOF") cannot begin a record — it would be read as the
            // terminator. Start one byte earlier so the real byte is still written; the
            // extra leading byte carries its own (modified) value, which is harmless.
            if (start == 0x454F46 && start > 0) start -= 1;

            // Extend the differing run. IPS records cap at 0xFFFF bytes; split beyond that.
            while (i < modified.Length && (i >= original.Length || original[i] != modified[i]))
            {
                if (i - start >= 0xFFFF) break;
                i++;
            }
            EmitRecord(ms, start, modified.AsSpan(start, i - start));
        }

        ms.Write(Eof);
        // Record the length when the patch shortens the file (truncate extension).
        if (modified.Length < original.Length)
        {
            ms.WriteByte((byte)(modified.Length >> 16));
            ms.WriteByte((byte)(modified.Length >> 8));
            ms.WriteByte((byte)modified.Length);
        }
        return ms.ToArray();
    }

    // Write one record, choosing RLE when the span is a single repeated byte long
    // enough to pay for itself (RLE costs 3 bytes of payload regardless of run length).
    private static void EmitRecord(Stream ms, int offset, ReadOnlySpan<byte> data)
    {
        // Try RLE: only when the whole span is one value and it saves space.
        bool uniform = true;
        for (int k = 1; k < data.Length; k++) if (data[k] != data[0]) { uniform = false; break; }
        if (uniform && data.Length > 3)
        {
            WriteOffset(ms, offset);
            ms.WriteByte(0); ms.WriteByte(0);                 // size 0 => RLE
            ms.WriteByte((byte)(data.Length >> 8)); ms.WriteByte((byte)data.Length);
            ms.WriteByte(data[0]);
            return;
        }

        WriteOffset(ms, offset);
        ms.WriteByte((byte)(data.Length >> 8)); ms.WriteByte((byte)data.Length);
        ms.Write(data);
    }

    private static void WriteOffset(Stream ms, int offset)
    {
        ms.WriteByte((byte)(offset >> 16));
        ms.WriteByte((byte)(offset >> 8));
        ms.WriteByte((byte)offset);
    }
}
