// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.
//
// ZOO reading, from Rahul Dhesi's published description of the zoo archive format (zoo 2.x).

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.OldArchives;

/// <summary>
/// A ZOO archive (zoo 1.x/2.x): methods 0 (stored), 1 (LZW) and 2 (LZH), long names and folders from
/// type-2 entries, deleted entries skipped, CRC-16 checked on every file.
/// </summary>
public sealed class ZooArchive : OldArchive
{
    private const uint Tag = 0xFDC4A7DC;

    private sealed record Info(long Offset, int Method, ushort Crc);

    private FileStream _fs = null!;
    private string _comment = "";
    private int _major, _minor;

    public override string Format => "ZOO";
    public override string Comment => _comment;
    public override string Description => $"ZOO {_major}.{_minor} archive";

    public static bool Probe(Stream fs)
    {
        if (fs.Length < 34) return false;
        var b = new byte[24];
        fs.Position = 0;
        if (ReadFully(fs, b, 0, 24) < 24) return false;
        return b[0] == 'Z' && b[1] == 'O' && b[2] == 'O' && BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20)) == Tag;
    }

    public static ZooArchive OpenFile(string path)
    {
        var a = new ZooArchive();
        try { a.Load(path); return a; }
        catch { a.Dispose(); throw; }
    }

    private byte[] Read(long at, int n)
    {
        var b = new byte[n];
        _fs.Position = at;
        if (ReadFully(_fs, b, 0, n) < n) throw new OldArchiveException("Truncated ZOO header.");
        return b;
    }

    private void Load(string path)
    {
        _fs = OpenVolume(path);
        if (!Probe(_fs)) throw new OldArchiveException("Not a ZOO archive.");
        var h = Read(0, 34);
        long pos = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(24));
        int minus = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(28));
        if (pos + minus != 0) WarningList.Add("The archive header's check value is wrong; reading anyway.");
        _major = h[32];
        _minor = h[33];
        if (_fs.Length >= 42)
        {
            var x = Read(34, 7);
            if (x[0] == 1)
            {
                long cpos = BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(1));
                int clen = BinaryPrimitives.ReadUInt16LittleEndian(x.AsSpan(5));
                if (cpos > 0 && clen > 0 && cpos + clen <= _fs.Length) _comment = Text(Read(cpos, clen));
            }
        }

        var seen = new HashSet<long>();
        while (pos > 0 && pos + 51 <= _fs.Length && seen.Add(pos))
        {
            var d = Read(pos, 51);
            if (BinaryPrimitives.ReadUInt32LittleEndian(d) != Tag)
            {
                WarningList.Add($"Stopped at byte {pos:N0}: no ZOO entry there (the file may be damaged).");
                break;
            }
            int type = d[4], method = d[5];
            long next = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(6));
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(10));
            ushort date = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(14));
            ushort time = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(16));
            ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(18));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(20));
            long packed = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(24));
            bool deleted = d[30] != 0;
            long cmtPos = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(32));
            int cmtLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(36));
            if (next == 0) break;   // the terminating null entry

            string shortName = DecodeName(d.AsSpan(38, 13), false);
            string longName = "", dir = "";
            if (type == 2 && pos + 56 <= _fs.Length)
            {
                var v = Read(pos + 51, 5);
                int vlen = BinaryPrimitives.ReadUInt16LittleEndian(v);
                if (vlen >= 2 && pos + 56 + vlen <= _fs.Length)
                {
                    var vd = Read(pos + 56, vlen);
                    int namLen = vd[0], dirLen = vd[1];
                    if (2 + namLen + dirLen <= vd.Length)
                    {
                        longName = DecodeName(vd.AsSpan(2, namLen), false);
                        dir = DecodeName(vd.AsSpan(2 + namLen, dirLen), false);
                    }
                }
            }
            if (!deleted)
            {
                string full = (dir.Length > 0 ? dir.TrimEnd('/', '\\') + "/" : "") + (longName.Length > 0 ? longName : shortName);
                string name = SafeExtract.SanitizeRelativePath(full);
                if (name.Length == 0) name = $"file{EntryList.Count:0000}";
                if (offset + packed > _fs.Length) throw new OldArchiveException($"{name}: data runs past the end of the archive.");
                string comment = cmtPos > 0 && cmtLen > 0 && cmtPos + cmtLen <= _fs.Length ? Text(Read(cmtPos, cmtLen)) : "";
                EntryList.Add(new OldEntry(EntryList.Count, name, size, packed, FromDos(((uint)date << 16) | time), false, false,
                    method switch { 0 => "stored", 1 => "LZW", 2 => "LZH", _ => $"method {method}" }, comment)
                    { Tag = new Info(offset, method, crc) });
            }
            pos = next;
        }
    }

    private static string Text(byte[] b) => DecodeName(b, false).Replace("\r\n", "\n").TrimEnd('\n', '\x1a');

    protected override void DecodeEntry(OldEntry entry, Stream output, string? password)
    {
        var info = (Info)entry.Tag!;
        var packed = new SegmentReadStream(new[] { ((Stream)_fs, info.Offset, entry.PackedSize) });
        var cs = new ChecksumStream(output, crc32: false);
        switch (info.Method)
        {
            case 0: packed.CopyTo(cs); break;
            case 1: ZooLzwDecoder.Decode(packed, entry.Size, cs); break;
            case 2: StaticHuffmanLz.Decode(packed, entry.Size, cs, 13, 4, 0); break;
            default: throw new OldArchiveException($"ZOO method {info.Method} isn't supported.");
        }
        Verify(entry, cs, info.Crc, "CRC-16");
    }
}
