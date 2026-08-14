# CDI Image Format Reference (working draft)

## ✅ Validation round 1 — cdi4dc 0.3b, v3.5 Audio/Data (2026-07-12)

First real image validated: `tests/fixtures/cdi4dc_audiodata_v35.cdi`, generated
by cdi4dc 0.3b (SiZiOUS) from a known-contents ISO. Reproduce with
`docs/reference/validate_cdi.py`. Confirmed to the byte:

- **cdi4dc emits v3.5** (magic `0x80000006`), *not* v2 as first assumed.
- **v3.5 locator = descriptor length from EOF** — confirmed (742 → desc at
  fileLen−742). The length-vs-offset distinction is real and correct in code.
- **Session/track counts**: `u16 nSessions` then `u16 nTracks` per session. ✅
- **Track start mark** `00 00 01 00 00 00 FF FF FF FF` ×2 — confirmed present,
  **but preceded by a plain 4-byte lead-in** (`00 00 00 00`), NOT the conditional
  `0x80000000` dword the first draft assumed. ⬅ **spec + parser correction.**
- **Filename field**: `u8 length` + ASCII path — confirmed exact (len 31 matched).
- **Data-track storage**: Mode 2 / Form 1, **2336-byte stored sectors, user data
  at offset +8** within each sector. Verified against three independent
  signatures — every byte position predicted exactly. This is the extractor's
  ground truth for Phase 2.

