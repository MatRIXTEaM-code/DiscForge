# DiscForge — what's left (session handoff)

State: v1.67.0 — everything under "Landed since v1.66.0" below is committed
(2c92b4f + 3e9f2f1 + 37078ba) and the version is unified at 1.67.0 across all
four projects. See "Landed since v1.67.0 (uncommitted)" for what's changed
since — it's on Andy's machine, tested, NOT yet committed/pushed. A fresh
Claude session can work from this file alone; the code comments carry the
details.

## Landed since v1.67.0 (uncommitted — on Andy's machine only)

- **DVD/BD `extract-sectors --disc` fix — a real, previously-unknown gap.**
  `DriveExtractionReader` unconditionally issued MMC READ CD (0xBE), a CD-only
  command, for every media type. DVD/BD sectors have no CD sync pattern, so
  `RequireDataSync` (built for CD data tracks) aborted at LBA 0 on EVERY DVD
  extraction, on any drive, at any point in this project's history —
  `extract-sectors`'s DVD support had literally never worked. Root-caused and
  fixed via live testing with a real PS2 disc (TSSTcorp SH-224DB).
  Fix: `DriveExtractionReader` now runs GET CONFIGURATION once at construction
  (`IsDvdOrBd`, via the existing `ConfigurationInfo`/`MmcProfile` parser) and
  switches to plain READ(10) 2048-byte user-data reads for DVD/BD, batched the
  same way the CD path is. `SectorExtraction` grew `ExtractDataType.DvdUserData2048`
  (2048 bytes, no sync/EDC to check — the drive's own Reed–Solomon ECC is the
  proof). `extract-sectors` auto-detects DVD/BD media and overrides
  `--as`/`--no-c2`/`--sub` with a printed note, since none of those concepts
  exist on that media; `--as dvd` also works explicitly.
  Files: `src/DiscForge.Core/Dumping/SectorExtraction.cs`,
  `src/DiscForge.Devices/Reading/DriveExtractionReader.cs`,
  `src/DiscForge.Cli/Program.cs`, `tests/DiscForge.Core.Tests/SectorExtractionTests.cs`
  (4 new tests). 2,500 tests green (net8.0 AND net8.0-windows both verified —
  see `build.sh cli-win` for the sandbox's multi-TFM build method).
  **Confirmed on real hardware**: a PAL Resident Evil 4 PS2 disc extracted
  clean, 2,228,528/2,228,528 sectors, COMPLETE, no aborts
  (`ps2game.iso`, MD5 `30255F8E8958A963212CA6455BB29EE0` — pending a redump.org
  cross-check to confirm bit-perfect, not just non-aborting).
  **Still needed**: `git add`/commit/push (Claude can't push from the sandbox —
  do this from Andy's machine), then update the "State" line above once it's in.

## Landed since v1.66.0

- **Track-aware `--disc`** — DONE. `ExtractSectorsDrive` walks the TOC: one span
  per track, per-track audio hint + `RequireDataSync`, the 150-sector audio pregap
  at a data→audio transition captured as its own boundary span
  (`ClassifyFailuresAsBoundary` → `BadSectorMap.BoundaryLba`, grade unaffected),
  all spans into ONE atomic bin + merged sidecar, cue with real per-track
  TRACK/INDEX 00/01 entries.
- **Auto-audit** — DONE. Every raw drive extraction now ends with
  `ExtractionAudit` (Core/Dumping): an independent re-read of the written file —
  sync census + sampled EDC on data spans, zero census everywhere; AUDIT
  PASS/FAIL printed, failure sets exit 2. `--no-audit` opts out.
- **`inspect-raw` honesty fix** — DONE. Sync-less sectors counted per data track
  (subcoded) and disc-wide (main-only), reported in notes, and the verdict
  states its coverage instead of overclaiming "clean".
- **Disc MRI** — DONE. `dforge disc-mri <bin|cue> [out.svg|png]`
  (Core/Forensics/DiscMri): per-sector evidence on the physical disc via real
  Red Book spiral geometry; radial streak = scratch, ring = pressing defect.
  Worst evidence wins per pixel. Sidecar auto-overlaid.
- **Dump Certificate** — DONE. `dforge dump-cert <image> [--gen-key|--key] |
  verify | prove | check` (Core/Preservation/SectorMerkle + DumpCertificate):
  signed (ECDSA P-256, merge-cert key format) provenance sidecar with a Merkle
  root over the sectors — `prove` emits a ~15-hash path for one sector, `check`
  verifies a bare 2352-byte slice against the signed root WITHOUT the image.
  Sidecar counts auto-included. AND extract-sectors grew `--cert [--cert-key f]`:
  a dump can now be born certified — drive, firmware, settings, per-span grades,
  audit verdict and Merkle root captured at the moment of extraction (gap 3
  closed).
- **Pressing DNA** — DONE. `dforge pressing-dna <a.cue> [b.cue]`
  (Core/Forensics/PressingDna): disc-genome's complement — the offset-SENSITIVE
  fingerprint (exact geometry, pregaps, audio edges, MCN/ISRC) that tells
  PRESSINGS of one title apart; names the constant-shift write-offset signature
  when it sees one. Verdicts: same pressing / same title different pressing /
  different discs.
- **Drive Dossier** — DONE (gap 4). `dforge drive-dossier <drive:|vendor model>`
  (Core/Devices/DriveDossier): local per-drive memory seeded by the knowledge
  base — observations accumulate across sessions into distilled facts and
  warnings (mute signatures, first-sector C2 wolf-cries, confirmed offset,
  overread reach). extract-sectors auto-records the sync-gate mute signature.
- **Disc Actuary** — DONE (feature E). `dforge disc-actuary <id> --record …` /
  `--collection` (Core/Forensics/DiscActuary): every quality scan appends to a
  per-disc time series; rot-kinetics' decay model fits each disc; the shelf
  ranks by remaining readable life — "re-dump these first, they're dying
  fastest". Accepts scan-import formats or manual --tier1. (Also fixed a latent
  RotKinetics DateTimeOffset overflow on near-zero slopes — projections beyond
  500 years now honestly report "no crossing".)

## The immediate arc

1. **Canonical re-dump — DONE.** Track-aware `--disc` proven on real hardware
   for the first time: `ps1-redump.bin` + `ps1-redump.cue` on Andy's PC, all 8
   tracks COMPLETE, AUDIT PASS (data track sync 153,904/153,904, EDC clean;
   audio pregaps read as genuine silence, not damage). This supersedes the old
   scattered interim dumps (`data.bin`, `game2.t02..t08.bin`) and the old
   half-void `game.bad.bin` — keep those only as prior evidence, don't use them.

2. **Redemption burn + round trip — DONE, via ImgBurn (DiscForge's own SPTI raw
   engine still doesn't work — see below).** `ps1-redump.cue`/`.bin` burned to
   the last CD-R (a CMC Magnetics disc, not the Taiyo Yuden NEXT.md previously
   assumed) on the TSSTcorp SH-224DB via ImgBurn 2.5.8.0, SAO write type, then
   verified by read-back: **289,321/289,322 sectors bit-perfect**. The one
   miscompare is at LBA 153903 — the LAST sector of the data track, right at
   the data→audio boundary. ImgBurn's own log: "The drive probably corrected
   the L-EC Area because it's wrong in the image file" — a well-known
   boundary-sector ECC quirk in CD preservation, not a systemic dump or burn
   problem. This is the first fully closed dump→burn→dump round trip this
   project has ever achieved (99.9997% bit-perfect). Note: the TSSTcorp's C2
   pointers are unreliable (flags the first sector of most read spans) —
   `--no-c2` was used reading on it; the sync gate + EDC checks carry integrity.

   **DiscForge's own `burn-raw --engine spti` still does not work** — it was
   the original goal here, but `--test-cue` was rejected by the real drive
   (ASC 0x26/0x00, "invalid field in parameter list") through FOUR rounds of
   fixes this session, all real and now committed: (1) the cue-sheet Data Form
   byte was wrong (0x10, not a defined MMC code); corrected against cdrdao's
   GenericMMC::createCueSheet to 0x00/0x10/0x20 by track type — but a PDF-spec
   extraction along the way suggested 0x08, which was ALSO wrong and rejected,
   a reminder that the WebFetch summarizer is unreliable for exact byte tables
   the same way it hallucinated a redump hash match earlier this session; (2)
   the lead-in structure was sending three Red-Book-style POINT entries
   (A0/A1/A2) that cdrdao doesn't send at all — replaced with cdrdao's single
   generic lead-in entry, entry count 14→12; (3) the MODE SELECT Data Block
   Type was hardcoded to 3 (raw+P-W subchannel) even for the Session-At-Once
   cue-sheet-test path — cdrdao uses 0 there, only using 3 for the actual Raw
   write type; fixed. `--test-cue` STILL rejects after all three. The
   diagnostic that broke the logjam: ImgBurn burning the SAME `ps1-redump.cue`
   on the SAME drive succeeds completely using **SAO** as the write type for
   the WHOLE burn (cue sheet included) — not the separate no-cue-sheet Raw
   mode DiscForge's `Burn()` falls back to. That strongly suggests DiscForge's
   Raw-mode fallback (added because "the WRITE(10) parks" in SAO — see the
   comment history in `SptiRawDaoBurnEngine.cs`) was working around the SAME
   Data Block Type bug just fixed in the test path, not a genuine SAO
   limitation — meaning `Burn()` itself may need the equivalent fix (SAO +
   cue sheet + plain-size WRITE(10), matching cdrdao's actual write loop,
   instead of the current lead-in-composing Raw-mode path) rather than the
   `--test-cue` cue sheet needing yet another guess. Next session: try that
   before guessing at cue-sheet bytes again.

## Hardware track (Plextor PX-W5224TA)

- The drive is a fine READER; its 22-year-old write side is retired from long burns.
- **0xD8 lead-in engine**: direct D8 window confirmed on this firmware = LBA −75..−1
  (pregap zone). Deep lead-in (TOC territory) needs redumper's seek-and-read-cache
  technique — research + implement. Building blocks shipped: `plextor-d8` command,
  `MmcCommands.PlextorReadCdDa`.
- **Offset confirmation**: knowledge base says +30 (reference). Needs a mainstream
  audio CD present in AccurateRip: rip with `--disc` (cue auto-emitted), then
  `accuraterip <cue> --url` → download dBAR → `detect-offset <cue> --db <file>`.
  (Disc-ID math is pinned to a published vector; a 404 means the pressing is absent.)

## Housekeeping (user-side, minutes)

- Verify the v1.66.0 Release workflow ran green; paste release notes into the
  GitHub release description.
- Uninstall the old "DiscForge 1.65" from Program Files (shadows `dforge` on PATH).
- COPTR + awesome-list submissions: paste-ready text in `docs/registry-submissions.md`.
- Cross-check AaruFormat interop against a real Aaru-generated `.aaruf`.

## Deliberately parked for a hardware/desktop session

- **Resumable dumps (gap 5)**: progress journal beside the `.part`; needs live
  drive testing to trust the seek/append semantics — don't build it blind.
- **`dforge prove` (feature H)**: the one-verb round trip orchestrates drive
  commands end-to-end; wire it when the redemption burn works.
- **WASM Core (feature C)**: needs NuGet/Blazor tooling the sandbox can't reach;
  build on Andy's machine or CI.

## Longer-term backlog (docs/ROADMAP.md)

Drive-capabilities DB growth, offset-shift disc detection, prototype scanner,
GUI views for recover/secure-rip, remaining non-atomic writers, PS1 backlog (CU2 etc.).
