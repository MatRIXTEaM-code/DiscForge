// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.
//
// ARJ reading, from "ARJ TECHNICAL INFORMATION" (Robert Jung, April 1993) and archives made by ARJ.

using System.Buffers.Binary;

namespace DiscForge.Core.OldArchives;

/// <summary>
/// An ARJ archive: methods 0 (stored), 1–3 (static-Huffman LZ) and 4 (fastest); folders; multi-volume
/// sets (.arj, .a01, .a02 …, each part of a split file compressed on its own); self-extracting .exe;
/// "garbled" (password) files using ARJ's classic scheme. CRC-32 checked on every part.
/// ARJ-SECURITY envelopes and GOST-encrypted archives are not supported.
/// </summary>
public sealed class ArjArchive : OldArchive
{
    private const int MaxHeader = 2600;
    private const int FlagGarbled = 0x01, FlagVolume = 0x04, FlagExtFile = 0x08;

    private sealed record Part(Stream File, long Offset, long Packed, long Size, uint Crc, int Method, bool Garbled, byte Modifier);

    private string _comment = "";
    private int _host;
    private bool _multi;

    public override string Format => "ARJ";
    public override string Comment => _comment;

    public override string Description =>
        $"ARJ archive (made on {HostName(_host)})" + (_multi ? $", {VolumeList.Count} volume(s)" : "") + (StartOffset > 0 ? ", self-extracting" : "")
        + (AnyEncrypted ? ", password-protected" : "");

    public static ArjArchive OpenFile(string path)
    {
        var a = new ArjArchive();
        try { a.Load(path); return a; }
        catch { a.Dispose(); throw; }
    }

    /// <summary>name.a03 → name.arj when it exists.</summary>
    public static string FirstVolumeFor(string path)
    {
        string stem = path[..^Path.GetExtension(path).Length];
        foreach (var c in new[] { stem + ".arj", stem + ".ARJ", stem + ".Arj" })
            if (File.Exists(c)) return c;
        return path;
    }

    internal static string NextVolumeName(string path)
    {
        string ext = Path.GetExtension(path);
        string stem = path[..^ext.Length];
        int n = 1;
        bool upper = ext.Length > 1 && char.IsUpper(ext[1]);
        if (ext.Length >= 4 && int.TryParse(ext.AsSpan(ext.Length - 2), out int cur) && (ext[1] is 'a' or 'A')) n = cur + 1;
        else if (ext.Length == 4 && int.TryParse(ext.AsSpan(1), out int cur3)) n = cur3 + 1;
        string next = n <= 99 ? $"{stem}.{(upper ? 'A' : 'a')}{n:00}" : $"{stem}.{n:000}";
        if (!File.Exists(next))
        {
            string other = n <= 99 ? $"{stem}.{(upper ? 'a' : 'A')}{n:00}" : next;
            if (File.Exists(other)) return other;
        }
        return next;
    }

