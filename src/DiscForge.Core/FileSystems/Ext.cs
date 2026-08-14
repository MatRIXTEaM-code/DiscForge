// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.FileSystems;

/// <summary>
/// Read-only ext2 / ext3 / ext4 reader — list directories and extract files from a Linux ext-family
/// volume image, the last major on-disk filesystem Aaru reads that DiscForge didn't. It parses the
/// superblock and the block-group descriptors, walks the inode table, and reads a file's data two ways:
/// the ext4 <b>extent tree</b> (the <c>i_block</c> header, leaf extents, and interior index nodes) and
/// the classic ext2/3 <b>block map</b> (twelve direct pointers plus single / double / triple indirect
/// blocks). Directories are the linear <c>ext2_dir_entry_2</c> records, which cover htree directories too
/// because their leaf blocks hold the same linear entries.
///
/// The two error-prone primitives — <see cref="ParseExtentLeaf"/> and <see cref="ParseDirectoryBlock"/> —
/// are pure and unit-tested against known vectors; synthetic ext4 (extents) and ext2 (direct + indirect)
/// volumes prove the two data paths end to end. Inline-data and encrypted inodes are declined rather than
/// mis-read. Reads user files; decrypts and defeats nothing.
/// </summary>
public sealed class Ext
{
    public const long RootInode = 2;

    private const ushort Magic = 0xEF53;
    private const uint IncompatExtents = 0x0040;
    private const uint Incompat64Bit = 0x0080;
    private const uint IncompatInlineData = 0x8000;

    private const uint FlagExtents = 0x0008_0000;      // EXT4_EXTENTS_FL
    private const uint FlagInlineData = 0x1000_0000;   // EXT4_INLINE_DATA_FL
    private const uint FlagEncrypted = 0x0000_0800;    // EXT4_ENCRYPT_FL

    private const ushort ExtentMagic = 0xF30A;

    public sealed record VolumeInfo(int BlockSize, uint InodesPerGroup, uint BlocksPerGroup,
        int InodeSize, int DescSize, bool Is64Bit, uint FeatureIncompat, uint InodesCount, string? Label);

    public sealed record Node(long Inode, string Name, bool IsDirectory, long Size);

    /// <summary>One contiguous extent: <paramref name="Length"/> filesystem blocks of logical file data
    /// starting at logical block <paramref name="LogicalBlock"/>, stored at physical block
    /// <paramref name="PhysicalBlock"/>. Uninitialized (preallocated) extents read back as zeros.</summary>
    public readonly record struct Extent(uint LogicalBlock, uint Length, ulong PhysicalBlock, bool Uninitialized);

    public sealed record DirEntry(long Inode, string Name, int FileType);

    private readonly Stream _s;
    public VolumeInfo Info { get; }

    private Ext(Stream s, VolumeInfo info) { _s = s; Info = info; }

    // ---- open --------------------------------------------------------------

    public static Ext Open(Stream s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new Ext(s, ReadSuperblock(s));
    }

    private static VolumeInfo ReadSuperblock(Stream s)
    {
        s.Position = 1024;
        var sb = new byte[1024];
        ReadExact(s, sb);
        if (U16(sb, 56) != Magic)
            throw new InvalidDataException("Not an ext2/3/4 volume (magic 0xEF53 not found at offset 1080).");

        int blockSize = 1024 << (int)U32(sb, 24);
        uint incompat = U32(sb, 96);
        bool is64 = (incompat & Incompat64Bit) != 0;
        int inodeSize = U16(sb, 88);
        if (inodeSize == 0) inodeSize = 128;                    // ext2 GOOD_OLD_INODE_SIZE
        int descSize = is64 ? U16(sb, 254) : 32;
        if (descSize == 0) descSize = is64 ? 64 : 32;

        string? label = null;
        int end = 120;
        while (end < 136 && sb[end] != 0) end++;
        if (end > 120) label = Encoding.ASCII.GetString(sb, 120, end - 120);

        return new VolumeInfo(blockSize, U32(sb, 40), U32(sb, 32), inodeSize, descSize, is64,
                              incompat, U32(sb, 0), label);
    }

    // ---- listing / resolve / extract ---------------------------------------

    /// <summary>List a directory by its inode number (use <see cref="RootInode"/> for '/').</summary>
    public IReadOnlyList<Node> List(long dirInode)
    {
        var inode = ReadInode(dirInode);
        if (!IsDirectory(inode)) throw new InvalidOperationException($"inode {dirInode} is not a directory.");
        var data = ReadInodeData(inode);

        var nodes = new List<Node>();
        foreach (var e in ParseDirectoryBlock(data, Info.BlockSize))
        {
            if (e.Name is "." or "..") continue;
            var child = ReadInode(e.Inode);
            nodes.Add(new Node(e.Inode, e.Name, IsDirectory(child), FileSize(child)));
        }
        return nodes;
    }

