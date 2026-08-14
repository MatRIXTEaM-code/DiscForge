# Hardware runbook — the drive-bound work, push-button

Everything here needs the physical drive (the Plextor PX-W5224A for CD work). Each section is a
copy-paste sequence using shipping `dforge` commands. Replace `D:` with your drive letter. Rebuild
the CLI first so you're on the current build:

```
dotnet publish src\DiscForge.Cli\DiscForge.Cli.csproj -c Release -f net8.0-windows -o C:\tools\dforge
```

The burn-day rung details live in [RAW_DAO.md](RAW_DAO.md); this is the operational checklist.

## 1. Finish rung 7 — mixed-mode audio (the one open burn-day step)

The mixed disc is already burned and its data track is PASS. Only the audio read remains. `--track`
pulls the track's start LBA, length and field mode from the TOC (so the unreadable pregap is
skipped automatically):

```
dforge read-raw D: audio_rb.bin --track 2
dforge raw-verify-readback golden.img audio_rb.bin --partial --report cert-audio.html
```

Expected: PASS or PASS-with-notes. That closes the entire RAW-DAO ladder (rungs 1–7).

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

---

## Still needs a command built first (then hardware)

These are drive-bound *and* not yet a single command — noted so the backlog is honest:

- **Drive-capabilities profile** — one command that probes read offset, C2 accuracy, cache-defeat
  and overread and writes a per-drive profile. The pieces exist (`read-offset`, C2 handling); the
  consolidated profiler does not.
- **Adaptive re-read Tier B** — wire the Tier-A controller (`AdaptiveReread`, already built and
  tested) to real `read-raw` retries with actual speed/flag strategies.
- **GUI burn Verify/Test** — the `BurnView` verify/test buttons still throw `NotImplementedException`;
  the CLI path (`raw-verify-readback`) is what to route them through.
