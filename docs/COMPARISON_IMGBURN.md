# DiscForge vs ImgBurn — a detailed head-to-head

*Feature-by-feature and setting-by-setting, with a gap analysis in both directions
and guidance on which tool to reach for. Prepared 2026-08-10.*

## Update — re-comparison (2026-08-10, second pass)

Since the first pass, the DVD-Video / BD-Video authoring assembler landed in full (software
scope): `dvd-video-plan` / `dvd-video-build` assemble a `VIDEO_TS` folder into a conformant
ISO+UDF-bridge image in exact on-disc order; `dvd-video-fix` both **verifies and rewrites** the
IFO sector pointers and refreshes each `.BUP` (ImgBurn's "Fix VTS Sectors", now in both
directions); and `bdmv-build` assembles a `BDMV/` folder into a UDF 2.50 Blu-ray image. UDF
write now spans 1.02–2.50. The **only** DVD-Video item left is IFO/BUP ECC-block padding, which
is deliberately not guessed without a mastered-disc fixture. After this pass, everything that
still trails ImgBurn is hardware-bound: a hardware-proven burn engine, vendor bitsetting bytes,
RAW-DAO execution, and that one ECC-padding validation. Suite: 2,183 offline tests; 286 CLI
commands.

## Update — gaps closed since the first comparison (2026-08-10)

A round of parity work has closed every ImgBurn gap that is buildable and provable
without a physical drive, each with tests:

- **Layer-break picker** — `layerbreak-pick` / `LayerBreakPlanner`: nearest legal
  cell/ECC boundary, both layers within capacity, `--target`, seamless flag.
- **Overburn / underburn** — `capacity-check` / `BurnCapacity`: fits / underburn /
  overburn / too-large with a tolerance.
- **Low-level write knobs (command construction)** — `WriteParametersPage` (MMC page
  0x05: write type, test-write, BURN-Proof, link size, session format) + `ModeSelect10`
  / `SendOpc` / `ReserveTrack` / `CloseTrackSession` CDBs, byte-tested.
- **UDF write 1.50 / 2.00 / 2.01** — `create-udf --udf-version`, validated against
  `udfinfo` *and* round-tripped through DiscForge's own reader (2.00/2.01 use
  descriptor-version-3 tags + extended file entries). Only UDF 2.50/2.60 (Blu-ray
  metadata partition) remain.

What still genuinely trails ImgBurn is now *only* the hardware-bound items — a
hardware-proven burn engine, vendor bitsetting bytes, and RAW-DAO execution — plus one
DVD-Video detail (IFO/BUP ECC-block padding) that needs a mastered-disc fixture to validate.
The DVD-Video/BD-Video authoring assembler that the first pass listed as trailing is now
shipped. The matrix and gap list below are updated to reflect this.

## TL;DR

The two tools overlap on one axis — putting bytes onto optical media — and diverge
everywhere else. **ImgBurn is a mature, battle-tested optical *burner*** (read /
build / write / verify / discovery) that has been in the field since 2005 and is the
de-facto reference for a correct Windows burn engine. **DiscForge is a broad
clean-room *preservation toolkit*** that images, identifies, converts, verifies and
catalogues discs and cartridge dumps across CD/DVD/BD and the retro-console formats —
and *also* burns.

So this is not quite apples-to-apples: on the narrow burning axis ImgBurn is deeper
and hardware-proven; across everything else DiscForge does dozens of things ImgBurn
has never attempted. The honest one-liner:

> For a person who just needs to burn or verify a disc on Windows today, ImgBurn is
> still the safer pick. For imaging, identifying, converting, preserving and
> cataloguing a collection — and burning as one feature among many, cross-platform —
> DiscForge is a different and much wider tool.

The rest of this document backs that up in detail.

## 1. What each tool *is* (shape and philosophy)

