# DiscForge — Roadmap & Idea Backlog

A living list of where DiscForge goes next. Everything here stays inside the
**clean-room boundary**: faithful imaging, identification, conversion, patching,
protection *detection*, recovery, and preservation — and never defeating copy
protection, region locks or console security, and never decrypting protected
content. Ideas that would cross that line are out of scope by design.

Last updated during the session that built the preservation platform below.

---

## Next up — when the Plextor PX-W5224TA arrives

A Plextor PX-W5224TA is on the way (the community's reference CD-dumping drive, prized for
negative-LBA lead-in and lead-out overread capture). This is the priority queue for when it lands.
The building blocks for most of these already exist in `Mmc/MmcCommands.cs` (READ CD 0xBE raw 2352,
C2 pointers, subchannel), `Dumping/DumpingWizard.cs`, and the App's `SectorView` / `CdrwinLauncher`.

1. **`extract-sectors` — native CDRWIN-style sector-range extractor.** *(top of the queue.)* A single
   command that mirrors CDRWIN's "Extract Disc/Tracks/Sectors to Image File": choose an extract mode
   (whole disc / track selection / **sector range**), set **start/end LBA**, pick the **datatype**
   (Mode 1 Raw 2352, Mode 2 Form 1/2, 2336, audio), and dump that span to an image — with the read
   controls that dialog exposes: **error recovery** (Abort / Ignore / Replace), **jitter correction**,
   **subcode analysis**, **C2**, and a **read-retry count**. All the low-level machinery exists
   (READ CD raw + C2 + subchannel; sector inspection in `SectorView`); this assembles it into one native
   command/dialog instead of shelling out to CDRWIN. Live-drive sector extraction is exactly what the
   Plextor is for, so this pairs directly with the hardware.

2. **Drive-capabilities database + a Plextor PX-W5224TA profile.** A bundled, queryable table keyed by
   drive model: combined read-offset correction, C2 quality, lead-in (negative-LBA) and lead-out overread
   depth, cache-read support, and preferred read command (BE / D8). Auto-detect the connected drive,
   look up its profile, warn if unsuitable, and pre-fill the offset. Ship a first-class Plextor
   PX-W5224TA profile (lead-in / pre-gap capture is its signature preservation capability). *(from the
   retro-backup forum sweep — Redump drive threads, redumper.)*

3. **par2 (Parchive) read/verify interop.** ✅ **SHIPPED** (`par2-verify`, `Preservation.Par2`). Reads a
   PAR2 set, checks each packet's own MD5, verifies the protected files slice by slice, and reports
   repairability against the available recovery slices — validated against real par2cmdline output
   (pristine / corrupted-slice / missing-file all match). This one wasn't hardware-gated, so it's done.

4. **Offset-shift disc detection.** Detect discs whose write offset changes partway through (a mastering
   anomaly most tools silently get wrong), building on the existing combined-offset handling.

5. **Prototype / debug-residue scanner.** A dedicated "is this a prototype?" pass — composite checksum,
   leftover `.sym`/`.map`/debug strings, retail-baseline diff — building on `DiscArchaeology` and the
   build-date extraction already in `DiscBillOfMaterials`.

See also the **Gated — needs external input** section below for the full dump → `dump-score` →
`dump-merge` → convert → burn → compare-hashes round-trip, which also unblocks once the drive and a real
disc are in hand.

---

## PS1 preservation backlog (from an r/psx sweep)

Clean-room PS1-specific ideas surfaced from the r/psx orbit (Redump wiki, psx-spx, ODE tooling). All
identify/verify/preserve — never LibCrypt-patching or region-defeat.

- ~~**CU2 sidecar read/write/verify**~~ — DONE (already existed, found 2026-08-27, since 2026-08-15). Full
  `DiscForge.Core.Cue.Cu2` (`Write`/`Parse`/`Verify`) plus `dforge cu2 write|verify`, tested in
  `Cu2Tests.cs`. Nothing to build.
- ~~**Pregap-accuracy check**~~ — DONE (already existed, found 2026-08-27). Full `DiscForge.Core.Cue.PregapConformance`
  plus `dforge pregap-check`. Nothing to build.
- ~~**On-disc region license-string reader**~~ — DONE (built 2026-08-27). New `DiscForge.Core.PlayStation.LicenseString`
  (`Parse`/`FromImage`/`CrossCheck`) plus `dforge license-check <image> [--json]`; 17 tests in
  `LicenseStringTests.cs`. See `docs/NEXT.md` for the layout source and scope notes (padding-pattern check
  is informational-only — not independently verified against a real disc dump).
- ~~**XA Mode 2 Form 1/2-aware EDC/ECC**~~ — DONE (verified 2026-08-27, no code change needed). Checked every
  EDC/health-map consumer in `DiscForge.Core` (`DiscHealthMap`, `DiscMri`, `PremasterGate`, `DumpReconstruct`,
  `DumpMerge`, `ExtractionAudit`, `RawImageInspector`, `DumpingWizard`, `RawReadbackCompare`): every one already
  branches on the XA subheader submode byte (`main[18] & 0x20`) and picks Form 1 EDC+ECC vs. Form 2 EDC-only
  (or skips entirely when the Form 2 EDC field is zero/unused) before validating. `SectorExtraction.cs` is
  explicitly per-datatype (the caller states which form it expects) so it was never exposed to this. No false
  Form 2 damage reports exist anywhere in the current codebase — this backlog item pre-dates a fix that's
  already landed (or was never actually broken; no git-blame trail points to a dedicated fix commit for it).
- ~~**Multi-disc set modeling**~~ — DONE (built 2026-08-28). `DiscForge.Core.Library.MultiDiscDetector`
  detects "(Disc N)"/"(Disc N of M)" titles, `MultiDiscManifestBuilder` rolls per-disc hashes into a set
  manifest, and `OdeExporter.PsioSet` shares one folder across all discs of a title and emits a real
  PSIO `MULTIDISC.LST` (byte-format verified against the PSIO Systems Manual). CLI: `multidisc-detect`,
  `multidisc-manifest`, extended `ode-export` (now variadic over cue paths). 20 new tests
  (`MultiDiscSetTests.cs` + 3 in `OdeExportTests.cs`). See `docs/NEXT.md` for detail.
- ~~**Full R–W (96-byte) subchannel deinterleave + preservation**~~ — DONE (built 2026-08-28), turned out
  to be mostly already shipped. `SubcodeFrame` already fully deinterleaved/reinterleaved all 8 channels
  across all 3 physical layouts, and `read-raw` already captured full raw P-W as the backbone of the whole
  burn-verify pipeline. The one genuine gap: the MMC CORRECTED (0x04) sub-channel selector was defined but
  never requested, and `SubchannelReader` had no CLI exposure. Built `SubchannelReader.ReadCorrected` +
  `SupportsCorrectedSubchannel`, `RawSubchannel.CompareRawAndCorrected` (raw-vs-drive-corrected
  cross-validation), and `dforge subchannel-dump <drive> <out.sub> [--corrected f] [--compare]`. 4 new
  tests. **Not yet run against real hardware.** See `docs/NEXT.md` for detail.
- **PS1 save-container identification/conversion** (adjacency, optional module) — the `.mcr/.mcd/.gme/.vgs/
  .vmp/.psv/.mcs` zoo + PocketStation, mapping each save's product code to a Redump identity.
- **"Un-capturable protection" honesty field** — a metadata note that a title's wobble-groove/ATIP physical
  signal is *not* representable in the dump (distinct from LibCrypt, which is in subchannel) — turns "my 1:1
  copy has everything" into an honest catalog field.

*Exclude (circumvention): LibCrypt patching to boot backups (psio-assist/aprip), modchip/swap-trick, ODE-as-
piracy-loader framing, region-string alteration, SBI-for-playability of cracked discs.*

---

## GameCube preservation backlog (from a NintendoLife/GC-community sweep)

Clean-room GameCube ideas (GC discs are unencrypted, so these are all safely in-scope). Never decrypt Wii
partitions, boot backups, or enable Datel/region-bypass exploits.

