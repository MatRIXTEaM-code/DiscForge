// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Iso;

/// <summary>One entry in an image's filesystem.</summary>
public sealed record IsoEntry
{
    public required string Name { get; init; }
    /// <summary>Full path from the root, using '/'.</summary>
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    /// <summary>First sector of this entry's data.</summary>
    public required uint Extent { get; init; }
    /// <summary>Length in bytes.</summary>
    public required uint Size { get; init; }

    /// <summary>Sector range this entry occupies — useful for working out whether
    /// a bad sector actually cost you anything.</summary>
    public uint SectorCount => (Size + 2047) / 2048;
    public uint LastSector => Extent + (SectorCount == 0 ? 0 : SectorCount - 1);
}

/// <summary>What was found on the image.</summary>
public sealed record IsoDirectory
{
    public required string VolumeId { get; init; }
    /// <summary>True when names came from the Joliet (UCS-2) hierarchy.</summary>
    public required bool Joliet { get; init; }
    /// <summary>True when POSIX names were found via Rock Ridge NM entries.</summary>
    public required bool RockRidge { get; init; }
    public required IReadOnlyList<IsoEntry> Entries { get; init; }

    public IEnumerable<IsoEntry> Files => Entries.Where(e => !e.IsDirectory);
    public IEnumerable<IsoEntry> Directories => Entries.Where(e => e.IsDirectory);
    public long TotalBytes => Files.Sum(f => (long)f.Size);
}

public sealed class IsoFormatException(string message) : Exception(message);

/// <summary>
/// Reads the filesystem out of an ISO 9660 image — the mirror of IsoBuilder.
/// Answers "what is actually on this disc?" without mounting or burning it.
///
/// Reading is fiddlier than writing, because the image decides the rules:
///  - the PVD (sector 16) describes the 8.3 hierarchy;
///  - a type-2 SVD carrying a UCS-2 escape describes a parallel Joliet tree with
///    long names — preferred when present;
///  - Rock Ridge hides POSIX names in the System Use area AFTER the identifier,
///    in an NM entry that must be found by walking SUSP entries;
///  - a directory record with length 0 means "skip to the next sector boundary",
///    NOT "end of directory" — miss that and you lose files.
///
/// Validated in docs/reference/iso_read.py against our own builders' output and
/// against `isoinfo` on a real ISO.
/// </summary>
public static class IsoReader
{
    public const int SectorSize = 2048;
    private const int MaxDepth = 32;

    /// <summary>Which name hierarchy to read.</summary>
    public enum NamePreference
    {
        /// <summary>Joliet if present, else ISO 9660 (+ Rock Ridge names when found).</summary>
        Auto,
        /// <summary>Force the ISO 9660 hierarchy (Rock Ridge names still apply).</summary>
        Iso9660,
        /// <summary>Force the Joliet hierarchy; fails if the image has none.</summary>
        Joliet,
    }

    /// <summary>
    /// Read the filesystem from a seekable stream positioned over an ISO image
    /// (e.g. a CDI data track's cooked user data).
    /// </summary>
    public static IsoDirectory Read(Stream iso, NamePreference prefer = NamePreference.Auto)
        => Read(iso, 0, prefer);

    /// <summary>
    /// Read the filesystem from a stream whose ISO is addressed from a non-zero
    /// base LBA — a Dreamcast GD-ROM's high-density data track, whose descriptors
    /// sit at track-relative sector 16 but whose extents are absolute, based at
    /// the track's start LBA (45000). Pass that LBA as <paramref name="baseLba"/>;
    /// zero gives the ordinary behaviour, unchanged.
    /// </summary>
    public static IsoDirectory Read(Stream iso, long baseLba, NamePreference prefer = NamePreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(iso);
        if (!iso.CanSeek)
            throw new ArgumentException("Reading an ISO requires a seekable stream.", nameof(iso));
        if (baseLba < 0) throw new ArgumentOutOfRangeException(nameof(baseLba));

        // A base LBA is handled by presenting the track in absolute-LBA space: a
        // read at absolute byte P maps to track byte P − baseLba×2048, zeros
        // below. Every offset read then Just Works, and the only other change is
        // scanning for the descriptors around baseLba+16 rather than 16.
        Stream s = baseLba > 0 ? new RebaseStream(iso, baseLba * SectorSize) : iso;

        var (pvd, svd) = FindDescriptors(s, baseLba);

        bool useJoliet = prefer switch
        {
            NamePreference.Joliet when svd is null =>
                throw new IsoFormatException("The image has no Joliet hierarchy."),
            NamePreference.Joliet => true,
            NamePreference.Iso9660 => false,
            _ => svd is not null,
        };

        var desc = useJoliet ? svd! : pvd;
        uint rootExtent = BinaryPrimitives.ReadUInt32LittleEndian(desc.AsSpan(156 + 2, 4));
        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(desc.AsSpan(156 + 10, 4));

        // Rock Ridge lives in the primary hierarchy only.
        bool rockRidge = !useJoliet && DetectRockRidge(s, rootExtent, rootSize);

        var entries = new List<IsoEntry>();
        Recurse(s, rootExtent, rootSize, "", useJoliet, rockRidge, entries, 0);

        return new IsoDirectory
        {
            VolumeId = ReadVolumeId(desc, useJoliet),
            Joliet = useJoliet,
            RockRidge = rockRidge,
            Entries = entries,
        };
    }

