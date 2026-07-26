<!-- GENERATED FILE — regenerate with:  scripts/gen_cli_doc.ps1  (captures `dforge` help) -->

# DiscForge — `dforge` command reference

`dforge` is the cross-platform command-line tool (Core builds and runs anywhere .NET 8
does). It exposes the same Core engine as the GUI. This reference is generated verbatim
from the tool's own `--help` output, so it never drifts: **90 commands**.

Run `dforge` with no arguments to print this list, or `dforge <command>` with no further
arguments to see that command's usage.

```
  identify <file>       Say what a file is (any format DiscForge knows)
  library scan <dir> [--dat f]   Identify+hash a whole tree, verify vs a DAT
  submission-info <image> [--out f]  Redump-style hashes/cuesheet/subchannel for a dump
  library rename <dir> --dat f [--apply]  Rename verified files to canonical names
  license <keygen|issue|verify|machine-id> …  Manage DiscForge licence keys (see --help)
  1g1r <dat> [--regions USA,Europe,Japan] [--keep-proto] [--drop-unlicensed] [--out f]
                          One-game-one-ROM: pick the best region per game from a DAT
  rebuild <src> <dest> --dat f [--per-game] [--move] [--apply]
                          Rebuild a clean, DAT-named set from a messy folder
  dat-diff <old.dat> <new.dat>  Compare two DAT revisions: added/removed/changed games
  library-report <dir> <out.html> [--dat f]  Scan a folder to a shareable HTML dashboard
  frontend-export <retroarch|gamelist> <dir> <out> [--name N] [--dat f]
                          Write a RetroArch .lpl or EmulationStation gamelist.xml for a folder
  m3u <out.m3u> <disc1> <disc2> [...]  Build a multi-disc M3U playlist (order preserved)
  torrentzip <out.zip> <file> [...]   Write a deterministic TorrentZip-structured archive
  hashgen <sfv|md5|sha1> <out> <file> [...]  Write a checksum sidecar for the files
  hashverify <sidecar.sfv|.md5|.sha1>  Re-hash the referenced files and report OK/FAIL
  ps1card-convert <in> <out> [raw|gme|vgs]  Convert a PS1 memory card between container formats
  save-convert <in> <out> <op> [--fill FF]  Fix a cartridge save's byte order or size.
                          op: swap16|swap32, pad <size|sram|flash|eeprom4k|eeprom16k|mempak>, trim
  rom-convert <in> <out> <op>  Fix a cartridge dump so it matches a DAT. op:
                          z64|v64|n64 (N64 byte order), snes-strip|snes-add,
                          smd|unsmd (Genesis interleave), nes-strip (iNES header)
  inspect <image.cdi>   Show version, sessions, and track layout
  extract <cdi> <dir>   Extract tracks to ISO/WAV (--raw for full sectors)
  convert <in> <out>    CDI <-> BIN+CUE / ISO / GDI / NRG, or MDS -> CDI
                        (.cdi<->.cue, .cdi<->.iso, .mds->.cdi)
  disc-convert <in> <out>  Universal hub: any format -> any via a canonical model
                        (.cue .chd .iso .cso .zso .wbfs .cdi .nrg .mds .gdi .ccd in;
                         .cue .chd .iso .cdi .nrg out)
  verify <cdi> [--checksums]  Structural checks + per-track CRC-32
  create <dir> <out.cdi>  Build a data CDI from a folder (--volume NAME, --rock-ridge)
  compare <a.cdi> <b.cdi>  Diff two images (structure + per-track CRC-32)
  create-audio <out.cdi> <a.wav> [b.wav ...]  Build an audio CD from WAVs
                          --gapless, --74, --postgap [sectors]
  ls <image.cdi>          List files inside the image (ISO 9660 or UDF, auto-detected)
                          --iso 8.3 names, --joliet, --udf force a filesystem
  extract-files <image.cdi> <dir>  Extract the filesystem contents
  build-raw <src> <out>   Compose a RAW DAO image (lead-in + subcode) from
                          a .cue or .cdi. --subcode pq|cooked|raw (default cooked)
  subch <file.sub>        Analyse a captured raw sub-channel sidecar: Q-CRC
                          validity, LibCrypt-style protection fingerprint
  cdg-preview <src>       Decode CD+G graphics from a raw image (2448) or a
                          .cue with .sub sidecar. --seconds N (default 30),
                          --out shot.ppm writes a screenshot
  cdg-render <file.cdg>   Render a CD+G frame to PNG. --at MM:SS (default end),
                          --out <file.png> (default <name>.png)
  cdg-extract <sub> <out.cdg>  Extract the CD+G packet stream from a raw
                          96-byte/sector sub-channel sidecar
  view-sector <img> <addr> [--count N] [--descramble]  Annotated hex view of
                          sectors. addr: LBA, mm:ss:ff, or +fileindex
  extract-sectors <img> <out> --start <addr> --count N  Pull a sector range
                          --as stored|user|raw2352, --byteswap for audio
  inspect-raw <img>       Analyse a raw image: TOC, Q health, CD-TEXT, MCN/ISRC,
                          scrambling, EDC/ECC. --deep checks every sector.
                          Also reads bare 2352 BINs (ECC gold-check on real rips)
  checksum <file>         CRC-32 + MD5 + SHA-1 + SHA-256 in one pass
                          --write [sha256|md5|sha1|sfv|all]   write sidecar(s)
                          --verify   check against an existing sidecar
  browse <image>          List files inside a .cdi or .iso (ISO 9660 or UDF)
                          --extract <dir> writes them out, --only <text> filters
  cue-check <sheet.cue>   Check a cuesheet against the data file it describes:
                          indexes inside the file, track types consistent,
                          arithmetic reaching the end. Exit 2 on errors, 1 on warnings
  ecc-repair <image.bin>  Rebuild damaged Mode 1 sectors from the Reed-Solomon
                          parity they already carry. --dry-run to check only
  fix-modes <image.cdi>   Correct track modes recorded wrongly in a CDI descriptor
  split <file> <size>     Split into .001/.002/… + .sfv manifest
                          sizes: bytes, 700m, 4g, or fat32 (= 4 GiB - 1)
  join <part|base> [out]  Rejoin parts, verifying CRCs + SHA-256 via the manifest
  ppf-apply <patch.ppf> <image.bin>  Apply a PlayStation Patch File to an image
                          --undo revert (PPF 3.0), --force skip validation, --dry-run
  ppf-create <orig> <mod> <out.ppf>  Build a PPF 3.0 from a before/after image pair
                          --desc, --fileid, --no-undo, --no-validation
  ppf-info <patch.ppf>    Show a patch's version, description, size and flags
  ppf-convert <in> <out> --to 1|2|3  Rewrite a patch in another PPF revision
  ppf-edit <in> <out>     Change a patch's --desc and/or --fileid in place
  ips-apply <patch.ips> <image> [--out f]  Apply an IPS patch (in place unless --out)
  ips-create <orig> <mod> <out.ips>  Build an IPS patch from a before/after pair
  bps-apply <patch.bps> <source> [--out f]  Apply a BPS patch (CRC-verified)
  bps-create <source> <target> <out.bps>  Build a BPS patch from a before/after pair
  create-udf <folder> <out.udf>  Build a UDF 1.02 filesystem image from a folder
                          --volume NAME sets the volume label
  gdi-info <disc.gdi>     Show a Dreamcast GD-ROM track layout and validate it
                          against the track files beside it
  gdi-browse <disc.gdi>   List the game filesystem on the high-density track
                          --extract <dir> writes the files out
  milcd-to-cdi <in.cue> <out.cdi>  Convert a Dreamcast MIL-CD Redump bin/cue to a
                          two-session CDI. --version v2|v3|v35, --gap <sectors>
  ipbin-info <image>      Identify a Dreamcast disc from its IP.BIN boot header
  saturn-info <image>     Identify a Sega Saturn disc from its header
                          (.gdi, .cue MIL-CD, .cdi, or a raw .bin/.iso)
  cdi-console-info <image>  Identify a Philips CD-i (Green Book) disc and list
                          its filesystem (pure CD-i or CD-i Bridge)
  psp-info <image>        Read a PSP UMD's PARAM.SFO metadata and filesystem
                          (.iso, .cso or .zso; --sfo dumps every SFO key/value)
  pbp-info <EBOOT.PBP>    List a PSP PBP package: version + each sub-file's size,
                          and its PARAM.SFO title/id/category if present
  pbp-extract <EBOOT.PBP> <dir>  Write each non-empty PBP sub-file to <dir>/<name>
                          (DATA.PSP is extracted raw and is NOT decrypted)
  bdmv-info <file|folder> Show a Blu-ray playlist (.mpls), clip-info (.clpi),
                          or enumerate titles from a BDMV folder
  iso-rebase <in> <out> <baseLBA>  Shift an ISO's LBAs (GD-ROM fix; base 45000)
  xiso-ls <image.iso>     List files in an Xbox XDVDFS image (--extract <dir>)
  create-xiso <folder> <out.iso>   Build an Xbox XISO from a folder
  ps2-info <image>        Identify a PlayStation 1/2 disc (game ID, region) from SYSTEM.CNF
  gcm-info <image>        GameCube disc: boot header + file tree; for a Wii disc,
                          the volume header + partition table (contents not read)
  wbfs-info <file>        List the Wii/GameCube discs in a WBFS container
                          (slot, game id, title, sizes)
  wbfs-extract <file> <slot> <out.iso>  Rebuild one disc's ISO from a WBFS
                          container (contents are copied as-is, not decrypted)
  rvz-info <image>        Identify an RVZ/WIA container and show its metadata
  rom-info <file>         Identify a cartridge ROM (N64, SNES, Genesis, GB/GBC, GBA,
                          NES, and more) and print its No-Intro CRC32/MD5/SHA1
  scummvm-detect <path>   ScummVM Advanced-Detector fingerprints (size + MD5 of the
                          first 5000 bytes) for a game folder or file. --recursive,
                          --bytes N to change the hashed length
  scummvm-export <cue> <dir>  Export a disc into a ScummVM game folder: data files +
                          each CD audio track as trackNN.wav. --flac/--ogg re-encode
                          the audio in-process (no ffmpeg needed); --high = better OGG
  disk-info <image>       Read a whole-disk image's partition table (auto-detect
                          MBR, GPT, or PS2 APA) and the filesystem in each partition
  floppy-info <image>     List a floppy image's contents (auto-detect C64 D64,
                          Amiga ADF, or DOS FAT12 .img)
  gameaudio-info <file>   Read a game-music file's metadata/structure (auto-detect
                          PSF/PSF2, SPC, VGM, NSF): system, tags, duration. No playback
  gci-info <file>         List GameCube saves in a .gci or a memory-card image
  gci-extract <card> <index> <out.gci>  Write one save from a card image to a .gci
  n64save-info <file>     Identify an N64 save by size, and list Controller Pak notes
  saturnsave-info <file>  List the directory of a Sega Saturn backup-memory image
  floppy-extract <image> <path-in-image> <out>  Extract one file from a floppy image
  cheat-decode <platform> <code>  Decode a Game Genie / GameShark code to address/value
                          platform: nes|snes|genesis|gb|gs-ps1
  cheat-encode <platform> <address> <value> [compare]  Encode a Game Genie code
                          platform: nes|snes|genesis|gb (hex address/value)
  cheat-apply-nes <rom> <code> <out>  Apply an NES Game Genie code to a ROM (NROM)
  adx-decode <in.adx> <out.wav>  Decode a CRI ADX ADPCM stream to a 16-bit WAV
  read-offset <samples> [in.wav out.wav]  Redump read-offset math; with a WAV,
                          slide it by <samples> (combined drive+disc offset)
  vab-info <file.vab>     Read a PlayStation VAB (VAG bank): programs, tones, VAGs
  seq-info <file.seq>     Read a PlayStation SEQ sequence: ppqn, tempo, event count
  str-demux <in.str> <out-dir>  Split a PSX .str into MDEC bitstreams + audio note
                          --sector-size 2352|2048 (default 2352)
  burn                  (Phase 4) You know what this does
```
