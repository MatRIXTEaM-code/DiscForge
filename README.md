# DiscForge

*A clean-room disc-imaging and retro-preservation toolkit for C# / .NET 8.*

**Proprietary — all rights reserved.** Not open source. See [LICENSE](LICENSE).
Source visibility does not grant permission to copy, fork or redistribute.

DiscForge is a clean-room disc-imaging and broad retro-preservation toolkit
written in C# / .NET 8. It reads, writes, converts and verifies optical-disc
images (CD / DVD / Blu-ray and the classic console formats), but the surface is
much wider than discs: floppy images, cartridge ROMs, console saves and memory
cards, game audio, ROM patches and cheat codes, a collection manager that
verifies a whole tree against a Redump/No-Intro DAT, a universal image
converter, and a Redump-style submission-info generator. Everything is built on
a pure, platform-agnostic Core engine, surfaced through a Windows WinForms GUI
and a 120-command `dforge` command-line tool.

## What it does

Every format below is implemented from public documentation and covered by the
offline test harness (**1150+ tests**).

- **Disc image containers** — CDI (native read/write), BIN/CUE, ISO, GDI, NRG,
  Alcohol MDS/MDF, CloneCD CCD, CHD (read/extract/create), CSO/ZSO, WBFS, XISO,
  UDF, plus a universal any-to-any converter (`disc-convert`).
- **Disc filesystems** — ISO 9660 (Joliet, Rock Ridge), UDF 1.02–2.50 read and
  UDF 1.02 write, El Torito boot records, Xbox XDVDFS.
- **Console disc identify** — PS1/PS2, Dreamcast (IP.BIN / GD-ROM), Sega Saturn,
  Philips CD-i, PSP UMD, GameCube/Wii, Xbox.
- **Cartridge ROMs** — N64, SNES, Genesis/Mega Drive, Game Boy / GBC / GBA, NES
  identification with No-Intro checksums.
- **Floppy & disk images** — C64 D64, Amiga ADF, DOS FAT12; MBR / GPT / PS2 APA
  partition tables.
- **Game audio** — CD-DA, CD+G, XA-ADPCM, VAG, ADX; PSF/PSF2, SPC, VGM, NSF
  metadata; VAB/SEQ structure.
- **Console saves** — PS1 `.mcr`, PS2 `.ps2`, Dreamcast VMU (read and write),
  GameCube `.gci`, N64 Controller Pak, Saturn backup memory.
- **Patches & cheats** — PPF v1–v3, IPS, BPS apply/create; Game Genie /
  GameShark decode/encode.
- **Verification & cataloguing** — CRC-32/MD5/SHA-1/SHA-256, AccurateRip, DAT
  verify, a library scanner/renamer, and a Redump submission-info generator.
- **Devices (Windows)** — drive detection, ripping, IMAPI burning, mount as a
  drive, disc-quality scan, CD-R manufacturer id.

The rule that shapes all of it: DiscForge builds faithful imaging, format
parsing, protection **detection**, patching, descrambling and save/asset
reading — and never writes code to defeat copy protection, region locks or
console security, and never decrypts encrypted console content (see
[docs/COMPARISON.md](docs/COMPARISON.md) §13).

## Clean-room boundary

- **In scope:** faithful imaging, format parsing, protection *detection*
  (LibCrypt / sub-channel fingerprints), patching, descrambling of publicly
  documented transforms (e.g. Dreamcast 1ST_READ.BIN), and reading saves and
  assets.
- **Out of scope, by rule:** anything that defeats copy protection, region
  locks or console security (soft-mods, self-boot/MIL-CD/ESR loaders, swap
  tricks, save-exploit loaders), cheat-code *generation*, and decrypting
  encrypted console content. WBFS extraction copies partition data as-is (it
  does not decrypt Wii partitions); `pbp-extract` writes `DATA.PSP` raw and does
  **not** decrypt it. DiscForge detects and preserves protection; it never
  circumvents it.

## Architecture

```
DiscForge.sln
├── src/
│   ├── DiscForge.Core/      # All format logic, pure managed, no P/Invoke — builds anywhere
│   ├── DiscForge.Devices/   # Drive access: SPTI (raw MMC) + IMAPI2 (burning), Windows
│   ├── DiscForge.Cli/       # `dforge` — 120 commands, cross-platform
│   └── DiscForge.App/       # WinForms GUI (net8.0-windows), thin shell over Core
├── tests/
│   ├── Harness/             # Custom offline test harness
│   └── DiscForge.Core.Tests/
└── docs/
```

Design rules: **Core is pure** (unit-testable on Linux CI, no Windows APIs);
**capability detection, not assumption** (drives are interrogated, features
light up per-drive); **no kernel drivers** (SPTI + IMAPI2 only); **clean-room**
(every format from public documentation, validated against an independent oracle
where one exists).

## Building & running

Requires the .NET 8 SDK. The Core, CLI and test harness build and run on any
platform .NET 8 supports (Linux, macOS, Windows). The WinForms **App is
Windows-only** (`net8.0-windows`, WinForms) and the **Devices** layer's live
drive access needs Windows.

Build and run the CLI:

```
dotnet build src/DiscForge.Cli/DiscForge.Cli.csproj -c Release
dotnet src/DiscForge.Cli/bin/Release/net8.0/dforge.dll            # prints the command list
dotnet src/DiscForge.Cli/bin/Release/net8.0/dforge.dll identify path/to/image
```

Run the test harness (offline, needs no hardware):

```
dotnet run --project tests/Harness/Harness.csproj -c Release
```

Build and run the GUI (Windows only):

```
dotnet run --project src/DiscForge.App
```

## Documentation