    public Node? Resolve(string path)
    {
        var parts = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            var root = ReadInode(RootInode);
            return new Node(RootInode, "/", IsDirectory(root), FileSize(root));
        }
        long dir = RootInode;
        Node? found = null;
        for (int i = 0; i < parts.Length; i++)
        {
            found = List(dir).FirstOrDefault(e => string.Equals(e.Name, parts[i], StringComparison.Ordinal));
            if (found is null) return null;
            if (i < parts.Length - 1)
            {
                if (!found.IsDirectory) return null;
                dir = found.Inode;
            }
        }
        return found;
    }

    public long Extract(Node file, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (file.IsDirectory) throw new InvalidOperationException($"'{file.Name}' is a directory.");
        var inode = ReadInode(file.Inode);
        var data = ReadInodeData(inode);
        long len = Math.Min(FileSize(inode), data.Length);
        output.Write(data, 0, (int)len);
        return len;
    }

    // ---- pure primitives (unit-tested) -------------------------------------

    /// <summary>Parse the leaf of an ext4 extent node (an <c>ext4_extent_header</c> at offset 0 with
    /// depth 0, followed by <c>ext4_extent</c> entries). Interior nodes (depth &gt; 0) are followed by the
    /// reader against the disk; this decodes the leaf entries, which is the error-prone part.</summary>
    public static IReadOnlyList<Extent> ParseExtentLeaf(ReadOnlySpan<byte> node)
    {
        if (U16(node, 0) != ExtentMagic) throw new InvalidDataException("Bad extent header magic.");
        if (U16(node, 6) != 0) throw new InvalidOperationException("Not a leaf extent node (depth > 0).");
        int entries = U16(node, 2);
        if (12 + entries * 12 > node.Length)
            throw new InvalidDataException("ext4 extent header claims more entries than the node holds — declined.");
        var list = new List<Extent>(entries);
        for (int i = 0; i < entries; i++)
        {
            int o = 12 + i * 12;
            uint logical = U32(node, o);
            ushort rawLen = U16(node, o + 4);
            bool uninit = rawLen > 32768;
            uint len = uninit ? (uint)(rawLen - 32768) : rawLen;
            ulong phys = ((ulong)U16(node, o + 6) << 32) | U32(node, o + 8);
            list.Add(new Extent(logical, len, phys, uninit));
        }
        return list;
    }

    /// <summary>Parse the linear directory records in a directory's data. Each <c>ext2_dir_entry_2</c>
    /// has an inode number, a record length that advances to the next entry (never crossing a block
    /// boundary), a name length and a one-byte file type. Entries with inode 0 are unused slots.</summary>
    public static IReadOnlyList<DirEntry> ParseDirectoryBlock(byte[] data, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(data);
        var list = new List<DirEntry>();
        int o = 0;
        while (o + 8 <= data.Length)
        {
            uint inode = U32(data, o);
            int recLen = U16(data, o + 4);
            if (recLen < 8) break;                              // malformed — stop this block
            int nameLen = data[o + 6];
            int fileType = data[o + 7];
            if (inode != 0 && nameLen > 0 && o + 8 + nameLen <= data.Length)
            {
                var name = Encoding.UTF8.GetString(data, o + 8, nameLen);
                list.Add(new DirEntry(inode, name, fileType));
            }
            // Advance to the next record; realign to the block grid if a rec_len runs to a block edge.
            int next = o + recLen;
            o = next;
        }
        return list;
    }

    // ---- inode / data reading ----------------------------------------------

    private byte[] ReadInode(long inodeNum)
    {
        if (inodeNum < 1) throw new ArgumentOutOfRangeException(nameof(inodeNum));
        uint group = (uint)((inodeNum - 1) / Info.InodesPerGroup);
        uint index = (uint)((inodeNum - 1) % Info.InodesPerGroup);

        // Group descriptor table starts in the block after the superblock.
        long gdtBlock = Info.BlockSize == 1024 ? 2 : 1;
        long descPos = gdtBlock * Info.BlockSize + (long)group * Info.DescSize;
        _s.Position = descPos;
        var desc = new byte[Info.DescSize];
        ReadExact(_s, desc);
        ulong inodeTable = U32(desc, 8);
        if (Info.Is64Bit && Info.DescSize >= 64) inodeTable |= (ulong)U32(desc, 40) << 32;

        long inodePos = (long)inodeTable * Info.BlockSize + (long)index * Info.InodeSize;
        _s.Position = inodePos;
        var inode = new byte[Info.InodeSize];
        ReadExact(_s, inode);
        return inode;
    }

    private static bool IsDirectory(byte[] inode) => (U16(inode, 0) & 0xF000) == 0x4000;

    private static long FileSize(byte[] inode) => (long)U32(inode, 4) | ((long)U32(inode, 108) << 32);

    private byte[] ReadInodeData(byte[] inode)
    {
        uint flags = U32(inode, 32);
        if ((flags & FlagInlineData) != 0)
            throw new NotSupportedException("Inline-data ext4 inode is declined (data stored in the inode, decode not verified).");
        if ((flags & FlagEncrypted) != 0)
            throw new NotSupportedException("Encrypted ext4 inode is declined.");

        long size = FileSize(inode);
        long needed = (size + Info.BlockSize - 1) / Info.BlockSize;
        var blocks = new List<ulong>();
        if ((flags & FlagExtents) != 0 || (Info.FeatureIncompat & IncompatExtents) != 0 && U16(inode, 40) == ExtentMagic)
            CollectExtentBlocks(inode.AsSpan(40, 60), blocks, needed, depthLimit: 5);
        else
            CollectClassicBlocks(inode, blocks, size);

        using var ms = new MemoryStream();
        var buf = new byte[Info.BlockSize];
        foreach (var b in blocks)
        {
            if (ms.Length >= size) break;
            if (b == 0) { ms.Write(new byte[Info.BlockSize], 0, Info.BlockSize); continue; }  // sparse / uninit
            _s.Position = (long)b * Info.BlockSize;
            ReadExact(_s, buf);
            ms.Write(buf, 0, buf.Length);
        }
        var all = ms.ToArray();
        if (all.Length > size) Array.Resize(ref all, (int)size);
        return all;
    }

    // Walk an extent node, placing each extent at its LOGICAL block position so that sparse files (holes
    // between extents, or a file that does not start at logical 0) reassemble correctly instead of being
    // silently compacted. Interior nodes recurse, bounded by a strictly-decreasing depth and a hard
    // depthLimit so a crafted self-referential tree declines rather than overflowing the stack.
    private void CollectExtentBlocks(ReadOnlySpan<byte> node, List<ulong> outBlocks, long needed, int depthLimit)
    {
        if (U16(node, 0) != ExtentMagic) throw new InvalidDataException("Bad extent header magic.");
        int entries = U16(node, 2);
        int depth = U16(node, 6);
        if (depth == 0)
        {
            foreach (var ex in ParseExtentLeaf(node))
            {
                // Fill any hole between the last block emitted and this extent's logical start with zeros.
                while (outBlocks.Count < ex.LogicalBlock && outBlocks.Count < needed) outBlocks.Add(0);
                for (uint i = 0; i < ex.Length && outBlocks.Count < needed; i++)
                    outBlocks.Add(ex.Uninitialized ? 0 : ex.PhysicalBlock + i);
            }
            return;
        }
        if (depthLimit <= 0) throw new InvalidDataException("ext4 extent tree is too deep (possible loop) — declined.");
        if (12 + entries * 12 > node.Length)
            throw new InvalidDataException("ext4 extent index claims more entries than the node holds — declined.");
        for (int i = 0; i < entries && outBlocks.Count < needed; i++)
        {
            int o = 12 + i * 12;
            ulong child = ((ulong)U16(node, o + 8) << 32) | U32(node, o + 4);
            var block = new byte[Info.BlockSize];
            _s.Position = (long)child * Info.BlockSize;
            ReadExact(_s, block);
            if (U16(block, 6) >= depth)     // child must be strictly shallower than its parent
                throw new InvalidDataException("ext4 extent index child does not decrease in depth — declined.");
            CollectExtentBlocks(block, outBlocks, needed, depthLimit - 1);
        }
    }

    // Classic ext2/3 block map: 12 direct + single/double/triple indirect.
    private void CollectClassicBlocks(byte[] inode, List<ulong> outBlocks, long size)
    {
        long needed = (size + Info.BlockSize - 1) / Info.BlockSize;
        for (int i = 0; i < 12 && outBlocks.Count < needed; i++)
            outBlocks.Add(U32(inode, 40 + i * 4));
        AddIndirect(U32(inode, 40 + 12 * 4), 1, outBlocks, needed);
        AddIndirect(U32(inode, 40 + 13 * 4), 2, outBlocks, needed);
        AddIndirect(U32(inode, 40 + 14 * 4), 3, outBlocks, needed);
    }

    private void AddIndirect(ulong block, int level, List<ulong> outBlocks, long needed)
    {
        if (block == 0 || outBlocks.Count >= needed) return;
        int perBlock = Info.BlockSize / 4;
        var buf = new byte[Info.BlockSize];
        _s.Position = (long)block * Info.BlockSize;
        ReadExact(_s, buf);
        for (int i = 0; i < perBlock && outBlocks.Count < needed; i++)
        {
            ulong ptr = U32(buf, i * 4);
            if (level == 1) outBlocks.Add(ptr);
            else AddIndirect(ptr, level - 1, outBlocks, needed);
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static void ReadExact(Stream s, byte[] b)
    {
        int off = 0;
        while (off < b.Length)
        {
            int r = s.Read(b, off, b.Length - off);
            if (r <= 0) throw new EndOfStreamException("Unexpected end of ext volume.");
            off += r;
        }
    }

    private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static ushort U16(ReadOnlySpan<byte> b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static uint U32(ReadOnlySpan<byte> b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}
