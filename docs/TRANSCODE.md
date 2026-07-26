# Transcode pipeline (FFmpeg)

Executes a DVD Shrink-style shrink plan — and general video conversion — by
driving an installed FFmpeg. DiscForge does **not** bundle FFmpeg (keeping its
own distribution free of FFmpeg's separate licensing); it locates an installed
`ffmpeg` on PATH or a configured path, and reports clearly if none is found.

## Layers

- **`TranscodePlanner`** (pure) — turns a `BitBudget.TitlePlan` into concrete
  encode parameters. Target video *size* → target *bitrate*
  (bits = bytes×8; bitrate = bits ÷ duration). Builds the FFmpeg argument
  vectors: single-pass, two-pass ("Deep Analysis"), or stream-copy when no
  compression is requested. Stream selection (keep specific audio/subtitle
  indices) and container/codec mapping (H.264/HEVC/MPEG-2; mp4/mkv/DVD) live
  here. No process is launched, so it is fully unit-tested.
- **`FfmpegRunner`** — executes the vectors, parses FFmpeg's stderr progress
  (time/fps/speed → percent against known duration), supports cancellation, and
  reports pass-by-pass. The process invocation is injectable (`IProcessRunner`)
  so the orchestration and parsing are testable with a fake; the real runner is
  a thin `Process` adapter. `Locate()` finds ffmpeg on PATH or common paths.

## CLI — `dforge transcode <in> <out> [options]`

```
--ratio <0.05-1.0>      video compression ratio
--target <dvd5|dvd9|N>  fit video to this size (with --orig-video)
--duration <seconds>    input duration (required for bitrate math)
--orig-video <bytes>    original video size (for ratio→bitrate / target fit)
--codec <h264|hevc|mpeg2>
--two-pass              Deep-Analysis 2-pass encode
--keep-audio <i,j>      audio stream indices to keep
--dry-run               print the ffmpeg command(s) without running
```

Audio is stream-copied (DVD Shrink never re-encoded audio); only video is
compressed. A ratio of 1.0 stream-copies the video (no quality loss).

## Validation

The command construction, bitrate math, and progress parsing are covered by 22
harness tests (169 total). Beyond that, the full pipeline was exercised against
real FFmpeg 6.1: single-pass and two-pass encodes of a real clip produced valid,
correctly-sized H.264 output, with live progress parsed from FFmpeg's own
stderr. So this layer is validated end-to-end, not just at the logic level.

## Boundary

CSS-encrypted video is never processed — transcode operates on unprotected or
personally-authored input only, consistent with DiscForge's clean-room stance.
