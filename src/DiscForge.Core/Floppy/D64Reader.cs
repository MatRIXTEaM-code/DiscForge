// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text;

namespace DiscForge.Core.Floppy;

/// <summary>Commodore 1541 file types (low nibble of the directory type byte).</summary>
public enum D64FileType
{
    Del = 0,
    Seq = 1,
    Prg = 2,
    Usr = 3,
    Rel = 4,
}

/// <summary>One entry in a D64 directory.</summary>
public sealed record D64Entry
{
    /// <summary>Filename, PETSCII decoded to ASCII with 0xA0 padding stripped.</summary>
    public required string Name { get; init; }
    public required D64FileType Type { get; init; }
    /// <summary>Whether the "closed" flag (bit 7 of the type byte) was set — a normal, complete file.</summary>
    public required bool Closed { get; init; }
    public required int SizeBlocks { get; init; }
    public required int FirstTrack { get; init; }
    public required int FirstSector { get; init; }
}

/// <summary>A parsed Commodore 64 / 1541 D64 disk image.</summary>
public sealed record D64Disk
{
    public required string DiskName { get; init; }
    public required string DiskId { get; init; }
    public required IReadOnlyList<D64Entry> Files { get; init; }
    /// <summary>35 or 40, depending on the image size.</summary>
    public required int Tracks { get; init; }
}

/// <summary>
/// Reads a Commodore 64 / 1541 D64 disk image — directory listing and file
/// extraction. Clean-room, from the public D64 layout.
///
/// A standard D64 is 683 blocks × 256 bytes (174,848 bytes, 35 tracks). A
/// 40-track variant (196,608 bytes) is also accepted. Sectors per track vary by
/// zone: tracks 1–17 → 21, 18–24 → 19, 25–30 → 18, 31–40 → 17. The BAM sits at
/// track 18 sector 0 (disk name at 0x90, disk id at 0xA2) and the directory chain
/// begins at track 18 sector 1. Files are stored as a track/sector-linked chain of
/// 256-byte blocks, each holding a 2-byte next-T/S link and 254 data bytes; on the
/// final block the track link is 0 and the sector link points at the last used byte.
///
/// PETSCII → ASCII mapping (printable range only): 0x20–0x5F pass through
/// unchanged (space, digits, punctuation and the unshifted uppercase A–Z at
/// 0x41–0x5A); 0xC1–0xDA (the shifted uppercase duplicates) map to A–Z; 0xA0 is
/// padding and is stripped; anything else becomes '?'.
/// </summary>
public static class D64Reader
{
    public const int BlockSize = 256;
    public const int Size35 = 174848;
    public const int Size40 = 196608;

    /// <summary>True if the byte length matches a supported D64 image.</summary>
    public static bool IsD64(long length) => length == Size35 || length == Size40;

    /// <summary>True if the buffer is a supported D64 image.</summary>
    public static bool IsD64(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return IsD64(data.LongLength);
    }

    public static D64Disk Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Read(ms.ToArray());
    }

    public static D64Disk Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsD64(data.LongLength))
            throw new InvalidDataException(
                $"Not a D64 image: {data.LongLength:N0} bytes (expected {Size35:N0} or {Size40:N0}).");

        int tracks = data.Length >= Size40 ? 40 : 35;

        int bam = Offset(18, 0);
        string diskName = Petscii(data, bam + 0x90, 16);
        string diskId = Petscii(data, bam + 0xA2, 2);

        var files = new List<D64Entry>();
        int track = 18, sector = 1;
        var seen = new HashSet<int>();
        while (track != 0)
        {
            if (track < 1 || track > tracks) break;
            int off = Offset(track, sector);
            if (off + BlockSize > data.Length) break;
            if (!seen.Add(track * 256 + sector)) break;   // cycle guard

            int nextT = data[off];
            int nextS = data[off + 1];

            for (int i = 0; i < 8; i++)
            {
                int e = off + i * 32;
                byte typeByte = data[e + 0x02];
                if (typeByte == 0) continue;   // empty/scratched slot

                int ft = data[e + 0x03];
                int fs = data[e + 0x04];
                string name = Petscii(data, e + 0x05, 16);
                int sizeBlocks = data[e + 0x1E] | (data[e + 0x1F] << 8);

                files.Add(new D64Entry
                {
                    Name = name,
                    Type = (D64FileType)(typeByte & 0x0F),
                    Closed = (typeByte & 0x80) != 0,
                    SizeBlocks = sizeBlocks,
                    FirstTrack = ft,
                    FirstSector = fs,
                });
            }

            track = nextT;
            sector = nextS;
        }

        return new D64Disk
        {
            DiskName = diskName,
            DiskId = diskId,
            Files = files,
            Tracks = tracks,
        };
    }

    /// <summary>Extract a file's bytes by following its track/sector chain.</summary>
    public static byte[] ExtractFile(byte[] data, D64Entry entry)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(entry);
        return FollowChain(data, entry.FirstTrack, entry.FirstSector);
    }

    private static byte[] FollowChain(byte[] data, int track, int sector)
    {
        var outBytes = new List<byte>();
        var seen = new HashSet<int>();
        while (track != 0)
        {
            if (track < 1 || track > 40)
                throw new InvalidDataException($"D64 file chain points to invalid track {track}.");
            int off = Offset(track, sector);
            if (off < 0 || off + BlockSize > data.Length)
                throw new InvalidDataException($"D64 file chain runs past the end of the image (track {track}, sector {sector}).");
            if (!seen.Add(track * 256 + sector))
                throw new InvalidDataException("D64 file chain loops back on itself.");

            int nextT = data[off];
            int nextS = data[off + 1];

            if (nextT == 0)
            {
                // Final block: the sector link points at the last used byte, so the
                // number of data bytes is (link - 1) — data starts at offset 2.
                int used = nextS - 1;
                if (used < 0) used = 0;
                if (used > 254) used = 254;
                for (int i = 0; i < used; i++) outBytes.Add(data[off + 2 + i]);
                break;
            }

            for (int i = 0; i < 254; i++) outBytes.Add(data[off + 2 + i]);
            track = nextT;
            sector = nextS;
        }
        return outBytes.ToArray();
    }

    /// <summary>Sectors on a track (1-based track number). 0 for out-of-range.</summary>
    public static int SectorsPerTrack(int track) => track switch
    {
        >= 1 and <= 17 => 21,
        >= 18 and <= 24 => 19,
        >= 25 and <= 30 => 18,
        >= 31 and <= 40 => 17,
        _ => 0,
    };

    /// <summary>Byte offset of a (1-based track, 0-based sector) within the image.</summary>
    public static int Offset(int track, int sector)
    {
        int blocks = 0;
        for (int t = 1; t < track; t++) blocks += SectorsPerTrack(t);
        return (blocks + sector) * BlockSize;
    }

    private static string Petscii(byte[] data, int at, int len)
    {
        if (at < 0 || at + len > data.Length) return "";
        // Strip trailing 0xA0 padding.
        int end = len;
        while (end > 0 && data[at + end - 1] == 0xA0) end--;

        var sb = new StringBuilder(end);
        for (int i = 0; i < end; i++)
        {
            byte b = data[at + i];
            char c = b switch
            {
                0xA0 => ' ',                       // padding that appears mid-string
                >= 0x20 and <= 0x5F => (char)b,    // space, digits, punctuation, A–Z
                >= 0xC1 and <= 0xDA => (char)(b - 0x80),  // shifted uppercase duplicate
                _ => '?',
            };
            sb.Append(c);
        }
        return sb.ToString();
    }
}