| Area | Doc |
|------|-----|
| Capability inventory (the map of everything) | [docs/INVENTORY.md](docs/INVENTORY.md) |
| Full `dforge` command reference | [docs/CLI.md](docs/CLI.md) |
| Protecting the software (licensing, obfuscation, signing) | [docs/SECURITY.md](docs/SECURITY.md) |
| WinForms GUI reference | [docs/GUI.md](docs/GUI.md) |
| CDI byte-level format spec | [docs/CDI_FORMAT.md](docs/CDI_FORMAT.md) |
| Image create / El Torito / Rock Ridge | [docs/CREATE.md](docs/CREATE.md) |
| Filesystem browsing | [docs/BROWSING.md](docs/BROWSING.md) |
| UDF read/write | [docs/UDF.md](docs/UDF.md) |
| Raw DAO, sub-channel, sector tools | [docs/RAW_DAO.md](docs/RAW_DAO.md), [docs/SECTOR_TOOLS.md](docs/SECTOR_TOOLS.md) |
| CHD | [docs/CHD.md](docs/CHD.md), [docs/CHD_MAP.md](docs/CHD_MAP.md) |
| CSO / ZSO | [docs/CISO.md](docs/CISO.md) |
| Audio / AccurateRip | [docs/AUDIO.md](docs/AUDIO.md), [docs/ACCURATERIP.md](docs/ACCURATERIP.md) |
| Checksums, split/join | [docs/CHECKSUMS_AND_SPLIT.md](docs/CHECKSUMS_AND_SPLIT.md) |
| Protection detection / CloneCD interop | [docs/PROTECTION_AND_INTEROP.md](docs/PROTECTION_AND_INTEROP.md) |
| DVD-Video shrink / reauthor | [docs/DVD_VIDEO_SHRINK.md](docs/DVD_VIDEO_SHRINK.md), [docs/TRANSCODE.md](docs/TRANSCODE.md) |
| PlayStation tools / media / video mode | [docs/PSX_TOOLS.md](docs/PSX_TOOLS.md), [docs/PSX_MEDIA.md](docs/PSX_MEDIA.md), [docs/PSX_VIDEO_MODE.md](docs/PSX_VIDEO_MODE.md) |
| Dreamcast tools / scramble / backup | [docs/DREAMCAST_TOOLS.md](docs/DREAMCAST_TOOLS.md), [docs/DREAMCAST_SCRAMBLE.md](docs/DREAMCAST_SCRAMBLE.md), [docs/DREAMCAST_BACKUP.md](docs/DREAMCAST_BACKUP.md) |
| Memory cards / VMU | [docs/MEMORY_CARDS.md](docs/MEMORY_CARDS.md), [docs/VMU.md](docs/VMU.md) |
| GameCube / Wii | [docs/GAMECUBE.md](docs/GAMECUBE.md) |
| Xbox XISO | [docs/XBOX.md](docs/XBOX.md) |
| ScummVM | [docs/SCUMMVM.md](docs/SCUMMVM.md) |
| DAT verify / Redump submission | [docs/DAT_VERIFY.md](docs/DAT_VERIFY.md), [docs/REDUMP2CDI.md](docs/REDUMP2CDI.md) |
| Redump physical-dump design (read offset, guard band) | [docs/REDUMP_PHYSICAL.md](docs/REDUMP_PHYSICAL.md) |
| Front-end / emulator export (RetroArch, ES, M3U) | [docs/FRONTENDS.md](docs/FRONTENDS.md) |
| Collection tools (1G1R, rebuild, TorrentZip, hashes, save convert) | [docs/COLLECTION_TOOLS.md](docs/COLLECTION_TOOLS.md) |
| Devices / mount / erase & speed | [docs/DEVICES.md](docs/DEVICES.md), [docs/MOUNT_AND_CCD_READ.md](docs/MOUNT_AND_CCD_READ.md), [docs/ERASE_AND_SPEED.md](docs/ERASE_AND_SPEED.md) |
| Deferred items (honest limitations) | [docs/ECM.md](docs/ECM.md), [docs/RVZ.md](docs/RVZ.md), [docs/VCD_AUTHORING.md](docs/VCD_AUTHORING.md), and the MDEC note in [docs/PSX_MEDIA.md](docs/PSX_MEDIA.md) |

## Known limitations (deferred, deliberately)

DiscForge ships a format only when it can be validated against an independent
oracle. A few items are documented as deferred rather than shipped half-working:

- **ECM decode/encode** — the sector-reconstruction machinery exists; blocked on
  a reference `.ecm` fixture to validate byte-for-byte ([docs/ECM.md](docs/ECM.md)).
- **RVZ / WIA → ISO decompression** — `rvz-info` identifies and reads metadata;
  full decode needs zstd/bzip2 codecs (unavailable in the offline build) plus a
  reference oracle ([docs/RVZ.md](docs/RVZ.md)).
- **MDEC frame → pixels** — `str-demux` fully demuxes STR into MDEC bitstreams +
  audio; the pixel decode is deferred pending a reference frame
  ([docs/PSX_MEDIA.md](docs/PSX_MEDIA.md)).
- **Full VCD/SVCD image authoring** — `vcd-info` reads/writes the INFO/ENTRIES
  control files (pbc-less scope); MPEG encode + XA Form 2 mux + the PSD tree are
  scoped but deferred ([docs/VCD_AUTHORING.md](docs/VCD_AUTHORING.md)).
- **DVD menu authoring** — deferred to dvdauthor by design; DiscForge reads DVD
  structure, plans the shrink/reauthor, and writes structural IFOs
  ([docs/DVD_VIDEO_SHRINK.md](docs/DVD_VIDEO_SHRINK.md)).
</content>
</invoke>