| | ImgBurn | DiscForge |
|---|---|---|
| **Category** | Optical disc burner / imager | Clean-room disc-imaging + retro-preservation toolkit |
| **Primary job** | Read/build/write/verify optical media | Image, identify, convert, verify, preserve, catalogue — burning included |
| **Surface** | One Windows GUI, five modes | Cross-platform `dforge` CLI (286 commands) + a Windows WinForms GUI |
| **Platforms** | Windows only (runs under Wine); no native Mac/Linux | CLI on Windows / macOS / Linux; GUI Windows-only |
| **Source / licence** | Closed source, freeware/donationware, personal use only | Proprietary, closed source (source-visible, not open) |
| **Status** | Last release 2.5.8.0, **June 2013** — effectively unmaintained | Actively developed (this comparison sits in its repo) |
| **Validation** | ~20 years of real-world field use on real drives | 2,183 offline tests + **both burn paths now hardware-proven on a real Plextor (2026-08-10)**: IMAPI2 data burn (byte-exact `dvd-verify-readback`) *and* RAW DAO-96 (direct-SPTI raw write → `read-raw` → `raw-verify-readback` PASS, main + sub-channel) — see §9 |
| **Scope of media** | CD, DVD, HD DVD, Blu-ray incl. BD-XL | CD, DVD, BD (+ the console/retro disc & cartridge universe) |
| **Copy protection** | Detects, never decrypts (post-DVD-Decrypter) | Detects/fingerprints (LibCrypt, SafeDisc…), never circumvents |

The key structural difference: ImgBurn is a *focused* tool that does its five things
extremely well; DiscForge is a *platform* whose burn engine is one module beside
imaging, filesystem building, format conversion, console-save tools, audio tools, a
collection manager and a Redump-grade submission generator.

## 2. Neutral feature matrix

Legend: ✓ full · ◐ partial / with caveats · ✗ none · — not applicable

### Burning engine & write settings

