# DiscForge — what's left (session handoff)

State: v1.66.0 released, plus the post-1.66 batch below awaiting commit.
2,467 tests green. A fresh Claude session can work from this file alone; the
code comments carry the details.

## Landed since v1.66.0 (built + tested, needs commit/push/release)

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
  closed). State: 2,477 tests green.

## The immediate arc

1. **Canonical re-dump.** With track-aware `--disc` built, one command re-dumps
   the PS1 game properly (auto-audited, MRI-able). Interim per-track dumps exist
   on Andy's PC: `data.bin` (track 1, COMPLETE) + `game2.t02..t08.bin` (audio,
   all COMPLETE). `game.bad.bin` is the old half-void dump — evidence, do not use.

2. **Redemption burn + round trip.** Burn the correct mixed-mode cue via
   `burn-raw --engine spti` on the TSSTcorp SH-224DB (drive letter D:) at 4–8x
   onto the last Taiyo Yuden CD-R, read back, `compare`/hash. Expect the first
   fully closed dump→burn→dump round trip. Note: the Samsung's C2 pointers are
   unreliable (flags the first sector of most read spans) — use `--no-c2` when
   reading on it; the sync gate + EDC checks carry integrity.

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

## Longer-term backlog (docs/ROADMAP.md)

Drive-capabilities DB growth, offset-shift disc detection, prototype scanner,
GUI views for recover/secure-rip, remaining non-atomic writers, PS1 backlog (CU2 etc.).
