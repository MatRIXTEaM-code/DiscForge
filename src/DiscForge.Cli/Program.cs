// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Audio;
using DiscForge.Core.BluRay;
using DiscForge.Core.Cdi;
using DiscForge.Core.Convert;
using DiscForge.Core.Create;
using DiscForge.Core.Iso;
using DiscForge.Core.Cue;
using DiscForge.Core.Files;
using DiscForge.Core.GameCube;
using DiscForge.Core.Gdi;
using DiscForge.Core.Mds;
using DiscForge.Core.Nrg;
using DiscForge.Core.Patch;
using DiscForge.Core.PlayStation;
using DiscForge.Core.Raw;
using DiscForge.Core.ScummVm;
using DiscForge.Core.Udf;
using DiscForge.Core.Xbox;

const string Banner = """
  ___                   _                    _
 / _ \ _ __   ___ _ __ | |_   _  __ _  __ _| | ___ _ __
| | | | '_ \ / _ \ '_ \| | | | |/ _` |/ _` | |/ _ \ '__|
| |_| | |_) |  __/ | | | | |_| | (_| | (_| | |  __/ |
 \___/| .__/ \___|_| |_|_|\__,_|\__, |\__, |_|\___|_|
      |_|                       |___/ |___/   v0.8.0
""";

if (args.Length == 0)
{
    Console.WriteLine(Banner);
    Console.WriteLine();
    Console.WriteLine("usage: dforge <command> [args]");
    Console.WriteLine();
    Console.WriteLine("commands:");
    Console.WriteLine("  identify <file>       Say what a file is (any format DiscForge knows)");
    Console.WriteLine("  library scan <dir> [--dat f]   Identify+hash a whole tree, verify vs a DAT");
    Console.WriteLine("  submission-info <image> [--out f]  Redump-style hashes/cuesheet/subchannel for a dump");
    Console.WriteLine("  library rename <dir> --dat f [--apply]  Rename verified files to canonical names");
    Console.WriteLine("  license <keygen|issue|verify|machine-id> …  Manage DiscForge licence keys (see --help)");
    Console.WriteLine("  1g1r <dat> [--regions USA,Europe,Japan] [--keep-proto] [--drop-unlicensed] [--out f]");
    Console.WriteLine("                          One-game-one-ROM: pick the best region per game from a DAT");
    Console.WriteLine("  rebuild <src> <dest> --dat f [--per-game] [--move] [--apply]");
    Console.WriteLine("                          Rebuild a clean, DAT-named set from a messy folder");
    Console.WriteLine("  dat-diff <old.dat> <new.dat>  Compare two DAT revisions: added/removed/changed games");
    Console.WriteLine("  library-report <dir> <out.html> [--dat f]  Scan a folder to a shareable HTML dashboard");
    Console.WriteLine("  frontend-export <retroarch|gamelist> <dir> <out> [--name N] [--dat f]");
    Console.WriteLine("                          Write a RetroArch .lpl or EmulationStation gamelist.xml for a folder");
    Console.WriteLine("  m3u <out.m3u> <disc1> <disc2> [...]  Build a multi-disc M3U playlist (order preserved)");
    Console.WriteLine("  torrentzip <out.zip> <file> [...]   Write a deterministic TorrentZip-structured archive");
    Console.WriteLine("  hashgen <sfv|md5|sha1> <out> <file> [...]  Write a checksum sidecar for the files");
    Console.WriteLine("  hashverify <sidecar.sfv|.md5|.sha1>  Re-hash the referenced files and report OK/FAIL");
    Console.WriteLine("  ps1card-convert <in> <out> [raw|gme|vgs]  Convert a PS1 memory card between container formats");
    Console.WriteLine("  save-convert <in> <out> <op> [--fill FF]  Fix a cartridge save's byte order or size.");
    Console.WriteLine("                          op: swap16|swap32, pad <size|sram|flash|eeprom4k|eeprom16k|mempak>, trim");
    Console.WriteLine("  rom-convert <in> <out> <op>  Fix a cartridge dump so it matches a DAT. op:");
    Console.WriteLine("                          z64|v64|n64 (N64 byte order), snes-strip|snes-add,");
    Console.WriteLine("                          smd|unsmd (Genesis interleave), nes-strip (iNES header)");
    Console.WriteLine("  inspect <image.cdi>   Show version, sessions, and track layout");
    Console.WriteLine("  extract <cdi> <dir>   Extract tracks to ISO/WAV (--raw for full sectors)");
    Console.WriteLine("  convert <in> <out>    CDI <-> BIN+CUE / ISO / GDI / NRG, or MDS -> CDI");
Console.WriteLine("                        (.cdi<->.cue, .cdi<->.iso, .mds->.cdi)");
    Console.WriteLine("  disc-convert <in> <out>  Universal hub: any format -> any via a canonical model");
Console.WriteLine("                        (.cue .chd .iso .cso .zso .wbfs .cdi .nrg .mds .gdi .ccd in;");
Console.WriteLine("                         .cue .chd .iso .cdi .nrg out)");
    Console.WriteLine("  verify <cdi> [--checksums]  Structural checks + per-track CRC-32");
    Console.WriteLine("  create <dir> <out.cdi>  Build a data CDI from a folder (--volume NAME, --rock-ridge)");
    Console.WriteLine("  compare <a.cdi> <b.cdi>  Diff two images (structure + per-track CRC-32)");
    Console.WriteLine("  create-audio <out.cdi> <a.wav> [b.wav ...]  Build an audio CD from WAVs");
    Console.WriteLine("                          --gapless, --74, --postgap [sectors]");
    Console.WriteLine("  ls <image.cdi>          List files inside the image (ISO 9660 or UDF, auto-detected)");
Console.WriteLine("                          --iso 8.3 names, --joliet, --udf force a filesystem");
    Console.WriteLine("  extract-files <image.cdi> <dir>  Extract the filesystem contents");
    Console.WriteLine("  build-raw <src> <out>   Compose a RAW DAO image (lead-in + subcode) from");
    Console.WriteLine("                          a .cue or .cdi. --subcode pq|cooked|raw (default cooked)");
    Console.WriteLine("  subch <file.sub>        Analyse a captured raw sub-channel sidecar: Q-CRC");
    Console.WriteLine("                          validity, LibCrypt-style protection fingerprint");
    Console.WriteLine("  cdg-preview <src>       Decode CD+G graphics from a raw image (2448) or a");
    Console.WriteLine("                          .cue with .sub sidecar. --seconds N (default 30),");
    Console.WriteLine("                          --out shot.ppm writes a screenshot");
    Console.WriteLine("  cdg-render <file.cdg>   Render a CD+G frame to PNG. --at MM:SS (default end),");
    Console.WriteLine("                          --out <file.png> (default <name>.png)");
    Console.WriteLine("  cdg-extract <sub> <out.cdg>  Extract the CD+G packet stream from a raw");
    Console.WriteLine("                          96-byte/sector sub-channel sidecar");
    Console.WriteLine("  view-sector <img> <addr> [--count N] [--descramble]  Annotated hex view of");
    Console.WriteLine("                          sectors. addr: LBA, mm:ss:ff, or +fileindex");
    Console.WriteLine("  extract-sectors <img> <out> --start <addr> --count N  Pull a sector range");
    Console.WriteLine("                          --as stored|user|raw2352, --byteswap for audio");
    Console.WriteLine("  inspect-raw <img>       Analyse a raw image: TOC, Q health, CD-TEXT, MCN/ISRC,");
    Console.WriteLine("                          scrambling, EDC/ECC. --deep checks every sector.");
    Console.WriteLine("                          Also reads bare 2352 BINs (ECC gold-check on real rips)");
    Console.WriteLine("  checksum <file>         CRC-32 + MD5 + SHA-1 + SHA-256 in one pass");
    Console.WriteLine("                          --write [sha256|md5|sha1|sfv|all]   write sidecar(s)");
    Console.WriteLine("                          --verify   check against an existing sidecar");
    Console.WriteLine("  browse <image>          List files inside a .cdi or .iso (ISO 9660 or UDF)");
    Console.WriteLine("                          --extract <dir> writes them out, --only <text> filters");
    Console.WriteLine("  cue-check <sheet.cue>   Check a cuesheet against the data file it describes:");
    Console.WriteLine("                          indexes inside the file, track types consistent,");
    Console.WriteLine("                          arithmetic reaching the end. Exit 2 on errors, 1 on warnings");
    Console.WriteLine("  ecc-repair <image.bin>  Rebuild damaged Mode 1 sectors from the Reed-Solomon");
    Console.WriteLine("                          parity they already carry. --dry-run to check only");
    Console.WriteLine("  fix-modes <image.cdi>   Correct track modes recorded wrongly in a CDI descriptor");
    Console.WriteLine("  split <file> <size>     Split into .001/.002/… + .sfv manifest");
    Console.WriteLine("                          sizes: bytes, 700m, 4g, or fat32 (= 4 GiB - 1)");
    Console.WriteLine("  join <part|base> [out]  Rejoin parts, verifying CRCs + SHA-256 via the manifest");
    Console.WriteLine("  ppf-apply <patch.ppf> <image.bin>  Apply a PlayStation Patch File to an image");
    Console.WriteLine("                          --undo revert (PPF 3.0), --force skip validation, --dry-run");
    Console.WriteLine("  ppf-create <orig> <mod> <out.ppf>  Build a PPF 3.0 from a before/after image pair");
    Console.WriteLine("                          --desc, --fileid, --no-undo, --no-validation");
    Console.WriteLine("  ppf-info <patch.ppf>    Show a patch's version, description, size and flags");
    Console.WriteLine("  ppf-convert <in> <out> --to 1|2|3  Rewrite a patch in another PPF revision");
    Console.WriteLine("  ppf-edit <in> <out>     Change a patch's --desc and/or --fileid in place");
    Console.WriteLine("  ips-apply <patch.ips> <image> [--out f]  Apply an IPS patch (in place unless --out)");
    Console.WriteLine("  ips-create <orig> <mod> <out.ips>  Build an IPS patch from a before/after pair");
    Console.WriteLine("  bps-apply <patch.bps> <source> [--out f]  Apply a BPS patch (CRC-verified)");
    Console.WriteLine("  bps-create <source> <target> <out.bps>  Build a BPS patch from a before/after pair");
    Console.WriteLine("  create-udf <folder> <out.udf>  Build a UDF 1.02 filesystem image from a folder");
    Console.WriteLine("                          --volume NAME sets the volume label");
    Console.WriteLine("  gdi-info <disc.gdi>     Show a Dreamcast GD-ROM track layout and validate it");
    Console.WriteLine("                          against the track files beside it");
    Console.WriteLine("  gdi-browse <disc.gdi>   List the game filesystem on the high-density track");
    Console.WriteLine("                          --extract <dir> writes the files out");
    Console.WriteLine("  milcd-to-cdi <in.cue> <out.cdi>  Convert a Dreamcast MIL-CD Redump bin/cue to a");
    Console.WriteLine("                          two-session CDI. --version v2|v3|v35, --gap <sectors>");
    Console.WriteLine("  ipbin-info <image>      Identify a Dreamcast disc from its IP.BIN boot header");
    Console.WriteLine("  saturn-info <image>     Identify a Sega Saturn disc from its header");
    Console.WriteLine("                          (.gdi, .cue MIL-CD, .cdi, or a raw .bin/.iso)");
    Console.WriteLine("  cdi-console-info <image>  Identify a Philips CD-i (Green Book) disc and list");
    Console.WriteLine("                          its filesystem (pure CD-i or CD-i Bridge)");
    Console.WriteLine("  psp-info <image>        Read a PSP UMD's PARAM.SFO metadata and filesystem");
    Console.WriteLine("                          (.iso, .cso or .zso; --sfo dumps every SFO key/value)");
    Console.WriteLine("  pbp-info <EBOOT.PBP>    List a PSP PBP package: version + each sub-file's size,");
    Console.WriteLine("                          and its PARAM.SFO title/id/category if present");
    Console.WriteLine("  pbp-extract <EBOOT.PBP> <dir>  Write each non-empty PBP sub-file to <dir>/<name>");
    Console.WriteLine("                          (DATA.PSP is extracted raw and is NOT decrypted)");
    Console.WriteLine("  bdmv-info <file|folder> Show a Blu-ray playlist (.mpls), clip-info (.clpi),");
    Console.WriteLine("                          or enumerate titles from a BDMV folder");
    Console.WriteLine("  iso-rebase <in> <out> <baseLBA>  Shift an ISO's LBAs (GD-ROM fix; base 45000)");
    Console.WriteLine("  xiso-ls <image.iso>     List files in an Xbox XDVDFS image (--extract <dir>)");
    Console.WriteLine("  create-xiso <folder> <out.iso>   Build an Xbox XISO from a folder");
    Console.WriteLine("  ps2-info <image>        Identify a PlayStation 1/2 disc (game ID, region) from SYSTEM.CNF");
    Console.WriteLine("  gcm-info <image>        GameCube disc: boot header + file tree; for a Wii disc,");
    Console.WriteLine("                          the volume header + partition table (contents not read)");
    Console.WriteLine("  wbfs-info <file>        List the Wii/GameCube discs in a WBFS container");
    Console.WriteLine("                          (slot, game id, title, sizes)");
    Console.WriteLine("  wbfs-extract <file> <slot> <out.iso>  Rebuild one disc's ISO from a WBFS");
    Console.WriteLine("                          container (contents are copied as-is, not decrypted)");
    Console.WriteLine("  rvz-info <image>        Identify an RVZ/WIA container and show its metadata");
    Console.WriteLine("  rom-info <file>         Identify a cartridge ROM (N64, SNES, Genesis, GB/GBC, GBA,");
    Console.WriteLine("                          NES, and more) and print its No-Intro CRC32/MD5/SHA1");
    Console.WriteLine("  scummvm-detect <path>   ScummVM Advanced-Detector fingerprints (size + MD5 of the");
    Console.WriteLine("                          first 5000 bytes) for a game folder or file. --recursive,");
    Console.WriteLine("                          --bytes N to change the hashed length");
    Console.WriteLine("  scummvm-export <cue> <dir>  Export a disc into a ScummVM game folder: data files +");
    Console.WriteLine("                          each CD audio track as trackNN.wav. --flac/--ogg re-encode");
    Console.WriteLine("                          the audio in-process (no ffmpeg needed); --high = better OGG");
    Console.WriteLine("  disk-info <image>       Read a whole-disk image's partition table (auto-detect");
    Console.WriteLine("                          MBR, GPT, or PS2 APA) and the filesystem in each partition");
    Console.WriteLine("  floppy-info <image>     List a floppy image's contents (auto-detect C64 D64,");
    Console.WriteLine("                          Amiga ADF, or DOS FAT12 .img)");
    Console.WriteLine("  gameaudio-info <file>   Read a game-music file's metadata/structure (auto-detect");
    Console.WriteLine("                          PSF/PSF2, SPC, VGM, NSF): system, tags, duration. No playback");
    Console.WriteLine("  gci-info <file>         List GameCube saves in a .gci or a memory-card image");
    Console.WriteLine("  gci-extract <card> <index> <out.gci>  Write one save from a card image to a .gci");
    Console.WriteLine("  n64save-info <file>     Identify an N64 save by size, and list Controller Pak notes");
    Console.WriteLine("  saturnsave-info <file>  List the directory of a Sega Saturn backup-memory image");
    Console.WriteLine("  floppy-extract <image> <path-in-image> <out>  Extract one file from a floppy image");
    Console.WriteLine("  cheat-decode <platform> <code>  Decode a Game Genie / GameShark code to address/value");
    Console.WriteLine("                          platform: nes|snes|genesis|gb|gs-ps1");
    Console.WriteLine("  cheat-encode <platform> <address> <value> [compare]  Encode a Game Genie code");
    Console.WriteLine("                          platform: nes|snes|genesis|gb (hex address/value)");
    Console.WriteLine("  cheat-apply-nes <rom> <code> <out>  Apply an NES Game Genie code to a ROM (NROM)");
    Console.WriteLine("  adx-decode <in.adx> <out.wav>  Decode a CRI ADX ADPCM stream to a 16-bit WAV");
    Console.WriteLine("  read-offset <samples> [in.wav out.wav]  Redump read-offset math; with a WAV,");
    Console.WriteLine("                          slide it by <samples> (combined drive+disc offset)");
    Console.WriteLine("  vab-info <file.vab>     Read a PlayStation VAB (VAG bank): programs, tones, VAGs");
    Console.WriteLine("  seq-info <file.seq>     Read a PlayStation SEQ sequence: ppqn, tempo, event count");
    Console.WriteLine("  str-demux <in.str> <out-dir>  Split a PSX .str into MDEC bitstreams + audio note");
    Console.WriteLine("                          --sector-size 2352|2048 (default 2352)");
    Console.WriteLine("  burn                  (Phase 4) You know what this does");
    return 0;
}

return args[0].ToLowerInvariant() switch
{
    "inspect" => Inspect(args),
    "fix-modes" => FixModesCommand.Run(args),
"browse" => ImageCommands.Browse(args),
    "cue-check" => ImageCommands.CueCheck(args),
    "ecc-repair" => ImageCommands.EccRepair(args),
    "extract" => Extract(args),
    "convert" => Convert(args),
    "disc-convert" => DiscConvert(args),
    "verify" => Verify(args),
    "create" => Create(args),
    "compare" => Compare(args),
    "create-audio" => CreateAudio(args),
    "ls" => Ls(args),
    "extract-files" => ExtractFiles(args),
    "build-raw" => BuildRaw(args),
    "subch" => Subch(args),
    "cdg-preview" => CdgPreview(args),
    "cdg-render" => CdgRender(args),
    "cdg-extract" => CdgExtract(args),
    "view-sector" => ViewSector(args),
    "extract-sectors" => ExtractSectors(args),
    "inspect-raw" => InspectRaw(args),
    "scan-protection" => ScanProtection(args),
    "to-ccd" => ToCcd(args),
    "dvd-info" => DvdInfo(args),
    "dvd-rewrite" => DvdRewrite(args),
    "vcd-info" => VcdInfo(args),
    "transcode" => Transcode(args),
    "accuraterip" => AccurateRipCmd(args),
    "mount" => MountCmd(args),
    "ccd-info" => CcdInfo(args),
    "wbfs-info" => WbfsInfo(args),
    "wbfs-extract" => WbfsExtract(args),
    "checksum" => Checksum(args),
    "split" => Split(args),
    "join" => Join(args),
    "ppf-apply" => PpfApply(args),
    "ppf-create" => PpfCreate(args),
    "ppf-info" => PpfInfo(args),
    "ppf-convert" => PpfConvert(args),
    "ppf-edit" => PpfEdit(args),
    "ips-apply" => IpsApply(args),
    "ips-create" => IpsCreate(args),
    "bps-apply" => BpsApply(args),
    "bps-create" => BpsCreate(args),
    "create-udf" => CreateUdf(args),
    "gdi-info" => GdiInfo(args),
    "gdi-browse" => GdiBrowse(args),
    "iso-rebase" => IsoRebase(args),
    "xiso-ls" => XisoLs(args),
    "create-xiso" => CreateXiso(args),
    "ps2-info" => Ps2Info(args),
    "scummvm-detect" => ScummvmDetect(args),
    "scummvm-export" => ScummvmExport(args),
    "psx-exe-info" => PsxExeInfo(args),
    "psx-pad" => PsxPad(args),
    "bin2src" => Bin2Src(args),
    "search" => SearchCmd(args),
    "tim-info" => TimInfo(args),
    "tim-extract" => TimExtract(args),
    "xa-extract" => XaExtractCmd(args),
    "vag-extract" => VagExtract(args),
    "adx-decode" => AdxDecodeCmd(args),
    "read-offset" => ReadOffsetCmd(args),
    "vab-info" => VabInfoCmd(args),
    "seq-info" => SeqInfoCmd(args),
    "str-demux" => StrDemuxCmd(args),
    "tmd-info" => TmdInfo(args),
    "tmd2dxf" => Tmd2Dxf(args),
    "tod-info" => TodInfo(args),
    "cdr-info" => CdrInfo(args),
    "psx-video-mode" => PsxVideoModeCmd(args),
    "vmu-info" => VmuInfo(args),
    "vmu-extract" => VmuExtract(args),
    "vmu-create" => VmuCreate(args),
    "vmu-add" => VmuAdd(args),
    "vms2vmi" => Vms2Vmi(args),
    "ps2mc-info" => Ps2McInfo(args),
    "ps2mc-extract" => Ps2McExtract(args),
    "psxmc-info" => PsxMcInfo(args),
    "psxmc-extract" => PsxMcExtract(args),
    "gci-info" => GciInfo(args),
    "gci-extract" => GciExtract(args),
    "n64save-info" => N64SaveInfo(args),
    "saturnsave-info" => SaturnSaveInfo(args),
    "chd-info" => ChdInfo(args),
    "identify" => IdentifyCmd(args),
    "gameaudio-info" => GameAudioInfo(args),
    "disk-info" => DiskInfo(args),
    "floppy-info" => FloppyInfo(args),
    "floppy-extract" => FloppyExtract(args),
    "dat-verify" => DatVerify(args),
    "library" => Library(args),
    "license" => LicenseCmd(args),
    "1g1r" => OneGameOneRomCmd(args),
    "rebuild" => RebuildCmd(args),
    "dat-diff" => DatDiffCmd(args),
    "library-report" => LibraryReportCmd(args),
    "frontend-export" => FrontendExportCmd(args),
    "m3u" => M3uCmd(args),
    "torrentzip" => TorrentZipCmd(args),
    "hashgen" => HashGenCmd(args),
    "hashverify" => HashVerifyCmd(args),
    "ps1card-convert" => Ps1CardConvertCmd(args),
    "save-convert" => SaveConvertCmd(args),
    "rom-convert" => RomConvertCmd(args),
    "submission-info" => SubmissionInfoCmd(args),
    "ciso-info" => CisoInfoCmd(args),
    "ciso-to-iso" => CisoToIso(args),
    "iso-to-ciso" => IsoToCiso(args),
    "dc-scramble" => DcScramble(args, scramble: true),
    "dc-descramble" => DcScramble(args, scramble: false),
    "bincue-merge" => BinCueMergeCmd(args),
    "bincue-split" => BinCueSplitCmd(args),
    "milcd-to-cdi" => MilcdToCdi(args),
    "ipbin-info" => IpBinInfo(args),
    "saturn-info" => SaturnInfo(args),
    "cdi-console-info" => CdInteractiveConsoleInfo(args),
    "psp-info" => PspInfo(args),
    "pbp-info" => PbpInfo(args),
    "pbp-extract" => PbpExtract(args),
    "gcm-info" => GcmInfo(args),
    "rvz-info" => ShowRvzInfo(args),
    "rom-info" => RomInfo(args),
    "bdmv-info" => BdmvInfo(args),
    "sbi-make" => SbiMake(args),
    "sbi-info" => SbiInfo(args),
    "psx-build" => PsxBuild(args),
    "chd-extract" => ChdExtract(args),
    "chd-extract-hd" => ChdExtractHd(args),
    "chd-create" => ChdCreate(args),
    "cheat-decode" => CheatDecode(args),
    "cheat-encode" => CheatEncode(args),
    "cheat-apply-nes" => CheatApplyNes(args),
    _ => Fail($"Unknown or not-yet-implemented command '{args[0]}'."),
};

static int Inspect(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge inspect <image.cdi>");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");

    using var fs = File.OpenRead(path);

    CdiTrailer trailer;
    try
    {
        trailer = CdiParser.ReadTrailer(fs);
    }
    catch (CdiFormatException ex)
    {
        return Fail(ex.Message);
    }

    Console.WriteLine($"File:       {Path.GetFileName(path)} ({fs.Length:N0} bytes)");
    Console.WriteLine($"Format:     CDI {VersionLabel(trailer.Version)}");
    Console.WriteLine($"Descriptor: offset {trailer.DescriptorOffset:N0}, " +
                      $"{fs.Length - trailer.DescriptorOffset:N0} bytes");

    try
    {
        var image = CdiParser.Parse(fs);
        Console.WriteLine($"Sessions:   {image.Sessions.Count}   Tracks: {image.TrackCount}");
        Console.WriteLine();
        Console.WriteLine("  #  Ses  Mode    Sector  Pregap  Length     LBA      File offset");
        Console.WriteLine("  -- ---  ------  ------  ------  ---------  -------  -----------");
        foreach (var t in image.AllTracks)
        {
            Console.WriteLine(
                $"  {t.Number,2} {t.SessionIndex + 1,3}  {t.Mode,-6}  {(int)t.SectorSize,6}  " +
                $"{t.PregapSectors,6}  {t.LengthSectors,9}  {t.StartLba,7}  {t.FileOffset,11:N0}");
        }
    }
    catch (CdiFormatException ex)
    {
        Console.WriteLine();
        Console.WriteLine($"Descriptor walk failed: {ex.Message}");
        Console.WriteLine("(Trailer parsed OK — this is likely a spec gap; " +
                          "please open an issue with the image version above.)");
        return 2;
    }

    return 0;
}

static int Extract(string[] args)
{
    // usage: dforge extract <image.cdi> <output-dir> [--raw]
    if (args.Length < 3)
        return Fail("usage: dforge extract <image.cdi> <output-dir> [--raw]");

    var path = args[1];
    var outDir = args[2];
    bool raw = args.Contains("--raw");
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    Directory.CreateDirectory(outDir);

    using var fs = File.OpenRead(path);
    CdiImage image;
    try { image = CdiParser.Parse(fs); }
    catch (CdiFormatException ex) { return Fail(ex.Message); }

    Console.WriteLine($"Extracting {image.TrackCount} track(s) from " +
                      $"{Path.GetFileName(path)} -> {outDir}");

    foreach (var t in image.AllTracks)
    {
        string ext = raw ? "bin"
            : t.Mode == CdiTrackMode.Audio ? "wav"
            : "iso";
        var outPath = Path.Combine(outDir, $"track{t.Number:D2}.{ext}");
        using var os = File.Create(outPath);

        long bytes;
        if (raw)
            bytes = CdiExtractor.ExtractRaw(fs, t, os);
        else if (t.Mode == CdiTrackMode.Audio)
        {
            CdiExtractor.ExtractAudioToWav(fs, t, os);
            bytes = os.Length;
        }
        else
            bytes = CdiExtractor.ExtractUserData(fs, t, os);

        Console.WriteLine(
            $"  track{t.Number:D2}  {t.Mode,-6} {(int)t.SectorSize}B  " +
            $"-> {Path.GetFileName(outPath)}  ({bytes:N0} bytes)");
    }

    Console.WriteLine("Done.");
    return 0;
}

