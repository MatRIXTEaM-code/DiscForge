# Subchannel-faithful copy (v1.8.0) — the CloneCD / BlindWrite capability

The one thing the PSX-backup tools of 2001 could do that generic burners
couldn't: preserve deliberately-corrupt sub-channel data. This is that.

## Why it exists

Some discs hide copy protection in intentionally-WRONG sub-channel Q.
PlayStation's **LibCrypt** is the archetype: ~16 sectors, in two clusters,
whose Q-CRC is deliberately broken. The console checks for exactly that
corruption. A burner that computes correct Q — which is what DiscForge
normally does, and does well — "repairs" those sectors and produces a disc
the console rejects as a copy.

CloneCD's answer was a checkbox: **"Don't repair sub-channel data."**
BlindWrite did the same. DiscForge's answer is verbatim mode: re-emit the
source's captured sub-channel byte for byte, Q corruption included.

## The pieces

`RawSubchannel` (Core) — the verbatim sidecar format (96 bytes/sector, raw
interleaved P-W, CloneCD-.sub-compatible) and a read-only analyser:

```
dforge subch <file.sub>
```

reports Q-CRC validity and whether the invalid frames form a **LibCrypt
fingerprint** — a small number (≤64) of corrupt Q frames scattered through
an otherwise-valid stream. Zero invalid = plain disc; hundreds = a bad rip,
not protection; a handful = preserve verbatim.

`RawTrack.SubVerbatim` (Core) — when set, the generator emits the source's
whole 96-byte frame unchanged (P, Q and R-W), instead of building Q from the
layout. `DiscLayout.HasVerbatimSubchannel` drives sector-type negotiation:
verbatim needs a 96-byte raw type, and the burn is refused on a PQ-16-only
drive rather than silently stripping the protection.

```
dforge build-raw game.cue faithful.img --verbatim
```

loads `game.sub` next to `game.bin` as a verbatim source and composes a DAO
image whose sub-channel is bit-identical to the source.

## Proven

Round-trip, in tests and live: a synthetic PSX-style disc (8000 Mode 2
sectors, 16 LibCrypt sectors in two clusters) → `build-raw --verbatim` →
subchannel recovered from the image → **byte-identical to the source .sub**,
same 16 corrupt frames in the same places. The contrast test shows
non-verbatim mode reduces those 16 to zero — protection destroyed — which is
the whole reason the mode exists. `SubchannelFidelityTests` pins it.

## What still needs hardware

Capture. Reading the source's raw 96-byte sub-channel off a disc needs the
drive (READ CD with sub-channel selection, already wired into
`MmcCommands.ReadCd`). The write path, the format, the analyser, and the
preservation guarantee are all done and tested offline; the rip side joins
them when a drive is in play — and not every drive returns honest raw
sub-channel, which is exactly why CloneCD's "select number of readers"
caveat existed.
