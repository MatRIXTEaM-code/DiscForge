# DiscForge v1.66.0 — the hardware era

The release where DiscForge met real drives — a Plextor PX-W5224A and a
TSSTcorp SH-224DB — and every lesson the bench taught was encoded back into
the engine. 2,448 tests green.

## Highlights

**The sync gate (`RequireDataSync`).** A drive can return SUCCESS status while
silently muting audio-read-as-data to zeros. DiscForge no longer believes it:
a raw data-track sector without the 12-byte sync pattern is now a failed read
regardless of what the drive claims. This single check catches the class of
failure where a dump is half empty and every tool in the chain says "clean".

**Native drive extraction (`extract-sectors`).** CDRWIN-style extraction as
one command — disc / tracks / sector-range modes, datatype selection, error
recovery (abort / ignore / replace with mode-aware dummy sectors), per-sector
retries, C2 gating, formatted-Q subcode capture (`.subq`) with CRC analysis
and Q-only re-reads, audio jitter consensus, and batched reads (24
sectors/command) for speed. Every unproven sector lands in a
`.badsectors.json` sidecar.

**Offset detection and AccurateRip.** `detect-offset` sweeps a rip against an
AccurateRip dBAR response with the sliding-window AR-v1 algorithm; disc-ID
math is pinned to a published reference vector. Whole-disc rips auto-emit a
usable cue sheet.

**Drive knowledge base (`drive-db`).** Community-reference data — read
offsets, overread reach, C2 reputation — for known drive families, with
sources on every entry, matched against the live INQUIRY.

**Plextor 0xD8 foundation (`plextor-d8`).** The vendor READ CD-DA command,
byte-exact CDBs cross-confirmed against DiscImageCreator and redumper; the
direct negative-LBA window (−75..−1, track-1 pregap territory) confirmed live
on real firmware.

**Burning.** Write-speed control on the raw SPTI engine with honest
reporting when a drive rejects the requested speed; `compose-verify` proves a
composed image against its sources (double-hash + descramble-compare); live
ATIP reading identifies blank media down to the dye vendor (validated against
genuine Taiyo Yuden).

**GUI.** Prose-wrap sweep across eight views — long descriptive text no
longer truncates.

All project versions are unified at 1.66.0 from this release onward.
