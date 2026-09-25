# DiscForge vs. ImgBurn — a full comparison

> **Update, v1.112.0 (2026-09-12):** all three items in "Concrete gaps worth closing" below have
> shipped — compressed-source (FLAC in-process, MP3/AAC/OGG/WMA/APE/MPC/WV via optional FFmpeg)
> audio CD authoring, a burn queue in `BurnView`, and automatic write-speed selection from a DVD/BD
> media ID's rated speed. See `CHANGELOG.md`'s v1.112.0 entry and `docs/NEXT.md` for what's proven
> (offline tests, plus a manual real-ffmpeg check) versus what still wants a real-hardware pass (the
> burn queue and the speed default). The rest of this document — the mode-by-mode comparison, "where
> ImgBurn still has a genuine edge" — is left as originally written; only the gaps section is now
> historical rather than open.

*A deep, mode-by-mode comparison against ImgBurn specifically, rather than the abbreviated
one-row treatment ImgBurn gets in `comparison_all_products.html`'s ten-tool matrix. ImgBurn is
worth a dedicated pass: it's the single most widely used Windows disc-burning tool in the
community DiscForge serves, so "how do we actually compare to the thing most people already
have" deserves a real answer rather than a summary row.*

## What ImgBurn is, honestly

ImgBurn (Lightning UK!, first released October 2005) is a free Windows CD/DVD/HD DVD/Blu-ray
burning and authoring tool. It has not been updated since **version 2.5.8.0, June 2013** — over
13 years of no development, no bug fixes, no new drive support. Its own installer has carried
bundled adware since 2.5.1.0 (Ask.com toolbar, then OpenCandy from 2.5.8.0 onward), which is why
most current recommendations point people to adware-stripped mirrors rather than the official
installer. Despite all of that, it remains the de facto standard for one specific job: reliably
burning an image file to optical media, and building DVD-Video/Blu-ray-Video discs from a
folder. That reputation is earned, not accidental, and this comparison treats it with the
respect that earns.

ImgBurn organizes itself around five modes, selected from icons on its main screen: **Read**,
**Build**, **Write**, **Verify**, and **Discovery**. The comparison below follows that structure,
then covers format breadth, audio/video authoring, and the odds and ends (booktype/bitsetting,
layer break, erase) separately.

## Read mode

ImgBurn's Read mode creates an image file from an unencrypted disc — essentially a straight
sector dump to BIN/CUE or ISO, with no error-recovery sophistication beyond basic retry-on-fail
and, on supported drives, a C2-error-aware read.

DiscForge's read path is built for a different standard entirely — Redump/preservation-grade
dumping, not "get a working copy":

- **Protection-aware dumping**: LibCrypt detection and handling, subchannel Q capture, weak/twin
  sector analysis — none of which ImgBurn attempts.
- **Adaptive re-read**: an escalating multi-strategy re-read controller (plain → C2-guided →
  slow+C2) that automatically tries harder on sectors that fail a first pass, confirmed working
  against real hardware this session (`disc-mri-reread`, `DriveRereadSource`).
- **Interrupt→resume**: a rip that gets killed partway through picks up where it left off
  instead of restarting — confirmed on real hardware this session (track 1's completed capture
  was correctly reused rather than re-read after a hard interrupt).
- **RAW reading with full P–W subchannel** (2352+96 bytes/sector) — ImgBurn's own documentation
  states it does not support RAW disc reading or writing subchannel data at all.
- **Mixed-mode and mode-transition handling**: DiscForge's read pipeline detects a sector-type
  boundary mid-track and falls back to sector-by-sector reading rather than failing outright —
  observed live this session on a real mixed-mode fixture disc (a mode transition at LBA 297
  triggered five rejected sector-type guesses before the correct fallback kicked in, and the
  read still finished cleanly).
- **Per-sector provenance**: every recovered sector's confidence level (agreed / EDC-verified /
  ECC-repaired / vote-verified / unrecovered) is recorded, not just "done" or "failed."

**Verdict: DiscForge substantially exceeds ImgBurn's Read mode.** This isn't close — ImgBurn was
never trying to be a preservation tool, and it shows.

## Build mode

ImgBurn's Build mode assembles DVD-Video, HD DVD-Video, and Blu-ray-Video discs from a
`VIDEO_TS`/`HVDVD_TS`/`BDMV` folder structure, handling the UDF/ISO 9660 authoring underneath.

DiscForge's equivalents:

- `dvd-video-plan` / `dvd-video-build` / `dvd-video-fix` — validate a VIDEO_TS folder, assemble
  it into a DVD-Video ISO+UDF image, and repair IFO sector pointers + refresh `.BUP` files to
  match actual file sizes (a real-world authoring problem ImgBurn doesn't address at all — a
  folder edited after mastering with mismatched IFO pointers is a genuine failure mode ImgBurn
  will silently carry through).
