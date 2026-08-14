# Devices layer — drive capability detection

DiscForge talks to real drives without any kernel driver. The design splits
cleanly in two:

- **Pure + testable (DiscForge.Core)**: the MMC command builders
  (`Mmc/MmcCommands.cs`), the response parsers (`Mmc/MmcParsers.cs` — INQUIRY,
  GET CONFIGURATION, mode page 2A), and the capability mapper
  (`Devices/DriveCapabilities.Build`). All of this is byte-in / facts-out, unit-
  tested against synthetic MMC responses — no hardware required.
- **Windows-only I/O (DiscForge.Devices)**: `Spti/SptiDevice` (user-mode SCSI
  pass-through via `DeviceIoControl` + `IOCTL_SCSI_PASS_THROUGH_DIRECT`) and
  `DriveDetector` (enumerate optical drives, run the three commands, hand the
  bytes to the pure mapper).

## Why no kernel driver

The original DiscJuggler shipped a kernel driver (`pfc.sys`), which is exactly
what modern Windows (driver signature enforcement, HVCI) rejects. SPTI is a
fully user-mode API: open `\\.\E:` with `CreateFile`, send raw MMC CDBs with
`DeviceIoControl`. Raw pass-through generally needs an elevated process, but no
driver install and no HVCI conflict.

## Capability detection flow

1. **INQUIRY (0x12)** → vendor / model / firmware, confirms it's an optical unit.
2. **GET CONFIGURATION (0x46)** → current + supported media profiles (CD-ROM,
   CD-R, DVD-ROM, DVD±R, BD-ROM, BD-R/RE, …) and write features (CD Track at
   Once 0x2D, CD Mastering / SAO+RAW 0x2E).
3. **MODE SENSE(10) page 0x2A** → CD-era fidelity flags: subchannel read, C2
   error pointers, Mode 2 forms, multisession, buffer-underrun protection.

The mapper then composes `DriveCapabilities`, taking the optimistic union for
read and the conservative intersection for write. Features light up per drive:
a 2024 LG reports data/ISO/BD burning and **no** RAW DAO; a vintage Plextor
Premium unlocks the full RAW toolkit. DiscForge never offers a burn mode a
drive hasn't demonstrated.

## Confidence notes (validated in code, pending on hardware)

The parsers are unit-tested for self-consistency against synthetic responses,
and the modern-vs-vintage mapping is covered. Two bit-level facts are documented
per MMC spec but should be confirmed against real drives:

- **CD Mastering RAW bit** (feature 0x2E, data byte 0 bit 3) → `RawDao96`.
- **Mode page 2A** exact bit offsets for C2 / subchannel across vendor quirks.

These are flagged in the source. A real drive (or a captured GET CONFIGURATION /
mode-page dump) is the next validation input for this layer — same "confirm
against ground truth" discipline used for the CDI parser.

# Burn engine

Burning splits into a pure **planner** (Core) and platform **engines** (Devices):

- `Burning/BurnPlanner` (Core, pure, tested): given a parsed image and a drive's
  `DriveCapabilities`, decides the method or refuses. A plain single data track
  on any writer → IMAPI2. Anything mixed-mode, multisession, or with audio →
  RAW DAO-96, gated behind `RawDao96`. A non-writer, or a modern drive asked for
  RAW, is refused with a clear reason rather than a doomed attempt.
- `Burning/Imapi2BurnEngine` (Devices, Windows): standard CD/DVD/BD data burns
  through the built-in IMAPI2 stack — no driver, the everyday path. Cooks the
  track's user data to a temp ISO and streams it to the recorder via COM.
- `Burning/RawDaoBurnEngine` (Devices, Windows): the byte-faithful RAW DAO-96
  path — the DiscJuggler-defining capability. Command sequence understood
  (MODE SELECT write parameters, SEND CUE SHEET, WRITE(10) raw sectors, CLOSE),
  but must be built against real RAW-capable hardware; stubbed for now and only
  ever selected when the drive reports support.

Neither engine can run in CI (no drive), so the planner carries the tested logic
and the engines are structured so a failed burn raises an exception — never a
false success.


## Write methods — and a correction

There are three, not two:

| Method | Path | Works on |
|---|---|---|
| `Imapi2Data` | IMAPI2 Data (session-at-once) | any writer — data CD/DVD/BD |
| `Imapi2TrackAtOnce` | IMAPI2 TrackAtOnce | **any CD writer — audio CDs** |
| `RawDao96` | native SPTI MMC | RAW-capable drives only |

**The correction:** the planner used to claim audio required RAW DAO-96, so it
refused audio burns on ordinary drives. That was wrong. IMAPI2's track-at-once
path is how Windows itself burns audio CDs and works on **any** CD writer.

What genuinely needs RAW DAO is narrower than "audio":

- **exact gaps** — a gapless mix, or a copy reproducing a source disc's gaps
  (TAO always writes the standard two seconds);
- **mixed-mode** (audio + data on one disc);
- **multisession**;
- **CD-TEXT and CD+G**, which live in the R-W sub-channel.

So a plain audio compilation burns on a modern drive; a byte-faithful audio *copy*
still wants a Plextor.

