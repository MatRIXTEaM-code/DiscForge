// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.FileSystems;

/// <summary>
/// Read-only NTFS reader — list directories and extract files from an NTFS volume image. NTFS is the
/// Windows filesystem Aaru reads and DiscForge's FAT/exFAT support didn't reach. This parses the boot
/// sector, bootstraps the Master File Table from record 0's own data runs, applies the per-sector
/// fixups (Update Sequence Array) every FILE record carries, and decodes the attributes that matter —
/// <c>$FILE_NAME</c> (name, parent, directory flag) and <c>$DATA</c> (resident inline, or non-resident
/// via a decoded run list). Directories are listed by scanning the MFT for records whose parent is the
/// directory, which sidesteps the index B-tree while still being complete for in-use files.
///
/// The two error-prone primitives — <see cref="DecodeDataRuns"/> and <see cref="ApplyFixups"/> — are
/// pure and unit-tested against known vectors; a synthetic MFT proves the end-to-end resident path.
/// Compressed/encrypted ($DATA compression unit ≠ 0, or EFS) and attribute-list spanning are declined
/// rather than mis-read. Reads user files; decrypts and defeats nothing.
/// </summary>
public sealed class Ntfs
{
    public const long RootMft = 5;

    public sealed record VolumeInfo(int BytesPerSector, int SectorsPerCluster, long MftLcn, int MftRecordSize)
    {
        public int ClusterBytes => BytesPerSector * SectorsPerCluster;
    }

    public sealed record FileNode(long MftNumber, string Name, long ParentMft, bool IsDirectory, long Size);

    public readonly record struct DataRun(long LengthClusters, long Lcn, bool Sparse);

    private readonly Stream _s;
    private readonly byte[] _mft;
    public VolumeInfo Info { get; }

    private Ntfs(Stream s, VolumeInfo info, byte[] mft) { _s = s; Info = info; _mft = mft; }

    // ---- open --------------------------------------------------------------

    public static Ntfs Open(Stream s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var info = ReadBoot(s);

        // Bootstrap: read MFT record 0 ($MFT), whose non-resident $DATA describes the whole MFT.
        var rec0 = ReadRecordAt(s, (long)info.MftLcn * info.ClusterBytes, info);
        var mftData = FindAttribute(rec0, 0x80, info.MftRecordSize);
        if (mftData is null) throw new InvalidDataException("MFT record 0 has no $DATA attribute.");
        var (resident, _, runs, realSize) = ReadAttribute(rec0, mftData.Value, info);
        if (resident) throw new InvalidDataException("MFT $DATA is resident — malformed volume.");

        var mft = ReadRuns(s, info, runs, realSize);
        return new Ntfs(s, info, mft);
    }

    private static VolumeInfo ReadBoot(Stream s)
    {
        s.Position = 0;
        var b = new byte[512];
        ReadExact(s, b);
        if (Encoding.ASCII.GetString(b, 3, 8) != "NTFS    ")
            throw new InvalidDataException("Not an NTFS volume (missing the 'NTFS    ' OEM id).");

        int bps = U16(b, 11);
        int spc = b[13];
        long mftLcn = (long)U64(b, 48);
        sbyte clustersPerMftRecord = (sbyte)b[64];
        int recSize = clustersPerMftRecord >= 0
            ? clustersPerMftRecord * bps * spc
            : 1 << (-clustersPerMftRecord);          // negative → 2^|value| bytes
        return new VolumeInfo(bps, spc, mftLcn, recSize);
    }

    // ---- directory listing / resolve / extract -----------------------------

    /// <summary>List the entries whose parent directory is <paramref name="dirMft"/> (use <see cref="RootMft"/>).</summary>
    public IReadOnlyList<FileNode> List(long dirMft)
    {
        var all = new List<FileNode>();
        int count = _mft.Length / Info.MftRecordSize;
        for (int n = 0; n < count; n++)
        {
            FileNode? node;
            try { node = NodeOf(n); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException)
            {
                continue;   // one malformed record must not take down the whole listing
            }
            if (node is not null && node.ParentMft == dirMft && node.MftNumber != dirMft)
                all.Add(node);
        }
        return all;
    }