static int Convert(string[] args)
{
    // usage: dforge convert <in.cdi> <out.cue>          (CDI -> BIN/CUE)
    //        dforge convert <in.cue> <out.cdi> [--version v2|v3|v35]
    //        dforge convert <in.mds> <out.cdi> [--version v2|v3|v35]   (Alcohol -> CDI)
    if (args.Length < 3)
        return Fail("usage: dforge convert <in.cdi> <out.cue> | <in.cue> <out.cdi> | " +
                    "<in.mds> <out.cdi> [--version v2|v3|v35]");

    var input = args[1];
    var output = args[2];
    if (!File.Exists(input)) return Fail($"File not found: {input}");

    var inExt = Path.GetExtension(input).ToLowerInvariant();
    var outExt = Path.GetExtension(output).ToLowerInvariant();

    if (inExt == ".cdi" && outExt == ".cue")
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var baseName = Path.GetFileNameWithoutExtension(output);
        using var fs = File.OpenRead(input);
        CdiImage image;
        try { image = CdiParser.Parse(fs); }
        catch (CdiFormatException ex) { return Fail(ex.Message); }

        var result = CdiConverter.CdiToBinCue(fs, image, outDir, baseName);
        foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
        Console.WriteLine($"Wrote {baseName}.cue and {result.BinFilenames.Count} BIN file(s) to {outDir}");
        return 0;
    }

    if (inExt == ".gdi" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        try
        {
            using var os = File.Create(output);
            GdiConverter.GdiToCdi(input, version, os);
            Console.WriteLine($"Converted {Path.GetFileName(input)} -> {Path.GetFileName(output)} " +
                              $"({VersionLabel(version)}).");
            return 0;
        }
        catch (Exception ex) when (ex is GdiFormatException or FileNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    if (inExt == ".cdi" && outExt == ".gdi")
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var baseName = Path.GetFileNameWithoutExtension(output);
        using var fs = File.OpenRead(input);
        CdiImage image;
        try { image = CdiParser.Parse(fs); }
        catch (CdiFormatException ex) { return Fail(ex.Message); }

        var result = GdiConverter.CdiToGdi(fs, image, outDir, baseName);
        foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
        Console.WriteLine($"Wrote {baseName}.gdi and {result.TrackFiles.Count} track file(s) to {outDir}");
        return 0;
    }

    if (inExt == ".cdi" && outExt == ".nrg")
    {
        using var fs = File.OpenRead(input);
        CdiImage image;
        try { image = CdiParser.Parse(fs); }
        catch (CdiFormatException ex) { return Fail(ex.Message); }
        using var os = File.Create(output);
        NrgConverter.CdiToNrg(fs, image, os);
        Console.WriteLine($"Converted {Path.GetFileName(input)} -> {Path.GetFileName(output)} (Nero NRG v2).");
        return 0;
    }

    if (inExt == ".nrg" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        using var fs = File.OpenRead(input);
        NrgImage image;
        try { image = NrgParser.Parse(fs); }
        catch (NrgFormatException ex) { return Fail(ex.Message); }
        using var os = File.Create(output);
        NrgConverter.NrgToCdi(fs, image, version, os);
        Console.WriteLine($"Converted {Path.GetFileName(input)} -> {Path.GetFileName(output)} ({VersionLabel(version)}).");
        return 0;
    }

    if (inExt == ".iso" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        try
        {
            using var os = File.Create(output);
            var r = IsoConverter.IsoToCdi(input, version, os);
            foreach (var w in r.Warnings) Console.WriteLine($"warning: {w}");
            Console.WriteLine(
                $"Wrapped {Path.GetFileName(input)} -> {Path.GetFileName(output)} " +
                $"({VersionLabel(version)}): {r.Sectors:N0} sectors, {r.BytesWritten:N0} bytes.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    if (inExt == ".cdi" && outExt == ".iso")
    {
        using var fs = File.OpenRead(input);
        CdiImage image;
        try { image = CdiParser.Parse(fs); }
        catch (CdiFormatException ex) { return Fail(ex.Message); }

        try
        {
            using var os = File.Create(output);
            var r = IsoConverter.CdiToIso(fs, image, os);
            foreach (var w in r.Warnings) Console.WriteLine($"warning: {w}");
            Console.WriteLine(
                $"Extracted {Path.GetFileName(input)} -> {Path.GetFileName(output)}: " +
                $"{r.Sectors:N0} sectors, {r.BytesWritten:N0} bytes.");
            return 0;
        }
        catch (InvalidDataException ex) { return Fail(ex.Message); }
    }

    if (inExt == ".mds" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        MdsImage mds;
        try { mds = MdsParser.Parse(File.ReadAllBytes(input)); }
        catch (MdsFormatException ex) { return Fail(ex.Message); }

        // The .mdf sits beside the .mds by convention; allow an explicit override.
        int mi = Array.IndexOf(args, "--mdf");
        var mdfPath = (mi >= 0 && mi + 1 < args.Length)
            ? args[mi + 1]
            : MdsConverter.DefaultMdfPath(input);

        try
        {
            using var os = File.Create(output);
            var result = MdsConverter.MdsToCdi(mds, mdfPath, version, os);
            foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
            Console.WriteLine(
                $"Converted {Path.GetFileName(input)} ({mds.Medium}, {result.TrackCount} track(s)) " +
                $"-> {Path.GetFileName(output)} ({VersionLabel(version)}), {result.CdiBytes:N0} bytes.");
            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            return Fail(ex.Message);
        }
    }

    if (inExt == ".cue" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        var cueText = File.ReadAllText(input);
        var cueDir = Path.GetDirectoryName(Path.GetFullPath(input))!;
        using var os = File.Create(output);
        try { CdiConverter.BinCueToCdi(cueText, cueDir, version, os); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        { return Fail(ex.Message); }
        Console.WriteLine($"Wrote {Path.GetFileName(output)} ({VersionLabel(version)})");
        return 0;
    }

    return Fail("convert needs .cdi->.cue or .cue->.cdi (by file extension).");
}

static int DiscConvert(string[] args)
{
    // usage: dforge disc-convert <in> <out>
    // The universal hub: reads <in> into a canonical disc model and writes <out>
    // from it, so any supported input converts to any supported output through one path.
    if (args.Length < 3)
        return Fail("usage: dforge disc-convert <in> <out>  " +
                    "(in: .cue .bin .chd .iso .cso .zso .wbfs .cdi .nrg .mds .gdi .ccd; " +
                    "out: .cue .chd .iso .cdi .nrg)");

    var input = args[1];
    var output = args[2];
    try
    {
        var model = DiscForge.Core.Convert.DiscConverter.Read(input);
        DiscForge.Core.Convert.DiscConverter.Write(model, output);

        long totalBytes = model.Tracks.Sum(t => (long)t.Data.Length);
        Console.WriteLine(
            $"{Path.GetExtension(input).TrimStart('.').ToUpperInvariant()} -> " +
            $"{Path.GetExtension(output).TrimStart('.').ToUpperInvariant()}: " +
            $"{model.Tracks.Count} track(s), {totalBytes:N0} bytes.");
        foreach (var t in model.Tracks)
            Console.WriteLine(
                $"  track {t.Number:D2}  {t.Type,-11} {t.SectorSize}B  " +
                $"{t.SectorCount:N0} sectors" + (t.PregapSectors > 0 ? $"  (+{t.PregapSectors} pregap)" : ""));
        Console.WriteLine($"Wrote {Path.GetFileName(output)}.");
        return 0;
    }
    catch (DiscForge.Core.Convert.DiscConvertException ex) { return Fail(ex.Message); }
}

static int Verify(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge verify <image.cdi> [--checksums]");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    bool userChecksums = args.Contains("--checksums");

    using var fs = File.OpenRead(path);
    CdiImage image;
    try { image = CdiParser.Parse(fs); }
    catch (CdiFormatException ex) { return Fail(ex.Message); }

    var report = CdiVerifier.Verify(fs, image, userChecksums);

    Console.WriteLine($"{Path.GetFileName(path)}: {VersionLabel(image.Version)}, " +
                      $"{image.Sessions.Count} session(s), {image.TrackCount} track(s)");
    Console.WriteLine();
    Console.WriteLine("  #   stored bytes   stored CRC32" + (userChecksums ? "   user CRC32" : ""));
    Console.WriteLine("  --  -------------  ------------" + (userChecksums ? "  -----------" : ""));
    foreach (var c in report.Checksums)
    {
        var line = $"  {c.TrackNumber,2}  {c.StoredBytes,13:N0}  {c.StoredCrc32:X8}";
        if (userChecksums && c.UserCrc32 is { } u) line += $"     {u:X8}";
        Console.WriteLine(line);
    }

    if (report.Issues.Count > 0)
    {
        Console.WriteLine();
        foreach (var issue in report.Issues)
            Console.WriteLine($"  [{issue.Severity}] {issue.Message}");
    }

    Console.WriteLine();
    Console.WriteLine(report.Passed
        ? (report.HasWarnings ? "PASS (with warnings)" : "PASS")
        : "FAIL");
    return report.Passed ? 0 : 2;
}

static int Create(string[] args)
{
    // usage: dforge create <dir> <out.cdi> [--volume NAME] [--version v2|v3|v35] [--rock-ridge]
    if (args.Length < 3)
        return Fail("usage: dforge create <dir> <out.cdi> [--volume NAME] [--version v2|v3|v35] [--rock-ridge]");

    var dir = args[1];
    var outPath = args[2];
    if (!Directory.Exists(dir)) return Fail($"Directory not found: {dir}");

    int vi = Array.IndexOf(args, "--volume");
    string volume = (vi >= 0 && vi + 1 < args.Length) ? args[vi + 1] : "OPENJUGGLER";
    var version = ParseVersionArg(args) ?? CdiVersion.V35;
    bool rockRidge = args.Contains("--rock-ridge");

    using var os = File.Create(outPath);
    var result = CdiCreator.CreateFromDirectory(volume, dir, version, os, rockRidge);

    foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
    Console.WriteLine(
        $"Created {Path.GetFileName(outPath)} ({VersionLabel(version)}): " +
        $"{result.IsoSectors} ISO sectors, {result.CdiBytes:N0} bytes.");
    return 0;
}

static int Compare(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge compare <a.cdi> <b.cdi> [--no-content]");
    var pathA = args[1];
    var pathB = args[2];
    if (!File.Exists(pathA)) return Fail($"File not found: {pathA}");
    if (!File.Exists(pathB)) return Fail($"File not found: {pathB}");
    bool content = !args.Contains("--no-content");

    using var fa = File.OpenRead(pathA);
    using var fb = File.OpenRead(pathB);
    CdiImage ia, ib;
    try { ia = CdiParser.Parse(fa); ib = CdiParser.Parse(fb); }
    catch (CdiFormatException ex) { return Fail(ex.Message); }

    var report = CdiComparer.Compare(fa, ia, fb, ib, content);

    Console.WriteLine($"A: {Path.GetFileName(pathA)}   B: {Path.GetFileName(pathB)}");
    if (report.Equal)
    {
        Console.WriteLine("\nIMAGES ARE EQUIVALENT" + (content ? " (structure + content)" : " (structure)"));
        return 0;
    }

    Console.WriteLine();
    foreach (var s in report.StructuralDifferences)
        Console.WriteLine($"  [structure] {s}");
    foreach (var t in report.TrackDifferences)
        Console.WriteLine($"  [track {t.TrackNumber}] {t.Field}: {t.ValueA} vs {t.ValueB}");
    foreach (var n in report.ContentMismatchTracks)
        Console.WriteLine($"  [track {n}] content differs (CRC-32 mismatch)");

    Console.WriteLine("\nIMAGES DIFFER");
    return 1;
}

static int CreateAudio(string[] args)
{
    // usage: dforge create-audio <out.cdi> <track1.wav> [track2.wav ...] [--gapless] [--74]
    if (args.Length < 3)
        return Fail("usage: dforge create-audio <out.cdi> <track1.wav> [track2.wav ...] " +
                    "[--gapless] [--74] [--postgap [sectors]] [--version v2|v3|v35]");

    var outPath = args[1];
    bool gapless = args.Contains("--gapless");
    bool only74 = args.Contains("--74");

    // Some third-party images omit the post-gap the standard expects before the
    // lead-out; --postgap adds the customary two seconds to the last track.
    int pg = Array.IndexOf(args, "--postgap");
    uint postgap = pg >= 0
        ? (pg + 1 < args.Length && uint.TryParse(args[pg + 1], out var v) ? v : 150u)
        : 0u;
    var version = ParseVersionArg(args) ?? CdiVersion.V35;

    // Collect the WAV paths, skipping flags AND any value a flag consumes —
    // otherwise `--postgap 150` would treat "150" as a filename.
    var wavs = new List<string>();
    for (int i = 2; i < args.Length; i++)
    {
        var a = args[i];
        if (a.StartsWith("--"))
        {
            // These flags take a value; step over it.
            if (a is "--postgap" or "--version")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) i++;
            }
            continue;
        }
        wavs.Add(a);
    }

    if (wavs.Count == 0) return Fail("Give at least one .wav file.");

    var tracks = wavs.Select((p, i) => new AudioTrackSource
    {
        Path = p,
        // Track 1's lead-in gap is mandatory and enforced by the creator.
        PregapSectors = (gapless && i > 0) ? 0u : 150u,
        // A post-gap belongs before the lead-out, i.e. on the last track only.
        PostgapSectors = (i == wavs.Count - 1) ? postgap : 0u,
    }).ToList();

    try
    {
        using var os = File.Create(outPath);
        var result = AudioCdCreator.Create(tracks, version, os, allow80Minute: !only74);

        foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
        Console.WriteLine(
            $"Created {Path.GetFileName(outPath)} ({VersionLabel(version)}): " +
            $"{result.TrackCount} audio track(s), {result.Duration:hh\\:mm\\:ss}, " +
            $"{result.TotalSectors:N0} sectors, {result.CdiBytes:N0} bytes.");
        return 0;
    }
    catch (Exception ex) when (ex is AudioCdException or WavFormatException or FileNotFoundException)
    {
        return Fail(ex.Message);
    }
}

static (CdiImage Image, CdiTrack Track, FileStream Stream)? OpenDataTrack(string path)
{
    var fs = File.OpenRead(path);
    CdiImage image;
    try { image = CdiParser.Parse(fs); }
    catch (CdiFormatException ex) { fs.Dispose(); Console.Error.WriteLine("error: " + ex.Message); return null; }

    // The filesystem lives in the first data track.
    var track = image.AllTracks.FirstOrDefault(t => t.Mode != CdiTrackMode.Audio);
    if (track is null)
    {
        fs.Dispose();
        Console.Error.WriteLine("error: this image has no data track (audio-only disc?).");
        return null;
    }
    return (image, track, fs);
}

static IsoReader.NamePreference PreferenceFrom(string[] args) =>
    args.Contains("--iso") ? IsoReader.NamePreference.Iso9660
    : args.Contains("--joliet") ? IsoReader.NamePreference.Joliet
    : IsoReader.NamePreference.Auto;

static int Ls(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ls <image.cdi> [--iso|--joliet]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var opened = OpenDataTrack(args[1]);
    if (opened is null) return 2;
    var (_, track, fs) = opened.Value;
    using (fs)
    {
        using var view = new CdiUserDataStream(fs, track);

        // A disc may carry ISO 9660, UDF, or both (a "UDF bridge"). Prefer what
        // the caller asked for; otherwise use whichever is actually there.
        bool wantUdf = args.Contains("--udf");
        bool wantIso = args.Contains("--iso") || args.Contains("--joliet");

        if (!wantIso && (wantUdf || !HasIso9660(view)))
        {
            if (!UdfReader.IsUdf(view))
                return Fail(wantUdf
                    ? "This image has no UDF filesystem."
                    : "This image has neither an ISO 9660 nor a UDF filesystem.");

            UdfVolume vol;
            try { vol = UdfReader.Read(view); }
            catch (UdfFormatException ex) { return Fail(ex.Message); }

            Console.WriteLine($"Volume: {vol.VolumeId}   Filesystem: UDF");
            Console.WriteLine();
            foreach (var e in vol.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
                Console.WriteLine(e.IsDirectory
                    ? $"  {"<DIR>",12}  {e.Path}"
                    : $"  {e.Size,12:N0}  {e.Path}");
            Console.WriteLine();
            Console.WriteLine($"{vol.Files.Count():N0} file(s), {vol.Directories.Count():N0} directory(ies), " +
                              $"{vol.TotalBytes:N0} bytes.");
            return 0;
        }

        IsoDirectory dir;
        try { dir = IsoReader.Read(view, PreferenceFrom(args)); }
        catch (IsoFormatException ex) { return Fail(ex.Message); }

        var names = dir.Joliet ? "Joliet" : dir.RockRidge ? "ISO 9660 + Rock Ridge" : "ISO 9660";
        Console.WriteLine($"Volume: {dir.VolumeId}   Names: {names}");
        if (UdfReader.IsUdf(view))
            Console.WriteLine("(This disc also has a UDF filesystem — use --udf to read that instead.)");
        Console.WriteLine();

        foreach (var e in dir.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
            Console.WriteLine(e.IsDirectory
                ? $"  {"<DIR>",12}  {e.Path}"
                : $"  {e.Size,12:N0}  {e.Path}");

        Console.WriteLine();
        Console.WriteLine($"{dir.Files.Count():N0} file(s), {dir.Directories.Count():N0} directory(ies), " +
                          $"{dir.TotalBytes:N0} bytes.");
        return 0;
    }
}

/// <summary>
/// Map an in-image path to an output path, refusing anything that escapes the
/// target directory. Paths come from the image and are untrusted input.
/// </summary>
static string? SafeTarget(string outDir, string imagePath)
{
    var relative = imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
    var target = Path.GetFullPath(Path.Combine(outDir, relative));
    var root = Path.GetFullPath(outDir);
    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"warning: skipping '{imagePath}' — it escapes the output directory.");
        return null;
    }
    return target;
}

/// <summary>True if the stream carries an ISO 9660 primary volume descriptor.</summary>
static bool HasIso9660(Stream image)
{
    try
    {
        image.Seek(16L * 2048, SeekOrigin.Begin);
        var b = new byte[6];
        if (image.Read(b, 0, 6) < 6) return false;
        return b[1] == 'C' && b[2] == 'D' && b[3] == '0' && b[4] == '0' && b[5] == '1';
    }
    catch { return false; }
}

static int ExtractFiles(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge extract-files <image.cdi> <output-dir> [--iso|--joliet]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var outDir = args[2];
    var opened = OpenDataTrack(args[1]);
    if (opened is null) return 2;
    var (_, track, fs) = opened.Value;
    using (fs)
    {
        using var view = new CdiUserDataStream(fs, track);

        bool wantUdf = args.Contains("--udf");
        bool wantIso = args.Contains("--iso") || args.Contains("--joliet");

        // UDF path: same auto-detection as `ls`.
        if (!wantIso && (wantUdf || !HasIso9660(view)))
        {
            if (!UdfReader.IsUdf(view))
                return Fail("This image has neither an ISO 9660 nor a UDF filesystem.");

            UdfVolume vol;
            try { vol = UdfReader.Read(view); }
            catch (UdfFormatException ex) { return Fail(ex.Message); }

            Directory.CreateDirectory(outDir);
            int n = 0; long total = 0;
            foreach (var e in vol.Files)
            {
                var target = SafeTarget(outDir, e.Path);
                if (target is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var os = File.Create(target);
                UdfReader.ExtractFile(view, vol, e, os);
                n++; total += e.Size;
                Console.WriteLine($"  {e.Size,12:N0}  {e.Path}");
            }
            Console.WriteLine();
            Console.WriteLine($"Extracted {n:N0} UDF file(s), {total:N0} bytes to {Path.GetFullPath(outDir)}");
            return 0;
        }

        IsoDirectory dir;
        try { dir = IsoReader.Read(view, PreferenceFrom(args)); }
        catch (IsoFormatException ex) { return Fail(ex.Message); }

        Directory.CreateDirectory(outDir);
        int count = 0;
        long bytes = 0;

        foreach (var e in dir.Files)
        {
            var target = SafeTarget(outDir, e.Path);
            if (target is null) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var os = File.Create(target);
            IsoReader.ExtractFile(view, e, os);
            count++;
            bytes += e.Size;
            Console.WriteLine($"  {e.Size,12:N0}  {e.Path}");
        }

        Console.WriteLine();
        Console.WriteLine($"Extracted {count:N0} file(s), {bytes:N0} bytes to {Path.GetFullPath(outDir)}");
        return 0;
    }
}

static CdiVersion? ParseVersionArg(string[] args)
{
    int i = Array.IndexOf(args, "--version");
    if (i < 0 || i + 1 >= args.Length) return null;
    return args[i + 1].ToLowerInvariant() switch
    {
        "v2" => CdiVersion.V2,
        "v3" => CdiVersion.V3,
        "v35" or "v3.5" => CdiVersion.V35,
        _ => null,
    };
}

static string VersionLabel(CdiVersion v) => v switch
{
    CdiVersion.V2 => "v2 (DiscJuggler 2.x)",
    CdiVersion.V3 => "v3 (DiscJuggler 3.x)",
    CdiVersion.V35 => "v3.5/4 (DiscJuggler 3.5+)",
    _ => "unknown",
};

static int BuildRaw(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge build-raw <src.cue|src.cdi> <out.img> [--subcode pq|cooked|raw]");

    var src = args[1];
    var outPath = args[2];
    bool verbatim = args.Contains("--verbatim");
    var form = RawSubcodeForm.Packed96;
    for (int i = 3; i < args.Length - 1; i++)
        if (args[i] == "--subcode")
            form = args[i + 1].ToLowerInvariant() switch
            {
                "pq" => RawSubcodeForm.Pq16,
                "cooked" or "packed" => RawSubcodeForm.Packed96,
                "raw" or "interleaved" => RawSubcodeForm.Interleaved96,
                _ => throw new ArgumentException($"Unknown subcode form '{args[i + 1]}'."),
            };

    try
    {
        using Stream? cdiStream = src.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)
            ? null : File.OpenRead(src);
        DiscLayout layout = cdiStream is null
            ? DiscLayout.FromCueFile(src, subVerbatim: verbatim)
            : DiscLayout.FromCdi(CdiParser.Parse(cdiStream), cdiStream);

        if (verbatim && !layout.HasVerbatimSubchannel && !layout.HasProgramRw)
            Console.WriteLine("  note: --verbatim set but no .sub sidecar was found; " +
                              "writing DiscForge's own sub-channel.");
        using (layout)
        {
            long total = RawImageGenerator.TotalSectors(layout);
            int size = RawImageGenerator.SectorSize(form);
            Console.WriteLine($"Layout: {layout.Tracks.Count} track(s), " +
                              $"{RawImageGenerator.ProgramSectors(layout):N0} program sectors" +
                              (layout.Mcn is not null ? ", MCN" : "") +
                              (!layout.CdText.IsEmpty ? ", CD-TEXT" : ""));
            foreach (var t in layout.Tracks)
                Console.WriteLine($"  track {t.Number:D2} {t.Mode,-6} pregap {t.PregapTotalSectors} " +
                                  $"({t.PregapStoredSectors} stored) length {t.LengthSectors}" +
                                  (t.PostgapSectors > 0 ? $" postgap {t.PostgapSectors}" : "") +
                                  (t.ExtraIndexes.Count > 0 ? $" +{t.ExtraIndexes.Count} index(es)" : "") +
                                  (t.Isrc is not null ? $" ISRC {t.Isrc}" : ""));
            Console.WriteLine($"Image: {total:N0} sectors x {size} = " +
                              $"{total * size / (1024.0 * 1024.0):N1} MB ({form})");

            using var output = File.Create(outPath);
            long lastPct = -1;
            RawImageGenerator.Generate(layout, form, output, new Progress<double>(f =>
            {
                long pct = (long)(f * 100);
                if (pct != lastPct && pct % 10 == 0)
                {
                    lastPct = pct;
                    Console.Write($"\r  composing… {pct}%");
                }
            }));
            Console.WriteLine("\r  composing… done");
        }
        Console.WriteLine($"Wrote {outPath}");
        return 0;
    }
    catch (Exception ex)
    {
        return Fail(ex.Message);
    }
}

static int Subch(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge subch <file.sub>");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
    try
    {
        using var fs = File.OpenRead(args[1]);
        if (fs.Length % 96 != 0)
            return Fail($"'{args[1]}' is {fs.Length:N0} bytes — not a whole number of " +
                        "96-byte sub-channel frames. Wrong file, or a different format.");
        var a = RawSubchannel.Analyse(fs);
        Console.WriteLine($"Frames:      {a.Frames:N0}");
        Console.WriteLine($"Q valid:     {a.QValid:N0}");
        Console.WriteLine($"Q invalid:   {a.QInvalid:N0}");
        if (a.QInvalid > 0)
        {
            var shown = a.InvalidLbas.Take(24).Select(l => l.ToString());
            Console.WriteLine($"  at LBA:    {string.Join(", ", shown)}" +
                (a.InvalidLbas.Count > 24 ? ", …" : ""));
        }
        Console.WriteLine();
        Console.WriteLine(a.Summary);
        if (a.QInvalid > 0)
            Console.WriteLine(a.Paired
                ? $"Invalid frames are predominantly paired ({a.PairedInvalid}/{a.QInvalid}) — the LibCrypt shape."
                : $"Invalid frames are mostly scattered ({a.PairedInvalid}/{a.QInvalid} paired) — reads more like damage.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BinCueMergeCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge bincue-merge <in.cue> <out.bin> [out.cue]");
    string inCue = args[1];
    string outBin = args[2];
    string outCue = args.Length > 3 ? args[3] : Path.ChangeExtension(outBin, ".cue");
    if (!File.Exists(inCue)) return Fail($"'{inCue}' not found.");
    try
    {
        var r = BinCueMerge.Merge(inCue, outBin, outCue);
        Console.WriteLine($"Merged {r.Tracks} track(s) into {Path.GetFileName(r.BinPath)} " +
                          $"({r.Bytes:N0} bytes) with {Path.GetFileName(r.CuePath)}.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BinCueSplitCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge bincue-split <in.cue> [out-dir] [base-name] [out.cue]");
    string inCue = args[1];
    if (!File.Exists(inCue)) return Fail($"'{inCue}' not found.");
    string outDir = args.Length > 2 ? args[2] : (Path.GetDirectoryName(Path.GetFullPath(inCue)) ?? ".");
    string baseName = args.Length > 3 ? args[3] : Path.GetFileNameWithoutExtension(inCue);
    string outCue = args.Length > 4 ? args[4] : Path.Combine(outDir, baseName + " (split).cue");
    try
    {
        var r = BinCueMerge.Split(inCue, outDir, baseName, outCue);
        Console.WriteLine($"Split into {r.Tracks} file(s) in {outDir}, cue {Path.GetFileName(r.CuePath)}:");
        foreach (var b in r.BinPaths) Console.WriteLine($"  {b}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SbiMake(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge sbi-make <disc.sub> [out.sbi] [--start-lba N]");
    string subPath = args[1];
    if (!File.Exists(subPath)) return Fail($"'{subPath}' not found.");
    string outSbi = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : Path.ChangeExtension(subPath, ".sbi");
    uint startLba = 0;
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--start-lba") uint.TryParse(args[i + 1], out startLba);
    try
    {
        var sub = File.ReadAllBytes(subPath);
        if (sub.Length % RawSubchannel.FrameSize != 0)
            return Fail($"'{subPath}' is {sub.Length:N0} bytes — not a whole number of 96-byte frames.");
        var doc = Sbi.FromSubchannel(sub, startLba);
        if (doc.IsEmpty)
        {
            Console.WriteLine("No LibCrypt subchannel found — nothing to write (the .sub already preserves everything).");
            return 0;
        }
        File.WriteAllBytes(outSbi, Sbi.Write(doc));
        Console.WriteLine($"Wrote {Path.GetFileName(outSbi)} with {doc.Entries.Count} LibCrypt entry(ies).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PsxBuild(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge psx-build <folder> <out.bin> [volume-id] [out.cue]");
    string folder = args[1];
    string outBin = args[2];
    string volId = args.Length > 3 ? args[3] : "PSX";
    string outCue = args.Length > 4 ? args[4] : Path.ChangeExtension(outBin, ".cue");
    if (!Directory.Exists(folder)) return Fail($"Folder not found: {folder}");
    try
    {
        int sectors = PsxImageBuilder.BuildFromFolder(folder, volId, outBin, outCue);
        Console.WriteLine($"Built {Path.GetFileName(outBin)} ({sectors:N0} Mode 2/2352 sectors, " +
                          $"{(long)sectors * 2352:N0} bytes) + {Path.GetFileName(outCue)}.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SbiInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge sbi-info <file.sbi>");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
    try
    {
        var doc = Sbi.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine(Sbi.Describe(doc));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdgPreview(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cdg-preview <raw-image|src.cue> [--seconds N] [--out shot.ppm]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");

    int seconds = 30;
    string? outPath = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
        else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
    }

    try
    {
        var decoder = new CdgDecoder();
        long sectorsFed = 0;
        long limit = seconds * 75L;
        var rw = new byte[96];

        if (args[1].EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        {
            // Straight from the .sub sidecar — tests the SOURCE.
            using var layout = DiscLayout.FromCueFile(args[1]);
            if (!layout.HasProgramRw)
                return Fail("This CUE has no .sub sidecar next to its BIN — no CD+G to decode.");
            foreach (var t in layout.Tracks)
            {
                if (t.SubSource is null) continue;
                long stored = t.PregapStoredSectors + t.LengthSectors;
                for (long s = 0; s < stored && sectorsFed < limit; s++, sectorsFed++)
                {
                    t.SubSource.Seek(t.SubByteOffset + s * 96, SeekOrigin.Begin);
                    t.SubSource.ReadExactly(rw, 0, 96);
                    for (int i = 0; i < 96; i++) rw[i] &= 0x3F;   // symbols only
                    decoder.FeedSector(rw);
                }
            }
        }
        else
        {
            // From a raw image — tests what would be (or was) ON THE DISC.
            using var fs = File.OpenRead(args[1]);
            var (size, form) = RawImageInspector.DetectLayout(fs);
            if (form is null or RawSubcodeForm.Pq16)
                return Fail($"This image carries no R-W symbols " +
                            $"({(form is null ? "no subcode" : "PQ-16")}) — no CD+G possible.");
            long leadIn = RawImageInspector.FindLeadInLength(fs, size, form.Value);
            long total = fs.Length / size;
            var sub = new byte[size - 2352];
            for (long s = leadIn; s < total && sectorsFed < limit; s++, sectorsFed++)
            {
                fs.Position = s * size + 2352;
                fs.ReadExactly(sub, 0, sub.Length);
                SubcodeFrame.ExtractRw(sub, form.Value, rw);
                decoder.FeedSector(rw);
            }
        }

        Console.WriteLine($"Decoded {sectorsFed:N0} sector(s) ({sectorsFed / 75.0:N1} s of playback):");
        Console.WriteLine($"  packets seen      {decoder.PacketsSeen:N0}");
        Console.WriteLine($"  graphics packets  {decoder.GraphicsPackets:N0}");
        Console.WriteLine($"  tiles drawn       {decoder.TileCount:N0}");
        Console.WriteLine($"  screen presets    {decoder.PresetCount:N0}");
        Console.WriteLine($"  palette loads     {decoder.PaletteLoads:N0}");
        if (decoder.GraphicsPackets == 0)
            Console.WriteLine("  (no CD+G content found in this range)");

        if (outPath is not null)
        {
            File.WriteAllBytes(outPath, decoder.ToPpm());
            Console.WriteLine($"Wrote screenshot: {outPath} (PPM; opens in IrfanView/GIMP/ffmpeg)");
        }
        return decoder.GraphicsPackets > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdgRender(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cdg-render <file.cdg> [--at MM:SS] [--out <file.png>]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");

    TimeSpan? at = null;
    string? outPath = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--at" && i + 1 < args.Length)
        {
            var parts = args[++i].Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int mm) || !int.TryParse(parts[1], out int ss))
                return Fail("--at expects MM:SS");
            at = new TimeSpan(0, mm, ss);
        }
        else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
    }

    try
    {
        var cdg = File.ReadAllBytes(args[1]);
        DiscForge.Core.Cdg.CdgImage image = at is { } t
            ? DiscForge.Core.Cdg.CdgRenderer.RenderFrameAt(cdg, t)
            : DiscForge.Core.Cdg.CdgRenderer.RenderFinalFrame(cdg);
        var png = DiscForge.Core.Cdg.CdgRenderer.RenderToPng(image);

        outPath ??= Path.ChangeExtension(args[1], ".png");
        File.WriteAllBytes(outPath, png);
        Console.WriteLine($"Rendered {image.Width}x{image.Height} frame " +
                          $"{(at is { } tt ? $"at {tt:mm\\:ss}" : "(end of stream)")} -> {outPath}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdgExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge cdg-extract <subchannel-sidecar> <out.cdg>");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");

    try
    {
        using var sub = File.OpenRead(args[1]);
        var cdg = DiscForge.Core.Cdg.CdgExtractor.Extract(sub);
        File.WriteAllBytes(args[2], cdg);
        Console.WriteLine($"Extracted {cdg.Length:N0} byte(s) " +
                          $"({cdg.Length / 24:N0} packet(s)) -> {args[2]}");
        return cdg.Length > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ViewSector(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge view-sector <image> <lba|mm:ss:ff|+index> [--count N] [--descramble]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
    int count = 1;
    for (int i = 3; i < args.Length - 1; i++)
        if (args[i] == "--count") count = Math.Clamp(int.Parse(args[i + 1]), 1, 64);
    bool descramble = args.Contains("--descramble");

    try
    {
        using var access = SectorAccess.Open(args[1]);
        long start = access.Resolve(args[2]);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {access.Kind}, " +
                          $"{access.TotalSectors:N0} sectors");

        for (long s = start; s < start + count && s < access.TotalSectors; s++)
        {
            var sec = access.Read(s);
            bool raw2352 = sec.Stored.Length == 2352;
            var data = sec.Stored;

            // Describe what we're looking at (and optionally descramble).
            string desc = "";
            if (raw2352)
            {
                bool hasSync = data[0] == 0 && data[1] == 0xFF && data[11] == 0;
                if (!hasSync) desc = "audio (no sync)";
                else
                {
                    var copy = (byte[])data.Clone();
                    CdScrambler.ScrambleInPlace(copy);
                    bool scrambled =
                        copy[15] is 1 or 2 &&
                        (copy[15] != 1 || EdcEcc.VerifyMode1(copy).EdcOk);
                    bool plain =
                        data[15] is 1 or 2 &&
                        (data[15] != 1 || EdcEcc.VerifyMode1(data).EdcOk);
                    if (scrambled && !plain)
                    {
                        desc = $"Mode {copy[15]}, scrambled";
                        if (descramble) { data = copy; desc += " (shown descrambled)"; }
                    }
                    else desc = $"Mode {data[15]}, unscrambled";
                    if (data == sec.Stored && data[15] == 1 && plain)
                    {
                        var (e, c) = EdcEcc.VerifyMode1(data);
                        desc += $"; EDC {(e ? "OK" : "BAD")}, ECC {(c ? "OK" : "BAD")}";
                    }
                    else if (descramble && data[15] == 1)
                    {
                        var (e, c) = EdcEcc.VerifyMode1(data);
                        desc += $"; EDC {(e ? "OK" : "BAD")}, ECC {(c ? "OK" : "BAD")}";
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"── sector +{sec.FileIndex}" +
                (sec.LeadIn ? " (lead-in)" : sec.Lba == long.MinValue ? "" : $"  LBA {sec.Lba}") +
                $"  MSF {sec.Msf}" +
                (sec.Track is { } t ? $"  track {t:D2}" +
                    (sec.Session is { } ss ? $" (session {ss})" : "") : "") +
                $"  [{sec.Stored.Length} bytes]" +
                (desc.Length > 0 ? $"  {desc}" : ""));

            for (int off = 0; off < data.Length; off += 16)
            {
                var row = data.AsSpan(off, Math.Min(16, data.Length - off));
                var hex = string.Join(" ", row.ToArray().Select(b => b.ToString("x2")));
                var ascii = new string(row.ToArray()
                    .Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
                Console.WriteLine($"  {off,5:X4}  {hex,-47}  {ascii,-16}  {Region(off, data.Length, raw2352)}");
            }

            if (sec.Subcode is { } sub && sec.SubcodeForm is { } form)
            {
                var q = new byte[12];
                SubcodeFrame.ExtractQ(sub, form, q);
                bool crcOk = DiscForge.Core.Util.Crc16.ComputeInverted(q.AsSpan(0, 10))
                             == (ushort)((q[10] << 8) | q[11]);
                Console.WriteLine($"  Q: {string.Join(" ", q.Select(b => b.ToString("x2")))}  " +
                    $"(CRC {(crcOk ? "OK" : "BAD")}; ctrl/adr {q[0]:x2} TNO {q[1]:x2} IDX {q[2]:x2} " +
                    $"rel {q[3]:x2}:{q[4]:x2}:{q[5]:x2} abs {q[7]:x2}:{q[8]:x2}:{q[9]:x2})");
            }
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }

    static string Region(int off, int len, bool raw)
    {
        if (!raw || len != 2352) return "";
        return off switch
        {
            0x000 => "sync 000-00B, header 00C-00F",
            0x010 => "user data 010-80F",
            0x810 => "EDC 810-813, pad 814-81B, ECC P 81C-8C7",
            0x8C0 => "ECC Q 8C8-92F",
            _ => "",
        };
    }
}

static int ExtractSectors(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge extract-sectors <image> <out> --start <addr> --count N " +
                    "[--as stored|user|raw2352] [--byteswap]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");

    string? startArg = null;
    long count = -1;
    string mode = "stored";
    bool byteswap = args.Contains("--byteswap");
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--start" && i + 1 < args.Length) startArg = args[++i];
        else if (args[i] == "--count" && i + 1 < args.Length) count = long.Parse(args[++i]);
        else if (args[i] == "--as" && i + 1 < args.Length) mode = args[++i].ToLowerInvariant();
    }
    if (startArg is null || count <= 0)
        return Fail("Both --start and --count are required.");
    if (mode is not ("stored" or "user" or "raw2352"))
        return Fail($"Unknown --as '{mode}'. Use stored, user, or raw2352.");

    try
    {
        using var access = SectorAccess.Open(args[1]);
        long start = access.Resolve(startArg);
        long end = Math.Min(start + count, access.TotalSectors);
        if (start >= access.TotalSectors)
            return Fail($"Start sector is past the end of the image ({access.TotalSectors:N0} sectors).");

        using var output = File.Create(args[2]);
        var raw = new byte[2352];
        long written = 0;
        for (long s = start; s < end; s++)
        {
            var sec = access.Read(s);
            byte[] payload;
            switch (mode)
            {
                case "user":
                    payload = sec.Stored.Length switch
                    {
                        2048 => sec.Stored,
                        2336 => sec.Stored[8..2056],           // XA: skip subheader
                        2352 when HasSync(sec.Stored) =>
                            Unscrambled(sec.Stored)[16..2064],
                        2352 => sec.Stored,                    // audio: all of it
                        _ => sec.Stored,
                    };
                    break;
                case "raw2352":
                    switch (sec.Stored.Length)
                    {
                        case 2352:
                            payload = HasSync(sec.Stored) ? Unscrambled(sec.Stored) : sec.Stored;
                            break;
                        case 2048:
                            RawSectorBuilder.BuildMode1(sec.Stored, sec.Msf, raw);
                            payload = raw;
                            break;
                        case 2336:
                            RawSectorBuilder.BuildMode2(sec.Stored, sec.Msf, raw);
                            payload = raw;
                            break;
                        default:
                            payload = sec.Stored;
                            break;
                    }
                    break;
                default:
                    payload = sec.Stored;
                    break;
            }

            if (byteswap)
            {
                payload = (byte[])payload.Clone();
                for (int i = 0; i + 1 < payload.Length; i += 2)
                    (payload[i], payload[i + 1]) = (payload[i + 1], payload[i]);
            }
            output.Write(payload, 0, payload.Length);
            written += payload.Length;
        }
        Console.WriteLine($"Extracted {end - start:N0} sector(s), {written:N0} bytes " +
                          $"({mode}{(byteswap ? ", byteswapped" : "")}) to {args[2]}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }

    static bool HasSync(byte[] s)
    {
        if (s[0] != 0 || s[11] != 0) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    static byte[] Unscrambled(byte[] s)
    {
        // If it validates as-is, it's already clean; else try descrambled.
        if (s[15] == 1 && EdcEcc.VerifyMode1(s).EdcOk) return s;
        var copy = (byte[])s.Clone();
        CdScrambler.ScrambleInPlace(copy);
        if (copy[15] is 1 or 2 && (copy[15] != 1 || EdcEcc.VerifyMode1(copy).EdcOk)) return copy;
        return s;
    }
}

static int InspectRaw(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge inspect-raw <image> [--deep]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
    bool deep = args.Contains("--deep");

    try
    {
        using var fs = File.OpenRead(args[1]);
        var r = RawImageInspector.Inspect(fs, deep);

        Console.WriteLine($"File:        {Path.GetFileName(args[1])} ({fs.Length:N0} bytes)");
        Console.WriteLine($"Format:      {r.SectorSize} bytes/sector — " +
            (r.Form is null ? "main channel only (no subcode)" : r.Form.ToString()));
        Console.WriteLine($"Sectors:     {r.TotalSectors:N0}" +
            (r.HasLeadIn ? $" ({r.LeadInSectors:N0} lead-in + {r.TotalSectors - r.LeadInSectors:N0} program)"
                         : r.Form is null ? "" : " (no lead-in — program-only rip)"));
        if (r.Form is not null)
        {
            Console.WriteLine($"Q integrity: {r.QFramesChecked - r.QCrcErrors}/{r.QFramesChecked} " +
                $"frames CRC-valid{(deep ? "" : " (sampled)")}" +
                (r.QCrcErrors > 0 ? $"  <-- {r.QCrcErrors} BAD" : ""));
            if (r.LeadOutStartSector > 0)
            {
                var lo = Msf.FromSectors(r.LeadOutStartSector);
                Console.WriteLine($"Lead-out:    {lo} ({r.LeadOutStartSector:N0} sectors)");
            }
            if (r.Mcn is not null) Console.WriteLine($"MCN:         {r.Mcn}");
            if (r.AlbumTitle is not null || r.AlbumPerformer is not null)
            {
                Console.WriteLine($"CD-TEXT:     \"{r.AlbumTitle}\"" +
                    (r.AlbumPerformer is not null ? $" — {r.AlbumPerformer}" : "") +
                    $"  ({r.CdTextPacksValid} packs valid" +
                    (r.CdTextPacksBad > 0 ? $", {r.CdTextPacksBad} bad" : "") + ")");
                for (int i = 0; i < r.TrackTitles.Count; i++)
                    Console.WriteLine($"             {i + 1:D2}. {r.TrackTitles[i]}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  #  Type   Start MSF    ISRC          Data checks");
        Console.WriteLine("  -- -----  -----------  ------------  --------------------------------");
        foreach (var t in r.Tracks)
        {
            string kind = t.IsData ? $"Data{(t.Mode is { } m ? $"{m}" : "")}" : "Audio";
            string data = "";
            if (t.IsData)
            {
                data = t.Scrambled switch
                {
                    true => "scrambled",
                    false => "unscrambled",
                    null => "undetermined",
                };
                if (t.DataSectorsChecked > 0)
                    data += t.EdcErrors == 0 && t.EccErrors == 0
                        ? $"; {t.CheckKind} OK ({t.DataSectorsChecked} checked)"
                        : $"; {t.CheckKind}: EDC errs {t.EdcErrors}, ECC errs {t.EccErrors} " +
                          $"of {t.DataSectorsChecked}  <-- BAD";
                else if (t.CheckKind is not null)
                    data += $"; checks: {t.CheckKind}";
            }
            Console.WriteLine($"  {t.Number:D2} {kind,-6} {Msf.FromSectors(t.StartSector),-12} " +
                              $"{t.Isrc ?? "-",-13} {data}");
        }

        foreach (var n in r.Notes) Console.WriteLine($"note: {n}");

        bool clean = r.QCrcErrors == 0 &&
                     r.Tracks.All(t => t.EdcErrors == 0 && t.EccErrors == 0);
        Console.WriteLine();
        Console.WriteLine(clean ? "Result: clean." : "Result: problems found (see above).");
        return clean ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ScanProtection(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge scan-protection <image.cdi>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    // Collect the file listing (best-effort) so the scanner can spot marker
    // files, then run the sector/subchannel fingerprint scan.
    var fileNames = new List<string>();
    try
    {
        var opened = OpenDataTrack(args[1]);
        if (opened is not null)
        {
            var (_, track, fs) = opened.Value;
            using (fs)
            {
                using var view = new CdiUserDataStream(fs, track);
                if (HasIso9660(view))
                {
                    var dir = IsoReader.Read(view, IsoReader.NamePreference.Auto);
                    foreach (var e in dir.Entries.Where(x => !x.IsDirectory))
                        fileNames.Add(e.Path);
                }
            }
        }
    }
    catch { /* listing is best-effort; the sector scan still runs */ }

    try
    {
        using var access = SectorAccess.Open(args[1]);
        var report = ProtectionScanner.Scan(access, fileNames);

        Console.WriteLine($"{Path.GetFileName(args[1])}: {access.TotalSectors:N0} sectors");
        Console.WriteLine();
        if (!report.AnyProtection)
        {
            Console.WriteLine("No known copy-protection fingerprint detected.");
            Console.WriteLine("(A clean scan is not a guarantee; it means none of the");
            Console.WriteLine(" recognised schemes left a detectable signature.)");
            return 0;
        }

        Console.WriteLine("Copy-protection fingerprint(s) detected:");
        Console.WriteLine();
        foreach (var f in report.Findings)
        {
            Console.WriteLine($"  {f.Scheme}");
            Console.WriteLine($"    Evidence: {f.Evidence}");
            Console.WriteLine($"    Guidance: {f.Guidance}");
            if (f.SignificantLbas.Count > 0)
            {
                var preview = string.Join(", ", f.SignificantLbas.Take(8));
                Console.WriteLine($"    Sectors:  {preview}" +
                    (f.SignificantLbas.Count > 8 ? $"  (+{f.SignificantLbas.Count - 8} more)" : ""));
            }
            Console.WriteLine();
        }
        Console.WriteLine("DiscForge detects protection so a backup can PRESERVE it faithfully.");
        Console.WriteLine("It does not circumvent, strip, or defeat any protection.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int MountCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge mount <image>\n" +
                    "  Describes how the image would mount as a virtual drive and, for\n" +
                    "  ISO-compatible images, prints the native Windows mount command\n" +
                    "  (no driver needed). Rich formats (audio/subchannel/multi-track)\n" +
                    "  report that a virtual-drive driver is required.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        string path = args[1];
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Gather lightweight facts about the image to feed the mount model.
        long totalSectors = 0;
        int trackCount = 1;
        bool hasAudio = false, hasSubchannel = false, isPlainData = true;

        if (ext == ".cdi" || ext == ".cue" || ext == ".bin" || ext == ".img" || ext == ".iso")
        {
            try
            {
                using var access = SectorAccess.Open(path);
                totalSectors = access.TotalSectors;
            }
            catch { /* fall back to size-based estimate below */ }
        }
        if (totalSectors == 0)
            totalSectors = new FileInfo(path).Length / 2048;

        // Inspect a .cdi/.cue for tracks + audio + subchannel where we can.
        try
        {
            var opened = OpenDataTrack(path);
            if (opened is not null)
            {
                var (image, _, fs) = opened.Value;
                using (fs)
                {
                    trackCount = image.AllTracks.Count();
                    hasAudio = image.AllTracks.Any(t => t.Mode == CdiTrackMode.Audio);
                    isPlainData = !hasAudio && trackCount <= 1;
                }
            }
        }
        catch { /* best effort; defaults stand */ }

        if (ext == ".sub" || File.Exists(Path.ChangeExtension(path, ".sub")))
            hasSubchannel = true;

        var media = DiscForge.Core.Mount.VirtualDisc.MediaFromSectors(totalSectors);
        var disc = DiscForge.Core.Mount.VirtualDisc.Describe(
            path, media, totalSectors, trackCount, hasAudio, hasSubchannel, isPlainData);

        Console.WriteLine($"{Path.GetFileName(path)}");
        Console.WriteLine($"  {disc.Summary}");
        Console.WriteLine();

        switch (disc.Strategy)
        {
            case DiscForge.Core.Mount.VirtualDisc.MountStrategy.NativeIso:
                Console.WriteLine("Mount now on Windows (no driver needed):");
                Console.WriteLine("  " + DiscForge.Core.Mount.VirtualDisc.NativeMountCommand(Path.GetFullPath(path)));
                Console.WriteLine("Unmount:");
                Console.WriteLine("  " + DiscForge.Core.Mount.VirtualDisc.NativeUnmountCommand(Path.GetFullPath(path)));
                break;

            case DiscForge.Core.Mount.VirtualDisc.MountStrategy.ConvertThenNativeIso:
                Console.WriteLine("This is a single data track in a non-ISO container. Export it to a");
                Console.WriteLine("plain .iso first, then Windows can mount it natively:");
                Console.WriteLine($"  dforge convert \"{path}\" \"{Path.ChangeExtension(path, ".iso")}\"");
                Console.WriteLine("  " + DiscForge.Core.Mount.VirtualDisc.NativeMountCommand(
                    Path.GetFullPath(Path.ChangeExtension(path, ".iso"))));
                break;

            case DiscForge.Core.Mount.VirtualDisc.MountStrategy.NeedsVirtualDriveDriver:
                Console.WriteLine("A faithful mount of this image (audio / subchannel / multi-track)");
                Console.WriteLine("needs a virtual optical drive — a kernel-mode driver DiscForge does");
                Console.WriteLine("not yet ship. You can still inspect, verify, extract, or convert it");
                Console.WriteLine("with the other commands.");
                break;
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CcdInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge ccd-info <image.ccd>\n" +
                    "  Reads a CloneCD control file and shows its table of contents.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var toc = DiscForge.Core.Convert.CloneCdReader.ReadFile(args[1]);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {toc.Summary}");
        if (toc.Catalog is not null) Console.WriteLine($"  Catalog (MCN): {toc.Catalog}");
        var (img, sub) = DiscForge.Core.Convert.CloneCdReader.SidecarsFor(args[1]);
        Console.WriteLine($"  Data image: {Path.GetFileName(img)}" +
            (File.Exists(img) ? " (present)" : " (missing)"));
        Console.WriteLine($"  Subchannel: {Path.GetFileName(sub)}" +
            (File.Exists(sub) ? " (present)" : " (none)"));
        Console.WriteLine();
        Console.WriteLine("Track  Type   Start LBA  Control");
        foreach (var t in toc.Tracks)
            Console.WriteLine($"  {t.Number,2}   {(t.IsData ? "Data " : "Audio")}  {t.StartLba,9}  0x{t.Control:X2}" +
                (t.Isrc is not null ? $"  ISRC={t.Isrc}" : ""));
        return 0;
    }
    catch (DiscForge.Core.Convert.CloneCdReader.CcdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int AccurateRipCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge accuraterip <image.cue> [--db <dBAR.bin>] [--url]\n" +
                    "  Computes AccurateRip v1/v2 checksums and disc IDs for the audio tracks.\n" +
                    "  --db <file>   verify against a downloaded AccurateRip database record\n" +
                    "  --url         print the AccurateRip lookup URL to fetch the record\n" +
                    "  (Fetching the record is an online step done separately.)");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string? dbPath = null;
    bool showUrl = args.Contains("--url");
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--db") dbPath = args[i + 1];

    try
    {
        using var layout = DiscLayout.FromCueFile(args[1]);
        var audioTracks = layout.Tracks.Where(t => t.Mode == RawTrackMode.Audio).OrderBy(t => t.Number).ToList();
        if (audioTracks.Count == 0)
            return Fail("No audio tracks in this image — AccurateRip applies to audio CDs.");

        // Build the TOC offsets (each track's start LBA, plus lead-out).
        var offsets = new List<int>();
        int lba = 0;
        foreach (var t in layout.Tracks.OrderBy(t => t.Number))
        {
            offsets.Add(lba);
            lba += t.TotalSectors;
        }
        offsets.Add(lba);   // lead-out

        var (id1, id2, cddb) = DiscForge.Core.Audio.AccurateRip.DiscIds(offsets);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {audioTracks.Count} audio track(s)");
        Console.WriteLine($"Disc IDs:  AR1={id1:X8}  AR2={id2:X8}  CDDB={cddb:X8}");
        if (showUrl)
            Console.WriteLine("Lookup:    " +
                DiscForge.Core.Audio.AccurateRipDatabase.LookupUrl(audioTracks.Count, id1, id2, cddb));
        Console.WriteLine();
        Console.WriteLine("Track  AccurateRip v1  AccurateRip v2");

        int firstNum = audioTracks.First().Number;
        int lastNum = audioTracks.Last().Number;
        var computed = new List<DiscForge.Core.Audio.AccurateRip.TrackChecksum>();

        foreach (var t in audioTracks)
        {
            // Read the track's stored audio bytes into memory (audio is 2352/sector).
            long start = t.SourceByteOffset;
            long lengthBytes = (long)t.LengthSectors * t.StoredSectorSize;
            var pcm = new byte[lengthBytes];
            lock (t.Source)
            {
                t.Source.Position = start;
                int read = 0;
                while (read < pcm.Length)
                {
                    int n = t.Source.Read(pcm, read, pcm.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }

            var cs = DiscForge.Core.Audio.AccurateRip.Compute(
                pcm, isFirstTrack: t.Number == firstNum, isLastTrack: t.Number == lastNum);
            computed.Add(cs);
            Console.WriteLine($"  {t.Number,2}   {cs.V1:X8}        {cs.V2:X8}");
        }

        Console.WriteLine();

        if (dbPath is not null)
        {
            if (!File.Exists(dbPath)) return Fail($"Database record not found: {dbPath}");
            var blob = File.ReadAllBytes(dbPath);
            var chunks = DiscForge.Core.Audio.AccurateRipDatabase.Parse(blob);
            var entries = DiscForge.Core.Audio.AccurateRipDatabase.ToEntries(chunks, (id1, id2, cddb));

            if (entries.Count == 0)
            {
                Console.WriteLine("The database record holds no pressing matching this disc's IDs.");
                Console.WriteLine("(The rip may be of a different pressing, or the record is for another disc.)");
                return 0;
            }

            var result = DiscForge.Core.Audio.AccurateRip.Verify(computed, entries);
            Console.WriteLine($"Verification against {chunks.Count} database pressing(s):");
            foreach (var v in result.Tracks)
            {
                string mark = v.Status switch
                {
                    DiscForge.Core.Audio.AccurateRip.TrackStatus.MatchV2 => $"ACCURATE (v2, confidence {v.Confidence})",
                    DiscForge.Core.Audio.AccurateRip.TrackStatus.MatchV1 => $"ACCURATE (v1, confidence {v.Confidence})",
                    _ => "not found / mismatch",
                };
                Console.WriteLine($"  Track {v.TrackIndex + 1,2}: {mark}");
            }
            Console.WriteLine();
            Console.WriteLine(result.Summary);
            return result.AllAccurate ? 0 : 1;
        }

        Console.WriteLine("To verify: fetch the AccurateRip record (see --url) and pass it with --db.");
        Console.WriteLine("A match means your rip is bit-identical to others' — a confirmed-good rip.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Transcode(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge transcode <input> <output> [options]\n" +
                    "  options:\n" +
                    "    --ratio <0.05-1.0>   video compression ratio (default: fit by --target)\n" +
                    "    --target <dvd5|dvd9|bytes>   fit video to this size\n" +
                    "    --duration <seconds> input duration (required for bitrate math)\n" +
                    "    --orig-video <bytes> original video size (for ratio→bitrate)\n" +
                    "    --codec <h264|hevc|mpeg2>    (default h264)\n" +
                    "    --two-pass           deep-analysis 2-pass encode\n" +
                    "    --keep-audio <i,j>   audio stream indices to keep\n" +
                    "    --dry-run            print the ffmpeg command(s), don't run\n" +
                    "  DiscForge does not bundle FFmpeg; install it and put it on PATH.");

    string input = args[1], output = args[2];
    if (!File.Exists(input)) return Fail($"Input not found: {input}");

    double ratio = 1.0, duration = 0;
    long origVideo = 0, targetBytes = 0;
    bool twoPass = args.Contains("--two-pass");
    bool dryRun = args.Contains("--dry-run");
    var codec = DiscForge.Core.Transcode.TranscodePlanner.VideoCodec.H264;
    var keepAudio = new List<int>();

    for (int i = 3; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--ratio" when i + 1 < args.Length: ratio = double.Parse(args[++i]); break;
            case "--duration" when i + 1 < args.Length: duration = double.Parse(args[++i]); break;
            case "--orig-video" when i + 1 < args.Length: origVideo = long.Parse(args[++i]); break;
            case "--target" when i + 1 < args.Length:
                targetBytes = args[i + 1].ToLowerInvariant() switch
                {
                    "dvd5" => DiscForge.Core.DvdVideo.BitBudget.Dvd5,
                    "dvd9" => DiscForge.Core.DvdVideo.BitBudget.Dvd9,
                    _ => long.Parse(args[i + 1]),
                };
                i++;
                break;
            case "--codec" when i + 1 < args.Length:
                codec = args[++i].ToLowerInvariant() switch
                {
                    "hevc" => DiscForge.Core.Transcode.TranscodePlanner.VideoCodec.Hevc,
                    "mpeg2" => DiscForge.Core.Transcode.TranscodePlanner.VideoCodec.Mpeg2,
                    _ => DiscForge.Core.Transcode.TranscodePlanner.VideoCodec.H264,
                };
                break;
            case "--keep-audio" when i + 1 < args.Length:
                keepAudio.AddRange(args[++i].Split(',').Select(int.Parse));
                break;
        }
    }

    if (duration <= 0)
        return Fail("--duration <seconds> is required to compute the target bitrate.");

    // If a target size was given and we know the original video size, derive the
    // ratio from the budget; otherwise use the explicit --ratio.
    if (targetBytes > 0 && origVideo > 0)
    {
        var plan = DiscForge.Core.DvdVideo.BitBudget.Compute(new[]
        {
            new DiscForge.Core.DvdVideo.BitBudget.TitlePlanRequest
            {
                Title = new DiscForge.Core.DvdVideo.BitBudget.TitleSizes
                { Name = "input", VideoBytes = origVideo },
                Mode = DiscForge.Core.DvdVideo.BitBudget.Mode.Automatic,
            },
        }, targetBytes);
        ratio = plan.AutomaticRatio;
    }

    var container = Path.GetExtension(output).ToLowerInvariant() switch
    {
        ".mkv" => DiscForge.Core.Transcode.TranscodePlanner.Container.Mkv,
        ".mpg" or ".vob" => DiscForge.Core.Transcode.TranscodePlanner.Container.DvdVideoMpeg2,
        _ => DiscForge.Core.Transcode.TranscodePlanner.Container.Mp4,
    };

    var titlePlan = new DiscForge.Core.DvdVideo.BitBudget.TitlePlan
    {
        Name = Path.GetFileName(input), VideoRatio = ratio,
        PlannedVideoBytes = origVideo > 0 ? (long)Math.Round(origVideo * ratio) : 0,
        PlannedTotalBytes = 0, Mode = DiscForge.Core.DvdVideo.BitBudget.Mode.Automatic,
    };

    var enc = DiscForge.Core.Transcode.TranscodePlanner.ForTitle(
        titlePlan, input, output, duration, codec, container,
        originalVideoBytes: origVideo, twoPass: twoPass, keepAudio: keepAudio);

    var vectors = DiscForge.Core.Transcode.TranscodePlanner.BuildArgs(enc);

    Console.WriteLine($"Plan: {(enc.CopyVideo ? "stream-copy (no re-encode)" : $"{codec}, {enc.VideoBitrate:N0} bps, ratio {ratio:P0}")}" +
                      $"{(enc.TwoPass ? ", two-pass" : "")}");
    Console.WriteLine();

    var ffmpeg = DiscForge.Core.Transcode.FfmpegRunner.Locate();
    if (dryRun || ffmpeg is null)
    {
        if (ffmpeg is null && !dryRun)
            Console.WriteLine("FFmpeg not found on PATH — showing the command(s) instead:\n");
        foreach (var v in vectors)
            Console.WriteLine("ffmpeg " + string.Join(" ", v.Select(QuoteIfNeeded)));
        if (ffmpeg is null && !dryRun)
        {
            Console.WriteLine();
            Console.WriteLine("Install FFmpeg (https://ffmpeg.org) and put ffmpeg on PATH to run this.");
        }
        return 0;
    }

    var runner = new DiscForge.Core.Transcode.FfmpegRunner(ffmpeg);
    Console.WriteLine($"Using {ffmpeg}");
    int pass = 1;
    double lastPct = 0;
    bool ok = runner.Run(enc, vectors,
        onProgress: p =>
        {
            if (p.Percent is { } pct)
            {
                // A new pass starts when progress falls back near zero after
                // having climbed high — a genuine reset, not jitter.
                if (vectors.Count > 1 && lastPct > 50 && pct < 10 && pass < vectors.Count) pass++;
                lastPct = pct;
                Console.Write($"\r  {(vectors.Count > 1 ? $"pass {pass}/{vectors.Count}: " : "")}{pct,5:F1}%   " +
                              $"{(p.SpeedX is { } sx ? $"{sx:F1}x" : "")}      ");
            }
        },
        onLog: l => { if (l.StartsWith("ffmpeg exited")) Console.WriteLine("\n" + l); });
    Console.WriteLine();
    Console.WriteLine(ok ? $"Done: {output}" : "Transcode failed (see ffmpeg output).");
    return ok ? 0 : 1;
}

static string QuoteIfNeeded(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

static int DvdInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge dvd-info <VIDEO_TS folder | disc root>\n" +
                    "  Shows the DVD-Video structure and a DVD-5 shrink plan.");
    string path = args[1];
    if (!Directory.Exists(path))
        return Fail($"Not a folder: {path}  (dvd-info reads a VIDEO_TS folder or disc root)");

    try
    {
        var src = new DiscForge.Core.DvdVideo.VideoTsSources.Folder(path);
        var dvd = DiscForge.Core.DvdVideo.IfoReader.Read(src);

        Console.WriteLine(dvd.Summary);
        Console.WriteLine();
        foreach (var ts in dvd.TitleSets)
        {
            Console.WriteLine($"Title set {ts.Number}:  video {ts.TitleVobBytes:N0} B, " +
                              $"menu {ts.MenuVobBytes:N0} B");
            foreach (var t in ts.Titles)
            {
                Console.WriteLine($"  Title {t.TitleNumber}: {t.Chapters} chapter(s), " +
                                  $"{t.AngleCount} angle(s)");
                foreach (var a in t.Audio)
                    Console.WriteLine($"    audio {a.Index}: {a.Codec} {a.Channels}ch " +
                                      $"[{(string.IsNullOrWhiteSpace(a.Language) ? "und" : a.Language)}]");
                foreach (var s in t.Subtitles)
                    Console.WriteLine($"    sub   {s.Index}: " +
                                      $"[{(string.IsNullOrWhiteSpace(s.Language) ? "und" : s.Language)}]");
            }
        }

        // A simple full-disc DVD-5 shrink plan: every title set automatic.
        var reqs = dvd.TitleSets.Select(ts => new DiscForge.Core.DvdVideo.BitBudget.TitlePlanRequest
        {
            Title = new DiscForge.Core.DvdVideo.BitBudget.TitleSizes
            {
                Name = $"VTS {ts.Number}",
                VideoBytes = ts.TitleVobBytes,
                OverheadBytes = ts.MenuVobBytes,
            },
            Mode = DiscForge.Core.DvdVideo.BitBudget.Mode.Automatic,
        }).ToList();

        var plan = DiscForge.Core.DvdVideo.BitBudget.Compute(
            reqs, DiscForge.Core.DvdVideo.BitBudget.Dvd5);

        Console.WriteLine();
        Console.WriteLine("Shrink-to-DVD-5 plan (full disc, automatic):");
        Console.WriteLine($"  {plan.Summary}");
        if (plan.AutomaticRatio < 1.0)
            Console.WriteLine($"  Video would be compressed to {plan.AutomaticRatio:P0} of original.");
        Console.WriteLine();
        Console.WriteLine("Note: DiscForge reads DVD-Video structure and plans the fit. The actual");
        Console.WriteLine("re-encode is a separate transcode step (not yet enabled). Encrypted");
        Console.WriteLine("(CSS) video is never processed.");
        return 0;
    }
    catch (DiscForge.Core.DvdVideo.IfoFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Re-emit a DVD-Video's IFO structure natively (VMG + VTS), optionally keeping
// only a subset of title sets. This writes the *structural* IFOs — the
// enumeration and stream map — not the navigation/cell tables a player walks, so
// it is a structural rewrite and diagnostic, not a playable-disc author (dvdauthor
// remains that path). See docs/DVD_VIDEO_SHRINK.md.
static int DvdRewrite(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge dvd-rewrite <VIDEO_TS folder | disc root> <out folder> [--keep 1,3]\n" +
                    "  Reads the DVD-Video IFO structure and re-emits VIDEO_TS.IFO + VTS_nn_0.IFO.\n" +
                    "  --keep selects title sets by number (renumbered contiguously). Structural\n" +
                    "  IFOs only (enumeration + streams); not a playable-disc author.");
    string inPath = args[1];
    string outPath = args[2];
    if (!Directory.Exists(inPath))
        return Fail($"Not a folder: {inPath}  (dvd-rewrite reads a VIDEO_TS folder or disc root)");

    int[]? keep = null;
    for (int i = 3; i < args.Length - 1; i++)
        if (args[i] == "--keep")
        {
            try
            {
                keep = args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(int.Parse).ToArray();
            }
            catch { return Fail($"--keep expects a comma-separated list of title-set numbers, got '{args[i + 1]}'."); }
        }

    try
    {
        var src = new DiscForge.Core.DvdVideo.VideoTsSources.Folder(inPath);
        var dvd = DiscForge.Core.DvdVideo.IfoReader.Read(src);

        var plan = keep is { Length: > 0 }
            ? DiscForge.Core.DvdVideo.IfoWriter.Keep(dvd, keep)
            : DiscForge.Core.DvdVideo.IfoWriter.PlanFrom(dvd);
        var files = DiscForge.Core.DvdVideo.IfoWriter.Write(plan);

        string videoTs = Path.Combine(outPath, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(videoTs, name), bytes);
            // A DVD keeps a byte-identical .BUP backup of each .IFO.
            File.WriteAllBytes(Path.Combine(videoTs, Path.ChangeExtension(name, ".BUP")), bytes);
        }

        Console.WriteLine($"Wrote {files.Count} IFO file(s) (+ .BUP backups) to {videoTs}");
        Console.WriteLine($"  {plan.TitleSets.Count} title set(s), " +
                          $"{plan.TitleSets.Sum(s => s.Titles.Count)} title(s).");
        Console.WriteLine();
        Console.WriteLine("Note: these are structural IFOs (enumeration + audio/subpicture streams).");
        Console.WriteLine("They round-trip through DiscForge's reader; producing a player-navigable");
        Console.WriteLine("disc (PGCI/cell tables) remains the dvdauthor step. CSS video is never processed.");
        return 0;
    }
    catch (DiscForge.Core.DvdVideo.IfoFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Read a Video CD / Super Video CD control file (INFO.VCD/INFO.SVD or
// ENTRIES.VCD/ENTRIES.SVD) and print what it identifies. Structural read only;
// see docs/VCD_AUTHORING.md for the scope and the clean-room note.
static int VcdInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge vcd-info <INFO.VCD | ENTRIES.VCD>\n" +
                    "  Reads a Video CD / Super Video CD control file and shows what it holds.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var data = File.ReadAllBytes(args[1]);
        string magic = data.Length >= 8 ? System.Text.Encoding.ASCII.GetString(data, 0, 8) : "";

        if (magic is "VIDEO_CD" or "SUPERVCD")
        {
            var info = DiscForge.Core.Vcd.VcdControl.ReadInfo(data);
            Console.WriteLine($"{(info.Kind == DiscForge.Core.Vcd.VcdKind.Vcd ? "Video CD" : "Super Video CD")} " +
                              $"INFO, version {info.Version}");
            Console.WriteLine($"  album id : \"{info.AlbumId}\"");
            Console.WriteLine($"  volume   : {info.VolumeNumber} of {info.VolumeCount}");
            return 0;
        }
        if (magic is "ENTRYVCD" or "ENTRYSVD")
        {
            var e = DiscForge.Core.Vcd.VcdControl.ReadEntries(data);
            Console.WriteLine($"{(e.Kind == DiscForge.Core.Vcd.VcdKind.Vcd ? "Video CD" : "Super Video CD")} " +
                              $"ENTRIES, version {e.Version}: {e.Entries.Count} entry-point(s)");
            foreach (var x in e.Entries)
                Console.WriteLine($"  track {x.TrackNumber,2} @ {x.Minute:00}:{x.Second:00}:{x.Frame:00}");
            return 0;
        }
        return Fail("Not a VCD/SVCD control file (expected a VIDEO_CD/SUPERVCD or ENTRYVCD/ENTRYSVD signature).");
    }
    catch (DiscForge.Core.Vcd.VcdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- PlayStation dev / asset utilities -------------------------------------

static int PsxExeInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge psx-exe-info <file.exe|file.psexe>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var h = DiscForge.Core.PlayStation.PsExe.ReadHeader(File.ReadAllBytes(args[1]));
        Console.WriteLine(h.Summary);
        Console.WriteLine($"  entry PC   : 0x{h.EntryPoint:X8}");
        Console.WriteLine($"  gp         : 0x{h.Gp:X8}");
        Console.WriteLine($"  load addr  : 0x{h.LoadAddress:X8}  (t_size {h.TextSize:N0} bytes)");
        Console.WriteLine($"  bss        : 0x{h.BssAddress:X8}  ({h.BssSize:N0} bytes)");
        Console.WriteLine($"  stack      : 0x{h.StackBase:X8} + 0x{h.StackOffset:X8}");
        if (h.RegionMarker.Length > 0) Console.WriteLine($"  marker     : {h.RegionMarker}");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.PsExeFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PsxPad(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge psx-pad <in> <out> [--multiple N | --psexe] [--fill 0xNN]\n" +
                    "  Pads a file to an N-byte boundary (default 2048), or a PS-EXE's payload\n" +
                    "  to 0x800 with t_size fixed (--psexe). --fill sets the pad byte.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    int multiple = DiscForge.Core.PlayStation.PsxPadding.SectorSize;
    bool psexe = false;
    byte fill = 0x00;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--psexe") psexe = true;
        else if (args[i] == "--multiple" && i + 1 < args.Length) multiple = int.Parse(args[++i]);
        else if (args[i] == "--fill" && i + 1 < args.Length)
        {
            string f = args[++i];
            fill = f.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (byte)System.Convert.ToInt32(f[2..], 16)
                : (byte)int.Parse(f);
        }
    }

    try
    {
        var data = File.ReadAllBytes(args[1]);
        var padded = psexe
            ? DiscForge.Core.PlayStation.PsxPadding.PadPsExe(data, fill)
            : DiscForge.Core.PlayStation.PsxPadding.PadToMultiple(data, multiple, fill);
        File.WriteAllBytes(args[2], padded);
        Console.WriteLine($"Padded {data.Length:N0} -> {padded.Length:N0} bytes -> {Path.GetFileName(args[2])}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Bin2Src(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge bin2src <file> [--name IDENT] [--asm] [--per-line N] [--out file]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string name = Path.GetFileNameWithoutExtension(args[1]);
    bool asm = false; int perLine = 12; string? outPath = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--asm") asm = true;
        else if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
        else if (args[i] == "--per-line" && i + 1 < args.Length) perLine = int.Parse(args[++i]);
        else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
    }

    try
    {
        var data = File.ReadAllBytes(args[1]);
        string src = asm
            ? DiscForge.Core.Util.BinToSource.ToAsm(data, name, perLine)
            : DiscForge.Core.Util.BinToSource.ToCArray(data, name, perLine);
        if (outPath is null) Console.Write(src);
        else { File.WriteAllText(outPath, src); Console.WriteLine($"Wrote {Path.GetFileName(outPath)} ({data.Length:N0} bytes encoded)."); }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SearchCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge search <file> (--hex 4d5a | --ascii TEXT) [--limit N]\n" +
                    "  Prints every byte offset where the pattern occurs.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    byte[]? needle = null; int limit = int.MaxValue;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--hex" && i + 1 < args.Length) needle = DiscForge.Core.Util.ByteSearch.ParseHex(args[++i]);
        else if (args[i] == "--ascii" && i + 1 < args.Length) needle = DiscForge.Core.Util.ByteSearch.FromAscii(args[++i]);
        else if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i]);
    }
    if (needle is null || needle.Length == 0) return Fail("Give a pattern with --hex or --ascii.");

    try
    {
        using var fs = File.OpenRead(args[1]);
        var hits = DiscForge.Core.Util.ByteSearch.FindAll(fs, needle);
        Console.WriteLine($"{hits.Count:N0} match(es).");
        for (int i = 0; i < hits.Count && i < limit; i++)
            Console.WriteLine($"  0x{hits[i]:X}  ({hits[i]:N0})");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VmuCreate(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge vmu-create <out.bin>  (a blank formatted 128 KB VMU)");
    try
    {
        File.WriteAllBytes(args[1], DiscForge.Core.Vmu.VmuBuilder.CreateFormatted());
        Console.WriteLine($"Wrote a blank formatted VMU: {Path.GetFileName(args[1])} (128 KB).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VmuAdd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge vmu-add <vmu.bin> <save.vms> [--name NAME] [--game] [--protect]\n" +
                    "  Adds a save to a VMU image (writes it back in place).");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");

    string name = Path.GetFileNameWithoutExtension(args[2]);
    if (name.Length > 12) name = name[..12];
    bool game = false, protect = false;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
        else if (args[i] == "--game") game = true;
        else if (args[i] == "--protect") protect = true;
    }

    try
    {
        var image = File.ReadAllBytes(args[1]);
        var vms = File.ReadAllBytes(args[2]);
        var updated = DiscForge.Core.Vmu.VmuBuilder.Add(image, name, vms, game, protect);
        File.WriteAllBytes(args[1], updated);
        Console.WriteLine($"Added '{name}' ({(vms.Length + 511) / 512} block(s)) to {Path.GetFileName(args[1])}.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Vms2Vmi(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge vms2vmi <save.vms> <out.vmi> [--desc TEXT] [--name VMUNAME]\n" +
                    "  Writes the VMI download-descriptor for a VMS save.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string resource = Path.GetFileNameWithoutExtension(args[1]);
    if (resource.Length > 8) resource = resource[..8];
    string vmuName = resource;
    string desc = resource;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--desc" && i + 1 < args.Length) desc = args[++i];
        else if (args[i] == "--name" && i + 1 < args.Length) vmuName = args[++i];
    }

    try
    {
        int size = (int)new FileInfo(args[1]).Length;
        var vmi = DiscForge.Core.Vmu.Vmi.Create(resource, vmuName, desc, size);
        File.WriteAllBytes(args[2], vmi);
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: VMI for \"{resource}\" ({size:N0} bytes).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Scramble / descramble a Dreamcast 1ST_READ.BIN (the bin2boot transform). A
// documented byte-slice permutation, not encryption; the two are exact inverses.
static int DcScramble(string[] args, bool scramble)
{
    string verb = scramble ? "dc-scramble" : "dc-descramble";
    if (args.Length < 3)
        return Fail($"usage: dforge {verb} <in.bin> <out.bin>\n" +
                    "  Slice-permutes a Dreamcast main binary. scramble <-> descramble are inverses.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var data = File.ReadAllBytes(args[1]);
        var result = scramble
            ? DiscForge.Core.Gdi.DreamcastScramble.Scramble(data)
            : DiscForge.Core.Gdi.DreamcastScramble.Descramble(data);
        File.WriteAllBytes(args[2], result);
        Console.WriteLine($"{(scramble ? "Scrambled" : "Descrambled")} {data.Length:N0} bytes -> {Path.GetFileName(args[2])}.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Scan a folder and render a shareable HTML collection dashboard.
static int LibraryReportCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge library-report <dir> <out.html> [--dat <file>]");
    string dir = args[1], outPath = args[2];
    if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");

    string? datPath = null;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--dat" && i + 1 < args.Length) datPath = args[++i];
        else return Fail($"Unknown option: {args[i]}");

    try
    {
        DiscForge.Core.Dat.DatFile? dat = null;
        if (datPath is not null)
        {
            if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");
            using var ds = File.OpenRead(datPath);
            dat = DiscForge.Core.Dat.DatFile.Parse(ds);
        }
        var report = DiscForge.Core.Library.LibraryScanner.Scan(dir, dat);
        File.WriteAllText(outPath, DiscForge.Core.Library.CollectionReportHtml.Build(report));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} — {report.Total:N0} files, {report.Verified:N0} verified, " +
                          $"{report.Missing.Count:N0} missing.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Compare two DAT revisions.
static int DatDiffCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge dat-diff <old.dat> <new.dat>");
    if (!File.Exists(args[1])) return Fail($"DAT not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"DAT not found: {args[2]}");
    try
    {
        DiscForge.Core.Dat.DatFile oldDat, newDat;
        using (var s = File.OpenRead(args[1])) oldDat = DiscForge.Core.Dat.DatFile.Parse(s);
        using (var s = File.OpenRead(args[2])) newDat = DiscForge.Core.Dat.DatFile.Parse(s);
        var d = DiscForge.Core.Dat.DatDiff.Compare(oldDat, newDat);

        Console.WriteLine($"Old: {d.OldGames:N0} games   New: {d.NewGames:N0} games");
        Console.WriteLine($"  +{d.Added.Count:N0} added   -{d.Removed.Count:N0} removed   ~{d.Changed.Count:N0} changed");
        void Section(string label, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            Console.WriteLine($"{label} ({list.Count:N0}):");
            foreach (var s in list.Take(50)) Console.WriteLine($"  {s}");
            if (list.Count > 50) Console.WriteLine($"  … and {list.Count - 50:N0} more");
        }
        Section("Added", d.Added);
        Section("Removed", d.Removed);
        Section("Changed", d.Changed.Select(c => $"{c.Game}  ({c.Detail})"));
        if (d.Identical) Console.WriteLine("The two DATs are identical.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Rebuild a clean, DAT-named set from a (possibly messy) source folder.
static int RebuildCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge rebuild <src-dir> <dest-dir> --dat <file> [--per-game] [--move] [--apply]\n" +
                    "  Verifies the source against the DAT and places each good file under its canonical name.\n" +
                    "  Without --apply it only shows the plan; --move relocates instead of copying.");
    string src = args[1], dest = args[2];
    if (!Directory.Exists(src)) return Fail($"Source folder not found: {src}");

    string? datPath = null;
    bool perGame = false, move = false, apply = false;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--dat" when i + 1 < args.Length: datPath = args[++i]; break;
            case "--per-game": perGame = true; break;
            case "--move": move = true; break;
            case "--apply": apply = true; break;
            default: return Fail($"Unknown option: {args[i]}");
        }
    if (datPath is null) return Fail("rebuild needs --dat <file>.");
    if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");

    try
    {
        DiscForge.Core.Dat.DatFile dat;
        using (var ds = File.OpenRead(datPath)) dat = DiscForge.Core.Dat.DatFile.Parse(ds);
        Console.WriteLine($"DAT: {dat.Name ?? Path.GetFileName(datPath)} ({dat.Count:N0} entries). Scanning {src}…");

        var report = DiscForge.Core.Library.LibraryScanner.Scan(src, dat);
        var layout = perGame ? DiscForge.Core.Library.RebuildLayout.PerGameFolder : DiscForge.Core.Library.RebuildLayout.Flat;
        var plan = DiscForge.Core.Library.SetRebuilder.Plan(report, dest, layout);

        Console.WriteLine($"To place: {plan.ToPlace:N0}   Already in place: {plan.AlreadyInPlace:N0}   " +
                          $"Unknown: {plan.Unknown:N0}   Missing from set: {plan.Missing:N0}");
        foreach (var a in plan.Actions.Take(40))
            Console.WriteLine($"  {Path.GetFileName(a.SourcePath)}  ->  {Path.GetRelativePath(dest, a.DestPath)}");
        if (plan.Actions.Count > 40) Console.WriteLine($"  … and {plan.Actions.Count - 40:N0} more");

        if (plan.Missing > 0)
        {
            Console.WriteLine($"Missing ({plan.Missing:N0}):");
            foreach (var m in plan.MissingRoms.Take(20)) Console.WriteLine($"  - {m.Name}");
            if (plan.MissingRoms.Count > 20) Console.WriteLine($"  … and {plan.MissingRoms.Count - 20:N0} more");
        }

        if (apply)
        {
            int placed = DiscForge.Core.Library.SetRebuilder.Apply(plan, move);
            Console.WriteLine($"{(move ? "Moved" : "Copied")} {placed:N0} file(s) into {dest}.");
        }
        else Console.WriteLine($"{plan.ToPlace:N0} file(s) planned. Re-run with --apply to {(move ? "move" : "copy")} them.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Licence tooling: generate a signing key pair, issue keys, verify keys, show machine id.
static int LicenseCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge license <keygen|issue|verify|machine-id> …\n" +
                    "  keygen <private.pem> <public.txt>            create a signing key pair (keep the private key!)\n" +
                    "  issue --private <f.pem> --name \"…\" [--edition Pro] [--days N] [--machine <id>]\n" +
                    "  verify <key> [--machine <id>]                check a key against the embedded public key\n" +
                    "  machine-id                                   print this machine's id");

    string sub = args[1].ToLowerInvariant();
    try
    {
        switch (sub)
        {
            case "keygen":
            {
                if (args.Length < 4) return Fail("usage: dforge license keygen <private.pem> <public.txt>");
                using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                File.WriteAllText(args[2], ec.ExportPkcs8PrivateKeyPem());
                string pubB64 = System.Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
                File.WriteAllText(args[3], pubB64);
                Console.WriteLine($"Private key -> {args[2]}   *** KEEP THIS SECRET ***");
                Console.WriteLine($"Public key  -> {args[3]}");
                Console.WriteLine("Paste this public key into LicenseConfig.PublicKeyBase64 (src/DiscForge.Core/Licensing/License.cs):");
                Console.WriteLine(pubB64);
                return 0;
            }

            case "issue":
            {
                string? priv = null, name = null, edition = "Standard", machine = null; int days = 0;
                for (int i = 2; i < args.Length; i++)
                    switch (args[i])
                    {
                        case "--private" when i + 1 < args.Length: priv = args[++i]; break;
                        case "--name" when i + 1 < args.Length: name = args[++i]; break;
                        case "--edition" when i + 1 < args.Length: edition = args[++i]; break;
                        case "--machine" when i + 1 < args.Length: machine = args[++i]; break;
                        case "--days" when i + 1 < args.Length: int.TryParse(args[++i], out days); break;
                        default: return Fail($"Unknown option: {args[i]}");
                    }
                if (priv is null || name is null) return Fail("issue needs --private <file.pem> and --name \"…\".");
                if (!File.Exists(priv)) return Fail($"Private key not found: {priv}");

                using var ec = System.Security.Cryptography.ECDsa.Create();
                ec.ImportFromPem(File.ReadAllText(priv));
                var info = new DiscForge.Core.Licensing.LicenseInfo
                {
                    Name = name,
                    Edition = edition!,
                    IssuedUtc = DateTime.UtcNow,
                    ExpiresUtc = days > 0 ? DateTime.UtcNow.AddDays(days) : null,
                    MachineId = string.IsNullOrWhiteSpace(machine) ? null : machine,
                };
                string key = DiscForge.Core.Licensing.License.Issue(info, ec.ExportPkcs8PrivateKey());
                Console.WriteLine($"Licence for {name} ({edition}" +
                                  (days > 0 ? $", {days} days" : ", perpetual") +
                                  (machine is not null ? $", machine {machine}" : "") + "):");
                Console.WriteLine(key);
                return 0;
            }

            case "verify":
            {
                if (args.Length < 3) return Fail("usage: dforge license verify <key> [--machine <id>]");
                string key = args[2];
                string? machine = null;
                for (int i = 3; i < args.Length; i++)
                    if (args[i] == "--machine" && i + 1 < args.Length) machine = args[++i];

                var r = DiscForge.Core.Licensing.License.Validate(
                    key, DiscForge.Core.Licensing.LicenseConfig.PublicSpki, machine, DateTime.UtcNow);
                Console.WriteLine($"State:   {r.State}");
                Console.WriteLine($"Message: {r.Message}");
                if (r.Info is { } info)
                {
                    Console.WriteLine($"Name:    {info.Name}");
                    Console.WriteLine($"Edition: {info.Edition}");
                    Console.WriteLine($"Issued:  {info.IssuedUtc:yyyy-MM-dd}");
                    Console.WriteLine($"Expires: {(info.ExpiresUtc is { } e ? e.ToString("yyyy-MM-dd") : "never")}");
                    Console.WriteLine($"Machine: {info.MachineId ?? "any"}");
                }
                return r.IsValid ? 0 : 1;
            }

            case "machine-id":
                Console.WriteLine(DiscForge.Core.Licensing.MachineId.FromRaw(Environment.MachineName));
                Console.WriteLine("(the DiscForge app shows the authoritative machine id in its Activation dialog)");
                return 0;

            default:
                return Fail($"Unknown subcommand '{sub}' (keygen|issue|verify|machine-id).");
        }
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// One-game-one-ROM: collapse a DAT to the single best copy of each game.
static int OneGameOneRomCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge 1g1r <dat> [--regions USA,Europe,Japan] [--keep-proto] [--drop-unlicensed] [--out <file.dat|file.txt>]\n" +
                    "  Picks the best region/revision of each game. --out .dat writes a filtered DAT; .txt writes names.");
    string datPath = args[1];
    if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");

    string? outPath = null;
    var opts = new DiscForge.Core.Dat.OneGameOneRomOptions();
    for (int i = 2; i < args.Length; i++)
        switch (args[i])
        {
            case "--regions" when i + 1 < args.Length:
                opts = new DiscForge.Core.Dat.OneGameOneRomOptions
                {
                    RegionPriority = args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    ExcludePrerelease = opts.ExcludePrerelease, ExcludeUnlicensed = opts.ExcludeUnlicensed,
                };
                break;
            case "--keep-proto":
                opts = new DiscForge.Core.Dat.OneGameOneRomOptions
                    { RegionPriority = opts.RegionPriority, ExcludePrerelease = false, ExcludeUnlicensed = opts.ExcludeUnlicensed };
                break;
            case "--drop-unlicensed":
                opts = new DiscForge.Core.Dat.OneGameOneRomOptions
                    { RegionPriority = opts.RegionPriority, ExcludePrerelease = opts.ExcludePrerelease, ExcludeUnlicensed = true };
                break;
            case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        DiscForge.Core.Dat.DatFile dat;
        using (var ds = File.OpenRead(datPath)) dat = DiscForge.Core.Dat.DatFile.Parse(ds);
        var report = DiscForge.Core.Dat.OneGameOneRom.Build(dat, opts);

        Console.WriteLine($"DAT: {dat.Name ?? Path.GetFileName(datPath)} — {report.TotalGames:N0} games -> " +
                          $"{report.Families:N0} kept (1G1R), regions [{string.Join(", ", opts.RegionPriority)}].");

        if (outPath is null)
        {
            foreach (var c in report.Choices.Take(60))
                Console.WriteLine($"  {c.Chosen.Game}" + (c.Rejected.Count > 0 ? $"   (−{c.Rejected.Count} variant(s))" : ""));
            if (report.Choices.Count > 60) Console.WriteLine($"  … and {report.Choices.Count - 60:N0} more");
            return 0;
        }

        string ext = Path.GetExtension(outPath).ToLowerInvariant();
        if (ext == ".txt")
        {
            File.WriteAllLines(outPath, report.ChosenGames.Select(g => g.Game));
            Console.WriteLine($"Wrote {report.Families:N0} game name(s) to {Path.GetFileName(outPath)}.");
        }
        else
        {
            string title = (dat.Name ?? "DAT") + " (1G1R)";
            File.WriteAllText(outPath, DiscForge.Core.Dat.DatWriter.WriteLogiqx(title, report.ChosenGames));
            Console.WriteLine($"Wrote a filtered DAT of {report.Families:N0} game(s) to {Path.GetFileName(outPath)}.");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Scan a folder and write a front-end library file (RetroArch .lpl or ES gamelist.xml).
static int FrontendExportCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge frontend-export <retroarch|gamelist> <folder> <out> [--name <name>] [--dat <file>]\n" +
                    "  retroarch → a RetroArch .lpl playlist; gamelist → an EmulationStation/RetroBat gamelist.xml.");
    string kind = args[1].ToLowerInvariant();
    if (kind is not ("retroarch" or "gamelist"))
        return Fail("kind must be 'retroarch' or 'gamelist'.");
    string dir = args[2], outPath = args[3];
    if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");

    string? datPath = null, name = null;
    for (int i = 4; i < args.Length; i++)
        switch (args[i])
        {
            case "--dat" when i + 1 < args.Length: datPath = args[++i]; break;
            case "--name" when i + 1 < args.Length: name = args[++i]; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        DiscForge.Core.Dat.DatFile? dat = null;
        if (datPath is not null)
        {
            if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");
            using var ds = File.OpenRead(datPath);
            dat = DiscForge.Core.Dat.DatFile.Parse(ds);
        }

        // Sidecar / metadata files that live alongside games but are not games themselves.
        var skipExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".m3u", ".lpl", ".xml", ".txt", ".dat", ".nfo", ".sbi", ".sub", ".png", ".jpg", ".jpeg" };

        var report = DiscForge.Core.Library.LibraryScanner.Scan(dir, dat);
        var items = new List<DiscForge.Core.Frontend.PlaylistItem>();
        foreach (var e in report.Entries)
        {
            if (skipExt.Contains(Path.GetExtension(e.FileName))) continue;
            string label = e.Match?.Game ?? Path.GetFileNameWithoutExtension(e.FileName);
            string system = e.RomPlatform.Length > 0 ? e.RomPlatform : e.Format;
            string rel = Path.GetRelativePath(dir, e.Path).Replace('\\', '/');
            items.Add(new DiscForge.Core.Frontend.PlaylistItem
            {
                Path = kind == "retroarch" ? e.Path : rel,
                Label = label,
                Crc32Hex = e.Crc32Hex,
                System = system,
            });
        }
        if (items.Count == 0) return Fail("No files found in the folder to export.");

        string text = kind == "retroarch"
            ? DiscForge.Core.Frontend.FrontendExport.BuildRetroArchLpl(name ?? Path.GetFileNameWithoutExtension(outPath), items)
            : DiscForge.Core.Frontend.FrontendExport.BuildEmulationStationGamelist(items);

        File.WriteAllText(outPath, text);
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} ({kind}): {items.Count} game(s).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Fix a cartridge save's byte order or size for cross-emulator / hardware use.
static int SaveConvertCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge save-convert <in> <out> <op> [--fill 00|FF]\n" +
                    "  op: swap16 | swap32 | pad <size|sram|flash|eeprom4k|eeprom16k|mempak> | trim");
    string inPath = args[1], outPath = args[2], op = args[3].ToLowerInvariant();
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");

    byte fill = 0x00;
    string? sizeArg = null;
    for (int i = 4; i < args.Length; i++)
    {
        if (args[i] == "--fill" && i + 1 < args.Length)
        {
            if (!byte.TryParse(args[++i], System.Globalization.NumberStyles.HexNumber, null, out fill))
                return Fail("--fill expects a hex byte (e.g. FF).");
        }
        else sizeArg ??= args[i];
    }

    static int? SizeOf(string s) => s.ToLowerInvariant() switch
    {
        "sram" => DiscForge.Core.Saves.SaveConvert.Sram,
        "flash" => DiscForge.Core.Saves.SaveConvert.FlashRam,
        "eeprom4k" => DiscForge.Core.Saves.SaveConvert.EepromSmall,
        "eeprom16k" => DiscForge.Core.Saves.SaveConvert.EepromLarge,
        "mempak" => DiscForge.Core.Saves.SaveConvert.ControllerPak,
        _ => int.TryParse(s, out int n) ? n : null,
    };

    try
    {
        var data = File.ReadAllBytes(inPath);
        byte[] outBytes;
        switch (op)
        {
            case "swap16": outBytes = DiscForge.Core.Saves.SaveConvert.WordSwap(data, 2); break;
            case "swap32": outBytes = DiscForge.Core.Saves.SaveConvert.WordSwap(data, 4); break;
            case "trim": outBytes = DiscForge.Core.Saves.SaveConvert.TrimTrailing(data, fill); break;
            case "pad":
                if (sizeArg is null) return Fail("pad needs a size (a byte count or sram|flash|eeprom4k|eeprom16k|mempak).");
                if (SizeOf(sizeArg) is not { } size) return Fail($"Unknown size '{sizeArg}'.");
                outBytes = DiscForge.Core.Saves.SaveConvert.Resize(data, size, fill);
                break;
            default: return Fail($"Unknown op '{op}' (swap16|swap32|pad|trim).");
        }
        File.WriteAllBytes(outPath, outBytes);
        Console.WriteLine($"{op}: wrote {Path.GetFileName(outPath)} ({outBytes.Length:N0} bytes, was {data.Length:N0}).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Fix a cartridge dump's copier header / interleave / byte order so it matches a DAT.
static int RomConvertCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge rom-convert <in> <out> <op>\n" +
                    "  op: z64|v64|n64 (N64 byte order), snes-strip|snes-add, smd|unsmd (Genesis), nes-strip");
    string inPath = args[1], outPath = args[2], op = args[3].ToLowerInvariant();
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");

    try
    {
        var data = File.ReadAllBytes(inPath);
        byte[] outBytes = op switch
        {
            "z64" => DiscForge.Core.Rom.RomConvert.ConvertN64(data, DiscForge.Core.Rom.N64ByteOrder.Z64),
            "v64" => DiscForge.Core.Rom.RomConvert.ConvertN64(data, DiscForge.Core.Rom.N64ByteOrder.V64),
            "n64" => DiscForge.Core.Rom.RomConvert.ConvertN64(data, DiscForge.Core.Rom.N64ByteOrder.N64),
            "snes-strip" => DiscForge.Core.Rom.RomConvert.StripSnesHeader(data),
            "snes-add" => DiscForge.Core.Rom.RomConvert.AddSnesHeader(data),
            "unsmd" or "bin" => DiscForge.Core.Rom.RomConvert.SmdToBin(data),
            "smd" => DiscForge.Core.Rom.RomConvert.BinToSmd(data),
            "nes-strip" => DiscForge.Core.Rom.RomConvert.StripInesHeader(data),
            _ => throw new DiscForge.Core.Rom.RomConvert.RomConvertException(
                     $"Unknown op '{op}' (z64|v64|n64|snes-strip|snes-add|smd|unsmd|nes-strip)."),
        };
        File.WriteAllBytes(outPath, outBytes);
        Console.WriteLine($"{op}: wrote {Path.GetFileName(outPath)} ({outBytes.Length:N0} bytes, was {data.Length:N0}).");
        return 0;
    }
    catch (DiscForge.Core.Rom.RomConvert.RomConvertException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Convert a PS1 memory card between raw / DexDrive / VGS container formats.
static int Ps1CardConvertCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge ps1card-convert <in> <out> [raw|gme|vgs]\n" +
                    "  Container transform only — the 128 KB of card data is preserved byte-for-byte.\n" +
                    "  Target defaults to the output extension (.gme→DexDrive, .vgs/.mem→VGS, else raw).");
    string inPath = args[1], outPath = args[2];
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");

    DiscForge.Core.PlayStation.Ps1CardFormat target;
    string sel = args.Length >= 4 ? args[3].ToLowerInvariant() : Path.GetExtension(outPath).TrimStart('.').ToLowerInvariant();
    target = sel switch
    {
        "gme" or "dexdrive" => DiscForge.Core.PlayStation.Ps1CardFormat.DexDrive,
        "vgs" or "mem" => DiscForge.Core.PlayStation.Ps1CardFormat.Vgs,
        _ => DiscForge.Core.PlayStation.Ps1CardFormat.Raw,
    };

    try
    {
        var data = File.ReadAllBytes(inPath);
        var from = DiscForge.Core.PlayStation.Ps1CardConvert.Detect(data);
        var outBytes = DiscForge.Core.PlayStation.Ps1CardConvert.Convert(data, target);
        File.WriteAllBytes(outPath, outBytes);
        Console.WriteLine($"Converted {from} -> {target}: wrote {Path.GetFileName(outPath)} ({outBytes.Length:N0} bytes).");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.Ps1CardConvert.Ps1CardFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Write a deterministic, TorrentZip-structured archive from an explicit file list.
static int TorrentZipCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge torrentzip <out.zip> <file> [file ...]\n" +
                    "  Deterministic archive (sorted, fixed timestamps, TORRENTZIPPED comment).");
    string outZip = args[1];
    var entries = new List<DiscForge.Core.Archive.ZipEntry>();
    for (int i = 2; i < args.Length; i++)
    {
        if (!File.Exists(args[i])) return Fail($"File not found: {args[i]}");
        entries.Add(new DiscForge.Core.Archive.ZipEntry(Path.GetFileName(args[i]), File.ReadAllBytes(args[i])));
    }
    try
    {
        File.WriteAllBytes(outZip, DiscForge.Core.Archive.TorrentZip.Create(entries));
        Console.WriteLine($"Wrote {Path.GetFileName(outZip)} ({entries.Count} file(s), TorrentZip-structured).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static bool TrySidecarKind(string s, out DiscForge.Core.Archive.SidecarKind kind)
{
    switch (s.ToLowerInvariant().TrimStart('.'))
    {
        case "sfv": kind = DiscForge.Core.Archive.SidecarKind.Sfv; return true;
        case "md5": kind = DiscForge.Core.Archive.SidecarKind.Md5; return true;
        case "sha1": kind = DiscForge.Core.Archive.SidecarKind.Sha1; return true;
        default: kind = default; return false;
    }
}

static string HashOfFile(DiscForge.Core.Archive.SidecarKind kind, string path)
{
    using var fs = File.OpenRead(path);
    if (kind == DiscForge.Core.Archive.SidecarKind.Sfv)
    {
        var c = new DiscForge.Core.Util.Crc32();
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0) c.Update(buf.AsSpan(0, n));
        return c.Value.ToString("X8");
    }
    using System.Security.Cryptography.HashAlgorithm alg =
        kind == DiscForge.Core.Archive.SidecarKind.Md5
            ? System.Security.Cryptography.MD5.Create()
            : System.Security.Cryptography.SHA1.Create();
    return System.Convert.ToHexString(alg.ComputeHash(fs));
}

// Write a checksum sidecar (.sfv / .md5 / .sha1) for a set of files.
static int HashGenCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge hashgen <sfv|md5|sha1> <out> <file> [file ...]");
    if (!TrySidecarKind(args[1], out var kind)) return Fail($"Unknown sidecar kind '{args[1]}' (sfv|md5|sha1).");
    string outPath = args[2];

    var lines = new List<DiscForge.Core.Archive.HashLine>();
    for (int i = 3; i < args.Length; i++)
    {
        if (!File.Exists(args[i])) return Fail($"File not found: {args[i]}");
        lines.Add(new DiscForge.Core.Archive.HashLine(Path.GetFileName(args[i]), HashOfFile(kind, args[i])));
    }
    try
    {
        File.WriteAllText(outPath, DiscForge.Core.Archive.HashSidecar.Build(kind, lines));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} ({lines.Count} entry(ies)).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Re-hash the files a sidecar references and report OK / FAIL / MISSING.
static int HashVerifyCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge hashverify <sidecar.sfv|.md5|.sha1>");
    string sidecar = args[1];
    if (!File.Exists(sidecar)) return Fail($"Sidecar not found: {sidecar}");
    if (!TrySidecarKind(Path.GetExtension(sidecar), out var kind))
        return Fail("Sidecar must have a .sfv, .md5 or .sha1 extension.");

    string baseDir = Path.GetDirectoryName(Path.GetFullPath(sidecar)) ?? ".";
    var entries = DiscForge.Core.Archive.HashSidecar.Parse(kind, File.ReadAllText(sidecar));

    int ok = 0, bad = 0, missing = 0;
    foreach (var e in entries)
    {
        string path = Path.Combine(baseDir, e.Name);
        if (!File.Exists(path)) { Console.WriteLine($"  MISSING  {e.Name}"); missing++; continue; }
        string actual = HashOfFile(kind, path);
        if (string.Equals(actual, e.Hash, StringComparison.OrdinalIgnoreCase)) { ok++; }
        else { Console.WriteLine($"  FAIL     {e.Name}  (have {actual}, expected {e.Hash})"); bad++; }
    }
    Console.WriteLine($"OK {ok}   FAIL {bad}   MISSING {missing}   ({entries.Count} listed)");
    return bad == 0 && missing == 0 ? 0 : 1;
}

// Build a multi-disc .m3u playlist from an explicit, ordered list of disc images.
static int M3uCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge m3u <out.m3u> <disc1> <disc2> [...]\n" +
                    "  Lists the discs in order so an emulator can swap discs and share one memory card.");
    string outPath = args[1];
    var discs = args[2..];

    // Use a bare filename when the disc sits beside the .m3u (the portable, common case),
    // otherwise keep the path the user gave.
    string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? "";
    var lines = discs.Select(d =>
    {
        string full = Path.GetFullPath(d);
        string discDir = Path.GetDirectoryName(full) ?? "";
        return string.Equals(discDir, outDir, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(full) : d;
    });

    try
    {
        File.WriteAllText(outPath, DiscForge.Core.Frontend.FrontendExport.BuildM3u(lines));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} ({discs.Length} disc(s)).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Verify one or more dumps against a redump-style Logiqx DAT file.
static int SubmissionInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge submission-info <image> [--out <file.txt>]");
    string input = args[1];
    if (!File.Exists(input)) return Fail($"Image not found: {input}");
    string? outPath = null;
    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
        else return Fail($"Unknown option: {args[i]}");
    try
    {
        var info = DiscForge.Core.Redump.SubmissionInfoGenerator.Generate(input);
        string text = info.ToRedumpText();
        if (outPath is not null)
        {
            File.WriteAllText(outPath, text);
            Console.WriteLine($"Wrote submission info for {info.FileName} " +
                              $"({info.Tracks.Count} track(s)) -> {Path.GetFileName(outPath)}.");
        }
        else Console.Write(text);
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Library(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge library scan <dir> [--dat <dat-file>]\n" +
                    "       dforge library rename <dir> --dat <dat-file> [--apply]");
    string sub = args[1];
    string dir = args[2];
    if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");

    string? datPath = null; bool apply = false;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--dat" when i + 1 < args.Length: datPath = args[++i]; break;
            case "--apply": apply = true; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        DiscForge.Core.Dat.DatFile? dat = null;
        if (datPath is not null)
        {
            if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");
            using var ds = File.OpenRead(datPath);
            dat = DiscForge.Core.Dat.DatFile.Parse(ds);
            Console.WriteLine($"DAT: {dat.Name ?? Path.GetFileName(datPath)} ({dat.Count:N0} entries)");
        }

        var report = DiscForge.Core.Library.LibraryScanner.Scan(dir, dat);

        if (sub == "rename")
        {
            if (dat is null) return Fail("library rename needs --dat <dat-file>.");
            var plan = report.RenamePlan();
            if (plan.Count == 0) { Console.WriteLine("Nothing to rename — no verified files are mis-named."); return 0; }
            foreach (var r in plan)
                Console.WriteLine($"  {Path.GetFileName(r.From)}  ->  {Path.GetFileName(r.To)}");
            if (apply)
            {
                int n = DiscForge.Core.Library.LibraryScanner.ApplyRenames(plan);
                Console.WriteLine($"Renamed {n:N0} file(s).");
            }
            else Console.WriteLine($"{plan.Count:N0} rename(s) planned. Re-run with --apply to perform them.");
            return 0;
        }

        // scan report
        foreach (var e in report.Entries)
        {
            string tag = e.Status.ToString().ToUpperInvariant();
            string extra = e.Match is not null ? $"  = {e.Match.Game}" :
                           (e.RomPlatform.Length > 0 ? $"  [{e.RomPlatform}]" : (e.Format.Length > 0 ? $"  [{e.Format}]" : ""));
            string sug = e.SuggestedName is not null ? $"  -> {e.SuggestedName}" : "";
            Console.WriteLine($"  {tag,-9} {e.FileName}{extra}{sug}");
        }
        Console.WriteLine();
        Console.WriteLine($"Total {report.Total:N0}   Verified {report.Verified:N0}   Misnamed {report.Misnamed:N0}   " +
                          $"Duplicates {report.Duplicates:N0}   Unknown {report.Unknown:N0}");
        if (report.Missing.Count > 0)
        {
            Console.WriteLine($"Missing from set: {report.Missing.Count:N0}");
            foreach (var m in report.Missing.Take(40)) Console.WriteLine($"  - {m.Name}  ({m.Game})");
            if (report.Missing.Count > 40) Console.WriteLine($"  … and {report.Missing.Count - 40:N0} more");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DatVerify(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge dat-verify <dat-file> <file> [file ...]\n" +
                    "  Checks each file's size + CRC-32/SHA-1 against a redump-style DAT and\n" +
                    "  reports whether it is a catalogued good dump.");
    if (!File.Exists(args[1])) return Fail($"DAT not found: {args[1]}");

    DiscForge.Core.Dat.DatFile dat;
    try { using var fs = File.OpenRead(args[1]); dat = DiscForge.Core.Dat.DatFile.Parse(fs); }
    catch (Exception ex) { return Fail($"Could not read the DAT: {ex.Message}"); }

    Console.WriteLine($"DAT: {dat.Name ?? Path.GetFileName(args[1])} ({dat.Count:N0} entries)");
    int verified = 0, failed = 0;
    for (int a = 2; a < args.Length; a++)
    {
        if (!File.Exists(args[a])) { Console.WriteLine($"  {Path.GetFileName(args[a])}: not found"); failed++; continue; }
        var sums = DiscForge.Core.Files.ImageChecksums.ComputeFile(args[a]);
        var m = dat.Verify(sums.Length, sums.Crc32, sums.Sha1, sums.Md5);
        if (m.Verified)
        {
            Console.WriteLine($"  ✓ {Path.GetFileName(args[a])}  →  {m.Rom!.Game}");
            Console.WriteLine($"      {m.Rom.Name}");
            verified++;
        }
        else
        {
            Console.WriteLine($"  ✗ {Path.GetFileName(args[a])}  →  {m.Reason}");
            if (m.Rom is not null) Console.WriteLine($"      nearest: {m.Rom.Game} / {m.Rom.Name}");
            failed++;
        }
    }
    Console.WriteLine($"{verified} verified, {failed} not verified.");
    return failed > 0 ? 1 : 0;
}

// Sniff any file and say what DiscForge thinks it is.
static int IdentifyCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge identify <file> [file ...]");
    int unknown = 0;
    for (int a = 1; a < args.Length; a++)
    {
        if (!File.Exists(args[a])) { Console.WriteLine($"{args[a]}: not found"); unknown++; continue; }
        try
        {
            using var fs = File.OpenRead(args[a]);
            var id = DiscForge.Core.Identify.FormatIdentifier.Identify(fs);
            Console.WriteLine($"{Path.GetFileName(args[a])}: {id.Name}" +
                              (id.Detail.Length > 0 ? $" — {id.Detail}" : "") +
                              $"  [{id.Category}]");
            if (!id.Recognised) unknown++;
        }
        catch (Exception ex) { Console.WriteLine($"{args[a]}: {ex.Message}"); unknown++; }
    }
    return unknown > 0 && args.Length == 2 ? 1 : 0;
}

static int CisoInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ciso-info <image.cso|.zso>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        using var fs = File.OpenRead(args[1]);
        var info = DiscForge.Core.Ciso.CisoImage.ReadInfo(fs);
        long comp = fs.Length;
        Console.WriteLine($"{info.Kind.ToString().ToUpperInvariant()} ({(info.Kind == DiscForge.Core.Ciso.CisoKind.Ciso ? "zlib" : "LZ4")}), " +
                          $"{info.UncompressedSize:N0} bytes uncompressed, {info.Blocks:N0} blocks of {info.BlockSize}");
        Console.WriteLine($"  compressed to {comp:N0} bytes ({100.0 * comp / Math.Max(1, info.UncompressedSize):N1}% of original)");
        return 0;
    }
    catch (DiscForge.Core.Ciso.CisoFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CisoToIso(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge ciso-to-iso <in.cso|.zso> <out.iso>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        using (var input = File.OpenRead(args[1]))
        using (var output = File.Create(args[2]))
            DiscForge.Core.Ciso.CisoImage.Decompress(input, output);
        Console.WriteLine($"Decompressed to {Path.GetFileName(args[2])} ({new FileInfo(args[2]).Length:N0} bytes).");
        return 0;
    }
    catch (DiscForge.Core.Ciso.CisoFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IsoToCiso(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge iso-to-ciso <in.iso> <out.cso>  (CSO / zlib)");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        long size = new FileInfo(args[1]).Length;
        using (var input = File.OpenRead(args[1]))
        using (var output = File.Create(args[2]))
            DiscForge.Core.Ciso.CisoImage.Compress(input, size, output);
        long comp = new FileInfo(args[2]).Length;
        Console.WriteLine($"Compressed to {Path.GetFileName(args[2])} ({comp:N0} bytes, " +
                          $"{100.0 * comp / Math.Max(1, size):N1}% of original).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Identify a CHD (compressed disc image) and show its CD track layout. Structure
// only — hunk decompression/extraction is a documented follow-up (docs/CHD.md).
static int ChdInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge chd-info <image.chd>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var info = DiscForge.Core.Chd.ChdReader.Read(File.ReadAllBytes(args[1]));
        Console.WriteLine(info.Summary);
        Console.WriteLine($"  hunk {info.HunkBytes:N0} B, unit {info.UnitBytes} B");
        foreach (var t in info.Tracks)
            Console.WriteLine($"  track {t.Number,2}: {t.Type,-10} {t.Frames:N0} frames" +
                              (t.Pregap > 0 ? $", pregap {t.Pregap}" : ""));
        return 0;
    }
    catch (DiscForge.Core.Chd.ChdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ChdExtract(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge chd-extract <image.chd> <out.bin> [out.cue] [--parent <p.chd> ...]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    string outBin = args[2];
    string outCue = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : Path.ChangeExtension(outBin, ".cue");
    byte[][]? chain = ReadParentChain(args);
    if (chain is null) return 1;   // ReadParentChain reported the error
    try
    {
        var r = DiscForge.Core.Chd.ChdExtractor.ExtractCd(File.ReadAllBytes(args[1]), chain);
        File.WriteAllBytes(outBin, r.Bin);
        // The cue names "disc.bin"; rewrite it to the chosen bin file name.
        File.WriteAllText(outCue, r.Cue.Replace("disc.bin", Path.GetFileName(outBin)));
        Console.WriteLine($"Extracted {r.Tracks} track(s), {r.Bin.Length:N0} bytes → {Path.GetFileName(outBin)} " +
                          $"+ {Path.GetFileName(outCue)}" + (r.Verified ? " (SHA-1 verified)." : "."));
        return 0;
    }
    catch (DiscForge.Core.Chd.ChdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ChdExtractHd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge chd-extract-hd <image.chd> <out.img> [--parent <p.chd> ...]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    byte[][]? chain = ReadParentChain(args);
    if (chain is null) return 1;
    try
    {
        var raw = DiscForge.Core.Chd.ChdHdExtractor.Extract(File.ReadAllBytes(args[1]), chain);
        File.WriteAllBytes(args[2], raw);
        Console.WriteLine($"Extracted hard-disk image, {raw.Length:N0} bytes → {Path.GetFileName(args[2])} (SHA-1 verified).");
        return 0;
    }
    catch (DiscForge.Core.Chd.ChdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ChdCreate(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge chd-create <in.cue | in.img> <out.chd>\n" +
                                     "  A .cue makes a CD CHD (bin/cue); any other input makes a hard-disk CHD.");
    string input = args[1], output = args[2];
    if (!File.Exists(input)) return Fail($"File not found: {input}");
    try
    {
        byte[] chd;
        if (input.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
            chd = DiscForge.Core.Chd.ChdWriter.CreateCdFromBinCue(File.ReadAllText(input), dir);
        }
        else
        {
            chd = DiscForge.Core.Chd.ChdWriter.CreateHd(File.ReadAllBytes(input));
        }
        File.WriteAllBytes(output, chd);
        Console.WriteLine($"Created {Path.GetFileName(output)} ({chd.Length:N0} bytes).");
        return 0;
    }
    catch (DiscForge.Core.Chd.ChdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Read the parent chain from repeated --parent <file> options, nearest-first. Returns
// an empty array when none are given, or null (after printing an error) if a flag is
// missing its path or names a file that can't be read.
static byte[][]? ReadParentChain(string[] args)
{
    var chain = new List<byte[]>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] != "--parent") continue;
        if (i + 1 >= args.Length) { Fail("--parent needs a parent .chd file path."); return null; }
        string path = args[++i];
        if (!File.Exists(path)) { Fail($"Parent CHD not found: {path}"); return null; }
        chain.Add(File.ReadAllBytes(path));
    }
    return chain.ToArray();
}

// ---- PS1 memory card (.mcr) ------------------------------------------------

static int PsxMcInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge psxmc-info <card.mcr>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var vol = DiscForge.Core.PlayStation.PsxMemoryCard.Read(File.ReadAllBytes(args[1]));
        Console.WriteLine($"PS1 memory card: {vol.Saves.Count} save(s), {vol.FreeBlocks}/15 blocks free");
        foreach (var s in vol.Saves)
            Console.WriteLine($"  {s.Name,-20} {s.Blocks.Count} block(s)" +
                              (s.Title.Length > 0 ? $"  \"{s.Title}\"" : ""));
        return 0;
    }
    catch (DiscForge.Core.PlayStation.PsxMcFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PsxMcExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge psxmc-extract <card.mcr> <out-dir>\n" +
                    "  Extracts every save as a raw .mcs block image.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var card = File.ReadAllBytes(args[1]);
        var vol = DiscForge.Core.PlayStation.PsxMemoryCard.Read(card);
        Directory.CreateDirectory(args[2]);
        int done = 0;
        foreach (var s in vol.Saves)
        {
            string safe = string.Concat(s.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            if (safe.Length == 0) safe = $"save{done}";
            File.WriteAllBytes(Path.Combine(args[2], safe + ".mcs"),
                               DiscForge.Core.PlayStation.PsxMemoryCard.Extract(card, s));
            done++;
        }
        Console.WriteLine($"Extracted {done} save(s) to {args[2]}");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.PsxMcFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- GameCube memory card (.gci / card image) ------------------------------

static int GciInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge gci-info <file.gci|card>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        if (DiscForge.Core.Saves.GcMemoryCardReader.IsGcMemoryCard(data))
        {
            var card = DiscForge.Core.Saves.GcMemoryCardReader.Read(data);
            Console.WriteLine($"GameCube memory card: {card.Saves.Count} save(s)");
            int i = 0;
            foreach (var s in card.Saves)
            {
                Console.WriteLine($"  [{i++}] {s.GameCode}/{s.Maker} {s.FileName,-24} {s.BlockCount} block(s)" +
                                  (s.Comment.Length > 0 ? $"  \"{s.Comment}\"" : ""));
            }
            return 0;
        }
        if (DiscForge.Core.Saves.GciReader.IsGci(data))
        {
            var s = DiscForge.Core.Saves.GciReader.Read(data);
            Console.WriteLine($"GameCube save (.gci): {s.GameCode}/{s.Maker} {s.FileName}, {s.BlockCount} block(s)");
            if (s.Comment.Length > 0) Console.WriteLine($"  comment: {s.Comment}");
            return 0;
        }
        return Fail("Not a GameCube .gci or memory-card image.");
    }
    catch (DiscForge.Core.Saves.GcSaveFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GciExtract(string[] args)
{
    if (args.Length < 4) return Fail("usage: dforge gci-extract <card> <index> <out.gci>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    if (!int.TryParse(args[2], out int index)) return Fail($"Invalid save index: {args[2]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var card = DiscForge.Core.Saves.GcMemoryCardReader.Read(data);
        if (index < 0 || index >= card.Saves.Count)
            return Fail($"Save index {index} out of range (0..{card.Saves.Count - 1}).");
        var gci = DiscForge.Core.Saves.GcMemoryCardReader.ExtractSaveToGci(data, card.Saves[index]);
        File.WriteAllBytes(args[3], gci);
        Console.WriteLine($"Wrote {gci.Length:N0} bytes to {args[3]}");
        return 0;
    }
    catch (DiscForge.Core.Saves.GcSaveFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- N64 saves -------------------------------------------------------------

static int N64SaveInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge n64save-info <file>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var type = DiscForge.Core.Saves.N64SaveReader.IdentifyBySize(data.Length);
        Console.WriteLine($"N64 save: {data.Length:N0} bytes — {DiscForge.Core.Saves.N64SaveReader.Describe(type)}");
        if (DiscForge.Core.Saves.N64ControllerPak.IsControllerPak(data))
        {
            var pak = DiscForge.Core.Saves.N64ControllerPak.Read(data);
            Console.WriteLine($"Controller Pak: {pak.Notes.Count} note(s)");
            foreach (var n in pak.Notes)
                Console.WriteLine($"  {n.GameCode}/{n.Publisher} \"{n.Name}\" (page {n.StartPage})");
        }
        return 0;
    }
    catch (DiscForge.Core.Saves.N64SaveFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- Saturn backup memory --------------------------------------------------

static int SaturnSaveInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge saturnsave-info <file>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var backup = DiscForge.Core.Saves.SaturnSaveReader.Read(data);
        Console.WriteLine($"Saturn backup: {backup.Saves.Count} save(s)");
        foreach (var s in backup.Saves)
            Console.WriteLine($"  {s.Name,-12} {s.Language,-9} {s.DataSize,7:N0} bytes" +
                              (s.Comment.Length > 0 ? $"  \"{s.Comment}\"" : ""));
        return 0;
    }
    catch (DiscForge.Core.Saves.SaturnSaveFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- PS2 memory card (.ps2) ------------------------------------------------

static int Ps2McInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ps2mc-info <card.ps2>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var vol = DiscForge.Core.PlayStation.Ps2MemoryCard.Read(File.ReadAllBytes(args[1]));
        Console.WriteLine($"PS2 memory card ({(vol.HasEcc ? "with ECC" : "raw")}), {vol.ClustersPerCard} clusters, " +
                          $"{vol.Saves.Count()} save(s), {vol.Files.Count()} file(s)");
        foreach (var s in vol.Saves)
        {
            Console.WriteLine($"  {s.Path.TrimStart('/')}");
            foreach (var f in vol.Files.Where(f => f.Path.StartsWith(s.Path + "/")))
                Console.WriteLine($"      {f.Path[(s.Path.Length + 1)..]}  ({f.Size:N0} bytes)");
        }
        return 0;
    }
    catch (DiscForge.Core.PlayStation.Ps2McFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Ps2McExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge ps2mc-extract <card.ps2> <out-dir>\n" +
                    "  Extracts every save folder and its files.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var card = File.ReadAllBytes(args[1]);
        var vol = DiscForge.Core.PlayStation.Ps2MemoryCard.Read(card);
        int done = 0;
        foreach (var f in vol.Files)
        {
            string outPath = Path.Combine(args[2], f.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, DiscForge.Core.PlayStation.Ps2MemoryCard.Extract(card, vol, f));
            done++;
        }
        Console.WriteLine($"Extracted {done} file(s) from {vol.Saves.Count()} save(s) to {args[2]}");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.Ps2McFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- Dreamcast VMU (memory card) -------------------------------------------

static int VmuInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge vmu-info <vmu.bin>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var vmu = DiscForge.Core.Vmu.VmuImage.Read(File.ReadAllBytes(args[1]));
        Console.WriteLine($"VMU: {(vmu.Formatted ? "formatted" : "UNFORMATTED")}, " +
                          $"{vmu.Files.Count} file(s), {vmu.FreeBlocks}/{vmu.UserBlocks} blocks free");
        foreach (var f in vmu.Files)
        {
            Console.WriteLine($"  {f.Name,-12} {(f.IsGame ? "game" : "data")} {f.SizeBlocks,3} block(s)" +
                              (f.CopyProtected ? " [copy-protected]" : ""));
            if (f.LongDescription is not null) Console.WriteLine($"      {f.LongDescription}");
        }
        return 0;
    }
    catch (DiscForge.Core.Vmu.VmuFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VmuExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge vmu-extract <vmu.bin> <out-dir> [--force]\n" +
                    "  Extracts every save as a .VMS file. --force extracts copy-protected saves.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    bool force = args.Any(a => a == "--force");
    try
    {
        var image = File.ReadAllBytes(args[1]);
        var vmu = DiscForge.Core.Vmu.VmuImage.Read(image);
        Directory.CreateDirectory(args[2]);
        int done = 0, skipped = 0;
        foreach (var f in vmu.Files)
        {
            try
            {
                var bytes = DiscForge.Core.Vmu.VmuImage.Extract(image, f, force);
                string name = f.Name.Length > 0 ? f.Name : $"save{done}";
                File.WriteAllBytes(Path.Combine(args[2], name + ".VMS"), bytes);
                done++;
            }
            catch (InvalidOperationException ex) { Console.WriteLine($"  skipped {f.Name}: {ex.Message}"); skipped++; }
        }
        Console.WriteLine($"Extracted {done} save(s) to {args[2]}" + (skipped > 0 ? $" ({skipped} skipped)" : ""));
        return 0;
    }
    catch (DiscForge.Core.Vmu.VmuFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DiskInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge disk-info <image>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    using var fs = File.OpenRead(args[1]);
    DiscForge.Core.Partition.DiskImage image;
    try
    {
        image = DiscForge.Core.Partition.PartitionTable.Read(fs);
    }
    catch (DiscForge.Core.Partition.PartitionFormatException ex)
    {
        return Fail(ex.Message);
    }

    Console.WriteLine($"Scheme: {image.Scheme}" +
                      (image.DiskGuid is not null ? $"   Disk GUID: {image.DiskGuid}" : ""));
    Console.WriteLine();
    Console.WriteLine("  #  Boot  Type                         Start          Size  Filesystem");
    Console.WriteLine("  -- ----  ---------------------------  -------------  ------------  ----------");
    foreach (var p in image.Partitions)
    {
        Console.WriteLine(
            $"  {p.Index,2}  {(p.Bootable ? "*" : " "),-4}  {Trim(p.TypeName, 27),-27}  " +
            $"{p.StartByte,13:N0}  {p.SizeBytes,12:N0}  {p.FileSystem}");
    }
    Console.WriteLine();
    Console.WriteLine($"{image.Partitions.Count} partition(s).");
    return 0;

    static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}

static int FloppyInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge floppy-info <image>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var data = File.ReadAllBytes(args[1]);
    try
    {
        if (DiscForge.Core.Floppy.D64Reader.IsD64(data))
        {
            var disk = DiscForge.Core.Floppy.D64Reader.Read(data);
            Console.WriteLine($"Format: C64 D64 ({disk.Tracks}-track)");
            Console.WriteLine($"Disk:   \"{disk.DiskName}\" ({disk.DiskId})");
            Console.WriteLine();
            foreach (var f in disk.Files)
                Console.WriteLine($"  {f.Type.ToString().ToUpperInvariant(),-3}  {f.SizeBlocks,4} blk  {f.Name}");
            Console.WriteLine();
            Console.WriteLine($"{disk.Files.Count} file(s).");
            return 0;
        }
        if (DiscForge.Core.Floppy.AdfReader.IsAdf(data))
        {
            var disk = DiscForge.Core.Floppy.AdfReader.Read(data);
            Console.WriteLine($"Format: Amiga ADF ({(disk.Ffs ? "FFS" : "OFS")})");
            Console.WriteLine($"Disk:   \"{disk.DiskName}\"");
            Console.WriteLine();
            foreach (var e in disk.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
                Console.WriteLine(e.IsDirectory ? $"  {"<DIR>",12}  {e.Path}" : $"  {e.Size,12:N0}  {e.Path}");
            Console.WriteLine();
            Console.WriteLine($"{disk.Entries.Count(e => !e.IsDirectory)} file(s).");
            return 0;
        }
        if (DiscForge.Core.Floppy.Fat12Reader.IsFat12(data))
        {
            var disk = DiscForge.Core.Floppy.Fat12Reader.Read(data);
            Console.WriteLine("Format: DOS FAT12");
            Console.WriteLine($"Volume: \"{disk.VolumeLabel}\"");
            Console.WriteLine();
            foreach (var e in disk.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
                Console.WriteLine(e.IsDirectory ? $"  {"<DIR>",12}  {e.Path}" : $"  {e.Size,12:N0}  {e.Path}");
            Console.WriteLine();
            Console.WriteLine($"{disk.Entries.Count(e => !e.IsDirectory)} file(s).");
            return 0;
        }
        return Fail("Not a recognised floppy image (D64, ADF, or FAT12).");
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
    {
        return Fail(ex.Message);
    }
}

static int FloppyExtract(string[] args)
{
    if (args.Length < 4) return Fail("usage: dforge floppy-extract <image> <path-in-image> <output-file>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var data = File.ReadAllBytes(args[1]);
    var want = args[2];
    var outPath = args[3];
    string Norm(string p) => "/" + p.Replace('\\', '/').TrimStart('/');

    try
    {
        byte[]? bytes = null;
        if (DiscForge.Core.Floppy.D64Reader.IsD64(data))
        {
            var disk = DiscForge.Core.Floppy.D64Reader.Read(data);
            // D64 has no directories; match by filename (case-insensitive).
            var entry = disk.Files.FirstOrDefault(f =>
                string.Equals(f.Name, want.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Fail($"No file named '{want}' in the image.");
            bytes = DiscForge.Core.Floppy.D64Reader.ExtractFile(data, entry);
        }
        else if (DiscForge.Core.Floppy.AdfReader.IsAdf(data))
        {
            var disk = DiscForge.Core.Floppy.AdfReader.Read(data);
            var entry = disk.Entries.FirstOrDefault(e =>
                !e.IsDirectory && string.Equals(e.Path, Norm(want), StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Fail($"No file at '{Norm(want)}' in the image.");
            bytes = DiscForge.Core.Floppy.AdfReader.ExtractFile(data, entry);
        }
        else if (DiscForge.Core.Floppy.Fat12Reader.IsFat12(data))
        {
            var disk = DiscForge.Core.Floppy.Fat12Reader.Read(data);
            var entry = disk.Entries.FirstOrDefault(e =>
                !e.IsDirectory && string.Equals(e.Path, Norm(want), StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Fail($"No file at '{Norm(want)}' in the image.");
            bytes = DiscForge.Core.Floppy.Fat12Reader.ExtractFile(data, entry);
        }
        else
        {
            return Fail("Not a recognised floppy image (D64, ADF, or FAT12).");
        }

        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"Wrote {bytes.Length:N0} bytes to {outPath}");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
    {
        return Fail(ex.Message);
    }
}

// Convert a PS1 game's video mode (PAL<->NTSC) by flipping the GPU display-mode
// bit at every display-mode command found. Video timing only — no region,
// protection, or cheat/centering changes (see docs/PSX_VIDEO_MODE.md).
static int PsxVideoModeCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge psx-video-mode <in.exe|in.bin> --to ntsc|pal [--ppf out.ppf | --out out.bin]\n" +
                    "  With neither --ppf nor --out, lists the display-mode sites it would change.\n" +
                    "  Video-mode (display timing) only; region/protection/centering are untouched.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    DiscForge.Core.PlayStation.PsxVideoMode? target = null;
    string? ppfOut = null, imgOut = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--to" && i + 1 < args.Length)
            target = args[++i].ToLowerInvariant() switch
            {
                "ntsc" => DiscForge.Core.PlayStation.PsxVideoMode.Ntsc,
                "pal" => DiscForge.Core.PlayStation.PsxVideoMode.Pal,
                _ => null,
            };
        else if (args[i] == "--ppf" && i + 1 < args.Length) ppfOut = args[++i];
        else if (args[i] == "--out" && i + 1 < args.Length) imgOut = args[++i];
    }
    if (target is null) return Fail("Give the target mode with --to ntsc  or  --to pal.");

    try
    {
        var data = File.ReadAllBytes(args[1]);
        var sites = DiscForge.Core.PlayStation.Ps1VideoModePatcher.FindSites(data, target.Value);

        Console.WriteLine($"{sites.Count:N0} display-mode site(s) would convert to {target.Value.ToString().ToUpperInvariant()}.");
        foreach (var s in sites)
            Console.WriteLine($"  @0x{s.Offset:X}: {s.CurrentMode} (0x{s.OldParam:X2}) -> 0x{s.NewParam:X2}");
        if (sites.Count == 0) { Console.WriteLine("Nothing to change."); return 0; }

        if (ppfOut is not null)
        {
            var ppf = DiscForge.Core.PlayStation.Ps1VideoModePatcher.CreatePpf(data, target.Value);
            File.WriteAllBytes(ppfOut, ppf!);
            Console.WriteLine($"Wrote PPF {Path.GetFileName(ppfOut)} ({ppf!.Length:N0} bytes, undoable).");
        }
        if (imgOut is not null)
        {
            var copy = (byte[])data.Clone();
            DiscForge.Core.PlayStation.Ps1VideoModePatcher.PatchInPlace(copy, target.Value);
            File.WriteAllBytes(imgOut, copy);
            Console.WriteLine($"Wrote converted image {Path.GetFileName(imgOut)}.");
        }
        if (ppfOut is null && imgOut is null)
            Console.WriteLine("(Listing only — pass --ppf or --out to write the conversion.)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Identify a CD-R/CD-RW's dye/stamper manufacturer from its ATIP — the "CDR
// Identifier" job. Decodes a saved raw ATIP response; reading ATIP straight from a
// drive is the Windows device layer's job (the GUI Drives/Media view), which then
// calls this same MediaIdentityParser.
static int CdrInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cdr-info <atip-dump-file>\n" +
                    "  Decodes a raw READ TOC/PMA/ATIP response and names the media maker where known.\n" +
                    "  (Reading ATIP live from a drive is done in the Windows GUI.)");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var id = DiscForge.Core.Media.MediaIdentityParser.ParseAtip(File.ReadAllBytes(args[1]));
        if (id is null) return Fail("No ATIP in that dump — a pressed (non-recordable) disc carries none.");

        if (id.AtipCode is not null)
        {
            Console.WriteLine($"ATIP media code: {id.AtipCode}");
            Console.WriteLine($"  manufacturer : {id.Manufacturer ?? "(not in the table — look up the code)"}");
            Console.WriteLine($"  type         : {(id.IsRewritable ? "CD-RW" : "CD-R")}");
            if (id.CapacityMb is double mb) Console.WriteLine($"  capacity     : {mb:N0} MB");
            if (id.LeadOut is (int m, int s, int f)) Console.WriteLine($"  lead-out     : {m:00}:{s:00}:{f:00}");
        }
        else if (id.MediaId is not null)
        {
            Console.WriteLine($"DVD/BD media ID: {id.MediaId}");
            Console.WriteLine($"  manufacturer : {id.Manufacturer ?? "(not in the table)"}");
            if (id.BookTypeName is not null) Console.WriteLine($"  book type    : {id.BookTypeName}");
        }
        foreach (var n in id.Notes) Console.WriteLine($"  note: {n}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TmdInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge tmd-info <file.tmd>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var model = DiscForge.Core.Media.Tmd.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine($"TMD: {model.Objects.Count} object(s), {model.VertexTotal:N0} vertices total");
        for (int i = 0; i < model.Objects.Count; i++)
        {
            var o = model.Objects[i];
            Console.WriteLine($"  obj {i}: {o.Vertices.Count:N0} vert, {o.Normals.Count:N0} normal, " +
                              $"{o.PrimitiveCount:N0} primitive(s), {o.Faces.Count:N0} face(s), scale 2^{o.Scale}");
        }
        return 0;
    }
    catch (DiscForge.Core.Media.TmdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Tmd2Dxf(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge tmd2dxf <file.tmd> <out.dxf>\n" +
                    "  Exports the model as DXF 3DFACE polygons (common flat/gouraud, tri/quad,\n" +
                    "  textured or not); objects with no recognised faces fall back to a point cloud.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var model = DiscForge.Core.Media.Tmd.Parse(File.ReadAllBytes(args[1]));
        File.WriteAllText(args[2], DiscForge.Core.Media.Tmd.ToDxf(model));
        int faces = model.Objects.Sum(o => o.Faces.Count);
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: {faces:N0} face(s), {model.VertexTotal:N0} vertices from " +
                          $"{model.Objects.Count} object(s).");
        return 0;
    }
    catch (DiscForge.Core.Media.TmdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TodInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge tod-info <file.tod>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var tod = DiscForge.Core.Media.Tod.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine($"TOD: version {tod.Version}, resolution {tod.Resolution}, {tod.Frames.Count:N0} frame(s)");
        int totalPackets = tod.Frames.Sum(f => f.Packets.Count);
        Console.WriteLine($"  {totalPackets:N0} packet(s) across all frames");
        Console.WriteLine("  (TOD structure per the public Sony description; validated by round trip, " +
                          "real-file validation pending a sample.)");
        return 0;
    }
    catch (DiscForge.Core.Media.TodFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VagExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge vag-extract <file.vag> <out.wav>\n" +
                    "  Decodes a PlayStation VAG (SPU-ADPCM) sample to a mono WAV.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var info = DiscForge.Core.PlayStation.Vag.ReadInfo(data);
        File.WriteAllBytes(args[2], DiscForge.Core.PlayStation.Vag.ToWav(data));
        int rate = info.SampleRate > 0 ? info.SampleRate : 44100;
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: mono @ {rate} Hz" +
                          (info.Name.Length > 0 ? $" (\"{info.Name}\")" : "") + ".");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.VagFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int AdxDecodeCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge adx-decode <in.adx> <out.wav>\n" +
                    "  Decodes a CRI ADX ADPCM stream (type 0x02/0x03) to a 16-bit PCM WAV.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var info = DiscForge.Core.GameAudio.AdxReader.ReadInfo(data);
        using (var outWav = File.Create(args[2]))
            DiscForge.Core.GameAudio.AdxDecoder.DecodeToWav(new MemoryStream(data), outWav);
        double seconds = info.SampleRate > 0 ? info.TotalSamples / (double)info.SampleRate : 0;
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: {info.Channels}ch @ {info.SampleRate} Hz, " +
                          $"{info.TotalSamples:N0} samples, {seconds:N1}s (encoding 0x{info.Encoding:X2}).");
        return 0;
    }
    catch (DiscForge.Core.GameAudio.AdxFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ReadOffsetCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge read-offset <samples> [in.wav out.wav]\n" +
                    "  <samples> is the combined read offset (drive read offset + disc write offset).\n" +
                    "  With no WAV, prints the offset arithmetic. With in/out WAVs, slides the audio.");
    if (!int.TryParse(args[1], out int samples))
        return Fail($"Not a sample count: '{args[1]}'.");

    // No file → just the arithmetic (bytes, and how many sectors a real dump must over-read).
    if (args.Length < 4)
    {
        Console.WriteLine($"Combined read offset: {samples:+#;-#;0} samples ({DiscForge.Core.Audio.ReadOffset.SamplesToBytes(samples):+#;-#;0} bytes).");
        Console.WriteLine($"Guard-band over-read needed: {DiscForge.Core.Audio.ReadOffset.OverreadSectors(samples)} sector(s) each edge " +
                          $"({DiscForge.Core.Audio.ReadOffset.SamplesPerSector} samples/sector).");
        return 0;
    }

    string inPath = args[2], outPath = args[3];
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");
    try
    {
        using var fs = File.OpenRead(inPath);
        var info = DiscForge.Core.Audio.WavReader.Read(fs);
        if (!info.IsCdAudioFormat)
            return Fail("read-offset needs a 44100 Hz / 16-bit / stereo (CD-DA) WAV.");

        var pcm = new byte[info.DataLength];
        fs.Seek(info.DataOffset, SeekOrigin.Begin);
        fs.ReadExactly(pcm, 0, pcm.Length);

        if (!DiscForge.Core.Audio.ReadOffset.ShiftDiscardsOnlySilence(pcm, samples))
            Console.WriteLine("Note: the edge this shift drops is not silent — a real dump should over-read the " +
                              "guard band instead of padding silence (see docs/REDUMP_PHYSICAL.md).");

        byte[] shifted = DiscForge.Core.Audio.ReadOffset.Apply(pcm, samples);
        var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(shifted);
        using (var os = File.Create(outPath))
            DiscForge.Core.Audio.WavWriter.Write(os, shorts, info.SampleRate, info.Channels);

        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: slid {samples:+#;-#;0} samples " +
                          $"({pcm.Length / DiscForge.Core.Audio.ReadOffset.BytesPerSample:N0} samples of CD audio).");
        return 0;
    }
    catch (DiscForge.Core.Audio.WavFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VabInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge vab-info <file.vab>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var vab = DiscForge.Core.PlayStation.Vab.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine($"VAB v{vab.Version} (id 0x{vab.VabId:X}): {vab.ProgramCount} program(s), " +
                          $"{vab.ToneCount} tone(s), {vab.VagCount} VAG waveform(s).");
        Console.WriteLine($"Master volume {vab.MasterVolume}, pan {vab.MasterPan}.");
        foreach (var p in vab.Programs)
        {
            Console.WriteLine($"  Program {p.Index}: {p.ToneCount} tone(s), vol {p.Volume}, pan {p.Pan}");
            foreach (var t in p.Tones)
                Console.WriteLine($"    tone -> VAG {t.Vag}, note {t.NoteLow}-{t.NoteHigh} (center {t.CenterNote}), " +
                                  $"vol {t.Volume}, pan {t.Pan}");
        }
        return 0;
    }
    catch (DiscForge.Core.PlayStation.VabFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SeqInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge seq-info <file.seq>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var seq = DiscForge.Core.PlayStation.Seq.Parse(File.ReadAllBytes(args[1]));
        double bpm = seq.Tempo > 0 ? 60_000_000.0 / seq.Tempo : 0;
        Console.WriteLine($"SEQ v{seq.Version}: {seq.Ppqn} ppqn, tempo {seq.Tempo} " +
                          $"({bpm:N1} BPM), {seq.EventCount} event(s).");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.SeqFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int StrDemuxCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge str-demux <in.str> <out-dir> [--sector-size 2352|2048]\n" +
                    "  Splits a PSX .str into per-frame MDEC bitstreams and reports XA audio.\n" +
                    "  MDEC pixel decode is deferred — see docs/PSX_MEDIA.md.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var layout = DiscForge.Core.PlayStation.StrDemuxer.Layout.Raw2352;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--sector-size" && i + 1 < args.Length)
            layout = args[++i] == "2048"
                ? DiscForge.Core.PlayStation.StrDemuxer.Layout.UserData2048
                : DiscForge.Core.PlayStation.StrDemuxer.Layout.Raw2352;

    try
    {
        Directory.CreateDirectory(args[2]);
        DiscForge.Core.PlayStation.StrDemuxResult result;
        using (var img = File.OpenRead(args[1]))
            result = DiscForge.Core.PlayStation.StrDemuxer.Demux(img, layout);

        foreach (var f in result.Frames)
        {
            string name = Path.Combine(args[2], $"frame_{f.FrameNumber:D5}.mdec");
            File.WriteAllBytes(name, f.Bitstream);
        }

        Console.WriteLine($"Demuxed {result.TotalSectorCount:N0} sector(s): " +
                          $"{result.Frames.Count} video frame(s) ({result.VideoSectorCount} sector(s)), " +
                          $"{result.AudioSectors.Count} XA-audio sector(s).");
        if (result.Frames.Count > 0)
        {
            var first = result.Frames[0];
            Console.WriteLine($"  Frame size {first.Width}x{first.Height}; wrote frame_*.mdec bitstreams to {args[2]}.");
        }
        Console.WriteLine("  MDEC pixel decode is deferred (docs/PSX_MEDIA.md) — bitstreams written, not images.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int XaExtractCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge xa-extract <raw image> <out.wav> [--sector-size 2352|2336] [--channel N]\n" +
                    "  Decodes CD-ROM XA ADPCM audio from a raw disc image to a WAV.\n" +
                    "  Rate and mono/stereo are read from the XA subheader.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var layout = DiscForge.Core.PlayStation.XaExtract.SectorLayout.Raw2352;
    int? channel = null;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--sector-size" && i + 1 < args.Length)
            layout = args[++i] == "2336"
                ? DiscForge.Core.PlayStation.XaExtract.SectorLayout.Mode2_2336
                : DiscForge.Core.PlayStation.XaExtract.SectorLayout.Raw2352;
        else if (args[i] == "--channel" && i + 1 < args.Length) channel = int.Parse(args[++i]);
    }

    try
    {
        DiscForge.Core.PlayStation.XaExtract.Result result;
        using (var img = File.OpenRead(args[1]))
            result = DiscForge.Core.PlayStation.XaExtract.Extract(img, layout, channel);

        using (var outWav = File.Create(args[2]))
            DiscForge.Core.Audio.WavWriter.Write(outWav, result.Pcm, result.SampleRate, result.Channels);

        double seconds = result.Pcm.Length / (double)(result.SampleRate * result.Channels);
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: {result.Channels}ch @ {result.SampleRate} Hz, " +
                          $"{result.SectorsUsed:N0} XA sector(s), {seconds:N1}s.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TimInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge tim-info <file.tim>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var tim = DiscForge.Core.Media.Tim.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine($"TIM {tim.Mode.ToString().Replace("Bpp", "")}bpp, {tim.Width}x{tim.Height}, " +
                          $"{tim.PaletteCount} palette(s)");
        return 0;
    }
    catch (DiscForge.Core.Media.TimFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TimExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge tim-extract <file.tim> <out.png> [--palette N]\n" +
                    "  Decodes a TIM to a PNG (palette N selects a CLUT for 4/8bpp images).");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    int palette = 0;
    for (int i = 3; i < args.Length - 1; i++)
        if (args[i] == "--palette") palette = int.Parse(args[i + 1]);

    try
    {
        var tim = DiscForge.Core.Media.Tim.Parse(File.ReadAllBytes(args[1]));
        File.WriteAllBytes(args[2], DiscForge.Core.Media.Tim.ToPng(tim, palette));
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])} ({tim.Width}x{tim.Height} RGBA PNG).");
        return 0;
    }
    catch (DiscForge.Core.Media.TimFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ToCcd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge to-ccd <image.cue> [--out <basename>]\n" +
                    "  Writes <basename>.ccd (and expects the matching .img/.sub alongside).");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string outBase = Path.GetFileNameWithoutExtension(args[1]);
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--out") outBase = args[i + 1];

    try
    {
        var ext = Path.GetExtension(args[1]).ToLowerInvariant();
        if (ext != ".cue")
            return Fail("to-ccd takes a .cue sheet (full TOC). " +
                        "Use build-raw to make the .img/.sub, then to-ccd on the .cue.");

        bool verbatim = args.Contains("--verbatim");
        using var layout = DiscLayout.FromCueFile(args[1], subVerbatim: verbatim);

        string ccd = CloneCdWriter.BuildCcd(layout);
        var names = CloneCdWriter.NamesFor(outBase);
        File.WriteAllText(names.Ccd, ccd);

        Console.WriteLine($"Wrote {names.Ccd}");
        Console.WriteLine($"  Expects alongside it: {Path.GetFileName(names.Img)}" +
            (layout.HasVerbatimSubchannel ? $" and {Path.GetFileName(names.Sub)}" : ""));
        Console.WriteLine();
        Console.WriteLine("Tip: generate the .img/.sub with:");
        Console.WriteLine($"  dforge build-raw \"{args[1]}\" \"{names.Img}\"" +
            (layout.HasVerbatimSubchannel ? " --verbatim" : ""));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Checksum(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge checksum <file> [--write [sha256|md5|sha1|sfv|all]] [--verify]");

    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    bool verify = args.Contains("--verify");
    string? write = null;
    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--write")
            write = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "sha256";

    try
    {
        long size = new FileInfo(path).Length;
        Console.WriteLine($"{Path.GetFileName(path)} ({size:N0} bytes)");
        long lastPct = -1;
        var sums = ImageChecksums.ComputeFile(path, new Progress<double>(f =>
        {
            long pct = (long)(f * 100);
            if (pct != lastPct && pct % 10 == 0) { lastPct = pct; Console.Write($"\r  hashing… {pct}%"); }
        }));
        Console.WriteLine("\r  hashing… done ");
        Console.WriteLine($"  CRC-32  {sums.Crc32}");
        Console.WriteLine($"  MD5     {sums.Md5}");
        Console.WriteLine($"  SHA-1   {sums.Sha1}");
        Console.WriteLine($"  SHA-256 {sums.Sha256}");

        if (write is not null)
        {
            var algos = write.ToLowerInvariant() == "all"
                ? new[] { "sha256", "md5", "sha1", "crc32" }
                : new[] { write.ToLowerInvariant() == "sfv" ? "crc32" : write.ToLowerInvariant() };
            foreach (var a in algos)
                Console.WriteLine($"  wrote {ImageChecksums.WriteSidecar(path, sums, a)}");
        }

        if (verify)
        {
            var sidecar = ImageChecksums.FindSidecar(path);
            if (sidecar is null)
                return Fail("No sidecar (.sha256/.sha1/.md5/.sfv) found next to the file.");
            string got = ImageChecksums.ValueFor(sums, sidecar.Algorithm);
            bool ok = got.Equals(sidecar.ExpectedHex, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(ok
                ? $"  VERIFIED against {Path.GetFileName(sidecar.SidecarPath)} ({sidecar.Algorithm})"
                : $"  MISMATCH: {Path.GetFileName(sidecar.SidecarPath)} says {sidecar.ExpectedHex}, " +
                  $"file is {got}");
            return ok ? 0 : 1;
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ScummvmDetect(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge scummvm-detect <folder|file> [--recursive] [--bytes N]");

    var path = args[1];
    bool recursive = args.Contains("--recursive");
    int bytes = ScummVmFingerprint.DefaultBytes;
    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--bytes" && i + 1 < args.Length && int.TryParse(args[i + 1], out int b) && b > 0)
            bytes = b;

    try
    {
        IReadOnlyList<ScummVmFingerprint.Fingerprint> prints;
        if (Directory.Exists(path))
            prints = ScummVmFingerprint.ForDirectory(path, recursive, bytes);
        else if (File.Exists(path))
            prints = new[] { ScummVmFingerprint.ForFile(path, bytes) };
        else
            return Fail($"'{path}' not found.");

        if (prints.Count == 0)
        {
            Console.WriteLine("No files found to fingerprint.");
            return 0;
        }

        Console.WriteLine($"ScummVM Advanced-Detector fingerprints (MD5 of first {bytes} bytes):");
        Console.WriteLine();
        int nameWidth = Math.Min(48, prints.Max(p => p.Name.Length));
        foreach (var p in prints)
            Console.WriteLine($"  {p.Name.PadRight(nameWidth)}  {p.Size,12:N0}  {p.Md5}");
        Console.WriteLine();
        Console.WriteLine("Match the (size, md5) pairs above against a game's entry on the ScummVM wiki");
        Console.WriteLine("or its detection tables to identify the title.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ScummvmExport(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge scummvm-export <image.cue> <outdir> [--flac|--ogg] [--high]");

    var cuePath = args[1];
    var outDir = args[2];
    if (!File.Exists(cuePath)) return Fail($"'{cuePath}' not found.");
    var format = args.Contains("--flac") ? ScummVmExport.AudioFormat.Flac
               : args.Contains("--ogg") ? ScummVmExport.AudioFormat.Ogg
               : ScummVmExport.AudioFormat.Wav;
    var oggQuality = args.Contains("--high") ? VorbisEncoder.Quality.High : VorbisEncoder.Quality.Standard;

    try
    {
        var result = ScummVmExport.Export(cuePath, outDir, format, oggQuality);
        Console.WriteLine($"Exported to {outDir}:");
        Console.WriteLine($"  data files extracted: {result.DataFilesExtracted}");
        Console.WriteLine($"  audio tracks: {result.AudioTracks.Count} ({result.AudioFormatWritten.ToString().ToLowerInvariant()})");
        foreach (var a in result.AudioTracks)
            Console.WriteLine($"    {Path.GetFileName(a.Path)}  ({a.Sectors:N0} sectors)");
        foreach (var w in result.Warnings)
            Console.WriteLine($"  note: {w}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IpBinInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge ipbin-info <image>   (.gdi, .cue, .cdi, or a raw .bin/.iso)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        IpBinHeader? header = IpBin.Identify(path);

        if (header is null)
            return Fail("No Dreamcast boot header (\"SEGA SEGAKATANA\") was found — " +
                        "this image has no bootable Dreamcast data track, or the format isn't supported here.");

        Console.WriteLine($"Dreamcast disc — {Path.GetFileName(path)}");
        Console.WriteLine($"  Title:       {header.Title}");
        Console.WriteLine($"  Product:     {header.ProductNumber}  {header.Version}");
        Console.WriteLine($"  Maker:       {header.Maker}");
        Console.WriteLine($"  Device:      {header.DeviceInfo}");
        Console.WriteLine($"  Region:      {(header.Regions.Count > 0 ? string.Join(", ", header.Regions) : "none")}" +
                          (header.RegionCode.Length > 0 ? $"  ({header.RegionCode})" : ""));
        Console.WriteLine($"  Released:    {header.ReleaseDate}");
        Console.WriteLine($"  Boot file:   {header.BootFile}");
        Console.WriteLine($"  Peripherals: {header.Peripherals}");
        foreach (var p in header.SupportedPeripherals)
            Console.WriteLine($"      - {p}");
        return 0;
    }
    catch (IpBinFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SaturnInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge saturn-info <image>   (.cue, .cdi, or a raw .bin/.iso)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        var header = DiscForge.Core.Saturn.SaturnDisc.Identify(path);
        if (header is null)
            return Fail("No Saturn disc header (\"SEGA SEGASATURN\") was found — " +
                        "this image has no Saturn data track, or the format isn't supported here.");

        Console.WriteLine($"Sega Saturn disc — {Path.GetFileName(path)}");
        Console.WriteLine($"  Title:       {header.Title}");
        Console.WriteLine($"  Product:     {header.ProductNumber}  {header.Version}");
        Console.WriteLine($"  Maker:       {header.MakerId}");
        Console.WriteLine($"  Device:      {header.DeviceInfo}");
        Console.WriteLine($"  Region:      {(header.Regions.Count > 0 ? string.Join(", ", header.Regions) : "none")}" +
                          (header.AreaSymbols.Length > 0 ? $"  ({header.AreaSymbols})" : ""));
        Console.WriteLine($"  Released:    {header.ReleaseDate}");
        Console.WriteLine($"  Peripherals: {header.Peripherals}");
        foreach (var p in header.SupportedPeripherals)
            Console.WriteLine($"      - {p}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int RomInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge rom-info <file>   (a cartridge ROM: .n64/.z64/.v64, .sfc/.smc, .md/.gen, .gb/.gbc, .gba, .nes, …)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    byte[] rom;
    try { rom = File.ReadAllBytes(path); }
    catch (Exception ex) { return Fail(ex.Message); }

    var id = DiscForge.Core.Rom.RomIdentify.Identify(rom);
    if (!id.Recognised)
        return Fail("No known cartridge-ROM signature matched — this is not a ROM format DiscForge identifies.");

    Console.WriteLine($"Cartridge ROM — {Path.GetFileName(path)} ({rom.Length:N0} bytes)");
    Console.WriteLine($"  Platform:   {id.Platform}");
    if (id.Title.Length > 0) Console.WriteLine($"  Title:      {id.Title}");
    if (id.GameCode.Length > 0) Console.WriteLine($"  GameCode:   {id.GameCode}");
    if (id.Region.Length > 0) Console.WriteLine($"  Region:     {id.Region}");
    foreach (var kv in id.Extra)
        Console.WriteLine($"  {kv.Key,-11} {kv.Value}");

    var h = DiscForge.Core.Rom.RomHashes.Compute(rom, id);
    Console.WriteLine("  Hashes (No-Intro; excluded copier headers stripped):");
    Console.WriteLine($"    CRC32:    {h.Crc32Hex}");
    Console.WriteLine($"    MD5:      {h.Md5}");
    Console.WriteLine($"    SHA1:     {h.Sha1}");

    foreach (var w in id.Warnings)
        Console.WriteLine($"  ! {w}");
    return 0;
}

static int CdInteractiveConsoleInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cdi-console-info <image>   (a CD-i .iso or raw Mode 2 track)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        using var fs = File.OpenRead(path);
        if (!DiscForge.Core.CdInteractive.CdInteractiveReader.IsCdInteractive(fs))
            return Fail("No CD-i signature at sector 16 — this image is neither pure CD-i " +
                        "(\"CD-I \" standard id) nor a CD-i Bridge disc (\"CD-RTOS\" system id).");

        fs.Seek(0, SeekOrigin.Begin);
        var disc = DiscForge.Core.CdInteractive.CdInteractiveReader.Read(fs);

        string kind = disc.Kind == DiscForge.Core.CdInteractive.CdInteractiveKind.PureCdi
            ? "pure CD-i (Green Book)" : "CD-i Bridge";
        Console.WriteLine($"Philips CD-i — {Path.GetFileName(path)}");
        Console.WriteLine($"  Kind:        {kind}");
        Console.WriteLine($"  Volume:      {disc.VolumeId}");
        Console.WriteLine($"  System:      {disc.SystemId}");
        if (disc.ApplicationId.Length > 0)
            Console.WriteLine($"  Application: {disc.ApplicationId}");

        int files = disc.Filesystem.Files.Count();
        int dirs = disc.Filesystem.Directories.Count();
        Console.WriteLine($"  Filesystem:  {files} file(s), {dirs} director(y/ies), " +
                          $"{disc.Filesystem.TotalBytes:N0} bytes");
        foreach (var f in disc.Filesystem.Files.Take(10))
            Console.WriteLine($"      {f.Path}  ({f.Size:N0} bytes)");
        if (files > 10)
            Console.WriteLine($"      … and {files - 10} more");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PspInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge psp-info <image> [--sfo]   (.iso, .cso or .zso)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    bool dumpSfo = args.Contains("--sfo");

    try
    {
        var game = DiscForge.Core.Psp.PspUmdReader.Read(path);

        Console.WriteLine($"PSP UMD — {Path.GetFileName(path)}");
        Console.WriteLine($"  Disc ID:      {game.DiscId}");
        Console.WriteLine($"  Title:        {game.Title}");
        Console.WriteLine($"  Category:     {game.Category}");
        Console.WriteLine($"  Disc version: {game.DiscVersion}");
        if (game.Region.Length > 0)
            Console.WriteLine($"  Region:       {game.Region}");

        int files = game.Filesystem.Files.Count();
        int dirs = game.Filesystem.Directories.Count();
        Console.WriteLine($"  Filesystem:   {files} file(s), {dirs} director(y/ies), " +
                          $"{game.Filesystem.TotalBytes:N0} bytes");

        string[] notable = { "EBOOT.BIN", "PARAM.SFO", "ICON0.PNG", "PIC1.PNG", "UPDATE/EBOOT.BIN" };
        foreach (var name in notable)
        {
            var hit = game.Filesystem.Files.FirstOrDefault(
                f => f.Path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                Console.WriteLine($"      {hit.Path}  ({hit.Size:N0} bytes)");
        }

        if (dumpSfo)
        {
            Console.WriteLine("  PARAM.SFO:");
            foreach (var (key, value) in game.Sfo.Entries)
                Console.WriteLine($"      {key,-18} = {(value.IsInt ? value.Number.ToString() : value.Text)}");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PbpInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge pbp-info <EBOOT.PBP>");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        var bytes = File.ReadAllBytes(path);
        var pbp = DiscForge.Core.Psp.PbpFile.Parse(bytes);

        Console.WriteLine($"PBP package — {Path.GetFileName(path)}");
        Console.WriteLine($"  Version: 0x{pbp.Version:X8}");
        Console.WriteLine("  Sections:");
        foreach (var s in pbp.Sections)
            Console.WriteLine($"      {s.Name,-10} {s.Size,12:N0} bytes  @ 0x{s.Offset:X}" +
                              (s.IsEmpty ? "  (empty)" : ""));

        var sfo = DiscForge.Core.Psp.PbpFile.GetParamSfo(bytes);
        if (sfo is not null)
        {
            Console.WriteLine("  PARAM.SFO:");
            Console.WriteLine($"      Title:    {sfo.GetString("TITLE")}");
            Console.WriteLine($"      Disc ID:  {sfo.GetString("DISC_ID")}");
            Console.WriteLine($"      Category: {sfo.GetString("CATEGORY")}");
        }
        else
        {
            Console.WriteLine("  PARAM.SFO: (none)");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PbpExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge pbp-extract <EBOOT.PBP> <output-dir>");
    var path = args[1];
    var outDir = args[2];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        var bytes = File.ReadAllBytes(path);
        var pbp = DiscForge.Core.Psp.PbpFile.Parse(bytes);
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"PBP package — {Path.GetFileName(path)}  (version 0x{pbp.Version:X8})");
        int written = 0;
        foreach (var s in pbp.Sections)
        {
            if (s.IsEmpty) continue;
            var dest = Path.Combine(outDir, s.Name);
            var data = DiscForge.Core.Psp.PbpFile.GetSection(bytes, s.Name);
            File.WriteAllBytes(dest, data);
            string note = s.Name == "DATA.PSP" ? "  (raw — not decrypted)" : "";
            Console.WriteLine($"  wrote {s.Name,-10} {s.Size,12:N0} bytes -> {dest}{note}");
            written++;
        }
        Console.WriteLine($"  {written} section(s) written. DATA.PSP, if present, is extracted raw and not decrypted.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GcmInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge gcm-info <image>   (GameCube .gcm/.iso or a Wii disc)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        using var fs = File.OpenRead(path);

        // A Wii disc (magic at 0x18) is checked first: for it we report only the
        // unencrypted volume header and partition table — never partition contents.
        if (WiiDisc.IsWii(fs))
        {
            var vol = WiiDisc.Read(fs);
            Console.WriteLine($"Nintendo Wii disc — {Path.GetFileName(path)}");
            Console.WriteLine($"  Game code:  {vol.GameCode}");
            Console.WriteLine($"  Title:      {vol.GameName}");
            Console.WriteLine($"  Partitions: {vol.Partitions.Count}");
            foreach (var p in vol.Partitions)
            {
                Console.WriteLine($"      - {p.Type,-13} (raw {p.RawType})  at 0x{p.Offset:X}");

                // Plaintext ticket + TMD metadata only. If a partition's details can't be
                // read (truncated/out-of-range), print the bare line rather than failing.
                WiiPartitionDetails? d = null;
                try { d = WiiDisc.ReadPartitionDetails(fs, p); }
                catch (GameCubeFormatException) { }
                if (d is not null)
                {
                    var idLabel = d.GameId is not null ? $"{d.TitleId} ({d.GameId})" : d.TitleId;
                    Console.WriteLine($"          Title id:    {idLabel}");
                    Console.WriteLine($"          Version:     {d.TitleVersion}   Contents: {d.ContentCount}");
                    Console.WriteLine($"          Data region: 0x{d.DataOffset:X}, {d.DataSize:N0} bytes (not read)");
                }
            }
            Console.WriteLine("  (Wii partition ticket/TMD metadata is plaintext; the encrypted");
            Console.WriteLine("   partition contents and title keys are intentionally not read.)");
            return 0;
        }

        if (!GcmReader.IsGcm(fs))
            return Fail("Not a GameCube or Wii disc (no 0xC2339F3D / 0x5D1C9EA3 magic).");

        var disc = GcmReader.Read(fs);
        Console.WriteLine($"Nintendo GameCube disc — {Path.GetFileName(path)}");
        Console.WriteLine($"  Game code:  {disc.GameCode}   Maker: {disc.MakerCode}");
        Console.WriteLine($"  Title:      {disc.GameName}");
        Console.WriteLine($"  Disc/Ver:   disc {disc.DiscId}, version {disc.Version}");

        int files = disc.Entries.Count(e => !e.IsDirectory);
        int dirs = disc.Entries.Count(e => e.IsDirectory);
        long totalBytes = disc.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        Console.WriteLine($"  Filesystem: {files} file(s), {dirs} director(y/ies), {totalBytes:N0} bytes");
        foreach (var e in disc.Entries)
        {
            if (e.IsDirectory)
                Console.WriteLine($"      {e.Path}/");
            else
                Console.WriteLine($"      {e.Path}  ({e.Size:N0} bytes @ 0x{e.Offset:X})");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ShowRvzInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge rvz-info <image.rvz|image.wia>");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        using var fs = File.OpenRead(path);
        var info = RvzReader.ReadInfo(fs);
        Console.WriteLine($"{info.Format} container — {Path.GetFileName(path)}");
        Console.WriteLine($"  Version:     0x{info.Version:X8}");
        Console.WriteLine($"  Compression: {info.Compression} (level {info.CompressionLevel})");
        Console.WriteLine($"  Chunk size:  {info.ChunkSize:N0} bytes");
        Console.WriteLine($"  ISO size:    {info.IsoSize:N0} bytes");
        Console.WriteLine($"  Game id:     {info.GameId}");
        Console.WriteLine($"  Title:       {info.GameName}");
        Console.WriteLine("  (Full RVZ/WIA -> ISO decompression is deferred; identification only.)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BdmvInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge bdmv-info <file.mpls|file.clpi|BDMV-folder>");
    var path = args[1];

    try
    {
        if (Directory.Exists(path))
        {
            var titles = BdmvReader.EnumerateTitles(path);
            Console.WriteLine($"Blu-ray BDMV — {titles.Count} playlist(s) in {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}");
            foreach (var t in titles)
            {
                Console.WriteLine($"  {t.PlaylistFile}: {BdmvTime.Format(t.Playlist.TotalDurationTicks)}, " +
                                  $"{t.Playlist.Items.Count} item(s), {t.ChapterCount} chapter(s), " +
                                  $"clip(s): {string.Join(", ", t.ClipIds)}");
            }
            return 0;
        }

        if (!File.Exists(path)) return Fail($"'{path}' not found.");
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".mpls")
        {
            var pl = BdmvReader.ReadPlaylist(path);
            PrintPlaylist(Path.GetFileName(path), pl);
            return 0;
        }
        if (ext == ".clpi")
        {
            var clip = BdmvReader.ReadClip(path);
            PrintClip(Path.GetFileName(path), clip);
            return 0;
        }
        return Fail("Expected a .mpls, a .clpi, or a BDMV folder.");
    }
    catch (Exception ex) { return Fail(ex.Message); }

    static void PrintPlaylist(string name, BluRayPlaylist pl)
    {
        Console.WriteLine($"Blu-ray playlist — {name} (MPLS {pl.Version})");
        Console.WriteLine($"  Duration: {BdmvTime.Format(pl.TotalDurationTicks)}   " +
                          $"Items: {pl.Items.Count}   Chapters: {pl.Chapters.Count}");
        for (int i = 0; i < pl.Items.Count; i++)
        {
            var it = pl.Items[i];
            Console.WriteLine($"  Item {i}: clip {it.ClipFileName}  " +
                              $"in {BdmvTime.Format(it.InTime)} -> out {BdmvTime.Format(it.OutTime)}  " +
                              $"({BdmvTime.Format(it.DurationTicks)})");
            foreach (var s in it.Streams)
            {
                string lang = string.IsNullOrEmpty(s.Language) ? "" : $"  [{s.Language}]";
                Console.WriteLine($"      {s.Kind,-20} PID 0x{s.Pid:X4}  {s.CodingName}{lang}");
            }
        }
        if (pl.Chapters.Count > 0)
        {
            Console.WriteLine("  Chapters:");
            for (int c = 0; c < pl.Chapters.Count; c++)
            {
                var m = pl.Chapters[c];
                Console.WriteLine($"      {c + 1,2}. item {m.PlayItemRef} @ {BdmvTime.Format(m.TimeTicks)}");
            }
        }
    }

    static void PrintClip(string name, BluRayClip clip)
    {
        Console.WriteLine($"Blu-ray clip-info — {name} (HDMV {clip.Version})");
        Console.WriteLine($"  Streams: {clip.Streams.Count}");
        foreach (var s in clip.Streams)
        {
            string attrs = s.Kind switch
            {
                StreamKind.Video => $"  {s.VideoFormat} {s.FrameRate}fps {s.AspectRatio}",
                StreamKind.Audio => $"  {s.AudioFormat} {s.SampleRate}",
                _ => "",
            };
            string lang = string.IsNullOrEmpty(s.Language) ? "" : $"  [{s.Language}]";
            Console.WriteLine($"    {s.Kind,-20} PID 0x{s.Pid:X4}  {s.CodingName}{attrs}{lang}");
        }
    }
}

static int MilcdToCdi(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge milcd-to-cdi <in.cue> <out.cdi> [--version v2|v3|v35] [--gap <sectors>]");

    var input = args[1];
    var output = args[2];
    if (!File.Exists(input)) return Fail($"'{input}' not found.");
    if (!input.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        return Fail("Input must be a .cue — a Redump MIL-CD bin/cue rip.");
    if (!output.EndsWith(".cdi", StringComparison.OrdinalIgnoreCase))
        return Fail("Output must be a .cdi.");

    var version = ParseVersionArg(args) ?? CdiVersion.V35;
    uint gap = CdiConverter.MultisessionGap;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--gap" && i + 1 < args.Length && uint.TryParse(args[i + 1], out uint g))
            gap = g;

    try
    {
        var cueText = File.ReadAllText(input);
        var cueDir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
        var sheet = CueSheet.Parse(cueText);
        var sessions = sheet.Tracks.Select(t => t.Session).Distinct().OrderBy(n => n).ToList();

        Console.WriteLine($"{sheet.Tracks.Count} track(s) across {sessions.Count} session(s).");
        if (sessions.Count < 2)
            Console.WriteLine("  note: no 'REM SESSION' markers found — a MIL-CD self-boot disc is two-session. " +
                              "The CDI will be single-session; confirm this is a Redump MIL-CD rip.");
        else
        {
            int hi = sessions[^1];
            var hiTracks = sheet.Tracks.Where(t => t.Session == hi).Select(t => t.Number);
            Console.WriteLine($"  high-density session {hi}: track(s) {string.Join(", ", hiTracks)} " +
                              $"(inter-session gap {gap} sectors).");
        }

        using (var os = File.Create(output))
            CdiConverter.BinCueToCdi(cueText, cueDir, version, os, gap);
        Console.WriteLine($"Wrote {Path.GetFileName(output)} ({version}).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Split(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge split <file> <size>   (sizes: bytes, 700m, 4g, fat32)");
    try
    {
        long partSize = ImageSplitter.ParsePartSize(args[2]);
        long lastPct = -1;
        var result = ImageSplitter.Split(args[1], partSize, new Progress<double>(f =>
        {
            long pct = (long)(f * 100);
            if (pct != lastPct && pct % 10 == 0) { lastPct = pct; Console.Write($"\r  splitting… {pct}%"); }
        }));
        Console.WriteLine("\r  splitting… done");
        foreach (var p in result.Parts)
            Console.WriteLine($"  {Path.GetFileName(p)} ({new FileInfo(p).Length:N0} bytes)");
        Console.WriteLine($"  {Path.GetFileName(result.ManifestPath)} (manifest)");
        Console.WriteLine($"SHA-256 {result.Sha256}");
        Console.WriteLine($"{result.Parts.Count} part(s), {result.TotalBytes:N0} bytes. " +
                          "Rejoin with: dforge join <first part>");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Join(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge join <first-part|base-name> [output]");
    try
    {
        string first = args[1];
        string output = args.Length >= 3 ? args[2]
            : System.Text.RegularExpressions.Regex.IsMatch(first, @"\.\d{3}$")
                ? first[..^4] : first;
        if (File.Exists(output))
            return Fail($"'{output}' already exists — refusing to overwrite it.");

        long lastPct = -1;
        var result = ImageSplitter.Join(first, output, new Progress<double>(f =>
        {
            long pct = (long)(f * 100);
            if (pct != lastPct && pct % 10 == 0) { lastPct = pct; Console.Write($"\r  joining… {pct}%"); }
        }));
        Console.WriteLine("\r  joining… done");
        Console.WriteLine($"Wrote {output}: {result.Parts} part(s), {result.TotalBytes:N0} bytes, " +
                          (result.Verified ? "CRC + SHA-256 verified." : "NOT verified."));
        if (result.Warning is not null) Console.WriteLine("  " + result.Warning);
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- PlayStation disc identification ---------------------------------------

static int Ps2Info(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ps2-info <image.iso|image.cdi|image.bin>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var id = SystemCnf.FromImage(args[1]);
        if (id is null)
            return Fail("No SYSTEM.CNF found — this does not look like a PlayStation disc image " +
                        "(or its filesystem could not be read).");

        Console.WriteLine($"{Path.GetFileName(args[1])}: PlayStation {(id.Console == PsConsole.Ps2 ? "2" : "1")} disc");
        Console.WriteLine($"  Game ID    : {(id.GameId.Length > 0 ? id.GameId : "(non-standard boot file)")}");
        Console.WriteLine($"  Region     : {id.Region}");
        if (id.VideoMode is not null) Console.WriteLine($"  Video mode : {id.VideoMode}");
        if (id.Version is not null) Console.WriteLine($"  Version    : {id.Version}");
        Console.WriteLine($"  Boot       : {id.BootPath}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- Xbox XDVDFS -----------------------------------------------------------

static int XisoLs(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge xiso-ls <image.iso> [--extract <dir>] [--base <sector>]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string? extractTo = null;
    long? baseSector = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--extract" && i + 1 < args.Length) extractTo = args[++i];
        else if (args[i] == "--base" && i + 1 < args.Length && long.TryParse(args[++i], out var b)) baseSector = b;
    }

    try
    {
        using var img = File.OpenRead(args[1]);
        var vol = XdvdfsReader.Read(img, baseSector);
        Console.WriteLine($"{Path.GetFileName(args[1])}: XDVDFS (Xbox), partition base sector {vol.BaseSector:N0}");
        Console.WriteLine();
        foreach (var e in vol.Files)
            Console.WriteLine($"  {e.Size,12:N0}  {e.Path}");
        Console.WriteLine();
        Console.WriteLine($"{vol.Files.Count():N0} file(s), {vol.TotalBytes:N0} bytes; " +
                          $"{vol.Directories.Count():N0} director(ies)");

        if (extractTo is not null)
        {
            Console.WriteLine($"\nExtracting to {extractTo}…");
            foreach (var e in vol.Files)
            {
                string outPath = Path.Combine(extractTo, e.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var o = File.Create(outPath);
                using var src = File.OpenRead(args[1]);
                XdvdfsReader.ExtractFile(src, vol, e, o);
            }
            Console.WriteLine("Done.");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CreateXiso(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge create-xiso <folder> <out.iso>");
    if (!Directory.Exists(args[1])) return Fail($"Folder not found: {args[1]}");

    try
    {
        // Stream straight to disk: files are opened on demand, so a full-size XISO
        // (well past 2 GB) never has to fit in memory.
        var children = WalkFolderToXiso(args[1]);
        IReadOnlyList<string> warnings;
        using (var output = File.Create(args[2]))
            warnings = XdvdfsBuilder.BuildToStream(output, children);

        foreach (var w in warnings) Console.WriteLine($"  warning: {w}");
        long size = new FileInfo(args[2]).Length;
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: XDVDFS (XISO), {size:N0} bytes " +
                          $"({size / 2048:N0} sectors).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static IReadOnlyList<XdvdfsBuilder.Node> WalkFolderToXiso(string folder)
{
    var nodes = new List<XdvdfsBuilder.Node>();
    foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(XdvdfsBuilder.Node.Dir(Path.GetFileName(dir), WalkFolderToXiso(dir)));
    foreach (var file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(XdvdfsBuilder.Node.FileFromPath(Path.GetFileName(file), file));
    return nodes;
}

// ---- Dreamcast GDI ---------------------------------------------------------

static int GdiInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge gdi-info <disc.gdi>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var disc = GdiParser.ParseFile(args[1]);
        var dir = Path.GetDirectoryName(Path.GetFullPath(args[1])) ?? ".";
        var report = GdiValidator.Validate(disc, dir);

        Console.WriteLine($"{Path.GetFileName(args[1])}: {disc.Tracks.Count} track(s)");
        Console.WriteLine();
        Console.WriteLine("  #   Start LBA   Type    Sector   File");
        foreach (var t in disc.Tracks)
        {
            long size = report.FileSizes.TryGetValue(t.FileName, out var sz) ? sz : -1;
            string area = t.IsHighDensity ? "HD" : "SD";
            Console.WriteLine($"  {t.Number,-3} {t.StartLba,10:N0}  {t.Type,-5} {area}  {t.SectorSize,5}   " +
                              $"{t.FileName}" + (size >= 0 ? $"  ({size:N0} bytes)" : "  (missing)"));
        }
        Console.WriteLine();
        if (disc.BootDataTrack is { } boot)
        {
            Console.WriteLine($"Boot data track: {boot.Number} (LBA {boot.StartLba:N0}) — the game filesystem, " +
                              "and what a PPF patch targets.");
            try
            {
                var header = IpBin.ReadFromDisc(disc, dir);
                if (header is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  Title      : {header.Title}");
                    Console.WriteLine($"  Product no.: {header.ProductNumber}   version {header.Version}");
                    Console.WriteLine($"  Region     : {(header.Regions.Count > 0 ? string.Join(", ", header.Regions) : "none declared")}");
                    Console.WriteLine($"  Maker      : {header.Maker}");
                    Console.WriteLine($"  Released   : {header.ReleaseDate}");
                    Console.WriteLine($"  Boot file  : {header.BootFile}");
                }
            }
            catch (IpBinFormatException ex)
            {
                Console.WriteLine($"  (boot header: {ex.Message})");
            }
        }

        foreach (var issue in report.Issues)
            Console.WriteLine($"  [{issue.Level}] {issue}");

        return report.HasErrors ? 2 : report.HasWarnings ? 1 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GdiBrowse(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge gdi-browse <disc.gdi> [--extract <dir>]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string? extractTo = null;
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--extract") extractTo = args[i + 1];

    try
    {
        var disc = GdiParser.ParseFile(args[1]);
        var dir = Path.GetDirectoryName(Path.GetFullPath(args[1])) ?? ".";
        var listing = GdiBrowser.Browse(disc, dir);

        Console.WriteLine($"{Path.GetFileName(args[1])}: game filesystem \"{listing.VolumeId}\"" +
                          (listing.Joliet ? " (Joliet)" : "") + (listing.RockRidge ? " (Rock Ridge)" : ""));
        Console.WriteLine();
        foreach (var e in listing.Entries.Where(e => !e.IsDirectory))
            Console.WriteLine($"  {e.Size,12:N0}  {e.Path}");
        Console.WriteLine();
        Console.WriteLine($"{listing.Entries.Count(e => !e.IsDirectory):N0} file(s), " +
                          $"{listing.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size):N0} bytes");

        if (extractTo is not null)
        {
            Console.WriteLine($"\nExtracting to {extractTo}…");
            foreach (var e in listing.Entries.Where(e => !e.IsDirectory))
            {
                string outPath = Path.Combine(extractTo, e.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var o = File.Create(outPath);
                GdiBrowser.ExtractFile(disc, dir, e, o);
            }
            Console.WriteLine("Done.");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IsoRebase(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge iso-rebase <in.iso> <out.iso> <baseLBA>   (GD-ROM: 45000)");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    if (!long.TryParse(args[3], out long baseLba) || baseLba < 0)
        return Fail($"'{args[3]}' is not a valid base LBA.");

    try
    {
        var rebased = IsoRebaser.Rebase(File.ReadAllBytes(args[1]), baseLba);
        File.WriteAllBytes(args[2], rebased);
        Console.WriteLine($"Rebased {Path.GetFileName(args[1])} by +{baseLba:N0} LBA -> {Path.GetFileName(args[2])} " +
                          $"({rebased.Length:N0} bytes). Read it back with a base-LBA reader (gdi-browse does this).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- UDF authoring ---------------------------------------------------------

static int CreateUdf(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge create-udf <folder> <out.udf> [--volume NAME]");
    string folder = args[1], outPath = args[2];
    if (!Directory.Exists(folder)) return Fail($"Folder not found: {folder}");

    string volumeId = "DISCFORGE";
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--volume" && i + 1 < args.Length) volumeId = args[++i];

    try
    {
        // Stream straight to disk so a full-size UDF image needn't fit in memory.
        var children = WalkFolderToUdf(folder);
        IReadOnlyList<string> warnings;
        using (var output = File.Create(outPath))
            warnings = UdfBuilder.BuildToStream(volumeId, output, children);
        foreach (var w in warnings) Console.WriteLine($"  warning: {w}");
        long size = new FileInfo(outPath).Length;
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: UDF 1.02, {size:N0} bytes " +
                          $"({size / 2048:N0} sectors), volume \"{volumeId}\".");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static IReadOnlyList<UdfBuilder.Node> WalkFolderToUdf(string folder)
{
    var nodes = new List<UdfBuilder.Node>();
    foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(UdfBuilder.Node.Dir(Path.GetFileName(dir), WalkFolderToUdf(dir)));
    foreach (var file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(UdfBuilder.Node.FileFromPath(Path.GetFileName(file), file));
    return nodes;
}

// ---- PPF patching ----------------------------------------------------------

static int PpfInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ppf-info <patch.ppf>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var patch = PpfPatch.ParseFile(args[1]);
        Console.WriteLine($"{Path.GetFileName(args[1])}: PPF {patch.Version.ToString()[1..]}.0");
        if (patch.Description.Length > 0)
            Console.WriteLine($"  Description : {patch.Description}");
        Console.WriteLine($"  Changes     : {patch.Records.Count:N0} record(s), " +
                          $"{patch.Records.Sum(r => r.Data.Length):N0} bytes");
        Console.WriteLine($"  Reaches to  : offset {patch.MaxTouchedOffset:N0}");
        Console.WriteLine($"  Undo data   : {(patch.CanUndo ? "yes — this patch can be reverted" : "no")}");
        Console.WriteLine($"  Validation  : {(patch.HasValidationBlock ? $"yes — 1024 bytes at 0x{patch.ValidationOffset:X}" : "no")}");
        if (patch.OriginalSize > 0)
            Console.WriteLine($"  Built from  : an image of {patch.OriginalSize:N0} bytes");
        if (patch.FileId is not null)
        {
            Console.WriteLine("  file_id.diz :");
            foreach (var line in patch.FileId.Split('\n'))
                Console.WriteLine($"    {line.TrimEnd()}");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PpfApply(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge ppf-apply <patch.ppf> <image.bin> [--undo] [--force] [--dry-run]");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Image not found: {args[2]}");

    bool undo = false, force = false, dryRun = false;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--undo": undo = true; break;
            case "--force": force = true; break;
            case "--dry-run": dryRun = true; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        var patch = PpfPatch.ParseFile(args[1]);
        Console.WriteLine($"Patch: PPF {patch.Version.ToString()[1..]}.0" +
                          (patch.Description.Length > 0 ? $" — {patch.Description}" : ""));

        using var image = new FileStream(args[2], FileMode.Open, FileAccess.ReadWrite);

        if (undo)
        {
            if (!patch.CanUndo)
                return Fail("This patch carries no undo data, so --undo cannot revert it " +
                            "(only PPF 3.0 patches written with undo can be reverted).");
            if (dryRun) { Console.WriteLine("Dry run: patch is undoable and the image is long enough."); return 0; }
            int reverted = PpfPatch.Undo(patch, image, force);
            Console.WriteLine($"Reverted {reverted:N0} record(s). {Path.GetFileName(args[2])} restored.");
            return 0;
        }

        var check = PpfPatch.CheckApplicable(patch, image);
        if (check.ValidationMatched)
            Console.WriteLine("Validation block matches — this is the image the patch targets.");
        if (!check.Ok)
        {
            if (!force) return Fail(check.Problem!);
            Console.WriteLine($"Warning (overridden by --force): {check.Problem}");
        }

        if (dryRun) { Console.WriteLine($"Dry run: {patch.Records.Count:N0} record(s) would be applied. Nothing written."); return 0; }

        int applied = PpfPatch.Apply(patch, image, force);
        Console.WriteLine($"Applied {applied:N0} record(s) to {Path.GetFileName(args[2])}." +
                          (patch.CanUndo ? " Keep the patch to undo later (ppf-apply --undo)." : ""));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IpsApply(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge ips-apply <patch.ips> <image> [--out <file>]");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Image not found: {args[2]}");
    string outPath = args[2];
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
        else return Fail($"Unknown option: {args[i]}");
    try
    {
        var patch = DiscForge.Core.Patch.IpsPatch.ParseFile(args[1]);
        var patched = DiscForge.Core.Patch.IpsPatch.Apply(patch, File.ReadAllBytes(args[2]));
        File.WriteAllBytes(outPath, patched);
        Console.WriteLine($"Applied {patch.Records.Count:N0} IPS record(s)" +
                          (patch.TruncateLength is { } t ? $", truncated to {t:N0} bytes" : "") +
                          $" -> {Path.GetFileName(outPath)} ({patched.Length:N0} bytes).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IpsCreate(string[] args)
{
    if (args.Length < 4) return Fail("usage: dforge ips-create <orig> <mod> <out.ips>");
    if (!File.Exists(args[1])) return Fail($"Original not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Modified not found: {args[2]}");
    try
    {
        var ips = DiscForge.Core.Patch.IpsPatch.Create(File.ReadAllBytes(args[1]), File.ReadAllBytes(args[2]));
        File.WriteAllBytes(args[3], ips);
        var parsed = DiscForge.Core.Patch.IpsPatch.Parse(ips);
        Console.WriteLine($"Wrote {Path.GetFileName(args[3])}: {parsed.Records.Count:N0} record(s), {ips.Length:N0} bytes.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BpsApply(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge bps-apply <patch.bps> <source> [--out <file>] [--no-verify]");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Source not found: {args[2]}");
    string outPath = args[2];
    bool verify = true;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
            case "--no-verify": verify = false; break;
            default: return Fail($"Unknown option: {args[i]}");
        }
    try
    {
        var patch = DiscForge.Core.Patch.BpsPatch.ParseFile(args[1]);
        var target = DiscForge.Core.Patch.BpsPatch.Apply(patch, File.ReadAllBytes(args[2]), verify);
        File.WriteAllBytes(outPath, target);
        Console.WriteLine($"Applied BPS patch -> {Path.GetFileName(outPath)} ({target.Length:N0} bytes, CRC verified)." +
                          (patch.Metadata.Length > 0 ? $" Metadata: {patch.Metadata}" : ""));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BpsCreate(string[] args)
{
    if (args.Length < 4) return Fail("usage: dforge bps-create <source> <target> <out.bps>");
    if (!File.Exists(args[1])) return Fail($"Source not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Target not found: {args[2]}");
    try
    {
        var bps = DiscForge.Core.Patch.BpsPatch.Create(File.ReadAllBytes(args[1]), File.ReadAllBytes(args[2]));
        File.WriteAllBytes(args[3], bps);
        Console.WriteLine($"Wrote {Path.GetFileName(args[3])}: {bps.Length:N0} bytes (source/target CRC-32 embedded).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PpfConvert(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge ppf-convert <in.ppf> <out.ppf> --to 1|2|3");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");

    PpfVersion? target = null;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--to" && i + 1 < args.Length)
            target = args[++i] switch
            {
                "1" or "1.0" => PpfVersion.V1,
                "2" or "2.0" => PpfVersion.V2,
                "3" or "3.0" => PpfVersion.V3,
                _ => null,
            };
    if (target is null) return Fail("Specify the target with --to 1, --to 2 or --to 3.");

    try
    {
        var patch = PpfPatch.ParseFile(args[1]);
        var converted = PpfPatch.ConvertTo(patch, target.Value);
        File.WriteAllBytes(args[2], converted);
        var check = PpfPatch.Parse(converted);
        Console.WriteLine($"Converted PPF {patch.Version.ToString()[1..]}.0 -> {check.Version.ToString()[1..]}.0: " +
                          $"{Path.GetFileName(args[2])}, {check.Records.Count:N0} record(s), {converted.Length:N0} bytes" +
                          (check.CanUndo ? ", undoable" : "") + (check.HasValidationBlock ? ", validated" : "") + ".");
        if (target == PpfVersion.V1 && patch.CanUndo)
            Console.WriteLine("Note: PPF 1.0 carries no undo or validation, so those were dropped.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PpfEdit(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge ppf-edit <in.ppf> <out.ppf> [--desc \"text\"] [--fileid \"text\"]");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");

    string? desc = null, fileId = null;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--desc" when i + 1 < args.Length: desc = args[++i]; break;
            case "--fileid" when i + 1 < args.Length: fileId = args[++i]; break;
            default: return Fail($"Unknown option: {args[i]}");
        }
    if (desc is null && fileId is null)
        return Fail("Nothing to change — pass --desc and/or --fileid.");

    try
    {
        var patch = PpfPatch.ParseFile(args[1]);
        var edited = PpfPatch.WithMetadata(patch, desc, fileId);
        File.WriteAllBytes(args[2], PpfPatch.Serialize(edited));
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: PPF {patch.Version.ToString()[1..]}.0, metadata updated" +
                          (desc is not null ? $" (description \"{desc}\")" : "") +
                          (fileId is not null ? " (file_id)" : "") + ".");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PpfCreate(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge ppf-create <original.bin> <modified.bin> <out.ppf> " +
                    "[--desc \"text\"] [--no-undo] [--no-validation] [--fileid \"text\"]");
    if (!File.Exists(args[1])) return Fail($"Original not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Modified not found: {args[2]}");

    var opts = new PpfPatch.CreateOptions();
    for (int i = 4; i < args.Length; i++)
        switch (args[i])
        {
            case "--desc" when i + 1 < args.Length: opts = opts with { Description = args[++i] }; break;
            case "--fileid" when i + 1 < args.Length: opts = opts with { FileId = args[++i] }; break;
            case "--no-undo": opts = opts with { IncludeUndo = false }; break;
            case "--no-validation": opts = opts with { IncludeValidation = false }; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        var ppf = PpfPatch.CreateFromFiles(args[1], args[2], opts);
        File.WriteAllBytes(args[3], ppf);
        var parsed = PpfPatch.Parse(ppf);
        Console.WriteLine($"Wrote {Path.GetFileName(args[3])}: PPF 3.0, {parsed.Records.Count:N0} record(s), " +
                          $"{ppf.Length:N0} bytes" +
                          (parsed.Records.Count == 0 ? " — the two images are identical." : "") +
                          (opts.IncludeUndo ? ", undoable" : "") +
                          (parsed.HasValidationBlock ? ", validated" : "") + ".");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GameAudioInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge gameaudio-info <file>   (a .psf/.psf2/.minipsf, .spc, .vgm/.vgz, or .nsf)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    byte[] data;
    try { data = File.ReadAllBytes(path); }
    catch (Exception ex) { return Fail(ex.Message); }

    Console.WriteLine($"Game audio — {Path.GetFileName(path)} ({data.Length:N0} bytes)");
    try
    {
        if (DiscForge.Core.GameAudio.PsfReader.IsPsf(data))
        {
            var f = DiscForge.Core.GameAudio.PsfReader.Read(data);
            Console.WriteLine($"  Format:     PSF family (version 0x{f.PsfVersion:X2})");
            Console.WriteLine($"  System:     {f.SystemName}");
            Console.WriteLine($"  Program:    {f.CompressedProgramSize:N0} compressed bytes (CRC32 {f.ProgramCrc32:X8}) — not decoded");
            if (f.Title is not null) Console.WriteLine($"  Title:      {f.Title}");
            if (f.Game is not null) Console.WriteLine($"  Game:       {f.Game}");
            if (f.Artist is not null) Console.WriteLine($"  Artist:     {f.Artist}");
            if (f.Length is not null) Console.WriteLine($"  Length:     {f.Length}");
            foreach (var kv in f.Tags)
                if (kv.Key is not ("title" or "game" or "artist" or "length"))
                    Console.WriteLine($"  {kv.Key,-11} {kv.Value.Replace("\n", "; ")}");
            return 0;
        }
        if (DiscForge.Core.GameAudio.SpcReader.IsSpc(data))
        {
            var f = DiscForge.Core.GameAudio.SpcReader.Read(data);
            Console.WriteLine("  Format:     SPC (SNES SPC700 dump)");
            Console.WriteLine($"  ID666:      {(f.HasId666 ? (f.TextFormatTag ? "present (text)" : "present (binary)") : "absent")}");
            if (f.SongTitle.Length > 0) Console.WriteLine($"  Song:       {f.SongTitle}");
            if (f.GameTitle.Length > 0) Console.WriteLine($"  Game:       {f.GameTitle}");
            if (f.Artist.Length > 0) Console.WriteLine($"  Artist:     {f.Artist}");
            if (f.DumperName.Length > 0) Console.WriteLine($"  Dumper:     {f.DumperName}");
            if (f.DumpDate.Length > 0) Console.WriteLine($"  Date:       {f.DumpDate}");
            if (f.Comments.Length > 0) Console.WriteLine($"  Comments:   {f.Comments}");
            return 0;
        }
        if (DiscForge.Core.GameAudio.VgmReader.IsVgm(data))
        {
            var f = DiscForge.Core.GameAudio.VgmReader.Read(data);
            Console.WriteLine($"  Format:     VGM v{f.Version}");
            Console.WriteLine($"  Samples:    {f.TotalSamples:N0} @ 44100 Hz  ({f.DurationSeconds:F2} s)");
            Console.WriteLine($"  Chips:      {(f.Chips.Count > 0 ? string.Join(", ", f.Chips) : "(none flagged)")}");
            if (f.Tags.TrackName.Length > 0) Console.WriteLine($"  Track:      {f.Tags.TrackName}");
            if (f.Tags.GameName.Length > 0) Console.WriteLine($"  Game:       {f.Tags.GameName}");
            if (f.Tags.System.Length > 0) Console.WriteLine($"  System:     {f.Tags.System}");
            if (f.Tags.Author.Length > 0) Console.WriteLine($"  Author:     {f.Tags.Author}");
            if (f.Tags.Date.Length > 0) Console.WriteLine($"  Date:       {f.Tags.Date}");
            if (f.Tags.Notes.Length > 0) Console.WriteLine($"  Notes:      {f.Tags.Notes}");
            return 0;
        }
        if (DiscForge.Core.GameAudio.NsfReader.IsNsf(data))
        {
            var f = DiscForge.Core.GameAudio.NsfReader.Read(data);
            Console.WriteLine($"  Format:     NSF v{f.Version} (NES Sound Format)");
            Console.WriteLine($"  Songs:      {f.TotalSongs} (starts at #{f.StartingSong})");
            Console.WriteLine($"  Region:     {(f.IsPal ? "PAL" : "NTSC")}");
            if (f.SongName.Length > 0) Console.WriteLine($"  Name:       {f.SongName}");
            if (f.Artist.Length > 0) Console.WriteLine($"  Artist:     {f.Artist}");
            if (f.Copyright.Length > 0) Console.WriteLine($"  Copyright:  {f.Copyright}");
            Console.WriteLine($"  Expansion:  {(f.ExpansionChips.Count > 0 ? string.Join(", ", f.ExpansionChips) : "none (2A03 only)")}");
            return 0;
        }
        if (DiscForge.Core.GameAudio.NsfReader.IsNsfe(data))
            return Fail("This is an NSFe file — a chunked container distinct from classic NSF; DiscForge does not parse it.");
    }
    catch (DiscForge.Core.GameAudio.GameAudioFormatException ex) { return Fail(ex.Message); }

    return Fail("No PSF/SPC/VGM/NSF signature matched — not a game-audio format DiscForge reads.");
}

static bool TryCheatPlatform(string s, out DiscForge.Core.Cheat.CheatPlatform platform, out bool isGameShark)
{
    isGameShark = false;
    switch (s.ToLowerInvariant())
    {
        case "nes": platform = DiscForge.Core.Cheat.CheatPlatform.Nes; return true;
        case "snes": platform = DiscForge.Core.Cheat.CheatPlatform.Snes; return true;
        case "genesis": case "megadrive": case "md":
            platform = DiscForge.Core.Cheat.CheatPlatform.Genesis; return true;
        case "gb": case "gameboy": case "gbc":
            platform = DiscForge.Core.Cheat.CheatPlatform.GameBoy; return true;
        case "gs-ps1": case "gameshark": case "ps1":
            platform = DiscForge.Core.Cheat.CheatPlatform.GameSharkPs1; isGameShark = true; return true;
        default: platform = default; return false;
    }
}

static int CheatDecode(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge cheat-decode <nes|snes|genesis|gb|gs-ps1> <code>");
    if (!TryCheatPlatform(args[1], out var platform, out bool isGameShark))
        return Fail($"Unknown platform '{args[1]}' (use nes|snes|genesis|gb|gs-ps1).");

    // GameShark value word may contain a space, so re-join the remaining args.
    string code = string.Join(' ', args[2..]);
    try
    {
        var c = isGameShark
            ? DiscForge.Core.Cheat.GameShark.Parse(code, platform)
            : DiscForge.Core.Cheat.GameGenie.Decode(platform, code);

        Console.WriteLine($"Platform:   {c.Platform}");
        Console.WriteLine($"Address:    0x{c.Address:X}");
        Console.WriteLine($"Value:      0x{c.Value:X}");
        Console.WriteLine(c.Compare is { } cmp ? $"Compare:    0x{cmp:X}" : "Compare:    (none)");
        if (!string.IsNullOrEmpty(c.Description))
            Console.WriteLine($"Note:       {c.Description}");
        return 0;
    }
    catch (DiscForge.Core.Cheat.CheatFormatException ex) { return Fail(ex.Message); }
}

static int CheatEncode(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge cheat-encode <nes|snes|genesis|gb> <address> <value> [compare]  (hex)");
    if (!TryCheatPlatform(args[1], out var platform, out bool isGameShark) || isGameShark)
        return Fail($"cheat-encode supports Game Genie platforms only (nes|snes|genesis|gb).");

    static bool ParseNum(string s, out long v) =>
        long.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s,
            System.Globalization.NumberStyles.HexNumber, null, out v);

    if (!ParseNum(args[2], out long address)) return Fail($"Bad hex address '{args[2]}'.");
    if (!ParseNum(args[3], out long value)) return Fail($"Bad hex value '{args[3]}'.");
    long? compare = null;
    if (args.Length >= 5)
    {
        if (!ParseNum(args[4], out long cmp)) return Fail($"Bad hex compare '{args[4]}'.");
        compare = cmp;
    }

    try
    {
        var code = new DiscForge.Core.Cheat.CheatCode
        {
            Platform = platform,
            Address = address,
            Value = value,
            Compare = compare,
        };
        Console.WriteLine(DiscForge.Core.Cheat.GameGenie.Encode(code));
        return 0;
    }
    catch (DiscForge.Core.Cheat.CheatFormatException ex) { return Fail(ex.Message); }
}

static int CheatApplyNes(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge cheat-apply-nes <rom> <code> <out>");
    string romPath = args[1], codeStr = args[2], outPath = args[3];
    if (!File.Exists(romPath)) return Fail($"File not found: {romPath}");

    try
    {
        var code = DiscForge.Core.Cheat.GameGenie.DecodeNes(codeStr);
        byte[] rom = File.ReadAllBytes(romPath);
        var result = DiscForge.Core.Cheat.CheatApply.ApplyNes(rom, code);

        Console.WriteLine($"Code:       {codeStr}  ->  address 0x{code.Address:X}, value 0x{code.Value:X}" +
                          (code.Compare is { } cmp ? $", compare 0x{cmp:X}" : ""));
        if (result.PatchedOffsets.Count == 0)
        {
            Console.WriteLine(result.CompareMismatch
                ? "No bytes patched (compare byte did not match)."
                : "No bytes patched (address did not map into PRG-ROM).");
        }
        else
        {
            foreach (int off in result.PatchedOffsets)
                Console.WriteLine($"Patched:    file offset 0x{off:X}");
        }

        File.WriteAllBytes(outPath, rom);
        Console.WriteLine($"Wrote:      {outPath} ({rom.Length:N0} bytes)");
        return 0;
    }
    catch (DiscForge.Core.Cheat.CheatFormatException ex) { return Fail(ex.Message); }
}

static int WbfsInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge wbfs-info <file.wbfs>\n" +
                    "  Lists the Wii/GameCube discs stored in a WBFS container.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        using var fs = File.OpenRead(args[1]);
        var wbfs = DiscForge.Core.Wbfs.WbfsReader.Read(fs);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {wbfs.Summary}");
        Console.WriteLine();
        Console.WriteLine("Slot  Game ID  Title");
        foreach (var d in wbfs.Discs)
            Console.WriteLine($"  {d.Slot,2}  {d.GameId,-7}  {d.Title}");
        return 0;
    }
    catch (DiscForge.Core.Wbfs.WbfsFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int WbfsExtract(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge wbfs-extract <file.wbfs> <slot> <out.iso>\n" +
                    "  Rebuilds one disc's ISO from a WBFS container (contents copied as-is).");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    if (!int.TryParse(args[2], out int slot)) return Fail($"Not a slot number: {args[2]}");

    try
    {
        using var fs = File.OpenRead(args[1]);
        var wbfs = DiscForge.Core.Wbfs.WbfsReader.Read(fs);
        var disc = wbfs.Discs.FirstOrDefault(d => d.Slot == slot);
        if (disc is null)
            return Fail($"No disc in slot {slot}. Present slots: " +
                        string.Join(", ", wbfs.Discs.Select(d => d.Slot)));

        using var os = File.Create(args[3]);
        long written = DiscForge.Core.Wbfs.WbfsReader.ExtractDisc(fs, disc, os);
        Console.WriteLine($"Extracted {disc.GameId} ({disc.Title}) to " +
                          $"{Path.GetFileName(args[3])} — {written:N0} bytes.");
        return 0;
    }
    catch (DiscForge.Core.Wbfs.WbfsFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Fail(string message)
{
    Console.Error.WriteLine($"dforge: {message}");
    return 1;
}
