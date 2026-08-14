# PSX / disc-tool audit — what to utilize, what's covered, what's out of scope

An assessment of 35 tools (ConsoleCopyWorld's PSX utilities index plus a handful of
general disc-imaging tools) against DiscForge as it stands. Each is marked
**Covered** (DiscForge already does this), **Worth adding** (in-scope, clean-room,
a good fit), **Adjacent** (partly covered / possible but lower priority), or
**Out of scope** (circumvention or off-mission, excluded by the clean-room rule in
COMPARISON.md §13 — DiscForge *detects and preserves* protection and region marks
but never defeats them, and is a disc-imaging tool, not a cheat engine or
debugger).

## General disc-imaging / identification tools

| Tool | What it does | Status |
|------|--------------|--------|
| **IMGBurn** | Build + burn images (ISO/BIN/CUE), verify | **Covered** — `create`, `build-raw`, `burn`, `verify`, `convert` cover the common flow (DVD layer-break control is the main gap) |
| **IsoBuster** | Read many image formats; extract; recover damaged sessions | **Covered (core)** — reads CDI/ISO/BIN-CUE/NRG/MDS/CCD/GDI/XISO/UDF; `browse`/`extract-files`, `ecc-repair`, `recovery`. Exotic-format breadth and deep session-recovery are where IsoBuster still leads |
| **DAEMON Tools** | Mount an image as a virtual drive | **Covered** — `mount` |
| **PlayBackup** | Create a PS1 backup | **Covered** — the PS1 backup flow (rip → image → PPF), see PS1_BACKUP.md |
| **RAW2ISO** | Strip 2352 raw sectors to 2048 user data | **Covered** — `extract-sectors --as user`, `convert … .iso` |
| **CDR Identifier** | Read a CD-R's ATIP to name the media/dye manufacturer | **Worth adding (small)** — a drive query (READ TOC/ATIP) that fits the Drives/`inspect-raw` layer; not yet exposed |
| **Perfect Copy** | 1:1 copy that reproduces protection | **Adjacent** — DiscForge does raw DAO + sub-channel capture and *preserves* LibCrypt/sub-channel fingerprints; it never *defeats* protection, so the preservation half is covered and the circumvention half is deliberately not |
| **CloneCD Database** | Per-game CloneCD read/write setting profiles | **Adjacent** — DiscForge reads/writes `.ccd` (`interop`); the "database" is a curated settings list, not code. A preset table could be added but is low value |
| **Insektor(s)** | "Protection killer" — removes CD copy protection | **Out of scope** — circumvention, excluded by the clean-room rule |

## PSX asset / format tools — the real opportunity

DiscForge already lists and extracts files from a PS1 image (`browse` /
`extract-files`) and understands XA Mode 2 sectors. The natural next layer is
making those extracts *format-aware* — recognising the PlayStation's own file
formats. All of these are publicly documented (nocash psx-spx and others), so they
are clean-room and unit-testable by round trip, exactly like the formats already in
Core.

| Tool | What it does | Status |
|------|--------------|--------|
| **Playstation XA Copier** | Extract CD-ROM XA ADPCM audio streams | **Worth adding (high fit)** — DiscForge already reads XA Mode 2 sectors; decoding/exporting XA-ADPCM to WAV is a clean, testable extension |
| **TIM RIP / Tim File Ripper / HPTimRip** | Extract TIM texture images | **Worth adding (high fit)** — TIM is a small, well-documented image format; a `tim-extract`/`tim-to-png` is clean-room and testable |
| **BGM 2 WAV** | Convert PSX background music (XA / SEQ+VAB) to WAV | **Worth adding (2nd tier)** — the XA path rides on the XA decoder above; SEQ/VAB (MIDI-like + sample bank) is a larger, separate job |
| **TMD RIP** | Extract TMD 3-D models | **Worth adding (2nd tier)** — documented model format; extraction is tractable |
| **TMD2DXF** | Convert TMD models to DXF (CAD interchange) | **Worth adding (2nd tier)** — a converter on top of the TMD reader |
| **TOD Info / TOD RIP** | Inspect / extract TOD animation streams | **Worth adding (2nd tier)** — documented animation format |
| **PSX Padding Tool** | Pad a PS-EXE / image to correct alignment | **Worth adding (trivial)** — a few lines once a PS-EXE header parser exists |
| **WinBin2Src** | Emit a binary file as a C/ASM source array | **Adjacent (trivial)** — a dev convenience; easy if wanted, low fit for an imaging tool |
| **Search** | Byte-pattern search across a file | **Adjacent** — partly covered by `view-sector` / `inspect-raw`; a generic pattern search is easy but low value |
| **ApplyEXE** | Apply / inject a PS-EXE | **Adjacent** — a PS-EXE header reader is worth having; injection overlaps homebrew dev, off-mission |
| **PSX EXE Linker** | Link / relocate a PS-EXE | **Out of scope** — homebrew build toolchain, not imaging |
| **PSX Chipmunk BASIC** | A BASIC interpreter for the PSX | **Out of scope** — a programming language, not a disc tool |
| **MIPSiCE** | MIPS (R3000) in-circuit debugger | **Out of scope** — a debugger, not imaging |
| **Memory Card Renamer** | Rename PS1 memory-card save titles | **Out of scope** — save-file editing, not disc imaging |
| **DemoMenu** | Build a demo-disc boot menu | **Out of scope (niche)** — a boot-menu authoring niche, far from the core mission |