Still ⚠️ (this image couldn't exercise them): multi-track sessions, audio-track
field values, ISRC/flags tail, and genuine DiscJuggler-authored (vs cdi4dc)
descriptors — the real DJ writer emits richer structures. Next corpus target:
a real DJ .cdi if one can be found.

---


The DiscJuggler CDI format was never officially documented by Padus. Everything
below derives from public reverse-engineering work in the preservation community
(the `cdirip` documentation lineage and the libmirage CDI parser notes are the
canonical public sources of format knowledge). This document is DiscForge's
clean-room specification: it records *format facts*, and our implementation is
written from this document, not from other codebases.

**Confidence markers:** ✅ = well-established, verify-on-first-image.
⚠️ = plausible but must be validated against real images before we trust it.

All multi-byte integers are **little-endian** unless stated. ✅

---

## 0. Canonical synthetic layout (DiscForge's own)

Two format concerns are deliberately separated:

- **Universal + confirmed** (from a real image): the trailer — version magic and
  the v2/v3-offset vs v3.5-length locator semantics — and the data-track storage
  model. These are ground truth and apply to any CDI.
- **Canonical layout**: a clean, fully-specified track/session body that
  *DiscForge itself* reads (CdiParser) and writes (CdiWriter / gen_cdi.py). It
  has NO mystery skips. It is what the synthetic fixture suite uses.

The canonical body is NOT claimed to be byte-identical to real DiscJuggler
descriptors — those are richer (see §4) and need a real DJ image to pin down.
The canonical layout exists to exercise parser/writer LOGIC (version dispatch,
locator math, session/track enumeration, per-track file-offset accumulation,
mode/sector-size decoding, error paths) under fully-controlled conditions, and
to give DiscForge a real write path to build the "Create image" feature on.

Canonical track block (all integers little-endian):

```
u32     lead-in            (0; 0x80000000 reserved for a future extra-dword)
u8[10]  start mark         00 00 01 00 00 00 FF FF FF FF
u8[10]  start mark         (repeated)
u32     reserved0          (0)
u8      filename length L
u8[L]   filename           (ASCII)
u32     pregap sectors
u32     track length sectors
u32     mode               0=audio, 1=mode1, 2=mode2
u32     start LBA          (absolute disc LBA)
u32     total sectors      (= pregap + length)
u32     sector size code   0=2048, 1=2336, 2=2352
u32     reserved1          (0; future ISRC/flags)
```

Descriptor = `u16 nSessions`, then per session `u16 nTracks` + track blocks +
`u32 sessionTail(0)`. Trailer per §2. Two independent implementations
(C# CdiWriter, Python gen_cdi.py) emit this identically; round-trip tests in
both languages confirm agreement.

**Fixture matrix** (`tests/fixtures/synthetic/`): single-track v2/v3/v35,
audio+data multisession, multi-track mixed-mode single session, and a
three-session image — covering every version, both locator conventions, all
three sector sizes, and multi-session offset accumulation.

---

## 1. Overall layout

A CDI file is: raw track data first, metadata last.

```
+--------------------------------------------+
| Track 1 data (pregap + user data sectors)  |
| Track 2 data ...                           |
| ...                                        |
| Descriptor ("header") — sessions, tracks   |
| Trailer: 8 bytes at absolute end of file   |
+--------------------------------------------+
```

The descriptor lives at the **end** of the file. ✅ This is why a truncated CDI
is unrecoverable and why the parser must start from the tail.

## 2. Trailer (last 8 bytes of file)

| Offset from EOF | Size | Field |
|---|---|---|
| -8 | 4 | Format version magic ✅ |
| -4 | 4 | Descriptor locator ✅ (semantics differ by version, below) |

Version magic values: ✅

| Value (LE) | Version |
|---|---|
| `0x80000004` | CDI v2   (DiscJuggler 2.x) |
| `0x80000005` | CDI v3   (DiscJuggler 3.x) |
| `0x80000006` | CDI v3.5 / v4 (DiscJuggler 3.5+/4.x/5.x/6.x) |

Descriptor locator semantics:

- **v2 / v3**: absolute file offset of the descriptor start. ✅
- **v3.5/v4**: the value is the descriptor's *length*, i.e. the descriptor starts
  at `fileLength - value`. ✅ (This is the classic gotcha; a v3.5 parser using
  the value as an absolute offset reads garbage.)

## 3. Descriptor structure

The descriptor begins with the number of sessions, then session blocks in order.

```
u16  nSessions                                ✅
for each session:
    u16  nTracks   (0 = open/empty session)   ✅
    for each track:
        <track block, see §4>
    <session tail bytes, version-dependent>   ⚠️ (v2: 12 bytes? v3+: varies — VALIDATE)
<disc-level tail: total sectors, volume id,
 disc-level flags, MCN/barcode>               ⚠️ layout to be pinned down empirically
```

## 4. Track block

Field order below follows the publicly documented cdirip parsing sequence.
Sizes/skips marked ⚠️ are exactly the ones we must confirm with hex dumps of
known images before freezing the parser.

```
u32   ⚠️ if value == 0x80000000 { u32 extra; }   // DJ4-era extra dword, then continue
u8[20]? track start mark pattern:                 ✅ pattern exists; exact repeat count ⚠️
        two repeats of 0x00 0x00 0x01 0x00 0x00 0x00 0xFF 0xFF 0xFF 0xFF
u8[4]  ⚠️ unknown
u8     filename length (L)                         ✅
u8[L]  filename (original source path, CP-ASCII)   ✅
u8[11+4+4] ⚠️ unknown / skipped region
u32    ⚠️ marker; if == 0x80000000 skip 8 more (DJ 3.00035+ quirk)
u8[2]  ⚠️ unknown
u32    pregap length (sectors)                     ✅
u32    track length  (sectors, excl. pregap)       ✅
u8[6]  ⚠️ unknown
u32    mode: 0=Audio, 1=Mode1, 2=Mode2/Mixed       ✅
u8[12] ⚠️ unknown (contains session index, track index within it) 
u32    start LBA                                   ✅
u32    total length (pregap + track, sectors)      ✅
u8[16] ⚠️ unknown
u32    sector size code: 0→2048, 1→2336, 2→2352    ✅
... trailing per-track fields (ISRC, flags)        ⚠️
```

**File position of track data**: tracks are stored back-to-back from offset 0 in
descriptor order; each track's stored size = `totalLength × sectorSize`. The
pregap sectors are stored in the file (unlike BIN/CUE where pregap may be
implicit). ✅

## 5. Sector layouts

| Sector size | Contents |
|---|---|
| 2048 | User data only (Mode 1 cooked) |
| 2336 | Mode 2 sub-header + data + EDC/ECC (Mode 2 raw-ish) |
| 2352 | Full raw sector incl. 16-byte header (or audio) |

For extraction to ISO, 2352 Mode 1 sectors take bytes 16..2063; 2336 Mode 2
Form 1 takes bytes 8..2055. ✅ (Standard Yellow Book, not CDI-specific.)

## 6. Sector cooking windows (user-data extraction)

The extractor (`CdiExtractor.UserDataWindow`) pulls user data from each stored
sector using this table. The 2336/Mode2 row is confirmed end-to-end: extracting
it from the real cdi4dc fixture reconstructs `source.iso` byte-for-byte.

| Stored size | Mode          | User offset | User length | Note |
|-------------|---------------|-------------|-------------|------|
| 2048        | any (cooked)  | 0           | 2048        | already user data |
| 2336        | Mode 2 Form 1 | 8           | 2048        | ✅ confirmed vs real image |
| 2352        | Mode 1        | 16          | 2048        | 12 sync + 4 header |
| 2352        | Mode 2 Form 1 | 24          | 2048        | +8 subheader |
| 2352        | Audio         | 0           | 2352        | whole sector is PCM |

Audio extraction wraps the raw sectors in a 16-bit/44.1kHz/stereo WAV header
(CD audio is exactly that PCM format; 2352 bytes = 588 stereo frames).

Pregap sectors are stored but excluded from cooked extraction; `--raw` emits the
full stored region (pregap included, no cooking).

## 7. Multisession

CDI's raison d'être: Dreamcast discs are typically 2 sessions
(session 1: audio warning track; session 2: data at LBA 45000 for "Audio/Data"
layout). The descriptor's session structure preserves this. LBA values in track
blocks are absolute disc LBAs, so session 2 tracks show large start LBAs. ✅

## 8. Validation plan (before the parser is trusted)

1. Build tiny known CDIs with cdi4dc (open tool, creates v3 images) — we control
   the input contents exactly.
2. Hex-dump descriptors of v2 / v3 / v3.5 images; walk fields against §4;
   promote ⚠️ → ✅ or correct.
3. Round-trip test: parse → extract → rebuild → byte-compare descriptors.
4. Real-world corpus: Dreamcast homebrew CDIs (legal, freely distributed) in
   both Data/Data and Audio/Data layouts.

## 9. Sources

- cdirip documentation lineage (format behaviour, field order, version quirks)
- libmirage CDI parser *behaviour* (multisession semantics) — consulted as
  documentation of the format, no code reuse
- ECMA-130 / Yellow Book for sector-level layouts
- Empirical hex analysis of images we generate ourselves (§7)
