# RAW DAO / SAO writing (v1.2.0)

The DiscJuggler capability: writing a CD as one continuous disc-at-once
stream — main channel and sub-channels together, lead-in included — so the
disc comes out exactly as specified. This is what makes the following real
in DiscForge:

- **Full CUE semantics on write** — multiple INDEX points per track, PREGAP
  (generated) and INDEX 00 (stored) gaps of any length, POSTGAP, and FLAGS
  (DCP, 4CH, PRE; SCMS is parsed and stored).
- **CD-TEXT** — album/track titles and performers in the lead-in R–W.
- **ISRC and MCN** — woven into the Q channel at the Red Book cadence.
- **Mixed-mode discs** — audio and data tracks in one session, gaps and
  indexes exactly where the layout says.
- **Data sector synthesis** — MODE1/2048 sources get sync, header, EDC and
  full RSPC ECC computed, then scrambling, exactly as a drive's encoder
  would have done.

## Architecture

Everything difficult lives in **Core** and is unit-tested with no hardware:

| Piece | File | Job |
|---|---|---|
| `Crc16` | `Util/Crc16.cs` | CRC-16/CCITT (Q frames, CD-TEXT packs), tested against the XMODEM vector |
| `CdScrambler` | `Raw/CdScrambler.cs` | ECMA-130 Annex B scrambler; data sectors are written to disc scrambled |
| `EdcEcc` | `Raw/EdcEcc.cs` | EDC + Reed-Solomon Product Code parity (P: 43 word-columns ×2 planes, Q: 26 diagonals ×2), verified by zero-syndrome evaluation at both generator roots |
| `RawSectorBuilder` | `Raw/RawSectorBuilder.cs` | 2048→2352 Mode 1 synthesis; 2336→2352 Mode 2 |
| `SubQ` / `SubcodeFrame` | `Raw/SubQ.cs` | Q frames (TOC, position, countdown, MCN, ISRC) and the three physical subcode layouts |
| `CdTextBuilder` | `Raw/CdTextBuilder.cs` | 18-byte pack generation (0x80/0x81/0x8F), 4 packs per lead-in sector |
| `DiscLayout` | `Raw/DiscLayout.cs` | The logical disc: tracks, gaps, indexes, flags, sources. Built from CUE or CDI |
| `RawImageGenerator` | `Raw/RawImageGenerator.cs` | The whole image: 22,500 lead-in sectors from MSF 95:00:00 + program area |

**Devices** adds only transport: `RawDaoBurnEngine` drives
`IMAPI2.MsftDiscFormat2RawCD` (late-bound, like every other engine):
`PrepareMedia` → negotiate `RequestedSectorType` from `SupportedSectorTypes`
→ optional `SetWriteSpeed` → stage the generated image to a temp file →
`WriteMedia` → `ReleaseMedia`. Per Microsoft's documentation the stream's
first sector is the lead-in at MSF 95:00:00 and IMAPI seeks to the
media-appropriate start — which is precisely why DiscForge can write its own
TOC and CD-TEXT.

Sector type negotiation: PQ-16 (2368 B/sector) when no CD-TEXT is present —
it's the most widely supported raw mode; packed 96 ("cooked", 2448, IMAPI2's
default) when R–W symbols are needed; interleaved 96 as fallback.

## Using it

- **App:** Open a `.cue` in the Burn view. A CUE always burns via RAW DAO —
  exact layout is what the format is for. CDI images route to RAW when the
  planner decides they need it (mixed-mode, multi-track, non-standard gaps).
- **CLI:** `dforge build-raw <src.cue|src.cdi> <out.img> [--subcode pq|cooked|raw]`
  composes the image offline — useful for inspection and for testing the
  pipeline on any platform.

