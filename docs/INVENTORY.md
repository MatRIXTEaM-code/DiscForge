# DiscForge — feature inventory

A clean-room, proprietary disc-imaging and retro-preservation toolkit in C# /
.NET 8. This is the map of what it does, grouped by area. Every capability is
implemented from public documentation and covered by the offline test harness
(**1150+ tests**). The rule that shapes all of it: DiscForge builds faithful
imaging, format parsing, protection *detection*, patching, descrambling and
save/asset reading — and never writes code to defeat copy protection, region
locks or console security, never decrypts encrypted console content, and never
generates cheats (COMPARISON.md §13).

Each area below lists the formats/platforms covered and the relevant CLI
command(s). The GUI (Windows) surfaces most of these as launcher tiles — see
GUI.md. Best-effort vs full coverage is noted where the code documents it.

## Disc image containers

Read, write and convert the common image formats.

- **CDI** (DiscForge's native): read / write / extract, multi-session, all
  sector sizes (`inspect`, `extract`, `verify`, `create`, `compare`, `fix-modes`).
- **BIN/CUE**: convert, merge per-track into one, split back
  (`convert`, `bincue-merge`, `bincue-split`, `cue-check`).
- **ISO 9660 image**: convert and create (`convert`, `create`, `iso-rebase`).
- **GDI, NRG, Alcohol MDS/MDF, CloneCD CCD**: read / convert
  (`convert`, `disc-convert`, `ccd-info`, `to-ccd`).
- **Universal converter**: any supported input to any supported output through a
  canonical model (`disc-convert`; in: `.cue .chd .iso .cso .zso .wbfs .cdi .nrg
  .mds .gdi .ccd`, out: `.cue .chd .iso .cdi .nrg`).
- **Raw DAO**: compose a full raw image with lead-in and sub-code, analyse it,
  repair Mode 1 sectors from their own parity (`build-raw`, `inspect-raw`,
  `ecc-repair`, `extract-sectors`, `view-sector`).

## Disc filesystems

Browse and extract files straight from an image; build new filesystems.

- **ISO 9660** read (Joliet, Rock Ridge long names) and **create** from a folder,
  with El Torito boot records (`ls`, `browse`, `extract-files`, `create`).
- **UDF** 1.02–2.50 read (incl. the Blu-ray metadata partition); **UDF 1.02
  write**, streamed with no 2 GB ceiling (`browse`, `create-udf`).
- **Xbox XDVDFS / XISO** read and write, streamed (`xiso-ls`, `create-xiso`).

## Compressed & catalogued images

- **CHD** (MAME/MESS): identify, **extract** a CD (bin/cue) or hard-disk image,
  and **create** a CHD (`chd-info`, `chd-extract`, `chd-extract-hd`,
  `chd-create`; parent-chain deltas supported). *(Full extraction is now
  shipped; earlier docs describing it as inspection-only are stale.)*
- **CSO / ZSO**: full read (zlib + clean-room LZ4) and CSO write
  (`ciso-info`, `ciso-to-iso`, `iso-to-ciso`).
- **RVZ / WIA**: identify + metadata only — see *Deferred* below (`rvz-info`).
- **`identify`**: sniff any file and name its format (everything in this
  document).

## Console disc identify

Read the boot/identification headers; do not defeat any security.

- **PlayStation 1/2** — game id + region from SYSTEM.CNF (`ps2-info`).
- **Dreamcast** — IP.BIN boot header, GD-ROM track layout and filesystem
  (`ipbin-info`, `gdi-info`, `gdi-browse`).
- **Sega Saturn** — disc header from `.gdi` / `.cue` MIL-CD / `.cdi` / raw
  `.bin`/`.iso` (`saturn-info`).
- **Philips CD-i** (Green Book) — identify and list the filesystem, pure CD-i or
  CD-i Bridge (`cdi-console-info`).
- **PSP UMD** — PARAM.SFO metadata + filesystem from `.iso`/`.cso`/`.zso`, and
  PBP packages (`psp-info`, `pbp-info`, `pbp-extract`).
- **GameCube / Wii** — boot header + file tree (GameCube), volume header +
  partition table (Wii, contents not read); WBFS containers (`gcm-info`,
  `wbfs-info`, `wbfs-extract`).
- **Blu-ray** — playlists (`.mpls`), clip-info (`.clpi`), BDMV title enumeration
  (`bdmv-info`).

## Cartridge ROMs

- Identify N64 (`.n64/.z64/.v64`), SNES (`.sfc/.smc`), Genesis/Mega Drive
  (`.md/.gen`), Game Boy / GBC (`.gb/.gbc`), GBA, NES and more, with No-Intro
  CRC-32/MD5/SHA-1 (`rom-info`).

## Floppy & disk images

- **Floppy** — list and extract from C64 D64, Amiga ADF, DOS FAT12 `.img`,
  auto-detected (`floppy-info`, `floppy-extract`).
- **Whole-disk** — read the partition table (MBR, GPT, or PS2 APA) and the
  filesystem in each partition (`disk-info`).

## Game audio

Decode/read only; no playback, no re-distribution of copyrighted audio.

- **CD audio** — extract to WAV; verify a rip against AccurateRip
  (`extract`, `accuraterip`).
- **CD+G** — decode graphics from a raw image or `.cue`+`.sub`, render a frame to
  PNG, extract the packet stream (`cdg-preview`, `cdg-render`, `cdg-extract`).
- **PlayStation** — XA-ADPCM → WAV, VAG → WAV, CRI ADX → WAV, VAB bank and SEQ
  sequence structure (`xa-extract`, `vag-extract`, `adx-decode`, `vab-info`,
  `seq-info`).
- **Game-music metadata** — PSF/PSF2/miniPSF, SPC, VGM/VGZ, NSF: system, tags,
  duration (`gameaudio-info`).
- **Audio CD authoring** — build a Red Book image from WAV files
  (`create-audio`).

## Console saves & memory cards

Read (and, for VMU, write) save containers; no decryption.

- **Dreamcast VMU** — read, extract, create a blank, add a save, and convert
  VMS → VMI (`vmu-info`, `vmu-extract`, `vmu-create`, `vmu-add`, `vms2vmi`).
- **PlayStation 2** — `.ps2` card read/extract (`ps2mc-info`, `ps2mc-extract`).
- **PlayStation 1** — `.mcr` card read/extract (`psxmc-info`, `psxmc-extract`).
- **GameCube** — list saves in a `.gci` or card image, extract one to `.gci`
  (`gci-info`, `gci-extract`).
- **N64** — identify a save by size, list Controller Pak notes (`n64save-info`).
- **Sega Saturn** — list a backup-memory image directory (`saturnsave-info`).

## Partition tables

- MBR, GPT and PlayStation 2 APA, auto-detected, with the filesystem in each
  partition (`disk-info`).

## Patches & cheats

- **PPF** (PlayStation Patch File) v1–v3: apply/undo, create, show info, convert
  between revisions, edit description/file-id (`ppf-apply`, `ppf-create`,
  `ppf-info`, `ppf-convert`, `ppf-edit`).
- **IPS** and **BPS**: apply and create, BPS CRC-verified (`ips-apply`,
  `ips-create`, `bps-apply`, `bps-create`).
- **PAL ↔ NTSC** video-mode conversion of a PS-EXE/image, emitted as an undoable
  PPF (`psx-video-mode`).
- **Cheats** — decode/encode Game Genie (NES/SNES/Genesis/GB) and decode
  GameShark (PS1), apply an NES Game Genie code to a NROM ROM. Codes are
  *decoded/applied*, never generated (`cheat-decode`, `cheat-encode`,
  `cheat-apply-nes`).

## PlayStation assets & utilities

- **Assets** — TIM → PNG, TMD → DXF (polygon faces), TOD info (`tim-info`,
  `tim-extract`, `tmd-info`, `tmd2dxf`, `tod-info`).
- **Executables** — PS-EXE header reader, padding, byte→source dump
  (`psx-exe-info`, `psx-pad`, `bin2src`).
- **Build** — a Mode 2/2352 bin/cue from a folder (`psx-build`).
- **Media** — STR demux to MDEC bitstreams + audio note (`str-demux`; MDEC pixel
  decode deferred, see below).

## Dreamcast

- **GD-ROM (GDI)** — parse/validate/browse/extract, IP.BIN, and MIL-CD Redump
  bin/cue → two-session CDI (`gdi-info`, `gdi-browse`, `ipbin-info`,
  `milcd-to-cdi`).
- **1ST_READ.BIN scramble/descramble** — the publicly documented transform
  (`dc-scramble`, `dc-descramble`).

## Protection detection & interop

Detection and preservation only — never circumvention.

- LibCrypt / sub-channel fingerprint scan (`scan-protection`, `subch`).
- SBI (sub-channel protection) make/read (`sbi-make`, `sbi-info`).
- CloneCD `.ccd` read/write (`ccd-info`, `to-ccd`).
- ATIP media-code / CD-R manufacturer id (`cdr-info`).

## DVD-Video & Video CD

- **DVD-Video** — read structure and streams, plan a DVD-Shrink-style fit,
  native structural IFO rewrite (`dvd-info`, `dvd-rewrite`), video re-encode to a
  target size (`transcode`).
- **VCD / SVCD** — read/write the INFO & ENTRIES control files, pbc-less scope
  (`vcd-info`).

## Workflows: verify, catalogue, convert, submit

- **Checksums** — CRC-32 + MD5 + SHA-1 + SHA-256 in one pass, with SFV/hash
  sidecars and verification (`checksum`).
- **Compare** — diff two images by structure and per-track CRC-32 (`compare`).
- **DAT verify** — check size + CRC-32/SHA-1 against a redump-style Logiqx DAT
  and name the disc (`dat-verify`).
- **Library** — identify + hash a whole tree, verify against a DAT, and rename
  verified files to canonical names (`library scan`, `library rename`).
- **Split / join** — split into parts with an SFV manifest, rejoin with per-part
  CRC and overall SHA-256 verification (`split`, `join`).
- **Submission info** — Redump-style hashes / cuesheet / sub-channel report for a
  dump (`submission-info`).
- **ScummVM** — Advanced-Detector fingerprints, and export a disc to a ScummVM
  game folder with WAV/FLAC/OGG audio (`scummvm-detect`, `scummvm-export`).

## Devices (Windows)

Live hardware access is Windows-only (SPTI + IMAPI2, no kernel drivers).

- Drive detection and per-drive capabilities, ripping, IMAPI burning, copy
  (validated before reading), disc-quality scan, C2 recovery — surfaced through
  the GUI (see GUI.md) and `mount` (mount an image as a drive).

## Honest, deliberate boundaries (deferred items)

DiscForge ships a format only once it can be validated against an independent
oracle. The following are documented as deferred rather than shipped
half-working:

- **ECM decode/encode** — reconstruction machinery (EDC/ECC) exists; blocked on a
  reference `.ecm` fixture to prove byte-for-byte interop (docs/ECM.md). No CLI
  command yet.
- **RVZ / WIA → ISO decompression** — `rvz-info` identifies and reads metadata;
  full decode needs zstd/bzip2 codecs (unavailable in the offline build) plus a
  real `.rvz`+ISO oracle for the group/exception-list machinery (docs/RVZ.md).
- **MDEC frame → pixels** — `str-demux` fully demuxes STR into MDEC bitstreams +
  audio; the Huffman/IDCT/YUV pixel decode is deferred pending a reference frame
  (docs/PSX_MEDIA.md).
- **Full VCD/SVCD image authoring** — control files ship (pbc-less); MPEG encode,
  XA Mode 2 Form 2 muxing and the PSD/segment tree are scoped but deferred
  (docs/VCD_AUTHORING.md).
- **DVD menu authoring** — deferred to dvdauthor by design; DiscForge writes only
  the structural IFOs (docs/DVD_VIDEO_SHRINK.md).

**Excluded by the clean-room rule** (not deferrals — out of scope permanently):
anything that defeats copy protection, region locks or console security
(soft-mods, self-boot/MIL-CD/ESR, swap-trick, save-exploit loaders), decryption
of encrypted console content (Wii partitions in WBFS extraction, `DATA.PSP` in
PBP extraction are copied as-is, not decrypted), and cheat-code *generation*.
DiscForge detects and preserves protection; it never circumvents it.
</content>
