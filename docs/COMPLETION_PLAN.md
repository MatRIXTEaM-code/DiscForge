# DiscForge — completion plan (2026-08-12)

A prioritized map of what's left to finish, split by what *gates* each item: pure code
(provable in the cloud with synthetic tests), a reference file (unblocked by dropping one
sample into `DFORGE_FIXTURES/`), the physical drive, or an external dependency. Written from a
full sweep of `src/` and `docs/`. The guiding rule is unchanged: **provably correct or declined** —
nothing ships that can silently corrupt output without a way to validate it.

## Progress & reconciliation (2026-08-12)

Shipped this session (all with tests, both target frameworks green): read-raw mixed-mode
(`--track`/`--field`), the self-validating GameCube junk regenerator (`gc-junk-fill`), the
lossless-conversion certificate (`verify-convert --report`), the emulation-readiness report
(`emu-ready`), DVD-Video navigation tables (VTS_PGCIT in `IfoWriter`), C2 consensus merge chained
into the sector's own RSPC ECC, the adaptive re-read controller (Tier A), and the minimal disc
descriptor (`min-descriptor`).

Found already-built (confirmed against code; coverage strengthened and stale docs corrected rather
than re-implemented): XISO multi-sector directory tables, and UDF extended attributes + named
streams.

Reconciled against actual code — genuinely still OPEN (adjacent components exist but do NOT cover
these): **filesystem-constrained erasure recovery** (DumpReconstruct is copy/ECC-driven, not
filesystem-aware), **UDF 2.60 write** (UdfBuilder tops out at 2.50 — no 0x0260 revision), and a
**per-sector physical-completeness coverage proof** (DumpCompleteness reconciles counts;
SectorMatterMap classifies matter; neither asserts "every sector accounted for, no silent gaps").

## Status at a glance

The RAW-DAO burn ladder (docs/RAW_DAO.md) is now proven on the Plextor PX-W5224A for rungs 1–6
and rung 7's data track; rung 7's audio re-read is the only hardware step outstanding. The
read-back comparator gained three correctness fixes this cycle (scramble-domain normalization,
`--partial` sub-range verify, and a guard so an empty/failed read-back can't grade PASS).

## Tier 1 — cloud-solvable now (pure algorithm, synthetic-test-provable)

These need no hardware and no external sample. Best candidates to burn down while the drive is
away.

- **read-raw mixed-mode single pass** — auto-switch field mode (Raw↔UserData) at the data→audio
  boundary and skip the unreadable pregap, so a mixed disc reads in one command. (In progress this
  cycle; field-decision logic refactored to be unit-testable.)
- **Nintendo LFG junk generator (gated)** — clean-room the lagged-Fibonacci padding PRNG from the
  public description so RVZ junk runs can be regenerated instead of zero-filled. Ship it behind an
  explicit opt-in and keep zero-fill the default until a real RVZ+ISO (or NKit recovery-block
  CRC32) proves it bit-exact. Algorithm is codeable now; only *final* validation is fixture-bound.
- **XISO multi-sector directory tables** — a directory whose entry table spans more than one
  sector (`XdvdfsReader`). Pure structural work.
- **UDF 2.60 (BD-R pseudo-overwrite) + BD authoring write path**, and **UDF extended-attribute /
  named-stream *writing*** (`UdfBuilder`). Spec work; read-back proves it.
- **IFO navigation-table composition** (`IfoWriter`) — compose the VMG/VTS nav tables a hardware
  player walks, the piece beyond the structural IFOs already written.
- **FRONTIER formal-methods batch** — provably-lossless conversion certificates (SMT/exhaustive
  `dec(enc(x))≡x`), sub-CIRC multi-copy symbol voting (first cut needs only C2 pointers),
  filesystem-constrained erasure solving, emulation-readiness analyzer, information-theoretic
  Minimal Disc Descriptor, physical-completeness proof extensions.
- **Adaptive re-read — Tier A logic harness** — the deterministic error-model half (Tier B needs
  the drive).
- **GUI placeholder fallback** cleanup (`CdrwinLauncher`).

## Tier 2 — unblocked by ONE reference file (drop into `DFORGE_FIXTURES/`)

Code is written or writeable; validation is inert until a sample exists. Each is a standing test
waiting for its oracle.

- **GameCube junk PRNG oracle** — the highest-leverage unblock: per VALIDATION-PLAN §1 this is
  *not* drive-gated. Any NKit-scrubbed GC image (its recovery-block CRC32 is the oracle) or one
  Redump-verified unscrubbed GC ISO validates the LFG regenerator end-to-end.
- **RVZ LFG-junk / Wii reconstruction** — Wii adds the partition hash-tree + AES layer; needs a
  real `.rvz`+ISO oracle cross-checked against two encoders.
- **GOD→ISO extract** (`GodContainer`) — public refs disagree on the block→offset formula by one
  block; one reference GOD package + its known ISO pins it.
- **MDEC STR v3 + pixel-exact interop** (`MdecFrameDecoder`) — needs a real `.str` clip.
- **ECM cross-tool interop** — core is done and byte-exact; only the CI interop regression waits
  on an external `.ecm`+`.bin`.
- **Confidence-caveat parsers** — MDS (Alcohol), TOD, CD-i, HDCD Type-B: implemented from public
  docs, awaiting a genuine file to confirm.
- **HFS+ reader** and **HFS extents-overflow** (fragmented fork); **UDF EA / named-stream
  reading** — each needs a volume that actually carries the feature.

## Tier 3 — gated on the physical drive (Plextor PX-W5224A)

- Rung 7 audio re-read (this session — read `--field audio` from the track's INDEX 01 LBA).
- `extract-sectors` CDRWIN-style range extractor; drive-capabilities profile (read offset, C2,
  cache-defeat, overread); adaptive re-read Tier B; burn **Verify**/**Test** wiring in the GUI
  (`BurnView`); bitsetting replay over SPTI; the full dump→score→merge→convert→burn→compare
  round-trip.

## Tier 4 — external dependency + live service

- **RFC-3161 trusted timestamps** — Core file drafted against `System.Security.Cryptography.Pkcs`,
  but that assembly needs a NuGet reference the cloud sandbox can't restore, plus a live TSA to
  validate against.

## Recommended near-term sequence

1. Finish read-raw mixed-mode (Tier 1) — closes the last usability gap in the burn/verify loop.
2. Land the LFG generator gated behind opt-in (Tier 1 code; Tier 2 validation) — turns "junk
   zero-filled" into "junk regenerable, pending one fixture."
3. Pick one Tier-1 formal-methods item (lossless-conversion certificates is the most on-brand).
4. Whenever a single sample can be sourced, the Tier-2 oracles unblock several features each —
   the GameCube NKit image is the highest-leverage one to find first.
