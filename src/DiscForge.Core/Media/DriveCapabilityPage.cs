// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Media;

/// <summary>
/// The MM Capabilities and Mechanical Status page (MODE SENSE page 0x2A),
/// decoded. This is where a drive says what it can actually do at the sector
/// level — as opposed to GET CONFIGURATION, which says what media it accepts.
///
/// The one that matters most for imaging is <see cref="C2Pointers"/>: without it
/// a drive can tell you a read failed but not WHICH bytes were wrong, which rules
/// out any form of error correction above the drive's own.
/// </summary>
public sealed record DriveCapabilityPage
{
    // Byte 2 — read capabilities
    public bool ReadsCdR { get; init; }
    public bool ReadsCdRw { get; init; }
    public bool ReadsMethod2 { get; init; }
    public bool ReadsDvdRom { get; init; }
    public bool ReadsDvdR { get; init; }
    public bool ReadsDvdRam { get; init; }

    // Byte 3 — write capabilities
    public bool WritesCdR { get; init; }
    public bool WritesCdRw { get; init; }
    public bool TestWrite { get; init; }
    public bool WritesDvdR { get; init; }
    public bool WritesDvdRam { get; init; }

    // Byte 4 — sector and session handling
    public bool AudioPlay { get; init; }
    public bool Mode2Form1 { get; init; }
    public bool Mode2Form2 { get; init; }
    public bool MultiSession { get; init; }
    /// <summary>Buffer-underrun-free writing (BURN-Proof / JustLink).</summary>
    public bool BufferUnderrunFree { get; init; }

    // Byte 5 — the imaging-relevant flags
    public bool CddaCommands { get; init; }
    /// <summary>"Accurate stream": the drive returns audio from where it was
    /// asked, every time. Without it, ripping needs jitter correction.</summary>
    public bool CddaAccurateStream { get; init; }
    /// <summary>R-W sub-channel can be read.</summary>
    public bool SubchannelRw { get; init; }
    /// <summary>R-W sub-channel comes back de-interleaved and corrected.</summary>
    public bool SubchannelRwCorrected { get; init; }
    /// <summary>
    /// C2 error pointers: per-byte flags saying which bytes of a sector the
    /// drive could not correct. The prerequisite for any error recovery beyond
    /// what the drive does internally — without it a bad read is opaque.
    /// </summary>
    public bool C2Pointers { get; init; }
    public bool ReadsIsrc { get; init; }
    public bool ReadsUpc { get; init; }
    public bool ReadsBarcode { get; init; }

    // Byte 6 — mechanics
    public bool CanLock { get; init; }
    public bool CanEject { get; init; }
    public LoadingMechanism Loading { get; init; }

    /// <summary>Drive cache, in kilobytes.</summary>
    public int BufferSizeKb { get; init; }
    /// <summary>Kilobytes per second, as reported. 176 KB/s is 1× for CD.</summary>
    public int MaxReadSpeedKbs { get; init; }
    public int CurrentReadSpeedKbs { get; init; }
    public int MaxWriteSpeedKbs { get; init; }
    public int CurrentWriteSpeedKbs { get; init; }

    public static double ToCdX(int kbs) => kbs / 176.4;
    public static double ToDvdX(int kbs) => kbs / 1385.0;
}

public enum LoadingMechanism
{
    Caddy = 0,
    Tray = 1,
    PopUp = 2,
    Reserved3 = 3,
    ChangerIndividualDiscs = 4,
    ChangerCartridge = 5,
    Unknown = 7,
}

/// <summary>
/// Parses MODE SENSE(10) responses for page 0x2A. Pure — hand it the bytes a
/// drive returned and it says what they mean, so the offsets are testable
/// against a captured response without hardware.
/// </summary>
public static class DriveCapabilityPageParser
{
    /// <summary>
    /// Decode a MODE SENSE(10) response. The 8-byte header is followed by any
    /// block descriptors (normally none on MMC devices) and then the page, so
    /// the page offset has to be computed rather than assumed.
    /// </summary>
    public static DriveCapabilityPage? Parse(ReadOnlySpan<byte> response)
    {
        if (response.Length < 16) return null;

        int blockDescriptorLength = (response[6] << 8) | response[7];
        int p = 8 + blockDescriptorLength;
        if (p + 16 > response.Length) return null;
        if ((response[p] & 0x3F) != 0x2A) return null;

        byte read2 = response[p + 2];
        byte write3 = response[p + 3];
        byte b4 = response[p + 4];
        byte b5 = response[p + 5];
        byte b6 = response[p + 6];

        int pageLength = response[p + 1];
        bool hasSpeedFields = p + 15 < response.Length;
        bool hasWriteSpeeds = pageLength >= 0x1C && p + 21 < response.Length;

        return new DriveCapabilityPage
        {
            ReadsCdR = (read2 & 0x01) != 0,
            ReadsCdRw = (read2 & 0x02) != 0,
            ReadsMethod2 = (read2 & 0x04) != 0,
            ReadsDvdRom = (read2 & 0x08) != 0,
            ReadsDvdR = (read2 & 0x10) != 0,
            ReadsDvdRam = (read2 & 0x20) != 0,

            WritesCdR = (write3 & 0x01) != 0,
            WritesCdRw = (write3 & 0x02) != 0,
            TestWrite = (write3 & 0x04) != 0,
            WritesDvdR = (write3 & 0x10) != 0,
            WritesDvdRam = (write3 & 0x20) != 0,

            AudioPlay = (b4 & 0x01) != 0,
            Mode2Form1 = (b4 & 0x10) != 0,
            Mode2Form2 = (b4 & 0x20) != 0,
            MultiSession = (b4 & 0x40) != 0,
            BufferUnderrunFree = (b4 & 0x80) != 0,

            CddaCommands = (b5 & 0x01) != 0,
            CddaAccurateStream = (b5 & 0x02) != 0,
            SubchannelRw = (b5 & 0x04) != 0,
            SubchannelRwCorrected = (b5 & 0x08) != 0,
            C2Pointers = (b5 & 0x10) != 0,          // bit 4, not bit 6
            ReadsIsrc = (b5 & 0x20) != 0,
            ReadsUpc = (b5 & 0x40) != 0,
            ReadsBarcode = (b5 & 0x80) != 0,

            CanLock = (b6 & 0x01) != 0,
            CanEject = (b6 & 0x08) != 0,
            Loading = (LoadingMechanism)((b6 >> 5) & 0x07),

            BufferSizeKb = (response[p + 12] << 8) | response[p + 13],
            MaxReadSpeedKbs = hasSpeedFields ? (response[p + 8] << 8) | response[p + 9] : 0,
            CurrentReadSpeedKbs = hasSpeedFields ? (response[p + 14] << 8) | response[p + 15] : 0,
            MaxWriteSpeedKbs = hasWriteSpeeds ? (response[p + 18] << 8) | response[p + 19] : 0,
            CurrentWriteSpeedKbs = hasWriteSpeeds ? (response[p + 20] << 8) | response[p + 21] : 0,
        };
    }
}