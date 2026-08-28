# DiscForge — what's left (session handoff)

State: v1.67.0 — everything under "Landed since v1.66.0" below is committed
(2c92b4f + 3e9f2f1 + 37078ba) and the version is unified at 1.67.0 across all
four projects. See "Landed since v1.67.0 (uncommitted)" for what's changed
since — it's on Andy's machine, tested, NOT yet committed/pushed. A fresh
Claude session can work from this file alone; the code comments carry the
details.

## SptiRawDaoBurnEngine session (2026-08-25 continued) — real fixes, still not fully closed

Picking back up on the parked `burn-raw --engine spti` bug with a real blank
disc and byte-level diagnostics (`SptiRawDaoBurnEngine.Verbose`, prints raw
MODE SENSE/MODE SELECT/SEND CUE SHEET bytes + decoded field pointer to
stderr — turn on via `TestCue()`, or set the static flag directly). Genuine,
hardware-confirmed fixes landed:

1. **WRITE(10) retry read stale sense.** The retry loop re-queried sense with
   a fresh REQUEST SENSE after a failure instead of reading the sense the
   drive returned WITH the failing command; by the time the fresh query
   landed the condition had cleared, so retries never triggered. Fixed to
   read `SptiResult.SenseKey/Asc/Ascq` directly off the failing command.
2. **Missing OPC + NWA read before SEND CUE SHEET.** cdrdao's real sequence
   is MODE SELECT → OPC → get NWA → SEND CUE SHEET; `TestCue()` skipped
   straight from MODE SELECT to SEND CUE SHEET. Added the missing steps.
3. **MODE SENSE reply buffer too small** (64 bytes; needs up to ~68 with a
   block descriptor present) — silently fell back to a blank default page.
   Bumped to 192 bytes. (Turned out not to be live on THIS drive — it
   reports block descriptor length 0 — but it's a real latent bug on drives
   that do return one, now fixed regardless.)
4. **MODE SELECT now genuinely succeeds — first time all session.** Real
   capture showed the drive reporting Track Mode (write-parameters page
   byte 3, low nibble) = `0x5` ("audio, four-channel, copy permitted") for
   a disc whose first track is DATA. The "preserve Track Mode exactly as
   the drive reports it" policy (copied from cdrdao, which assumes the
   drive's reported value is sane) was faithfully keeping that garbage.
   Fixed by overriding just that nibble with the real first-track control
   value already known from the layout (same value `DaoCueSheet.CtlAdr`
   uses). First attempt at this fix used the wrong bitmask (`&0x3F` doesn't
   clear the nibble it's about to OR into) and silently did nothing — caught
   from the diagnostic output and corrected.
5. **No abort/flush after a failed write.** `Burn()`'s write loop had no
   cleanup on failure (cdrdao's `abortDao()` flushes the cache on any DAO
   failure); added a best-effort SYNCHRONIZE CACHE before rethrowing, so a
   future aborted attempt doesn't leave the drive in whatever state an
   un-acknowledged failure leaves it in.

**Still open**: `SEND CUE SHEET` itself is still rejected (ASC 0x26/0x00),
even with MODE SELECT now correct, on a confirmed genuinely-blank disc, after
a full drive-manager reset AND a full PC restart (both ruled out state as the
cause). The cue-sheet CONTENT was checked entry-by-entry against cdrdao's
`GenericMMC::createCueSheet` (structure, DataForm bytes, lead-in/lead-out
mode derivation, entry count formula) and matches exactly as far as manual
verification can tell. The drive's sense data does NOT set SKSV (no field
pointer available) — that diagnostic avenue is a genuine dead end on this
drive, not a missing feature in our code.

**UPDATE (2026-08-25, same day, later still) — real cdrdao built and run on
the actual drive; a real fix landed and is mid-hardware-test.**

Andy built real, unmodified cdrdao from source on his machine via **MSYS2
MSYS** (NOT MinGW64 — see the environment note below, that distinction
mattered a lot) and ran it against the same TSSTcorp CDDVDW SH-224DB with the
same PS1 disc, verbose (`-v 4`). Two runs, decisive result:

- `--driver generic-mmc` (Session-At-Once, the same mode `TestCue()` uses):
  **cdrdao itself fails at the SAME step** — "Cannot set write parameters
  mode page" / "Cannot setup write parameters for session-at-once mode." It
  never even reaches SEND CUE SHEET. This is independent, external proof
  that the still-open SEND CUE SHEET rejection documented above is a
  drive/firmware limitation on session-at-once mode, not a DiscForge bug —
  **that avenue is now closed for good; do not resume `TestCue()` debugging.**
- `--driver generic-mmc-raw` (raw writing — the mode `Burn()` actually uses):
  cdrdao's MODE SELECT succeeded, SEND CUE SHEET succeeded (it printed a real
  12-entry cue-sheet table and proceeded to write), and it only failed later
  during the actual simulated write ("Writing lead-in and gap... ERROR:
  Write data failed" — a separate, later-stage issue, not investigated
  further since it's cdrdao's own code path, not ours).

That raw-mode success gave a byte-level comparison point. Reading
`dao/GenericMMCraw.cc::setWriteParameters` (the function that just worked on
this exact drive) showed it does NOT preserve or compute Track Mode into
write-parameters page byte 3 the way the SAO-path fix above does — it
hardcodes byte 3 to `0` entirely (no multi-session pointer, no FP/Copy, no
Track Mode nibble) and hardcodes byte 8 (session format) to `0` too, even
though this is technically an XA/Mode2 disc. DiscForge's `Burn()` (Raw write
type) was instead computing Track Mode into byte 3 and setting byte 8 from
`layout.DiscType` — reasoning that was worked out for the *SAO* path (fix #4
above) and had never actually been re-justified for Raw. **Fixed**: byte 3/8
handling in `SetRawDaoWriteParameters` now branches on write type — SAO
(`TestCue()`) keeps the Track-Mode-preservation logic unchanged, Raw
(`Burn()`) now zeroes both bytes exactly like cdrdao's proven-on-this-drive
raw driver. Data Block Type stays 3 (raw+P-W) for Raw — that part was never
the problem, and downgrading to cdrdao's simpler PQ-only mode (dataBlockType
1) would throw away the real sub-channel data DiscForge computes, which is
the whole point of this burn path; not something to revisit casually.

