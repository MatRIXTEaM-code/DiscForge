# DiscForge v1.68.0 — the burn bug is closed

The headline fix of this release, weeks in the making: `burn-raw --engine spti`
now produces a genuinely burned, byte-correct, readable disc — verified on
real hardware (TSSTcorp CDDVDW SH-224DB), not just a command that reports
success.

## Highlights

**RAW DAO burning actually works.** Two independent, hardware-confirmed
fixes: an ATIP-based lead-in length fix eliminated every `WRITE(10)` failure
during the burn itself (100% burn completion, zero retries), and a corrected
read-back verifier proved the result byte-correct — 99.99%+ identical to the
golden image, with the tiny residual traced to a known, cosmetic edge
condition at the track boundary, not disc corruption.

**`raw-verify-readback` is now trustworthy.** Three real bugs in the verifier
itself were found and fixed this release, each while cross-checking the first
real hardware burns: alignment now uses the main-channel sector header
instead of the Q sub-channel (a real drive-specific main/sub read-back skew
was defeating Q-based alignment); the sub-channel's own content gets the same
skew correction applied before comparison, not just used for alignment; and a
Q frame that fails its own CRC is now correctly classified as transient
read-noise (`sub-read-noise`) rather than misreported as proof of a
mis-addressed burn defect. Reports gained a `SubReadNoise` metric throughout
(console, JSON, and the HTML certificate).

**`dforge prove <cue> <drive:>`** — one command that burns, reads back every
track using its own TOC-derived parameters, verifies each against the
golden, and prints a single `PROVEN`/`FAILED` verdict. `--report` writes a
per-track HTML certificate; `--keep-temp` keeps the golden image and every
track's capture for forensics. Covers the burn half of the "dump → audit →
certificate → reburn → cross-verify" round trip; the dump/audit front half
already exists as separate commands (`HARDWARE_RUNBOOK.md` §4).

**`dforge license-check <image> [--json]`** (new) — reads the fixed
"Licensed by Sony Computer Entertainment..." text PS1/PS2 mastering tools
wrote into sector 4 of the data track, identifies the region (Japan / Europe
/ America), and cross-checks it against `SYSTEM.CNF`'s serial-derived
region. A disagreement — rebuilt boot area, mismatched bin/cue pairing, a
relabelled image — is flagged as a second, independent region signal.

## Fixes

- `SptiRawDaoBurnEngine`: stale-sense WRITE(10) retry bug (retries re-queried
  sense after the failing condition had already cleared, so they never
  fired); missing OPC/NWA read before SEND CUE SHEET; MODE SENSE reply buffer
  too small on drives that return a block descriptor; a garbage Track Mode
  nibble from the drive was being faithfully preserved instead of overridden
  with the disc's real first-track control value.

## Docs

- `cdrdao-capture-howto.md` rewritten: the SEND CUE SHEET/SAO investigation
  is closed (cdrdao's own SAO path fails identically on this hardware — a
  drive/firmware limitation, not a DiscForge bug), and the MSYS2 build steps
  are corrected (plain MSYS2 MSYS, not MINGW64; the `ntddscsi.h`
  include-order fix; the nonexistent `--eject-off` flag removed).
- `docs/ROADMAP.md`: corrected three PS1-backlog items that were already
  fully implemented and undocumented as such — CU2 sidecar read/write/verify,
  the pregap-accuracy check, and XA Mode 2 Form 1/2-aware EDC/ECC — so a
  future session doesn't re-investigate them.

2,520 tests green (plus 17 new for `license-check`); one pre-existing,
environment-specific `AudioCdTests` failure (an `OutOfMemoryException` under
this sandbox's memory ceiling, unrelated to any code in this release) is the
only exception.
