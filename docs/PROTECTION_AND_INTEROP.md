# Protection detection & CloneCD interop (imaging deepening)

Two clean-room additions that put DiscForge at parity with — and in places
ahead of — CloneCD on the faithful-imaging front.

## Protection detection (`dforge scan-protection <image>`)

`ProtectionScanner` recognises the common optical copy-protection schemes by
their **public, documented fingerprints**, so a faithful backup knows what it
must preserve. This is detection, never circumvention — DiscForge identifies a
scheme the way CloneCD's read profiles do, then advises preserving it verbatim.

Detected:
- **LibCrypt** (PlayStation) — a small cluster of intentionally-corrupt
  subchannel Q frames. Guidance: copy with subchannel-faithful (verbatim) mode.
- **SafeDisc / SecuROM / Laserlock** — marker files in the ISO listing
  (`00000001.TMP`, `SINTF32.DLL`, `LASERLOK.*`, …) cross-checked against
  structural signs.
- **SafeDisc-like weak sectors** — a *cluster* of Mode-1 sectors whose EDC is
  intentionally invalid (distinct from scattered read errors). Guidance: image
  in RAW mode and preserve them as read; do not regenerate EDC/ECC.

The scan reads a bounded sample (not the whole disc) and reports evidence,
guidance, and the significant LBAs. A clean scan means no recognised signature
was found — not a guarantee of no protection.

**DiscForge never bypasses, strips, or defeats protection.** It detects so a
backup of a disc you own reproduces the original faithfully instead of silently
"repairing" the features that make it authentic.

## CloneCD interop (`dforge to-ccd <image.cue>`)

`CloneCdWriter` emits the CloneCD control file (`.ccd`) that pairs with the raw
`.img` (2352-byte main channel) and optional `.sub` (96-byte subchannel)
DiscForge already generates. The `.ccd` follows the published INI structure:
`[CloneCD]` version, `[Disc]` entry/session counts, `[Session N]` mode lines,
and one `[Entry N]` per TOC point (A0 first-track, A1 last-track, A2 lead-out,
plus each track) with ADR/Control and P-MSF/PLBA fields.

Workflow:
```
dforge build-raw album.cue album.img          # raw main channel (+ --verbatim for .sub)
dforge to-ccd    album.cue --out album        # the .ccd descriptor
```
The resulting `album.ccd` + `album.img` (+ `album.sub`) load in CloneCD-aware
tools. MSF/PLBA math is lead-in-corrected (LBA + 150).

## Status

Both are pure `DiscForge.Core` additions with CLI surface; the GUI is
unchanged. 12 new harness tests cover the .ccd structure and the scanner's
marker/clean-disc paths. Engine test count: 129 passing.