    /// <summary>Offset of the ARJ main header (0 normally; later for self-extractors), or -1.</summary>
    public static long FindStart(Stream fs)
    {
        var buf = new byte[(int)Math.Min(512 * 1024, fs.Length)];
        fs.Position = 0;
        int n = ReadFully(fs, buf, 0, buf.Length);
        for (int i = 0; i + 4 <= n; i++)
        {
            if (buf[i] != 0x60 || buf[i + 1] != 0xEA) continue;
            int size = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i + 2));
            if (size < 30 || size > MaxHeader) continue;
            if (i + 4 + size + 4 > n) continue;
            var body = buf.AsSpan(i + 4, size);
            if (Crc32Std.Compute(body) != BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 4 + size))) continue;
            if (body[6] != 2) continue;   // the main header has file type 2
            return i;
        }
        return -1;
    }

    private (byte[] Body, long Next)? ReadHeaderAt(Stream fs, long pos)
    {
        var h = new byte[4];
        fs.Position = pos;
        if (ReadFully(fs, h, 0, 4) < 4) throw new OldArchiveException("Truncated header.");
        if (h[0] != 0x60 || h[1] != 0xEA) throw new OldArchiveException($"No ARJ header at byte {pos:N0}.");
        int size = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(2));
        if (size == 0) return null;   // end of archive
        if (size > MaxHeader) throw new OldArchiveException("Header too large.");
        var body = new byte[size + 4];
        if (ReadFully(fs, body, 0, body.Length) < body.Length) throw new OldArchiveException("Truncated header.");
        if (Crc32Std.Compute(body.AsSpan(0, size)) != BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(size)))
            throw new OldArchiveException("Header CRC doesn't match.");
        long p = pos + 4 + size + 4;
        // Extended headers: (size, data, CRC-32) until a zero size.
        var eh = new byte[2];
        while (true)
        {
            fs.Position = p;
            if (ReadFully(fs, eh, 0, 2) < 2) throw new OldArchiveException("Truncated header.");
            int es = BinaryPrimitives.ReadUInt16LittleEndian(eh);
            p += 2;
            if (es == 0) break;
            p += es + 4;
        }
        return (body[..size], p);
    }

    private static string CString(byte[] b, ref int i)
    {
        int end = Array.IndexOf(b, (byte)0, i);
        if (end < 0) end = b.Length;
        string s = DecodeName(b.AsSpan(i, end - i), false);
        i = Math.Min(end + 1, b.Length);
        return s;
    }

    private void Load(string path)
    {
        string? current = path;
        bool first = true;
        (string Name, OldEntry Entry, List<Part> Parts)? open = null;
        while (current is not null)
        {
            var fs = OpenVolume(current);
            long start = first ? FindStart(fs) : (FindStart(fs) is var s && s >= 0 ? s : -1);
            if (start < 0) throw new OldArchiveException(first ? "No ARJ header found." : $"{Path.GetFileName(current)} isn't an ARJ volume.");
            if (first) StartOffset = start;
            var main = ReadHeaderAt(fs, start) ?? throw new OldArchiveException("Empty ARJ main header.");
            var mb = main.Body;
            int flags = mb[4];
            if (first)
            {
                _host = mb[3];
                _multi = (flags & FlagVolume) != 0;
                int ni = mb[0];
                CString(mb, ref ni);
                _comment = CString(mb, ref ni).Replace("\r\n", "\n");
                if ((flags & 0x40) != 0) WarningList.Add("This is an ARJ-SECURITY (authenticated) archive; the security envelope isn't checked.");
            }
            bool volumeContinues = (flags & FlagVolume) != 0;
            long pos = main.Next;
            while (pos < fs.Length)
            {
                (byte[] Body, long Next)? hdr;
                try { hdr = ReadHeaderAt(fs, pos); }
                catch (OldArchiveException ex)
                {
                    WarningList.Add($"{Path.GetFileName(current)}: stopped at byte {pos:N0} — {ex.Message}");
                    break;
                }
                if (hdr is null) break;
                var b = hdr.Value.Body;
                int fhs = b[0];
                int fflags = b[4], method = b[5], type = b[6];
                byte modifier = b[7];
                uint dos = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
                long packed = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
                long size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20));
                int ni = fhs;
                string rawName = CString(b, ref ni);
                string comment = CString(b, ref ni);
                long dataOffset = hdr.Value.Next;
                if (dataOffset + packed > fs.Length)
                {
                    WarningList.Add($"{rawName}: data runs past the end of {Path.GetFileName(current)} — the archive is truncated.");
                    break;
                }
                pos = dataOffset + packed;
                if (type == 4) continue;   // volume label
                bool isDir = type == 3;
                string name = SafeExtract.SanitizeRelativePath(rawName);
                if (name.Length == 0 && !isDir) name = $"file{EntryList.Count:0000}";
                if (name.Length == 0) continue;
                var part = new Part(fs, dataOffset, packed, size, crc, method, (fflags & FlagGarbled) != 0, modifier);

                if ((fflags & FlagExtFile) != 0)
                {
                    if (open is { } o && o.Name == name) o.Parts.Add(part);
                    else if (!first || EntryList.Count > 0 || open is not null)
                        WarningList.Add($"{name}: continues from a volume that isn't part of this set; skipped.");
                    else
                        WarningList.Add($"{name}: starts in an earlier volume that isn't here; skipped.");
                    if ((fflags & FlagVolume) == 0 && open is { } done && done.Name == name)
                    {
                        Finish(done);
                        open = null;
                    }
                    continue;
                }
                if (open is { } pending)
                {
                    WarningList.Add($"{pending.Name}: the rest of this file is missing.");
                    open = null;
                }
                var entry = new OldEntry(EntryList.Count, name, isDir ? 0 : size, isDir ? 0 : packed, FromDos(dos), isDir,
                    (fflags & FlagGarbled) != 0, isDir ? "" : MethodName(method), comment.Replace("\r\n", "\n"));
                var list = new List<Part> { part };
                if (!isDir && (fflags & FlagVolume) != 0) open = (name, entry, list);
                else EntryList.Add(entry with { Tag = list });
            }
            first = false;
            if (!volumeContinues) break;
            string next = NextVolumeName(current);
            if (!File.Exists(next))
            {
                WarningList.Add($"The next volume, {Path.GetFileName(next)}, isn't here." +
                                (open is not null ? $" {open.Value.Name} can't be unpacked without it." : ""));
                break;
            }
            current = next;
        }
        if (open is { } last && !WarningList.Any(w => w.Contains(last.Name)))
            WarningList.Add($"{last.Name}: the rest of this file is missing.");

        void Finish((string Name, OldEntry Entry, List<Part> Parts) d)
        {
            long total = d.Parts.Sum(p => p.Size), packedTotal = d.Parts.Sum(p => p.Packed);
            EntryList.Add(d.Entry with
            {
                Index = EntryList.Count, Size = total, PackedSize = packedTotal, VolumeCount = d.Parts.Count, Tag = d.Parts,
            });
        }
    }

    private static string HostName(int h) => h switch
    {
        0 => "MS-DOS", 1 => "PRIMOS", 2 => "Unix", 3 => "Amiga", 4 => "Mac OS", 5 => "OS/2", 6 => "Apple GS",
        7 => "Atari ST", 8 => "NeXT", 9 => "VAX VMS", 10 => "Windows 95", 11 => "Windows NT", _ => $"host {h}",
    };

    private static string MethodName(int m) => m switch
    {
        0 => "stored",
        1 => "method 1 (best)",
        2 => "method 2",
        3 => "method 3",
        4 => "method 4 (fastest)",
        _ => $"method {m}",
    };

    protected override void DecodeEntry(OldEntry entry, Stream output, string? password)
    {
        if (entry.Tag is not List<Part> parts) return;
        if (entry.IsEncrypted && string.IsNullOrEmpty(password))
            throw new OldArchivePasswordException("This file is password-protected — enter the password.");
        long done = 0;
        foreach (var part in parts)
        {
            Stream packed = new SegmentReadStream(new[] { (part.File, part.Offset, part.Packed) });
            if (part.Garbled) packed = new GarbleStream(packed, System.Text.Encoding.Latin1.GetBytes(password!), part.Modifier);
            var cs = new ChecksumStream(output, crc32: true);
            try
            {
                switch (part.Method)
                {
                    case 0: CopyExactly(packed, cs, part.Size); break;
                    case 1:
                    case 2:
                    case 3: StaticHuffmanLz.Decode(packed, part.Size, cs, 16, 5, 0); break;
                    case 4: ArjFastDecoder.Decode(packed, part.Size, cs); break;
                    default: throw new OldArchiveException($"ARJ method {part.Method} isn't supported.");
                }
            }
            catch (OldArchiveException) when (part.Garbled)
            {
                throw new OldArchivePasswordException("Wrong password (or the file is damaged).");
            }
            if (cs.Count != part.Size) throw new OldArchiveException("The file is damaged (wrong size).");
            if (cs.Crc != part.Crc)
            {
                if (part.Garbled) throw new OldArchivePasswordException("Wrong password (or the file is damaged).");
                throw new OldArchiveException($"CRC-32 doesn't match (expected {part.Crc:X8}, got {cs.Crc:X8}) — the file is damaged.");
            }
            done += cs.Count;
        }
        if (done != entry.Size) throw new OldArchiveException("The file is damaged (wrong size).");
    }

    private static void CopyExactly(Stream src, Stream dst, long n)
    {
        var buf = new byte[1 << 16];
        while (n > 0)
        {
            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, n));
            if (r <= 0) throw new OldArchiveException("Stored data is truncated.");
            dst.Write(buf, 0, r);
            n -= r;
        }
    }

    /// <summary>ARJ's classic "garble": each byte XOR (password byte + header modifier).</summary>
    private sealed class GarbleStream(Stream inner, byte[] password, byte modifier) : Stream
    {
        private int _i;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            for (int k = 0; k < n; k++)
            {
                buffer[offset + k] ^= (byte)(password[_i] + modifier);
                if (++_i == password.Length) _i = 0;
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
