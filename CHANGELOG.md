# Changelog

All notable changes to DiscForge are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

DiscForge is a clean-room disc-imaging and retro-preservation toolkit: it images,
identifies, converts, patches and verifies discs and cartridge dumps, and reads
and manages console saves. It **detects** copy protection but never circumvents
it, and never defeats console security or decrypts protected content.

## [Unreleased]

### Added
- **Xbox 360 GOD → ISO** (`god-extract`): reconstructs the XDVDFS disc image from a Games-on-Demand
  package. The block→offset formula is ambiguous by one hash block between public references, so this
  reconstructs with both conventions and writes the result ONLY if it is a valid XDVDFS volume (the
  disc's own descriptor is the oracle), declining rather than emit a shifted, corrupt ISO
  (`GodExtractor`). Decrypts nothing.
- **Wii RVZ structure read** (`rvz-info` on a Wii `.rvz`): maps a Wii disc's partitions
  (DATA/UPDATE/CHANNEL and offsets) from the UNENCRYPTED regions only, via `RvzDecoder.ReadWiiStructure`
  (+ `DecodeUnencryptedPrefix`). No keys, no decryption. The encrypted-ISO rebuild stays declined on
  clean-room / no-circumvention grounds; GameCube `rvz-decode` is unaffected.
- **Cross-feature integration tests** — end-to-end chains (ISO → descriptor → coverage → recover;
  C2 rescue → lossless certificate) hardening feature interactions, plus a session summary and
  turnkey commit plan (`docs/SESSION_SUMMARY_2026-08-12.md`).

### Fixed
- **fs-recover could silently zero unlisted tail content** (found by an adversarial review): the
  free-space reconstruction validated the fill *value* but not that the erased sector was truly free,
  so a tail sector that was actually unlisted file content (incomplete enumeration, a secondary
  namespace) could be overwritten. Reconstruction is now limited to sectors at or beyond the PVD
  Volume Space Size — provably outside the filesystem — so content can never be wiped.
- **c2-merge buffer overflow with >64 reads**: the byte-vote fallback buffer was indexed by read
  number but capped at 64, throwing on 65+ reads of a non-identical sector; now bounded correctly.

### Added
- **XISO undocumented-offset auto-detection**: when none of the four documented Xbox partition bases
  match, `XdvdfsReader` now scans the leading window (up to 64 MiB) for the volume descriptor, so a
  raw dump with an unusual offset auto-detects (an explicit `baseSector` still handles anything past
  the window).
- **Fixture-ready oracle validators** (`docs/FIXTURES.md`): inert-by-default harnesses that
  self-validate the moment a real sample is dropped under `DFORGE_FIXTURES` — an un-scrubbed GameCube
  ISO validates the clean-room junk generator against a real disc, and a real `.rvz`+ISO pair
  validates the RVZ decoder's data path. Joins the existing ECM/MDEC fixture slots.
- **Hardware runbook** (`docs/HARDWARE_RUNBOOK.md`): a turnkey, copy-paste checklist for the
  drive-bound work — rung 7's audio finish, read-offset calibration, bitsetting capture/replay, and
  the full dump→verify→merge→convert→burn→prove round-trip — plus an honest list of the pieces that
  still need a command built.
- **Filesystem-constrained recovery** (`fs-recover <image.iso> --erased <list>`): uses the ISO 9660
  filesystem to make sense of erased/unreadable sectors — reconstructs FREE SPACE under the disc's
  own validated fill convention (declined if the surviving free sectors aren't uniform, and only the
  genuine image tail, never a mid-image gap that could be unlisted metadata), identifies file-content
  sectors by file name and the exact byte range lost, and bounds metadata as such. File data is never
  guessed (`FilesystemConstrainedRecovery`).
- **Physical-coverage proof** (`coverage-proof <image.iso>`): proves the image's structures account
  for every addressable sector exactly once — a stronger property than count reconciliation. Reports
  SILENT GAPS (sectors no structure claims) and OVERLAPS (two structures claiming the same sector — a
  mastering bug or corruption); passes only on an exact partition (`PhysicalCoverage`).
- **UDF 2.60 write** (`create-udf --udf-version 2.60`): the UDF writer now stamps revision 2.60. For
  a mastered read-only image this is 2.50's structure (metadata partition, descriptor version 3) with
  the revision bumped; the 2.60 pseudo-overwrite partition is BD-R *incremental recording*, out of
  scope for whole-image mastering and documented as such.
- **Minimal disc descriptor** (`min-descriptor <image>`): factors an image into constant-fill runs,
  duplicate sectors (back-references) and the genuinely unique sectors that are its irreducible
  content, and reports how much is fill/repetition versus real data — an honest information floor
  for the format as dumped. The descriptor reconstructs the image byte-for-byte, so it is provably
  complete, not lossy (`MinimalDiscDescriptor`).
- **Adaptive re-read controller (Tier A)** (`AdaptiveReread`): the deterministic logic for a
  stubborn-sector re-read strategy — accept once a read validates or consensus covers every byte,
  keep re-reading while the uncertain-byte count is still falling, escalate to the next strategy on
  a plateau or read cap, and give up when every strategy is spent. A pure function of the read
  history, proven against a simulated flaky-sector model; hardware wiring (speed/flags) is Tier B.

### Changed / improved
- **C2 consensus merge now chains the sector's own ECC** (`c2-merge`): when byte-level voting across
  reads still fails EDC on a data sector, the residual errors are handed to the sector's Reed-Solomon
  Product Code with the no-vouch positions as erasures — voting narrows the damage into the RSPC's
  budget, so the two stages together rescue sectors neither manages alone (new `EccRecovered` count).
- **UDF extended attributes + named streams — coverage hardened**: EA and named-stream *writing* were
  already implemented; added many-streams, multi-sector-stream and determinism round-trips, and
  corrected the stale "tag 266 untested" note in docs/UDF.md (the write→read round-trip covers it).

### Added
- **Lossless-conversion certificate** (`verify-convert --report cert.html`): a shareable proof that
  a format conversion preserved every byte — both images are decoded to raw sectors and, when they
  match, a single content SHA-256 attests to both (HTML + JSON). A concrete `dec(enc(x)) ≡ x`
  statement for a round-trip, the conversion-side analogue of the burn certificate
  (`ConversionCertificate`).
- **Emulation-readiness report** (`emu-ready <cue>`): grades whether a dump has what an emulator
  needs to *run* — beyond being physically whole — checking every referenced track present and
  whole-sector, a bootable data track and whether it is raw (2352) or cooked (2048), CD-DA audio
  tracks and their pregaps, and the subchannel a LibCrypt/SBI-protected title needs. Verdict is
  READY / READY WITH CAVEATS / NOT READY (`EmulationReadiness`).
- **DVD-Video navigation tables** (`IfoWriter`): the writer now emits the program-chain navigation
  layer, not just the structural IFO — `VTS_PGCIT` (one PGC per title with its program count =
  chapters, one cell per program, and playback duration), `VTS_C_ADT` and `VTS_VOBU_ADMAP`, with
  coherent pointers, spanning multiple sectors when a title has many chapters. `IfoReader` parses
  the program chains back (`TitleSet.ProgramChains`), so the whole nav layer round-trips; only the
  mux-time per-VOBU sector addresses remain deferred to the dvdauthor runner.
- **Xbox XISO multi-sector directory tables — confirmed done**: the reader and writer already
  handle a directory whose entry table spans more than one 2048-byte sector (boundary padding, the
  BST resolving across sectors); added coverage for the previously-untested cases (a multi-sector
  table inside a *subdirectory*, and root + subdir spanning at once) and corrected the stale
  "unfinished" note in docs/XBOX.md.
- **GameCube junk regenerator, self-validating** (`gc-junk-fill`): a clean-room reconstruction of
  the deterministic junk padding a GameCube disc writes into its gaps (a lagged-Fibonacci PRNG,
  taps k=521/j=32, XOR, warmed per 0x40000 block). Because the PRNG isn't yet confirmed byte-exact
  against a real disc, the fill is **gated by self-validation**: `GcJunkReconstructor` regenerates
  the image's OWN surviving junk first and only fills the scrubbed regions if it matches
  byte-for-byte — a fully-scrubbed image (nothing to check against) is declined on purpose. A wrong
  PRNG constant can therefore only cause a decline, never a silent corruption (`GcJunkGenerator`,
  `GcJunkReconstructor`, building on the existing `gc-junk-map`).
- **`read-raw --track N`**: read one track of a disc by number, taking its start LBA, length and
  field mode (data = Raw, audio = UserData) straight from the TOC. A track's TOC start is its
  INDEX 01, so an audio track's unreadable pregap is skipped automatically — the easy way to read
  one track of a **mixed-mode** disc for verification. Adds a `--field data|audio|auto` override
  for forcing the mode directly (`DiscReader.ReadToc(SptiDevice)`, `RawDiscReader.FieldSelect`).

### Fixed / proven
- **RAW-DAO burn ladder proven on hardware** (Plextor PX-W5224A, see [docs/RAW_DAO.md](docs/RAW_DAO.md)):
  rungs 1–6 and rung 7's data track all **PASS** — transport, plain audio, gapless two-track,
  CD-TEXT + ISRC + MCN, pure Mode-1 data, and the mixed-mode data track — main channel byte-identical
  every time, with only drive-re-derived ancillary sub-channel bytes differing.
- **`raw-verify-readback` scramble-domain normalization**: data sectors are stored scrambled but
  drives return them descrambled on a raw read; the comparator now normalizes scramble state before
  judging, so a byte-faithful data burn reads as **PASS (descrambled-on-read)** instead of a false
  main-channel FAIL. Genuine corruption still fails (won't match in either domain).
- **`raw-verify-readback --partial`**: verify one track of a multi-track disc against a whole-disc
  golden without the un-read sectors counting as dropouts — graded on the overlap only.
- **`raw-verify-readback` empty-read-back guard**: a read-back that overlaps the golden in zero
  sectors now grades **FAIL**, not a vacuous "all 0 compared sectors" PASS.
- **Apple II WOZ reader** (`woz-info`): parse the Applesauce WOZ archival format (INFO / TMAP /
  TRKS / META, optional FLUX chunk) and validate the header CRC-32. Reports disk type, bit
  timing, boot format, and the copy-protection-relevant flags (cross-track synchronization,
  weak/fake-bit cleaning) — WOZ preserves protection faithfully without defeating it
  (`WozReader`). WOZ1 is recognised; full v1 track decode is a follow-up.
- **KryoFlux raw-stream reader** (`kryoflux-info`): decode the KryoFlux flux format — the in-band
  cells (Flux1/2/3, Nop1/2/3, Ovl16) and the OOB blocks (KFInfo, Index, StreamEnd) — and report
  flux-transition count, index pulses, sample clock, inferred RPM and hardware/firmware metadata
  (`KryoFluxStreamReader`). Completes the flux trio (raw `flux pack` + SCP + KryoFlux).
- **PC-98 / PC-88 D88 floppy reader** (`d88-info`): a whole new platform — parse the 688-byte D88
  header (name, write-protect, media type, 164-entry track offset table) and walk each track's
  sector headers to report geometry; multi-disk D88 files are detected (`D88Reader`).
- **SuperCard Pro (SCP) flux reader** (`scp-info`): parse the community flux-capture format —
  header, 168-entry track offset table, per-revolution metadata — validate the file checksum,
  infer RPM from the index duration, and decode a track's flux transitions to nanosecond
  intervals honouring the 0x0000 overflow convention (`ScpReader`). Completes the phase-1
  `flux pack` story with a real flux format; KryoFlux stream is a follow-up.
- **DVD/BD read-back verification** (`dvd-verify-readback`): verify a burned DVD/BD against its
  source image at ECC-block (16-sector) granularity, **layer-break aware** — attributes each
  mismatch to L0/L1, checks the break sits on a legal boundary, treats trailing blank sectors as
  benign padding, and reports an MD5 alongside the sector-level diff. Tells you *where* a burn
  differs, not just that it did (`DvdReadbackCompare`).
- **Clean-room bitsetting** (`booktype-trace`, see [docs/BITSETTING.md](docs/BITSETTING.md)):
  decode a captured SCSI/MMC trace of a drive setting the book type and learn a **verbatim replay
  recipe** from the user's own drive — DiscForge never fabricates vendor book-type bytes. Includes
  an MMC trace parser, an honest analyzer (opcode/field decode + candidate book type), and a recipe
  that reproduces the captured command byte-for-byte, JSON round-tripped (`BookType`, `MmcTrace`,
  `BookTypeBitsetting`, `BookTypeRecipe`).
- **Burn-validation certificate** for `raw-verify-readback` (`--report out.html`, `--json`): a
  shareable, self-contained sector-level proof of a byte-faithful RAW burn (main + sub-channel),
  and the comparator hardened for every real burn-day layout — audio PQ-16 (hardware test #1),
  Packed96, Interleaved96, and multi-track/MCN/ISRC discs (`RawReadbackReport`).
- **Closed-loop RAW-burn verification** (`raw-verify-readback`, see [docs/RAW_DAO.md](docs/RAW_DAO.md)
  and [docs/OUTPERFORM_IMGBURN.md](docs/OUTPERFORM_IMGBURN.md)): compare a raw disc read-back
  against the golden image `build-raw` produced — the full 2352 main channel, EDC/ECC, and every
  Q frame — and classify any difference as main-data / mis-addressed / protection-loss / dropout
  (defects → FAIL) or sub-timing (a drive re-deriving ancillary bytes → PASS with notes). Aligns
  the two by decoded disc address, so a read-back that omits the drive-owned lead-in still lines
  up. This is the verification ImgBurn's MD5-of-user-data cannot do — it never writes a
  sub-channel — and it is the piece that makes DiscForge's RAW burn *provable* on hardware day.
  Fully exercised in CI with synthetic golden/read-back pairs (`RawReadbackCompare`).
- **DVD-Video and BD-Video authoring assemblers** (see [docs/DVD_VIDEO_AUTHORING.md](docs/DVD_VIDEO_AUTHORING.md)):
  - `dvd-video-plan` / `dvd-video-build` — validate a `VIDEO_TS` folder (`DvdVideoLayout`)
    and assemble it into a conformant ISO 9660 + UDF 1.02 bridge with the files in the exact
    DVD-Video on-disc order (Video Manager first, then each title set with its IFO leading
    and BUP trailing). Validated against `udfinfo`/`isoinfo` and by starting-LBA order.
  - **"Fix VTS Sectors" verification** (`DvdVideoIfo`) — reads each IFO's internal sector
    pointers (`VTSI_LAST_SECTOR`, `VTSM_VOBS`, `VTSTT_VOBS`, `VTS_LAST_SECTOR`, and the VMG
    equivalents) and checks them against the actual file layout, flagging a source whose
    IFOs were edited without updating the pointers.
  - `bdmv-plan` / `bdmv-build` — validate a Blu-ray `BDMV` folder (`BdmvLayout`:
    index/MovieObject/PLAYLIST/CLIPINF/STREAM/BACKUP) and assemble it into a **pure UDF 2.50**
    BD-Video image (Blu-ray's filesystem), validated against `udfinfo` (`udfrev=2.50`).
  - **"Fix VTS Sectors" rewrite** (`dvd-video-fix`) — the write half: recomputes each IFO's
    four file-location pointers from the folder's actual file sizes and rewrites them in place,
    then refreshes every `.BUP` as an exact copy of its `.IFO`. Only the whole-file / VOB-location
    pointers move (the IFO's internal PGC/table pointers are left untouched, matching ImgBurn's
    scope); dry-run by default, `--apply` to write. Round-trip tested against the verifier.
  - Assembly of already-authored folders — no transcoding/menu authoring. IFO/BUP ECC-block
    padding remains the one follow-up (coupled to the pointer values; not guessed without a
    mastered-disc fixture).
- Closing ImgBurn parity gaps (pure, testable half; execution on real drives follows the
  burn engine's existing "pending hardware" status):
  - `layerbreak-pick` — choose a legal dual-layer break: the cell boundary nearest the
    balance point (or a `--target`), both layers within one layer's capacity, on a
    16-sector ECC boundary; falls back to the nearest ECC boundary for a plain data DL
    image. (`LayerBreakPlanner`.)
  - `capacity-check` — compare an image (in 2048-byte sectors) to media capacity:
    fits / underburn / overburn / too-large, with an `--overburn` allowance within a
    drive/media tolerance. (`BurnCapacity`.)
  - **Low-level write-knob command construction** — `WriteParametersPage` (the MMC 0x05
    mode page: write type DAO/TAO/RAW, test-write, BURN-Proof, link size, session format)
    plus `ModeSelect10` / `SendOpc` / `ReserveTrack` / `CloseTrackSession` CDB builders,
    byte-tested against the MMC layout. These feed the native RAW-DAO engine; execution
    over SPTI is Windows/hardware.
  - **UDF 1.50 / 2.00 / 2.01 / 2.50 write** — `create-udf --udf-version`
    (`UdfBuilder.UdfRevision`). 1.50 differs from 1.02 only in the recorded revision;
    2.00/2.01 use ECMA-167 3rd-edition descriptor tags (version 3) and an Extended File
    Entry for every node; **2.50 (Blu-ray) wraps the content in a Metadata Partition** —
    a Type-2 partition map plus Metadata + Mirror File Entries (types 250/251). All five
    validated against `udfinfo` (no warnings) and round-tripped through DiscForge's own
    metadata-partition-aware reader. Only 2.60 (BD-R pseudo-overwrite) remains — see
    [docs/UDF.md](docs/UDF.md).
- `god-info` — identify an Xbox 360 GOD (Games on Demand) package from its header:
  the kind (CON/LIVE/PIRS), content type (`0x7000` = Games on Demand), content size,
  and the `Data####` payload inventory. Structure parsing only — nothing decrypted,
  no signature touched. GOD → ISO reconstruction is deferred pending a reference
  fixture (the public block-offset formulas disagree by one block). See
  [docs/XBOX.md](docs/XBOX.md).
- `str-frames` — decode a PlayStation `.str`'s **version 2** MDEC video frames to PNG
  (the front half of the MDEC path that `str-demux` left off): a 16-bit-LE/MSB-first
  bit reader, the DC + MPEG-1 Table B-14 AC variable-length codes, and the existing
  dequant/IDCT/YCbCr pipeline, assembling 4:2:0 macroblocks in column-major order.
  Validated oracle-free against hand-built VLC codes and DC-only frames (a fix went in
  so absent AC coefficients dequantize to exactly zero). Version 3 is reported, not
  mis-decoded. See [docs/PSX_MEDIA.md](docs/PSX_MEDIA.md).
- `ecm` / `unecm` — the classic ECM lossless pre-compression transform for raw CD
  images: strip the regenerable per-sector sync, EDC and Reed-Solomon parity, and
  rebuild them byte-for-byte (whole-file EDC verified on decode). Built on the existing
  `EdcEcc` machinery; the address convention (Mode 1 stored, Mode 2 reconstructed) is
  pinned from the public spec, and every rebuilt sector is validated by independent
  syndrome evaluation. See [docs/ECM.md](docs/ECM.md).
- `iso-create` El Torito bootable-disc support (`--boot` / `--boot-emulation`).
- `create-udf-bridge` — a single image readable as both ISO 9660 (+Joliet) and
  UDF 1.02, sharing one copy of the file data (validated against genisoimage).
- Filesystem conformance linters: `udf-lint`, `fat-lint`, `hfs-lint` (joining the
  existing `iso-lint`), cross-validated against udfinfo / dosfsck / genisoimage /
  hformat.
- `fs-verify` — cross-check a disc's ISO/Joliet/UDF views by content, and
  catalogue the HFS side of a Mac+PC hybrid (shared / Mac-only / PC-only).
- `disc-diff` — file-level comparison of two images (added/removed/changed/moved).
- `chd-verify` — CHD archival integrity (per-hunk CRC + whole-image SHA-1),
  agreeing with chdman.
- `ps2mc-ecc` — verify/repair a PS2 memory card's per-page Hamming ECC
  (cross-validated against mymcplus).
- `catalog-export` — portable JSON/CSV index of an optical archive to keep beside
  a NAS/cloud backup.
- `drives` / `burn` in the CLI: optical burning on Windows (IMAPI2), macOS
  (hdiutil) and Linux (growisofs/wodim). The CLI now multi-targets net8.0 and
  net8.0-windows.
- `cdi-extract` — extract a file (or, with `--all`, every file) from a Philips
  CD-i disc image, handling the Mode 2 Form 1 / Form 2 sector mix so real-time
  streams (e.g. `/MPEGAV/*.DAT`) come out whole. Validated against a real CD-i
  "Movie" disc (pulls `CDI_FLM1.APP` as a valid OS-9 module).
- `floppy-image` — image a floppy disk to a flat `.img` (raw 512-byte sectors)
  from the drive letter (Windows) or a device path (macOS/Linux), reporting the
  recognised geometry (1.44 MB, 720 KB, …). Pairs with `floppy-info`/`fat-ls`/
  `fat-lint`.
- `raw-dump` — a read-only drive/media diagnostic for the Hitachi-LG GDR-816x
  DVD-ROM family (GDR-8161B/2B/3B/4B): identify the drive and, with `--stream-read`,
  confirm a standard READ(12)+streaming read where a plain read is refused. It
  reports bytes as-is and does not descramble or decode console (GameCube/Wii/GD-ROM)
  disc formats — DiscForge stays on the identify/verify/preserve side of the line.
- `read-disc` — image a data disc (DVD/BD/data-CD) to a flat ISO via READ(10)
  (Windows SPTI), so `read-disc` + `burn` clones a personal, unencrypted disc.
  Refuses discs that declare a copy-protection system (CSS/CPRM/AACS) and stops
  on a copy-protected sector; on macOS/Linux it prints the equivalent `dd`
  command (the OS exposes a data disc as a block device).
- Format identification for ~42 more types: virtual disks (VHD/VHDX/VMDK/VDI/
  QCOW2/DMG), non-disc filesystems (NTFS/exFAT/ext/HFS+/SquashFS), more archives,
  audio, images, console ROMs (Game Boy, Master System) and patch formats.
- `docs/QUICKSTART.md` (task-first on-ramp) and canonical `ps1mc-*` memory-card
  command names (aliasing `psxmc-*` / `ps1card-*`).

### Changed
- Format identification now names **ECM** (`ECM\0`) and **Xbox 360 STFS/GOD** packages
  (`CON `/`LIVE`/`PIRS`), pointing at `unecm` / `god-info` respectively.
- Optional cross-tool interop tests (`InteropFixtureTests`): set the `DFORGE_FIXTURES`
  environment variable to a directory holding a reference `.ecm`+`.bin` (and/or a real
  `.str`) to oracle-validate the ECM and MDEC decoders against third-party output. Inert
  and green when no fixtures are present, so nothing needs to be checked into CI.
- `rvz-info` now also reports an RVZ/WIA container's **disc-structure summary** —
  GameCube vs Wii layout and the partition / raw-data-region / group counts — parsed
  from the uncompressed disc directory (no codec needed). Full RVZ → ISO
  decompression stays deferred on two documented blockers: enabling zstd is a
  maintainer policy call (vendor a managed package vs clean-room reimplement), and the
  Wii hash/junk path needs a reference RVZ+ISO oracle. See [docs/RVZ.md](docs/RVZ.md).

### Fixed
- `submission-info` now auto-fills the part of "Common Disc Info" that IS in the
  image: for a PlayStation disc it reads the serial, region and video mode from the
  disc's own `SYSTEM.CNF` and fills the Region line plus a "Detected from image"
  block. The marketing title and physical ring/drive fields stay blank for the
  submitter, since those genuinely aren't derivable from the image.
- `floppy-info` now surfaces VFAT long file names (matching `fat-ls`) and reads
  FAT16/FAT32 floppies too — both share the one FAT reader.
- `disc-report` now runs the CD-i identifier, so a Philips CD-i image reports its
  kind, volume and filesystem in the consolidated report (not just its container).
- `cdi-console-info` now lists the filesystem of a pure CD-i (Green Book) disc.
  Green Book uses the ISO 9660 layout but with big-endian numeric fields and an
  empty root-directory record in the volume descriptor — the tree is reached
  through the big-endian path table. The reader followed ISO 9660's little-endian
  root record and reported zero files; it now walks the path table (validated
  against a real Philips CD-i "Movie" disc — 18 files across `/`, `/CDI`, `/MPEGAV`).
- UDF File Set Descriptor now uses a partition-relative tag location, so strict
  readers (udfinfo, OS drivers) accept the volume.
- Help now lists every top-level command (dozens were previously hidden); the
  `COMMANDS.md` command count corrected.
- `installer/publish.ps1` names the CLI framework explicitly (required now that
  the CLI multi-targets).
- The launcher's tile labels render an `&` literally (WinForms was drawing the
  "Verify & Lint" ampersand as a mnemonic underscore).
- Memory-card help/docs now lead with the canonical `ps1mc-convert` /
  `ps1mc-format` names (aliasing `ps1card-convert` / `psxmc-format`), matching
  `ps1mc-info` / `ps1mc-extract`; removed duplicate command-reference entries.
- PAR2 verifier no longer drops a valid packet whose length isn't a 4-byte
  multiple, so verify/repair works on unpadded sets (the packet MD5 remains the
  integrity gate).
- `catalog-export` CSV and ODE folder-name sanitising are now platform-independent
  (fixed `\n` line endings; the strict FAT/exFAT reserved-character set applied on
  every OS), so output is byte-identical whether generated on Windows, macOS or
  Linux, and an ODE SD-card folder authored off-Windows stays valid.
- `read-stability` no longer over-grades a disc as "degrading" over one or two
  flaky sectors on a small image.

### Pending validation (hardware/reference required)
- Round-trip test of the full PS1 dump → convert → burn workflow against a real
  disc (pending hardware).
- Runtime burn tests on real optical writers (Windows/macOS/Linux); only the
  command construction is unit-tested.
- The full xUnit suite has not been executed in the cloud dev environment (xunit
  absent from the offline package cache) — run it on Windows CI to confirm green.
- Full player-verified Video CD image assembly (pending a reference VCD to
  validate the Mode 2/Form 2 track layout).

## [1.11.0] - 2026-07-27

### Added
- **VOB / MPEG program-stream demuxer** (`vob-demux`, `MpegProgramStream`) — splits
  an *unencrypted* VOB/MPG into its elementary video, audio and DVD private
  (AC3/DTS/LPCM/subpicture) streams. Does not decrypt CSS-scrambled content.
- **Video CD control-file writer** (`vcd-control`, `VideoCdControl`) — emits the
  `INFO.VCD` and `ENTRIES.VCD` control sectors for a VCD/SVCD.
- **DVD-Video IFO editor** (`dvd-ifo dump` / `dvd-ifo build`, `IfoPlanJson`) — dump
  a disc's structure to editable JSON, edit chapters/angles/audio/subtitle
  languages, and rebuild the IFOs. IFO files are unencrypted, so this stays inside
  the clean-room boundary.
- **PlayStation 1 memory-card formatter** — `PsxMemoryCard.Format()` builds a fresh
  empty 128 KB card; exposed as the `psxmc-format` CLI command and a
  "Format new PS1 card…" button in the Memory Cards tile.
- **Unit-tested SET CD SPEED CDB builder** (`SetCdSpeed`) with a multiplier API; the
  existing read-speed (drive-slowdown) path now shares it as its single source of
  truth.

### Changed
- The `dforge` banner now reports the real assembly version instead of a hardcoded
  string, so it can no longer drift from the build.
- Version bumped to 1.11.0.

### Fixed
- Overlapping text in the **Game Media** (CD+G time field) and **Pack Discs**
  (folder-grouping checkboxes) tiles.

### Repository / build
- Consolidated CI into three workflows: fast Linux Core tests, a full Windows
  build that produces the installer artifact, and a tagged-release pipeline that
  attaches the installer plus portable zips and the Linux CLI.
- Added `.gitattributes` to normalise line endings and protect the checked-in
  binary disc-image and memory-card test fixtures from corruption.

## [1.10.0] - 2026-07-26

### Added
- **Licence activation** — public-key (ECDsa P-256) licence keys: in-app activation
  with an evaluation nag + watermark until activated, plus a standalone Licence
  Generator vendor tool.
- **Detailed in-app Help tile** documenting what every tile does and how to use it.
- **Frontend / emulator export** — RetroArch `.lpl` playlists, EmulationStation
  `gamelist.xml`, and multi-disc `.m3u` playlists.
- **Collection tooling** — 1G1R set builder, DAT rebuilder, DAT diff, TorrentZip
  archiver, checksum sidecars (SFV/MD5/SHA-1), and a shareable collection HTML
  report.
- **PlayStation 1 memory-card** container conversion (raw / DexDrive / VGS) and
  save extraction.
- **ROM / save fix-ups** — cartridge byte-order and interleave conversion, header
  strip/add, and save padding/trim to match a DAT.
- **Redump-grade audio read-offset** arithmetic (combined drive + pressing offset,
  overread, silence analysis).

### Changed
- `GUI.md` and `CLI.md` are now generated from source, so the documentation can no
  longer drift from the tool.

### Fixed
- Format identification for CDI, MP4, raw CD data tracks and DAT files.
- A false WonderSwan match on large disc tracks (now gated on power-of-two size and
  a non-zero header checksum).

### Security
- Obfuscation (ConfuserEx) and Authenticode code-signing wired into `publish.ps1`;
  `SECURITY.md` documents the deterrent-not-DRM stance.

[Unreleased]: https://github.com/MatRIXTEaM-code/DiscForge/compare/v1.11.0...HEAD
[1.11.0]: https://github.com/MatRIXTEaM-code/DiscForge/releases/tag/v1.11.0
[1.10.0]: https://github.com/MatRIXTEaM-code/DiscForge/releases/tag/v1.10.0