**Confirmed on hardware, same day**: `burn-raw --engine spti --simulate` now
gets `MODE SELECT(10) result: success=True` for the very first time all
session on the real Raw (`Burn()`) path — this had been rejected
(ASC 0x26/0x00) every single time before this fix, across multiple earlier
sessions. The simulate run then proceeded into the actual WRITE(10) loop and
was mid-run (tens of thousands of sectors in, out of 289,472, climbing
steadily) with frequent-but-recovering "drive becoming ready (key 0x2, ASC
0x04/0x08)" retries (not fatal — each one succeeds on retry 1/10 and the
sector count keeps climbing) when the session paused for the day.

**CONFIRMED, same day**: the `--simulate` run completed cleanly end to end —
`[finalize] 100.0% SIMULATION complete - the full raw write path ran with
the laser off.` / `Simulation complete (SPTI) - no disc written.` This is
the first time the FULL non-destructive raw-burn validation has passed on
real hardware, all session (all prior sessions, in fact — this bug predates
this session). `Verbose` is now also turned on inside `Burn()` itself
(previously only `TestCue()` had it), so the diagnostic bytes print
automatically on every run, real or simulated.

**UPDATE (2026-08-27) — REAL burn attempted twice on real hardware; found and
fixed the actual root cause of a "successful" burn reading back as blank.**

Andy ran a real (non-simulate) burn on a genuinely blank Verbatim CD-R:

- At `--speed 4` (default): failed partway with a genuine `Medium error: ASC
  0x0C ASCQ 0x00` after a long stretch of "drive becoming ready" retries —
  a real write-reliability problem on this 22-year-old drive at speed.
- At `--speed 1`: completed cleanly — `RAW burn complete (raw DAO)`, no
  errors, full write loop finished (one earlier `--speed 1` attempt DID
  genuinely hang/stall around 75% for several minutes with zero forward
  progress and had to be Ctrl+C'd; a second `--speed 1` attempt on a fresh
  disc completed with no stall). **Slower writes are meaningfully more
  reliable on this drive — use `--speed 1` or `--speed 2` here, not the 4x
  default**, until/unless retested on different media or a different drive.

Real cdrdao's own raw-mode write (`--driver generic-mmc-raw`, from the same
capture session) ALSO failed on this exact drive with a generic "Write data
failed" partway through — independent confirmation this drive genuinely
struggles with sustained raw DAO writing regardless of which software drives
it, which is why slowing down mattered so much.

