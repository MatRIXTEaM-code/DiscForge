<!-- GENERATED FILE - regenerate with:  scripts\gen_cli_doc.ps1  (captures 'dforge' help) -->

# DiscForge - `dforge` command reference

`dforge` is the cross-platform command-line tool (Core builds and runs anywhere .NET 8
does). It exposes the same Core engine as the GUI. This reference is generated verbatim
from the tool's own help output, so it never drifts: **293 commands**.

Run `dforge` with no arguments to print this list, or `dforge <command>` with no further
arguments to see that command's usage.

```
  identify <file>       Say what a file is (any format DiscForge knows)
  library scan <dir> [--dat f] [--html out.html]   Identify+hash a whole tree, verify vs a DAT; --html writes a friendly color-coded audit dashboard
  catalog-export <dir> [--dat f] [--json out.json] [--csv out.csv]  Write a portable catalog of an optical archive (identity, hashes, verification status) to keep beside a NAS/cloud backup
  submission-info <image> [--out f]  Redump-style hashes/cuesheet/subchannel for a dump
  submission-pack <image> <out-dir> [--game N]  Assemble a submission-ready folder (dump+info+dat+cue)
  library rename <dir> --dat f [--apply]  Rename verified files to canonical names
  license <keygen|issue|verify|machine-id> …  Manage DiscForge licence keys (see --help)
  1g1r <dat> [--regions USA,Europe,Japan] [--keep-proto] [--drop-unlicensed] [--out f]
                          One-game-one-ROM: pick the best region per game from a DAT
  rebuild <src> <dest> --dat f [--per-game] [--move] [--apply]
                          Rebuild a clean, DAT-named set from a messy folder
  dat-diff <old.dat> <new.dat>  Compare two DAT revisions: added/removed/changed games
  dat-build <dir> <out.dat> [--name N] [--recursive]  Hash a folder into a Redump-style DAT
  library-report <dir> <out.html> [--dat f]  Scan a folder to a shareable HTML dashboard
  frontend-export <retroarch|gamelist> <dir> <out> [--name N] [--dat f]
                          Write a RetroArch .lpl or EmulationStation gamelist.xml for a folder
  m3u <out.m3u> <disc1> <disc2> [...]  Build a multi-disc M3U playlist (order preserved)
  torrentzip <out.zip> <file> [...]   Write a deterministic TorrentZip-structured archive
  hashgen <sfv|md5|sha1> <out> <file> [...]  Write a checksum sidecar for the files
  hashverify <sidecar.sfv|.md5|.sha1>  Re-hash the referenced files and report OK/FAIL
  preserve pack <manifest.json> <file> [...] [--protection raw.bin]  Hash-manifest a set (+ self-digest)
  preserve verify <manifest.json>  Prove a set is byte-for-byte what was recorded
  preserve-master build <image> [--out f.dpm.json] | verify <f.dpm.json>  One master fusing identity, per-file fixity+Merkle, completeness and the protection profile
  flux pack <raw> <out.dfflux> [--rate N --bits N --channels N --rpm N --profile S] | info <f.dfflux>  Store a raw optical RF/flux capture with its calibration metadata (phase-1 low-level preservation)
  lineage <keygen|init|append|sign|verify|show> …  Append-only, signed chain-of-custody for a dump
  library-watch <dir> [--update]   Watch a collection for silent corruption (bit rot)
  remaster <pack|rebuild|verify> …  Decompose an ISO to a recipe+store and rebuild it byte-exact
  ps1mc-convert <in> <out> [raw|gme|vgs]  Convert a PS1 memory card between container
                          formats (raw .mcr / DexDrive .gme / VGS). Alias: ps1card-convert
  ps1mc-format <out.mcr> [raw|gme|vgs]  Write a freshly-formatted, empty PS1 memory card. Alias: psxmc-format
  ps2mc-ecc <card.ps2> [--repair <out.ps2>] [--json]  Verify (and optionally repair) a PS2 memory card's per-page Hamming ECC — catches silent bit-rot in a save dump and corrects single-bit errors (CLEAN/CORRECTABLE/CORRUPT)
  save-convert <in> <out> <op> [--fill FF]  Fix a cartridge save's byte order or size.
                          op: swap16|swap32, pad <size|sram|flash|eeprom4k|eeprom16k|mempak>, trim
  rom-convert <in> <out> <op>  Fix a cartridge dump so it matches a DAT. op:
                          z64|v64|n64 (N64 byte order), snes-strip|snes-add,
                          smd|unsmd (Genesis interleave), nes-strip (iNES header)
  inspect <image.cdi>   Show version, sessions, and track layout
  extract <cdi> <dir>   Extract tracks to ISO/WAV (--raw for full sectors)
  convert <in> <out>    CDI <-> BIN+CUE / ISO / GDI / NRG, or MDS -> CDI
                        (.cdi<->.cue, .cdi<->.iso, .mds->.cdi)
  disc-convert <in> <out>  Universal hub: any format -> any via a canonical model
                        (.cue .chd .iso .cso .zso .wbfs .cdi .nrg .mds .gdi .ccd in;
                         .cue .chd .iso .cdi .nrg out)
  verify <cdi> [--checksums]  Structural checks + per-track CRC-32
  create <dir> <out.cdi>  Build a data CDI from a folder (--volume NAME, --rock-ridge)
  compare <a.cdi> <b.cdi>  Diff two images (structure + per-track CRC-32)
  create-audio <out.cdi> <a.wav> [b.wav ...]  Build an audio CD from WAVs
                          --gapless, --74, --postgap [sectors]
  ls <image.cdi>          List files inside the image (ISO 9660 or UDF, auto-detected)
                          --iso 8.3 names, --joliet, --udf force a filesystem
  extract-files <image.cdi> <dir>  Extract the filesystem contents
  build-raw <src> <out>   Compose a RAW DAO image (lead-in + subcode) from
                          a .cue or .cdi. --subcode pq|cooked|raw (default cooked)
  subch <file.sub>        Analyse a captured raw sub-channel sidecar: Q-CRC
                          validity, LibCrypt-style protection fingerprint
  cdtext <file.cdt>       Decode CD-TEXT packs into album/track title & performer
  deemph <in.wav> <out.wav>  Apply CD de-emphasis (50/15µs) to a pre-emphasised track
  silence-split <in.wav>  Find track boundaries by silence; emit a cue sheet
  audio-dynamics <in.wav> DR value, peak/RMS/crest, and clipping detection
  hdcd-scan <in.wav>      Detect HDCD encoding hidden in 16-bit PCM least-significant bits
  libcrypt <file.sub>     Characterise LibCrypt: variant, magic/key material,
                          per-sector CRC deltas. --sbi <out> writes the sidecar
  cdg-frames <src> <dir>  Export a CD+G frame sequence as numbered PNGs (--fps N)
  cdg-preview <src>       Decode CD+G graphics from a raw image (2448) or a
                          .cue with .sub sidecar. --seconds N (default 30),
                          --out shot.ppm writes a screenshot
  cdg-render <file.cdg>   Render a CD+G frame to PNG. --at MM:SS (default end),
                          --out <file.png> (default <name>.png)
  cdg-extract <sub> <out.cdg>  Extract the CD+G packet stream from a raw
                          96-byte/sector sub-channel sidecar
  view-sector <img> <addr> [--count N] [--descramble]  Annotated hex view of
                          sectors. addr: LBA, mm:ss:ff, or +fileindex
  extract-sectors <img> <out> --start <addr> --count N  Pull a sector range
                          --as stored|user|raw2352, --byteswap for audio
  inspect-raw <img>       Analyse a raw image: TOC, Q health, CD-TEXT, MCN/ISRC,
                          scrambling, EDC/ECC. --deep checks every sector.
                          Also reads bare 2352 BINs (ECC gold-check on real rips)
  raw-verify-readback <golden.img> <readback.bin>  Prove a RAW burn is byte-faithful:
                          compare a disc read-back to the golden image (main + sub-channel)
  dvd-verify-readback <source.iso> <readback.bin> [--layer-break LBA]  Verify a burned DVD/BD
                          against its source at ECC-block granularity, layer-break aware
  booktype-trace <trace-file> [--save recipe.json]  Decode a captured bitsetting (book-type)
                          command trace and learn a verbatim replay recipe from your own drive
  dump-merge <out> <in1> <in2> [in3 ...]  Merge several imperfect rips of the SAME
                          disc into one image (EDC-verified where possible)
  merge-cert <out> <in1> <in2> [...] [--key f | --gen-key] | verify <cert> [out in...]  Bad-sector-aware merge + a signed, checkable per-sector provenance certificate
  c2-merge <out.bin> <in1.bin> [in1.c2] <in2.bin> [in2.c2] ...  Byte-level C2 recovery: reassemble a sector from the good bytes of several C2-flagged reads
  dvd-ecc self-test | repair <block.bin> <out.bin>  DVD RS-PC error correction on a 208×182 ECC block (PI/PO product code); software-first, round-trip validated
  flux-demod self-test | encode <in> <out.dff> [--cell N --jitter J] | decode <in.dff> <out>  Demodulate an optical flux capture to the EFM bitstream (software-first; clock recovery + NRZI)
  collection-triage <folder> [--dat f] [--html out.html] [--json]  Whole-collection worklist: per-dump verified / incomplete / re-cut / duplicate / check, with a shareable HTML dashboard
  salvage-plan <folder> [--json]  Find where several incomplete dumps of the same disc can be merged into a complete image (complementary holes)
  reconstruct <out> <in1> [in2 ...]  Best-possible image via agree/EDC/ECC/vote with
                          per-sector provenance (--no-ecc, --provenance <map>)
  dump-score <raw.bin>    Score a raw image's dump confidence (0-100 + grade) from EDC
  disc-anomalies <iso>    Find hidden / orphan data no file or ISO structure explains
  disc-date <iso>         Date a disc from its ISO timestamps; flag re-mastering / tampering
  disc-delta <base.iso> <target.iso> <out.delta>  File-level delta carrying only what changed
  disc-patch <base.iso> <in.delta> <out.iso>      Rebuild the target byte-exact from base + delta
  disc-genome <a.cue> [b.cue]  Offset-invariant disc fingerprint; compare two rips for same-disc
  health-map <in.bin> <out.svg>  Render a per-sector EDC/ECC health heatmap (SVG)
  error-pattern <in.bin>  Classify failing sectors: scratch/rot (recover) vs protection (preserve)
  disc-fs <image.iso>     Identify every filesystem a disc carries (ISO/Joliet/UDF/HFS/CD-XA)
  hfs-ls <image>          Walk a classic Mac HFS volume: files, folders, fork sizes
  hfs-lint <image> [--json]  HFS integrity check: MDB signature/geometry, catalog B-tree walkability, file/dir count consistency, fork-extent bounds (fsck-style)
  hfs-resources <image> [macpath]  List the resources in each file's Mac resource fork (icons, vers, code, ...)
  hfs-orphans <image>     Carve HFS free space: leftover/deleted data the catalog hides
  udf-orphans <image>     Carve UDF free space: leftover/deleted data via the space bitmap
  fs-orphans <image>      Auto-detect HFS/UDF and carve free space (both on a hybrid)
  disc-cluster <path...>  Group un-identified dumps by content — same title, different variants
  hidden-sessions <cue>   Map every session; flag data sessions a naive rip would skip (CD Extra)
  disc-rot <history.json> Triage C1/C2 scan history over time; predict which discs to dump first
  rot-kinetics <history.json> [--temp C --rh %]  First-order (Arrhenius) decay fit + survival forecast
  bler <scan.csv>         C1/C2 surface-quality report: BLER, E22/E32, Red Book pass/fail
  scan-import <scan.txt>  Import an Opti Drive Control/Nero/KProbe quality scan (CD C1/C2, DVD
                          PIE/PIF/POF, BD LDC/BIS) → feed disc-rot/disc-print/bler --emit rot|print|bler
  dpm <scan.csv>          Data-position timing: detect ring protection, fingerprint layout
  protection-scan <iso>   Fingerprint copy protection (SafeDisc/SecuROM/LibCrypt…) as metadata
  protection-profile <image> [--json]  Unified protection profile: schemes + where they live + whether this capture mode can preserve each
                          --raw <raw.bin>  also fuse on-disc signals (twin sectors, error band)
  twin-scan <raw.bin>     Detect twin / re-addressed sectors (header-address protection)
  weak-sectors <raw.bin>  Predict channel-weak sectors (scramble+EFM+DSV physical model)
  efm-spectrum <raw.bin>  EFM run-length spectrum, duty asymmetry, DSV, grade
  xa-map <raw.bin>        Map CD-ROM XA streams: file/channel, video/audio, interleave
  recover-oracle <frames> Model CIRC recovery: can a burst of N frames be corrected?
  recover-toc <raw.sub>   Rebuild the TOC from the Q sub-channel (dead lead-in recovery)
  scratch-verdict <in.bin>  Per-scratch recovery outlook (corrected/concealed/re-read)
  recovery-map <in.bin> <out.svg>  SVG map coloured by recovery outlook per region
  covert-scan <iso>       Hunt for hidden data in zero-expected regions (slack, system area)
  matter-map <in> <out.svg>  Classify each region (zero/text/structured/high-entropy) as SVG
  phylo <dir|iso...>      Build a family tree of a title's releases from file deltas
  iso-lint <iso>          Strict ISO 9660 conformance check (spec violations)
  udf-lint <iso|udf>      Strict UDF conformance check: descriptor tag checksums/CRCs, partition-relative tag locations, FSD reachability, LVID consistency (the check that catches the classic "File Set Descriptor not found")
  iso-pathtable <iso>     Audit the ISO 9660 path table (L/M agreement, parent tree)
  iso-rockridge <iso>     Recover Rock Ridge POSIX metadata (perms, owners, symlinks, times)
  redbook-audit <cue>     Strict Red Book (CD structure) conformance check
  boot-catalog <iso>      Decode a bootable disc's El Torito boot catalog
  premaster-check <cue>   Master-readiness gate: structure + capacity + data integrity
  dump-provenance <path...>  Infer what tool produced a dump from its fileset + geometry
  pregap-scan <audio.bin> Detect hidden-track audio in a pregap/gap (HTOA)
  dvd-nav <VTS_xx_0.IFO>  Map DVD program chains; flag unreferenced (hidden) PGCs
  consensus <keygen|attest|verify>  Federated, signed cross-dumper consensus ledger
  disc-print <scan.json> [b.json]   Physical-copy fingerprint from an error scan; compare two
  disc-genealogy <collection.json>  Provenance family tree + authenticity/counterfeit verdicts
  mastering-print <image> [--json] | compare <a> <b> [--json]  Mastering fingerprint from the ISO volume descriptor; compare two copies to flag a re-mastered reproduction
  collection-archive <build|verify|extract>  Dedup a library to unique blobs; rebuild any disc exact
  vault <create|check|heal>  Self-healing container: Reed-Solomon parity repairs bit-rot
  par2-verify <file.par2>  Read & verify a PAR2 recovery set; report per-file status and repairability
  chunk-manifest <file...>  FastCDC content-defined chunking + Merkle root; dedup a set to unique chunks
  fuzz-parsers <seed> [--iterations N]  Robustness-fuzz the format parsers; report unclean crashes/hangs
  disc-semdiff <a> <b>    Region-level (shift-tolerant) diff of two images: where they diverge, not a byte wall
  completeness-check <cue>  Dump-coverage certificate: reconcile cue layout, data size and subchannel; flag gaps
  pregap-check <cue> [--json]  Audit a cue's pregaps vs PlayStation/Redump convention (2s data/audio boundary, no negative gaps)
  subq-map <disc.sub> [--json] [--form packed|interleaved|pq16]  Recover each track's real INDEX 00/01 and pregap from a captured subchannel
  redump-cue <in.cue> <disc.sub> <out.cue> [--snap-pregap]  Re-cut a split bin/cue at the subchannel's INDEX 00 boundaries (Redump-conformant, byte-preserving)
  bad-sectors <map.badsectors.json> [--json]  Show a dump's unreadable-sector map: counts, coalesced runs, and per-track positions
  redump-diff <cue> <dat> [--game "name"] [--json]  Explain WHY a dump doesn't match Redump: per-file verdict + the cause (split, padding, offset, bad sector)
  dump-audit <cue|image> [--dat f] [--json]  "Is my dump good?" — one plain verdict (GOOD/SUSPECT/BAD) fusing structure, holes, EDC/ECC, end-sectors, pregaps, DAT match
  read-stability <pass1> <pass2> [pass3 ...] [--sector-size N] [--json]  Disc-rot early warning: flag sectors that read inconsistently across passes (stable/marginal/degrading)
  verify-convert <a> <b> [--json]  Prove a format conversion was lossless: decode both images (bin/cue, .chd, .bin) to raw sectors and compare byte-for-byte
  chd-verify <image.chd> [--parent p.chd ...] [--json]  Check a CHD's integrity without extracting: decompress every hunk, check each map CRC-16, and confirm the whole image matches its stored SHA-1 (VALID/CORRUPT/UNVERIFIED) — the archival-integrity check chdman's verify performs
  fs-verify <image> [--json]  Cross-check a disc's filesystem views (ISO 9660, Joliet, UDF) and confirm they describe the same files with the same bytes — catches truncated dumps, tampering, and content hidden from one filesystem (AGREE/DIVERGENT/INCOMPLETE)
  disc-diff <a> <b> [--json]  Compare two disc images at the file level: what was added, removed, changed (by content), or moved/renamed — for two pressings, patched vs original, or revisions
  redump-prep <in.cue> <out-dir> [--sub f] [--snap-pregap] [--dat f --game "n"] [--offset N] [--json]  One-step submission prep: re-cut + carry holes + checks + submission text
  cu2 <write|verify> <cue> [file.cu2]  Generate or cross-check a Cybdyn CU2 track map (PSIO/xStation) from a cue
  ode-export psio <cue> <out-dir> [--name N]  Lay a PS1 dump out for a PSIO/xStation ODE: game folder + bin/cue + generated CU2
  ode-layout <gdemu|rhea|phoebe|mode> <games-dir> <out-dir>  Arrange a set of converted games into an ODE SD-card layout (numbered folders + sidecars; menu built by the device tool)
  disc-bom <iso>          Technical bill-of-materials: engine, middleware, runtime, build date
  ring-code "<runout>" | group <json>  Parse IFPI ring codes; group discs by plant/master
  offset-detect <rip.bin> <reference.bin>  Detect the CD-DA read offset between two PCM rips
  checksum <file>         CRC-32 + MD5 + SHA-1 + SHA-256 in one pass
                          --write [sha256|md5|sha1|sfv|all]   write sidecar(s)
                          --verify   check against an existing sidecar
  browse <image>          List files inside a .cdi or .iso (ISO 9660 or UDF)
                          --extract <dir> writes them out, --only <text> filters
  cue-check <sheet.cue>   Check a cuesheet against the data file it describes:
                          indexes inside the file, track types consistent,
                          arithmetic reaching the end. Exit 2 on errors, 1 on warnings
  cue-repair <in.cue> [out.cue] [--json]  Fix a broken cue: wrong FILE refs (vs the actual bins), out-of-order track numbers, missing INDEX 01; re-emit clean
  ecc-repair <image.bin>  Rebuild damaged Mode 1 sectors from the Reed-Solomon
                          parity they already carry. --dry-run to check only
  fix-modes <image.cdi>   Correct track modes recorded wrongly in a CDI descriptor
  split <file> <size>     Split into .001/.002/… + .sfv manifest
                          sizes: bytes, 700m, 4g, or fat32 (= 4 GiB - 1)
  join <part|base> [out]  Rejoin parts, verifying CRCs + SHA-256 via the manifest
  ppf-apply <patch.ppf> <image.bin>  Apply a PlayStation Patch File to an image
                          --undo revert (PPF 3.0), --force skip validation, --dry-run
  ppf-create <orig> <mod> <out.ppf>  Build a PPF 3.0 from a before/after image pair
                          --desc, --fileid, --no-undo, --no-validation
  ppf-info <patch.ppf>    Show a patch's version, description, size and flags
  ppf-convert <in> <out> --to 1|2|3  Rewrite a patch in another PPF revision
  ppf-edit <in> <out>     Change a patch's --desc and/or --fileid in place
  ips-apply <patch.ips> <image> [--out f]  Apply an IPS patch (in place unless --out)
  ips-create <orig> <mod> <out.ips>  Build an IPS patch from a before/after pair
  bps-apply <patch.bps> <source> [--out f]  Apply a BPS patch (CRC-verified)
  bps-create <source> <target> <out.bps>  Build a BPS patch from a before/after pair
  create-udf <folder> <out.udf> [--udf-version 1.02|1.50|2.00|2.01|2.50]  Build a UDF filesystem image from a folder
                          --volume NAME sets the volume label
  create-udf-bridge <folder> <out.iso>  Build a UDF-bridge image readable as BOTH
                          ISO 9660 (with Joliet) and UDF 1.02, sharing one copy of
                          the file data. --volume NAME sets the label; --json for JSON
  dvd-video-plan <VIDEO_TS-folder>  Validate a VIDEO_TS folder and show the DVD-Video on-disc file order
  dvd-video-build <VIDEO_TS-folder> <out.iso>  Assemble a VIDEO_TS folder into a DVD-Video ISO+UDF image
  dvd-video-fix <VIDEO_TS-folder> [--apply]  Rewrite IFO sector pointers + refresh .BUP to match file sizes (Fix VTS Sectors)
  bdmv-plan <BDMV-folder>  Validate a Blu-ray BDMV folder (index/MovieObject/PLAYLIST/CLIPINF/STREAM)
  bdmv-build <BDMV-folder> <out.iso>  Assemble a BDMV folder into a BD-Video UDF 2.50 image
  gdi-info <disc.gdi>     Show a Dreamcast GD-ROM track layout and validate it
                          against the track files beside it
  gdi-browse <disc.gdi>   List the game filesystem on the high-density track
                          --extract <dir> writes the files out
  milcd-to-cdi <in.cue> <out.cdi>  Convert a Dreamcast MIL-CD Redump bin/cue to a
                          two-session CDI. --version v2|v3|v35, --gap <sectors>
  ipbin-info <image>      Identify a Dreamcast disc from its IP.BIN boot header
  pvr-info <file.pvr>     Describe a Dreamcast PVR texture header (format, size, GBIX) + integrity
  pvm-info <file.pvm>     List the textures in a Dreamcast PVM archive (names, formats, sizes)
  mpeg-info <file>        Describe an MPEG program stream (VCD/VOB/Sofdec .sfd): video size, fps, streams
  saturn-info <image>     Identify a Sega Saturn disc from its header
  pcfx-info <image> [--json]  Identify a NEC PC-FX disc from its "PC-FX:Hu_CD-ROM" boot signature + boot-header text
  segacd-info <image>     Identify a Sega CD / Mega-CD disc from its header
  opera-ls <image>        List the 3DO Opera file system of a 3DO disc image
  neogeo-ipl <ipl|image>  Parse a Neo Geo CD IPL.TXT boot script (load list)
                          (.gdi, .cue MIL-CD, .cdi, or a raw .bin/.iso)
  cdi-console-info <image>  Identify a Philips CD-i (Green Book) disc and list
                          its filesystem (pure CD-i or CD-i Bridge)
  cdi-extract <image> <path> <out-file> | <image> <out-dir> --all  Extract a file (or all files) from
                          a CD-i disc, handling the Mode 2 Form 1/Form 2 sector mix (e.g. /MPEGAV/*.DAT)
  psp-info <image>        Read a PSP UMD's PARAM.SFO metadata and filesystem
                          (.iso, .cso or .zso; --sfo dumps every SFO key/value)
  pbp-info <EBOOT.PBP>    List a PSP PBP package: version + each sub-file's size,
                          and its PARAM.SFO title/id/category if present
  pbp-extract <EBOOT.PBP> <dir>  Write each non-empty PBP sub-file to <dir>/<name>
                          (DATA.PSP is extracted raw and is NOT decrypted)
  bdmv-info <file|folder> Show a Blu-ray playlist (.mpls), clip-info (.clpi),
                          or enumerate titles from a BDMV folder
  iso-rebase <in> <out> <baseLBA>  Shift an ISO's LBAs (GD-ROM fix; base 45000)
  xiso-ls <image.iso>     List files in an Xbox XDVDFS image (--extract <dir>)
  create-xiso <folder> <out.iso>   Build an Xbox XISO from a folder
  god-info <header>       Identify an Xbox 360 GOD package (type, size, Data#### inventory)
  iso-create <folder> <out.iso> [--volume-id N] [--no-joliet] [--rock-ridge]   Build a standard ISO 9660 data-disc image from a folder (Joliet by default)
  ps2-info <image>        Identify a PlayStation 1/2 disc (game ID, region) from SYSTEM.CNF
  gcm-info <image>        GameCube disc: boot header + file tree; for a Wii disc,
                          the volume header + partition table (contents not read)
  gcm-banner <image> <out.png>  Extract a GameCube disc's banner icon (opening.bnr)
  gcm-extract <image> <out-dir>  Extract the GameCube disc's file tree to a folder
  tpl-info <file.tpl>     List textures in a GameCube/Wii TPL (size + GX format)
  tpl-extract <file.tpl> <out>  Decode TPL textures to PNG (--index N)
  wbfs-info <file>        List the Wii/GameCube discs in a WBFS container
                          (slot, game id, title, sizes)
  wbfs-extract <file> <slot> <out.iso>  Rebuild one disc's ISO from a WBFS
                          container (contents are copied as-is, not decrypted)
  rvz-info <image>        Identify an RVZ/WIA container and show its metadata
  nkit-info <image>       Detect an NKit-scrubbed GC/Wii image; show source CRC32 for Redump matching
  gc-verify <image> [--json]  Single-image GameCube 'good dump' health check: bounds, region cross-check, size class
  gc-junk-map <image> [--json]  Map a GameCube disc's non-game padding and classify each region (junk present / zeroed / structured)
  dvd-layerbreak <pfi>    Read a DVD PFI/.physical: book type, layers, PTP/OTP, layer-break LBA + verify
  layerbreak-pick <total-sectors> [--target N] [--cells a,b,..] [--max-layer N] [--seamless]  Choose a legal DVD-DL layer break
  capacity-check <image-sectors> <cd74|cd80|dvd5|dvd9|bd25|bd50|N> [--overburn]  Check an image against media capacity
  rom-info <file>         Identify a cartridge ROM (N64, SNES, Genesis, GB/GBC, GBA,
                          NES, and more) and print its No-Intro CRC32/MD5/SHA1
  rom-integrity <file> [--json]  Recompute a cartridge's own checksums (GB header+global, Genesis content, GBA header+logo) to catch a bad dump
  fds-info <file.fds> [--json]  Read a Famicom Disk System image: per-side identity + file table (name, type, load address, size)
  n64-info <rom>          N64 CIC boot-chip ID + CRC1/CRC2 boot-checksum verify
  scummvm-detect <path>   ScummVM Advanced-Detector fingerprints (size + MD5 of the
                          first 5000 bytes) for a game folder or file. --recursive,
                          --bytes N to change the hashed length
  scummvm-export <cue> <dir>  Export a disc into a ScummVM game folder: data files +
                          each CD audio track as trackNN.wav. --flac/--ogg re-encode
                          the audio in-process (no ffmpeg needed); --high = better OGG
  disk-info <image>       Read a whole-disk image's partition table (auto-detect
                          MBR, GPT, or PS2 APA) and the filesystem in each partition
  rdb-info <image>        Read an Amiga Rigid Disk Block: geometry + partitions + FS
  apm-info <image>        Read an Apple Partition Map (Mac / hybrid-CD partitions)
  disc-report <image>     Identify a disc and run every matching parser (one report)
  floppy-info <image>     List a floppy image's contents (auto-detect C64 D64,
                          Amiga ADF, or DOS FAT12 .img)
  floppy-image <drive> <out.img>  Image a floppy disk to a flat .img (Windows drive letter, needs
                          admin; macOS/Linux device path). Reports geometry; pair with floppy-info
  fat-ls <image>          List a FAT16/FAT32 volume (boot images, hybrid FAT partitions)
  fat-lint <image> [--json]  FAT12/16/32 integrity check: BPB, FAT-copy agreement, cluster-chain validity, cross-links, lost clusters (fsck-style)
  drives                  List optical recorders + capabilities (Windows via device stack; macOS via system_profiler)
  burn <image.iso> [drive] [--verify] [--speed N]  Burn a data ISO to a blank CD/DVD/BD (Windows IMAPI2, or macOS hdiutil)
  read-disc <drive> <out.iso> [--continue-on-error] [--retries N]  Image a data DVD/BD/data-CD to a flat ISO
                          (Windows SPTI). Pair with `burn` to clone a personal, unencrypted disc. Refuses
                          copy-protected discs (CSS/CPRM/AACS); for audio/mixed CDs rip in the GUI
  raw-dump <drive> [--stream-read]  Drive/media diagnostic for the Hitachi-LG GDR-816x DVD-ROM family:
                          identify the drive and (optionally) confirm a raw READ(12)+streaming read. Reports
                          bytes as-is; does NOT descramble or decode console (GameCube/Wii/GD-ROM) discs
  fat-extract <image> <out-dir>  Extract a FAT16/FAT32 volume's tree (--only /PATH)
  gameaudio-info <file>   Read a game-music file's metadata/structure (auto-detect
                          PSF/PSF2, SPC, VGM, NSF): system, tags, duration. No playback
  gci-info <file>         List GameCube saves in a .gci or a memory-card image
  gci-extract <card> <index> <out.gci>  Write one save from a card image to a .gci
  n64save-info <file>     Identify an N64 save by size, and list Controller Pak notes
  saturnsave-info <file>  List the directory of a Sega Saturn backup-memory image
  floppy-extract <image> <path-in-image> <out>  Extract one file from a floppy image
  woz-info <file.woz>     Inspect an Apple II WOZ image: disk type, tracks, protection flags, CRC
  scp-info <file.scp>     Inspect a SuperCard Pro flux image: tracks, revolutions, RPM, checksum
  kryoflux-info <file.raw>  Inspect a KryoFlux raw stream: flux count, index pulses, RPM, hardware info
  d88-info <file.d88>     Inspect a PC-98/PC-88 D88 floppy image: media type, tracks, sector geometry
  cheat-decode <platform> <code>  Decode a Game Genie / GameShark code to address/value
                          platform: nes|snes|genesis|gb|gs-ps1
  cheat-encode <platform> <address> <value> [compare]  Encode a Game Genie code
                          platform: nes|snes|genesis|gb (hex address/value)
  cheat-apply-nes <rom> <code> <out>  Apply an NES Game Genie code to a ROM (NROM)
  adx-decode <in.adx> <out.wav>  Decode a CRI ADX ADPCM stream to a 16-bit WAV
  dsp-decode <in.dsp> <out.wav>  Decode a Nintendo GameCube/Wii DSP-ADPCM stream to a 16-bit WAV
  read-offset <samples> [in.wav out.wav]  Redump read-offset math; with a WAV,
                          slide it by <samples> (combined drive+disc offset)
  vab-info <file.vab>     Read a PlayStation VAB (VAG bank): programs, tones, VAGs
  seq-info <file.seq>     Read a PlayStation SEQ sequence: ppqn, tempo, event count
  str-demux <in.str> <out-dir>  Split a PSX .str into MDEC bitstreams + audio note
  str-frames <in.str> <out-dir>  Decode PSX .str v2 video frames to PNG images
  mdec-info <in.str>      MDEC codec params per frame (version, qscale, macroblocks)
                          --sector-size 2352|2048 (default 2352)
  vob-demux <in.vob|.mpg> <out-dir>  Split an unencrypted MPEG program stream (VOB/MPG)
                          into elementary video/audio/subpicture streams (no CSS decrypt)
  vcd-control <out-dir> [--album N] [--svcd] [--entry T:M:S:F ...]  Write INFO.VCD/ENTRIES.VCD
  vcd-psd <PSD.VCD> [LOT.VCD]  Decode VCD PlayBack Control: menus, play lists, links
  dvd-ifo <dump|build> …  Dump a DVD's structure to editable JSON, or rebuild IFOs from it

More commands:
  chd-info <image.chd>    Show a CHD's version, codecs, hunk geometry and CD track layout
  chd-create <in.cue|in.img> <out.chd>   Create a CHD (v5) from a bin/cue or raw image
  chd-extract <image.chd> <out.bin> [out.cue] [--parent p.chd ...]   Decompress a CD CHD to bin/cue
  chd-extract-hd <image.chd> <out.img> [--parent ...]   Decompress a hard-disk CHD to a raw image
  ciso-info <image.cso|.zso>   Show a compressed-ISO (CSO/ZSO) header
  ciso-to-iso <in.cso|.zso> <out.iso>   Decompress a CSO/ZSO to a plain ISO
  iso-to-ciso <in.iso> <out.cso>   Compress an ISO to CSO (zlib)
  psx-build <folder> <out.bin> [volume-id] [out.cue]   Build a PlayStation data track from a folder
  psx-exe-info <file.exe>   Read a PS-EXE header (load address, entry point, size)
  psx-pad <in> <out> [--multiple N | --psexe] [--fill 0xNN]   Pad a PS1 binary to a size/boundary
  psx-video-mode <in> --to ntsc|pal [--ppf out.ppf | --out out.bin]   Convert a PS1 game's video mode
  vag-extract <file.vag> <out.wav>   Decode a PlayStation VAG (SPU-ADPCM) sample to WAV
  xa-extract <raw image> <out.wav> [--sector-size N] [--channel N]   Extract CD-XA ADPCM audio to WAV
  tim-info <file.tim>     Describe a PlayStation TIM texture
  tim-extract <file.tim> <out.png> [--palette N]   Decode a TIM texture to PNG
  tmd-info <file.tmd>     Describe a PlayStation TMD 3D model
  tmd2dxf <file.tmd> <out.dxf>   Convert a PlayStation TMD model to DXF
  ps1mc-info <card.mcr>   List PlayStation 1 memory-card saves (title, blocks). Alias: psxmc-info
  ps1mc-extract <card.mcr> <out-dir>   Extract PS1 saves to files. Alias: psxmc-extract
  ps2mc-info <card.ps2>   List PlayStation 2 memory-card files/saves
  ps2mc-extract <card.ps2> <out-dir>   Extract PS2 memory-card files
  vmu-info <vmu.bin>      List Dreamcast VMU saves
  vmu-create <out.bin>    Write a blank formatted 128 KB Dreamcast VMU
  vmu-add <vmu.bin> <save.vms> [--name N] [--game] [--protect]   Add a save to a VMU
  vmu-extract <vmu.bin> <out-dir> [--force]   Extract VMU saves
  vms2vmi <save.vms> <out.vmi> [--desc T] [--name N]   Wrap a raw VMS save as a VMI+VMS pair
  dc-scramble <in> <out>  Apply the Dreamcast bootstrap (1ST_READ.BIN) scramble
  dc-descramble <in> <out>   Reverse the Dreamcast bootstrap scramble
  tod-info <file.tod>     Describe a Dreamcast TOD model file
  dvd-info <VIDEO_TS|disc root>   Summarise a DVD-Video's structure
  dvd-rewrite <VIDEO_TS|disc root> <out folder> [--keep 1,3]   Rebuild VIDEO_TS keeping selected titles
  vcd-info <INFO.VCD|ENTRIES.VCD>   Read a Video CD control/entry file
  accuraterip <image.cue> [--db <dBAR.bin>] [--url]   AccurateRip v1/v2 checksums + disc IDs; verify vs a DB record
  scan-protection <image.cdi>   Fingerprint copy protection as metadata (identify only)
  sbi-make <disc.sub> [out.sbi] [--start-lba N]   Write an SBI from a captured subchannel (LibCrypt preservation)
  sbi-info <file.sbi>     Describe an SBI subchannel-patch file
  ecm <in.bin> [out.ecm]  Shrink a raw image to ECM (strip regenerable sync/EDC/ECC; lossless)
  unecm <in.ecm> [out.bin]   Rebuild the raw image from an ECM file (EDC-verified)
  bincue-merge <in.cue> <out.bin> [out.cue]   Merge a multi-bin cue into one bin+cue
  bincue-split <in.cue> [out-dir] [base] [out.cue]   Split a single-bin cue into per-track bins
  to-ccd <image.cue> [--out basename]   Convert a cue/bin to CloneCD .ccd/.img/.sub
  ccd-info <image.ccd>    Read a CloneCD control file
  cdr-info <atip-dump>    Read an ATIP dump (blank CD-R manufacturer/dye type)
  mount <image>           Mount a disc image, or show how to mount it where supported
  transcode <input> <output> [options]   Transcode audio between DiscForge-supported formats
  dat-verify <dat-file> <file ...>   Verify one or more files against a Redump/No-Intro DAT
  bin2src <file> [--name ID] [--asm] [--per-line N] [--out f]   Emit a file as C/asm source bytes
  search <file> (--hex 4d5a | --ascii TEXT) [--limit N]   Search a file for a hex or ASCII pattern
```
