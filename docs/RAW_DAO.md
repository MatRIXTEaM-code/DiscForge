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

1. **`SupportedSectorTypes` probe**: open the app, insert a blank CD-R, burn
   log shows the negotiated type. Note what the TSSTcorp and the two
   MATSHITAs each report.
2. **Plain audio CUE** (2 tracks, standard gaps, no CD-TEXT) on CD-RW —
   PQ-16 path, no scrambling ambiguity. Does it play? Does the gap land?
3. **Gapless audio CUE** (INDEX 00 continuous) — the DiscJuggler party trick.
4. **CD-TEXT audio CUE** on CD-R — check titles on a CD-TEXT-capable player
   or a rip with subcode.
5. **ISRC/MCN** — rip the disc's sub-channels back and check the frames.
6. **MODE1/2048 data CUE** — the full synthesis + scrambling path. Mount it.
7. **Mixed-mode CUE** — the final boss. Compare against a DiscJuggler burn
   if one survives.

Each failure mode is isolated by this ordering: 2 tests transport, 6 tests
synthesis, and anything wrong in between is subcode layout.

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
