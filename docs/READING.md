# Read mode (disc -> image)

The other half of a disc tool: pull a disc into a `.cdi`. As everywhere else,
the decisions are pure and tested; only the transport touches hardware.

## Pieces

- `Core/Mmc/TocParser.cs` (pure) — parses MMC READ TOC/PMA/ATIP (0x43) format 0.
  The TOC does NOT carry track lengths: each track runs to the start of the next,
  and the last to the lead-out. Deriving that is the parser's real job.
  CONTROL bit 2 (0x04) marks a data track; bits 0/1/3 (pre-emphasis, copy
  permitted, four-channel) must not be mistaken for it.
- `Core/Reading/ReadPlanner.cs` (pure) — TOC + drive capabilities -> a plan.
  It is **media-aware**: `DriveCapabilities.MediaProfile` carries what the drive
  reported was actually in it (CD-ROM / DVD-ROM / BD-ROM), not just what the
  drive supports.
  - **DVD/BD: always cooked 2048.** Raw 2352 sectors are a CD concept; DVD and BD
    have no raw form, so a raw read is refused up front rather than failing at
    LBA 0 with "invalid field in CDB". (Found with a real DVD.)
  - Capability is checked against the media present, so a DVD-only drive reading
    a DVD isn't refused for lacking CD read.
  - Data-only disc: cooked, 2048 bytes/sector. Fast, works everywhere.
  - Any audio present: raw, 2352 bytes/sector for every track. CD-DA has no
    cooked form, so this is forced regardless of preference.
  - `preferRaw` reads data tracks raw too (keeps Mode 2 form data intact).
  - Refuses a drive that reports no CD read capability; skips zero-length tracks
    with a warning; warns that a mixed-mode disc needs a RAW burner to write back.
- `Core/Mmc/MmcCommands.cs` — `ReadToc`, `Read10` and `ReadCd` CDB builders.

  **Command and field selection matter, and drives enforce both:**
  - Cooked 2048-byte data -> **READ(10) (0x28)**. Unambiguous and universally
    supported.
  - Raw 2352 / audio -> **READ CD (0xBE)**, the only command that returns them.

  Using READ CD for cooked data with Expected Sector Type = "Any" and a
  user-data-only field selection is rejected by real drives with
  "Illegal request: invalid field in CDB" — the drive cannot infer which bytes to
  strip. (Found on the first real disc; the spec permits the combination, but
  hardware disagrees.) For raw data reads, a drive that refuses "Any" is retried
  once with an explicit Mode 1 sector type.

  **CD-DA needs a different field selection from raw data.** Audio sectors have no
  sync, header, sub-header or EDC/ECC, so requesting them (0xF8) is an illegal
  combination and is rejected. Audio must be read with User Data only (0x10),
  which returns the full 2352-byte audio frame. Raw *data* sectors do have all
  those fields, so 0xF8 is correct there.
- `Devices/Reading/DiscReader.cs` — SPTI transport. Streams straight into
  `CdiWriter` via `DataWriter`, so a 700 MB rip never lands in memory.
- `App/Views/ReadView.cs` — drive picker, TOC display, per-track plan grid,
  progress, and the event log.

## Jitter correction (opt-in, audio only)

`ReadOptions.CorrectJitter` reads overlapping chunks and aligns them by
correlation instead of trusting the drive's positioning — see docs/AUDIO.md for
why CD-DA needs this and data tracks don't.

The subtlety in wiring it in: corrected output is **sample-accurate, not
sector-aligned**. A +3 sample correction means the stream no longer sits on
2352-byte boundaries — but the CDI track has declared exactly
`LengthSectors x 2352` bytes and the writer verifies that count. So the loop
tracks bytes emitted, reads from wherever that lands (a 2-sector overlap absorbs
the remainder, giving 1176-1764 samples of comparison window against a 128-sample
minimum), and stops precisely on the declared length.

When a chunk can't be aligned confidently — silence, where every offset matches —
the drive's own positioning is kept rather than acting on a guess.

