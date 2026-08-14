# Redump2CDI — what DiscForge can learn from it

## What Redump2CDI is

Redump2CDI (darcag3nt, 2023) converts **Redump BIN/CUE rips of Dreamcast
CD-ROMs** into **DiscJuggler CDI** images. Its scope is specifically the
CD-ROM Dreamcast titles that Redump catalogues — MiL-CDs (Sega's own
music-CD-plus-game hybrids) and unlicensed self-boot software (e.g. Action
Replay) — *not* GD-ROMs. The reason it exists: Redump's BIN/CUE is an archival
form that most burning/mounting/emulator tooling won't accept for these discs,
whereas CDI is the format that ecosystem (DiscJuggler, GDEMU, emulators) reads.
Recent versions also tolerate cue commands they don't need (`FLAGS`,
`CDTEXTFILE`, `PERFORMER`, …) and handle `MODE1/2352` tracks.

So Redump2CDI is, in essence, a **BIN/CUE → DiscJuggler-CDI converter tuned for
the Dreamcast two-session self-boot layout.**

## What DiscForge already has

DiscForge is not missing the CDI-writing capability — it has a full stack:

- `Cdi/CdiWriter.cs` — writes DiscJuggler CDI, **multi-session capable** (its
  `Write` takes a list *of sessions*, each a track list).
- `Convert/IsoConverter.cs` (`IsoToCdi`), `Convert/MdsConverter.cs`
  (`MdsToCdi`, which already fans tracks across sessions), and
  `Cdi/CdiConverter.cs` (`BinCueToCdi`) — plus the `convert` CLI command
  advertising `CDI <-> BIN+CUE / ISO / GDI / NRG`.
- `Cue/CueSheet.cs` already parses `FLAGS`, `PERFORMER`, pregaps and the
  `MODE1/2352` track type, so the cue-tolerance Redump2CDI added is largely
  covered here already.
- Dreamcast-specific tooling: `Gdi/DreamcastScramble.cs`, `docs/DREAMCAST_*.md`,
  GD-ROM GDI reading/rebasing.

## The one real gap to pull from it

`Cdi/CdiConverter.BinCueToCdi` writes a **single-session** CDI — its own doc
says *"All tracks go into one session (CUE has no session info)."* That is
exactly the case Redump2CDI handles specially: a Dreamcast CD-ROM self-boot disc
is a **two-session** disc — a low-density first session, then a high-density
second session that begins the bootable game area — and a CDI that actually
boots must preserve that split. A plain cue sheet doesn't carry session
boundaries explicitly, but for these discs the boundary is inferable (the
Redump cue's `REM` session markers, and/or the second data track that opens the
high-density area at the Dreamcast start LBA).

**Proposed enhancement (in-scope, faithful conversion):** teach `BinCueToCdi`
to recognise a Dreamcast two-session Redump layout and emit a two-session CDI
that mirrors it, instead of collapsing everything into one session. Concretely:

1. Read Redump `REM` session hints (Redump cues mark the second session) — or
   fall back to detecting the high-density data track — to find the session
   split.
2. Group tracks into session 1 / session 2 and hand both to the existing
   multi-session `CdiWriter.Write` (the writer already supports this; only the
   *grouping* is missing).
3. Preserve each track's mode/size and pregap exactly as today.

This is faithful **container** conversion — it repackages the disc's existing
structure so it mounts/burns, using DiscForge's own CDI writer. It is *not*
self-boot synthesis.

**Status: implemented.** `CueSheet` now parses `REM SESSION n`, `CdiConverter.BinCueToCdi`
groups tracks into sessions and emits a two-session CDI, and both a `milcd-to-cdi`
CLI command and a GUI tile drive it. The inter-session gap is **settled at 11400
sectors** (`CdiConverter.MultisessionGap`): first-session lead-out (6750, 01:30:00)
+ next-session lead-in (4500, 01:00:00) + the 150-sector (00:02:00) track pregap —
the layout DiscImageCreator/Redump use for a multisession CD (lead-out + lead-in are
11250; the recorded data begins 150 sectors further). It is added to the running LBA
before each new session and is overridable per rip. A pressed reference image would
only be needed to chase a per-title anomaly, not to confirm this standard layout.

## Clean-room boundary

Stay on the preservation side, consistent with the existing line in
`DreamcastScramble.cs` ("does NOT build a self-boot (MIL-CD) disc"): DiscForge
should **faithfully preserve** an already-self-boot disc's two-session layout in
the CDI container, but must **not** synthesize a bootstrap / IP.BIN or otherwise
*create* self-boot capability a source disc didn't have. Redump2CDI's own job is
the former (it repackages discs that are already self-boot), so mirroring that
scope keeps the feature firmly in-bounds. MiL-CD and unlicensed titles the user
already owns and has imaged are fine to transcode between container formats.

## Recommendation

No new writer is needed — DiscForge already writes multi-session CDI. The single,
well-scoped win is **session-aware `BinCueToCdi`** (Dreamcast two-session
detection → the existing multi-session `CdiWriter`), which would let DiscForge do
natively what Redump2CDI does, using code it already has. A `--dreamcast` /
`--sessions` flag on `convert` (or auto-detection from Redump `REM` markers) is
the natural surface.
