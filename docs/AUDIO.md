# Audio CDs

DiscForge could already pull audio *off* a disc (`extract` writes WAVs). This
is the other half: building a Red Book audio CD from WAV files.

```
dforge create-audio mixtape.cdi track1.wav track2.wav track3.wav
dforge create-audio album.cdi *.wav --gapless      # no gaps between tracks
dforge create-audio safe.cdi *.wav --74            # refuse to exceed 74 minutes
```

Then burn it like any other image (Burn view, or the CLI once written to disc).

## Red Book rules this enforces

- **44.1 kHz / 16-bit / stereo only.** Anything else is refused, naming what the
  file actually is. **No resampling** — a wrong conversion applied silently is
  worse than a refusal, and dedicated tools do it better.
- **Every track is 2352 bytes/sector.** Audio that doesn't fill its last sector
  is padded with silence and the padding is reported; a CD has no partial sectors.
- **Track 1 carries the mandatory 150-sector (2 second) lead-in gap.** Later
  tracks default to the customary 150; `--gapless` sets them to 0 for a
  continuous mix.
- **Capacity is checked.** 74 min = 333,000 sectors; 80 min = 360,000. Over-length
  compilations are refused with the actual running time. Over 74 minutes warns
  that 80-minute media is needed.
- **Post-gap** (`--postgap [sectors]`, default 150 = 2s) appends silence after
  the last track, before the lead-out. Some third-party images omit the post-gap
  the standard expects, and a few players clip the final moment of audio as a
  result. It counts as part of the track's length, so later tracks move
  accordingly.
- Max 99 tracks. Tracks under 4 seconds warn (Red Book minimum; some players skip
  them).

## WAV parsing

RIFF is a **chunked** format: the `data` chunk is not at a fixed offset. Real
files carry LIST/INFO, fact and cue chunks first, so the chunk list is walked.
Assuming data starts at byte 44 works until it doesn't — there's a test for it.

## MP3 — deliberately not supported

Decoding MP3 needs a decoder, and an audio-transcoding stack inside a disc imager
is the wrong shape: it's a domain where EAC, dBpoweramp and foobar2000 are far
better. Convert to WAV first; DiscForge takes it from there.

## DAE jitter correction (Core/Audio/JitterCorrection.cs)

CD-DA sectors carry **no header**. A data sector states its own address; an audio
sector doesn't. So when a drive is asked for sector N it may return audio
beginning a few samples either side of N, and the error varies between reads.
Concatenating chunks blindly gives clicks at the joins, and drift across a track.
This is why EAC and cdparanoia exist.

The fix: read chunks that **overlap**, then find where the new chunk truly lines
up against the tail of the previous one, and stitch there rather than where the
drive claimed.

- `Align(reference, candidate)` returns the jitter the drive applied — positive
  means it returned audio from later than asked.
- **Silence and constant tones return `Confident: false`.** Every offset matches
  in silence, so any answer would be invented; the caller keeps the drive's own
  positioning rather than acting on a guess.
- Ambiguous or unrelated audio is likewise not confident — no false alignments.
- `MinimumOverlapSamples(maxOffset)` — sliding +/- maxOffset costs **2x** maxOffset
  of the buffer, so the overlap must exceed that with a window to spare.

Two bugs the Python reference caught before any C# ran:
1. **Sign inversion** — a drive reading 1 sample early reported as `-1`.
2. **Window bounds** — taking only *one* maxOffset of slack (the obvious
   mistake) leaves negative jitter untestable, so it silently never gets detected.

Validated end to end: a 19,300-sample track read in overlapping chunks with the
drive jittering differently on every read reassembles **byte-identically**, while
naive concatenation of the same reads is provably corrupt.

## Validated

- Red Book arithmetic (capacities, bytes/sector, sector rounding, track layout)
  checked independently.
- **Round trip**: WAV -> CDI -> `ExtractAudioToWav` -> WAV, samples byte-identical.
- Refusals proven for wrong sample rate / channels / bit depth, over-length
  compilations, empty compilations and missing files.

## Not done yet

- **Never burned to a real audio CD.** The image structure is tested, but audio
  burning needs RAW DAO, whose engine is still a stub awaiting capable hardware.
- No CD-TEXT (album/track titles) — that needs sub-channel writing.
- No ISRC/UPC codes.
- No gap detection or track splitting from a single long WAV + cue.
