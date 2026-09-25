// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.
//
// LHA / LZH (and LArc) reading. Header levels 0-3 and their extension headers as described in the
// LHa for UNIX header notes; details cross-checked with Lhasa (ISC licence, see Codecs.cs).

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.OldArchives;

/// <summary>
/// An LHA/LZH archive (LHarc, LHA, LHa for UNIX, Amiga LhA, UNLHA32 …) or a LArc archive: header
/// levels 0 to 3, methods -lh0- -lh1- -lh4- -lh5- -lh6- -lh7- -lhx- -lz4- -lz5- -lzs-, folders
/// (-lhd-), self-extracting .exe/.com files. CRC-16 checked on every file.
/// </summary>
public sealed class LhaArchive : OldArchive
{
    private const int MaxSfxSearch = 256 * 1024;

    private sealed record Info(string Method, long DataOffset, ushort Crc, int Level, int Os);

    private FileStream _fs = null!;
    private readonly HashSet<string> _methods = new();

    public override string Format => "LHA";

    public override string Description
    {
        get
        {
            string methods = string.Join(" ", _methods.Where(m => m != "-lhd-").OrderBy(m => m, StringComparer.Ordinal));
            return (_methods.Any(m => m.StartsWith("-lz")) && !_methods.Any(m => m.StartsWith("-lh") && m != "-lhd-") ? "LArc archive" : "LHA/LZH archive")
                   + (methods.Length > 0 ? $" ({methods})" : "") + (StartOffset > 0 ? ", self-extracting" : "");
        }
    }

    public static LhaArchive OpenFile(string path)
    {
        var a = new LhaArchive();
        try { a.Load(path); return a; }
        catch { a.Dispose(); throw; }
    }

    /// <summary>Offset of the first LHA header (0 for a plain archive), or -1.</summary>
    public static long FindStart(Stream fs)
    {
        var buf = new byte[(int)Math.Min(MaxSfxSearch + 64, fs.Length)];
        fs.Position = 0;
        int n = ReadFully(fs, buf, 0, buf.Length);
        // The Amiga LhASFX self-extractor carries something that looks like an LHA header in its
        // program code; the real archive is the next one after it.
        int skip = 0;
        var span = buf.AsSpan(0, n);
        int marker = span.IndexOf("LhASFX V1.2,"u8);
        for (int i = 0; i + 24 <= n; i++)
        {
            if (!LooksLikeMethod(buf.AsSpan(i + 2, 5))) continue;
            if (!HeaderPlausible(buf.AsSpan(i, n - i), fs.Length - i)) continue;
            if (marker >= 0 && i > marker && skip == 0) { skip = 1; continue; }
            return i;
        }
        return -1;
    }

    private static bool LooksLikeMethod(ReadOnlySpan<byte> m) =>
        m[0] == '-' && m[4] == '-' && m[1] == 'l' && (m[2] == 'h' || m[2] == 'z')
        && (char.IsAsciiLetterOrDigit((char)m[3]));

    private static bool HeaderPlausible(ReadOnlySpan<byte> b, long remaining)
    {
        if (b.Length < 24) return false;
        int level = b[20];
        long packed = BinaryPrimitives.ReadUInt32LittleEndian(b[7..]);
        if (packed > remaining) return false;
        switch (level)
        {
            case 0:
            case 1:
            {
                int hl = b[0];
                if (hl < 22 || hl + 2 > b.Length) return false;
                int sum = 0;
                for (int i = 2; i < hl + 2; i++) sum += b[i];
                return (sum & 0xFF) == b[1];
            }
            case 2:
            {
                int hl = BinaryPrimitives.ReadUInt16LittleEndian(b);
                return hl >= 26 && hl <= b.Length;
            }
            case 3:
                return BinaryPrimitives.ReadUInt16LittleEndian(b) == 4;
            default:
                return false;
        }
    }

    private void Load(string path)
    {
        _fs = OpenVolume(path);
        long start = FindStart(_fs);
        if (start < 0) throw new OldArchiveException("No LHA header found.");
        StartOffset = start;
        long pos = start;
        while (pos < _fs.Length)
        {
            _fs.Position = pos;
            int first = _fs.ReadByte();
            if (first <= 0) break;   // 0 = end of archive
            _fs.Position = pos;
            long next;
            try { next = ReadHeader(pos); }
            catch (OldArchiveException ex)
            {
                WarningList.Add($"Stopped reading at byte {pos:N0}: {ex.Message}");
                break;
            }
            if (next <= pos) break;
            pos = next;
        }
        if (EntryList.Count == 0 && WarningList.Count == 0) WarningList.Add("The archive is empty.");
    }

