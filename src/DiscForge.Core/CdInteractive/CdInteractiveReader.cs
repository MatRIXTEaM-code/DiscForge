// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Iso;

namespace DiscForge.Core.CdInteractive;

// NOTE ON NAMING: the `Cdi` prefix and the DiscForge.Core.Cdi namespace already
// belong to the DiscJuggler ".cdi" image code — a completely different thing.
// Philips CD-i (Compact Disc Interactive, the Green Book format) lives here under
// DiscForge.Core.CdInteractive with a CdInteractive*/GreenBook flavour so the two
// never collide.

/// <summary>Which flavour of CD-i a disc is.</summary>
public enum CdInteractiveKind
{
    /// <summary>A pure Green Book CD-i disc — the volume descriptor's standard
    /// identifier is "CD-I " rather than the ISO 9660 "CD001".</summary>
    PureCdi,

    /// <summary>A CD-i Bridge disc (Video CD, Photo CD, …) — the standard
    /// identifier is the ordinary "CD001", but the system identifier carries
    /// "CD-RTOS" (usually "CD-RTOS CD-BRIDGE"), marking it as CD-i playable.</summary>
    Bridge,
}

/// <summary>What was found on a CD-i disc.</summary>
public sealed record CdInteractiveDisc
{
    public required CdInteractiveKind Kind { get; init; }
    /// <summary>The volume identifier (offset 40 of the volume descriptor).</summary>
    public required string VolumeId { get; init; }
    /// <summary>The system identifier (offset 8, 32 bytes) — "CD-RTOS …" on CD-i.</summary>
    public required string SystemId { get; init; }
    /// <summary>The application identifier (offset 574, 128 bytes) — often names the
    /// CD-i application/boot module. Empty when the field is blank.</summary>
    public string ApplicationId { get; init; } = "";
    /// <summary>The ISO 9660 filesystem tree read off the disc.</summary>
    public required IsoDirectory Filesystem { get; init; }
}

/// <summary>Thrown when an image does not carry a CD-i signature.</summary>
public sealed class CdInteractiveFormatException(string message) : Exception(message);

/// <summary>
/// Identifies Philips CD-i (Green Book) discs and reads their filesystem.
///
/// A CD-i disc is a CD-ROM/XA Mode 2 disc carrying an ISO 9660 volume structure
/// at sector 16. Two flavours exist, distinguished by the volume descriptor:
///  - <b>Pure CD-i</b>: the standard identifier at byte offset 1 is "CD-I "
///    (a Green Book marker) instead of the ISO "CD001".
///  - <b>CD-i Bridge</b> (Video CD, Photo CD, …): the standard identifier is the
///    ordinary "CD001", but the primary volume descriptor's system identifier
///    (32 bytes at offset 8) contains "CD-RTOS" (typically "CD-RTOS CD-BRIDGE").
///
/// So the reliable CD-i signal is: standard-id == "CD-I ", OR system-id contains
/// "CD-RTOS". The filesystem itself is plain ISO 9660, so it is read with the
/// existing <see cref="IsoReader"/>; for a pure disc the "CD-I " marker is
/// overlaid back to "CD001" on the fly so the ISO reader accepts it.
///
/// Descriptive metadata only — no copy protection is read or defeated.
/// </summary>
public static class CdInteractiveReader
{
    private const int SectorSize = 2048;
    private const int SectorSixteen = 16;

    // Byte offset of sector 16's user data for the two common geometries.
    private const long CookedOffset = (long)SectorSixteen * SectorSize;               // 0x8000
    private const int RawSectorSize = 2352;      // raw CD-ROM/XA Mode 2 sector
    private const int RawUserOffset = 24;        // 12 sync + 4 header + 8 subheader
    private const long RawOffset = (long)SectorSixteen * RawSectorSize + RawUserOffset; // 37656

