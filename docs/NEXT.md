# DiscForge — what's left (session handoff)

State as of v1.66.0 (tagged & released). 2,448 tests green. A fresh Claude session
can work from this file alone; the code comments carry the details.

## The immediate arc — finish the mixed-mode story

1. **Track-aware `--disc`** *(top priority — the fix this file exists for).*
   `extract-sectors <drive:> out.bin --disc` currently treats the disc as ONE span
   (`ExtractSectorsDrive` in `src/DiscForge.Cli/Program.cs`). It must instead walk
   `reader.Toc.Tracks`: one span per track with per-track audio hint, per-track
   `RequireDataSync`, correct datatype, audio pregaps captured (e.g. the 150
   sectors between a data track and track 2), all into one bin, with the emitted
   cue carrying real per-track TRACK/INDEX entries. Track-boundary transition
   sectors go into `BadSectorMap.BoundaryLba` (the field exists), not UnreadableLba.
   Why: a hand-written single-track cue for an 8-track PS1 disc caused a three-disc
   burn-failure odyssey (see test docstrings "the 135,417-lie" and
   RawImageGeneratorDummyTests).

2. **Canonical re-dump.** With (1) built, one command re-dumps the PS1 game
   properly. Interim per-track dumps already exist on Andy's PC:
   `data.bin` (track 1, COMPLETE) + `game2.t02..t08.bin` (audio, all COMPLETE).
   `game.bad.bin` is the old half-void dump — evidence, do not use.

3. **Redemption burn + round trip.** Burn the correct mixed-mode cue via
   `burn-raw --engine spti` on the TSSTcorp SH-224DB (drive letter D:) at 4–8x
   onto the last Taiyo Yuden CD-R, read back, `compare`/hash. Expect the first
   fully closed dump→burn→dump round trip. Note: the Samsung's C2 pointers are
   unreliable (flags the first sector of most read spans) — use `--no-c2` when
   reading on it; the sync gate + EDC checks carry integrity.

4. **`inspect-raw` honesty fix.** It silently skips sync-less sectors and printed
   "Result: clean" on a file that was 47% zeros. It must count and report
   unrecognized/sync-less sectors in a nominally-data image.

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
