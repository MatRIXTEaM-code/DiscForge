// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Wbfs;

/// <summary>Raised when a stream is not a well-formed WBFS container.</summary>
public sealed class WbfsFormatException(string message) : Exception(message);

/// <summary>One Wii/GameCube disc stored inside a WBFS container.</summary>
public sealed record WbfsDisc
{
    /// <summary>Disc-table slot this disc occupies (0-based).</summary>
    public required int Slot { get; init; }
    /// <summary>The 6-character game id from the disc header (e.g. "RMCE01").</summary>
    public required string GameId { get; init; }
    /// <summary>The disc title from the header (offset 0x20).</summary>
    public required string Title { get; init; }
}

/// <summary>
/// A parsed WBFS ("Wii Backup File System") container: the sparse wrapper a
/// Wii/GameCube ISO is stored in. This carries the container geometry and the
/// discs present; the encrypted contents of each disc are never touched — the
/// ISO is extracted byte-for-byte, exactly as it was written.
/// </summary>
public sealed record WbfsFile
{
    /// <summary>The partition's hd-sector size in bytes (1 &lt;&lt; hd_sec_sz_s).</summary>
    public required int HdSectorSize { get; init; }
    /// <summary>The WBFS sector size in bytes (1 &lt;&lt; wbfs_sec_sz_s).</summary>
    public required long WbfsSectorSize { get; init; }
    /// <summary>Discs found in the disc table.</summary>
    public required IReadOnlyList<WbfsDisc> Discs { get; init; }

    public string Summary =>
        $"WBFS: {Discs.Count} disc(s), hd sector {HdSectorSize} B, " +
        $"wbfs sector {WbfsSectorSize:N0} B.";
}

/// <summary>
/// Reads WBFS containers and rebuilds the ISO they wrap.
///
/// Clean-room: the layout is taken from the public WBFS format description, not
/// from any third-party source. WBFS is a sparse container — a header, a disc
/// table, and per-disc a copy of the first 0x100 bytes of the Wii disc header
/// followed by a "wlba" table mapping the ISO's WBFS-sectors to sectors stored
/// in the file. Reconstruction walks that table. Nothing here decrypts the disc;
/// the ISO comes out exactly as it went in (its partitions stay encrypted).
///
/// All multi-byte fields are BIG-ENDIAN.
/// </summary>
public static class WbfsReader
{
    private static ReadOnlySpan<byte> Magic => "WBFS"u8;

    /// <summary>The wbfs_head fixed part: magic(4) + n_hd_sec(4) + 4 bytes.</summary>
    private const int HeadSize = 12;

    /// <summary>
    /// Nominal Wii disc size (143432 × 0x8000 bytes). The number of wlba entries
    /// per disc is this divided by the WBFS sector size — it is derived, never
    /// stored, so a reader must know it to walk the table.
    /// </summary>
    private const long WiiDiscSize = 0x118240000L;

