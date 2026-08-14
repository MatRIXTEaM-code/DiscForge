# How DiscForge outperforms ImgBurn — the route

*Strategy note and working plan. Prepared 2026-08-10.*

The head-to-head (`docs/COMPARISON_IMGBURN.md`) shows DiscForge has already matched
ImgBurn's build/authoring checklist and, on everything that isn't burning, does dozens of
things ImgBurn never attempted. The one axis where ImgBurn still leads is *burning*, and its
lead there is not a feature list — it is **twenty years of hardware-proven trust**. So the
route to actually outperform it is not "add more burn settings" (we have them). It is three
moves, in leverage order.

## The three moves

**1 — Close the credibility gap (turn ◐ into ✓).** *Done — both burn paths, hardware-proven
2026-08-10.* First the IMAPI2 data burn was proven end-to-end on a real Plextor PX-W5224A — a
full burn → read-back → byte-exact `dvd-verify-readback` PASS (identical MD5 on every sector,
with a certificate). Then the harder one fell too: the **RAW-DAO-96** write was rebuilt over
**direct SPTI** with MMC **Write Type = Raw** (the ImgBurn/cdrdao approach — IMAPI2's raw-CD
writer rejects hand-built images), and it wrote a complete disc (lead-in + program, 2448-byte
raw sectors with interleaved P-W sub-channel, the lead-in sized from the drive's own ATIP
next-writable-address). `read-raw` pulled the disc back and `raw-verify-readback` graded it
**PASS — byte-identical on the main channel AND the sub-channel** across every compared sector.
Both of the project's biggest ◐s are now ✓.

**2 — Do what ImgBurn structurally cannot (the leapfrog).** *Delivered and hardware-proven
2026-08-10.* Two capabilities its architecture rules out — both now working on a real Plextor:

- **Byte-faithful RAW DAO-96 with sub-channel** — writing the whole disc, main + 96-byte
  sub-channel, lead-in included, so exact gaps, indexes, ISRC/MCN, CD-TEXT and *protection
  fingerprints* land on disc. ImgBurn has no RAW ripping or burning at all. **Proven:** the
  direct-SPTI Write-Type-Raw engine streamed a full raw disc to the PX-W5224A and it read back
  clean. The Plextor family is one of the few that can do this correctly.
- **Verified burns** — because we write the sub-channel, we read the disc back and prove it
  matches the exact bytes we sent, down to every Q frame. **Proven:** `raw-verify-readback`
  reported main + sub-channel byte-identical. ImgBurn verifies an MD5 of the user data and
  stops there.

**3 — Win the category so the overlap stops mattering.** Cross-platform, scriptable
end-to-end, actively maintained, every format validated against an independent oracle —
against a Windows-only GUI frozen since June 2013. For a pipeline or a preservation workflow,
that already makes ImgBurn a single-purpose accessory.

The honest caveat: raw burn *reliability reputation* is earned in the field across many drives
and media, not in one proven burn — we don't out-code ImgBurn's twenty years head-on. We don't
need to, because moves 2 and 3 win on axes it cannot contest, and move 2 is now demonstrably
real rather than promised.

## What is buildable now vs. what needs the hardware

The RAW-DAO **payload** side is essentially built and provable in software, and CI proves it:

| Piece | State |
|---|---|
| `build-raw` — compose the full DAO-96 image (lead-in TOC, program area, EDC/ECC, scramble, sub-channel in all three forms, verbatim protection preservation) | **done, tested** |
| `inspect-raw --deep` — re-decode a golden image independently: every Q-CRC, every data sector's EDC/ECC, TOC/MCN/ISRC/CD-TEXT | **done, tested** |
| `raw-verify-readback` — compare a disc read-back to the golden image (main + sub-channel), classify every difference, grade PASS/FAIL; `--report` writes an HTML certificate | **done, tested** — hardened for audio/PQ-16, Packed96, Interleaved96, multi-track |
| `dvd-verify-readback` — verify a burned DVD/BD vs its source at ECC-block granularity, layer-break aware | **done, tested** |
| `booktype-trace` — learn a bitsetting command from your drive's own trace and replay it verbatim | **done, tested** (replay issue is the hardware step) |
| The burn **transport** — data path (IMAPI2) and RAW DAO-96 (`SptiRawDaoBurnEngine`, direct SPTI, Write Type = Raw) | **hardware-proven 2026-08-10** (Plextor PX-W5224A: raw write → `read-raw` → `raw-verify-readback` PASS, main + sub-channel) |
| `read-raw` — pull the program area back as full 2448-byte sectors (2352 main + 96 raw P-W sub) for verification, auto data/audio field mode | **done, hardware-proven** |

