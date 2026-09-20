// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text.Json;

namespace DiscForge.App;

/// <summary>Serializable settings model (public members for the JSON reflection serializer).</summary>
internal sealed class SettingsModel
{
    public bool FirstRunComplete { get; set; }
    public bool RetroSkin { get; set; }
    public List<string> Recent { get; set; } = new();

    /// <summary>Last-used path to an external disc-dumping tool (e.g. for discs whose sector
    /// encoding needs drive-vendor-specific commands DiscForge doesn't implement — a GameCube
    /// disc on an unmodified PC drive is the known case). Remembered so the picker only needs
    /// asking once; DiscForge never bundles or ships this tool itself.</summary>
    public string? ExternalDumperPath { get; set; }

    /// <summary>Last-used path to an external PS1 dumping tool — CloneCD is the known case, a
    /// long-established tool in the PS1 preservation community. Kept as its own field, separate
    /// from <see cref="ExternalDumperPath"/>, so remembering one external tool doesn't overwrite
    /// the other; a user working with both GameCube and PS1 discs needs both remembered at once.
    /// Same escape-hatch shape and same caveat: DiscForge never bundles or ships this tool.</summary>
    public string? ExternalDumperPathPs1 { get; set; }

    /// <summary>Last-used path to an external DVD dumping tool — XReveal is the known case, a
    /// commercial CSS-aware DVD backup tool. Same escape-hatch shape and same caveat as the
    /// other <c>ExternalDumperPath*</c> fields, kept separate so it doesn't clobber them.</summary>
    public string? ExternalDumperPathDvd { get; set; }

    /// <summary>Last-used path to an external Blu-ray dumping tool — CloneBD is the known case,
    /// a commercial AACS-aware Blu-ray backup tool. Same escape-hatch shape and same caveat as
    /// the other <c>ExternalDumperPath*</c> fields, kept separate so it doesn't clobber them.</summary>
    public string? ExternalDumperPathBluray { get; set; }

    /// <summary>Last-used path to IsoBuster — a general-purpose optical-disc utility, not tied
    /// to one console/format the way the fields above are. Same escape-hatch shape and same
    /// caveat as the other <c>ExternalDumperPath*</c> fields, kept separate so it doesn't
    /// clobber them.</summary>
    public string? ExternalDumperPathIsoBuster { get; set; }

    /// <summary>Last-used path to DVDFab — the all-in-one version of what
    /// <see cref="ExternalDumperPathDvd"/> (Xreveal) and <see cref="ExternalDumperPathBluray"/>
    /// (CloneBD) already do separately: ripping/copying protected DVDs and Blu-rays in one tool.
    /// Kept as its own field for the same reason every escape-hatch field here is separate —
    /// configuring DVDFab shouldn't disturb Xreveal's or CloneBD's remembered path.</summary>
    public string? ExternalDumperPathDvdFab { get; set; }

    /// <summary>Last-used path to ImgBurn — used from BurnView, not ReadView: it's a burning
    /// tool first (its "create image from disc" mode is secondary), so its escape-hatch button
    /// lives on the screen that burns. Same field shape as
    /// <see cref="ExternalDumperPathIsoBuster"/> despite the different screen.</summary>
    public string? ExternalDumperPathImgBurn { get; set; }

    /// <summary>Last-used path to Alcohol 120% — used from BurnView, same reasoning as
    /// <see cref="ExternalDumperPathImgBurn"/> (a burning/mounting suite, not a ripper).</summary>
    public string? ExternalDumperPathAlcohol120 { get; set; }

    /// <summary>Last-used path to DAEMON Tools — used from BurnView, same reasoning as
    /// <see cref="ExternalDumperPathImgBurn"/> (primarily virtual-drive mounting, with burning as
    /// a secondary feature — it doesn't rip discs at all).</summary>
    public string? ExternalDumperPathDaemonTools { get; set; }

    /// <summary>Last-used path to Wiimms ISO Tools (WIT) — used from ReadView, alongside
    /// <see cref="ExternalDumperPath"/> (Rawdump2), because it fills the gap right after that
    /// button's job ends: Rawdump2 pulls a raw Wii dump off the drive, but DiscForge's own Wii
    /// support only reads the header/partition table — it never decrypts a Wii disc, so it can't
    /// turn that raw dump into a scrubbed, verifiable ISO the way WIT can. Own remembered path so
    /// configuring it doesn't disturb Rawdump2's.</summary>
    public string? ExternalDumperPathWit { get; set; }

