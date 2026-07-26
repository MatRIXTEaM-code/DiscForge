// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

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

        // The filesystem is ISO 9660. A pure CD-i disc marks the descriptor with
        // "CD-I " rather than "CD001"; overlay it back so IsoReader accepts the
        // otherwise-identical structure. (For a Bridge disc it is already "CD001",
        // making the overlay a harmless no-op.)
        var patched = new PatchOverlayStream(
            isoStream, CookedOffset + StandardIdOffset, Encoding.ASCII.GetBytes(IsoStandardId));

        IsoDirectory fs = IsoReader.Read(patched, prefer);

        return new CdInteractiveDisc
        {
            Kind = kind,
            VolumeId = volumeId.Length > 0 ? volumeId : fs.VolumeId,
            SystemId = systemId,
            ApplicationId = applicationId,
            Filesystem = fs,
        };
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