    /// <summary>True if the stream begins with the "WBFS" magic. Non-destructive.</summary>
    public static bool IsWbfs(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("A seekable stream is required.", nameof(stream));
        if (stream.Length < 4) return false;

        long pos = stream.Position;
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            Span<byte> m = stackalloc byte[4];
            stream.ReadExactly(m);
            return m.SequenceEqual(Magic);
        }
        finally { stream.Seek(pos, SeekOrigin.Begin); }
    }

    /// <summary>Parse a WBFS container's header and disc table.</summary>
    public static WbfsFile Read(Stream stream)
    {
        var g = ReadGeometry(stream);

        // The disc table sits right after the fixed head, filling the rest of the
        // first hd-sector: one byte per slot, nonzero = slot in use.
        int maxDisc = g.HdSectorSize - HeadSize;
        var table = new byte[maxDisc];
        stream.Seek(HeadSize, SeekOrigin.Begin);
        int tableRead = stream.Read(table, 0, maxDisc);

        var discs = new List<WbfsDisc>();
        var headerCopy = new byte[0x100];
        for (int slot = 0; slot < tableRead; slot++)
        {
            if (table[slot] == 0) continue;

            long infoOffset = (long)g.HdSectorSize + (long)slot * g.DiscInfoSize;
            if (infoOffset + 0x100 > stream.Length)
                throw new WbfsFormatException(
                    $"Disc-table slot {slot} points at a disc-info block beyond the end of the file.");

            stream.Seek(infoOffset, SeekOrigin.Begin);
            stream.ReadExactly(headerCopy);

            discs.Add(new WbfsDisc
            {
                Slot = slot,
                GameId = AsciiTrim(headerCopy, 0x00, 6),
                Title = AsciiTrim(headerCopy, 0x20, 0x40),
            });
        }

        return new WbfsFile
        {
            HdSectorSize = g.HdSectorSize,
            WbfsSectorSize = g.WbfsSectorSize,
            Discs = discs,
        };
    }

    /// <summary>
    /// Rebuild a disc's ISO from the container into <paramref name="isoOut"/>.
    /// The wlba table maps each ISO WBFS-sector to a sector in the file: a nonzero
    /// entry is copied verbatim; a zero entry is a sparse hole, emitted as zeros.
    /// Trailing sparse sectors are trimmed — the ISO ends at the last stored
    /// sector (WBFS discs are sparse, so the tail is all zero anyway).
    /// </summary>
    /// <returns>The number of bytes written to <paramref name="isoOut"/>.</returns>
    public static long ExtractDisc(Stream wbfs, WbfsDisc disc, Stream isoOut)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(isoOut);
        var g = ReadGeometry(wbfs);

        long infoOffset = (long)g.HdSectorSize + (long)disc.Slot * g.DiscInfoSize;
        long wlbaOffset = infoOffset + 0x100;
        long wlbaBytes = (long)g.WbfsSecPerDisc * 2;
        if (wlbaOffset + wlbaBytes > wbfs.Length)
            throw new WbfsFormatException(
                $"Disc in slot {disc.Slot} has a wlba table that runs past the end of the file.");

        var wlbaRaw = new byte[wlbaBytes];
        wbfs.Seek(wlbaOffset, SeekOrigin.Begin);
        wbfs.ReadExactly(wlbaRaw);

        // Decode the table and find the last stored sector (to trim the tail).
        var wlba = new ushort[g.WbfsSecPerDisc];
        int lastUsed = -1;
        for (int i = 0; i < wlba.Length; i++)
        {
            wlba[i] = BinaryPrimitives.ReadUInt16BigEndian(wlbaRaw.AsSpan(i * 2, 2));
            if (wlba[i] != 0) lastUsed = i;
        }

        long written = 0;
        var buffer = new byte[g.WbfsSectorSize];
        for (int i = 0; i <= lastUsed; i++)
        {
            if (wlba[i] == 0)
            {
                Array.Clear(buffer);
                isoOut.Write(buffer, 0, buffer.Length);
            }
            else
            {
                long src = (long)wlba[i] * g.WbfsSectorSize;
                if (src + g.WbfsSectorSize > wbfs.Length)
                    throw new WbfsFormatException(
                        $"wlba entry {i} references sector {wlba[i]}, which lies beyond the end of the file.");
                wbfs.Seek(src, SeekOrigin.Begin);
                wbfs.ReadExactly(buffer, 0, buffer.Length);
                isoOut.Write(buffer, 0, buffer.Length);
            }
            written += g.WbfsSectorSize;
        }

        return written;
    }

    // ---- geometry -----------------------------------------------------------

    private readonly record struct Geometry(
        int HdSectorSize, long WbfsSectorSize, uint WbfsSecPerDisc, long DiscInfoSize);

    private static Geometry ReadGeometry(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("A seekable stream is required.", nameof(stream));
        if (stream.Length < HeadSize)
            throw new WbfsFormatException(
                $"File is {stream.Length} bytes — too short to be a WBFS header ({HeadSize}).");

        Span<byte> head = stackalloc byte[HeadSize];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(head);

        if (!head[..4].SequenceEqual(Magic))
            throw new WbfsFormatException("Not a WBFS container: missing the 'WBFS' magic.");

        // n_hd_sec @4 is read for completeness/validation; the geometry needed to
        // walk the container is the two shift exponents.
        int hdSecSzS = head[8];
        int wbfsSecSzS = head[9];
        if (hdSecSzS is < 9 or > 31)
            throw new WbfsFormatException($"WBFS hd_sec_sz_s = {hdSecSzS} is out of range.");
        if (wbfsSecSzS < hdSecSzS || wbfsSecSzS > 40)
            throw new WbfsFormatException(
                $"WBFS wbfs_sec_sz_s = {wbfsSecSzS} is invalid (must be >= hd_sec_sz_s {hdSecSzS}).");

        int hdSectorSize = 1 << hdSecSzS;
        long wbfsSectorSize = 1L << wbfsSecSzS;
        uint wbfsSecPerDisc = (uint)(WiiDiscSize >> wbfsSecSzS);
        if (wbfsSecPerDisc == 0)
            throw new WbfsFormatException(
                $"WBFS wbfs_sec_sz_s = {wbfsSecSzS} yields no sectors per disc.");

        // disc_info = 0x100-byte header copy + the wlba table, aligned up to a
        // whole hd-sector.
        long rawInfo = 0x100 + (long)wbfsSecPerDisc * 2;
        long discInfoSize = (rawInfo + hdSectorSize - 1) & ~((long)hdSectorSize - 1);

        return new Geometry(hdSectorSize, wbfsSectorSize, wbfsSecPerDisc, discInfoSize);
    }

    private static string AsciiTrim(byte[] data, int at, int len)
    {
        int end = at + len;
        if (end > data.Length) end = data.Length;
        int stop = at;
        while (stop < end && data[stop] != 0) stop++;
        return Encoding.ASCII.GetString(data, at, stop - at).TrimEnd();
    }
}