    /// <summary>Last-used path to a user's own generic disc-reading tool — used from ReadView's
    /// "Other tool…" button. Deliberately not named after any specific product, unlike every
    /// other <c>ExternalDumperPath*</c> field: added as the declined alternative to wiring a
    /// named copy-protection-removal tool (AnyDVD) into the Read Disc tile, which this project's
    /// clean-room, detect-but-never-circumvent design doesn't do. This field is for whatever
    /// disc-reading/imaging tool isn't already covered by a named button. Own remembered path,
    /// same reason as every other field here.</summary>
    public string? ExternalDumperPathOther { get; set; }

    /// <summary>Last-used path to Xbox Backup Creator (XBC) — used from XboxView. DiscForge's own
    /// Xbox support (<c>DiscForge.Core.Xbox</c>) only understands the XDVDFS filesystem inside an
    /// already-extracted image; it never reads an Xbox or Xbox 360 disc's security sectors, which
    /// live outside that filesystem and need drive-specific handling this project doesn't
    /// reimplement (same posture as the CSS/AACS tools on ReadView). XBC is the established
    /// community tool for that step. Own remembered path, same reason as every other field here.</summary>
    public string? ExternalDumperPathXbc { get; set; }

    /// <summary>Last-used path to abgx360 — used from XboxView, alongside
    /// <see cref="ExternalDumperPathXbc"/>: XBC pulls the disc, abgx360 verifies/repairs the
    /// resulting Xbox 360 ISO against known-good hashes and can rebuild its header. A companion
    /// step, not a replacement, so it gets its own button and its own remembered path rather than
    /// sharing XBC's.</summary>
    public string? ExternalDumperPathAbgx360 { get; set; }

    /// <summary>Last-used path to MemcardRex — used from MemoryCardView. DiscForge can read and
    /// extract PS1/PS2 memory cards fully but cannot write a save back onto one (Dreamcast VMU is
    /// the only card format this project's own code can write to) — injecting or editing a PS1
    /// save is exactly the gap MemcardRex fills. Own remembered path, same reason as every other
    /// field here.</summary>
    public string? ExternalDumperPathMemcardRex { get; set; }

    /// <summary>Last-used path to a PS2 Save Builder-style tool — used from MemoryCardView,
    /// alongside <see cref="ExternalDumperPathMemcardRex"/>. MemcardRex only covers PS1; PS2 save
    /// injection is the same missing-writer gap one console family over. Own remembered path so
    /// configuring it doesn't disturb MemcardRex's.</summary>
    public string? ExternalDumperPathPs2SaveBuilder { get; set; }

    /// <summary>Last-used path to GCMM (GameCube Memory Manager) — used from MemoryCardView.
    /// DiscForge reads and decodes GameCube <c>.gci</c> saves (<c>DiscForge.Core.Saves</c>) but,
    /// like PS1/PS2, has no writer for that format — Dreamcast VMU is still the only card format
    /// this project's own code can write to. GCMM writes a <c>.gci</c> back onto a real GameCube
    /// card or an SD adapter. Own remembered path, same reason as every other field here.</summary>
    public string? ExternalDumperPathGcmm { get; set; }

    /// <summary>Last-used path to KryoFlux's DTC (capture) software — used from FloppyView.
    /// DiscForge already reads and inspects a KryoFlux raw stream once one exists
    /// (<c>kryoflux-info</c>, <c>DiscForge.Core</c>'s flux container), but nothing here talks to
    /// KryoFlux capture hardware over USB — DTC is that vendor's own capture program, and
    /// DiscForge has no reason to reimplement a USB device driver for someone else's board. Own
    /// remembered path, same reason as every other field here.</summary>
    public string? ExternalDumperPathKryoFlux { get; set; }

    /// <summary>Last-used path to Greaseweazle's host software (<c>gw</c>) — used from
    /// FloppyView, same reasoning as <see cref="ExternalDumperPathKryoFlux"/>: DiscForge can read
    /// a SuperCard Pro flux file (<c>scp-info</c>) once one exists, but capturing flux from a
    /// Greaseweazle board over USB is the vendor's own tool's job, not this project's. Own
    /// remembered path so configuring it doesn't disturb KryoFlux's.</summary>
    public string? ExternalDumperPathGreaseweazle { get; set; }

    /// <summary>Last-used path to UMDGen — used from PspView. DiscForge already reads a PSP UMD's
    /// filesystem and PARAM.SFO (<c>DiscForge.Core.Psp</c>, <c>psp-info</c>) without decrypting
    /// anything, but it has no PSP ISO editor/rebuilder of its own. UMDGen fills that role — and,
    /// unlike every other tool on this page, it isn't a ripper either: a physical UMD is dumped by
    /// homebrew running on the PSP itself, not by anything a PC talks to over USB, so this button
    /// is for working with an image you already have rather than acquiring one.</summary>
    public string? ExternalDumperPathUmdGen { get; set; }

