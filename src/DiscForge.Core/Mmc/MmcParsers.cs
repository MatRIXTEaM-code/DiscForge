// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Text;

namespace DiscForge.Core.Mmc;

/// <summary>Parsed standard INQUIRY data.</summary>
public sealed record InquiryData
{
    public required byte PeripheralDeviceType { get; init; } // 5 = CD/DVD
    public required string VendorId { get; init; }
    public required string ProductId { get; init; }
    public required string FirmwareRevision { get; init; }

    public bool IsOpticalDrive => PeripheralDeviceType == 0x05;

    public static InquiryData Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 36)
            throw new ArgumentException($"INQUIRY response too short ({d.Length} bytes).");
        static string Ascii(ReadOnlySpan<byte> s) =>
            Encoding.ASCII.GetString(s).Trim();
        return new InquiryData
        {
            PeripheralDeviceType = (byte)(d[0] & 0x1F),
            VendorId = Ascii(d.Slice(8, 8)),
            ProductId = Ascii(d.Slice(16, 16)),
            FirmwareRevision = Ascii(d.Slice(32, 4)),
        };
    }
}

/// <summary>MMC media profile codes (subset DiscForge cares about).</summary>
public enum MmcProfile : ushort
{
    /// <summary>No current profile — no media, or the drive didn't say.</summary>
    None = 0x0000,
    CdRom = 0x0008, CdR = 0x0009, CdRw = 0x000A,
    DvdRom = 0x0010, DvdMinusRSeq = 0x0011, DvdRam = 0x0012,
    DvdMinusRwRestricted = 0x0013, DvdMinusRwSeq = 0x0014, DvdMinusRDl = 0x0015,
    DvdPlusRw = 0x001A, DvdPlusR = 0x001B, DvdPlusRwDl = 0x002A, DvdPlusRDl = 0x002B,
    BdRom = 0x0040, BdRSrm = 0x0041, BdRRrm = 0x0042, BdRe = 0x0043,
}

/// <summary>Parsed GET CONFIGURATION result: current profile, supported
/// profiles, and the write-relevant features we key on.</summary>
public sealed record ConfigurationInfo
{
    public required MmcProfile CurrentProfile { get; init; }
    public required IReadOnlySet<ushort> SupportedProfiles { get; init; }

    /// <summary>CD Mastering feature (0x002E) present — SAO/DAO CD writing.</summary>
    public bool CdMastering { get; init; }
    /// <summary>CD Mastering RAW write bit — the RAW-DAO capability. Spec-derived;
    /// flagged for hardware confirmation (see docs).</summary>
    public bool CdMasteringRaw { get; init; }
    /// <summary>CD Track at Once feature (0x002D) present.</summary>
    public bool CdTrackAtOnce { get; init; }

    public bool HasProfile(MmcProfile p) => SupportedProfiles.Contains((ushort)p);

    public static ConfigurationInfo Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8)
            throw new ArgumentException($"GET CONFIGURATION response too short ({d.Length}).");

        ushort current = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(6, 2));
        var profiles = new HashSet<ushort>();
        bool mastering = false, masteringRaw = false, tao = false;

        int p = 8; // first feature descriptor
        while (p + 4 <= d.Length)
        {
            ushort code = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p, 2));
            int addLen = d[p + 3];
            int dataStart = p + 4;
            int dataEnd = Math.Min(dataStart + addLen, d.Length);

            switch (code)
            {
                case 0x0000: // Profile List: pairs of (profile u16, byte)
                    for (int q = dataStart; q + 4 <= dataEnd; q += 4)
                        profiles.Add(BinaryPrimitives.ReadUInt16BigEndian(d.Slice(q, 2)));
                    break;
                case 0x002D: // CD Track at Once
                    tao = true;
                    break;
                case 0x002E: // CD Mastering (SAO/RAW)
                    mastering = true;
                    // MMC-5 CD Mastering feature data byte 0: bit3 = RAW.
                    // (Confidence: spec-documented; VERIFY on real hardware.)
                    if (dataStart < dataEnd)
                        masteringRaw = (d[dataStart] & 0x08) != 0;
                    break;
            }

            if (addLen <= 0) break;      // guard against malformed zero-advance
            p = dataStart + addLen;
        }

        // Current profile isn't always in the profile list; include it.
        profiles.Add(current);

        return new ConfigurationInfo
        {
            CurrentProfile = (MmcProfile)current,
            SupportedProfiles = profiles,
            CdMastering = mastering,
            CdMasteringRaw = masteringRaw,
            CdTrackAtOnce = tao,
        };
    }
}

/// <summary>
/// Parsed MODE SENSE page 0x2A (MM Capabilities). Bit positions per MMC-3/MMC-4.
/// This page is legacy but remains the most reliable single source for CD-era
/// read/write fidelity flags (subchannel, C2, RAW hints, buffer-underrun).
/// </summary>
public sealed record MmCapabilities
{
    public bool CdRRead { get; init; }
    public bool CdRwRead { get; init; }
    public bool DvdRomRead { get; init; }
    public bool DvdRRead { get; init; }

    public bool CdRWrite { get; init; }
    public bool CdRwWrite { get; init; }
    public bool DvdRWrite { get; init; }
    public bool TestWrite { get; init; }

    public bool Mode2Form1 { get; init; }
    public bool Mode2Form2 { get; init; }
    public bool MultiSession { get; init; }

