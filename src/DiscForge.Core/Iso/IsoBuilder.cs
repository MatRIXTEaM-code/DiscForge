// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Iso;

/// <summary>
/// Self-contained ISO 9660 (Level 1) image builder with optional Joliet — no
/// external tools. Supports a full directory tree and, when Joliet is enabled,
/// carries a second directory hierarchy of UCS-2 long names (a type-2
/// Supplementary Volume Descriptor) over the SAME shared file extents, so real
/// filenames survive ("My Photos.jpg" instead of "MY_PHOTO.JPG;1"). Windows
/// reads the Joliet names; the ISO 9660 8.3 names remain as a fallback.
///
/// Builds are deterministic (fixed timestamps) so identical input yields
/// byte-identical output. Validated against `isoinfo` / `isoinfo -J`
/// (docs/reference/iso_build_joliet.py). No Rock Ridge / El Torito yet.
/// </summary>
public static class IsoBuilder
{
    public const int SectorSize = 2048;

    // ---- Input model ----

    /// <summary>
    /// Where a file's bytes come from. Carries a Length that can be known WITHOUT
    /// reading the data, so the layout can be computed for a DVD-sized tree while
    /// only streaming payloads at write time. This is what lets DiscForge author
    /// images larger than the 2 GB .NET array ceiling.
    /// </summary>
    public abstract class FileSource
    {
        public abstract long Length { get; }
        public abstract Stream OpenRead();

        public static FileSource FromBytes(byte[] data) => new BytesSource(data);
        public static FileSource FromFile(string path) => new PathSource(path);

        private sealed class BytesSource(byte[] data) : FileSource
        {
            public override long Length => data.LongLength;
            public override Stream OpenRead() => new MemoryStream(data, writable: false);
        }