## PAL/NTSC & region tools — mostly the boundary

This whole cluster is patching, which invites a clean split. **Video-mode
conversion** (changing PAL↔NTSC display timing) is a modification of the game's
display, not defeating any protection; DiscForge already *applies* such patches
through its PPF engine, so a PAL4U/Zapper patch works today — it just doesn't
*generate* the conversion. **Region-bypass, swap-trick, and PAR/cheat-code
generation** are either circumvention of region/anti-mod protection or cheat
authoring, and are out of scope by rule.

| Tool | What it does | Status |
|------|--------------|--------|
| **PAL4U** | NTSC↔PAL graphic-mode patcher on ISO/EXE | **Adjacent** — the PPF patches it makes are applied by DiscForge (`ppf-apply`); native generation is possible but lower priority (it's modification, not preservation) |
| **PALNTSC Color Mode Converter** | Analyse image, emit PAL/NTSC codes | **Adjacent** — same family as PAL4U |
| **Zapper 2000** | Bidirectional PAL↔NTSC patcher | **Adjacent** — same family |
| **ASA Patcher** | PS1 area-code patch + PS2 swap-trick + EA patch | **Out of scope** — area-code/swap-trick defeat region/copy protection |
| **Patch-It** | Modify CDRWIN images for import (region) | **Out of scope** — region circumvention |
| **SetRegion** | Alter the region byte in a PSX RAW image | **Out of scope** — region circumvention |
| **Gamester** | Change region to bypass region lock | **Out of scope** — region circumvention |
| **MODPAR Code Generator** | ActionReplay codes to defeat anti-mod checks | **Out of scope** — anti-protection + cheat codes |
| **PALPAR Code Generator** | PAR codes to run PAL games on NTSC | **Out of scope** — cheat-code generation |
| **Y-Fix Par Code Finder** | PAR codes to re-centre after a mode conversion | **Out of scope** — cheat-code generation |

## Update — built (July 2026)

Everything in-scope from the lists above has since been implemented, clean-room
and round-trip tested, following the same build+test+commit rhythm as the rest of
Core (639 harness tests). New CLI commands:

- `psx-exe-info` — PS-EXE header reader (entry, load layout, region marker).
- `psx-pad` — pad a file to a boundary, or a PS-EXE payload to 0x800 (`--psexe`).
- `bin2src` — emit a binary as a C array or GNU-assembler `.byte` block.
- `search` — find a `--hex`/`--ascii` pattern's offsets (chunked, boundary-safe).
- `tim-info` / `tim-extract` — TIM texture → PNG (4/8/16/24bpp + CLUT), via a
  dependency-free PNG encoder.
- `xa-extract` — CD-ROM XA ADPCM audio → WAV (decoder from the public XA spec).
- `tmd-info` / `tmd2dxf` — TMD model geometry; DXF point-cloud export.
- `tod-info` — TOD animation structure (round-trip validated, pending a real
  sample).
- `cdr-info` — ATIP media-code → dye/stamper manufacturer (the "CDR Identifier"
  job), reusing DiscForge's existing `MediaIdentityParser` and manufacturer table;
  live drive reads happen in the Windows GUI/device layer.

Honest limits carried in the code: XA is 4-bit mode (the common case), 8-bit is
skipped; TIM export flattens semi-transparency to opaque; TMD2DXF is a vertex
point cloud (primitive polygon decoding is a follow-up); TOD awaits a real-file
check. The out-of-scope circumvention/cheat tools remain excluded, and PAL/NTSC
patch *generation* remains the open judgment call below.

## Recommendation — what to actually build

The clear, in-scope, high-value work is a **PSX asset-extraction layer** that turns
DiscForge's existing "extract files from a PS1 image" into "understand PS1 file
formats," clean-room from public documentation and validated the same way every
other format in Core is:

1. **XA-ADPCM audio export** (`Playstation XA Copier`, and the XA half of
   `BGM 2 WAV`) — decode CD-ROM XA ADPCM to WAV. Best fit: the XA Mode 2 sector
   handling already exists; this adds the ADPCM decode + WAV mux.
2. **TIM image extraction** (`TIM RIP` / `Tim File Ripper` / `HPTimRip`) — parse
   TIM and export PNG. Small, well-documented, immediately useful.
3. **A PS-EXE header reader** — underpins `ApplyEXE` inspection and the
   `PSX Padding Tool`, and rounds out the PS1 identification `ps2-info` already does.
4. *(second tier)* **TMD / TOD** model and animation extractors, and **TMD2DXF**.

Everything else is either already covered, a trivial convenience, or excluded by
the clean-room boundary. Video-mode (PAL/NTSC) patch *generation* is the one
genuine judgment call left open: DiscForge already applies those patches, and
generating them is feasible, but it is game modification rather than preservation —
worth a deliberate decision before building, not an automatic yes.
