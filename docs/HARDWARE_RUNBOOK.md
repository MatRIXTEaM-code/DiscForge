# Hardware runbook — the drive-bound work, push-button

Everything here needs the physical drive (the Plextor PX-W5224A for CD work). Each section is a
copy-paste sequence using shipping `dforge` commands. Replace `D:` with your drive letter. Rebuild
the CLI first so you're on the current build:

```
dotnet publish src\DiscForge.Cli\DiscForge.Cli.csproj -c Release -f net8.0-windows -o C:\tools\dforge
```

The burn-day rung details live in [RAW_DAO.md](RAW_DAO.md); this is the operational checklist.

## 1. Rung 7 — mixed-mode audio — CLOSED 2026-08-29

Done. A fresh `mixed.cue` disc was burned on the PX-W5224A and both tracks verified
independently via `--track N`: data track PASS (450 sectors, byte-identical), audio track
PASS (350 sectors, byte-identical, 1 benign sub-timing note). The entire RAW-DAO ladder
(rungs 1–7) is now PASS on real hardware — see `RAW_DAO.md`'s 2026-08-29 status block for
the full result.

Note for next time: `golden.img`, `data.bin`, `a.bin` and `mixed.cue` are **generic
filenames reused across every fixture in this runbook** — after running a different rung's
loop, these get silently overwritten with that rung's content. Before reusing an old
`golden.img`, sanity-check it against the fixture you actually want with
`dforge inspect-raw golden.img --deep` (sector count and track layout should match the CUE),
or just regenerate the source files fresh from the recipe below before rebuilding.

## 2. Drive read-offset calibration

Redump-style read-offset for accurate audio extraction. Get the drive's sample offset (from the
Redump drive database or a known-offset disc), then correct a rip:

```
dforge read-offset <samples>                 # offset math only
dforge read-offset <samples> raw.wav out.wav # apply the offset to a WAV rip
```

## 3. Bitsetting (book type) — capture, learn, replay

DiscForge never fabricates vendor book-type bytes; it learns them from a trace of your own drive,
then replays that exact command. See [BITSETTING.md](BITSETTING.md).

```
:: 1) capture your drive setting the book type (vendor tool + a SCSI/USB sniffer), save as trace.txt
dforge booktype-trace trace.txt --save recipe.json      # decode + learn a replay recipe
dforge booktype-set D: --recipe recipe.json             # replay it over SPTI on that drive
```

## 4. Full dump → verify → merge → convert → burn → prove round-trip

The end-to-end preservation loop, all shipping commands:

```
:: read the disc (retry on error), or raw-dump for a raw 2352 image
dforge read-disc D: game.iso --continue-on-error --retries 8

:: score / audit the dump's confidence
dforge dump-score raw.bin                    # 0–100 confidence from EDC/ECC
dforge dump-audit game.cue --dat Redump.dat  # GOOD / SUSPECT / BAD, fused verdict

:: if you took several imperfect rips of the SAME disc, merge them (byte/C2 consensus)
dforge dump-merge merged.bin rip1.bin rip2.bin rip3.bin --sector-size 2352
dforge c2-merge merged.bin rip1.bin rip1.c2 rip2.bin rip2.c2   # C2-guided, chains RSPC ECC

:: convert between formats losslessly, and PROVE it lost nothing
dforge convert game.cue game.chd
dforge verify-convert game.cue game.chd --report convert-cert.html

:: burn it back (RAW-DAO for exact layout) and prove the burn landed byte-for-byte
dforge burn-raw game.cue D: --engine spti
dforge read-raw D: readback.bin --length <program-sectors>
dforge raw-verify-readback golden.img readback.bin --report burn-cert.html

:: final fixity: checksums both ways
dforge hashgen sha1 game.sha1 game.iso
```

## 5. Pull a specific sector range (investigation)

```
dforge extract-sectors game.iso slice.bin --start <addr> --count N
```

## 6. Drive status / next-writable-address (before any raw burn)

```
dforge writeinfo D:      # disc status + NWA (the raw-DAO write setup value)
dforge drives            # list recorders
dforge blank D:          # fast-erase a CD-RW before re-burning (--full for a full erase)
```

> **Status 2026-08-29:** both new probes confirmed on the PX-W5224A. **Cache-defeat: genuinely
> re-read** (cold/warm read times both ~0.7 ms, ratio 0.98 — no cache short-circuit), consistent
> with this drive family's community reputation as a reliable dumper. **Overread: yes**, matching
> the bundled knowledge-base reference exactly. **`reread-probe` at LBA 0: RECOVERED in 1 read**,
> correctly identified track 1 as a data track and validated it via real EDC/ECC against the actual
> disc. That run only exercises the "accept on first read" path, not escalation — a genuinely
> marginal/scratched sector is needed to prove the SwitchStrategy → GiveUp ladder end to end;
> worth doing if a scratched disc turns up.

## 7. Drive-capabilities profile (advertised + empirically probed)

`drive-profile` consolidates a drive's read/write reach, write modes, and read-fidelity flags from
INQUIRY + GET CONFIGURATION + mode page 2Ah, then runs two non-destructive hardware probes when a
disc is loaded — lead-out OVERREAD and CACHE-DEFEAT (a timing comparison: read a sector, read it
again immediately, and check whether the drive actually went back to the media or just served its
own cache — see `DriveCacheDefeatProbe` for the method). `--no-probe` skips both if you only want
the advertised half.

```
dforge drive-profile D: --out profile.json
```

C2 ACCURACY still needs a known-defective disc (no probe can prove pointer accuracy without one),
and audio READ-OFFSET still needs an AccurateRip reference (`read-offset`) — both are reported
honestly as unprobed/undetermined rather than guessed.

## 8. Adaptive re-read, Tier B (real hardware)

`reread-probe` drives the Tier-A decision logic (`AdaptiveReread`, proven hardware-free by its own
test suite) against ONE real sector, escalating through three strategies — plain re-read, C2-guided
re-read, then a slow (4x) C2-guided re-read — until the sector is recovered (EDC valid for data, or
every read agrees with no C2 flags for audio) or every strategy is exhausted:

```
dforge reread-probe D: --lba <n>
```

Point it at a sector you already suspect is marginal (flagged by `disc-scan`, or one a prior dump
had to retry). It's scoped to a single sector by design — a validation/diagnostic tool proving the
real-hardware path works, not (yet) wired into `read-disc`'s own retry loop.

## 9. GUI burn Verify/Test

Already wired for the path that matters for preservation: `BurnView`'s Verify and Test buttons call
`VerifyDiscAsync`/`VerifyRawDiscAsync`/`TestRawDiscAsync`, all implemented, for both plain-ISO
verify and RAW/CUE verify+test. The one `NotImplementedException` left is Test on a plain ISO via
the IMAPI2 data path specifically — Windows' own IMAPI2 API has no simulated-write mode for data
discs, so there is nothing to route to a CLI equivalent; the message the button shows explains
this and suggests a rewritable disc instead. (An earlier version of this runbook described the
Verify/Test buttons as unimplemented outright — that was stale; corrected 2026-08-29.)
