# DVD-Video structure & shrink planning (DVD Shrink parity, part 1)

The clean-room foundation for a DVD Shrink-style workflow: read the DVD-Video
structure, let the user choose what to keep (reauthor), and compute the
compression needed to fit a target disc. Everything here is structure and
arithmetic — no video is decoded, and CSS-encrypted video is never processed.
The actual re-encode is a later transcode stage (FFmpeg).

## Structure reading — `IfoReader`

Parses the `VIDEO_TS` IFO files (the public DVD-Video layout):
- **VMG** (`VIDEO_TS.IFO`) — title count and the TT_SRPT title table.
- **VTS** (`VTS_nn_0.IFO`) — per-title-set audio and subtitle stream
  attributes (codec, language, channels).
- VOB sizes per title set (the compressible video pool).

Output is a `DvdStructure`: title sets, disc-global titles with their chapters,
angles, audio and subtitle streams, and menu/video byte totals — the map the
reauthor selection and the budgeter consume.

Sources (`VideoTsSources`): a `Folder` on disk (ripped/authored DVD) or a
`FromListing` over a filesystem view of an image (UDF/ISO), so it works on both
folders and `.cdi`/`.iso` images.

## Fit planning — `BitBudget`

The arithmetic at the heart of DVD Shrink: given titles (as stream sizes) and a
target capacity (DVD-5, DVD-9, or custom), compute the video compression ratio
each title needs. Per-title modes match DVD Shrink:
- **Automatic** — share the compression to hit the target.
- **No compression** — protect a title's quality (others absorb the squeeze).
- **Custom ratio** — set a title's compression by hand.
- **Still/omit** — drop a title's video.

Only video is compressible (as in DVD Shrink); audio/subtitle sizes are fixed,
so deselecting a stream frees its whole size. A quality floor (~39%) makes the
planner report an honest "won't fit — drop a stream or split to two discs"
rather than promising impossible quality.

## CLI — `dforge dvd-info <VIDEO_TS folder>`

Shows the structure (title sets, titles, chapters, audio/subtitle streams) and
a full-disc DVD-5 shrink plan with the computed video ratio. Verified against a
synthetic DVD-9: an 8 GB payload plans to 54% video compression to hit the
DVD-5 target exactly — the same ballpark DVD Shrink reports.

## Status & boundary

Reading and planning are done and tested (18 new harness tests; 147 total).
Still to come on the transcode side: the FFmpeg re-encode that executes a plan,
reauthor output (rebuilding a playable VIDEO_TS), and rip-to-mp4/mkv. CSS
decryption is intentionally absent — DiscForge handles unprotected or
personally-authored DVD-Video only.

## Reauthor output (rebuild a playable VIDEO_TS)

`ReauthorPlanner` completes the DVD Shrink workflow: from a title selection
(which titles, which audio/subtitle streams, per-title compression mode) it
runs the budget, derives per-title encode steps, and emits the **dvdauthor
control XML** that reassembles the re-encoded VOBs into a playable VIDEO_TS.

Like the transcode layer, DiscForge orchestrates rather than reimplements: the
video is re-encoded with FFmpeg, and the DVD-Video muxing / IFO generation is
driven through `dvdauthor`. The planner is pure and fully unit-tested (10 tests
covering budget integration, stream-keep effects, and the dvdauthor XML
structure); the actual dvdauthor run happens on the user's machine.

Menus are dropped by default (the common reauthor case), so the rebuilt disc
plays the first kept title on insert.

## Native IFO writer — `IfoWriter` (structural rewrite)

`IfoWriter` is the other half of `IfoReader`: it emits the DVD-Video IFO files —
`VIDEO_TS.IFO` (the VMG) and one `VTS_nn_0.IFO` per title set — from a structural
plan, so what the reader enumerates the writer produces. It fills the format's
header, size and pointer fields coherently (VMGI_MAT / VTSI_MAT, the TT_SRPT
title table with each title's chapter and angle counts and its title-set mapping,
and each VTS's TITLE-domain audio and subpicture stream attributes), and builds
deterministically — the same plan yields byte-identical output.

Validation is by round trip, like UDF, XISO and NRG: a plan is written and read
back through `IfoReader`, asserting the title-set count, titles and streams match
(the big-endian fields included, since a byte-swap there is the classic source of
absurd chapter counts). `PlanFrom(structure)` turns a disc just read back into a
writable plan — read → write → read is stable — and `Keep(structure, numbers)`
performs a structural rewrite that drops title sets and renumbers the survivors
contiguously.

**Honest scope.** This is the *structural* IFO layer — enumeration, the title
map, and the audio/subpicture stream attributes that the reauthor selection and
`BitBudget` consume. It does **not** compose the navigation tables a hardware
player walks to actually play a disc (the PGCI program-chain / cell-playback
tables, the C_ADT cell-address table, the VOBU_ADMAP), because those must be
generated in lock-step with the muxed VOBs. Producing genuinely player-navigable
output therefore stays the job of the `dvdauthor` runner the reauthor plan
drives; `IfoWriter` adds a native, dependency-free, round-trippable writer for the
structural layer beneath it — and, being pure, the foundation a fuller navigation
emitter builds on. Nothing here decodes, encodes or decrypts video; IFO files are
unencrypted even on a CSS disc, so this stays inside the clean-room boundary.

### CLI — `dforge dvd-rewrite <VIDEO_TS folder> <out folder> [--keep 1,3]`

Reads a disc's IFO structure and re-emits `VIDEO_TS.IFO` + `VTS_nn_0.IFO` (with a
byte-identical `.BUP` backup each) into `<out>/VIDEO_TS`. `--keep` selects title
sets by number, renumbering the survivors contiguously. Structural IFOs only, per
the scope note above.

## GUI

Two launcher tiles surface this in the retro GUI:
- **DVD Shrink** (`DvdShrinkView`) — browse to a VIDEO_TS folder, see the
  structure, pick DVD-5/DVD-9, and get the fit plan with the compression ratio.
- **Protection** (`ProtectionView`) — scan an image for copy-protection
  fingerprints with preservation guidance.

Both use the same Core code as the CLI; they are buttons over the engine.