Costs roughly one extra sector-read per chunk (~10%). A drive with "accurate
stream" reports offset 0 throughout and loses nothing but that.

## Pre-flight probe

`DiscReader.Probe` test-reads a single sector of each planned track before a rip
commits to anything. If the drive can't honour the plan it says so immediately,
with a reason, instead of leaving a truncated part-file behind.

This matters because **not every drive can read raw 2352-byte sectors** — many
modern ones can't — and there is no reliable capability bit to ask beforehand.
The probe turns that into an instant, explained refusal.

## Damaged discs: retry, and opt-in salvage

A single unreadable sector used to kill an entire rip. Now:

- A failing 27-sector chunk is re-read **one sector at a time**, so one bad sector
  costs one sector, not 27.
- Each sector is retried (`ReadOptions.RetriesPerSector`, default 3) — marginal
  sectors frequently come back on a later attempt.
- If a sector still won't read, the default is to **stop**. A dump with silent
  holes is worse than no dump.
- `ReadOptions.ContinueOnError` (opt-in) salvages the rest: unreadable sectors are
  zero-filled and **every one is recorded** in `ReadReport.BadSectors`.
  `ReadReport.Complete` is false, and the UI says so in red. A partial image must
  never be mistaken for a clean one.

## Partial images are never published

A CDI's version magic lives in a trailer written *after* all the track data. A rip
that dies part-way therefore leaves a file that looks like an image but has no
trailer — and every tool, ours included, then reports a baffling "not a CDI image:
unknown version magic 0x00000000" for what is really a truncated read.

So a rip writes to `<name>.cdi.partial` and is renamed to `.cdi` only once the
trailer is down. A failed read deletes the partial and says plainly that nothing
was written. `CdiParser` also now names this cause when it sees an all-zero magic,
rather than leaving people hunting a format problem that doesn't exist.

## Failure handling

Read errors are surfaced, never silently zero-filled — a dump you can't trust is
worse than no dump. `SptiResult.Describe()` decodes SCSI sense data, so failures
read as "no disc in the drive" or "uncorrectable read error — the disc is damaged
or dirty here" rather than a hex code.

## Validated

- `docs/reference/toc_parse.py` — TOC parsing across data/audio/mixed discs,
  truncated responses, and the control-bit confusion cases.
- READ CD / READ TOC CDB bit-packing (24-bit transfer length, field flags,
  sector-type bits) checked against the MMC layout.
- TOC -> plan -> CDI -> parse round-trip, proving a planned rip produces an image
  our own parser reads back correctly.

## Encrypted discs (CSS) — out of scope, by choice

Commercial DVD-Video is CSS-encrypted. A drive will hand over the unencrypted
parts (UDF structures, IFO files) and then refuse the scrambled VOB sectors with
ASC 0x6F — "read of scrambled sector without authentication".

DiscForge **does not implement CSS authentication or decryption**, and will not.
Circumventing an effective technological protection measure is an offence under
CDPA s296ZA-ZF in the UK (and equivalents elsewhere), and *making* such a tool is
covered by s296ZB — the framing "it's just a feature" doesn't change that. This
is the same boundary that kept the project clean-room: no disassembly of the
original binaries, no licensed bootstrap, no protection-defeat profiles.

The sense decoder reports 0x6F accurately rather than calling it a damaged disc,
because mislabelling it sends people hunting a fault that doesn't exist.

Unencrypted discs — your own data, homebrew, audio CDs, pressed data discs — read
normally.

## Not done yet

- **Never run against real hardware.** The command shapes follow the spec and the
  logic is tested, but the first real disc is the real test.
- Multisession discs are read as a single session.
- No subchannel capture (needed for CD-TEXT/ISRC and for exact audio pregaps).
- No C2 error pointers / re-read on error — a damaged disc fails rather than
  retrying. AccurateRip-style paranoia is future work.
- Sessions/pregaps are not reconstructed from the raw TOC (format 2).
