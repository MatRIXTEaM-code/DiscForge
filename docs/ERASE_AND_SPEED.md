# Erasing and write speed (v1.1.0)

Two additions to the Burn screen, closing the first of the parity gaps with
ImgBurn: blanking rewritable media, and choosing a write speed instead of
always burning at the drive's maximum.

Both live in `Imapi2MediaTools` (DiscForge.Devices) and follow the house
IMAPI2 style: late-bound COM by ProgID, no interop assembly, plain-language
errors.

## Erase

**UI:** Burn screen → *Speed / Media* group → **Erase disc…**. It acts on the
drive highlighted in the destination list (or the only drive, if there's just
one). The dialog offers:

- **Quick** (Yes) — blanks the lead-in/TOC so the disc reads as empty.
  Seconds. The right choice almost always.
- **Full** (No) — overwrites the entire surface. Takes as long as a burn.
  For media that's been misbehaving, or when the old contents must be
  unrecoverable.

Only rewritable media can be erased (CD-RW, DVD-RW, DVD+RW, BD-RE); the
attempt is refused in plain words for anything else. After an erase the view
re-detects drives, so the "disc is not blank" warning clears itself.

Engine: `IMAPI2.MsftDiscFormat2Erase` — `IsCurrentMediaSupported` gate,
`FullErase` flag, blocking `EraseMedia()`.

Note: DVD-RAM and BD-RE don't strictly need erasing (they overwrite in
place), but IMAPI2 accepts the operation where the media supports it.

## Write speed

**UI:** Burn screen → *Speed / Media* group → speed dropdown. Populated on
**Detect drives** by asking each drive what it supports *for the media it
currently holds* — speeds are a property of drive+media together, which is
why the list is empty (just "Max") until a writable disc is in. With several
drives the dropdown offers the union; each engine snaps to what its own
drive supports.

Labels show the familiar X factor with the real rate: `8x (11.1 MB/s)`.
1x divisors: CD 75 sectors/s, DVD ≈ 677, BD ≈ 2195 (IMAPI2's unit is
2048-byte sectors per second throughout).

Plumbing:

- `BurnPlan.WriteSpeedSectorsPerSecond` (Core) — `null` = drive default (max).
- Data engine: `IDiscFormat2Data.SetWriteSpeed(sps, false)` after the media
  check.
- Audio engine: `IDiscFormat2TrackAtOnce.SetWriteSpeed` — which is only valid
  **after** `PrepareMedia`, hence the different placement.

A speed request is a request, not a promise: drives snap to their nearest
supported speed, and a drive that rejects the call entirely still burns at
its default rather than failing the job. The log records what was asked for
and (on the data path) what the drive actually chose.

## Why burn slower at all?

- Older or marginal media often burns more reliably below its rated speed.
- Audio CDs for old players: 8x–16x burns tend to read better on 1990s
  hardware than 48x ones.
- A slower burn keeps the drive quieter and cooler during long DL/BD jobs.

## Hardware test checklist

1. CD-RW in the TSSTcorp: quick erase a written disc → detect shows blank →
   burn a small ISO at the lowest offered speed → verify.
2. Same disc: full erase (time it) → burn at max → verify.
3. DVD+RW if available: quick erase, re-detect, burn.
4. Audio: burn a short WAV compilation at a low speed on CD-R; confirm the
   log shows the requested speed and the burn time roughly matches it.
5. Negative: press Erase with a pressed CD-ROM in the drive — expect the
   plain-language refusal, no exception dialog.