- **★ GC junk-padding PRNG — regenerate / detect-scrub / recover** *(top pick)*. GC/Wii discs fill gaps with
  pseudo-random junk *fully determined by the 4-byte game ID + disc number*. Implementing that generator lets
  DiscForge (a) regenerate the expected junk, (b) *detect* scrubbed/trimmed/zeroed dumps by diffing, and (c)
  *recover* a scrubbed image to a bit-exact Redump ISO and confirm by CRC32 — independently of any NKit block.
  Elevates the existing NKit-*read* into full scrubbing analysis + recovery. (github.com/DonMikone/nkit2iso)
- **Single-image "good dump" health report** — a lone-file verdict (no re-dump needed): DVD magic 0xC2339F3D,
  sane FST/DOL bounds, expected size for the disc class, no truncation, padding == regenerated junk. Fills the
  gap where Redump's only check is "dump twice and compare."
- **Ring-code / mastering-code schema + Redump ring matching** — capture the red date/plant code (AYYMDDBB),
  blue DOL-xxxx game/rev code, green S0, IFPI/SID, and cross-check the disc's physical provenance against
  Redump's ring fields (how collectors tell revisions apart when the data CRC is identical).
- **DTK/ADP streamed-audio + DSP-ADPCM decode to WAV** — the parallel to the existing TPL texture decoder:
  decode GC disc-streamed audio (32-byte/28-sample GC-ADPCM) and in-game `.dsp` files (coeffs/loop) for
  content verification/preservation.
- **GC memory-card preservation** — parse `.gci/.gcs/.sav` saves (header, region, internal filename, dates),
  extract the banner (CI8/RGB5A3 96×32) + animated icon to PNG (reuses TPL muscle), and parse full `.raw/.mci`
  card images (dir/BAT + backups, checksums) with corruption flagging. (github.com/GerbilSoft/mcrecover)
- **Apploader + bi2.bin parse** — surface region (bi2 @0x18), header version/rev byte (@0x07), audio-stream
  enable flag (@0x08), and confirm the boot chain (header→apploader→DOL→FST) is complete/consistent; feeds the
  health report.
- **Revision/variant-aware DAT match** — report "matched Redump entry + revision (rev 0/1/2)", and correctly
  *label* unlicensed/special discs (Datel/AR, GBP start-up, service/kiosk, multi-game demo) instead of flagging
  them as bad dumps; enumerate demo-disc contents from the FST.

*Exclude: booting backups/loaders (Swiss/Nintendont/DIOS-MIOS), modchips/boot exploits, Datel region-bypass at
runtime, cheat-engine execution, and any Wii partition decryption / title-key handling.*

---

## Recently shipped — the preservation platform

These are done, tested and in the repo (CLI command in `code`):

Shipped since: **Disc genealogy + counterfeit detection** (`disc-genealogy`, `Forensics.DiscGenealogy`) — the
forensic capstone. It fuses a collection's physical-provenance signals — the stamped **matrix**, the
**mastering (glass-master) IFPI SID**, the **mould (replication-line) SID**, an optional **physical error-map
fingerprint**, and the **media type** — into one family tree: glass master → pressing plant → individual
copies, with same-physical-copy links from the error maps. Every disc gets an authenticity verdict — a
recordable (CD-R) burn or a "pressing" missing the master identifiers its siblings carry is flagged, while a
valid mastering SID or an old matrix-only pressing reads as authentic. Assessment only; it reports what the
physical evidence shows and defeats nothing. Reads a JSON collection (composes with `ring-code` + `disc-print`
output); emits the report as `--json`. Validated against a synthetic multi-plant collection (families, plant
branches, error-map sibling links, and every authenticity path — authentic / suspect / likely-counterfeit /
unknown).

Shipped since: **DiscForge Explorer** (`tools/discforge-explorer.html`) — a single self-contained,
offline HTML viewer that auto-detects and renders DiscForge JSON: a `disc-report --json` becomes a visual disc
overview (identity hero + colour-coded structure cards for the filesystems, boot records, console headers and
protection), and a `disc-genealogy --json` becomes the provenance family tree with a colour-coded authenticity
table. Drag-drop or paste any DiscForge JSON; embedded samples so it's never empty; no dependencies, no
network, no storage. The shareable face of everything the CLI already emits.

- **Multi-read disc recovery** (`dump-merge`) — merge several imperfect rips of the
  same disc into one image; keep agreed sectors, use any copy whose EDC validates,
  majority-vote and re-check the rest, report the unrecoverable ones.
- **Verifiable preservation packages** (`preserve pack` / `preserve verify`) — a
  hash-manifest with a self-digest that proves a set is byte-for-byte what was recorded.