Multisession images are **refused** with an explanation: the OS raw path
writes one closed session. Faithful multisession is the future SPTI engine's
job. CD+G program-area R–W passthrough is plumbed in the generator but not
yet wired to a source format (CDI and plain BIN/CUE don't carry subcode).

## Validated here vs. assumed until hardware

Validated by tests (65 harness checks + the xunit suite), including
independent re-decoding of generated images:

- CRC-16 against the published XMODEM vector; scrambler self-inverse.
- ECC: every P and Q codeword evaluates to zero syndromes at both roots.
- TOC entries, pregap countdowns, index transitions, MCN/ISRC packing and
  cadence, CD-TEXT pack CRCs and text recovery, FLAGS→control-bit mapping.

Awaiting real-drive validation (all documented, all easy to flip):

1. **PQ-16 pad layout** — Q verbatim in bytes 0–11, P as bit 7 of byte 15
   (the MMC formatted-Q reading convention, mirrored for writing).
2. **ECC parity byte order** — r0 in the first parity row, r1 in the second.
   Syndrome tests pass for either convention; a real pressed disc read via
   `dforge` + comparison will settle it in minutes.
3. **Lead-in main channel** — zeros. Drives are expected to ignore it.
4. **Whether IMAPI2 accepts a mixed-mode raw image** — Microsoft's own
   IRawCDImageCreator refuses mixed layouts, but WriteMedia takes any
   conforming stream; the TSSTcorp will answer this.

## Hardware test checklist (in order — cheapest information first)

> **Status 2026-08-10:** rung 1–2 are **PASS on a Plextor PX-W5224A**. The direct-SPTI
> Write-Type-Raw engine wrote a full raw disc (single silent audio track), `read-raw` pulled
> it back at 2448, and `raw-verify-readback` graded it **byte-identical, main + sub-channel**.
> Transport and the base synthesis path are proven; rungs 3–7 widen coverage. The
> ready-to-run fixtures for each rung are in the next section.
>
> **Status 2026-08-11:** rung 3 (gapless two-track) burned on a Plextor PX-W5224A. Main
> channel **byte-identical across all 900 program sectors**, the gapless track-1→track-2
> boundary (LBA 500) **perfect**, and every generated Q frame carries a **valid CRC**. The
> read-back showed 13 sub-channel deltas — all traced to the **drive/media**, not the
> generator (see "Gapless rung diagnosis" below). Rung 3 is a **PASS with notes**.
>
> **Status 2026-08-11 (rung 4/5):** `meta.cue` (CD-TEXT + MCN + per-track ISRC, two audio
> tracks with a 150-sector stored INDEX-00 pregap) burned on the PX-W5224A. `inspect-raw
> --deep` on the golden decoded the album + both track titles (800 CD-TEXT packs valid), the
> MCN and both ISRCs; the burn read back (`--length 900` from LBA 0) **main-channel identical**
> with **1 timing-only** sub-channel note (sector 641, drive re-derived ancillary P/Q, address
> unchanged). The program-area **Q — carrying ISRC + MCN — is byte-faithful**. On-disc CD-TEXT
> (lead-in R–W) is proven in software; a lead-in rip is the only thing left to confirm it
> physically. Rung 4/5 is a **PASS**.

1. **`SupportedSectorTypes` probe**: insert a blank CD-R; `dforge writeinfo D:` and the burn
   log show the negotiated type / next-writable-address. ✅ PX-W5224A: raw P-W accepted,
   NWA read correctly (default −0 / raw-mode −11634 ATIP lead-in start).
2. **Plain audio CUE** on CD-R — transport, no scrambling ambiguity. ✅ **PASS** (PX-W5224A).
3. **Gapless audio CUE** (INDEX 00 continuous) — the DiscJuggler party trick. ✅ **PASS with
   notes** (PX-W5224A) — main + Q perfect; see the diagnosis below.
4. **CD-TEXT audio CUE** on CD-R — check titles on a CD-TEXT-capable player or a subcode rip.
   ✅ **PASS (software + program-Q)** (PX-W5224A, `meta.cue`) — see the rung 4/5 note below.
5. **ISRC/MCN** — rip the disc's sub-channels back and check the frames. ✅ **PASS** — the
   program-area Q (ISRC + MCN) read back byte-faithful; main channel identical, 1 timing-only.
6. **MODE1/2048 data CUE** — the full synthesis + scrambling path. Mount it. ✅ **PASS**
   (PX-W5224A, `data.cue`) — disc byte-perfect; see the rung 6 note below.
7. **Mixed-mode CUE** — the final boss. Compare against a DiscJuggler burn if one survives.

Each failure mode is isolated by this ordering: 2 tests transport, 6 tests
synthesis, and anything wrong in between is subcode layout.

## Ready-to-run burn-day fixtures

Every fixture below **composes and self-verifies in software** (`build-raw` → `inspect-raw
--deep` clean, confirmed 2026-08-10). For each disc: compose the golden, pre-flight it, burn,
read back, verify. The generic loop (blank CD-R in `D:`):

```
dforge build-raw <fixture>.cue golden.img --subcode raw
dforge inspect-raw golden.img --deep                 # exit 0 = clean
dforge burn-raw <fixture>.cue D: --engine spti       # RAW DAO-96, Write Type = Raw
dforge read-raw D: readback.bin --length <program-sectors>
dforge raw-verify-readback golden.img readback.bin --report cert.html
```

Create the payload bins once (PowerShell), then drop in the cues:

```
$z=New-Object byte[] (2352*500); [IO.File]::WriteAllBytes("a.bin",$z)
$z=New-Object byte[] (2352*400); [IO.File]::WriteAllBytes("b.bin",$z)
$d=New-Object byte[] (2048*300); for($i=0;$i -lt $d.Length){$d[$i]=($i*7) -band 0xff;$i++}; [IO.File]::WriteAllBytes("data.bin",$d)
```

**Rung 3 — gapless audio** (`gapless.cue`), program-sectors = 900:

```
FILE "a.bin" BINARY
  TRACK 01 AUDIO
    INDEX 01 00:00:00
FILE "b.bin" BINARY
  TRACK 02 AUDIO
    INDEX 01 00:00:00
```

**Rung 4/5 — CD-TEXT + ISRC + MCN** (`meta.cue`), read `--length 900` from LBA 0 (the disc's
readable program: track 1 = 500 + track 2 stored pregap = 150 + track 2 = 250; the `inspect-raw`
"1050 program sectors" is the MSF total that also counts the 150-sector pre-gap ahead of LBA 0,
which isn't readable from LBA 0):

```
CATALOG 1234567890123
TITLE "DiscForge Test Album"
PERFORMER "MaTRIX TeAm"
FILE "a.bin" BINARY
  TRACK 01 AUDIO
    TITLE "Track One"
    PERFORMER "Artist A"
    ISRC ABCDE1234567
    INDEX 01 00:00:00
FILE "b.bin" BINARY
  TRACK 02 AUDIO
    TITLE "Track Two"
    PERFORMER "Artist B"
    ISRC ABCDE7654321
    INDEX 00 00:00:00
    INDEX 01 00:02:00
```

**Rung 6 — data Mode-1** (`data.cue`), program-sectors = 300 (read `--length 300` from LBA 0):

```
FILE "data.bin" BINARY
  TRACK 01 MODE1/2048
    INDEX 01 00:00:00
```

**Rung 7 — mixed-mode** (`mixed.cue`): a data track (LBA 0–299) then an audio track
(LBA 300–799, incl. its 150-sector INDEX-00 pregap). Because `read-raw` picks ONE field mode
per pass (data = Raw, audio = UserData; Raw is illegal on CD-DA), read the two tracks
separately and verify each with `--partial` (the read-back is an intentional sub-range of the
whole-disc golden, so its uncovered tail must not count as dropouts). The clean way is
`--track N`, which pulls each track's start LBA, length and field mode from the TOC — and because
a track's TOC start is its INDEX 01, the audio track's unreadable pregap is skipped for free:

```
dforge read-raw D: data_rb.bin  --track 1
dforge read-raw D: audio_rb.bin --track 2
dforge raw-verify-readback golden.img data_rb.bin  --partial --report cert-data.html
dforge raw-verify-readback golden.img audio_rb.bin --partial --report cert-audio.html
```

> **Status 2026-08-12:** rung 7's **data track is PASS** (main byte-identical, descrambled-on-read
> as expected). The audio pass needs the `--track 2` read above (its earlier failure was a positioning
> error reading the pregap at LBA 300 — the drive can't seek there; `--track 2` starts at the audio's
> INDEX 01 instead). Manual equivalents if you prefer explicit LBAs: `--start 0 --length 300 --field
> data` for the data track, and `--start 450 --length 350 --field audio` for the audio (LBA 450 =
> track 2's INDEX 01 from `inspect-raw`).

The `mixed.cue` fixture itself:

```
FILE "data.bin" BINARY
  TRACK 01 MODE1/2048
    INDEX 01 00:00:00
FILE "a.bin" BINARY
  TRACK 02 AUDIO
    INDEX 00 00:00:00
    INDEX 01 00:02:00
```

For `read-raw --length`, use the **track** length you want to compare (e.g. `--length 500` for
a 500-sector audio track from its index-1 LBA); `raw-verify-readback` aligns golden↔read-back by
decoded disc address, so a read-back that omits the drive-owned lead-in/pregap still lines up.

## Gapless rung diagnosis (2026-08-11)

The rung-3 read-back (`gapless.cue`, `a.bin` 500 + `b.bin` 400, read 900 program sectors from
LBA 0) was compared against the golden byte-for-byte, deinterleaving each 96-byte sub-channel
into its eight P–W planes. The result rules the generator **out** as the source of the 13 deltas:

- **Main channel: 0 mismatches** across all 900 sectors — the audio payload is bit-perfect.
- **Gapless boundary (LBA 500): 0 deltas.** A multi-track/gapless generator bug would surface
  precisely at the track-1→track-2 seam; it is clean. All 13 deltas sit inside **track 1**.
- **Golden Q-CRC: valid on all 900 sectors.** Every Q frame we generated — address, track,
  index, MSF, CRC — is self-consistent end to end.
- **Golden R–W: all-zero on all 900 sectors**, as intended (no CD+G / CD-TEXT sidecar).

What the 13 deltas actually are, all **drive/media-side**:

- **4 are single-bit Q hits** (sectors 147, 217, 256, 266) where the **read-back Q fails its
  CRC** while the golden's passes — i.e. a bit was flipped *after* our valid frame, so the
  corruption is the write/read channel, not our data. (These are what the comparator grades as
  "mis-addressed": a flipped track/MSF bit with a now-stale CRC.)
- **9 are single-frame R–W hits** where the drive returned a few stray R–W bits in one subcode
  frame that we wrote as zero. R–W carries only CD+G/CD-TEXT, so these are cosmetic.

They cluster in a **1.7-second window (sectors 147–276)** with a periodic ~10-sector cadence
whose frame-phase drifts ~6 frames per event — the signature of a localized media/write
transient on this particular CD-R, not a uniform logic error (which would touch every sector).
Re-reading reproduced them identically because they are physically encoded on the disc.

**Verdict:** `RawImageGenerator` is proven correct across the gapless two-track path (valid Q
on every sector, intended-zero R–W, perfect seam); the deltas are a Plextor raw-write / media
artifact with no effect on audio fidelity or Q addressing. A cleaner blank (or a second burn)
is the way to chase a zero-delta gapless cert; the code needs no change.

## Rung 6 diagnosis (2026-08-11) — data track: the scramble-domain fix

The MODE1/2048 burn (`data.cue`, 300 sectors) first read back as **300 main-channel "mismatches"
with 0 broken EDC** — the tell that the disc is fine but the two sides were in different scramble
domains. Confirmed by staging both images and XORing bytes 12–2351 per sector: the XOR was a
**single constant across all 300 sectors** — exactly the ECMA-130 scrambler sequence — and the
read-back's header decoded to the true `00 02 00 01` (MSF 00:02:00, mode 1), i.e. **descrambled**.
Every unscrambled byte (sync, header, user data, EDC, ECC) matched the golden. So `build-raw`
composes data sectors **scrambled** (as they sit on disc), but the drive's raw `READ CD` returns
them **descrambled** — the burn is byte-perfect.

The fix is in `raw-verify-readback`, not the burn path: when a data sector's raw 2352 differs, the
comparator now **normalizes scramble state** (`CdScrambler.ScrambleInPlace` is its own inverse and
leaves the 12 sync bytes untouched) before judging. A sector that is byte-identical once normalized
is reported as **`descrambled-on-read`** (a warning, PASS-with-notes), not a `main-data` defect;
genuine corruption still fails because a real bit-flip won't match in either domain. Covered by
`RawReadbackCompareTests.A_descrambled_data_readback_is_not_a_defect` (full suite 2251 green).

## Lead-out note

DiscForge does **not** write the lead-out — the drive appends it from the TOC's A2 pointer in
the lead-in we write (that is why the PX-W5224A burn finalised and read back cleanly with only
`SYNCHRONIZE CACHE`, no explicit lead-out). If a future target drive refuses to auto-append,
the generator's TOC already carries the lead-out start, so emitting an explicit lead-out is an
additive change, not a redesign.

---

# v1.4.0 additions: the inspector and CD+G

## `dforge inspect-raw <image> [--deep]`

The validation instrument for hardware week. Feed it any raw image — a
DiscForge-generated one, a rip of a burned disc, or a bare 2352 BIN of a
pressed disc — and it reports what is actually in the bytes:

- format detection by Q-CRC voting (2368 PQ / 2448 packed / 2448 interleaved
  / 2352 main-only);
- lead-in boundary, TOC decode, lead-out position;
- sub-channel CRC health (sampled by default, every frame with `--deep`);
- MCN and per-track ISRC recovered from the Q stream;
- CD-TEXT decoded back out of the lead-in R–W, pack CRCs counted;
- per-track scramble detection (EDC-decided, since sync survives scrambling);
- data integrity: Mode 1 EDC + full ECC syndromes; XA Form 1/2 EDC where a
  duplicated subheader proves the sector is really XA; an honest
  "none (formless Mode 2)" otherwise.

Exit code 0 = clean, 1 = problems, so it scripts.

The inspector shares no assumptions with the generator — detection is by
voting, CD-TEXT by pack bytes, ECC by independent syndrome evaluation — so
DiscForge's own images passing is evidence, and a rip disagreeing is a
finding. **Before burning anything**: run it with `--deep` on any 2352 BIN
ripped from a real pressed disc. A clean ECC verdict there settles the
parity-order convention (open validation point #2) for free.

## CD+G / program-area R–W passthrough

Put a CloneCD-style `.sub` sidecar next to the CUE's BIN (`karaoke.bin` +
`karaoke.sub`, or `karaoke.bin.sub`): 96 bytes per sector, raw interleaved,
1:1 with the stored sectors. DiscForge then:

- validates the sidecar's length against the BIN at load time (a mismatch is
  refused loudly, not burned as garbage graphics);
- passes the six R–W bit-planes through to the disc while **keeping P and Q
  its own** — positioning always comes from the layout, never from a rip's
  possibly-damaged sub-channels;
- forces a 96-byte sector type at burn time, and refuses outright if the
  drive only does PQ-16 (which would silently produce a music-only disc).

Lead-in R–W remains CD-TEXT's; program-area R–W is the sidecar's.

---

# Closed-loop burn verification: `raw-verify-readback`

This is the move that makes DiscForge *outperform* ImgBurn on the burn axis
rather than merely match it. ImgBurn's "Verify" reads the burned disc back and
compares an MD5 of the **user data**; it has no way to check the sub-channel,
because it never writes one. DiscForge writes the whole disc — main channel and
96-byte sub-channel — so it can read the disc back raw and compare against the
exact bytes it sent, and say precisely what (if anything) the drive changed.

## The protocol (burn day)

1. **Compose** the golden image and keep it (use `raw` to match the interleaved-96 the engine
   burns): `dforge build-raw disc.cue golden.img --subcode raw`
2. **Pre-flight** the golden bytes before burning — every Q frame and every data
   sector's EDC/ECC: `dforge inspect-raw golden.img --deep` (exit 0 = clean).
3. **Burn** RAW DAO-96 over direct SPTI (Write Type = Raw):
   `dforge burn-raw disc.cue D: --engine spti`  (add `--simulate` first for a laser-off dry run).
4. **Read the disc back raw** to `readback.bin` (full 2448 incl. sub-channel):
   `dforge read-raw D: readback.bin --length <program-sectors>`
5. **Prove it landed** (and emit a shareable certificate):
   `dforge raw-verify-readback golden.img readback.bin --report cert.html`

Proven end-to-end on a Plextor PX-W5224A on 2026-08-10: main channel **and** sub-channel
byte-identical on read-back.

The comparator aligns the two by decoded disc address (a read-back that omits
the drive-owned lead-in, or starts at a different base, still lines up), then for
every overlapping program sector it compares the full 2352 main channel and the
sub-channel byte-for-byte and classifies any difference:

| Verdict | Meaning | Grade |
|---|---|---|
| main-data | the on-disc 2352 differs (user data or ECC changed); flagged specially when the read-back's EDC no longer validates | **defect → FAIL** |
| mis-addressed | a Q frame decodes to a different track / index / absolute address than was written | **defect → FAIL** |
| protection-loss | a *deliberately-corrupt* (LibCrypt-style) golden Q did not survive the burn bit-for-bit | **defect → FAIL** |
| dropout | the read-back is missing program sectors the golden had | **defect → FAIL** |
| sub-timing | ancillary sub-channel bytes differ but the decoded address is unchanged (a drive re-deriving P/Q timing) | warning → PASS with notes |
| (none) | every compared sector is byte-identical, main + sub | **PASS** |

Exit code is 1 on any defect, 0 otherwise, so it gates a script. Because the
"protection-loss" check keys off the golden's own intentionally-invalid Q CRC,
it needs no external metadata — the image carries the evidence. This is a
verification ImgBurn's architecture cannot perform, and it is fully exercised in
CI with synthetic golden/read-back pairs (`RawReadbackCompareTests`); the only
thing hardware adds is a real disc to read back.