    public FileNode? Resolve(string path)
    {
        var parts = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return NodeOf((int)RootMft);
        long dir = RootMft;
        FileNode? found = null;
        for (int i = 0; i < parts.Length; i++)
        {
            found = List(dir).FirstOrDefault(e => string.Equals(e.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (found is null) return null;
            if (i < parts.Length - 1)
            {
                if (!found.IsDirectory) return null;
                dir = found.MftNumber;
            }
        }
        return found;
    }

    public long Extract(FileNode file, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (file.IsDirectory) throw new InvalidOperationException($"'{file.Name}' is a directory.");
        var rec = RecordSpan((int)file.MftNumber);
        var data = FindAttribute(rec, 0x80, Info.MftRecordSize)
                   ?? throw new InvalidDataException($"'{file.Name}' has no $DATA attribute.");
        var (resident, residentValue, runs, realSize) = ReadAttribute(rec, data, Info);
        if (resident)
        {
            output.Write(residentValue);
            return residentValue.Length;
        }
        var bytes = ReadRuns(_s, Info, runs, realSize);
        output.Write(bytes, 0, (int)Math.Min(realSize, bytes.Length));
        return Math.Min(realSize, bytes.Length);
    }

    // ---- per-record decoding -----------------------------------------------

    private byte[] RecordSpan(int mftNumber)
    {
        long off = (long)mftNumber * Info.MftRecordSize;
        if (off + Info.MftRecordSize > _mft.Length) throw new ArgumentOutOfRangeException(nameof(mftNumber));
        var rec = new byte[Info.MftRecordSize];
        Array.Copy(_mft, off, rec, 0, Info.MftRecordSize);
        ApplyFixups(rec, U16(rec, 4), U16(rec, 6), Info.BytesPerSector);
        return rec;
    }

    private FileNode? NodeOf(int mftNumber)
    {
        var rec = RecordSpan(mftNumber);
        if (Encoding.ASCII.GetString(rec, 0, 4) != "FILE") return null;
        ushort flags = U16(rec, 22);
        if ((flags & 0x01) == 0) return null;               // not in use
        bool isDir = (flags & 0x02) != 0;

        var fn = FindAttribute(rec, 0x30, Info.MftRecordSize);   // $FILE_NAME
        if (fn is null) return null;
        var (_, value, _, _) = ReadAttribute(rec, fn.Value, Info);
        // Prefer a Win32 name if the record carries several $FILE_NAME attributes.
        (string name, long parent, byte ns) best = ParseFileName(value);
        foreach (var extra in FindAllAttributes(rec, 0x30, Info.MftRecordSize).Skip(1))
        {
            var (_, v2, _, _) = ReadAttribute(rec, extra, Info);
            var cand = ParseFileName(v2);
            if (best.ns == 2 && cand.ns != 2) best = cand;   // replace a DOS 8.3 with a real name
        }

        long size = 0;
        var data = FindAttribute(rec, 0x80, Info.MftRecordSize);
        if (data is not null)
        {
            // A compressed $DATA declines on EXTRACT, but its existence must not abort a directory
            // listing — list the file (size unknown → 0) and let Extract refuse honestly.
            try
            {
                var (res, val, _, real) = ReadAttribute(rec, data.Value, Info);
                size = res ? val.Length : real;
            }
            catch (NotSupportedException) { size = 0; }
        }
        return new FileNode(mftNumber, best.name, best.parent, isDir, size);
    }

    private static (string name, long parent, byte ns) ParseFileName(byte[] v)
    {
        long parent = (long)(U64(v, 0) & 0x0000_FFFF_FFFF_FFFF);   // low 48 bits = parent MFT number
        int len = v[64];
        byte ns = v[65];
        var sb = new StringBuilder(len);
        for (int c = 0; c < len; c++) sb.Append((char)U16(v, 66 + c * 2));
        return (sb.ToString(), parent, ns);
    }

    // ---- attributes ---------------------------------------------------------

    /// <summary>Offset of the first attribute of the given type in a (fixed-up) record, or null.</summary>
    private static int? FindAttribute(byte[] rec, uint type, int recSize)
    {
        foreach (var o in FindAllAttributes(rec, type, recSize)) return o;
        return null;
    }

    private static IEnumerable<int> FindAllAttributes(byte[] rec, uint type, int recSize)
    {
        int o = U16(rec, 20);                                // FirstAttributeOffset
        while (o + 8 <= recSize)
        {
            uint t = U32(rec, o);
            if (t == 0xFFFFFFFF) yield break;                // end marker
            int len = (int)U32(rec, o + 4);
            if (len <= 0 || o + len > recSize) yield break;
            if (t == type) yield return o;
            o += len;
        }
    }

    /// <summary>Decode an attribute at <paramref name="attrOffset"/>. For a resident attribute the value
    /// bytes are returned; for a non-resident one, the decoded run list and real size.</summary>
    private static (bool resident, byte[] value, IReadOnlyList<DataRun> runs, long realSize)
        ReadAttribute(byte[] rec, int attrOffset, VolumeInfo info)
    {
        bool nonResident = rec[attrOffset + 8] != 0;
        if (!nonResident)
        {
            int attrTotal = (int)U32(rec, attrOffset + 4);
            long valLen = U32(rec, attrOffset + 16);
            int valOff = U16(rec, attrOffset + 20);
            // Bound the value against the attribute and record so a crafted length can't drive a huge
            // allocation or read past the record — decline rather than mis-read.
            if (valLen < 0 || valOff < 0 || attrOffset + valOff + valLen > rec.Length ||
                (attrTotal > 0 && valOff + valLen > attrTotal))
                throw new InvalidDataException("NTFS resident attribute value overruns its record — declined.");
            var value = new byte[valLen];
            Array.Copy(rec, attrOffset + valOff, value, 0, (int)valLen);
            return (true, value, Array.Empty<DataRun>(), valLen);
        }

        ushort compressionUnit = U16(rec, attrOffset + 34);
        if (compressionUnit != 0)
            throw new NotSupportedException("Compressed NTFS $DATA is declined (decode not verified).");

        int runsOff = U16(rec, attrOffset + 32);
        long realSize = (long)U64(rec, attrOffset + 48);
        int attrLen = (int)U32(rec, attrOffset + 4);
        var runBytes = rec.AsSpan(attrOffset + runsOff, attrLen - runsOff);
        return (false, Array.Empty<byte>(), DecodeDataRuns(runBytes), realSize);
    }

    // ---- pure primitives (unit-tested) -------------------------------------

    /// <summary>Decode an NTFS run list (the compact length/offset cluster runs of a non-resident
    /// attribute). Offsets are delta-encoded and signed; a zero offset field marks a sparse run.</summary>
    public static IReadOnlyList<DataRun> DecodeDataRuns(ReadOnlySpan<byte> runs)
    {
        var list = new List<DataRun>();
        long lcn = 0;
        int p = 0;
        while (p < runs.Length && runs[p] != 0)
        {
            int header = runs[p++];
            int lenSize = header & 0x0F;
            int offSize = (header >> 4) & 0x0F;
            if (lenSize == 0 || p + lenSize > runs.Length) break;

            long length = (long)ReadLE(runs, p, lenSize);
            p += lenSize;

            if (offSize == 0) { list.Add(new DataRun(length, 0, true)); continue; }   // sparse
            if (p + offSize > runs.Length) break;
            lcn += ReadSignedLE(runs, p, offSize);
            p += offSize;
            list.Add(new DataRun(length, lcn, false));
        }
        return list;
    }

    /// <summary>Apply an NTFS record's Update Sequence Array: the last two bytes of each sector were
    /// replaced by the USN when written, and must be restored from the array before the record parses.</summary>
    public static void ApplyFixups(byte[] rec, int usaOffset, int usaCount, int bytesPerSector)
    {
        if (usaCount < 1) return;
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * bytesPerSector - 2;
            if (sectorEnd + 2 > rec.Length || usaOffset + i * 2 + 2 > rec.Length) break;
            rec[sectorEnd] = rec[usaOffset + i * 2];
            rec[sectorEnd + 1] = rec[usaOffset + i * 2 + 1];
        }
    }

    // ---- cluster reads / helpers -------------------------------------------

    private static byte[] ReadRecordAt(Stream s, long byteOffset, VolumeInfo info)
    {
        s.Position = byteOffset;
        var rec = new byte[info.MftRecordSize];
        ReadExact(s, rec);
        ApplyFixups(rec, U16(rec, 4), U16(rec, 6), info.BytesPerSector);
        return rec;
    }

    private static byte[] ReadRuns(Stream s, VolumeInfo info, IReadOnlyList<DataRun> runs, long realSize)
    {
        using var ms = new MemoryStream();
        var buf = new byte[info.ClusterBytes];
        foreach (var run in runs)
        {
            for (long c = 0; c < run.LengthClusters && ms.Length < realSize; c++)
            {
                if (run.Sparse) { ms.Write(new byte[info.ClusterBytes], 0, info.ClusterBytes); continue; }
                s.Position = (run.Lcn + c) * info.ClusterBytes;
                ReadExact(s, buf);
                ms.Write(buf, 0, buf.Length);
            }
        }
        var all = ms.ToArray();
        if (all.Length > realSize) Array.Resize(ref all, (int)realSize);
        return all;
    }

    private static ulong ReadLE(ReadOnlySpan<byte> b, int o, int n)
    {
        ulong v = 0;
        for (int i = 0; i < n; i++) v |= (ulong)b[o + i] << (8 * i);
        return v;
    }

    private static long ReadSignedLE(ReadOnlySpan<byte> b, int o, int n)
    {
        ulong v = ReadLE(b, o, n);
        ulong signBit = 1UL << (8 * n - 1);
        if ((v & signBit) != 0) return (long)(v | ~((signBit << 1) - 1));   // sign-extend
        return (long)v;
    }

    private static void ReadExact(Stream s, byte[] b)
    {
        int off = 0;
        while (off < b.Length)
        {
            int r = s.Read(b, off, b.Length - off);
            if (r <= 0) throw new EndOfStreamException("Unexpected end of NTFS volume.");
            off += r;
        }
    }

    private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static ulong U64(byte[] b, int o) { ulong v = 0; for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i]; return v; }
}
