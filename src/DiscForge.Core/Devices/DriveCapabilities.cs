// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Mmc;

namespace DiscForge.Core.Devices;

/// <summary>
/// What a specific drive can actually do, discovered at runtime — never assumed.
/// Built purely from parsed MMC responses (INQUIRY + GET CONFIGURATION + mode
/// page 2A), so the whole mapping is unit-testable without hardware. The Windows
/// Devices layer supplies the raw bytes; <see cref="Build"/> does the reasoning.
///
/// The GUI/CLI light features up strictly from this model. A 2024 LG reports
/// data/ISO/BD burning and no RAW DAO; a vintage Plextor Premium unlocks the
/// full toolkit. DiscForge never offers a burn mode the drive hasn't
/// demonstrated support for.
/// </summary>
public sealed record DriveCapabilities
{
    public required string DevicePath { get; init; }
    public required string Vendor { get; init; }
    public required string Model { get; init; }
    public required string FirmwareRevision { get; init; }

    public bool CdRead { get; init; }
    public bool CdWrite { get; init; }
    public bool DvdRead { get; init; }
    public bool DvdWrite { get; init; }
    public bool BdRead { get; init; }
    public bool BdWrite { get; init; }

    public bool TrackAtOnce { get; init; }
    public bool SessionAtOnce { get; init; }   // == CD Mastering (SAO/DAO)
    public bool DiscAtOnce { get; init; }

    /// <summary>RAW DAO with 2352 + packed subchannel — the endangered
    /// capability needed for byte-faithful CDI burns of mixed/multisession
    /// discs. Spec-derived from the CD Mastering RAW bit; confirm on hardware.</summary>
    public bool RawDao96 { get; init; }

    /// <summary>
    /// The media profile reported by the drive when it was interrogated — i.e.
    /// what disc was actually in it (CD-ROM, DVD-ROM, BD-ROM…), as opposed to
    /// what the drive *supports*. <see cref="MmcProfile.None"/> means no media or
    /// the drive didn't say.
    ///
    /// This matters for reading: raw 2352-byte sectors are a CD concept. DVD and
    /// BD sectors are always 2048 bytes and have no raw form, so a raw read of
    /// one is rejected by the drive.
    /// </summary>
    public MmcProfile MediaProfile { get; init; } = MmcProfile.None;

    /// <summary>
    /// What state the loaded disc is in — blank, appendable, finalised — from
    /// READ DISC INFORMATION. Null when there's no disc or the drive didn't say.
    ///
    /// <see cref="MediaProfile"/> only reports the disc TYPE; a written DVD+R DL
    /// and a blank one are both "DvdPlusRDl". This is the difference between
    /// "you can burn this" and "that disc is full".
    /// </summary>
    public DiscInformation? Disc { get; init; }

    /// <summary>True when there's a blank disc ready to be written.</summary>
    public bool MediaIsBlank => Disc?.IsBlank == true;

    /// <summary>True when the media in the drive is a CD (the only media with
    /// 2352-byte raw sectors and CD-DA audio).</summary>
    public bool MediaIsCd =>
        MediaProfile is MmcProfile.CdRom or MmcProfile.CdR or MmcProfile.CdRw;

    /// <summary>True when we know the media is NOT a CD (DVD/BD).</summary>
    public bool MediaIsDvdOrBd => MediaProfile is not MmcProfile.None && !MediaIsCd;

    public bool RawReadSubchannel { get; init; }
    public bool C2ErrorReporting { get; init; }
    public bool BufferUnderrunProtection { get; init; }

    /// <summary>
    /// Compose capabilities from parsed MMC data. GET CONFIGURATION profiles are
    /// the primary source for media families; mode page 2A fills in CD-era
    /// fidelity flags. Where the two disagree we take the optimistic union for
    /// read and the conservative intersection for write.
    /// </summary>
    public static DriveCapabilities Build(
        string devicePath, InquiryData inquiry, ConfigurationInfo config, MmCapabilities? page2a,
        DiscInformation? disc = null)
    {
        bool cdWriteProfile = config.HasProfile(MmcProfile.CdR) || config.HasProfile(MmcProfile.CdRw);
        bool dvdWriteProfile =
            config.HasProfile(MmcProfile.DvdMinusRSeq) || config.HasProfile(MmcProfile.DvdPlusR) ||
            config.HasProfile(MmcProfile.DvdPlusRw) || config.HasProfile(MmcProfile.DvdRam) ||
            config.HasProfile(MmcProfile.DvdMinusRwSeq) || config.HasProfile(MmcProfile.DvdPlusRDl) ||
            config.HasProfile(MmcProfile.DvdMinusRDl);
        bool bdWriteProfile =
            config.HasProfile(MmcProfile.BdRSrm) || config.HasProfile(MmcProfile.BdRRrm) ||
            config.HasProfile(MmcProfile.BdRe);

        bool cdReadProfile = config.HasProfile(MmcProfile.CdRom) || cdWriteProfile;
        bool dvdReadProfile = config.HasProfile(MmcProfile.DvdRom) || dvdWriteProfile;
        bool bdReadProfile = config.HasProfile(MmcProfile.BdRom) || bdWriteProfile;

        return new DriveCapabilities
        {
            DevicePath = devicePath,
            Vendor = inquiry.VendorId,
            Model = inquiry.ProductId,
            FirmwareRevision = inquiry.FirmwareRevision,

            // What's actually in the drive right now — drives the read strategy.
            MediaProfile = config.CurrentProfile,
            // ...and what state it's in, which decides whether it can be burned.
            Disc = disc,

            CdRead = cdReadProfile || (page2a?.CdRRead ?? false),
            DvdRead = dvdReadProfile || (page2a?.DvdRomRead ?? false),
            BdRead = bdReadProfile,

            // Write: require the profile AND (if 2A present) its agreement.
            CdWrite = cdWriteProfile && (page2a?.CdRWrite ?? true),
            DvdWrite = dvdWriteProfile && (page2a?.DvdRWrite ?? true),
            BdWrite = bdWriteProfile,

            TrackAtOnce = config.CdTrackAtOnce,
            SessionAtOnce = config.CdMastering,
            DiscAtOnce = config.CdMastering,
            RawDao96 = config.CdMasteringRaw,

            RawReadSubchannel = page2a?.ReadSubchannel ?? false,
            C2ErrorReporting = page2a?.C2Pointers ?? false,
            BufferUnderrunProtection = page2a?.BufferUnderrunProtection ?? false,
        };
    }

    /// <summary>Human summary for the drive list UI, DiscJuggler-style.</summary>
    public string Summary()
    {
        var caps = new List<string>();
        if (BdWrite) caps.Add("BD-R"); else if (BdRead) caps.Add("BD-ROM");
        if (DvdWrite) caps.Add("DVD±R");
        if (CdWrite) caps.Add(RawDao96 ? "CD-R (RAW DAO)" : "CD-R");
        else if (CdRead) caps.Add("CD-ROM");
        return $"{Vendor} {Model} [{string.Join(", ", caps)}]";
    }
}
