# DiscForge v1.70.0 — the GameCube preservation backlog

Every item in ROADMAP.md's "GameCube preservation backlog" (7 items) worked through in one pass:
five closed, one shipped with an honest partial scope, one deferred outright for lack of a usable
public spec.

## Highlights

**Full GameCube boot-chain confirmation.** New bi2.bin parser (`GcBi2.cs`) plus `GcBoot.CheckChain`,
which walks the whole chain — bi2 → apploader → DOL → FST — and collects every problem it finds
rather than stopping at the first, so `gc-verify` can report all of them at once. `gc-verify` also
now cross-checks the disc's padding against the junk-generator classifier (flagging scrubbed/mixed/
suspicious padding) and flags a nonzero bi2 debug-monitor size as a probable dev-kit build.

**CRC-32/Redump confirmation on junk reconstruction.** `gc-junk-fill` always reports the finished
image's own CRC-32, and a new `--expect-crc32 <hex>` flag checks it against a caller-supplied
known-good value (e.g. from a Redump entry) — independent of, and layered on top of, the existing
self-validation gate that proves the junk generator against the disc's own surviving padding.

**GameCube-specific ring codes.** New `gc-ringcode` command decodes the three inner-ring codes a
GameCube disc carries beyond the generic IFPI mastering/mould codes: the red manufacturing-date code,
the blue disc-identity code (which embeds the disc's own game code, disc number and ROM revision),
and the green anomaly flag. Cross-checks against values you already have — the disc header's game
code, or a specific Redump entry's disc number/revision — since this project bundles no Redump
database of its own.

**Save banner/icon decode.** New `gci-banner` command decodes a GameCube save's own banner and first
icon frame straight to PNG, reusing the RGB5A3/CI8 tiling this project already proved for the
opening.bnr banner and TPL textures. Scoped deliberately to one frame: this session found the exact
palette-placement rules for multi-frame animated icons genuinely underdocumented outside a source
this project won't use (see below), so only the provably-correct single-frame case ships.

**Revision/variant-aware DAT matching.** New `dat-tags` command (and a `tags:` line now printed by
`dat-verify`) parses the public No-Intro/Redump catalogued-name convention — region, revision, disc
number, and demo/kiosk/beta/prototype/unlicensed/sample flags — out of a DAT match's free-text name.
It composes directly with the new ring-code check above: parse a matched entry's revision here, feed
it into the ring-code cross-check, and a GameCube dump's physical ring code and its catalogued DAT
entry can confirm each other.

## Explicitly deferred

**GameCube DTK/ADP disc-streamed audio.** Multiple public sources (YAGCD, gc-forever's wiki, a
GitHub decoder's README) were checked and none gave a byte-exact spec for the disc-interleaved
streaming audio format. The only concrete source found was a Nintendo document marked CONFIDENTIAL —
declined as a clean-room basis, same standard applied everywhere else in this project. The `.dsp`
FILE-format decoder (a separate, already-shipped piece) is unaffected and continues to work.

**GC memory-card directory/BAT corruption checksums, `.gcs`/`.sav` containers.** The BAT checksum
footer is well-corroborated, but the directory-block checksum footer offset has a genuine,
unresolved contradiction between a fetched source and simple structural arithmetic, and no
checksum *algorithm* was confirmed with enough confidence to ship. Left undone rather than guessed
at, same as `.gcs`/`.sav` (no documented byte-level spec found).

## Fixed

- No regressions found in existing suites; all new work is additive.

2,633 tests green (`bash build.sh test`); both the sandbox-buildable `net8.0` CLI target and the
Windows `net8.0-windows` target build clean (0 warnings/errors). `DiscForge.App` itself still can't
be compiled in the sandbox — no Windows Desktop SDK available there, unchanged from every earlier
session.