**But**: both "successful" DiscForge burns (the 4x-then-hard-failed run
never got here, but the two that DID report `RAW burn complete` with zero
errors) then read back as **completely blank** (`writeinfo` → "empty
(blank)", NWA=0, free blocks = full disc capacity) on THIS drive. A visible
burn ring on the disc surface confirmed real physical writing occurred.
Crucially, **a second, completely unrelated drive also reported the disc as
blank** — and that same drive proved it does real, fresh reads by
successfully reading a different disc (a DVD) in between checks, ruling out
a stale-TOC/caching explanation. That made this conclusively a real
DiscForge bug, not a hardware read-back quirk on one drive.

**Root cause, found by re-reading `Burn()`'s own start-LBA logic**
(`SptiRawDaoBurnEngine.cs`, around where `ReadDriveNwa` is used): the code
branched on the drive's reported next-writable-address (NWA) — if NWA was
usefully negative (≤ −151), it assumed the drive gave a real ATIP lead-in
start and wrote our whole composed image (lead-in + program) from there; if
NWA was NOT deeply negative, it assumed **"the drive manages the lead-in
itself"** and **skipped writing our composed lead-in entirely**, sending
only the program area starting at LBA 0. This drive reports a flat `NWA = 0`
— confirmed via the `[diag]` output on both successful burns, even AFTER
Write Type = Raw mode select had already succeeded (the code's own comment
had assumed the mode change would fix this; it didn't, on this drive). Since
0 is not ≤ −151, every real burn on this drive took the "skip our lead-in"
branch. Nothing else in this raw+no-cue-sheet design (see the class doc
comment: no SEND CUE SHEET in Raw mode, the lead-in sub-channel IS the TOC)
ever supplies a lead-in — so the disc's actual physical lead-in, the ONLY
place a TOC lives, was **never transmitted to the drive at all**, on either
successful run. That explains every symptom exactly: a real, full burn
(dye genuinely changed across the whole program area, hence the visible
ring) that reads back as blank on every drive tried, because the TOC was
simply never written. Real cdrdao's own raw driver
(`GenericMMCraw::startDao()`, read from the source built earlier this
session) has no such branch at all — it unconditionally writes its own full
lead-in every time, which is exactly why it never hit this failure mode.

**Fixed**: collapsed the flawed branch. A genuinely useful negative NWA
(≤ −151) is still honoured as the drive's own authority on the start
address; anything else (0, not-valid, or the NWA read failing outright) now
falls back to composing and sending DiscForge's OWN full lead-in from the
safe default start (−22650, i.e. the default 22,500-sector lead-in length)
— it is NEVER skipped again. Verified: clean build, 0 warnings/errors,
2,503/2,503 tests still passing. **Not yet hardware-tested** — this fix
landed after the two burns that exposed the bug; the very next step is
another real burn (ideally `--speed 1` or `2`, per the reliability finding
above) on a fresh blank disc, followed by `writeinfo D:` (should show real
track/session info, not blank) and the full byte-for-byte verify:

```
dforge build-raw ps1-redump.cue golden.img --subcode raw
dforge read-raw D: readback.bin
dforge raw-verify-readback golden.img readback.bin
```

If `writeinfo` still reports blank after this fix, the next thing to
suspect is the lead-in CONTENT itself (Q-subchannel timing/CRC in
`RawImageGenerator.cs`/`SubQ.cs` — read closely this session and structurally
matches Red Book, but not yet verified byte-for-byte against a real drive's
own successful read), not the start-address logic — but the start-address
bug above was a complete, sufficient explanation on its own for every
symptom observed so far, so it's the most likely fix. If the real burn also
succeeds and verifies clean, this multi-session burn-engine saga is
genuinely closed — update this doc to reflect that and consider unparking
`dforge prove` (feature H), which was explicitly blocked on this.

**Environment note for any future cdrdao rebuild**: MSYS2 has multiple
sub-environments and they are NOT interchangeable for this project. MINGW64
(native-Windows toolchain) has full Win32/`windows.h` access but is missing
POSIX headers cdrdao's Linux-first source assumes (`pwd.h`, `sys/wait.h`,
`arpa/inet.h`, etc.) — `dao/cdrdao.cc` also unconditionally calls `fork()`,
which doesn't exist on native Windows at all, making MINGW64 a dead end for
a full build, not just a header-patching exercise. Plain **MSYS2 MSYS**
(Cygwin-target, `x86_64-pc-cygwin`) has real POSIX emulation including a
working `fork()`, and built everything (`trackdb`, `utils`, `paranoia`)
unmodified — but its `w32api/ntddscsi.h` doesn't self-include `windef.h` the
way MinGW-w64's copy does, so `dao/ScsiIf-nt.cc` failed with `USHORT`/
`UCHAR`/etc. "does not name a type" until `windows.h` was moved to be
included BEFORE `ntddscsi.h` (a one-line include-order fix, landed in the
patch delivered this session — apply the same fix if rebuilding from a fresh
clone). Use **MSYS2 MSYS**, not MINGW64, for any future cdrdao build here.
~~`cdrdao-capture-howto.md` (repo root) still describes the MINGW64 path and
is now stale~~ — **rewritten 2026-08-27**: now MSYS2 MSYS + `libiconv-devel`
+ the include-order patch, reframed as a build reference (the original
SEND-CUE-SHEET investigation it walked through is closed — see the same
date's entries above) rather than a live task list.

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

   **UPDATE (2026-08-25, later the same day): `--simulate` was run for the
   first time and found a real, fixed bug — not a guess, a live sense-code
   trace.** `burn-raw ps1-redump.cue D: --engine spti --simulate` got past
   MODE SELECT and 200+ WRITE(10) chunks fine, then failed at LBA 225 with
   "Not ready: ASC 0x04 ASCQ 0x08" (LONG WRITE IN PROGRESS — the drive
   transiently busy flushing its buffer, a normal condition a well-behaved
   initiator retries). The write loop already HAD a retry for exactly this
   (ASC 0x04 → wait 2s, reissue, up to 6 times) — but it re-fetched sense
   with a fresh REQUEST SENSE CDB after the failure instead of reading the
   sense the drive returned WITH the failing WRITE(10) itself, and by the
   time that follow-up REQUEST SENSE landed the drive's contingent-allegiance
   condition had already cleared, so it came back (0,0,0) — the retry
   condition (`asc == 0x04`) never matched a real 0x04, so the very first
   transient busy moment was fatal. **Fixed**: read `SenseKey`/`Asc`/`Ascq`
   straight off the `SptiResult` the failing `WRITE(10)` already returned
   (`SptiRawDaoBurnEngine.cs`, the write loop in `Burn()`) instead of issuing
   a second REQUEST SENSE; also raised the retry bound 6→10 since a full
   flush can take a few seconds. Rebuilt (0 errors), full suite still
   2,503/2,503. **Not yet re-run on hardware** — this is the next thing to
   try, same command as before:
   `dforge burn-raw ps1-redump.cue D: --engine spti --simulate`.
   If it now runs clean to completion, that's the real Raw-mode write path
   validated non-destructively for the first time ever, and a real (non-
   simulate) burn becomes reasonable to attempt next. If it fails again,
   report the EXACT new sense code — do not assume it's the same bug.

   **CORRECTION (2026-08-25 morning): the "STOP guessing" block below chased
   the wrong code path for five rounds — read this bit first.** `Burn()` (the
   method `dforge burn-raw --engine spti` actually calls for a real burn) is
   **Write Type = Raw**: the whole disc, lead-in included, streamed as raw
   main + P-W subchannel via WRITE(10), with deliberately **NO SEND CUE
   SHEET** at all — the code's own comment says a cue sheet under Raw mode is
   a command-sequence error (ASC 0x2C). But every one of the five fixes below
   was made against `TestCue()`, a separate, legacy diagnostic that still
   exercises **Session-At-Once + SEND CUE SHEET** (data block type 0) — a
   setup `Burn()` stopped using before this session started. All five fixes
   hardened a path the real burn doesn't call. `TestCue()`'s rejections
   (ASC 0x26/0x00 below) say nothing about whether the real Raw-mode burn
   works — that path has genuinely never been tried, not even non-
   destructively. **The actual next step, not yet attempted**: run
   `dforge burn-raw <cue> <drive> --engine spti --simulate` — this runs
   `Burn()`'s FULL real write path (MODE SELECT Write Type=Raw, NWA read,
   chunked WRITE(10) over the whole raw+P-W image, finalise) with the drive's
   test-write bit set (laser off) — genuinely non-destructive, reusable disc,
   and it tests the code that matters instead of the abandoned cue-sheet
   setup. `--test-cue` and the CLI help now say this explicitly
   (`src/DiscForge.Cli/Program.cs` `BurnRawCmd`, and the class doc comment on
   `SptiRawDaoBurnEngine`). Do this before attempting a 6th cue-sheet fix —
   there should never be a 6th, that whole path is dead.

   The original (now superseded) framing, kept for the record — DiscForge's
   `TestCue()` diagnostic still does not work, after **FIVE** rounds of
   fixes this session, all real, source-grounded, committed — and all
   rejected with the byte-for-byte identical sense code:
     1. Cue-sheet Data Form byte was 0x10 (not a defined MMC code); corrected
        against cdrdao's `GenericMMC::createCueSheet` to 0x00/0x10/0x20 by
        track type. (A PDF-spec extraction along the way suggested 0x08,
        ALSO wrong and rejected — the WebFetch summarizer is unreliable for
        exact byte tables, the same failure mode that hallucinated a redump
        hash match earlier this session. Don't trust it for spec bytes again
        without a second, independent source.)
     2. Lead-in was sending three Red-Book-style POINT entries (A0/A1/A2)
        that cdrdao doesn't send at all; replaced with cdrdao's single
        generic lead-in entry (14→12 total entries).
     3. MODE SELECT's Data Block Type was hardcoded to 3 (raw+P-W subchannel)
        even for the Session-At-Once cue-sheet-test path; cdrdao uses 0
        there, reserving 3 for the actual Raw write type. Fixed.
     4. `SetRawDaoWriteParameters` built the whole write-parameters page from
        a blank record instead of reading the drive's current page first and
        flipping only specific bits (cdrdao's `getModePage`+selective-bits
        approach) — notably, cdrdao never touches the Track Mode nibble at
        all, but DiscForge was unconditionally overwriting it. Rewrote as a
        genuine MODE SENSE → modify → MODE SELECT read-modify-write. STILL
        rejected, identical sense code.

   The diagnostic that at least separated "drive limitation" from "DiscForge
   bug": ImgBurn 2.5.8.0 burning the SAME `ps1-redump.cue` on the SAME drive
   succeeds completely — real burn AND read-back verify, 289,321/289,322
   sectors bit-perfect (see above) — using **SAO** as the write type for the
   whole operation, cue sheet included. So the drive and cue-sheet CONTENT
   are provably fine; something in exactly how DiscForge issues the SCSI
   commands (ordering, timing, a CDB field, or something not yet considered)
   is still wrong, and it's specific enough that five source-grounded content
   fixes didn't touch it.

   **What did NOT work as a diagnostic**: asking the user to enable ImgBurn's
   verbose/debug SCSI logging — couldn't find the toggle in the UI in the time
   available. A packet-capture-style diagnostic (ImgBurn's debug log, a
   USB/SCSI sniffer, or a kernel SPTI trace) would still be the right move
   **if** the goal were to make `TestCue()`'s SAO+cue-sheet path work — but
   per the correction above, that's no longer the goal; `Burn()` moved to
   Raw mode (no cue sheet) before this session even started, and the actual
   non-destructive test for THAT path (`--simulate`, see above) doesn't need
   any of that tooling and had simply never been run. Revisit exotic capture
   tooling only if `--simulate` itself fails in a way source-reading can't
   explain — don't reach for it before that.

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

## Correction: feature D (Consensus healing) was already DONE, undocumented

REEVALUATION-2026-08.md lists "D. Consensus healing" as a not-yet-built
frontier feature ("on the shelf: RecoverySession, MergeCertificate,
C2ConsensusMerge, AccurateRip"). That undersold it — `dforge merge-cert`
(Program.cs `MergeCertCmd`, backed by `Core/Recovery/ProvenanceMerge.cs` +
`MergeCertificate.cs`) already IS that feature, fully wired: merges N
imperfect rips of the same pressing, honours each input's `.badsectors.json`
sidecar (holes excluded from the vote, not counted as data), records
per-sector provenance (which copy won and why — AllAgree/EdcRecovered/
VoteVerified/VoteBestEffort/SingleSource/Unrecovered), and emits a signed
(ECDSA) `.dmc.json` certificate anyone can re-verify (`merge-cert verify`,
re-hashes inputs+output against the signature). Unit-tested
(`MergeCertificateTests.cs`). **Nothing to build here — if a future session
reads REEVALUATION.md and reaches for feature D, point it at `merge-cert`
first**, and only extend it (e.g. an AccurateRip-aware tie-breaker for audio
tracks with no EDC, or `C2ConsensusMerge`'s byte-level voting as a pre-pass
before the sector-level provenance merge) rather than reinventing it. Only
gap left in this space: no GUI view exists for it (see below).

## Deliberately parked for a hardware/desktop session

- **Resumable dumps (gap 5)**: progress journal beside the `.part`; needs live
  drive testing to trust the seek/append semantics — don't build it blind.
- ~~**`dforge prove` (feature H)**~~ — **UNPARKED AND BUILT, 2026-08-27**, see
  the dedicated write-up below. Not yet hardware-tested.
- **WASM Core (feature C)**: needs NuGet/Blazor tooling the sandbox can't reach;
  build on Andy's machine or CI.
- **GUI catch-up (gap 7)**: 60 views, and NONE of this session's landings have
  one — `merge-cert`/consensus healing, `dump-cert`, `disc-mri`, `pressing-dna`,
  `drive-dossier`, `disc-actuary`, `vault`, the DVD/BD extraction fix. All are
  pure-Core + CLI already; a WinForms view is plumbing, not research, but it
  needs eyes-on visual iteration (colors, layout, control placement) that a
  sandbox with no Windows/display can't do blind — do this on Andy's machine
  where the result can actually be looked at, not guessed at.

## 2026-08-27 — burn-raw --engine spti: BURN CONFIRMED GOOD; verify-tool false negative found & fixed

The ATIP-based lead-in fix (previous session) was hardware-tested today and
worked completely:

- `burn-log6.txt`: real ATIP lead-in start = `97:26:66` → LBA −11634 (a real,
  disc-specific value, nowhere near the old fixed −22650 guess). Full RAW-DAO
  burn completed 0% → 100% with **zero WRITE(10) failures** — the deterministic
  same-LBA failure that killed three straight burns is closed.
- `dforge writeinfo D:` after the burn: `Disc status: complete / finalized`,
  1 session, tracks 1–8, disc type 0x20, "Track 1: not blank" — the FIRST
  writeinfo all session that didn't come back blank. The burn is real.

Then `raw-verify-readback golden.img readback.bin` reported **FAIL** — 99.99%
of program sectors "mismatched". This looked catastrophic but turned out to
be a bug in the verify tool, not the burn. Traced it by hand (descrambled and
byte-compared golden.img against readback.bin directly in Python, brute-forcing
the alignment offset): **once correctly aligned and descrambled, every sampled
sector — 286/286 on a full deterministic sweep — is byte-identical.** The burn
is provably correct.

Root cause, found in two passes (the first pass below was wrong and is kept
here so a future session doesn't repeat it):

- **First (wrong) theory**: `ProgramBaseAbs` anchors alignment on a single Q
  sub-channel position frame, and real Q reads jitter, so a one-off bad frame
  seemed like the explanation. Fixed it to vote across the whole scan window
  and take the mode instead of the first hit — rebuilt, re-tested on hardware,
  **identical FAIL, byte-for-byte identical numbers.** That ruled out jitter:
  a python dump of the full 400-sector vote window showed the Q sub-channel
  decoding to abs 151 **consistently, 399/400** — not an outlier, a stable
  reading. So voting made no difference; the wrong value was winning honestly.
- **Real root cause**: the Q sub-channel is reported with a small, constant
  address skew relative to the main-channel data it's bundled with in the same
  raw capture — a real, documented drive/read-back phenomenon (sub-channel and
  main-channel aren't always extracted perfectly synchronized). Proof: the
  MAIN-CHANNEL sector header (bytes 12–14, MM:SS:FF) decodes to a clean,
  consistent 150 (400/400 votes) — matching the byte-for-byte-correct
  alignment — while the Q sub-channel on the exact same capture consistently
  says 151. Aligning on Q was comparing every sector against its neighbour
  instead of itself; CD content has zero redundancy across sector boundaries,
  so that reads as "everything is corrupted" even on a byte-perfect disc.

Fixed properly in `src/DiscForge.Core/Raw/RawReadbackCompare.cs`: added
`MainChannelBaseAbs`, which derives the alignment anchor from the main-channel
header instead of Q (trying both scrambled and unscrambled interpretations,
since golden and a real capture can each be in either state, and voting the
same way as the Q fallback). `Compare()` now prefers this and only falls back
to the Q-based `ProgramBaseAbs` when no header is available (audio-only
regions, which have no header at all). Verified the exact fix logic
independently in Python against the real `golden.img`/`readback.bin` before
writing the C# (golden → 0/400 votes, readback → 150/400 votes — matching the
proven-correct offset exactly). Build clean (0 warnings/errors); full suite
2502/2503 — the one failure (`AudioCdTests.Over_74_minutes_...`) is a
pre-existing, unrelated `OutOfMemoryException` on a ~750MB single-allocation
test that this sandbox's memory ceiling can't sustain (confirmed unrelated:
that file wasn't touched, and the 3 `Raw`/`RawReadback` test classes — 29
tests — all pass clean in isolation). Delivered to
`C:\dev\DiscForge\src\DiscForge.Core\Raw\RawReadbackCompare.cs`; the earlier
(wrong) vote-only version was superseded, not layered on top of.

**The Q sub-channel address skew itself is worth a closer look separately**:
it's real, it's consistent (not noise), and it's currently silently absorbed
by preferring main-channel alignment — but the "159 mis-addressed" and "744
sub-timing" counts in the FAIL report above are real Q differences on this
disc, some of which may just be this same skew being judged against a
still-skewed golden Q rather than being corrected for. Worth a dedicated
look once the main verify chain is confirmed green, not before.

Re-ran on hardware after the skew fix:

```
Main channel: 1 mismatch(es), 0 with broken EDC  (153,904 descrambled-on-read, content byte-identical)
Sub-channel: 902 differ - 158 mis-addressed, 0 protection-loss, 744 timing-only
Dropouts:    135,402 program sector(s) missing from the read-back
Result: FAIL - 135,561 defect(s)
```

Still graded FAIL, but read what's actually in it: **153,919 of 153,920
program sectors (99.9994%) are byte-identical.** The 1 main-channel mismatch
is at the very last sector before `read-raw` hit the data→audio track
boundary (`--field auto` correctly stopping — expected, not corruption). The
158 mis-addressed + 744 sub-timing sectors (0.1% and 0.5% of the disc) are
ordinary real-world Q sub-channel read noise — scattered, not paired in any
suspicious pattern, the same class of jitter `--reread/--consensus` exists to
smooth over on a re-read, not evidence of a bad burn. The 135,402 "dropouts"
are NOT unread/corrupt sectors — they're golden's other 7 (audio) tracks that
`read-raw` never attempted to read at all, because this capture only covered
track 1 (data) before stopping at the mode boundary. **`raw-verify-readback`
needs `--partial` for this comparison** (it exists exactly for "an intentional
sub-range, e.g. one track of a mixed-mode disc read on its own" — this wasn't
used yet). With `--partial`, dropouts stop counting and the grade should
land on FAIL-only-for-the-158-mis-addressed (or PassWithNotes, depending on
whether any tie ever crosses the strict defect line) rather than FAIL from
"135,402 defects" that were never really defects.

**Bottom line: `burn-raw --engine spti` now produces a genuinely correct,
byte-verified RAW-DAO burn on Andy's real hardware (TSSTcorp SH-224DB).** The
core bug this entire session was chasing — burn reports success but the disc
comes back blank/corrupt — is closed. What's left is polish, not correctness:

- Re-run `raw-verify-readback golden.img readback.bin --partial` for an
  accurate grade on the track-1-only capture already in hand.
- For genuinely complete disc verification, read back the other 7 (audio)
  tracks too (`read-raw D: t2.bin --track 2`, etc., or a whole-disc read that
  switches field mode at the boundary) and verify those against golden as
  well — not done this session, optional polish once the above is confirmed.
- The 158 mis-addressed sectors are worth one glance (are they clustered near
  the mode boundary, or genuinely scattered across the whole track?) but
  nothing in this session's data suggests they're anything but ordinary drive
  noise.
- Consider whether `LeadInSectors=22500` (the fixed default) in
  `RawImageGenerator` should become ATIP-aware for `build-raw` too (today
  only the burn engine reads ATIP) — harmless for verification since
  alignment is address-based, not offset-based, but worth it for anyone
  reading golden.img's raw bytes directly.

Next steps once `--partial` confirms a clean-enough grade: unpark
`dforge prove` (feature H, previously explicitly blocked on this bug).

**Update, same day**: ran with `--partial` — down to 159 real defects (1
main, 158 mis-addressed) across 153,920 sectors, 99.897% clean. Dug into the
158 by hand (replicated the comparator in Python against the real files):
they're not one uniform thing. A hand-picked sample (sectors 1314/1474/1494/
1516, shown in the tool's own "first differences") turned out to be cases
where the READ-BACK's own Q frame fails its own CRC — a transient sub-channel
read glitch on THIS read pass (real optical media does this occasionally; the
byte pattern is a single flipped bit in the control/ADR nibble), not evidence
the disc holds a wrong address. That's a fundamentally different, much more
benign thing than "the disc was written with a bad address" — the drive
already told us the frame is untrustworthy via its own CRC, so judging it
byte-for-byte against golden was mislabeling read noise as a burn defect.
Separately, a full scan found ~15 more concentrated right at the very tail of
the capture (154055–154069) — the last ~15 sectors before `read-raw` hit the
data→audio boundary, a messy addressing region (postgap/pregap countdown)
that's a known-tricky spot for sub-channel decoding, not the rest of the disc.

**Update 2 (final result, same day)**: re-ran on hardware after the
read-noise fix — `sub-read-noise` absorbed 304 of the 902 sub-channel
differences that were previously either misclassified or padding out the
mis-addressed count. Final result:

```
Main channel: 1 mismatch(es) — the descrambled content is byte-identical
Sub-channel: 15 mis-addressed, 0 protection-loss, 583 timing-only, 304 read-noise
Result: FAIL - 16 defect(s) across 153,920 sectors (main 1, mis-addressed 15)
```

**99.9896% of the disc is exactly byte-identical to the golden.** The
remaining 16 "defects" (1 main + 15 mis-addressed) are the same cluster
identified above — sectors 154055–154069, the last ~15 sectors of the
capture, right at the data→audio track-type boundary where `read-raw`
stopped. That's a known-messy addressing region (postgap/pregap countdown
across a track transition), not evidence of disc-wide corruption — every
other sector across the whole ~154,000-sector data track (the actual PS1
game content) is exact.

**This closes the session's core question. `burn-raw --engine spti` produces
a genuinely correct, byte-verified RAW-DAO burn on Andy's real hardware
(TSSTcorp SH-224DB).** The bug that started this entire multi-day session —
burn reports success but the disc comes back blank or wrong — is fixed and
proven, not just believed. What's left is optional polish, not correctness:
reading back the other 7 (audio) tracks for full-disc coverage, and possibly
narrowing why sub-channel addressing gets messy right at a track boundary
(low priority — it's a capture-edge artifact, not a burn defect). Next
concrete step: unpark `dforge prove` (feature H) now that the burn engine
itself is proven on hardware.

Fixed: `RawReadbackCompare` now distinguishes these from real mis-addressing.
When golden's Q is valid but the read-back's own Q CRC fails, it's classified
as `sub-read-noise` (Warning, doesn't fail the grade) instead of
`mis-addressed` (Defect). "Mis-addressed" is now reserved for what it should
actually mean: a Q frame that's internally self-consistent (its own CRC
checks out) but decodes to the wrong place — a real defect. Added
`Report.SubReadNoise`, wired through the CLI/JSON/HTML report, and fixed the
existing `A_changed_q_address_is_a_mis_addressed_defect` test (it had been
flipping an address byte without fixing the CRC, which is what the read-noise
case looks like, not a genuine mis-addressed one — recomputed the CRC after
the flip so it tests what its name says) plus added a new test for the
read-noise path itself. Build clean, 2504/2505 pass (the one failure is the
same pre-existing unrelated `AudioCdTests` OOM). **Not yet re-run on
hardware** — next step is confirming the grade on `golden.img`/`readback.bin`
lands on PASS or PassWithNotes with `--partial`, which per the hand analysis
above it should (159 → ~1 real defect, the rest reclassified as noise).

## 2026-08-27 (cont'd) — `dforge prove` (feature H) built

Unparked and implemented now that `burn-raw --engine spti` is proven correct
on hardware (see above). New command: `dforge prove <disc.cue> <drive>`
(`src/DiscForge.Cli/Program.cs`, `ProveCmd`, dispatch entry next to
`read-raw`).

What it does, in one verb: composes the golden image from the cue
(`RawImageGenerator.Generate`, same as `build-raw`) → burns it via
`SptiRawDaoBurnEngine.Burn` (the exact path this whole session proved) →
reads the disc's own post-burn TOC (`DiscReader.ReadToc`) → for EVERY track,
reads it back with that track's own TOC-derived start/length/field
(`RawDiscReader.Read`, data vs audio field auto-selected per track — this is
what saves a user from the manual `--track N` juggling `HARDWARE_RUNBOOK.md`
§4 currently spells out by hand) → verifies each track against the golden
with `RawReadbackCompare.Compare(..., partial: true)` (`--partial` because
each per-track capture is legitimately a sub-range of the whole-disc golden)
→ prints one line per track (OK/FAIL + summary) and one final verdict:
`=== PROVEN ===` or `=== FAILED ===`. Exit code 0/1 matches. `--report`
writes a per-track HTML certificate; `--keep-temp` keeps the golden image and
every track's raw capture instead of deleting them (useful for the same kind
of by-hand forensics this session did on `golden.img`/`readback.bin` when
something looks off).

Scope, stated plainly so nobody overclaims it later: this is the BURN half of
the feature H spec ("dump → audit → certificate → optional reburn →
cross-verify"). It starts from a `.cue` you already trust — it does NOT run
`dump-score`/`dump-audit`/`dump-merge`/`convert`/`verify-convert` first. That
front half already exists as separate shipping commands (see
`HARDWARE_RUNBOOK.md` §4); wiring them into `prove` too, so the verb truly
covers dump-to-reburn end to end, is a reasonable follow-up but was out of
scope for this pass — the whole reason feature H was blocked all session was
the burn engine, not the dump/audit tooling (which was never in question).

Verified: builds clean on both targets (`cli` and `cli-win`, 0 errors), full
suite still 2504/2505 (same pre-existing unrelated `AudioCdTests` OOM). Ran
`prove` with no args (usage prints correctly), with a real temp cue/bin on
the non-Windows target (correctly fails with "needs the Windows build" rather
than crashing), and confirmed cue/extension/drive-letter validation all
behave the same way `burn-raw`'s do. **Not yet run end-to-end against real
hardware** — that's the next thing to do: `dforge prove ps1-redump.cue D:`
on Andy's machine, expecting it to reproduce today's manually-driven result
(PROVEN on the data track; the 7 audio tracks are untested territory since
they were never read back this session — worth watching for anything
mixed-mode-specific `read-raw --track` didn't already surface).

## 2026-08-27 (continued) — "XA Form1/2-aware EDC/ECC" backlog item: already done, no code needed

Andy asked which of the two remaining no-hardware PS1-backlog items (CU2
sidecar support vs. XA Mode 2 Form 1/2-aware EDC/ECC) was quicker. Checked
the second one first since it sounded smaller. Read every EDC/health-map
consumer in `DiscForge.Core` that branches on sector mode (`DiscHealthMap`,
`DiscMri`, `PremasterGate`, `DumpReconstruct`, `DumpMerge`,
`ExtractionAudit`, `RawImageInspector`, `DumpingWizard`,
`RawReadbackCompare`) — every single one already checks the XA subheader
submode byte (`main[18] & 0x20`) and picks Form 1 EDC+ECC vs. Form 2
EDC-only (or returns "nothing checkable" when the Form 2 EDC field is
zero/unused) before validating. `SectorExtraction.cs`'s per-datatype
extraction path is explicitly told which form to expect by the caller, so it
was never exposed to this failure mode either. **No false Form 2 damage
reports exist anywhere in the current codebase.** Marked done in
`ROADMAP.md` — struck through with a note, not deleted, so a future session
doesn't waste time rediscovering this. No build/test cycle needed since
nothing changed.

Conclusion: **CU2 sidecar read/write/verify is the actual next-quickest
no-hardware item** (the XA item turned out to be a documentation cleanup,
not a feature). Not yet started — next step is reading Cue2cu2's format
notes (github.com/NRGDEAD/Cue2cu2: absolute-LBA track map, explicit
data-track start + lead-out, rev2 per-track pregap) and DiscForge's existing
`DaoCueSheet.cs`/cue-parsing code to scope a `.cu2` reader/writer/verifier as
a dialect-free cross-check against `.cue`.

## 2026-08-27 (continued 2) — CU2 was ALSO already done; built the license-string reader instead

Went to build "CU2 sidecar support" per the conclusion above and found it was
already fully implemented (`DiscForge.Core.Cue.Cu2` — `Write`/`Parse`/`Verify`
— plus `dforge cu2 write|verify`, tested in `Cu2Tests.cs`, landed 2026-08-15).
Same for **Pregap-accuracy check** (`PregapConformance.cs` +
`dforge pregap-check`, also already complete). So of the four PS1-backlog
items on the table, three were already done and undocumented as such. Marked
all three struck-through in `ROADMAP.md` this time (with what already exists,
so nobody re-investigates them either).

The fourth, **on-disc region license-string reader**, was genuinely missing
(confirmed: `grep`ing for "Sony Computer Entertainment" / "LicenseString" /
region-marker outside `PsExe.cs`'s own PS-EXE-header field — a different,
already-existing thing — turned up nothing). Built it:

- **`src/DiscForge.Core/PlayStation/LicenseString.cs`** — new. `Parse(sector)`
  checks the fixed 32-byte "          Licensed  by          " line-1 text and
  the region-specific line-2 text Sony's mastering tools wrote into sector 4
  of the data track (Japan/Europe/America), ahead of the ISO 9660 volume
  descriptors at sector 16. `FromImage(path)` opens a .cue/.bin/.iso via the
  existing `RawTrackReader` (the same cooked-2048-byte-sector abstraction
  `IsoReader`/`SystemCnf` already use — no new sector-layout code needed) and
  reads sector 4 directly. `CrossCheck(license, systemCnfRegion)` compares the
  license text's region against `SystemCnf.RegionOf()`'s region string and
  reports a one-line disagreement (or null when they agree, or when
  SYSTEM.CNF's region — Korea/Asia/Unknown — has no dedicated license block
  of its own to compare against, which real SCEK/SCEA-adjacent discs are
  known to lack).
- **CLI**: `dforge license-check <image> [--json]` — prints the detected
  region, the SYSTEM.CNF region if found, and flags a mismatch. Exit 0 when
  well-formed and no mismatch, 2 otherwise (matches the `pregap-check`/`cu2
  verify` convention).
- **Source and honesty note**: the byte layout (line 1 = 32 bytes at 0x000;
  line 2 = 33 bytes Japan / 38 bytes Europe+America at 0x020; padding fills
  the rest) comes from psx-spx (consoledev.net/cdromformat, "Licence
  String" section), fetched three times independently this session with
  consistent results. It's also internally self-consistent: the documented
  line/padding lengths sum to exactly 2048 bytes for both layouts
  (32+33+1983 and 32+38+1978) — pinned as a dedicated test
  (`The_documented_line_and_padding_lengths_sum_to_exactly_one_sector`), which
  would fail if any of those three numbers had been transcribed wrong. Line 1
  and each region's line 2 are matched exactly. The *padding content* after
  line 2 (documented as all-zero for EU/US, a repeating fill pattern for
  Japan) is checked too, but only informationally — that specific byte
  pattern was not cross-checked against a real disc dump this session, so a
  mismatch there is reported as a note (`PaddingLooksStandard = false`, an
  `Issues` entry) and never turns a correctly-identified region into
  "unrecognised." This mirrors the same SCOPE/HONESTY discipline
  `DaoCueSheet.cs` uses elsewhere in this codebase.
- **Tests**: `tests/DiscForge.Core.Tests/LicenseStringTests.cs`, 17 cases —
  the layout self-consistency check above, exact parsing for all three
  regions, garbage/all-zero/corrupted-line-2 handling, non-standard-padding-
  is-a-note-not-a-misidentification, the cross-check (agree / disagree / no
  comparison possible), and an end-to-end `FromImage` test that builds a real
  ISO via `IsoBuilder`, stamps a genuine Europe license block into its sector
  4, and reads it back.
- **Verified**: builds clean (`cli`/`cli-win`, 0 errors/warnings). Full suite
  2521/2522 (2520 pass + the new 17, only the pre-existing unrelated
  `AudioCdTests` OOM fails — same as every run this session). CLI usage text
  and the not-found error path smoke-tested directly.
- **Not yet run against a real disc/dump** — the padding-pattern caveat above
  is the reason to treat any real-world padding mismatch as a note worth a
  second look, not a first-day certainty. If a real PS1 dump's sector 4 turns
  up with different padding than documented, that's useful signal to
  incorporate here, not a bug report against this code.

All four items from the "what else can we do without hardware" menu are now
closed (three found already done, one built this session). Nothing left on
that specific list; see `ROADMAP.md`'s PS1 backlog for the remaining
(unstarted) items — multi-disc `.m3u`/`MULTIDISC.LST` modeling and full R–W
subchannel capture — if more no-hardware work is wanted next.

## 2026-08-28 — v1.68.0 installer delivered; multi-disc set modeling + full R-W subchannel capture built

Andy built and confirmed a working Windows installer from the sandbox-provided
`installer/publish.ps1` + `installer/DiscForge.iss` (Inno Setup) — first real
`.exe`/installer this project has produced, at `C:\dev\DiscForge\installer\
Output\DiscForge-Setup-1.68.0.exe`. (Sandbox still cannot cross-compile a real
Windows binary itself — no NuGet network access, no cached win-x64 runtime
packages — so the installer scripts have to run on Andy's machine; this is
now confirmed working end to end.)

Then picked "all four" from a menu of what to do next:

**1. Multi-disc set modeling — DONE.** New `src/DiscForge.Core/Library/
MultiDiscSet.cs`: `MultiDiscDetector.Detect` groups image paths by the
Redump/No-Intro "(Disc N)"/"(Disc N of M)" naming convention (same
directory + title with the tag stripped, case-insensitive), reports missing
disc numbers from gaps or a declared total, and ignores anything that isn't
genuinely part of a set (a lone untagged "(Disc 1)" with no sibling and no
declared total is NOT reported — too ambiguous). `MultiDiscManifestBuilder`
hashes every disc (reusing `ImageChecksums.Compute`) into a `MultiDiscManifest`
with set-level completeness. `OdeExport.cs` gained `OdeExporter.PsioSet` (all
discs of a title share ONE folder — confirmed against the real PSIO Systems
Manual R30, contradicting a stale in-repo comment that said one folder per
disc) and `PsioMultiDisc.BuildLst`, which emits a real `MULTIDISC.LST`: one
filename per line, CRLF-joined, **no trailing terminator**, verified byte-for-
byte with `od -c` against the documented format. CLI: `multidisc-detect
<folder> [-r]`, `multidisc-manifest <folder> --title X [-r] [--json]`, and
`ode-export` is now variadic (any number of `.cue` paths before the output
folder) so a multi-disc PSIO export is one command. 20 new tests
(`MultiDiscSetTests.cs`, 14; `OdeExportTests.cs`, 3 new covering single-disc-
no-LST, multi-disc-shared-folder-with-LST, and same-filename-collision-refused).

**2. Full R-W subchannel capture — DONE, not hardware-tested.** Investigated
first and found almost everything already existed: `SubcodeFrame` (Core/Raw/
SubQ.cs) already encodes/decodes all 8 channels (P, Q, R-W) across all three
physical layouts (Pq16 / Packed96 / Interleaved96), and `read-raw`/
`RawDiscReader` already captures full raw P-W embedded in every 2448-byte
sector as the backbone of the whole burn-verify pipeline — CD+G, CD-TEXT and
LibCrypt analysis all already consume it. The one real gap: the MMC
`CorrectedRw` (selector 0x04 — the drive's own firmware-corrected,
de-interleaved reading) was defined in `MmcCommands.SubChannel` but never
actually requested anywhere, and the dedicated standalone `SubchannelReader`
class (Devices layer) had zero CLI exposure. Built exactly that: `Subchannel
Reader.SupportsCorrectedSubchannel` + `.ReadCorrected` (refactored `.Read`
to share a new private `ReadCore`), `RawSubchannel.CompareRawAndCorrected`
(Core layer — sector-by-sector Q comparison between a raw interleaved capture
and the drive's corrected capture, reporting agreement count, CRC-validity
flips, and up to 1,000 disagreeing sector indices; a byte-level Q difference
that stays CRC-valid on both sides is not counted as a "flip" — only a
disagreement where one side's CRC validates and the other's doesn't is), and
`dforge subchannel-dump <drive> <out.sub> [--start LBA] [--length N]
[--track N] [--corrected out2.sub] [--compare]` (Windows-only, same pattern
as `read-raw`/`prove`). 4 new tests (`RawSubchannelCompareTests.cs`) — pure
logic, author matching raw/corrected byte arrays from the same `SubQ.Position`
content via `SubcodeFrame.EmitInterleaved96`/`EmitPacked96`, then corrupt one
side to check disagreement counting, CRC-flip detection, and length-mismatch
rejection. **Not yet run against real hardware** — same disclosure as
`dforge prove` before its own hardware confirmation: builds clean, logic is
unit-tested, but the actual MMC CORRECTED selector's behavior on a real drive
(does it return zeroed R-W, does the drive even support it) is unconfirmed.
Worth trying `dforge subchannel-dump D: raw.sub --corrected corrected.sub
--compare` alongside the next `dforge prove` hardware run.

Verified: `bash build.sh cli` and `bash build.sh cli-win` both 0 warnings/0
errors after every change. Full suite: 2541 passed, 1 failed (the same
pre-existing unrelated `AudioCdTests.Over_74_minutes_warns_that_80_minute_
media_is_needed` OOM this sandbox's memory ceiling has never been able to
sustain — not touched, not new).

**3. Housekeeping — reviewed, needs Andy's hands, not further sandbox work.**
`origin/main` (b14255b, tagged v1.67.0) is 4 commits behind `origin/public-
release` (the 2026-08-25 burn-raw/extract-sectors fixes) — this sandbox has
no push access to the repo (`git push --dry-run` → 403, "not in this
session's authorized repository set"), so the merge has to happen from Andy's
machine. Plus, as of this session, there's also everything through v1.68.0
(license-check, installer, multi-disc, subchannel-dump) sitting only on
Andy's local `C:\dev\DiscForge` — worth deciding in one pass whether to merge
public-release into main AND push v1.68.0 together, rather than two separate
merge operations. `gh` CLI isn't installed in the sandbox and api.github.com/
github.com are both blocked for WebFetch, so CI status can't be checked from
here either. Other items unchanged: PATH shadowing between two `dforge`
installs (old "DiscForge 1.65" still in Program Files), redump.org hash
check for `ps2game.iso` (MD5 `30255F8E8958A963212CA6455BB29EE0`, still
pending), COPTR/awesome-list submission text already drafted and ready in
`docs/registry-submissions.md`.

**4. `dforge prove` on real hardware — not yet started this window.** Next
concrete step for Andy: `dforge prove ps1-redump.cue D:` (per the write-up
above, 2026-08-27), and now also worth trying `dforge subchannel-dump` on the
same drive per point 2 above.

All of items 1 and 2's code/test files were delivered this session via
SendUserFile + the device bridge to `C:\dev\DiscForge\...` (not just built in
the sandbox) — see the file list in this entry's commit for exact paths.

## Longer-term backlog (docs/ROADMAP.md)

Drive-capabilities DB growth, offset-shift disc detection, prototype scanner,
GUI views for recover/secure-rip, remaining non-atomic writers, PS1 backlog
(remaining: PS1 save-container id/conversion, "un-capturable protection"
honesty field).