- `bdmv-plan` / `bdmv-build` — validate and assemble a Blu-ray BDMV folder (index/MovieObject/
  PLAYLIST/CLIPINF/STREAM) into a BD-Video UDF 2.50 image.
- No HD DVD-Video authoring — a dead format ImgBurn supported at the height of the format war;
  not worth building given HD DVD's total commercial failure and near-zero remaining relevance.
- General-purpose data/ISO 9660/UDF authoring (`IsoBuilder`, `UdfBuilder`) with Joliet, Rock
  Ridge, El Torito boot images, and UDF revisions through 2.60 — matching ImgBurn's general
  authoring breadth.

**Verdict: roughly even on DVD/BD-Video authoring, DiscForge ahead on validation/repair depth**
(`dvd-video-fix`'s IFO-pointer repair has no ImgBurn equivalent), **ImgBurn ahead only on HD
DVD**, which is not a meaningful gap in 2026.

## Write mode

ImgBurn's Write mode burns an image file to disc — TAO by default, with per-drive write-speed
selection (including automatic speed recommendation from the media's manufacturer ID) and a
write queue for burning several images back-to-back unattended.

DiscForge's write path:

- `burn` — data ISO to CD/DVD/BD via Windows IMAPI2 (or macOS `hdiutil`), with `--verify` and
  `--speed`.
- `burn-raw` — **RAW DAO-96 burning (full 2352+96-byte subchannel, protection re-creation)** via
  direct SPTI commands. This is the single largest capability gap between the two tools in
  DiscForge's favor: ImgBurn's own documentation states it does not support RAW disc burning.
  The closest anyone gets in the free-tool space is `cdrdao`'s RAW-DAO writing on Linux/classic
  Windows, or commercial copy tools (Alcohol 120%, CloneCD) with no independent verification.
  DiscForge's RAW burns are validated byte-for-byte against golden builds via `raw-verify-
  readback`, confirmed on real Plextor hardware this session (RAW DAO burn succeeded even though
  the drive's own capability report advertised `RAW-DAO n` — the advertised capability bit
  appears unreliable on this specific drive, and the actual write mode worked anyway).
- `burn-plan` — previews a burn's exact SCSI/MMC command sequence offline, no drive needed. The
  CLI's own help text calls this out directly: "no equivalent in ImgBurn."
- Booktype/bitsetting via `booktype-trace` (decode a captured bitsetting recipe) and
  `booktype-set` (replay a learned recipe on a drive) — a capture-and-replay model, versus
  ImgBurn's built-in per-vendor lookup tables. ImgBurn's approach covers more drives out of the
  box with zero setup; DiscForge's approach is more rigorous (it's driven by an actual observed
  recipe rather than a vendor guess) but requires that recipe to exist first.
- Layer break selection: ImgBurn offers manual layer-break entry for dual-layer DVD-Video.
  DiscForge has `dvd-layerbreak` (read back and verify a burned layer break), `layerbreak-pick`
  (automatically choose a *legal* DVD-DL layer break given cell boundaries and a target), and
  `dvd-layerbreak-plan` (recommend a break at an actual VOBU boundary from the real IFO
  structure) — DiscForge automates what ImgBurn leaves to the user's judgment.
- **No write queue** — ImgBurn lets you queue several images and walk away; DiscForge has no
  equivalent today. See "Concrete gaps worth closing" below.
- **No confirmed automatic write-speed-by-media-ID selection** — DiscForge defaults conservatively
  (speed 4× unless told otherwise, per an in-code note: "max-speed on aged media is how round-
  trip #1 died"), rather than reading the media's manufacturer ID and picking a recommended
  speed the way ImgBurn does. This is a deliberate conservatism, not an oversight, but it means a
  user burning a known-good, fast-rated blank disc doesn't get ImgBurn's "just pick the fast
  speed for me" convenience.

**Verdict: DiscForge's write path is more capable at the physical layer (RAW-DAO, offline burn
planning, automated layer-break selection) but has two real day-to-day convenience gaps**
(batch/queue burning, automatic speed-by-media-ID) **that ImgBurn handles better for a casual
"burn this and walk away" user.**

## Verify mode

ImgBurn's Verify mode checks a disc is 100% readable, optionally comparing it against the source
image via checksum/hash.

DiscForge's verify surface is considerably deeper:

- `raw-verify-readback` / `dvd-verify-readback` — prove a burn is byte-faithful against the
  golden source, layer-break aware for DVD/BD.
- `verify-convert` — prove a *format conversion* was lossless by decoding both images to raw
  sectors and comparing byte-for-byte.
- `chd-verify`, `fs-verify`, `hashverify` — integrity checks across CHD, filesystem-level
  cross-consistency (ISO 9660 vs Joliet vs UDF agreeing on the same files/bytes), and SFV/MD5/
  SHA1 sidecars.
- `dump-cert` / `merge-cert` / `dump-ledger` — **signed, Merkle-provable dump certificates** that
  let you prove any 2 KB slice of a dump against the original capture event, and a public
  hash-chained ledger for cross-checking independent dumps of the same disc against each other.
  ImgBurn has no cryptographic provenance concept at all — its "verify" is a one-time pass/fail,
  not a durable, checkable proof.

**Verdict: no contest.** ImgBurn's Verify is a checksum comparison; DiscForge's is a full
provenance and cryptographic-proof stack built for archival trust, not just "did the burn work."

## Discovery mode

ImgBurn's Discovery mode is really two things bolted together: a read-speed test across the
disc, and (in combination with the companion tool DVDInfoPro) a media-quality scan reading
PI/PIF/POF error rates for DVD or C1/C2 for CD.

DiscForge splits this into two commands that the CLI help text explicitly frames against ImgBurn:

- `read-benchmark` — "the read-speed half of ImgBurn's Discovery."
- `disc-scan` — "the honest half of ImgBurn's Discovery mode": a genuine media-quality scan
  (CD C1/C2, and via `scan-import`, DVD PIE/PIF/POF and BD LDC/BIS pulled from a foreign tool's
  export — Opti Drive Control, Nero DiscSpeed/CD-DVD Speed).

Where DiscForge goes further than ImgBurn+DVDInfoPro ever did: **Disc Actuary**
(`disc-actuary`/`DiscActuary.cs`) turns every quality scan into a point in a longitudinal time
series per physical disc, so you can track a disc's degradation over months or years and get an
actuarial estimate of remaining life — not just a single point-in-time snapshot. Nothing in the
ImgBurn/DVDInfoPro combination does this.

**Verdict: DiscForge matches ImgBurn+DVDInfoPro's one-time capability and adds a longitudinal
dimension neither tool has.**

## Format breadth

ImgBurn reads/writes: BIN, CUE, CCD, CDI, DI (Atari disk image), DVD, GI, IMG, ISO, MDS, NRG,
PDI.

DiscForge reads/converts across: BIN/CUE, CCD (read + convert to), CDI, ISO, GDI, MDS (convert
from), NRG (convert to/from), CHD (create **and** extract — ImgBurn has no CHD support at all),
CSO/ZSO (compressed ISO variants), WBFS. DiscForge does **not** support DI/GI/PDI — Atari-specific
formats (Atari ST disk images and Atari Jaguar CD images) that are extremely niche even within
retro preservation, and were likely added to ImgBurn by specific community request rather than
broad demand.

**Verdict: DiscForge's format breadth is wider where it matters for modern preservation/emulation
workflows (CHD, GDI, CSO/ZSO, WBFS); ImgBurn covers a small set of legacy Atari formats DiscForge
doesn't and likely shouldn't chase.**

## Audio CD authoring

This is ImgBurn's clearest remaining edge. ImgBurn can burn an audio CD directly from source
files in AAC, APE, FLAC, M4A, MP3, MP4, MPC, OGG, PCM, WAV, WMA, or WV — decoding whatever
compressed format you hand it on the fly.

DiscForge's `AudioCdCreator` (the `Create.AudioCdCreator` class the app's own diagnostics report
flags as `audio-create`) accepts **Red Book WAV only** (44.1 kHz/16-bit/stereo) — no compressed-
format source decoding at all. This is a real, honest, addressable gap — see below.

**Verdict: ImgBurn clearly leads here.** DiscForge's audio-ripping side (extracting FROM a disc)
already produces WAV, FLAC, or Ogg — the gap is specifically on the authoring-FROM-compressed-
audio side.

## Odds and ends

- **Erase/blank a rewritable disc**: both tools have this (`blank` in DiscForge, with a fast/
  minimal default and a `--full` option — matching ImgBurn's quick/full erase choice).
- **Unicode filenames**: both support it — DiscForge's Joliet writer uses UCS-2/UTF-16BE names
  and its UDF writer uses OSTA-compressed Unicode, matching ImgBurn's stated Unicode support.
- **Multi-session discs**: ImgBurn's own documentation states it does not support multi-session
  burning. DiscForge's session model (`SessionCount` fields throughout the dump-completeness and
  cue-parsing code) is read/analysis-aware of multiple sessions but multi-session *writing* isn't
  a headline feature either — this is roughly even, and neither tool should be judged harshly for
  it; multi-session optical writing is a niche need in 2026.
- **Single-drive-per-operation**: both tools work this way; not a meaningful difference.

## Where DiscForge clearly leads

1. **RAW DAO-96 burning with subchannel and protection re-creation** — ImgBurn explicitly cannot
   do this at all.
2. **Preservation-grade reading** — adaptive re-read, interrupt/resume, protection-aware capture,
   per-sector provenance — versus ImgBurn's straightforward "read the disc" approach.
3. **Cryptographic provenance** — signed dump certificates, Merkle proofs, a public cross-dumper
   consensus ledger. ImgBurn has no equivalent concept.
4. **Longitudinal disc-health tracking** (Disc Actuary) versus ImgBurn+DVDInfoPro's one-shot scan.
5. **Automated, VOBU-aware layer-break planning** versus ImgBurn's manual entry.
6. **Modern format breadth** — CHD create/extract, GDI, CSO/ZSO, WBFS — none of which ImgBurn
   touches.
7. **Active development and support** versus 13 years of no updates and a
   bundled-adware installer.
8. **Offline burn-sequence preview** (`burn-plan`) — explicitly called out in the CLI's own help
   text as having no ImgBurn equivalent.

## Where ImgBurn still has a genuine edge

1. **Audio CD authoring from compressed sources** (MP3/FLAC/AAC/OGG/etc.) — DiscForge is WAV-only.
2. **Write queue / batch burning** — ImgBurn lets you queue several images unattended; DiscForge
   has no batch-burn concept in the CLI or GUI today.
3. **Automatic write-speed-by-media-ID** — ImgBurn reads the media's manufacturer code and
   suggests a speed; DiscForge defaults conservatively and expects the user (or `drive-profile`)
   to make that call.
4. **Zero-dependency, tiny footprint, extreme battle-testing** — ImgBurn is a small native
   Win32 app with 20 years of real-world use across essentially every consumer optical drive ever
   sold. DiscForge is a .NET 8 application; broader in capability, but with far less real-world
   drive-compatibility mileage behind it yet.
5. **Simplicity for a one-off "just burn this ISO" task** — ImgBurn's five-icon mode picker is
   about as low-friction as a burning tool gets. DiscForge's CLI surface (400+ commands) and GUI
   tile grid are built for a much wider mission, and that breadth is not free: a first-time user
   who only wants to burn one ISO has more to navigate past.

## Concrete gaps worth closing (the "new features" half of this deep dive)

Ranked by how directly they'd close a real ImgBurn-parity gap, and how much new engineering each
actually needs:

1. **Compressed-source audio CD authoring — the highest-value, lowest-risk addition.**
   DiscForge already has a clean-room FLAC *decoder* (used for AaruFormat/CHD FLAC blocks) and a
   FLAC *encoder* (`Audio/FlacEncoder.cs`). Wiring the existing FLAC decoder into
   `AudioCdCreator`'s track-loading path (decode FLAC → 44.1kHz/16-bit PCM → feed the existing
   Red Book pipeline unchanged) closes the single largest audio-authoring gap with genuinely
   modest new code, since the hard part (a correct, validated FLAC decoder) is already shipped
   and tested elsewhere in the codebase. MP3/AAC/WMA/APE/MPC/M4A/WV decoding would each need
   either a new clean-room decoder (a real undertaking per format) or a licensing/dependency
   decision DiscForge hasn't had to make anywhere else in the project — worth scoping separately
   and honestly, not bundled into "just add audio formats."

2. **A burn queue.** Straightforward, GUI-side work: a list of image+drive+options entries the
   Burn screen works through sequentially, reusing the existing single-burn logic per entry. No
   new Core algorithm needed — this is UI/orchestration work in `BurnView.cs` plus a small
   `BurnQueue` model, not a research problem.

3. **Automatic write-speed suggestion from media ID.** `drive-profile`/`Imapi2MediaTools`
   already query which speeds a drive supports for loaded media; the missing piece is reading the
   media's own manufacturer/dye-type identifier (ATIP for CD-R — `cdr-info` already reads ATIP
   dumps for reporting) and mapping it to a recommended speed the way ImgBurn's built-in media
   database does. Moderate effort: the read side exists, the recommendation table doesn't.

None of these are hardware-gated to develop (only to fully validate) and none require touching
the preservation/verification core this project is built around — they're squarely in "make the
everyday burning experience match ImgBurn's convenience" territory, which is a fair and honest
thing for ImgBurn to still be ahead on.

## Bottom line

If the question is "which tool preserves a disc more faithfully, verifies more rigorously, or
handles RAW/subchannel/protection-aware work at all" — DiscForge wins outright, and ImgBurn was
never trying to compete on that ground. If the question is "which tool burns a random ISO I
downloaded the fastest, with the least friction, and the best built-in defaults for speed and
batching" — ImgBurn, despite 13 years of no updates, still has a real, honestly-earned edge in
day-to-day convenience. The gaps identified above (audio authoring from compressed sources, a
burn queue, auto speed-by-media) are exactly the kind of "make the daily-driver experience as
good as the preservation experience" work that would close that gap without diluting what makes
DiscForge different in the first place.
