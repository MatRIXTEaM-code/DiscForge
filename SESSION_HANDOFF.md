# DiscForge — Session Handoff

_Resume point. Last updated 2026-07-26 (later)._

## Project

- Repo: `C:\dev\DiscForge` (C# / .NET 8, proprietary, clean-room).
- Tests: **1251 passing, 0 failing** (136 test classes) — Core + CLI verified in the cloud.
- Run tests: `dotnet run --project tests/Harness/Harness.csproj -c Release`
  (add `-- <Name>` to filter, e.g. `-- Library`).
- Build Core/CLI (both build in the cloud): `dotnet build src/DiscForge.Cli/DiscForge.Cli.csproj -c Release`.
- Build/run the WinForms app (Windows only — **cannot** build in the cloud sandbox):
  `dotnet build DiscForge.sln -c Release -t:Rebuild` then run
  `.\src\DiscForge.App\bin\Release\net8.0-windows\DiscForge.exe`, or `run-app.ps1` (run it as a FILE,
  not by pasting its lines — `$PSScriptRoot` is only set when executing a .ps1).
- CLI now has **133 commands** (added this session: `read-offset`, `frontend-export`, `m3u`, `1g1r`,
  `rebuild`, `torrentzip`, `hashgen`, `hashverify`, `ps1card-convert`, `rom-convert`, `save-convert`,
  `dat-diff`, `library-report`). The `dforge` on PATH is likely STALE —
  republish it (`dotnet publish src\DiscForge.Cli\DiscForge.Cli.csproj -c Release -o <its folder>`).
- Docs are current: `README.md`, `docs/INVENTORY.md`, `docs/CLI.md`, `docs/GUI.md`, plus the new
  `docs/REDUMP_PHYSICAL.md` (physical-dump design). Read these first for the full surface.
- **Local build needed:** the three new App tiles below were written against verified Core
  signatures but the WinForms App can't build in the cloud — run
  `dotnet build DiscForge.sln -c Release -t:Rebuild` to confirm them.

## How work flows (for whoever resumes)

- Edit files in the cloud repo, then deliver to the PC via `SendUserFile` →
  `mcp__remote-devices__device_commit_files` to `C:\dev\DiscForge\...`. The PC is the source of truth.
- Test harness is a CUSTOM xUnit-compatible runner: NO `Record.Exception`, NO `Xunit.Sdk`;
  `Assert.Throws<T>` and `[Fact]` are available.
- The WinForms App cannot build in the cloud, so App changes are written against verified Core
  signatures and confirmed by the user's local rebuild (the write-here / build-there loop).
- Clean-room boundary: faithful imaging, format parsing, protection DETECTION, patching,
  descrambling, save/asset reading are fine — NEVER defeat copy protection, region locks, or
  console security; never decrypt encrypted console content (Wii partitions, PSP DATA.PSP, etc.).

## Where we are (this arc is essentially complete)

DiscForge grew from a disc-imaging tool into a broad retro-preservation toolkit AND a workflow
product. Done and pushed (all green):

- **Format engine** — optical containers (CDI, BIN/CUE, ISO, GDI, NRG, MDS/MDF, CCD, CHD read+
  extract+create, CSO/ZSO, WBFS, XISO, UDF), filesystems, console-disc identify (PS1/2, Dreamcast,
  Saturn, CD-i, PSP, GameCube/Wii, Xbox, 3DO, PC-Engine CD, Neo-Geo CD, Mega-CD), cartridge ROMs
  (N64/SNES/Genesis/GB/GBA/NES + handhelds, No-Intro hashing), floppies (D64/ADF/FAT12), game audio
  (CD+G, ADX, PSF/SPC/VGM/NSF, VAB/SEQ, STR demux), console saves (PS1/2/VMU/GCI/N64/Saturn),
  partition tables (MBR/GPT/APA), patches (PPF/IPS/BPS), cheats (Game Genie/GameShark), BDMV.
- **The three "game-changer" workflows** (#111–113):
  - `library scan/rename` — batch identify+hash+DAT-verify+canonical-rename a whole collection.
  - `disc-convert` — universal any-format-in → any-format-out via a canonical `DiscModel` hub.
  - `submission-info` — redump.org-style per-track+combined hashes/cuesheet/sub-channel for a dump.
- **GUI** — new tiles: Examine (universal inspector), Library, Convert, Submit, plus **Extract**
  (pull files/saves out of WBFS / D64 / ADF / FAT12 / PS1 .mcr / GC card / VMU / PBP), **Cheat
  Codes** (Game Genie + GameShark decode/encode), and **Game Media** (ADX→WAV, CD+G→PNG with a
  preview). Launcher now has ~46 tiles.

## Open / blocked — need an external input, cannot finish in the cloud

| Item | Task | Needs |
|------|------|-------|
| ECM decode/encode | #91 | a real `.ecm` + its original `.bin` (docs/ECM.md) |
| RVZ → ISO decode | #102 | zstd/bzip2 libs (offline-unavailable) + a real `.rvz` (docs/RVZ.md) |
| MDEC pixel decode (STR→PNG) | part of #110 | a reference STR frame (docs/PSX_MEDIA.md) |
| Physical Redump dump-loop (drive I/O) | #116 | a real optical drive — see docs/REDUMP_PHYSICAL.md; software half is DONE |

## Recently completed (this session)

1. **#114 Docs refresh — DONE** (README/INVENTORY/CLI/GUI + this handoff).
2. **#115 GUI extract/action tiles — DONE (pending local build):** three new tiles — Extract,
   Cheat Codes, Game Media (see the GUI bullet above). Written against verified Core signatures;
   confirm with a local `dotnet build DiscForge.sln`.
3. **#116 Physical-dump half — software portion DONE:** `ReadOffset` + `Silence` in
   `src/DiscForge.Core/Audio/ReadOffset.cs` (sample/sector geometry, combined offset, `Apply`
   slide, guard-band over-read count, silence/peak analysis), 24 new tests, the `read-offset` CLI
   command, and the `docs/REDUMP_PHYSICAL.md` design doc. The physical drive-offset table lookup,
   offset auto-detection, and the C2+sub-channel dump loop remain for the user's Windows machine.

4. **Front-end / emulator export — DONE:** `DiscForge.Core.Frontend.FrontendExport` builds a
   RetroArch `.lpl`, an EmulationStation/RetroBat `gamelist.xml`, and a multi-disc `.m3u` from a
   DiscForge-scanned folder. CLI: `frontend-export <retroarch|gamelist> <dir> <out>` and
   `m3u <out> <disc…>`. New **Playlists** GUI tile. 7 tests, `docs/FRONTENDS.md`. Clean-room:
   cataloguing only. (Tiles now ~47; needs the local App rebuild like the other new tiles.)

5. **Collection-management suite — DONE:** built on the extended DAT parser (now reads
   `cloneof` + `description`; `GameName` parses region/langs/rev/flags from the No-Intro/Redump
   naming convention).
   - **1G1R** (`DiscForge.Core.Dat.OneGameOneRom`) — one best copy per game by region priority,
     clone-folding, per-disc; `1g1r` CLI writes a filtered DAT (`DatWriter`) or name list.
   - **Set rebuilder** (`DiscForge.Core.Library.SetRebuilder`) — verify + place under canonical
     DAT names (flat or per-game), report missing/unknown; `rebuild` CLI.
   - **TorrentZip** (`DiscForge.Core.Archive.TorrentZip`) — deterministic, TORRENTZIPPED-comment,
     with an honest zlib-interop caveat; `torrentzip` CLI.
   - **Hash sidecars** (`HashSidecar`) — SFV/md5/sha1 build+parse; `hashgen`/`hashverify` CLI.
   - **PS1 save converter** (`DiscForge.Core.PlayStation.Ps1CardConvert`) — raw ↔ DexDrive .gme ↔
     VGS, card data byte-for-byte; `ps1card-convert` CLI.
   - New **Sets** GUI tile (1G1R + rebuild). `docs/COLLECTION_TOOLS.md`. Tiles now ~48.

6. **ROM/save fix-up + DAT/report tools — DONE:**
   - **`rom-convert`** (`DiscForge.Core.Rom.RomConvert`) — N64 byte order (z64/v64/n64), SNES copier
     header strip/add, Genesis SMD interleave, iNES header strip; fixes DAT matching. Reversible.
   - **`save-convert`** (`DiscForge.Core.Saves.SaveConvert`) — generic reversible save transforms:
     word-swap (N64 endian), pad/resize to canonical sizes, trim padding.
   - **`dat-diff`** (`DiscForge.Core.Dat.DatDiff`) — added/removed/changed games between two DATs.
   - **`library-report`** (`DiscForge.Core.Library.CollectionReportHtml`) — self-contained HTML
     collection dashboard from a scan.
   - 27 new tests, all CLI, docs in `docs/COLLECTION_TOOLS.md`.

Next candidates: unblock ECM/RVZ/MDEC with real samples; build the Windows drive-offset table +
dump-loop orchestration from the REDUMP_PHYSICAL.md design when at a real drive; a LaunchBox
platform-XML exporter (needs a sample Data/Platforms/*.xml to match); or GUI tiles for the new
CLI-only tools (rom-convert / save-convert / torrentzip / hashgen). Much of the software surface is
now comprehensive — the remaining flagship items are hardware/sample-gated.

## Other carryover / gaps

- **Best-effort (documented, not broken)**: PS2 APA map, Saturn save DATA extraction (dir only),
  Amiga ADF hardlinks/dircache, PC-Engine/Neo-Geo identify heuristics, SNES/Genesis/GB Game Genie
  (no external reference vector), NDS identify.
- **Real-sample validation** (only the user can do): wild DiscJuggler CDI, real `.mpls`/`.clpi`,
  real GameCube/Wii/RVZ, real floppies, real ROMs vs a No-Intro DAT, real STR/PSF/`.ecm`.

## To resume

Reopen this chat (persists across reboot/devices), or start fresh pointed at `C:\dev\DiscForge`
+ this file + `README.md`. Drop sample files into `C:\dev\DiscForge\samples\` to unblock
ECM/RVZ/MDEC and the real-world validations.