    /// <summary>Last-used path to GBxCart RW / FlashGBX — used from CartridgeView, for the
    /// Game Boy/Game Boy Color/Game Boy Advance family. DiscForge reads GB/GBC/GBA ROM dumps once
    /// they exist but has no code that talks to cartridge-reading hardware — that's always a
    /// flashcart's own software. GBxCart RW is the community's de facto standard for this family.
    /// Own remembered path, same reason as every other field here.</summary>
    public string? ExternalDumperPathGbxCart { get; set; }

    /// <summary>Last-used path to Cart Reader (sanni's open-source Arduino cart dumper) — used
    /// from CartridgeView, for N64/SNES/Genesis/NES, the cartridge families GBxCart RW doesn't
    /// cover. Same reasoning as <see cref="ExternalDumperPathGbxCart"/>: DiscForge reads the
    /// resulting ROM dumps but has no cartridge-reading hardware of its own. Kept as a second
    /// button/remembered path rather than folded into GBxCart RW's, since cartridge dumping is
    /// genuinely split across more than one piece of hardware and DiscForge doesn't favour one
    /// brand over the other.</summary>
    public string? ExternalDumperPathCartReader { get; set; }

    /// <summary>Last-used path to Pseudo Saturn Kai — used from MemoryCardView. DiscForge already
    /// parses a Sega Saturn backup-memory image's save directory (<c>SaturnSaveReader</c>, the
    /// Saturn detail in Examine) — names, comments, sizes — but deliberately stops at the
    /// directory listing: full save-data extraction via the block-link list was left unimplemented
    /// rather than risk returning wrong bytes, and there is no writer at all, nor anything that
    /// talks to a real Saturn's internal RAM or a backup cartridge. Pseudo Saturn Kai is the
    /// established homebrew tool for dumping and restoring Saturn backup memory on real hardware.
    /// Own remembered path, same reason as every other field here.</summary>
    public string? ExternalDumperPathPseudoSaturnKai { get; set; }

    /// <summary>Last-used path to a card formatter (e.g. the SD Association's official SD Card
    /// Formatter, Tuxera-developed) — used from FormatMediaView. DiscForge has no code that talks
    /// to a card reader/writer at all, and formatting removable media (partition table + filesystem
    /// at the physical/logical level a card's own controller expects) is out of scope for an
    /// optical-disc/cartridge preservation tool to reimplement — this is prep work for a flashcart's
    /// SD card, not disc or cartridge dumping itself. Own remembered path, same reason as every
    /// other field here.</summary>
    public string? ExternalDumperPathCardFormatter { get; set; }

    /// <summary>Last-used path to a sector-level drive/image cloning tool (e.g. HDD Raw Copy Tool)
    /// — used from RawCopyView. A different domain from every other external-tool field here: those
    /// are all optical-disc/cartridge/floppy specific, while this clones a whole physical drive (or
    /// an existing raw image) byte-for-byte — a generic block-device operation DiscForge's own
    /// sector-level code (built around ECMA-130 CD/DVD/BD structure) has no reason to duplicate.
    /// Own remembered path, same reason as every other field here.</summary>
    public string? ExternalDumperPathHddRawCopy { get; set; }
}

/// <summary>
/// Small persisted-settings store in %APPDATA%\DiscForge\settings.json:
/// first-run flag and the recent-files list. Raises <see cref="Changed"/> so the
/// menu can rebuild its Recent submenu live.
/// </summary>
internal static class Settings
{
    private const int MaxRecent = 8;

    public static event Action? Changed;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscForge");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly SettingsModel _model = Load();

