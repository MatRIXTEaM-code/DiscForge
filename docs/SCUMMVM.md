# ScummVM interoperability — findings and design

ScummVM is a clean-room reimplementation of classic adventure-game engines: it
runs a game from the game's **own data files**, driven by ScummVM's independently
written engine. That makes it a natural downstream target for DiscForge — DiscForge
images and extracts the disc, ScummVM plays the extracted result — and it sits
squarely inside DiscForge's clean-room boundary (extracting the user's own data,
hashing, and transcoding; nothing is circumvented).

## What ScummVM actually consumes

ScummVM does **not** read `.bin/.cue`, `.iso`, or `.chd` disc images directly. It
runs from a **folder on disk** containing:

1. **The game's data files** — extracted from the disc's data track (the exact
   set per game is listed on the ScummVM wiki).
2. **Each CD audio track as its own file**, named `trackNN` / `track_NN` / `trackN`
   (e.g. `track02.flac`), in one of **FLAC, OGG, MP3, or M4A**. Uncompressed WAV is
   *not* accepted for CD-audio substitution. Track numbers keep their disc numbering:
   on a typical mixed-mode game track 1 is the data track, so audio files start at
   `track02`.

It **identifies** a game with its *Advanced Detector*: for each key data file it
computes the **MD5 of the first 5000 bytes** (the engine-overridable default,
`md5Bytes`) together with the file's **exact byte size**, and matches that pair
against its built-in detection tables (`ADGF_TAILMD5` hashes from the end instead;
`ADGF_MACRESFORK` hashes a Mac resource fork).

## Mapping to DiscForge's existing building blocks

| ScummVM need | DiscForge today | Gap |
|---|---|---|
| Extract data-track files to a folder | `Files/ImageBrowser.cs` — `List(path)` / `Extract(path, files, outDir, …)` handles ISO9660, UDF, and raw bin/cue | none — direct fit |
| Enumerate CD audio tracks + their sector ranges | `Cue/CueSheet.cs` (`CueTrack.Type == AUDIO`, `Msf`), `Cue/BinCueMerge.cs` (`Split`, `TrackStartSector`), `Chd/ChdExtractor.cs` (`ExtractCd`) for CHD sources | none |
| Audio track → PCM/WAV in-process | `Audio/WavWriter.cs` (`Write`/`ToBytes`); pattern already exists in `Cdi/CdiExtractor.ExtractAudioToWav` | small: a bin/cue-track→WAV helper (byte-swap + `WavWriter`) |
| Encode WAV → **FLAC/OGG** | no in-process FLAC/OGG encoder (`Chd/ChdFlac.cs` is a *decoder*); `Transcode/FfmpegRunner.cs` can shell out but `TranscodePlanner` only builds *video* arg-vectors today | **the real dependency** — see below |
| MD5 + size fingerprint | `Files/ImageChecksums.cs` computes whole-stream MD5/SHA; `Util/Crc32.cs` etc. | small: a **bounded** (first-5000-byte) MD5 helper — trivial with `IncrementalHash` |
| Hash → known-title matching pattern | `Dat/DatFile.cs` (redump DAT: `ByCrc`/`BySha1`, `DatMatch`) is the model to mirror | a ScummVM-style signature table if we want naming, not just fingerprints |

## Proposed features

### A. `scummvm-detect` (small, fully in-process, no external deps)

Walk a disc image's data files (via `ImageBrowser.List`), and for each emit the
ScummVM fingerprint: `filename`, `size`, and `md5-of-first-5000-bytes`. Output as
plain text/JSON the user can paste into a ScummVM wiki/table lookup. This is a
natural sibling of the existing `identify` and `scan-protection` *detection*
commands and needs only a new bounded-MD5 helper plus a CLI arm. It does **not**
identify the title by itself (that needs ScummVM's tables), but it produces the
exact bytes ScummVM's detector uses, which is the useful, verifiable half.

### B. `scummvm-export` (the full "make a ScummVM folder" flow)

1. `ImageBrowser.Extract` the data-track files into `<out>/`.
2. For each `AUDIO` track (from `CueSheet`/`BinCueMerge`, or `ChdExtractor` for a
   CHD), write `trackNN.wav` in-process, preserving disc track numbers.
3. Transcode each `trackNN.wav` → `trackNN.flac` (or `.ogg`) and delete the WAV.

Step 3 is the crux: **FLAC/OGG output requires an encoder DiscForge doesn't have
in-process.** Two honest options:

- **Shell out to ffmpeg** via the existing `FfmpegRunner` (extend
  `TranscodePlanner` to emit `-c:a flac` / `-c:a libvorbis` audio arg-vectors).
  Fast to build; adds a runtime dependency on the user having ffmpeg (DiscForge
  does not bundle it — same posture as the existing `transcode` command).
- **Write a clean-room FLAC encoder** in-process (mirrors the existing
  `ChdFlac` *decoder*). No external dependency and fully self-contained, but a
  substantial amount of work.

Recommended: ship **A** first (self-contained, testable, immediately useful), and
do **B** with the ffmpeg path, leaving an in-process FLAC encoder as a later
"no-dependency" upgrade. A GUI tile (`CdrwinLauncher._tiles` → a new
`Views/ScummVmView.cs`, modelled on `Views/BrowseView.cs`) can front either.

## Clean-room note

Everything here is extraction, hashing, and transcoding of the user's own disc.
ScummVM itself is the canonical example of the same clean-room principle DiscForge
follows — it reinterprets a game's data through an independent engine rather than
defeating anything — so the interop is a good philosophical fit, not just a
technical one.