    /// <summary>Copy one file's bytes out of the image.</summary>
    public static void ExtractFile(Stream iso, IsoEntry entry, Stream output)
        => ExtractFile(iso, 0, entry, output);

    /// <summary>Copy a file's bytes out of an ISO addressed from a non-zero base
    /// LBA (see the base-LBA <see cref="Read(Stream, long, NamePreference)"/>).</summary>
    public static void ExtractFile(Stream iso, long baseLba, IsoEntry entry, Stream output)
    {
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (entry.IsDirectory)
            throw new ArgumentException($"'{entry.Path}' is a directory.", nameof(entry));

        Stream s = baseLba > 0 ? new RebaseStream(iso, baseLba * SectorSize) : iso;
        s.Seek((long)entry.Extent * SectorSize, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        long remaining = entry.Size;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int n = s.Read(buffer, 0, want);
            if (n <= 0)
                throw new EndOfStreamException(
                    $"'{entry.Path}' ends past the end of the image — it claims {entry.Size:N0} " +
                    $"bytes at sector {entry.Extent}, but the image ran out {remaining:N0} bytes early.");
            output.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    // ---- descriptors --------------------------------------------------------

    private static (byte[] Pvd, byte[]? Svd) FindDescriptors(Stream iso, long baseLba = 0)
    {
        byte[]? pvd = null, svd = null;

        // The Volume Descriptor Set begins at sector 16 of the volume — which,
        // for a base-LBA ISO presented in absolute space, is baseLba + 16.
        for (long lba = baseLba + 16; lba <= baseLba + 100; lba++)
        {
            var s = ReadSector(iso, lba);
            if (s is null || s.Length < 7) break;
            if (Encoding.ASCII.GetString(s, 1, 5) != "CD001")
            {
                if (pvd is null) break;   // not an ISO at all
                break;
            }

            byte type = s[0];
            if (type == 0xFF) break;                       // terminator
            if (type == 1 && pvd is null) pvd = s;
            else if (type == 2 && svd is null && IsUcs2Escape(s)) svd = s;
        }

        if (pvd is null)
            throw new IsoFormatException(
                "No ISO 9660 primary volume descriptor at sector 16 — this track does not " +
                "appear to hold an ISO 9660 filesystem.");
        return (pvd, svd);
    }

    /// <summary>Joliet advertises UCS-2 via an escape sequence at offset 88.</summary>
    private static bool IsUcs2Escape(byte[] descriptor)
    {
        if (descriptor[88] != (byte)'%' || descriptor[89] != (byte)'/') return false;
        byte level = descriptor[90];
        return level is (byte)'@' or (byte)'C' or (byte)'E';
    }

    private static string ReadVolumeId(byte[] desc, bool joliet)
    {
        var raw = desc.AsSpan(40, 32);
        var s = joliet
            ? Encoding.BigEndianUnicode.GetString(raw)
            : Encoding.ASCII.GetString(raw);
        return s.TrimEnd(' ', '\0');
    }

    private static bool DetectRockRidge(Stream iso, uint rootExtent, uint rootSize)
    {
        // The root '.' record carries the mandatory SP entry (and usually ER) in
        // its System Use area when Rock Ridge is present.
        var data = ReadRange(iso, (long)rootExtent * SectorSize, (int)Math.Min(rootSize, SectorSize));
        if (data.Length < 34) return false;

        int recLen = data[0];
        if (recLen < 34 || recLen > data.Length) return false;

        var rec = data.AsSpan(0, recLen);
        for (int i = 34; i + 1 < rec.Length; i++)
        {
            if (rec[i] == (byte)'S' && rec[i + 1] == (byte)'P') return true;
            if (rec[i] == (byte)'E' && rec[i + 1] == (byte)'R') return true;
        }
        return false;
    }

    // ---- directory walking --------------------------------------------------

    private static void Recurse(Stream iso, uint extent, uint size, string prefix,
                                bool joliet, bool rockRidge, List<IsoEntry> acc, int depth)
    {
        if (depth > MaxDepth)
            throw new IsoFormatException(
                $"Directory nesting exceeds {MaxDepth} levels at '{prefix}' — the image may be " +
                "corrupt or contain a loop.");

        foreach (var e in ReadDirectory(iso, extent, size, joliet, rockRidge, prefix))
        {
            acc.Add(e);
            if (e.IsDirectory)
                Recurse(iso, e.Extent, e.Size, e.Path, joliet, rockRidge, acc, depth + 1);
        }
    }

    private static List<IsoEntry> ReadDirectory(Stream iso, uint extent, uint size,
                                                bool joliet, bool rockRidge, string prefix)
    {
        var data = ReadRange(iso, (long)extent * SectorSize, (int)size);
        var entries = new List<IsoEntry>();

        int p = 0;
        while (p < data.Length)
        {
            int recLen = data[p];
            if (recLen == 0)
            {
                // Zero length means "pad to the next sector", not end-of-directory.
                int next = (p / SectorSize + 1) * SectorSize;
                if (next <= p) break;
                p = next;
                continue;
            }
            if (recLen < 33 || p + recLen > data.Length) break;

            var rec = data.AsSpan(p, recLen);
            uint childExtent = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(2, 4));
            uint childSize = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(10, 4));
            byte flags = rec[25];
            int idLen = rec[32];

            if (idLen == 0 || 33 + idLen > recLen) { p += recLen; break; }

            var ident = rec.Slice(33, idLen);

            // '.' and '..' are single 0x00 / 0x01 identifiers.
            if (idLen == 1 && (ident[0] == 0x00 || ident[0] == 0x01)) { p += recLen; continue; }

            bool isDir = (flags & 0x02) != 0;
            string name = DecodeName(ident, joliet, isDir);

            if (rockRidge)
            {
                var nm = RockRidgeName(rec, idLen);
                if (!string.IsNullOrEmpty(nm)) name = nm;
            }

            entries.Add(new IsoEntry
            {
                Name = name,
                Path = prefix + "/" + name,
                IsDirectory = isDir,
                Extent = childExtent,
                Size = childSize,
            });
            p += recLen;
        }

        return entries;
    }