    // Field offsets within the volume descriptor.
    private const int StandardIdOffset = 1;      // 5 bytes: "CD001" / "CD-I "
    private const int SystemIdOffset = 8;        // 32 bytes
    private const int VolumeIdOffset = 40;       // 32 bytes
    private const int ApplicationIdOffset = 574; // 128 bytes

    private const string CdiStandardId = "CD-I ";
    private const string CdRtosMarker = "CD-RTOS";
    private const string IsoStandardId = "CD001";

    /// <summary>
    /// True when the image's sector 16 carries a CD-i signature — a "CD-I "
    /// standard identifier, or a "CD-RTOS" system identifier. Both the cooked
    /// 2048-byte geometry (sector 16 at 0x8000) and the raw 2352-byte Mode 2
    /// geometry (user data at 16*2352 + 24) are tried.
    /// </summary>
    public static bool IsCdInteractive(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanSeek)
            throw new ArgumentException("Identifying a CD-i disc requires a seekable stream.", nameof(image));

        return TryReadDescriptor(image, CookedOffset, out var d) && IsSignature(d)
            || TryReadDescriptor(image, RawOffset, out d) && IsSignature(d);
    }

    /// <summary>
    /// Read a CD-i disc: its kind, volume/system/application identifiers, and its
    /// ISO 9660 filesystem tree. Throws <see cref="CdInteractiveFormatException"/>
    /// when no CD-i signature is present.
    /// </summary>
    public static CdInteractiveDisc Read(Stream image, IsoReader.NamePreference prefer = IsoReader.NamePreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanSeek)
            throw new ArgumentException("Reading a CD-i disc requires a seekable stream.", nameof(image));

        // Locate the volume descriptor and pick the stream geometry that feeds
        // IsoReader (base-0 for cooked; a cooked-user-data view for raw Mode 2).
        byte[]? descriptor = null;
        Stream? isoStream = null;

        if (TryReadDescriptor(image, CookedOffset, out var cooked) && IsSignature(cooked))
        {
            descriptor = cooked;
            isoStream = image;                                   // 2048/sector: ISO base 0
        }
        else if (TryReadDescriptor(image, RawOffset, out var raw) && IsSignature(raw))
        {
            descriptor = raw;
            // Present just the 2048 user bytes of each raw 2352-byte sector, so
            // the ISO structure reads as though it were a cooked track. Best-effort:
            // assumes a single Mode 2 data track based at sector 0.
            long sectors = image.Length / RawSectorSize;
            isoStream = new RawTrackUserDataStream(image, 0, RawSectorSize, RawUserOffset, SectorSize, sectors);
        }

        if (descriptor is null || isoStream is null)
            throw new CdInteractiveFormatException(
                "No CD-i signature at sector 16 — the image has neither a \"CD-I \" standard " +
                "identifier nor a \"CD-RTOS\" system identifier, so it is not a CD-i disc.");

        var kind = Ascii(descriptor, StandardIdOffset, 5) == CdiStandardId
            ? CdInteractiveKind.PureCdi
            : CdInteractiveKind.Bridge;

        string systemId = Trim(Ascii(descriptor, SystemIdOffset, 32));
        string volumeId = Trim(Ascii(descriptor, VolumeIdOffset, 32));
        string applicationId = Trim(Ascii(descriptor, ApplicationIdOffset, 128));

        // The filesystem differs by flavour:
        //  - A pure CD-i (Green Book) disc uses the ISO 9660 layout but with
        //    big-endian (Motorola) numeric fields AND an empty root-directory
        //    record in the volume descriptor — the tree is reached through the
        //    big-endian ("type M") path table instead. Read it directly.
        //  - A CD-i Bridge disc (Video CD, Photo CD, …) is ordinary little-endian
        //    ISO 9660, so the existing IsoReader handles it (after overlaying the
        //    "CD-I "/"CD001" standard id, a no-op for a Bridge disc's real "CD001").
        IsoDirectory fs;
        if (kind == CdInteractiveKind.PureCdi)
        {
            fs = ReadGreenBookFilesystem(isoStream, descriptor, volumeId);
        }
        else
        {
            var patched = new PatchOverlayStream(
                isoStream, CookedOffset + StandardIdOffset, Encoding.ASCII.GetBytes(IsoStandardId));
            fs = IsoReader.Read(patched, prefer);
        }

        return new CdInteractiveDisc
        {
            Kind = kind,
            VolumeId = volumeId.Length > 0 ? volumeId : fs.VolumeId,
            SystemId = systemId,
            ApplicationId = applicationId,
            Filesystem = fs,
        };
    }

    /// <summary>
    /// Extract one file's data from a CD-i image to <paramref name="output"/>.
    ///
    /// CD-i files live in CD-ROM/XA Mode 2 sectors, and a real-time file mixes Form
    /// 1 (2048 bytes/sector) with Form 2 (2324 bytes/sector) — the per-sector
    /// sub-header submode bit says which. On a raw 2352-byte image this reads that
    /// bit per sector and takes the right amount; on a cooked 2048 image every
    /// sector is Form-1-sized. The file is truncated to its directory-recorded
    /// length. Returns the number of bytes written.
    /// </summary>
    public static long ExtractFile(Stream image, string path, Stream output)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);

        var disc = Read(image);
        string want = Normalise(path);
        var entry = disc.Filesystem.Entries.FirstOrDefault(
            e => !e.IsDirectory && Normalise(e.Path) == want)
            ?? throw new CdInteractiveFormatException(
                $"No file '{path}' in the CD-i filesystem. Run cdi-console-info to list the files.");

        // Which geometry is this image? (cooked 2048 vs raw 2352 Mode 2)
        bool raw = !(TryReadDescriptor(image, CookedOffset, out var cooked) && IsSignature(cooked));

        // CD-i records a file's length in 2048-byte logical blocks, not in real
        // bytes — so read that many sectors from the extent and take each one's
        // Form-appropriate user data (Form 1 = 2048, Form 2 = 2324). A plain data
        // file (all Form 1) then has its tail trimmed to the exact byte length; a
        // real-time file (any Form 2 sector — e.g. an /MPEGAV MPEG stream) has no
        // 2048-based byte length, so its full user data is kept.
        long sectorCount = (entry.Size + SectorSize - 1) / SectorSize;
        long lba = entry.Extent;
        long written = 0;
        bool anyForm2 = false;
        var sector = new byte[raw ? RawSectorSize : SectorSize];

        for (long i = 0; i < sectorCount; i++)
        {
            long at = (lba + i) * sector.Length;
            if (at < 0 || at + sector.Length > image.Length) break;   // ran off the end of the image
            image.Seek(at, SeekOrigin.Begin);
            image.ReadExactly(sector, 0, sector.Length);

            if (raw)
            {
                // Sub-header submode (byte 18): bit 5 (0x20) set => Form 2 (2324 bytes).
                bool form2 = (sector[18] & 0x20) != 0;
                anyForm2 |= form2;
                int userLen = form2 ? 2324 : SectorSize;
                output.Write(sector, RawUserOffset, userLen);
                written += userLen;
            }
            else
            {
                output.Write(sector, 0, SectorSize);
                written += SectorSize;
            }
        }

        // A plain data file carries an exact byte length; trim the last sector's pad.
        if (!anyForm2 && output.CanSeek && written > entry.Size)
        {
            output.SetLength(entry.Size);
            written = entry.Size;
        }

        return written;
    }

    private static string Normalise(string p) =>
        "/" + p.Replace('\\', '/').Trim('/').ToUpperInvariant();

    // ---- Green Book (pure CD-i) filesystem ---------------------------------

    // Volume-descriptor offsets for the path-table pointers (ISO 9660 layout; on a
    // Green Book disc only the big-endian halves are populated).
    private const int PathTableSizeBeOffset = 136;   // BE half of the both-endian size at 132
    private const int PathTableLocBeOffset = 148;     // "type M" (big-endian) path table location

    private const int MaxPathTableBytes = 1 << 20;    // 1 MiB — a sane ceiling
    private const int MaxDirBytes = 1 << 24;          // 16 MiB per directory

    /// <summary>
    /// Read a pure CD-i (Green Book) filesystem. The volume descriptor's root
    /// directory record is empty; the hierarchy is enumerated from the big-endian
    /// path table (every directory, with its parent), and each directory's own
    /// records are then read (big-endian extents/sizes) to list the files. Reading
    /// stops gracefully at the end of a truncated image, so a partial dump still
    /// yields whatever filesystem it covers.
    /// </summary>
    private static IsoDirectory ReadGreenBookFilesystem(Stream cooked, byte[] vd, string volumeId)
    {
        uint ptLoc = BinaryPrimitives.ReadUInt32BigEndian(vd.AsSpan(PathTableLocBeOffset, 4));
        uint ptSize = BinaryPrimitives.ReadUInt32BigEndian(vd.AsSpan(PathTableSizeBeOffset, 4));

        var empty = new IsoDirectory { VolumeId = volumeId, Joliet = false, RockRidge = false, Entries = Array.Empty<IsoEntry>() };
        if (ptLoc == 0 || ptSize == 0 || ptSize > MaxPathTableBytes) return empty;

        var pt = ReadCooked(cooked, ptLoc, (int)ptSize);
        if (pt.Length == 0) return empty;

        // Parse the path table: 1-based entries, each pointing at a directory and
        // its parent entry. Entry 1 is the root (empty name).
        var dirs = new List<(int Index, uint Extent, int Parent, string Name)>();
        for (int p = 0, index = 0; p + 8 <= pt.Length;)
        {
            int li = pt[p];
            if (li == 0) break;
            uint ext = BinaryPrimitives.ReadUInt32BigEndian(pt.AsSpan(p + 2, 4));
            int parent = BinaryPrimitives.ReadUInt16BigEndian(pt.AsSpan(p + 6, 2));
            index++;
            string name = index == 1 ? "" : Trim(Ascii(pt, p + 8, li));
            dirs.Add((index, ext, parent, name));
            p += 8 + li + (li & 1);   // identifiers are padded to an even length
        }
        if (dirs.Count == 0) return empty;

        // Full path per directory index (parents always precede children).
        var pathOf = new Dictionary<int, string>();
        foreach (var d in dirs)
            pathOf[d.Index] = d.Index == 1 ? "" :
                (pathOf.TryGetValue(d.Parent, out var pp) ? pp : "") + "/" + d.Name;
        var dirExtents = new HashSet<uint>(dirs.Select(d => d.Extent));

        var entries = new List<IsoEntry>();

        // Non-root directories become entries in their own right.
        foreach (var d in dirs.Where(d => d.Index != 1))
        {
            uint size = DirectorySize(cooked, d.Extent);
            entries.Add(new IsoEntry
            {
                Name = d.Name, Path = pathOf[d.Index], IsDirectory = true,
                Extent = d.Extent, Size = size,
            });
        }

        // Files: walk each directory's records; an entry whose extent is itself a
        // directory (in the path table) is a subdirectory, already added above.
        foreach (var d in dirs)
        {
            uint dirSize = DirectorySize(cooked, d.Extent);
            if (dirSize == 0) continue;
            var data = ReadCooked(cooked, d.Extent, (int)Math.Min(dirSize, MaxDirBytes));
            string basePath = pathOf[d.Index];

            for (int q = 0; q < data.Length;)
            {
                int rl = data[q];
                if (rl == 0)
                {
                    int next = (q / SectorSize + 1) * SectorSize;
                    if (next <= q) break;
                    q = next;
                    continue;
                }
                if (rl < 34 || q + rl > data.Length) break;

                uint ext = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(q + 6, 4));
                uint size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(q + 14, 4));
                int idLen = data[q + 32];
                if (idLen == 0 || 33 + idLen > rl) { q += rl; continue; }

                // '.' (0x00) and '..' (0x01) — skip.
                if (idLen == 1 && (data[q + 33] == 0x00 || data[q + 33] == 0x01)) { q += rl; continue; }

                if (!dirExtents.Contains(ext))
                {
                    string name = CleanFileName(Ascii(data, q + 33, idLen));
                    entries.Add(new IsoEntry
                    {
                        Name = name, Path = basePath + "/" + name, IsDirectory = false,
                        Extent = ext, Size = size,
                    });
                }
                q += rl;
            }
        }

        return new IsoDirectory { VolumeId = volumeId, Joliet = false, RockRidge = false, Entries = entries };
    }

    /// <summary>The declared size of a directory, from its own "." record (the
    /// first record, big-endian data length). 0 if it can't be read.</summary>
    private static uint DirectorySize(Stream cooked, uint extent)
    {
        var first = ReadCooked(cooked, extent, SectorSize);
        if (first.Length < 18) return 0;
        uint size = BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(14, 4));
        return size > MaxDirBytes ? 0u : size;
    }

    /// <summary>Read <paramref name="length"/> bytes starting at cooked-sector
    /// <paramref name="lba"/>, tolerating the end of a truncated image (returns
    /// however many bytes were actually available, possibly none).</summary>
    private static byte[] ReadCooked(Stream cooked, uint lba, int length)
    {
        long at = (long)lba * SectorSize;
        if (at < 0 || at >= cooked.Length) return Array.Empty<byte>();
        int want = (int)Math.Min(length, cooked.Length - at);
        var buf = new byte[want];
        cooked.Seek(at, SeekOrigin.Begin);
        int got = 0;
        while (got < want)
        {
            int n = cooked.Read(buf, got, want - got);
            if (n <= 0) break;
            got += n;
        }
        return got == want ? buf : buf[..got];
    }

    /// <summary>Strip a trailing ISO ";version" from a CD-i file identifier.</summary>
    private static string CleanFileName(string id)
    {
        int semi = id.IndexOf(';');
        if (semi >= 0) id = id[..semi];
        return id.TrimEnd(' ');
    }

    // ---- helpers ------------------------------------------------------------

    private static bool IsSignature(byte[] descriptor) =>
        Ascii(descriptor, StandardIdOffset, 5) == CdiStandardId
        || Ascii(descriptor, SystemIdOffset, 32).Contains(CdRtosMarker, StringComparison.Ordinal);

    private static bool TryReadDescriptor(Stream image, long offset, out byte[] descriptor)
    {
        descriptor = Array.Empty<byte>();
        if (offset < 0 || offset + SectorSize > image.Length) return false;
        var buf = new byte[SectorSize];
        image.Seek(offset, SeekOrigin.Begin);
        image.ReadExactly(buf, 0, SectorSize);
        descriptor = buf;
        return true;
    }

    private static string Ascii(byte[] data, int at, int len)
    {
        if (at < 0 || at + len > data.Length) return "";
        return Encoding.ASCII.GetString(data, at, len);
    }

    private static string Trim(string s) => s.TrimEnd(' ', '\0');

    /// <summary>
    /// A read-only, seekable pass-through that overlays a short run of bytes at a
    /// fixed absolute offset — used to present a pure CD-i "CD-I " standard
    /// identifier as the ISO "CD001" without copying the whole image. Does not own
    /// the underlying stream.
    /// </summary>
    private sealed class PatchOverlayStream(Stream inner, long patchOffset, byte[] patch) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long start = inner.Position;
            int n = inner.Read(buffer, offset, count);
            // Overlay any patched bytes that fall within [start, start + n).
            long pEnd = patchOffset + patch.Length;
            long rEnd = start + n;
            long from = Math.Max(start, patchOffset);
            long to = Math.Min(rEnd, pEnd);
            for (long p = from; p < to; p++)
                buffer[offset + (int)(p - start)] = patch[(int)(p - patchOffset)];
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