        private sealed class PathSource(string path) : FileSource
        {
            private long? _length;
            public override long Length => _length ??= new FileInfo(path).Length;
            public override Stream OpenRead() =>
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                               bufferSize: 1 << 16, FileOptions.SequentialScan);
        }
    }

    public sealed record FileEntry(string Name, byte[] Data);

    /// <summary>El Torito boot media emulation type.</summary>
    public enum BootMediaType : byte
    {
        NoEmulation = 0, Floppy12 = 1, Floppy144 = 2, Floppy288 = 3, HardDisk = 4,
    }

    /// <summary>An El Torito boot image (caller-supplied — nothing copyrighted
    /// is embedded by DiscForge). NoEmulation is the norm for modern boot loaders.</summary>
    public sealed record BootImage(byte[] Data, BootMediaType Media = BootMediaType.NoEmulation);

    public sealed class Node
    {
        public required string Name { get; init; }
        public required bool IsDir { get; init; }
        public FileSource? Source { get; init; }
        public List<Node> Children { get; init; } = new();

        public static Node File(string name, byte[] data) =>
            new() { Name = name, IsDir = false, Source = FileSource.FromBytes(data) };

        public static Node File(string name, FileSource source) =>
            new() { Name = name, IsDir = false, Source = source };

        /// <summary>A file streamed from disk — never loaded whole into memory.</summary>
        public static Node FromPath(string path) =>
            new() { Name = Path.GetFileName(path), IsDir = false, Source = FileSource.FromFile(path) };

        public static Node Dir(string name, IEnumerable<Node> children) =>
            new() { Name = name, IsDir = true, Children = children.ToList() };
    }

    public sealed record BuildResult(byte[] Image, IReadOnlyList<string> Warnings);

    /// <summary>
    /// A computed image layout: every extent assigned, total size known, but no
    /// file data read yet. Call <see cref="WriteTo"/> to stream the image out.
    /// </summary>
    public sealed class IsoLayout
    {
        internal string VolumeId = "";
        internal Dir Root = null!;
        internal List<Dir> IsoOrder = new();
        internal List<Dir> JolOrder = new();
        internal List<FileNode> Files = new();
        internal bool Joliet;
        internal bool RockRidge;
        internal BootImage? Boot;
        internal int PvdSector, BootRecSector, SvdSector, TermSector;
        internal int IsoPt, IsoPts, IsoPtL, IsoPtM;
        internal int JolPt, JolPts, JolPtL, JolPtM;
        internal int BootCatSector, BootImgSector;
        /// <summary>Sectors before the first file payload — the part held in memory.</summary>
        internal int MetaSectors;

        public int VolumeSectors { get; internal set; }
        public IReadOnlyList<string> Warnings { get; internal set; } = Array.Empty<string>();
        public long ImageBytes => (long)VolumeSectors * SectorSize;

        /// <summary>Stream the image. Constant memory regardless of image size:
        /// metadata is buffered (kilobytes), file payloads are copied through.</summary>
        public void WriteTo(Stream output) => WriteLayout(this, output);
    }

    // ---- Public API ----

    public static BuildResult Build(string volumeId, IReadOnlyList<FileEntry> files,
        bool joliet = true, BootImage? boot = null, bool rockRidge = false)
        => BuildTree(volumeId, files.Select(f => Node.File(f.Name, f.Data)).ToList(), joliet, boot, rockRidge);

    /// <summary>In-memory build. Convenient for tests and small images; for
    /// anything disc-sized use <see cref="Plan"/> + <see cref="IsoLayout.WriteTo"/>,
    /// which has no 2 GB ceiling.</summary>
    public static BuildResult BuildTree(string volumeId, IReadOnlyList<Node> rootChildren,
        bool joliet = true, BootImage? boot = null, bool rockRidge = false)
    {
        var layout = Plan(volumeId, rootChildren, joliet, boot, rockRidge);
        if (layout.ImageBytes > int.MaxValue)
            throw new NotSupportedException(
                $"Image is {layout.ImageBytes:N0} bytes, which exceeds the in-memory limit. " +
                "Use Plan(...) and IsoLayout.WriteTo(stream) to stream it instead.");

        var ms = new MemoryStream((int)layout.ImageBytes);
        layout.WriteTo(ms);
        return new BuildResult(ms.ToArray(), layout.Warnings);
    }

    /// <summary>
    /// Compute the image layout. Only file *lengths* are read here — no payload
    /// data is touched — so this is cheap even for a DVD-sized tree.
    /// </summary>
    public static IsoLayout Plan(string volumeId, IReadOnlyList<Node> rootChildren,
        bool joliet = true, BootImage? boot = null, bool rockRidge = false)
    {
        var warnings = new List<string>();

        var root = new Dir { Name = "", Level = 0 };
        BuildGraph(root, rootChildren, warnings);
        root.Parent = root;

        var dirs = new List<Dir>();
        CollectDirs(root, dirs);

        var isoOrder = NumberDirectories(root, dirs, forJoliet: false);
        var jolOrder = joliet ? NumberDirectories(root, dirs, forJoliet: true) : new List<Dir>();

        int isoPt = isoOrder.Sum(d => PathTableRecordLength(d, forJoliet: false));
        int isoPts = CeilSectors(isoPt);
        int jolPt = joliet ? jolOrder.Sum(d => PathTableRecordLength(d, forJoliet: true)) : 0;
        int jolPts = joliet ? CeilSectors(jolPt) : 0;

        // Volume descriptor placement: PVD, [Boot Record], [SVD], terminator.
        int pvdSector = 16;
        int nextVd = 17;
        int bootRecSector = boot is not null ? nextVd++ : -1;
        int svdSector = joliet ? nextVd++ : -1;
        int termSector = nextVd++;
        int dataStart = nextVd;

        int isoPtL = dataStart;
        int isoPtM = isoPtL + isoPts;
        int jolPtL = isoPtM + isoPts;
        int jolPtM = jolPtL + jolPts;
        int cursor = jolPtM + jolPts;

        foreach (var d in isoOrder)
        {
            d.IsoSize = DirectoryContentSize(d, forJoliet: false, rockRidge, isRoot: d.Level == 0);
            d.IsoExtent = cursor;
            cursor += CeilSectors(d.IsoSize);
        }
        if (joliet)
            foreach (var d in jolOrder)
            {
                d.JolSize = DirectoryContentSize(d, forJoliet: true, rockRidge: false, isRoot: d.Level == 0);
                d.JolExtent = cursor;
                cursor += CeilSectors(d.JolSize);
            }

        int bootCatSector = -1, bootImgSector = -1;
        if (boot is not null)
        {
            bootCatSector = cursor; cursor += 1;
            bootImgSector = cursor; cursor += CeilSectors(boot.Data.Length);
        }

        // Everything up to here is metadata, held in memory when writing.
        int metaSectors = cursor;

        var files = new List<FileNode>();
        CollectFiles(root, files);
        foreach (var f in files)
        {
            long len = f.Size;
            if (len > uint.MaxValue)
                throw new NotSupportedException(
                    $"'{f.Name}' is {len:N0} bytes. ISO 9660 stores a file's length as a 32-bit " +
                    "value, so a single file cannot exceed 4 GiB - 1. Multi-extent files are not " +
                    "supported yet — split the file or use a different container.");
            if (len == 0) f.Extent = 0;
            else { f.Extent = cursor; cursor += CeilSectors(len); }
        }

        return new IsoLayout
        {
            VolumeId = volumeId,
            Root = root,
            IsoOrder = isoOrder,
            JolOrder = jolOrder,
            Files = files,
            Joliet = joliet,
            RockRidge = rockRidge,
            Boot = boot,
            PvdSector = pvdSector,
            BootRecSector = bootRecSector,
            SvdSector = svdSector,
            TermSector = termSector,
            IsoPt = isoPt, IsoPts = isoPts, IsoPtL = isoPtL, IsoPtM = isoPtM,
            JolPt = jolPt, JolPts = jolPts, JolPtL = jolPtL, JolPtM = jolPtM,
            BootCatSector = bootCatSector,
            BootImgSector = bootImgSector,
            MetaSectors = metaSectors,
            VolumeSectors = cursor,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Stream an image from a layout. The layout is emitted in strictly ascending
    /// sector order — system area, descriptors, path tables, directory records,
    /// boot catalog, then file payloads — so this needs no seeking and holds only
    /// the metadata region in memory.
    /// </summary>
    private static void WriteLayout(IsoLayout L, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var meta = new byte[(long)L.MetaSectors * SectorSize];

        WriteVolumeDescriptor(meta.AsSpan(L.PvdSector * SectorSize, SectorSize), 1, false,
            L.VolumeId, L.VolumeSectors, L.IsoPtL, L.IsoPtM, L.IsoPt, L.Root);
        if (L.Boot is not null)
            WriteBootRecord(meta.AsSpan(L.BootRecSector * SectorSize, SectorSize), L.BootCatSector);
        if (L.Joliet)
            WriteVolumeDescriptor(meta.AsSpan(L.SvdSector * SectorSize, SectorSize), 2, true,
                L.VolumeId, L.VolumeSectors, L.JolPtL, L.JolPtM, L.JolPt, L.Root);

        var term = meta.AsSpan(L.TermSector * SectorSize, SectorSize);
        term[0] = 0xFF;
        Encoding.ASCII.GetBytes("CD001").CopyTo(term.Slice(1));
        term[6] = 1;

        WritePathTable(meta.AsSpan(L.IsoPtL * SectorSize, L.IsoPts * SectorSize), L.IsoOrder, false, true);
        WritePathTable(meta.AsSpan(L.IsoPtM * SectorSize, L.IsoPts * SectorSize), L.IsoOrder, false, false);
        if (L.Joliet)
        {
            WritePathTable(meta.AsSpan(L.JolPtL * SectorSize, L.JolPts * SectorSize), L.JolOrder, true, true);
            WritePathTable(meta.AsSpan(L.JolPtM * SectorSize, L.JolPts * SectorSize), L.JolOrder, true, false);
        }

        foreach (var d in L.IsoOrder)
            WriteDirectory(meta.AsSpan(d.IsoExtent * SectorSize, CeilSectors(d.IsoSize) * SectorSize),
                d, false, L.RockRidge, d.Level == 0);
        if (L.Joliet)
            foreach (var d in L.JolOrder)
                WriteDirectory(meta.AsSpan(d.JolExtent * SectorSize, CeilSectors(d.JolSize) * SectorSize),
                    d, true, false, d.Level == 0);

        if (L.Boot is not null)
        {
            WriteBootCatalog(meta.AsSpan(L.BootCatSector * SectorSize, SectorSize), L.Boot, L.BootImgSector);
            L.Boot.Data.CopyTo(meta.AsSpan(L.BootImgSector * SectorSize));
        }

        output.Write(meta, 0, meta.Length);

        // File payloads, in the same (ascending) order their extents were assigned.
        var buffer = new byte[1 << 16];
        foreach (var f in L.Files)
        {
            long len = f.Size;
            if (len == 0) continue;

            using var src = f.Source.OpenRead();
            long copied = 0;
            int n;
            while (copied < len && (n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, len - copied))) > 0)
            {
                output.Write(buffer, 0, n);
                copied += n;
            }
            if (copied != len)
                throw new IOException(
                    $"'{f.Name}' changed while reading: expected {len:N0} bytes, got {copied:N0}.");

            long padding = (long)CeilSectors(len) * SectorSize - len;
            WriteZeros(output, padding, buffer);
        }
    }

    private static void WriteZeros(Stream output, long count, byte[] buffer)
    {
        Array.Clear(buffer);
        while (count > 0)
        {
            int n = (int)Math.Min(buffer.Length, count);
            output.Write(buffer, 0, n);
            count -= n;
        }
    }

    // ---- Internal graph ----

    // internal (not private): IsoLayout's internal fields reference these,
    // and a field cannot be more accessible than its type (CS0052).
    internal abstract class Entry
    {
        public required string Name { get; init; }
        public string IsoId = "\0";
        public byte[] JolId = { 0x00 };       // UTF-16BE
        public string JolName = "";            // for ordinal sorting
        public int Extent;                     // file extent (shared)
        /// <summary>Byte length of this entry's own data. Files derive it from
        /// their source (so it can never drift); directories carry per-hierarchy
        /// sizes in IsoSize/JolSize instead and leave this at 0. It's a long
        /// because a source file may exceed int range — Plan() rejects anything
        /// past the ISO 9660 u32 limit with a clear message.</summary>
        public virtual long Size => 0;
        public abstract bool IsDir { get; }
    }

    internal sealed class FileNode : Entry
    {
        public required FileSource Source { get; init; }
        public override bool IsDir => false;
        public override long Size => Source.Length;
    }

    internal sealed class Dir : Entry
    {
        public List<Entry> Children { get; } = new();
        public int IsoNumber, IsoExtent, IsoSize;
        public int JolNumber, JolExtent, JolSize;
        public int Level;
        public Dir Parent = null!;
        public override bool IsDir => true;
    }

    private static void BuildGraph(Dir parent, IReadOnlyList<Node> children, List<string> warnings)
    {
        foreach (var n in children)
        {
            if (n.IsDir)
            {
                var d = new Dir { Name = n.Name, Level = parent.Level + 1, Parent = parent };
                d.IsoId = ToDirId(n.Name, out var c1);
                d.JolName = JolietName(n.Name);
                d.JolId = Encoding.BigEndianUnicode.GetBytes(d.JolName);
                if (c1) warnings.Add($"'{n.Name}' -> ISO '{d.IsoId}' (8.3); Joliet keeps '{d.JolName}'.");
                parent.Children.Add(d);
                BuildGraph(d, n.Children, warnings);
            }
            else
            {
                var f = new FileNode
                {
                    Name = n.Name,
                    Source = n.Source ?? FileSource.FromBytes(Array.Empty<byte>()),
                };
                f.IsoId = ToFileId(n.Name, out var c2);
                f.JolName = JolietName(n.Name);
                f.JolId = Encoding.BigEndianUnicode.GetBytes(f.JolName);
                if (c2) warnings.Add($"'{n.Name}' -> ISO '{f.IsoId}' (8.3); Joliet keeps '{f.JolName}'.");
                parent.Children.Add(f);
            }
        }
    }

    private static void CollectDirs(Dir d, List<Dir> acc)
    {
        acc.Add(d);
        foreach (var c in d.Children) if (c is Dir sub) CollectDirs(sub, acc);
    }

    private static void CollectFiles(Dir d, List<FileNode> acc)
    {
        foreach (var c in d.Children)
        {
            if (c is Dir sub) CollectFiles(sub, acc);
            else if (c is FileNode f) acc.Add(f);
        }
    }

    private static string SortKey(Entry e, bool joliet) => joliet ? e.JolName : e.IsoId;

    private static List<Dir> NumberDirectories(Dir root, List<Dir> dirs, bool forJoliet)
    {
        var order = new List<Dir> { root };
        if (forJoliet) root.JolNumber = 1; else root.IsoNumber = 1;
        int counter = 2;
        int maxLevel = dirs.Count == 0 ? 0 : dirs.Max(d => d.Level);
        for (int lvl = 1; lvl <= maxLevel; lvl++)
        {
            var levelDirs = dirs.Where(d => d.Level == lvl)
                .OrderBy(d => forJoliet ? d.Parent.JolNumber : d.Parent.IsoNumber)
                .ThenBy(d => SortKey(d, forJoliet), StringComparer.Ordinal)
                .ToList();
            foreach (var d in levelDirs)
            {
                if (forJoliet) d.JolNumber = counter; else d.IsoNumber = counter;
                counter++; order.Add(d);
            }
        }
        return order;
    }

    private static int CeilSectors(long bytes) => (int)((bytes + SectorSize - 1) / SectorSize);

    private static int DirRecordLength(int idByteLength, int suLength = 0)
    {
        int len = 33 + idByteLength;
        if ((len & 1) != 0) len++;   // pad after identifier so SU starts even
        len += suLength;
        return (len & 1) != 0 ? len + 1 : len;
    }

    private static byte[] IdentBytes(Entry e, bool joliet) => joliet ? e.JolId : Encoding.ASCII.GetBytes(e.IsoId);

    private static int PathTableRecordLength(Dir d, bool forJoliet)
    {
        int idLen = (forJoliet ? d.JolNumber : d.IsoNumber) == 1
            ? 1
            : (forJoliet ? d.JolId.Length : Encoding.ASCII.GetByteCount(d.IsoId));
        int len = 8 + idLen;
        return (len & 1) != 0 ? len + 1 : len;
    }

    private static int DirectoryContentSize(Dir d, bool forJoliet, bool rockRidge, bool isRoot)
    {
        bool rr = rockRidge && !forJoliet;
        int selfSu = rr ? SuArea(isRoot ? SuKind.RootSelf : SuKind.Self, ".", true, 1).Length : 0;
        int parentSu = rr ? SuArea(SuKind.Parent, "..", true, 1).Length : 0;
        int pos = DirRecordLength(1, selfSu) + DirRecordLength(1, parentSu);
        foreach (var c in d.Children.OrderBy(x => SortKey(x, forJoliet), StringComparer.Ordinal))
        {
            int idLen = IdentBytes(c, forJoliet).Length;
            int su = rr ? SuArea(SuKind.Named, c.Name, c.IsDir, idLen).Length : 0;
            int rl = DirRecordLength(idLen, su);
            int inSector = pos % SectorSize;
            if (inSector + rl > SectorSize) pos += SectorSize - inSector;
            pos += rl;
        }
        return pos;
    }

    // ---- Writers ----

    private static void WriteBothU32(Span<byte> dst, uint v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, v);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(4), v);
    }

    private static void WriteBothU16(Span<byte> dst, ushort v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dst, v);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2), v);
    }

    private static void WriteVolumeDescriptor(Span<byte> vd, byte type, bool joliet,
        string volumeId, int volumeSectors, int ptL, int ptM, int ptSize, Dir root)
    {
        vd[0] = type;
        Encoding.ASCII.GetBytes("CD001").CopyTo(vd.Slice(1));
        vd[6] = 1;

        if (joliet)
        {
            // UCS-2 level 3 escape sequence.
            vd[88] = (byte)'%'; vd[89] = (byte)'/'; vd[90] = (byte)'E';
            WriteUcs2Field(vd.Slice(40, 32), volumeId);
        }
        else
        {
            WriteAField(vd.Slice(8, 32), "");
            WriteAField(vd.Slice(40, 32), volumeId.ToUpperInvariant());
        }

        WriteBothU32(vd.Slice(80, 8), (uint)volumeSectors);
        WriteBothU16(vd.Slice(120, 4), 1);
        WriteBothU16(vd.Slice(124, 4), 1);
        WriteBothU16(vd.Slice(128, 4), SectorSize);
        WriteBothU32(vd.Slice(132, 8), (uint)ptSize);
        BinaryPrimitives.WriteUInt32LittleEndian(vd.Slice(140, 4), (uint)ptL);
        BinaryPrimitives.WriteUInt32BigEndian(vd.Slice(148, 4), (uint)ptM);

        int rootExtent = joliet ? root.JolExtent : root.IsoExtent;
        int rootSize = joliet ? root.JolSize : root.IsoSize;
        WriteDirRecord(vd.Slice(156, 34), rootExtent, rootSize, true, SelfId);

        if (!joliet)
        {
            WriteAField(vd.Slice(318, 128), "OPENJUGGLER");
            WriteAField(vd.Slice(446, 128), "OPENJUGGLER");
            WriteAField(vd.Slice(574, 128), "OPENJUGGLER ISO BUILDER");
        }
        FillZeroDate(vd.Slice(813, 17));
        FillZeroDate(vd.Slice(830, 17));
        FillZeroDate(vd.Slice(847, 17));
        FillZeroDate(vd.Slice(864, 17));
        vd[881] = 1;
    }

    private static void WriteBootRecord(Span<byte> br, int bootCatalogSector)
    {
        br[0] = 0; // Boot Record descriptor
        Encoding.ASCII.GetBytes("CD001").CopyTo(br.Slice(1));
        br[6] = 1;
        Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION").CopyTo(br.Slice(7)); // zero-padded to 32
        BinaryPrimitives.WriteUInt32LittleEndian(br.Slice(71, 4), (uint)bootCatalogSector);
    }

    private static void WriteBootCatalog(Span<byte> cat, BootImage boot, int bootImageSector)
    {
        // Validation entry (bytes 0..31).
        cat[0] = 1;    // header id
        cat[1] = 0;    // platform: 80x86
        cat[30] = 0x55;
        cat[31] = 0xAA;
        // Checksum: all 16-bit LE words in the 32-byte entry must sum to 0.
        ushort sum = 0;
        for (int i = 0; i < 32; i += 2)
            sum += BinaryPrimitives.ReadUInt16LittleEndian(cat.Slice(i, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(cat.Slice(28, 2), (ushort)((0x10000 - sum) & 0xFFFF));

        // Default/initial entry (bytes 32..63).
        cat[32] = 0x88; // bootable
        cat[33] = (byte)boot.Media;
        BinaryPrimitives.WriteUInt16LittleEndian(cat.Slice(34, 2), 0); // load segment: default
        cat[36] = 0;    // system type
        int sectorCount = Math.Max(1, (boot.Data.Length + 511) / 512);
        BinaryPrimitives.WriteUInt16LittleEndian(cat.Slice(38, 2), (ushort)sectorCount);
        BinaryPrimitives.WriteUInt32LittleEndian(cat.Slice(40, 4), (uint)bootImageSector);
    }

    private static void WritePathTable(Span<byte> region, List<Dir> order, bool joliet, bool littleEndian)
    {
        int p = 0;
        foreach (var d in order)
        {
            int number = joliet ? d.JolNumber : d.IsoNumber;
            byte[] ident = number == 1 ? new byte[] { 0x00 } : (joliet ? d.JolId : Encoding.ASCII.GetBytes(d.IsoId));
            int extent = joliet ? d.JolExtent : d.IsoExtent;
            int parentNumber = joliet ? d.Parent.JolNumber : d.Parent.IsoNumber;

            region[p] = (byte)ident.Length;
            region[p + 1] = 0;
            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(region.Slice(p + 2, 4), (uint)extent);
                BinaryPrimitives.WriteUInt16LittleEndian(region.Slice(p + 6, 2), (ushort)parentNumber);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(region.Slice(p + 2, 4), (uint)extent);
                BinaryPrimitives.WriteUInt16BigEndian(region.Slice(p + 6, 2), (ushort)parentNumber);
            }
            ident.CopyTo(region.Slice(p + 8));
            int rl = 8 + ident.Length;
            if ((rl & 1) != 0) rl++;
            p += rl;
        }
    }

    private static readonly byte[] SelfId = { 0x00 };
    private static readonly byte[] ParentId = { 0x01 };

    private static void WriteDirectory(Span<byte> region, Dir d, bool joliet, bool rockRidge, bool isRoot)
    {
        bool rr = rockRidge && !joliet;
        int selfExtent = joliet ? d.JolExtent : d.IsoExtent;
        int selfSize = joliet ? d.JolSize : d.IsoSize;
        int parentExtent = joliet ? d.Parent.JolExtent : d.Parent.IsoExtent;
        int parentSize = joliet ? d.Parent.JolSize : d.Parent.IsoSize;

        byte[] selfSu = rr ? SuArea(isRoot ? SuKind.RootSelf : SuKind.Self, ".", true, 1) : Array.Empty<byte>();
        byte[] parentSu = rr ? SuArea(SuKind.Parent, "..", true, 1) : Array.Empty<byte>();

        int p = 0;
        p += WriteDirRecord(region.Slice(p), selfExtent, selfSize, true, SelfId, selfSu);
        p += WriteDirRecord(region.Slice(p), parentExtent, parentSize, true, ParentId, parentSu);

        foreach (var c in d.Children.OrderBy(x => SortKey(x, joliet), StringComparer.Ordinal))
        {
            var ident = IdentBytes(c, joliet);
            byte[] su = rr ? SuArea(SuKind.Named, c.Name, c.IsDir, ident.Length) : Array.Empty<byte>();
            int rl = DirRecordLength(ident.Length, su.Length);
            int inSector = p % SectorSize;
            if (inSector + rl > SectorSize) p += SectorSize - inSector;

            int extent = c.IsDir ? (joliet ? ((Dir)c).JolExtent : ((Dir)c).IsoExtent) : c.Extent;
            long size = c.IsDir ? (joliet ? ((Dir)c).JolSize : ((Dir)c).IsoSize) : c.Size;
            WriteDirRecord(region.Slice(p), extent, size, c.IsDir, ident, su);
            p += rl;
        }
    }

    private static int WriteDirRecord(Span<byte> dst, int extentSector, long dataLength, bool isDir, byte[] ident,
                                      byte[]? su = null)
    {
        // The record stores length as a both-endian u32; anything larger would
        // silently wrap. Plan() rejects such files up front, so this is a guard
        // against a future caller bypassing it rather than an expected path.
        if (dataLength < 0 || dataLength > uint.MaxValue)
            throw new NotSupportedException(
                $"Directory record length {dataLength:N0} exceeds the ISO 9660 32-bit limit.");

        int afterIdent = 33 + ident.Length;
        if ((afterIdent & 1) != 0) afterIdent++;       // pad byte so SU area is even-aligned
        int suLen = su?.Length ?? 0;
        int recLen = afterIdent + suLen;
        if ((recLen & 1) != 0) recLen++;

        dst[0] = (byte)recLen;
        dst[1] = 0;
        WriteBothU32(dst.Slice(2, 8), (uint)extentSector);
        WriteBothU32(dst.Slice(10, 8), (uint)dataLength);
        WriteFixedDateTime(dst.Slice(18, 7));
        dst[25] = (byte)(isDir ? 0x02 : 0x00);
        dst[26] = 0;
        dst[27] = 0;
        WriteBothU16(dst.Slice(28, 4), 1);
        dst[32] = (byte)ident.Length;
        ident.CopyTo(dst.Slice(33));
        if (suLen > 0) su!.CopyTo(dst.Slice(afterIdent));
        return recLen;
    }

    // ---- Rock Ridge (SUSP / RRIP) System Use entries ----

    private enum SuKind { RootSelf, Self, Parent, Named }

    private static byte[] SuArea(SuKind kind, string name, bool isDir, int identByteLen)
    {
        using var ms = new MemoryStream();
        if (kind == SuKind.RootSelf) { ms.Write(SuSp()); ms.Write(SuEr()); }
        if (kind == SuKind.Named)
        {
            // Keep the whole directory record under 255 bytes: cap the NM name.
            int baseEven = 33 + identByteLen; if ((baseEven & 1) != 0) baseEven++;
            int maxNm = 254 - baseEven - 5 /*NM hdr*/ - 36 /*PX*/ - 12 /*TF*/;
            var nb = Encoding.UTF8.GetBytes(name);
            if (nb.Length > maxNm && maxNm > 0)
                name = Encoding.UTF8.GetString(nb, 0, maxNm);
            ms.Write(SuNm(name));
        }
        ms.Write(SuPx(isDir));
        ms.Write(SuTf());
        return ms.ToArray();
    }

    private static byte[] SuSp() => new byte[] { (byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF, 0 };

    private static byte[] SuEr()
    {
        var id = Encoding.ASCII.GetBytes("RRIP_1991A");
        var b = new byte[8 + id.Length];
        b[0] = (byte)'E'; b[1] = (byte)'R'; b[2] = (byte)(8 + id.Length); b[3] = 1;
        b[4] = (byte)id.Length; b[5] = 0; b[6] = 0; b[7] = 1;
        id.CopyTo(b, 8);
        return b;
    }

    private static byte[] SuPx(bool isDir)
    {
        uint mode = isDir ? 0x41EDu /*040755*/ : 0x81A4u /*0100644*/;
        uint nlink = isDir ? 2u : 1u;
        var b = new byte[36];
        b[0] = (byte)'P'; b[1] = (byte)'X'; b[2] = 36; b[3] = 1;
        WriteBothU32(b.AsSpan(4, 8), mode);
        WriteBothU32(b.AsSpan(12, 8), nlink);
        WriteBothU32(b.AsSpan(20, 8), 0);
        WriteBothU32(b.AsSpan(28, 8), 0);
        return b;
    }

    private static byte[] SuTf()
    {
        var b = new byte[12];
        b[0] = (byte)'T'; b[1] = (byte)'F'; b[2] = 12; b[3] = 1; b[4] = 0x02; // modify time
        WriteFixedDateTime(b.AsSpan(5, 7));
        return b;
    }

    private static byte[] SuNm(string name)
    {
        var nb = Encoding.UTF8.GetBytes(name);
        var b = new byte[5 + nb.Length];
        b[0] = (byte)'N'; b[1] = (byte)'M'; b[2] = (byte)(5 + nb.Length); b[3] = 1; b[4] = 0;
        nb.CopyTo(b, 5);
        return b;
    }

    // ---- Field helpers ----

    private static void WriteAField(Span<byte> dst, string s)
    {
        dst.Fill((byte)' ');
        var b = Encoding.ASCII.GetBytes(s);
        b.AsSpan(0, Math.Min(b.Length, dst.Length)).CopyTo(dst);
    }

    private static void WriteUcs2Field(Span<byte> dst, string s)
    {
        // UCS-2 big-endian, padded with UCS-2 spaces (0x00 0x20).
        for (int i = 0; i + 1 < dst.Length; i += 2) { dst[i] = 0x00; dst[i + 1] = 0x20; }
        var b = Encoding.BigEndianUnicode.GetBytes(s);
        b.AsSpan(0, Math.Min(b.Length, dst.Length)).CopyTo(dst);
    }

    // Deterministic timestamp (2025-01-01T00:00:00Z-ish) for reproducible builds.
    private static void WriteFixedDateTime(Span<byte> dst)
    {
        dst[0] = 125; dst[1] = 1; dst[2] = 1; dst[3] = 0; dst[4] = 0; dst[5] = 0; dst[6] = 0;
    }

    private static void FillZeroDate(Span<byte> dst)
    {
        for (int i = 0; i < 16; i++) dst[i] = (byte)'0';
        dst[16] = 0;
    }

    private static string ToFileId(string name, out bool changed)
    {
        string orig = Path.GetFileName(name).ToUpperInvariant();
        string baseName = Clean(Path.GetFileNameWithoutExtension(orig));
        string ext = Clean(Path.GetExtension(orig).TrimStart('.'));
        if (baseName.Length == 0) baseName = "FILE";
        if (baseName.Length > 8) baseName = baseName[..8];
        if (ext.Length > 3) ext = ext[..3];
        string id = ext.Length > 0 ? $"{baseName}.{ext};1" : $"{baseName}.;1";
        string ideal = $"{Path.GetFileNameWithoutExtension(orig)}.{Path.GetExtension(orig).TrimStart('.')};1";
        changed = id != ideal;
        return id;
    }

    private static string ToDirId(string name, out bool changed)
    {
        string upper = Path.GetFileName(name).ToUpperInvariant();
        string cleaned = Clean(upper);
        if (cleaned.Length == 0) cleaned = "DIR";
        if (cleaned.Length > 8) cleaned = cleaned[..8];
        changed = cleaned != upper;
        return cleaned;
    }

    private static string Clean(string s) =>
        new(s.Select(c => (c is >= 'A' and <= 'Z' or >= '0' and <= '9') ? c : '_').ToArray());

    private static string JolietName(string name)
    {
        var bad = new HashSet<char> { '*', '/', ':', ';', '?', '\\' };
        var cleaned = new string(Path.GetFileName(name).Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }
}