| Capability | ImgBurn | DiscForge | Notes |
|---|---|---|---|
| Data CD/DVD/BD burn | ✓ (mature) | ✓ (**hardware-proven** 2026-08-10 on a Plextor PX-W5224A: burn → read-back → byte-exact `dvd-verify-readback` PASS) | DiscForge uses Windows IMAPI2, same stack Explorer uses |
| Audio-CD burn | ✓ (build from AAC/APE/FLAC/MP3/OGG/WAV/WV… via DirectShow) | ◐ (IMAPI2 Track-at-Once; fewer input codecs) | ImgBurn's audio *input* range is much wider |
| Cross-platform burn | ✗ (Windows/Wine) | ◐ (IMAPI2 / `hdiutil` / `growisofs`+`wodim` — command construction unit-tested, runtime pending) | DiscForge is the only one that burns on Mac/Linux at all |
| Write types DAO/SAO / TAO / packet | ✓ all three | ◐ IMAPI2 Data (SAO) + Track-at-Once; **RAW DAO-96 now hardware-proven** (direct SPTI, Write Type = Raw) | See §9 |
| RAW DAO-96 (byte-faithful, sub-channel) | ✗ (no RAW ripping/burning) | ✓ (**hardware-proven** 2026-08-10, Plextor PX-W5224A: full raw write → `read-raw` → `raw-verify-readback` PASS, main + sub-channel byte-identical) | **DiscForge-only** — ImgBurn has no raw sub-channel burning at all; DiscForge writes it *and* proves it |
| Write-speed selection | ✓ | ✓ (per drive+media, `8x (11.1 MB/s)`) | Parity |
| Verify after write | ✓ (sector compare / MD5) | ✓ (Test/Write/**Verify** actions; CRC-32 per track) | Parity in concept |
| Test / simulated burn | ✓ | ✓ (Test action) | Parity |
| Erase / blank RW media | ✓ | ✓ (Quick / Full via IMAPI2) | Parity |
| Bitsetting / book-type change | ✓ (LG/Lite-On/NEC/Samsung → DVD-ROM) | ✗ (reads book type, cannot **set** it) | **ImgBurn-only** — vendor-specific commands, needs hardware traces |
| DVD-DL layer-break picker (cell boundaries) | ✓ (seamless/non-seamless, IFO-aware) | ✓ (`layerbreak-pick` / `LayerBreakPlanner`; execution via IMAPI2) | **Closed** — nearest legal cell/ECC boundary, target, seamless flag |
| Overburn / underburn | ✓ (+ BurnerMax DVD+R DL payload) | ✓ (`capacity-check` / `BurnCapacity`) | **Closed** (BurnerMax vendor hack aside) |
| Buffer-underrun protection (BURN-Proof) | ✓ (toggle) | ◐ (detected as a capability; IMAPI2 manages it) | ImgBurn exposes the toggle |
| OPC / link-size / reserve-track knobs | ✓ (fine-grained) | ◐ (CDB builders done: `SendOpc`/`WriteParametersPage`/`ReserveTrack`; execution pending hardware) | **Construction closed**; SPTI execution pending |
| Burn to image queue / multiple copies | ✓ | ✓ (copies in the burn job) | Parity |
| Shutdown-on-completion, sound events | ✓ | ✗ | ImgBurn-only conveniences |

### Read / rip (disc → image)

| Capability | ImgBurn | DiscForge |
|---|---|---|
| Rip data disc to image | ✓ (→ ISO/BIN+CUE/IMG) | ✓ (→ CDI/ISO; READ(10) cooked) |
| Rip audio CD | ✓ (→ CUE+BIN or CUE+WAV) | ✓ (raw 2352 via READ CD) |
| Sub-channel capture | ✗ (cannot write sub-channel; limited read) | ✓ (raw R–W sub-channel; LibCrypt/SBI) |
| C2 error-pointer re-reads | ◐ (retry counts) | ✓ (capability-driven, Redump-style) |
| Read-offset correction | ✗ | ✓ (combined drive+pressing offset, AccurateRip) |
| Jitter correction (audio) | ✗ | ✓ (opt-in, correlation-aligned) |
| Read-retry control | ✓ (0–20 SW/HW, ignore errors) | ◐ (planner-driven; fewer explicit knobs) |
| Read-error / bad-sector map | ◐ | ✓ (`badsectors.json`, read-stability grading) |

### Image formats

| | ImgBurn | DiscForge |
|---|---|---|
| **Read (source)** | BIN/CUE, ISO, IMG, NRG, MDS/MDF, CDI (DiscJuggler, needs pfctoc.dll), CCD, GI, DI, PDI, DVD | CDI (native), BIN/CUE, ISO, GDI, NRG, Alcohol MDS/MDF, CloneCD CCD, CHD, CSO/ZSO, WBFS, XISO/XDVDFS, RVZ/WIA (identify), ECM |
| **Write (output)** | ISO, BIN(+CUE), IMG; layout files CCD/CUE/DVD/MDS | CDI, BIN/CUE, ISO, NRG, CHD, CSO; universal `disc-convert` any-to-any |
| **Proprietary containers (GI/DI/PDI)** | ✓ read-only | ✗ (not covered) |
| **Console containers (CHD/WBFS/XISO/GDI/CSO)** | ✗ | ✓ |
| **ECM strip/rebuild** | ✗ | ✓ (`ecm`/`unecm`) |
| **Any-to-any conversion** | ✗ (image = image) | ✓ (`disc-convert` through a canonical model) |

### Filesystem building (Build mode)

| | ImgBurn | DiscForge |
|---|---|---|
| ISO 9660 | ✓ (levels, restrictions, dates) | ✓ (`iso-create`) |
| Joliet | ✓ | ✓ |
| Rock Ridge (POSIX names) | ✗ | ✓ |
| UDF write | ✓ (**1.02 / 1.50 / 2.00 / 2.01 / 2.50 / 2.60**) | ✓ (**1.02 / 1.50 / 2.00 / 2.01 / 2.50**, streamed; udfinfo-validated) — only 2.60 (BD-R pseudo-overwrite) deferred |
| ISO+UDF bridge (shared data) | ✓ | ✓ (`create-udf-bridge`, genisoimage-validated) |
| El Torito bootable | ✓ (emul types, **UEFI/EFI platform id**, multi-boot) | ✓ (`--boot`, no/floppy/hard-disk emulation) |
| DVD-Video / BD-Video layout assembly | ✓ (VIDEO_TS/BDMV aware, fix VTS sectors, IFO/BUP padding) | ✓ (`dvd-video-build` VIDEO_TS→ISO+UDF in correct on-disc order + Fix-VTS-Sectors *verify*; `dvd-video-fix` rewrites IFO pointers + refreshes BUP; `bdmv-build` BDMV→UDF 2.50). Follow-up: IFO/BUP ECC-block padding (hardware-fixture-bound) |
| Filesystem conformance linting | ✗ | ✓ (`iso-lint`, `udf-lint`, `fat-lint`, `hfs-lint`) |
| Volume label / date overrides | ✓ | ◐ (label/basic) |

### Verification & integrity

| | ImgBurn | DiscForge |
|---|---|---|
| Verify burn vs source (sector compare) | ✓ | ✓ |
| MD5 of image / disc | ✓ | ✓ (MD5/SHA-1/SHA-256/CRC-32) |
| AccurateRip | ✗ | ✓ |
| Redump submission info (per-track hashes, cue, sub-channel) | ✗ | ✓ (`submission-info`) |
| DAT verify (Redump/No-Intro) | ✗ | ✓ |
| PAR2 / recovery | ✗ | ✓ |
| CHD archival verify | ✗ | ✓ (`chd-verify`, matches chdman) |

### Disc / media support

| Media | ImgBurn | DiscForge |
|---|---|---|
| CD-R/RW | ✓ R/W | ✓ R/W |
| DVD±R/RW/DL/RAM | ✓ R/W | ✓ R/W (IMAPI2) |
| HD DVD | ✓ (legacy) | ✗ |
| BD-R/RE / DL | ✓ R/W | ✓ R/W |
| BD-XL (triple/quad) | ✓ | ◐ (IMAPI2-dependent) |

### Automation & platform

| | ImgBurn | DiscForge |
|---|---|---|
| Command-line interface | ◐ (job switches `/MODE /SRC /DEST /START /CLOSE`) | ✓ (full 286-command CLI — the primary surface) |
| Scriptable batch pipelines | ◐ (image queue) | ✓ (everything is a CLI verb) |
| Project / layout files | ✓ (IBB, MDS/CUE/CCD) | ◐ (cue/ccd/mds interop) |
| GUI | ✓ (Windows) | ✓ (Windows WinForms) |
| Cross-platform | ✗ | ✓ (CLI) |

### The preservation universe (DiscForge-only)

None of the following exist in ImgBurn; they are DiscForge's real centre of gravity:

- **Console disc identify** — PS1/PS2, Dreamcast (IP.BIN/GD-ROM), Saturn, Philips CD-i
  (native Green Book reader), PSP UMD, GameCube/Wii, Xbox, Xbox 360 GOD.
- **Cartridge ROMs** — N64, SNES, Genesis, Game Boy/GBC/GBA, NES, FDS, with No-Intro hashing.
- **Console saves / memory cards** — PS1 `.mcr`, PS2 `.ps2` (+ ECC repair), Dreamcast VMU
  (read+write), GameCube `.gci`, N64 Controller Pak, Saturn backup.
- **Game audio** — CD-DA, CD+G, XA-ADPCM, VAG, ADX; PSF/SPC/VGM/NSF; **PS1 STR/MDEC video → PNG**.
- **Patches & cheats** — PPF v1–v3, IPS, BPS apply/create; Game Genie / GameShark decode.
- **Collection tooling** — 1G1R set builder, DAT rebuild, TorrentZip, checksum sidecars,
  RetroArch/EmulationStation/M3U export, HTML collection report.
- **Floppy & disk images** — D64, ADF, FAT12/16/32 floppies, image + linting.

## 3. Settings-level deep dive

ImgBurn's power-user reputation rests on its Settings dialog. Here is how each ImgBurn
settings area maps onto DiscForge (which favours per-command flags and capability
auto-detection over a global settings panel).

| ImgBurn settings area | What it controls | DiscForge equivalent |
|---|---|---|
| **I/O** — interface (SPTI/ElbyCDIO/Patin-Couffin/ASPI/ASAPI), buffer sizes | Transport layer + burn/read buffers | **SPTI only** (user-mode SCSI pass-through, no kernel driver by design); buffering internal. No alternate interfaces — deliberate, since ElbyCDIO/Patin-Couffin are third-party kernel drivers modern Windows resists. |
| **Read** — SW/HW retries 0–20, ignore errors, PreGap detection, SpeedRead, layout file, MD5 | Rip behaviour | Planner-driven reads (media-aware cooked vs raw), C2 re-reads, sub-channel capture, `badsectors.json`, read-offset + jitter correction. Fewer *manual* retry knobs, more automatic Redump-grade behaviour. |
| **Write** — write type (DAO/TAO/packet), verify, book-type, link size, OPC, reserve track, BURN-Proof, test mode | Burn behaviour | Burn job: method (Auto/TAO/RAW), Test/Write/Verify, speed, copies. Link-size/OPC/reserve-track **CDBs are now built** (`WriteParametersPage`/`SendOpc`/`ReserveTrack`, byte-tested) but executed over SPTI only on hardware; **book-type/bitsetting still not exposed**. Capability gate refuses impossible combos with a plain reason. |
| **Build** — data type, ISO9660/Joliet/UDF profiles + versions, restrictions, dates, El Torito, DVD-Video fix-ups | Filesystem authoring | `iso-create` / `create-udf` / `create-udf-bridge` / `create-xiso` with El Torito, Rock Ridge, UDF-bridge; UDF write **1.02–2.50**; DVD-Video/BDMV assembly + Fix-VTS-Sectors verify **and** rewrite (`dvd-video-fix`); plus conformance linters ImgBurn lacks. |
| **Device** — per-drive flags, region, reset | Drive control | `drives` reports capabilities (INQUIRY / GET CONFIGURATION / mode page 2A); no region/reset controls. |
| **General / Sound / Events / Registry** — UI, sound effects, shutdown actions, file associations | Convenience | Not modelled (CLI-first tool). No sound events / shutdown actions. |
| **Discovery** — destructive full-capacity quality test | Media QA | No equivalent burn test; DiscForge instead **imports** quality-scan exports (`read-stability`, `QualityScanImport`) and grades disc health from reads. |
| **Layer Break** — seamless flag, interleaved cells, IFO update | DVD-DL transition | `dvd-layerbreak` analysis **plus `layerbreak-pick`** (`LayerBreakPlanner`: nearest legal cell/ECC boundary, both layers within capacity, `--target`, seamless flag) — now matched in construction; the actual transition write is executed via IMAPI2 on hardware. |

## 4. Gap analysis A — what ImgBurn has that DiscForge doesn't (roadmap)

This is the post-parity-round state. Most items are **closed** (✅); what remains is
either hardware-bound or deliberately out of scope.

Closed since the first comparison:

- ✅ **DVD-DL layer-break picker** — `layerbreak-pick` / `LayerBreakPlanner`.
- ✅ **Overburn / underburn** — `capacity-check` / `BurnCapacity`.
- ✅ **Low-level write knobs (construction)** — `WriteParametersPage` + `ModeSelect10` /
  `SendOpc` / `ReserveTrack` / `CloseTrackSession` CDBs (execution over SPTI still needs
  a drive, the same status the burn engine already carries).
- ✅ **Wider UDF write** — 1.02 / 1.50 / 2.00 / 2.01, udfinfo-validated and
  reader-round-tripped.

Genuinely still trailing, and why:

1. **A hardware-proven burn engine.** Not a feature but *validation* — and now largely done.
   The **IMAPI2 data-burn path is hardware-proven** (2026-08-10: full burn → read-back →
   byte-exact `dvd-verify-readback` PASS on a Plextor PX-W5224A). The **RAW DAO-96 path is also
   hardware-proven** the same day: rebuilt over **direct SPTI** with MMC **Write Type = Raw**
   (IMAPI2 rejects hand-built raw images), it wrote a full raw disc — lead-in + program, 2448-byte
   sectors with interleaved P-W sub-channel, lead-in sized from the drive's ATIP NWA — and
   `read-raw` + `raw-verify-readback` confirmed it back **byte-identical on both main and
   sub-channel**. Both of the biggest ◐s are now ✓. What remains is breadth (more drives, media,
   and disc shapes) and an optional written lead-out — coverage, not new architecture.
2. **Bitsetting / book-type change.** The command construction is vendor-specific magic
   (ImgBurn ships separate LG/Lite-On/NEC/Samsung implementations); it can't be built or
   verified without captured command traces from the actual drives. Hardware-bound.
3. **Full DVD-Video / BD-Video layout assembly.** DiscForge now assembles a DVD-Video
   `VIDEO_TS` folder into a conformant ISO+UDF-bridge image with the correct on-disc order
   (`dvd-video-build`), verifies each IFO's internal sector pointers against the real layout
   ("Fix VTS Sectors" *verify*), *rewrites* those IFO pointers + refreshes each `.BUP` to match
   actual file sizes (`dvd-video-fix`, the write half of "Fix VTS Sectors"), and assembles a
   **BD-Video `BDMV/`** folder into a UDF 2.50 image (`bdmv-build`, udfinfo-validated). Remaining:
   IFO/BUP ECC-block padding (coupled to the pointer values; not guessed without a mastered-disc
   fixture — hardware-bound).
4. **UDF 2.50 / 2.60 write** — needs the Blu-ray metadata partition (structural work).
5. **Manual read-retry execution** (0–20) — lives in the Windows-only `DiscForge.Devices`
   layer (which already has a `RetriesPerSector`); a small Windows extension, unprovable
   in a Linux CI.
6. **Quality/discovery burn test**, HD DVD media, sound/shutdown conveniences —
   hardware-bound or negligible-value.

Deliberately out of scope (agreed): reimplementing MP3/AAC/OGG/APE decoders in an
offline zero-dependency build (WAV/FLAC input already works), the near-undocumented
GI/DI/PDI proprietary containers, and HD DVD (dead format).

## 5. Gap analysis B — what DiscForge has that ImgBurn doesn't

The list is long because the tools aim at different targets. Highlights:

- **Identification of ~everything** — one `identify` names hundreds of formats; console
  disc/cartridge recognition; CD-i Green Book; Xbox 360 GOD.
- **Any-to-any conversion** through a canonical model (`disc-convert`), plus CHD, CSO/ZSO,
  WBFS, XISO, ECM.
- **Redump-grade preservation** — read-offset arithmetic, sub-channel/LibCrypt capture,
  C2 re-reads, AccurateRip, `submission-info`, DAT verify, PAR2.
- **Filesystem conformance linting** (ISO/UDF/FAT/HFS) validated against independent oracles.
- **The whole retro universe** — memory cards & saves (read+write), patches/cheats decode,
  game audio, PS1 STR/MDEC video decode, collection management and front-end export.
- **Cross-platform CLI** — the only one of the two that runs on macOS and Linux, and the
  only one scriptable end-to-end.
- **Clean-room engineering discipline** — every format validated against an oracle, and
  gaps (RVZ decode, GOD extraction) documented honestly rather than shipped half-working.

ImgBurn does exactly none of section 5. DiscForge does most of ImgBurn's section-2
burning table, with the validation and low-level-control caveats noted.

## 6. Positioning — which to reach for

**Pick ImgBurn when:**
- You are on Windows and simply need to **burn or verify a disc right now** on real
  hardware, with maximum confidence it will work.
- You need **bitsetting / book-type change**, or any low-level write control **executed
  and proven on the drive today** — DiscForge now *builds* the layer-break, overburn and
  write-parameter commands, but ImgBurn has burned them on real hardware for years.

**Pick DiscForge when:**
- You want to **image, identify, convert, verify, preserve or catalogue** discs and
  dumps — especially retro-console material — where ImgBurn simply has no features.
- You are assembling a **DVD-Video / BD-Video** disc from compliant folders and want the
  IFO sector-pointer check *and* rewrite (`dvd-video-fix`) plus a UDF 2.50 BD-Video image.
- You need **Redump-grade** ripping (sub-channel, offsets, C2, submission info).
- You are on **macOS or Linux**, or want a **scriptable CLI** pipeline.
- You want **any-to-any image conversion**, CHD/CSO/WBFS/XISO/ECM, or filesystem linting.

**Use both** — a natural workflow: image and verify a disc with DiscForge (superior
ripping, hashing and submission info), then burn the resulting image with ImgBurn while
DiscForge's own burn engine matures on real hardware.

## 7. Honest caveats (both directions)

- **DiscForge's burning is not yet hardware-validated at ImgBurn's level.** The planner
  and job logic are thoroughly unit-tested and the IMAPI2 path is real, but live burns
  on physical writers are still pending, and the **RAW DAO-96 engine is stubbed**. Until
  that validation lands, treat ImgBurn as the more dependable *burner* of the two.
- **ImgBurn is unmaintained since 2013** and closed source, may misdetect newer drive
  firmware, and its imgburn.com installer historically bundled OpenCandy/Ask adware
  (declinable; use the official site or `/NOCANDY`). It cannot do anything in section 5.
- **Neither tool circumvents copy protection.** ImgBurn (post-DVD-Decrypter) and
  DiscForge both detect but never decrypt; DiscForge additionally *preserves* protection
  fingerprints (LibCrypt/sub-channel) faithfully.

## 8. One-screen summary

| Axis | Winner | Why |
|---|---|---|
| Burning a disc on Windows today | **ImgBurn** | Hardware-proven, low-level control, bitsetting, layer break |
| Everything else about discs | **DiscForge** | Imaging, identify, convert, verify, preserve, catalogue, cross-platform |
| Retro / console preservation | **DiscForge** | ImgBurn has none of it |
| Cross-platform / scripting | **DiscForge** | ImgBurn is Windows-only, GUI-first |
| Low-level write control | **ImgBurn** (proven) / **tie in construction** | DiscForge now *builds* layer-break, overburn, OPC/write-parameter CDBs; ImgBurn additionally has **bitsetting** and years of on-drive proof |
| Long-term maintenance | **DiscForge** | ImgBurn frozen since 2013 |
