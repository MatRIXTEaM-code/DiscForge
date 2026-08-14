# DiscForge

[![CI](https://github.com/MatRIXTEaM-code/DiscForge/actions/workflows/ci.yml/badge.svg)](https://github.com/MatRIXTEaM-code/DiscForge/actions/workflows/ci.yml)
[![Build](https://github.com/MatRIXTEaM-code/DiscForge/actions/workflows/build.yml/badge.svg)](https://github.com/MatRIXTEaM-code/DiscForge/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/MatRIXTEaM-code/DiscForge)](https://github.com/MatRIXTEaM-code/DiscForge/releases/latest)
[![License: GPL-3.0-or-later](https://img.shields.io/badge/License-GPL--3.0--or--later-blue.svg)](LICENSE)

*A clean-room disc-imaging and retro-preservation toolkit for C# / .NET 8.*

**Free software — GPL-3.0-or-later.** See [LICENSE](LICENSE) and [NOTICE](NOTICE).
Copyright (C) 2026 MaTRIX TeAm.

DiscForge reads, writes, converts, verifies, repairs and **re-burns** optical-disc
images (CD / DVD / Blu-ray and the classic console formats), and its surface is
much wider than discs: floppy images, cartridge ROMs, console saves and memory
cards, game audio, ROM patches, a collection manager that verifies a whole tree
against a Redump/No-Intro DAT, a universal image converter, and a Redump-style
submission-info generator. Everything is built on a pure, platform-agnostic Core
engine, surfaced through a Windows WinForms GUI and a 380-command `dforge`
command-line tool, backed by **2,400+ automated tests**.

**New here?** Start with the **[Quickstart](docs/QUICKSTART.md)** — it maps the ten
things people most often want (identify a file, check a dump is good, build a
bootable ISO, catalog a collection, burn a disc) to the exact commands. On
Windows, `.\launch-discforge.ps1` builds and starts the GUI in one step.

## The integrity model

One rule shapes everything: **provably correct or declined.** Every output is
proven against independent evidence — the checksums a format itself carries,
multi-pass consensus reads, reference implementations, external databases — or
the operation is refused. DiscForge never silently emits possibly-corrupt data:

- Compressed AaruFormat blocks decode **only** if they match their stored
  CRC-64; CHD extraction self-verifies against the image's SHA-1.
- Burn verification rebuilds the golden image and compares the disc's read-back
  byte-for-byte, sub-channel included — an empty or short read-back **fails**.
- Unreadable sectors are recorded in a sidecar map that survives every later
  format conversion, because a zero-filled hole hashes like real data.
- Codec implementations are validated against reference implementations:
  LZMA vs liblzma streams, FLAC vs reference-encoder streams, fuzzy hashing
  byte- and score-exact vs ssdeep 2.14.1.
- Unsupported structures (encrypted content, unverified compression variants)
  are declined with an explanation, never guessed at.

**Platform support.** The `dforge` CLI runs on Windows, macOS and Linux; all
imaging analysis, conversion and filesystem work is fully cross-platform.
Burning uses IMAPI2/SPTI on Windows (including RAW DAO-96), `hdiutil` on macOS
and `growisofs`/`wodim` on Linux; a native Linux SG_IO SCSI layer is in place
with real-hardware validation in progress. The graphical app is Windows-only.

## What it does

Every format below is implemented from public documentation and covered by the
offline test harness.

- **Preservation dumping** — raw 2352+96 reads with multi-pass sub-channel
  consensus (majority-voted Q that preserves LibCrypt-style intentional
  errors), C2 error mapping, per-sector provenance, read-stability and
  media-quality (C2 band) scanning, drive capability/overread profiling.
- **Disc image containers** — CDI (native read/write), BIN/CUE, ISO, GDI, NRG,
  Alcohol MDS/MDF, CloneCD CCD, CHD (read / verify / extract / **create**,
  chdman-accepted), CSO/ZSO, WBFS, XISO, RVZ/WIA decode, ECM decode, plus a
  universal any-to-any converter (`disc-convert`).
- **Aaru interop** — reads AaruFormat images (uncompressed, LZMA and FLAC, every
  block CRC-64-gated), writes uncompressed AaruFormat, exports CICM metadata
  sidecars.
- **Filesystems** — ISO 9660 (Joliet, Rock Ridge), UDF 1.02–2.50 read and 1.02
  write, El Torito boot records, Xbox XDVDFS, and read-only extraction from
  FAT, exFAT, NTFS, ext2/3/4 and HFS volume images; hybrid-disc views are
  cross-checked and divergences reported.
- **Recovery** — `dforge recover` grades a damaged image (INTACT / RECOVERABLE /
  DAMAGED / UNREADABLE) with concrete next steps and an HTML report; salvage
  planning, filesystem-constrained re-reads, orphan carving, disc health/rot
  mapping.
- **Authoring & burning** — ISO mastering with BIOS+UEFI hybrid El Torito boot,
  automatic DVD-9 layer-break planning (ECC- or VOBU-aligned), DVD-Video and
  BDMV build/plan, and RAW DAO-96 burning with protection re-creation, verified
  by consensus read-back.
- **Audio** — secure ripping with AccurateRip verification, drive read-offset
  detection, C2-guided re-read planning with conservative confidence grading
  (self-consistency can never earn "Verified" — only independent corroboration
  can), CD-DA / CD+G / XA-ADPCM / VAG / ADX, de-emphasis, HDCD detection.
- **Console formats** — PS1/PS2, Dreamcast, Saturn, CD-i, PSP, GameCube/Wii,
  Xbox identify; cartridge ROMs (N64, SNES, Genesis, GB/GBC/GBA, NES) with
  No-Intro checksums; saves and memory cards (PS1, PS2, VMU, GCI, N64, Saturn);
  PPF/IPS/BPS patches; Game Genie / GameShark decode.
- **Verification & cataloguing** — CRC-32/MD5/SHA-1/SHA-256, DAT verify with
  evidence-strength labelling (SHA-1 vs MD5 vs CRC-32), AccurateRip, entropy
  and ssdeep-compatible fuzzy hashing, library scan/rename, 1G1R, TorrentZip,
  collection triage, Redump submission packaging.
- **Devices (Windows)** — drive detection, ripping, IMAPI2 + raw SPTI burning,
  mount as a drive, disc-quality scan, CD-R manufacturer id.

## Clean-room boundary

- **In scope:** faithful imaging, format parsing, protection *detection*
  (LibCrypt / sub-channel fingerprints), patching, descrambling of publicly
  documented transforms, and reading saves and assets.
- **Out of scope, by rule:** anything that defeats copy protection, region
  locks or console security; cheat-code *generation*; and decrypting encrypted
  console content. DiscForge detects and preserves protection; it never
  circumvents it. See [NOTICE](NOTICE) — this is a deliberate and permanent
  limitation, unchanged by the GPL relicense.

## How it compares

An honest head-to-head against ImgBurn, Aaru, DiscImageCreator, IsoBuster,
Alcohol/CloneCD, cdrtools, CHDMAN and EAC — including where each of them still
leads — is maintained at
[docs/comparison_all_products.html](docs/comparison_all_products.html).

## Architecture

```
DiscForge.sln
├── src/
│   ├── DiscForge.Core/      # All format logic, pure managed — builds and runs anywhere
│   ├── DiscForge.Devices/   # Drive access: SPTI (raw MMC) + IMAPI2 (burning), Windows
│   ├── DiscForge.Cli/       # `dforge` — 380 commands, cross-platform
│   └── DiscForge.App/       # WinForms GUI (net8.0-windows), thin shell over Core
├── tests/
│   ├── Harness/             # Custom offline test harness
│   └── DiscForge.Core.Tests/  # 2,400+ tests, reference vectors included
└── docs/
```

Design rules: **Core is pure** (unit-testable on Linux CI); **capability
detection, not assumption** (drives are interrogated, features light up
per-drive); **no kernel drivers** (SPTI + IMAPI2 + SG_IO only); **clean-room**
(every format from public documentation, validated against an independent
oracle where one exists).

## Building & running

Requires the .NET 8 SDK. Core, CLI and tests build and run on any platform
.NET 8 supports; the WinForms App is Windows-only.

```
# CLI, any platform
dotnet build src/DiscForge.Cli/DiscForge.Cli.csproj -c Release
dotnet src/DiscForge.Cli/bin/Release/net8.0/dforge.dll               # command list
dotnet src/DiscForge.Cli/bin/Release/net8.0/dforge.dll identify path/to/image

# tests (offline, no hardware needed)
dotnet test

# GUI (Windows): builds, refreshes the installed CLI, launches
.\launch-discforge.ps1
```

## Documentation

| Area | Doc |
|------|-----|
| Capability inventory (the map of everything) | [docs/INVENTORY.md](docs/INVENTORY.md) |
| Full `dforge` command reference | [docs/CLI.md](docs/CLI.md) |
| WinForms GUI reference | [docs/GUI.md](docs/GUI.md) |
| CDI byte-level format spec | [docs/CDI_FORMAT.md](docs/CDI_FORMAT.md) |
| Image create / El Torito / Rock Ridge | [docs/CREATE.md](docs/CREATE.md) |
| Filesystem browsing | [docs/BROWSING.md](docs/BROWSING.md) |
| UDF read/write | [docs/UDF.md](docs/UDF.md) |
| Raw DAO, sub-channel, sector tools | [docs/RAW_DAO.md](docs/RAW_DAO.md), [docs/SECTOR_TOOLS.md](docs/SECTOR_TOOLS.md) |
| CHD | [docs/CHD.md](docs/CHD.md), [docs/CHD_MAP.md](docs/CHD_MAP.md) |
| Audio / AccurateRip | [docs/AUDIO.md](docs/AUDIO.md), [docs/ACCURATERIP.md](docs/ACCURATERIP.md) |
| Protection detection / CloneCD interop | [docs/PROTECTION_AND_INTEROP.md](docs/PROTECTION_AND_INTEROP.md) |
| DVD-Video shrink / reauthor | [docs/DVD_VIDEO_SHRINK.md](docs/DVD_VIDEO_SHRINK.md) |
| PlayStation / Dreamcast / GameCube / Xbox | [docs/PSX_TOOLS.md](docs/PSX_TOOLS.md), [docs/DREAMCAST_TOOLS.md](docs/DREAMCAST_TOOLS.md), [docs/GAMECUBE.md](docs/GAMECUBE.md), [docs/XBOX.md](docs/XBOX.md) |
| DAT verify / Redump submission | [docs/DAT_VERIFY.md](docs/DAT_VERIFY.md), [docs/REDUMP_PHYSICAL.md](docs/REDUMP_PHYSICAL.md) |
| Collection tools (1G1R, rebuild, TorrentZip) | [docs/COLLECTION_TOOLS.md](docs/COLLECTION_TOOLS.md) |
| Devices / mount / erase & speed | [docs/DEVICES.md](docs/DEVICES.md), [docs/ERASE_AND_SPEED.md](docs/ERASE_AND_SPEED.md) |
| Registry submissions (COPTR, awesome lists) | [docs/registry-submissions.md](docs/registry-submissions.md) |

The `docs/` folder holds ~40 further per-format documents recording each
format's derivation from public sources.

## Known limitations (honest, by design)

DiscForge ships a capability only when it can be validated; the rest is
documented as deferred rather than half-working:

- **AaruFormat LZMA-subchannel-transform** blocks (a rare variant) are declined
  pending a real fixture; uncompressed, LZMA and FLAC decode fully, CRC-gated.
- **NTFS-compressed and encrypted files** are listed but their extraction is
  declined (decode unverified); same for ext4 inline-data/encrypted inodes.
- **RVZ decode** zero-fills the "junk" (disc padding) regions — output is
  data-exact but not hash-identical to the original disc where junk differed.
- **Linux SG_IO** drive layer is structurally complete and layout-tested, but
  awaits validation against real optical hardware.
- **MDEC frame → pixels**, **full VCD/SVCD authoring** and **DVD menu
  authoring** remain deferred (see the per-format docs).
