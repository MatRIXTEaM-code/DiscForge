# DiscForge v1.67.0 — every dump now proves itself

v1.66.0 taught DiscForge to distrust drives; v1.67.0 makes the distrust a
habit. Every extraction now ends by independently re-reading what it wrote,
whole-disc dumps understand mixed-mode layouts natively, and a dump can leave
the drive with a signed certificate that proves any single sector of it years
later. 2,496 tests green.

## Highlights

**Automatic post-dump audit.** Every raw drive extraction ends with an
independent audit of the written file — a sync census and sampled EDC sweep
over data spans, a zero census everywhere — printed as `AUDIT [PASS/FAIL]`.
The engine's own opinion of the dump is no longer the last word; the bytes on
disk are. (`--no-audit` opts out.) The same honesty reached `inspect-raw`: it
now counts and reports sync-less sectors instead of skipping them, so a
half-empty image can never again print "clean".

**Track-aware whole-disc extraction.** `extract-sectors <drive:> out.bin
--disc` walks the TOC: one span per track with per-track sync gating, the
audio pregap at a data→audio transition captured as boundary geometry (never
misgraded as damage), everything written into one atomic image with a true
per-track cue — INDEX 00 and 01 included. Mixed-mode discs are first-class.

**Disc MRI (`disc-mri`).** Per-sector evidence rendered on the physical disc
using real Red Book spiral geometry. A scratch appears as a radial streak, a
pressing defect as a ring, a muted read region as a solid annulus — damage
becomes something you see. Worst evidence wins per pixel, so nothing hides.

**Dump Certificate (`dump-cert`, and `extract-sectors --cert`).** A signed
(ECDSA P-256), machine-readable record of the dump event: image hashes, drive
and firmware, settings, per-span grades, audit verdict — and a Merkle tree
over the sectors. `prove` extracts a ~15-hash audit path for any sector;
`check` verifies a bare 2352-byte slice against the signed root without the
rest of the image. Dumps can be born certified.

**Pressing DNA (`pressing-dna`).** The offset-sensitive fingerprint that
tells two pressings of one title apart: exact geometry, pregap lengths, where
the audio physically sits in each track, MCN/ISRC — including recognition of
the constant-shift write-offset signature by name. `disc-genome` answers
"same content?"; this answers "same pressing?".

**Drive Dossier (`drive-dossier`).** Local, per-drive institutional memory:
observed behaviour accumulates across sessions into distilled facts and
warnings — mute signatures (auto-recorded by extract-sectors when the sync
gate fires), untrustworthy C2 patterns, bench-confirmed offsets, overread
reach. The knowledge base is the seed; the dossier is what your drive
actually did.

**Disc Actuary (`disc-actuary`).** Longitudinal media health: every quality
scan appends to a per-disc time series, a first-order decay model fits each
disc's error growth, and the collection ranks by estimated remaining readable
life — "re-dump these first, they're dying fastest." Accepts imported scan
files or manual readings. (Also fixes a latent overflow in the rot-kinetics
projection on near-stable discs.)

## Fixes

- Pack Discs view: option checkboxes no longer overlap the Remove button.
- `RotKinetics`: projections beyond 500 years report "no crossing" instead of
  overflowing.
