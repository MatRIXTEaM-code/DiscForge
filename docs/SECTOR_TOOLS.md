# Sector tools (v1.5.0) — the CDRWIN 4 inheritance

CDRWIN's two most-loved diagnostics, rebuilt on DiscForge's foundations:
the Sector Viewer and Extract Sectors. Both sit on a unified sector layer
(`SectorAccess` in Core) that gives random access to ANY image DiscForge
understands, with the addressing subtleties handled once:

- **.cdi** — mapped through the descriptor: LBA space has gaps between
  sessions, the file doesn't, and per-track stored sizes differ. Session and
  track come back with every read.
- **.iso** — 2048-byte sectors, LBA = file index.
- **raw DAO images** (2368/2448, detected by Q-CRC voting) — the lead-in
  shifts everything; addressing accounts for it.
- **bare 2352 BINs** — main channel only.

Addresses everywhere: a plain number is an **LBA**, `mm:ss:ff` is absolute
MSF (`95:00:00`+ reaches into a raw image's lead-in), `+N` is a raw file
index for when you mean exactly that sector.

## view-sector

```
dforge view-sector <image> <addr> [--count N] [--descramble]
```

Annotated hex+ASCII of any sector: identity line (file index, LBA, MSF,
track/session where known), region annotations for raw sectors (sync,
header, user data, EDC, pad, ECC P/Q), automatic mode and scramble detection
with inline EDC/ECC verdicts, and the decoded Q frame (CRC state, control/
ADR, TNO, INDEX, relative and absolute time) when the image carries subcode.
`--descramble` shows data sectors in readable form.

## extract-sectors

```
dforge extract-sectors <image> <out> --start <addr> --count N
                       [--as stored|user|raw2352] [--byteswap]
```

- `stored` (default) — the bytes exactly as the image holds them.
- `user` — cooked payload: 2048 from Mode 1 (descrambling as needed),
  the 2048 XA payload from 2336 bodies, everything from audio.
- `raw2352` — full raw sectors, synthesising sync/header/EDC/ECC from
  2048-stored sources via the same machinery the RAW burner uses
  (unscrambled — pipe through `build-raw` if you need surface form).
- `--byteswap` — 16-bit endian swap for audio interchange with
  big-endian-expecting tools (CDRWIN's Intel/Motorola toggle).

Round-trips proven in tests: `user` extraction of a generated Mode 1 image
is byte-identical to the original payload, and `raw2352` synthesis from a
2048-stored CDI passes the inspector's EDC/ECC checks.

## Deferred with intent: dup-DVD's on-the-fly copy

Two-drive read-while-write copying is the right next hardware feature, and
exactly the wrong thing to write untested — it is underrun-sensitive by
nature. It goes on the bench the week the RW discs and a second drive are
in play.

---

# v1.7.0 additions

## CD+G end-to-end, proven semantically

`CdgDecoder` (Core) decodes CD+G graphics — palette loads, memory/border
presets, tile blocks, XOR tiles — from R–W symbols into a 300×216
framebuffer. With it, the passthrough claim was upgraded from "symbols
survive" to "the PICTURE survives": authored graphics went .sub → layout →
generated DAO image → decoded back, and the framebuffer, palette, and XOR
arithmetic came out identical to a reference decode of the source (pinned in
`CdgDecoderTests`, plus a live run where the screenshots from source and
disc image were byte-identical).

```
dforge cdg-preview <raw-image|src.cue> [--seconds N] [--out shot.ppm]
```

decodes either the CUE's .sub sidecar (tests the source) or a raw image
(tests what's on / would be on the disc), prints packet statistics, and
writes a PPM screenshot. When a REAL karaoke rip is available, this is the
proof tool: `cdg-preview rip.img --out check.ppm` and look at the picture.
Scrolling and transparency instructions are accepted but not rendered yet.

## Drive-based sector viewing

The Sector Viewer's new **Open Drive…** button points it at a live disc:
same addressing, but every read is a fresh SCSI command. `DriveSectorAccess`
(Devices) negotiates per drive and remembers what worked: READ CD raw 2352 +
formatted Q (so CDs show their live Q sub-channel), falling back to raw
without subcode, then READ(10) cooked 2048 for DVD/BD. An unreadable sector
throws with the SCSI sense code — a viewer that showed silent zeros would be
worse than one that says the drive couldn't read it.

## Raw Lab (GUI)

`build-raw` and `inspect-raw` now live in the app: **Raw Lab** in the
sidebar. Analyse any raw image or BIN (full inspector report, deep mode
checkbox); compose DAO images from CUE/CDI with subcode form choice and
progress. Compose feeds its output straight into the Analyse box — the
intended workflow is compose → analyse → burn, in that order.
