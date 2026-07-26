// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;

namespace DiscForge.Core.Mmc;

/// <summary>
/// Builders for the SCSI/MMC command descriptor blocks (CDBs) DiscForge needs
/// to interrogate a drive. Pure byte construction — no device I/O — so the
/// command shapes are unit-testable without hardware. The transport that sends
/// these lives in DiscForge.Devices (SPTI, Windows-only).
///
/// References: SPC-3 (INQUIRY), MMC-5 (GET CONFIGURATION, MODE SENSE).
/// </summary>
public static class MmcCommands
{
    /// <summary>INQUIRY (0x12): vendor/model/firmware + peripheral type.</summary>
    public static byte[] Inquiry(byte allocationLength = 36) =>
        [0x12, 0x00, 0x00, 0x00, allocationLength, 0x00];

    /// <summary>
    /// GET CONFIGURATION (0x46): media profiles + feature descriptors.
    /// RT=0 returns all features from <paramref name="startingFeature"/>.
    /// </summary>
    public static byte[] GetConfiguration(ushort startingFeature = 0, ushort allocationLength = 512)
    {
        var cdb = new byte[10];
        cdb[0] = 0x46;
        cdb[1] = 0x00; // RT = 0: all features
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(2), startingFeature);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7), allocationLength);
        return cdb;
    }

    /// <summary>
    /// MODE SENSE(10) (0x5A) for a given page code. Page 0x2A is the MM
    /// Capabilities and Mechanical Status page (rich CD/DVD capability source).
    /// </summary>
    public static byte[] ModeSense10(byte pageCode = 0x2A, ushort allocationLength = 512)
    {
        var cdb = new byte[10];
        cdb[0] = 0x5A;
        cdb[2] = (byte)(pageCode & 0x3F); // PC=0 (current values)
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7), allocationLength);
        return cdb;
    }

    /// <summary>
    /// READ TOC/PMA/ATIP (0x43), format 0: the track list with LBA addresses.
    /// MSF=0 so addresses come back as plain LBAs.
    /// </summary>
    public static byte[] ReadToc(byte startingTrack = 0, ushort allocationLength = 4096)
    {
        var cdb = new byte[10];
        cdb[0] = 0x43;
        cdb[1] = 0x00;                 // MSF = 0 -> LBA addressing
        cdb[2] = 0x00;                 // Format 0: TOC
        cdb[6] = startingTrack;        // 0 = from the first track
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7), allocationLength);
        return cdb;
    }

    /// <summary>
    /// READ(10) (0x28): plain cooked reads of 2048-byte user data. Universally
    /// supported and unambiguous — the drive knows exactly what to return.
    ///
    /// Prefer this over READ CD for cooked data. READ CD with sector type "Any"
    /// and a user-data-only field selection is rejected by many drives
    /// ("Illegal request: invalid field in CDB") because it cannot infer which
    /// bytes to strip. READ CD is for raw sectors and audio; READ(10) is for
    /// cooked data.
    /// </summary>
    public static byte[] Read10(uint startLba, ushort sectorCount)
    {
        var cdb = new byte[10];
        cdb[0] = 0x28;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2), startLba);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7), sectorCount);
        return cdb;
    }

    /// <summary>
    /// READ DISC INFORMATION (0x51). Answers the question GET CONFIGURATION
    /// can't: is the disc in the drive actually BLANK? The media profile only
    /// says what type it is — a written DVD+R DL and a blank one look identical.
    /// </summary>
    public static byte[] ReadDiscInformation(ushort allocationLength = 34)
    {
        var cdb = new byte[10];
        cdb[0] = 0x51;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7), allocationLength);
        return cdb;
    }

 /// <summary>Format field for READ TOC/PMA/ATIP (CDB byte 2, bits 0-3).</summary>
    public enum TocFormat : byte
    {
        /// <summary>The track list.</summary>
        Toc = 0x00,
        /// <summary>Session information: first/last session and the last session's
        /// first track. The cheapest way to learn a disc is multi-session.</summary>
        SessionInfo = 0x01,
        /// <summary>Full TOC: every session's raw Q sub-channel entries.</summary>
        FullToc = 0x02,
        /// <summary>ATIP — recordable media only. Carries the dye/stamper
        /// manufacturer code and the disc's rated write window.</summary>
        Atip = 0x04,
    }

    /// <summary>
    /// READ TOC/PMA/ATIP (0x43) with an explicit format.
    ///
    /// ATIP (format 0100b) is the interesting one: pressed discs don't have it —
    /// the drive answers with a check condition, which is itself the answer
    /// ("this is not recordable media"). Recordable discs return a lead-in start
    /// time whose MSF triple identifies who made the dye.
    ///
    /// MSF addressing is used for ATIP because the fields ARE minute/second/frame
    /// values; formats 0 and 2 use LBA.
    /// </summary>
    public static byte[] ReadTocFormat(TocFormat format, ushort allocationLength = 1024,
                                       byte trackOrSession = 0)
    {
        var cdb = new byte[10];
        cdb[0] = 0x43;
        cdb[1] = format == TocFormat.Atip ? (byte)0x02 : (byte)0x00;   // MSF for ATIP
        cdb[2] = (byte)((byte)format & 0x0F);
        cdb[6] = trackOrSession;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7), allocationLength);
        return cdb;
    }

    /// <summary>Format field for READ DISC STRUCTURE (CDB byte 7).</summary>
    public enum DiscStructureFormat : byte
    {
        /// <summary>Physical format information: book type, layers, capacity, and
        /// (on -R/-RW) the manufacturer and media type ID strings.</summary>
        PhysicalFormat = 0x00,
        /// <summary>Copyright information — read only to report whether CSS/CPRM
        /// is present. DiscForge does not implement any circumvention.</summary>
        CopyrightInfo = 0x01,
        /// <summary>Burst cutting area.</summary>
        Bca = 0x03,
        /// <summary>Manufacturing information.</summary>
        Manufacturing = 0x04,
        /// <summary>ADIP — the +R/+RW equivalent of a media ID. Drive-dependent:
        /// plenty of drives simply refuse this one.</summary>
        Adip = 0x11,
    }

    /// <summary>
    /// READ DISC STRUCTURE (0xAD): the DVD/BD counterpart to ATIP. Format 0x00
    /// works almost everywhere and carries book type, layer count and capacity;
    /// the media ID strings live inside it for -R/-RW and in ADIP for +R/+RW.
    /// </summary>
    public static byte[] ReadDiscStructure(DiscStructureFormat format = DiscStructureFormat.PhysicalFormat,
                                           byte layer = 0, ushort allocationLength = 68,
                                           byte mediaType = 0x00)
    {
        var cdb = new byte[12];
        cdb[0] = 0xAD;
        cdb[1] = (byte)(mediaType & 0x0F);   // 0 = DVD / HD-DVD, 1 = BD
        cdb[6] = layer;
        cdb[7] = (byte)format;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(8), allocationLength);
        return cdb;
    }   