- **Bit-rot watchdog** (`library-watch`) — snapshots a collection and, on later runs,
  flags SUSPECTED ROT (content changed while the file's timestamp never moved).
- **Deterministic re-mastering** (`remaster pack` / `rebuild` / `verify`) — decompose an
  ISO into a content-addressed file store + a small structural recipe, and regenerate
  the **byte-exact** original; dedupes shared files across a collection.
- **Dump confidence + read offset** (`dump-score`, `offset-detect`) — a 0–100 dump grade
  from EDC health, and CD-DA read-offset detection by correlation.
- **Reed-Solomon sector correction** (`ecc-repair`, and inline during a C2-guided read) —
  repairs a damaged data sector from a *single read* using the RSPC P/Q parity the sector
  already carries. C2 pointers become erasures (two corrections per codeword, not one), the
  P/Q product code walks bursts down across passes, and every repair is EDC-confirmed before
  it is accepted. Covers **Mode 1 and Mode 2 Form 1** (CD-XA / PlayStation data) — Form 1's
  header is excluded from the parity, so it is blanked and locked for the decode and the real
  address restored afterwards.
- **Disc archaeology** (`disc-anomalies`) — maps everything a cooked ISO 9660 image
  legitimately contains (system area, every volume descriptor, both path tables, the ISO 9660
  *and* Joliet directory hierarchies, every catalogued file) and reports the non-zero bytes
  left over: leftover mastering data, files deleted but not overwritten, payloads in the
  system area or **past the declared volume end**. Each find is zoned and classified
  (text-like / high-entropy / binary) with an entropy score. Surfaces what normal extraction
  discards; decodes and defeats nothing.
- **Disc genome fingerprint** (`disc-genome`) — an offset-invariant identity for a pressing:
  an exact hash of the TOC geometry and the addressed data, plus an offset-tolerant audio
  loudness envelope compared with a small shift search. Two correct rips of the same disc
  match even when the CD-DA read offset (and so the raw bytes and a naive SHA) differ —
  DAT-less identification and dedup.
- **Unified reconstruction pipeline** (`reconstruct`) — one operation that resolves every
  sector by the whole recovery ladder in confidence order (agreement → a copy that passes
  EDC → single-read RSPC ECC repair → majority vote → ECC repair of the vote) and records a
  **per-sector provenance map** of how each sector was saved. Supersedes a plain merge.
- **Disc health heat-map** (`health-map`) — renders per-sector EDC/ECC health (or a
  reconstruction provenance map) as an SVG, so the *shape* of the damage is visible: a solid
  red block is a scratch (physical, recover it); a thin repeating band is more likely a
  deliberate protection pattern (preserve it). Aggregates worst-of-block so nothing hides.
- **Filesystem identification** (`disc-fs`) — reports every filesystem a disc carries, not
  just the one your OS mounts: ISO 9660, Joliet, UDF, CD-XA, Apple HFS/HFS+ and the Apple
  Partition Map, flagging genuine hybrids (Mac + PC, UDF-bridge) by filesystem *family*.
- **Cross-source verification provenance** (`preserve corroborate`) — folds an independent
  dump of the same disc (a second drive, a second copy) into the preservation manifest as
  tamper-evident provenance, keyed on container-independent per-track CRC-32s. Two drives that
  agree is the strongest evidence a dump is faithful short of a published DAT match; the
  manifest now records who agrees, and `preserve verify` reports "cross-source verified". Reads
  the per-track CRCs straight from a saved `submission-info` report. A down payment on the
  roadmap's chain-of-custody provenance item. A `--genome` mode corroborates by offset-invariant
  identity instead of CRC — for two drives that read the same disc at different CD-DA read offsets
  (a PlayStation disc with audio is the classic case), whose raw CRCs differ even though the dump
  is faithful; it records the layout/data match, the audio similarity and the offset gap.
- **Signed chain-of-custody lineage** (`lineage`) — an append-only, hash-linked history of how
  a dump came to be: dumped on drive X, corroborated by drive Z, ECC-repaired here, merged from
  these reads, sealed with this manifest digest. Every event carries the hash of the one before
  it, so nothing can be inserted, reordered or removed without breaking the chain; an ECDSA
  (NIST P-256) signature over the chain head then makes the whole history tamper-evident and
  attributable, verifiable by anyone with no prior key exchange (the public key travels with it).
  CLI: `lineage keygen | init | append | sign | verify | show`. Completes the chain-of-custody item.
- **Content-aware disc delta** (`disc-delta` / `disc-patch`) — diffs two ISO images at the file
  level and emits a delta carrying only the files that changed or are new; files unchanged in
  both stay in the base and are merely referenced (reusing the `remaster` content-addressed
  cover). Applying the delta to the base regenerates the target **byte-for-byte** (verified
  against the recorded hash, and refused against the wrong base). Two related discs — a game and
  its revision, two region variants — cost one image plus their differences: a 4 MB test image
  reduced to a 6 KB delta.
- **Copy-protection fingerprint catalog** (`protection-scan`) — precisely identify the protection scheme,
  version and parameters a disc carries and record them as preservation *metadata*. It matches the marks a
  scheme leaves: the tell-tale files/directories it drops on the filesystem (SafeDisc's `00000001.TMP` and
  `.icd`, SecuROM's `CMS*.DLL`, a `LASERLOK` directory, CD-Cops, StarForce, VOB ProtectCD, TAGES…), the
  signature strings inside the wrapped executables (with SafeDisc's exact version triplet decoded from its
  `BoG_` marker), and — for PlayStation discs, with a raw subchannel — LibCrypt's deliberately corrupted
  Q, caught by its failing CRC-16. Each detection carries its evidence, a confidence, and a note that it is
  metadata only. Detection and documentation only: it names and dates what is there so a faithful dump can
  be catalogued like a museum labels an artefact — it never removes, bypasses, weakens or circumvents any
  protection.
- **Disc-rot triage / actuarial prediction** (`disc-rot`) — track a disc's C1/C2 error scans over time
  and predict which discs are dying, so the failing ones get dumped first. Optical rot shows up as a
  rising block-error rate (C1/BLER) long before data is actually lost, and as C2 errors once the first
  correction layer starts to be overrun. Given a history of scans it grades each disc against the
  standard thresholds (Red Book's BLER ceiling of 220, C2 onset, uncorrectable CU = data loss), fits the
  trend of the error rates, projects when the disc will cross into failure, and ranks a whole collection
  by urgency — an actuarial dump-order for a shelf of aging discs. A disc already showing CU is "dump
  now"; a disc whose BLER is climbing gets a projected years-to-failure; a stable disc drops to the
  bottom. Reads scan results and forecasts — it never touches a disc.
- **Hidden-session archaeology** (`hidden-sessions`) — surface the data sessions a normal player or a
  plain audio rip never sees. The classic case is the Enhanced CD / CD Extra layout: session 1 is
  ordinary audio tracks and a second session tucked behind them carries a data track (bonus content, a
  PC installer, videos) that an audio ripper stops short of and a single-session read never reaches. It
  maps every session from the disc's cue+bin, classifies each (audio / data / mixed), measures the
  lead-out/lead-in gap between them, and flags every data session that isn't the first as hidden — the
  thing to extract and preserve rather than discard. Data in the *first* session (ordinary mixed-mode)
  is not flagged; a second, third… data session is. Exits non-zero when hidden data is present so a
  script knows a plain rip would be incomplete. Detection and mapping only — it defeats nothing.
- **DAT-less content clustering** (`disc-cluster`) — take a messy folder of un-identified dumps and
  group the ones that are the same title (its regions, revisions, budget re-releases) with no external
  DAT/database. It compares discs by what they actually contain: the set of files by path (region
  variants keep the same directory layout and filenames) and by content hash (they share most of the
  same file bytes, differing only in the localised pieces), with a small volume-id nudge. Variants of
  one title score high and link; unrelated titles share almost nothing and stay apart. Clustering is
  the connected components of the above-threshold similarity graph (single-linkage), each group labelled
  from its members' volume ids and reported with its weakest in-group link as cohesion. Identification
  by self-similarity, not by a reference list — offset-invariant, needs nothing but the discs.
- **Error-pattern forensics** (`error-pattern`) — classify a disc's failing sectors by the *shape*
  of the pattern they form, so you know whether to fight them or keep them. A radial scratch takes out
  a near-solid burst of adjacent sectors; surface rot scatters pinholes irregularly; a copy-protection
  scheme places its bad sectors at regular, repeating intervals — a periodicity stochastic damage does
  not produce. It segments the failures into lesions, classifies each (scratch / surface-rot /
  deliberate-pattern), catches even a wide-pitch protection comb whose sectors fall into separate
  clusters, and returns a verdict (Scratch / SurfaceRot / DeliberatePattern / Mixed) with a
  recommendation that respects the clean-room line: recover the physical damage, preserve the
  deliberate pattern as-is (repairing a protection pattern would corrupt a faithful dump). Works from a
  raw EDC scan or a `reconstruct --provenance` map; exits non-zero when the failures look deliberate so
  a script never blindly "repairs" protection. Categorises and recommends — decodes and defeats nothing.
- **Temporal / mastering fingerprinting** (`disc-date`) — reads the timestamps and identifiers
  an ISO 9660 image was pressed with: the volume creation/modification/expiry/effective dates, the
  recording date of every catalogued file, and the system/publisher/preparer/application strings —
  then flags the contradictions that reveal a disc was quietly altered after mastering. The signature
  tell is a file dated *after* the volume was created (on an untouched master every file predates the
  volume, so a newer file means part of the disc was rebuilt); it also flags a missing volume date, a
  modification date preceding creation, a file-date span too wide to be one mastering, and implausible
  future dates. Detection-only forensics — it reads what is there and reports; it changes and defeats
  nothing.
- Earlier this session: VOB/MPEG-PS demux (`vob-demux`), VCD control files
  (`vcd-control`), DVD IFO editor (`dvd-ifo`), PS1 memory-card formatter (`psxmc-format`),
  unit-tested SET CD SPEED, and GUI tiles for the above.

Together: **prove faithful → watch for rot → store deduplicated & self-verifying →
recover the scratched → grade the dump.** A preservation platform, not just a tool.

---

## Esoteric forensics round

A batch of esoteric, clean-room forensics — all Core-tested, all with CLI commands.

- **Covert-channel / hidden-data sweep** (`covert-scan`) — hunt for data concealed where the format
  expects zeros: file slack (the tail of a file's last sector), the ISO system area, reserved fields.
  Surfaces stashed payloads, watermarks and old hidden tracks with an entropy read. Detection only.
- **Sector "matter" map** (`matter-map`) — classify what *kind* of data each region is (padding / text /
  structured / high-entropy compressed-or-encrypted) from entropy + byte distribution, rendered as a
  coloured SVG strip. The disc's anatomy at a glance; it classifies, never decrypts.
- **Disc phylogenetics** (`phylo`) — reconstruct a title's family tree from file-level distances via
  average-linkage (UPGMA): near-identical variants sit as siblings, revised pressings branch higher.
  Newick + indented output. (Correctly places a byte-exact rebuild at divergence 0.)
- **image-lint** (`iso-lint`) — a strict ISO 9660 conformance checker: magic, both-endian field
  agreement, block size, descriptor terminator, volume/root bounds — every deviation with a severity.
- **Dump-tool fingerprint** (`dump-provenance`) — infer what produced a dump from its container format
  (.ccd/.img/.sub = CloneCD, .mds/.mdf = Alcohol, a submission-info = Redump…) and sector geometry.
- **Pregap / hidden-track forensics** (`pregap-scan`) — detect the audio some CDs hide in track 1's
  pregap (HTOA) or an inter-track gap, that ordinary rips drop. Reads the raw PCM and calls silence
  vs. real signal.

Also fixed this round: **`to-ccd`** now writes a complete, correct CloneCD set itself (`.ccd` + a
2352-byte `.img` from program LBA 0 + a separate 96-byte `.sub`), instead of pointing at `build-raw`,
which produced a combined-interleaved image with lead-in and no `.sub`; and the **DVD structure**
reader's subtitle-language parse bug (every disc read blank because it tested the wrong bit) plus its
raw-record-dump formatting.

- **DVD navigation / hidden-PGC map** (`dvd-nav`) — read a VTS IFO's navigation tables (unencrypted) and
  map its program chains, flagging any that are neither a title entry point nor referenced by a title:
  content physically on the disc that normal playback never reaches (hidden cuts, dev leftovers,
  region-gated sequences). Parsed to the DVD-Video spec (VTS_PGCIT + VTS_PTT_SRPT) with defensive
  bounds-checking, validated against synthetic IFOs built to spec. Reads the table of contents only.

That completes the whole esoteric list. (When a real `VTS_*.IFO` is handy, worth a confirmation run
against actual disc structures, but the parse follows the spec and is fully covered by tests.)

---

## Physical-encoding deep dive

Down below the byte level, into the channel code the laser actually reads — territory almost no
preservation tool touches. All clean-room modelling, all Core-tested.

- **EFM channel codec** (`Efm`) — Eight-to-Fourteen Modulation: each byte → a 14-bit channel word obeying
  the run-length rule (3T..11T pit/land lengths), with 3 merging bits between words chosen to hold that
  rule across the boundary *and* keep the Digital Sum Value (the DC balance the servo depends on) near
  zero. Encode/decode round-trip, run-length validation, DSV measurement. (The byte→codeword assignment
  is a canonical enumeration of the valid words, not the licensed ECMA table; the run-length/DSV physics
  it models — which is what governs readability — is faithful.)
- **Weak-sector prediction** (`weak-sectors`) — models copy protection at the layer where it actually
  lives. A SafeDisc-style weak sector is data whose *scrambled* form (ECMA-130 CD scrambler), once EFM-
  encoded, yields a channel stream with too few transitions and a wandering DSV — so drives read it
  unreliably. This runs scramble → EFM → DSV for each sector and flags the outliers: the deliberately-weak
  sectors, predicted from the data alone. (Cross-validated end to end: content equal to the scramble
  sequence collapses to all-zeros and is correctly flagged; an independent Python reimplementation of the
  scrambler LFSR reproduces the same weak sector.) Pure modelling and detection — it explains the physics
  and defeats nothing.
- **Reed-Solomon GF(256) codec** (`ReedSolomonGf256`) — a general errors-and-erasures RS(n,k) decoder
  (primitive poly 0x11D, Berlekamp-Massey → Chien → Forney) — the maths core of CD error correction.
  Rigorously tested: corrects up to (n−k)/2 errors, up to (n−k) known erasures, mixed, and reports the
  uncorrectable honestly.
- **CIRC recovery model + oracle** (`recover-oracle`) — models why a scratch that would obliterate a plain
  RS codeword is shrugged off by a CD. CIRC's two stages (C1 = RS(32,28), C2 = RS(28,24)) are separated by
  a cross-interleave that delays each C2 symbol by 4·j frames, smearing one codeword across ~109 frames;
  a physical burst, de-interleaved, hits each C2 codeword with only ~burst/4 erasures. The oracle computes
  that erasure load and gives the verdict (corrected vs interpolation), and a real simulation on the RS
  codec proves it — a 12-frame burst that would wipe a single codeword is fully recovered once interleaved
  (correctable to 16 frames for this interleave), and the simulation's measured erasure load matches the
  oracle's prediction exactly. The quantitative recoverability oracle the weak-sector work pointed toward.
- **Sub-channel TOC recovery** (`recover-toc`) — rebuild a disc's table of contents from the program-area
  Q sub-channel when the lead-in is dead. The lead-in holds the TOC, but the same track/index/time
  addressing repeats in every sector's Q channel — so a disc a drive refuses to give a TOC for can still
  be laid out: walk the body's Q frames, keep the CRC-valid ones, and read off where each track's index 1
  begins and whether it is audio or data. Each track's *exact* start comes from its relative time
  (absolute − relative, modal-voted), so it survives even the boundary frame being corrupted. A
  "won't-mount" disc becomes preservable. Cross-validated against an independently-built damaged
  sub-channel. Reads and reconstructs; changes nothing.
- **Scratch recovery outlook** (`scratch-verdict`) — turns the physical-layer models into a practical
  per-region verdict on a real dump. It joins the error-shape classifier (`error-pattern`) to the
  correction models: an audio region rides on CIRC, so the oracle decides whether a scratch is corrected
  outright, concealed by interpolation, or an audible loss; a data region has no concealment — single-read
  RSPC ECC may repair it, else re-read / reconstruct; a deliberate pattern is left alone. "This scratch
  spans N sectors" becomes "corrected / concealed / re-read", split by track type. Advisory only — the
  capstone that makes the scrambler → EFM → weak-sector and RS → CIRC → oracle stacks actionable.
- **Red Book auditor** (`redbook-audit`) — the physical-CD-layer sibling to `image-lint`: where image-lint
  checks a filesystem image against the ISO 9660 grammar, this holds a disc's TRACK structure up against
  the Red Book (IEC 60908) rules and reports every deviation with a severity. It checks the track count
  (1–99) and sequential numbering from 1, that every track carries an INDEX 01, the 4-second (300-sector)
  minimum track length and 2-second (150-sector) minimum pause, the MCN (13 digits) and ISRC
  (CC-XXX-YY-NNNNN) grammar, and data/audio ordering (data first for mixed-mode, or a later session for
  CD-Extra — never wedged between audio tracks). Reads structure from a cue sheet, checks lengths when the
  image is present. Validates and reports; changes nothing.
- **Recovery map** (`recovery-map`) — the visual capstone for the scratch-verdict stack, and the sibling to
  the EDC health map. Where the health map paints intact / damaged / repaired, this paints what can be
  *done* about each damaged region: an audio burst CIRC corrects is green, one interpolation only conceals
  is amber, one beyond concealment is red; a data lesion (ECC / re-read / reconstruct) is orange; a
  deliberate protection pattern is purple, flagged to preserve. Clean sectors stay dark so the eye goes
  straight to the regions that need a decision. Audio vs data is read per sector from the sync mark, so
  mixed-mode discs map correctly; a standalone SVG with a legend of the outlooks present. Visualises the
  advisory models; recovers nothing itself.
- **Premaster gate** (`premaster-check`) — the go/no-go check a mastering engineer runs before cutting a
  glass master. It folds the Red Book structural audit together with the two things that stop a press run:
  the program must fit inside a pressable CD (74:00 nominal / 80:00 maximum Red Book capacity), and every
  data track must be physically intact — a single sector failing EDC/ECC is a defect that would ship on
  every copy. MCN and per-track ISRC are flagged as master-hygiene advisories, never blockers. One
  verdict: ready, or exactly what disqualifies it. Deliberately does NOT emit a DDP fileset — DCA's DDP
  binary is a proprietary format outside the clean-room boundary; the gate works entirely from open
  formats (cue + image) and open Red Book limits. Validates and reports; changes nothing.
- **LibCrypt analyzer** (`libcrypt`) — the deep read of a PlayStation disc's subchannel protection, sitting
  above the raw-subchannel shape detector and the SBI sidecar writer. It *characterises* LibCrypt rather
  than just spotting it: it separates the two generations (first-gen alters the Q address but keeps the CRC
  valid, so it only shows as an address disagreeing with the sector's true position; second-gen breaks the
  CRC outright), measures each affected sector's Q against the clean value it should have carried, and
  reconstructs the key material from the per-sector CRC deltas. It emits a stable, translation-invariant
  16-bit fingerprint (CRC-16 over the relative-LBA/CRC-delta records) so two rips of the same disc match
  and a database can look it up, and it bridges to the existing SBI writer for the emulator sidecar.
  Preservation, the opposite of circumvention: it reads and describes the disc's own protection so a
  faithful reproduction carries exactly what the original did — a disc without LibCrypt yields an empty
  report. Removes nothing, patches nothing, defeats nothing.
- **EFM spectrum** (`efm-spectrum`) — the physical-quality read one layer below the bytes, built on the EFM
  channel codec. It encodes data through EFM and measures the shape of the resulting pit/land stream: the
  3T..11T run-length spectrum (with an ASCII histogram — the textbook monotonic decay from I3 to I11), the
  pit/land duty asymmetry (the DC balance the RF eye inherits, the encoding-domain analogue of β), the DSV
  excursion (the servo's headroom), the spectral entropy (whether energy spreads across run lengths or
  piles onto one), and a coarse grade. With --scramble it CD-scrambles each sector first, modelling how
  data physically sits on the disc. It is explicit about its domain: from ideal channel bits it derives the
  structural properties that bound readability — it does not measure true analog jitter or a real RF eye's
  β (those need the recovered HF signal); it models the substrate they ride on. Read-only.
- **BLER surface-quality report** (`bler`) — the archival health verdict a plant or archivist quotes,
  built on the CD's two-stage CIRC correction. It ingests a per-second C1/C2 error scan and tallies the
  six standard classes: BLER (the C1 block-error rate — every C1 frame with a bad symbol, of the 7,350 per
  second, Red Book ceiling 220/s), E22 (a C2 frame that needed its full two-error correction — a warning),
  and E32 (a C2 failure — an uncorrectable error, which must never occur on a conformant disc). It reports
  average/peak/95th BLER, the totals, the longest error burst, and the Red Book pass/fail plus an archival
  grade. The classifiers use the true RS(32,28)/RS(28,24) capacities. Honest about its domain: BLER is a
  drive READ-TIME metric — it cannot be recovered from an already-corrected image, so this judges a
  captured scan rather than inventing errors from a rip. A flexible CSV parser (header-mapped or a minimal
  second,bler,cu form) reads the scans real quality-scan tools export. Read-only.
- **DPM — Data Position Measurement** (`dpm`) — the disc's physical layout read from read-timing. Because a
  drive reads a spinning disc, the time each sector takes traces where it physically sits; plotting read
  speed against LBA reveals rings and bands the logical image can't show. Ring-based copy protections
  (SecuROM, StarForce) write data at a fixed radius, so a genuine disc shows a sharp, repeatable slowdown
  a naive burn can't reproduce. This ingests a per-position read-speed scan, fits a local (moving-median)
  baseline, flags the regions reading markedly slower, and decides whether the shape is a deliberate ring,
  broad surface damage, or a clean profile — plus a scale-invariant fingerprint of the profile shape, so
  two dumps of one disc match even from drives of different speed and a copy off a different master is told
  apart. Measurement and fingerprinting for preservation/verification: it detects and records a ring so a
  faithful copy can be checked against the original; it circumvents nothing, and (like BLER) needs the
  dumper's timing scan since a finished image carries no timing. Read-only.
- **El Torito boot catalog** (`boot-catalog`) — the reader for a bootable disc's boot structure, the
  counterpart to the ISO 9660 volume grammar. A bootable disc plants a Boot Record volume descriptor at
  sector 17 pointing to a boot catalog; the catalog opens with a validation entry (whose sixteen 16-bit
  words must sum to zero) and lists boot options — the default entry plus any platform sections for a
  multi-boot disc (a BIOS x86 image and a UEFI image side by side). Each entry says whether it is bootable,
  what firmware it targets (x86/PowerPC/Mac/EFI), whether it emulates a floppy or hard disk or boots with
  no emulation, and where its boot image lives. This finds the catalog, verifies the checksum, and decodes
  every entry — for identification and preservation of install/live media. Reads and reports; changes
  nothing, and a non-bootable image yields null.
- **XA stream map** (`xa-map`) — the multimedia read of a CD-ROM XA disc, the structure behind PlayStation
  FMV, Video CD and CD-i. Every Mode 2 sector carries an 8-byte subheader (file, channel, submode,
  coding); the submode bits split the sector into video/audio/data, mark Form 1 (2048-byte, with EDC)
  versus Form 2 (2324-byte, no EDC), and flag the end of each record (EOR) and file (EOF). Real-time titles
  finely interleave several (file,channel) streams so the drive feeds video and audio together in one pass.
  This walks the sectors, tallies each stream by kind, reads the first audio coding it sees (sample rate,
  mono/stereo, bit depth), and measures how tightly the streams interleave — turning a raw image into a map
  of what plays and how it is laid out. Handles raw 2352 and headerless Mode 2 2336 geometry. Read-only.
- **CD-TEXT reader** (`cdtext`) — the decoder that turns CD-TEXT back into album and track metadata, the
  counterpart to the CD-TEXT builder. CD-TEXT rides in the lead-in's R–W sub-channels as 18-byte packs;
  each names its type (title, performer, songwriter…), the track it belongs to, its running sequence, and
  carries twelve bytes of text plus a CRC, with the strings of a type flowing NUL-separated across packs
  (album first, then one per track). This validates each pack's CRC, drops the repeats the lead-in loops
  through, reassembles the size-information pack (first/last track, language, character set), and stitches
  the fields back into per-track text. It reads a flat pack stream (skipping a 4-byte .cdt header) or
  reverses the six-bit R–W symbol packing to read a captured lead-in — a clean round-trip against the
  builder, cross-checked against an independent encoder. Read-only.
- **CD de-emphasis filter** (`deemph`) — a DSP restoration for pre-emphasised discs. Many 1980s CDs were
  mastered with pre-emphasis (highs boosted before recording, to be cut on playback); a flat digital rip of
  one sounds bright and harsh until de-emphasis is applied. This implements the exact analog de-emphasis
  transfer function H(s) = (1 + s·T2)/(1 + s·T1) with T1 = 50 µs and T2 = 15 µs, discretised by the
  bilinear transform into a first-order IIR — flat at DC, sloping to the −10 dB (15/50) shelf at high
  frequency. Because it is derived from the transfer function rather than hard-coded coefficients, its
  response is verified against the analog target it must match, and against a measured tone (a 13 kHz tone
  attenuates by ~−9.4 dB, on the curve). Apply only to tracks the disc flags as pre-emphasised (Q control
  bit 1 / cue PRE); it restores the intended flat response and changes nothing about a normal track.
- **Silence-based track splitter** (`silence-split`) — recovers track boundaries from a gapless album rip
  (a needle-drop, a single-file CD image, a live set) by finding the silent gaps between songs. It frames
  the audio, measures each window's peak, marks windows below a level threshold, and treats a run of
  silence longer than a minimum as a track boundary — while short intra-song pauses stay inside their
  track and leading/trailing silence is trimmed. It reports each track's audio span and the preceding gap,
  and emits a cue sheet whose INDEX 00 marks the pregap and INDEX 01 the audio onset, snapped to CD
  sectors (1/75 s). Threshold, minimum-gap and minimum-track are all tunable. Analysis only — it locates
  and describes boundaries, it does not cut the audio.
- **Sega CD / Mega-CD header** (`segacd-info`) — the boot-header reader for Sega's CD console, alongside the
  existing Saturn and Dreamcast headers. The data track's first sector opens with the boot signature
  ("SEGADISCSYSTEM" / "SEGABOOTDISC") and carries the standard Sega hardware header at offset 0x100 — the
  console name, copyright and build date, domestic and international titles, product code and checksum,
  supported input devices, and the region field. It decodes the region both ways: the classic J/U/E letter
  style and the later single-hex-digit bitfield (Japan/US/Europe), always preserving the raw field. Reads a
  bin/cue or a raw data track / ISO; identification only, it reads and reports.
- **VCD PlayBack Control** (`vcd-psd`) — the decoder for a Video CD's interactive layer, alongside the
  existing INFO.VCD/ENTRIES.VCD support. PBC lives in PSD.VCD as a chain of list descriptors: a play list
  plays a sequence of items and links to the previous/next/return list; a selection list is a menu whose
  numbered selections each jump to another list; an end list closes a sequence. Descriptors reference one
  another by offsets counted in 8-byte units (0xFFFF = none), and LOT.VCD indexes lists by LID. This walks
  PSD.VCD descriptor by descriptor, decodes each type (including the SVCD extended selection list), and
  resolves the offsets so the menu graph — which selection jumps where — can be read out and named by LID.
  Follows the VCD 2.0 / White Book PSD structure. Read-only; it parses and reports.
- **3DO Opera file system** (`opera-ls`) — the reader for the 3DO console's own CD layout, in place of ISO
  9660. A volume label sits in block 0 (record type 1, the "ZZZZZ" sync, volume name, block size/count,
  and the avatar block-address copies of the root directory); each directory is a run of blocks, every
  block headed by a small record (links + first-free offset) followed by fixed entries — flags carrying
  the file/directory type, byte and block counts, a 32-byte name, and the entry's own avatar list. All
  fields are big-endian, as the console is. It validates the label, walks the root directory and recurses
  into subdirectories (with cycle and depth guards), and returns the full file tree with sizes and type
  tags. Reads a cooked 2048 image or a raw 2352 Mode 1 image (whose user data it extracts). Read-only.

---

## Game-changers — the six bold features

A round of ambitious, platform-redefining features — all clean-room (measure, identify, verify,
preserve; never defeat), all Core-tested, all with CLI commands.

- **Federated preservation consensus** (`consensus`) — a decentralised, cryptographically-verifiable
  alternative to a central preservation database. Each dumper signs an attestation binding a disc's
  offset-invariant genome identity to their own public key; the attestations collect in an append-only,
  hash-linked ledger anyone can verify. When independent keys who never coordinated attest the same
  genome, that agreement *is* the proof of the canonical image — trust becomes arithmetic. Also flags
  disputes (two images claiming one title). `consensus keygen | attest | verify`.
- **Physical-copy fingerprint** (`disc-print`) — identify the individual disc, not just the title. From
  the positional pattern of C1/C2 read errors it builds a per-copy "defect constellation": two scans of
  the same disc share it (defects don't move; rot only adds), a different copy of the same title has its
  errors elsewhere. Reports distinctiveness, so a near-flawless disc is honestly called unfingerprintable
  rather than guessed. Anti-counterfeit provenance for rare media.
- **Collection-scale dedup archive** (`collection-archive`) — collapse a whole library (every revision,
  region, sampler) to the set of genuinely-unique file blobs plus a tiny per-disc recipe, while every
  disc stays reconstructable byte-for-byte. Shared files are stored once no matter how many discs carry
  them; a variant-heavy collection shrinks several-fold and gets *more* verifiable. Draws the
  relationship graph too. `build | verify | extract`.
- **Self-healing preservation container** (`vault`) — wrap an image in Reed-Solomon parity (GF(256),
  systematic Cauchy) so the archive repairs its own bit-rot: any k of the k+m blocks rebuild the exact
  original. Every block is hashed for silent-corruption detection; the image SHA-256, genome id and
  lineage digest travel inside, so a healed image proves it is still authentic. A format designed to
  outlive its medium. `create | check | heal`.
- **Software bill-of-materials** (`disc-bom`) — a technical dossier for a disc the way a modern build has
  an SBOM: the engine (Unreal/Unity/RenderWare…), middleware (Bink/Smacker, Miles/FMOD, Havok/PhysX), the
  compiler runtime, the platform's asset pipeline (a PlayStation disc's STR/XA/VAG files give it away),
  and — from the disc's own timestamps — when it was mastered.
- **Ring-code parser + pressing linkage** (`ring-code`) — decode the IFPI mastering SID (glass master)
  and mould SID (pressing plant) plus the matrix string from a disc's inner-ring text (typed, or OCR'd
  from a ring photo at the app layer), validate the SID formats, link them to the genome, and group a
  collection by shared plant or shared master. The gold standard of pressing identification, automated.

---

## Esoteric backlog — buildable now (clean-room, Core-testable)

**Cleared.** Every item that was buildable now without external samples/hardware has shipped —
temporal / mastering fingerprinting, error-pattern forensics, DAT-less content clustering,
hidden-session archaeology, disc-rot triage and the copy-protection fingerprint catalog (see
"Recently shipped"). What remains is gated on external input (below) or is a deliberately larger
follow-on (deep UDF/HFS tree walking). New esoteric ideas land here as they come up.

Shipped since: **twin-sector / header-address forensics** (`twin-scan`) — detects SafeDisc-style twin
sectors (two sectors claiming one address) and re-addressed sectors straight from a raw image, after
establishing the image's own base offset so a shifted dump isn't mistaken for tampering; and
**protection cross-check** (`protection-scan --raw`) — fuses the filesystem catalog, the error-pattern
shape and the header-address scan into one verdict (Corroborated / FilesystemOnly / PhysicalOnly / None),
so filesystem marks backed by a physical on-disc signature read as confirmed, and the mismatches get
flagged. Both clean-room detection only.

**Fresh-vein run (shipped).** A long sequence of clean-room, Core-tested additions across the protection,
channel, correction, physical, boot, multimedia, metadata, DSP and filesystem layers: the LibCrypt
analyzer (`libcrypt`), EFM run-length spectrum (`efm-spectrum`), C1/C2 BLER surface-quality report
(`bler`), Data-Position Measurement (`dpm`), the El Torito boot-catalog reader (`boot-catalog`), the
CD-ROM XA stream map (`xa-map`), the CD-TEXT reader (`cdtext`), CD de-emphasis (`deemph`), the
silence-based track splitter (`silence-split`), the Sega CD / Mega-CD header (`segacd-info`), the VCD
PlayBack Control decoder (`vcd-psd`), the 3DO Opera file system reader (`opera-ls`), and the HFS
free-space orphan carve (`hfs-orphans`).

Shipped since: **Amiga RDB partition parser** (`rdb-info`) — finds the 'RDSK' block in the first 16
blocks, verifies the additive checksum, reads the drive geometry and vendor strings, and walks the linked
'PART' list (with a loop guard), decoding each partition's BCPL name, bootable flag, DOSType (DOS\0 OFS /
DOS\1 FFS / PFS / SFS…) and cylinder-derived start/size.

Shipped since: **Neo Geo CD IPL.TXT boot parser** (`neogeo-ipl`) — parses the console's boot script (the
ordered list of files loaded at startup, each with its target bank/offset) from an IPL.TXT directly or
located inside a disc image's ISO filesystem; **audio dynamics analyzer** (`audio-dynamics`) — the DR value
(TT/Pleasurize meter), sample peak / RMS / crest factor in dBFS, and clipping detected as runs of
consecutive full-scale samples; and the **CD+G frame-sequence exporter** (`cdg-frames`) — exports a CD+G
stream (or its .sub sidecar) as numbered PNG frames at a chosen fps, completing the animation half over the
existing single-frame renderer.

Shipped since: **protection metadata in preservation manifests** — `PreservationPackage.SetProtection`
records the cross-checked protection verdict (standing, schemes, physical-signature flag, evidence,
guidance) as a `ProtectionRecord` on the manifest, covered by the tamper-evident digest and round-tripping
through JSON; wired into `preserve pack --protection <raw.bin>` (fuses the on-disc error-shape + twin-sector
signals) and surfaced by `preserve verify`. Detection provenance only — it records what was found, and
reproduces/defeats nothing.

Shipped since: **Machine-readable `--json` output** — the read-only analyzers now emit structured JSON on
`--json`, so DiscForge drops into automated preservation pipelines instead of being human-output only:
`disc-report` (identity + every matching probe, assembled), plus `iso-lint`, `iso-pathtable`,
`redbook-audit`, `premaster-check`, `bler`, `dpm`, `audio-dynamics`, `apm-info`, `rdb-info` (their result
records serialized directly, computed fields and all). Clean relaxed-escaped JSON via a shared `EmitJson`.

Shipped since: **The review's three recommended builds — FAT16/32, submission packaging, HDCD detection** —
• **FAT16 / FAT32 reader** (`fat-ls`, `fat-extract`, `Fat.FatReader`): the general-purpose FAT reader that
picks up where the floppy-only `Fat12Reader` stops — the filesystem inside El Torito hard-disk boot images,
a hybrid disc's FAT partition, and card/UMD media. Decides the FAT type from the cluster count, follows 12-,
16- or 32-bit cluster chains, reassembles VFAT long file names, and recurses the tree. Validated against
**real FAT16 and FAT32 images built with mkfs.fat + mtools** (type, label, enumeration, long names, subdir
recursion, byte-exact extraction), with a real FAT16 image embedded in the regression test.
• **Submission packager** (`submission-pack`, `Redump.SubmissionPackage`): the packaging layer over the
existing `submission-info`. Assembles the folder a preservation submitter actually needs — the dump file(s)
(a .cue's referenced files copied too), the redump-style info text, a matching Logiqx DAT, and the cuesheet,
all named from the game. Validated end-to-end: build a bundle from a real ISO, then `dat-verify` the copied
dump against the bundle's own DAT.
• **HDCD detection** (`hdcd-scan`, `Audio.Hdcd`): flags HDCD-encoded audio by scanning the samples'
least-significant bits for the control-code packets, using the published libhdcd/ffmpeg constants. Detection
keys on the **self-checking Type-B** packet (a ~24-bit constraint → essentially no false positives); Type-A
codes are reported but explicitly noise-floored (their ~11-bit pattern hits ~1/2048 samples by chance — the
validation caught this and the detector now ignores it, so ordinary CDs aren't mis-flagged). Validated by
embedding conforming packets (detected, incl. per-channel in stereo) and confirming silence and 2M random
samples are NOT flagged. Confirmation against a genuine HDCD disc is still advisable (flagged, like MDEC).
All three are clean-room and read-only.

Shipped since: **A four-feature batch across cartridges, textures, cataloguing and PSX video** —
• **N64 CIC boot-chip identifier** (`n64-info`, `Rom.N64Cic`): identifies the cartridge's CIC security chip
from the CRC-32 of its 4032-byte IPL3 bootcode (6101/6102/6103/6105/6106 and the PAL 71xx twins), and
recomputes the header's CRC1/CRC2 boot checksums to confirm the ROM is intact — reading any byte order
(.z64/.v64/.n64). The boot-checksum port (6102/6105/6106 branches, overflow, ROL) was cross-checked against
an independent implementation; the CIC table uses the long-published bootcode CRC constants.
• **GameCube/Wii TPL texture decoder** (`tpl-info`, `tpl-extract`, `GameCube.Tpl`): unpacks the console's
tiled texture container to straight RGBA/PNG across the full GX format set — I4, I8, IA4, IA8, RGB565, RGB5A3,
RGBA8 (its AR/GB cache-line split), the palette formats CI4/CI8/CI14X2, and the S3TC-style CMPR — reusing the
RGB5A3 path from the banner work. Every format's tiling/packing validated by byte-exact synthetic textures
with known pixels.
• **Preservation DAT emitter** (`dat-build`, `Dat.DatBuilder`): the write side to `dat-verify` — hashes a
folder of dumps (size + CRC-32/MD5/SHA-1, via the same `ImageChecksums` the verifier uses) into a Logiqx DAT,
turning a collection into its own reference set. Validated by a real end-to-end round-trip: build a DAT from
a folder, then `dat-verify` the files against it.
• **PSX MDEC pipeline + `mdec-info`** (`PlayStation.Mdec`): the validatable core of the STR video path —
the frame-header/geometry parser (codec version, quant scale, macroblock count) plus the transform stack
(inverse zig-zag, PSX de-quantisation, 8×8 IDCT cross-checked against an independent reference to <5e-7, and
4:2:0 YCbCr→RGB). `mdec-info` reports a video's codec parameters per frame. The final VLC pixel-decode layer
is deliberately **gated on a real `.str` sample** — its ~110-entry code table can't be confirmed against a
genuine PlayStation frame in-house, and shipping it unverified would breach the "validate against real data"
bar; it lands when a sample disc is available (like the GameCube dump).
All four are clean-room and defeat nothing.

Shipped since: **Rock Ridge / SUSP reader** (`iso-rockridge`, `Iso.RockRidge`) — the ISO-9660 counterpart to
the HFS/UDF filesystem archaeology. A Unix/Linux CD keeps its *real* filesystem — long case-sensitive names,
POSIX permissions and ownership, symlinks, device nodes and true timestamps — in the SUSP "System Use" bytes
after each directory record's identifier, invisible to a plain ISO reader that sees only truncated 8.3 names.
The previous code pulled only the NM (name) entry from the record's own bytes; this walks the full System Use
Sharing Protocol, **following CE continuation blocks into other sectors**, and decodes the RRIP entries: NM
(name, across CONTINUE fragments), PX (st_mode/nlink/uid/gid → a `drwxr-xr-x` mode string, setuid/setgid/
sticky and all), SL (symlink target reassembled from its component list), TF (timestamps, 7-byte and 17-byte
long forms), and the CL/PL/RE deep-directory relocation markers. `IsoEntry` now carries a `RockRidgeInfo`, and
`iso-rockridge <image>` prints the POSIX view (`--json` too). Validated **end-to-end against a real Rock Ridge
ISO built by the in-repo `IsoBuilder`** (SP/ER/NM/PX/TF round-trip — file `-rw-r--r--`, dir `drwxr-xr-x`,
nested paths intact) plus a byte-exact synthetic-SUSP suite for the entries the builder doesn't emit (SL, CE
continuation, TF long form, CL/RE), and added to the fuzz harness (now 24 parsers). Reads and reports only.

Shipped since: **HFS resource-fork reader** (`hfs-resources`, `HfsResourceFork`) — the deep-archaeology
follow-on. Every ISO 9660 / Joliet / data-fork tool silently discards the *resource fork* — the second data
stream classic Mac files carry — so the most interesting half of a Mac hybrid disc (icons, version stamps,
code, dialogs, sounds, Finder bundle info) was invisible even after `hfs-ls` enumerated the tree. This opens
each fork and walks its real structure (header → resource map → type list → per-type reference lists → name
list, per Inside Macintosh) into a flat catalogue of every resource with its four-character type, id, name
and length; `HfsReader` now captures the resource fork's extent record so the bytes can be pulled from the
image, and the `'vers'` resource is decoded to its human-readable version string (e.g. `1.0, © 1994 …`).
`hfs-resources <image> [macpath]` lists them (grouped by type, `--vers` for just the version stamps,
`--json` for the structured dump). Validated end-to-end against a **real hfsutils-made HFS volume** carrying
a MacBinary-imported resource fork — the CLI read it back byte-for-byte — plus a byte-exact synthetic-fork
unit suite, and added to the fuzz harness (now 23 parsers) so a malformed fork can never hang or over-read.
Structure only; it decodes and reports, and defeats nothing.

Shipped since: **Command reference (`docs/COMMANDS.md`)** — a single generated catalogue of all **171**
CLI commands, auto-built from the CLI's own no-args help and bucketed into twelve task-oriented sections
(disc identity, console readers, filesystems & partitions, convert/create, verify/preserve, conformance,
forensics, recovery, raw sectors, multimedia, audio, patches/saves). Notes the `--json` availability and
carries the clean-room disclaimer at the top. Regenerate after adding commands — nothing is hand-listed, so
it can never drift from the actual CLI surface. Closes the discoverability gap: the full command set was only
findable by scrolling raw help.

Shipped since: **Robustness / fuzz harness** — `FuzzRobustnessTests` throws ~700 adversarial inputs
(empty, truncated, all-zero, random, all-0xFF, and valid-magic-prefixed to reach deep paths) at 30 binary
parsers and asserts none hang (infinite loop) or exhaust memory (unbounded allocation from a malformed
length/count field) — the two failure modes that would let a damaged/hostile disc image DoS the tool. All
pass: the length guards hold. A permanent regression guard against future parsers regressing on either.

Shipped since: **GameCube boot metadata + file extraction** — `GcBoot` reads the apploader (fixed 0x2440:
build date, entry point, size) and the DOL executable (header 0x420: entry point, code/data section counts,
total size, BSS), now shown inline by `gcm-info`; and `gcm-extract <image> <out-dir> [--only]` pulls the
disc's FST file tree out to a folder, preserving the directory structure. Rounds out the GameCube read
path (header + region + banner + boot + FST extract) ahead of the real dump. Unencrypted data only.

Shipped since: **GameCube banner + region** (`gcm-banner`, folded into `gcm-info`) — reads a disc's
`opening.bnr`: the human-facing title, developer and description (one language for BNR1, six for BNR2) and
the 96×32 icon, de-tiling the console's 4×4-block RGB5A3 texels and decoding them to RGBA/PNG. Plus a
region decoder from the game code's fourth character (E=USA, P=Europe, J=Japan…). `gcm-info` now prints
region + banner metadata inline; `gcm-banner <image> <out.png>` extracts the icon. Prepared ahead of a
real GameCube dump arriving (a GDR-8162B + FriiDump is inbound). Clean-room — unencrypted metadata only.

Shipped since: **ISO 9660 path-table auditor** (`iso-pathtable`) — the structural companion to
`image-lint`. It parses the Type-L (little-endian) and Type-M (big-endian) path tables, checks they
describe the same directories at the same extents with the same parent links, validates the parent
references form a proper hierarchy-ordered tree, confirms the declared size, and cross-checks that each
entry's extent actually opens with a "." directory record self-referencing it. Validated against a real
`IsoBuilder`-made ISO with nested directories, plus a corruption-detection case.

Shipped since: **Apple Partition Map parser** (`apm-info`) — reads the Mac/hybrid-CD partition scheme
(the map that points at the HFS/HFS+ partition): the Driver Descriptor Record, then each self-describing
'PM' entry's name, type (Apple_HFS / Apple_Free / Apple_partition_map…), block extent and status, probing
the block size (512/2048/…) so both hard-disk and CD-geometry maps read. Completes the partition family
(MBR/GPT/APA/RDB → +APM) and folds into `disc-report`. *(An HFS+ reader was scoped but deferred: the
sandbox can't mount hfsplus to populate a real volume for validation, and I won't ship a filesystem reader
on weaker evidence than the classic-HFS one got — it waits on a real HFS+ hybrid sample.)*

Shipped since: **`disc-report`** — a capstone that identifies a disc image (via `FormatIdentifier`) and
then runs every read-only parser that matches it (Saturn / Sega CD console headers, 3DO Opera FS, El Torito
boot catalog, HFS and UDF filesystem + free-space orphan carve) in one consolidated report, each probe
guarded so one non-match never breaks the rest. Pure composition of already-tested commands.

Still open — buildable-now fresh veins: mostly exhausted; new esoteric ideas land here as they surface.

---

## Gated — needs external input (not blocked on effort)

- **CTDB audio verification** — a second-source rip check alongside AccurateRip. Needs a
  real rip with a *published CTDB CRC* to validate the exact algorithm before building, so
  it never reports "mismatch" on a perfect rip.
- **Full VCD image assembly** — the control files (`vcd-control`) exist; assembling a complete
  player-verified VCD image (MPEG track in Mode 2/Form 2 + ISO tree) needs a reference VCD to
  diff against.
- **ECM / RVZ decode, LaunchBox exporter** — need sample files to build against.
- **PSX MDEC full pixel decode** — the transform pipeline (IDCT/dequant/zig-zag/YCbCr) and the frame-header
  parser are shipped and validated (`mdec-info`, `PlayStation.Mdec`); the remaining VLC bitstream layer needs
  a real `.str` frame to confirm its ~110-entry code table before it can decode to PNG with confidence. Drop a
  sample `.str` (e.g. from the eBay PlayStation disc) and this becomes `mdec-decode <frame> <out.png>`.
- **Physical dumping & the burn round-trip** — need a real optical drive and a real disc
  (the eBay PlayStation disc). When it lands: dump → `dump-score` → `dump-merge` if scratched
  → convert → burn → compare hashes → `preserve pack`.
- **Deep UDF / HFS archaeology** — *the directory readers are now in.* UDF has long had a full
  recursive reader (`UdfReader` — Blu-ray/UDF 2.50 metadata partitions and all), and classic Mac **HFS**
  now has one too (`HfsReader` / `hfs-ls`): it reads the Master Directory Block and walks the catalog
  B-tree to enumerate every folder and file with its data-*and*-resource-fork sizes and full Mac path —
  the Mac side of a hybrid CD, previously unreadable, is now a fully-enumerable tree (validated against a
  real hfsutils-made volume). **The HFS half of the orphan-data follow-on is now shipped** — `hfs-orphans`
  (`HfsFreeSpace`) reads the volume bitmap and reports the allocation blocks marked free that still hold
  non-zero data (leftover slack / deleted files the catalog no longer lists), merging consecutive leftover
  blocks into regions with byte offsets and non-zero counts. **The UDF-bridge side is now shipped too** —
  `udf-orphans` (`UdfFreeSpace`) does the same carve for UDF: it follows the Anchor → Main Volume
  Descriptor Sequence → Partition Descriptor to the Unallocated Space Bitmap and reports the free blocks
  still holding data. Its bitmap semantics were validated against a real mkudffs volume and are the
  *opposite* of HFS on both axes — a set bit means FREE (not allocated), packed least-significant-first —
  which a guess would have gotten wrong. **Both carves are now unified under one command** — `fs-orphans`
  auto-detects HFS and/or UDF in an image and runs the appropriate carve(s), reporting both on a hybrid.
  The orphan-data vein is complete. **And the resource-fork layer is now shipped** — `hfs-resources`
  (`HfsResourceFork`) opens each file's Mac resource fork and lists every resource inside it (type, id,
  name, size — icons, `'vers'` version stamps, code, dialogs, sounds, Finder bundle info), decoding the
  version stamp to human-readable text. That is the content ISO-side extraction always dropped; the Mac
  half of a hybrid disc is now not just enumerable but *readable to the resource*. Remaining, genuinely
  gated on a sample: the UDF **Extended Attribute / named-stream** space (needs a real UDF volume that
  carries EAs or a Stream Directory to validate against) and the HFS **extents-overflow** B-tree (for the
  rare fragmented fork that spills past its three catalog extents — needs a fragmented volume to build one).

---

## Suggested order for the next session

1. **Unblock the gated items** opportunistically as samples / hardware arrive (a real optical drive and
   disc for the dump→burn round-trip; a published CTDB CRC; a reference VCD; ECM/RVZ samples).
2. Or pick a **candidate next direction** from the backlog note above (twin-sector detection from the
   image, protection metadata in `preserve pack`, error-pattern ↔ protection cross-checking).
3. Or take on the larger **deep UDF / HFS archaeology** follow-on.

*The entire "buildable now" clean-room backlog is now shipped: Reed-Solomon sector correction, disc
archaeology, temporal / mastering fingerprinting, error-pattern forensics, DAT-less content clustering,
hidden-session archaeology, disc-rot triage and the copy-protection fingerprint catalog.*