The leapfrog is now *assembled, provable, and proven on hardware*. `raw-verify-readback` is
what converts "we think the burn is faithful" into "we proved it," and on 2026-08-10 it did
exactly that for a real RAW-DAO burn.

## The burn-day protocol (turnkey)

Documented in full in `docs/RAW_DAO.md`; validated end-to-end 2026-08-10. In short:

1. `dforge build-raw disc.cue golden.img --subcode raw` — compose and keep the golden.
2. `dforge inspect-raw golden.img --deep` — pre-flight the bytes (exit 0 = clean).
3. `dforge burn-raw disc.cue D: --engine spti` — RAW DAO-96 over direct SPTI (Write Type = Raw).
4. `dforge read-raw D: readback.bin --length N` — read the program area back (full 2448).
5. `dforge raw-verify-readback golden.img readback.bin` — prove it landed, byte-for-byte,
   including the sub-channel ImgBurn's verify can't see. Exit 1 on any defect.

Run it in the hardware-test order in `docs/RAW_DAO.md` (audio → gapless → CD-TEXT → ISRC/MCN →
data → mixed-mode), so each PASS isolates transport, then synthesis, then subcode. The first
rung — audio, transport — is now **PASS on a PX-W5224A**.

## What remains hardware- or fixture-bound

- **Broader burn-engine coverage** — one RAW-DAO burn is proven end-to-end (audio, PX-W5224A);
  field reliability is earned across more drives, media and disc shapes (gapless, CD-TEXT,
  ISRC/MCN, data, mixed-mode) using the protocol above. A written **lead-out** is not yet
  emitted (the drive finalises without one here); add it if a target drive needs it.
- **Bitsetting / book-type** — the vendor command bytes are learned, not fabricated:
  `booktype-trace` decodes a capture of your own drive setting the book type and stores a
  verbatim replay recipe (see `docs/BITSETTING.md`). Capturing the trace and issuing the replay
  over SPTI are the hardware steps; everything else is done and CI-proven.
- **DVD-Video IFO/BUP ECC-block padding** — coupled to the pointer values; not guessed
  without a mastered-disc fixture.

The RAW-DAO-96 execution — the write half of the leapfrog — is **closed and hardware-proven**.
The route from here is to widen coverage across the drives and disc types you own using the
protocol above, capture the bitsetting traces, and keep shipping the RAW-faithful, verified
burning ImgBurn was never built to do.

## Market-gap notes (2026-08-11, from user research)

Commentary on why modern CD/DVD burners feel frozen, and where DiscForge already answers it
or could. The vendors left the category (streaming/cloud replaced discs), so the living tools
stopped evolving — but the preservation/enthusiast need did not, which is DiscForge's whitespace.

Gaps raised, mapped to DiscForge:
- **Active development / not frozen** — already true (this repo; ImgBurn 2013, CDBurnerXP 2019).
- **Cross-platform, modern engine** — already true (one CLI on Win/macOS/Linux).
- **Lossless / raw audio, exact fidelity** — already true (raw 2352, sub-channel, RAW DAO-96).
- **Cloud-source burning** (assemble a disc from Google Drive / OneDrive / Dropbox) — **shipped
  (2026-08-11), MVP**: an `IFileSource` abstraction with local + HTTP(S) providers, a manifest
  format, and `source-stage` (materialise mixed origins into a staging folder for build-raw /
  iso-create / burn). OAuth cloud providers plug into the same interface next.
- **Modern UI** — **shipped (2026-08-11)**: `dforge ui` serves a modern loopback web app over the
  engine (quick actions + a command bar reaching every verb), cross-platform, no heavy framework.
  The older WinForms GUI remains for now.
- **Smart/AI capacity planning, auto-sort, tagging** — **shipped (2026-08-11), core**: `disc-span`
  plans the fewest discs (First-Fit-Decreasing across CD/DVD/BD/BDXL) with folder-grouping and
  oversize detection; it reads a folder or a source manifest (cross-origin planning). Tagging /
  auto-sort could build on this next.
- **4K/HDR & modern container ingest for video authoring** — out of current scope (DiscForge
  images/preserves rather than transcodes), but worth noting as a boundary.

Update 2026-08-11: three of these (cloud-source, modern UI, smart planning) moved from idea to
shipped this session, each with tests. Also this session: a clean-room **Zstandard decoder**
(validated against 120+ reference streams) that additionally unblocks reading **zstd-compressed
CHDs** (`cdzs`/`zstd`) — modern chdman output DiscForge previously refused.