## Burn jobs (Core/Burning/BurnJob.cs)

`BurnPlanner` answers "can this image go on this drive, and how?".
`BurnJobPlanner` layers the user's *request* on top: destination (drive or image
file), actions (Test/Write/Verify), method override (Auto/TAO/RAW), and copies.
It validates and expands the job into ordered `BurnStep`s:

- Nothing ticked, or copies < 1 -> refused.
- RAW forced on a drive without RAW DAO-96 -> refused, naming the drive.
- TAO forced for an image that needs RAW (mixed/multisession/audio) -> refused.
- Test or multiple copies to an image file -> refused (they're disc concepts).
- Ordering: an optional Test runs once up front, then Write/Verify per copy.

All pure and unit-tested; no hardware needed to prove the decisions.

An **image file destination works fully with no hardware**: Write copies the
image, Verify compares it against the source with `CdiComparer` (structure +
per-track CRC-32). That exercises the whole job pipeline — plan, steps, event
log, verification — on any machine.


## Burning to several drives at once

The classic tools listed destinations with **checkboxes**, not radio buttons, for
a reason: you could tick every drive and burn them simultaneously. Duplication was
a first-class use case, and DiscForge now does the same.

`BurnJobPlanner.PlanAll(image, MultiBurnJob)` plans **each destination
independently** and returns a `MultiBurnPlan`:

- A destination that can't honour the job gets a `Refusal` and **sits it out** —
  one incapable drive must not sink the whole run. You're told which and why.
- The job as a whole is only refused when *nothing* can run (and then every
  reason is listed).
- The same drive or the same image file twice is refused: two writers racing for
  one target would corrupt it.
- Copies multiply per destination — 2 drives x 2 copies = 4 discs.
- Drives and an image file can be targets of the same job.

Execution runs the destinations **concurrently**: each gets its own burn engine
and its own handle on the source image (they must never share a `Stream`), and one
failing doesn't abort the others — the log gives a per-destination verdict and the
summary counts successes and failures. Progress is the mean across destinations,
since they run at their own speeds.

**Untested against multiple real drives** — the planning is pure and unit-tested,
but simultaneous burning needs more than one writer to prove.


## Copying a disc

`CopyPlanner.Plan(sourceToc, CopyJob)` plans a disc-to-disc copy: read the
source, then burn what was read to one or more destinations (drives and/or an
image file).

**The point of planning the whole copy up front** is that it can be refused
*before a single sector is read*. Reading an audio CD takes minutes; discovering
only afterwards that the burner can't write audio back is the worst possible time
to find out. The burn method depends only on the image's *shape* — track count,
sessions, whether there's audio — and the shape is known from the source's TOC,
so `BurnPlanner.Plan(ImageShape, drive)` can answer before the image exists.

Practical consequence on typical modern hardware: **an audio CD copy is refused
immediately** ("requires RAW DAO-96, which <drive> does not support") while a data
disc copy plans fine. Copying an audio disc to an *image file* always works —
archiving has no drive limits.

### Single-drive copying

Source and destination may be the same drive: the disc is read to an image, then
you're asked to swap it for a blank. `CopyPlan.RequiresDiscSwap` flags it.

### Why not "on the fly"?

Copying source-straight-to-burner with no intermediate image is deliberately not
implemented. It was a disk-space optimisation from an era when gigabytes cost more
than discs. Today it is strictly worse:

- a reader that stalls retrying a marginal sector starves the burner;
- you lose the chance to verify the image, or salvage bad sectors, before
  committing media;
- it can't work at all with a single drive.

An intermediate image is cheap and safer. (Our IMAPI2 engine already stages
through a temp file regardless, because IMAPI2 wants a stream it can pull from.)


## Verify means read-back, not re-burn

Each `BurnStepKind` is a different operation, and they must not be treated alike:

| Step | What it does |
|---|---|
| `Write` | burns the image |
| `Verify` | **reads the disc back** and compares it against the source, byte for byte |
| `Test` | a simulated burn — **not available** on the IMAPI2 data path, and refused rather than faked |

This was got wrong at first: the view called the burn engine for every step, so
**Verify re-burned the disc** instead of checking it. On a written disc the media
check caught it; on a blank one it would have silently burned over it. Verify now
reads the disc's own TOC (rather than assuming it matches the source — if the burn
went wrong, that difference is exactly the point), then compares through
`DiscSectorStream` and reports the first differing byte.

A burner may pad the last sectors, so only the bytes actually written are compared.

## Media state before burning

`DriveCapabilities.Disc` carries READ DISC INFORMATION (0x51): blank, appendable
or finalised, and whether it's erasable. `MediaProfile` alone cannot tell you —
a blank DVD+R DL and a full one are both `DvdPlusRDl`.

Without it, burning to a full disc fails deep inside IMAPI2 with
`0xC0AA0402 "the requested operation is only valid with supported media"`, which
sounds like a software fault and isn't. The destination list now shows
"DvdPlusRDl — finalised and write-once — it cannot be reused" before you press
anything, and the engine pre-flights the disc (supported? blank? big enough?)
the way the reader probes before ripping.