    public bool ReadSubchannel { get; init; }   // R-W supported
    public bool C2Pointers { get; init; }
    public bool Isrc { get; init; }

    public bool BufferUnderrunProtection { get; init; } // BUF

    /// <summary>
    /// Locate page 0x2A inside a MODE SENSE(10) response and parse it.
    /// Response header is 8 bytes; block descriptors (if any) follow; then mode
    /// pages. We scan pages by (page code, page length) to find 0x2A robustly.
    /// </summary>
    public static MmCapabilities ParseFromModeSense10(ReadOnlySpan<byte> resp)
    {
        if (resp.Length < 8)
            throw new ArgumentException("MODE SENSE response too short.");
        int blockDescLen = BinaryPrimitives.ReadUInt16BigEndian(resp.Slice(6, 2));
        int pos = 8 + blockDescLen;

        while (pos + 2 <= resp.Length)
        {
            byte pageCode = (byte)(resp[pos] & 0x3F);
            int pageLen = resp[pos + 1];
            if (pageCode == 0x2A)
                return ParsePage(resp.Slice(pos, Math.Min(pageLen + 2, resp.Length - pos)));
            if (pageLen <= 0) break;
            pos += 2 + pageLen;
        }
        throw new ArgumentException("Mode page 0x2A not present in response.");
    }

    /// <summary>Parse the page body (starting at the page-code byte).</summary>
    public static MmCapabilities ParsePage(ReadOnlySpan<byte> pg)
    {
        if (pg.Length < 7)
            throw new ArgumentException("Mode page 0x2A too short.");
        byte read = pg[2], write = pg[3], b4 = pg[4], b5 = pg[5], b6 = pg[6];

        return new MmCapabilities
        {
            CdRRead = (read & 0x01) != 0,
            CdRwRead = (read & 0x02) != 0,
            DvdRomRead = (read & 0x08) != 0,
            DvdRRead = (read & 0x10) != 0,

            CdRWrite = (write & 0x01) != 0,
            CdRwWrite = (write & 0x02) != 0,
            DvdRWrite = (write & 0x10) != 0,
            TestWrite = (write & 0x04) != 0,

            Mode2Form1 = (b4 & 0x10) != 0,
            Mode2Form2 = (b4 & 0x20) != 0,
            MultiSession = (b4 & 0x40) != 0,

            ReadSubchannel = (b5 & 0x04) != 0,
            C2Pointers = (b5 & 0x10) != 0,
            Isrc = (b5 & 0x20) != 0,

            BufferUnderrunProtection = (b6 & 0x80) != 0,
        };
    }
}

/// <summary>State of the disc currently in the drive.</summary>
public enum DiscStatus
{
    /// <summary>Nothing recorded — ready to be written.</summary>
    Empty = 0,
    /// <summary>Partly recorded and still open for more.</summary>
    Incomplete = 1,
    /// <summary>Closed. Nothing more can be written to it.</summary>
    Finalized = 2,
    Other = 3,
}

/// <summary>
/// What READ DISC INFORMATION (0x51) says about the loaded disc.
///
/// This answers the question GET CONFIGURATION cannot: is the disc actually
/// BLANK? The media profile only reports the disc *type* — a written DVD+R DL
/// and a blank one are both simply "DvdPlusRDl". Without this, a burn to a
/// full disc fails deep inside IMAPI2 with an opaque "operation is only valid
/// with supported media", which sends people hunting a software fault.
/// </summary>
public sealed record DiscInformation
{
    public required DiscStatus Status { get; init; }
    /// <summary>The disc can be erased and rewritten (CD-RW, DVD-RW/+RW, BD-RE).</summary>
    public required bool Erasable { get; init; }
    public required int Sessions { get; init; }
    public required int FirstTrack { get; init; }

    /// <summary>Blank and ready to write.</summary>
    public bool IsBlank => Status == DiscStatus.Empty;
    /// <summary>Has data, but is rewritable — erase it and it's usable.</summary>
    public bool NeedsErasing => Status != DiscStatus.Empty && Erasable;
    /// <summary>Has data and is write-once: this disc can never be written again.</summary>
    public bool IsSpent => Status != DiscStatus.Empty && !Erasable;

    /// <summary>Plain-English summary, for logs and refusals.</summary>
    public string Describe() => Status switch
    {
        DiscStatus.Empty => "blank",
        DiscStatus.Incomplete => Erasable ? "partly written (appendable, erasable)"
                                          : "partly written (appendable)",
        DiscStatus.Finalized => Erasable ? "finalised — erase it to reuse"
                                         : "finalised and write-once — it cannot be reused",
        _ => "in an unrecognised state",
    };

    /// <summary>
    /// Parse a READ DISC INFORMATION response. Byte 2 packs the lot: bits 0-1
    /// disc status, bits 2-3 last-session state, bit 4 erasable.
    /// </summary>
    public static DiscInformation Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8)
            throw new ArgumentException(
                $"READ DISC INFORMATION response too short ({d.Length} bytes; 8 expected).");

        byte b2 = d[2];
        return new DiscInformation
        {
            Status = (DiscStatus)(b2 & 0x03),
            Erasable = (b2 & 0x10) != 0,
            FirstTrack = d[3],
            Sessions = d[4],
        };
    }
}