    private static string DecodeName(ReadOnlySpan<byte> ident, bool joliet, bool isDir)
    {
        string n = joliet
            ? Encoding.BigEndianUnicode.GetString(ident)
            : Encoding.ASCII.GetString(ident);

        if (isDir) return n;

        // Strip the ";1" version, then a bare trailing dot ("FILE." -> "FILE").
        int semi = n.IndexOf(';');
        if (semi >= 0) n = n[..semi];
        if (n.Length > 1 && n.EndsWith('.')) n = n[..^1];
        return n;
    }

    /// <summary>Walk SUSP entries for an NM (alternate name) entry.</summary>
    private static string? RockRidgeName(ReadOnlySpan<byte> rec, int idLen)
    {
        int baseOffset = 33 + idLen;
        if ((baseOffset & 1) != 0) baseOffset++;   // identifier padded to even
        if (baseOffset >= rec.Length) return null;

        var su = rec[baseOffset..];
        string? name = null;
        int p = 0;
        while (p + 4 <= su.Length)
        {
            int len = su[p + 2];
            if (len < 4 || p + len > su.Length) break;

            if (su[p] == (byte)'N' && su[p + 1] == (byte)'M' && len > 5)
                // byte p+4 holds flags; bit 0 = CONTINUE, so parts concatenate.
                name = (name ?? "") + Encoding.UTF8.GetString(su.Slice(p + 5, len - 5));

            p += len;
        }
        return name;
    }

    // ---- stream helpers -----------------------------------------------------

    private static byte[]? ReadSector(Stream iso, long lba)
    {
        long off = lba * SectorSize;
        if (off + SectorSize > iso.Length) return null;
        return ReadRange(iso, off, SectorSize);
    }

    private static byte[] ReadRange(Stream iso, long offset, int length)
    {
        if (length < 0) length = 0;
        if (offset < 0 || offset >= iso.Length)
            throw new IsoFormatException(
                $"The image is truncated: a structure at offset {offset:N0} lies past the end " +
                $"({iso.Length:N0} bytes).");

        length = (int)Math.Min(length, iso.Length - offset);
        var buf = new byte[length];
        iso.Seek(offset, SeekOrigin.Begin);
        iso.ReadExactly(buf, 0, length);
        return buf;
    }
}

/// <summary>
/// A read-only view that shifts an inner stream forward by a fixed number of
/// bytes, so its content appears to start at that absolute position — the bytes
/// below read as zeros. Used to present a track whose ISO is addressed from a
/// base LBA as though it sat at that LBA in a full disc, so ordinary absolute-LBA
/// reading resolves every extent correctly.
/// </summary>
internal sealed class RebaseStream(Stream inner, long shift) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => shift + inner.Length;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0 || _position >= Length) return 0;
        count = (int)Math.Min(count, Length - _position);

        int produced = 0;
        // The zero region below the shift.
        if (_position < shift)
        {
            int zeros = (int)Math.Min(count, shift - _position);
            Array.Clear(buffer, offset, zeros);
            _position += zeros;
            produced += zeros;
            offset += zeros;
            count -= zeros;
        }
        // The inner stream beyond the shift.
        if (count > 0)
        {
            inner.Seek(_position - shift, SeekOrigin.Begin);
            while (count > 0)
            {
                int n = inner.Read(buffer, offset, count);
                if (n <= 0) break;
                _position += n;
                produced += n;
                offset += n;
                count -= n;
            }
        }
        return produced;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => _position,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
