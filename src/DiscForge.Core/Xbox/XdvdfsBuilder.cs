// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Xbox;

/// <summary>
/// Writes an XDVDFS image (a trimmed "XISO") — the other half of
/// <see cref="XdvdfsReader"/>, for repacking a folder of files into the Original
/// Xbox disc filesystem. It produces the base-0 XISO shape: a volume descriptor
/// at sector 32, then each directory's entry table and each file's data.
///
/// Directory entries within a table form a binary tree. Microsoft's authoring
/// balances that tree, and so does this writer: entries are name-sorted, then a
/// balanced binary search tree is built over them (mid element as each subtree
/// root) and laid out pre-order so the root sits at table offset 0 where a reader
/// begins. Lookups are O(log n), which is the shape mastered discs use rather
/// than a right-leaning chain. Directory tables that span more than one sector
/// are handled too: no entry may straddle a 2048-byte boundary, so an entry that
/// would cross one is pushed to the next sector (the skipped bytes become 0xFF
/// padding), and because subtree links are byte-offset/4 the tree still resolves
/// across sectors. A round-trip test with 300 entries exercises that path.
///
/// Builds are deterministic (a fixed timestamp), so the same tree yields
/// byte-identical output — which is what makes the round-trip test meaningful.
/// </summary>
public static class XdvdfsBuilder
{
    public const int SectorSize = 2048;
    private const int VolumeDescriptorSector = 32;
    private const int DataStartSector = 33;
    private const ushort NoSubtree = 0xFFFF;
    private const byte AttrDirectory = 0x10;
    private const byte AttrNormal = 0x20;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

    // ---- the tree the caller supplies --------------------------------------

    public abstract class Node
    {
        public string Name { get; }
        private protected Node(string name) => Name = name;

        /// <summary>A file whose whole content is in memory.</summary>
        public static Node File(string name, byte[] data) =>
            new FileNode(name, data.Length) { Data = data };

        /// <summary>A file streamed on demand from <paramref name="open"/> — its
        /// bytes are never all held in memory, so an image larger than RAM (or the
        /// 2 GB byte[] limit) can be written with <see cref="BuildToStream"/>.</summary>
        public static Node File(string name, long length, Func<Stream> open) =>
            new FileNode(name, length) { Open = open };

        /// <summary>A file streamed from a path on disk.</summary>
        public static Node FileFromPath(string name, string path) =>
            new FileNode(name, new FileInfo(path).Length) { Open = () => System.IO.File.OpenRead(path) };

        public static Node Dir(string name, IEnumerable<Node> children) =>
            new DirNode(name, children.ToList());
    }

    internal sealed class FileNode(string name, long length) : Node(name)
    {
        public long Length { get; } = length;
        /// <summary>In-memory content, or null when the file is streamed.</summary>
        public byte[]? Data { get; init; }
        /// <summary>Opens the content stream, or null when the file is in memory.</summary>
        public Func<Stream>? Open { get; init; }
    }

    internal sealed class DirNode(string name, List<Node> children) : Node(name)
    {
        public IReadOnlyList<Node> Children { get; } = children;
    }

    public sealed record BuildResult(byte[] Image, IReadOnlyList<string> Warnings);

    // ---- entry point --------------------------------------------------------

    public static byte[] Build(IEnumerable<Node> rootChildren) => BuildResultOf(rootChildren).Image;

    public static BuildResult BuildResultOf(IEnumerable<Node> rootChildren)
    {
        ArgumentNullException.ThrowIfNull(rootChildren);
        var (root, imageBytes, warnings) = PlanTree(rootChildren);

        if (imageBytes > int.MaxValue)
            throw new NotSupportedException(
                $"This tree needs a {imageBytes / (1024 * 1024):N0} MB image, past the in-memory " +
                "builder's ~2 GB ceiling. Use BuildToStream (dforge create-xiso writes to disk) for " +
                "full-size images.");

        using var ms = new MemoryStream((int)imageBytes);
        WriteToStream(ms, root, imageBytes);
        return new BuildResult(ms.ToArray(), warnings);
    }