    private static SettingsModel Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt or unreadable -> defaults */ }
        return new SettingsModel();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(_model, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
        Changed?.Invoke();
    }

    public static bool FirstRunComplete => _model.FirstRunComplete;

    public static bool RetroSkin
    {
        get => _model.RetroSkin;
        set { _model.RetroSkin = value; Save(); }
    }


    public static void MarkFirstRunComplete()
    {
        if (_model.FirstRunComplete) return;
        _model.FirstRunComplete = true;
        Save();
    }

    public static string? ExternalDumperPath
    {
        get => _model.ExternalDumperPath;
        set { _model.ExternalDumperPath = value; Save(); }
    }

    public static string? ExternalDumperPathPs1
    {
        get => _model.ExternalDumperPathPs1;
        set { _model.ExternalDumperPathPs1 = value; Save(); }
    }

    public static string? ExternalDumperPathDvd
    {
        get => _model.ExternalDumperPathDvd;
        set { _model.ExternalDumperPathDvd = value; Save(); }
    }

    public static string? ExternalDumperPathBluray
    {
        get => _model.ExternalDumperPathBluray;
        set { _model.ExternalDumperPathBluray = value; Save(); }
    }

    public static string? ExternalDumperPathIsoBuster
    {
        get => _model.ExternalDumperPathIsoBuster;
        set { _model.ExternalDumperPathIsoBuster = value; Save(); }
    }

    public static string? ExternalDumperPathDvdFab
    {
        get => _model.ExternalDumperPathDvdFab;
        set { _model.ExternalDumperPathDvdFab = value; Save(); }
    }

    public static string? ExternalDumperPathImgBurn
    {
        get => _model.ExternalDumperPathImgBurn;
        set { _model.ExternalDumperPathImgBurn = value; Save(); }
    }

    public static string? ExternalDumperPathAlcohol120
    {
        get => _model.ExternalDumperPathAlcohol120;
        set { _model.ExternalDumperPathAlcohol120 = value; Save(); }
    }

    public static string? ExternalDumperPathDaemonTools
    {
        get => _model.ExternalDumperPathDaemonTools;
        set { _model.ExternalDumperPathDaemonTools = value; Save(); }
    }

    public static string? ExternalDumperPathWit
    {
        get => _model.ExternalDumperPathWit;
        set { _model.ExternalDumperPathWit = value; Save(); }
    }

    public static string? ExternalDumperPathOther
    {
        get => _model.ExternalDumperPathOther;
        set { _model.ExternalDumperPathOther = value; Save(); }
    }

    public static string? ExternalDumperPathXbc
    {
        get => _model.ExternalDumperPathXbc;
        set { _model.ExternalDumperPathXbc = value; Save(); }
    }

    public static string? ExternalDumperPathAbgx360
    {
        get => _model.ExternalDumperPathAbgx360;
        set { _model.ExternalDumperPathAbgx360 = value; Save(); }
    }

    public static string? ExternalDumperPathMemcardRex
    {
        get => _model.ExternalDumperPathMemcardRex;
        set { _model.ExternalDumperPathMemcardRex = value; Save(); }
    }

    public static string? ExternalDumperPathPs2SaveBuilder
    {
        get => _model.ExternalDumperPathPs2SaveBuilder;
        set { _model.ExternalDumperPathPs2SaveBuilder = value; Save(); }
    }

    public static string? ExternalDumperPathGcmm
    {
        get => _model.ExternalDumperPathGcmm;
        set { _model.ExternalDumperPathGcmm = value; Save(); }
    }

    public static string? ExternalDumperPathKryoFlux
    {
        get => _model.ExternalDumperPathKryoFlux;
        set { _model.ExternalDumperPathKryoFlux = value; Save(); }
    }

    public static string? ExternalDumperPathGreaseweazle
    {
        get => _model.ExternalDumperPathGreaseweazle;
        set { _model.ExternalDumperPathGreaseweazle = value; Save(); }
    }

    public static string? ExternalDumperPathUmdGen
    {
        get => _model.ExternalDumperPathUmdGen;
        set { _model.ExternalDumperPathUmdGen = value; Save(); }
    }

    public static string? ExternalDumperPathGbxCart
    {
        get => _model.ExternalDumperPathGbxCart;
        set { _model.ExternalDumperPathGbxCart = value; Save(); }
    }

    public static string? ExternalDumperPathCartReader
    {
        get => _model.ExternalDumperPathCartReader;
        set { _model.ExternalDumperPathCartReader = value; Save(); }
    }

    public static string? ExternalDumperPathPseudoSaturnKai
    {
        get => _model.ExternalDumperPathPseudoSaturnKai;
        set { _model.ExternalDumperPathPseudoSaturnKai = value; Save(); }
    }

    public static string? ExternalDumperPathCardFormatter
    {
        get => _model.ExternalDumperPathCardFormatter;
        set { _model.ExternalDumperPathCardFormatter = value; Save(); }
    }

    public static string? ExternalDumperPathHddRawCopy
    {
        get => _model.ExternalDumperPathHddRawCopy;
        set { _model.ExternalDumperPathHddRawCopy = value; Save(); }
    }

    public static IReadOnlyList<string> Recent => _model.Recent;

    public static void AddRecent(string path)
    {
        _model.Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _model.Recent.Insert(0, path);
        while (_model.Recent.Count > MaxRecent) _model.Recent.RemoveAt(_model.Recent.Count - 1);
        Save();
    }

    public static void ClearRecent()
    {
        if (_model.Recent.Count == 0) return;
        _model.Recent.Clear();
        Save();
    }
}