    private byte[] ReadExact(long at, int count)
    {
        var b = new byte[count];
        _fs.Position = at;
        if (ReadFully(_fs, b, 0, count) < count) throw new OldArchiveException("Truncated header.");
        return b;
    }

    /// <summary>Parse the header at <paramref name="pos"/>, add the entry, return the next header's offset.</summary>
    private long ReadHeader(long pos)
    {
        var head = ReadExact(pos, 22);
        int level = head[20];
        string method = Encoding.ASCII.GetString(head, 2, 5);
        long packed = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(7));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(11));
        uint time = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(15));
        byte[]? rawName = null;
        byte[]? rawDir = null;
        string comment = "";
        DateTime modified;
        ushort crc;
        int os = 0;
        long dataOffset;

        void Ext(int type, ReadOnlySpan<byte> data, ref DateTime mod)
        {
            switch (type)
            {
                case 0x01: rawName = data.ToArray(); break;
                case 0x02: rawDir = data.ToArray(); break;
                case 0x3F: comment = DecodeName(data, true); break;
                case 0x41 when data.Length >= 16:
                    long ft = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
                    try { if (ft > 0) mod = DateTime.FromFileTime(ft); } catch (ArgumentOutOfRangeException) { }
                    break;
                case 0x42 when data.Length >= 16:
                    // 64-bit sizes, for files over 4 GB.
                    packed = (long)BinaryPrimitives.ReadUInt64LittleEndian(data);
                    size = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
                    break;
                case 0x54 when data.Length >= 4:
                    mod = FromUnix(BinaryPrimitives.ReadUInt32LittleEndian(data));
                    break;
            }
        }

        switch (level)
        {
            case 0:
            case 1:
            {
                int hl = head[0];
                var h = ReadExact(pos, hl + 2);
                int sum = 0;
                for (int i = 2; i < h.Length; i++) sum += h[i];
                if ((sum & 0xFF) != h[1]) throw new OldArchiveException("Header checksum doesn't match.");
                int nameLen = h[21];
                if (22 + nameLen + 2 > h.Length) throw new OldArchiveException("Bad header.");
                rawName = h.AsSpan(22, nameLen).ToArray();
                crc = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(22 + nameLen));
                modified = FromDos(time);
                long p = pos + hl + 2;
                if (level == 1)
                {
                    if (24 + nameLen < h.Length) os = h[24 + nameLen];
                    int nextSize = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(h.Length - 2));
                    while (nextSize > 0)
                    {
                        if (nextSize < 3) throw new OldArchiveException("Bad extension header.");
                        var x = ReadExact(p, nextSize);
                        Ext(x[0], x.AsSpan(1, nextSize - 3), ref modified);
                        packed -= nextSize;   // level 1: the packed size includes the extension headers
                        p += nextSize;
                        nextSize = BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(nextSize - 2));
                    }
                    if (packed < 0) throw new OldArchiveException("Bad sizes in header.");
                }
                dataOffset = p;
                break;
            }
            case 2:
            {
                int hl = BinaryPrimitives.ReadUInt16LittleEndian(head);
                if (hl < 26) throw new OldArchiveException("Bad level 2 header.");
                os = ReadExact(pos + 23, 1)[0];
                if (os == 'K') hl += 2;   // OS-9/68k LHA writes the length two bytes short
                var h = ReadExact(pos, hl);
                crc = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(21));
                modified = FromUnix(time);
                ParseExt(h, 24, 2, (t, d) => Ext(t, d, ref modified));
                dataOffset = pos + hl;
                break;
            }
            case 3:
            {
                var fixedPart = ReadExact(pos, 32);
                if (BinaryPrimitives.ReadUInt16LittleEndian(fixedPart) != 4) throw new OldArchiveException("Unsupported level 3 header.");
                long hl = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(24));
                if (hl < 32 || hl > 1 << 20) throw new OldArchiveException("Bad level 3 header.");
                var h = ReadExact(pos, (int)hl);
                crc = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(21));
                os = h[23];
                modified = FromUnix(time);
                ParseExt(h, 28, 4, (t, d) => Ext(t, d, ref modified));
                dataOffset = pos + hl;
                break;
            }
            default:
                throw new OldArchiveException($"Unknown LHA header level {level}.");
        }

        if (dataOffset + packed > _fs.Length) throw new OldArchiveException("File data runs past the end of the archive — it is truncated.");

        // Build the stored path: directory part (0xFF or '\' separated) + name.
        var sb = new StringBuilder();
        if (rawDir is not null)
        {
            var d = (byte[])rawDir.Clone();
            for (int i = 0; i < d.Length; i++) if (d[i] == 0xFF) d[i] = (byte)'/';
            sb.Append(DecodeName(d, true));
            if (sb.Length > 0 && sb[^1] != '/') sb.Append('/');
        }
        if (rawName is not null)
        {
            // Level 0/1 names may carry a path with '\' or 0xFF separators, and Amiga comments after a NUL.
            int nul = Array.IndexOf(rawName, (byte)0);
            if (nul >= 0)
            {
                if (comment.Length == 0 && nul + 1 < rawName.Length) comment = DecodeName(rawName.AsSpan(nul + 1), true);
                rawName = rawName[..nul];
            }
            var n = (byte[])rawName.Clone();
            for (int i = 0; i < n.Length; i++) if (n[i] == 0xFF) n[i] = (byte)'/';
            sb.Append(DecodeName(n, true));
        }
        string stored = sb.ToString();
        // A size-0 entry whose name ends in a separator is a folder (Amiga LhA 1.22 stored folders as -lh0-).
        bool isDir = method == "-lhd-" || (size == 0 && packed == 0 && stored.Length > 0 && stored[^1] is '/' or '\\');
        string name = SafeExtract.SanitizeRelativePath(stored);
        if (name.Length == 0) name = isDir ? "" : $"file{EntryList.Count:0000}";
        _methods.Add(method);
        if (isDir && stored.Contains('|'))
        {
            // Unix symbolic links are stored as folder entries named "link|target"; DiscForge doesn't create links.
            WarningList.Add($"Skipped the symbolic link {stored.Replace("|", " → ")}.");
            return dataOffset + packed;
        }
        if (!(isDir && name.Length == 0))
            EntryList.Add(new OldEntry(EntryList.Count, name, isDir ? 0 : size, isDir ? 0 : packed, modified, isDir, false,
                isDir ? "" : method.Trim('-'), comment) { Tag = new Info(method, dataOffset, crc, level, os) });
        return dataOffset + packed;
    }

    private delegate void ExtHandler(int type, ReadOnlySpan<byte> data);

    private static void ParseExt(byte[] h, int offset, int sizeField, ExtHandler handle)
    {
        while (offset + sizeField <= h.Length)
        {
            long len = sizeField == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(offset)) : BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(offset));
            if (len == 0) break;
            if (len < sizeField + 1 || offset + len > h.Length) throw new OldArchiveException("Bad extension header.");
            var chunk = h.AsSpan(offset + sizeField, (int)len - sizeField);
            handle(chunk[0], chunk[1..]);
            offset += (int)len;
        }
    }

    protected override void DecodeEntry(OldEntry entry, Stream output, string? password)
    {
        var info = (Info)entry.Tag!;
        if (entry.Size == 0 && entry.PackedSize == 0) return;
        var packed = new SegmentReadStream(new[] { ((Stream)_fs, info.DataOffset, entry.PackedSize) });
        var cs = new ChecksumStream(output, crc32: false);
        switch (info.Method)
        {
            case "-lh0-":
            case "-lz4-":
                packed.CopyTo(cs);
                break;
            case "-lh1-": Lh1Decoder.Decode(packed, entry.Size, cs); break;
            case "-lh4-": StaticHuffmanLz.Decode(packed, entry.Size, cs, 14, 4, (byte)' '); break;
            case "-lh5-": StaticHuffmanLz.Decode(packed, entry.Size, cs, 14, 4, (byte)' '); break;
            case "-lh6-": StaticHuffmanLz.Decode(packed, entry.Size, cs, 16, 5, (byte)' '); break;
            case "-lh7-" when info.Level == 1 && info.Os == ' ':
                // LHARK's own -lh7-, incompatible with everyone else's.
                StaticHuffmanLz.Decode(packed, entry.Size, cs, 16, 6, (byte)' ', lhark: true); break;
            case "-lh7-": StaticHuffmanLz.Decode(packed, entry.Size, cs, 17, 5, (byte)' '); break;
            case "-lhx-": StaticHuffmanLz.Decode(packed, entry.Size, cs, 20, 5, (byte)' '); break;
            case "-lzs-": LarcDecoders.DecodeLzs(packed, entry.Size, cs); break;
            case "-lz5-": LarcDecoders.DecodeLz5(packed, entry.Size, cs); break;
            default:
                throw new OldArchiveException($"Compression method {info.Method} isn't supported (DiscForge reads -lh0/1/4/5/6/7/x- and -lz4/5/s-).");
        }
        Verify(entry, cs, info.Crc, "CRC-16");
    }
}