    /// <summary>
    /// Write the XISO straight to a seekable stream — the streamed path with no
    /// 2 GB ceiling. Files supplied via <see cref="Node.File(string, long, Func{Stream})"/>
    /// or <see cref="Node.FileFromPath"/> are copied through without ever being
    /// fully held in memory, so an image larger than RAM can be authored. Returns
    /// any layout warnings. Output must be seekable (a file, not a pipe).
    /// </summary>
    public static IReadOnlyList<string> BuildToStream(Stream output, IEnumerable<Node> rootChildren)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rootChildren);
        if (!output.CanSeek)
            throw new ArgumentException("Writing an XISO needs a seekable stream (a file).", nameof(output));

        var (root, imageBytes, warnings) = PlanTree(rootChildren);
        WriteToStream(output, root, imageBytes);
        return warnings;
    }

    // Plan the tree and assign every file/directory a start sector; returns the
    // planned root and the total image size in bytes.
    private static (Planned Root, long ImageBytes, List<string> Warnings) PlanTree(IEnumerable<Node> rootChildren)
    {
        var warnings = new List<string>();
        var root = new Planned(new DirNode("", rootChildren.ToList()), isDir: true);
        Plan(root, warnings);

        // Assign sectors post-order: a directory's table references its children's
        // start sectors, so children must be placed first.
        uint cursor = DataStartSector;
        AssignSectors(root, ref cursor, warnings);
        return (root, (long)cursor * SectorSize, warnings);
    }

    // ---- planning -----------------------------------------------------------

    private sealed class Planned(Node source, bool isDir)
    {
        public Node Source { get; } = source;
        public bool IsDir { get; } = isDir;
        public List<Planned> Children { get; } = new();

        public uint Sector;        // start sector of data (file) or entry table (dir)
        public uint Size;          // byte size of data (file) or table (dir)
        public byte[]? Table;      // built entry table, for directories
    }

    private static void Plan(Planned parent, List<string> warnings)
    {
        if (parent.Source is not DirNode dir) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in dir.Children)
        {
            if (!seen.Add(child.Name))
                warnings.Add($"Duplicate name '{child.Name}' in a directory — XDVDFS requires unique " +
                             "names; the later entry may be unreachable.");
            var planned = new Planned(child, child is DirNode);
            parent.Children.Add(planned);
            if (child is DirNode) Plan(planned, warnings);
        }
    }

    private static void AssignSectors(Planned node, ref uint cursor, List<string> warnings)
    {
        // Files first (post-order), so a directory's table can reference them.
        foreach (var child in node.Children)
        {
            if (child.IsDir)
            {
                AssignSectors(child, ref cursor, warnings);
            }
            else
            {
                long length = ((FileNode)child.Source).Length;
                child.Size = (uint)length;
                child.Sector = cursor;
                if (length > 0) cursor += Sectors(length);
            }
        }

        // Now every child has a start sector: build this directory's table.
        node.Table = BuildTable(node, warnings);
        node.Size = (uint)node.Table.Length;
        node.Sector = cursor;
        if (node.Table.Length > 0) cursor += Sectors(node.Table.Length);
    }

    /// <summary>Compose a directory's entry table: children name-sorted, built into
    /// a balanced binary search tree, laid out pre-order (root at offset 0).</summary>
    private static byte[] BuildTable(Planned dir, List<string> warnings)
    {
        var children = dir.Children
            .OrderBy(c => c.Source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (children.Count == 0) return Array.Empty<byte>();

        // Build a balanced binary search tree over the name-sorted children, so
        // lookups are O(log n) rather than the O(n) of a right-leaning chain —
        // the shape Microsoft's authoring uses. The tree's root must sit at table
        // offset 0 (where a reader begins), so entries are laid out pre-order
        // (root first, then left subtree, then right).
        var root = BuildBalanced(children, 0, children.Count - 1);

        var flat = new List<TreeNode>();
        PreOrder(root, flat);

        // Lay entries out pre-order. XDVDFS forbids a directory entry from
        // straddling a 2048-byte sector boundary, so when the next entry would
        // cross one, advance to the start of the next sector; the skipped bytes
        // become 0xFF padding. Because subtree links are byte-offset/4 the tree
        // still resolves across sectors — this is what makes multi-sector
        // directory tables read correctly.
        int offset = 0;
        foreach (var node in flat)
        {
            node.Bytes = EncodeEntry(node.Entry);
            int within = offset % SectorSize;
            if (within + node.Bytes.Length > SectorSize)
                offset += SectorSize - within;      // pad to the next sector
            node.Offset = offset;
            offset += node.Bytes.Length;
        }

        // Subtree links are 16-bit word offsets (offset / 4), so the whole table
        // must stay within 0xFFFF words. That is 128 sectors — vast for a
        // directory, but guard it rather than emit a table that silently wraps.
        if (offset / 4 > NoSubtree - 1)
            warnings.Add($"A directory has too many entries to address ({offset:N0} bytes of table); " +
                         "the image may not read correctly.");

        // Round up to whole sectors and fill with 0xFF — XDVDFS pads unused
        // directory-table space with 0xFF, and the size is sector-granular.
        int total = (int)Sectors(offset) * SectorSize;
        var table = new byte[total];
        table.AsSpan().Fill(0xFF);
        foreach (var node in flat)
        {
            node.Bytes!.CopyTo(table, node.Offset);
            ushort left = node.Left is not null ? (ushort)(node.Left.Offset / 4) : NoSubtree;
            ushort right = node.Right is not null ? (ushort)(node.Right.Offset / 4) : NoSubtree;
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(node.Offset, 2), left);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(node.Offset + 2, 2), right);
        }
        return table;
    }

    private sealed class TreeNode(Planned entry)
    {
        public Planned Entry { get; } = entry;
        public TreeNode? Left;
        public TreeNode? Right;
        public int Offset;
        public byte[]? Bytes;
    }

    private static TreeNode? BuildBalanced(IReadOnlyList<Planned> sorted, int lo, int hi)
    {
        if (lo > hi) return null;
        int mid = (lo + hi) / 2;
        return new TreeNode(sorted[mid])
        {
            Left = BuildBalanced(sorted, lo, mid - 1),
            Right = BuildBalanced(sorted, mid + 1, hi),
        };
    }

    private static void PreOrder(TreeNode? node, List<TreeNode> acc)
    {
        if (node is null) return;
        acc.Add(node);
        PreOrder(node.Left, acc);
        PreOrder(node.Right, acc);
    }

    private static byte[] EncodeEntry(Planned node)
    {
        var nameBytes = Encoding.ASCII.GetBytes(node.Source.Name);
        int len = 14 + nameBytes.Length;
        len = (len + 3) & ~3;   // pad to a 4-byte boundary
        var e = new byte[len];

        // left/right are filled in by BuildTable once offsets are known.
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4, 4), node.Sector);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8, 4), node.Size);
        e[12] = node.IsDir ? AttrDirectory : AttrNormal;
        e[13] = (byte)nameBytes.Length;
        nameBytes.CopyTo(e, 14);
        return e;
    }

    // ---- writing ------------------------------------------------------------

    // Write the whole image to a seekable stream: zero-fill to the final size,
    // then place the volume descriptor and every table/file at its sector.
    private static void WriteToStream(Stream output, Planned root, long imageBytes)
    {
        output.SetLength(imageBytes);   // zero-fills the gaps between structures
        WriteVolumeDescriptor(output, root);
        WriteTree(output, root);
        output.Flush();
    }

    private static void WriteVolumeDescriptor(Stream output, Planned root)
    {
        var sector = new byte[SectorSize];
        Magic.CopyTo(sector, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0x14, 4), root.Sector);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0x18, 4), root.Size);
        // Timestamp left zero for deterministic output.
        Magic.CopyTo(sector, 0x7EC);

        output.Seek((long)VolumeDescriptorSector * SectorSize, SeekOrigin.Begin);
        output.Write(sector, 0, sector.Length);
    }

    private static void WriteTree(Stream output, Planned node)
    {
        if (node.IsDir && node.Table is { Length: > 0 })
        {
            output.Seek((long)node.Sector * SectorSize, SeekOrigin.Begin);
            output.Write(node.Table, 0, node.Table.Length);
        }
        else if (!node.IsDir)
        {
            var file = (FileNode)node.Source;
            if (file.Length > 0)
            {
                output.Seek((long)node.Sector * SectorSize, SeekOrigin.Begin);
                if (file.Data is not null)
                    output.Write(file.Data, 0, file.Data.Length);
                else if (file.Open is not null)
                    CopyExact(file.Open(), output, file.Length);
            }
        }

        foreach (var child in node.Children) WriteTree(output, child);
    }

    // Copy exactly `count` bytes (or until the source ends) in chunks, so a large
    // file never lands wholly in memory.
    private static void CopyExact(Stream source, Stream dest, long count)
    {
        using (source)
        {
            var buffer = new byte[64 * 1024];
            long remaining = count;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int n = source.Read(buffer, 0, want);
                if (n <= 0) break;
                dest.Write(buffer, 0, n);
                remaining -= n;
            }
        }
    }

    private static uint Sectors(long bytes) => (uint)((bytes + SectorSize - 1) / SectorSize);
}