/// <summary>Sector content selection for READ CD (CDB byte 9).</summary>
    [Flags]
    public enum SectorFields
    {
        /// <summary>
        /// User data only. For a Mode 1 data sector that's 2048 bytes; for CD-DA
        /// it's the full 2352-byte audio frame.
        ///
        /// This is the ONLY legal selection for CD-DA: audio sectors have no sync,
        /// header, sub-header or EDC/ECC, so asking for them (see <see cref="Raw"/>)
        /// is an illegal field combination and drives reject it.
        /// </summary>
        UserData = 0x10,

        /// <summary>Sync + all headers + user data + EDC/ECC — a full 2352-byte
        /// sector. Valid for data sectors only, never for CD-DA.</summary>
        Raw = 0xF8,
    }

    /// <summary>Expected sector type filter for READ CD (CDB byte 1, bits 2-4).</summary>
    public enum ExpectedSectorType : byte
    {
        Any = 0, Cdda = 1, Mode1 = 2, Mode2 = 3, Mode2Form1 = 4, Mode2Form2 = 5,
    }

    /// <summary>
    /// READ CD (0xBE): the workhorse for ripping. Unlike READ(10) it can return
    /// raw 2352-byte sectors and audio, which is why it — not READ(10) — is what
    /// a disc imager uses.
    /// </summary>
    /// <summary>Sub-channel selection for READ CD byte 10.</summary>
    public enum SubChannel : byte
    {
        None = 0x00,
        /// <summary>Formatted Q: 16 extra bytes per sector (Q + CRC + pad + P flag).</summary>
        FormattedQ = 0x02,
        /// <summary>Raw interleaved P-W: 96 extra bytes per sector.</summary>
        RawPw = 0x01,
        /// <summary>Corrected, de-interleaved R-W: 96 extra bytes per sector.</summary>
        CorrectedRw = 0x04,
    }

/// <summary>
    /// READ CD (0xBE) requesting C2 error pointers alongside the sector.
    ///
    /// Byte 9 bit 1 asks for the 294-byte C2 block: one bit per main-channel
    /// byte, saying which the drive could not correct. The transfer therefore
    /// grows from 2352 to 2646 bytes per sector, C2 following the data.
    ///
    /// Not every drive supports this — MODE SENSE page 2Ah bit 4 of byte 5 says
    /// whether it does (see DriveCapabilityPage.C2Pointers), and a drive that
    /// doesn't rejects the command rather than quietly returning zeros. Ask
    /// first; don't assume from a successful read that the pointers are real.
    /// </summary>
    public static byte[] ReadCdWithC2(uint startLba, uint sectorCount,
        ExpectedSectorType expected = ExpectedSectorType.Any)
    {
        if (sectorCount > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "Transfer length is 24-bit.");

        var cdb = new byte[12];
        cdb[0] = 0xBE;
        cdb[1] = (byte)(((byte)expected & 0x07) << 2);
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2), startLba);
        cdb[6] = (byte)((sectorCount >> 16) & 0xFF);
        cdb[7] = (byte)((sectorCount >> 8) & 0xFF);
        cdb[8] = (byte)(sectorCount & 0xFF);
        // Raw sector (0xF8) plus C2 error pointers (0x02).
        cdb[9] = (byte)(SectorFields.Raw | (SectorFields)0x02);
        cdb[10] = (byte)SubChannel.None;
        return cdb;
    }

    /// <summary>Bytes returned per sector when C2 pointers are requested:
    /// 2352 main channel + 294 C2.</summary>
    public const int SectorBytesWithC2 = 2352 + 294;    
public static byte[] ReadCd(uint startLba, uint sectorCount,
        ExpectedSectorType expected = ExpectedSectorType.Any,
        SectorFields fields = SectorFields.UserData,
        SubChannel subChannel = SubChannel.None)
    {
        if (sectorCount > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "Transfer length is 24-bit.");

        var cdb = new byte[12];
        cdb[0] = 0xBE;
        cdb[1] = (byte)(((byte)expected & 0x07) << 2);
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(2), startLba);
        // Transfer length is 24-bit, big-endian, at bytes 6..8.
        cdb[6] = (byte)((sectorCount >> 16) & 0xFF);
        cdb[7] = (byte)((sectorCount >> 8) & 0xFF);
        cdb[8] = (byte)(sectorCount & 0xFF);
        cdb[9] = (byte)fields;
        cdb[10] = (byte)subChannel;
        return cdb;
    }
}
