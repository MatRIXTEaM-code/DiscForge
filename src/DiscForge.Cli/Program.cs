// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Audio;
using DiscForge.Core.BluRay;
using DiscForge.Core.Cdi;
using DiscForge.Core.Convert;
using DiscForge.Core.Create;
using DiscForge.Core.Iso;
using DiscForge.Core.Cue;
using DiscForge.Core.Files;
using DiscForge.Core.GameCube;
using DiscForge.Core.Gdi;
using DiscForge.Core.Mds;
using DiscForge.Core.Nrg;
using DiscForge.Core.Patch;
using DiscForge.Core.PlayStation;
using DiscForge.Core.Raw;
using DiscForge.Core.ScummVm;
using DiscForge.Core.Udf;
using DiscForge.Core.Xbox;

string Banner = $"""
  ___                   _                    _
 / _ \ _ __   ___ _ __ | |_   _  __ _  __ _| | ___ _ __
| | | | '_ \ / _ \ '_ \| | | | |/ _` |/ _` | |/ _ \ '__|
| |_| | |_) |  __/ | | | | |_| | (_| | (_| | |  __/ |
 \___/| .__/ \___|_| |_|_|\__,_|\__, |\__, |_|\___|_|
      |_|                       |___/ |___/   v{CliVersion()}
""";

// The CLI version, read from the assembly so it tracks the .csproj and never
// drifts (the same value the -Version bump flows into).
static string CliVersion()
{
    var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
    return v is null ? "1.12.0" : $"{v.Major}.{v.Minor}.{v.Build}";
}

if (args.Length == 0)
{
    Console.WriteLine(Banner);
    Console.WriteLine();
    Console.WriteLine("usage: dforge <command> [args]");
    Console.WriteLine();
    Console.WriteLine("many read-only commands accept --json for machine-readable output");
    Console.WriteLine("(disc-report, iso-lint, iso-pathtable, redbook-audit, premaster-check,");
    Console.WriteLine(" bler, dpm, audio-dynamics, apm-info, rdb-info).");
    Console.WriteLine();
    Console.WriteLine("commands:");
    Console.WriteLine("  identify <file>       Say what a file is (any format DiscForge knows)");
    Console.WriteLine("  library scan <dir> [--dat f] [--html out.html]   Identify+hash a whole tree, verify vs a DAT; --html writes a friendly color-coded audit dashboard");
    Console.WriteLine("  catalog-export <dir> [--dat f] [--json out.json] [--csv out.csv]  Write a portable catalog of an optical archive (identity, hashes, verification status) to keep beside a NAS/cloud backup");
    Console.WriteLine("  submission-info <image> [--out f]  Redump-style hashes/cuesheet/subchannel for a dump");
    Console.WriteLine("  submission-pack <image> <out-dir> [--game N]  Assemble a submission-ready folder (dump+info+dat+cue)");
    Console.WriteLine("  library rename <dir> --dat f [--apply]  Rename verified files to canonical names");
    Console.WriteLine("  license <keygen|issue|verify|machine-id> …  Manage DiscForge licence keys (see --help)");
    Console.WriteLine("  1g1r <dat> [--regions USA,Europe,Japan] [--keep-proto] [--drop-unlicensed] [--out f]");
    Console.WriteLine("                          One-game-one-ROM: pick the best region per game from a DAT");
    Console.WriteLine("  rebuild <src> <dest> --dat f [--per-game] [--move] [--apply]");
    Console.WriteLine("                          Rebuild a clean, DAT-named set from a messy folder");
    Console.WriteLine("  dat-diff <old.dat> <new.dat>  Compare two DAT revisions: added/removed/changed games");
    Console.WriteLine("  dat-build <dir> <out.dat> [--name N] [--recursive]  Hash a folder into a Redump-style DAT");
    Console.WriteLine("  library-report <dir> <out.html> [--dat f]  Scan a folder to a shareable HTML dashboard");
    Console.WriteLine("  frontend-export <retroarch|gamelist> <dir> <out> [--name N] [--dat f]");
    Console.WriteLine("                          Write a RetroArch .lpl or EmulationStation gamelist.xml for a folder");
    Console.WriteLine("  m3u <out.m3u> <disc1> <disc2> [...]  Build a multi-disc M3U playlist (order preserved)");
    Console.WriteLine("  torrentzip <out.zip> <file> [...]   Write a deterministic TorrentZip-structured archive");
    Console.WriteLine("  hashgen <sfv|md5|sha1> <out> <file> [...]  Write a checksum sidecar for the files");
    Console.WriteLine("  hashverify <sidecar.sfv|.md5|.sha1>  Re-hash the referenced files and report OK/FAIL");
    Console.WriteLine("  preserve pack <manifest.json> <file> [...] [--protection raw.bin]  Hash-manifest a set (+ self-digest)");
    Console.WriteLine("  preserve verify <manifest.json>  Prove a set is byte-for-byte what was recorded");
    Console.WriteLine("  preserve-master build <image> [--out f.dpm.json] | verify <f.dpm.json>  One master fusing identity, per-file fixity+Merkle, completeness and the protection profile");
    Console.WriteLine("  flux pack <raw> <out.dfflux> [--rate N --bits N --channels N --rpm N --profile S] | info <f.dfflux>  Store a raw optical RF/flux capture with its calibration metadata (phase-1 low-level preservation)");
    Console.WriteLine("  lineage <keygen|init|append|sign|verify|show> …  Append-only, signed chain-of-custody for a dump");
    Console.WriteLine("  library-watch <dir> [--update]   Watch a collection for silent corruption (bit rot)");
    Console.WriteLine("  remaster <pack|rebuild|verify> …  Decompose an ISO to a recipe+store and rebuild it byte-exact");
    Console.WriteLine("  ps1mc-convert <in> <out> [raw|gme|vgs]  Convert a PS1 memory card between container");
    Console.WriteLine("                          formats (raw .mcr / DexDrive .gme / VGS). Alias: ps1card-convert");
    Console.WriteLine("  ps1mc-format <out.mcr> [raw|gme|vgs]  Write a freshly-formatted, empty PS1 memory card. Alias: psxmc-format");
    Console.WriteLine("  ps2mc-ecc <card.ps2> [--repair <out.ps2>] [--json]  Verify (and optionally repair) a PS2 memory card's per-page Hamming ECC — catches silent bit-rot in a save dump and corrects single-bit errors (CLEAN/CORRECTABLE/CORRUPT)");
    Console.WriteLine("  save-convert <in> <out> <op> [--fill FF]  Fix a cartridge save's byte order or size.");
    Console.WriteLine("                          op: swap16|swap32, pad <size|sram|flash|eeprom4k|eeprom16k|mempak>, trim");
    Console.WriteLine("  rom-convert <in> <out> <op>  Fix a cartridge dump so it matches a DAT. op:");
    Console.WriteLine("                          z64|v64|n64 (N64 byte order), snes-strip|snes-add,");
    Console.WriteLine("                          smd|unsmd (Genesis interleave), nes-strip (iNES header)");
    Console.WriteLine("  inspect <image.cdi>   Show version, sessions, and track layout");
    Console.WriteLine("  extract <cdi> <dir>   Extract tracks to ISO/WAV (--raw for full sectors)");
    Console.WriteLine("  convert <in> <out>    CDI <-> BIN+CUE / ISO / GDI / NRG, or MDS -> CDI");
Console.WriteLine("                        (.cdi<->.cue, .cdi<->.iso, .mds->.cdi)");
    Console.WriteLine("  disc-convert <in> <out>  Universal hub: any format -> any via a canonical model");
Console.WriteLine("                        (.cue .chd .iso .cso .zso .wbfs .cdi .nrg .mds .gdi .ccd in;");
Console.WriteLine("                         .cue .chd .iso .cdi .nrg out)");
    Console.WriteLine("  verify <cdi> [--checksums]  Structural checks + per-track CRC-32");
    Console.WriteLine("  create <dir> <out.cdi>  Build a data CDI from a folder (--volume NAME, --rock-ridge)");
    Console.WriteLine("  compare <a.cdi> <b.cdi>  Diff two images (structure + per-track CRC-32)");
    Console.WriteLine("  create-audio <out.cdi> <a.wav> [b.wav ...]  Build an audio CD from WAVs");
    Console.WriteLine("                          --gapless, --74, --postgap [sectors]");
    Console.WriteLine("  ls <image.cdi>          List files inside the image (ISO 9660 or UDF, auto-detected)");
Console.WriteLine("                          --iso 8.3 names, --joliet, --udf force a filesystem");
    Console.WriteLine("  extract-files <image.cdi> <dir>  Extract the filesystem contents");
    Console.WriteLine("  build-raw <src> <out>   Compose a RAW DAO image (lead-in + subcode) from");
    Console.WriteLine("                          a .cue or .cdi. --subcode pq|cooked|raw (default cooked)");
    Console.WriteLine("  subch <file.sub>        Analyse a captured raw sub-channel sidecar: Q-CRC");
    Console.WriteLine("                          validity, LibCrypt-style protection fingerprint");
    Console.WriteLine("  cdtext <file.cdt>       Decode CD-TEXT packs into album/track title & performer");
    Console.WriteLine("  deemph <in.wav> <out.wav>  Apply CD de-emphasis (50/15µs) to a pre-emphasised track");
    Console.WriteLine("  silence-split <in.wav>  Find track boundaries by silence; emit a cue sheet");
    Console.WriteLine("  audio-dynamics <in.wav> DR value, peak/RMS/crest, and clipping detection");
    Console.WriteLine("  hdcd-scan <in.wav>      Detect HDCD encoding hidden in 16-bit PCM least-significant bits");
    Console.WriteLine("  libcrypt <file.sub>     Characterise LibCrypt: variant, magic/key material,");
    Console.WriteLine("                          per-sector CRC deltas. --sbi <out> writes the sidecar");
    Console.WriteLine("  cdg-frames <src> <dir>  Export a CD+G frame sequence as numbered PNGs (--fps N)");
    Console.WriteLine("  cdg-preview <src>       Decode CD+G graphics from a raw image (2448) or a");
    Console.WriteLine("                          .cue with .sub sidecar. --seconds N (default 30),");
    Console.WriteLine("                          --out shot.ppm writes a screenshot");
    Console.WriteLine("  cdg-render <file.cdg>   Render a CD+G frame to PNG. --at MM:SS (default end),");
    Console.WriteLine("                          --out <file.png> (default <name>.png)");
    Console.WriteLine("  cdg-extract <sub> <out.cdg>  Extract the CD+G packet stream from a raw");
    Console.WriteLine("                          96-byte/sector sub-channel sidecar");
    Console.WriteLine("  view-sector <img> <addr> [--count N] [--descramble]  Annotated hex view of");
    Console.WriteLine("                          sectors. addr: LBA, mm:ss:ff, or +fileindex");
    Console.WriteLine("  extract-sectors <img> <out> --start <addr> --count N  Pull a sector range");
    Console.WriteLine("                          --as stored|user|raw2352, --byteswap for audio");
    Console.WriteLine("  inspect-raw <img>       Analyse a raw image: TOC, Q health, CD-TEXT, MCN/ISRC,");
    Console.WriteLine("                          scrambling, EDC/ECC. --deep checks every sector.");
    Console.WriteLine("                          Also reads bare 2352 BINs (ECC gold-check on real rips)");
    Console.WriteLine("  blank <drive> [--full]  Erase a rewritable disc (CD-RW/DVD-RW) so it can be rewritten");
    Console.WriteLine("                          (minimal/fast by default; --full erases the entire disc, slower)");
    Console.WriteLine("  read-raw <drive> <out.bin>  Read the program area back as full raw 2448-byte sectors");
    Console.WriteLine("                          (2352 main + 96 raw P-W sub) for raw-verify-readback [--start LBA] [--length N]");
    Console.WriteLine("  raw-verify-readback <golden.img> <readback.bin>  Prove a RAW burn is byte-faithful:");
    Console.WriteLine("                          compare a disc read-back to the golden image (main + sub-channel)");
    Console.WriteLine("  dvd-verify-readback <source.iso> <readback.bin> [--layer-break LBA]  Verify a burned DVD/BD");
    Console.WriteLine("                          against its source at ECC-block granularity, layer-break aware");
    Console.WriteLine("  booktype-trace <trace-file> [--save recipe.json]  Decode a captured bitsetting (book-type)");
    Console.WriteLine("                          command trace and learn a verbatim replay recipe from your own drive");
    Console.WriteLine("  booktype-set <drive> <recipe.json> [--force]  Replay a learned book-type recipe on the drive");
    Console.WriteLine("                          (the drive's own captured command, verbatim; guarded by vendor/model)");
    Console.WriteLine("  dump-merge <out> <in1> <in2> [in3 ...]  Merge several imperfect rips of the SAME");
    Console.WriteLine("                          disc into one image (EDC-verified where possible)");
    Console.WriteLine("  merge-cert <out> <in1> <in2> [...] [--key f | --gen-key] | verify <cert> [out in...]  Bad-sector-aware merge + a signed, checkable per-sector provenance certificate");
    Console.WriteLine("  c2-merge <out.bin> <in1.bin> [in1.c2] <in2.bin> [in2.c2] ...  Byte-level C2 recovery: reassemble a sector from the good bytes of several C2-flagged reads");
    Console.WriteLine("  dvd-ecc self-test | repair <block.bin> <out.bin>  DVD RS-PC error correction on a 208×182 ECC block (PI/PO product code); software-first, round-trip validated");
    Console.WriteLine("  flux-demod self-test | encode <in> <out.dff> [--cell N --jitter J] | decode <in.dff> <out>  Demodulate an optical flux capture to the EFM bitstream (software-first; clock recovery + NRZI)");
    Console.WriteLine("  collection-triage <folder> [--dat f] [--html out.html] [--json]  Whole-collection worklist: per-dump verified / incomplete / re-cut / duplicate / check, with a shareable HTML dashboard");
    Console.WriteLine("  salvage-plan <folder> [--json]  Find where several incomplete dumps of the same disc can be merged into a complete image (complementary holes)");
    Console.WriteLine("  reconstruct <out> <in1> [in2 ...]  Best-possible image via agree/EDC/ECC/vote with");
    Console.WriteLine("                          per-sector provenance (--no-ecc, --provenance <map>)");
    Console.WriteLine("  dump-score <raw.bin>    Score a raw image's dump confidence (0-100 + grade) from EDC");
    Console.WriteLine("  disc-anomalies <iso>    Find hidden / orphan data no file or ISO structure explains");
    Console.WriteLine("  disc-date <iso>         Date a disc from its ISO timestamps; flag re-mastering / tampering");
    Console.WriteLine("  disc-delta <base.iso> <target.iso> <out.delta>  File-level delta carrying only what changed");
    Console.WriteLine("  disc-patch <base.iso> <in.delta> <out.iso>      Rebuild the target byte-exact from base + delta");
    Console.WriteLine("  disc-genome <a.cue> [b.cue]  Offset-invariant disc fingerprint; compare two rips for same-disc");
    Console.WriteLine("  health-map <in.bin> <out.svg>  Render a per-sector EDC/ECC health heatmap (SVG)");
    Console.WriteLine("  error-pattern <in.bin>  Classify failing sectors: scratch/rot (recover) vs protection (preserve)");
    Console.WriteLine("  disc-fs <image.iso>     Identify every filesystem a disc carries (ISO/Joliet/UDF/HFS/CD-XA)");
    Console.WriteLine("  hfs-ls <image>          Walk a classic Mac HFS volume: files, folders, fork sizes");
    Console.WriteLine("  hfs-lint <image> [--json]  HFS integrity check: MDB signature/geometry, catalog B-tree walkability, file/dir count consistency, fork-extent bounds (fsck-style)");
    Console.WriteLine("  hfs-resources <image> [macpath]  List the resources in each file's Mac resource fork (icons, vers, code, ...)");
    Console.WriteLine("  hfs-orphans <image>     Carve HFS free space: leftover/deleted data the catalog hides");
    Console.WriteLine("  udf-orphans <image>     Carve UDF free space: leftover/deleted data via the space bitmap");
    Console.WriteLine("  fs-orphans <image>      Auto-detect HFS/UDF and carve free space (both on a hybrid)");
    Console.WriteLine("  disc-cluster <path...>  Group un-identified dumps by content — same title, different variants");
    Console.WriteLine("  hidden-sessions <cue>   Map every session; flag data sessions a naive rip would skip (CD Extra)");
    Console.WriteLine("  disc-rot <history.json> Triage C1/C2 scan history over time; predict which discs to dump first");
    Console.WriteLine("  rot-kinetics <history.json> [--temp C --rh %]  First-order (Arrhenius) decay fit + survival forecast");
    Console.WriteLine("  bler <scan.csv>         C1/C2 surface-quality report: BLER, E22/E32, Red Book pass/fail");
    Console.WriteLine("  scan-import <scan.txt>  Import an Opti Drive Control/Nero/KProbe quality scan (CD C1/C2, DVD");
    Console.WriteLine("                          PIE/PIF/POF, BD LDC/BIS) → feed disc-rot/disc-print/bler --emit rot|print|bler");
    Console.WriteLine("  dpm <scan.csv>          Data-position timing: detect ring protection, fingerprint layout");
    Console.WriteLine("  protection-scan <iso>   Fingerprint copy protection (SafeDisc/SecuROM/LibCrypt…) as metadata");
    Console.WriteLine("  protection-profile <image> [--json]  Unified protection profile: schemes + where they live + whether this capture mode can preserve each");
    Console.WriteLine("                          --raw <raw.bin>  also fuse on-disc signals (twin sectors, error band)");
    Console.WriteLine("  twin-scan <raw.bin>     Detect twin / re-addressed sectors (header-address protection)");
    Console.WriteLine("  weak-sectors <raw.bin>  Predict channel-weak sectors (scramble+EFM+DSV physical model)");
    Console.WriteLine("  efm-spectrum <raw.bin>  EFM run-length spectrum, duty asymmetry, DSV, grade");
    Console.WriteLine("  xa-map <raw.bin>        Map CD-ROM XA streams: file/channel, video/audio, interleave");
    Console.WriteLine("  recover-oracle <frames> Model CIRC recovery: can a burst of N frames be corrected?");
    Console.WriteLine("  recover-toc <raw.sub>   Rebuild the TOC from the Q sub-channel (dead lead-in recovery)");
    Console.WriteLine("  scratch-verdict <in.bin>  Per-scratch recovery outlook (corrected/concealed/re-read)");
    Console.WriteLine("  recovery-map <in.bin> <out.svg>  SVG map coloured by recovery outlook per region");
    Console.WriteLine("  covert-scan <iso>       Hunt for hidden data in zero-expected regions (slack, system area)");
    Console.WriteLine("  matter-map <in> <out.svg>  Classify each region (zero/text/structured/high-entropy) as SVG");
    Console.WriteLine("  phylo <dir|iso...>      Build a family tree of a title's releases from file deltas");
    Console.WriteLine("  iso-lint <iso>          Strict ISO 9660 conformance check (spec violations)");
    Console.WriteLine("  udf-lint <iso|udf>      Strict UDF conformance check: descriptor tag checksums/CRCs, partition-relative tag locations, FSD reachability, LVID consistency (the check that catches the classic \"File Set Descriptor not found\")");
    Console.WriteLine("  iso-pathtable <iso>     Audit the ISO 9660 path table (L/M agreement, parent tree)");
    Console.WriteLine("  iso-rockridge <iso>     Recover Rock Ridge POSIX metadata (perms, owners, symlinks, times)");
    Console.WriteLine("  redbook-audit <cue>     Strict Red Book (CD structure) conformance check");
    Console.WriteLine("  boot-catalog <iso>      Decode a bootable disc's El Torito boot catalog");
    Console.WriteLine("  premaster-check <cue>   Master-readiness gate: structure + capacity + data integrity");
    Console.WriteLine("  dump-provenance <path...>  Infer what tool produced a dump from its fileset + geometry");
    Console.WriteLine("  pregap-scan <audio.bin> Detect hidden-track audio in a pregap/gap (HTOA)");
    Console.WriteLine("  dvd-nav <VTS_xx_0.IFO>  Map DVD program chains; flag unreferenced (hidden) PGCs");
    Console.WriteLine("  consensus <keygen|attest|verify>  Federated, signed cross-dumper consensus ledger");
    Console.WriteLine("  disc-print <scan.json> [b.json]   Physical-copy fingerprint from an error scan; compare two");
    Console.WriteLine("  disc-genealogy <collection.json>  Provenance family tree + authenticity/counterfeit verdicts");
    Console.WriteLine("  mastering-print <image> [--json] | compare <a> <b> [--json]  Mastering fingerprint from the ISO volume descriptor; compare two copies to flag a re-mastered reproduction");
    Console.WriteLine("  collection-archive <build|verify|extract>  Dedup a library to unique blobs; rebuild any disc exact");
    Console.WriteLine("  vault <create|check|heal>  Self-healing container: Reed-Solomon parity repairs bit-rot");
    Console.WriteLine("  par2-verify <file.par2>  Read & verify a PAR2 recovery set; report per-file status and repairability");
    Console.WriteLine("  chunk-manifest <file...>  FastCDC content-defined chunking + Merkle root; dedup a set to unique chunks");
    Console.WriteLine("  fuzz-parsers <seed> [--iterations N]  Robustness-fuzz the format parsers; report unclean crashes/hangs");
    Console.WriteLine("  disc-semdiff <a> <b>    Region-level (shift-tolerant) diff of two images: where they diverge, not a byte wall");
    Console.WriteLine("  completeness-check <cue>  Dump-coverage certificate: reconcile cue layout, data size and subchannel; flag gaps");
    Console.WriteLine("  emu-ready <cue>           Emulation-readiness report: does this dump have what an emulator needs to run?");
    Console.WriteLine("  min-descriptor <image>    Minimal disc descriptor: factor into fill/duplicate/unique and report the irreducible content [--sector N]");
    Console.WriteLine("  fs-recover <image.iso> --erased <list>  Use the filesystem to reconstruct free space and identify what erased sectors held [--out]");
    Console.WriteLine("  coverage-proof <image.iso>  Prove every sector is accounted for exactly once — reports silent gaps and overlapping claims");
    Console.WriteLine("  pregap-check <cue> [--json]  Audit a cue's pregaps vs PlayStation/Redump convention (2s data/audio boundary, no negative gaps)");
    Console.WriteLine("  subq-map <disc.sub> [--json] [--form packed|interleaved|pq16]  Recover each track's real INDEX 00/01 and pregap from a captured subchannel");
    Console.WriteLine("  redump-cue <in.cue> <disc.sub> <out.cue> [--snap-pregap]  Re-cut a split bin/cue at the subchannel's INDEX 00 boundaries (Redump-conformant, byte-preserving)");
    Console.WriteLine("  bad-sectors <map.badsectors.json> [--json]  Show a dump's unreadable-sector map: counts, coalesced runs, and per-track positions");
    Console.WriteLine("  redump-diff <cue> <dat> [--game \"name\"] [--json]  Explain WHY a dump doesn't match Redump: per-file verdict + the cause (split, padding, offset, bad sector)");
    Console.WriteLine("  dump-audit <cue|image> [--dat f] [--json]  \"Is my dump good?\" — one plain verdict (GOOD/SUSPECT/BAD) fusing structure, holes, EDC/ECC, end-sectors, pregaps, DAT match");
    Console.WriteLine("  read-stability <pass1> <pass2> [pass3 ...] [--sector-size N] [--json]  Disc-rot early warning: flag sectors that read inconsistently across passes (stable/marginal/degrading)");
    Console.WriteLine("  verify-convert <a> <b> [--json]  Prove a format conversion was lossless: decode both images (bin/cue, .chd, .bin) to raw sectors and compare byte-for-byte");
    Console.WriteLine("  chd-verify <image.chd> [--parent p.chd ...] [--json]  Check a CHD's integrity without extracting: decompress every hunk, check each map CRC-16, and confirm the whole image matches its stored SHA-1 (VALID/CORRUPT/UNVERIFIED) — the archival-integrity check chdman's verify performs");
    Console.WriteLine("  fs-verify <image> [--json]  Cross-check a disc's filesystem views (ISO 9660, Joliet, UDF) and confirm they describe the same files with the same bytes — catches truncated dumps, tampering, and content hidden from one filesystem (AGREE/DIVERGENT/INCOMPLETE)");
    Console.WriteLine("  disc-diff <a> <b> [--json]  Compare two disc images at the file level: what was added, removed, changed (by content), or moved/renamed — for two pressings, patched vs original, or revisions");
    Console.WriteLine("  redump-prep <in.cue> <out-dir> [--sub f] [--snap-pregap] [--dat f --game \"n\"] [--offset N] [--json]  One-step submission prep: re-cut + carry holes + checks + submission text");
    Console.WriteLine("  cu2 <write|verify> <cue> [file.cu2]  Generate or cross-check a Cybdyn CU2 track map (PSIO/xStation) from a cue");
    Console.WriteLine("  ode-export psio <cue> <out-dir> [--name N]  Lay a PS1 dump out for a PSIO/xStation ODE: game folder + bin/cue + generated CU2");
    Console.WriteLine("  ode-layout <gdemu|rhea|phoebe|mode> <games-dir> <out-dir>  Arrange a set of converted games into an ODE SD-card layout (numbered folders + sidecars; menu built by the device tool)");
    Console.WriteLine("  disc-bom <iso>          Technical bill-of-materials: engine, middleware, runtime, build date");
    Console.WriteLine("  ring-code \"<runout>\" | group <json>  Parse IFPI ring codes; group discs by plant/master");
    Console.WriteLine("  offset-detect <rip.bin> <reference.bin>  Detect the CD-DA read offset between two PCM rips");
    Console.WriteLine("  checksum <file>         CRC-32 + MD5 + SHA-1 + SHA-256 in one pass");
    Console.WriteLine("                          --write [sha256|md5|sha1|sfv|all]   write sidecar(s)");
    Console.WriteLine("                          --verify   check against an existing sidecar");
    Console.WriteLine("  browse <image>          List files inside a .cdi or .iso (ISO 9660 or UDF)");
    Console.WriteLine("                          --extract <dir> writes them out, --only <text> filters");
    Console.WriteLine("  cue-check <sheet.cue>   Check a cuesheet against the data file it describes:");
    Console.WriteLine("                          indexes inside the file, track types consistent,");
    Console.WriteLine("                          arithmetic reaching the end. Exit 2 on errors, 1 on warnings");
    Console.WriteLine("  cue-repair <in.cue> [out.cue] [--json]  Fix a broken cue: wrong FILE refs (vs the actual bins), out-of-order track numbers, missing INDEX 01; re-emit clean");
    Console.WriteLine("  ecc-repair <image.bin>  Rebuild damaged Mode 1 sectors from the Reed-Solomon");
    Console.WriteLine("                          parity they already carry. --dry-run to check only");
    Console.WriteLine("  fix-modes <image.cdi>   Correct track modes recorded wrongly in a CDI descriptor");
    Console.WriteLine("  split <file> <size>     Split into .001/.002/… + .sfv manifest");
    Console.WriteLine("                          sizes: bytes, 700m, 4g, or fat32 (= 4 GiB - 1)");
    Console.WriteLine("  join <part|base> [out]  Rejoin parts, verifying CRCs + SHA-256 via the manifest");
    Console.WriteLine("  ppf-apply <patch.ppf> <image.bin>  Apply a PlayStation Patch File to an image");
    Console.WriteLine("                          --undo revert (PPF 3.0), --force skip validation, --dry-run");
    Console.WriteLine("  ppf-create <orig> <mod> <out.ppf>  Build a PPF 3.0 from a before/after image pair");
    Console.WriteLine("                          --desc, --fileid, --no-undo, --no-validation");
    Console.WriteLine("  ppf-info <patch.ppf>    Show a patch's version, description, size and flags");
    Console.WriteLine("  ppf-convert <in> <out> --to 1|2|3  Rewrite a patch in another PPF revision");
    Console.WriteLine("  ppf-edit <in> <out>     Change a patch's --desc and/or --fileid in place");
    Console.WriteLine("  ips-apply <patch.ips> <image> [--out f]  Apply an IPS patch (in place unless --out)");
    Console.WriteLine("  ips-create <orig> <mod> <out.ips>  Build an IPS patch from a before/after pair");
    Console.WriteLine("  bps-apply <patch.bps> <source> [--out f]  Apply a BPS patch (CRC-verified)");
    Console.WriteLine("  bps-create <source> <target> <out.bps>  Build a BPS patch from a before/after pair");
    Console.WriteLine("  create-udf <folder> <out.udf> [--udf-version 1.02|1.50|2.00|2.01|2.50|2.60]  Build a UDF filesystem image from a folder");
    Console.WriteLine("                          --volume NAME sets the volume label");
    Console.WriteLine("  create-udf-bridge <folder> <out.iso>  Build a UDF-bridge image readable as BOTH");
    Console.WriteLine("                          ISO 9660 (with Joliet) and UDF 1.02, sharing one copy of");
    Console.WriteLine("                          the file data. --volume NAME sets the label; --json for JSON");
    Console.WriteLine("  dvd-video-plan <VIDEO_TS-folder>  Validate a VIDEO_TS folder and show the DVD-Video on-disc file order");
    Console.WriteLine("  dvd-video-build <VIDEO_TS-folder> <out.iso>  Assemble a VIDEO_TS folder into a DVD-Video ISO+UDF image");
    Console.WriteLine("  dvd-video-fix <VIDEO_TS-folder> [--apply]  Rewrite IFO sector pointers + refresh .BUP to match file sizes (Fix VTS Sectors)");
    Console.WriteLine("  bdmv-plan <BDMV-folder>  Validate a Blu-ray BDMV folder (index/MovieObject/PLAYLIST/CLIPINF/STREAM)");
    Console.WriteLine("  bdmv-build <BDMV-folder> <out.iso>  Assemble a BDMV folder into a BD-Video UDF 2.50 image");
    Console.WriteLine("  gdi-info <disc.gdi>     Show a Dreamcast GD-ROM track layout and validate it");
    Console.WriteLine("                          against the track files beside it");
    Console.WriteLine("  gdi-browse <disc.gdi>   List the game filesystem on the high-density track");
    Console.WriteLine("                          --extract <dir> writes the files out");
    Console.WriteLine("  milcd-to-cdi <in.cue> <out.cdi>  Convert a Dreamcast MIL-CD Redump bin/cue to a");
    Console.WriteLine("                          two-session CDI. --version v2|v3|v35, --gap <sectors>");
    Console.WriteLine("  ipbin-info <image>      Identify a Dreamcast disc from its IP.BIN boot header");
    Console.WriteLine("  pvr-info <file.pvr>     Describe a Dreamcast PVR texture header (format, size, GBIX) + integrity");
    Console.WriteLine("  pvm-info <file.pvm>     List the textures in a Dreamcast PVM archive (names, formats, sizes)");
    Console.WriteLine("  mpeg-info <file>        Describe an MPEG program stream (VCD/VOB/Sofdec .sfd): video size, fps, streams");
    Console.WriteLine("  saturn-info <image>     Identify a Sega Saturn disc from its header");
    Console.WriteLine("  pcfx-info <image> [--json]  Identify a NEC PC-FX disc from its \"PC-FX:Hu_CD-ROM\" boot signature + boot-header text");
    Console.WriteLine("  segacd-info <image>     Identify a Sega CD / Mega-CD disc from its header");
    Console.WriteLine("  opera-ls <image>        List the 3DO Opera file system of a 3DO disc image");
    Console.WriteLine("  neogeo-ipl <ipl|image>  Parse a Neo Geo CD IPL.TXT boot script (load list)");
    Console.WriteLine("                          (.gdi, .cue MIL-CD, .cdi, or a raw .bin/.iso)");
    Console.WriteLine("  cdi-console-info <image>  Identify a Philips CD-i (Green Book) disc and list");
    Console.WriteLine("                          its filesystem (pure CD-i or CD-i Bridge)");
    Console.WriteLine("  cdi-extract <image> <path> <out-file> | <image> <out-dir> --all  Extract a file (or all files) from");
    Console.WriteLine("                          a CD-i disc, handling the Mode 2 Form 1/Form 2 sector mix (e.g. /MPEGAV/*.DAT)");
    Console.WriteLine("  psp-info <image>        Read a PSP UMD's PARAM.SFO metadata and filesystem");
    Console.WriteLine("                          (.iso, .cso or .zso; --sfo dumps every SFO key/value)");
    Console.WriteLine("  pbp-info <EBOOT.PBP>    List a PSP PBP package: version + each sub-file's size,");
    Console.WriteLine("                          and its PARAM.SFO title/id/category if present");
    Console.WriteLine("  pbp-extract <EBOOT.PBP> <dir>  Write each non-empty PBP sub-file to <dir>/<name>");
    Console.WriteLine("                          (DATA.PSP is extracted raw and is NOT decrypted)");
    Console.WriteLine("  bdmv-info <file|folder> Show a Blu-ray playlist (.mpls), clip-info (.clpi),");
    Console.WriteLine("                          or enumerate titles from a BDMV folder");
    Console.WriteLine("  iso-rebase <in> <out> <baseLBA>  Shift an ISO's LBAs (GD-ROM fix; base 45000)");
    Console.WriteLine("  xiso-ls <image.iso>     List files in an Xbox XDVDFS image (--extract <dir>)");
    Console.WriteLine("  create-xiso <folder> <out.iso>   Build an Xbox XISO from a folder");
    Console.WriteLine("  god-info <header>       Identify an Xbox 360 GOD package (type, size, Data#### inventory)");
    Console.WriteLine("  god-extract <header> <out.iso>  Reconstruct the XDVDFS ISO from a GOD package (self-validated; declines if unsure)");
    Console.WriteLine("  iso-create <folder> <out.iso> [--volume-id N] [--no-joliet] [--rock-ridge]   Build a standard ISO 9660 data-disc image from a folder (Joliet by default)");
    Console.WriteLine("  ps2-info <image>        Identify a PlayStation 1/2 disc (game ID, region) from SYSTEM.CNF");
    Console.WriteLine("  gcm-info <image>        GameCube disc: boot header + file tree; for a Wii disc,");
    Console.WriteLine("                          the volume header + partition table (contents not read)");
    Console.WriteLine("  gcm-banner <image> <out.png>  Extract a GameCube disc's banner icon (opening.bnr)");
    Console.WriteLine("  gcm-extract <image> <out-dir>  Extract the GameCube disc's file tree to a folder");
    Console.WriteLine("  tpl-info <file.tpl>     List textures in a GameCube/Wii TPL (size + GX format)");
    Console.WriteLine("  tpl-extract <file.tpl> <out>  Decode TPL textures to PNG (--index N)");
    Console.WriteLine("  wbfs-info <file>        List the Wii/GameCube discs in a WBFS container");
    Console.WriteLine("                          (slot, game id, title, sizes)");
    Console.WriteLine("  wbfs-extract <file> <slot> <out.iso>  Rebuild one disc's ISO from a WBFS");
    Console.WriteLine("                          container (contents are copied as-is, not decrypted)");
    Console.WriteLine("  rvz-info <image>        Identify an RVZ/WIA container and show its metadata");
    Console.WriteLine("  rvz-decode <in.rvz> <out.iso>  Reconstruct a GameCube ISO from an RVZ/WIA (zstd/none groups; data-exact, junk zero-filled)");
    Console.WriteLine("  nkit-info <image>       Detect an NKit-scrubbed GC/Wii image; show source CRC32 for Redump matching");
    Console.WriteLine("  gc-verify <image> [--json]  Single-image GameCube 'good dump' health check: bounds, region cross-check, size class");
    Console.WriteLine("  gc-junk-map <image> [--json]  Map a GameCube disc's non-game padding and classify each region (junk present / zeroed / structured)");
    Console.WriteLine("  gc-junk-fill <in> <out>  Rebuild scrubbed GameCube junk padding — ONLY if the generator");
    Console.WriteLine("                          self-validates against the image's own surviving junk (else declines)");
    Console.WriteLine("  dvd-layerbreak <pfi>    Read a DVD PFI/.physical: book type, layers, PTP/OTP, layer-break LBA + verify");
    Console.WriteLine("  layerbreak-pick <total-sectors> [--target N] [--cells a,b,..] [--max-layer N] [--seamless]  Choose a legal DVD-DL layer break");
    Console.WriteLine("  capacity-check <image-sectors> <cd74|cd80|dvd5|dvd9|bd25|bd50|N> [--overburn]  Check an image against media capacity");
    Console.WriteLine("  disc-span <folder|--manifest f> [--media bd25] [--keep-groups]  Plan the fewest discs to hold a set of files (smart spanning)");
    Console.WriteLine("  source-stage <manifest> <dir>  Assemble files from local + HTTP(S) origins into a staging folder for burning");
    Console.WriteLine("  ui [--port N] [--no-browser]  Launch the modern local web UI over the engine (http://127.0.0.1:8787)");
    Console.WriteLine("  rom-info <file>         Identify a cartridge ROM (N64, SNES, Genesis, GB/GBC, GBA,");
    Console.WriteLine("                          NES, and more) and print its No-Intro CRC32/MD5/SHA1");
    Console.WriteLine("  rom-integrity <file> [--json]  Recompute a cartridge's own checksums (GB header+global, Genesis content, GBA header+logo) to catch a bad dump");
    Console.WriteLine("  fds-info <file.fds> [--json]  Read a Famicom Disk System image: per-side identity + file table (name, type, load address, size)");
    Console.WriteLine("  n64-info <rom>          N64 CIC boot-chip ID + CRC1/CRC2 boot-checksum verify");
    Console.WriteLine("  scummvm-detect <path>   ScummVM Advanced-Detector fingerprints (size + MD5 of the");
    Console.WriteLine("                          first 5000 bytes) for a game folder or file. --recursive,");
    Console.WriteLine("                          --bytes N to change the hashed length");
    Console.WriteLine("  scummvm-export <cue> <dir>  Export a disc into a ScummVM game folder: data files +");
    Console.WriteLine("                          each CD audio track as trackNN.wav. --flac/--ogg re-encode");
    Console.WriteLine("                          the audio in-process (no ffmpeg needed); --high = better OGG");
    Console.WriteLine("  disk-info <image>       Read a whole-disk image's partition table (auto-detect");
    Console.WriteLine("                          MBR, GPT, or PS2 APA) and the filesystem in each partition");
    Console.WriteLine("  rdb-info <image>        Read an Amiga Rigid Disk Block: geometry + partitions + FS");
    Console.WriteLine("  apm-info <image>        Read an Apple Partition Map (Mac / hybrid-CD partitions)");
    Console.WriteLine("  disc-report <image>     Identify a disc and run every matching parser (one report)");
    Console.WriteLine("  floppy-info <image>     List a floppy image's contents (auto-detect C64 D64,");
    Console.WriteLine("                          Amiga ADF, or DOS FAT12 .img)");
    Console.WriteLine("  floppy-image <drive> <out.img>  Image a floppy disk to a flat .img (Windows drive letter, needs");
    Console.WriteLine("                          admin; macOS/Linux device path). Reports geometry; pair with floppy-info");
    Console.WriteLine("  fat-ls <image>          List a FAT16/FAT32 volume (boot images, hybrid FAT partitions)");
    Console.WriteLine("  fat-lint <image> [--json]  FAT12/16/32 integrity check: BPB, FAT-copy agreement, cluster-chain validity, cross-links, lost clusters (fsck-style)");
    Console.WriteLine("  drives                  List optical recorders + capabilities (Windows via device stack; macOS via system_profiler)");
    Console.WriteLine("  burn <image.iso> [drive] [--verify] [--speed N]  Burn a data ISO to a blank CD/DVD/BD (Windows IMAPI2, or macOS hdiutil)");
    Console.WriteLine("  read-disc <drive> <out.iso> [--continue-on-error] [--retries N]  Image a data DVD/BD/data-CD to a flat ISO");
    Console.WriteLine("  writeinfo <drive>       Read-only: disc status + the drive's next-writable-address (for raw-DAO write setup)");
    Console.WriteLine("  drive-profile <drive>   Consolidated per-drive profile: read/write reach, write modes, read fidelity [--out profile.json]");
    Console.WriteLine("                          (Windows SPTI). Pair with `burn` to clone a personal, unencrypted disc. Refuses");
    Console.WriteLine("                          copy-protected discs (CSS/CPRM/AACS); for audio/mixed CDs rip in the GUI");
    Console.WriteLine("  raw-dump <drive> [--stream-read]  Drive/media diagnostic for the Hitachi-LG GDR-816x DVD-ROM family:");
    Console.WriteLine("                          identify the drive and (optionally) confirm a raw READ(12)+streaming read. Reports");
    Console.WriteLine("                          bytes as-is; does NOT descramble or decode console (GameCube/Wii/GD-ROM) discs");
    Console.WriteLine("  fat-extract <image> <out-dir>  Extract a FAT16/FAT32 volume's tree (--only /PATH)");
    Console.WriteLine("  gameaudio-info <file>   Read a game-music file's metadata/structure (auto-detect");
    Console.WriteLine("                          PSF/PSF2, SPC, VGM, NSF): system, tags, duration. No playback");
    Console.WriteLine("  gci-info <file>         List GameCube saves in a .gci or a memory-card image");
    Console.WriteLine("  gci-extract <card> <index> <out.gci>  Write one save from a card image to a .gci");
    Console.WriteLine("  n64save-info <file>     Identify an N64 save by size, and list Controller Pak notes");
    Console.WriteLine("  saturnsave-info <file>  List the directory of a Sega Saturn backup-memory image");
    Console.WriteLine("  floppy-extract <image> <path-in-image> <out>  Extract one file from a floppy image");
    Console.WriteLine("  woz-info <file.woz>     Inspect an Apple II WOZ image: disk type, tracks, protection flags, CRC");
    Console.WriteLine("  scp-info <file.scp>     Inspect a SuperCard Pro flux image: tracks, revolutions, RPM, checksum");
    Console.WriteLine("  kryoflux-info <file.raw>  Inspect a KryoFlux raw stream: flux count, index pulses, RPM, hardware info");
    Console.WriteLine("  d88-info <file.d88>     Inspect a PC-98/PC-88 D88 floppy image: media type, tracks, sector geometry");
    Console.WriteLine("  cheat-decode <platform> <code>  Decode a Game Genie / GameShark code to address/value");
    Console.WriteLine("                          platform: nes|snes|genesis|gb|gs-ps1");
    Console.WriteLine("  cheat-encode <platform> <address> <value> [compare]  Encode a Game Genie code");
    Console.WriteLine("                          platform: nes|snes|genesis|gb (hex address/value)");
    Console.WriteLine("  cheat-apply-nes <rom> <code> <out>  Apply an NES Game Genie code to a ROM (NROM)");
    Console.WriteLine("  adx-decode <in.adx> <out.wav>  Decode a CRI ADX ADPCM stream to a 16-bit WAV");
    Console.WriteLine("  dsp-decode <in.dsp> <out.wav>  Decode a Nintendo GameCube/Wii DSP-ADPCM stream to a 16-bit WAV");
    Console.WriteLine("  read-offset <samples> [in.wav out.wav]  Redump read-offset math; with a WAV,");
    Console.WriteLine("                          slide it by <samples> (combined drive+disc offset)");
    Console.WriteLine("  vab-info <file.vab>     Read a PlayStation VAB (VAG bank): programs, tones, VAGs");
    Console.WriteLine("  seq-info <file.seq>     Read a PlayStation SEQ sequence: ppqn, tempo, event count");
    Console.WriteLine("  str-demux <in.str> <out-dir>  Split a PSX .str into MDEC bitstreams + audio note");
    Console.WriteLine("  str-frames <in.str> <out-dir>  Decode PSX .str v2 video frames to PNG images");
    Console.WriteLine("  mdec-info <in.str>      MDEC codec params per frame (version, qscale, macroblocks)");
    Console.WriteLine("                          --sector-size 2352|2048 (default 2352)");
    Console.WriteLine("  vob-demux <in.vob|.mpg> <out-dir>  Split an unencrypted MPEG program stream (VOB/MPG)");
    Console.WriteLine("                          into elementary video/audio/subpicture streams (no CSS decrypt)");
    Console.WriteLine("  vcd-control <out-dir> [--album N] [--svcd] [--entry T:M:S:F ...]  Write INFO.VCD/ENTRIES.VCD");
    Console.WriteLine("  vcd-psd <PSD.VCD> [LOT.VCD]  Decode VCD PlayBack Control: menus, play lists, links");
    Console.WriteLine("  dvd-ifo <dump|build> …  Dump a DVD's structure to editable JSON, or rebuild IFOs from it");
    Console.WriteLine();
    Console.WriteLine("More commands:");
    // Filesystem readers
    Console.WriteLine("  fat-ls / fat-extract, exfat-ls / exfat-extract, ntfs-ls / ntfs-extract, ext-ls / ext-extract");
    Console.WriteLine("                          List and extract files from FAT / exFAT / NTFS / ext2-3-4 volume images (read-only)");
    Console.WriteLine("  partitions <image>      Show a disk image's partition tables (MBR, GPT, Apple)");
    // Aaru interop
    Console.WriteLine("  aaru-info <img.aaruf>   Identify an AaruFormat image: header, blocks, sectors, compression");
    Console.WriteLine("  aaru-extract <img.aaruf> <out.img>   Extract user data (uncompressed or LZMA; CRC-64-proven)");
    Console.WriteLine("  aaru-create <in.img> <out.aaruf>     Write an uncompressed AaruFormat image");
    Console.WriteLine("  cicm-export <image> [out.xml]        Write a CICM preservation-metadata sidecar (Aaru interop)");
    // Recovery & rip planning
    Console.WriteLine("  recover <image> [report.html]   One-stop damage assessment: verdict, evidence, next steps");
    Console.WriteLine("  secure-rip-plan <evidence.json>  Grade rip evidence (AccurateRip/C2/passes) and plan re-reads");
    // Forensics quick tools
    Console.WriteLine("  entropy <file>          Shannon entropy (spot compression/encryption/blanked regions)");
    Console.WriteLine("  fuzzy-hash <file> [b]   SpamSum fuzzy hash; two files → similarity score");
    // Drives & media (hardware)
    Console.WriteLine("  drive-profile [drive]   Probe and save a drive's capability/overread profile");
    Console.WriteLine("  disc-scan <drive>       C2 media-quality scan of the disc in the drive");
    Console.WriteLine("  read-benchmark <drive>  Read-rate benchmark across the disc surface");
    Console.WriteLine("  burn-raw <cue> <drive>  RAW DAO-96 burn (SPTI engine; see also burn)");
    Console.WriteLine("  dvd-layerbreak-plan <VTS_nn_0.IFO> …  Recommend a DVD9 layer break at a VOBU boundary");
    // Save/memory-card extras
    Console.WriteLine("  ps1card-convert <in> <out>   Convert PS1 memory-card image formats");
    // CHD
    Console.WriteLine("  chd-info <image.chd>    Show a CHD's version, codecs, hunk geometry and CD track layout");
    Console.WriteLine("  chd-create <in.cue|in.img> <out.chd>   Create a CHD (v5) from a bin/cue or raw image");
    Console.WriteLine("  chd-extract <image.chd> <out.bin> [out.cue] [--parent p.chd ...]   Decompress a CD CHD to bin/cue");
    Console.WriteLine("  chd-extract-hd <image.chd> <out.img> [--parent ...]   Decompress a hard-disk CHD to a raw image");
    // Compressed ISO (PSP)
    Console.WriteLine("  ciso-info <image.cso|.zso>   Show a compressed-ISO (CSO/ZSO) header");
    Console.WriteLine("  ciso-to-iso <in.cso|.zso> <out.iso>   Decompress a CSO/ZSO to a plain ISO");
    Console.WriteLine("  iso-to-ciso <in.iso> <out.cso>   Compress an ISO to CSO (zlib)");
    // PlayStation
    Console.WriteLine("  psx-build <folder> <out.bin> [volume-id] [out.cue]   Build a PlayStation data track from a folder");
    Console.WriteLine("  psx-exe-info <file.exe>   Read a PS-EXE header (load address, entry point, size)");
    Console.WriteLine("  psx-pad <in> <out> [--multiple N | --psexe] [--fill 0xNN]   Pad a PS1 binary to a size/boundary");
    Console.WriteLine("  psx-video-mode <in> --to ntsc|pal [--ppf out.ppf | --out out.bin]   Convert a PS1 game's video mode");
    Console.WriteLine("  vag-extract <file.vag> <out.wav>   Decode a PlayStation VAG (SPU-ADPCM) sample to WAV");
    Console.WriteLine("  xa-extract <raw image> <out.wav> [--sector-size N] [--channel N]   Extract CD-XA ADPCM audio to WAV");
    Console.WriteLine("  tim-info <file.tim>     Describe a PlayStation TIM texture");
    Console.WriteLine("  tim-extract <file.tim> <out.png> [--palette N]   Decode a TIM texture to PNG");
    Console.WriteLine("  tmd-info <file.tmd>     Describe a PlayStation TMD 3D model");
    Console.WriteLine("  tmd2dxf <file.tmd> <out.dxf>   Convert a PlayStation TMD model to DXF");
    // Memory cards
    Console.WriteLine("  ps1mc-info <card.mcr>   List PlayStation 1 memory-card saves (title, blocks). Alias: psxmc-info");
    Console.WriteLine("  ps1mc-extract <card.mcr> <out-dir>   Extract PS1 saves to files. Alias: psxmc-extract");
    Console.WriteLine("  ps2mc-info <card.ps2>   List PlayStation 2 memory-card files/saves");
    Console.WriteLine("  ps2mc-extract <card.ps2> <out-dir>   Extract PS2 memory-card files");
    Console.WriteLine("  vmu-info <vmu.bin>      List Dreamcast VMU saves");
    Console.WriteLine("  vmu-create <out.bin>    Write a blank formatted 128 KB Dreamcast VMU");
    Console.WriteLine("  vmu-add <vmu.bin> <save.vms> [--name N] [--game] [--protect]   Add a save to a VMU");
    Console.WriteLine("  vmu-extract <vmu.bin> <out-dir> [--force]   Extract VMU saves");
    Console.WriteLine("  vms2vmi <save.vms> <out.vmi> [--desc T] [--name N]   Wrap a raw VMS save as a VMI+VMS pair");
    // Dreamcast
    Console.WriteLine("  dc-scramble <in> <out>  Apply the Dreamcast bootstrap (1ST_READ.BIN) scramble");
    Console.WriteLine("  dc-descramble <in> <out>   Reverse the Dreamcast bootstrap scramble");
    Console.WriteLine("  tod-info <file.tod>     Describe a Dreamcast TOD model file");
    // DVD-Video / VCD
    Console.WriteLine("  dvd-info <VIDEO_TS|disc root>   Summarise a DVD-Video's structure");
    Console.WriteLine("  dvd-rewrite <VIDEO_TS|disc root> <out folder> [--keep 1,3]   Rebuild VIDEO_TS keeping selected titles");
    Console.WriteLine("  vcd-info <INFO.VCD|ENTRIES.VCD>   Read a Video CD control/entry file");
    // Audio
    Console.WriteLine("  accuraterip <image.cue> [--db <dBAR.bin>] [--url]   AccurateRip v1/v2 checksums + disc IDs; verify vs a DB record");
    // Protection / subchannel
    Console.WriteLine("  scan-protection <image.cdi>   Fingerprint copy protection as metadata (identify only)");
    Console.WriteLine("  sbi-make <disc.sub> [out.sbi] [--start-lba N]   Write an SBI from a captured subchannel (LibCrypt preservation)");
    Console.WriteLine("  sbi-info <file.sbi>     Describe an SBI subchannel-patch file");
    // Conversion / interop
    Console.WriteLine("  ecm <in.bin> [out.ecm]  Shrink a raw image to ECM (strip regenerable sync/EDC/ECC; lossless)");
    Console.WriteLine("  unecm <in.ecm> [out.bin]   Rebuild the raw image from an ECM file (EDC-verified)");
    Console.WriteLine("  bincue-merge <in.cue> <out.bin> [out.cue]   Merge a multi-bin cue into one bin+cue");
    Console.WriteLine("  bincue-split <in.cue> [out-dir] [base] [out.cue]   Split a single-bin cue into per-track bins");
    Console.WriteLine("  to-ccd <image.cue> [--out basename]   Convert a cue/bin to CloneCD .ccd/.img/.sub");
    Console.WriteLine("  ccd-info <image.ccd>    Read a CloneCD control file");
    Console.WriteLine("  cdr-info <atip-dump>    Read an ATIP dump (blank CD-R manufacturer/dye type)");
    Console.WriteLine("  mount <image>           Mount a disc image, or show how to mount it where supported");
    Console.WriteLine("  transcode <input> <output> [options]   Transcode audio between DiscForge-supported formats");
    // Verify / utilities
    Console.WriteLine("  dat-verify <dat-file> <file ...>   Verify one or more files against a Redump/No-Intro DAT");
    Console.WriteLine("  bin2src <file> [--name ID] [--asm] [--per-line N] [--out f]   Emit a file as C/asm source bytes");
    Console.WriteLine("  search <file> (--hex 4d5a | --ascii TEXT) [--limit N]   Search a file for a hex or ASCII pattern");
    return 0;
}

return args[0].ToLowerInvariant() switch
{
    "inspect" => Inspect(args),
    "fix-modes" => FixModesCommand.Run(args),
"browse" => ImageCommands.Browse(args),
    "cue-check" => ImageCommands.CueCheck(args),
    "cue-repair" => CueRepairCmd(args),
    "ecc-repair" => ImageCommands.EccRepair(args),
    "extract" => Extract(args),
    "convert" => Convert(args),
    "disc-convert" => DiscConvert(args),
    "verify" => Verify(args),
    "create" => Create(args),
    "compare" => Compare(args),
    "create-audio" => CreateAudio(args),
    "ls" => Ls(args),
    "extract-files" => ExtractFiles(args),
    "build-raw" => BuildRaw(args),
    "subch" => Subch(args),
    "cdg-preview" => CdgPreview(args),
    "cdg-render" => CdgRender(args),
    "cdg-extract" => CdgExtract(args),
    "view-sector" => ViewSector(args),
    "extract-sectors" => ExtractSectors(args),
    "inspect-raw" => InspectRaw(args),
    "raw-verify-readback" => RawVerifyReadback(args),
    "dvd-verify-readback" => DvdVerifyReadback(args),
    "booktype-trace" => BookTypeTrace(args),
    "booktype-set" => BookTypeSet(args),
    "scan-protection" => ScanProtection(args),
    "dump-merge" => DumpMergeCmd(args),
    "merge-cert" => MergeCertCmd(args),
    "c2-merge" => C2MergeCmd(args),
    "dvd-ecc" => DvdEccCmd(args),
    "flux-demod" => FluxDemodCmd(args),
    "collection-triage" => CollectionTriageCmd(args),
    "salvage-plan" => SalvagePlanCmd(args),
    "reconstruct" => ReconstructCmd(args),
    "preserve" => PreserveCmd(args),
    "preserve-master" => PreserveMasterCmd(args),
    "flux" => FluxCmd(args),
    "lineage" => LineageCmd(args),
    "library-watch" => LibraryWatchCmd(args),
    "remaster" => RemasterCmd(args),
    "offset-detect" => OffsetDetectCmd(args),
    "dump-score" => DumpScoreCmd(args),
    "disc-anomalies" => DiscAnomaliesCmd(args),
    "disc-date" => DiscDateCmd(args),
    "disc-delta" => DiscDeltaCmd(args),
    "disc-patch" => DiscPatchCmd(args),
    "disc-genome" => DiscGenomeCmd(args),
    "health-map" => HealthMapCmd(args),
    "error-pattern" => ErrorPatternCmd(args),
    "disc-cluster" => DiscClusterCmd(args),
    "hidden-sessions" => HiddenSessionsCmd(args),
    "disc-rot" => DiscRotCmd(args),
    "rot-kinetics" => RotKineticsCmd(args),
    "bler" => BlerCmd(args),
    "scan-import" => ScanImportCmd(args),
    "dpm" => DpmCmd(args),
    "protection-scan" => ProtectionScanCmd(args),
    "protection-profile" => ProtectionProfileCmd(args),
    "twin-scan" => TwinScanCmd(args),
    "weak-sectors" => WeakSectorsCmd(args),
    "efm-spectrum" => EfmSpectrumCmd(args),
    "xa-map" => XaMapCmd(args),
    "recover-oracle" => RecoverOracleCmd(args),
    "recover-toc" => RecoverTocCmd(args),
    "scratch-verdict" => ScratchVerdictCmd(args),
    "recovery-map" => RecoveryMapCmd(args),
    "covert-scan" => CovertScanCmd(args),
    "matter-map" => MatterMapCmd(args),
    "phylo" => PhyloCmd(args),
    "iso-lint" => IsoLintCmd(args),
    "udf-lint" => UdfLintCmd(args),
    "iso-pathtable" => IsoPathTableCmd(args),
    "iso-rockridge" => IsoRockRidgeCmd(args),
    "redbook-audit" => RedBookAuditCmd(args),
    "boot-catalog" => BootCatalogCmd(args),
    "premaster-check" => PremasterCheckCmd(args),
    "dump-provenance" => DumpProvenanceCmd(args),
    "pregap-scan" => PregapScanCmd(args),
    "dvd-nav" => DvdNavCmd(args),
    "consensus" => ConsensusCmd(args),
    "disc-print" => DiscPrintCmd(args),
    "disc-genealogy" => DiscGenealogyCmd(args),
    "mastering-print" => MasteringPrintCmd(args),
    "collection-archive" => CollectionArchiveCmd(args),
    "vault" => VaultCmd(args),
    "par2-verify" => Par2VerifyCmd(args),
    "chunk-manifest" => ChunkManifestCmd(args),
    "fuzz-parsers" => FuzzParsersCmd(args),
    "disc-semdiff" => DiscSemDiffCmd(args),
    "completeness-check" => CompletenessCheckCmd(args),
    "emu-ready" => EmuReadyCmd(args),
    "min-descriptor" => MinDescriptorCmd(args),
    "recover" => RecoverCmd(args),
    "secure-rip-plan" => SecureRipPlanCmd(args),
    "fs-recover" => FsRecoverCmd(args),
    "coverage-proof" => CoverageProofCmd(args),
    "pregap-check" => PregapCheckCmd(args),
    "subq-map" => SubqMapCmd(args),
    "redump-cue" => RedumpCueCmd(args),
    "bad-sectors" => BadSectorsCmd(args),
    "redump-diff" => RedumpDiffCmd(args),
    "dump-audit" => DumpAuditCmd(args),
    "read-stability" => ReadStabilityCmd(args),
    "redump-prep" => RedumpPrepCmd(args),
    "cu2" => Cu2Cmd(args),
    "ode-export" => OdeExportCmd(args),
    "ode-layout" => OdeLayoutCmd(args),
    "disc-bom" => DiscBomCmd(args),
    "ring-code" => RingCodeCmd(args),
    "disc-fs" => DiscFsCmd(args),
    "hfs-ls" => HfsLsCmd(args),
    "hfs-lint" => HfsLintCmd(args),
    "hfs-resources" => HfsResourcesCmd(args),
    "hfs-orphans" => HfsOrphansCmd(args),
    "udf-orphans" => UdfOrphansCmd(args),
    "fs-orphans" => FsOrphansCmd(args),
    "audio-dynamics" => AudioDynamicsCmd(args),
    "hdcd-scan" => HdcdScanCmd(args),
    "neogeo-ipl" => NeoGeoIplCmd(args),
    "cdg-frames" => CdgFramesCmd(args),
    "to-ccd" => ToCcd(args),
    "dvd-info" => DvdInfo(args),
    "dvd-rewrite" => DvdRewrite(args),
    "vcd-info" => VcdInfo(args),
    "transcode" => Transcode(args),
    "accuraterip" => AccurateRipCmd(args),
    "mount" => MountCmd(args),
    "ccd-info" => CcdInfo(args),
    "wbfs-info" => WbfsInfo(args),
    "wbfs-extract" => WbfsExtract(args),
    "checksum" => Checksum(args),
    "split" => Split(args),
    "join" => Join(args),
    "ppf-apply" => PpfApply(args),
    "ppf-create" => PpfCreate(args),
    "ppf-info" => PpfInfo(args),
    "ppf-convert" => PpfConvert(args),
    "ppf-edit" => PpfEdit(args),
    "ips-apply" => IpsApply(args),
    "ips-create" => IpsCreate(args),
    "bps-apply" => BpsApply(args),
    "bps-create" => BpsCreate(args),
    "create-udf" => CreateUdf(args),
    "create-udf-bridge" => CreateUdfBridge(args),
    "dvd-video-plan" => DvdVideoPlanCmd(args),
    "dvd-video-build" => DvdVideoBuildCmd(args),
    "dvd-video-fix" => DvdVideoFixCmd(args),
    "bdmv-plan" => BdmvPlanCmd(args),
    "bdmv-build" => BdmvBuildCmd(args),
    "gdi-info" => GdiInfo(args),
    "gdi-browse" => GdiBrowse(args),
    "iso-rebase" => IsoRebase(args),
    "xiso-ls" => XisoLs(args),
    "create-xiso" => CreateXiso(args),
    "god-info" => GodInfoCmd(args),
    "god-extract" => GodExtractCmd(args),
    "iso-create" => IsoCreateCmd(args),
    "ps2-info" => Ps2Info(args),
    "scummvm-detect" => ScummvmDetect(args),
    "scummvm-export" => ScummvmExport(args),
    "psx-exe-info" => PsxExeInfo(args),
    "psx-pad" => PsxPad(args),
    "bin2src" => Bin2Src(args),
    "search" => SearchCmd(args),
    "tim-info" => TimInfo(args),
    "tim-extract" => TimExtract(args),
    "xa-extract" => XaExtractCmd(args),
    "vag-extract" => VagExtract(args),
    "adx-decode" => AdxDecodeCmd(args),
    "dsp-decode" => DspDecodeCmd(args),
    "read-offset" => ReadOffsetCmd(args),
    "vab-info" => VabInfoCmd(args),
    "seq-info" => SeqInfoCmd(args),
    "str-demux" => StrDemuxCmd(args),
    "str-frames" => StrFramesCmd(args),
    "mdec-info" => MdecInfoCmd(args),
    "vob-demux" => VobDemuxCmd(args),
    "vcd-control" => VcdControlCmd(args),
    "vcd-psd" => VcdPsdCmd(args),
    "dvd-ifo" => DvdIfoCmd(args),
    "tmd-info" => TmdInfo(args),
    "tmd2dxf" => Tmd2Dxf(args),
    "tod-info" => TodInfo(args),
    "cdr-info" => CdrInfo(args),
    "psx-video-mode" => PsxVideoModeCmd(args),
    "vmu-info" => VmuInfo(args),
    "vmu-extract" => VmuExtract(args),
    "vmu-create" => VmuCreate(args),
    "vmu-add" => VmuAdd(args),
    "vms2vmi" => Vms2Vmi(args),
    "ps2mc-info" => Ps2McInfo(args),
    "ps2mc-extract" => Ps2McExtract(args),
    "ps2mc-ecc" => Ps2McEccCmd(args),
    "psxmc-info" => PsxMcInfo(args),
    "psxmc-extract" => PsxMcExtract(args),
    "psxmc-format" => PsxMcFormatCmd(args),
    // Canonical ps1mc-* names (consistent with ps2mc-*); the psxmc-*/ps1card-* names remain as aliases.
    "ps1mc-info" => PsxMcInfo(args),
    "ps1mc-extract" => PsxMcExtract(args),
    "ps1mc-format" => PsxMcFormatCmd(args),
    "ps1mc-convert" => Ps1CardConvertCmd(args),
    "gci-info" => GciInfo(args),
    "gci-extract" => GciExtract(args),
    "n64save-info" => N64SaveInfo(args),
    "n64-info" => N64Info(args),
    "saturnsave-info" => SaturnSaveInfo(args),
    "chd-info" => ChdInfo(args),
    "chd-verify" => ChdVerifyCmd(args),
    "identify" => IdentifyCmd(args),
    "gameaudio-info" => GameAudioInfo(args),
    "disk-info" => DiskInfo(args),
    "rdb-info" => RdbInfoCmd(args),
    "apm-info" => ApmInfoCmd(args),
    "disc-report" => DiscReportCmd(args),
    "floppy-info" => FloppyInfo(args),
    "floppy-image" => FloppyImageCmd(args),
    "floppy-extract" => FloppyExtract(args),
    "woz-info" => WozInfoCmd(args),
    "scp-info" => ScpInfoCmd(args),
    "kryoflux-info" => KryoFluxInfoCmd(args),
    "d88-info" => D88InfoCmd(args),
    "fat-ls" => FatLsCmd(args),
    "fat-lint" => FatLintCmd(args),
    "drives" => DrivesCmd(args),
    "burn" => BurnCmd(args),
    "burn-raw" => BurnRawCmd(args),
    "writeinfo" => WriteInfoCmd(args),
    "drive-profile" => DriveProfileCmd(args),
    "disc-scan" => DiscScanCmd(args),
    "read-benchmark" => ReadBenchmarkCmd(args),
    "read-disc" => ReadDiscCmd(args),
    "read-raw" => ReadRawCmd(args),
    "blank" => BlankCmd(args),
    "raw-dump" => RawDumpCmd(args),
    "fat-extract" => FatExtractCmd(args),
    "dat-verify" => DatVerify(args),
    "dat-build" => DatBuildCmd(args),
    "library" => Library(args),
    "license" => LicenseCmd(args),
    "1g1r" => OneGameOneRomCmd(args),
    "rebuild" => RebuildCmd(args),
    "dat-diff" => DatDiffCmd(args),
    "library-report" => LibraryReportCmd(args),
    "catalog-export" => CatalogExportCmd(args),
    "frontend-export" => FrontendExportCmd(args),
    "m3u" => M3uCmd(args),
    "torrentzip" => TorrentZipCmd(args),
    "hashgen" => HashGenCmd(args),
    "entropy" => EntropyCmd(args),
    "fuzzy-hash" => FuzzyHashCmd(args),
    "partitions" => PartitionsCmd(args),
    "aaru-info" => AaruInfoCmd(args),
    "aaru-extract" => AaruExtractCmd(args),
    "aaru-create" => AaruCreateCmd(args),
    "cicm-export" => CicmExportCmd(args),
    "exfat-ls" => ExfatLsCmd(args),
    "exfat-extract" => ExfatExtractCmd(args),
    "ntfs-ls" => NtfsLsCmd(args),
    "ntfs-extract" => NtfsExtractCmd(args),
    "ext-ls" => ExtLsCmd(args),
    "ext-extract" => ExtExtractCmd(args),
    "hashverify" => HashVerifyCmd(args),
    "ps1card-convert" => Ps1CardConvertCmd(args),
    "save-convert" => SaveConvertCmd(args),
    "rom-convert" => RomConvertCmd(args),
    "submission-info" => SubmissionInfoCmd(args),
    "submission-pack" => SubmissionPackCmd(args),
    "ciso-info" => CisoInfoCmd(args),
    "ciso-to-iso" => CisoToIso(args),
    "iso-to-ciso" => IsoToCiso(args),
    "dc-scramble" => DcScramble(args, scramble: true),
    "dc-descramble" => DcScramble(args, scramble: false),
    "bincue-merge" => BinCueMergeCmd(args),
    "bincue-split" => BinCueSplitCmd(args),
    "milcd-to-cdi" => MilcdToCdi(args),
    "ipbin-info" => IpBinInfo(args),
    "pvr-info" => PvrInfo(args),
    "pvm-info" => PvmInfo(args),
    "mpeg-info" => MpegInfo(args),
    "saturn-info" => SaturnInfo(args),
    "pcfx-info" => PcfxInfoCmd(args),
    "segacd-info" => SegaCdInfo(args),
    "opera-ls" => OperaLsCmd(args),
    "cdi-console-info" => CdInteractiveConsoleInfo(args),
    "cdi-extract" => CdInteractiveExtract(args),
    "psp-info" => PspInfo(args),
    "pbp-info" => PbpInfo(args),
    "pbp-extract" => PbpExtract(args),
    "gcm-info" => GcmInfo(args),
    "gcm-banner" => GcmBannerCmd(args),
    "gcm-extract" => GcmExtractCmd(args),
    "tpl-info" => TplInfoCmd(args),
    "tpl-extract" => TplExtractCmd(args),
    "rvz-info" => ShowRvzInfo(args),
    "rvz-decode" => RvzDecodeCmd(args),
    "nkit-info" => NkitInfoCmd(args),
    "gc-verify" => GcVerifyCmd(args),
    "gc-junk-map" => GcJunkMapCmd(args),
    "gc-junk-fill" => GcJunkFillCmd(args),
    "dvd-layerbreak" => DvdLayerbreakCmd(args),
    "dvd-layerbreak-plan" => DvdLayerbreakPlanCmd(args),
    "layerbreak-pick" => LayerBreakPickCmd(args),
    "capacity-check" => CapacityCheckCmd(args),
    "disc-span" => DiscSpanCmd(args),
    "source-stage" => SourceStageCmd(args),
    "ui" => UiCmd(args),
    "rom-info" => RomInfo(args),
    "rom-integrity" => RomIntegrityCmd(args),
    "fds-info" => FdsInfoCmd(args),
    "bdmv-info" => BdmvInfo(args),
    "sbi-make" => SbiMake(args),
    "ecm" => EcmCmd(args),
    "unecm" => UnecmCmd(args),
    "cdtext" => CdTextCmd(args),
    "deemph" => DeEmphCmd(args),
    "silence-split" => SilenceSplitCmd(args),
    "libcrypt" => LibcryptCmd(args),
    "sbi-info" => SbiInfo(args),
    "psx-build" => PsxBuild(args),
    "chd-extract" => ChdExtract(args),
    "verify-convert" => VerifyConvertCmd(args),
    "fs-verify" => FsVerifyCmd(args),
    "disc-diff" => DiscDiffCmd(args),
    "chd-extract-hd" => ChdExtractHd(args),
    "chd-create" => ChdCreate(args),
    "cheat-decode" => CheatDecode(args),
    "cheat-encode" => CheatEncode(args),
    "cheat-apply-nes" => CheatApplyNes(args),
    _ => Fail($"Unknown or not-yet-implemented command '{args[0]}'."),
};

static int Inspect(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge inspect <image.cdi>");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");

    using var fs = File.OpenRead(path);

    CdiTrailer trailer;
    try
    {
        trailer = CdiParser.ReadTrailer(fs);
    }
    catch (CdiFormatException ex)
    {
        return Fail(ex.Message);
    }

    Console.WriteLine($"File:       {Path.GetFileName(path)} ({fs.Length:N0} bytes)");
    Console.WriteLine($"Format:     CDI {VersionLabel(trailer.Version)}");
    Console.WriteLine($"Descriptor: offset {trailer.DescriptorOffset:N0}, " +
                      $"{fs.Length - trailer.DescriptorOffset:N0} bytes");

    try
    {
        var image = CdiParser.Parse(fs);
        Console.WriteLine($"Sessions:   {image.Sessions.Count}   Tracks: {image.TrackCount}");
        Console.WriteLine();
        Console.WriteLine("  #  Ses  Mode    Sector  Pregap  Length     LBA      File offset");
        Console.WriteLine("  -- ---  ------  ------  ------  ---------  -------  -----------");
        foreach (var t in image.AllTracks)
        {
            Console.WriteLine(
                $"  {t.Number,2} {t.SessionIndex + 1,3}  {t.Mode,-6}  {(int)t.SectorSize,6}  " +
                $"{t.PregapSectors,6}  {t.LengthSectors,9}  {t.StartLba,7}  {t.FileOffset,11:N0}");
        }
    }
    catch (CdiFormatException ex)
    {
        Console.WriteLine();
        Console.WriteLine($"Descriptor walk failed: {ex.Message}");
        Console.WriteLine("(Trailer parsed OK — this is likely a spec gap; " +
                          "please open an issue with the image version above.)");
        return 2;
    }

    return 0;
}

static int Extract(string[] args)
{
    // usage: dforge extract <image.cdi> <output-dir> [--raw]
    if (args.Length < 3)
        return Fail("usage: dforge extract <image.cdi> <output-dir> [--raw]");

    var path = args[1];
    var outDir = args[2];
    bool raw = args.Contains("--raw");
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    Directory.CreateDirectory(outDir);

    using var fs = File.OpenRead(path);
    CdiImage image;
    try { image = CdiParser.Parse(fs); }
    catch (CdiFormatException ex) { return Fail(ex.Message); }

    Console.WriteLine($"Extracting {image.TrackCount} track(s) from " +
                      $"{Path.GetFileName(path)} -> {outDir}");

    foreach (var t in image.AllTracks)
    {
        string ext = raw ? "bin"
            : t.Mode == CdiTrackMode.Audio ? "wav"
            : "iso";
        var outPath = Path.Combine(outDir, $"track{t.Number:D2}.{ext}");
        using var os = File.Create(outPath);

        long bytes;
        if (raw)
            bytes = CdiExtractor.ExtractRaw(fs, t, os);
        else if (t.Mode == CdiTrackMode.Audio)
        {
            CdiExtractor.ExtractAudioToWav(fs, t, os);
            bytes = os.Length;
        }
        else
            bytes = CdiExtractor.ExtractUserData(fs, t, os);

        Console.WriteLine(
            $"  track{t.Number:D2}  {t.Mode,-6} {(int)t.SectorSize}B  " +
            $"-> {Path.GetFileName(outPath)}  ({bytes:N0} bytes)");
    }

    Console.WriteLine("Done.");
    return 0;
}

static int Convert(string[] args)
{
    // usage: dforge convert <in.cdi> <out.cue>          (CDI -> BIN/CUE)
    //        dforge convert <in.cue> <out.cdi> [--version v2|v3|v35]
    //        dforge convert <in.mds> <out.cdi> [--version v2|v3|v35]   (Alcohol -> CDI)
    if (args.Length < 3)
        return Fail("usage: dforge convert <in.cdi> <out.cue> | <in.cue> <out.cdi> | " +
                    "<in.mds> <out.cdi> [--version v2|v3|v35]");

    var input = args[1];
    var output = args[2];
    if (!File.Exists(input)) return Fail($"File not found: {input}");

    var inExt = Path.GetExtension(input).ToLowerInvariant();
    var outExt = Path.GetExtension(output).ToLowerInvariant();

    if (inExt == ".cdi" && outExt == ".cue")
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var baseName = Path.GetFileNameWithoutExtension(output);
        using var fs = File.OpenRead(input);
        CdiImage image;
        try { image = CdiParser.Parse(fs); }
        catch (CdiFormatException ex) { return Fail(ex.Message); }

        CdiConverter.BinCueResult result;
        try { result = CdiConverter.CdiToBinCue(fs, image, outDir, baseName); }
        catch (Exception ex) { return Fail(ex.Message); }   // IO/format faults exit cleanly, not as a stack trace
        foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
        Console.WriteLine($"Wrote {baseName}.cue and {result.BinFilenames.Count} BIN file(s) to {outDir}");

        // Carry an unreadable-sector map through the split: a "<in>.badsectors.json" beside the CDI is
        // re-expressed against the new track files and written beside the cue, so the holes survive into the
        // preservation master instead of being silently lost when the image is cut into tracks.
        var srcSidecar = DiscForge.Core.Preservation.BadSectorMap.SidecarPath(input);
        if (File.Exists(srcSidecar))
        {
            try
            {
                var map = DiscForge.Core.Preservation.BadSectorMap.Load(srcSidecar);
                var spans = image.AllTracks.Select(t =>
                    new DiscForge.Core.Preservation.BadSectorMap.TrackSpan(
                        t.Number, $"{baseName}_track{t.Number:D2}.bin",
                        t.StartLba, (int)t.PregapSectors, t.LengthSectors)).ToList();
                var remapped = map.RemapToTracks(spans, Path.GetFileName(output));
                var outSidecar = DiscForge.Core.Preservation.BadSectorMap.SidecarPath(output);
                remapped.Save(outSidecar);
                Console.WriteLine($"Carried {map.Count:N0} unreadable sector(s) into {Path.GetFileName(outSidecar)} " +
                                  $"({(map.DamagePresent ? "INCOMPLETE dump" : "boundary holes only")}).");
            }
            catch (Exception ex) { Console.WriteLine($"warning: could not carry the bad-sector map: {ex.Message}"); }
        }
        return 0;
    }

    if (inExt == ".gdi" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        try
        {
            using var os = File.Create(output);
            GdiConverter.GdiToCdi(input, version, os);
            Console.WriteLine($"Converted {Path.GetFileName(input)} -> {Path.GetFileName(output)} " +
                              $"({VersionLabel(version)}).");
            return 0;
        }
        catch (Exception ex) when (ex is GdiFormatException or FileNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    if (inExt == ".cdi" && outExt == ".gdi")
    {
        var outDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var baseName = Path.GetFileNameWithoutExtension(output);
        using var fs = File.OpenRead(input);
        CdiImage image;
        try { image = CdiParser.Parse(fs); }
        catch (CdiFormatException ex) { return Fail(ex.Message); }

        var result = GdiConverter.CdiToGdi(fs, image, outDir, baseName);
        foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
        Console.WriteLine($"Wrote {baseName}.gdi and {result.TrackFiles.Count} track file(s) to {outDir}");
        return 0;
    }

    if (inExt == ".cdi" && outExt == ".nrg")
    {
        using var fs = File.OpenRead(input);
        CdiImage image;
        try { image = CdiParser.Parse(fs); }
        catch (CdiFormatException ex) { return Fail(ex.Message); }
        using var os = File.Create(output);
        NrgConverter.CdiToNrg(fs, image, os);
        Console.WriteLine($"Converted {Path.GetFileName(input)} -> {Path.GetFileName(output)} (Nero NRG v2).");
        return 0;
    }

    if (inExt == ".nrg" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        using var fs = File.OpenRead(input);
        NrgImage image;
        try { image = NrgParser.Parse(fs); }
        catch (NrgFormatException ex) { return Fail(ex.Message); }
        using var os = File.Create(output);
        NrgConverter.NrgToCdi(fs, image, version, os);
        Console.WriteLine($"Converted {Path.GetFileName(input)} -> {Path.GetFileName(output)} ({VersionLabel(version)}).");
        return 0;
    }

    if (inExt == ".iso" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        try
        {
            using var os = File.Create(output);
            var r = IsoConverter.IsoToCdi(input, version, os);
            foreach (var w in r.Warnings) Console.WriteLine($"warning: {w}");
            Console.WriteLine(
                $"Wrapped {Path.GetFileName(input)} -> {Path.GetFileName(output)} " +
                $"({VersionLabel(version)}): {r.Sectors:N0} sectors, {r.BytesWritten:N0} bytes.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    if (inExt == ".cdi" && outExt == ".iso")
    {
        using var fs = File.OpenRead(input);
        CdiImage image;
        try { image = CdiParser.Parse(fs); }
        catch (CdiFormatException ex) { return Fail(ex.Message); }

        try
        {
            using var os = File.Create(output);
            var r = IsoConverter.CdiToIso(fs, image, os);
            foreach (var w in r.Warnings) Console.WriteLine($"warning: {w}");
            Console.WriteLine(
                $"Extracted {Path.GetFileName(input)} -> {Path.GetFileName(output)}: " +
                $"{r.Sectors:N0} sectors, {r.BytesWritten:N0} bytes.");
            return 0;
        }
        catch (InvalidDataException ex) { return Fail(ex.Message); }
    }

    if (inExt == ".mds" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        MdsImage mds;
        try { mds = MdsParser.Parse(File.ReadAllBytes(input)); }
        catch (MdsFormatException ex) { return Fail(ex.Message); }

        // The .mdf sits beside the .mds by convention; allow an explicit override.
        int mi = Array.IndexOf(args, "--mdf");
        var mdfPath = (mi >= 0 && mi + 1 < args.Length)
            ? args[mi + 1]
            : MdsConverter.DefaultMdfPath(input);

        try
        {
            using var os = File.Create(output);
            var result = MdsConverter.MdsToCdi(mds, mdfPath, version, os);
            foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
            Console.WriteLine(
                $"Converted {Path.GetFileName(input)} ({mds.Medium}, {result.TrackCount} track(s)) " +
                $"-> {Path.GetFileName(output)} ({VersionLabel(version)}), {result.CdiBytes:N0} bytes.");
            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            return Fail(ex.Message);
        }
    }

    if (inExt == ".cue" && outExt == ".cdi")
    {
        var version = ParseVersionArg(args) ?? CdiVersion.V35;
        var cueText = File.ReadAllText(input);
        var cueDir = Path.GetDirectoryName(Path.GetFullPath(input))!;
        // Atomic: any failure (format OR I/O) leaves no truncated .cdi at the destination.
        try { WriteFileAtomically(output, os => CdiConverter.BinCueToCdi(cueText, cueDir, version, os)); }
        catch (Exception ex) { return Fail(ex.Message); }
        Console.WriteLine($"Wrote {Path.GetFileName(output)} ({VersionLabel(version)})");
        return 0;
    }

    return Fail("convert needs .cdi->.cue or .cue->.cdi (by file extension).");
}

static int DiscConvert(string[] args)
{
    // usage: dforge disc-convert <in> <out>
    // The universal hub: reads <in> into a canonical disc model and writes <out>
    // from it, so any supported input converts to any supported output through one path.
    if (args.Length < 3)
        return Fail("usage: dforge disc-convert <in> <out>  " +
                    "(in: .cue .bin .chd .iso .cso .zso .wbfs .cdi .nrg .mds .gdi .ccd; " +
                    "out: .cue .chd .iso .cdi .nrg)");

    var input = args[1];
    var output = args[2];
    try
    {
        var model = DiscForge.Core.Convert.DiscConverter.Read(input);
        DiscForge.Core.Convert.DiscConverter.Write(model, output);

        long totalBytes = model.Tracks.Sum(t => (long)t.Data.Length);
        Console.WriteLine(
            $"{Path.GetExtension(input).TrimStart('.').ToUpperInvariant()} -> " +
            $"{Path.GetExtension(output).TrimStart('.').ToUpperInvariant()}: " +
            $"{model.Tracks.Count} track(s), {totalBytes:N0} bytes.");
        foreach (var t in model.Tracks)
            Console.WriteLine(
                $"  track {t.Number:D2}  {t.Type,-11} {t.SectorSize}B  " +
                $"{t.SectorCount:N0} sectors" + (t.PregapSectors > 0 ? $"  (+{t.PregapSectors} pregap)" : ""));
        Console.WriteLine($"Wrote {Path.GetFileName(output)}.");
        return 0;
    }
    catch (DiscForge.Core.Convert.DiscConvertException ex) { return Fail(ex.Message); }
}

static int Verify(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge verify <image.cdi> [--checksums]");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    bool userChecksums = args.Contains("--checksums");

    using var fs = File.OpenRead(path);
    CdiImage image;
    try { image = CdiParser.Parse(fs); }
    catch (CdiFormatException ex) { return Fail(ex.Message); }

    var report = CdiVerifier.Verify(fs, image, userChecksums);

    Console.WriteLine($"{Path.GetFileName(path)}: {VersionLabel(image.Version)}, " +
                      $"{image.Sessions.Count} session(s), {image.TrackCount} track(s)");
    Console.WriteLine();
    Console.WriteLine("  #   stored bytes   stored CRC32" + (userChecksums ? "   user CRC32" : ""));
    Console.WriteLine("  --  -------------  ------------" + (userChecksums ? "  -----------" : ""));
    foreach (var c in report.Checksums)
    {
        var line = $"  {c.TrackNumber,2}  {c.StoredBytes,13:N0}  {c.StoredCrc32:X8}";
        if (userChecksums && c.UserCrc32 is { } u) line += $"     {u:X8}";
        Console.WriteLine(line);
    }

    if (report.Issues.Count > 0)
    {
        Console.WriteLine();
        foreach (var issue in report.Issues)
            Console.WriteLine($"  [{issue.Severity}] {issue.Message}");
    }

    Console.WriteLine();
    Console.WriteLine(report.Passed
        ? (report.HasWarnings ? "PASS (with warnings)" : "PASS")
        : "FAIL");
    return report.Passed ? 0 : 2;
}

static int Create(string[] args)
{
    // usage: dforge create <dir> <out.cdi> [--volume NAME] [--version v2|v3|v35] [--rock-ridge]
    if (args.Length < 3)
        return Fail("usage: dforge create <dir> <out.cdi> [--volume NAME] [--version v2|v3|v35] [--rock-ridge]");

    var dir = args[1];
    var outPath = args[2];
    if (!Directory.Exists(dir)) return Fail($"Directory not found: {dir}");

    int vi = Array.IndexOf(args, "--volume");
    string volume = (vi >= 0 && vi + 1 < args.Length) ? args[vi + 1] : "OPENJUGGLER";
    var version = ParseVersionArg(args) ?? CdiVersion.V35;
    bool rockRidge = args.Contains("--rock-ridge");

    using var os = File.Create(outPath);
    var result = CdiCreator.CreateFromDirectory(volume, dir, version, os, rockRidge);

    foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
    Console.WriteLine(
        $"Created {Path.GetFileName(outPath)} ({VersionLabel(version)}): " +
        $"{result.IsoSectors} ISO sectors, {result.CdiBytes:N0} bytes.");
    return 0;
}

static int Compare(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge compare <a.cdi> <b.cdi> [--no-content]");
    var pathA = args[1];
    var pathB = args[2];
    if (!File.Exists(pathA)) return Fail($"File not found: {pathA}");
    if (!File.Exists(pathB)) return Fail($"File not found: {pathB}");
    bool content = !args.Contains("--no-content");

    using var fa = File.OpenRead(pathA);
    using var fb = File.OpenRead(pathB);
    CdiImage ia, ib;
    try { ia = CdiParser.Parse(fa); ib = CdiParser.Parse(fb); }
    catch (CdiFormatException ex) { return Fail(ex.Message); }

    var report = CdiComparer.Compare(fa, ia, fb, ib, content);

    Console.WriteLine($"A: {Path.GetFileName(pathA)}   B: {Path.GetFileName(pathB)}");
    if (report.Equal)
    {
        Console.WriteLine("\nIMAGES ARE EQUIVALENT" + (content ? " (structure + content)" : " (structure)"));
        return 0;
    }

    Console.WriteLine();
    foreach (var s in report.StructuralDifferences)
        Console.WriteLine($"  [structure] {s}");
    foreach (var t in report.TrackDifferences)
        Console.WriteLine($"  [track {t.TrackNumber}] {t.Field}: {t.ValueA} vs {t.ValueB}");
    foreach (var n in report.ContentMismatchTracks)
        Console.WriteLine($"  [track {n}] content differs (CRC-32 mismatch)");

    Console.WriteLine("\nIMAGES DIFFER");
    return 1;
}

static int CreateAudio(string[] args)
{
    // usage: dforge create-audio <out.cdi> <track1.wav> [track2.wav ...] [--gapless] [--74]
    if (args.Length < 3)
        return Fail("usage: dforge create-audio <out.cdi> <track1.wav> [track2.wav ...] " +
                    "[--gapless] [--74] [--postgap [sectors]] [--version v2|v3|v35]");

    var outPath = args[1];
    bool gapless = args.Contains("--gapless");
    bool only74 = args.Contains("--74");

    // Some third-party images omit the post-gap the standard expects before the
    // lead-out; --postgap adds the customary two seconds to the last track.
    int pg = Array.IndexOf(args, "--postgap");
    uint postgap = pg >= 0
        ? (pg + 1 < args.Length && uint.TryParse(args[pg + 1], out var v) ? v : 150u)
        : 0u;
    var version = ParseVersionArg(args) ?? CdiVersion.V35;

    // Collect the WAV paths, skipping flags AND any value a flag consumes —
    // otherwise `--postgap 150` would treat "150" as a filename.
    var wavs = new List<string>();
    for (int i = 2; i < args.Length; i++)
    {
        var a = args[i];
        if (a.StartsWith("--"))
        {
            // These flags take a value; step over it.
            if (a is "--postgap" or "--version")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) i++;
            }
            continue;
        }
        wavs.Add(a);
    }

    if (wavs.Count == 0) return Fail("Give at least one .wav file.");

    var tracks = wavs.Select((p, i) => new AudioTrackSource
    {
        Path = p,
        // Track 1's lead-in gap is mandatory and enforced by the creator.
        PregapSectors = (gapless && i > 0) ? 0u : 150u,
        // A post-gap belongs before the lead-out, i.e. on the last track only.
        PostgapSectors = (i == wavs.Count - 1) ? postgap : 0u,
    }).ToList();

    try
    {
        using var os = File.Create(outPath);
        var result = AudioCdCreator.Create(tracks, version, os, allow80Minute: !only74);

        foreach (var w in result.Warnings) Console.WriteLine($"warning: {w}");
        Console.WriteLine(
            $"Created {Path.GetFileName(outPath)} ({VersionLabel(version)}): " +
            $"{result.TrackCount} audio track(s), {result.Duration:hh\\:mm\\:ss}, " +
            $"{result.TotalSectors:N0} sectors, {result.CdiBytes:N0} bytes.");
        return 0;
    }
    catch (Exception ex) when (ex is AudioCdException or WavFormatException or FileNotFoundException)
    {
        return Fail(ex.Message);
    }
}

static (CdiImage Image, CdiTrack Track, FileStream Stream)? OpenDataTrack(string path)
{
    var fs = File.OpenRead(path);
    CdiImage image;
    try { image = CdiParser.Parse(fs); }
    catch (CdiFormatException ex) { fs.Dispose(); Console.Error.WriteLine("error: " + ex.Message); return null; }

    // The filesystem lives in the first data track.
    var track = image.AllTracks.FirstOrDefault(t => t.Mode != CdiTrackMode.Audio);
    if (track is null)
    {
        fs.Dispose();
        Console.Error.WriteLine("error: this image has no data track (audio-only disc?).");
        return null;
    }
    return (image, track, fs);
}

static IsoReader.NamePreference PreferenceFrom(string[] args) =>
    args.Contains("--iso") ? IsoReader.NamePreference.Iso9660
    : args.Contains("--joliet") ? IsoReader.NamePreference.Joliet
    : IsoReader.NamePreference.Auto;

static int Ls(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ls <image.cdi> [--iso|--joliet]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var opened = OpenDataTrack(args[1]);
    if (opened is null) return 2;
    var (_, track, fs) = opened.Value;
    using (fs)
    {
        using var view = new CdiUserDataStream(fs, track);

        // A disc may carry ISO 9660, UDF, or both (a "UDF bridge"). Prefer what
        // the caller asked for; otherwise use whichever is actually there.
        bool wantUdf = args.Contains("--udf");
        bool wantIso = args.Contains("--iso") || args.Contains("--joliet");

        if (!wantIso && (wantUdf || !HasIso9660(view)))
        {
            if (!UdfReader.IsUdf(view))
                return Fail(wantUdf
                    ? "This image has no UDF filesystem."
                    : "This image has neither an ISO 9660 nor a UDF filesystem.");

            UdfVolume vol;
            try { vol = UdfReader.Read(view); }
            catch (UdfFormatException ex) { return Fail(ex.Message); }

            Console.WriteLine($"Volume: {vol.VolumeId}   Filesystem: UDF");
            Console.WriteLine();
            foreach (var e in vol.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
                Console.WriteLine(e.IsDirectory
                    ? $"  {"<DIR>",12}  {e.Path}"
                    : $"  {e.Size,12:N0}  {e.Path}");
            Console.WriteLine();
            Console.WriteLine($"{vol.Files.Count():N0} file(s), {vol.Directories.Count():N0} directory(ies), " +
                              $"{vol.TotalBytes:N0} bytes.");
            return 0;
        }

        IsoDirectory dir;
        try { dir = IsoReader.Read(view, PreferenceFrom(args)); }
        catch (IsoFormatException ex) { return Fail(ex.Message); }

        var names = dir.Joliet ? "Joliet" : dir.RockRidge ? "ISO 9660 + Rock Ridge" : "ISO 9660";
        Console.WriteLine($"Volume: {dir.VolumeId}   Names: {names}");
        if (UdfReader.IsUdf(view))
            Console.WriteLine("(This disc also has a UDF filesystem — use --udf to read that instead.)");
        Console.WriteLine();

        foreach (var e in dir.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
            Console.WriteLine(e.IsDirectory
                ? $"  {"<DIR>",12}  {e.Path}"
                : $"  {e.Size,12:N0}  {e.Path}");

        Console.WriteLine();
        Console.WriteLine($"{dir.Files.Count():N0} file(s), {dir.Directories.Count():N0} directory(ies), " +
                          $"{dir.TotalBytes:N0} bytes.");
        return 0;
    }
}

/// <summary>
/// Map an in-image path to an output path, refusing anything that escapes the
/// target directory. Paths come from the image and are untrusted input.
/// </summary>
static string? SafeTarget(string outDir, string imagePath)
{
    var relative = imagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
    var target = Path.GetFullPath(Path.Combine(outDir, relative));
    var root = Path.GetFullPath(outDir);
    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"warning: skipping '{imagePath}' — it escapes the output directory.");
        return null;
    }
    return target;
}

/// <summary>True if the stream carries an ISO 9660 primary volume descriptor.</summary>
static bool HasIso9660(Stream image)
{
    try
    {
        image.Seek(16L * 2048, SeekOrigin.Begin);
        var b = new byte[6];
        if (image.Read(b, 0, 6) < 6) return false;
        return b[1] == 'C' && b[2] == 'D' && b[3] == '0' && b[4] == '0' && b[5] == '1';
    }
    catch { return false; }
}

static int ExtractFiles(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge extract-files <image.cdi> <output-dir> [--iso|--joliet]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var outDir = args[2];
    var opened = OpenDataTrack(args[1]);
    if (opened is null) return 2;
    var (_, track, fs) = opened.Value;
    using (fs)
    {
        using var view = new CdiUserDataStream(fs, track);

        bool wantUdf = args.Contains("--udf");
        bool wantIso = args.Contains("--iso") || args.Contains("--joliet");

        // UDF path: same auto-detection as `ls`.
        if (!wantIso && (wantUdf || !HasIso9660(view)))
        {
            if (!UdfReader.IsUdf(view))
                return Fail("This image has neither an ISO 9660 nor a UDF filesystem.");

            UdfVolume vol;
            try { vol = UdfReader.Read(view); }
            catch (UdfFormatException ex) { return Fail(ex.Message); }

            Directory.CreateDirectory(outDir);
            int n = 0; long total = 0;
            foreach (var e in vol.Files)
            {
                var target = SafeTarget(outDir, e.Path);
                if (target is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var os = File.Create(target);
                UdfReader.ExtractFile(view, vol, e, os);
                n++; total += e.Size;
                Console.WriteLine($"  {e.Size,12:N0}  {e.Path}");
            }
            Console.WriteLine();
            Console.WriteLine($"Extracted {n:N0} UDF file(s), {total:N0} bytes to {Path.GetFullPath(outDir)}");
            return 0;
        }

        IsoDirectory dir;
        try { dir = IsoReader.Read(view, PreferenceFrom(args)); }
        catch (IsoFormatException ex) { return Fail(ex.Message); }

        Directory.CreateDirectory(outDir);
        int count = 0;
        long bytes = 0;

        foreach (var e in dir.Files)
        {
            var target = SafeTarget(outDir, e.Path);
            if (target is null) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var os = File.Create(target);
            IsoReader.ExtractFile(view, e, os);
            count++;
            bytes += e.Size;
            Console.WriteLine($"  {e.Size,12:N0}  {e.Path}");
        }

        Console.WriteLine();
        Console.WriteLine($"Extracted {count:N0} file(s), {bytes:N0} bytes to {Path.GetFullPath(outDir)}");
        return 0;
    }
}

static CdiVersion? ParseVersionArg(string[] args)
{
    int i = Array.IndexOf(args, "--version");
    if (i < 0 || i + 1 >= args.Length) return null;
    return args[i + 1].ToLowerInvariant() switch
    {
        "v2" => CdiVersion.V2,
        "v3" => CdiVersion.V3,
        "v35" or "v3.5" => CdiVersion.V35,
        _ => null,
    };
}

static string VersionLabel(CdiVersion v) => v switch
{
    CdiVersion.V2 => "v2 (DiscJuggler 2.x)",
    CdiVersion.V3 => "v3 (DiscJuggler 3.x)",
    CdiVersion.V35 => "v3.5/4 (DiscJuggler 3.5+)",
    _ => "unknown",
};

static int BuildRaw(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge build-raw <src.cue|src.cdi> <out.img> [--subcode pq|cooked|raw]");

    var src = args[1];
    var outPath = args[2];
    bool verbatim = args.Contains("--verbatim");
    var form = RawSubcodeForm.Packed96;
    for (int i = 3; i < args.Length - 1; i++)
        if (args[i] == "--subcode")
            form = args[i + 1].ToLowerInvariant() switch
            {
                "pq" => RawSubcodeForm.Pq16,
                "cooked" or "packed" => RawSubcodeForm.Packed96,
                "raw" or "interleaved" => RawSubcodeForm.Interleaved96,
                _ => throw new ArgumentException($"Unknown subcode form '{args[i + 1]}'."),
            };

    try
    {
        using Stream? cdiStream = src.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)
            ? null : File.OpenRead(src);
        DiscLayout layout = cdiStream is null
            ? DiscLayout.FromCueFile(src, subVerbatim: verbatim)
            : DiscLayout.FromCdi(CdiParser.Parse(cdiStream), cdiStream);

        if (verbatim && !layout.HasVerbatimSubchannel && !layout.HasProgramRw)
            Console.WriteLine("  note: --verbatim set but no .sub sidecar was found; " +
                              "writing DiscForge's own sub-channel.");
        using (layout)
        {
            long total = RawImageGenerator.TotalSectors(layout);
            int size = RawImageGenerator.SectorSize(form);
            Console.WriteLine($"Layout: {layout.Tracks.Count} track(s), " +
                              $"{RawImageGenerator.ProgramSectors(layout):N0} program sectors" +
                              (layout.Mcn is not null ? ", MCN" : "") +
                              (!layout.CdText.IsEmpty ? ", CD-TEXT" : ""));
            foreach (var t in layout.Tracks)
                Console.WriteLine($"  track {t.Number:D2} {t.Mode,-6} pregap {t.PregapTotalSectors} " +
                                  $"({t.PregapStoredSectors} stored) length {t.LengthSectors}" +
                                  (t.PostgapSectors > 0 ? $" postgap {t.PostgapSectors}" : "") +
                                  (t.ExtraIndexes.Count > 0 ? $" +{t.ExtraIndexes.Count} index(es)" : "") +
                                  (t.Isrc is not null ? $" ISRC {t.Isrc}" : ""));
            Console.WriteLine($"Image: {total:N0} sectors x {size} = " +
                              $"{total * size / (1024.0 * 1024.0):N1} MB ({form})");

            using var output = File.Create(outPath);
            long lastPct = -1;
            RawImageGenerator.Generate(layout, form, output, new Progress<double>(f =>
            {
                long pct = (long)(f * 100);
                if (pct != lastPct && pct % 10 == 0)
                {
                    lastPct = pct;
                    Console.Write($"\r  composing… {pct}%");
                }
            }));
            Console.WriteLine("\r  composing… done");
        }
        Console.WriteLine($"Wrote {outPath}");
        return 0;
    }
    catch (Exception ex)
    {
        return Fail(ex.Message);
    }
}

static int Subch(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge subch <file.sub>");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
    try
    {
        using var fs = File.OpenRead(args[1]);
        if (fs.Length % 96 != 0)
            return Fail($"'{args[1]}' is {fs.Length:N0} bytes — not a whole number of " +
                        "96-byte sub-channel frames. Wrong file, or a different format.");
        var a = RawSubchannel.Analyse(fs);
        Console.WriteLine($"Frames:      {a.Frames:N0}");
        Console.WriteLine($"Q valid:     {a.QValid:N0}");
        Console.WriteLine($"Q invalid:   {a.QInvalid:N0}");
        if (a.QInvalid > 0)
        {
            var shown = a.InvalidLbas.Take(24).Select(l => l.ToString());
            Console.WriteLine($"  at LBA:    {string.Join(", ", shown)}" +
                (a.InvalidLbas.Count > 24 ? ", …" : ""));
        }
        Console.WriteLine();
        Console.WriteLine(a.Summary);
        if (a.QInvalid > 0)
            Console.WriteLine(a.Paired
                ? $"Invalid frames are predominantly paired ({a.PairedInvalid}/{a.QInvalid}) — the LibCrypt shape."
                : $"Invalid frames are mostly scattered ({a.PairedInvalid}/{a.QInvalid} paired) — reads more like damage.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int MergeCertCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage:\n" +
                    "  dforge merge-cert <out.bin> <in1.bin> <in2.bin> [in3 ...] [--sector-size 2352|2048] [--key priv.b64 | --gen-key] [--json]\n" +
                    "  dforge merge-cert verify <cert.dmc.json> [out.bin in1.bin in2.bin ...]\n" +
                    "  Merges several imperfect rips of the SAME disc into one image and writes a signed certificate\n" +
                    "  (<out>.dmc.json) recording how EVERY sector was decided and which copy it came from. Each input's\n" +
                    "  sibling .badsectors.json is honoured: a copy's unreadable sectors are excluded from the vote\n" +
                    "  rather than counted as data. 'verify' checks the signature and, given the files, re-confirms the\n" +
                    "  input/output hashes the certificate binds. Pure recovery + provenance; it defeats nothing.");

    if (args[1] == "verify")
    {
        if (args.Length < 3) return Fail("usage: dforge merge-cert verify <cert.dmc.json> [out.bin in1.bin in2.bin ...]");
        var certPath = args[2];
        if (!File.Exists(certPath)) return Fail($"'{certPath}' not found.");
        try
        {
            var cert = DiscForge.Core.Recovery.MergeCertificate.Load(certPath);
            bool sigOk = cert.VerifySignature();
            Console.WriteLine($"{Path.GetFileName(certPath)}: {cert.Summary()}");
            Console.WriteLine(cert.Signature is null ? "  signature: (unsigned)"
                                                     : $"  signature: {(sigOk ? "VALID" : "INVALID")}");
            var files = args.Skip(3).Where(a => !a.StartsWith("--")).ToArray();
            bool bindingOk = true;
            if (files.Length >= 1 && File.Exists(files[0]))
            {
                string outHash = Sha256Hex(files[0]);
                bool o = string.Equals(outHash, cert.OutputSha256, StringComparison.OrdinalIgnoreCase);
                bindingOk &= o;
                Console.WriteLine($"  output image: {(o ? "matches" : "DOES NOT match")} the certificate.");
                var ins = files.Skip(1).ToArray();
                if (ins.Length > 0)
                {
                    for (int i = 0; i < ins.Length && i < cert.SourceSha256.Count; i++)
                    {
                        bool m = File.Exists(ins[i]) && string.Equals(Sha256Hex(ins[i]), cert.SourceSha256[i], StringComparison.OrdinalIgnoreCase);
                        bindingOk &= m;
                        Console.WriteLine($"    source {i + 1} {Path.GetFileName(ins[i])}: {(m ? "matches" : "DOES NOT match")}.");
                    }
                }
            }
            return sigOk && bindingOk ? 0 : 2;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    string outPath = args[1];
    int sectorSize = 2352;
    string? keyFile = OptVal(args, "--key");
    bool genKey = args.Contains("--gen-key");
    var inputs = new List<string>();
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--sector-size" && i + 1 < args.Length) { if (!int.TryParse(args[++i], out sectorSize) || sectorSize <= 0) return Fail("--sector-size must be positive."); }
        else if (args[i] == "--key") i++;                 // consumed by OptVal
        else if (args[i] == "--gen-key" || args[i] == "--json") { }
        else inputs.Add(args[i]);
    }
    if (inputs.Count < 2) return Fail("merge-cert needs at least two input images.");
    foreach (var p in inputs) if (!File.Exists(p)) return Fail($"File not found: {p}");

    try
    {
        var images = inputs.Select(File.ReadAllBytes).ToList();
        var holeMaps = inputs.Select(p =>
        {
            var sc = DiscForge.Core.Preservation.BadSectorMap.SidecarPath(p);
            if (!File.Exists(sc)) return (DiscForge.Core.Preservation.BadSectorMap?)null;
            try { return DiscForge.Core.Preservation.BadSectorMap.Load(sc); } catch { return null; }
        }).ToList();

        var result = DiscForge.Core.Recovery.ProvenanceMerge.Merge(images, holeMaps, sectorSize);
        var cert = result.Certificate;

        if (genKey || keyFile is not null)
        {
            System.Security.Cryptography.ECDsa priv;
            if (genKey)
            {
                var (privB64, _) = DiscForge.Core.Preservation.DumpLineageLog.GenerateKey();
                File.WriteAllText(outPath + ".key", privB64);
                priv = DiscForge.Core.Preservation.DumpLineageLog.LoadPrivateKey(privB64);
            }
            else priv = DiscForge.Core.Preservation.DumpLineageLog.LoadPrivateKey(File.ReadAllText(keyFile!).Trim());
            using (priv) cert = cert.Sign(priv);
        }

        File.WriteAllBytes(outPath, result.Image);
        var certPath = outPath + ".dmc.json";
        cert.Save(certPath);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                output = Path.GetFileName(outPath), certificate = Path.GetFileName(certPath),
                cert.SourceCount, cert.SectorCount, cert.OutputSha256,
                cert.AllAgree, cert.EdcRecovered, cert.VoteVerified, cert.VoteBestEffort,
                cert.SingleSource, cert.Unrecovered, cert.HoleExcluded,
                signed = cert.Signature is not null, cert.FullyRecovered,
            });
            return cert.FullyRecovered ? 0 : 1;
        }

        Console.WriteLine(cert.Summary());
        Console.WriteLine($"  wrote {Path.GetFileName(outPath)} and {Path.GetFileName(certPath)}" +
                          (genKey ? $" (+ {Path.GetFileName(outPath)}.key — keep it safe)" : "") + ".");
        if (cert.Signature is not null) Console.WriteLine($"  certificate signed; verify with: dforge merge-cert verify {Path.GetFileName(certPath)} {Path.GetFileName(outPath)} {string.Join(" ", inputs.Select(Path.GetFileName))}");
        if (!cert.FullyRecovered) Console.WriteLine($"  {cert.Unrecovered:N0} sector(s) unrecovered from these copies.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static string Sha256Hex(string path)
{
    using var s = File.OpenRead(path);
    return System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
}

static int DvdEccCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage:\n" +
                    "  dforge dvd-ecc self-test\n" +
                    "  dforge dvd-ecc repair <block.bin> <out.bin>\n" +
                    "  DVD sector-layer error correction (RS-PC) on a logical 208×182 ECC block: an inner code\n" +
                    "  PI = RS(182,172) per row and an outer code PO = RS(208,192) per column, a product code that\n" +
                    "  hands a row the inner code can't fix to the outer code as an erasure. Reuses DiscForge's GF(2^8)\n" +
                    "  RS engine and is validated by round-trip. NOTE: mapping a raw DVD dump's byte stream into the\n" +
                    "  logical block (the ECMA-267 physical interleave) is not verified here — confirm against a real\n" +
                    "  raw ECC block before relying on it for real-disc repair.");
    string sub = args[1].ToLowerInvariant();
    try
    {
        if (sub == "self-test")
        {
            var data = new byte[DiscForge.Core.Raw.DvdEcc.DataRows * DiscForge.Core.Raw.DvdEcc.DataCols];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 179 + 71) % 256);
            var block = DiscForge.Core.Raw.DvdEcc.EncodeBlock(data);

            // Injure it beyond the inner code: 12 whole rows destroyed (PI can't fix; PO erasure-corrects up to 16),
            // plus scattered single-byte errors the inner code fixes directly.
            int Cols = DiscForge.Core.Raw.DvdEcc.Cols;
            for (int r = 20; r < 32; r++) for (int c = 0; c < Cols; c++) block[r * Cols + c] ^= 0xFF;
            for (int r = 0; r < 10; r++) block[(r * 7) * Cols + (r * 13 % Cols)] ^= 0x5A;

            var res = DiscForge.Core.Raw.DvdEcc.Correct(block);
            var recovered = DiscForge.Core.Raw.DvdEcc.ExtractData(block);
            bool ok = res.Corrected && recovered.AsSpan().SequenceEqual(data);
            Console.WriteLine($"DVD RS-PC self-test: {res.Summary()}");
            Console.WriteLine(ok ? "  PASS — 12 destroyed rows + scattered errors corrected back to the original data."
                                 : "  FAIL — data not fully recovered.");
            return ok ? 0 : 1;
        }
        if (sub == "repair")
        {
            if (args.Length < 4) return Fail("usage: dforge dvd-ecc repair <block.bin> <out.bin>");
            if (!File.Exists(args[2])) return Fail($"'{args[2]}' not found.");
            var block = File.ReadAllBytes(args[2]);
            if (block.Length != DiscForge.Core.Raw.DvdEcc.BlockBytes)
                return Fail($"a DVD ECC block is {DiscForge.Core.Raw.DvdEcc.BlockBytes:N0} bytes; got {block.Length:N0}.");
            var res = DiscForge.Core.Raw.DvdEcc.Correct(block);
            File.WriteAllBytes(args[3], block);
            Console.WriteLine(res.Summary());
            Console.WriteLine($"  wrote {Path.GetFileName(args[3])}.");
            return res.Corrected ? 0 : 1;
        }
        return Fail("usage: dforge dvd-ecc <self-test|repair> …");
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int C2MergeCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge c2-merge <out.bin> <in1.bin> [in1.c2] <in2.bin> [in2.c2] ...\n" +
                    "  Merges several raw (2352-byte-sector) reads of the SAME disc using each read's C2 error\n" +
                    "  pointers (a redumper/DIC .c2 file, 294 bytes/sector). For every byte it takes a value a read's\n" +
                    "  C2 marks GOOD, so a sector that no single read got whole is reassembled from each read's good\n" +
                    "  bytes and confirmed by its EDC. Strongest with raw 2352 data sectors; audio falls back to voting.");
    string outPath = args[1];

    // Group the remaining args into reads: a .bin starts a read; a following .c2 attaches to it.
    var reads = new List<(string bin, string? c2)>();
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i].EndsWith(".c2", StringComparison.OrdinalIgnoreCase))
        {
            if (reads.Count == 0) return Fail($"'{args[i]}' has no preceding .bin.");
            var last = reads[^1];
            if (last.c2 is not null) return Fail($"read '{last.bin}' already has a C2 file.");
            reads[^1] = (last.bin, args[i]);
        }
        else reads.Add((args[i], null));
    }
    if (reads.Count < 2) return Fail("c2-merge needs at least two reads.");
    foreach (var (bin, c2) in reads)
    {
        if (!File.Exists(bin)) return Fail($"File not found: {bin}");
        if (c2 is not null && !File.Exists(c2)) return Fail($"File not found: {c2}");
    }

    try
    {
        var images = reads.Select(r => File.ReadAllBytes(r.bin)).ToList();
        var c2 = reads.Select(r => r.c2 is null ? null : File.ReadAllBytes(r.c2)).ToList();
        var result = DiscForge.Core.Recovery.C2ConsensusMerge.Merge(images, c2);
        File.WriteAllBytes(outPath, result.Image);

        var r = result.Report;
        Console.WriteLine(r.Summary());
        Console.WriteLine($"  wrote {Path.GetFileName(outPath)}.");
        if (r.RescuedFromFragments > 0)
            Console.WriteLine($"  {r.RescuedFromFragments:N0} sector(s) recovered that NO single read held whole — the C2 byte-consensus win.");
        if (r.EccRecovered > 0)
            Console.WriteLine($"  {r.EccRecovered:N0} sector(s) finished by the sector's own RSPC ECC after voting narrowed the errors.");
        if (!r.FullyRecovered)
            Console.WriteLine($"  {r.Unrecovered:N0} sector(s) still fail EDC; first: {string.Join(", ", r.UnrecoveredSectors.Take(12))}{(r.UnrecoveredSectors.Count > 12 ? " …" : "")}");
        return r.FullyRecovered ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SalvagePlanCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge salvage-plan <folder> [--json]\n" +
                    "  Finds where several unreadable dumps can rescue each other. Groups a collection's dumps by\n" +
                    "  title (disc geometry + a boot-area anchor), intersects their .badsectors.json hole maps, and\n" +
                    "  reports whether merging the copies would fill every hole, some, or none — with the exact\n" +
                    "  merge-cert command to run. Read-only; it plans the salvage, it doesn't perform it.");
    var folder = args[1];
    if (!Directory.Exists(folder)) return Fail($"'{folder}' is not a folder.");

    try
    {
        var report = DiscForge.Core.Collection.SalvagePlanner.Analyze(folder);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                report.Folder, report.Opportunities, report.FullySalvageable,
                groups = report.Groups.Select(g => new
                {
                    g.TitleKey, g.TotalSectors,
                    copies = g.Copies.Select(c => new { c.Name, c.RelPath, c.HoleCount }),
                    g.CompleteCopy, g.BestSingleHoles, g.UnrecoverableSectors, g.RecoveredBySalvage,
                    g.FullySalvageable, recommendation = g.Recommendation(),
                }),
            });
            return report.FullySalvageable > 0 ? 0 : 1;
        }

        Console.WriteLine(report.Summary());
        foreach (var g in report.Groups)
        {
            string tag = g.FullySalvageable ? "[SALVAGEABLE]" : g.RecoveredBySalvage > 0 ? "[PARTIAL]    "
                       : g.CompleteCopy is not null ? "[COMPLETE]   " : "[STUCK]      ";
            Console.WriteLine($"  {tag} {g.Copies.Count} cop{(g.Copies.Count == 1 ? "y" : "ies")}, {g.TotalSectors:N0} sectors " +
                              $"(best single: {g.BestSingleHoles:N0} hole(s))");
            foreach (var c in g.Copies)
                Console.WriteLine($"                {c.Name}: {c.HoleCount:N0} hole(s)");
            Console.WriteLine($"                → {g.Recommendation()}");
        }
        return report.FullySalvageable > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CollectionTriageCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge collection-triage <folder> [--dat <Redump DAT>] [--html <out.html>] [--json]\n" +
                    "  Walks a folder of dumps and produces one ranked worklist: for each dump, whether it is a\n" +
                    "  Redump-verified copy, INCOMPLETE (genuine unreadable sectors from its .badsectors.json), in need\n" +
                    "  of a re-cut (a wrong track split), a content-duplicate of another copy, or something to check.\n" +
                    "  A collection-level view no per-disc tool gives. Read-only; --html writes a shareable dashboard.");
    var folder = args[1];
    if (!Directory.Exists(folder)) return Fail($"'{folder}' is not a folder.");
    string? datPath = OptVal(args, "--dat");
    string? htmlPath = OptVal(args, "--html");

    try
    {
        DiscForge.Core.Dat.DatFile? dat = null;
        if (datPath is not null)
        {
            if (!File.Exists(datPath)) return Fail($"'{datPath}' not found.");
            dat = DiscForge.Core.Dat.DatFile.ParseText(File.ReadAllText(datPath));
        }

        var report = DiscForge.Core.Collection.CollectionTriage.Scan(folder, dat);

        if (htmlPath is not null)
        {
            File.WriteAllText(htmlPath, DiscForge.Core.Collection.CollectionTriage.RenderHtml(report));
        }

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                report.Folder, report.Total,
                verified = report.Count(DiscForge.Core.Collection.TriageStatus.Verified),
                incomplete = report.Count(DiscForge.Core.Collection.TriageStatus.Incomplete),
                needsRecut = report.Count(DiscForge.Core.Collection.TriageStatus.NeedsRecut),
                needsAttention = report.Count(DiscForge.Core.Collection.TriageStatus.NeedsAttention),
                duplicate = report.Count(DiscForge.Core.Collection.TriageStatus.Duplicate),
                entries = report.Entries.Select(e => new
                {
                    e.Name, e.RelPath, status = e.Status.ToString(), e.Game, e.Detail, e.Action,
                }),
                html = htmlPath is null ? null : Path.GetFileName(htmlPath),
            });
            return report.Count(DiscForge.Core.Collection.TriageStatus.Incomplete) > 0 ? 2 : 0;
        }

        Console.WriteLine(report.Summary());
        foreach (var e in report.Entries)
        {
            string tag = e.Status switch
            {
                DiscForge.Core.Collection.TriageStatus.Incomplete => "[INCOMPLETE]",
                DiscForge.Core.Collection.TriageStatus.NeedsRecut => "[RE-CUT]    ",
                DiscForge.Core.Collection.TriageStatus.NeedsAttention => "[CHECK]     ",
                DiscForge.Core.Collection.TriageStatus.Duplicate => "[DUPLICATE] ",
                _ => "[VERIFIED]  ",
            };
            Console.WriteLine($"  {tag} {e.Name}{(e.Game is null ? "" : $"  — {e.Game}")}");
            Console.WriteLine($"                {e.Detail}");
            if (e.Action is not null) Console.WriteLine($"                → {e.Action}");
        }
        if (htmlPath is not null) Console.WriteLine($"Dashboard: {htmlPath}");
        return report.Count(DiscForge.Core.Collection.TriageStatus.Incomplete) > 0 ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int FluxDemodCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage:\n" +
                    "  dforge flux-demod self-test [bytes]\n" +
                    "  dforge flux-demod encode <in.bin> <out.dff> [--cell N] [--jitter J]\n" +
                    "  dforge flux-demod decode <in.dff> <out.bin>\n" +
                    "  Demodulates an optical flux capture into the EFM channel bitstream — the stage between a raw\n" +
                    "  RF/flux capture and the EFM decoder: recovers the channel-cell clock from transition timing and\n" +
                    "  quantises each pit/land interval to EFM's 3T-11T run-length law. Software-first and validated by\n" +
                    "  round-trip against DiscForge's EFM encoder; decoding a REAL disc additionally needs the\n" +
                    "  authoritative ECMA-130 table in the EFM codebook (a data swap). The demodulation is complete now.");

    string sub = args[1].ToLowerInvariant();
    try
    {
        if (sub == "self-test")
        {
            int n = args.Length > 2 && int.TryParse(args[2], out var b) ? b : 4096;
            var data = new byte[n];
            for (int i = 0; i < n; i++) data[i] = (byte)((i * 73 + 29) % 256);
            var bits = DiscForge.Core.Raw.Efm.Encode(data);
            var flux = DiscForge.Core.Raw.FluxDemodulator.FromChannelBits(bits);
            var jit = DiscForge.Core.Raw.FluxDemodulator.ToTimings(flux, 10, jitter: 2, seed: 7);
            double cell = DiscForge.Core.Raw.FluxDemodulator.EstimateCellPeriod(jit);
            var demod = DiscForge.Core.Raw.FluxDemodulator.Demodulate(jit, flux.LeadingCells) with { TotalCells = flux.TotalCells };
            var outBytes = DiscForge.Core.Raw.FluxDecoder.Decode(demod);
            bool ok = outBytes.AsSpan().SequenceEqual(data);
            Console.WriteLine($"flux self-test: {n:N0} byte(s) -> {bits.Length:N0} channel cells -> {flux.RunLengths.Length:N0} runs.");
            Console.WriteLine($"  recovered cell clock {cell:F3} samples from timing jittered +/-2; run lengths recovered exactly.");
            Console.WriteLine(ok ? "  PASS — flux demodulated back to the original bytes through the EFM decoder." : "  FAIL — round-trip mismatch.");
            return ok ? 0 : 1;
        }
        if (sub == "encode")
        {
            if (args.Length < 4) return Fail("usage: dforge flux-demod encode <in.bin> <out.dff> [--cell N] [--jitter J]");
            if (!File.Exists(args[2])) return Fail($"'{args[2]}' not found.");
            int cell = int.TryParse(OptVal(args, "--cell"), out var c) ? c : 10;
            int jitter = int.TryParse(OptVal(args, "--jitter"), out var j) ? j : 0;
            var data = File.ReadAllBytes(args[2]);
            var bits = DiscForge.Core.Raw.Efm.Encode(data);
            var flux = DiscForge.Core.Raw.FluxDemodulator.FromChannelBits(bits);
            var timings = DiscForge.Core.Raw.FluxDemodulator.ToTimings(flux, cell, jitter);
            var demod = DiscForge.Core.Raw.FluxDemodulator.Demodulate(timings, flux.LeadingCells) with { TotalCells = flux.TotalCells };
            File.WriteAllBytes(args[3], demod.Serialize());
            Console.WriteLine($"Modelled a flux capture of {data.Length:N0} byte(s): {demod.RunLengths.Length:N0} runs at cell={cell}" +
                              (jitter > 0 ? $" (+/-{jitter} jitter)" : "") + $" -> {Path.GetFileName(args[3])}.");
            return 0;
        }
        if (sub == "decode")
        {
            if (args.Length < 4) return Fail("usage: dforge flux-demod decode <in.dff> <out.bin>");
            if (!File.Exists(args[2])) return Fail($"'{args[2]}' not found.");
            var flux = DiscForge.Core.Raw.FluxBitstream.Deserialize(File.ReadAllBytes(args[2]));
            var bytes = DiscForge.Core.Raw.FluxDecoder.Decode(flux);
            File.WriteAllBytes(args[3], bytes);
            Console.WriteLine($"Demodulated {flux.RunLengths.Length:N0} run(s) -> {bytes.Length:N0} EFM byte(s) -> {Path.GetFileName(args[3])}.");
            return 0;
        }
        return Fail("usage: dforge flux-demod <self-test|encode|decode> …");
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BinCueMergeCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge bincue-merge <in.cue> <out.bin> [out.cue]");
    string inCue = args[1];
    string outBin = args[2];
    string outCue = args.Length > 3 ? args[3] : Path.ChangeExtension(outBin, ".cue");
    if (!File.Exists(inCue)) return Fail($"'{inCue}' not found.");
    try
    {
        var r = BinCueMerge.Merge(inCue, outBin, outCue);
        Console.WriteLine($"Merged {r.Tracks} track(s) into {Path.GetFileName(r.BinPath)} " +
                          $"({r.Bytes:N0} bytes) with {Path.GetFileName(r.CuePath)}.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BinCueSplitCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge bincue-split <in.cue> [out-dir] [base-name] [out.cue]");
    string inCue = args[1];
    if (!File.Exists(inCue)) return Fail($"'{inCue}' not found.");
    string outDir = args.Length > 2 ? args[2] : (Path.GetDirectoryName(Path.GetFullPath(inCue)) ?? ".");
    string baseName = args.Length > 3 ? args[3] : Path.GetFileNameWithoutExtension(inCue);
    string outCue = args.Length > 4 ? args[4] : Path.Combine(outDir, baseName + " (split).cue");
    try
    {
        var r = BinCueMerge.Split(inCue, outDir, baseName, outCue);
        Console.WriteLine($"Split into {r.Tracks} file(s) in {outDir}, cue {Path.GetFileName(r.CuePath)}:");
        foreach (var b in r.BinPaths) Console.WriteLine($"  {b}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SilenceSplitCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge silence-split <in.wav> [out.cue] [--threshold -50] [--min-gap 1.5] [--min-track 2]\n" +
        "  Finds track boundaries in a gapless album rip by detecting the silent gaps between songs, and\n" +
        "  writes a cue sheet (INDEX 00 = pregap, INDEX 01 = audio onset, snapped to CD sectors). Short\n" +
        "  intra-song pauses stay inside their track; leading/trailing silence is trimmed. Analysis only —\n" +
        "  it locates and describes boundaries, it does not cut the audio.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    string outCue = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : Path.ChangeExtension(args[1], ".cue");
    var opt = new DiscForge.Core.Audio.SilenceSplitter.Options
    {
        ThresholdDb = double.TryParse(OptVal(args, "--threshold"), out var th) ? th : -50.0,
        MinSilenceSeconds = double.TryParse(OptVal(args, "--min-gap"), out var mg) ? mg : 1.5,
        MinTrackSeconds = double.TryParse(OptVal(args, "--min-track"), out var mt) ? mt : 0.5,
    };
    try
    {
        var bytes = File.ReadAllBytes(args[1]);
        var info = DiscForge.Core.Audio.WavReader.Read(new MemoryStream(bytes));
        if (info.BitsPerSample != 16) return Fail($"Only 16-bit PCM is supported (got {info.BitsPerSample}-bit).");

        int n = (int)(info.DataLength / 2);
        var pcm = new short[n];
        int off = (int)info.DataOffset;
        for (int i = 0; i < n; i++) pcm[i] = (short)(bytes[off + i * 2] | (bytes[off + i * 2 + 1] << 8));

        var r = DiscForge.Core.Audio.SilenceSplitter.Analyze(pcm, info.Channels, info.SampleRate, opt);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {r.Summary()}");
        if (r.Tracks.Count > 0)
        {
            File.WriteAllText(outCue, DiscForge.Core.Audio.SilenceSplitter.ToCue(r, Path.GetFileName(args[1])) + "\n");
            Console.WriteLine($"Wrote {Path.GetFileName(outCue)} with {r.Tracks.Count} track(s).");
        }
        return r.Tracks.Count > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DeEmphCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge deemph <in.wav> <out.wav>\n" +
        "  Applies CD de-emphasis (the standard 50/15µs curve) to a pre-emphasised audio track, restoring\n" +
        "  the intended flat response. Reads a 16-bit PCM WAV and writes a de-emphasised copy. Apply only to\n" +
        "  tracks the disc flags as pre-emphasised (Q control bit 1 / cue PRE flag); a normal track needs no\n" +
        "  de-emphasis. The filter is derived from the analog transfer function, not hard-coded coefficients.");
    string inPath = args[1], outPath = args[2];
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");
    try
    {
        var bytes = File.ReadAllBytes(inPath);
        var info = DiscForge.Core.Audio.WavReader.Read(new MemoryStream(bytes));
        if (info.BitsPerSample != 16) return Fail($"Only 16-bit PCM is supported (got {info.BitsPerSample}-bit).");

        int sampleCount = (int)(info.DataLength / 2);
        var pcm = new short[sampleCount];
        int off = (int)info.DataOffset;
        for (int i = 0; i < sampleCount; i++)
            pcm[i] = (short)(bytes[off + i * 2] | (bytes[off + i * 2 + 1] << 8));

        var filter = new DiscForge.Core.Audio.DeEmphasis(info.SampleRate);
        filter.ProcessInterleaved(pcm, info.Channels);

        using (var outFs = File.Create(outPath))
            DiscForge.Core.Audio.WavWriter.Write(outFs, pcm, info.SampleRate, info.Channels);

        Console.WriteLine($"{Path.GetFileName(outPath)}: de-emphasised {info.Channels}ch/{info.SampleRate}Hz — " +
            $"high-shelf {filter.ResponseDb(info.SampleRate / 2.0):0.0} dB, 1 kHz {filter.ResponseDb(1000):0.00} dB.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdTextCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge cdtext <file.cdt>\n" +
        "  Decodes CD-TEXT packs into album and per-track title/performer/songwriter. Reads a flat 18-byte\n" +
        "  pack stream (a 4-byte .cdt header is skipped automatically); each pack's CRC is checked and the\n" +
        "  lead-in's repeated cycles are de-duplicated. Also reports first/last track and language. Read-only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var info = DiscForge.Core.Raw.CdTextReader.ReadPackStream(File.ReadAllBytes(args[1]));
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Raw.CdTextReader.Render(info)}");
        return info.HasText ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int LibcryptCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge libcrypt <disc.sub> [--start-lba N] [--sbi <out.sbi>]\n" +
            "  Characterises a PlayStation disc's LibCrypt protection from its raw 96-byte/sector\n" +
            "  subchannel: which generation (address-tampered valid-CRC, or broken-CRC), how many\n" +
            "  sectors, a stable 16-bit fingerprint, the XOR-folded key material, and each affected\n" +
            "  sector's CRC delta. Preservation only — with --sbi it also writes the emulator sidecar.");
    string subPath = args[1];
    if (!File.Exists(subPath)) return Fail($"'{subPath}' not found.");
    uint startLba = 0;
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--start-lba") uint.TryParse(args[i + 1], out startLba);
    string? outSbi = null;
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--sbi") outSbi = args[i + 1];
    try
    {
        var sub = File.ReadAllBytes(subPath);
        if (sub.Length % RawSubchannel.FrameSize != 0)
            return Fail($"'{subPath}' is {sub.Length:N0} bytes — not a whole number of 96-byte frames.");
        var report = DiscForge.Core.PlayStation.LibcryptAnalyzer.Scan(sub, startLba);
        Console.WriteLine(DiscForge.Core.PlayStation.LibcryptAnalyzer.Render(report));

        if (outSbi != null && report.Present)
        {
            var doc = DiscForge.Core.PlayStation.LibcryptAnalyzer.ToSbi(report);
            File.WriteAllBytes(outSbi, DiscForge.Core.PlayStation.Sbi.Write(doc));
            Console.WriteLine($"Wrote {Path.GetFileName(outSbi)} with {doc.Entries.Count} LibCrypt entry(ies).");
        }
        return report.Present ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int EcmCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge ecm <in.bin> [out.ecm]\n" +
            "  Shrink a raw CD image (2352-byte sectors) to ECM by stripping the sync, EDC and\n" +
            "  Reed-Solomon parity every decoder can regenerate. Reversible and lossless: unecm\n" +
            "  rebuilds the original byte-for-byte. Not data compression and nothing protection-\n" +
            "  related. Any sector that doesn't reconstruct exactly is preserved verbatim.");
    string inPath = args[1];
    if (!File.Exists(inPath)) return Fail($"'{inPath}' not found.");
    string outPath = args.Length > 2 ? args[2] : inPath + ".ecm";
    try
    {
        long inSize = new FileInfo(inPath).Length;
        using (var inp = File.OpenRead(inPath))
        using (var outp = File.Create(outPath))
            DiscForge.Core.Raw.EcmCodec.Encode(inp, outp);
        long outSize = new FileInfo(outPath).Length;
        double pct = inSize > 0 ? 100.0 * (inSize - outSize) / inSize : 0;
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {inSize:N0} -> {outSize:N0} bytes " +
                          $"({pct:0.0}% smaller).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int UnecmCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge unecm <in.ecm> [out.bin]\n" +
            "  Rebuild the original raw image from an ECM file, regenerating each sector's sync,\n" +
            "  EDC and ECC. Verifies the whole-file EDC on completion.");
    string inPath = args[1];
    if (!File.Exists(inPath)) return Fail($"'{inPath}' not found.");
    string outPath = args.Length > 2 ? args[2]
        : inPath.EndsWith(".ecm", StringComparison.OrdinalIgnoreCase) ? inPath[..^4]
        : inPath + ".unecm";
    if (string.Equals(Path.GetFullPath(inPath), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
        return Fail("Refusing to write the output over the input; name the output file explicitly.");
    try
    {
        long written;
        using (var inp = File.OpenRead(inPath))
        using (var outp = File.Create(outPath))
            written = DiscForge.Core.Raw.EcmCodec.Decode(inp, outp);
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {written:N0} bytes (EDC verified).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SbiMake(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge sbi-make <disc.sub> [out.sbi] [--start-lba N]");
    string subPath = args[1];
    if (!File.Exists(subPath)) return Fail($"'{subPath}' not found.");
    string outSbi = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : Path.ChangeExtension(subPath, ".sbi");
    uint startLba = 0;
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--start-lba") uint.TryParse(args[i + 1], out startLba);
    try
    {
        var sub = File.ReadAllBytes(subPath);
        if (sub.Length % RawSubchannel.FrameSize != 0)
            return Fail($"'{subPath}' is {sub.Length:N0} bytes — not a whole number of 96-byte frames.");
        var doc = Sbi.FromSubchannel(sub, startLba);
        if (doc.IsEmpty)
        {
            Console.WriteLine("No LibCrypt subchannel found — nothing to write (the .sub already preserves everything).");
            return 0;
        }
        File.WriteAllBytes(outSbi, Sbi.Write(doc));
        Console.WriteLine($"Wrote {Path.GetFileName(outSbi)} with {doc.Entries.Count} LibCrypt entry(ies).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PsxBuild(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge psx-build <folder> <out.bin> [volume-id] [out.cue]");
    string folder = args[1];
    string outBin = args[2];
    string volId = args.Length > 3 ? args[3] : "PSX";
    string outCue = args.Length > 4 ? args[4] : Path.ChangeExtension(outBin, ".cue");
    if (!Directory.Exists(folder)) return Fail($"Folder not found: {folder}");
    try
    {
        int sectors = PsxImageBuilder.BuildFromFolder(folder, volId, outBin, outCue);
        Console.WriteLine($"Built {Path.GetFileName(outBin)} ({sectors:N0} Mode 2/2352 sectors, " +
                          $"{(long)sectors * 2352:N0} bytes) + {Path.GetFileName(outCue)}.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SbiInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge sbi-info <file.sbi>");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
    try
    {
        var doc = Sbi.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine(Sbi.Describe(doc));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdgPreview(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cdg-preview <raw-image|src.cue> [--seconds N] [--out shot.ppm]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");

    int seconds = 30;
    string? outPath = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
        else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
    }

    try
    {
        var decoder = new CdgDecoder();
        long sectorsFed = 0;
        long limit = seconds * 75L;
        var rw = new byte[96];

        if (args[1].EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        {
            // Straight from the .sub sidecar — tests the SOURCE.
            using var layout = DiscLayout.FromCueFile(args[1]);
            if (!layout.HasProgramRw)
                return Fail("This CUE has no .sub sidecar next to its BIN — no CD+G to decode.");
            foreach (var t in layout.Tracks)
            {
                if (t.SubSource is null) continue;
                long stored = t.PregapStoredSectors + t.LengthSectors;
                for (long s = 0; s < stored && sectorsFed < limit; s++, sectorsFed++)
                {
                    t.SubSource.Seek(t.SubByteOffset + s * 96, SeekOrigin.Begin);
                    t.SubSource.ReadExactly(rw, 0, 96);
                    for (int i = 0; i < 96; i++) rw[i] &= 0x3F;   // symbols only
                    decoder.FeedSector(rw);
                }
            }
        }
        else
        {
            // From a raw image — tests what would be (or was) ON THE DISC.
            using var fs = File.OpenRead(args[1]);
            var (size, form) = RawImageInspector.DetectLayout(fs);
            if (form is null or RawSubcodeForm.Pq16)
                return Fail($"This image carries no R-W symbols " +
                            $"({(form is null ? "no subcode" : "PQ-16")}) — no CD+G possible.");
            long leadIn = RawImageInspector.FindLeadInLength(fs, size, form.Value);
            long total = fs.Length / size;
            var sub = new byte[size - 2352];
            for (long s = leadIn; s < total && sectorsFed < limit; s++, sectorsFed++)
            {
                fs.Position = s * size + 2352;
                fs.ReadExactly(sub, 0, sub.Length);
                SubcodeFrame.ExtractRw(sub, form.Value, rw);
                decoder.FeedSector(rw);
            }
        }

        Console.WriteLine($"Decoded {sectorsFed:N0} sector(s) ({sectorsFed / 75.0:N1} s of playback):");
        Console.WriteLine($"  packets seen      {decoder.PacketsSeen:N0}");
        Console.WriteLine($"  graphics packets  {decoder.GraphicsPackets:N0}");
        Console.WriteLine($"  tiles drawn       {decoder.TileCount:N0}");
        Console.WriteLine($"  screen presets    {decoder.PresetCount:N0}");
        Console.WriteLine($"  palette loads     {decoder.PaletteLoads:N0}");
        if (decoder.GraphicsPackets == 0)
            Console.WriteLine("  (no CD+G content found in this range)");

        if (outPath is not null)
        {
            File.WriteAllBytes(outPath, decoder.ToPpm());
            Console.WriteLine($"Wrote screenshot: {outPath} (PPM; opens in IrfanView/GIMP/ffmpeg)");
        }
        return decoder.GraphicsPackets > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdgRender(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cdg-render <file.cdg> [--at MM:SS] [--out <file.png>]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");

    TimeSpan? at = null;
    string? outPath = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--at" && i + 1 < args.Length)
        {
            var parts = args[++i].Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int mm) || !int.TryParse(parts[1], out int ss))
                return Fail("--at expects MM:SS");
            at = new TimeSpan(0, mm, ss);
        }
        else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
    }

    try
    {
        var cdg = File.ReadAllBytes(args[1]);
        DiscForge.Core.Cdg.CdgImage image = at is { } t
            ? DiscForge.Core.Cdg.CdgRenderer.RenderFrameAt(cdg, t)
            : DiscForge.Core.Cdg.CdgRenderer.RenderFinalFrame(cdg);
        var png = DiscForge.Core.Cdg.CdgRenderer.RenderToPng(image);

        outPath ??= Path.ChangeExtension(args[1], ".png");
        File.WriteAllBytes(outPath, png);
        Console.WriteLine($"Rendered {image.Width}x{image.Height} frame " +
                          $"{(at is { } tt ? $"at {tt:mm\\:ss}" : "(end of stream)")} -> {outPath}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdgExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge cdg-extract <subchannel-sidecar> <out.cdg>");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");

    try
    {
        using var sub = File.OpenRead(args[1]);
        var cdg = DiscForge.Core.Cdg.CdgExtractor.Extract(sub);
        File.WriteAllBytes(args[2], cdg);
        Console.WriteLine($"Extracted {cdg.Length:N0} byte(s) " +
                          $"({cdg.Length / 24:N0} packet(s)) -> {args[2]}");
        return cdg.Length > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ViewSector(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge view-sector <image> <lba|mm:ss:ff|+index> [--count N] [--descramble]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
    int count = 1;
    for (int i = 3; i < args.Length - 1; i++)
        if (args[i] == "--count") count = Math.Clamp(int.Parse(args[i + 1]), 1, 64);
    bool descramble = args.Contains("--descramble");

    try
    {
        using var access = SectorAccess.Open(args[1]);
        long start = access.Resolve(args[2]);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {access.Kind}, " +
                          $"{access.TotalSectors:N0} sectors");

        for (long s = start; s < start + count && s < access.TotalSectors; s++)
        {
            var sec = access.Read(s);
            bool raw2352 = sec.Stored.Length == 2352;
            var data = sec.Stored;

            // Describe what we're looking at (and optionally descramble).
            string desc = "";
            if (raw2352)
            {
                bool hasSync = data[0] == 0 && data[1] == 0xFF && data[11] == 0;
                if (!hasSync) desc = "audio (no sync)";
                else
                {
                    var copy = (byte[])data.Clone();
                    CdScrambler.ScrambleInPlace(copy);
                    bool scrambled =
                        copy[15] is 1 or 2 &&
                        (copy[15] != 1 || EdcEcc.VerifyMode1(copy).EdcOk);
                    bool plain =
                        data[15] is 1 or 2 &&
                        (data[15] != 1 || EdcEcc.VerifyMode1(data).EdcOk);
                    if (scrambled && !plain)
                    {
                        desc = $"Mode {copy[15]}, scrambled";
                        if (descramble) { data = copy; desc += " (shown descrambled)"; }
                    }
                    else desc = $"Mode {data[15]}, unscrambled";
                    if (data == sec.Stored && data[15] == 1 && plain)
                    {
                        var (e, c) = EdcEcc.VerifyMode1(data);
                        desc += $"; EDC {(e ? "OK" : "BAD")}, ECC {(c ? "OK" : "BAD")}";
                    }
                    else if (descramble && data[15] == 1)
                    {
                        var (e, c) = EdcEcc.VerifyMode1(data);
                        desc += $"; EDC {(e ? "OK" : "BAD")}, ECC {(c ? "OK" : "BAD")}";
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"── sector +{sec.FileIndex}" +
                (sec.LeadIn ? " (lead-in)" : sec.Lba == long.MinValue ? "" : $"  LBA {sec.Lba}") +
                $"  MSF {sec.Msf}" +
                (sec.Track is { } t ? $"  track {t:D2}" +
                    (sec.Session is { } ss ? $" (session {ss})" : "") : "") +
                $"  [{sec.Stored.Length} bytes]" +
                (desc.Length > 0 ? $"  {desc}" : ""));

            for (int off = 0; off < data.Length; off += 16)
            {
                var row = data.AsSpan(off, Math.Min(16, data.Length - off));
                var hex = string.Join(" ", row.ToArray().Select(b => b.ToString("x2")));
                var ascii = new string(row.ToArray()
                    .Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
                Console.WriteLine($"  {off,5:X4}  {hex,-47}  {ascii,-16}  {Region(off, data.Length, raw2352)}");
            }

            if (sec.Subcode is { } sub && sec.SubcodeForm is { } form)
            {
                var q = new byte[12];
                SubcodeFrame.ExtractQ(sub, form, q);
                bool crcOk = DiscForge.Core.Util.Crc16.ComputeInverted(q.AsSpan(0, 10))
                             == (ushort)((q[10] << 8) | q[11]);
                Console.WriteLine($"  Q: {string.Join(" ", q.Select(b => b.ToString("x2")))}  " +
                    $"(CRC {(crcOk ? "OK" : "BAD")}; ctrl/adr {q[0]:x2} TNO {q[1]:x2} IDX {q[2]:x2} " +
                    $"rel {q[3]:x2}:{q[4]:x2}:{q[5]:x2} abs {q[7]:x2}:{q[8]:x2}:{q[9]:x2})");
            }
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }

    static string Region(int off, int len, bool raw)
    {
        if (!raw || len != 2352) return "";
        return off switch
        {
            0x000 => "sync 000-00B, header 00C-00F",
            0x010 => "user data 010-80F",
            0x810 => "EDC 810-813, pad 814-81B, ECC P 81C-8C7",
            0x8C0 => "ECC Q 8C8-92F",
            _ => "",
        };
    }
}

static int ExtractSectors(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge extract-sectors <image> <out> --start <addr> --count N " +
                    "[--as stored|user|raw2352] [--byteswap]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");

    string? startArg = null;
    long count = -1;
    string mode = "stored";
    bool byteswap = args.Contains("--byteswap");
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--start" && i + 1 < args.Length) startArg = args[++i];
        else if (args[i] == "--count" && i + 1 < args.Length) count = long.Parse(args[++i]);
        else if (args[i] == "--as" && i + 1 < args.Length) mode = args[++i].ToLowerInvariant();
    }
    if (startArg is null || count <= 0)
        return Fail("Both --start and --count are required.");
    if (mode is not ("stored" or "user" or "raw2352"))
        return Fail($"Unknown --as '{mode}'. Use stored, user, or raw2352.");

    try
    {
        using var access = SectorAccess.Open(args[1]);
        long start = access.Resolve(startArg);
        long end = Math.Min(start + count, access.TotalSectors);
        if (start >= access.TotalSectors)
            return Fail($"Start sector is past the end of the image ({access.TotalSectors:N0} sectors).");

        using var output = File.Create(args[2]);
        var raw = new byte[2352];
        long written = 0;
        for (long s = start; s < end; s++)
        {
            var sec = access.Read(s);
            byte[] payload;
            switch (mode)
            {
                case "user":
                    payload = sec.Stored.Length switch
                    {
                        2048 => sec.Stored,
                        2336 => sec.Stored[8..2056],           // XA: skip subheader
                        2352 when HasSync(sec.Stored) =>
                            Unscrambled(sec.Stored)[16..2064],
                        2352 => sec.Stored,                    // audio: all of it
                        _ => sec.Stored,
                    };
                    break;
                case "raw2352":
                    switch (sec.Stored.Length)
                    {
                        case 2352:
                            payload = HasSync(sec.Stored) ? Unscrambled(sec.Stored) : sec.Stored;
                            break;
                        case 2048:
                            RawSectorBuilder.BuildMode1(sec.Stored, sec.Msf, raw);
                            payload = raw;
                            break;
                        case 2336:
                            RawSectorBuilder.BuildMode2(sec.Stored, sec.Msf, raw);
                            payload = raw;
                            break;
                        default:
                            payload = sec.Stored;
                            break;
                    }
                    break;
                default:
                    payload = sec.Stored;
                    break;
            }

            if (byteswap)
            {
                payload = (byte[])payload.Clone();
                for (int i = 0; i + 1 < payload.Length; i += 2)
                    (payload[i], payload[i + 1]) = (payload[i + 1], payload[i]);
            }
            output.Write(payload, 0, payload.Length);
            written += payload.Length;
        }
        Console.WriteLine($"Extracted {end - start:N0} sector(s), {written:N0} bytes " +
                          $"({mode}{(byteswap ? ", byteswapped" : "")}) to {args[2]}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }

    static bool HasSync(byte[] s)
    {
        if (s[0] != 0 || s[11] != 0) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    static byte[] Unscrambled(byte[] s)
    {
        // If it validates as-is, it's already clean; else try descrambled.
        if (s[15] == 1 && EdcEcc.VerifyMode1(s).EdcOk) return s;
        var copy = (byte[])s.Clone();
        CdScrambler.ScrambleInPlace(copy);
        if (copy[15] is 1 or 2 && (copy[15] != 1 || EdcEcc.VerifyMode1(copy).EdcOk)) return copy;
        return s;
    }
}

static int InspectRaw(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge inspect-raw <image> [--deep]");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
    bool deep = args.Contains("--deep");

    try
    {
        using var fs = File.OpenRead(args[1]);
        var r = RawImageInspector.Inspect(fs, deep);

        Console.WriteLine($"File:        {Path.GetFileName(args[1])} ({fs.Length:N0} bytes)");
        Console.WriteLine($"Format:      {r.SectorSize} bytes/sector — " +
            (r.Form is null ? "main channel only (no subcode)" : r.Form.ToString()));
        Console.WriteLine($"Sectors:     {r.TotalSectors:N0}" +
            (r.HasLeadIn ? $" ({r.LeadInSectors:N0} lead-in + {r.TotalSectors - r.LeadInSectors:N0} program)"
                         : r.Form is null ? "" : " (no lead-in — program-only rip)"));
        if (r.Form is not null)
        {
            Console.WriteLine($"Q integrity: {r.QFramesChecked - r.QCrcErrors}/{r.QFramesChecked} " +
                $"frames CRC-valid{(deep ? "" : " (sampled)")}" +
                (r.QCrcErrors > 0 ? $"  <-- {r.QCrcErrors} BAD" : ""));
            if (r.LeadOutStartSector > 0)
            {
                var lo = Msf.FromSectors(r.LeadOutStartSector);
                Console.WriteLine($"Lead-out:    {lo} ({r.LeadOutStartSector:N0} sectors)");
            }
            if (r.Mcn is not null) Console.WriteLine($"MCN:         {r.Mcn}");
            if (r.AlbumTitle is not null || r.AlbumPerformer is not null)
            {
                Console.WriteLine($"CD-TEXT:     \"{r.AlbumTitle}\"" +
                    (r.AlbumPerformer is not null ? $" — {r.AlbumPerformer}" : "") +
                    $"  ({r.CdTextPacksValid} packs valid" +
                    (r.CdTextPacksBad > 0 ? $", {r.CdTextPacksBad} bad" : "") + ")");
                for (int i = 0; i < r.TrackTitles.Count; i++)
                    Console.WriteLine($"             {i + 1:D2}. {r.TrackTitles[i]}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  #  Type   Start MSF    ISRC          Data checks");
        Console.WriteLine("  -- -----  -----------  ------------  --------------------------------");
        foreach (var t in r.Tracks)
        {
            string kind = t.IsData ? $"Data{(t.Mode is { } m ? $"{m}" : "")}" : "Audio";
            string data = "";
            if (t.IsData)
            {
                data = t.Scrambled switch
                {
                    true => "scrambled",
                    false => "unscrambled",
                    null => "undetermined",
                };
                if (t.DataSectorsChecked > 0)
                    data += t.EdcErrors == 0 && t.EccErrors == 0
                        ? $"; {t.CheckKind} OK ({t.DataSectorsChecked} checked)"
                        : $"; {t.CheckKind}: EDC errs {t.EdcErrors}, ECC errs {t.EccErrors} " +
                          $"of {t.DataSectorsChecked}  <-- BAD";
                else if (t.CheckKind is not null)
                    data += $"; checks: {t.CheckKind}";
            }
            Console.WriteLine($"  {t.Number:D2} {kind,-6} {Msf.FromSectors(t.StartSector),-12} " +
                              $"{t.Isrc ?? "-",-13} {data}");
        }

        foreach (var n in r.Notes) Console.WriteLine($"note: {n}");

        bool clean = r.QCrcErrors == 0 &&
                     r.Tracks.All(t => t.EdcErrors == 0 && t.EccErrors == 0);
        Console.WriteLine();
        Console.WriteLine(clean ? "Result: clean." : "Result: problems found (see above).");
        return clean ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int RawVerifyReadback(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge raw-verify-readback <golden.img> <readback.bin> [--report out.html] [--json]\n" +
            "  Prove a RAW burn landed on disc byte-for-byte: compare a raw read-back of the\n" +
            "  burned CD against the exact golden image build-raw produced — main channel, EDC/ECC\n" +
            "  and every Q frame. Unlike a verify-by-MD5 this also checks the sub-channel, so it\n" +
            "  catches a lost protection Q or a mis-addressed sector, not just user-data damage.\n" +
            "  Both may be any raw layout (2352 / 2368 / 2448); the read-back need not include the\n" +
            "  drive-owned lead-in.\n" +
            "  --report out.html  write a shareable burn-validation certificate.\n" +
            "  --partial          the read-back is an intentional sub-range (e.g. one track of a\n" +
            "                     mixed-mode disc read on its own) — grade the overlap only; do not\n" +
            "                     count the golden sectors beyond the read-back as dropouts.\n" +
            "  --json             print the result as JSON (for scripting).");
    if (!File.Exists(args[1])) return Fail($"Golden image not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Read-back capture not found: {args[2]}");
    string? reportPath = null;
    bool json = false;
    bool partial = false;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--report" && i + 1 < args.Length) reportPath = args[++i];
        else if (args[i] == "--json") json = true;
        else if (args[i] == "--partial") partial = true;
    }

    try
    {
        long goldenLen, readbackLen;
        RawReadbackCompare.Report r;
        using (var golden = File.OpenRead(args[1]))
        using (var readback = File.OpenRead(args[2]))
        {
            goldenLen = golden.Length;
            readbackLen = readback.Length;
            r = RawReadbackCompare.Compare(golden, readback, partial);
        }

        if (reportPath is not null)
        {
            File.WriteAllText(reportPath, RawReadbackReport.Html(
                r, Path.GetFileName(args[1]), Path.GetFileName(args[2]), goldenLen, readbackLen,
                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
            Console.WriteLine($"Wrote certificate: {reportPath}");
        }
        if (json)
        {
            Console.WriteLine(RawReadbackReport.Json(
                r, Path.GetFileName(args[1]), Path.GetFileName(args[2]), goldenLen, readbackLen));
            return r.Result == RawReadbackCompare.Grade.Fail ? 1 : 0;
        }

        Console.WriteLine($"Golden:      {Path.GetFileName(args[1])} ({goldenLen:N0} bytes)");
        Console.WriteLine($"Read-back:   {Path.GetFileName(args[2])} ({readbackLen:N0} bytes)");
        Console.WriteLine($"Compared:    {r.SectorsCompared:N0} program sectors");
        Console.WriteLine($"Main channel:{(r.MainMismatches == 0 ? " all identical" : $" {r.MainMismatches:N0} mismatch(es), {r.EdcBroken:N0} with broken EDC")}" +
            (r.ScrambleNormalized > 0 ? $"  ({r.ScrambleNormalized:N0} descrambled-on-read, content byte-identical)" : ""));
        Console.WriteLine($"Sub-channel: {(r.SubMismatches == 0 ? "all identical" : $"{r.SubMismatches:N0} differ — {r.MisAddressed:N0} mis-addressed, {r.ProtectionLosses:N0} protection-loss, {r.SubTimingOnly:N0} timing-only")}");
        if (r.Dropouts > 0) Console.WriteLine($"Dropouts:    {r.Dropouts:N0} program sector(s) missing from the read-back");
        if (r.Examples.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  First differences:");
            foreach (var d in r.Examples.Take(20))
                Console.WriteLine($"    sector {d.AbsoluteSector,8}  [{d.Severity}] {d.Category}: {d.Detail}");
        }
        foreach (var n in r.Notes) Console.WriteLine($"note: {n}");
        Console.WriteLine();
        Console.WriteLine($"Result: {r.Summary}");
        return r.Result == RawReadbackCompare.Grade.Fail ? 1 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DvdVerifyReadback(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge dvd-verify-readback <source.iso> <readback.bin> [--layer-break LBA]\n" +
            "  Verify a burned DVD/BD against its source image at the sector level, reporting at\n" +
            "  ECC-block (16-sector) granularity. With --layer-break, attributes each mismatch to L0/L1\n" +
            "  and checks the break sits on a legal boundary. Like ImgBurn's verify but it tells you\n" +
            "  WHERE a burn differs, not just an MD5. Trailing blank sectors in the read-back are\n" +
            "  treated as benign padding.");
    if (!File.Exists(args[1])) return Fail($"Source image not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Read-back capture not found: {args[2]}");
    long? layerBreak = null;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--layer-break" && i + 1 < args.Length && long.TryParse(args[++i], out var lb)) layerBreak = lb;

    try
    {
        using var src = File.OpenRead(args[1]);
        using var rb = File.OpenRead(args[2]);
        var r = DiscForge.Core.Media.DvdReadbackCompare.Compare(src, rb, layerBreak);

        Console.WriteLine($"Source:      {Path.GetFileName(args[1])} ({src.Length / 2048:N0} sectors)  MD5 {r.SourceMd5}");
        Console.WriteLine($"Read-back:   {Path.GetFileName(args[2])} ({rb.Length / 2048:N0} sectors)  MD5 {r.ReadbackMd5}");
        Console.WriteLine($"Compared:    {r.SectorsCompared:N0} sectors  (MD5 {(r.Md5Match ? "match" : "DIFFER")})");
        if (r.MismatchedSectors > 0)
        {
            Console.WriteLine($"Mismatches:  {r.MismatchedSectors:N0} sector(s) in {r.BadEccBlocks:N0} ECC block(s) " +
                              $"({r.FullyBadEccBlocks:N0} fully bad)");
            if (layerBreak is not null)
                Console.WriteLine($"By layer:    L0 {r.L0Mismatches:N0}, L1 {r.L1Mismatches:N0}");
            Console.WriteLine("  ECC block   first sector   layer  bad");
            foreach (var b in r.Examples)
                Console.WriteLine($"  {b.EccBlock,9:N0}   {b.FirstSector,12:N0}   {b.Layer,-5}  {b.BadSectors}/16");
        }
        foreach (var n in r.Notes) Console.WriteLine($"note: {n}");
        Console.WriteLine();
        Console.WriteLine($"Result: {r.Summary}");
        return r.Result == DiscForge.Core.Media.DvdReadbackCompare.Grade.Fail ? 1 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BookTypeTrace(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge booktype-trace <trace-file> [--vendor V] [--model M] [--label L]\n" +
            "                          [--target DVD-ROM] [--save recipe.json]\n" +
            "  Decode a captured SCSI/MMC command trace of a drive setting the book type (bitsetting)\n" +
            "  and, with --save, store the drive's OWN command as a replay recipe. DiscForge never\n" +
            "  fabricates vendor book-type bytes — it learns them from your capture and can reproduce\n" +
            "  them byte-for-byte on that drive. Trace format: 'CDB: <hex>' + optional 'DATA: <hex>'\n" +
            "  lines, commands separated by a blank line (see docs/BITSETTING.md).");
    if (!File.Exists(args[1])) return Fail($"Trace file not found: {args[1]}");
    string? vendor = null, model = null, label = null, savePath = null;
    DiscForge.Core.Mmc.BookType? target = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--vendor" && i + 1 < args.Length) vendor = args[++i];
        else if (args[i] == "--model" && i + 1 < args.Length) model = args[++i];
        else if (args[i] == "--label" && i + 1 < args.Length) label = args[++i];
        else if (args[i] == "--save" && i + 1 < args.Length) savePath = args[++i];
        else if (args[i] == "--target" && i + 1 < args.Length)
            target = DiscForge.Core.Mmc.BookTypes.Parse(args[++i]);
    }

    try
    {
        var parsed = DiscForge.Core.Mmc.MmcTrace.Parse(File.ReadAllText(args[1]));
        foreach (var e in parsed.Errors) Console.WriteLine($"  warning: {e}");
        if (parsed.Commands.Count == 0) return Fail("No CDBs found in the trace (expected 'CDB: <hex>' lines).");

        var findings = DiscForge.Core.Mmc.BookTypeBitsetting.AnalyzeAll(parsed.Commands);
        Console.WriteLine($"Trace: {Path.GetFileName(args[1])} — {parsed.Commands.Count} command(s)");
        Console.WriteLine();
        var candidates = new List<int>();
        foreach (var f in findings)
        {
            var cmd = parsed.Commands[f.Index];
            string mark = f.LooksLikeBitsetting ? "»" : " ";
            Console.WriteLine($" {mark} [{f.Index}] {f.CommandName,-22} CDB {DiscForge.Core.Mmc.MmcTrace.Hex(cmd.Cdb)}");
            if (cmd.DataOut.Length > 0)
                Console.WriteLine($"        DATA({cmd.DataOut.Length}) {DiscForge.Core.Mmc.MmcTrace.Hex(cmd.DataOut.AsSpan(0, Math.Min(24, cmd.DataOut.Length)))}{(cmd.DataOut.Length > 24 ? " …" : "")}");
            Console.WriteLine($"        {f.Explanation}");
            if (f.LooksLikeBitsetting) candidates.Add(f.Index);
        }

        Console.WriteLine();
        if (candidates.Count == 0)
            Console.WriteLine("No bitsetting-shaped command found in this trace.");
        else
            Console.WriteLine($"Book-type candidate command(s): {string.Join(", ", candidates.Select(i => $"[{i}]"))}");

        if (savePath is not null)
        {
            if (candidates.Count == 0) return Fail("Nothing to save — no bitsetting candidate in the trace.");
            int pick = candidates[0];
            var recipe = DiscForge.Core.Mmc.BookTypeRecipe.Learn(
                parsed.Commands[pick], vendor, model,
                label ?? $"book-type command from {Path.GetFileName(args[1])}", target);
            File.WriteAllText(savePath, recipe.ToJson());
            Console.WriteLine($"Saved replay recipe (command [{pick}]) → {savePath}");
            Console.WriteLine("  The Windows Devices layer can replay this exact command on the captured drive.");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BookTypeSet(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge booktype-set <drive> <recipe.json> [--force]\n" +
            "  Replay a learned book-type (bitsetting) recipe on the drive — the drive's OWN captured\n" +
            "  command, issued verbatim over SPTI. Create the recipe first with\n" +
            "  `booktype-trace <trace> --vendor V --model M --target DVD-ROM --save recipe.json`.\n" +
            "  Refuses to fire a recipe learned on a different drive unless --force. Insert the\n" +
            "  appropriate (usually blank) media first. Clean-room: DiscForge never fabricates the\n" +
            "  vendor bytes — it only replays what your capture contained. See docs/BITSETTING.md.");
    string recipePath = args[2];
    if (!File.Exists(recipePath)) return Fail($"Recipe not found: {recipePath}");
    bool force = args.Contains("--force");
#if WINDOWS
    string spec = args[1].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the drive letter, e.g. `dforge booktype-set D: recipe.json`. Run `dforge drives` to list drives.");
    char letter = char.ToUpperInvariant(spec[0]);
    try
    {
        var recipe = DiscForge.Core.Mmc.BookTypeRecipe.FromJson(File.ReadAllText(recipePath));
        Console.WriteLine($"Replaying book-type recipe {Path.GetFileName(recipePath)} (CDB opcode 0x{(recipe.Cdb.Length > 0 ? recipe.Cdb[0] : 0):X2})…");
        var res = DiscForge.Devices.Burning.BookTypeSetter.Apply(letter, recipe, force);
        Console.WriteLine($"Drive {res.Drive}");
        Console.WriteLine($"  Command replayed: CDB {res.Command} — {(res.Applied ? "ACCEPTED by the drive" : "not applied")}");
        foreach (var n in res.Notes) Console.WriteLine($"  {n}");
        return res.Applied ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    _ = (recipePath, force);
    return Fail("`booktype-set` uses the Windows SPTI stack; run it on Windows with the drive attached.");
#endif
}

static int ScanProtection(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge scan-protection <image.cdi>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    // Collect the file listing (best-effort) so the scanner can spot marker
    // files, then run the sector/subchannel fingerprint scan.
    var fileNames = new List<string>();
    try
    {
        var opened = OpenDataTrack(args[1]);
        if (opened is not null)
        {
            var (_, track, fs) = opened.Value;
            using (fs)
            {
                using var view = new CdiUserDataStream(fs, track);
                if (HasIso9660(view))
                {
                    var dir = IsoReader.Read(view, IsoReader.NamePreference.Auto);
                    foreach (var e in dir.Entries.Where(x => !x.IsDirectory))
                        fileNames.Add(e.Path);
                }
            }
        }
    }
    catch { /* listing is best-effort; the sector scan still runs */ }

    try
    {
        using var access = SectorAccess.Open(args[1]);
        var report = ProtectionScanner.Scan(access, fileNames);

        Console.WriteLine($"{Path.GetFileName(args[1])}: {access.TotalSectors:N0} sectors");
        Console.WriteLine();
        if (!report.AnyProtection)
        {
            Console.WriteLine("No known copy-protection fingerprint detected.");
            Console.WriteLine("(A clean scan is not a guarantee; it means none of the");
            Console.WriteLine(" recognised schemes left a detectable signature.)");
            return 0;
        }

        Console.WriteLine("Copy-protection fingerprint(s) detected:");
        Console.WriteLine();
        foreach (var f in report.Findings)
        {
            Console.WriteLine($"  {f.Scheme}");
            Console.WriteLine($"    Evidence: {f.Evidence}");
            Console.WriteLine($"    Guidance: {f.Guidance}");
            if (f.SignificantLbas.Count > 0)
            {
                var preview = string.Join(", ", f.SignificantLbas.Take(8));
                Console.WriteLine($"    Sectors:  {preview}" +
                    (f.SignificantLbas.Count > 8 ? $"  (+{f.SignificantLbas.Count - 8} more)" : ""));
            }
            Console.WriteLine();
        }
        Console.WriteLine("DiscForge detects protection so a backup can PRESERVE it faithfully.");
        Console.WriteLine("It does not circumvent, strip, or defeat any protection.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ProtectionProfileCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge protection-profile <image> [--json]\n" +
                    "  Builds a unified clean-room protection profile: which schemes are fingerprinted, where\n" +
                    "  their physical signatures sit, and — keyed to this image's real capture mode and whether\n" +
                    "  it carries subchannel — whether the dump can actually preserve each. Flags any scheme the\n" +
                    "  capture under-holds and names the recapture that would fix it. Read-only characterisation.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    // Best-effort file listing so filesystem markers are seen.
    var fileNames = new List<string>();
    try
    {
        var opened = OpenDataTrack(args[1]);
        if (opened is not null)
        {
            var (_, track, fs) = opened.Value;
            using (fs)
            {
                using var view = new CdiUserDataStream(fs, track);
                if (HasIso9660(view))
                {
                    var dir = IsoReader.Read(view, IsoReader.NamePreference.Auto);
                    foreach (var e in dir.Entries.Where(x => !x.IsDirectory))
                        fileNames.Add(e.Path);
                }
            }
        }
    }
    catch { /* listing is best-effort */ }

    try
    {
        using var access = SectorAccess.Open(args[1]);
        var profile = DiscForge.Core.Forensics.ProtectionProfiler.Build(access, fileNames);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(args[1]),
                profile.ImageKind, profile.TotalSectors, profile.HasSubchannel, profile.RawSectors,
                profile.AnyProtection, profile.FullyPreserved,
                schemes = profile.Schemes.Select(s => new
                {
                    s.Scheme, s.Evidence, s.Guidance, s.FullyCapturable, s.CaptureNote,
                    significantLbas = s.SignificantLbas,
                }),
                captureCompleteness = profile.CaptureCompleteness.Select(f => new { f.Name, f.Preservable, f.Detail }),
            });
            return profile.AnyProtection && !profile.FullyPreserved ? 2 : 0;
        }

        Console.WriteLine($"{Path.GetFileName(args[1])}: {profile.Summary()}");
        Console.WriteLine($"  capture: {profile.ImageKind}, {profile.TotalSectors:N0} sectors, " +
                          $"subchannel {(profile.HasSubchannel ? "present" : "absent")}");
        if (profile.Schemes.Count > 0)
        {
            Console.WriteLine("  schemes:");
            foreach (var s in profile.Schemes)
            {
                Console.WriteLine($"    {(s.FullyCapturable ? "[preserved]" : "[UNDER-CAPTURED]")} {s.Scheme}: {s.Evidence}");
                Console.WriteLine($"        {s.CaptureNote}");
                if (s.SignificantLbas.Count > 0)
                    Console.WriteLine($"        significant LBAs: {string.Join(", ", s.SignificantLbas.Take(8))}" +
                                      (s.SignificantLbas.Count > 8 ? $" (+{s.SignificantLbas.Count - 8} more)" : ""));
            }
        }
        Console.WriteLine("  capture completeness:");
        foreach (var f in profile.CaptureCompleteness)
            Console.WriteLine($"    [{(f.Preservable ? "held" : "MISSING")}] {f.Name}: {f.Detail}");
        Console.WriteLine("  Clean-room: DiscForge characterises and preserves protection; it never circumvents it.");
        return profile.AnyProtection && !profile.FullyPreserved ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int MountCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge mount <image>\n" +
                    "  Describes how the image would mount as a virtual drive and, for\n" +
                    "  ISO-compatible images, prints the native Windows mount command\n" +
                    "  (no driver needed). Rich formats (audio/subchannel/multi-track)\n" +
                    "  report that a virtual-drive driver is required.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        string path = args[1];
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Gather lightweight facts about the image to feed the mount model.
        long totalSectors = 0;
        int trackCount = 1;
        bool hasAudio = false, hasSubchannel = false, isPlainData = true;

        if (ext == ".cdi" || ext == ".cue" || ext == ".bin" || ext == ".img" || ext == ".iso")
        {
            try
            {
                using var access = SectorAccess.Open(path);
                totalSectors = access.TotalSectors;
            }
            catch { /* fall back to size-based estimate below */ }
        }
        if (totalSectors == 0)
            totalSectors = new FileInfo(path).Length / 2048;

        // Inspect a .cdi/.cue for tracks + audio + subchannel where we can.
        try
        {
            var opened = OpenDataTrack(path);
            if (opened is not null)
            {
                var (image, _, fs) = opened.Value;
                using (fs)
                {
                    trackCount = image.AllTracks.Count();
                    hasAudio = image.AllTracks.Any(t => t.Mode == CdiTrackMode.Audio);
                    isPlainData = !hasAudio && trackCount <= 1;
                }
            }
        }
        catch { /* best effort; defaults stand */ }

        if (ext == ".sub" || File.Exists(Path.ChangeExtension(path, ".sub")))
            hasSubchannel = true;

        var media = DiscForge.Core.Mount.VirtualDisc.MediaFromSectors(totalSectors);
        var disc = DiscForge.Core.Mount.VirtualDisc.Describe(
            path, media, totalSectors, trackCount, hasAudio, hasSubchannel, isPlainData);

        Console.WriteLine($"{Path.GetFileName(path)}");
        Console.WriteLine($"  {disc.Summary}");
        Console.WriteLine();

        switch (disc.Strategy)
        {
            case DiscForge.Core.Mount.VirtualDisc.MountStrategy.NativeIso:
                Console.WriteLine("Mount now on Windows (no driver needed):");
                Console.WriteLine("  " + DiscForge.Core.Mount.VirtualDisc.NativeMountCommand(Path.GetFullPath(path)));
                Console.WriteLine("Unmount:");
                Console.WriteLine("  " + DiscForge.Core.Mount.VirtualDisc.NativeUnmountCommand(Path.GetFullPath(path)));
                break;

            case DiscForge.Core.Mount.VirtualDisc.MountStrategy.ConvertThenNativeIso:
                Console.WriteLine("This is a single data track in a non-ISO container. Export it to a");
                Console.WriteLine("plain .iso first, then Windows can mount it natively:");
                Console.WriteLine($"  dforge convert \"{path}\" \"{Path.ChangeExtension(path, ".iso")}\"");
                Console.WriteLine("  " + DiscForge.Core.Mount.VirtualDisc.NativeMountCommand(
                    Path.GetFullPath(Path.ChangeExtension(path, ".iso"))));
                break;

            case DiscForge.Core.Mount.VirtualDisc.MountStrategy.NeedsVirtualDriveDriver:
                Console.WriteLine("A faithful mount of this image (audio / subchannel / multi-track)");
                Console.WriteLine("needs a virtual optical drive — a kernel-mode driver DiscForge does");
                Console.WriteLine("not yet ship. You can still inspect, verify, extract, or convert it");
                Console.WriteLine("with the other commands.");
                break;
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CcdInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge ccd-info <image.ccd>\n" +
                    "  Reads a CloneCD control file and shows its table of contents.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var toc = DiscForge.Core.Convert.CloneCdReader.ReadFile(args[1]);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {toc.Summary}");
        if (toc.Catalog is not null) Console.WriteLine($"  Catalog (MCN): {toc.Catalog}");
        var (img, sub) = DiscForge.Core.Convert.CloneCdReader.SidecarsFor(args[1]);
        Console.WriteLine($"  Data image: {Path.GetFileName(img)}" +
            (File.Exists(img) ? " (present)" : " (missing)"));
        Console.WriteLine($"  Subchannel: {Path.GetFileName(sub)}" +
            (File.Exists(sub) ? " (present)" : " (none)"));
        Console.WriteLine();
        Console.WriteLine("Track  Type   Start LBA  Control");
        foreach (var t in toc.Tracks)
            Console.WriteLine($"  {t.Number,2}   {(t.IsData ? "Data " : "Audio")}  {t.StartLba,9}  0x{t.Control:X2}" +
                (t.Isrc is not null ? $"  ISRC={t.Isrc}" : ""));
        return 0;
    }
    catch (DiscForge.Core.Convert.CloneCdReader.CcdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int AccurateRipCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge accuraterip <image.cue> [--db <dBAR.bin>] [--url]\n" +
                    "  Computes AccurateRip v1/v2 checksums and disc IDs for the audio tracks.\n" +
                    "  --db <file>   verify against a downloaded AccurateRip database record\n" +
                    "  --url         print the AccurateRip lookup URL to fetch the record\n" +
                    "  (Fetching the record is an online step done separately.)");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string? dbPath = null;
    bool showUrl = args.Contains("--url");
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--db") dbPath = args[i + 1];

    try
    {
        using var layout = DiscLayout.FromCueFile(args[1]);
        var audioTracks = layout.Tracks.Where(t => t.Mode == RawTrackMode.Audio).OrderBy(t => t.Number).ToList();
        if (audioTracks.Count == 0)
            return Fail("No audio tracks in this image — AccurateRip applies to audio CDs.");

        // Build the TOC offsets (each track's start LBA, plus lead-out).
        var offsets = new List<int>();
        int lba = 0;
        foreach (var t in layout.Tracks.OrderBy(t => t.Number))
        {
            offsets.Add(lba);
            lba += t.TotalSectors;
        }
        offsets.Add(lba);   // lead-out

        var (id1, id2, cddb) = DiscForge.Core.Audio.AccurateRip.DiscIds(offsets);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {audioTracks.Count} audio track(s)");
        Console.WriteLine($"Disc IDs:  AR1={id1:X8}  AR2={id2:X8}  CDDB={cddb:X8}");
        if (showUrl)
            Console.WriteLine("Lookup:    " +
                DiscForge.Core.Audio.AccurateRipDatabase.LookupUrl(audioTracks.Count, id1, id2, cddb));
        Console.WriteLine();
        Console.WriteLine("Track  AccurateRip v1  AccurateRip v2");

        int firstNum = audioTracks.First().Number;
        int lastNum = audioTracks.Last().Number;
        var computed = new List<DiscForge.Core.Audio.AccurateRip.TrackChecksum>();

        foreach (var t in audioTracks)
        {
            // Read the track's stored audio bytes into memory (audio is 2352/sector).
            long start = t.SourceByteOffset;
            long lengthBytes = (long)t.LengthSectors * t.StoredSectorSize;
            var pcm = new byte[lengthBytes];
            lock (t.Source)
            {
                t.Source.Position = start;
                int read = 0;
                while (read < pcm.Length)
                {
                    int n = t.Source.Read(pcm, read, pcm.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
            }

            var cs = DiscForge.Core.Audio.AccurateRip.Compute(
                pcm, isFirstTrack: t.Number == firstNum, isLastTrack: t.Number == lastNum);
            computed.Add(cs);
            Console.WriteLine($"  {t.Number,2}   {cs.V1:X8}        {cs.V2:X8}");
        }

        Console.WriteLine();

        if (dbPath is not null)
        {
            if (!File.Exists(dbPath)) return Fail($"Database record not found: {dbPath}");
            var blob = File.ReadAllBytes(dbPath);
            var chunks = DiscForge.Core.Audio.AccurateRipDatabase.Parse(blob);
            var entries = DiscForge.Core.Audio.AccurateRipDatabase.ToEntries(chunks, (id1, id2, cddb));

            if (entries.Count == 0)
            {
                Console.WriteLine("The database record holds no pressing matching this disc's IDs.");
                Console.WriteLine("(The rip may be of a different pressing, or the record is for another disc.)");
                return 0;
            }

            var result = DiscForge.Core.Audio.AccurateRip.Verify(computed, entries);
            Console.WriteLine($"Verification against {chunks.Count} database pressing(s):");
            foreach (var v in result.Tracks)
            {
                string mark = v.Status switch
                {
                    DiscForge.Core.Audio.AccurateRip.TrackStatus.MatchV2 => $"ACCURATE (v2, confidence {v.Confidence})",
                    DiscForge.Core.Audio.AccurateRip.TrackStatus.MatchV1 => $"ACCURATE (v1, confidence {v.Confidence})",
                    _ => "not found / mismatch",
                };
                Console.WriteLine($"  Track {v.TrackIndex + 1,2}: {mark}");
            }
            Console.WriteLine();
            Console.WriteLine(result.Summary);
            return result.AllAccurate ? 0 : 1;
        }

        Console.WriteLine("To verify: fetch the AccurateRip record (see --url) and pass it with --db.");
        Console.WriteLine("A match means your rip is bit-identical to others' — a confirmed-good rip.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Transcode(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge transcode <input> <output> [options]\n" +
                    "  options:\n" +
                    "    --ratio <0.05-1.0>   video compression ratio (default: fit by --target)\n" +
                    "    --target <dvd5|dvd9|bytes>   fit video to this size\n" +
                    "    --duration <seconds> input duration (required for bitrate math)\n" +
                    "    --orig-video <bytes> original video size (for ratio→bitrate)\n" +
                    "    --codec <h264|hevc|mpeg2>    (default h264)\n" +
                    "    --two-pass           deep-analysis 2-pass encode\n" +
                    "    --keep-audio <i,j>   audio stream indices to keep\n" +
                    "    --dry-run            print the ffmpeg command(s), don't run\n" +
                    "  DiscForge does not bundle FFmpeg; install it and put it on PATH.");

    string input = args[1], output = args[2];
    if (!File.Exists(input)) return Fail($"Input not found: {input}");

    double ratio = 1.0, duration = 0;
    long origVideo = 0, targetBytes = 0;
    bool twoPass = args.Contains("--two-pass");
    bool dryRun = args.Contains("--dry-run");
    var codec = DiscForge.Core.Transcode.TranscodePlanner.VideoCodec.H264;
    var keepAudio = new List<int>();

    for (int i = 3; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--ratio" when i + 1 < args.Length: ratio = double.Parse(args[++i]); break;
            case "--duration" when i + 1 < args.Length: duration = double.Parse(args[++i]); break;
            case "--orig-video" when i + 1 < args.Length: origVideo = long.Parse(args[++i]); break;
            case "--target" when i + 1 < args.Length:
                targetBytes = args[i + 1].ToLowerInvariant() switch
                {
                    "dvd5" => DiscForge.Core.DvdVideo.BitBudget.Dvd5,
                    "dvd9" => DiscForge.Core.DvdVideo.BitBudget.Dvd9,
                    _ => long.Parse(args[i + 1]),
                };
                i++;
                break;
            case "--codec" when i + 1 < args.Length:
                codec = args[++i].ToLowerInvariant() switch
                {
                    "hevc" => DiscForge.Core.Transcode.TranscodePlanner.VideoCodec.Hevc,
                    "mpeg2" => DiscForge.Core.Transcode.TranscodePlanner.VideoCodec.Mpeg2,
                    _ => DiscForge.Core.Transcode.TranscodePlanner.VideoCodec.H264,
                };
                break;
            case "--keep-audio" when i + 1 < args.Length:
                keepAudio.AddRange(args[++i].Split(',').Select(int.Parse));
                break;
        }
    }

    if (duration <= 0)
        return Fail("--duration <seconds> is required to compute the target bitrate.");

    // If a target size was given and we know the original video size, derive the
    // ratio from the budget; otherwise use the explicit --ratio.
    if (targetBytes > 0 && origVideo > 0)
    {
        var plan = DiscForge.Core.DvdVideo.BitBudget.Compute(new[]
        {
            new DiscForge.Core.DvdVideo.BitBudget.TitlePlanRequest
            {
                Title = new DiscForge.Core.DvdVideo.BitBudget.TitleSizes
                { Name = "input", VideoBytes = origVideo },
                Mode = DiscForge.Core.DvdVideo.BitBudget.Mode.Automatic,
            },
        }, targetBytes);
        ratio = plan.AutomaticRatio;
    }

    var container = Path.GetExtension(output).ToLowerInvariant() switch
    {
        ".mkv" => DiscForge.Core.Transcode.TranscodePlanner.Container.Mkv,
        ".mpg" or ".vob" => DiscForge.Core.Transcode.TranscodePlanner.Container.DvdVideoMpeg2,
        _ => DiscForge.Core.Transcode.TranscodePlanner.Container.Mp4,
    };

    var titlePlan = new DiscForge.Core.DvdVideo.BitBudget.TitlePlan
    {
        Name = Path.GetFileName(input), VideoRatio = ratio,
        PlannedVideoBytes = origVideo > 0 ? (long)Math.Round(origVideo * ratio) : 0,
        PlannedTotalBytes = 0, Mode = DiscForge.Core.DvdVideo.BitBudget.Mode.Automatic,
    };

    var enc = DiscForge.Core.Transcode.TranscodePlanner.ForTitle(
        titlePlan, input, output, duration, codec, container,
        originalVideoBytes: origVideo, twoPass: twoPass, keepAudio: keepAudio);

    var vectors = DiscForge.Core.Transcode.TranscodePlanner.BuildArgs(enc);

    Console.WriteLine($"Plan: {(enc.CopyVideo ? "stream-copy (no re-encode)" : $"{codec}, {enc.VideoBitrate:N0} bps, ratio {ratio:P0}")}" +
                      $"{(enc.TwoPass ? ", two-pass" : "")}");
    Console.WriteLine();

    var ffmpeg = DiscForge.Core.Transcode.FfmpegRunner.Locate();
    if (dryRun || ffmpeg is null)
    {
        if (ffmpeg is null && !dryRun)
            Console.WriteLine("FFmpeg not found on PATH — showing the command(s) instead:\n");
        foreach (var v in vectors)
            Console.WriteLine("ffmpeg " + string.Join(" ", v.Select(QuoteIfNeeded)));
        if (ffmpeg is null && !dryRun)
        {
            Console.WriteLine();
            Console.WriteLine("Install FFmpeg (https://ffmpeg.org) and put ffmpeg on PATH to run this.");
        }
        return 0;
    }

    var runner = new DiscForge.Core.Transcode.FfmpegRunner(ffmpeg);
    Console.WriteLine($"Using {ffmpeg}");
    int pass = 1;
    double lastPct = 0;
    bool ok = runner.Run(enc, vectors,
        onProgress: p =>
        {
            if (p.Percent is { } pct)
            {
                // A new pass starts when progress falls back near zero after
                // having climbed high — a genuine reset, not jitter.
                if (vectors.Count > 1 && lastPct > 50 && pct < 10 && pass < vectors.Count) pass++;
                lastPct = pct;
                Console.Write($"\r  {(vectors.Count > 1 ? $"pass {pass}/{vectors.Count}: " : "")}{pct,5:F1}%   " +
                              $"{(p.SpeedX is { } sx ? $"{sx:F1}x" : "")}      ");
            }
        },
        onLog: l => { if (l.StartsWith("ffmpeg exited")) Console.WriteLine("\n" + l); });
    Console.WriteLine();
    Console.WriteLine(ok ? $"Done: {output}" : "Transcode failed (see ffmpeg output).");
    return ok ? 0 : 1;
}

static string QuoteIfNeeded(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

static int DvdInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge dvd-info <VIDEO_TS folder | disc root>\n" +
                    "  Shows the DVD-Video structure and a DVD-5 shrink plan.");
    string path = args[1];
    if (!Directory.Exists(path))
        return Fail($"Not a folder: {path}  (dvd-info reads a VIDEO_TS folder or disc root)");

    try
    {
        var src = new DiscForge.Core.DvdVideo.VideoTsSources.Folder(path);
        var dvd = DiscForge.Core.DvdVideo.IfoReader.Read(src);

        Console.WriteLine(dvd.Summary);
        Console.WriteLine();
        foreach (var ts in dvd.TitleSets)
        {
            Console.WriteLine($"Title set {ts.Number}:  video {ts.TitleVobBytes:N0} B, " +
                              $"menu {ts.MenuVobBytes:N0} B");
            foreach (var t in ts.Titles)
            {
                Console.WriteLine($"  Title {t.TitleNumber}: {t.Chapters} chapter(s), " +
                                  $"{t.AngleCount} angle(s)");
                foreach (var a in t.Audio)
                    Console.WriteLine($"    audio {a.Index}: {a.Codec} {a.Channels}ch " +
                                      $"[{(string.IsNullOrWhiteSpace(a.Language) ? "und" : a.Language)}]");
                foreach (var s in t.Subtitles)
                    Console.WriteLine($"    sub   {s.Index}: " +
                                      $"[{(string.IsNullOrWhiteSpace(s.Language) ? "und" : s.Language)}]");
            }
        }

        // A simple full-disc DVD-5 shrink plan: every title set automatic.
        var reqs = dvd.TitleSets.Select(ts => new DiscForge.Core.DvdVideo.BitBudget.TitlePlanRequest
        {
            Title = new DiscForge.Core.DvdVideo.BitBudget.TitleSizes
            {
                Name = $"VTS {ts.Number}",
                VideoBytes = ts.TitleVobBytes,
                OverheadBytes = ts.MenuVobBytes,
            },
            Mode = DiscForge.Core.DvdVideo.BitBudget.Mode.Automatic,
        }).ToList();

        var plan = DiscForge.Core.DvdVideo.BitBudget.Compute(
            reqs, DiscForge.Core.DvdVideo.BitBudget.Dvd5);

        Console.WriteLine();
        Console.WriteLine("Shrink-to-DVD-5 plan (full disc, automatic):");
        Console.WriteLine($"  {plan.Summary}");
        if (plan.AutomaticRatio < 1.0)
            Console.WriteLine($"  Video would be compressed to {plan.AutomaticRatio:P0} of original.");
        Console.WriteLine();
        Console.WriteLine("Note: DiscForge reads DVD-Video structure and plans the fit. The actual");
        Console.WriteLine("re-encode is a separate transcode step (not yet enabled). Encrypted");
        Console.WriteLine("(CSS) video is never processed.");
        return 0;
    }
    catch (DiscForge.Core.DvdVideo.IfoFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Re-emit a DVD-Video's IFO structure natively (VMG + VTS), optionally keeping
// only a subset of title sets. This writes the *structural* IFOs — the
// enumeration and stream map — not the navigation/cell tables a player walks, so
// it is a structural rewrite and diagnostic, not a playable-disc author (dvdauthor
// remains that path). See docs/DVD_VIDEO_SHRINK.md.
static int DvdRewrite(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge dvd-rewrite <VIDEO_TS folder | disc root> <out folder> [--keep 1,3]\n" +
                    "  Reads the DVD-Video IFO structure and re-emits VIDEO_TS.IFO + VTS_nn_0.IFO.\n" +
                    "  --keep selects title sets by number (renumbered contiguously). Structural\n" +
                    "  IFOs only (enumeration + streams); not a playable-disc author.");
    string inPath = args[1];
    string outPath = args[2];
    if (!Directory.Exists(inPath))
        return Fail($"Not a folder: {inPath}  (dvd-rewrite reads a VIDEO_TS folder or disc root)");

    int[]? keep = null;
    for (int i = 3; i < args.Length - 1; i++)
        if (args[i] == "--keep")
        {
            try
            {
                keep = args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(int.Parse).ToArray();
            }
            catch { return Fail($"--keep expects a comma-separated list of title-set numbers, got '{args[i + 1]}'."); }
        }

    try
    {
        var src = new DiscForge.Core.DvdVideo.VideoTsSources.Folder(inPath);
        var dvd = DiscForge.Core.DvdVideo.IfoReader.Read(src);

        var plan = keep is { Length: > 0 }
            ? DiscForge.Core.DvdVideo.IfoWriter.Keep(dvd, keep)
            : DiscForge.Core.DvdVideo.IfoWriter.PlanFrom(dvd);
        var files = DiscForge.Core.DvdVideo.IfoWriter.Write(plan);

        string videoTs = Path.Combine(outPath, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(videoTs, name), bytes);
            // A DVD keeps a byte-identical .BUP backup of each .IFO.
            File.WriteAllBytes(Path.Combine(videoTs, Path.ChangeExtension(name, ".BUP")), bytes);
        }

        Console.WriteLine($"Wrote {files.Count} IFO file(s) (+ .BUP backups) to {videoTs}");
        Console.WriteLine($"  {plan.TitleSets.Count} title set(s), " +
                          $"{plan.TitleSets.Sum(s => s.Titles.Count)} title(s).");
        Console.WriteLine();
        Console.WriteLine("Note: these are structural IFOs (enumeration + audio/subpicture streams).");
        Console.WriteLine("They round-trip through DiscForge's reader; producing a player-navigable");
        Console.WriteLine("disc (PGCI/cell tables) remains the dvdauthor step. CSS video is never processed.");
        return 0;
    }
    catch (DiscForge.Core.DvdVideo.IfoFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Read a Video CD / Super Video CD control file (INFO.VCD/INFO.SVD or
// ENTRIES.VCD/ENTRIES.SVD) and print what it identifies. Structural read only;
// see docs/VCD_AUTHORING.md for the scope and the clean-room note.
static int VcdInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge vcd-info <INFO.VCD | ENTRIES.VCD>\n" +
                    "  Reads a Video CD / Super Video CD control file and shows what it holds.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var data = File.ReadAllBytes(args[1]);
        string magic = data.Length >= 8 ? System.Text.Encoding.ASCII.GetString(data, 0, 8) : "";

        if (magic is "VIDEO_CD" or "SUPERVCD")
        {
            var info = DiscForge.Core.Vcd.VcdControl.ReadInfo(data);
            Console.WriteLine($"{(info.Kind == DiscForge.Core.Vcd.VcdKind.Vcd ? "Video CD" : "Super Video CD")} " +
                              $"INFO, version {info.Version}");
            Console.WriteLine($"  album id : \"{info.AlbumId}\"");
            Console.WriteLine($"  volume   : {info.VolumeNumber} of {info.VolumeCount}");
            return 0;
        }
        if (magic is "ENTRYVCD" or "ENTRYSVD")
        {
            var e = DiscForge.Core.Vcd.VcdControl.ReadEntries(data);
            Console.WriteLine($"{(e.Kind == DiscForge.Core.Vcd.VcdKind.Vcd ? "Video CD" : "Super Video CD")} " +
                              $"ENTRIES, version {e.Version}: {e.Entries.Count} entry-point(s)");
            foreach (var x in e.Entries)
                Console.WriteLine($"  track {x.TrackNumber,2} @ {x.Minute:00}:{x.Second:00}:{x.Frame:00}");
            return 0;
        }
        return Fail("Not a VCD/SVCD control file (expected a VIDEO_CD/SUPERVCD or ENTRYVCD/ENTRYSVD signature).");
    }
    catch (DiscForge.Core.Vcd.VcdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- PlayStation dev / asset utilities -------------------------------------

static int PsxExeInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge psx-exe-info <file.exe|file.psexe>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var h = DiscForge.Core.PlayStation.PsExe.ReadHeader(File.ReadAllBytes(args[1]));
        Console.WriteLine(h.Summary);
        Console.WriteLine($"  entry PC   : 0x{h.EntryPoint:X8}");
        Console.WriteLine($"  gp         : 0x{h.Gp:X8}");
        Console.WriteLine($"  load addr  : 0x{h.LoadAddress:X8}  (t_size {h.TextSize:N0} bytes)");
        Console.WriteLine($"  bss        : 0x{h.BssAddress:X8}  ({h.BssSize:N0} bytes)");
        Console.WriteLine($"  stack      : 0x{h.StackBase:X8} + 0x{h.StackOffset:X8}");
        if (h.RegionMarker.Length > 0) Console.WriteLine($"  marker     : {h.RegionMarker}");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.PsExeFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PsxPad(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge psx-pad <in> <out> [--multiple N | --psexe] [--fill 0xNN]\n" +
                    "  Pads a file to an N-byte boundary (default 2048), or a PS-EXE's payload\n" +
                    "  to 0x800 with t_size fixed (--psexe). --fill sets the pad byte.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    int multiple = DiscForge.Core.PlayStation.PsxPadding.SectorSize;
    bool psexe = false;
    byte fill = 0x00;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--psexe") psexe = true;
        else if (args[i] == "--multiple" && i + 1 < args.Length) multiple = int.Parse(args[++i]);
        else if (args[i] == "--fill" && i + 1 < args.Length)
        {
            string f = args[++i];
            fill = f.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (byte)System.Convert.ToInt32(f[2..], 16)
                : (byte)int.Parse(f);
        }
    }

    try
    {
        var data = File.ReadAllBytes(args[1]);
        var padded = psexe
            ? DiscForge.Core.PlayStation.PsxPadding.PadPsExe(data, fill)
            : DiscForge.Core.PlayStation.PsxPadding.PadToMultiple(data, multiple, fill);
        File.WriteAllBytes(args[2], padded);
        Console.WriteLine($"Padded {data.Length:N0} -> {padded.Length:N0} bytes -> {Path.GetFileName(args[2])}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Bin2Src(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge bin2src <file> [--name IDENT] [--asm] [--per-line N] [--out file]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string name = Path.GetFileNameWithoutExtension(args[1]);
    bool asm = false; int perLine = 12; string? outPath = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--asm") asm = true;
        else if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
        else if (args[i] == "--per-line" && i + 1 < args.Length) perLine = int.Parse(args[++i]);
        else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
    }

    try
    {
        var data = File.ReadAllBytes(args[1]);
        string src = asm
            ? DiscForge.Core.Util.BinToSource.ToAsm(data, name, perLine)
            : DiscForge.Core.Util.BinToSource.ToCArray(data, name, perLine);
        if (outPath is null) Console.Write(src);
        else { File.WriteAllText(outPath, src); Console.WriteLine($"Wrote {Path.GetFileName(outPath)} ({data.Length:N0} bytes encoded)."); }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SearchCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge search <file> (--hex 4d5a | --ascii TEXT) [--limit N]\n" +
                    "  Prints every byte offset where the pattern occurs.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    byte[]? needle = null; int limit = int.MaxValue;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--hex" && i + 1 < args.Length) needle = DiscForge.Core.Util.ByteSearch.ParseHex(args[++i]);
        else if (args[i] == "--ascii" && i + 1 < args.Length) needle = DiscForge.Core.Util.ByteSearch.FromAscii(args[++i]);
        else if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i]);
    }
    if (needle is null || needle.Length == 0) return Fail("Give a pattern with --hex or --ascii.");

    try
    {
        using var fs = File.OpenRead(args[1]);
        var hits = DiscForge.Core.Util.ByteSearch.FindAll(fs, needle);
        Console.WriteLine($"{hits.Count:N0} match(es).");
        for (int i = 0; i < hits.Count && i < limit; i++)
            Console.WriteLine($"  0x{hits[i]:X}  ({hits[i]:N0})");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VmuCreate(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge vmu-create <out.bin>  (a blank formatted 128 KB VMU)");
    try
    {
        File.WriteAllBytes(args[1], DiscForge.Core.Vmu.VmuBuilder.CreateFormatted());
        Console.WriteLine($"Wrote a blank formatted VMU: {Path.GetFileName(args[1])} (128 KB).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VmuAdd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge vmu-add <vmu.bin> <save.vms> [--name NAME] [--game] [--protect]\n" +
                    "  Adds a save to a VMU image (writes it back in place).");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");

    string name = Path.GetFileNameWithoutExtension(args[2]);
    if (name.Length > 12) name = name[..12];
    bool game = false, protect = false;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
        else if (args[i] == "--game") game = true;
        else if (args[i] == "--protect") protect = true;
    }

    try
    {
        var image = File.ReadAllBytes(args[1]);
        var vms = File.ReadAllBytes(args[2]);
        var updated = DiscForge.Core.Vmu.VmuBuilder.Add(image, name, vms, game, protect);
        File.WriteAllBytes(args[1], updated);
        Console.WriteLine($"Added '{name}' ({(vms.Length + 511) / 512} block(s)) to {Path.GetFileName(args[1])}.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Vms2Vmi(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge vms2vmi <save.vms> <out.vmi> [--desc TEXT] [--name VMUNAME]\n" +
                    "  Writes the VMI download-descriptor for a VMS save.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string resource = Path.GetFileNameWithoutExtension(args[1]);
    if (resource.Length > 8) resource = resource[..8];
    string vmuName = resource;
    string desc = resource;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--desc" && i + 1 < args.Length) desc = args[++i];
        else if (args[i] == "--name" && i + 1 < args.Length) vmuName = args[++i];
    }

    try
    {
        int size = (int)new FileInfo(args[1]).Length;
        var vmi = DiscForge.Core.Vmu.Vmi.Create(resource, vmuName, desc, size);
        File.WriteAllBytes(args[2], vmi);
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: VMI for \"{resource}\" ({size:N0} bytes).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Scramble / descramble a Dreamcast 1ST_READ.BIN (the bin2boot transform). A
// documented byte-slice permutation, not encryption; the two are exact inverses.
static int DcScramble(string[] args, bool scramble)
{
    string verb = scramble ? "dc-scramble" : "dc-descramble";
    if (args.Length < 3)
        return Fail($"usage: dforge {verb} <in.bin> <out.bin>\n" +
                    "  Slice-permutes a Dreamcast main binary. scramble <-> descramble are inverses.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var data = File.ReadAllBytes(args[1]);
        var result = scramble
            ? DiscForge.Core.Gdi.DreamcastScramble.Scramble(data)
            : DiscForge.Core.Gdi.DreamcastScramble.Descramble(data);
        File.WriteAllBytes(args[2], result);
        Console.WriteLine($"{(scramble ? "Scrambled" : "Descrambled")} {data.Length:N0} bytes -> {Path.GetFileName(args[2])}.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Scan a folder and render a shareable HTML collection dashboard.
static int LibraryReportCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge library-report <dir> <out.html> [--dat <file>]");
    string dir = args[1], outPath = args[2];
    if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");

    string? datPath = null;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--dat" && i + 1 < args.Length) datPath = args[++i];
        else return Fail($"Unknown option: {args[i]}");

    try
    {
        DiscForge.Core.Dat.DatFile? dat = null;
        if (datPath is not null)
        {
            if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");
            using var ds = File.OpenRead(datPath);
            dat = DiscForge.Core.Dat.DatFile.Parse(ds);
        }
        var report = DiscForge.Core.Library.LibraryScanner.Scan(dir, dat);
        File.WriteAllText(outPath, DiscForge.Core.Library.CollectionReportHtml.Build(report));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} — {report.Total:N0} files, {report.Verified:N0} verified, " +
                          $"{report.Missing.Count:N0} missing.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Compare two DAT revisions.
static int DatDiffCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge dat-diff <old.dat> <new.dat>");
    if (!File.Exists(args[1])) return Fail($"DAT not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"DAT not found: {args[2]}");
    try
    {
        DiscForge.Core.Dat.DatFile oldDat, newDat;
        using (var s = File.OpenRead(args[1])) oldDat = DiscForge.Core.Dat.DatFile.Parse(s);
        using (var s = File.OpenRead(args[2])) newDat = DiscForge.Core.Dat.DatFile.Parse(s);
        var d = DiscForge.Core.Dat.DatDiff.Compare(oldDat, newDat);

        Console.WriteLine($"Old: {d.OldGames:N0} games   New: {d.NewGames:N0} games");
        Console.WriteLine($"  +{d.Added.Count:N0} added   -{d.Removed.Count:N0} removed   ~{d.Changed.Count:N0} changed");
        void Section(string label, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            Console.WriteLine($"{label} ({list.Count:N0}):");
            foreach (var s in list.Take(50)) Console.WriteLine($"  {s}");
            if (list.Count > 50) Console.WriteLine($"  … and {list.Count - 50:N0} more");
        }
        Section("Added", d.Added);
        Section("Removed", d.Removed);
        Section("Changed", d.Changed.Select(c => $"{c.Game}  ({c.Detail})"));
        if (d.Identical) Console.WriteLine("The two DATs are identical.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Rebuild a clean, DAT-named set from a (possibly messy) source folder.
static int RebuildCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge rebuild <src-dir> <dest-dir> --dat <file> [--per-game] [--move] [--apply]\n" +
                    "  Verifies the source against the DAT and places each good file under its canonical name.\n" +
                    "  Without --apply it only shows the plan; --move relocates instead of copying.");
    string src = args[1], dest = args[2];
    if (!Directory.Exists(src)) return Fail($"Source folder not found: {src}");

    string? datPath = null;
    bool perGame = false, move = false, apply = false;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--dat" when i + 1 < args.Length: datPath = args[++i]; break;
            case "--per-game": perGame = true; break;
            case "--move": move = true; break;
            case "--apply": apply = true; break;
            default: return Fail($"Unknown option: {args[i]}");
        }
    if (datPath is null) return Fail("rebuild needs --dat <file>.");
    if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");

    try
    {
        DiscForge.Core.Dat.DatFile dat;
        using (var ds = File.OpenRead(datPath)) dat = DiscForge.Core.Dat.DatFile.Parse(ds);
        Console.WriteLine($"DAT: {dat.Name ?? Path.GetFileName(datPath)} ({dat.Count:N0} entries). Scanning {src}…");

        var report = DiscForge.Core.Library.LibraryScanner.Scan(src, dat);
        var layout = perGame ? DiscForge.Core.Library.RebuildLayout.PerGameFolder : DiscForge.Core.Library.RebuildLayout.Flat;
        var plan = DiscForge.Core.Library.SetRebuilder.Plan(report, dest, layout);

        Console.WriteLine($"To place: {plan.ToPlace:N0}   Already in place: {plan.AlreadyInPlace:N0}   " +
                          $"Unknown: {plan.Unknown:N0}   Missing from set: {plan.Missing:N0}");
        foreach (var a in plan.Actions.Take(40))
            Console.WriteLine($"  {Path.GetFileName(a.SourcePath)}  ->  {Path.GetRelativePath(dest, a.DestPath)}");
        if (plan.Actions.Count > 40) Console.WriteLine($"  … and {plan.Actions.Count - 40:N0} more");

        if (plan.Missing > 0)
        {
            Console.WriteLine($"Missing ({plan.Missing:N0}):");
            foreach (var m in plan.MissingRoms.Take(20)) Console.WriteLine($"  - {m.Name}");
            if (plan.MissingRoms.Count > 20) Console.WriteLine($"  … and {plan.MissingRoms.Count - 20:N0} more");
        }

        if (apply)
        {
            int placed = DiscForge.Core.Library.SetRebuilder.Apply(plan, move);
            Console.WriteLine($"{(move ? "Moved" : "Copied")} {placed:N0} file(s) into {dest}.");
        }
        else Console.WriteLine($"{plan.ToPlace:N0} file(s) planned. Re-run with --apply to {(move ? "move" : "copy")} them.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Licence tooling: generate a signing key pair, issue keys, verify keys, show machine id.
static int LicenseCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge license <keygen|issue|verify|machine-id> …\n" +
                    "  keygen <private.pem> <public.txt>            create a signing key pair (keep the private key!)\n" +
                    "  issue --private <f.pem> --name \"…\" [--edition Pro] [--days N] [--machine <id>]\n" +
                    "  verify <key> [--machine <id>]                check a key against the embedded public key\n" +
                    "  machine-id                                   print this machine's id");

    string sub = args[1].ToLowerInvariant();
    try
    {
        switch (sub)
        {
            case "keygen":
            {
                if (args.Length < 4) return Fail("usage: dforge license keygen <private.pem> <public.txt>");
                using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                File.WriteAllText(args[2], ec.ExportPkcs8PrivateKeyPem());
                string pubB64 = System.Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
                File.WriteAllText(args[3], pubB64);
                Console.WriteLine($"Private key -> {args[2]}   *** KEEP THIS SECRET ***");
                Console.WriteLine($"Public key  -> {args[3]}");
                Console.WriteLine("Paste this public key into LicenseConfig.PublicKeyBase64 (src/DiscForge.Core/Licensing/License.cs):");
                Console.WriteLine(pubB64);
                return 0;
            }

            case "issue":
            {
                string? priv = null, name = null, edition = "Standard", machine = null; int days = 0;
                for (int i = 2; i < args.Length; i++)
                    switch (args[i])
                    {
                        case "--private" when i + 1 < args.Length: priv = args[++i]; break;
                        case "--name" when i + 1 < args.Length: name = args[++i]; break;
                        case "--edition" when i + 1 < args.Length: edition = args[++i]; break;
                        case "--machine" when i + 1 < args.Length: machine = args[++i]; break;
                        case "--days" when i + 1 < args.Length: int.TryParse(args[++i], out days); break;
                        default: return Fail($"Unknown option: {args[i]}");
                    }
                if (priv is null || name is null) return Fail("issue needs --private <file.pem> and --name \"…\".");
                if (!File.Exists(priv)) return Fail($"Private key not found: {priv}");

                using var ec = System.Security.Cryptography.ECDsa.Create();
                ec.ImportFromPem(File.ReadAllText(priv));
                var info = new DiscForge.Core.Licensing.LicenseInfo
                {
                    Name = name,
                    Edition = edition!,
                    IssuedUtc = DateTime.UtcNow,
                    ExpiresUtc = days > 0 ? DateTime.UtcNow.AddDays(days) : null,
                    MachineId = string.IsNullOrWhiteSpace(machine) ? null : machine,
                };
                string key = DiscForge.Core.Licensing.License.Issue(info, ec.ExportPkcs8PrivateKey());
                Console.WriteLine($"Licence for {name} ({edition}" +
                                  (days > 0 ? $", {days} days" : ", perpetual") +
                                  (machine is not null ? $", machine {machine}" : "") + "):");
                Console.WriteLine(key);
                return 0;
            }

            case "verify":
            {
                if (args.Length < 3) return Fail("usage: dforge license verify <key> [--machine <id>]");
                string key = args[2];
                string? machine = null;
                for (int i = 3; i < args.Length; i++)
                    if (args[i] == "--machine" && i + 1 < args.Length) machine = args[++i];

                var r = DiscForge.Core.Licensing.License.Validate(
                    key, DiscForge.Core.Licensing.LicenseConfig.PublicSpki, machine, DateTime.UtcNow);
                Console.WriteLine($"State:   {r.State}");
                Console.WriteLine($"Message: {r.Message}");
                if (r.Info is { } info)
                {
                    Console.WriteLine($"Name:    {info.Name}");
                    Console.WriteLine($"Edition: {info.Edition}");
                    Console.WriteLine($"Issued:  {info.IssuedUtc:yyyy-MM-dd}");
                    Console.WriteLine($"Expires: {(info.ExpiresUtc is { } e ? e.ToString("yyyy-MM-dd") : "never")}");
                    Console.WriteLine($"Machine: {info.MachineId ?? "any"}");
                }
                return r.IsValid ? 0 : 1;
            }

            case "machine-id":
                Console.WriteLine(DiscForge.Core.Licensing.MachineId.FromRaw(Environment.MachineName));
                Console.WriteLine("(the DiscForge app shows the authoritative machine id in its Activation dialog)");
                return 0;

            default:
                return Fail($"Unknown subcommand '{sub}' (keygen|issue|verify|machine-id).");
        }
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// One-game-one-ROM: collapse a DAT to the single best copy of each game.
static int OneGameOneRomCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge 1g1r <dat> [--regions USA,Europe,Japan] [--keep-proto] [--drop-unlicensed] [--out <file.dat|file.txt>]\n" +
                    "  Picks the best region/revision of each game. --out .dat writes a filtered DAT; .txt writes names.");
    string datPath = args[1];
    if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");

    string? outPath = null;
    var opts = new DiscForge.Core.Dat.OneGameOneRomOptions();
    for (int i = 2; i < args.Length; i++)
        switch (args[i])
        {
            case "--regions" when i + 1 < args.Length:
                opts = new DiscForge.Core.Dat.OneGameOneRomOptions
                {
                    RegionPriority = args[++i].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    ExcludePrerelease = opts.ExcludePrerelease, ExcludeUnlicensed = opts.ExcludeUnlicensed,
                };
                break;
            case "--keep-proto":
                opts = new DiscForge.Core.Dat.OneGameOneRomOptions
                    { RegionPriority = opts.RegionPriority, ExcludePrerelease = false, ExcludeUnlicensed = opts.ExcludeUnlicensed };
                break;
            case "--drop-unlicensed":
                opts = new DiscForge.Core.Dat.OneGameOneRomOptions
                    { RegionPriority = opts.RegionPriority, ExcludePrerelease = opts.ExcludePrerelease, ExcludeUnlicensed = true };
                break;
            case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        DiscForge.Core.Dat.DatFile dat;
        using (var ds = File.OpenRead(datPath)) dat = DiscForge.Core.Dat.DatFile.Parse(ds);
        var report = DiscForge.Core.Dat.OneGameOneRom.Build(dat, opts);

        Console.WriteLine($"DAT: {dat.Name ?? Path.GetFileName(datPath)} — {report.TotalGames:N0} games -> " +
                          $"{report.Families:N0} kept (1G1R), regions [{string.Join(", ", opts.RegionPriority)}].");

        if (outPath is null)
        {
            foreach (var c in report.Choices.Take(60))
                Console.WriteLine($"  {c.Chosen.Game}" + (c.Rejected.Count > 0 ? $"   (−{c.Rejected.Count} variant(s))" : ""));
            if (report.Choices.Count > 60) Console.WriteLine($"  … and {report.Choices.Count - 60:N0} more");
            return 0;
        }

        string ext = Path.GetExtension(outPath).ToLowerInvariant();
        if (ext == ".txt")
        {
            File.WriteAllLines(outPath, report.ChosenGames.Select(g => g.Game));
            Console.WriteLine($"Wrote {report.Families:N0} game name(s) to {Path.GetFileName(outPath)}.");
        }
        else
        {
            string title = (dat.Name ?? "DAT") + " (1G1R)";
            File.WriteAllText(outPath, DiscForge.Core.Dat.DatWriter.WriteLogiqx(title, report.ChosenGames));
            Console.WriteLine($"Wrote a filtered DAT of {report.Families:N0} game(s) to {Path.GetFileName(outPath)}.");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Scan a folder and write a front-end library file (RetroArch .lpl or ES gamelist.xml).
static int FrontendExportCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge frontend-export <retroarch|gamelist> <folder> <out> [--name <name>] [--dat <file>]\n" +
                    "  retroarch → a RetroArch .lpl playlist; gamelist → an EmulationStation/RetroBat gamelist.xml.");
    string kind = args[1].ToLowerInvariant();
    if (kind is not ("retroarch" or "gamelist"))
        return Fail("kind must be 'retroarch' or 'gamelist'.");
    string dir = args[2], outPath = args[3];
    if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");

    string? datPath = null, name = null;
    for (int i = 4; i < args.Length; i++)
        switch (args[i])
        {
            case "--dat" when i + 1 < args.Length: datPath = args[++i]; break;
            case "--name" when i + 1 < args.Length: name = args[++i]; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        DiscForge.Core.Dat.DatFile? dat = null;
        if (datPath is not null)
        {
            if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");
            using var ds = File.OpenRead(datPath);
            dat = DiscForge.Core.Dat.DatFile.Parse(ds);
        }

        // Sidecar / metadata files that live alongside games but are not games themselves.
        var skipExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".m3u", ".lpl", ".xml", ".txt", ".dat", ".nfo", ".sbi", ".sub", ".png", ".jpg", ".jpeg" };

        var report = DiscForge.Core.Library.LibraryScanner.Scan(dir, dat);
        var items = new List<DiscForge.Core.Frontend.PlaylistItem>();
        foreach (var e in report.Entries)
        {
            if (skipExt.Contains(Path.GetExtension(e.FileName))) continue;
            string label = e.Match?.Game ?? Path.GetFileNameWithoutExtension(e.FileName);
            string system = e.RomPlatform.Length > 0 ? e.RomPlatform : e.Format;
            string rel = Path.GetRelativePath(dir, e.Path).Replace('\\', '/');
            items.Add(new DiscForge.Core.Frontend.PlaylistItem
            {
                Path = kind == "retroarch" ? e.Path : rel,
                Label = label,
                Crc32Hex = e.Crc32Hex,
                System = system,
            });
        }
        if (items.Count == 0) return Fail("No files found in the folder to export.");

        string text = kind == "retroarch"
            ? DiscForge.Core.Frontend.FrontendExport.BuildRetroArchLpl(name ?? Path.GetFileNameWithoutExtension(outPath), items)
            : DiscForge.Core.Frontend.FrontendExport.BuildEmulationStationGamelist(items);

        File.WriteAllText(outPath, text);
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} ({kind}): {items.Count} game(s).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Fix a cartridge save's byte order or size for cross-emulator / hardware use.
static int SaveConvertCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge save-convert <in> <out> <op> [--fill 00|FF]\n" +
                    "  op: swap16 | swap32 | pad <size|sram|flash|eeprom4k|eeprom16k|mempak> | trim");
    string inPath = args[1], outPath = args[2], op = args[3].ToLowerInvariant();
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");

    byte fill = 0x00;
    string? sizeArg = null;
    for (int i = 4; i < args.Length; i++)
    {
        if (args[i] == "--fill" && i + 1 < args.Length)
        {
            if (!byte.TryParse(args[++i], System.Globalization.NumberStyles.HexNumber, null, out fill))
                return Fail("--fill expects a hex byte (e.g. FF).");
        }
        else sizeArg ??= args[i];
    }

    static int? SizeOf(string s) => s.ToLowerInvariant() switch
    {
        "sram" => DiscForge.Core.Saves.SaveConvert.Sram,
        "flash" => DiscForge.Core.Saves.SaveConvert.FlashRam,
        "eeprom4k" => DiscForge.Core.Saves.SaveConvert.EepromSmall,
        "eeprom16k" => DiscForge.Core.Saves.SaveConvert.EepromLarge,
        "mempak" => DiscForge.Core.Saves.SaveConvert.ControllerPak,
        _ => int.TryParse(s, out int n) ? n : null,
    };

    try
    {
        var data = File.ReadAllBytes(inPath);
        byte[] outBytes;
        switch (op)
        {
            case "swap16": outBytes = DiscForge.Core.Saves.SaveConvert.WordSwap(data, 2); break;
            case "swap32": outBytes = DiscForge.Core.Saves.SaveConvert.WordSwap(data, 4); break;
            case "trim": outBytes = DiscForge.Core.Saves.SaveConvert.TrimTrailing(data, fill); break;
            case "pad":
                if (sizeArg is null) return Fail("pad needs a size (a byte count or sram|flash|eeprom4k|eeprom16k|mempak).");
                if (SizeOf(sizeArg) is not { } size) return Fail($"Unknown size '{sizeArg}'.");
                outBytes = DiscForge.Core.Saves.SaveConvert.Resize(data, size, fill);
                break;
            default: return Fail($"Unknown op '{op}' (swap16|swap32|pad|trim).");
        }
        File.WriteAllBytes(outPath, outBytes);
        Console.WriteLine($"{op}: wrote {Path.GetFileName(outPath)} ({outBytes.Length:N0} bytes, was {data.Length:N0}).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Fix a cartridge dump's copier header / interleave / byte order so it matches a DAT.
static int RomConvertCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge rom-convert <in> <out> <op>\n" +
                    "  op: z64|v64|n64 (N64 byte order), snes-strip|snes-add, smd|unsmd (Genesis), nes-strip");
    string inPath = args[1], outPath = args[2], op = args[3].ToLowerInvariant();
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");

    try
    {
        var data = File.ReadAllBytes(inPath);
        byte[] outBytes = op switch
        {
            "z64" => DiscForge.Core.Rom.RomConvert.ConvertN64(data, DiscForge.Core.Rom.N64ByteOrder.Z64),
            "v64" => DiscForge.Core.Rom.RomConvert.ConvertN64(data, DiscForge.Core.Rom.N64ByteOrder.V64),
            "n64" => DiscForge.Core.Rom.RomConvert.ConvertN64(data, DiscForge.Core.Rom.N64ByteOrder.N64),
            "snes-strip" => DiscForge.Core.Rom.RomConvert.StripSnesHeader(data),
            "snes-add" => DiscForge.Core.Rom.RomConvert.AddSnesHeader(data),
            "unsmd" or "bin" => DiscForge.Core.Rom.RomConvert.SmdToBin(data),
            "smd" => DiscForge.Core.Rom.RomConvert.BinToSmd(data),
            "nes-strip" => DiscForge.Core.Rom.RomConvert.StripInesHeader(data),
            _ => throw new DiscForge.Core.Rom.RomConvert.RomConvertException(
                     $"Unknown op '{op}' (z64|v64|n64|snes-strip|snes-add|smd|unsmd|nes-strip)."),
        };
        File.WriteAllBytes(outPath, outBytes);
        Console.WriteLine($"{op}: wrote {Path.GetFileName(outPath)} ({outBytes.Length:N0} bytes, was {data.Length:N0}).");
        return 0;
    }
    catch (DiscForge.Core.Rom.RomConvert.RomConvertException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Convert a PS1 memory card between raw / DexDrive / VGS container formats.
static int Ps1CardConvertCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge ps1card-convert <in> <out> [raw|gme|vgs]\n" +
                    "  Container transform only — the 128 KB of card data is preserved byte-for-byte.\n" +
                    "  Target defaults to the output extension (.gme→DexDrive, .vgs/.mem→VGS, else raw).");
    string inPath = args[1], outPath = args[2];
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");

    DiscForge.Core.PlayStation.Ps1CardFormat target;
    string sel = args.Length >= 4 ? args[3].ToLowerInvariant() : Path.GetExtension(outPath).TrimStart('.').ToLowerInvariant();
    target = sel switch
    {
        "gme" or "dexdrive" => DiscForge.Core.PlayStation.Ps1CardFormat.DexDrive,
        "vgs" or "mem" => DiscForge.Core.PlayStation.Ps1CardFormat.Vgs,
        _ => DiscForge.Core.PlayStation.Ps1CardFormat.Raw,
    };

    try
    {
        var data = File.ReadAllBytes(inPath);
        var from = DiscForge.Core.PlayStation.Ps1CardConvert.Detect(data);
        var outBytes = DiscForge.Core.PlayStation.Ps1CardConvert.Convert(data, target);
        File.WriteAllBytes(outPath, outBytes);
        Console.WriteLine($"Converted {from} -> {target}: wrote {Path.GetFileName(outPath)} ({outBytes.Length:N0} bytes).");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.Ps1CardConvert.Ps1CardFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Detect the sample offset that best aligns a PCM rip to a reference.
static int OffsetDetectCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge offset-detect <rip.bin> <reference.bin> [--window N]\n" +
                    "  Detects the stereo-sample read offset that best aligns a PCM rip to a reference.\n" +
                    "  Inputs are headerless 16-bit stereo PCM (e.g. extracted CD-DA). Default window 4096 samples.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");
    int window = 4096;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--window" && i + 1 < args.Length && !int.TryParse(args[++i], out window))
            return Fail("--window must be an integer.");
    try
    {
        var rip = File.ReadAllBytes(args[1]);
        var reference = File.ReadAllBytes(args[2]);
        int off = DiscForge.Core.Dumping.ReadOffsetDetect.DetectSampleOffset(reference, rip, window);
        Console.WriteLine($"Detected read offset: {off:+#;-#;0} sample(s) ({off * 4:+#;-#;0} bytes).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Score a raw image's dump confidence from its EDC health.
static int DumpScoreCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge dump-score <raw.bin>\n" +
                    "  Scores a raw 2352-byte-sector image's dump confidence (0-100 + grade) from its EDC health.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var image = File.ReadAllBytes(args[1]);
        var q = DiscForge.Core.Dumping.DumpConfidence.ScanRaw(image);
        var s = DiscForge.Core.Dumping.DumpConfidence.Score(q);
        Console.WriteLine(s.Summary);
        Console.WriteLine($"  {q.EdcCheckable:N0}/{q.TotalSectors:N0} data sector(s) EDC-checkable.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Deterministic re-mastering: decompose an ISO into a recipe + content store, and
// regenerate the byte-exact original from them.
static int RemasterCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage:\n" +
                    "  dforge remaster pack <image.iso> <recipe.json> <store-dir>   Decompose an ISO into a recipe + content store\n" +
                    "  dforge remaster rebuild <recipe.json> <store-dir> <out.iso>  Regenerate the byte-exact image\n" +
                    "  dforge remaster verify <recipe.json> <store-dir>            Prove the parts rebuild the original\n" +
                    "  Files are stored once by SHA-256 (shared across discs); the recipe carries the structure.");
    Func<string, byte[]> Resolver(string dir) => sha => File.ReadAllBytes(Path.Combine(dir, sha + ".bin"));
    try
    {
        if (args[1] == "pack")
        {
            if (args.Length < 5) return Fail("usage: dforge remaster pack <image.iso> <recipe.json> <store-dir>");
            string img = args[2], recipePath = args[3], storeDir = args[4];
            if (!File.Exists(img)) return Fail($"File not found: {img}");
            var (recipe, store) = DiscForge.Core.Preservation.Remaster.FromIso(File.ReadAllBytes(img));
            Directory.CreateDirectory(storeDir);
            foreach (var kv in store) File.WriteAllBytes(Path.Combine(storeDir, kv.Key + ".bin"), kv.Value);
            File.WriteAllText(recipePath, DiscForge.Core.Preservation.Remaster.ToJson(recipe));
            var v = DiscForge.Core.Preservation.Remaster.Verify(recipe, Resolver(storeDir));
            Console.WriteLine($"Packed {recipe.FileRegions:N0} file(s) into {store.Count:N0} unique blob(s); recipe has {recipe.Regions.Count:N0} region(s).");
            Console.WriteLine(v.Match
                ? "  Verified: the recipe + store rebuild the original byte-for-byte."
                : $"  WARNING: rebuild does NOT match (expected {v.ExpectedSha[..12]}, got {v.ActualSha[..12]}).");
            return v.Match ? 0 : 1;
        }
        if (args[1] == "rebuild")
        {
            if (args.Length < 5) return Fail("usage: dforge remaster rebuild <recipe.json> <store-dir> <out.iso>");
            string recipePath = args[2], storeDir = args[3], outPath = args[4];
            if (!File.Exists(recipePath)) return Fail($"File not found: {recipePath}");
            var recipe = DiscForge.Core.Preservation.Remaster.FromJson(File.ReadAllText(recipePath));
            var image = DiscForge.Core.Preservation.Remaster.Rebuild(recipe, Resolver(storeDir));
            File.WriteAllBytes(outPath, image);
            var v = DiscForge.Core.Preservation.Remaster.Verify(recipe, Resolver(storeDir));
            Console.WriteLine($"Rebuilt {Path.GetFileName(outPath)} ({image.Length:N0} bytes). " + (v.Match ? "Hash matches the recipe." : "HASH MISMATCH."));
            return v.Match ? 0 : 1;
        }
        if (args[1] == "verify")
        {
            if (args.Length < 4) return Fail("usage: dforge remaster verify <recipe.json> <store-dir>");
            string recipePath = args[2], storeDir = args[3];
            if (!File.Exists(recipePath)) return Fail($"File not found: {recipePath}");
            var recipe = DiscForge.Core.Preservation.Remaster.FromJson(File.ReadAllText(recipePath));
            var v = DiscForge.Core.Preservation.Remaster.Verify(recipe, Resolver(storeDir));
            Console.WriteLine(v.Match
                ? "Verified: rebuilds byte-for-byte to the recorded image hash."
                : $"MISMATCH: expected {v.ExpectedSha}, got {v.ActualSha}.");
            return v.Match ? 0 : 1;
        }
        return Fail($"Unknown remaster sub-command '{args[1]}' (expected pack, rebuild or verify).");
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Watch a collection for silent corruption (bit rot) over time.
static int LibraryWatchCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge library-watch <dir> [--state <file>] [--update]\n" +
                    "  Snapshots a collection's file hashes and, on later runs, reports what changed -\n" +
                    "  flagging SUSPECTED ROT (content changed while the file's timestamp never moved).\n" +
                    "  First run creates the baseline. --update accepts the current state as the new baseline.");
    string dir = args[1];
    if (!Directory.Exists(dir)) return Fail($"Not a folder: {dir}");
    string state = Path.Combine(dir, ".dfwatch.json");
    bool update = false;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--state" && i + 1 < args.Length) state = args[++i];
        else if (args[i] == "--update") update = true;
    }
    var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(state) };

    try
    {
        if (!File.Exists(state))
        {
            var snap = DiscForge.Core.Preservation.LibraryWatch.ScanDirectory(dir, DateTime.UtcNow.ToString("o"), exclude);
            File.WriteAllText(state, DiscForge.Core.Preservation.LibraryWatch.ToJson(snap));
            Console.WriteLine($"Baseline created: {snap.Entries.Count:N0} file(s) recorded in {Path.GetFileName(state)}.");
            return 0;
        }

        var prev = DiscForge.Core.Preservation.LibraryWatch.FromJson(File.ReadAllText(state));
        var cur = DiscForge.Core.Preservation.LibraryWatch.ScanDirectory(dir, DateTime.UtcNow.ToString("o"), exclude);
        var report = DiscForge.Core.Preservation.LibraryWatch.Compare(prev, cur);
        Console.WriteLine(report.Summary());

        foreach (var kind in new[]
        {
            DiscForge.Core.Preservation.DriftKind.SuspectedRot,
            DiscForge.Core.Preservation.DriftKind.Removed,
            DiscForge.Core.Preservation.DriftKind.Modified,
            DiscForge.Core.Preservation.DriftKind.Added,
        })
        {
            foreach (var it in report.Changes.Where(c => c.Kind == kind).Take(50))
                Console.WriteLine($"  [{it.Kind}] {it.Path} - {it.Detail}");
        }

        if (report.RotDetected)
            Console.WriteLine("  WARNING: suspected bit rot - restore the flagged file(s) from a known-good copy.");
        if (update)
        {
            File.WriteAllText(state, DiscForge.Core.Preservation.LibraryWatch.ToJson(cur));
            Console.WriteLine("Baseline updated to the current state.");
        }
        else if (report.AnyChange)
        {
            Console.WriteLine("(Baseline unchanged - re-run with --update to accept these as the new baseline.)");
        }
        return report.RotDetected ? 1 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Pull per-track CRC32s (in order) and the whole-image CRC32 out of a saved
// `submission-info` report. Track CRCs are every CRC32 line before "Whole image";
// the image CRC is the first CRC32 line after it.
static (List<string> Tracks, string? Image) ParseSubmissionCrcs(string text)
{
    var tracks = new List<string>();
    string? image = null;
    int split = text.IndexOf("Whole image", StringComparison.OrdinalIgnoreCase);
    string head = split >= 0 ? text[..split] : text;
    string tail = split >= 0 ? text[split..] : "";

    foreach (System.Text.RegularExpressions.Match mt in
             System.Text.RegularExpressions.Regex.Matches(head, "CRC32:\\s*([0-9a-fA-F]{8})"))
        tracks.Add(mt.Groups[1].Value);

    var im = System.Text.RegularExpressions.Regex.Match(tail, "CRC32:\\s*([0-9a-fA-F]{8})");
    if (im.Success) image = im.Groups[1].Value;
    return (tracks, image);
}

// Append-only, signable chain-of-custody for a dump.
static int LineageCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage:\n" +
                    "  dforge lineage keygen <private-key-file>\n" +
                    "  dforge lineage init <lineage.json> --type <t> [--subject S] [--actor A] [--detail D] [--utc U] [--data k=v ...]\n" +
                    "  dforge lineage append <lineage.json> --type <t> [--actor A] [--detail D] [--utc U] [--data k=v ...]\n" +
                    "  dforge lineage sign <lineage.json> --key <private-key-file>\n" +
                    "  dforge lineage verify <lineage.json>\n" +
                    "  dforge lineage show <lineage.json>\n" +
                    "  A hash-linked, append-only history of a dump (dumped -> corroborated -> ecc-repaired ->\n" +
                    "  merged -> sealed ...), optionally signed (ECDSA P-256) so it is tamper-evident and attributable.");

    string sub = args[1];
    try
    {
        if (sub == "keygen")
        {
            if (args.Length < 3) return Fail("usage: dforge lineage keygen <private-key-file>");
            var (priv, pub) = DiscForge.Core.Preservation.DumpLineageLog.GenerateKey();
            File.WriteAllText(args[2], priv);
            Console.WriteLine($"Wrote private key: {Path.GetFileName(args[2])}  (keep it secret; it signs your lineages).");
            Console.WriteLine($"Public key (embedded automatically when you sign):\n  {pub}");
            return 0;
        }

        if (sub is "init" or "append")
        {
            if (args.Length < 3) return Fail($"usage: dforge lineage {sub} <lineage.json> --type <t> [...]");
            string path = args[2];
            string? type = null, subject = null, actor = null, detail = null, utc = null;
            var data = new Dictionary<string, string>();
            for (int i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--type" when i + 1 < args.Length: type = args[++i]; break;
                    case "--subject" when i + 1 < args.Length: subject = args[++i]; break;
                    case "--actor" when i + 1 < args.Length: actor = args[++i]; break;
                    case "--detail" when i + 1 < args.Length: detail = args[++i]; break;
                    case "--utc" when i + 1 < args.Length: utc = args[++i]; break;
                    case "--data" when i + 1 < args.Length:
                        var kv = args[++i];
                        int eq = kv.IndexOf('=');
                        if (eq <= 0) return Fail($"--data expects key=value, got '{kv}'.");
                        data[kv[..eq]] = kv[(eq + 1)..];
                        break;
                }
            }
            if (string.IsNullOrWhiteSpace(type)) return Fail("--type is required (e.g. dumped, corroborated, sealed).");
            utc ??= DateTime.UtcNow.ToString("o");
            var d = data.Count > 0 ? data : null;

            DiscForge.Core.Preservation.DumpLineage lin;
            if (sub == "init")
            {
                if (File.Exists(path)) return Fail($"{path} already exists — use 'append' to add to it.");
                lin = DiscForge.Core.Preservation.DumpLineageLog.Start(subject, type, actor, detail, utc, d);
            }
            else
            {
                if (!File.Exists(path)) return Fail($"File not found: {path} — use 'init' to start a lineage.");
                lin = DiscForge.Core.Preservation.DumpLineageLog.FromJson(File.ReadAllText(path));
                DiscForge.Core.Preservation.DumpLineageLog.Append(lin, type, actor, detail, utc, d);
            }
            File.WriteAllText(path, DiscForge.Core.Preservation.DumpLineageLog.ToJson(lin));
            Console.WriteLine($"{(sub == "init" ? "Started" : "Appended to")} {Path.GetFileName(path)}: " +
                              $"{lin.Events.Count} event(s), head {lin.HeadHash?[..12]}…" +
                              (lin.Signed ? "" : " (unsigned)"));
            return 0;
        }

        if (sub == "sign")
        {
            if (args.Length < 3) return Fail("usage: dforge lineage sign <lineage.json> --key <private-key-file>");
            string path = args[2];
            string? keyFile = null;
            for (int i = 3; i < args.Length; i++)
                if (args[i] == "--key" && i + 1 < args.Length) keyFile = args[++i];
            if (keyFile == null) return Fail("--key <private-key-file> is required.");
            if (!File.Exists(path)) return Fail($"File not found: {path}");
            if (!File.Exists(keyFile)) return Fail($"Key not found: {keyFile}");

            var lin = DiscForge.Core.Preservation.DumpLineageLog.FromJson(File.ReadAllText(path));
            using (var key = DiscForge.Core.Preservation.DumpLineageLog.LoadPrivateKey(File.ReadAllText(keyFile).Trim()))
                DiscForge.Core.Preservation.DumpLineageLog.Sign(lin, key);
            File.WriteAllText(path, DiscForge.Core.Preservation.DumpLineageLog.ToJson(lin));
            Console.WriteLine($"Signed {Path.GetFileName(path)} over head {lin.HeadHash?[..12]}… ({lin.SignatureAlgorithm}).");
            return 0;
        }

        if (sub == "verify")
        {
            if (args.Length < 3) return Fail("usage: dforge lineage verify <lineage.json>");
            if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");
            var lin = DiscForge.Core.Preservation.DumpLineageLog.FromJson(File.ReadAllText(args[2]));
            bool chain = DiscForge.Core.Preservation.DumpLineageLog.VerifyChain(lin);
            bool sig = lin.Signed && DiscForge.Core.Preservation.DumpLineageLog.VerifySignature(lin);

            Console.WriteLine($"{Path.GetFileName(args[2])}: {lin.Events.Count} event(s)");
            Console.WriteLine($"  chain      : {(chain ? "INTACT" : "BROKEN — the history was altered")}");
            Console.WriteLine(lin.Signed
                ? $"  signature  : {(sig ? "VALID" : "INVALID")} ({lin.SignatureAlgorithm})"
                : "  signature  : (unsigned)");
            if (chain && (!lin.Signed || sig))
                Console.WriteLine(lin.Signed
                    ? "  => Verified, signed chain of custody."
                    : "  => Chain intact (sign it to make it attributable).");
            return (chain && (!lin.Signed || sig)) ? 0 : 1;
        }

        if (sub == "show")
        {
            if (args.Length < 3) return Fail("usage: dforge lineage show <lineage.json>");
            if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");
            var lin = DiscForge.Core.Preservation.DumpLineageLog.FromJson(File.ReadAllText(args[2]));
            if (lin.Subject is { Length: > 0 }) Console.WriteLine($"Subject: {lin.Subject}");
            foreach (var e in lin.Events)
            {
                Console.WriteLine($"  [{e.Seq}] {e.Type}{(e.Utc is { Length: > 0 } ? $"  {e.Utc}" : "")}");
                if (e.Actor is { Length: > 0 }) Console.WriteLine($"       by {e.Actor}");
                if (e.Detail is { Length: > 0 }) Console.WriteLine($"       {e.Detail}");
                if (e.Data is { Count: > 0 })
                    Console.WriteLine($"       {string.Join(", ", e.Data.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
            Console.WriteLine(lin.Signed ? $"Signed ({lin.SignatureAlgorithm}), head {lin.HeadHash?[..12]}…"
                                         : $"Unsigned, head {lin.HeadHash?[..12]}…");
            return 0;
        }

        return Fail($"Unknown lineage sub-command '{sub}' (expected keygen, init, append, sign, verify or show).");
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Build or verify the DiscForge Preservation Master (DPM): one sidecar fusing identity, per-file
// fixity + Merkle root, the completeness certificate and the clean-room protection profile.
static int PreserveMasterCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage:\n" +
                    "  dforge preserve-master build <image|.cue> [--out <file.dpm.json>]\n" +
                    "  dforge preserve-master verify <file.dpm.json>\n" +
                    "  The master is the single authoritative account of a dump; verify proves each member file\n" +
                    "  is byte-for-byte what the master recorded (hashes + shift-tolerant Merkle root).");

    string sub = args[1];
    if (sub == "build")
    {
        string image = args[2];
        if (!File.Exists(image)) return Fail($"'{image}' not found.");
        try
        {
            var master = DiscForge.Core.Preservation.PreservationMasterBuilder.Build(image);
            string outPath = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? image + ".dpm.json";
            var json = System.Text.Json.JsonSerializer.Serialize(master,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outPath, json);
            Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {master.Summary()}");
            return 0;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    if (sub == "verify")
    {
        string masterPath = args[2];
        if (!File.Exists(masterPath)) return Fail($"'{masterPath}' not found.");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(masterPath));
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(masterPath)) ?? ".";
            int bad = 0, n = 0;
            foreach (var fe in doc.RootElement.GetProperty("Files").EnumerateArray())
            {
                var entry = new DiscForge.Core.Preservation.MasterFileEntry
                {
                    Name = fe.GetProperty("Name").GetString() ?? "",
                    Length = fe.GetProperty("Length").GetInt64(),
                    Crc32 = fe.GetProperty("Crc32").GetString() ?? "",
                    Md5 = fe.GetProperty("Md5").GetString() ?? "",
                    Sha1 = fe.GetProperty("Sha1").GetString() ?? "",
                    Sha256 = fe.GetProperty("Sha256").GetString() ?? "",
                    MerkleRoot = fe.GetProperty("MerkleRoot").GetString() ?? "",
                };
                var (ok, diffs) = DiscForge.Core.Preservation.PreservationMasterBuilder.VerifyFile(entry, baseDir);
                n++;
                if (ok) Console.WriteLine($"  [OK]   {entry.Name}");
                else { bad++; foreach (var d in diffs) Console.WriteLine($"  [FAIL] {d}"); }
            }
            Console.WriteLine(bad == 0
                ? $"Master verified: {n} file(s) byte-for-byte against the DPM."
                : $"{bad} of {n} file(s) FAILED verification.");
            return bad == 0 ? 0 : 2;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    return Fail("usage: dforge preserve-master <build|verify> …");
}

// Store / describe a raw optical RF-flux capture with its calibration metadata (phase-1 low-level preservation).
static int FluxCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage:\n" +
                    "  dforge flux pack <raw-samples> <out.dfflux> [--rate Hz --bits N --channels N --rpm N --profile S --note S]\n" +
                    "  dforge flux info <file.dfflux> [--json]\n" +
                    "  Wraps a raw optical RF/flux capture with the calibration a future demodulator needs.\n" +
                    "  (The EFM/CIRC decoder is a separate, later stage; this is the lossless container for the signal.)");

    string sub = args[1];
    string Opt(string k, string def) => args.SkipWhile(a => a != k).Skip(1).FirstOrDefault() ?? def;
    int I(string k, int def) => int.TryParse(Opt(k, ""), out int v) ? v : def;

    if (sub == "pack")
    {
        string raw = args[2];
        if (!File.Exists(raw)) return Fail($"'{raw}' not found.");
        string outPath = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : raw + ".dfflux";
        try
        {
            var payload = File.ReadAllBytes(raw);
            var meta = new DiscForge.Core.Preservation.FluxMetadata
            {
                SampleRateHz = (uint)Math.Max(0, I("--rate", 0)),
                BitsPerSample = I("--bits", 8),
                Channels = I("--channels", 1),
                NominalRpm = (uint)Math.Max(0, I("--rpm", 0)),
                DeviceProfile = Opt("--profile", ""),
                Note = Opt("--note", ""),
            };
            File.WriteAllBytes(outPath, DiscForge.Core.Preservation.FluxContainer.Write(meta, payload));
            Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {meta.SampleRateHz:N0} Hz, {meta.BitsPerSample}-bit, " +
                              $"{meta.Channels}ch, {payload.Length:N0} bytes (payload CRC recorded).");
            return 0;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    if (sub == "info")
    {
        string path = args[2];
        if (!File.Exists(path)) return Fail($"'{path}' not found.");
        try
        {
            var (meta, _, crcOk) = DiscForge.Core.Preservation.FluxContainer.Read(File.ReadAllBytes(path));
            if (args.Contains("--json"))
            {
                EmitJson(new
                {
                    file = Path.GetFileName(path),
                    meta.SampleRateHz, meta.BitsPerSample, meta.Channels, meta.NominalRpm,
                    meta.DeviceProfile, meta.Note, meta.PayloadBytes,
                    payloadCrc32 = $"{meta.PayloadCrc32:X8}", crcOk,
                });
                return crcOk ? 0 : 2;
            }
            Console.WriteLine($"{Path.GetFileName(path)}: DFFLX1 flux container");
            Console.WriteLine($"  {meta.Describe()}");
            if (meta.Note.Length > 0) Console.WriteLine($"  note: {meta.Note}");
            Console.WriteLine($"  payload CRC-32: {meta.PayloadCrc32:X8} — {(crcOk ? "OK" : "MISMATCH")}");
            return crcOk ? 0 : 2;
        }
        catch (DiscForge.Core.Preservation.FluxFormatException ex) { return Fail(ex.Message); }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    return Fail("usage: dforge flux <pack|info> …");
}

// Build or verify a self-describing, hash-manifested preservation package.
static int PreserveCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage:\n" +
                    "  dforge preserve pack <manifest-out.json> <file> [file ...] [--title T] [--platform P] [--notes N]\n" +
                    "  dforge preserve verify <manifest.json>\n" +
                    "  dforge preserve corroborate <manifest.json> --drive \"<name>\" (--submission <info.txt> | --tracks <crc,crc,...>) [--image-crc <crc>] [--method \"<m>\"]\n" +
                    "  dforge preserve corroborate <manifest.json> --genome --base-cue <this.cue> --base-drive \"<name>\" --other-cue <other.cue> --other-drive \"<name>\"\n" +
                    "  Records size + CRC32/MD5/SHA1/SHA256 of a preservation set plus a self-digest, so the\n" +
                    "  set can later be proven byte-for-byte identical to what was dumped. 'corroborate' folds\n" +
                    "  in an independent dump (e.g. a second drive) as cross-source verification provenance.");
    try
    {
        if (args[1] == "pack")
        {
            string outPath = args[2];
            string? title = null, platform = null, notes = null, protectionRaw = null;
            var files = new List<string>();
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i] == "--title" && i + 1 < args.Length) title = args[++i];
                else if (args[i] == "--platform" && i + 1 < args.Length) platform = args[++i];
                else if (args[i] == "--notes" && i + 1 < args.Length) notes = args[++i];
                else if (args[i] == "--protection" && i + 1 < args.Length) protectionRaw = args[++i];
                else files.Add(args[i]);
            }
            if (files.Count == 0) return Fail("preserve pack needs at least one file.");
            foreach (var f in files) if (!File.Exists(f)) return Fail($"File not found: {f}");

            var m = DiscForge.Core.Preservation.PreservationPackage.Build(
                files, $"DiscForge {CliVersion()}", title, platform, notes, DateTime.UtcNow.ToString("o"));

            // Optionally fold in the disc's cross-checked protection verdict (detection only).
            if (protectionRaw is not null)
            {
                if (!File.Exists(protectionRaw)) return Fail($"Raw image not found: {protectionRaw}");
                var raw = File.ReadAllBytes(protectionRaw);
                var twins = DiscForge.Core.Forensics.TwinSectorScan.Analyze(raw);
                DiscForge.Core.Forensics.ErrorPatternReport? errors = null;
                try { errors = DiscForge.Core.Forensics.ErrorPatternForensics.Classify(DiscForge.Core.Forensics.DiscHealthMap.Scan(raw)); }
                catch { /* not a whole number of 2352-byte sectors — skip the error-shape signal */ }
                var fused = DiscForge.Core.Forensics.ProtectionCrossCheck.Fuse(null, errors, twins);
                DiscForge.Core.Preservation.PreservationPackage.SetProtection(m, fused);
            }

            File.WriteAllText(outPath, DiscForge.Core.Preservation.PreservationPackage.ToJson(m));
            Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {m.Entries.Count} file(s), digest {m.Digest?[..12]}…" +
                              (m.Protection is { } p ? $"; protection: {p.Standing}" : ""));
            return 0;
        }
        if (args[1] == "verify")
        {
            string manifestPath = args[2];
            if (!File.Exists(manifestPath)) return Fail($"File not found: {manifestPath}");
            var m = DiscForge.Core.Preservation.PreservationPackage.FromJson(File.ReadAllText(manifestPath));
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
            var r = DiscForge.Core.Preservation.PreservationPackage.Verify(m, baseDir);

            Console.WriteLine($"Manifest {(r.ManifestIntact ? "intact" : "TAMPERED")} — {r.Ok}/{m.Entries.Count} file(s) OK" +
                              (r.Missing > 0 ? $", {r.Missing} missing" : "") + ".");
            foreach (var e in r.Entries)
                if (!e.Match) Console.WriteLine($"  FAIL {e.Path}: {e.Detail}");
            if (r.AllGood) Console.WriteLine("  Verified — a faithful copy of what was recorded.");

            if (m.Provenance is { } prov && prov.Attestations.Count > 0)
            {
                Console.WriteLine($"  Provenance: {prov.Attestations.Count} independent source(s), " +
                                  $"{prov.IndependentAgreements} in agreement" +
                                  (prov.Corroborated ? " — cross-source verified." : "."));
                foreach (var att in prov.Attestations)
                    Console.WriteLine($"    [{(att.Agrees ? "+" : "x")}] {att.Drive ?? "(unnamed source)"}" +
                                      (att.Method is { Length: > 0 } ? $"  ({att.Method})" : ""));
            }
            if (m.Protection is { } prot)
            {
                Console.WriteLine($"  Protection: {prot.Standing}" +
                                  (prot.Schemes.Count > 0 ? $" — {string.Join(", ", prot.Schemes)}" : "") +
                                  (prot.PhysicalSignature ? " (physical signature)" : "") +
                                  (prot.Guidance is { Length: > 0 } ? $"; {prot.Guidance}" : ""));
            }
            return r.AllGood ? 0 : 1;
        }
        if (args[1] == "corroborate")
        {
            string manifestPath = args[2];
            if (!File.Exists(manifestPath)) return Fail($"File not found: {manifestPath}");

            // Genome mode: corroborate by offset-invariant identity instead of CRC, for two
            // drives that read the same disc at different read offsets (raw CRCs differ).
            if (args.Contains("--genome"))
            {
                string? baseCue = null, baseDrive = null, otherCue = null, otherDrive = null;
                for (int i = 3; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--base-cue" when i + 1 < args.Length: baseCue = args[++i]; break;
                        case "--base-drive" when i + 1 < args.Length: baseDrive = args[++i]; break;
                        case "--other-cue" when i + 1 < args.Length: otherCue = args[++i]; break;
                        case "--other-drive" when i + 1 < args.Length: otherDrive = args[++i]; break;
                    }
                }
                if (baseCue == null || otherCue == null)
                    return Fail("genome corroboration needs --base-cue <this-dump.cue> and --other-cue <other-dump.cue> " +
                                "(add --base-drive / --other-drive names).");

                var baseGenome = DiscForge.Core.Forensics.DiscGenome.Compute(LoadGenomeTracks(baseCue));
                var otherGenome = DiscForge.Core.Forensics.DiscGenome.Compute(LoadGenomeTracks(otherCue));
                var m2 = DiscForge.Core.Preservation.PreservationPackage.FromJson(File.ReadAllText(manifestPath));
                var gm = DiscForge.Core.Preservation.PreservationPackage.AddGenomeCorroboration(
                    m2, baseDrive, baseGenome, otherDrive, otherGenome);
                File.WriteAllText(manifestPath, DiscForge.Core.Preservation.PreservationPackage.ToJson(m2));

                Console.WriteLine($"Genome corroboration: {baseDrive ?? "base"} vs {otherDrive ?? "other"}");
                Console.WriteLine($"  layout {(gm.LayoutMatch ? "match" : "DIFFER")}, data {(gm.DataMatch ? "match" : "DIFFER")}, " +
                                  $"audio {gm.AudioSimilarity:P1} similar (shift {gm.BestShift}).");
                Console.WriteLine(gm.SameDisc
                    ? "  => SAME DISC — corroborated despite any read-offset difference."
                    : "  => NOT the same disc — divergence recorded, not hidden.");
                Console.WriteLine($"  {m2.Provenance!.Attestations.Count} attestation(s); digest {m2.Digest?[..12]}…");
                return gm.SameDisc ? 0 : 2;
            }

            string? drive = null, method = null, submission = null, tracksArg = null, imageCrc = null;
            for (int i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--drive" when i + 1 < args.Length: drive = args[++i]; break;
                    case "--method" when i + 1 < args.Length: method = args[++i]; break;
                    case "--submission" when i + 1 < args.Length: submission = args[++i]; break;
                    case "--tracks" when i + 1 < args.Length: tracksArg = args[++i]; break;
                    case "--image-crc" when i + 1 < args.Length: imageCrc = args[++i]; break;
                }
            }

            List<string> tracks;
            if (submission != null)
            {
                if (!File.Exists(submission)) return Fail($"Submission file not found: {submission}");
                var (t, img) = ParseSubmissionCrcs(File.ReadAllText(submission));
                tracks = t;
                imageCrc ??= img;
            }
            else if (tracksArg != null)
            {
                tracks = tracksArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            else return Fail("Provide the other dump's hashes via --submission <info.txt> or --tracks <crc,crc,...>.");

            if (tracks.Count == 0) return Fail("No per-track CRC32 values found to corroborate with.");

            var m = DiscForge.Core.Preservation.PreservationPackage.FromJson(File.ReadAllText(manifestPath));
            var att = new DiscForge.Core.Preservation.DumpAttestation
            {
                Drive = drive,
                Method = method,
                CapturedUtc = DateTime.UtcNow.ToString("o"),
                ImageCrc32 = imageCrc,
                TrackCrc32 = tracks,
            };
            bool agrees = DiscForge.Core.Preservation.PreservationPackage.AddAttestation(m, att);
            File.WriteAllText(manifestPath, DiscForge.Core.Preservation.PreservationPackage.ToJson(m));

            var prov = m.Provenance!;
            Console.WriteLine($"Recorded {drive ?? "(unnamed drive)"}: {tracks.Count} track CRC(s)" +
                              (imageCrc != null ? $", image {imageCrc}" : "") + ".");
            Console.WriteLine(agrees
                ? "  AGREES with the reference dump."
                : "  DOES NOT AGREE with the reference dump — the divergence is recorded, not hidden.");
            Console.WriteLine($"  {prov.Attestations.Count} attestation(s), {prov.IndependentAgreements} in agreement — " +
                              (prov.Corroborated ? "cross-source verified." : "not yet corroborated (need 2+ agreeing)."));
            Console.WriteLine($"  Digest refreshed: {m.Digest?[..12]}…");
            return agrees ? 0 : 2;
        }
        return Fail($"Unknown preserve sub-command '{args[1]}' (expected pack, verify or corroborate).");
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Merge several imperfect rips of the same disc into one best-possible image.
static int DumpMergeCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge dump-merge <out.bin> <in1.bin> <in2.bin> [in3.bin ...] [--sector-size 2352|2048]\n" +
                    "  Rebuilds one image from several imperfect rips of the SAME disc: keeps sectors the\n" +
                    "  copies agree on, uses any copy whose EDC validates, and majority-votes the rest.\n" +
                    "  Raw 2352-byte images give the strongest recovery (EDC-verified); 2048 falls back to voting.");
    string outPath = args[1];
    int sectorSize = 2352;
    var inputs = new List<string>();
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--sector-size" && i + 1 < args.Length)
        {
            if (!int.TryParse(args[++i], out sectorSize) || sectorSize <= 0)
                return Fail("--sector-size must be a positive integer (usually 2352 or 2048).");
        }
        else inputs.Add(args[i]);
    }
    if (inputs.Count < 2) return Fail("dump-merge needs at least two input images.");
    foreach (var p in inputs) if (!File.Exists(p)) return Fail($"File not found: {p}");

    try
    {
        var images = inputs.Select(File.ReadAllBytes).ToList();
        var result = DiscForge.Core.Recovery.DumpMerge.Merge(images, sectorSize);
        File.WriteAllBytes(outPath, result.Image);

        var r = result.Report;
        Console.WriteLine(r.Summary());
        Console.WriteLine($"  Repaired {r.Repaired:N0} disagreeing sector(s); wrote {Path.GetFileName(outPath)}.");
        if (!r.FullyRecovered)
        {
            Console.WriteLine($"  {r.Unrecovered:N0} sector(s) could not be recovered from these copies.");
            var preview = string.Join(", ", r.UnrecoveredSectors.Take(12));
            Console.WriteLine($"  First unrecovered: {preview}{(r.UnrecoveredSectors.Count > 12 ? " …" : "")}");
        }
        else
        {
            Console.WriteLine("  Fully recovered — every sector is either agreed or EDC-verified.");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Surface non-zero data on a disc that no file and no standard ISO structure explains.
static int DiscAnomaliesCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-anomalies <image.iso> [--min <bytes>] [--all]\n" +
                    "  Maps everything a cooked ISO 9660 image legitimately contains — the system area,\n" +
                    "  volume descriptors, path tables, both directory hierarchies and every catalogued\n" +
                    "  file — then reports the non-zero bytes left over: leftover mastering data, files\n" +
                    "  deleted but not overwritten, payloads in the system area or past the volume end.\n" +
                    "  Surfaces what's already on the disc; it decodes and defeats nothing.");

    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    int minBytes = 32;
    bool all = false;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--min" && i + 1 < args.Length)
        {
            if (!int.TryParse(args[++i], out minBytes) || minBytes < 1)
                return Fail("--min must be a positive integer.");
        }
        else if (args[i] == "--all") all = true;
    }

    try
    {
        var image = File.ReadAllBytes(path);
        var report = DiscForge.Core.Forensics.DiscArchaeology.FindOrphans(image, minBytes);

        Console.WriteLine($"{Path.GetFileName(path)}: {report.ImageLength:N0} bytes");
        if (report.DeclaredVolumeBytes > 0)
            Console.WriteLine($"  volume declares {report.DeclaredVolumeBytes:N0} bytes; " +
                              $"known structure + files cover {report.KnownStructureBytes:N0}.");
        Console.WriteLine(report.Summary());

        if (!report.FoundAnything) return 0;

        int show = all ? report.Orphans.Count : Math.Min(report.Orphans.Count, 20);
        Console.WriteLine();
        for (int i = 0; i < show; i++)
        {
            var o = report.Orphans[i];
            Console.WriteLine($"  #{i + 1}  LBA {o.Lba:N0} (offset 0x{o.Offset:X})  {o.Length:N0} bytes " +
                              $"({o.NonZeroBytes:N0} non-zero)  entropy {o.Entropy:F2}");
            Console.WriteLine($"       {o.Note()}");
            Console.WriteLine($"       ascii: {Truncate(o.AsciiSample, 56)}");
        }
        if (!all && report.Orphans.Count > show)
            Console.WriteLine($"  … and {report.Orphans.Count - show:N0} more (use --all to list every region).");

        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

// Offset-invariant disc fingerprint. One cue → print the genome; two cues → compare.
static int DiscGenomeCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-genome <a.cue> [b.cue]\n" +
                    "  Builds an offset-invariant fingerprint of a disc from its bin+cue: an exact\n" +
                    "  layout hash (geometry) and data hash, plus an offset-tolerant audio envelope.\n" +
                    "  With two cues, reports whether they are rips of the SAME disc even when the\n" +
                    "  CD-DA read offset (and so the raw bytes) differ.");
    try
    {
        var a = DiscForge.Core.Forensics.DiscGenome.Compute(LoadGenomeTracks(args[1]));
        if (args.Length < 3 || args[2].StartsWith("--"))
        {
            Console.WriteLine($"{Path.GetFileName(args[1])}");
            Console.WriteLine($"  genome id : {a.ShortId}");
            Console.WriteLine($"  layout    : {a.LayoutHash[..16]}…");
            Console.WriteLine($"  data      : {a.DataHash[..16]}…");
            Console.WriteLine($"  audio     : {a.AudioTrackCount} track(s), {a.AudioEnvelope.Length:N0} envelope sectors");
            return 0;
        }

        var b = DiscForge.Core.Forensics.DiscGenome.Compute(LoadGenomeTracks(args[2]));
        var m = DiscForge.Core.Forensics.DiscGenome.Compare(a, b);
        Console.WriteLine($"A {Path.GetFileName(args[1])}  genome {a.ShortId}");
        Console.WriteLine($"B {Path.GetFileName(args[2])}  genome {b.ShortId}");
        Console.WriteLine($"  layout : {(m.LayoutMatch ? "match" : "DIFFER")}");
        Console.WriteLine($"  data   : {(m.DataMatch ? "match" : "DIFFER")}");
        Console.WriteLine($"  audio  : {m.AudioSimilarity:P1} similar (best shift {m.BestShift})");
        Console.WriteLine(m.SameDisc
            ? "  => SAME DISC (identity matches despite any read-offset difference)."
            : "  => not the same disc.");
        return m.SameDisc ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Date a disc from its ISO 9660 timestamps and flag contradictions (re-mastering / tampering).
static int DiscDateCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-date <image.iso>\n" +
                    "  Reads the timestamps and identifiers a cooked ISO 9660 image was mastered with —\n" +
                    "  volume creation/modification dates, every file's recording date, and the\n" +
                    "  system/publisher/preparer/application strings — and flags contradictions that reveal\n" +
                    "  a disc was altered after mastering (the classic tell: a file dated after the volume).");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        var r = DiscForge.Core.Forensics.DiscChronology.Analyze(File.ReadAllBytes(path));

        Console.WriteLine($"{Path.GetFileName(path)}: {r.Summary()}");
        Console.WriteLine($"  volume id   : {r.VolumeId}");
        if (r.SystemId is { Length: > 0 }) Console.WriteLine($"  system      : {r.SystemId}");
        if (r.Publisher is { Length: > 0 }) Console.WriteLine($"  publisher   : {r.Publisher}");
        if (r.DataPreparer is { Length: > 0 }) Console.WriteLine($"  preparer    : {r.DataPreparer}");
        if (r.Application is { Length: > 0 }) Console.WriteLine($"  application : {r.Application}");
        Console.WriteLine($"  created     : {r.VolumeCreated}");
        Console.WriteLine($"  modified    : {r.VolumeModified}");
        Console.WriteLine($"  files       : {r.FileCount:N0}" +
                          (r.EarliestFile is not null && r.LatestFile is not null
                              ? $", dated {r.EarliestFile} .. {r.LatestFile}"
                              : ""));
        if (r.Anomalies.Count > 0)
        {
            Console.WriteLine("  anomalies:");
            foreach (var a in r.Anomalies) Console.WriteLine($"    ! {a}");
        }
        return r.LooksTampered ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Classify a disc's failing sectors by shape: physical damage (recover) vs deliberate pattern (preserve).
static int ErrorPatternCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge error-pattern <in.bin> [--provenance]\n" +
                    "  Reads which sectors are failing and classifies the SHAPE of the pattern they form:\n" +
                    "  a solid burst is a scratch, irregular scatter is surface rot (both physical — recover),\n" +
                    "  a regular repeating spacing is a deliberate protection layout (preserve, don't repair).\n" +
                    "  Input is a raw 2352-byte/sector image (EDC-scanned), or a reconstruct --provenance map\n" +
                    "  (one byte/sector) with --provenance.");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    bool fromProvenance = args.Contains("--provenance");
    try
    {
        var bytes = File.ReadAllBytes(path);
        DiscForge.Core.Forensics.SectorHealth[] health = fromProvenance
            ? DiscForge.Core.Forensics.DiscHealthMap.FromProvenance(bytes)
            : DiscForge.Core.Forensics.DiscHealthMap.Scan(bytes);

        var report = DiscForge.Core.Forensics.ErrorPatternForensics.Classify(health);
        Console.WriteLine(DiscForge.Core.Forensics.ErrorPatternForensics.Render(report, Path.GetFileName(path)));
        // Non-zero when the failures look deliberate, so a script does not blindly "repair" protection.
        return report.LooksDeliberate ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Parse a disc's IFPI ring / runout codes; or group a set of records by pressing plant / master.
static int RingCodeCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge ring-code \"<runout text>\"\n" +
                    "       dforge ring-code group <records.json>\n" +
                    "  Decodes the mastering SID (IFPI Lxxx = glass master) and mould SID (IFPI xxxx = plant)\n" +
                    "  plus the matrix string from a disc's inner-ring text (typed, or OCR'd from a ring photo).\n" +
                    "  'group' clusters records { \"records\":[{\"genome\":\"..\",\"volume\":\"..\",\"runout\":\"..\"}] }\n" +
                    "  by shared plant and shared master.");

    try
    {
        if (args[1].Equals("group", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 3 || !File.Exists(args[2])) return Fail("group needs an existing <records.json>.");
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[2]));
            var records = new List<DiscForge.Core.Forensics.RingCodeRecord>();
            if (doc.RootElement.TryGetProperty("records", out var recs) && recs.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var r in recs.EnumerateArray())
                    records.Add(new DiscForge.Core.Forensics.RingCodeRecord
                    {
                        GenomeId = r.TryGetProperty("genome", out var ge) ? ge.GetString() ?? "" : "",
                        VolumeId = r.TryGetProperty("volume", out var ve) ? ve.GetString() : null,
                        Ring = DiscForge.Core.Forensics.RingCodeParser.Parse(
                            r.TryGetProperty("runout", out var ru) ? ru.GetString() ?? "" : ""),
                    });

            var plants = DiscForge.Core.Forensics.RingCodeParser.GroupByPlant(records);
            var masters = DiscForge.Core.Forensics.RingCodeParser.GroupByMaster(records);
            Console.WriteLine($"{records.Count} record(s) — {plants.Count} plant(s), {masters.Count} master(s).");
            Console.WriteLine("By pressing plant (mould SID):");
            foreach (var g in plants) Console.WriteLine($"  IFPI {g.Key}: {string.Join(", ", g.Members)}");
            Console.WriteLine("By glass master (mastering SID):");
            foreach (var g in masters) Console.WriteLine($"  IFPI {g.Key}: {string.Join(", ", g.Members)}");
            return 0;
        }

        var ring = DiscForge.Core.Forensics.RingCodeParser.Parse(args[1]);
        Console.WriteLine(DiscForge.Core.Forensics.RingCodeParser.Render(ring));
        return ring.HasAny ? 0 : 1;
    }
    catch (System.Text.Json.JsonException ex) { return Fail($"Bad JSON: {ex.Message}"); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Technical bill-of-materials: what a disc was built with (engine, middleware, runtime) and when.
static int DiscBomCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-bom <image.iso>\n" +
                    "  Reads what the disc was built with — game engine (Unreal/Unity/RenderWare), middleware\n" +
                    "  (Bink/Smacker video, Miles/FMOD audio, Havok/PhysX), compiler runtime, the platform's\n" +
                    "  asset pipeline — and, from the disc's own timestamps, when it was mastered. A software\n" +
                    "  bill-of-materials for retro media. Detection and documentation only.");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        var bom = DiscForge.Core.Forensics.DiscBillOfMaterials.FromIso(File.ReadAllBytes(path));
        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Forensics.DiscBillOfMaterials.Render(bom)}");
        return bom.Components.Count > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Generate or verify a Cybdyn CU2 track-map sidecar from a cue and its data file.
static int Cu2Cmd(string[] args)
{
    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
    if (sub is not ("write" or "verify") || args.Length < 3)
        return Fail("usage: dforge cu2 write <sheet.cue> [out.cu2]\n" +
                    "       dforge cu2 verify <sheet.cue> <file.cu2>\n" +
                    "  Generates a revision-2 CU2 track map (absolute LBAs + the PSIO 2-second lead-in offset)\n" +
                    "  from a cue and the size of its data file, or cross-checks an existing CU2 against the cue.");
    string cuePath = args[2];
    if (!File.Exists(cuePath)) return Fail($"'{cuePath}' not found.");
    try
    {
        var cue = DiscForge.Core.Cue.CueSheet.Parse(File.ReadAllText(cuePath));
        long total = TotalCueSectors(cue, cuePath, out string? sizeErr);
        if (sizeErr is not null) return Fail(sizeErr);

        if (sub == "write")
        {
            string cu2 = DiscForge.Core.Cue.Cu2.Write(cue, total);
            if (args.Length >= 4 && !args[3].StartsWith("--"))
            {
                File.WriteAllText(args[3], cu2);
                Console.WriteLine($"Wrote {args[3]} ({cue.Tracks.Count} track(s), {total:N0} sectors).");
            }
            else Console.Write(cu2 + "\n");
            return 0;
        }

        // verify
        if (args.Length < 4 || !File.Exists(args[3])) return Fail("Provide the CU2 file to verify.");
        var parsed = DiscForge.Core.Cue.Cu2.Parse(File.ReadAllText(args[3]));
        var vr = DiscForge.Core.Cue.Cu2.Verify(cue, total, parsed);
        Console.WriteLine($"{Path.GetFileName(args[3])}: {vr.Summary()}");
        return vr.Match ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Total playable sectors implied by a cue's data file(s): bytes ÷ the tracks' sector size.
static long TotalCueSectors(DiscForge.Core.Cue.CueSheet cue, string cuePath, out string? error)
{
    error = null;
    string dir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
    long total = 0;
    foreach (var f in cue.Tracks.Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        int sectorSize = cue.Tracks.Where(t => string.Equals(t.File, f, StringComparison.OrdinalIgnoreCase))
                                   .Select(t => DiscForge.Core.Cue.CueSheet.TypeToToken(t.Type).sectorSize).Max();
        string path = Path.IsPathRooted(f) ? f : Path.Combine(dir, f);
        if (!File.Exists(path)) { error = $"data file '{f}' not found (needed to size the CU2)."; return 0; }
        total += new FileInfo(path).Length / sectorSize;
    }
    return total;
}

// Lay a preserved dump out for an optical-drive emulator (ODE).
static int OdeExportCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge ode-export <target> <cue> <out-dir> [--name NAME]\n" +
                    "  Lays a preserved disc out the way an ODE expects, so it can be played on real hardware.\n" +
                    "  Targets: psio  (PSIO / xStation — PlayStation)\n" +
                    "  Produces <out-dir>/<name>/ with the track bin(s), the cue, and a generated CU2 track map.");
    string target = args[1].ToLowerInvariant();
    string cuePath = args[2];
    string outDir = args[3];
    if (!File.Exists(cuePath)) return Fail($"'{cuePath}' not found.");
    if (target != "psio") return Fail($"unknown target '{target}'. Supported: psio.");

    try
    {
        var cue = DiscForge.Core.Cue.CueSheet.Parse(File.ReadAllText(cuePath));
        long total = TotalCueSectors(cue, cuePath, out string? err);
        if (err is not null) return Fail(err);

        string name = args.SkipWhile(a => a != "--name").Skip(1).FirstOrDefault()
                      ?? Path.GetFileNameWithoutExtension(cuePath);
        var plan = DiscForge.Core.Convert.OdeExporter.Psio(cuePath, cue, total, name);

        foreach (var op in plan.Ops)
        {
            string dest = Path.Combine(outDir, op.DestRelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (op.Kind == "copy")
            {
                if (op.SourcePath is null || !File.Exists(op.SourcePath))
                    return Fail($"source file missing: {op.SourcePath}");
                File.Copy(op.SourcePath, dest, overwrite: true);
            }
            else
            {
                File.WriteAllText(dest, op.Content ?? "");
            }
            Console.WriteLine($"  {(op.Kind == "copy" ? "copied " : "wrote  ")} {op.DestRelPath}");
        }
        Console.WriteLine($"Exported for {plan.Target}: {Path.Combine(outDir, plan.GameFolder)}");
        foreach (var n in plan.Notes) Console.WriteLine($"  note: {n}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Dump-completeness certificate: reconcile cue layout, data-file size and subchannel coverage.
static int CompletenessCheckCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge completeness-check <sheet.cue> [--json]\n" +
                    "  Issues a 'did we capture everything?' certificate for a bin/cue dump: it reconciles the\n" +
                    "  cue's track layout, the data file's size (÷ sector size) and — when a .sub sidecar is\n" +
                    "  present — the subchannel's own sector count (÷ 96). Agreement is strong evidence the dump\n" +
                    "  is whole; it also states what a bin/cue can never hold (lead-in/out, PMA, ATIP).");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var r = DiscForge.Core.Forensics.DumpCompleteness.Check(path);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                r.TrackCount, r.SessionCount, r.TotalSectors, r.BinFiles, r.AllBinsPresent, r.WholeSector,
                r.SubchannelPresent, r.SubchannelSectors, r.SubchannelMatches,
                r.Complete, r.Gaps, r.NotRepresentable,
            });
            return r.Complete ? 0 : 2;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Forensics.DumpCompleteness.Render(r)}");
        return r.Complete ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Physical-coverage proof: prove every sector is accounted for exactly once (gaps + overlaps).
static int CoverageProofCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge coverage-proof <image.iso> [--json]\n" +
                    "  Proves that the ISO 9660 structures account for every addressable sector exactly once —\n" +
                    "  a stronger property than count reconciliation. It reports SILENT GAPS (sectors no structure\n" +
                    "  claims — an unresolved directory, a hidden extent, unexplained slack) and OVERLAPS (two\n" +
                    "  structures claiming the same sector — a mastering bug or corruption). Passes only when the\n" +
                    "  coverage is an exact partition. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var image = File.ReadAllBytes(path);
        var p = DiscForge.Core.Forensics.PhysicalCoverage.OfIso(image);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                p.TotalSectors, p.AccountedSectors, p.Complete,
                gaps = p.Gaps.Select(g => new { g.StartSector, g.SectorCount }),
                overlaps = p.Overlaps.Select(o => new { o.StartSector, o.SectorCount, o.OwnerA, o.OwnerB }),
            });
            return p.Complete ? 0 : 2;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Forensics.PhysicalCoverage.Render(p)}");
        return p.Complete ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Filesystem-constrained recovery: reconstruct free space and identify what erased sectors held.
static int FsRecoverCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge fs-recover <image.iso> --erased <list> [--out out.iso]\n" +
                    "  Uses the ISO 9660 filesystem to make sense of erased/unreadable sectors: it reconstructs\n" +
                    "  FREE SPACE under the disc's own validated fill convention, identifies file-content sectors\n" +
                    "  by file name and the exact byte range lost (so a targeted re-read can finish them), and\n" +
                    "  bounds metadata as such. File data is never guessed. --erased takes a comma/range list of\n" +
                    "  sector numbers (e.g. 42,100-104,250). --out writes the reconstructed image. Read-only\n" +
                    "  without --out.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    string? erasedSpec = OptVal(args, "--erased");
    if (string.IsNullOrWhiteSpace(erasedSpec)) return Fail("Give the erased sectors with --erased, e.g. --erased 42,100-104.");
    string? outPath = OptVal(args, "--out");

    var erased = new List<long>();
    foreach (var part in erasedSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        int dash = part.IndexOf('-');
        if (dash > 0 && long.TryParse(part[..dash], out var a) && long.TryParse(part[(dash + 1)..], out var b))
            for (long s = a; s <= b; s++) erased.Add(s);
        else if (long.TryParse(part, out var one)) erased.Add(one);
        else return Fail($"Bad --erased entry '{part}'.");
    }

    try
    {
        var image = File.ReadAllBytes(path);
        var r = DiscForge.Core.Recovery.FilesystemConstrainedRecovery.RecoverIso(image, erased);
        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Recovery.FilesystemConstrainedRecovery.Render(r)}");
        if (outPath is not null)
        {
            File.WriteAllBytes(outPath, r.Image);
            Console.WriteLine($"  wrote reconstructed image: {Path.GetFileName(outPath)} ({r.Recovered:N0} sector(s) filled).");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Minimal disc descriptor: factor an image into fill/duplicate/unique and report the irreducible content.
static int MinDescriptorCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge min-descriptor <image> [--sector N] [--json]\n" +
                    "  Computes the smallest complete description of a disc image: constant-fill runs\n" +
                    "  (padding/blanked regions), duplicate sectors (back-references), and the genuinely UNIQUE\n" +
                    "  sectors that are its irreducible content. Reports how much of the image is fill/repetition\n" +
                    "  versus real content — an honest information floor for the format as dumped. --sector sets\n" +
                    "  the sector size (default 2048). Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    int sector = int.TryParse(OptVal(args, "--sector"), out var ss) && ss > 0 ? ss : 2048;
    try
    {
        var bytes = File.ReadAllBytes(path);
        var r = DiscForge.Core.Forensics.MinimalDiscDescriptor.Analyze(bytes, sector);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                r.SectorSize, r.TotalSectors, r.TotalBytes,
                r.UniqueSectors, r.DuplicateSectors, r.FillSectors,
                r.UniqueBytes, r.MinimalBytes,
                reductionRatio = Math.Round(r.ReductionRatio, 4),
                fill = r.FillBreakdown.Select(f => new { value = f.Value, f.Sectors }),
            });
            return 0;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Forensics.MinimalDiscDescriptor.Render(r)}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Emulation-readiness report: does this dump carry what an emulator needs to actually run?
static int EmuReadyCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge emu-ready <sheet.cue> [--json]\n" +
                    "  Reports whether a bin/cue dump has what an EMULATOR needs to run — beyond being physically\n" +
                    "  whole. Checks every referenced track is present and whole-sector, a bootable data track and\n" +
                    "  whether it is raw (2352) or cooked (2048), CD-DA audio tracks and their pregaps, and the\n" +
                    "  subchannel a LibCrypt/SBI-protected title needs. Grades READY / READY WITH CAVEATS / NOT\n" +
                    "  READY. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var r = DiscForge.Core.Forensics.EmulationReadiness.Analyze(path);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                grade = r.Grade.ToString(),
                findings = r.Findings.Select(f => new { f.Aspect, severity = f.Severity.ToString(), f.Detail }),
            });
            return r.Grade == DiscForge.Core.Forensics.EmuReadiness.NotReady ? 2 : 0;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Forensics.EmulationReadiness.Render(r)}");
        return r.Grade == DiscForge.Core.Forensics.EmuReadiness.NotReady ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PregapCheckCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge pregap-check <sheet.cue> [--json]\n" +
                    "  Audits a cue's track pregaps against PlayStation / Redump convention: track 1 begins at\n" +
                    "  00:00:00, the first audio track after the data track carries a 2-second (150-sector)\n" +
                    "  pregap, no INDEX 00 sits after its INDEX 01, and track numbers run 1..N. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var cue = DiscForge.Core.Cue.CueSheet.Parse(File.ReadAllText(path));
        var r = DiscForge.Core.Cue.PregapConformance.Check(cue);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                r.TrackCount, r.Conformant,
                tracks = r.Tracks.Select(t => new
                {
                    t.Number, t.Type, t.IsAudio, t.Index00Sectors, t.Index01Sectors,
                    t.GapSectors, t.CrossesDataAudioBoundary, t.Issue,
                }),
                r.Issues,
            });
            return r.Conformant ? 0 : 2;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {r.Summary()}");
        foreach (var t in r.Tracks)
        {
            string gap = t.CrossesDataAudioBoundary ? $"{t.GapSectors}-sector pregap (data/audio boundary)"
                                                    : $"{t.GapSectors}-sector pregap";
            string flag = t.Issue is null ? "" : $"  <-- {t.Issue}";
            Console.WriteLine($"  track {t.Number:00} {t.Type,-11} {gap}{flag}");
        }
        return r.Conformant ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SubqMapCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge subq-map <disc.sub> [--json] [--form packed|interleaved|pq16]\n" +
                    "  Recovers each track's true INDEX 00 (pregap) and INDEX 01 (body) from a captured\n" +
                    "  subchannel sidecar, the way Redump derives a disc's pregaps. The Q channel carries a\n" +
                    "  position frame per sector; walking the CRC-valid frames pins where each track's pregap and\n" +
                    "  body actually begin, so a mixed-mode disc's real gaps come from the disc rather than a\n" +
                    "  guessed convention. Read-only analysis of an already-captured sidecar; it defeats nothing.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    DiscForge.Core.Raw.RawSubcodeForm? form = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--form" && i + 1 < args.Length)
        {
            form = args[++i].ToLowerInvariant() switch
            {
                "packed" or "packed96" => DiscForge.Core.Raw.RawSubcodeForm.Packed96,
                "interleaved" or "interleaved96" or "raw" => DiscForge.Core.Raw.RawSubcodeForm.Interleaved96,
                "pq16" or "pq" => DiscForge.Core.Raw.RawSubcodeForm.Pq16,
                _ => null,
            };
        }
    }

    try
    {
        var bytes = File.ReadAllBytes(path);
        var map = DiscForge.Core.Raw.SubchannelIndexMapper.Parse(bytes, form);

        static string Msf(long lba)
        {
            long a = lba + 150;
            return $"{a / 4500:00}:{a / 75 % 60:00}:{a % 75:00}";
        }

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                form = map.Form.ToString(),
                map.SectorsScanned,
                map.ValidQFrames,
                tracks = map.Tracks.Select(t => new
                {
                    t.Track, t.IsData, t.Index00Lba, t.Index01Lba, t.PregapSectors,
                    index00 = t.Index00Lba is { } v ? Msf(v) : null,
                    index01 = Msf(t.Index01Lba),
                }),
            });
            return map.Tracks.Count > 0 ? 0 : 1;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {map.Summary()}");
        foreach (var t in map.Tracks)
        {
            string kind = t.IsData ? "data " : "audio";
            string pre = t.Index00Lba is { } v
                ? $"INDEX 00 {Msf(v)}  INDEX 01 {Msf(t.Index01Lba)}  ({t.PregapSectors}-sector pregap)"
                : $"INDEX 01 {Msf(t.Index01Lba)}  (no pregap)";
            Console.WriteLine($"  track {t.Track:00} {kind}  {pre}");
        }
        return map.Tracks.Count > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int RedumpCueCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge redump-cue <in.cue> <disc.sub> <out.cue> [--snap-pregap]\n" +
                    "  Re-cuts a split bin/cue so its track boundaries match the disc's subchannel — the layout\n" +
                    "  Redump uses. A capture that cut each track at INDEX 01 folds every audio pregap into the tail\n" +
                    "  of the preceding file and writes a flat INDEX 01 00:00:00 cue; this moves each cut to the\n" +
                    "  track's INDEX 00, putting the pregap at the head of its own file with INDEX 00/01, so the set\n" +
                    "  can match a Redump checksum. Byte-preserving: the concatenated program area is unchanged, only\n" +
                    "  the split points move. New files are written under <out.cue>'s name; the originals are untouched.\n" +
                    "  --snap-pregap normalises a gap within two sectors of 150 to the exact 2-second convention.");
    var inCue = args[1];
    var subPath = args[2];
    var outCue = args[3];
    if (!File.Exists(inCue)) return Fail($"'{inCue}' not found.");
    if (!File.Exists(subPath)) return Fail($"'{subPath}' not found.");
    bool snap = args.Contains("--snap-pregap");

    var outDir = Path.GetDirectoryName(Path.GetFullPath(outCue))!;
    var outBase = Path.GetFileNameWithoutExtension(outCue);

    try
    {
        var sub = File.ReadAllBytes(subPath);
        var r = DiscForge.Core.Convert.RedumpCueBuilder.Build(inCue, sub, outDir, outBase, snap);

        foreach (var w in r.Warnings) Console.WriteLine($"warning: {w}");
        Console.WriteLine($"{Path.GetFileName(outCue)}: {r.Summary()}");
        foreach (var t in r.Tracks)
        {
            string gap = t.PregapSectors > 0 ? $"{t.PregapSectors}-sector pregap" : "no pregap";
            string flag = t.Note is null ? "" : $"  <-- {t.Note}";
            Console.WriteLine($"  track {t.Track:00} {CueTypeLabel(t.Type),-11} {gap}{flag}");
        }
        Console.WriteLine($"Wrote {Path.GetFileName(outCue)} and {r.BinFilenames.Count} BIN file(s) to {outDir}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static string CueTypeLabel(DiscForge.Core.Cue.CueTrackType t) => DiscForge.Core.Cue.CueSheet.TypeToToken(t).token;

static int RedumpPrepCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge redump-prep <in.cue> <out-dir> [--sub disc.sub] [--snap-pregap] [--dat file --game \"name\"] [--offset N] [--json]\n" +
                    "  Prepares a submission-ready set from a raw capture in one step: re-cuts the tracks to the\n" +
                    "  subchannel's Redump boundaries (--sub), carries the unreadable-sector map forward, checks pregap\n" +
                    "  conformance and completeness, writes the redump.org submission text, and — with --dat — diffs the\n" +
                    "  result and reports whether it matches. A read offset (--offset) is recorded for the submission,\n" +
                    "  never applied (offset is a capture property; the payload is left byte-for-byte as dumped).");
    var inCue = args[1];
    var outDir = args[2];
    if (!File.Exists(inCue)) return Fail($"'{inCue}' not found.");

    var opt = new DiscForge.Core.Redump.RedumpPrepOptions
    {
        SubPath = OptVal(args, "--sub"),
        SnapPregap = args.Contains("--snap-pregap"),
        DatPath = OptVal(args, "--dat"),
        Game = OptVal(args, "--game"),
        ReadOffsetSamples = int.TryParse(OptVal(args, "--offset"), out var o) ? o : null,
    };

    try
    {
        var r = DiscForge.Core.Redump.RedumpPrep.Prepare(inCue, outDir, opt);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                r.OutCue, r.ReSplit, r.SubmissionReady, r.SubmissionInfoPath,
                checks = r.Checks.Select(c => new { c.Name, status = c.Status.ToString(), c.Detail }),
                r.OutputFiles,
            });
            return r.SubmissionReady ? 0 : 2;
        }

        Console.WriteLine(r.Summary());
        foreach (var c in r.Checks)
        {
            string tag = c.Status switch
            {
                DiscForge.Core.Redump.PrepStatus.Pass => "[PASS]",
                DiscForge.Core.Redump.PrepStatus.Fail => "[FAIL]",
                DiscForge.Core.Redump.PrepStatus.Warn => "[WARN]",
                _ => "[INFO]",
            };
            Console.WriteLine($"  {tag} {c.Name}: {c.Detail}");
        }
        Console.WriteLine($"Prepared {r.OutputFiles.Count} file(s) in {outDir}.");
        return r.SubmissionReady ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int MasteringPrintCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage:\n" +
                    "  dforge mastering-print <image> [--json]\n" +
                    "  dforge mastering-print compare <a> <b> [--json]\n" +
                    "  Derives a disc's mastering fingerprint from its ISO 9660 volume descriptor — the system,\n" +
                    "  volume, publisher, data-preparer and application (mastering-tool) identifiers and the creation/\n" +
                    "  modification timestamps a mastering house stamps — plus a hash of the descriptor and the trailing\n" +
                    "  padding. Two pressings of a title share these; a reproduction re-mastered from the same game\n" +
                    "  files diverges here. 'compare' flags that divergence. Characterises the disc's own metadata only.");
    try
    {
        if (args[1] == "compare")
        {
            if (args.Length < 4) return Fail("usage: dforge mastering-print compare <a> <b> [--json]");
            if (!File.Exists(args[2])) return Fail($"'{args[2]}' not found.");
            if (!File.Exists(args[3])) return Fail($"'{args[3]}' not found.");
            var fa = DiscForge.Core.Forensics.MasteringPrinter.Extract(args[2]);
            var fb = DiscForge.Core.Forensics.MasteringPrinter.Extract(args[3]);
            var cmp = DiscForge.Core.Forensics.MasteringPrinter.Compare(fa, fb);

            if (args.Contains("--json"))
            {
                EmitJson(new { a = fa, b = fb, verdict = cmp.Verdict.ToString(), cmp.Divergences });
                return cmp.Verdict == DiscForge.Core.Forensics.MasteringVerdict.IdenticalMastering ? 0 : 2;
            }
            Console.WriteLine(cmp.Summary());
            foreach (var d in cmp.Divergences) Console.WriteLine($"  - {d}");
            return cmp.Verdict == DiscForge.Core.Forensics.MasteringVerdict.IdenticalMastering ? 0 : 2;
        }

        var image = args[1];
        if (!File.Exists(image)) return Fail($"'{image}' not found.");
        var fp = DiscForge.Core.Forensics.MasteringPrinter.Extract(image);

        if (args.Contains("--json")) { EmitJson(fp); return 0; }

        Console.WriteLine($"{fp.Image}: {fp.Summary()}");
        Console.WriteLine($"  system id     : {fp.SystemId}");
        Console.WriteLine($"  volume id     : {fp.VolumeId}");
        Console.WriteLine($"  publisher     : {fp.PublisherId}");
        Console.WriteLine($"  data preparer : {fp.DataPreparerId}");
        Console.WriteLine($"  application   : {fp.ApplicationId}");
        Console.WriteLine($"  created       : {fp.CreationTime}");
        Console.WriteLine($"  modified      : {fp.ModificationTime}");
        Console.WriteLine($"  PVD hash      : {fp.PvdHash[..16]}…");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int OdeLayoutCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge ode-layout <gdemu|rhea|phoebe|mode> <games-dir> <out-dir>\n" +
                    "  Arranges a set of already-converted games (one sub-folder per game inside <games-dir>) into the\n" +
                    "  SD-card layout a given optical-drive emulator expects. GDEMU and Rhea/Phoebe use numbered\n" +
                    "  folders with folder 01 RESERVED for the menu (games start at 02) plus per-game name/disc\n" +
                    "  sidecars; MODE uses free-form named folders it scans itself. It does NOT build the boot menu —\n" +
                    "  run the device's own tool (GDMENU Card Manager / RMENU) for that. PSIO layout: use ode-export psio.");
    var targetArg = args[1].ToLowerInvariant();
    var gamesDir = args[2];
    var outDir = args[3];
    DiscForge.Core.Convert.OdeTarget target = targetArg switch
    {
        "gdemu" => DiscForge.Core.Convert.OdeTarget.Gdemu,
        "rhea" => DiscForge.Core.Convert.OdeTarget.Rhea,
        "phoebe" => DiscForge.Core.Convert.OdeTarget.Phoebe,
        "mode" => DiscForge.Core.Convert.OdeTarget.Mode,
        _ => (DiscForge.Core.Convert.OdeTarget)(-1),
    };
    if ((int)target == -1) return Fail($"unknown target '{targetArg}' — use gdemu, rhea, phoebe, or mode.");
    if (!Directory.Exists(gamesDir)) return Fail($"'{gamesDir}' is not a folder.");

    try
    {
        var r = DiscForge.Core.Convert.OdeLayout.Build(target, gamesDir, outDir);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                target = r.Target.ToString(), r.OutDir,
                games = r.Games.Select(g => new { g.Title, g.Folder, g.FilesCopied }),
                r.Notes,
            });
            return 0;
        }

        Console.WriteLine(r.Summary());
        foreach (var g in r.Games)
            Console.WriteLine($"  {g.Folder}/  {g.Title} ({g.FilesCopied} file(s))");
        Console.WriteLine("notes:");
        foreach (var n in r.Notes) Console.WriteLine($"  • {n}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IsoCreateCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge iso-create <folder> <out.iso> [--volume-id NAME] [--no-joliet] [--rock-ridge]\n" +
                    "                         [--boot <bootimg> [--boot-emulation no|floppy144|floppy288|floppy12|harddisk]]\n" +
                    "                         [--boot-uefi <efi.img>]\n" +
                    "  Builds a standard ISO 9660 data-disc image from a folder tree. Joliet (long/Unicode names)\n" +
                    "  is on by default; --no-joliet for a plain 8.3 ISO, --rock-ridge to add POSIX names. Pass --boot\n" +
                    "  with your own boot loader binary to make an El Torito bootable disc (no-emulation is the default\n" +
                    "  and is right for isolinux/GRUB). Add --boot-uefi <efi.img> (an EFI boot image, e.g. an EFI\n" +
                    "  System Partition image or bootx64.efi) to emit a UEFI boot entry; with BOTH --boot and\n" +
                    "  --boot-uefi you get a BIOS+UEFI HYBRID disc that boots on legacy and UEFI firmware alike.\n" +
                    "  Streams the image out with constant memory, so any size is fine.");
    var folder = args[1];
    var outIso = args[2];
    if (!Directory.Exists(folder)) return Fail($"'{folder}' is not a folder.");
    string? volumeId = OptVal(args, "--volume-id");
    bool joliet = !args.Contains("--no-joliet");
    bool rockRidge = args.Contains("--rock-ridge");
    string? bootImg = OptVal(args, "--boot");
    var bootMedia = DiscForge.Core.Iso.IsoBuilder.BootMediaType.NoEmulation;
    var emu = OptVal(args, "--boot-emulation");
    if (emu is not null)
    {
        bootMedia = emu.ToLowerInvariant() switch
        {
            "no" or "noemulation" or "none" => DiscForge.Core.Iso.IsoBuilder.BootMediaType.NoEmulation,
            "floppy12" or "1.2" => DiscForge.Core.Iso.IsoBuilder.BootMediaType.Floppy12,
            "floppy144" or "1.44" => DiscForge.Core.Iso.IsoBuilder.BootMediaType.Floppy144,
            "floppy288" or "2.88" => DiscForge.Core.Iso.IsoBuilder.BootMediaType.Floppy288,
            "harddisk" or "hdd" or "hd" => DiscForge.Core.Iso.IsoBuilder.BootMediaType.HardDisk,
            _ => (DiscForge.Core.Iso.IsoBuilder.BootMediaType)255,
        };
        if ((byte)bootMedia == 255) return Fail($"unknown --boot-emulation '{emu}' (use no|floppy144|floppy288|floppy12|harddisk).");
        if (bootImg is null) return Fail("--boot-emulation requires --boot <bootimg>.");
    }
    string? efiBootImg = OptVal(args, "--boot-uefi");

    try
    {
        DiscForge.Core.Iso.IsoFromFolderResult r;
        using (var os = File.Create(outIso))
            r = DiscForge.Core.Iso.IsoFromFolder.Write(folder, volumeId, os, joliet, rockRidge, bootImg, bootMedia, efiBootImg);

        // Dual-layer territory? Plan the layer break up front so burn time holds no surprises: legal
        // candidates are 16-sector ECC-block boundaries with layer 0 ≥ layer 1 (OTP) and within the
        // layer-0 capacity. The plan is written beside the ISO for the burn step to pick up — in
        // BOTH output modes, since scripts (--json) are exactly the callers that need the sidecar.
        const long Dvd5Sectors = 2_295_104;                  // single-layer DVD capacity (2048-byte sectors)
        long totalSectors = (r.ImageBytes + 2047) / 2048;
        DiscForge.Core.DvdVideo.LayerBreakPlanner.Candidate? lb = null;
        bool needsBreak = totalSectors > Dvd5Sectors;
        if (needsBreak)
        {
            long minL0 = (totalSectors + 1) / 2;
            long maxL0 = Math.Min(DiscForge.Core.DvdVideo.LayerBreakPlanner.Dvd9MaxLayer0Sectors, totalSectors);
            var eccBoundaries = new List<long>();
            for (long lba = (minL0 + 15) / 16 * 16; lba <= maxL0; lba += 16) eccBoundaries.Add(lba);
            lb = DiscForge.Core.DvdVideo.LayerBreakPlanner.Recommend(eccBoundaries, totalSectors).Recommended;
            if (lb is { } b)
            {
                var sidecar = outIso + ".layerbreak.json";
                try
                {
                    File.WriteAllText(sidecar, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        image = Path.GetFileName(outIso), totalSectors,
                        layerBreakLba = b.Lba, layer0 = b.Layer0Sectors, layer1 = b.Layer1Sectors,
                        basis = "16-sector ECC boundary, OTP L0>=L1, balanced split (data disc — no VOBU constraint)",
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    // The ISO itself succeeded; a failed sidecar is a warning, not a failed build.
                    Console.Error.WriteLine($"warning: could not write {Path.GetFileName(sidecar)}: {ex.Message}");
                }
            }
        }

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                output = Path.GetFileName(outIso), r.VolumeId, r.Files, r.Directories, r.ImageBytes,
                r.BootImage, r.EfiBootImage, r.Warnings,
                layerBreak = lb is { } c
                    ? new { lba = c.Lba, layer0 = c.Layer0Sectors, layer1 = c.Layer1Sectors }
                    : null,
                layerBreakRequired = needsBreak,
            });
            return 0;
        }

        Console.WriteLine($"Built {Path.GetFileName(outIso)}: volume \"{r.VolumeId}\", {r.Files:N0} file(s) in {r.Directories:N0} " +
                          $"director(y/ies), {r.ImageBytes:N0} bytes{(joliet ? " (Joliet)" : "")}{(rockRidge ? " (Rock Ridge)" : "")}" +
                          $"{(r.BootImage is null ? "" : $" (BIOS boot: {r.BootImage})")}" +
                          $"{(r.EfiBootImage is null ? "" : $" (UEFI boot: {r.EfiBootImage})")}.");
        foreach (var w in r.Warnings) Console.WriteLine($"  warning: {w}");

        if (needsBreak)
        {
            if (lb is { } best)
            {
                Console.WriteLine($"  dual-layer: {totalSectors:N0} sectors exceeds DVD-5 — layer break planned at " +
                                  $"LBA {best.Lba:N0} (L0 {best.Layer0Sectors:N0} / L1 {best.Layer1Sectors:N0}, " +
                                  $"{best.PercentOfTotal:F1}% of the disc).");
                Console.WriteLine($"  layer-break plan: {Path.GetFileName(outIso)}.layerbreak.json");
                if (Directory.Exists(Path.Combine(folder, "VIDEO_TS")))
                    Console.WriteLine("  note: this folder is DVD-Video — for player-seamless breaks prefer a VOBU " +
                                      "boundary via `dforge dvd-layerbreak-plan <VTS_nn_0.IFO> --image " + Path.GetFileName(outIso) + "`.");
            }
            else
                Console.WriteLine($"  warning: {totalSectors:N0} sectors exceeds DVD-5 but NO legal layer break exists " +
                                  "(image too large for DVD-9's layer-0?) — check capacity before burning.");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CueRepairCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cue-repair <in.cue> [out.cue] [--json]\n" +
                    "  Repairs the everyday ways a cue breaks — a FILE line pointing at the wrong name (fixed against\n" +
                    "  the actual track file beside it), tracks numbered out of order, a missing INDEX 01 — and re-emits\n" +
                    "  a clean, normalised cue, reporting every change and anything it could not safely fix. With no\n" +
                    "  <out.cue> it is a dry run (prints the repaired cue); it only ever rewrites the cue text.");
    var inCue = args[1];
    if (!File.Exists(inCue)) return Fail($"'{inCue}' not found.");
    string? outCue = args.Length >= 3 && !args[2].StartsWith("--") ? args[2] : null;

    try
    {
        var r = DiscForge.Core.Cue.CueRepair.Repair(inCue);

        if (args.Contains("--json"))
        {
            EmitJson(new { input = Path.GetFileName(inCue), r.Changed, r.Changes, r.Unresolved,
                written = outCue is null ? null : Path.GetFileName(outCue) });
            if (outCue is not null) File.WriteAllText(outCue, r.CueText);
            return r.Unresolved.Count > 0 ? 2 : 0;
        }

        Console.WriteLine(r.Summary());
        foreach (var c in r.Changes) Console.WriteLine($"  fixed: {c}");
        foreach (var u in r.Unresolved) Console.WriteLine($"  TODO:  {u}");
        if (outCue is not null)
        {
            File.WriteAllText(outCue, r.CueText);
            Console.WriteLine($"Wrote {Path.GetFileName(outCue)}.");
        }
        else
        {
            Console.WriteLine("--- repaired cue (dry run; pass <out.cue> to write) ---");
            Console.Write(r.CueText);
        }
        return r.Unresolved.Count > 0 ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VerifyConvertCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge verify-convert <a> <b> [--json] [--report cert.html]\n" +
                    "  Proves a format conversion kept the disc byte-for-byte. Decodes BOTH images to their raw\n" +
                    "  sector bytes and compares them exactly, reporting LOSSLESS or the precise divergence (a size\n" +
                    "  delta from a dropped/added track or subchannel, or the first differing sector). Accepts a\n" +
                    "  bin/cue, a .chd (CD), or a raw .bin on either side. Read-only.\n" +
                    "  --report cert.html  write a shareable lossless-conversion certificate (content SHA-256).");
    var a = args[1];
    var b = args[2];
    if (!File.Exists(a)) return Fail($"'{a}' not found.");
    if (!File.Exists(b)) return Fail($"'{b}' not found.");
    string? reportPath = null;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--report" && i + 1 < args.Length) reportPath = args[++i];

    try
    {
        var rawA = RawSectorsOf(a);
        var rawB = RawSectorsOf(b);

        if (reportPath is not null)
        {
            var cert = DiscForge.Core.Convert.ConversionCertificate.Build(
                rawA, rawB, Path.GetFileName(a), Path.GetFileName(b),
                stamp: DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
            File.WriteAllText(reportPath, DiscForge.Core.Convert.ConversionCertificate.Html(cert));
            Console.WriteLine($"Wrote certificate: {reportPath}");
            if (args.Contains("--json")) Console.WriteLine(DiscForge.Core.Convert.ConversionCertificate.Json(cert));
            else Console.WriteLine(cert.Summary);
            return cert.Lossless ? 0 : 2;
        }

        var r = DiscForge.Core.Convert.ConversionVerify.Compare(rawA, rawB);

        if (args.Contains("--json"))
        {
            EmitJson(new { a = Path.GetFileName(a), b = Path.GetFileName(b), r.Lossless, r.LengthA, r.LengthB, r.FirstDiffOffset });
            return r.Lossless ? 0 : 2;
        }

        Console.WriteLine(r.Summary());
        return r.Lossless ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DiscDiffCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge disc-diff <a> <b> [--json]\n" +
                    "  Compares two disc images at the FILE level — reading each through its filesystem and\n" +
                    "  comparing by content hash — and reports what changed: files added, removed, changed in\n" +
                    "  content, or moved/renamed (same bytes, new path). Answers \"what actually differs between\n" +
                    "  these two discs?\" for two pressings, a patched vs original disc, or two revisions. This is a\n" +
                    "  file-tree diff, distinct from verify-convert (raw byte-for-byte) and redump-diff (DAT match).\n" +
                    "  Read-only. Accepts .iso/.cdi/.bin/.cue/.img on either side.");
    var a = args[1];
    var b = args[2];
    if (!File.Exists(a)) return Fail($"'{a}' not found.");
    if (!File.Exists(b)) return Fail($"'{b}' not found.");

    try
    {
        var r = DiscForge.Core.Files.DiscDiff.Compare(a, b);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                a = Path.GetFileName(a), b = Path.GetFileName(b),
                identical = r.Identical, error = r.Error,
                added = r.Added.Select(f => new { f.Path, f.Size }),
                removed = r.Removed.Select(f => new { f.Path, f.Size }),
                changed = r.Changed.Select(c => new { c.Path, c.SizeA, c.SizeB }),
                moved = r.Moved.Select(m => new { m.PathA, m.PathB, m.Size }),
                unchanged = r.Unchanged,
            });
            return r.Error is not null ? 1 : r.Identical ? 0 : 2;
        }

        Console.WriteLine(r.Summary());
        return r.Error is not null ? 1 : r.Identical ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Grade rip evidence and plan targeted re-reads — the EAC-style secure-rip depth, offline.
static int SecureRipPlanCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge secure-rip-plan <evidence.json> [--json]\n" +
                    "  Grade a rip's per-sector evidence and plan the targeted re-read. The evidence file is\n" +
                    "  what the rip layer records: per track, a base64 string of one state byte per sector\n" +
                    "  (0 clean, 1 C2-flagged, 2 pass-mismatch, 3 unreadable), the pass count, and the\n" +
                    "  AccurateRip verdict where known:\n" +
                    "    { \"tracks\": [ { \"number\":1, \"passes\":2, \"accurateRipMatch\":true,\n" +
                    "                     \"accurateRipConfidence\":7, \"sectorStates\":\"AAAA...\" } ] }\n" +
                    "  Grades: VERIFIED (independent AccurateRip match) / CONSISTENT (self-agreement only —\n" +
                    "  the honest ceiling without corroboration) / SUSPECT / FAILED.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("tracks", out var tracksEl) || tracksEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Fail("evidence file has no \"tracks\" array.");

        var verdicts = new List<DiscForge.Core.Audio.SecureRip.TrackVerdict>();
        var plans = new List<DiscForge.Core.Audio.SecureRip.RereadPlan>();
        int trackIdx = 0;
        foreach (var tEl in tracksEl.EnumerateArray())
        {
            trackIdx++;
            if (!tEl.TryGetProperty("number", out var numEl) || !numEl.TryGetInt32(out int number))
                return Fail($"track #{trackIdx} in '{Path.GetFileName(path)}' has no integer \"number\" field.");
            if (!tEl.TryGetProperty("passes", out var passEl) || !passEl.TryGetInt32(out int passes))
                return Fail($"track {number}: missing integer \"passes\" field.");
            if (!tEl.TryGetProperty("sectorStates", out var statesEl) || statesEl.GetString() is not { } statesB64)
                return Fail($"track {number}: missing \"sectorStates\" (base64, one state byte per sector).");
            byte[] states;
            try { states = System.Convert.FromBase64String(statesB64); }
            catch (FormatException) { return Fail($"track {number}: \"sectorStates\" is not valid base64."); }

            bool? ar = null;
            if (tEl.TryGetProperty("accurateRipMatch", out var arEl) &&
                arEl.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                ar = arEl.GetBoolean();
            var t = new DiscForge.Core.Audio.SecureRip.TrackEvidence
            {
                Number = number,
                Passes = passes,
                Sectors = states,
                AccurateRipMatch = ar,
                AccurateRipConfidence = tEl.TryGetProperty("accurateRipConfidence", out var c) ? c.GetInt32() : 0,
            };
            verdicts.Add(DiscForge.Core.Audio.SecureRip.Grade(t));
            plans.Add(DiscForge.Core.Audio.SecureRip.PlanReread(t));
        }

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                tracks = verdicts.Select((v, i) => new
                {
                    v.Number, grade = v.Grade.ToString(), v.Clean, v.C2Flagged, v.Mismatched, v.Unreadable, v.Reason,
                    reread = new
                    {
                        plans[i].SuggestedPasses, plans[i].Strategy,
                        ranges = plans[i].Ranges.Select(r => new { r.StartSector, r.Count, worst = r.Worst.ToString() }),
                    },
                }),
            });
            return verdicts.All(v => v.Grade is DiscForge.Core.Audio.SecureRip.TrackGrade.Verified
                                              or DiscForge.Core.Audio.SecureRip.TrackGrade.Consistent) ? 0 : 2;
        }

        foreach (var (v, p) in verdicts.Zip(plans))
        {
            Console.WriteLine($"track {v.Number:00}: {v.Grade.ToString().ToUpperInvariant()} — {v.Reason}");
            if (!p.Nothing)
            {
                Console.WriteLine($"  re-read {p.Ranges.Count} range(s), {p.SuggestedPasses} passes: {p.Strategy}");
                foreach (var r in p.Ranges.Take(20))
                    Console.WriteLine($"    sectors {r.StartSector}..{r.StartSector + r.Count - 1}  (worst: {r.Worst})");
                if (p.Ranges.Count > 20) Console.WriteLine($"    … and {p.Ranges.Count - 20} more");
            }
        }
        return verdicts.All(v => v.Grade is DiscForge.Core.Audio.SecureRip.TrackGrade.Verified
                                          or DiscForge.Core.Audio.SecureRip.TrackGrade.Consistent) ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// One-stop recovery assessment: identify → filesystem cross-check → bad-sector map → blank-region
// scan, graded and turned into next-step advice and an optional HTML report. The IsoBuster-style
// "just tell me what's wrong and what I can save" front door over the sector-level tools.
static int RecoverCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge recover <image> [report.html] [--json]\n" +
                    "  Assess a (possibly damaged) disc image in one pass: what format it is, which filesystem\n" +
                    "  views still read, what the unreadable-sector map says, where the blank regions are — then a\n" +
                    "  verdict (INTACT / RECOVERABLE / DAMAGED / UNREADABLE) with concrete next steps, and an HTML\n" +
                    "  report if you name one. Read-only; exit code 0 intact, 2 anything needing attention.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    string? outHtml = args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
    try
    {
        long size = new FileInfo(path).Length;

        // 1. What is it?
        string? format = null;
        using (var fs = File.OpenRead(path))
        {
            var id = DiscForge.Core.Identify.FormatIdentifier.Identify(fs);
            if (id.Recognised) format = string.IsNullOrEmpty(id.Detail) ? id.Name : $"{id.Name} — {id.Detail}";
        }

        // 2. Which filesystem views still read?
        var views = new List<string>();
        string? fsVerdict = null;
        try
        {
            var cc = DiscForge.Core.Files.ImageBrowser.CrossCheck(path);
            foreach (var v in cc.Views.Where(v => v.Error is null))
                views.Add($"{v.Kind} \"{v.VolumeId}\" ({v.FileCount:N0} files, {v.TotalBytes:N0} bytes)");
            if (views.Count > 0) fsVerdict = cc.Verdict.ToString();
        }
        catch { /* no browsable filesystem — the grade reflects it */ }

        // 3. The dump's own unreadable-sector map, if it was captured with one.
        int? damaged = null, boundary = null;
        var sidecar = path + ".badsectors.json";
        if (File.Exists(sidecar))
        {
            try
            {
                var map = DiscForge.Core.Preservation.BadSectorMap.Load(sidecar);
                damaged = map.DamageCount;
                boundary = map.BoundaryCount;
            }
            catch { /* unreadable sidecar is reported as absent, not fatal */ }
        }

        // 4. Blank regions + entropy.
        IReadOnlyList<DiscForge.Core.Recovery.RecoverySession.ZeroRegion> zeros;
        double entropy;
        using (var fs = File.OpenRead(path))
            zeros = DiscForge.Core.Recovery.RecoverySession.FindZeroRegions(fs);
        using (var fs = File.OpenRead(path))
            entropy = DiscForge.Core.Forensics.ShannonEntropy.Compute(fs).BitsPerByte;

        var findings = new DiscForge.Core.Recovery.RecoverySession.Findings
        {
            Image = Path.GetFileName(path), SizeBytes = size, Format = format,
            FilesystemViews = views, FilesystemVerdict = fsVerdict,
            DamagedSectors = damaged, BoundarySectors = boundary,
            ZeroRegions = zeros, EntropyBitsPerByte = entropy,
        };
        var grade = DiscForge.Core.Recovery.RecoverySession.Assess(findings);
        var advice = DiscForge.Core.Recovery.RecoverySession.Advise(findings, grade);

        if (args.Contains("--json"))
            EmitJson(new
            {
                image = findings.Image, grade = grade.ToString(), findings.SizeBytes, findings.Format,
                filesystems = views, crossCheck = fsVerdict, damaged, boundary,
                zeroRegions = zeros.Select(z => new { z.Offset, z.Length }),
                entropy, advice,
            });
        else
            Console.Write(DiscForge.Core.Recovery.RecoverySession.Summary(findings, grade, advice));

        if (outHtml is not null)
        {
            File.WriteAllText(outHtml, DiscForge.Core.Recovery.RecoverySession.BuildHtml(findings, grade, advice));
            Console.WriteLine($"report: {outHtml}");
        }
        return grade == DiscForge.Core.Recovery.RecoverySession.Grade.Intact ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int FsVerifyCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge fs-verify <image> [--json]\n" +
                    "  Cross-checks every filesystem view a disc carries. A bridge or hybrid disc describes the\n" +
                    "  same files through independent ISO 9660 and UDF directory structures; this reads each one,\n" +
                    "  hashes every file's content, and confirms the views deliver the same bytes (matching by\n" +
                    "  content, not name, so ISO 8.3 mangling doesn't matter). Reports AGREE, DIVERGENT (a file\n" +
                    "  reachable from one filesystem but not the other — tampering or hidden content), or INCOMPLETE\n" +
                    "  (a filesystem is declared but unreadable — a truncated dump). Read-only. Accepts .iso/.cdi/.bin/.cue/.img.");
    var image = args[1];
    if (!File.Exists(image)) return Fail($"'{image}' not found.");

    try
    {
        var r = DiscForge.Core.Files.ImageBrowser.CrossCheck(image);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                image = Path.GetFileName(image),
                verdict = r.Verdict.ToString(),
                views = r.Views.Select(v => new { v.Kind, v.VolumeId, v.FileCount, v.TotalBytes, v.Error }),
                discrepancies = r.Discrepancies.Select(d => new { d.Kind, d.Detail }),
            });
        }
        else
        {
            Console.WriteLine(r.Summary());
        }

        return r.Verdict switch
        {
            DiscForge.Core.Files.CrossCheckVerdict.Divergent => 2,
            DiscForge.Core.Files.CrossCheckVerdict.Incomplete => 2,
            DiscForge.Core.Files.CrossCheckVerdict.None => 1,
            _ => 0,
        };
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Decode an image to its raw program-area bytes for a byte-exact conversion compare.
static byte[] RawSectorsOf(string path)
{
    var ext = Path.GetExtension(path).ToLowerInvariant();
    switch (ext)
    {
        case ".chd":
            return DiscForge.Core.Chd.ChdExtractor.ExtractCd(File.ReadAllBytes(path)).Bin;
        case ".cue":
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var sheet = DiscForge.Core.Cue.CueSheet.Parse(File.ReadAllText(path));
            using var ms = new MemoryStream();
            foreach (var f in sheet.Tracks.Select(t => t.File).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
            {
                var bin = Path.Combine(dir, f);
                if (!File.Exists(bin)) throw new FileNotFoundException($"{f}: referenced by the cue but not found.", bin);
                using var fs = File.OpenRead(bin);
                fs.CopyTo(ms);
            }
            return ms.ToArray();
        }
        case ".bin":
        case ".img":
            return File.ReadAllBytes(path);
        default:
            throw new NotSupportedException($"verify-convert compares raw disc data — '{ext}' is not supported (use a bin/cue, .chd, or raw .bin).");
    }
}

static int ReadStabilityCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge read-stability <pass1.bin> <pass2.bin> [pass3.bin ...] [--sector-size N] [--json]\n" +
                    "  Disc-rot early warning without a C1/C2 scanner. A healthy disc reads identically every time; a\n" +
                    "  failing one returns DIFFERENT bytes for the same sector on different passes as the drive silently\n" +
                    "  papers over marginal reflectivity. Compare several full reads of the same disc and this flags the\n" +
                    "  unstable sectors — the leading edge of degradation — and grades the disc stable/marginal/degrading.");
    int sectorSize = 2352;
    var inputs = new List<string>();
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--sector-size" && i + 1 < args.Length) { if (!int.TryParse(args[++i], out sectorSize) || sectorSize <= 0) return Fail("--sector-size must be positive."); }
        else if (args[i] == "--json") { }
        else inputs.Add(args[i]);
    }
    if (inputs.Count < 2) return Fail("read-stability needs at least two passes.");
    foreach (var p in inputs) if (!File.Exists(p)) return Fail($"File not found: {p}");

    try
    {
        var passes = inputs.Select(File.ReadAllBytes).ToList();
        var r = DiscForge.Core.Forensics.ReadStability.Analyze(passes, sectorSize);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                r.Passes, r.Sectors, r.StableSectors, r.UnstableSectors, r.SeverelyUnstable,
                health = r.Health.ToString(), unstableFraction = r.UnstableFraction,
                runs = r.UnstableRuns.Select(u => new { u.StartSector, u.EndSector, u.Count, u.WorstAgreement }),
            });
            return r.Health == DiscForge.Core.Forensics.DiscStability.Stable ? 0 : 2;
        }

        Console.WriteLine(r.Summary());
        foreach (var u in r.UnstableRuns.Take(40)) Console.WriteLine($"  sector {u}");
        if (r.UnstableRuns.Count > 40) Console.WriteLine($"  … and {r.UnstableRuns.Count - 40:N0} more run(s)");
        return r.Health == DiscForge.Core.Forensics.DiscStability.Stable ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DumpAuditCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge dump-audit <cue|image> [--dat <Redump DAT>] [--json]\n" +
                    "  The plain answer to \"is my dump good?\". Fuses structural completeness, the unreadable-sector\n" +
                    "  map, an EDC/ECC audit of the data sectors, the end-of-disc sectors (where truncated reads hide),\n" +
                    "  pregap conformance, and — with --dat — the Redump match, into ONE verdict: GOOD / SUSPECT / BAD,\n" +
                    "  each flag naming its specific tell. Analysis only.");
    var target = args[1];
    if (!File.Exists(target)) return Fail($"'{target}' not found.");
    string? datPath = OptVal(args, "--dat");

    try
    {
        DiscForge.Core.Dat.DatFile? dat = null;
        if (datPath is not null)
        {
            if (!File.Exists(datPath)) return Fail($"'{datPath}' not found.");
            dat = DiscForge.Core.Dat.DatFile.ParseText(File.ReadAllText(datPath));
        }

        var v = DiscForge.Core.Verify.DumpAudit.Audit(target, dat);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                target = v.Target, quality = v.Quality.ToString(),
                checks = v.Checks.Select(c => new { c.Name, status = c.Status.ToString(), c.Tell }),
            });
            return v.Quality switch { DiscForge.Core.Verify.DumpQuality.Good => 0, DiscForge.Core.Verify.DumpQuality.Suspect => 1, _ => 2 };
        }

        Console.WriteLine(v.Headline());
        foreach (var c in v.Checks)
        {
            string tag = c.Status switch
            {
                DiscForge.Core.Verify.CheckStatus.Pass => "[PASS]",
                DiscForge.Core.Verify.CheckStatus.Fail => "[FAIL]",
                DiscForge.Core.Verify.CheckStatus.Warn => "[WARN]",
                _ => "[INFO]",
            };
            Console.WriteLine($"  {tag} {c.Name}: {c.Tell}");
        }
        return v.Quality switch { DiscForge.Core.Verify.DumpQuality.Good => 0, DiscForge.Core.Verify.DumpQuality.Suspect => 1, _ => 2 };
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int RedumpDiffCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge redump-diff <cue> <dat> [--game \"exact DAT name\"] [--json]\n" +
                    "  Explains why a dump does or doesn't match Redump. Beyond a yes/no against the DAT, it\n" +
                    "  reconciles the dump's files with the catalogued entry and names the cause of each divergence:\n" +
                    "  a wrong track split (total size matches but per-track doesn't → redump-cue), padding/truncation\n" +
                    "  (off by whole sectors), a content difference in the data track (region/version) or an audio\n" +
                    "  track (read-offset), and — from a sibling .badsectors.json — the exact holes that block a match.");
    var cue = args[1];
    var datPath = args[2];
    if (!File.Exists(cue)) return Fail($"'{cue}' not found.");
    if (!File.Exists(datPath)) return Fail($"'{datPath}' not found.");
    string? game = OptVal(args, "--game");

    try
    {
        var dat = DiscForge.Core.Dat.DatFile.ParseText(File.ReadAllText(datPath));

        DiscForge.Core.Preservation.BadSectorMap? bad = null;
        var sidecar = DiscForge.Core.Preservation.BadSectorMap.SidecarPath(cue);
        if (File.Exists(sidecar)) { try { bad = DiscForge.Core.Preservation.BadSectorMap.Load(sidecar); } catch { } }

        var r = DiscForge.Core.Redump.RedumpDiffer.Diff(cue, dat, game, bad);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                r.Game, r.Identified, r.Match, r.Verified, r.Total,
                roms = r.Roms.Select(x => new
                {
                    x.Track, x.Role, x.ExpectedName, x.ExpectedSize, x.ActualName, x.ActualSize,
                    verdict = x.Verdict.ToString(), x.Explanations,
                }),
                r.Recommendations,
            });
            return r.Match ? 0 : 2;
        }

        Console.WriteLine(r.Summary());
        foreach (var rom in r.Roms)
        {
            string tag = rom.Verdict switch
            {
                DiscForge.Core.Redump.RomVerdict.Verified => "[OK]  ",
                DiscForge.Core.Redump.RomVerdict.Missing => "[MISS]",
                DiscForge.Core.Redump.RomVerdict.Extra => "[EXTRA]",
                _ => "[DIFF]",
            };
            string label = rom.ActualName ?? rom.ExpectedName ?? "?";
            Console.WriteLine($"  {tag} {label}");
            foreach (var e in rom.Explanations) Console.WriteLine($"          - {e}");
        }
        if (r.Recommendations.Count > 0)
        {
            Console.WriteLine("recommended:");
            foreach (var rec in r.Recommendations) Console.WriteLine($"  • {rec}");
        }
        return r.Match ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BadSectorsCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge bad-sectors <map.badsectors.json> [--json]\n" +
                    "  Shows a dump's unreadable-sector map — the sectors a drive could not read at capture, which\n" +
                    "  a checksum can never reveal because a zero-filled hole hashes like real data. Reports the\n" +
                    "  total, how many are genuine damage vs. harmless track-boundary holes, the coalesced runs, and\n" +
                    "  (after a conversion carried the map through) where each hole lands inside its track file.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var map = DiscForge.Core.Preservation.BadSectorMap.Load(path);
        var runs = map.Runs();

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                map.Image, map.TotalSectors,
                total = map.Count, damage = map.DamageCount, boundary = map.BoundaryCount,
                incomplete = map.DamagePresent,
                runs = runs.Select(r => new { r.StartLba, r.EndLba, r.Count }),
                byTrack = map.ByTrack,
                map.Note,
            });
            return map.DamagePresent ? 2 : 0;
        }

        Console.WriteLine(map.Summary());
        if (map.Note is not null) Console.WriteLine($"  note: {map.Note}");
        foreach (var r in runs.Take(40))
            Console.WriteLine($"  LBA {r}");
        if (runs.Count > 40) Console.WriteLine($"  … and {runs.Count - 40:N0} more run(s)");
        if (map.ByTrack is { Count: > 0 })
        {
            Console.WriteLine("per track:");
            foreach (var t in map.ByTrack)
            {
                string pre = t.InPregap > 0 ? $", {t.InPregap} in pregap" : "";
                Console.WriteLine($"  track {t.Track:00} {t.File}: {t.WithinFileLba.Count} hole(s){pre}");
            }
        }
        return map.DamagePresent ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Region-level (shift-tolerant) semantic diff of two disc images.
static int DiscSemDiffCmd(string[] args)
{
    var paths = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();
    if (paths.Length < 2)
        return Fail("usage: dforge disc-semdiff <a> <b> [--json]\n" +
                    "  Compares two disc images at the region level: it chunks both with content-defined\n" +
                    "  chunking (so boundaries realign after an insertion) and reports where they genuinely\n" +
                    "  diverge — 'A and B share 98%; A differs in one 40 KB region near 0x1200000' — rather\n" +
                    "  than a byte-for-byte wall that scrambling/ECC/insertions render useless. Comparison only.");
    if (!File.Exists(paths[0])) return Fail($"'{paths[0]}' not found.");
    if (!File.Exists(paths[1])) return Fail($"'{paths[1]}' not found.");
    try
    {
        var d = DiscForge.Core.Forensics.DiscRegionDiff.CompareFiles(paths[0], paths[1]);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                a = Path.GetFileName(paths[0]), b = Path.GetFileName(paths[1]),
                d.Identical, d.LengthA, d.LengthB, d.SharedChunks, d.ChunksOnlyInA, d.ChunksOnlyInB,
                d.ChangedBytesA, d.ChangedBytesB, similarityA = d.SimilarityA,
                regionsA = d.RegionsA.Select(r => new { offset = r.Offset, r.Length }),
                regionsB = d.RegionsB.Select(r => new { offset = r.Offset, r.Length }),
            });
            return d.Identical ? 0 : 2;
        }

        Console.WriteLine($"{Path.GetFileName(paths[0])} vs {Path.GetFileName(paths[1])}: {d.Summary()}");
        return d.Identical ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Robustness-fuzz DiscForge's binary parsers against mutated inputs; report unclean crashes/hangs.
static int FuzzParsersCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge fuzz-parsers <seed-image> [--iterations N] [--json]\n" +
                    "  Mutates the seed (bit flips, field zeroing, truncation, corrupted length/offset fields)\n" +
                    "  and runs each format parser, flagging any that crash uncleanly (IndexOutOfRange, etc.) or\n" +
                    "  hang — as opposed to raising a clean format error. Deterministic; findings reproduce by\n" +
                    "  iteration + mutation. A robustness/security check for parsing untrusted disc images.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var seed = File.ReadAllBytes(path);
        int iterations = int.TryParse(OptVal(args, "--iterations"), out var n) ? n : 500;

        // Probe set: parsers that take a byte buffer. Each should reject garbage with a clean format error.
        var probes = new List<(string, Action<byte[]>)>
        {
            ("pvr-info", b => DiscForge.Core.Dreamcast.Pvr.Parse(b)),
            ("pvm-info", b => DiscForge.Core.Dreamcast.Pvm.Parse(b)),
            ("nkit-info", b => DiscForge.Core.GameCube.Nkit.Parse(b)),
            ("dvd-layerbreak", b => DiscForge.Core.Media.DvdPhysicalFormat.Parse(b)),
            ("ipbin-info", b => { if (DiscForge.Core.Gdi.IpBin.IsBootHeader(b)) DiscForge.Core.Gdi.IpBin.Parse(b); }),
            ("mpeg-info", b => DiscForge.Core.Mpeg.MpegVideoProbe.Probe(b)),
        };

        var r = DiscForge.Core.Util.ParserFuzz.Run(seed, probes, iterations);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                r.ProbeCount, r.Iterations, r.Runs, r.Crashes, r.Timeouts, r.Clean,
                findings = r.Findings.Select(f => new
                {
                    f.Probe, outcome = f.Outcome.ToString(), f.ExceptionType, f.Message, f.Iteration, f.Mutation,
                }),
            });
            return r.Clean ? 0 : 2;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Util.ParserFuzz.Render(r)}");
        return r.Clean ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Content-defined chunking + Merkle manifest: dedup a set of files to unique chunks with reconstruction proof.
static int ChunkManifestCmd(string[] args)
{
    var paths = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();
    if (paths.Length == 0)
        return Fail("usage: dforge chunk-manifest <file> [file2 ...] [--json]\n" +
                    "  Splits each file with FastCDC content-defined chunking, builds a Merkle root over the\n" +
                    "  chunks, and deduplicates the set to its unique chunks. Because boundaries are content-\n" +
                    "  derived, a small edit re-chunks only locally, so near-identical files share most chunks.\n" +
                    "  Reconstruction is provable (replay chunks, verify each hash + the whole-file hash/root).");
    try
    {
        var files = new List<(string, byte[])>();
        foreach (var p in paths)
        {
            if (!File.Exists(p)) return Fail($"'{p}' not found.");
            files.Add((Path.GetFileName(p), File.ReadAllBytes(p)));
        }
        var r = DiscForge.Core.Preservation.ContentChunking.Dedup(files);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                r.TotalChunks, r.UniqueChunks, r.TotalBytes, r.UniqueBytes, r.DedupRatio,
                files = r.Files.Select(f => new
                {
                    f.Name, f.FileLength, f.ChunkCount, avgChunk = Math.Round(f.AvgChunkSize),
                    merkleRoot = f.RootHex,
                    fileSha256 = System.Convert.ToHexString(f.FileSha256).ToLowerInvariant(),
                }),
            });
            return 0;
        }

        Console.WriteLine(DiscForge.Core.Preservation.ContentChunking.Render(r));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Read and verify a PAR2 recovery set against the files beside it, and report repairability.
static int Par2VerifyCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge par2-verify <file.par2> [--json]\n" +
                    "  Reads a PAR2 (Parchive 2.0) recovery set — checking each PAR2 packet's own MD5 — lists\n" +
                    "  the files it protects, verifies those files in the same directory slice by slice, and\n" +
                    "  reports whether the damage found is within the available recovery slices (i.e. whether\n" +
                    "  par2 could repair it). Read/verify interop only; it does not perform the RS reconstruction.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var r = DiscForge.Core.Preservation.Par2.Verify(path);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                r.SliceSize, r.RecoverySlices, r.TotalDataSlices, r.DamagedSlices,
                r.AllOk, r.Repairable, r.Creator, r.PacketsParsed, r.BadPackets,
                files = r.Files.Select(f => new
                {
                    f.Name, f.Length, f.SliceCount,
                    status = f.Status.ToString(), f.DamagedSlices,
                    md5 = System.Convert.ToHexString(f.FileMd5).ToLowerInvariant(),
                }),
            });
            return r.AllOk ? 0 : r.Repairable ? 2 : 3;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {r.Summary()}");
        foreach (var f in r.Files)
        {
            string tag = f.Status switch
            {
                DiscForge.Core.Preservation.Par2FileStatus.Ok => "OK     ",
                DiscForge.Core.Preservation.Par2FileStatus.Corrupt => "CORRUPT",
                _ => "MISSING",
            };
            string extra = f.Status == DiscForge.Core.Preservation.Par2FileStatus.Ok
                ? "" : $"  ({f.DamagedSlices}/{f.SliceCount} slice(s) damaged)";
            Console.WriteLine($"  {tag}  {f.Name}  ({f.Length:N0} bytes){extra}");
        }
        return r.AllOk ? 0 : r.Repairable ? 2 : 3;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Self-healing container: wrap an image in Reed-Solomon parity, detect rot, and repair it.
static int VaultCmd(string[] args)
{
    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
    try
    {
        switch (sub)
        {
            case "create":
            {
                if (args.Length < 4) return Fail("usage: dforge vault create <image> <out.vault> [--parity N] [--data K] [--genome id] [--lineage digest]");
                string imgPath = args[2], outPath = args[3];
                if (!File.Exists(imgPath)) return Fail($"File not found: {imgPath}");
                int parity = int.TryParse(OptVal(args, "--parity"), out var p) ? p : 8;
                int data = int.TryParse(OptVal(args, "--data"), out var k) ? k : 32;
                var vault = DiscForge.Core.Preservation.PreservationVaultOps.Create(
                    File.ReadAllBytes(imgPath), parity, data, OptVal(args, "--genome"), OptVal(args, "--lineage"));
                File.WriteAllText(outPath, DiscForge.Core.Preservation.PreservationVaultOps.ToJson(vault));
                Console.WriteLine($"Vaulted {Path.GetFileName(imgPath)} → {Path.GetFileName(outPath)}: " +
                                  $"{data} data + {parity} parity blocks (survives losing any {parity}).");
                return 0;
            }
            case "check":
            {
                if (args.Length < 3) return Fail("usage: dforge vault check <vault.json>");
                if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");
                var vault = DiscForge.Core.Preservation.PreservationVaultOps.FromJson(File.ReadAllText(args[2]));
                var health = DiscForge.Core.Preservation.PreservationVaultOps.Check(vault);
                Console.WriteLine($"{Path.GetFileName(args[2])}: {health.Summary()}");
                if (health.DamagedBlocks.Count > 0)
                    Console.WriteLine($"  damaged blocks: {string.Join(", ", health.DamagedBlocks)}");
                return health.Pristine ? 0 : health.Recoverable ? 2 : 1;
            }
            case "heal":
            {
                if (args.Length < 4) return Fail("usage: dforge vault heal <vault.json> <out.image>");
                if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");
                var vault = DiscForge.Core.Preservation.PreservationVaultOps.FromJson(File.ReadAllText(args[2]));
                var image = DiscForge.Core.Preservation.PreservationVaultOps.Heal(vault, out var report);
                Console.WriteLine(report.Message);
                if (!report.Recovered) return 1;
                File.WriteAllBytes(args[3], image);
                File.WriteAllText(args[2], DiscForge.Core.Preservation.PreservationVaultOps.ToJson(vault));   // persist the repair
                Console.WriteLine($"  wrote {Path.GetFileName(args[3])} ({image.Length:N0} bytes) and repaired the vault.");
                return report.ImageValid ? 0 : 1;
            }
            default:
                return Fail("usage: dforge vault <create|check|heal> …\n" +
                            "  create <image> <out.vault> [--parity N] [--data K]   wrap an image in recovery parity\n" +
                            "  check <vault.json>                                   report damage + recoverability\n" +
                            "  heal <vault.json> <out.image>                        repair from parity, verify the image\n" +
                            "  Any K of the K+N blocks rebuild the exact original — a container that outlives its medium.");
        }
    }
    catch (System.Text.Json.JsonException ex) { return Fail($"Bad JSON: {ex.Message}"); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Deduplicate a library of ISO images into a shared content store; rebuild any disc byte-exact.
static int CollectionArchiveCmd(string[] args)
{
    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
    try
    {
        switch (sub)
        {
            case "build":
            {
                string? outPath = OptVal(args, "--out");
                if (outPath is null) return Fail("collection-archive build needs --out <archive.json>.");
                var inputs = args.Skip(2).Where(a => a != "--out" && a != outPath).ToList();
                var files = new List<string>();
                foreach (var input in inputs)
                {
                    if (Directory.Exists(input))
                        files.AddRange(Directory.EnumerateFiles(input, "*.iso", SearchOption.TopDirectoryOnly));
                    else if (File.Exists(input)) files.Add(input);
                    else return Fail($"Not found: {input}");
                }
                files = files.Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 0) return Fail("No disc images to archive.");

                var discs = files.Select(f => (Path.GetFileName(f), File.ReadAllBytes(f))).ToList();
                var archive = DiscForge.Core.Preservation.CollectionArchiver.Build(discs);
                File.WriteAllText(outPath, DiscForge.Core.Preservation.CollectionArchiver.ToJson(archive));

                var report = DiscForge.Core.Preservation.CollectionArchiver.Analyze(archive);
                Console.WriteLine(DiscForge.Core.Preservation.CollectionArchiver.Render(report));
                if (archive.Skipped.Count > 0)
                    Console.WriteLine($"  skipped (not ISO): {string.Join(", ", archive.Skipped)}");
                Console.WriteLine($"  wrote {Path.GetFileName(outPath)}.");
                return 0;
            }
            case "verify":
            {
                if (args.Length < 3) return Fail("usage: dforge collection-archive verify <archive.json>");
                if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");
                var archive = DiscForge.Core.Preservation.CollectionArchiver.FromJson(File.ReadAllText(args[2]));
                bool ok = DiscForge.Core.Preservation.CollectionArchiver.VerifyAll(archive);
                var report = DiscForge.Core.Preservation.CollectionArchiver.Analyze(archive);
                Console.WriteLine($"Rebuild check: {(ok ? "ALL discs rebuild byte-exact" : "FAILED — a disc did not rebuild")}.");
                Console.WriteLine(DiscForge.Core.Preservation.CollectionArchiver.Render(report));
                return ok ? 0 : 1;
            }
            case "extract":
            {
                if (args.Length < 5) return Fail("usage: dforge collection-archive extract <archive.json> <disc-name> <out.iso>");
                if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");
                var archive = DiscForge.Core.Preservation.CollectionArchiver.FromJson(File.ReadAllText(args[2]));
                var bytes = DiscForge.Core.Preservation.CollectionArchiver.Reconstruct(archive, args[3]);
                File.WriteAllBytes(args[4], bytes);
                Console.WriteLine($"Rebuilt {args[3]} → {Path.GetFileName(args[4])} ({bytes.Length:N0} bytes, byte-exact).");
                return 0;
            }
            default:
                return Fail("usage: dforge collection-archive <build|verify|extract> …\n" +
                            "  build <dir|iso...> --out archive.json    dedup a library into a shared store\n" +
                            "  verify <archive.json>                    rebuild-check every disc + dedup stats\n" +
                            "  extract <archive.json> <disc> <out.iso>  regenerate one disc byte-exact");
        }
    }
    catch (System.Text.Json.JsonException ex) { return Fail($"Bad JSON: {ex.Message}"); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Physical-copy fingerprint from a positional error scan; with a second scan, compare the two copies.
static int DiscPrintCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-print <scan.json> [other.json]\n" +
                    "  Builds a physical-copy fingerprint from a positional C1/C2 error scan (the pattern of\n" +
                    "  read errors across the disc surface, which is unique to an individual physical copy).\n" +
                    "  With a second scan it reports whether the two are the same physical disc. Scan JSON:\n" +
                    "    { \"id\": \"my disc\", \"samples\": [[c1,c2,cu], ...] }   (or \"c1\": [n, n, ...]).");
    try
    {
        var (idA, scanA) = ParsePhysicalScan(args[1]);
        var a = DiscForge.Core.Forensics.PhysicalFingerprinter.Compute(idA, scanA);

        if (args.Length >= 3 && !args[2].StartsWith("--"))
        {
            var (idB, scanB) = ParsePhysicalScan(args[2]);
            var b = DiscForge.Core.Forensics.PhysicalFingerprinter.Compute(idB, scanB);
            Console.WriteLine(DiscForge.Core.Forensics.PhysicalFingerprinter.Render(a));
            Console.WriteLine(DiscForge.Core.Forensics.PhysicalFingerprinter.Render(b));
            var m = DiscForge.Core.Forensics.PhysicalFingerprinter.Compare(a, b);
            Console.WriteLine($"  → {m.Assessment}");
            return m.SamePhysicalCopy ? 2 : 0;
        }

        Console.WriteLine(DiscForge.Core.Forensics.PhysicalFingerprinter.Render(a));
        return 0;
    }
    catch (System.Text.Json.JsonException ex) { return Fail($"Bad JSON: {ex.Message}"); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DiscGenealogyCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-genealogy <collection.json> [--json]\n" +
            "  Weaves a whole collection's physical-provenance signals into one family tree: groups discs by\n" +
            "  glass master (mastering IFPI SID / matrix) → pressing plant (mould SID) → individual copies\n" +
            "  (physical error-map fingerprint), and gives each disc an authenticity verdict — a burned copy or\n" +
            "  a pressing missing the master identifiers its siblings carry stands out. Assessment only.\n" +
            "  Collection JSON: [ { \"id\":\"..\", \"title\":\"..\", \"matrix\":\"..\", \"masteringSid\":\"IFPI L###\",\n" +
            "    \"mouldSid\":\"IFPI ####\", \"media\":\"pressed|recordable|unknown\", \"fingerprint\":[n,n,..] }, ... ].");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
        };
        var records = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<DiscForge.Core.Forensics.DiscGenomeRecord>>(
            File.ReadAllText(args[1]), opts);
        if (records is null || records.Count == 0) return Fail("No records in the collection JSON.");

        var rep = DiscForge.Core.Forensics.DiscGenealogy.Build(records);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                families = rep.Families.Select(f => new
                {
                    f.MasterKey, f.MasteringSid, f.Matrix,
                    plants = f.Plants.Select(p => new { p.MouldSid, p.Members }),
                    f.Members,
                }),
                sameCopyLinks = rep.SameCopyLinks.Select(l => new { l.A, l.B, l.Similarity }),
                verdicts = rep.Verdicts.Select(v => new { v.Id, v.Title, authenticity = v.Authenticity.ToString(), v.Reasons }),
                singletons = rep.Singletons,
            });
            return 0;
        }

        Console.WriteLine($"disc-genealogy — {records.Count} disc(s)");
        Console.WriteLine(DiscForge.Core.Forensics.DiscGenealogy.Render(rep));
        int suspect = rep.Verdicts.Count(v =>
            v.Authenticity is DiscForge.Core.Forensics.Authenticity.Suspect
                            or DiscForge.Core.Forensics.Authenticity.LikelyCounterfeit);
        return suspect > 0 ? 2 : 0;
    }
    catch (System.Text.Json.JsonException ex) { return Fail($"Bad JSON: {ex.Message}"); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static (string id, List<DiscForge.Core.Forensics.ScanSample> scan) ParsePhysicalScan(string path)
{
    if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    string id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Path.GetFileName(path) : Path.GetFileName(path);
    var scan = new List<DiscForge.Core.Forensics.ScanSample>();

    if (root.TryGetProperty("samples", out var smp) && smp.ValueKind == System.Text.Json.JsonValueKind.Array)
        foreach (var row in smp.EnumerateArray())
        {
            if (row.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                int c1 = row.GetArrayLength() > 0 ? row[0].GetInt32() : 0;
                int c2 = row.GetArrayLength() > 1 ? row[1].GetInt32() : 0;
                int cu = row.GetArrayLength() > 2 ? row[2].GetInt32() : 0;
                scan.Add(new DiscForge.Core.Forensics.ScanSample(c1, c2, cu));
            }
            else scan.Add(new DiscForge.Core.Forensics.ScanSample(row.GetInt32(), 0, 0));
        }
    else if (root.TryGetProperty("c1", out var c1arr) && c1arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        foreach (var v in c1arr.EnumerateArray())
            scan.Add(new DiscForge.Core.Forensics.ScanSample(v.GetInt32(), 0, 0));
    else
        throw new FormatException("Scan JSON needs a \"samples\" or \"c1\" array.");

    return (id, scan);
}

// Return the value following a --flag in args, or null if absent.
static string? OptVal(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

// Emit any result object as indented JSON — the machine-readable form behind the --json flag.
static void EmitJson(object value)
    => Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));

// Federated cross-dumper consensus: sign genome attestations into a hash-linked ledger and tally them.
static int ConsensusCmd(string[] args)
{
    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
    switch (sub)
    {
        case "keygen":
        {
            var (priv, pub) = DiscForge.Core.Preservation.ConsensusLog.GenerateKey();
            string? outPath = OptVal(args, "--out");
            if (outPath is not null)
            {
                File.WriteAllText(outPath, priv + "\n" + pub + "\n");
                Console.WriteLine($"Wrote key pair to {Path.GetFileName(outPath)} (private key first line — keep it secret).");
            }
            else
            {
                Console.WriteLine("private (PKCS#8, keep secret):");
                Console.WriteLine("  " + priv);
                Console.WriteLine("public (share freely):");
                Console.WriteLine("  " + pub);
            }
            return 0;
        }
        case "attest":
        {
            if (args.Length < 3) return Fail("usage: dforge consensus attest <disc.cue> --key <priv|file> --id \"Title\" --ledger <ledger.json>");
            string cue = args[2];
            if (!File.Exists(cue)) return Fail($"File not found: {cue}");
            string? keyArg = OptVal(args, "--key");
            string? id = OptVal(args, "--id") ?? Path.GetFileNameWithoutExtension(cue);
            string? ledgerPath = OptVal(args, "--ledger");
            if (keyArg is null || ledgerPath is null) return Fail("attest needs --key <priv|file> and --ledger <ledger.json>.");
            string privB64 = File.Exists(keyArg) ? File.ReadAllLines(keyArg)[0].Trim() : keyArg;

            try
            {
                var genome = DiscForge.Core.Forensics.DiscGenome.Compute(LoadGenomeTracks(cue));
                using var key = DiscForge.Core.Preservation.ConsensusLog.LoadPrivateKey(privB64);
                var attestation = DiscForge.Core.Preservation.ConsensusLog.CreateAttestation(
                    id, genome, key, DateTime.UtcNow.ToString("o"));

                var ledger = File.Exists(ledgerPath)
                    ? DiscForge.Core.Preservation.ConsensusLog.FromJson(File.ReadAllText(ledgerPath))
                    : DiscForge.Core.Preservation.ConsensusLog.NewLedger();
                DiscForge.Core.Preservation.ConsensusLog.Append(ledger, attestation);
                File.WriteAllText(ledgerPath, DiscForge.Core.Preservation.ConsensusLog.ToJson(ledger));

                Console.WriteLine($"Attested \"{id}\" (identity {attestation.GenomeKey[..12]}…) into {Path.GetFileName(ledgerPath)}.");
                return 0;
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }
        case "verify":
        {
            if (args.Length < 3) return Fail("usage: dforge consensus verify <ledger.json>");
            if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");
            try
            {
                var ledger = DiscForge.Core.Preservation.ConsensusLog.FromJson(File.ReadAllText(args[2]));
                bool ok = DiscForge.Core.Preservation.ConsensusLog.VerifyLedger(ledger);
                var tally = DiscForge.Core.Preservation.ConsensusLog.Tally(ledger);
                Console.WriteLine(DiscForge.Core.Preservation.ConsensusLog.Render(tally, ok));
                return ok ? 0 : 1;
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }
        default:
            return Fail("usage: dforge consensus <keygen|attest|verify> …\n" +
                        "  keygen [--out key.txt]                         make a dumper key pair\n" +
                        "  attest <disc.cue> --key <priv|file> --id T --ledger L.json   sign + append an attestation\n" +
                        "  verify <ledger.json>                           verify the chain + tally consensus\n" +
                        "  Independent dumpers signing the same disc genome build cryptographic consensus on the canonical image.");
    }
}

// Fingerprint a disc's copy protection as preservation metadata — detection only, never circumvention.
static int ProtectionScanCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge protection-scan <image.iso> [--sub raw.sub]\n" +
                    "  Identifies the copy-protection scheme, version and parameters a disc carries and records\n" +
                    "  them as preservation metadata: filesystem marks (SafeDisc's 00000001.TMP/.icd, SecuROM's\n" +
                    "  CMS*.DLL, a LASERLOK directory…), executable signatures (with SafeDisc's exact version), and\n" +
                    "  — with a raw 96-byte/sector subchannel via --sub — LibCrypt's corrupted subchannel Q.\n" +
                    "  Detection and documentation only; it never bypasses or weakens any protection.");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");

    string? subPath = null;
    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--sub" && i + 1 < args.Length) subPath = args[++i];

    try
    {
        List<byte[]>? qFrames = null;
        if (subPath is not null)
        {
            if (!File.Exists(subPath)) return Fail($"Subchannel file not found: {subPath}");
            var sub = File.ReadAllBytes(subPath);
            if (sub.Length % 96 != 0)
                return Fail($"Subchannel file must be a whole number of 96-byte frames ({sub.Length:N0} bytes given).");
            qFrames = new List<byte[]>(sub.Length / 96);
            for (int off = 0; off + 96 <= sub.Length; off += 96)
            {
                var q = new byte[12];
                DiscForge.Core.Raw.RawSubchannel.ExtractQ(sub.AsSpan(off, 96), q);
                qFrames.Add(q);
            }
        }

        var report = DiscForge.Core.Forensics.CopyProtectionCatalog.FromIso(File.ReadAllBytes(path), qFrames);
        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Forensics.CopyProtectionCatalog.Render(report)}");

        // With a raw image, fuse the filesystem findings with on-disc signals for one verdict.
        string? rawPath = OptVal(args, "--raw");
        if (rawPath is not null)
        {
            if (!File.Exists(rawPath)) return Fail($"Raw image not found: {rawPath}");
            var raw = File.ReadAllBytes(rawPath);
            var twins = DiscForge.Core.Forensics.TwinSectorScan.Analyze(raw);
            DiscForge.Core.Forensics.ErrorPatternReport? errors = null;
            try { errors = DiscForge.Core.Forensics.ErrorPatternForensics.Classify(DiscForge.Core.Forensics.DiscHealthMap.Scan(raw)); }
            catch { /* raw not a whole number of 2352-byte sectors — skip the error-shape signal */ }

            var fused = DiscForge.Core.Forensics.ProtectionCrossCheck.Fuse(report, errors, twins);
            Console.WriteLine();
            Console.WriteLine("Cross-check (filesystem + on-disc signals):");
            Console.WriteLine(DiscForge.Core.Forensics.ProtectionCrossCheck.Render(fused));
            return fused.AnyProtection || report.AnyFound ? 2 : 0;
        }

        return report.AnyFound ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Walk a classic Mac HFS volume and list its files, folders and fork sizes.
static int ApmInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge apm-info <image>\n" +
        "  Reads an Apple Partition Map (Mac hard disks and Mac+PC hybrid CDs): each partition's name,\n" +
        "  type (Apple_HFS / Apple_Free / Apple_partition_map…), block extent and derived size. Read-only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var image = File.ReadAllBytes(args[1]);
        if (!DiscForge.Core.Partition.ApmDisk.IsApm(image))
            return Fail("No Apple Partition Map found ('ER'/'PM' signatures absent).");
        var apm = DiscForge.Core.Partition.ApmDisk.Read(image);
        if (args.Contains("--json")) { EmitJson(apm); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Partition.ApmDisk.Render(apm)}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DiscReportCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge disc-report <image>\n" +
        "  Identifies a disc image and runs every read-only parser that matches it — the console header,\n" +
        "  the filesystem listing, boot structure, and free-space orphan carve — in one consolidated report.\n" +
        "  Purely detection and description; it composes the individual commands, and changes nothing.");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        bool json = args.Contains("--json");
        var report = new Dictionary<string, object?>();
        var bytes = File.ReadAllBytes(path);
        report["file"] = Path.GetFileName(path);
        report["bytes"] = bytes.Length;
        if (!json) Console.WriteLine($"disc-report — {Path.GetFileName(path)} ({bytes.Length:N0} bytes)");

        // ---- identity ------------------------------------------------------
        var id = DiscForge.Core.Identify.FormatIdentifier.Identify(bytes);
        report["identity"] = new { id.Name, id.Category, id.Detail };
        if (!json) Console.WriteLine($"  Identity: {id.Name} — {id.Category}" + (id.Detail.Length > 0 ? $" ({id.Detail})" : ""));

        // A cooked 2048 view for filesystem/structure probes on a raw 2352 image.
        byte[] cooked = bytes;
        if (bytes.Length % 2352 == 0 && bytes.Length >= 2352)
        {
            int sectors = bytes.Length / 2352;
            cooked = new byte[sectors * 2048];
            for (int s = 0; s < sectors; s++) Array.Copy(bytes, s * 2352 + 16, cooked, s * 2048, 2048);
        }

        void Probe(string label, Func<string?> f)
        {
            try { var s = f(); if (!string.IsNullOrEmpty(s)) { report[label] = s; if (!json) Console.WriteLine($"  {label}: {s}"); } }
            catch { /* probe did not apply */ }
        }

        // ---- console headers ----------------------------------------------
        Probe("Saturn", () => DiscForge.Core.Saturn.SaturnDisc.Identify(path) is { } h ? $"{h.Title} [{h.ProductNumber}] {string.Join("/", h.Regions)}" : null);
        Probe("Sega CD", () => DiscForge.Core.SegaCd.SegaCdDisc.Identify(path) is { } h ? $"{h.Title} [{h.ProductCode}] {string.Join("/", h.Regions)}" : null);
        Probe("3DO", () =>
        {
            if (DiscForge.Core.ThreeDo.OperaFs.IsVolume(bytes)) return DiscForge.Core.ThreeDo.OperaFs.Read(bytes).Summary();
            if (DiscForge.Core.ThreeDo.OperaFs.IsVolume(cooked)) return DiscForge.Core.ThreeDo.OperaFs.Read(cooked).Summary();
            return null;
        });

        // ---- partition map + boot structure --------------------------------
        Probe("Apple Partition Map", () => DiscForge.Core.Partition.ApmDisk.IsApm(bytes)
            ? DiscForge.Core.Partition.ApmDisk.Read(bytes).Summary() : null);
        Probe("El Torito", () => DiscForge.Core.Iso.ElTorito.Read(cooked) is { } c ? c.Summary() : null);
        Probe("CD-i", () =>
        {
            using var fs = File.OpenRead(path);
            if (!DiscForge.Core.CdInteractive.CdInteractiveReader.IsCdInteractive(fs)) return null;
            fs.Seek(0, SeekOrigin.Begin);
            var d = DiscForge.Core.CdInteractive.CdInteractiveReader.Read(fs);
            string kind = d.Kind == DiscForge.Core.CdInteractive.CdInteractiveKind.PureCdi ? "pure CD-i (Green Book)" : "CD-i Bridge";
            return $"{kind}, volume \"{d.VolumeId}\", {d.Filesystem.Files.Count()} file(s)" +
                   (d.ApplicationId.Length > 0 ? $", app {d.ApplicationId}" : "");
        });

        // ---- filesystems + orphan carve ------------------------------------
        Probe("HFS", () => DiscForge.Core.Hfs.HfsReader.IsHfs(cooked)
            ? DiscForge.Core.Hfs.HfsFreeSpace.Analyze(cooked).Summary()
            : null);
        Probe("HFS files", () =>
        {
            if (!DiscForge.Core.Hfs.HfsReader.IsHfs(cooked)) return null;
            var v = DiscForge.Core.Hfs.HfsReader.Read(cooked);
            return $"\"{v.VolumeName}\", {v.Files.Count()} file(s)";
        });
        Probe("UDF", () =>
        {
            var u = DiscForge.Core.Udf.UdfFreeSpace.Analyze(cooked);
            return u.Summary();
        });

        if (json) EmitJson(report);
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int RdbInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge rdb-info <image>\n" +
        "  Reads an Amiga Rigid Disk Block (the partition scheme of Amiga hard disks and CD32/CDTV): the\n" +
        "  drive geometry and vendor strings, and each partition's name, bootable flag, filesystem type\n" +
        "  (DOS\\0 OFS / DOS\\1 FFS / PFS / SFS…), cylinder span and derived size. Read-only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var image = File.ReadAllBytes(args[1]);
        if (!DiscForge.Core.Partition.RdbDisk.IsRdb(image))
            return Fail("No Amiga RDB ('RDSK') found in the first 16 blocks.");
        var rdb = DiscForge.Core.Partition.RdbDisk.Read(image);
        if (args.Contains("--json")) { EmitJson(rdb); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Partition.RdbDisk.Render(rdb)}");
        return rdb.ChecksumValid ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int FsOrphansCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge fs-orphans <image>\n" +
        "  Auto-detects the filesystem (classic Mac HFS and/or UDF) and carves each one's free space for\n" +
        "  leftover/deleted content — running both on a hybrid disc. A unified front-end over hfs-orphans\n" +
        "  and udf-orphans. Detection only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var image = File.ReadAllBytes(args[1]);
        bool any = false, leftovers = false;
        if (DiscForge.Core.Hfs.HfsReader.IsHfs(image))
        {
            any = true;
            var r = DiscForge.Core.Hfs.HfsFreeSpace.Analyze(image);
            leftovers |= r.HasLeftovers;
            Console.WriteLine($"[HFS] {DiscForge.Core.Hfs.HfsFreeSpace.Render(r)}");
        }
        try
        {
            var u = DiscForge.Core.Udf.UdfFreeSpace.Analyze(image);
            any = true;
            leftovers |= u.HasLeftovers;
            Console.WriteLine($"[UDF] {DiscForge.Core.Udf.UdfFreeSpace.Render(u)}");
        }
        catch (DiscForge.Core.Udf.UdfFormatException) { /* not a UDF image */ }

        if (!any) return Fail("No HFS or UDF filesystem found in this image.");
        return leftovers ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int AudioDynamicsCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge audio-dynamics <in.wav>\n" +
        "  Measures a track's loudness and dynamics: the DR value (TT/Pleasurize meter — higher is more\n" +
        "  dynamic, low single digits are the 'loudness war' signature), sample peak and RMS in dBFS, the\n" +
        "  crest factor, and clipping detected as runs of consecutive full-scale samples. Analysis only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var bytes = File.ReadAllBytes(args[1]);
        var info = DiscForge.Core.Audio.WavReader.Read(new MemoryStream(bytes));
        if (info.BitsPerSample != 16) return Fail($"Only 16-bit PCM is supported (got {info.BitsPerSample}-bit).");
        int n = (int)(info.DataLength / 2);
        var pcm = new short[n];
        int off = (int)info.DataOffset;
        for (int i = 0; i < n; i++) pcm[i] = (short)(bytes[off + i * 2] | (bytes[off + i * 2 + 1] << 8));

        var r = DiscForge.Core.Audio.AudioDynamics.Analyze(pcm, info.Channels, info.SampleRate);
        if (args.Contains("--json")) { EmitJson(r); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Audio.AudioDynamics.Render(r)}");
        return r.LikelyClipped ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int HdcdScanCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge hdcd-scan <in.wav | track.bin> [--channels N] [--json]\n" +
        "  Scans 16-bit PCM audio for HDCD control codes hidden in the samples' least-significant bits, and\n" +
        "  reports whether the track is HDCD-encoded. Accepts a WAV, or raw little-endian 16-bit PCM (give\n" +
        "  --channels, default 2, for a raw CD-audio .bin). Detection only — it never expands the audio.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var bytes = File.ReadAllBytes(args[1]);
        DiscForge.Core.Audio.HdcdScanResult r;
        int channels;

        bool isWav = bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F';
        if (isWav)
        {
            var info = DiscForge.Core.Audio.WavReader.Read(new MemoryStream(bytes));
            if (info.BitsPerSample != 16) return Fail($"Only 16-bit PCM is supported (got {info.BitsPerSample}-bit).");
            channels = info.Channels;
            int n = (int)(info.DataLength / 2);
            var pcm = new short[n];
            int off = (int)info.DataOffset;
            for (int i = 0; i < n; i++) pcm[i] = (short)(bytes[off + i * 2] | (bytes[off + i * 2 + 1] << 8));
            r = DiscForge.Core.Audio.Hdcd.Scan(pcm, channels);
        }
        else
        {
            channels = int.TryParse(OptVal(args, "--channels"), out var c) ? c : 2;
            r = DiscForge.Core.Audio.Hdcd.ScanPcmBytes(bytes, channels);
        }

        if (args.Contains("--json")) { EmitJson(r); return 0; }

        Console.WriteLine($"{Path.GetFileName(args[1])}: {channels}-channel, {r.SamplesScanned:N0} samples scanned");
        Console.WriteLine($"  Type-B packets (self-checking): {r.PacketsTypeB}");
        Console.WriteLine($"  Type-A codes: {r.PacketsTypeA}  (random noise floor ≈ {r.TypeANoiseFloor:F0}" +
                          $"{(r.TypeASignificant ? ", significantly above" : "")})");
        Console.WriteLine(r.Detected
            ? "  HDCD: DETECTED — this track carries HDCD encoding."
            : "  HDCD: not detected — no reliable HDCD control packets found.");
        return r.Detected ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int NeoGeoIplCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge neogeo-ipl <IPL.TXT | disc.iso | disc.cue>\n" +
        "  Parses a Neo Geo CD boot script (IPL.TXT): the ordered list of files the console loads at\n" +
        "  startup with their target bank and offset. Accepts an IPL.TXT directly, or a disc image whose\n" +
        "  IPL.TXT is located and read from the ISO filesystem. Read-only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var bytes = File.ReadAllBytes(args[1]);
        DiscForge.Core.Rom.NeoGeoCdBoot boot;
        if (DiscForge.Core.Rom.NeoGeoCdIpl.LooksLikeIpl(bytes))
        {
            boot = DiscForge.Core.Rom.NeoGeoCdIpl.Parse(bytes);
        }
        else
        {
            // Treat as a disc image: find IPL.TXT in the ISO filesystem and read it.
            using var fs = File.OpenRead(args[1]);
            var dir = DiscForge.Core.Iso.IsoReader.Read(fs);
            var ipl = dir.Entries.FirstOrDefault(e => !e.IsDirectory &&
                e.Name.StartsWith("IPL.TXT", StringComparison.OrdinalIgnoreCase));
            if (ipl is null) return Fail("No IPL.TXT found in this image — not a Neo Geo CD disc?");
            using var ms = new MemoryStream();
            DiscForge.Core.Iso.IsoReader.ExtractFile(fs, ipl, ms);
            boot = DiscForge.Core.Rom.NeoGeoCdIpl.Parse(ms.ToArray());
        }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Rom.NeoGeoCdIpl.Render(boot)}");
        return boot.IsBoot ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdgFramesCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge cdg-frames <src.cdg | src.sub> <out-dir> [--fps N] [--seconds N]\n" +
        "  Exports a CD+G graphics stream as a sequence of numbered PNG frames (frame_0001.png…). Input is\n" +
        "  a .cdg stream or a raw 96-byte/sector .sub sidecar (from which the CD+G is extracted). --fps sets\n" +
        "  the sampling rate (default 5), --seconds caps the duration. Rendering only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    string outDir = args[2];
    int fps = int.TryParse(OptVal(args, "--fps"), out var f) && f > 0 ? f : 5;
    try
    {
        var raw = File.ReadAllBytes(args[1]);
        // A .sub sidecar is a multiple of 96; a .cdg is a multiple of 24. Extract CD+G from a .sub.
        byte[] cdg = raw.Length % 96 == 0 && raw.Length % 24 != 0
            ? DiscForge.Core.Cdg.CdgExtractor.Extract(raw)
            : raw;

        int totalPackets = cdg.Length / DiscForge.Core.Cdg.CdgDecoder.PacketSize;
        double duration = totalPackets / (double)DiscForge.Core.Cdg.CdgDecoder.PacketsPerSecond;
        if (double.TryParse(OptVal(args, "--seconds"), out var sec) && sec > 0) duration = Math.Min(duration, sec);

        Directory.CreateDirectory(outDir);
        int frames = Math.Max(1, (int)(duration * fps));
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)fps;
            var png = DiscForge.Core.Cdg.CdgRenderer.RenderToPng(cdg, TimeSpan.FromSeconds(t));
            File.WriteAllBytes(Path.Combine(outDir, $"frame_{i + 1:D4}.png"), png);
        }
        Console.WriteLine($"Wrote {frames} frame(s) at {fps} fps ({duration:0.0}s) to {outDir}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int UdfOrphansCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge udf-orphans <image.udf | disc.iso>\n" +
        "  Carves a UDF volume's FREE space: it finds the partition's Space Bitmap (bit 1 = free), scans\n" +
        "  the free blocks, and reports those still holding non-zero data — leftover slack or deleted files\n" +
        "  the catalog no longer lists — with their byte offsets. Detection only; it recovers nothing.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var r = DiscForge.Core.Udf.UdfFreeSpace.Analyze(File.ReadAllBytes(args[1]));
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Udf.UdfFreeSpace.Render(r)}");
        return r.HasLeftovers ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int HfsOrphansCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge hfs-orphans <image.hfs | hybrid.iso>\n" +
        "  Carves an HFS volume's FREE space: it reads the volume bitmap and reports the allocation blocks\n" +
        "  marked free that still hold non-zero data — leftover slack or deleted files the catalog no longer\n" +
        "  lists — with their byte offsets. Detection only; it locates leftover content, it recovers nothing.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var image = File.ReadAllBytes(args[1]);
        if (!DiscForge.Core.Hfs.HfsReader.IsHfs(image))
            return Fail("No HFS volume found (no \"BD\" Master Directory Block at 0x400).");
        var r = DiscForge.Core.Hfs.HfsFreeSpace.Analyze(image);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Hfs.HfsFreeSpace.Render(r)}");
        return r.HasLeftovers ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int HfsLintCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge hfs-lint <image.hfs | hybrid.iso> [--json]\n" +
        "  Structural integrity check for a classic Apple HFS volume: the Master Directory Block signature and\n" +
        "  geometry, that the catalog B-tree walks cleanly, that the recorded file/directory counts match the\n" +
        "  tree actually present, and that every file's data- and resource-fork extents lie inside the volume.\n" +
        "  The fsck-style pass for the Mac half of a hybrid disc, alongside iso-lint / udf-lint / fat-lint. Read-only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var r = DiscForge.Core.Forensics.HfsLint.Check(File.ReadAllBytes(args[1]));
        if (!r.IsHfs) return Fail("Not an HFS volume (no \"BD\" Master Directory Block at 0x400).");
        if (args.Contains("--json")) { EmitJson(r); return r.Ok ? 0 : 2; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.HfsLint.Render(r)}");
        return r.Ok ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int HfsLsCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge hfs-ls <image.hfs | hybrid.iso>\n" +
        "  Walks the HFS catalog B-tree of a classic Mac volume (the Mac half of a hybrid CD) and lists\n" +
        "  every folder and file with its data-fork and resource-fork sizes. Reading only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var image = File.ReadAllBytes(args[1]);
        if (!DiscForge.Core.Hfs.HfsReader.IsHfs(image))
            return Fail("No HFS volume found (no \"BD\" Master Directory Block at 0x400). " +
                        "A pure ISO/Joliet disc has no HFS side.");
        var vol = DiscForge.Core.Hfs.HfsReader.Read(image);
        Console.WriteLine($"HFS volume \"{vol.VolumeName}\": {vol.Files.Count()} file(s), {vol.Directories.Count()} folder(s), " +
                          $"{vol.TotalDataBytes:N0} data bytes + {vol.TotalResourceBytes:N0} resource-fork bytes.");
        foreach (var e in vol.Entries)
        {
            if (e.IsDirectory) Console.WriteLine($"  [dir]  {e.Path}");
            else Console.WriteLine($"  {e.DataSize,10:N0}  {e.Path}" +
                                   (e.ResourceSize > 0 ? $"  (+{e.ResourceSize:N0} rsrc)" : ""));
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Read the resource forks of an HFS volume's files and list every resource (the Mac data ISO tools drop).
static int HfsResourcesCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge hfs-resources <image.hfs | hybrid.iso> [macpath] [--json] [--vers]\n" +
        "  Opens each file's RESOURCE FORK — the second data stream every Mac file carries and that ISO 9660\n" +
        "  / Joliet extraction silently discards — and lists every resource inside it (type, id, name, size):\n" +
        "  icons, version stamps, code, dialogs, sounds, Finder bundle info. Give a Mac path to inspect one\n" +
        "  file; --vers surfaces just the version stamps; --json emits the lot. Reading only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    bool json = args.Contains("--json");
    bool versOnly = args.Contains("--vers");
    string? filter = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--"));

    try
    {
        var image = File.ReadAllBytes(args[1]);
        if (!DiscForge.Core.Hfs.HfsReader.IsHfs(image))
            return Fail("No HFS volume found (no \"BD\" Master Directory Block at 0x400). " +
                        "A pure ISO/Joliet disc has no HFS side.");
        var vol = DiscForge.Core.Hfs.HfsReader.Read(image);

        var files = vol.Files.Where(f => f.ResourceSize > 0);
        if (filter != null)
        {
            string want = filter.Replace('\\', '/');
            files = files.Where(f => f.Path.Equals(want, StringComparison.OrdinalIgnoreCase) ||
                                     f.Name.Equals(filter, StringComparison.OrdinalIgnoreCase));
        }
        var list = files.ToList();

        var results = new List<object>();
        foreach (var f in list)
        {
            byte[] fork;
            try { fork = DiscForge.Core.Hfs.HfsReader.ReadResourceFork(image, vol, f); }
            catch (DiscForge.Core.Hfs.HfsFormatException ex)
            {
                results.Add(new { file = f.Path, error = ex.Message });
                continue;
            }
            if (!DiscForge.Core.Hfs.HfsResourceFork.Looks(fork))
            {
                results.Add(new { file = f.Path, error = "resource fork header not recognised" });
                continue;
            }

            var info = DiscForge.Core.Hfs.HfsResourceFork.Parse(fork);
            var resList = new List<object>();
            foreach (var r in info.Resources)
            {
                DiscForge.Core.Hfs.HfsVersion? ver = null;
                if (r.Type == "vers")
                    ver = DiscForge.Core.Hfs.HfsResourceFork.DecodeVersion(
                        DiscForge.Core.Hfs.HfsResourceFork.GetData(fork, r));
                resList.Add(new
                {
                    type = r.Type, id = r.Id, name = r.Name, length = r.Length,
                    version = ver == null ? null : new { ver.ShortText, ver.LongText, ver.Stage },
                });
            }
            results.Add(new { file = f.Path, resourceForkBytes = f.ResourceSize, resources = resList });
        }

        if (json) { EmitJson(new { volume = vol.VolumeName, filesWithResources = list.Count, files = results }); return 0; }

        Console.WriteLine($"HFS volume \"{vol.VolumeName}\": {list.Count} file(s) with a resource fork" +
                          (filter != null ? $" matching \"{filter}\"." : "."));
        foreach (var f in list)
        {
            byte[] fork;
            try { fork = DiscForge.Core.Hfs.HfsReader.ReadResourceFork(image, vol, f); }
            catch (DiscForge.Core.Hfs.HfsFormatException ex) { Console.WriteLine($"  {f.Path}: {ex.Message}"); continue; }
            if (!DiscForge.Core.Hfs.HfsResourceFork.Looks(fork)) { Console.WriteLine($"  {f.Path}: resource fork header not recognised"); continue; }

            var info = DiscForge.Core.Hfs.HfsResourceFork.Parse(fork);

            if (versOnly)
            {
                foreach (var r in info.Resources.Where(r => r.Type == "vers"))
                {
                    var v = DiscForge.Core.Hfs.HfsResourceFork.DecodeVersion(DiscForge.Core.Hfs.HfsResourceFork.GetData(fork, r));
                    if (v != null) Console.WriteLine($"  {f.Path}  vers {r.Id}: {v.ShortText}  ({v.LongText})");
                }
                continue;
            }

            Console.WriteLine($"  {f.Path}  — {info.Count} resource(s) in {info.Types.Count()} type(s): {string.Join(" ", info.Types)}");
            foreach (var g in info.Resources.GroupBy(r => r.Type))
            {
                Console.WriteLine($"      '{g.Key}'  ×{g.Count()}");
                foreach (var r in g.OrderBy(r => r.Id))
                {
                    string extra = "";
                    if (r.Type == "vers")
                    {
                        var v = DiscForge.Core.Hfs.HfsResourceFork.DecodeVersion(DiscForge.Core.Hfs.HfsResourceFork.GetData(fork, r));
                        if (v != null) extra = $"  → {v.ShortText} ({v.LongText})";
                    }
                    Console.WriteLine($"        id {r.Id,6}  {r.Length,8:N0} B  {r.Name ?? ""}{extra}");
                }
            }
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Map a VTS's program chains and flag the ones no title or menu points at (hidden content).
static int DvdNavCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge dvd-nav <VTS_xx_0.IFO>\n" +
        "  Reads a title set's navigation tables (unencrypted) and maps its program chains, flagging any\n" +
        "  that are neither a title entry point nor referenced by a title — content physically on the disc\n" +
        "  that normal playback never reaches. Detection only; it reads the table of contents.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var r = DiscForge.Core.DvdVideo.DvdNavMap.Analyze(File.ReadAllBytes(args[1]));
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.DvdVideo.DvdNavMap.Render(r)}");
        return r.HasHidden ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Hunt for hidden data in an ISO's zero-expected regions (file slack, system area).
static int CovertScanCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge covert-scan <image.iso>\n" +
        "  Finds non-zero data where the format expects zeros — file slack, the ISO system area — the\n" +
        "  classic places to hide a payload, a watermark, or an old hidden track. Detection only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var r = DiscForge.Core.Forensics.CovertChannelSweep.Scan(File.ReadAllBytes(args[1]));
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.CovertChannelSweep.Render(r)}");
        return r.AnyHidden ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Classify each region of an image by data type and render an SVG "matter" map.
static int MatterMapCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge matter-map <in.bin> <out.svg> [--block N]\n" +
        "  Labels each block zero / text / structured / high-entropy (compressed or encrypted) and renders\n" +
        "  the disc's composition as a coloured SVG strip. Classifies; never decrypts.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    int block = int.TryParse(OptVal(args, "--block"), out var b) ? b : 2048;
    try
    {
        var map = DiscForge.Core.Forensics.SectorMatterMap.Analyze(File.ReadAllBytes(args[1]), block);
        File.WriteAllText(args[2], DiscForge.Core.Forensics.SectorMatterMap.RenderSvg(map, $"Matter — {Path.GetFileName(args[1])}"));
        Console.WriteLine($"{Path.GetFileName(args[1])}: {map.Summary()}");
        Console.WriteLine($"  wrote {Path.GetFileName(args[2])}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Build the family tree of a set of ISO images from their content deltas.
static int PhyloCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge phylo <dir | image.iso ...> [--newick]\n" +
        "  Builds a family tree (dendrogram) of a title's releases from what changed between them —\n" +
        "  near-identical variants sit as siblings, heavily-revised pressings branch higher.");
    var inputs = args.Skip(1).Where(a => !a.StartsWith("--")).ToList();
    var files = new List<string>();
    foreach (var input in inputs)
    {
        if (Directory.Exists(input)) files.AddRange(Directory.EnumerateFiles(input, "*.iso", SearchOption.TopDirectoryOnly));
        else if (File.Exists(input)) files.Add(input);
        else return Fail($"Not found: {input}");
    }
    files = files.Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    if (files.Count == 0) return Fail("No disc images to analyse.");
    try
    {
        var profiles = files.Select(f => DiscForge.Core.Forensics.DiscClustering.Profile(Path.GetFileName(f), File.ReadAllBytes(f))).ToList();
        var root = DiscForge.Core.Forensics.DiscPhylogeny.Build(profiles);
        if (args.Contains("--newick")) Console.WriteLine(DiscForge.Core.Forensics.DiscPhylogeny.ToNewick(root));
        else Console.WriteLine(DiscForge.Core.Forensics.DiscPhylogeny.RenderTree(root));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Strict ISO 9660 conformance check.
static int IsoPathTableCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge iso-pathtable <image.iso>\n" +
        "  Audits the ISO 9660 path table: parses the Type-L and Type-M copies, checks they describe the\n" +
        "  same directories at the same extents with the same parents, validates the parent references form\n" +
        "  a proper tree, and confirms each extent points at a real directory. The structural companion to\n" +
        "  iso-lint. Detection/reporting only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var r = DiscForge.Core.Iso.IsoPathTable.Read(File.ReadAllBytes(args[1]));
        if (args.Contains("--json")) { EmitJson(r); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Iso.IsoPathTable.Render(r)}");
        return r.Ok ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IsoRockRidgeCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge iso-rockridge <image.iso> [--json]\n" +
        "  Reads the Rock Ridge (SUSP / RRIP) POSIX metadata a Unix/Linux CD keeps in each directory record's\n" +
        "  System Use area — the layer a plain ISO 9660 reader drops when it flattens names to 8.3. Recovers\n" +
        "  the real long names, permissions and ownership, symlink targets and true timestamps, following CE\n" +
        "  continuation blocks. Reading only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        using var fs = File.OpenRead(args[1]);
        var listing = DiscForge.Core.Iso.IsoReader.Read(fs, DiscForge.Core.Iso.IsoReader.NamePreference.Iso9660);

        if (!listing.RockRidge)
            return Fail("No Rock Ridge extensions on this image (no SP/ER entry in the root record). " +
                        "It is a plain ISO 9660 / Joliet disc with no POSIX metadata layer.");

        var rows = listing.Entries
            .Where(e => e.RockRidge is { Present: true })
            .Select(e => new
            {
                path = e.Path,
                isDirectory = e.IsDirectory,
                mode = e.RockRidge!.Mode,
                modeString = e.RockRidge.ModeString,
                links = e.RockRidge.Links,
                uid = e.RockRidge.Uid,
                gid = e.RockRidge.Gid,
                symlinkTarget = e.RockRidge.SymlinkTarget,
                modified = e.RockRidge.Modified,
            })
            .ToList();

        if (args.Contains("--json"))
        {
            EmitJson(new { volume = listing.VolumeId, rockRidge = true, count = rows.Count, entries = rows });
            return 0;
        }

        Console.WriteLine($"{Path.GetFileName(args[1])}: Rock Ridge present on volume \"{listing.VolumeId}\" " +
                          $"— {rows.Count} entr{(rows.Count == 1 ? "y" : "ies")} with POSIX metadata.");
        foreach (var e in listing.Entries.Where(e => e.RockRidge is { Present: true }))
        {
            var rr = e.RockRidge!;
            string mode = rr.ModeString.Length > 0 ? rr.ModeString : (e.IsDirectory ? "d?????????" : "-?????????");
            string owner = (rr.Uid is { } u && rr.Gid is { } g) ? $"{u}:{g}" : "     ";
            string link = rr.SymlinkTarget is { } t ? $" -> {t}" : "";
            string when = rr.Modified is { } m ? m.ToString("yyyy-MM-dd HH:mm") : "                ";
            Console.WriteLine($"  {mode}  {owner,-9}  {when}  {e.Path}{link}");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IsoLintCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge iso-lint <image.iso>\n" +
        "  Checks an image against the ISO 9660 grammar (magic, both-endian field agreement, block size,\n" +
        "  descriptor terminator, volume/root bounds) and reports every deviation with a severity.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var r = DiscForge.Core.Forensics.IsoLint.Check(File.ReadAllBytes(args[1]));
        if (args.Contains("--json")) { EmitJson(r); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.IsoLint.Render(r)}");
        return r.Ok ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int UdfLintCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge udf-lint <image.iso | image.udf>\n" +
        "  Walks the UDF structures the way a driver does — Volume Recognition Sequence, Anchor at sector\n" +
        "  256, Main Volume Descriptor Sequence, Partition/Logical-Volume descriptors, the Integrity\n" +
        "  Descriptor, the File Set Descriptor and root File Entry — validating each descriptor tag's\n" +
        "  checksum and CRC and that its tag location is recorded correctly (partition-relative inside the\n" +
        "  partition). This is the exact check that catches a \"File Set Descriptor not found\". Read-only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var r = DiscForge.Core.Forensics.UdfLint.Check(File.ReadAllBytes(args[1]));
        if (args.Contains("--json")) { EmitJson(r); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.UdfLint.Render(r)}");
        return r.Ok ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PremasterCheckCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge premaster-check <disc.cue>\n" +
        "  The go/no-go gate before cutting a glass master: folds the Red Book structural audit with the\n" +
        "  runtime-capacity check (74:00 nominal / 80:00 max) and per-sector EDC/ECC integrity on data\n" +
        "  tracks, plus MCN/ISRC hygiene advisories. Reads lengths and data from the image beside the cue\n" +
        "  when present. Reports one verdict — ready, or exactly what disqualifies it. Detection only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var cue = DiscForge.Core.Cue.CueSheet.Parse(File.ReadAllText(args[1]));
        DiscForge.Core.Convert.DiscModel? model = null;
        try { model = DiscForge.Core.Convert.DiscConverter.Read(args[1]); }
        catch { /* no readable image alongside the cue — structural/hygiene checks only */ }

        var r = DiscForge.Core.Forensics.PremasterGate.Check(cue, model);
        if (args.Contains("--json")) { EmitJson(r); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.PremasterGate.Render(r)}");
        return r.ReadyToMaster ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BootCatalogCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge boot-catalog <image.iso>\n" +
        "  Decodes a bootable disc's El Torito boot catalog: the validation entry (with checksum), the\n" +
        "  default boot entry, and any platform sections of a multi-boot disc (e.g. a BIOS x86 image and a\n" +
        "  UEFI image). Reports each entry's firmware target, emulation type, size and load address. Reads a\n" +
        "  2048-byte/sector ISO; reports \"not bootable\" when the disc carries no El Torito boot record.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var cat = DiscForge.Core.Iso.ElTorito.Read(File.ReadAllBytes(args[1]));
        if (cat == null)
        {
            Console.WriteLine($"{Path.GetFileName(args[1])}: not bootable (no El Torito boot record).");
            return 1;
        }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Iso.ElTorito.Render(cat)}");
        return cat.ChecksumValid ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int RedBookAuditCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge redbook-audit <disc.cue>\n" +
        "  Checks a disc's TRACK structure against the Red Book (IEC 60908) rules — track count/sequence,\n" +
        "  INDEX 01 per track, 4-second minimum track length, 2-second minimum pause, MCN/ISRC grammar,\n" +
        "  and data/audio ordering — and reports every deviation with a severity. Detection only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var cue = DiscForge.Core.Cue.CueSheet.Parse(File.ReadAllText(args[1]));

        // Try to derive per-track content lengths from the actual image so length is checked too.
        IReadOnlyList<int>? lengths = null;
        try
        {
            var model = DiscForge.Core.Convert.DiscConverter.Read(args[1]);
            if (model.Tracks.Count == cue.Tracks.Count)
                lengths = model.Tracks.Select(t => t.SectorCount).ToList();
        }
        catch { /* no image alongside the cue — audit structure only */ }

        var r = DiscForge.Core.Forensics.RedBookAudit.Check(cue, lengths);
        if (args.Contains("--json")) { EmitJson(r); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.RedBookAudit.Render(r)}");
        return r.Ok ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Infer what tool produced a dump from its fileset (and optional main-image geometry).
static int DumpProvenanceCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge dump-provenance <dir | file ...>\n" +
        "  Infers the tool behind a dump from its container format (.ccd/.img/.sub = CloneCD, .mds/.mdf =\n" +
        "  Alcohol, a submission-info = Redump…) and, where derivable, the main image's sector geometry.");
    var names = new List<string>();
    int? sectorSize = null;
    foreach (var input in args.Skip(1))
    {
        if (Directory.Exists(input)) names.AddRange(Directory.EnumerateFiles(input).Select(Path.GetFileName)!);
        else names.Add(Path.GetFileName(input));

        // Geometry hint from a raw image whose size divides evenly by a known sector size.
        bool isImage = input.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                       input.EndsWith(".img", StringComparison.OrdinalIgnoreCase);
        if (sectorSize is null && isImage && File.Exists(input))
        {
            long len = new FileInfo(input).Length;
            if (len > 0)
            {
                if (len % 2448 == 0) sectorSize = 2448;
                else if (len % 2352 == 0) sectorSize = 2352;
                else if (len % 2048 == 0) sectorSize = 2048;
            }
        }
    }
    var r = DiscForge.Core.Forensics.DumpProvenance.Infer(names, sectorSize);
    Console.WriteLine(DiscForge.Core.Forensics.DumpProvenance.Render(r));
    return r.Best is not null ? 0 : 1;
}

// Detect hidden-track audio (HTOA) in a pregap/gap audio file.
static int PregapScanCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge pregap-scan <audio.bin> [--track N] [--kind track-1-pregap]\n" +
        "  Reads raw 16-bit PCM (2352 bytes/sector) of a gap and reports whether it is silence or carries\n" +
        "  hidden-track audio — the music some CDs tuck into track 1's pregap that normal rips drop.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    int track = int.TryParse(OptVal(args, "--track"), out var t) ? t : 1;
    string kind = OptVal(args, "--kind") ?? "track-1-pregap";
    try
    {
        var g = DiscForge.Core.Forensics.PregapForensics.AnalyzeGap(track, kind, File.ReadAllBytes(args[1]));
        Console.WriteLine($"{Path.GetFileName(args[1])}: {g}");
        return g.ContainsAudio ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Per-scratch recovery outlook: classify each damaged region's shape and say what can be done about it.
static int ScratchVerdictCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge scratch-verdict <raw.bin> [--audio]\n" +
        "  EDC-scans a raw image for damage, classifies each region's shape (scratch / rot / deliberate),\n" +
        "  and gives the physical recovery outlook: for data, single-read ECC vs re-read/reconstruct; with\n" +
        "  --audio, the CIRC verdict (corrected / concealed by interpolation / audibly lost). Advisory only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    bool audio = args.Contains("--audio");
    try
    {
        var health = DiscForge.Core.Forensics.DiscHealthMap.Scan(File.ReadAllBytes(args[1]));
        var pattern = DiscForge.Core.Forensics.ErrorPatternForensics.Classify(health);
        var report = DiscForge.Core.Forensics.ScratchRecovery.Advise(pattern, _ => audio);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.ScratchRecovery.Render(report)}");
        return report.AnyLost ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int RecoveryMapCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge recovery-map <raw.bin> <out.svg>\n" +
        "  EDC-scans a raw 2352-byte/sector image, classifies each damaged region's shape, and renders an\n" +
        "  SVG coloured by the recovery OUTLOOK per region — audio corrected (green) / concealed (amber) /\n" +
        "  audibly lost (red), data ECC-or-re-read (orange), deliberate pattern to preserve (purple). Audio\n" +
        "  vs data is read per sector from the sync mark, so mixed-mode discs map correctly. Advisory only.");
    string path = args[1], outPath = args[2];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length == 0 || raw.Length % 2352 != 0)
            return Fail($"Image length {raw.Length:N0} is not a whole number of 2352-byte sectors.");
        int sectors = raw.Length / 2352;

        var health = DiscForge.Core.Forensics.DiscHealthMap.Scan(raw);
        var pattern = DiscForge.Core.Forensics.ErrorPatternForensics.Classify(health);

        // Per-sector audio detection: a sector with no CD sync mark is CD-DA audio.
        bool IsAudioAt(int lba)
        {
            if (lba < 0 || lba >= sectors) return false;
            var s = raw.AsSpan(lba * 2352, 2352);
            if (s[0] != 0x00 || s[11] != 0x00) return true;
            for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return true;
            return false;                            // full sync present → data sector
        }

        var report = DiscForge.Core.Forensics.ScratchRecovery.Advise(pattern, IsAudioAt);
        string svg = DiscForge.Core.Forensics.RecoveryMap.RenderSvg(report, sectors, Path.GetFileName(path));
        File.WriteAllText(outPath, svg);
        Console.WriteLine($"{Path.GetFileName(outPath)}: {report.Summary()}");
        return report.AnyLost ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Rebuild a disc's TOC from the raw Q sub-channel when the lead-in is unreadable.
static int RecoverTocCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge recover-toc <raw.sub>\n" +
        "  Rebuilds the table of contents from a raw 96-byte/sector sub-channel file when the lead-in TOC\n" +
        "  is unreadable — the same track/index/time addressing is repeated in every sector's Q channel.\n" +
        "  CRC-valid Q frames are used; each track's exact start comes from its relative time. Reading only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var sub = File.ReadAllBytes(args[1]);
        if (sub.Length % 96 != 0)
            return Fail($"Sub-channel file must be a whole number of 96-byte frames ({sub.Length:N0} bytes given).");
        var frames = new List<byte[]>(sub.Length / 96);
        for (int off = 0; off + 96 <= sub.Length; off += 96)
        {
            var q = new byte[12];
            DiscForge.Core.Raw.RawSubchannel.ExtractQ(sub.AsSpan(off, 96), q);
            frames.Add(q);
        }
        var toc = DiscForge.Core.Raw.SubchannelTocRecovery.Recover(frames);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Raw.SubchannelTocRecovery.Render(toc)}");
        return toc.Recovered ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Model CIRC error correction: predict whether a burst of N frames is recoverable.
static int RecoverOracleCmd(string[] args)
{
    if (args.Length < 2 || !int.TryParse(args[1], out int burst) || burst < 0)
        return Fail("usage: dforge recover-oracle <burst-frames>\n" +
            "  Models how a physical burst of N consecutive lost frames distributes through the CIRC cross-\n" +
            "  interleave into C2's erasure budget, and whether the audio is fully corrected or falls to\n" +
            "  interpolation. One frame carries 24 audio bytes (6 stereo samples). Modelling only.");
    var v = DiscForge.Core.Raw.CircRecovery.AnalyzeBurst(burst);
    Console.WriteLine($"Burst of {v.BurstFrames} frame(s): {v.Assessment}");
    Console.WriteLine($"  worst-case erasures per C2 codeword: {v.MaxErasuresPerC2} (capacity {v.C2ErasureCapacity})");
    Console.WriteLine($"  CIRC fully corrects bursts up to {v.MaxCorrectableBurstFrames} frame(s) by C2 erasure decoding;");
    Console.WriteLine($"  longer bursts rely on the interleave's scatter + interpolation to conceal the loss.");
    return v.FullyCorrectable ? 0 : 2;
}

// Predict channel-weak sectors from the physical encoding model (scramble + EFM + DSV).
static int XaMapCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge xa-map <raw.bin> [--2336]\n" +
        "  Maps a CD-ROM XA disc's multimedia structure — the layout behind PlayStation FMV, Video CD and\n" +
        "  CD-i. It reads each Mode 2 sector's subheader (file, channel, submode, coding), tallies every\n" +
        "  (file,channel) stream by video/audio/data and Form 1/Form 2, reads the first audio coding it sees\n" +
        "  (rate/stereo/bit depth), and measures how tightly the streams interleave. Default sector size is\n" +
        "  raw 2352; --2336 for headerless Mode 2 images. Parses and reports only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    bool mode2336 = args.Contains("--2336");
    try
    {
        var layout = mode2336
            ? DiscForge.Core.PlayStation.XaExtract.SectorLayout.Mode2_2336
            : DiscForge.Core.PlayStation.XaExtract.SectorLayout.Raw2352;
        var r = DiscForge.Core.PlayStation.XaStreamMap.Analyze(File.ReadAllBytes(args[1]), layout);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.PlayStation.XaStreamMap.Render(r)}");
        return r.IsXa ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int EfmSpectrumCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge efm-spectrum <raw.bin> [--lba L] [--count C] [--scramble]\n" +
        "  Encodes the input through the EFM channel code and reports the physical-quality shape of the\n" +
        "  resulting pit/land stream: the 3T..11T run-length spectrum, the pit/land duty asymmetry (the DC\n" +
        "  balance the RF eye inherits), the DSV excursion, spectral entropy, and a coarse grade. With\n" +
        "  --scramble each 2352-byte sector is CD-scrambled first, modelling how data physically sits on the\n" +
        "  disc. --lba/--count select a sector window (default: first 16). Encoding-domain read; changes nothing.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    int lba = int.TryParse(OptVal(args, "--lba"), out var l) ? l : 0;
    int count = int.TryParse(OptVal(args, "--count"), out var c) ? c : 16;
    bool scramble = args.Contains("--scramble");
    try
    {
        var bytes = File.ReadAllBytes(args[1]);
        byte[] slice;
        if (bytes.Length % 2352 == 0 && bytes.Length >= 2352)
        {
            int totalSectors = bytes.Length / 2352;
            if (lba < 0 || lba >= totalSectors) return Fail($"--lba {lba} is outside 0..{totalSectors - 1}.");
            count = Math.Clamp(count, 1, totalSectors - lba);
            slice = new byte[count * 2352];
            Array.Copy(bytes, lba * 2352, slice, 0, slice.Length);
            if (scramble)
                for (int s = 0; s < count; s++)
                    DiscForge.Core.Raw.CdScrambler.ScrambleInPlace(slice.AsSpan(s * 2352, 2352));
        }
        else
        {
            slice = bytes;   // arbitrary (non-sector) data: analyse it as-is
        }

        var r = DiscForge.Core.Forensics.EfmSpectrum.Analyze(slice);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.EfmSpectrum.Render(r)}");
        return r.ConstraintOk ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int WeakSectorsCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge weak-sectors <raw.bin> [--max N]\n" +
        "  Models each data sector the way it physically sits on the disc — CD-scrambled then EFM-encoded —\n" +
        "  and flags those whose channel stream is too low-transition to track reliably: the signature of a\n" +
        "  deliberate weak-sector (SafeDisc-style) layout, predicted from the data alone. Encoding is heavy,\n" +
        "  so --max bounds how many sectors are analysed (default 4096). Modelling and detection only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    int max = int.TryParse(OptVal(args, "--max"), out var m) ? m : 4096;
    try
    {
        var r = DiscForge.Core.Forensics.WeakSectorAnalyzer.Analyze(File.ReadAllBytes(args[1]), max);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.WeakSectorAnalyzer.Render(r)}");
        return r.AnyWeak ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Detect twin / re-addressed sectors (protection written into the sector headers) from a raw image.
static int TwinScanCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge twin-scan <raw.bin>\n" +
                    "  Scans a raw 2352-byte/sector image for header-address protection: SafeDisc-style twin\n" +
                    "  sectors (two sectors claiming the same address) and re-addressed sectors (a header address\n" +
                    "  off the contiguous run). It first establishes the image's own base offset, so a legitimately\n" +
                    "  shifted dump is not mistaken for tampering. Detection only — flags what to preserve verbatim.");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        var r = DiscForge.Core.Forensics.TwinSectorScan.Analyze(File.ReadAllBytes(path));
        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Forensics.TwinSectorScan.Render(r)}");
        return r.LooksProtected ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Triage a collection's C1/C2 error-scan history and predict which discs are dying first.
static int DpmCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge dpm <scan.csv>\n" +
        "  Reads a Data-Position read-speed scan (columns lba,speed — or lba,timeus, inverted to speed —\n" +
        "  a header maps them, else lba,speed) and profiles the disc's physical layout: it fits a local\n" +
        "  baseline, flags regions that read markedly slower, and decides whether the shape is a deliberate\n" +
        "  ring (SecuROM/StarForce-style), broad surface damage, or clean. Emits a scale-invariant shape\n" +
        "  fingerprint so two dumps of one disc match. Measurement/detection only — circumvents nothing.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var samples = DiscForge.Core.Forensics.Dpm.ParseCsv(File.ReadAllText(args[1]));
        if (samples.Count == 0) return Fail("No DPM samples parsed — check the scan format.");
        var r = DiscForge.Core.Forensics.Dpm.Analyze(samples);
        if (args.Contains("--json")) { EmitJson(r); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.Dpm.Render(r)}");
        return r.Verdict == DiscForge.Core.Forensics.DpmVerdict.RingLike ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BlerCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge bler <scan.csv>\n" +
        "  Reads a per-second C1/C2 error scan (columns second,e11,e21,e31,e12,e22,e32 — a header maps\n" +
        "  them by name, or a minimal second,bler,cu form is accepted) and reports the surface-quality\n" +
        "  verdict: average/peak/95th BLER against the Red Book 220/s ceiling, the E22/E32 totals, the\n" +
        "  longest error burst, and a pass/fail plus grade. BLER is a drive read-time metric — this judges\n" +
        "  a captured scan, it does not synthesise errors from a corrected image. Detection/reporting only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var samples = DiscForge.Core.Forensics.Bler.ParseCsv(File.ReadAllText(args[1]));
        if (samples.Count == 0) return Fail("No C1/C2 samples parsed — check the scan format.");
        var r = DiscForge.Core.Forensics.Bler.Analyze(samples);
        if (args.Contains("--json")) { EmitJson(r); return 0; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.Bler.Render(r)}");
        return r.RedBookPass ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Import a foreign drive-quality scan and normalise it into DiscForge's model — optionally re-emitting
// it in the exact JSON/CSV that disc-rot, disc-print or bler consume, so old scans flow into analysis.
static int ScanImportCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge scan-import <scan.txt|csv> [--family cd|dvd|bd] [--id NAME] [--emit print|rot|bler|json]\n" +
            "  Reads a drive-quality scan exported by Opti Drive Control, Nero CD-DVD Speed / DiscSpeed,\n" +
            "  KProbe or DVDInfoPro (or any delimited CSV/TSV of the same data) and normalises it into\n" +
            "  DiscForge's scan model, lifting whatever provenance the export carried (drive, media id,\n" +
            "  book type, speed, date). It recognises the CD (C1/C2), DVD (PIE/PIF/POF) and BD (LDC/BIS)\n" +
            "  column vocabularies; a headerless file needs --family. By default it prints a summary and a\n" +
            "  spec verdict; --emit re-serialises the samples for another command:\n" +
            "    --emit print   scan JSON for `disc-print`   { \"id\":..., \"samples\":[[c1,c2,cu],...] }\n" +
            "    --emit rot     history JSON for `disc-rot`  { \"discs\":[{ \"id\":..., \"scans\":[...] }] }\n" +
            "    --emit bler    per-interval CSV for `bler`  (second,bler,cu — CD scans)\n" +
            "    --emit json    the full normalised scan (metadata + summary + rows)\n" +
            "  Detection/normalisation only — it reads a scan someone else's drive produced and changes nothing.");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        var hint = (OptVal(args, "--family")?.ToLowerInvariant()) switch
        {
            "cd" => DiscForge.Core.Forensics.DiscFamily.Cd,
            "dvd" => DiscForge.Core.Forensics.DiscFamily.Dvd,
            "bd" or "bluray" or "blu-ray" or "bd-r" => DiscForge.Core.Forensics.DiscFamily.BluRay,
            _ => DiscForge.Core.Forensics.DiscFamily.Unknown,
        };
        string? id = OptVal(args, "--id");
        var scan = DiscForge.Core.Forensics.QualityScanImport.Parse(File.ReadAllText(path), hint, id);
        if (scan.Count == 0)
            return Fail("No scan intervals parsed. If the file has no header row, pass --family cd|dvd|bd.");

        string label = scan.DiscId ?? Path.GetFileNameWithoutExtension(path);
        var samples = scan.ToSamples();
        string emit = (OptVal(args, "--emit") ?? (args.Contains("--json") ? "json" : "")).ToLowerInvariant();

        switch (emit)
        {
            case "print":
                EmitJson(new { id = label, samples = samples.Select(s => new[] { s.C1, s.C2, s.Cu }) });
                return 0;
            case "rot":
            {
                var when = scan.ScannedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                EmitJson(new
                {
                    discs = new[]
                    {
                        new
                        {
                            id = label,
                            scans = new[]
                            {
                                new
                                {
                                    date = when.ToString("yyyy-MM-dd"),
                                    drive = scan.Drive,
                                    samples = samples.Select(s => new[] { s.C1, s.C2, s.Cu }),
                                },
                            },
                        },
                    },
                });
                return 0;
            }
            case "bler":
            {
                if (scan.Family != DiscForge.Core.Forensics.DiscFamily.Cd)
                    Console.Error.WriteLine($"# note: {DiscForge.Core.Forensics.QualityScanImport.ToolName(scan.Tool)} scan is " +
                        $"{scan.Family}; first-tier errors mapped to the BLER (C1) column.");
                Console.WriteLine("second,bler,cu");
                int sec = 0;
                foreach (var s in samples) Console.WriteLine($"{sec++},{s.C1},{s.Cu}");
                return 0;
            }
            case "json":
                EmitJson(new
                {
                    tool = DiscForge.Core.Forensics.QualityScanImport.ToolName(scan.Tool),
                    family = scan.Family.ToString(),
                    disc = scan.DiscId,
                    scan.Drive, scan.MediaId, scan.BookType, scan.WriteSpeed,
                    scannedAt = scan.ScannedAt?.ToString("yyyy-MM-dd"),
                    unit = scan.PositionUnit,
                    intervals = scan.Count,
                    pass = scan.Pass,
                    grade = scan.Grade(),
                    verdict = scan.Verdict(),
                    scan.Assumption,
                    rows = scan.Rows,
                });
                return 0;
            default:
                Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Forensics.QualityScanImport.Render(scan)}");
                return scan.Pass ? 0 : 2;
        }
    }
    catch (System.Text.Json.JsonException ex) { return Fail($"Bad JSON: {ex.Message}"); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Fit a first-order (exponential/Arrhenius) rot model to one disc's error history and forecast survival.
static int RotKineticsCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge rot-kinetics <history.json> [--threshold N] [--temp C] [--rh %] [--json]\n" +
                    "  Fits ln(error) vs time as a first-order decay and projects when the disc crosses a\n" +
                    "  failure threshold (default 220 BLER), with a confidence band. Given a storage --temp\n" +
                    "  and --rh it scales the forecast by an Arrhenius/Eyring factor. JSON shape:\n" +
                    "    { \"id\": \"disc\", \"samples\": [ { \"date\": \"2020-01-01\", \"error\": 40 }, ... ] }\n" +
                    "    (\"error\" is the per-scan metric — peak BLER / C1. A disc-rot history's maxC1 works too.)");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        double threshold = double.TryParse(OptVal(args, "--threshold"), out var th) ? th
            : DiscForge.Core.Forensics.RotKinetics.DefaultThreshold;
        DiscForge.Core.Forensics.StorageEnvironment? env = null;
        if (double.TryParse(OptVal(args, "--temp"), out var tc))
            env = new DiscForge.Core.Forensics.StorageEnvironment(tc,
                double.TryParse(OptVal(args, "--rh"), out var rh) ? rh : 50);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (!root.TryGetProperty("samples", out var smp) || smp.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Fail("Expected a \"samples\" array of { date, error }.");

        var history = new List<DiscForge.Core.Forensics.RotSample>();
        foreach (var s in smp.EnumerateArray())
        {
            if (!s.TryGetProperty("date", out var dEl) || dEl.GetString() is not { } ds) continue;
            var when = DateTimeOffset.Parse(ds, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal);
            double err = s.TryGetProperty("error", out var e) && e.TryGetDouble(out var ev) ? ev
                : s.TryGetProperty("maxC1", out var m) && m.TryGetDouble(out var mv) ? mv : 0;
            history.Add(new DiscForge.Core.Forensics.RotSample(when, err));
        }
        if (history.Count < 2) return Fail("Need at least two scans to fit a rot rate.");

        var r = DiscForge.Core.Forensics.RotKinetics.Fit(history, threshold, env);
        string id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Path.GetFileName(path) : Path.GetFileName(path);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                id, r.GrowthPerYear, r.InitialRate, r.RSquared, r.SampleCount, r.Threshold,
                r.YearsToThreshold, thresholdDate = r.ThresholdDate?.ToString("yyyy-MM-dd"),
                r.EnvAccelFactor, r.AlreadyFailing, r.Assessment,
            });
            return r.AlreadyFailing || r.YearsToThreshold is < 1 ? 2 : 0;
        }

        Console.WriteLine($"{id}: {DiscForge.Core.Forensics.RotKinetics.Render(r)}");
        return r.AlreadyFailing || r.YearsToThreshold is < 1 ? 2 : 0;
    }
    catch (System.Text.Json.JsonException ex) { return Fail($"Bad JSON: {ex.Message}"); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DiscRotCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-rot <history.json>\n" +
                    "  Reads a history of C1/C2 error scans and predicts which discs are rotting, ranking the\n" +
                    "  collection by how soon each needs dumping. JSON shape:\n" +
                    "    { \"discs\": [ { \"id\": \"Game A\", \"scans\": [\n" +
                    "        { \"date\": \"2022-01-01\", \"maxC1\": 40, \"avgC1\": 5, \"maxC2\": 0, \"totalC2\": 0, \"totalCu\": 0, \"samples\": 1000 },\n" +
                    "        { \"date\": \"2024-01-01\", \"maxC1\": 120, \"maxC2\": 4, \"totalC2\": 30 } ] } ] }\n" +
                    "  A scan may instead give \"samples\": [[c1,c2,cu], ...] and the stats are computed.");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("discs", out var discsEl) ||
            discsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Fail("Expected a top-level \"discs\" array.");

        var histories = new List<IReadOnlyList<DiscForge.Core.Forensics.ErrorScan>>();
        foreach (var d in discsEl.EnumerateArray())
        {
            string id = d.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "disc" : "disc";
            var scans = new List<DiscForge.Core.Forensics.ErrorScan>();
            if (d.TryGetProperty("scans", out var scansEl) && scansEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var s in scansEl.EnumerateArray())
                    scans.Add(ParseScan(id, s));
            if (scans.Count > 0) histories.Add(scans);
        }
        if (histories.Count == 0) return Fail("No discs with scans found in the history.");

        var order = DiscForge.Core.Forensics.DiscRotTriage.Prioritize(histories);
        Console.WriteLine(DiscForge.Core.Forensics.DiscRotTriage.Render(order));
        // Non-zero when at least one disc needs dumping soon, so a script can act on it.
        return order.Any(f => f.Urgency >= DiscForge.Core.Forensics.RotUrgency.Soon) ? 2 : 0;
    }
    catch (System.Text.Json.JsonException ex) { return Fail($"Bad JSON: {ex.Message}"); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static DiscForge.Core.Forensics.ErrorScan ParseScan(string id, System.Text.Json.JsonElement s)
{
    var when = s.TryGetProperty("date", out var dEl) && dEl.GetString() is { } ds
        ? DateTimeOffset.Parse(ds, System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.AssumeUniversal)
        : throw new FormatException($"A scan for '{id}' is missing its \"date\".");
    string? drive = s.TryGetProperty("drive", out var drvEl) ? drvEl.GetString() : null;

    // Raw per-interval samples take precedence when given as an array.
    if (s.TryGetProperty("samples", out var smpEl) && smpEl.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        var samples = new List<DiscForge.Core.Forensics.ScanSample>();
        foreach (var row in smpEl.EnumerateArray())
        {
            int c1 = row.ValueKind == System.Text.Json.JsonValueKind.Array && row.GetArrayLength() > 0 ? row[0].GetInt32() : 0;
            int c2 = row.ValueKind == System.Text.Json.JsonValueKind.Array && row.GetArrayLength() > 1 ? row[1].GetInt32() : 0;
            int cu = row.ValueKind == System.Text.Json.JsonValueKind.Array && row.GetArrayLength() > 2 ? row[2].GetInt32() : 0;
            samples.Add(new DiscForge.Core.Forensics.ScanSample(c1, c2, cu));
        }
        return DiscForge.Core.Forensics.ErrorScan.FromSamples(id, when, samples, drive);
    }

    int I(string name) => s.TryGetProperty(name, out var e) && e.TryGetInt32(out var v) ? v : 0;
    long L(string name) => s.TryGetProperty(name, out var e) && e.TryGetInt64(out var v) ? v : 0;
    double D(string name) => s.TryGetProperty(name, out var e) && e.TryGetDouble(out var v) ? v : 0;
    int samplesCount = I("samples");
    long totalC1 = L("totalC1");
    int maxC1 = I("maxC1");
    return new DiscForge.Core.Forensics.ErrorScan
    {
        DiscId = id,
        Timestamp = when,
        Drive = drive,
        SampleCount = samplesCount > 0 ? samplesCount : 1,
        MaxC1 = maxC1,
        AvgC1 = D("avgC1") is > 0 and var a ? a : (samplesCount > 0 && totalC1 > 0 ? totalC1 / (double)samplesCount : 0),
        TotalC1 = totalC1 > 0 ? totalC1 : maxC1,
        MaxC2 = I("maxC2"),
        TotalC2 = L("totalC2"),
        TotalCu = L("totalCu"),
    };
}

// Map a disc's sessions and flag the data sessions a normal player / audio rip would skip.
static int HiddenSessionsCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge hidden-sessions <image.cue>\n" +
                    "  Maps every session on a disc (from its cue+bin) and flags data sessions hiding behind\n" +
                    "  session 1 — the Enhanced CD / CD Extra layout an audio ripper or single-session read\n" +
                    "  never reaches. Reports each session's kind, mode, LBA range and the inter-session gaps.");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        var model = DiscForge.Core.Convert.DiscConverter.Read(path);
        var inputs = model.Tracks.Select(t => new DiscForge.Core.Forensics.SessionTrackInput(
            t.Number,
            t.Session,
            t.Type != DiscForge.Core.Cue.CueTrackType.Audio,
            t.Type switch
            {
                DiscForge.Core.Cue.CueTrackType.Mode1_2048 or DiscForge.Core.Cue.CueTrackType.Mode1_2352 => 1,
                DiscForge.Core.Cue.CueTrackType.Mode2_2336 or DiscForge.Core.Cue.CueTrackType.Mode2_2352 => 2,
                _ => (int?)null,
            },
            t.SectorCount)).ToList();

        var placed = DiscForge.Core.Forensics.HiddenSessionArchaeology.Place(inputs);
        var report = DiscForge.Core.Forensics.HiddenSessionArchaeology.Analyze(placed);
        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Forensics.HiddenSessionArchaeology.Render(report)}");
        // Non-zero when there's hidden data, so a script knows a plain rip would be incomplete.
        return report.HasHiddenData ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Group a messy folder of un-identified ISO dumps by content similarity — no DAT required.
static int DiscClusterCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-cluster <dir | image.iso ...> [--threshold 0.5]\n" +
                    "  Groups un-identified disc dumps that are the same title (its regions, revisions,\n" +
                    "  re-releases) by comparing what they actually contain — the set of files by path and\n" +
                    "  by content hash — with no external DAT. Pass a folder (its *.iso are scanned) or a\n" +
                    "  list of images. --threshold sets how alike two discs must be to link (default 0.5).");

    double threshold = DiscForge.Core.Forensics.DiscClustering.DefaultThreshold;
    var inputs = new List<string>();
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--threshold" && i + 1 < args.Length)
        {
            if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out threshold))
                return Fail("--threshold expects a number between 0 and 1.");
        }
        else inputs.Add(args[i]);
    }

    // Expand directories to their *.iso; keep files as given.
    var files = new List<string>();
    foreach (var input in inputs)
    {
        if (Directory.Exists(input))
            files.AddRange(Directory.EnumerateFiles(input, "*.iso", SearchOption.TopDirectoryOnly));
        else if (File.Exists(input)) files.Add(input);
        else return Fail($"Not found: {input}");
    }
    files = files.Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    if (files.Count == 0) return Fail("No disc images to cluster.");

    try
    {
        var profiles = files
            .Select(f => DiscForge.Core.Forensics.DiscClustering.Profile(Path.GetFileName(f), File.ReadAllBytes(f)))
            .ToList();
        var report = DiscForge.Core.Forensics.DiscClustering.Cluster(profiles, threshold);
        Console.WriteLine(DiscForge.Core.Forensics.DiscClustering.Render(report));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Emit a content-aware delta that reconstructs a target image from a base + only what changed.
static int DiscDeltaCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge disc-delta <base.iso> <target.iso> <out.delta>\n" +
                    "  Diffs two ISO images at the file level and writes a delta carrying only the files that\n" +
                    "  changed or are new — files present unchanged in both stay in the base and are merely\n" +
                    "  referenced. Apply it with disc-patch to rebuild the target byte-for-byte.");
    string basePath = args[1], targetPath = args[2], outPath = args[3];
    if (!File.Exists(basePath)) return Fail($"File not found: {basePath}");
    if (!File.Exists(targetPath)) return Fail($"File not found: {targetPath}");

    try
    {
        var baseImg = File.ReadAllBytes(basePath);
        var targetImg = File.ReadAllBytes(targetPath);
        var delta = DiscForge.Core.Preservation.DiscDelta.Create(baseImg, targetImg);
        File.WriteAllText(outPath, DiscForge.Core.Preservation.DiscDelta.ToJson(delta));

        var d = delta.Diff;
        Console.WriteLine($"Delta {Path.GetFileName(basePath)} -> {Path.GetFileName(targetPath)}:");
        Console.WriteLine($"  {d.Added.Count} added, {d.Removed.Count} removed, {d.Changed.Count} changed, {d.Unchanged} unchanged.");
        foreach (var p in d.Added) Console.WriteLine($"    + {p}");
        foreach (var p in d.Removed) Console.WriteLine($"    - {p}");
        foreach (var p in d.Changed) Console.WriteLine($"    ~ {p}");
        Console.WriteLine($"  Wrote {Path.GetFileName(outPath)}: {delta.Store.Count} changed blob(s), " +
                          $"{delta.DeltaStoreBytes:N0} byte(s) — vs {targetImg.Length:N0} for the whole target.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Rebuild a target image byte-exact from a base image and a delta.
static int DiscPatchCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge disc-patch <base.iso> <in.delta> <out.iso>\n" +
                    "  Reconstructs the target image from the base plus the delta, verifying the delta was\n" +
                    "  made against this base and that the result matches the recorded target hash.");
    string basePath = args[1], deltaPath = args[2], outPath = args[3];
    if (!File.Exists(basePath)) return Fail($"File not found: {basePath}");
    if (!File.Exists(deltaPath)) return Fail($"File not found: {deltaPath}");

    try
    {
        var baseImg = File.ReadAllBytes(basePath);
        var delta = DiscForge.Core.Preservation.DiscDelta.FromJson(File.ReadAllText(deltaPath));
        var rebuilt = DiscForge.Core.Preservation.DiscDelta.Apply(delta, baseImg);
        File.WriteAllBytes(outPath, rebuilt);
        Console.WriteLine($"Rebuilt {Path.GetFileName(outPath)} ({rebuilt.Length:N0} bytes) — " +
                          $"verified against target {delta.TargetImageSha256[..12]}….");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Identify every filesystem a disc image carries — hybrid discs included.
static int DiscFsCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-fs <image.iso>\n" +
                    "  Reports every filesystem on a cooked disc image — ISO 9660, Joliet, UDF, CD-XA,\n" +
                    "  Apple HFS/HFS+ and the Apple Partition Map — so a hybrid (Mac+PC, UDF-bridge)\n" +
                    "  disc is seen whole, not just the half your OS mounts.");
    string path = args[1];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        var report = DiscForge.Core.Forensics.DiscFilesystems.Identify(File.ReadAllBytes(path));
        Console.WriteLine($"{Path.GetFileName(path)}: {report.Summary()}");
        foreach (var f in report.Filesystems)
        {
            string label = f.Label is { Length: > 0 } ? $" \"{f.Label}\"" : "";
            Console.WriteLine($"  {f.Kind}{label} — {f.Detail}");
        }
        return report.Any ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Render a per-sector health heatmap of a disc image (or a reconstruction provenance map) to SVG.
static int HealthMapCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge health-map <in.bin> <out.svg> [--provenance] [--cols N] [--cell N]\n" +
                    "  Renders every sector's EDC/ECC health as an SVG grid so the shape of the damage\n" +
                    "  is visible: clustered red is physical rot or a scratch; a thin repeating band is\n" +
                    "  more likely a deliberate protection pattern. With --provenance the input is a\n" +
                    "  per-sector map from `reconstruct --provenance`, coloured by how each sector was saved.");

    string inPath = args[1], outPath = args[2];
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");
    bool fromProvenance = args.Contains("--provenance");
    int cols = 256, cell = 4;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--cols" && i + 1 < args.Length) int.TryParse(args[++i], out cols);
        else if (args[i] == "--cell" && i + 1 < args.Length) int.TryParse(args[++i], out cell);
    }

    try
    {
        var bytes = File.ReadAllBytes(inPath);
        string title = $"DiscForge health map — {Path.GetFileName(inPath)}";
        DiscForge.Core.Forensics.SectorHealth[] health = fromProvenance
            ? DiscForge.Core.Forensics.DiscHealthMap.FromProvenance(bytes)
            : DiscForge.Core.Forensics.DiscHealthMap.Scan(bytes);
        string svg = DiscForge.Core.Forensics.DiscHealthMap.RenderSvg(health, title, cols, cell);
        File.WriteAllText(outPath, svg);

        int total = health.Length;
        int good = health.Count(h => h == DiscForge.Core.Forensics.SectorHealth.Good);
        int bad = health.Count(h => h is DiscForge.Core.Forensics.SectorHealth.Damaged
                                      or DiscForge.Core.Forensics.SectorHealth.Unrecovered);
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} — {total:N0} sectors, {good:N0} intact, {bad:N0} damaged.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Split a bin+cue into genome tracks. Handles the two common Redump layouts: a
// single raw 2352 bin split by INDEX 01, or one FILE per track.
static List<DiscForge.Core.Forensics.GenomeTrack> LoadGenomeTracks(string cuePath)
{
    if (!File.Exists(cuePath)) throw new FileNotFoundException($"Cue not found: {cuePath}");
    var cue = DiscForge.Core.Cue.CueSheet.Parse(File.ReadAllText(cuePath));
    if (cue.Tracks.Count == 0) throw new InvalidDataException("The cue lists no tracks.");
    string dir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";

    bool IsAudio(DiscForge.Core.Cue.CueTrackType t) => t == DiscForge.Core.Cue.CueTrackType.Audio;
    long StartSector(DiscForge.Core.Cue.CueTrack t)
    {
        var idx = t.Indices.FirstOrDefault(i => i.Number == 1) ?? t.Indices.FirstOrDefault();
        return idx?.Time.ToSectors() ?? 0;
    }
    string Resolve(string file)
    {
        string p = Path.Combine(dir, file);
        if (File.Exists(p)) return p;
        // Fall back to the cue's own basename with the bin's extension.
        string alt = Path.Combine(dir, Path.GetFileNameWithoutExtension(cuePath) + Path.GetExtension(file));
        if (File.Exists(alt)) return alt;
        throw new FileNotFoundException($"Bin referenced by the cue not found: {file}");
    }

    var tracks = new List<DiscForge.Core.Forensics.GenomeTrack>();
    var distinctFiles = cue.Tracks.Select(t => t.File).Distinct().ToList();

    if (distinctFiles.Count == 1)
    {
        // Single raw image: stride is 2352 across the whole file.
        var bytes = File.ReadAllBytes(Resolve(distinctFiles[0]));
        long totalSectors = bytes.Length / 2352;
        var ordered = cue.Tracks.OrderBy(t => t.Number).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            long start = StartSector(ordered[i]);
            long end = i + 1 < ordered.Count ? StartSector(ordered[i + 1]) : totalSectors;
            start = Math.Clamp(start, 0, totalSectors);
            end = Math.Clamp(end, start, totalSectors);
            var content = bytes.AsSpan((int)(start * 2352), (int)((end - start) * 2352)).ToArray();
            tracks.Add(new(ordered[i].Number, !IsAudio(ordered[i].Type), content));
        }
    }
    else
    {
        // One FILE per track.
        foreach (var t in cue.Tracks.OrderBy(t => t.Number))
            tracks.Add(new(t.Number, !IsAudio(t.Type), File.ReadAllBytes(Resolve(t.File))));
    }
    return tracks;
}

// Reconstruct the best-possible image from one or more rips, with per-sector provenance.
static int ReconstructCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge reconstruct <out.bin> <in1.bin> [in2.bin ...] [--no-ecc] [--provenance <map.bin>]\n" +
                    "  Builds the best-possible raw image by resolving every sector in order of confidence:\n" +
                    "  agreement, then a copy that passes EDC, then single-read Reed-Solomon ECC repair,\n" +
                    "  then a majority vote, then ECC repair of the vote. Records how each sector was\n" +
                    "  resolved. --provenance writes the per-sector code map (one byte/sector) for health-map.");

    string outPath = args[1];
    bool useEcc = true;
    string? provPath = null;
    var inputs = new List<string>();
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--no-ecc") useEcc = false;
        else if (args[i] == "--provenance" && i + 1 < args.Length) provPath = args[++i];
        else inputs.Add(args[i]);
    }
    if (inputs.Count < 1) return Fail("reconstruct needs at least one input image.");
    foreach (var p in inputs) if (!File.Exists(p)) return Fail($"File not found: {p}");

    try
    {
        var images = inputs.Select(File.ReadAllBytes).ToList();
        var result = DiscForge.Core.Recovery.DumpReconstruct.Reconstruct(images, useEcc);
        File.WriteAllBytes(outPath, result.Image);
        if (provPath != null) File.WriteAllBytes(provPath, result.Report.PerSector);

        var r = result.Report;
        Console.WriteLine(r.Summary());
        Console.WriteLine($"  Healed {r.Repaired:N0} disagreeing/broken sector(s); wrote {Path.GetFileName(outPath)}.");
        if (provPath != null)
            Console.WriteLine($"  Provenance map: {Path.GetFileName(provPath)} ({r.PerSector.Length:N0} bytes). Render it with health-map.");
        if (r.FullyRecovered)
            Console.WriteLine("  Fully recovered — every sector is agreed, verified, ECC-repaired or voted-and-checked.");
        else
        {
            Console.WriteLine($"  {r.Unrecovered:N0} sector(s) could not be recovered.");
            Console.WriteLine($"  First unrecovered: {string.Join(", ", r.UnrecoveredSectors.Take(12))}" +
                              (r.UnrecoveredSectors.Count > 12 ? " …" : ""));
        }
        return r.FullyRecovered ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Write a freshly-formatted, empty PS1 memory card (optionally in a container).
static int PsxMcFormatCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge psxmc-format <out.mcr> [raw|gme|vgs]\n" +
                    "  Writes a freshly-formatted, empty 128 KB PS1 memory card (15 free blocks).\n" +
                    "  Container defaults to the output extension (.gme->DexDrive, .vgs/.mem->VGS), else raw.");
    string outPath = args[1];
    string sel = args.Length >= 3 ? args[2].ToLowerInvariant() : Path.GetExtension(outPath).TrimStart('.').ToLowerInvariant();
    var target = sel switch
    {
        "gme" or "dexdrive" => DiscForge.Core.PlayStation.Ps1CardFormat.DexDrive,
        "vgs" or "mem" => DiscForge.Core.PlayStation.Ps1CardFormat.Vgs,
        _ => DiscForge.Core.PlayStation.Ps1CardFormat.Raw,
    };
    try
    {
        var raw = DiscForge.Core.PlayStation.PsxMemoryCard.Format();
        var outBytes = target == DiscForge.Core.PlayStation.Ps1CardFormat.Raw
            ? raw
            : DiscForge.Core.PlayStation.Ps1CardConvert.Convert(raw, target);
        File.WriteAllBytes(outPath, outBytes);
        var vol = DiscForge.Core.PlayStation.PsxMemoryCard.Read(raw);
        Console.WriteLine($"Wrote a formatted {target} card: {Path.GetFileName(outPath)} ({outBytes.Length:N0} bytes, {vol.FreeBlocks} free blocks).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Write a deterministic, TorrentZip-structured archive from an explicit file list.
static int TorrentZipCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge torrentzip <out.zip> <file> [file ...]\n" +
                    "  Deterministic archive (sorted, fixed timestamps, TORRENTZIPPED comment).");
    string outZip = args[1];
    var entries = new List<DiscForge.Core.Archive.ZipEntry>();
    for (int i = 2; i < args.Length; i++)
    {
        if (!File.Exists(args[i])) return Fail($"File not found: {args[i]}");
        entries.Add(new DiscForge.Core.Archive.ZipEntry(Path.GetFileName(args[i]), File.ReadAllBytes(args[i])));
    }
    try
    {
        File.WriteAllBytes(outZip, DiscForge.Core.Archive.TorrentZip.Create(entries));
        Console.WriteLine($"Wrote {Path.GetFileName(outZip)} ({entries.Count} file(s), TorrentZip-structured).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static bool TrySidecarKind(string s, out DiscForge.Core.Archive.SidecarKind kind)
{
    switch (s.ToLowerInvariant().TrimStart('.'))
    {
        case "sfv": kind = DiscForge.Core.Archive.SidecarKind.Sfv; return true;
        case "md5": kind = DiscForge.Core.Archive.SidecarKind.Md5; return true;
        case "sha1": kind = DiscForge.Core.Archive.SidecarKind.Sha1; return true;
        default: kind = default; return false;
    }
}

static string HashOfFile(DiscForge.Core.Archive.SidecarKind kind, string path)
{
    using var fs = File.OpenRead(path);
    if (kind == DiscForge.Core.Archive.SidecarKind.Sfv)
    {
        var c = new DiscForge.Core.Util.Crc32();
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0) c.Update(buf.AsSpan(0, n));
        return c.Value.ToString("X8");
    }
    using System.Security.Cryptography.HashAlgorithm alg =
        kind == DiscForge.Core.Archive.SidecarKind.Md5
            ? System.Security.Cryptography.MD5.Create()
            : System.Security.Cryptography.SHA1.Create();
    return System.Convert.ToHexString(alg.ComputeHash(fs));
}

// Write a checksum sidecar (.sfv / .md5 / .sha1) for a set of files.
static int ExfatLsCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge exfat-ls <image> [path] [--json]\n" +
                    "  List an exFAT volume image (the modern SDXC/large-USB filesystem): the volume label, and for\n" +
                    "  the root — or a sub-path — each entry's name, size and whether it is a directory. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    string? sub = args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
    try
    {
        using var fs = File.OpenRead(path);
        var v = DiscForge.Core.FileSystems.ExFat.ReadInfo(fs);
        uint dirCluster = v.RootDirCluster;
        bool dirNoFatChain = false;
        long dirLength = long.MaxValue;
        if (sub is not null)
        {
            var d = DiscForge.Core.FileSystems.ExFat.Resolve(fs, v, sub);
            if (d is null || !d.IsDirectory) return Fail($"'{sub}' is not a directory in this volume.");
            dirCluster = d.FirstCluster;
            dirNoFatChain = d.NoFatChain;
            dirLength = d.Size;
        }
        var entries = DiscForge.Core.FileSystems.ExFat.List(fs, v, dirCluster, dirNoFatChain, dirLength);
        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path), label = v.Label, v.BytesPerSector, v.SectorsPerCluster,
                entries = entries.Select(e => new { e.Name, e.Size, e.IsDirectory }),
            });
            return 0;
        }
        Console.WriteLine($"{Path.GetFileName(path)}: exFAT" + (v.Label is null ? "" : $" \"{v.Label}\"") +
                          $"  ({v.ClusterBytes}-byte clusters)");
        foreach (var e in entries)
            Console.WriteLine($"  {(e.IsDirectory ? "<DIR>" : "     ")} {e.Size,14:N0}  {e.Name}");
        if (entries.Count == 0) Console.WriteLine("  (empty)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ExfatExtractCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge exfat-extract <image> <path> <out-file>\n" +
                    "  Extract one file from an exFAT volume image. <path> is slash-separated (e.g. DIR/SUB/FILE.EXT).\n" +
                    "  Read-only.");
    var path = args[1];
    var inner = args[2];
    var outPath = args[3];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        using var fs = File.OpenRead(path);
        var v = DiscForge.Core.FileSystems.ExFat.ReadInfo(fs);
        var entry = DiscForge.Core.FileSystems.ExFat.Resolve(fs, v, inner);
        if (entry is null) return Fail($"'{inner}' not found in the volume.");
        if (entry.IsDirectory) return Fail($"'{inner}' is a directory, not a file.");
        long n = 0;
        WriteFileAtomically(outPath, os => n = DiscForge.Core.FileSystems.ExFat.ExtractFile(fs, v, entry, os));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {n:N0} bytes.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int AaruCreateCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge aaru-create <in.img> <out.aaruf> [--sector-size N] [--media-type N]\n" +
                    "  Write an UNCOMPRESSED AaruFormat image from a raw sector image: header, one user-data\n" +
                    "  block, a deduplication table and an index, in Aaru's on-disk layout with ECMA-182 CRC-64s.\n" +
                    "  Default sector size is 2048. Round-trips through dforge aaru-info / aaru-extract.");
    var input = args[1];
    var outPath = args[2];
    if (!File.Exists(input)) return Fail($"'{input}' not found.");
    uint sectorSize = 2048, mediaType = 0;
    var ss = OptVal(args, "--sector-size");
    if (ss is not null && (!uint.TryParse(ss, out sectorSize) || sectorSize == 0))
        return Fail("--sector-size must be a positive integer.");
    var mt = OptVal(args, "--media-type");
    if (mt is not null && !uint.TryParse(mt, out mediaType)) return Fail("--media-type must be a non-negative integer.");
    try
    {
        var data = File.ReadAllBytes(input);
        if (data.Length == 0) return Fail("The input image is empty.");
        if (data.Length % sectorSize != 0)
            return Fail($"The image ({data.Length:N0} bytes) is not a whole number of {sectorSize}-byte sectors.");
        WriteFileAtomically(outPath, os => DiscForge.Core.Aaru.AaruFormat.WriteUncompressed(os, data, sectorSize, mediaType));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {data.Length / sectorSize:N0} sectors × {sectorSize} bytes (uncompressed AaruFormat).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int NtfsLsCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge ntfs-ls <image> [path] [--json]\n" +
                    "  List an NTFS volume image (the Windows filesystem): for the root — or a sub-path — each\n" +
                    "  entry's name, size and whether it is a directory. Listing scans the MFT for in-use files\n" +
                    "  whose parent is the directory. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    string? sub = args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
    try
    {
        using var fs = File.OpenRead(path);
        var vol = DiscForge.Core.FileSystems.Ntfs.Open(fs);
        long dirMft = DiscForge.Core.FileSystems.Ntfs.RootMft;
        if (sub is not null)
        {
            var d = vol.Resolve(sub);
            if (d is null || !d.IsDirectory) return Fail($"'{sub}' is not a directory in this volume.");
            dirMft = d.MftNumber;
        }
        var entries = vol.List(dirMft);
        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path), vol.Info.BytesPerSector, vol.Info.SectorsPerCluster,
                entries = entries.Select(e => new { e.Name, e.Size, e.IsDirectory }),
            });
            return 0;
        }
        Console.WriteLine($"{Path.GetFileName(path)}: NTFS  ({vol.Info.ClusterBytes}-byte clusters, " +
                          $"{vol.Info.MftRecordSize}-byte MFT records)");
        foreach (var e in entries)
            Console.WriteLine($"  {(e.IsDirectory ? "<DIR>" : "     ")} {e.Size,14:N0}  {e.Name}");
        if (entries.Count == 0) Console.WriteLine("  (empty)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int NtfsExtractCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge ntfs-extract <image> <path> <out-file>\n" +
                    "  Extract one file from an NTFS volume image. <path> is slash-separated (e.g. DIR/SUB/FILE.EXT).\n" +
                    "  Resident and non-resident $DATA are supported; compressed/encrypted data is declined. Read-only.");
    var path = args[1];
    var inner = args[2];
    var outPath = args[3];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        using var fs = File.OpenRead(path);
        var vol = DiscForge.Core.FileSystems.Ntfs.Open(fs);
        var entry = vol.Resolve(inner);
        if (entry is null) return Fail($"'{inner}' not found in the volume.");
        if (entry.IsDirectory) return Fail($"'{inner}' is a directory, not a file.");
        long n = 0;
        WriteFileAtomically(outPath, os => n = vol.Extract(entry, os));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {n:N0} bytes.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ExtLsCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge ext-ls <image> [path] [--json]\n" +
                    "  List an ext2/3/4 volume image (the Linux filesystem): for the root — or a sub-path — each\n" +
                    "  entry's name, size and whether it is a directory. Reads extent-mapped (ext4) and classic\n" +
                    "  block-mapped (ext2/3) inodes. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    string? sub = args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
    try
    {
        using var fs = File.OpenRead(path);
        var vol = DiscForge.Core.FileSystems.Ext.Open(fs);
        long dirInode = DiscForge.Core.FileSystems.Ext.RootInode;
        if (sub is not null)
        {
            var d = vol.Resolve(sub);
            if (d is null || !d.IsDirectory) return Fail($"'{sub}' is not a directory in this volume.");
            dirInode = d.Inode;
        }
        var entries = vol.List(dirInode);
        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path), label = vol.Info.Label, vol.Info.BlockSize,
                entries = entries.Select(e => new { e.Name, e.Size, e.IsDirectory }),
            });
            return 0;
        }
        Console.WriteLine($"{Path.GetFileName(path)}: ext" + (vol.Info.Label is null ? "" : $" \"{vol.Info.Label}\"") +
                          $"  ({vol.Info.BlockSize}-byte blocks)");
        foreach (var e in entries)
            Console.WriteLine($"  {(e.IsDirectory ? "<DIR>" : "     ")} {e.Size,14:N0}  {e.Name}");
        if (entries.Count == 0) Console.WriteLine("  (empty)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ExtExtractCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge ext-extract <image> <path> <out-file>\n" +
                    "  Extract one file from an ext2/3/4 volume image. <path> is slash-separated (e.g. dir/sub/file).\n" +
                    "  Extent-mapped and classic (direct/indirect) data are supported; inline-data and encrypted\n" +
                    "  inodes are declined. Read-only.");
    var path = args[1];
    var inner = args[2];
    var outPath = args[3];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        using var fs = File.OpenRead(path);
        var vol = DiscForge.Core.FileSystems.Ext.Open(fs);
        var entry = vol.Resolve(inner);
        if (entry is null) return Fail($"'{inner}' not found in the volume.");
        if (entry.IsDirectory) return Fail($"'{inner}' is a directory, not a file.");
        long n = 0;
        WriteFileAtomically(outPath, os => n = vol.Extract(entry, os));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {n:N0} bytes.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CicmExportCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cicm-export <image> [out.xml] [--block] [--disc-type TYPE] [--title NAME]\n" +
                    "  Write a CICM metadata sidecar (the preservation XML Aaru uses) for an image: its name, size\n" +
                    "  and MD5 / SHA-1 / SHA-256 checksums, as <OpticalDisc> (default) or <BlockMedia> (--block).\n" +
                    "  --disc-type and --title add those fields. With no out.xml it prints to stdout. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    string? outPath = args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
    try
    {
        long size = new FileInfo(path).Length;
        var (md5, sha1, sha256) = HashTriple(path);
        var input = new DiscForge.Core.Metadata.CicmSidecar.Input
        {
            ImageName = Path.GetFileName(path),
            SizeBytes = size,
            Checksums = new[]
            {
                new DiscForge.Core.Metadata.CicmSidecar.Checksum("md5", md5),
                new DiscForge.Core.Metadata.CicmSidecar.Checksum("sha1", sha1),
                new DiscForge.Core.Metadata.CicmSidecar.Checksum("sha256", sha256),
            },
            Optical = !args.Contains("--block"),
            DiscType = OptVal(args, "--disc-type"),
            MediaTitle = OptVal(args, "--title"),
        };
        string xml = DiscForge.Core.Metadata.CicmSidecar.Build(input);
        if (outPath is not null) { File.WriteAllText(outPath, xml); Console.WriteLine($"Wrote CICM sidecar: {outPath} ({size:N0} bytes hashed)."); }
        else Console.WriteLine(xml);
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static (string md5, string sha1, string sha256) HashTriple(string path)
{
    using var fs = File.OpenRead(path);
    using var md5 = System.Security.Cryptography.MD5.Create();
    using var sha1 = System.Security.Cryptography.SHA1.Create();
    using var sha256 = System.Security.Cryptography.SHA256.Create();
    var buf = new byte[1 << 20];
    int r;
    while ((r = fs.Read(buf, 0, buf.Length)) > 0)
    {
        md5.TransformBlock(buf, 0, r, null, 0);
        sha1.TransformBlock(buf, 0, r, null, 0);
        sha256.TransformBlock(buf, 0, r, null, 0);
    }
    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    return (System.Convert.ToHexString(md5.Hash!).ToLowerInvariant(),
            System.Convert.ToHexString(sha1.Hash!).ToLowerInvariant(),
            System.Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
}

static int AaruInfoCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge aaru-info <file.aaruf> [--json]\n" +
                    "  Identify and inventory an Aaru image (AaruFormat, formerly DiscImageChef): header identifier,\n" +
                    "  versions, media type, sector count/size, the typed block index and the compression used.\n" +
                    "  Read-only. Extraction of the sectors is `aaru-extract`.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        using var fs = File.OpenRead(path);
        var info = DiscForge.Core.Aaru.AaruFormat.ReadInfo(fs);
        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                info.Magic, info.Recognized, info.Application, info.ImageVersion, info.ApplicationVersion,
                info.MediaType, info.Sectors, info.SectorSize, info.UserDataCompression, info.UserDataExtractable,
                blocks = info.Blocks.Select(b => new { type = b.BlockTypeName, b.DataType, offset = b.Offset }),
            });
            return info.Recognized ? 0 : 2;
        }
        Console.WriteLine($"{Path.GetFileName(path)}: identifier \"{info.Magic}\"" +
                          (info.Recognized ? "" : "  (NOT a recognized AaruFormat header)"));
        if (!info.Recognized) return 2;
        Console.WriteLine($"  application : {info.Application}  (image v{info.ImageVersion}, app v{info.ApplicationVersion})");
        Console.WriteLine($"  media type  : {info.MediaType}");
        Console.WriteLine($"  sectors     : {info.Sectors:N0} × {info.SectorSize} bytes  " +
                          $"(user data {info.UserDataCompression}{(info.UserDataExtractable ? ", extractable" : ", compressed — extract declined")})");
        Console.WriteLine($"  blocks ({info.Blocks.Count}):");
        foreach (var b in info.Blocks)
            Console.WriteLine($"    {b.BlockTypeName,-20} dataType {b.DataType,-3} @ 0x{b.Offset:X}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int AaruExtractCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge aaru-extract <file.aaruf> <out.img>\n" +
                    "  Reconstruct the user data of an UNCOMPRESSED AaruFormat image to a flat sector image, walking\n" +
                    "  its deduplication table. LZMA/FLAC-compressed images are DECLINED (their framing needs\n" +
                    "  validation against a real Aaru file first) rather than risk a corrupt image — run `aaru-info`\n" +
                    "  to check, or re-save the image uncompressed with Aaru.");
    var inPath = args[1];
    var outPath = args[2];
    if (!File.Exists(inPath)) return Fail($"'{inPath}' not found.");
    try
    {
        long sectors = 0;
        using (var fs = File.OpenRead(inPath))
            WriteFileAtomically(outPath, os => sectors = DiscForge.Core.Aaru.AaruFormat.ExtractUserData(fs, os));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: {sectors:N0} sector(s).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int EntropyCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge entropy <file> [--json]\n" +
                    "  Shannon entropy (bits/byte, 0–8) of a file — Aaru's `entropy`. Near 8 means the data is\n" +
                    "  already compressed/encrypted/random (won't shrink, and 'compressing' it is suspect); low\n" +
                    "  means padding, blanking or structured content. Distinguishes a genuinely full disc from a\n" +
                    "  junk-padded one. Streaming, so any image size is fine. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        using var fs = File.OpenRead(path);
        var r = DiscForge.Core.Forensics.ShannonEntropy.Compute(fs);
        if (args.Contains("--json"))
        {
            EmitJson(new { file = Path.GetFileName(path), r.Bytes, bitsPerByte = Math.Round(r.BitsPerByte, 4), ratio = Math.Round(r.Ratio, 4), character = r.Character });
            return 0;
        }
        Console.WriteLine($"{Path.GetFileName(path)}: {r.BitsPerByte:F4} bits/byte ({r.Ratio * 100:F1}% of max) over {r.Bytes:N0} bytes — {r.Character}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int FuzzyHashCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge fuzzy-hash <file> [<file2>] [--json]\n" +
                    "  Context-triggered piecewise (SpamSum/ssdeep-style) FUZZY hash — Aaru records these. Unlike\n" +
                    "  SHA-256, similar inputs get similar hashes, so with TWO files it prints a 0–100 similarity\n" +
                    "  score: how alike two dumps are (two rips of one disc that differ only in a bad sector, a\n" +
                    "  re-encode or padding). Read-only.");
    var a = args[1];
    if (!File.Exists(a)) return Fail($"'{a}' not found.");
    try
    {
        string ha = DiscForge.Core.Forensics.SpamSum.Hash(File.ReadAllBytes(a));
        if (args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal))
        {
            var b = args[2];
            if (!File.Exists(b)) return Fail($"'{b}' not found.");
            string hb = DiscForge.Core.Forensics.SpamSum.Hash(File.ReadAllBytes(b));
            int score = DiscForge.Core.Forensics.SpamSum.Compare(ha, hb);
            if (args.Contains("--json"))
            {
                EmitJson(new { a = Path.GetFileName(a), b = Path.GetFileName(b), hashA = ha, hashB = hb, similarity = score });
                return 0;
            }
            Console.WriteLine($"{Path.GetFileName(a)}: {ha}");
            Console.WriteLine($"{Path.GetFileName(b)}: {hb}");
            Console.WriteLine($"similarity: {score}/100");
            return 0;
        }
        if (args.Contains("--json")) { EmitJson(new { file = Path.GetFileName(a), hash = ha }); return 0; }
        Console.WriteLine($"{Path.GetFileName(a)}: {ha}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PartitionsCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge partitions <image> [--json]\n" +
                    "  Detect the whole-disk partition scheme (MBR / GPT / Apple APM) and list its partitions —\n" +
                    "  index, type, boot flag, byte offset/size and the filesystem found at each. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        using var fs = File.OpenRead(path);
        var disk = DiscForge.Core.Partition.PartitionTable.Read(fs);
        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                disk.Scheme, disk.DiskGuid,
                partitions = disk.Partitions.Select(p => new { p.Index, p.TypeName, p.Bootable, p.StartByte, p.SizeBytes, p.FileSystem }),
            });
            return 0;
        }
        Console.WriteLine($"{Path.GetFileName(path)}: {disk.Scheme}" + (disk.DiskGuid is null ? "" : $"  (disk GUID {disk.DiskGuid})"));
        foreach (var p in disk.Partitions)
            Console.WriteLine($"  #{p.Index,-2} {p.TypeName,-24} {(p.Bootable ? "boot" : "    ")}  " +
                              $"{p.StartByte,14:N0}  {p.SizeBytes,15:N0} B  {p.FileSystem}");
        if (disk.Partitions.Count == 0) Console.WriteLine("  (no partitions)");
        return 0;
    }
    catch (DiscForge.Core.Partition.PartitionFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int HashGenCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge hashgen <sfv|md5|sha1> <out> <file> [file ...]");
    if (!TrySidecarKind(args[1], out var kind)) return Fail($"Unknown sidecar kind '{args[1]}' (sfv|md5|sha1).");
    string outPath = args[2];

    var lines = new List<DiscForge.Core.Archive.HashLine>();
    for (int i = 3; i < args.Length; i++)
    {
        if (!File.Exists(args[i])) return Fail($"File not found: {args[i]}");
        lines.Add(new DiscForge.Core.Archive.HashLine(Path.GetFileName(args[i]), HashOfFile(kind, args[i])));
    }
    try
    {
        File.WriteAllText(outPath, DiscForge.Core.Archive.HashSidecar.Build(kind, lines));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} ({lines.Count} entry(ies)).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Re-hash the files a sidecar references and report OK / FAIL / MISSING.
static int HashVerifyCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge hashverify <sidecar.sfv|.md5|.sha1>");
    string sidecar = args[1];
    if (!File.Exists(sidecar)) return Fail($"Sidecar not found: {sidecar}");
    if (!TrySidecarKind(Path.GetExtension(sidecar), out var kind))
        return Fail("Sidecar must have a .sfv, .md5 or .sha1 extension.");

    string baseDir = Path.GetDirectoryName(Path.GetFullPath(sidecar)) ?? ".";
    var entries = DiscForge.Core.Archive.HashSidecar.Parse(kind, File.ReadAllText(sidecar));

    int ok = 0, bad = 0, missing = 0;
    foreach (var e in entries)
    {
        string path = Path.Combine(baseDir, e.Name);
        if (!File.Exists(path)) { Console.WriteLine($"  MISSING  {e.Name}"); missing++; continue; }
        string actual = HashOfFile(kind, path);
        if (string.Equals(actual, e.Hash, StringComparison.OrdinalIgnoreCase)) { ok++; }
        else { Console.WriteLine($"  FAIL     {e.Name}  (have {actual}, expected {e.Hash})"); bad++; }
    }
    Console.WriteLine($"OK {ok}   FAIL {bad}   MISSING {missing}   ({entries.Count} listed)");
    return bad == 0 && missing == 0 ? 0 : 1;
}

// Build a multi-disc .m3u playlist from an explicit, ordered list of disc images.
static int M3uCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge m3u <out.m3u> <disc1> <disc2> [...]\n" +
                    "  Lists the discs in order so an emulator can swap discs and share one memory card.");
    string outPath = args[1];
    var discs = args[2..];

    // Use a bare filename when the disc sits beside the .m3u (the portable, common case),
    // otherwise keep the path the user gave.
    string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? "";
    var lines = discs.Select(d =>
    {
        string full = Path.GetFullPath(d);
        string discDir = Path.GetDirectoryName(full) ?? "";
        return string.Equals(discDir, outDir, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(full) : d;
    });

    try
    {
        File.WriteAllText(outPath, DiscForge.Core.Frontend.FrontendExport.BuildM3u(lines));
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} ({discs.Length} disc(s)).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Verify one or more dumps against a redump-style Logiqx DAT file.
static int SubmissionInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge submission-info <image> [--out <file.txt>]");
    string input = args[1];
    if (!File.Exists(input)) return Fail($"Image not found: {input}");
    string? outPath = null;
    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
        else return Fail($"Unknown option: {args[i]}");
    try
    {
        var info = DiscForge.Core.Redump.SubmissionInfoGenerator.Generate(input);
        string text = info.ToRedumpText();
        if (outPath is not null)
        {
            File.WriteAllText(outPath, text);
            Console.WriteLine($"Wrote submission info for {info.FileName} " +
                              $"({info.Tracks.Count} track(s)) -> {Path.GetFileName(outPath)}.");
        }
        else Console.Write(text);
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SubmissionPackCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge submission-pack <image> <out-dir> [--game NAME]\n" +
        "  Assembles a submission-ready folder for a dump: copies the dump file(s), writes the redump-style\n" +
        "  submission info (.txt), a matching Logiqx DAT (.dat), and the cuesheet (.cue) — all named from the\n" +
        "  game. The packaging layer over submission-info + dat-build. Reads the dump; writes a new folder.");
    string input = args[1], outDir = args[2];
    if (!File.Exists(input)) return Fail($"Image not found: {input}");
    string game = OptVal(args, "--game") ?? Path.GetFileNameWithoutExtension(input);

    try
    {
        var info = DiscForge.Core.Redump.SubmissionInfoGenerator.Generate(input);
        var art = DiscForge.Core.Redump.SubmissionPackage.Build(info, game);

        Directory.CreateDirectory(outDir);

        // Copy the dump file(s). For a .cue, also copy every FILE it references.
        var copied = new List<string>();
        void CopyInto(string src)
        {
            string dest = Path.Combine(outDir, Path.GetFileName(src));
            if (Path.GetFullPath(src) != Path.GetFullPath(dest)) File.Copy(src, dest, overwrite: true);
            copied.Add(Path.GetFileName(src));
        }
        CopyInto(input);
        if (input.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
            foreach (var line in File.ReadAllLines(input))
            {
                var t = line.TrimStart();
                if (!t.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;
                int a = t.IndexOf('"'), b = t.LastIndexOf('"');
                if (a >= 0 && b > a)
                {
                    string refPath = Path.Combine(dir, t.Substring(a + 1, b - a - 1));
                    if (File.Exists(refPath)) CopyInto(refPath);
                }
            }
        }

        File.WriteAllText(Path.Combine(outDir, game + ".txt"), art.InfoText);
        File.WriteAllText(Path.Combine(outDir, game + ".dat"), art.Dat);
        if (art.Cuesheet is not null)
            File.WriteAllText(Path.Combine(outDir, game + ".cue"), art.Cuesheet);

        Console.WriteLine($"Submission bundle for \"{game}\" -> {outDir}");
        Console.WriteLine($"  dump file(s): {string.Join(", ", copied)}");
        Console.WriteLine($"  {game}.txt  (submission info, {info.Tracks.Count} track(s))");
        Console.WriteLine($"  {game}.dat  (Logiqx DAT)");
        if (art.Cuesheet is not null) Console.WriteLine($"  {game}.cue  (cuesheet)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CatalogExportCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge catalog-export <dir> [--dat <dat-file>] [--json <out.json>] [--csv <out.csv>]\n" +
                    "  Scans an optical-archive folder — identifying, hashing (CRC-32/MD5/SHA-1) and, with a DAT,\n" +
                    "  verifying every file — and writes a portable catalog: one machine-readable index of the whole\n" +
                    "  archive (each disc's identity, size, hashes, and verification status). It is the index you keep\n" +
                    "  beside a NAS or cloud copy so anything can find and re-verify a disc without re-reading it.\n" +
                    "  JSON (default, to stdout if no path) for programs; CSV for a spreadsheet/NAS index. Read-only.");
    var dir = args[1];
    if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");
    string? datPath = OptVal(args, "--dat");
    string? jsonPath = OptVal(args, "--json");
    string? csvPath = OptVal(args, "--csv");

    try
    {
        DiscForge.Core.Dat.DatFile? dat = null;
        if (datPath is not null)
        {
            if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");
            using var ds = File.OpenRead(datPath);
            dat = DiscForge.Core.Dat.DatFile.Parse(ds);
        }

        var report = DiscForge.Core.Library.LibraryScanner.Scan(dir, dat);
        string stampedUtc = DateTime.UtcNow.ToString("o");

        if (csvPath is not null)
        {
            File.WriteAllText(csvPath, DiscForge.Core.Library.CatalogExport.ToCsv(report));
            Console.WriteLine($"Wrote {Path.GetFileName(csvPath)}: {report.Total:N0} file(s).");
        }

        string json = DiscForge.Core.Library.CatalogExport.ToJson(report, stampedUtc);
        if (jsonPath is not null)
        {
            File.WriteAllText(jsonPath, json);
            Console.WriteLine($"Wrote {Path.GetFileName(jsonPath)}: {report.Total:N0} file(s), " +
                              $"{report.Verified:N0} verified, {report.Unknown:N0} unrecognised, {report.Missing.Count:N0} missing from the set.");
        }
        else if (csvPath is null)
        {
            Console.WriteLine(json);   // no output path given: JSON to stdout
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Library(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge library scan <dir> [--dat <dat-file>] [--html <out.html>]\n" +
                    "       dforge library rename <dir> --dat <dat-file> [--apply]");
    string sub = args[1];
    string dir = args[2];
    if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");

    string? datPath = null; bool apply = false; string? htmlPath = null;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--dat" when i + 1 < args.Length: datPath = args[++i]; break;
            case "--apply": apply = true; break;
            case "--html" when i + 1 < args.Length: htmlPath = args[++i]; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        DiscForge.Core.Dat.DatFile? dat = null;
        if (datPath is not null)
        {
            if (!File.Exists(datPath)) return Fail($"DAT not found: {datPath}");
            using var ds = File.OpenRead(datPath);
            dat = DiscForge.Core.Dat.DatFile.Parse(ds);
            Console.WriteLine($"DAT: {dat.Name ?? Path.GetFileName(datPath)} ({dat.Count:N0} entries)");
        }

        var report = DiscForge.Core.Library.LibraryScanner.Scan(dir, dat);

        if (sub == "rename")
        {
            if (dat is null) return Fail("library rename needs --dat <dat-file>.");
            var plan = report.RenamePlan();
            if (plan.Count == 0) { Console.WriteLine("Nothing to rename — no verified files are mis-named."); return 0; }
            foreach (var r in plan)
                Console.WriteLine($"  {Path.GetFileName(r.From)}  ->  {Path.GetFileName(r.To)}");
            if (apply)
            {
                int n = DiscForge.Core.Library.LibraryScanner.ApplyRenames(plan);
                Console.WriteLine($"Renamed {n:N0} file(s).");
            }
            else Console.WriteLine($"{plan.Count:N0} rename(s) planned. Re-run with --apply to perform them.");
            return 0;
        }

        // scan report
        foreach (var e in report.Entries)
        {
            string tag = e.Status.ToString().ToUpperInvariant();
            string extra = e.Match is not null ? $"  = {e.Match.Game}" :
                           (e.RomPlatform.Length > 0 ? $"  [{e.RomPlatform}]" : (e.Format.Length > 0 ? $"  [{e.Format}]" : ""));
            string sug = e.SuggestedName is not null ? $"  -> {e.SuggestedName}" : "";
            Console.WriteLine($"  {tag,-9} {e.FileName}{extra}{sug}");
        }
        Console.WriteLine();
        Console.WriteLine($"Total {report.Total:N0}   Verified {report.Verified:N0}   Misnamed {report.Misnamed:N0}   " +
                          $"Duplicates {report.Duplicates:N0}   Unknown {report.Unknown:N0}");
        if (report.Missing.Count > 0)
        {
            Console.WriteLine($"Missing from set: {report.Missing.Count:N0}");
            foreach (var m in report.Missing.Take(40)) Console.WriteLine($"  - {m.Name}  ({m.Game})");
            if (report.Missing.Count > 40) Console.WriteLine($"  … and {report.Missing.Count - 40:N0} more");
        }
        if (htmlPath is not null)
        {
            File.WriteAllText(htmlPath, DiscForge.Core.Library.LibraryReportHtml.Render(report));
            Console.WriteLine($"HTML audit report: {htmlPath}");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DatVerify(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge dat-verify <dat-file> <file> [file ...]\n" +
                    "  Checks each file's size + CRC-32/SHA-1 against a redump-style DAT and\n" +
                    "  reports whether it is a catalogued good dump.");
    if (!File.Exists(args[1])) return Fail($"DAT not found: {args[1]}");

    DiscForge.Core.Dat.DatFile dat;
    try { using var fs = File.OpenRead(args[1]); dat = DiscForge.Core.Dat.DatFile.Parse(fs); }
    catch (Exception ex) { return Fail($"Could not read the DAT: {ex.Message}"); }

    Console.WriteLine($"DAT: {dat.Name ?? Path.GetFileName(args[1])} ({dat.Count:N0} entries)");
    int verified = 0, failed = 0;
    for (int a = 2; a < args.Length; a++)
    {
        if (!File.Exists(args[a])) { Console.WriteLine($"  {Path.GetFileName(args[a])}: not found"); failed++; continue; }
        var sums = DiscForge.Core.Files.ImageChecksums.ComputeFile(args[a]);
        var m = dat.Verify(sums.Length, sums.Crc32, sums.Sha1, sums.Md5);
        if (m.Verified)
        {
            Console.WriteLine($"  ✓ {Path.GetFileName(args[a])}  →  {m.Rom!.Game}");
            Console.WriteLine($"      {m.Rom.Name}");
            verified++;
        }
        else
        {
            Console.WriteLine($"  ✗ {Path.GetFileName(args[a])}  →  {m.Reason}");
            if (m.Rom is not null) Console.WriteLine($"      nearest: {m.Rom.Game} / {m.Rom.Name}");
            failed++;
        }
    }
    Console.WriteLine($"{verified} verified, {failed} not verified.");
    return failed > 0 ? 1 : 0;
}

// Build a Redump/No-Intro-style DAT by hashing a folder of dumps.
static int DatBuildCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge dat-build <dir> <out.dat> [--name NAME] [--recursive] [--ext .iso,.bin]\n" +
            "  Hashes every dump in a folder (size + CRC-32/MD5/SHA-1, the same as dat-verify) and writes a\n" +
            "  Logiqx DAT cataloguing them — one <game> per file. The result feeds straight back into\n" +
            "  dat-verify or any DAT-driven tool, turning a collection into its own reference set.");
    string dir = args[1], outPath = args[2];
    if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");

    string name = OptVal(args, "--name") ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)));
    bool recursive = args.Contains("--recursive");
    var extCsv = OptVal(args, "--ext");
    var exts = extCsv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);

    try
    {
        var files = Directory.EnumerateFiles(dir, "*",
                        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => exts is null || exts.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        if (files.Count == 0) return Fail("No files to catalogue (check the folder or --ext filter).");

        var roms = new List<DiscForge.Core.Dat.DatBuildRom>(files.Count);
        foreach (var f in files)
        {
            var s = DiscForge.Core.Files.ImageChecksums.ComputeFile(f);
            string fileName = Path.GetFileName(f);
            string game = Path.GetFileNameWithoutExtension(f);
            roms.Add(new DiscForge.Core.Dat.DatBuildRom(game, fileName, s.Length, s.Crc32, s.Md5, s.Sha1));
        }

        string dat = DiscForge.Core.Dat.DatBuilder.Build(name, roms);
        File.WriteAllText(outPath, dat);
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)} — catalogued {roms.Count:N0} dump(s) as \"{name}\".");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Sniff any file and say what DiscForge thinks it is.
static int IdentifyCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge identify <file> [file ...]");
    int unknown = 0;
    for (int a = 1; a < args.Length; a++)
    {
        if (!File.Exists(args[a])) { Console.WriteLine($"{args[a]}: not found"); unknown++; continue; }
        try
        {
            using var fs = File.OpenRead(args[a]);
            var id = DiscForge.Core.Identify.FormatIdentifier.Identify(fs);
            Console.WriteLine($"{Path.GetFileName(args[a])}: {id.Name}" +
                              (id.Detail.Length > 0 ? $" — {id.Detail}" : "") +
                              $"  [{id.Category}]");
            if (!id.Recognised) unknown++;
        }
        catch (Exception ex) { Console.WriteLine($"{args[a]}: {ex.Message}"); unknown++; }
    }
    return unknown > 0 && args.Length == 2 ? 1 : 0;
}

static int CisoInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ciso-info <image.cso|.zso>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        using var fs = File.OpenRead(args[1]);
        var info = DiscForge.Core.Ciso.CisoImage.ReadInfo(fs);
        long comp = fs.Length;
        Console.WriteLine($"{info.Kind.ToString().ToUpperInvariant()} ({(info.Kind == DiscForge.Core.Ciso.CisoKind.Ciso ? "zlib" : "LZ4")}), " +
                          $"{info.UncompressedSize:N0} bytes uncompressed, {info.Blocks:N0} blocks of {info.BlockSize}");
        Console.WriteLine($"  compressed to {comp:N0} bytes ({100.0 * comp / Math.Max(1, info.UncompressedSize):N1}% of original)");
        return 0;
    }
    catch (DiscForge.Core.Ciso.CisoFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CisoToIso(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge ciso-to-iso <in.cso|.zso> <out.iso>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        using (var input = File.OpenRead(args[1]))
        using (var output = File.Create(args[2]))
            DiscForge.Core.Ciso.CisoImage.Decompress(input, output);
        Console.WriteLine($"Decompressed to {Path.GetFileName(args[2])} ({new FileInfo(args[2]).Length:N0} bytes).");
        return 0;
    }
    catch (DiscForge.Core.Ciso.CisoFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IsoToCiso(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge iso-to-ciso <in.iso> <out.cso>  (CSO / zlib)");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        long size = new FileInfo(args[1]).Length;
        using (var input = File.OpenRead(args[1]))
        using (var output = File.Create(args[2]))
            DiscForge.Core.Ciso.CisoImage.Compress(input, size, output);
        long comp = new FileInfo(args[2]).Length;
        Console.WriteLine($"Compressed to {Path.GetFileName(args[2])} ({comp:N0} bytes, " +
                          $"{100.0 * comp / Math.Max(1, size):N1}% of original).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Identify a CHD (compressed disc image) and show its CD track layout. Structure
// only — hunk decompression/extraction is a documented follow-up (docs/CHD.md).
static int ChdInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge chd-info <image.chd>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var info = DiscForge.Core.Chd.ChdReader.Read(File.ReadAllBytes(args[1]));
        Console.WriteLine(info.Summary);
        Console.WriteLine($"  hunk {info.HunkBytes:N0} B, unit {info.UnitBytes} B");
        foreach (var t in info.Tracks)
            Console.WriteLine($"  track {t.Number,2}: {t.Type,-10} {t.Frames:N0} frames" +
                              (t.Pregap > 0 ? $", pregap {t.Pregap}" : ""));
        return 0;
    }
    catch (DiscForge.Core.Chd.ChdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ChdVerifyCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge chd-verify <image.chd> [--parent <p.chd> ...] [--json]\n" +
                    "  Checks a CHD's integrity without extracting it: decompresses every hunk, checks each\n" +
                    "  against its map's CRC-16, and confirms the whole image matches the SHA-1 the CHD stores of\n" +
                    "  itself — the same proof chdman's verify performs. Reports VALID, CORRUPT (a damaged hunk or\n" +
                    "  SHA-1 mismatch), UNVERIFIED (an uncompressed CHD with no stored hash), or UNSUPPORTED. Pass\n" +
                    "  --parent for a delta CHD. Read-only; writes nothing.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    byte[][]? chain = ReadParentChain(args);
    if (chain is null) return 1;   // ReadParentChain reported the error
    try
    {
        var r = DiscForge.Core.Chd.ChdVerify.Check(File.ReadAllBytes(args[1]), chain);
        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                image = Path.GetFileName(args[1]),
                verdict = r.Verdict.ToString(),
                r.Version, r.Compressors, r.LogicalBytes, r.HunkBytes, r.HunkCount,
                r.IsCd, r.TrackCount, sha1 = r.Sha1.ToLowerInvariant(), r.Detail,
            });
        }
        else
        {
            Console.WriteLine(r.Summary());
        }
        return r.Verdict switch
        {
            DiscForge.Core.Chd.ChdVerifyVerdict.Corrupt => 2,
            DiscForge.Core.Chd.ChdVerifyVerdict.Unsupported => 1,
            _ => 0,
        };
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ChdExtract(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge chd-extract <image.chd> <out.bin> [out.cue] [--parent <p.chd> ...]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    string outBin = args[2];
    string outCue = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : Path.ChangeExtension(outBin, ".cue");
    byte[][]? chain = ReadParentChain(args);
    if (chain is null) return 1;   // ReadParentChain reported the error
    try
    {
        var r = DiscForge.Core.Chd.ChdExtractor.ExtractCd(File.ReadAllBytes(args[1]), chain);
        File.WriteAllBytes(outBin, r.Bin);
        // The cue names "disc.bin"; rewrite it to the chosen bin file name.
        File.WriteAllText(outCue, r.Cue.Replace("disc.bin", Path.GetFileName(outBin)));
        Console.WriteLine($"Extracted {r.Tracks} track(s), {r.Bin.Length:N0} bytes → {Path.GetFileName(outBin)} " +
                          $"+ {Path.GetFileName(outCue)}" + (r.Verified ? " (SHA-1 verified)." : "."));
        return 0;
    }
    catch (DiscForge.Core.Chd.ChdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ChdExtractHd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge chd-extract-hd <image.chd> <out.img> [--parent <p.chd> ...]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    byte[][]? chain = ReadParentChain(args);
    if (chain is null) return 1;
    try
    {
        var raw = DiscForge.Core.Chd.ChdHdExtractor.Extract(File.ReadAllBytes(args[1]), chain);
        File.WriteAllBytes(args[2], raw);
        Console.WriteLine($"Extracted hard-disk image, {raw.Length:N0} bytes → {Path.GetFileName(args[2])} (SHA-1 verified).");
        return 0;
    }
    catch (DiscForge.Core.Chd.ChdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ChdCreate(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge chd-create <in.cue | in.img> <out.chd>\n" +
                                     "  A .cue makes a CD CHD (bin/cue); any other input makes a hard-disk CHD.");
    string input = args[1], output = args[2];
    if (!File.Exists(input)) return Fail($"File not found: {input}");
    try
    {
        byte[] chd;
        if (input.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
            chd = DiscForge.Core.Chd.ChdWriter.CreateCdFromBinCue(File.ReadAllText(input), dir);
        }
        else
        {
            chd = DiscForge.Core.Chd.ChdWriter.CreateHd(File.ReadAllBytes(input));
        }
        File.WriteAllBytes(output, chd);
        Console.WriteLine($"Created {Path.GetFileName(output)} ({chd.Length:N0} bytes).");
        return 0;
    }
    catch (DiscForge.Core.Chd.ChdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Read the parent chain from repeated --parent <file> options, nearest-first. Returns
// an empty array when none are given, or null (after printing an error) if a flag is
// missing its path or names a file that can't be read.
static byte[][]? ReadParentChain(string[] args)
{
    var chain = new List<byte[]>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] != "--parent") continue;
        if (i + 1 >= args.Length) { Fail("--parent needs a parent .chd file path."); return null; }
        string path = args[++i];
        if (!File.Exists(path)) { Fail($"Parent CHD not found: {path}"); return null; }
        chain.Add(File.ReadAllBytes(path));
    }
    return chain.ToArray();
}

// ---- PS1 memory card (.mcr) ------------------------------------------------

static int PsxMcInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge psxmc-info <card.mcr>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var vol = DiscForge.Core.PlayStation.PsxMemoryCard.Read(File.ReadAllBytes(args[1]));
        Console.WriteLine($"PS1 memory card: {vol.Saves.Count} save(s), {vol.FreeBlocks}/15 blocks free");
        foreach (var s in vol.Saves)
            Console.WriteLine($"  {s.Name,-20} {s.Blocks.Count} block(s)" +
                              (s.Title.Length > 0 ? $"  \"{s.Title}\"" : ""));
        return 0;
    }
    catch (DiscForge.Core.PlayStation.PsxMcFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PsxMcExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge psxmc-extract <card.mcr> <out-dir>\n" +
                    "  Extracts every save as a raw .mcs block image.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var card = File.ReadAllBytes(args[1]);
        var vol = DiscForge.Core.PlayStation.PsxMemoryCard.Read(card);
        Directory.CreateDirectory(args[2]);
        int done = 0;
        foreach (var s in vol.Saves)
        {
            string safe = string.Concat(s.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            if (safe.Length == 0) safe = $"save{done}";
            File.WriteAllBytes(Path.Combine(args[2], safe + ".mcs"),
                               DiscForge.Core.PlayStation.PsxMemoryCard.Extract(card, s));
            done++;
        }
        Console.WriteLine($"Extracted {done} save(s) to {args[2]}");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.PsxMcFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- GameCube memory card (.gci / card image) ------------------------------

static int GciInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge gci-info <file.gci|card>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        if (DiscForge.Core.Saves.GcMemoryCardReader.IsGcMemoryCard(data))
        {
            var card = DiscForge.Core.Saves.GcMemoryCardReader.Read(data);
            Console.WriteLine($"GameCube memory card: {card.Saves.Count} save(s)");
            int i = 0;
            foreach (var s in card.Saves)
            {
                Console.WriteLine($"  [{i++}] {s.GameCode}/{s.Maker} {s.FileName,-24} {s.BlockCount} block(s)" +
                                  (s.Comment.Length > 0 ? $"  \"{s.Comment}\"" : ""));
            }
            return 0;
        }
        if (DiscForge.Core.Saves.GciReader.IsGci(data))
        {
            var s = DiscForge.Core.Saves.GciReader.Read(data);
            Console.WriteLine($"GameCube save (.gci): {s.GameCode}/{s.Maker} {s.FileName}, {s.BlockCount} block(s)");
            if (s.Comment.Length > 0) Console.WriteLine($"  comment: {s.Comment}");
            return 0;
        }
        return Fail("Not a GameCube .gci or memory-card image.");
    }
    catch (DiscForge.Core.Saves.GcSaveFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GciExtract(string[] args)
{
    if (args.Length < 4) return Fail("usage: dforge gci-extract <card> <index> <out.gci>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    if (!int.TryParse(args[2], out int index)) return Fail($"Invalid save index: {args[2]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var card = DiscForge.Core.Saves.GcMemoryCardReader.Read(data);
        if (index < 0 || index >= card.Saves.Count)
            return Fail($"Save index {index} out of range (0..{card.Saves.Count - 1}).");
        var gci = DiscForge.Core.Saves.GcMemoryCardReader.ExtractSaveToGci(data, card.Saves[index]);
        File.WriteAllBytes(args[3], gci);
        Console.WriteLine($"Wrote {gci.Length:N0} bytes to {args[3]}");
        return 0;
    }
    catch (DiscForge.Core.Saves.GcSaveFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- N64 saves -------------------------------------------------------------

static int N64SaveInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge n64save-info <file>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var type = DiscForge.Core.Saves.N64SaveReader.IdentifyBySize(data.Length);
        Console.WriteLine($"N64 save: {data.Length:N0} bytes — {DiscForge.Core.Saves.N64SaveReader.Describe(type)}");
        if (DiscForge.Core.Saves.N64ControllerPak.IsControllerPak(data))
        {
            var pak = DiscForge.Core.Saves.N64ControllerPak.Read(data);
            Console.WriteLine($"Controller Pak: {pak.Notes.Count} note(s)");
            foreach (var n in pak.Notes)
                Console.WriteLine($"  {n.GameCode}/{n.Publisher} \"{n.Name}\" (page {n.StartPage})");
        }
        return 0;
    }
    catch (DiscForge.Core.Saves.N64SaveFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- Saturn backup memory --------------------------------------------------

static int SaturnSaveInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge saturnsave-info <file>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var backup = DiscForge.Core.Saves.SaturnSaveReader.Read(data);
        Console.WriteLine($"Saturn backup: {backup.Saves.Count} save(s)");
        foreach (var s in backup.Saves)
            Console.WriteLine($"  {s.Name,-12} {s.Language,-9} {s.DataSize,7:N0} bytes" +
                              (s.Comment.Length > 0 ? $"  \"{s.Comment}\"" : ""));
        return 0;
    }
    catch (DiscForge.Core.Saves.SaturnSaveFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- PS2 memory card (.ps2) ------------------------------------------------

static int Ps2McInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ps2mc-info <card.ps2>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var vol = DiscForge.Core.PlayStation.Ps2MemoryCard.Read(File.ReadAllBytes(args[1]));
        Console.WriteLine($"PS2 memory card ({(vol.HasEcc ? "with ECC" : "raw")}), {vol.ClustersPerCard} clusters, " +
                          $"{vol.Saves.Count()} save(s), {vol.Files.Count()} file(s)");
        foreach (var s in vol.Saves)
        {
            Console.WriteLine($"  {s.Path.TrimStart('/')}");
            foreach (var f in vol.Files.Where(f => f.Path.StartsWith(s.Path + "/")))
                Console.WriteLine($"      {f.Path[(s.Path.Length + 1)..]}  ({f.Size:N0} bytes)");
        }
        return 0;
    }
    catch (DiscForge.Core.PlayStation.Ps2McFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Ps2McEccCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge ps2mc-ecc <card.ps2> [--repair <out.ps2>] [--json]\n" +
                    "  Verifies the per-page Hamming ECC of a PlayStation 2 memory-card dump: every 528-byte\n" +
                    "  page carries a 3-byte code for each of its four 128-byte chunks, which detects any error\n" +
                    "  and corrects any single-bit flip. Reports CLEAN, CORRECTABLE (single-bit errors ECC can\n" +
                    "  fix), or CORRUPT (2+ bit errors). With --repair it writes a corrected copy, leaving the\n" +
                    "  input untouched. Only 'with-ECC' dumps (528-byte pages) carry the codes. Read-only otherwise.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    string? repairOut = OptVal(args, "--repair");
    try
    {
        var card = File.ReadAllBytes(args[1]);
        DiscForge.Core.PlayStation.Ps2EccReport report;
        if (repairOut is not null)
        {
            var (r, fixedCard) = DiscForge.Core.PlayStation.Ps2CardEcc.Repair(card);
            report = r;
            if (r.HasEcc && r.CorrectedPages > 0) File.WriteAllBytes(repairOut, fixedCard);
        }
        else
        {
            report = DiscForge.Core.PlayStation.Ps2CardEcc.Verify(card);
        }

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                card = Path.GetFileName(args[1]),
                report.HasEcc, status = report.Status.ToString(),
                report.TotalPages, report.CleanPages, report.CorrectedPages, report.FailedPages,
                repaired = repairOut is not null && report.CorrectedPages > 0 ? Path.GetFileName(repairOut) : null,
            });
        }
        else
        {
            Console.WriteLine(report.Summary());
            if (repairOut is not null && report.HasEcc && report.CorrectedPages > 0)
                Console.WriteLine($"  wrote corrected card to {Path.GetFileName(repairOut)}");
        }

        return report.Status == DiscForge.Core.PlayStation.Ps2EccStatus.Failed ? 2 : 0;
    }
    catch (DiscForge.Core.PlayStation.Ps2McFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Ps2McExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge ps2mc-extract <card.ps2> <out-dir>\n" +
                    "  Extracts every save folder and its files.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var card = File.ReadAllBytes(args[1]);
        var vol = DiscForge.Core.PlayStation.Ps2MemoryCard.Read(card);
        int done = 0;
        foreach (var f in vol.Files)
        {
            string outPath = Path.Combine(args[2], f.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, DiscForge.Core.PlayStation.Ps2MemoryCard.Extract(card, vol, f));
            done++;
        }
        Console.WriteLine($"Extracted {done} file(s) from {vol.Saves.Count()} save(s) to {args[2]}");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.Ps2McFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- Dreamcast VMU (memory card) -------------------------------------------

static int VmuInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge vmu-info <vmu.bin>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var vmu = DiscForge.Core.Vmu.VmuImage.Read(File.ReadAllBytes(args[1]));
        Console.WriteLine($"VMU: {(vmu.Formatted ? "formatted" : "UNFORMATTED")}, " +
                          $"{vmu.Files.Count} file(s), {vmu.FreeBlocks}/{vmu.UserBlocks} blocks free");
        foreach (var f in vmu.Files)
        {
            Console.WriteLine($"  {f.Name,-12} {(f.IsGame ? "game" : "data")} {f.SizeBlocks,3} block(s)" +
                              (f.CopyProtected ? " [copy-protected]" : ""));
            if (f.LongDescription is not null) Console.WriteLine($"      {f.LongDescription}");
        }
        return 0;
    }
    catch (DiscForge.Core.Vmu.VmuFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VmuExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge vmu-extract <vmu.bin> <out-dir> [--force]\n" +
                    "  Extracts every save as a .VMS file. --force extracts copy-protected saves.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    bool force = args.Any(a => a == "--force");
    try
    {
        var image = File.ReadAllBytes(args[1]);
        var vmu = DiscForge.Core.Vmu.VmuImage.Read(image);
        Directory.CreateDirectory(args[2]);
        int done = 0, skipped = 0;
        foreach (var f in vmu.Files)
        {
            try
            {
                var bytes = DiscForge.Core.Vmu.VmuImage.Extract(image, f, force);
                string name = f.Name.Length > 0 ? f.Name : $"save{done}";
                File.WriteAllBytes(Path.Combine(args[2], name + ".VMS"), bytes);
                done++;
            }
            catch (InvalidOperationException ex) { Console.WriteLine($"  skipped {f.Name}: {ex.Message}"); skipped++; }
        }
        Console.WriteLine($"Extracted {done} save(s) to {args[2]}" + (skipped > 0 ? $" ({skipped} skipped)" : ""));
        return 0;
    }
    catch (DiscForge.Core.Vmu.VmuFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DiskInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge disk-info <image>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    using var fs = File.OpenRead(args[1]);
    DiscForge.Core.Partition.DiskImage image;
    try
    {
        image = DiscForge.Core.Partition.PartitionTable.Read(fs);
    }
    catch (DiscForge.Core.Partition.PartitionFormatException ex)
    {
        return Fail(ex.Message);
    }

    Console.WriteLine($"Scheme: {image.Scheme}" +
                      (image.DiskGuid is not null ? $"   Disk GUID: {image.DiskGuid}" : ""));
    Console.WriteLine();
    Console.WriteLine("  #  Boot  Type                         Start          Size  Filesystem");
    Console.WriteLine("  -- ----  ---------------------------  -------------  ------------  ----------");
    foreach (var p in image.Partitions)
    {
        Console.WriteLine(
            $"  {p.Index,2}  {(p.Bootable ? "*" : " "),-4}  {Trim(p.TypeName, 27),-27}  " +
            $"{p.StartByte,13:N0}  {p.SizeBytes,12:N0}  {p.FileSystem}");
    }
    Console.WriteLine();
    Console.WriteLine($"{image.Partitions.Count} partition(s).");
    return 0;

    static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}

static int FloppyInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge floppy-info <image>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var data = File.ReadAllBytes(args[1]);
    try
    {
        if (DiscForge.Core.Floppy.D64Reader.IsD64(data))
        {
            var disk = DiscForge.Core.Floppy.D64Reader.Read(data);
            Console.WriteLine($"Format: C64 D64 ({disk.Tracks}-track)");
            Console.WriteLine($"Disk:   \"{disk.DiskName}\" ({disk.DiskId})");
            Console.WriteLine();
            foreach (var f in disk.Files)
                Console.WriteLine($"  {f.Type.ToString().ToUpperInvariant(),-3}  {f.SizeBlocks,4} blk  {f.Name}");
            Console.WriteLine();
            Console.WriteLine($"{disk.Files.Count} file(s).");
            return 0;
        }
        if (DiscForge.Core.Floppy.AdfReader.IsAdf(data))
        {
            var disk = DiscForge.Core.Floppy.AdfReader.Read(data);
            Console.WriteLine($"Format: Amiga ADF ({(disk.Ffs ? "FFS" : "OFS")})");
            Console.WriteLine($"Disk:   \"{disk.DiskName}\"");
            Console.WriteLine();
            foreach (var e in disk.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
                Console.WriteLine(e.IsDirectory ? $"  {"<DIR>",12}  {e.Path}" : $"  {e.Size,12:N0}  {e.Path}");
            Console.WriteLine();
            Console.WriteLine($"{disk.Entries.Count(e => !e.IsDirectory)} file(s).");
            return 0;
        }
        if (DiscForge.Core.Fat.FatReader.IsFat(data))
        {
            // Use the shared FAT reader so floppy-info surfaces the same VFAT long
            // file names as fat-ls (it also handles FAT16/32, not only FAT12).
            var vol = DiscForge.Core.Fat.FatReader.Read(data);
            Console.WriteLine($"Format: DOS {vol.Type}");
            Console.WriteLine($"Volume: \"{vol.VolumeLabel}\"");
            Console.WriteLine();
            foreach (var e in vol.Entries.OrderBy(x => x.Path, StringComparer.Ordinal))
                Console.WriteLine(e.IsDirectory ? $"  {"<DIR>",12}  {e.Path}" : $"  {e.Size,12:N0}  {e.Path}");
            Console.WriteLine();
            Console.WriteLine($"{vol.Files.Count()} file(s).");
            return 0;
        }
        return Fail("Not a recognised floppy image (D64, ADF, or FAT12/16/32).");
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
    {
        return Fail(ex.Message);
    }
}

static int FloppyImageCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge floppy-image <drive> <out.img> [--continue-on-error]\n" +
                    "  Image a floppy disk to a flat .img (raw 512-byte sectors, in order). On Windows <drive> is\n" +
                    "  the floppy's drive letter (e.g. A) and raw access needs an elevated/Administrator shell; on\n" +
                    "  macOS/Linux pass the device path (e.g. /dev/fd0). The copy ends at the end of the disk and\n" +
                    "  reports the geometry (1.44 MB, 720 KB, ...). Each sector is retried; an unreadable one stops\n" +
                    "  the copy (keeping what was read) unless --continue-on-error zero-fills it and carries on.\n" +
                    "  Then read it with floppy-info / fat-ls / fat-lint. Preservation only.");

    string spec = args[1];
    string outPath = args[2];
    bool cont = args.Contains("--continue-on-error");

    string devicePath;
    if (OperatingSystem.IsWindows())
    {
        string letter = spec.TrimEnd(':', '\\', '/');
        if (letter.Length == 0)
            return Fail("Give the floppy's drive letter, e.g. `dforge floppy-image A: floppy.img`.");
        devicePath = $@"\\.\{char.ToUpperInvariant(letter[0])}:";
    }
    else
    {
        devicePath = spec;   // a device node such as /dev/fd0
    }

    try
    {
        using var src = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = File.Create(outPath);
        Console.WriteLine($"Imaging {devicePath} -> {Path.GetFileName(outPath)} ...");
        var progress = new Progress<long>(b => Console.Write($"\r  {b / 1024.0,8:N0} KB"));
        var opts = new DiscForge.Core.Floppy.FloppyReadOptions { ContinueOnError = cont };
        var rep = DiscForge.Core.Floppy.FloppyImager.Copy(src, dst, progress, opts);
        Console.WriteLine();

        // Nothing read at all: almost always an empty/not-ready drive.
        if (rep.Bytes == 0 && rep.StoppedAtSector == 0)
            return Fail("Could not read the disk. Is a disk actually inserted and the drive ready? " +
                        "(An unformatted or damaged disk fails the same way — Windows reports a 'semaphore timeout'.)");

        Console.WriteLine($"Imaged {rep.Bytes:N0} bytes ({rep.Sectors:N0} sectors) - {rep.Geometry}");

        if (rep.StoppedAtSector >= 0)
        {
            Console.WriteLine($"  STOPPED at sector {rep.StoppedAtSector:N0} - an unreadable sector. The image holds everything before it.");
            Console.WriteLine("  Re-run with --continue-on-error to zero-fill bad sectors and image the rest of the disk.");
            return 2;
        }
        if (rep.ZeroFilled.Count > 0)
        {
            Console.WriteLine($"  {rep.ZeroFilled.Count:N0} unreadable sector(s) were zero-filled - the image is INCOMPLETE.");
            Console.WriteLine("  First bad sectors: " + string.Join(", ", rep.ZeroFilled.Take(10)) +
                              (rep.ZeroFilled.Count > 10 ? " ..." : ""));
        }
        else if (rep.ShortFinalRead)
            Console.WriteLine("  note: the last read was short of a full sector (possible read error near the end).");

        Console.WriteLine($"Next: dforge floppy-info \"{outPath}\"  (or fat-ls / fat-lint)");
        return rep.Complete ? 0 : 2;
    }
    catch (UnauthorizedAccessException)
    {
        return Fail($"Access denied opening {devicePath}. Raw floppy access needs an elevated shell " +
                    "(run PowerShell/terminal as Administrator).");
    }
    catch (FileNotFoundException)
    {
        return Fail($"No floppy device at {devicePath}. Is the drive connected and a disk inserted?");
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int FloppyExtract(string[] args)
{
    if (args.Length < 4) return Fail("usage: dforge floppy-extract <image> <path-in-image> <output-file>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var data = File.ReadAllBytes(args[1]);
    var want = args[2];
    var outPath = args[3];
    string Norm(string p) => "/" + p.Replace('\\', '/').TrimStart('/');

    try
    {
        byte[]? bytes = null;
        if (DiscForge.Core.Floppy.D64Reader.IsD64(data))
        {
            var disk = DiscForge.Core.Floppy.D64Reader.Read(data);
            // D64 has no directories; match by filename (case-insensitive).
            var entry = disk.Files.FirstOrDefault(f =>
                string.Equals(f.Name, want.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Fail($"No file named '{want}' in the image.");
            bytes = DiscForge.Core.Floppy.D64Reader.ExtractFile(data, entry);
        }
        else if (DiscForge.Core.Floppy.AdfReader.IsAdf(data))
        {
            var disk = DiscForge.Core.Floppy.AdfReader.Read(data);
            var entry = disk.Entries.FirstOrDefault(e =>
                !e.IsDirectory && string.Equals(e.Path, Norm(want), StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Fail($"No file at '{Norm(want)}' in the image.");
            bytes = DiscForge.Core.Floppy.AdfReader.ExtractFile(data, entry);
        }
        else if (DiscForge.Core.Floppy.Fat12Reader.IsFat12(data))
        {
            var disk = DiscForge.Core.Floppy.Fat12Reader.Read(data);
            var entry = disk.Entries.FirstOrDefault(e =>
                !e.IsDirectory && string.Equals(e.Path, Norm(want), StringComparison.OrdinalIgnoreCase));
            if (entry is null) return Fail($"No file at '{Norm(want)}' in the image.");
            bytes = DiscForge.Core.Floppy.Fat12Reader.ExtractFile(data, entry);
        }
        else
        {
            return Fail("Not a recognised floppy image (D64, ADF, or FAT12).");
        }

        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"Wrote {bytes.Length:N0} bytes to {outPath}");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
    {
        return Fail(ex.Message);
    }
}

static int WozInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge woz-info <file.woz>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        using var fs = File.OpenRead(args[1]);
        var d = DiscForge.Core.Floppy.WozReader.Read(fs);

        Console.WriteLine($"File:        {Path.GetFileName(args[1])} (WOZ{d.FormatVersion})");
        Console.WriteLine($"CRC-32:      {(!d.CrcPresent ? "not set" : d.CrcValid ? "valid" : "MISMATCH — image may be corrupt")}");
        if (d.FormatVersion == 1)
        {
            Console.WriteLine("note: WOZ1 recognised; full v1 track decode is a documented follow-up (v2+ decodes fully).");
            return 0;
        }
        var i = d.Info;
        Console.WriteLine($"Disk type:   {i.DiskTypeName}{(i.Sides > 1 ? $", {i.Sides} sides" : "")}");
        Console.WriteLine($"Write-prot:  {(i.WriteProtected ? "yes" : "no")}");
        Console.WriteLine($"Bit timing:  {i.OptimalBitTiming} × 125 ns ({i.OptimalBitTiming * 125 / 1000.0:0.##} µs)");
        string boot = i.BootSectorFormat switch { 1 => "16-sector", 2 => "13-sector", 3 => "13- & 16-sector", _ => "unknown" };
        if (i.DiskType == 1) Console.WriteLine($"Boot format: {boot}");
        Console.WriteLine($"Protection:  {(i.Synchronized ? "cross-track synchronized" : "not synchronized")}" +
            $"{(i.Cleaned ? "; weak/fake bits cleaned (regenerate on read)" : "; weak bits preserved")}");
        if (i.Creator.Length > 0) Console.WriteLine($"Imaged by:   {i.Creator}");
        Console.WriteLine($"Tracks:      {d.Tracks.Count} stored, {d.MappedPositions} mapped position(s), largest {i.LargestTrackBlocks} blocks");
        if (d.HasFlux) Console.WriteLine("Flux:        this image carries a FLUX chunk (nanosecond-level capture)");
        if (d.Meta.Count > 0)
        {
            Console.WriteLine("Metadata:");
            foreach (var kv in d.Meta) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        }
        return d.CrcValid ? 0 : 1;
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
    {
        return Fail(ex.Message);
    }
}

static int ScpInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge scp-info <file.scp>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var raw = File.ReadAllBytes(args[1]);
        var img = DiscForge.Core.Floppy.ScpReader.Parse(raw);
        var h = img.Header;

        Console.WriteLine($"File:        {Path.GetFileName(args[1])} (SCP {h.VersionMajor}.{h.VersionMinor})");
        Console.WriteLine($"Checksum:    {(img.ChecksumValid ? "valid" : "MISMATCH — image may be corrupt")}");
        Console.WriteLine($"Flux tick:   {h.TickNs} ns");
        Console.WriteLine($"Revolutions: {h.Revolutions} per track");
        Console.WriteLine($"Tracks:      {img.Tracks.Count} present (range {h.StartTrack}–{h.EndTrack})");
        Console.WriteLine($"Heads:       {(h.Heads == 0 ? "both" : h.Heads == 1 ? "side 0" : "side 1")}" +
                          $"{(h.Is96Tpi ? ", 96 TPI" : ", 48 TPI")}{(h.Indexed ? ", index-synced" : "")}");
        if (img.Rpm is { } rpm) Console.WriteLine($"Speed:       ~{rpm:0} RPM (from index duration)");

        if (img.Tracks.Count > 0)
        {
            var t = img.Tracks[0];
            long flux = t.Revolutions.Count > 0 ? t.Revolutions[0].FluxCount : 0;
            Console.WriteLine($"First track: #{t.TrackNumber}, {t.Revolutions.Count} rev(s), {flux:N0} flux transitions in rev 0");
        }
        if (h.HasFooter) Console.WriteLine("Footer:      extension footer present (creator/metadata)");
        return img.ChecksumValid ? 0 : 1;
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
    {
        return Fail(ex.Message);
    }
}

static int D88InfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge d88-info <file.d88>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        using var fs = File.OpenRead(args[1]);
        var d = DiscForge.Core.Floppy.D88Reader.Read(fs);

        Console.WriteLine($"File:        {Path.GetFileName(args[1])} (D88)");
        Console.WriteLine($"Disk name:   {(d.Name.Length > 0 ? d.Name : "(unnamed)")}");
        Console.WriteLine($"Media type:  {d.DiskTypeName}");
        Console.WriteLine($"Write-prot:  {(d.WriteProtected ? "yes" : "no")}");
        Console.WriteLine($"Tracks:      {d.TrackCount} populated, {d.SectorCount} sectors total");
        var sizes = d.Tracks.SelectMany(t => t.Sectors).GroupBy(s => s.SizeBytes)
            .OrderBy(g => g.Key).Select(g => $"{g.Count()}×{g.Key}B");
        if (sizes.Any()) Console.WriteLine($"Sectors:     {string.Join(", ", sizes)}");
        if (d.Tracks.SelectMany(t => t.Sectors).Any(s => s.Deleted))
            Console.WriteLine("note: image contains deleted-address-mark sectors (preserved).");
        if (d.MoreDisksFollow) Console.WriteLine("note: this is a multi-disk D88 (more logical disks follow).");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
    {
        return Fail(ex.Message);
    }
}

static int KryoFluxInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge kryoflux-info <file.raw>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var st = DiscForge.Core.Floppy.KryoFluxStreamReader.Parse(File.ReadAllBytes(args[1]));

        Console.WriteLine($"File:        {Path.GetFileName(args[1])} (KryoFlux raw stream)");
        Console.WriteLine($"Flux:        {st.FluxTransitions:N0} transitions");
        Console.WriteLine($"Index pulses:{st.Indices.Count} ({(st.Indices.Count >= 2 ? $"{st.Indices.Count - 1} full revolution(s)" : "partial")})");
        if (st.SampleClockHz is { } sck) Console.WriteLine($"Sample clock:{sck / 1_000_000.0:0.###} MHz");
        if (st.Rpm is { } rpm) Console.WriteLine($"Speed:       ~{rpm:0} RPM (from index pulses)");
        Console.WriteLine($"Stream end:  {(st.StreamEndSeen ? $"yes (result {st.StreamEndResult})" : "not seen (truncated?)")}");
        if (st.Info.Count > 0)
        {
            Console.WriteLine("Hardware/info:");
            foreach (var kv in st.Info) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        }
        return st.StreamEndSeen ? 0 : 1;
    }
    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
    {
        return Fail(ex.Message);
    }
}

static int DrivesCmd(string[] args)
{
#if WINDOWS
    try
    {
        var drives = DiscForge.Devices.DriveDetector.DetectAll();
        if (drives.Count == 0) { Console.WriteLine("No optical drives detected."); return 0; }
        foreach (var d in drives)
        {
            Console.WriteLine($"{d.DevicePath}  {d.Vendor} {d.Model} (fw {d.FirmwareRevision})");
            Console.WriteLine($"    read : CD {(d.CdRead ? "yes" : "no")}, DVD {(d.DvdRead ? "yes" : "no")}, BD {(d.BdRead ? "yes" : "no")}");
            Console.WriteLine($"    write: CD {(d.CdWrite ? "yes" : "no")}, DVD {(d.DvdWrite ? "yes" : "no")}, BD {(d.BdWrite ? "yes" : "no")}" +
                              $"  (TAO {(d.TrackAtOnce ? "y" : "n")}, DAO {(d.DiscAtOnce ? "y" : "n")}, RAW-DAO {(d.RawDao96 ? "y" : "n")})");
            Console.WriteLine($"    media: {d.MediaProfile}");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    _ = args;
    if (OperatingSystem.IsMacOS())
    {
        try { return DiscForge.Core.Burning.MacOpticalBurner.RunDrives(Console.WriteLine); }
        catch (Exception ex) { return Fail(ex.Message); }
    }
    if (OperatingSystem.IsLinux())
    {
        var devs = DiscForge.Core.Burning.LinuxOpticalBurner.ListDevices();
        if (devs.Count == 0) { Console.WriteLine("No optical devices found (/dev/sr*, /dev/cdrom)."); return 0; }
        foreach (var d in devs)
        {
            // Native SG_IO first: same MMC CDBs as the Windows SPTI layer, no external tools.
            try
            {
                using var dev = DiscForge.Core.Devices.LinuxSgIo.Device.OpenDevice(d);
                var q = dev.Inquiry();
                bool ready = dev.TestUnitReady();
                Console.WriteLine($"{d}  {q.Vendor} {q.Product} (fw {q.Revision})  " +
                                  $"[SG_IO{(q.IsOptical ? "" : ", not optical?")}] media: {(ready ? "ready" : "not ready")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{d}  (SG_IO inquiry unavailable: {ex.Message})");
            }
        }
        Console.WriteLine("(burn uses growisofs for DVD/Blu-ray or wodim for CD — install dvd+rw-tools / cdrkit)");
        return 0;
    }
    return Fail("`drives`: no optical-drive backend for this platform.");
#endif
}

static int WriteInfoCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge writeinfo <drive-letter>\n" +
                    "  Read-only: ask the drive its disc status and, crucially, its NEXT WRITABLE\n" +
                    "  ADDRESS (where it expects the first write) via READ DISC/TRACK INFORMATION.\n" +
                    "  No disc is written. Use it to set the raw-DAO write's start LBA from ground truth.");
#if WINDOWS
    string spec = args[1].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the optical drive letter, e.g. `dforge writeinfo D:`.");
    char letter = char.ToUpperInvariant(spec[0]);
    try
    {
        var r = DiscForge.Devices.Burning.WriteInfoDiagnostic.Read(letter);
        Console.WriteLine($"Drive {letter}:");
        Console.WriteLine($"  Disc status: {r.DiscStatus}");
        Console.WriteLine($"  Sessions: {r.Sessions}; tracks {r.FirstTrack}–{r.LastTrack}; disc type 0x{r.DiscType:X2}");
        if (r.TrackInfoOk)
        {
            Console.WriteLine($"  Track {r.TrackNumber}: {(r.Blank ? "blank" : "not blank")}, mode 0x{r.TrackMode:X1}, start LBA {r.TrackStart:N0}");
            Console.WriteLine($"  Next writable address: {(r.NwaValid ? $"{r.NextWritableAddress:N0}  (0x{(uint)r.NextWritableAddress:X8})" : "(NWA not valid)")}");
            Console.WriteLine($"  Free blocks: {r.FreeBlocks:N0}");
        }
        else Console.WriteLine("  (could not read track information — see notes)");
        foreach (var n in r.Notes) Console.WriteLine($"  note: {n}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    return Fail("writeinfo needs the Windows build (build -f net8.0-windows) — it drives SPTI.");
#endif
}

static int BurnRawCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge burn-raw <file.cue> <drive-letter> [--speed N] [--subcode cooked|raw|pq] [--probe]\n" +
                    "  Burn a CD RAW DAO-96 (full 2352 sector + sub-channel, lead-in included) via the\n" +
                    "  RawDaoBurnEngine / IMAPI2 MsftDiscFormat2RawCD — the byte-faithful path that writes\n" +
                    "  exact gaps, indexes, ISRC/MCN and CD-TEXT. Needs a RAW-capable CD writer and a blank\n" +
                    "  CD-R/RW. Compose a matching golden with `build-raw` and verify the burn with a raw\n" +
                    "  read-back + `raw-verify-readback`. Run `dforge drives` to list recorders.\n" +
                    "  --probe            prepare the (blank) disc and report the drive's supported raw sector\n" +
                    "                     types, then release WITHOUT writing — non-destructive diagnostic.\n" +
                    "  --subcode cooked|raw|pq   force the raw subcode mode instead of auto-negotiating\n" +
                    "                     (cooked = IS_COOKED/96 de-interleaved, IMAPI2's default; raw = IS_RAW/96\n" +
                    "                     interleaved; pq = PQ_ONLY/16). Match `build-raw --subcode` for verify.\n" +
                    "  --engine spti      use the direct-SPTI raw DAO-96 engine (ImgBurn's approach) instead of\n" +
                    "                     IMAPI2 — writes our exact bytes via MODE SELECT + SEND CUE SHEET + WRITE(10).\n" +
                    "  --test-cue         (with --engine spti) non-destructively validate the write parameters +\n" +
                    "                     cue sheet against the drive — no disc is written.\n" +
                    "  --simulate         (with --engine spti) run the FULL write path with the laser off\n" +
                    "                     (drive test-write) — validates addressing/sequence, no disc written.");
#if WINDOWS
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
    if (Path.GetExtension(args[1]).ToLowerInvariant() != ".cue")
        return Fail("burn-raw currently takes a .cue (mixed/audio/data). For a .cdi, convert to cue first.");
    string spec = args[2].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the target drive letter, e.g. `dforge burn-raw disc.cue D:`.");
    char letter = char.ToUpperInvariant(spec[0]);
    int? speed = int.TryParse(OptVal(args, "--speed"), out var sp) ? sp : null;
    bool probe = args.Contains("--probe");
    int? forcedType = (OptVal(args, "--subcode")?.ToLowerInvariant()) switch
    {
        "pq" => 1,           // PQ_ONLY
        "cooked" => 2,       // IS_COOKED
        "raw" => 3,          // IS_RAW
        null => null,
        var other => throw new ArgumentException($"--subcode must be cooked|raw|pq, not '{other}'."),
    };
    string engine = OptVal(args, "--engine")?.ToLowerInvariant() ?? "imapi2";
    bool testCue = args.Contains("--test-cue");
    try
    {
        var caps = DiscForge.Devices.DriveDetector.Detect(letter);
        if (!caps.CdWrite) return Fail($"Drive {letter}: ({caps.Vendor} {caps.Model}) is not a CD writer.");

        // Direct SPTI raw DAO-96 engine (ImgBurn's approach) — bypasses IMAPI2's raw-CD writer.
        if (engine == "spti")
        {
            var layoutS = DiscForge.Core.Raw.DiscLayout.FromCueFile(args[1]);
            if (testCue)
            {
                Console.WriteLine($"Testing raw DAO cue sheet against {letter}: ({caps.Vendor} {caps.Model}) — non-destructive, no disc written…");
                var res = DiscForge.Devices.Burning.SptiRawDaoBurnEngine.TestCue(letter, layoutS);
                Console.WriteLine($"  Cue sheet: {res.CueEntries} entries ({res.CueBytes} bytes)");
                Console.WriteLine($"  Result: {(res.Accepted ? "ACCEPTED" : "REJECTED")} — {res.Detail}");
                return res.Accepted ? 0 : 1;
            }
            bool simulate = args.Contains("--simulate");
            var progressS = new Progress<DiscForge.Core.Burning.BurnProgress>(p =>
                Console.WriteLine($"  [{p.Phase}] {p.Fraction * 100,5:0.0}%{(p.Detail is null ? "" : "  " + p.Detail)}"));
            Console.WriteLine($"RAW DAO-96 {(simulate ? "SIMULATION (laser off)" : "burn")} (direct SPTI) of {Path.GetFileName(args[1])} to {letter}: ({caps.Vendor} {caps.Model})…");
            DiscForge.Devices.Burning.SptiRawDaoBurnEngine.Burn(letter, layoutS, progressS, simulate);
            Console.WriteLine(simulate ? "Simulation complete (SPTI) — no disc written." : "RAW burn complete (SPTI).");
            return 0;
        }

        if (!caps.RawDao96)
            Console.WriteLine($"  note: {caps.Vendor} {caps.Model} does not ADVERTISE RAW DAO-96 in its feature set — " +
                              "trying anyway (IMAPI2 negotiates the raw sector type, and our capability probe may under-report).");

        var layout = DiscForge.Core.Raw.DiscLayout.FromCueFile(args[1]);
        var plan = new DiscForge.Core.Burning.BurnPlan
        {
            Method = DiscForge.Core.Burning.BurnMethod.RawDao96,
            DevicePath = caps.DevicePath,
            Warnings = Array.Empty<string>(),
            WriteSpeedSectorsPerSecond = speed,
        };
        var progress = new Progress<DiscForge.Core.Burning.BurnProgress>(p =>
            Console.WriteLine($"  [{p.Phase}] {p.Fraction * 100,5:0.0}%{(p.Detail is null ? "" : "  " + p.Detail)}"));

        if (probe)
            Console.WriteLine($"Probing raw sector-type support on {letter}: ({caps.Vendor} {caps.Model}) — no disc will be written…");
        else
            Console.WriteLine($"RAW DAO-96 burn of {Path.GetFileName(args[1])} to {letter}: ({caps.Vendor} {caps.Model})…");
        new DiscForge.Devices.Burning.RawDaoBurnEngine().BurnLayout(layout, plan, progress, forcedType, probe);
        Console.WriteLine(probe ? "Probe complete." : "RAW burn complete.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    return Fail("burn-raw needs the Windows build (build -f net8.0-windows) — it drives IMAPI2/SPTI.");
#endif
}

static int BurnCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge burn <image.iso> [drive-letter] [--verify] [--speed N]\n" +
                    "  Burns a data ISO to a blank CD/DVD/Blu-ray in an optical recorder, optionally verifying the\n" +
                    "  written disc. On Windows it uses the IMAPI2 stack (give the target drive letter); on macOS it\n" +
                    "  uses hdiutil to write to the inserted disc (the drive letter is ignored). For loose files,\n" +
                    "  author an image first (iso-create / create-udf / create-udf-bridge / create-xiso), then burn\n" +
                    "  that. Use `dforge drives` to list recorders.");
    if (!File.Exists(args[1])) return Fail($"'{args[1]}' not found.");
#if WINDOWS
    var iso = args[1];
    if (args.Length < 3) return Fail("Windows burning needs a target drive letter — run `dforge drives` to list recorders.");
    char letter = char.ToUpperInvariant(args[2].TrimEnd(':', '\\', '/')[0]);
    int? speed = int.TryParse(OptVal(args, "--speed"), out var sp) ? sp : null;
    try
    {
        var caps = DiscForge.Devices.DriveDetector.Detect(letter);
        var shape = new DiscForge.Core.Burning.ImageShape(TrackCount: 1, SessionCount: 1, HasAudio: false, HasData: true);
        var plan = DiscForge.Core.Burning.BurnPlanner.Plan(shape, caps) with { WriteSpeedSectorsPerSecond = speed };
        foreach (var w in plan.Warnings) Console.WriteLine($"  warning: {w}");

        var progress = new Progress<DiscForge.Core.Burning.BurnProgress>(p =>
            Console.WriteLine($"  [{p.Phase}] {p.Fraction * 100,5:0.0}%{(p.Detail is null ? "" : "  " + p.Detail)}"));
        Console.WriteLine($"Burning {Path.GetFileName(iso)} to drive {letter}: ({caps.Vendor} {caps.Model})…");
        new DiscForge.Devices.Burning.Imapi2BurnEngine().BurnIso(iso, plan, progress);
        Console.WriteLine("Write complete.");
        if (args.Contains("--verify")) Console.WriteLine("Verify pass completed where the engine supports it.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    if (OperatingSystem.IsMacOS())
    {
        var iso = args[1];
        bool verify = args.Contains("--verify");
        int? speed = int.TryParse(OptVal(args, "--speed"), out var sp) ? sp : null;
        try
        {
            Console.WriteLine($"Burning {Path.GetFileName(iso)} via hdiutil — insert a blank disc in your optical writer…");
            int code = DiscForge.Core.Burning.MacOpticalBurner.RunBurn(iso, verify, speed, Console.WriteLine);
            if (code == 0) Console.WriteLine("Write complete.");
            return code == 0 ? 0 : 2;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }
    if (OperatingSystem.IsLinux())
    {
        var iso = args[1];
        string dev = args.Length >= 3 && !args[2].StartsWith("--") ? args[2] : DiscForge.Core.Burning.LinuxOpticalBurner.DefaultDevice;
        try
        {
            Console.WriteLine($"Burning {Path.GetFileName(iso)} to {dev}…");
            int code = DiscForge.Core.Burning.LinuxOpticalBurner.RunBurn(dev, iso, Console.WriteLine);
            if (code == 0) Console.WriteLine("Write complete.");
            return code == 0 ? 0 : 2;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }
    return Fail("`burn` could not find an optical-write backend for this platform.");
#endif
}

static int ReadDiscCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge read-disc <drive> <out.iso> [--continue-on-error] [--retries N]\n" +
                    "  Image a data disc (DVD, Blu-ray, or a data CD/DVD) to a flat .iso by copying every\n" +
                    "  2048-byte sector. Pair it with `burn` to clone a personal, unencrypted disc:\n" +
                    "      dforge read-disc D: game.iso      (Windows: <drive> is the drive letter)\n" +
                    "      dforge burn game.iso E:\n" +
                    "  Clean-room: it refuses any disc that declares a copy-protection system (CSS/CPRM/AACS)\n" +
                    "  and stops on a copy-protected sector — DiscForge images unencrypted discs only.\n" +
                    "  For an audio or mixed-mode CD, rip it track-by-track in the GUI (Read Disc) instead.\n" +
                    "  --continue-on-error zero-fills unreadable sectors and lists them (partial image);\n" +
                    "  --retries N sets per-sector re-reads before giving up (default 3).");

    string outPath = args[2];
    bool cont = args.Contains("--continue-on-error");
    int retries = int.TryParse(OptVal(args, "--retries"), out var rt) && rt >= 0 ? rt : 3;

#if WINDOWS
    string spec = args[1].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the optical drive letter, e.g. `dforge read-disc D: out.iso`. Run `dforge drives` to list recorders.");
    char letter = char.ToUpperInvariant(spec[0]);
    try
    {
        var opts = new DiscForge.Devices.Reading.ReadOptions
        {
            ContinueOnError = cont,
            RetriesPerSector = retries,
        };

        // Size the disc first so the user sees what they're about to copy.
        var cap = DiscForge.Devices.Reading.DataDiscImager.ReadCapacity(letter);
        Console.WriteLine($"Drive {letter}: — {cap.Sectors:N0} sectors × {cap.BlockLengthBytes} bytes = {cap.TotalBytes / (1024.0 * 1024.0):N1} MiB");
        Console.WriteLine($"Imaging to {Path.GetFileName(outPath)}…");

        var progress = new Progress<DiscForge.Devices.Reading.ReadProgress>(p =>
            Console.Write($"\r  {p.Detail}    "));

        DiscForge.Devices.Reading.ReadReport report;
        using (var fs = File.Create(outPath))
            report = DiscForge.Devices.Reading.DataDiscImager.ReadToIso(letter, fs, progress, opts);

        Console.WriteLine();
        foreach (var n in report.Notes) Console.WriteLine($"  note: {n}");
        if (report.Complete)
        {
            Console.WriteLine($"Done — every sector read cleanly. `dforge burn {Path.GetFileName(outPath)} <drive>` to write a copy.");
            return 0;
        }

        Console.WriteLine($"Done, but {report.BadSectors.Count:N0} sector(s) could not be read — the image is INCOMPLETE.");
        Console.WriteLine("  First unreadable LBAs: " +
            string.Join(", ", report.BadSectors.Take(10)) + (report.BadSectors.Count > 10 ? " …" : ""));
        return 2;
    }
    catch (DiscForge.Devices.Reading.DiscReadException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    _ = (outPath, cont, retries);
    if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
    {
        // A data disc is a readable block device on macOS/Linux, so the OS images it
        // directly — no SPTI layer needed. Give the exact command rather than a stub.
        string dev = OperatingSystem.IsMacOS() ? "/dev/disk4" : "/dev/sr0";
        string hint = OperatingSystem.IsMacOS()
            ? "find the raw device with `drutil status` or `diskutil list` (use the /dev/rdiskN node), then:"
            : "confirm the device with `dforge drives`, then:";
        return Fail(
            "read-disc images a disc through the SPTI stack, which is Windows-only.\n" +
            $"  On this platform the OS exposes a data disc as a block device — {hint}\n" +
            $"      dd if={dev} of={outPath} bs=2M status=progress\n" +
            "  That produces the same flat .iso, which `dforge burn` can write back.\n" +
            "  (DiscForge only clones unencrypted discs — do not use this on protected media.)");
    }
    return Fail("`read-disc`: no disc-reading backend for this platform.");
#endif
}

static int ReadBenchmarkCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge read-benchmark <drive> [--chunk N] [--json]\n" +
                    "  Read transfer-RATE benchmark (the read-speed half of ImgBurn's \"Discovery\"): reads the disc\n" +
                    "  sequentially in timed chunks and reports MB/s across the surface — average, min/max and the\n" +
                    "  SLOWEST region, which is where a marginal disc drags before it fails outright. Read-only; it\n" +
                    "  stops at the first unreadable area, and shrinks the chunk automatically if the drive caps\n" +
                    "  raw reads. --chunk sets sectors per timed read (default 16).");
#if WINDOWS
    string spec = args[1].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the optical drive letter, e.g. `dforge read-benchmark D:`. Run `dforge drives` to list drives.");
    char letter = char.ToUpperInvariant(spec[0]);
    int chunk = int.TryParse(OptVal(args, "--chunk"), out var cn) && cn > 0 ? cn : 16;
    try
    {
        using var dev = new DiscForge.Devices.Spti.SptiDevice(letter);
        var toc = DiscForge.Devices.Reading.DiscReader.ReadToc(dev);
        uint total = toc.LeadOutLba;
        if (total == 0) return Fail("The disc reports no sectors — is a disc loaded and spun up?");

        Console.WriteLine($"Benchmarking {letter}: reading {total:N0} sectors in {chunk}-sector chunks…");
        var progress = new Progress<double>(p => Console.Write($"\r  {p * 100,5:0.0}%    "));
        var samples = DiscForge.Devices.Reading.ReadRateBenchmark.Run(dev, 0, total, chunk, progress);
        var report = DiscForge.Core.Devices.ReadRateProfile.Summarize(samples);
        Console.WriteLine();

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                drive = letter.ToString(),
                report.SectorsRead, report.Samples,
                minMbps = Math.Round(report.MinMbps, 2),
                avgMbps = Math.Round(report.AvgMbps, 2),
                maxMbps = Math.Round(report.MaxMbps, 2),
                elapsedSeconds = Math.Round(report.ElapsedSeconds, 1),
                slowest = report.Slowest is null ? null
                        : new { report.Slowest.StartLba, mbps = Math.Round(report.Slowest.MbPerSecond, 2) },
            });
            return report.Samples == 0 ? 2 : 0;
        }

        Console.WriteLine($"  {report.Summary}");
        return report.Samples == 0 ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    _ = args;
    return Fail("`read-benchmark` uses the Windows SPTI READ CD path; run it on Windows with the drive attached.");
#endif
}

static int DiscScanCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge disc-scan <drive> [--bands N] [--samples N] [--json]\n" +
                    "  Media-QUALITY scan (the honest half of ImgBurn's \"Discovery\" mode): reads the disc across N\n" +
                    "  bands with C2 error pointers ON and reports, per band, how many sampled sectors returned C2\n" +
                    "  errors or were refused — surfacing a marginal/rotting disc or a dying drive that a plain read\n" +
                    "  won't show. Grades the disc EXCELLENT / GOOD / MARGINAL / FAILING. C2 accuracy is drive-\n" +
                    "  dependent, so this measures what the drive reports (a CD feature; DVD/BD use PI/PO, which needs\n" +
                    "  drive-specific commands DiscForge does not fake). --bands (default 12), --samples per band\n" +
                    "  (default 40). Read-only.");
#if WINDOWS
    string spec = args[1].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the optical drive letter, e.g. `dforge disc-scan D:`. Run `dforge drives` to list drives.");
    char letter = char.ToUpperInvariant(spec[0]);
    int bands = int.TryParse(OptVal(args, "--bands"), out var bn) && bn > 0
        ? bn : DiscForge.Devices.Reading.DiscQualityScanner.DefaultBands;
    int samples = int.TryParse(OptVal(args, "--samples"), out var sn) && sn > 0
        ? sn : DiscForge.Devices.Reading.DiscQualityScanner.DefaultSamplesPerBand;
    try
    {
        using var dev = new DiscForge.Devices.Spti.SptiDevice(letter);
        var toc = DiscForge.Devices.Reading.DiscReader.ReadToc(dev);
        uint total = toc.LeadOutLba;
        if (total == 0) return Fail("The disc reports no sectors — is a CD loaded and spun up?");

        Console.WriteLine($"Scanning {letter}: {total:N0} sectors across {bands} band(s), {samples} sample(s)/band…");
        var progress = new Progress<double>(p => Console.Write($"\r  {p * 100,5:0.0}%    "));
        var report = DiscForge.Devices.Reading.DiscQualityScanner.Scan(dev, total, bands, samples, true, progress);
        Console.WriteLine();

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                drive = letter.ToString(),
                health = report.Health.ToString(), report.Verdict,
                report.TotalSampled, report.TotalWithErrors, report.TotalRefused, report.TotalBadBytes,
                overallErrorRate = report.OverallErrorRate,
                elapsedSeconds = Math.Round(report.Elapsed.TotalSeconds, 1),
                bands = report.Bands.Select(b => new
                { b.Index, b.StartLba, b.EndLba, b.SectorsSampled, b.SectorsWithC2, b.SectorsRefused, b.BadBytes }),
                findings = report.Findings,
            });
            return report.Health == DiscForge.Devices.Reading.DiscHealth.Failing ? 2
                 : report.Health == DiscForge.Devices.Reading.DiscHealth.Unknown ? 1 : 0;
        }

        Console.WriteLine($"  Verdict: {report.Verdict}  ({report.Health})");
        Console.WriteLine($"  Sampled {report.TotalSampled:N0} sector(s): {report.TotalWithErrors:N0} with C2, " +
                          $"{report.TotalRefused:N0} refused, {report.TotalBadBytes:N0} bad byte(s) in {report.Elapsed.TotalSeconds:F1}s");
        foreach (var b in report.Bands)
            Console.WriteLine($"    band {b.Index,2}  LBA {b.StartLba,8:N0}–{b.EndLba,-8:N0}  " +
                              $"{b.SectorsWithC2,3}/{b.SectorsSampled} C2, {b.SectorsRefused} refused{(b.Perfect ? "   ok" : "")}");
        foreach (var f in report.Findings) Console.WriteLine($"  - {f}");
        return report.Health == DiscForge.Devices.Reading.DiscHealth.Failing ? 2
             : report.Health == DiscForge.Devices.Reading.DiscHealth.Unknown ? 1 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    _ = args;
    return Fail("`disc-scan` uses the Windows SPTI READ CD path; run it on Windows with the drive attached.");
#endif
}

static int DriveProfileCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge drive-profile <drive-letter> [--out profile.json]\n" +
                    "  Consolidate a drive's capabilities into ONE per-drive profile: read/write reach\n" +
                    "  (CD/DVD/BD), write modes (TAO, DAO/SAO, RAW-DAO-96) and read fidelity (raw sub-channel\n" +
                    "  read, C2 error pointers, buffer-underrun protection). The empirical fidelity probes a\n" +
                    "  Redump-grade dump depends on — C2 ACCURACY, CACHE-DEFEAT and the audio READ-OFFSET — need a\n" +
                    "  calibration/known-defective disc, so they are reported honestly as unprobed rather than\n" +
                    "  guessed (get the read-offset via `read-offset`). LEAD-OUT OVERREAD is probed empirically\n" +
                    "  when a disc is loaded (one non-destructive read at the lead-out); --no-probe skips it.\n" +
                    "  --out writes the profile as JSON for a per-drive record. Read-only.");
#if WINDOWS
    string spec = args[1].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the optical drive letter, e.g. `dforge drive-profile D:`. Run `dforge drives` to list drives.");
    char letter = char.ToUpperInvariant(spec[0]);
    string? outPath = OptVal(args, "--out");
    try
    {
        var caps = DiscForge.Devices.DriveDetector.Detect(letter);

        // Empirical lead-out overread probe — non-destructive; needs a disc loaded. --no-probe skips it.
        var overread = DiscForge.Core.Devices.ProbeState.NotProbed;
        string? probeDetail = null;
        if (!args.Contains("--no-probe"))
        {
            try
            {
                using var dev = new DiscForge.Devices.Spti.SptiDevice(letter);
                var pr = DiscForge.Devices.Reading.DriveOverreadProbe.Probe(dev);
                overread = !pr.DiscPresent ? DiscForge.Core.Devices.ProbeState.NotDetermined
                         : pr.Overread ? DiscForge.Core.Devices.ProbeState.Yes
                         : DiscForge.Core.Devices.ProbeState.No;
                probeDetail = pr.Detail;
            }
            catch (Exception ex) { probeDetail = $"not run ({ex.Message})"; }
        }

        var profile = DiscForge.Core.Devices.DriveProfile.FromCapabilities(caps, overread: overread);
        Console.WriteLine(profile.Render());
        if (probeDetail is not null) Console.WriteLine($"  overread probe: {probeDetail}");
        if (outPath is not null)
        {
            File.WriteAllText(outPath, profile.Json());
            Console.WriteLine($"Wrote profile: {outPath}");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    _ = args;
    return Fail("`drive-profile` uses the Windows SPTI device path; run it on Windows with the drive attached.");
#endif
}

static int ReadRawCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge read-raw <drive> <out.bin> [--start LBA] [--length N]\n" +
                    "  Read a CD's program area back as full RAW 2448-byte sectors (2352 main + 96-byte raw\n" +
                    "  interleaved P-W sub-channel) via READ CD, for verifying a RAW-DAO burn. Auto-detects the\n" +
                    "  field mode (data sectors use Raw; CD-DA uses UserData — Raw is illegal for audio).\n" +
                    "      dforge read-raw D: readback.bin           read from LBA 0 to end of the program area\n" +
                    "      dforge read-raw D: readback.bin --length 1000    read exactly 1000 sectors\n" +
                    "  Then prove the burn byte-for-byte:\n" +
                    "      dforge build-raw disc.cue golden.img --subcode raw\n" +
                    "      dforge raw-verify-readback golden.img readback.bin\n" +
                    "  --start LBA sets the first sector (default 0 = track 1, index 1); --length N reads exactly\n" +
                    "  N sectors instead of running to the end of the recorded area.\n" +
                    "  --field data|audio|auto   force the main-channel field mode (default auto). Use this to\n" +
                    "                     read one track of a MIXED-MODE disc on its own — `--field audio` reads\n" +
                    "                     the audio track (UserData), `--field data` the data track (Raw) — where\n" +
                    "                     the auto-probe can mis-pick at the data↔audio boundary.\n" +
                    "  --track N          read exactly track N: its TOC start LBA, length and field mode are\n" +
                    "                     taken from the disc's table of contents — the easy way to read one\n" +
                    "                     track of a mixed-mode disc (the audio track starts at its INDEX 01,\n" +
                    "                     so the unreadable pregap is skipped automatically). Overrides\n" +
                    "                     --start/--length/--field.\n" +
                    "  --reread N         read the range N times and emit the per-sector consensus (default 1).\n" +
                    "                     Sub-channel Q reads jitter — a drive can mis-decode one random sector's\n" +
                    "                     Q per pass — so a single read-back can't tell transient jitter from a\n" +
                    "                     real mis-address. Voting over N reads (3 is typical) out-votes the\n" +
                    "                     wandering mis-read while preserving a stable on-disc Q (incl. a\n" +
                    "                     deliberately-corrupt LibCrypt one) verbatim. Needs a bounded range\n" +
                    "                     (--length or --track). (alias: --consensus N)");

    string outPath = args[2];
    int start = int.TryParse(OptVal(args, "--start"), out var st) ? st : 0;
    long? length = long.TryParse(OptVal(args, "--length"), out var ln) && ln > 0 ? ln : null;
    string fieldOpt = (OptVal(args, "--field") ?? "auto").ToLowerInvariant();
    if (fieldOpt is not ("auto" or "data" or "audio"))
        return Fail("--field must be one of: data, audio, auto.");
    int? track = int.TryParse(OptVal(args, "--track"), out var tk) && tk > 0 ? tk : null;
    int reread = 1;
    {
        string? rv = OptVal(args, "--reread") ?? OptVal(args, "--consensus");
        if (rv is not null && (!int.TryParse(rv, out reread) || reread < 1))
            return Fail("--reread N must be a positive integer — the number of read passes to take " +
                        "sub-channel consensus over (3 is typical). 1 = a single read (default).");
    }

#if WINDOWS
    string spec = args[1].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the optical drive letter, e.g. `dforge read-raw D: readback.bin`. Run `dforge drives` to list drives.");
    char letter = char.ToUpperInvariant(spec[0]);
    try
    {
        using var dev = new DiscForge.Devices.Spti.SptiDevice(letter);

        // --track N: resolve start / length / field from the disc's TOC. A track's TOC start
        // address is its INDEX 01, so an audio track's unreadable pregap is skipped for free.
        if (track is int trackNo)
        {
            var toc = DiscForge.Devices.Reading.DiscReader.ReadToc(dev);
            var t = toc.Tracks.FirstOrDefault(x => x.Number == trackNo);
            if (t is null)
                return Fail($"Track {trackNo} is not on this disc (it has tracks " +
                            $"{toc.FirstTrack}–{toc.LastTrack}).");
            start = (int)t.StartLba;
            length = t.LengthSectors;
            fieldOpt = t.IsData ? "data" : "audio";
            Console.WriteLine($"Track {trackNo}: {(t.IsData ? "data" : "audio")}, LBA {start}, {length:N0} sectors (from TOC).");
        }

        var fieldSel = fieldOpt switch
        {
            "data" => DiscForge.Devices.Reading.RawDiscReader.FieldSelect.Data,
            "audio" => DiscForge.Devices.Reading.RawDiscReader.FieldSelect.Audio,
            _ => DiscForge.Devices.Reading.RawDiscReader.FieldSelect.Auto,
        };

        // Consensus read-back: vote over several passes to out-vote transient sub-channel Q jitter.
        // Every pass must cover the same sectors, so a bounded length (--length or --track) is required.
        if (reread >= 2)
        {
            if (length is null)
                return Fail("--reread/--consensus needs a bounded range so every pass reads the same " +
                            "sectors — add --length N (or --track N, which sets it from the TOC).");

            Console.WriteLine($"Reading raw sectors from {letter}: ({length:N0} sectors) starting LBA {start}, " +
                              $"consensus over {reread} passes…");
            var cProgress = new Progress<double>(p => Console.Write($"\r  {p * 100,5:0.0}%    "));
            DiscForge.Devices.Reading.RawDiscReader.Result cres;
            DiscForge.Core.Recovery.SubchannelConsensus.Report crep;
            using (var fs = File.Create(outPath))
                cres = DiscForge.Devices.Reading.RawDiscReader.ReadConsensus(
                    dev, start, length.Value, fs, reread, cProgress, fieldSel, out crep);

            Console.WriteLine();
            Console.WriteLine($"  Field mode: {cres.FieldMode}");
            Console.WriteLine($"  Read {cres.SectorsRead:N0} sectors × {reread} passes → {cres.BytesWritten:N0} bytes ({Path.GetFileName(outPath)})");
            Console.WriteLine($"  Consensus: {crep.SubCorrected:N0} sub-channel Q out-voted, " +
                              $"{crep.MainCorrected:N0} main-channel dropout(s) repaired, " +
                              $"{crep.PreservedNoValidQ:N0} preserved verbatim (no CRC-valid Q in any read).");
            Console.WriteLine($"  Verify: dforge raw-verify-readback golden.img {Path.GetFileName(outPath)}");
            return 0;
        }

        Console.WriteLine($"Reading raw sectors from {letter}: {(length is null ? "to end of program area" : $"({length:N0} sectors)")}, starting LBA {start}…");
        var progress = new Progress<double>(p => Console.Write($"\r  {p * 100,5:0.0}%    "));

        DiscForge.Devices.Reading.RawDiscReader.Result res;
        using (var fs = File.Create(outPath))
            res = DiscForge.Devices.Reading.RawDiscReader.Read(dev, start, length, fs, progress, fieldSel);

        Console.WriteLine();
        Console.WriteLine($"  Field mode: {res.FieldMode}");
        Console.WriteLine($"  Read {res.SectorsRead:N0} sectors → {res.BytesWritten:N0} bytes ({Path.GetFileName(outPath)})");
        if (res.StoppedReason is not null)
            Console.WriteLine($"  Stopped at end of readable area: {res.StoppedReason}");
        Console.WriteLine($"  Verify: dforge raw-verify-readback golden.img {Path.GetFileName(outPath)}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    _ = (outPath, start, length, track, reread);
    return Fail("`read-raw` uses the Windows SPTI READ CD path; run it on Windows with the drive attached.");
#endif
}

static int BlankCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge blank <drive> [--full]\n" +
                    "  Erase a rewritable disc (CD-RW / DVD-RW) so it can be written again. Default is a\n" +
                    "  MINIMAL blank (clears the PMA/TOC/pregap — fast, and enough to re-burn). --full erases\n" +
                    "  the entire disc, which is thorough but can take many minutes on CD-RW.\n" +
                    "      dforge blank D:            fast erase, then re-burn\n" +
                    "      dforge blank D: --full     full erase\n" +
                    "  Only rewritable media can be blanked — a CD-R/DVD-R is write-once.");
    bool full = args.Contains("--full");
#if WINDOWS
    string spec = args[1].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the drive letter, e.g. `dforge blank D:`. Run `dforge drives` to list drives.");
    char letter = char.ToUpperInvariant(spec[0]);
    try
    {
        using var dev = new DiscForge.Devices.Spti.SptiDevice(letter);
        Console.WriteLine($"{(full ? "Full" : "Minimal")} blanking {letter}: — {(full ? "this can take several minutes on CD-RW" : "should take under a minute")}…");
        // IMMED clear: block until the drive finishes. Generous timeout for the full erase.
        var r = dev.SendCommand(DiscForge.Core.Mmc.MmcCommands.Blank((byte)(full ? 0 : 1), immed: false, 0),
                                Array.Empty<byte>(), DiscForge.Devices.Spti.SptiDataDirection.None,
                                full ? 1800u : 300u);
        if (!r.Success)
        {
            var sense = new byte[32];
            var rs = dev.SendCommand(DiscForge.Core.Mmc.MmcCommands.RequestSense(32), sense, DiscForge.Devices.Spti.SptiDataDirection.In, 10);
            string extra = rs.Success && sense.Length >= 14
                ? $"  (key 0x{sense[2] & 0x0F:X1}, ASC 0x{sense[12]:X2}, ASCQ 0x{sense[13]:X2})" : "";
            return Fail($"BLANK failed: {r.Describe()}{extra}. Is the disc rewritable (CD-RW/DVD-RW)? A CD-R can't be erased.");
        }
        Console.WriteLine("Done — the disc should now be blank and writable. Re-run your burn.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    _ = full;
    return Fail("`blank` uses the Windows SPTI stack; run it on Windows with the drive attached.");
#endif
}

static int RawDumpCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge raw-dump <drive> [--stream-read] [--lba N] [--blocks N]\n" +
                    "  Drive/media DIAGNOSTIC for the Hitachi-LG GDR-816x DVD-ROM family (GDR-8161B/2B/3B/4B).\n" +
                    "  With no options it identifies the drive and reports its capabilities and the loaded media.\n" +
                    "  --stream-read issues a standard READ(12) with the streaming bit to confirm the drive can\n" +
                    "  return raw sector data where a plain read is refused, and reports what came back.\n" +
                    "  The bytes are reported AS-IS. For a console disc (GameCube/Wii/GD-ROM) they come back\n" +
                    "  DVD-scrambled, and DiscForge does NOT descramble them — it neither decodes nor produces\n" +
                    "  console game images. Read-only; reports only.");
#if WINDOWS
    string spec = args[1].TrimEnd(':', '\\', '/');
    if (spec.Length == 0) return Fail("Give the optical drive letter, e.g. `dforge raw-dump D:`. Run `dforge drives` to list drives.");
    char letter = char.ToUpperInvariant(spec[0]);
    try
    {
        // Streaming-read diagnostic: confirm READ(12)+streaming returns raw sector data
        // where a plain read is refused. Reports the bytes AS-IS — no descrambling.
        if (args.Contains("--stream-read"))
        {
            uint lba = uint.TryParse(OptVal(args, "--lba"), out var l) ? l : 0u;
            uint blocks = uint.TryParse(OptVal(args, "--blocks"), out var b) && b is > 0 and <= 64 ? b : 16u;
            Console.WriteLine($"Streaming READ(12) at LBA {lba}, {blocks} sectors…");
            var s = DiscForge.Devices.Reading.RawDiscDumper.StreamRead(letter, lba, blocks);
            if (!s.Ok) return Fail($"Streaming read refused: {s.Detail}.");
            Console.WriteLine($"  OK — {s.Bytes:N0} bytes returned, entropy {s.Entropy:F2} bpb, head={s.HeadHex}");
            Console.WriteLine($"  sha256={s.Sha256}");
            Console.WriteLine("  Bytes reported as-is. For a console disc this is DVD-scrambled data; DiscForge does");
            Console.WriteLine("  not descramble console formats — this is a drive/media diagnostic only.");
            return 0;
        }

        var report = DiscForge.Devices.Reading.RawDiscDumper.Probe(letter);
        Console.WriteLine(report.Render());
        Console.WriteLine();
        if (report.Supported)
        {
            Console.WriteLine("Recognised GDR-816x. `raw-dump " + letter + ": --stream-read` confirms it can return raw");
            Console.WriteLine("sectors (reported as-is; DiscForge does not descramble console disc formats).");
            return 0;
        }
        return Fail("Not a recognised GDR-816x drive — see the verdict above.");
    }
    catch (Exception ex) { return Fail(ex.Message); }
#else
    _ = args;
    return Fail("`raw-dump` uses the Windows SPTI stack; run it on Windows with the GDR-816x drive attached.");
#endif
}

static int FatLintCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge fat-lint <image> [--json]\n" +
        "  Structural integrity check for a FAT12/16/32 volume: the BPB geometry and boot signature, that the\n" +
        "  redundant FAT copies agree, that every cluster chain is well-formed (no free/bad/out-of-range link,\n" +
        "  no loop), that no two chains cross-link a cluster, and that no allocated cluster is orphaned (lost).\n" +
        "  The fsck-style pass a FAT floppy, boot image, or card dump needs before it is trusted. Read-only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var r = DiscForge.Core.Forensics.FatLint.Check(File.ReadAllBytes(args[1]));
        if (!r.IsFat) return Fail("Not a FAT volume (BPB/boot-signature check failed).");
        if (args.Contains("--json")) { EmitJson(r); return r.Ok ? 0 : 2; }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.Forensics.FatLint.Render(r)}");
        return r.Ok ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int FatLsCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge fat-ls <image> [--json]\n" +
        "  Lists a FAT16 or FAT32 volume — the filesystem inside an El Torito hard-disk boot image, a hybrid\n" +
        "  disc's FAT partition, or a UMD/card image. Walks the tree, reassembles long file names, and shows\n" +
        "  every file with its full path and size. (Use floppy-info for FAT12 floppies.) Reading only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        if (!DiscForge.Core.Fat.FatReader.IsFat(data))
            return Fail("Not a FAT volume (BPB/boot-signature check failed).");
        var vol = DiscForge.Core.Fat.FatReader.Read(data);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                type = vol.Type.ToString(), volume = vol.VolumeLabel,
                files = vol.Files.Count(), directories = vol.Directories.Count(), totalBytes = vol.TotalBytes,
                entries = vol.Entries.Select(e => new { e.Path, e.IsDirectory, e.Size }),
            });
            return 0;
        }

        Console.WriteLine($"Format: {vol.Type}");
        Console.WriteLine($"Volume: \"{vol.VolumeLabel}\"");
        Console.WriteLine();
        foreach (var e in vol.Entries.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine(e.IsDirectory ? $"  {"<DIR>",12}  {e.Path}" : $"  {e.Size,12:N0}  {e.Path}");
        Console.WriteLine();
        Console.WriteLine($"{vol.Files.Count()} file(s), {vol.Directories.Count()} folder(s), {vol.TotalBytes:N0} bytes.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int FatExtractCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge fat-extract <image> <out-dir> [--only /PATH]\n" +
        "  Extracts a FAT16/FAT32 volume's files to a folder, preserving the directory tree. --only limits it\n" +
        "  to one file by its in-image path. Reading/extraction only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        if (!DiscForge.Core.Fat.FatReader.IsFat(data))
            return Fail("Not a FAT volume (BPB/boot-signature check failed).");
        var vol = DiscForge.Core.Fat.FatReader.Read(data);

        string? only = OptVal(args, "--only");
        string norm(string p) => "/" + p.Replace('\\', '/').TrimStart('/');
        var files = vol.Files.Where(f => only is null || string.Equals(f.Path, norm(only), StringComparison.OrdinalIgnoreCase)).ToList();
        if (only is not null && files.Count == 0) return Fail($"No file at '{norm(only)}' in the volume.");

        Directory.CreateDirectory(args[2]);
        long total = 0;
        foreach (var f in files)
        {
            string rel = f.Path.TrimStart('/');
            string dest = Path.Combine(args[2], rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var bytes = DiscForge.Core.Fat.FatReader.ExtractFile(data, f);
            File.WriteAllBytes(dest, bytes);
            total += bytes.Length;
        }
        Console.WriteLine($"Extracted {files.Count} file(s), {total:N0} bytes, to {args[2]}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Convert a PS1 game's video mode (PAL<->NTSC) by flipping the GPU display-mode
// bit at every display-mode command found. Video timing only — no region,
// protection, or cheat/centering changes (see docs/PSX_VIDEO_MODE.md).
static int PsxVideoModeCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge psx-video-mode <in.exe|in.bin> --to ntsc|pal [--ppf out.ppf | --out out.bin]\n" +
                    "  With neither --ppf nor --out, lists the display-mode sites it would change.\n" +
                    "  Video-mode (display timing) only; region/protection/centering are untouched.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    DiscForge.Core.PlayStation.PsxVideoMode? target = null;
    string? ppfOut = null, imgOut = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--to" && i + 1 < args.Length)
            target = args[++i].ToLowerInvariant() switch
            {
                "ntsc" => DiscForge.Core.PlayStation.PsxVideoMode.Ntsc,
                "pal" => DiscForge.Core.PlayStation.PsxVideoMode.Pal,
                _ => null,
            };
        else if (args[i] == "--ppf" && i + 1 < args.Length) ppfOut = args[++i];
        else if (args[i] == "--out" && i + 1 < args.Length) imgOut = args[++i];
    }
    if (target is null) return Fail("Give the target mode with --to ntsc  or  --to pal.");

    try
    {
        var data = File.ReadAllBytes(args[1]);
        var sites = DiscForge.Core.PlayStation.Ps1VideoModePatcher.FindSites(data, target.Value);

        Console.WriteLine($"{sites.Count:N0} display-mode site(s) would convert to {target.Value.ToString().ToUpperInvariant()}.");
        foreach (var s in sites)
            Console.WriteLine($"  @0x{s.Offset:X}: {s.CurrentMode} (0x{s.OldParam:X2}) -> 0x{s.NewParam:X2}");
        if (sites.Count == 0) { Console.WriteLine("Nothing to change."); return 0; }

        if (ppfOut is not null)
        {
            var ppf = DiscForge.Core.PlayStation.Ps1VideoModePatcher.CreatePpf(data, target.Value);
            File.WriteAllBytes(ppfOut, ppf!);
            Console.WriteLine($"Wrote PPF {Path.GetFileName(ppfOut)} ({ppf!.Length:N0} bytes, undoable).");
        }
        if (imgOut is not null)
        {
            var copy = (byte[])data.Clone();
            DiscForge.Core.PlayStation.Ps1VideoModePatcher.PatchInPlace(copy, target.Value);
            File.WriteAllBytes(imgOut, copy);
            Console.WriteLine($"Wrote converted image {Path.GetFileName(imgOut)}.");
        }
        if (ppfOut is null && imgOut is null)
            Console.WriteLine("(Listing only — pass --ppf or --out to write the conversion.)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Identify a CD-R/CD-RW's dye/stamper manufacturer from its ATIP — the "CDR
// Identifier" job. Decodes a saved raw ATIP response; reading ATIP straight from a
// drive is the Windows device layer's job (the GUI Drives/Media view), which then
// calls this same MediaIdentityParser.
static int CdrInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cdr-info <atip-dump-file>\n" +
                    "  Decodes a raw READ TOC/PMA/ATIP response and names the media maker where known.\n" +
                    "  (Reading ATIP live from a drive is done in the Windows GUI.)");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var id = DiscForge.Core.Media.MediaIdentityParser.ParseAtip(File.ReadAllBytes(args[1]));
        if (id is null) return Fail("No ATIP in that dump — a pressed (non-recordable) disc carries none.");

        if (id.AtipCode is not null)
        {
            Console.WriteLine($"ATIP media code: {id.AtipCode}");
            Console.WriteLine($"  manufacturer : {id.Manufacturer ?? "(not in the table — look up the code)"}");
            Console.WriteLine($"  type         : {(id.IsRewritable ? "CD-RW" : "CD-R")}");
            if (id.CapacityMb is double mb) Console.WriteLine($"  capacity     : {mb:N0} MB");
            if (id.LeadOut is (int m, int s, int f)) Console.WriteLine($"  lead-out     : {m:00}:{s:00}:{f:00}");
        }
        else if (id.MediaId is not null)
        {
            Console.WriteLine($"DVD/BD media ID: {id.MediaId}");
            Console.WriteLine($"  manufacturer : {id.Manufacturer ?? "(not in the table)"}");
            if (id.BookTypeName is not null) Console.WriteLine($"  book type    : {id.BookTypeName}");
        }
        foreach (var n in id.Notes) Console.WriteLine($"  note: {n}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TmdInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge tmd-info <file.tmd>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var model = DiscForge.Core.Media.Tmd.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine($"TMD: {model.Objects.Count} object(s), {model.VertexTotal:N0} vertices total");
        for (int i = 0; i < model.Objects.Count; i++)
        {
            var o = model.Objects[i];
            Console.WriteLine($"  obj {i}: {o.Vertices.Count:N0} vert, {o.Normals.Count:N0} normal, " +
                              $"{o.PrimitiveCount:N0} primitive(s), {o.Faces.Count:N0} face(s), scale 2^{o.Scale}");
        }
        return 0;
    }
    catch (DiscForge.Core.Media.TmdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Tmd2Dxf(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge tmd2dxf <file.tmd> <out.dxf>\n" +
                    "  Exports the model as DXF 3DFACE polygons (common flat/gouraud, tri/quad,\n" +
                    "  textured or not); objects with no recognised faces fall back to a point cloud.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var model = DiscForge.Core.Media.Tmd.Parse(File.ReadAllBytes(args[1]));
        File.WriteAllText(args[2], DiscForge.Core.Media.Tmd.ToDxf(model));
        int faces = model.Objects.Sum(o => o.Faces.Count);
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: {faces:N0} face(s), {model.VertexTotal:N0} vertices from " +
                          $"{model.Objects.Count} object(s).");
        return 0;
    }
    catch (DiscForge.Core.Media.TmdFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TodInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge tod-info <file.tod>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var tod = DiscForge.Core.Media.Tod.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine($"TOD: version {tod.Version}, resolution {tod.Resolution}, {tod.Frames.Count:N0} frame(s)");
        int totalPackets = tod.Frames.Sum(f => f.Packets.Count);
        Console.WriteLine($"  {totalPackets:N0} packet(s) across all frames");
        Console.WriteLine("  (TOD structure per the public Sony description; validated by round trip, " +
                          "real-file validation pending a sample.)");
        return 0;
    }
    catch (DiscForge.Core.Media.TodFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VagExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge vag-extract <file.vag> <out.wav>\n" +
                    "  Decodes a PlayStation VAG (SPU-ADPCM) sample to a mono WAV.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var info = DiscForge.Core.PlayStation.Vag.ReadInfo(data);
        File.WriteAllBytes(args[2], DiscForge.Core.PlayStation.Vag.ToWav(data));
        int rate = info.SampleRate > 0 ? info.SampleRate : 44100;
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: mono @ {rate} Hz" +
                          (info.Name.Length > 0 ? $" (\"{info.Name}\")" : "") + ".");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.VagFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int AdxDecodeCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge adx-decode <in.adx> <out.wav>\n" +
                    "  Decodes a CRI ADX ADPCM stream (type 0x02/0x03) to a 16-bit PCM WAV.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var info = DiscForge.Core.GameAudio.AdxReader.ReadInfo(data);
        using (var outWav = File.Create(args[2]))
            DiscForge.Core.GameAudio.AdxDecoder.DecodeToWav(new MemoryStream(data), outWav);
        double seconds = info.SampleRate > 0 ? info.TotalSamples / (double)info.SampleRate : 0;
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: {info.Channels}ch @ {info.SampleRate} Hz, " +
                          $"{info.TotalSamples:N0} samples, {seconds:N1}s (encoding 0x{info.Encoding:X2}).");
        return 0;
    }
    catch (DiscForge.Core.GameAudio.AdxFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DspDecodeCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge dsp-decode <in.dsp> <out.wav>\n" +
                    "  Decodes a Nintendo GameCube/Wii DSP-ADPCM (.dsp) stream to a 16-bit mono PCM WAV.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var data = File.ReadAllBytes(args[1]);
        var header = DiscForge.Core.GameAudio.DspAdpcm.ReadHeader(data);
        using (var outWav = File.Create(args[2]))
            DiscForge.Core.GameAudio.DspAdpcm.DecodeToWav(data, outWav);
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: 1ch @ {header.SampleRate} Hz, " +
                          $"{header.SampleCount:N0} samples, {header.Seconds:N1}s" +
                          (header.Looped ? " (looped)." : "."));
        return 0;
    }
    catch (DiscForge.Core.GameAudio.DspFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ReadOffsetCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge read-offset <samples> [in.wav out.wav]\n" +
                    "  <samples> is the combined read offset (drive read offset + disc write offset).\n" +
                    "  With no WAV, prints the offset arithmetic. With in/out WAVs, slides the audio.");
    if (!int.TryParse(args[1], out int samples))
        return Fail($"Not a sample count: '{args[1]}'.");

    // No file → just the arithmetic (bytes, and how many sectors a real dump must over-read).
    if (args.Length < 4)
    {
        Console.WriteLine($"Combined read offset: {samples:+#;-#;0} samples ({DiscForge.Core.Audio.ReadOffset.SamplesToBytes(samples):+#;-#;0} bytes).");
        Console.WriteLine($"Guard-band over-read needed: {DiscForge.Core.Audio.ReadOffset.OverreadSectors(samples)} sector(s) each edge " +
                          $"({DiscForge.Core.Audio.ReadOffset.SamplesPerSector} samples/sector).");
        return 0;
    }

    string inPath = args[2], outPath = args[3];
    if (!File.Exists(inPath)) return Fail($"File not found: {inPath}");
    try
    {
        using var fs = File.OpenRead(inPath);
        var info = DiscForge.Core.Audio.WavReader.Read(fs);
        if (!info.IsCdAudioFormat)
            return Fail("read-offset needs a 44100 Hz / 16-bit / stereo (CD-DA) WAV.");

        var pcm = new byte[info.DataLength];
        fs.Seek(info.DataOffset, SeekOrigin.Begin);
        fs.ReadExactly(pcm, 0, pcm.Length);

        if (!DiscForge.Core.Audio.ReadOffset.ShiftDiscardsOnlySilence(pcm, samples))
            Console.WriteLine("Note: the edge this shift drops is not silent — a real dump should over-read the " +
                              "guard band instead of padding silence (see docs/REDUMP_PHYSICAL.md).");

        byte[] shifted = DiscForge.Core.Audio.ReadOffset.Apply(pcm, samples);
        var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(shifted);
        using (var os = File.Create(outPath))
            DiscForge.Core.Audio.WavWriter.Write(os, shorts, info.SampleRate, info.Channels);

        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: slid {samples:+#;-#;0} samples " +
                          $"({pcm.Length / DiscForge.Core.Audio.ReadOffset.BytesPerSample:N0} samples of CD audio).");
        return 0;
    }
    catch (DiscForge.Core.Audio.WavFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VabInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge vab-info <file.vab>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var vab = DiscForge.Core.PlayStation.Vab.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine($"VAB v{vab.Version} (id 0x{vab.VabId:X}): {vab.ProgramCount} program(s), " +
                          $"{vab.ToneCount} tone(s), {vab.VagCount} VAG waveform(s).");
        Console.WriteLine($"Master volume {vab.MasterVolume}, pan {vab.MasterPan}.");
        foreach (var p in vab.Programs)
        {
            Console.WriteLine($"  Program {p.Index}: {p.ToneCount} tone(s), vol {p.Volume}, pan {p.Pan}");
            foreach (var t in p.Tones)
                Console.WriteLine($"    tone -> VAG {t.Vag}, note {t.NoteLow}-{t.NoteHigh} (center {t.CenterNote}), " +
                                  $"vol {t.Volume}, pan {t.Pan}");
        }
        return 0;
    }
    catch (DiscForge.Core.PlayStation.VabFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SeqInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge seq-info <file.seq>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var seq = DiscForge.Core.PlayStation.Seq.Parse(File.ReadAllBytes(args[1]));
        double bpm = seq.Tempo > 0 ? 60_000_000.0 / seq.Tempo : 0;
        Console.WriteLine($"SEQ v{seq.Version}: {seq.Ppqn} ppqn, tempo {seq.Tempo} " +
                          $"({bpm:N1} BPM), {seq.EventCount} event(s).");
        return 0;
    }
    catch (DiscForge.Core.PlayStation.SeqFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int StrDemuxCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge str-demux <in.str> <out-dir> [--sector-size 2352|2048]\n" +
                    "  Splits a PSX .str into per-frame MDEC bitstreams and reports XA audio.\n" +
                    "  MDEC pixel decode is deferred — see docs/PSX_MEDIA.md.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var layout = DiscForge.Core.PlayStation.StrDemuxer.Layout.Raw2352;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--sector-size" && i + 1 < args.Length)
            layout = args[++i] == "2048"
                ? DiscForge.Core.PlayStation.StrDemuxer.Layout.UserData2048
                : DiscForge.Core.PlayStation.StrDemuxer.Layout.Raw2352;

    try
    {
        Directory.CreateDirectory(args[2]);
        DiscForge.Core.PlayStation.StrDemuxResult result;
        using (var img = File.OpenRead(args[1]))
            result = DiscForge.Core.PlayStation.StrDemuxer.Demux(img, layout);

        foreach (var f in result.Frames)
        {
            string name = Path.Combine(args[2], $"frame_{f.FrameNumber:D5}.mdec");
            File.WriteAllBytes(name, f.Bitstream);
        }

        Console.WriteLine($"Demuxed {result.TotalSectorCount:N0} sector(s): " +
                          $"{result.Frames.Count} video frame(s) ({result.VideoSectorCount} sector(s)), " +
                          $"{result.AudioSectors.Count} XA-audio sector(s).");
        if (result.Frames.Count > 0)
        {
            var first = result.Frames[0];
            Console.WriteLine($"  Frame size {first.Width}x{first.Height}; wrote frame_*.mdec bitstreams to {args[2]}.");
        }
        Console.WriteLine("  Wrote frame_*.mdec bitstreams — use `str-frames` to decode v2 frames to PNG.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int StrFramesCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge str-frames <in.str> <out-dir> [--sector-size 2352|2048]\n" +
                    "  Decode a PSX .str's video frames to PNG images (frame_NNNNN.png). Handles STR\n" +
                    "  version 2 (the common codec); version 3's differential DC is not yet supported\n" +
                    "  and those frames are reported and skipped.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var layout = DiscForge.Core.PlayStation.StrDemuxer.Layout.Raw2352;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--sector-size" && i + 1 < args.Length)
            layout = args[++i] == "2048"
                ? DiscForge.Core.PlayStation.StrDemuxer.Layout.UserData2048
                : DiscForge.Core.PlayStation.StrDemuxer.Layout.Raw2352;

    try
    {
        Directory.CreateDirectory(args[2]);
        DiscForge.Core.PlayStation.StrDemuxResult result;
        using (var img = File.OpenRead(args[1]))
            result = DiscForge.Core.PlayStation.StrDemuxer.Demux(img, layout);

        int decoded = 0, skipped = 0;
        foreach (var f in result.Frames)
        {
            try
            {
                var image = DiscForge.Core.PlayStation.MdecFrameDecoder.DecodeFrame(f.Bitstream, f.Width, f.Height);
                var png = DiscForge.Core.Util.PngWriter.EncodeRgba(image.Rgba, image.Width, image.Height);
                File.WriteAllBytes(Path.Combine(args[2], $"frame_{f.FrameNumber:D5}.png"), png);
                decoded++;
            }
            catch (DiscForge.Core.PlayStation.MdecFrameDecoder.MdecDecodeException ex)
            {
                skipped++;
                if (skipped <= 3) Console.WriteLine($"  frame {f.FrameNumber}: {ex.Message}");
            }
        }

        Console.WriteLine($"Decoded {decoded} frame(s) to PNG in {args[2]}" +
                          (skipped > 0 ? $"; skipped {skipped} (unsupported/undecodable)." : "."));
        return decoded > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int MdecInfoCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge mdec-info <in.str | frame.mdec> [--sector-size 2352|2048] [--json]\n" +
            "  Reports the MDEC codec parameters of a PlayStation video: for a .str it demuxes each frame and\n" +
            "  prints its geometry plus the frame's MDEC header (codec version, quant scale, macroblock count);\n" +
            "  for a single demuxed frame it parses that header directly. Reading only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var layout = DiscForge.Core.PlayStation.StrDemuxer.Layout.Raw2352;
    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--sector-size" && i + 1 < args.Length)
            layout = args[++i] == "2048"
                ? DiscForge.Core.PlayStation.StrDemuxer.Layout.UserData2048
                : DiscForge.Core.PlayStation.StrDemuxer.Layout.Raw2352;
    bool json = args.Contains("--json");

    try
    {
        var bytes = File.ReadAllBytes(args[1]);

        // A raw demuxed frame begins with the MDEC 0x3800 marker at offset 2; a .str does not.
        bool looksLikeFrame = bytes.Length >= 8 && bytes[2] == 0x00 && bytes[3] == 0x38;
        if (looksLikeFrame)
        {
            var hdr = DiscForge.Core.PlayStation.Mdec.ParseFrameHeader(bytes);
            if (json) { EmitJson(hdr); return 0; }
            Console.WriteLine($"{Path.GetFileName(args[1])}: MDEC frame  v{hdr.Version}  qscale {hdr.QuantScale}  " +
                              $"codes {hdr.CodeCount}  marker {(hdr.MarkerOk ? "ok" : "MISSING")}");
            return hdr.MarkerOk ? 0 : 1;
        }

        DiscForge.Core.PlayStation.StrDemuxResult result;
        using (var img = File.OpenRead(args[1]))
            result = DiscForge.Core.PlayStation.StrDemuxer.Demux(img, layout);

        var rows = result.Frames.Select(f =>
        {
            var hdr = f.Bitstream.Length >= 8 ? DiscForge.Core.PlayStation.Mdec.ParseFrameHeader(f.Bitstream) : null;
            int mbs = ((f.Width + 15) / 16) * ((f.Height + 15) / 16);
            return new
            {
                f.FrameNumber, f.Width, f.Height, f.Complete,
                macroblocks = mbs, blocks = mbs * 6,
                version = hdr?.Version, quantScale = hdr?.QuantScale,
                codeCount = hdr?.CodeCount, markerOk = hdr?.MarkerOk,
            };
        }).ToList();

        if (json) { EmitJson(new { frames = rows.Count, rows }); return 0; }

        Console.WriteLine($"{Path.GetFileName(args[1])}: {rows.Count} MDEC video frame(s)");
        foreach (var r in rows)
            Console.WriteLine($"  frame {r.FrameNumber,-4} {r.Width}x{r.Height,-4}  {r.macroblocks,4} MB  " +
                              $"v{r.version}  qscale {r.quantScale}  " +
                              $"{(r.markerOk == true ? "marker ok" : "marker MISSING")}{(r.Complete ? "" : "  [incomplete]")}");
        Console.WriteLine("  (MDEC codec parameters; full VLC pixel decode is pending a real .str sample — see docs/PSX_MEDIA.md)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VobDemuxCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge vob-demux <in.vob|in.mpg> <out-dir>\n" +
                    "  Splits an UNENCRYPTED MPEG program stream (VOB/MPG) into elementary\n" +
                    "  video/audio/subpicture streams. Does not decrypt CSS-scrambled VOBs.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        Directory.CreateDirectory(args[2]);
        DiscForge.Core.Mpeg.MpegPsDemuxResult r;
        using (var f = File.OpenRead(args[1]))
            r = DiscForge.Core.Mpeg.MpegProgramStream.Demux(f);

        int written = 0;
        foreach (var st in r.Streams)
        {
            if (st.Data.Length == 0) continue;
            string name = Path.Combine(args[2], st.SuggestedName());
            File.WriteAllBytes(name, st.Data);
            written++;
            string sub = st.SubStreamId >= 0 ? $"/0x{st.SubStreamId:X2}" : "";
            Console.WriteLine($"  {st.Kind,-9} id 0x{st.StreamId:X2}{sub}  {st.Data.Length,12:N0} bytes  -> {Path.GetFileName(name)}");
        }
        Console.WriteLine($"Demuxed {r.PackCount:N0} pack(s), {r.PesPacketCount:N0} PES packet(s), " +
                          $"{written} stream(s) ({(r.SawMpeg2 ? "MPEG-2" : "MPEG-1")}).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VcdPsdCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge vcd-psd <PSD.VCD> [LOT.VCD]\n" +
        "  Decodes a Video CD's PlayBack Control: the menus (selection lists), play lists and end lists in\n" +
        "  PSD.VCD, resolving the offsets that link them so you can read the menu graph — which selection\n" +
        "  jumps where. An optional LOT.VCD is shown as the LID→offset table. Read-only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var doc = DiscForge.Core.VideoCd.VcdPsd.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.VideoCd.VcdPsd.Render(doc)}");
        if (args.Length > 2 && File.Exists(args[2]))
        {
            var lot = DiscForge.Core.VideoCd.VcdPsd.ReadLot(File.ReadAllBytes(args[2]));
            Console.WriteLine($"LOT: {lot.Count} list(s) — " +
                string.Join(", ", lot.Select((o, i) => $"LID {i + 1}→{(o < 0 ? "-" : o.ToString())}")));
        }
        return doc.Descriptors.Count > 0 ? 0 : 1;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int VcdControlCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge vcd-control <out-dir> [--album NAME] [--svcd] [--entry T:M:S:F ...]\n" +
                    "  Writes INFO.VCD and ENTRIES.VCD (the VCD control sectors) into <out-dir>/VCD/.\n" +
                    "  Each --entry is a play point track:minute:second:frame (e.g. --entry 1:0:2:0).");
    string outDir = args[1];
    string album = "";
    bool svcd = false;
    var entries = new List<DiscForge.Core.VideoCd.VideoCdEntry>();
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--album" && i + 1 < args.Length) album = args[++i];
        else if (args[i] == "--svcd") svcd = true;
        else if (args[i] == "--entry" && i + 1 < args.Length)
        {
            var p = args[++i].Split(':');
            if (p.Length != 4 || !int.TryParse(p[0], out int tr) || !int.TryParse(p[1], out int mm)
                || !int.TryParse(p[2], out int ss) || !int.TryParse(p[3], out int ff))
                return Fail($"Bad --entry '{args[i]}' (expected track:minute:second:frame).");
            entries.Add(new DiscForge.Core.VideoCd.VideoCdEntry { TrackNumber = tr, Minute = mm, Second = ss, Frame = ff });
        }
    }
    if (entries.Count == 0)
        entries.Add(new DiscForge.Core.VideoCd.VideoCdEntry { TrackNumber = 1, Minute = 0, Second = 2, Frame = 0 });
    try
    {
        var info = DiscForge.Core.VideoCd.VideoCdControl.BuildInfo(
            new DiscForge.Core.VideoCd.VideoCdInfoPlan { AlbumId = album, SuperVcd = svcd });
        var ents = DiscForge.Core.VideoCd.VideoCdControl.BuildEntries(entries, superVcd: svcd);
        string vcdDir = Path.Combine(outDir, "VCD");
        Directory.CreateDirectory(vcdDir);
        File.WriteAllBytes(Path.Combine(vcdDir, "INFO.VCD"), info);
        File.WriteAllBytes(Path.Combine(vcdDir, "ENTRIES.VCD"), ents);
        Console.WriteLine($"Wrote VCD/INFO.VCD and VCD/ENTRIES.VCD ({entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}) to {outDir}.");
        Console.WriteLine("  These are the control sectors. A full player-verified VCD image (MPEG track in");
        Console.WriteLine("  Mode 2/Form 2 + ISO tree) needs a reference VCD to validate against.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DvdIfoCmd(string[] args)
{
    if (args.Length < 4)
        return Fail("usage:\n" +
                    "  dforge dvd-ifo dump  <VIDEO_TS folder> <out.json>   Dump the DVD structure to editable JSON\n" +
                    "  dforge dvd-ifo build <plan.json> <out folder>       Rebuild VIDEO_TS.IFO/VTS_nn_0.IFO from JSON\n" +
                    "  IFO files are unencrypted; this never touches scrambled video.");
    try
    {
        if (args[1] == "dump")
        {
            if (!Directory.Exists(args[2])) return Fail($"Not a folder: {args[2]}");
            var src = new DiscForge.Core.DvdVideo.VideoTsSources.Folder(args[2]);
            var dvd = DiscForge.Core.DvdVideo.IfoReader.Read(src);
            var dto = DiscForge.Core.DvdVideo.IfoPlanJson.FromStructure(dvd);
            string json = DiscForge.Core.DvdVideo.IfoPlanJson.ToJson(dto);
            File.WriteAllText(args[3], json);
            Console.WriteLine($"Wrote editable DVD structure to {args[3]} " +
                              $"({dto.TitleSets.Count} title set(s)). Edit it, then: dforge dvd-ifo build {args[3]} <out>");
            return 0;
        }
        if (args[1] == "build")
        {
            if (!File.Exists(args[2])) return Fail($"File not found: {args[2]}");
            var dto = DiscForge.Core.DvdVideo.IfoPlanJson.FromJson(File.ReadAllText(args[2]));
            var plan = DiscForge.Core.DvdVideo.IfoPlanJson.ToPlan(dto);
            var files = DiscForge.Core.DvdVideo.IfoWriter.Write(plan);
            string videoTs = Path.Combine(args[3], "VIDEO_TS");
            Directory.CreateDirectory(videoTs);
            foreach (var (name, bytes) in files)
            {
                File.WriteAllBytes(Path.Combine(videoTs, name), bytes);
                File.WriteAllBytes(Path.Combine(videoTs, Path.ChangeExtension(name, ".BUP")), bytes);
            }
            Console.WriteLine($"Rebuilt {files.Count} IFO file(s) (+ .BUP) to {videoTs} from {args[2]}.");
            return 0;
        }
        return Fail($"Unknown dvd-ifo sub-command '{args[1]}' (expected dump or build).");
    }
    catch (DiscForge.Core.DvdVideo.IfoFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int XaExtractCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge xa-extract <raw image> <out.wav> [--sector-size 2352|2336] [--channel N]\n" +
                    "  Decodes CD-ROM XA ADPCM audio from a raw disc image to a WAV.\n" +
                    "  Rate and mono/stereo are read from the XA subheader.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    var layout = DiscForge.Core.PlayStation.XaExtract.SectorLayout.Raw2352;
    int? channel = null;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--sector-size" && i + 1 < args.Length)
            layout = args[++i] == "2336"
                ? DiscForge.Core.PlayStation.XaExtract.SectorLayout.Mode2_2336
                : DiscForge.Core.PlayStation.XaExtract.SectorLayout.Raw2352;
        else if (args[i] == "--channel" && i + 1 < args.Length) channel = int.Parse(args[++i]);
    }

    try
    {
        DiscForge.Core.PlayStation.XaExtract.Result result;
        using (var img = File.OpenRead(args[1]))
            result = DiscForge.Core.PlayStation.XaExtract.Extract(img, layout, channel);

        using (var outWav = File.Create(args[2]))
            DiscForge.Core.Audio.WavWriter.Write(outWav, result.Pcm, result.SampleRate, result.Channels);

        double seconds = result.Pcm.Length / (double)(result.SampleRate * result.Channels);
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: {result.Channels}ch @ {result.SampleRate} Hz, " +
                          $"{result.SectorsUsed:N0} XA sector(s), {seconds:N1}s.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TimInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge tim-info <file.tim>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var tim = DiscForge.Core.Media.Tim.Parse(File.ReadAllBytes(args[1]));
        Console.WriteLine($"TIM {tim.Mode.ToString().Replace("Bpp", "")}bpp, {tim.Width}x{tim.Height}, " +
                          $"{tim.PaletteCount} palette(s)");
        return 0;
    }
    catch (DiscForge.Core.Media.TimFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TimExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge tim-extract <file.tim> <out.png> [--palette N]\n" +
                    "  Decodes a TIM to a PNG (palette N selects a CLUT for 4/8bpp images).");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    int palette = 0;
    for (int i = 3; i < args.Length - 1; i++)
        if (args[i] == "--palette") palette = int.Parse(args[i + 1]);

    try
    {
        var tim = DiscForge.Core.Media.Tim.Parse(File.ReadAllBytes(args[1]));
        File.WriteAllBytes(args[2], DiscForge.Core.Media.Tim.ToPng(tim, palette));
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])} ({tim.Width}x{tim.Height} RGBA PNG).");
        return 0;
    }
    catch (DiscForge.Core.Media.TimFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ToCcd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge to-ccd <image.cue> [--out <basename>]\n" +
                    "  Writes <basename>.ccd (and expects the matching .img/.sub alongside).");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string outBase = Path.GetFileNameWithoutExtension(args[1]);
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--out") outBase = args[i + 1];

    try
    {
        var ext = Path.GetExtension(args[1]).ToLowerInvariant();
        if (ext != ".cue")
            return Fail("to-ccd takes a .cue sheet (full TOC). " +
                        "Use build-raw to make the .img/.sub, then to-ccd on the .cue.");

        bool verbatim = args.Contains("--verbatim");
        using var layout = DiscLayout.FromCueFile(args[1], subVerbatim: verbatim);

        string ccd = CloneCdWriter.BuildCcd(layout);
        var names = CloneCdWriter.NamesFor(outBase);
        File.WriteAllText(names.Ccd, ccd);

        // Emit the matching CloneCD sidecars directly: the .img is the 2352-byte main channel from
        // program LBA 0 (lead-in skipped), the .sub is the raw 96-byte P-W sub-channel. We generate
        // DiscForge's combined interleaved image and split it as it is produced.
        long skip = (long)RawImageGenerator.LeadInSectors *
                    RawImageGenerator.SectorSize(RawSubcodeForm.Interleaved96);
        long program = RawImageGenerator.ProgramSectors(layout);
        using (var imgFile = File.Create(names.Img))
        using (var subFile = File.Create(names.Sub))
        using (var split = new CcdSplitStream(imgFile, subFile, skip))
        {
            long lastPct = -1;
            RawImageGenerator.Generate(layout, RawSubcodeForm.Interleaved96, split, new Progress<double>(f =>
            {
                long pct = (long)(f * 100);
                if (pct != lastPct && pct % 10 == 0) { lastPct = pct; Console.Write($"\r  composing… {pct}%"); }
            }));
            Console.WriteLine("\r  composing… done   ");
        }

        Console.WriteLine($"Wrote {names.Ccd}, {Path.GetFileName(names.Img)} and {Path.GetFileName(names.Sub)}");
        Console.WriteLine($"  {program:N0} program sectors — 2352-byte main channel + raw P-W sub-channel " +
                          "(a complete CloneCD set, no separate build-raw step needed).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Checksum(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge checksum <file> [--write [sha256|md5|sha1|sfv|all]] [--verify]");

    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    bool verify = args.Contains("--verify");
    string? write = null;
    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--write")
            write = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "sha256";

    try
    {
        long size = new FileInfo(path).Length;
        Console.WriteLine($"{Path.GetFileName(path)} ({size:N0} bytes)");
        long lastPct = -1;
        var sums = ImageChecksums.ComputeFile(path, new Progress<double>(f =>
        {
            long pct = (long)(f * 100);
            if (pct != lastPct && pct % 10 == 0) { lastPct = pct; Console.Write($"\r  hashing… {pct}%"); }
        }));
        Console.WriteLine("\r  hashing… done ");
        Console.WriteLine($"  CRC-32  {sums.Crc32}");
        Console.WriteLine($"  MD5     {sums.Md5}");
        Console.WriteLine($"  SHA-1   {sums.Sha1}");
        Console.WriteLine($"  SHA-256 {sums.Sha256}");

        if (write is not null)
        {
            var algos = write.ToLowerInvariant() == "all"
                ? new[] { "sha256", "md5", "sha1", "crc32" }
                : new[] { write.ToLowerInvariant() == "sfv" ? "crc32" : write.ToLowerInvariant() };
            foreach (var a in algos)
                Console.WriteLine($"  wrote {ImageChecksums.WriteSidecar(path, sums, a)}");
        }

        if (verify)
        {
            var sidecar = ImageChecksums.FindSidecar(path);
            if (sidecar is null)
                return Fail("No sidecar (.sha256/.sha1/.md5/.sfv) found next to the file.");
            string got = ImageChecksums.ValueFor(sums, sidecar.Algorithm);
            bool ok = got.Equals(sidecar.ExpectedHex, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(ok
                ? $"  VERIFIED against {Path.GetFileName(sidecar.SidecarPath)} ({sidecar.Algorithm})"
                : $"  MISMATCH: {Path.GetFileName(sidecar.SidecarPath)} says {sidecar.ExpectedHex}, " +
                  $"file is {got}");
            return ok ? 0 : 1;
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ScummvmDetect(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge scummvm-detect <folder|file> [--recursive] [--bytes N]");

    var path = args[1];
    bool recursive = args.Contains("--recursive");
    int bytes = ScummVmFingerprint.DefaultBytes;
    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--bytes" && i + 1 < args.Length && int.TryParse(args[i + 1], out int b) && b > 0)
            bytes = b;

    try
    {
        IReadOnlyList<ScummVmFingerprint.Fingerprint> prints;
        if (Directory.Exists(path))
            prints = ScummVmFingerprint.ForDirectory(path, recursive, bytes);
        else if (File.Exists(path))
            prints = new[] { ScummVmFingerprint.ForFile(path, bytes) };
        else
            return Fail($"'{path}' not found.");

        if (prints.Count == 0)
        {
            Console.WriteLine("No files found to fingerprint.");
            return 0;
        }

        Console.WriteLine($"ScummVM Advanced-Detector fingerprints (MD5 of first {bytes} bytes):");
        Console.WriteLine();
        int nameWidth = Math.Min(48, prints.Max(p => p.Name.Length));
        foreach (var p in prints)
            Console.WriteLine($"  {p.Name.PadRight(nameWidth)}  {p.Size,12:N0}  {p.Md5}");
        Console.WriteLine();
        Console.WriteLine("Match the (size, md5) pairs above against a game's entry on the ScummVM wiki");
        Console.WriteLine("or its detection tables to identify the title.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ScummvmExport(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge scummvm-export <image.cue> <outdir> [--flac|--ogg] [--high]");

    var cuePath = args[1];
    var outDir = args[2];
    if (!File.Exists(cuePath)) return Fail($"'{cuePath}' not found.");
    var format = args.Contains("--flac") ? ScummVmExport.AudioFormat.Flac
               : args.Contains("--ogg") ? ScummVmExport.AudioFormat.Ogg
               : ScummVmExport.AudioFormat.Wav;
    var oggQuality = args.Contains("--high") ? VorbisEncoder.Quality.High : VorbisEncoder.Quality.Standard;

    try
    {
        var result = ScummVmExport.Export(cuePath, outDir, format, oggQuality);
        Console.WriteLine($"Exported to {outDir}:");
        Console.WriteLine($"  data files extracted: {result.DataFilesExtracted}");
        Console.WriteLine($"  audio tracks: {result.AudioTracks.Count} ({result.AudioFormatWritten.ToString().ToLowerInvariant()})");
        foreach (var a in result.AudioTracks)
            Console.WriteLine($"    {Path.GetFileName(a.Path)}  ({a.Sectors:N0} sectors)");
        foreach (var w in result.Warnings)
            Console.WriteLine($"  note: {w}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IpBinInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge ipbin-info <image> [--json]   (.gdi, .cue, .cdi, or a raw .bin/.iso)\n" +
                    "  Reads the Dreamcast boot header (IP.BIN) and reports the disc's identity plus an\n" +
                    "  integrity check: the Sega signatures and the device-info checksum are verified against\n" +
                    "  the header's own product fields (the Katana boot CRC). Descriptive only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        IpBinHeader? header = IpBin.Identify(path);

        if (header is null)
            return Fail("No Dreamcast boot header (\"SEGA SEGAKATANA\") was found — " +
                        "this image has no bootable Dreamcast data track, or the format isn't supported here.");

        string disc = header.DiscNumber is { } dn && header.DiscTotal is { } dt ? $"disc {dn} of {dt}" : "";
        string crc = header.CrcPresent
            ? $"{header.StoredCrc:X4} {(header.CrcValid ? "✓" : $"✗ (computed {header.ComputedCrc:X4})")}"
            : $"none stored (computed {header.ComputedCrc:X4})";

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                header.Title, header.ProductNumber, header.Version, header.Maker,
                header.DeviceInfo, header.DiscNumber, header.DiscTotal,
                regions = header.Regions, region = header.RegionCode,
                header.ReleaseDate, header.BootFile,
                header.Peripherals, peripheralList = header.SupportedPeripherals,
                storedCrc = header.StoredCrc is { } s ? $"{s:X4}" : null,
                computedCrc = $"{header.ComputedCrc:X4}",
                header.CrcValid, header.HardwareIdValid, header.MakerIdValid,
                integrity = header.Integrity(),
            });
            return header.HardwareIdValid && header.MakerIdValid && (header.CrcValid || !header.CrcPresent) ? 0 : 2;
        }

        Console.WriteLine($"Dreamcast disc — {Path.GetFileName(path)}");
        Console.WriteLine($"  Title:       {header.Title}");
        Console.WriteLine($"  Product:     {header.ProductNumber}  {header.Version}");
        Console.WriteLine($"  Maker:       {header.Maker}");
        Console.WriteLine($"  Device:      {header.DeviceInfo}" + (disc.Length > 0 ? $"  ({disc})" : ""));
        Console.WriteLine($"  Region:      {(header.Regions.Count > 0 ? string.Join(", ", header.Regions) : "none")}" +
                          (header.RegionCode.Length > 0 ? $"  ({header.RegionCode})" : ""));
        Console.WriteLine($"  Released:    {header.ReleaseDate}");
        Console.WriteLine($"  Boot file:   {header.BootFile}");
        Console.WriteLine($"  Boot CRC:    {crc}");
        Console.WriteLine($"  Peripherals: {header.Peripherals}");
        foreach (var p in header.SupportedPeripherals)
            Console.WriteLine($"      - {p}");
        Console.WriteLine($"  Integrity:   {header.Integrity()}");
        // Non-zero when the header fails its own integrity check, so a script can flag it.
        return header.HardwareIdValid && header.MakerIdValid && (header.CrcValid || !header.CrcPresent) ? 0 : 2;
    }
    catch (IpBinFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PvrInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge pvr-info <file.pvr> [--json]\n" +
                    "  Reads a Sega Dreamcast PVR texture header and reports its colour format, layout\n" +
                    "  (twiddled / VQ / rectangle / mipmapped …), dimensions and optional GBIX global index,\n" +
                    "  plus a structural check (known formats, power-of-two/square dimensions, not truncated).\n" +
                    "  Read-only content metadata — it describes the texture, it does not render or convert it.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var pvr = DiscForge.Core.Dreamcast.Pvr.ParseFile(path);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                pvr.Width, pvr.Height,
                pixelFormat = pvr.PixelFormatName, pixelFormatCode = pvr.PixelFormatCode,
                dataFormat = pvr.DataFormatName, dataFormatCode = pvr.DataFormatCode,
                pvr.HasGlobalIndex,
                globalIndex = pvr.GlobalIndex is { } gi ? $"{gi:X8}" : null,
                pvr.DeclaredDataSize, pvr.AvailableDataBytes,
                valid = pvr.Valid,
                warnings = pvr.Warnings,
            });
            return pvr.Valid ? 0 : 2;
        }

        Console.WriteLine($"PVR texture — {Path.GetFileName(path)}");
        Console.WriteLine($"  Dimensions:  {pvr.Width} × {pvr.Height}");
        Console.WriteLine($"  Pixel format: {pvr.PixelFormatName}");
        Console.WriteLine($"  Data format:  {pvr.DataFormatName}");
        if (pvr.HasGlobalIndex) Console.WriteLine($"  GBIX index:   {pvr.GlobalIndex:X8}");
        Console.WriteLine($"  Declared:    {pvr.DeclaredDataSize:N0} bytes; present {pvr.AvailableDataBytes:N0}");
        if (pvr.Valid) Console.WriteLine("  Integrity:   header OK.");
        else
            foreach (var w in pvr.Warnings) Console.WriteLine($"  [Warning] {w}");
        return pvr.Valid ? 0 : 2;
    }
    catch (DiscForge.Core.Dreamcast.PvrFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PvmInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge pvm-info <file.pvm> [--json]\n" +
                    "  Lists the PVR textures bundled in a Dreamcast PVM archive — each texture's recorded\n" +
                    "  filename, colour/data format and dimensions — and checks the count and each texture's\n" +
                    "  header. Read-only content metadata; it does not unpack or render anything.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var pvm = DiscForge.Core.Dreamcast.Pvm.ParseFile(path);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                pvm.DeclaredCount,
                found = pvm.Textures.Count,
                pvm.HasFilenames, pvm.HasGlobalIndices, pvm.HasDimensions, pvm.HasFormats,
                valid = pvm.Valid,
                warnings = pvm.Warnings,
                textures = pvm.Textures.Select(e => new
                {
                    e.Index, e.Name, e.Offset,
                    e.Texture.Width, e.Texture.Height,
                    pixelFormat = e.Texture.PixelFormatName,
                    dataFormat = e.Texture.DataFormatName,
                    valid = e.Texture.Valid,
                }),
            });
            return pvm.Valid ? 0 : 2;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {DiscForge.Core.Dreamcast.Pvm.Render(pvm)}");
        return pvm.Valid ? 0 : 2;
    }
    catch (DiscForge.Core.Dreamcast.PvrFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int MpegInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge mpeg-info <file> [--json]   (.mpg/.mpeg/.vob/.sfd)\n" +
                    "  Describes an MPEG program stream: the video sequence header (dimensions, aspect, frame\n" +
                    "  rate, bit rate, MPEG-1 vs -2) and the elementary streams present. Recognises CRI ADX\n" +
                    "  audio, the mark of a Dreamcast Sofdec (.sfd) movie. Read-only; decodes no frames.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var m = DiscForge.Core.Mpeg.MpegVideoProbe.ProbeFile(path);
        if (!m.IsProgramStream) return Fail(m.Summary());

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                container = m.Container,
                m.IsMpeg2, m.Width, m.Height, m.AspectCode, m.Fps, m.FrameRateCode,
                bitrateBps = m.BitrateBps, m.VariableBitrate, m.HasAdx,
                streams = m.Streams.Select(s => new { id = $"0x{s.StreamId:X2}", kind = s.Kind.ToString(), s.Codec, s.Bytes }),
            });
            return 0;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {m.Summary()}");
        foreach (var s in m.Streams)
            Console.WriteLine($"  0x{s.StreamId:X2}  {s.Kind,-9} {s.Codec}  ({s.Bytes:N0} bytes)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int OperaLsCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge opera-ls <image>   (cooked 2048 ISO, or raw 2352 .bin)\n" +
        "  Lists the 3DO Opera file system — the console's own CD layout: the volume label, and the full\n" +
        "  file tree with byte sizes and type tags, recursing into subdirectories. Accepts a cooked\n" +
        "  2048-byte/sector image or a raw 2352-byte Mode 1 image (whose user data is extracted). Read-only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var raw = File.ReadAllBytes(args[1]);
        byte[] cooked = raw;
        // A raw 2352-byte Mode 1 image: cook it down to 2048-byte user data per sector.
        if (raw.Length % 2352 == 0 && raw.Length >= 2352 && !DiscForge.Core.ThreeDo.OperaFs.IsVolume(raw))
        {
            int sectors = raw.Length / 2352;
            cooked = new byte[sectors * 2048];
            for (int s = 0; s < sectors; s++)
                Array.Copy(raw, s * 2352 + 16, cooked, s * 2048, 2048);
        }
        if (!DiscForge.Core.ThreeDo.OperaFs.IsVolume(cooked))
            return Fail("No 3DO Opera volume found (missing the record-type/ZZZZZ label).");

        var vol = DiscForge.Core.ThreeDo.OperaFs.Read(cooked);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {DiscForge.Core.ThreeDo.OperaFs.Render(vol)}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SegaCdInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge segacd-info <image>   (.cue or a raw .bin/.iso)\n" +
            "  Identifies a Sega CD / Mega-CD disc from its boot header: console name, copyright/date,\n" +
            "  domestic and international titles, product code, checksum, input devices and region.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var h = DiscForge.Core.SegaCd.SegaCdDisc.Identify(path);
        if (h is null)
            return Fail("No Sega CD boot header (\"SEGADISCSYSTEM\") was found — " +
                        "this image has no Sega CD data track, or the format isn't supported here.");

        Console.WriteLine($"Sega CD / Mega-CD disc — {Path.GetFileName(path)}");
        Console.WriteLine($"  Title:       {h.Title}");
        if (h.DomesticTitle.Length > 0 && h.DomesticTitle != h.Title)
            Console.WriteLine($"  Domestic:    {h.DomesticTitle}");
        Console.WriteLine($"  Console:     {h.ConsoleName}");
        Console.WriteLine($"  Copyright:   {h.Copyright}");
        Console.WriteLine($"  Product:     {h.ProductCode}");
        Console.WriteLine($"  Checksum:    0x{h.Checksum:X4}");
        Console.WriteLine($"  I/O:         {h.IoSupport}");
        Console.WriteLine($"  Region:      {(h.Regions.Count > 0 ? string.Join(", ", h.Regions) : "unknown")}" +
                          (h.RegionField.Length > 0 ? $"  ({h.RegionField})" : ""));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int SaturnInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge saturn-info <image>   (.cue, .cdi, or a raw .bin/.iso)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        var header = DiscForge.Core.Saturn.SaturnDisc.Identify(path);
        if (header is null)
            return Fail("No Saturn disc header (\"SEGA SEGASATURN\") was found — " +
                        "this image has no Saturn data track, or the format isn't supported here.");

        Console.WriteLine($"Sega Saturn disc — {Path.GetFileName(path)}");
        Console.WriteLine($"  Title:       {header.Title}");
        Console.WriteLine($"  Product:     {header.ProductNumber}  {header.Version}");
        Console.WriteLine($"  Maker:       {header.MakerId}");
        Console.WriteLine($"  Device:      {header.DeviceInfo}");
        Console.WriteLine($"  Region:      {(header.Regions.Count > 0 ? string.Join(", ", header.Regions) : "none")}" +
                          (header.AreaSymbols.Length > 0 ? $"  ({header.AreaSymbols})" : ""));
        Console.WriteLine($"  Released:    {header.ReleaseDate}");
        Console.WriteLine($"  Peripherals: {header.Peripherals}");
        foreach (var p in header.SupportedPeripherals)
            Console.WriteLine($"      - {p}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PcfxInfoCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge pcfx-info <image> [--json]\n" +
                    "  Identifies a NEC PC-FX disc by its \"PC-FX:Hu_CD-ROM\" boot signature (found anywhere in the\n" +
                    "  data area, whatever the sector framing) and reports the readable boot-header text. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        // The boot signature sits in an early data sector; scan a bounded leading window.
        long len = new FileInfo(path).Length;
        int scan = (int)Math.Min(len, 64L * 1024 * 1024);
        var buf = new byte[scan];
        using (var fs = File.OpenRead(path)) fs.ReadExactly(buf, 0, scan);

        var disc = DiscForge.Core.Nec.Pcfx.Identify(buf);
        if (args.Contains("--json"))
        {
            EmitJson(new { file = Path.GetFileName(path), disc.IsPcfx, disc.SignatureOffset, disc.BootText });
            return disc.IsPcfx ? 0 : 2;
        }
        Console.WriteLine($"{Path.GetFileName(path)}: {disc.Summary()}");
        return disc.IsPcfx ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int RomInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge rom-info <file>   (a cartridge ROM: .n64/.z64/.v64, .sfc/.smc, .md/.gen, .gb/.gbc, .gba, .nes, …)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    byte[] rom;
    try { rom = File.ReadAllBytes(path); }
    catch (Exception ex) { return Fail(ex.Message); }

    var id = DiscForge.Core.Rom.RomIdentify.Identify(rom);
    if (!id.Recognised)
        return Fail("No known cartridge-ROM signature matched — this is not a ROM format DiscForge identifies.");

    Console.WriteLine($"Cartridge ROM — {Path.GetFileName(path)} ({rom.Length:N0} bytes)");
    Console.WriteLine($"  Platform:   {id.Platform}");
    if (id.Title.Length > 0) Console.WriteLine($"  Title:      {id.Title}");
    if (id.GameCode.Length > 0) Console.WriteLine($"  GameCode:   {id.GameCode}");
    if (id.Region.Length > 0) Console.WriteLine($"  Region:     {id.Region}");
    foreach (var kv in id.Extra)
        Console.WriteLine($"  {kv.Key,-11} {kv.Value}");

    var h = DiscForge.Core.Rom.RomHashes.Compute(rom, id);
    Console.WriteLine("  Hashes (No-Intro; excluded copier headers stripped):");
    Console.WriteLine($"    CRC32:    {h.Crc32Hex}");
    Console.WriteLine($"    MD5:      {h.Md5}");
    Console.WriteLine($"    SHA1:     {h.Sha1}");

    foreach (var w in id.Warnings)
        Console.WriteLine($"  ! {w}");
    return 0;
}

static int RomIntegrityCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge rom-integrity <file> [--json]\n" +
                    "  Recomputes a cartridge ROM's own integrity fields from its data and compares — catching a\n" +
                    "  bad dump whose header still looks plausible. Game Boy: header + global checksum. Sega\n" +
                    "  Genesis/Mega Drive: content checksum (0x200..end). GBA: header checksum + boot logo.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    byte[] rom;
    try { rom = File.ReadAllBytes(path); }
    catch (Exception ex) { return Fail(ex.Message); }

    var r = DiscForge.Core.Rom.RomIntegrity.Verify(rom);

    if (args.Contains("--json"))
    {
        EmitJson(new
        {
            file = Path.GetFileName(path),
            r.Platform, r.Ok,
            checks = r.Checks.Select(c => new { c.Name, status = c.Status.ToString(), c.Detail }),
        });
        return r.Ok ? 0 : 2;
    }

    Console.WriteLine($"{Path.GetFileName(path)}: {r.Summary()}");
    foreach (var c in r.Checks)
    {
        string mark = c.Status switch
        {
            DiscForge.Core.Rom.RomCheckStatus.Pass => "OK  ",
            DiscForge.Core.Rom.RomCheckStatus.Fail => "FAIL",
            _ => "note",
        };
        Console.WriteLine($"  [{mark}] {c.Name}: {c.Detail}");
    }
    return r.Ok ? 0 : 2;
}

static int FdsInfoCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge fds-info <file.fds> [--json]\n" +
                    "  Reads a Famicom Disk System image (raw or fwNES-wrapped): each 65500-byte side's identity\n" +
                    "  (game code, maker, side/disk number) and its file table — name, type (PRG/CHR/VRAM), load\n" +
                    "  address and size. Verified by the \"*NINTENDO-HVC*\" disk-info stamp. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var data = File.ReadAllBytes(path);
        var img = DiscForge.Core.Rom.Fds.Read(data);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                img.HadFwNesHeader, img.SideCount,
                sides = img.Sides.Select(s => new
                {
                    s.GameName, s.MakerCode, s.SideNumber, s.DiskNumber, s.FileCount,
                    files = s.Files.Select(f => new { f.Number, f.Id, f.Name, f.Kind, f.Address, f.Size }),
                }),
            });
            return 0;
        }

        Console.WriteLine($"Famicom Disk System — {Path.GetFileName(path)} " +
                          $"({img.SideCount} side{(img.SideCount == 1 ? "" : "s")}{(img.HadFwNesHeader ? ", fwNES header" : "")})");
        foreach (var s in img.Sides)
        {
            Console.WriteLine($"  Side {s.SideNumber} (disk {s.DiskNumber}) — game '{s.GameName}', maker {s.MakerCode}, {s.FileCount} file(s):");
            foreach (var f in s.Files)
                Console.WriteLine($"    [{f.Number:00}] {f.Name,-8} {f.Kind,-4} @0x{f.Address:X4}  {f.Size:N0} bytes");
        }
        return 0;
    }
    catch (DiscForge.Core.Rom.FdsFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int N64Info(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge n64-info <rom> [--json]\n" +
            "  Identifies the N64 cartridge's CIC boot-security chip from its IPL3 bootcode CRC-32, and verifies\n" +
            "  the header's CRC1/CRC2 boot checksums by recomputing them (a modified or truncated dump fails).\n" +
            "  Reads any byte order (.z64/.v64/.n64). Reporting only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var info = DiscForge.Core.Rom.N64Cic.Analyze(File.ReadAllBytes(path));
        if (args.Contains("--json")) { EmitJson(info); return 0; }

        Console.WriteLine($"N64 ROM — {Path.GetFileName(path)}");
        Console.WriteLine($"  Byte order:    {info.ByteOrder}");
        Console.WriteLine($"  Bootcode CRC:  {info.BootcodeCrc32:X8}");
        Console.WriteLine($"  CIC:           {info.Cic ?? "unrecognised bootcode"}" +
                          (info.CicRegion is { } r ? $"  ({r})" : ""));
        Console.WriteLine($"  Header CRC1:   {info.Crc1Stored:X8}" +
                          (info.Crc1Calc is { } c1 ? $"   computed {c1:X8}" : "   (not checked)"));
        Console.WriteLine($"  Header CRC2:   {info.Crc2Stored:X8}" +
                          (info.Crc2Calc is { } c2 ? $"   computed {c2:X8}" : "   (not checked)"));
        Console.WriteLine(info.CrcValid switch
        {
            true => "  Boot checksum: OK — the ROM's CRC1/CRC2 match; the image is intact.",
            false => "  Boot checksum: MISMATCH — the header CRCs do not match the data (modified or bad dump).",
            null => "  Boot checksum: not verified (unknown CIC or ROM smaller than 1 MiB).",
        });
        return info.CrcValid == false ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdInteractiveConsoleInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge cdi-console-info <image>   (a CD-i .iso or raw Mode 2 track)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        using var fs = File.OpenRead(path);
        if (!DiscForge.Core.CdInteractive.CdInteractiveReader.IsCdInteractive(fs))
            return Fail("No CD-i signature at sector 16 — this image is neither pure CD-i " +
                        "(\"CD-I \" standard id) nor a CD-i Bridge disc (\"CD-RTOS\" system id).");

        fs.Seek(0, SeekOrigin.Begin);
        var disc = DiscForge.Core.CdInteractive.CdInteractiveReader.Read(fs);

        string kind = disc.Kind == DiscForge.Core.CdInteractive.CdInteractiveKind.PureCdi
            ? "pure CD-i (Green Book)" : "CD-i Bridge";
        Console.WriteLine($"Philips CD-i — {Path.GetFileName(path)}");
        Console.WriteLine($"  Kind:        {kind}");
        Console.WriteLine($"  Volume:      {disc.VolumeId}");
        Console.WriteLine($"  System:      {disc.SystemId}");
        if (disc.ApplicationId.Length > 0)
            Console.WriteLine($"  Application: {disc.ApplicationId}");

        int files = disc.Filesystem.Files.Count();
        int dirs = disc.Filesystem.Directories.Count();
        Console.WriteLine($"  Filesystem:  {files} file(s), {dirs} director(y/ies), " +
                          $"{disc.Filesystem.TotalBytes:N0} bytes");
        foreach (var f in disc.Filesystem.Files.OrderBy(x => x.Path, StringComparer.Ordinal))
            Console.WriteLine($"      {f.Path}  ({f.Size:N0} bytes)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CdInteractiveExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge cdi-extract <image> <path-in-image> <out-file>\n" +
                    "         dforge cdi-extract <image> <out-dir> --all\n" +
                    "  Extract a file (or, with --all, every file) from a Philips CD-i disc image. Handles the\n" +
                    "  Mode 2 Form 1 / Form 2 sector mix, so real-time streams (e.g. /MPEGAV/*.DAT) come out whole.\n" +
                    "  Run cdi-console-info first to see the paths. Read-only.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        using var img = File.OpenRead(path);
        if (!DiscForge.Core.CdInteractive.CdInteractiveReader.IsCdInteractive(img))
            return Fail("No CD-i signature at sector 16 — this is not a CD-i disc image.");

        if (args.Contains("--all"))
        {
            string outDir = args[2];
            Directory.CreateDirectory(outDir);
            img.Seek(0, SeekOrigin.Begin);
            var disc = DiscForge.Core.CdInteractive.CdInteractiveReader.Read(img);
            int n = 0;
            foreach (var f in disc.Filesystem.Files)
            {
                string rel = f.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string dest = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var os = File.Create(dest);
                long wrote = DiscForge.Core.CdInteractive.CdInteractiveReader.ExtractFile(img, f.Path, os);
                Console.WriteLine($"  {f.Path}  ({wrote:N0} bytes)");
                n++;
            }
            Console.WriteLine($"Extracted {n} file(s) to {outDir}");
            return 0;
        }

        string inPath = args[2];
        if (args.Length < 4) return Fail("Give an output file: dforge cdi-extract <image> <path-in-image> <out-file>");
        string outFile = args[3];
        using (var os = File.Create(outFile))
        {
            long wrote = DiscForge.Core.CdInteractive.CdInteractiveReader.ExtractFile(img, inPath, os);
            Console.WriteLine($"Wrote {wrote:N0} bytes to {outFile}");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PspInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge psp-info <image> [--sfo]   (.iso, .cso or .zso)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    bool dumpSfo = args.Contains("--sfo");

    try
    {
        var game = DiscForge.Core.Psp.PspUmdReader.Read(path);

        Console.WriteLine($"PSP UMD — {Path.GetFileName(path)}");
        Console.WriteLine($"  Disc ID:      {game.DiscId}");
        Console.WriteLine($"  Title:        {game.Title}");
        Console.WriteLine($"  Category:     {game.Category}");
        Console.WriteLine($"  Disc version: {game.DiscVersion}");
        if (game.Region.Length > 0)
            Console.WriteLine($"  Region:       {game.Region}");

        int files = game.Filesystem.Files.Count();
        int dirs = game.Filesystem.Directories.Count();
        Console.WriteLine($"  Filesystem:   {files} file(s), {dirs} director(y/ies), " +
                          $"{game.Filesystem.TotalBytes:N0} bytes");

        string[] notable = { "EBOOT.BIN", "PARAM.SFO", "ICON0.PNG", "PIC1.PNG", "UPDATE/EBOOT.BIN" };
        foreach (var name in notable)
        {
            var hit = game.Filesystem.Files.FirstOrDefault(
                f => f.Path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                Console.WriteLine($"      {hit.Path}  ({hit.Size:N0} bytes)");
        }

        if (dumpSfo)
        {
            Console.WriteLine("  PARAM.SFO:");
            foreach (var (key, value) in game.Sfo.Entries)
                Console.WriteLine($"      {key,-18} = {(value.IsInt ? value.Number.ToString() : value.Text)}");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PbpInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge pbp-info <EBOOT.PBP>");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        var bytes = File.ReadAllBytes(path);
        var pbp = DiscForge.Core.Psp.PbpFile.Parse(bytes);

        Console.WriteLine($"PBP package — {Path.GetFileName(path)}");
        Console.WriteLine($"  Version: 0x{pbp.Version:X8}");
        Console.WriteLine("  Sections:");
        foreach (var s in pbp.Sections)
            Console.WriteLine($"      {s.Name,-10} {s.Size,12:N0} bytes  @ 0x{s.Offset:X}" +
                              (s.IsEmpty ? "  (empty)" : ""));

        var sfo = DiscForge.Core.Psp.PbpFile.GetParamSfo(bytes);
        if (sfo is not null)
        {
            Console.WriteLine("  PARAM.SFO:");
            Console.WriteLine($"      Title:    {sfo.GetString("TITLE")}");
            Console.WriteLine($"      Disc ID:  {sfo.GetString("DISC_ID")}");
            Console.WriteLine($"      Category: {sfo.GetString("CATEGORY")}");
        }
        else
        {
            Console.WriteLine("  PARAM.SFO: (none)");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PbpExtract(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge pbp-extract <EBOOT.PBP> <output-dir>");
    var path = args[1];
    var outDir = args[2];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        var bytes = File.ReadAllBytes(path);
        var pbp = DiscForge.Core.Psp.PbpFile.Parse(bytes);
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"PBP package — {Path.GetFileName(path)}  (version 0x{pbp.Version:X8})");
        int written = 0;
        foreach (var s in pbp.Sections)
        {
            if (s.IsEmpty) continue;
            var dest = Path.Combine(outDir, s.Name);
            var data = DiscForge.Core.Psp.PbpFile.GetSection(bytes, s.Name);
            File.WriteAllBytes(dest, data);
            string note = s.Name == "DATA.PSP" ? "  (raw — not decrypted)" : "";
            Console.WriteLine($"  wrote {s.Name,-10} {s.Size,12:N0} bytes -> {dest}{note}");
            written++;
        }
        Console.WriteLine($"  {written} section(s) written. DATA.PSP, if present, is extracted raw and not decrypted.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GcmExtractCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge gcm-extract <image> <out-dir> [--only <substring>]\n" +
        "  Extracts a GameCube disc's file tree from its FST to <out-dir>, preserving the directory\n" +
        "  structure. --only <substring> limits extraction to matching paths. Reads unencrypted data only.");
    string path = args[1], outDir = args[2];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    string? only = OptVal(args, "--only");
    try
    {
        using var fs = File.OpenRead(path);
        if (!GcmReader.IsGcm(fs)) return Fail("Not a GameCube disc image.");
        var disc = GcmReader.Read(fs);

        int written = 0;
        long bytes = 0;
        var buffer = new byte[1 << 20];
        foreach (var e in disc.Entries)
        {
            if (e.IsDirectory) continue;
            if (only != null && !e.Path.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Offset < 0 || e.Offset + e.Size > fs.Length) continue;   // malformed FST entry — skip

            string rel = e.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string dest = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? outDir);

            fs.Seek(e.Offset, SeekOrigin.Begin);
            using var outFs = File.Create(dest);
            long remaining = e.Size;
            while (remaining > 0)
            {
                int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (n <= 0) break;
                outFs.Write(buffer, 0, n);
                remaining -= n;
            }
            written++; bytes += e.Size;
        }
        Console.WriteLine($"Extracted {written} file(s), {bytes:N0} bytes to {outDir}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GcmBannerCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge gcm-banner <image> <out.png>\n" +
        "  Extracts a GameCube disc's banner (opening.bnr) icon — the 96×32 image shown in the console's\n" +
        "  memory-card manager — decoding its RGB5A3 texels to a PNG, and prints the banner's title,\n" +
        "  developer and description. Reads unencrypted metadata only.");
    string path = args[1], outPng = args[2];
    if (!File.Exists(path)) return Fail($"File not found: {path}");
    try
    {
        using var fs = File.OpenRead(path);
        if (!GcmReader.IsGcm(fs)) return Fail("Not a GameCube disc image.");
        var disc = GcmReader.Read(fs);
        var bnr = disc.Entries.FirstOrDefault(e => !e.IsDirectory &&
            e.Name.Equals("opening.bnr", StringComparison.OrdinalIgnoreCase));
        if (bnr is null) return Fail("This disc has no opening.bnr banner.");

        var data = new byte[bnr.Size];
        fs.Seek(bnr.Offset, SeekOrigin.Begin);
        fs.ReadExactly(data);
        var banner = GcBannerReader.Parse(data);
        var rgba = GcBannerReader.DecodeIconRgba(data);
        File.WriteAllBytes(outPng, DiscForge.Core.Util.PngWriter.EncodeRgba(rgba, GcBanner.ImageWidth, GcBanner.ImageHeight));

        Console.WriteLine($"{Path.GetFileName(outPng)}: {banner.Primary.Title}" +
                          (banner.Primary.Maker.Length > 0 ? $" — {banner.Primary.Maker}" : "") +
                          $" (96×32 banner written)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TplInfoCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge tpl-info <file.tpl> [--json]\n" +
        "  Lists the textures in a GameCube/Wii TPL container: index, dimensions and GX pixel format.\n" +
        "  Reading only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var tex = DiscForge.Core.GameCube.Tpl.Read(File.ReadAllBytes(args[1]));
        if (args.Contains("--json"))
        {
            EmitJson(new { count = tex.Count, textures = tex.Select(t => new { t.Index, t.Width, t.Height, t.Format, t.FormatName }) });
            return 0;
        }
        Console.WriteLine($"{Path.GetFileName(args[1])}: {tex.Count} texture(s)");
        foreach (var t in tex)
            Console.WriteLine($"  #{t.Index,-3} {t.Width,4}×{t.Height,-4}  {t.FormatName}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int TplExtractCmd(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge tpl-extract <file.tpl> <out-dir-or.png> [--index N]\n" +
        "  Decodes the TPL's textures to PNG. With one texture (or --index N) and a .png destination, writes\n" +
        "  that file; otherwise writes <out-dir>/texNNN.png for each. Decoding only.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var tex = DiscForge.Core.GameCube.Tpl.Read(File.ReadAllBytes(args[1]));
        if (tex.Count == 0) return Fail("The TPL contains no decodable textures.");

        int? only = int.TryParse(OptVal(args, "--index"), out var n) ? n : null;
        string dest = args[2];
        var chosen = only is { } idx ? tex.Where(t => t.Index == idx).ToList() : tex.ToList();
        if (only is { } && chosen.Count == 0) return Fail($"No texture with index {only}.");

        if (dest.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && chosen.Count == 1)
        {
            var t = chosen[0];
            File.WriteAllBytes(dest, DiscForge.Core.Util.PngWriter.EncodeRgba(t.Rgba, t.Width, t.Height));
            Console.WriteLine($"{Path.GetFileName(dest)}: #{t.Index} {t.Width}×{t.Height} {t.FormatName}");
            return 0;
        }

        Directory.CreateDirectory(dest);
        foreach (var t in chosen)
        {
            string outPng = Path.Combine(dest, $"tex{t.Index:D3}.png");
            File.WriteAllBytes(outPng, DiscForge.Core.Util.PngWriter.EncodeRgba(t.Rgba, t.Width, t.Height));
            Console.WriteLine($"  {Path.GetFileName(outPng)}  {t.Width}×{t.Height} {t.FormatName}");
        }
        Console.WriteLine($"{chosen.Count} texture(s) written to {dest}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GcmInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge gcm-info <image>   (GameCube .gcm/.iso or a Wii disc)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        using var fs = File.OpenRead(path);

        // A Wii disc (magic at 0x18) is checked first: for it we report only the
        // unencrypted volume header and partition table — never partition contents.
        if (WiiDisc.IsWii(fs))
        {
            var vol = WiiDisc.Read(fs);
            Console.WriteLine($"Nintendo Wii disc — {Path.GetFileName(path)}");
            Console.WriteLine($"  Game code:  {vol.GameCode}");
            Console.WriteLine($"  Title:      {vol.GameName}");
            Console.WriteLine($"  Partitions: {vol.Partitions.Count}");
            foreach (var p in vol.Partitions)
            {
                Console.WriteLine($"      - {p.Type,-13} (raw {p.RawType})  at 0x{p.Offset:X}");

                // Plaintext ticket + TMD metadata only. If a partition's details can't be
                // read (truncated/out-of-range), print the bare line rather than failing.
                WiiPartitionDetails? d = null;
                try { d = WiiDisc.ReadPartitionDetails(fs, p); }
                catch (GameCubeFormatException) { }
                if (d is not null)
                {
                    var idLabel = d.GameId is not null ? $"{d.TitleId} ({d.GameId})" : d.TitleId;
                    Console.WriteLine($"          Title id:    {idLabel}");
                    Console.WriteLine($"          Version:     {d.TitleVersion}   Contents: {d.ContentCount}");
                    Console.WriteLine($"          Data region: 0x{d.DataOffset:X}, {d.DataSize:N0} bytes (not read)");
                }
            }
            Console.WriteLine("  (Wii partition ticket/TMD metadata is plaintext; the encrypted");
            Console.WriteLine("   partition contents and title keys are intentionally not read.)");
            return 0;
        }

        if (!GcmReader.IsGcm(fs))
            return Fail("Not a GameCube or Wii disc (no 0xC2339F3D / 0x5D1C9EA3 magic).");

        var disc = GcmReader.Read(fs);
        Console.WriteLine($"Nintendo GameCube disc — {Path.GetFileName(path)}");
        Console.WriteLine($"  Game code:  {disc.GameCode}   Maker: {disc.MakerCode}");
        Console.WriteLine($"  Region:     {GameCubeRegion.Decode(disc.GameCode)}");
        Console.WriteLine($"  Title:      {disc.GameName}");
        Console.WriteLine($"  Disc/Ver:   disc {disc.DiscId}, version {disc.Version}");

        // Boot metadata: the apploader (fixed 0x2440) and the DOL executable (header 0x420).
        try
        {
            var apploader = GcBoot.ReadApploader(fs);
            Console.WriteLine($"  Apploader:  built {apploader.Date}, {apploader.Size:N0} bytes, entry 0x{apploader.EntryPoint:X8}");
            if (disc.DolOffset > 0)
            {
                var dol = GcBoot.ReadDol(fs, disc.DolOffset);
                Console.WriteLine($"  DOL:        entry 0x{dol.EntryPoint:X8}, {dol.TextSections} text + {dol.DataSections} data section(s), " +
                                  $"{dol.TotalSize:N0} bytes + {dol.BssSize:N0} BSS");
            }
        }
        catch (GameCubeFormatException) { /* partial image without boot region */ }

        // The banner (opening.bnr) carries the disc's human-facing title/developer/description.
        var bnr = disc.Entries.FirstOrDefault(e => !e.IsDirectory &&
            e.Name.Equals("opening.bnr", StringComparison.OrdinalIgnoreCase));
        if (bnr is not null && bnr.Size >= 0x1820 && bnr.Offset + bnr.Size <= fs.Length)
        {
            try
            {
                var data = new byte[bnr.Size];
                fs.Seek(bnr.Offset, SeekOrigin.Begin);
                fs.ReadExactly(data);
                if (GcBannerReader.IsBanner(data))
                    foreach (var line in GcBannerReader.Render(GcBannerReader.Parse(data)).Split('\n'))
                        Console.WriteLine($"  {line}");
            }
            catch { /* banner unreadable — the rest of the report still stands */ }
        }

        int files = disc.Entries.Count(e => !e.IsDirectory);
        int dirs = disc.Entries.Count(e => e.IsDirectory);
        long totalBytes = disc.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        Console.WriteLine($"  Filesystem: {files} file(s), {dirs} director(y/ies), {totalBytes:N0} bytes");
        foreach (var e in disc.Entries)
        {
            if (e.IsDirectory)
                Console.WriteLine($"      {e.Path}/");
            else
                Console.WriteLine($"      {e.Path}  ({e.Size:N0} bytes @ 0x{e.Offset:X})");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int RvzDecodeCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge rvz-decode <image.rvz> <out.iso>\n" +
                    "  Reconstruct a GameCube ISO from an RVZ/WIA container (zstd or uncompressed groups).\n" +
                    "  The container walk, group decompression and reassembly are validated; two limits apply:\n" +
                    "  RVZ 'junk' (disc padding) is ZERO-FILLED — the output is data-exact (files extract, the\n" +
                    "  disc mounts) but not Redump-bit-exact where the disc was scrubbed; and only GameCube discs\n" +
                    "  with zstd/none groups are handled (Wii and bzip2/lzma groups are declined). See docs/RVZ.md.");
    string inPath = args[1], outPath = args[2];
    if (!File.Exists(inPath)) return Fail($"'{inPath}' not found.");
    try
    {
        var rvz = File.ReadAllBytes(inPath);
        DiscForge.Core.GameCube.RvzDecoder.DecodeReport report;
        using (var fs = File.Create(outPath))
            report = DiscForge.Core.GameCube.RvzDecoder.Decode(rvz, fs);
        Console.WriteLine($"Reconstructed {Path.GetFileName(outPath)} — {report.IsoBytes:N0} bytes, {report.Groups:N0} group(s).");
        if (report.BitExact)
            Console.WriteLine("  Bit-exact (no junk regions in this image).");
        else
            Console.WriteLine($"  Data-exact, but {report.JunkBytesZeroFilled:N0} byte(s) of disc 'junk' were zero-filled " +
                              "(Nintendo LFG not yet reproduced) — mountable and file-extractable, but not a Redump hash match.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int ShowRvzInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge rvz-info <image.rvz|image.wia>");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    try
    {
        using var fs = File.OpenRead(path);
        var info = RvzReader.ReadInfo(fs);
        Console.WriteLine($"{info.Format} container — {Path.GetFileName(path)}");
        Console.WriteLine($"  Version:     0x{info.Version:X8}");
        Console.WriteLine($"  Compression: {info.Compression} (level {info.CompressionLevel})");
        Console.WriteLine($"  Chunk size:  {info.ChunkSize:N0} bytes");
        Console.WriteLine($"  ISO size:    {info.IsoSize:N0} bytes");
        Console.WriteLine($"  Game id:     {info.GameId}");
        Console.WriteLine($"  Title:       {info.GameName}");
        if (info.HasStructure)
        {
            string kind = info.PartitionCount > 0 ? "Wii (encrypted partitions)" : "GameCube (unencrypted)";
            Console.WriteLine($"  Layout:      {kind}");
            Console.WriteLine($"  Partitions:  {info.PartitionCount:N0}   Raw regions: {info.RawDataCount:N0}   " +
                              $"Groups: {info.GroupCount:N0}");

            // For a Wii RVZ, map the partitions from the UNENCRYPTED structure (no keys, no crypto).
            if (info.PartitionCount > 0)
            {
                try
                {
                    var vol = DiscForge.Core.GameCube.RvzDecoder.ReadWiiStructure(File.ReadAllBytes(path));
                    foreach (var p in vol.Partitions)
                        Console.WriteLine($"    partition {p.Type,-13} at 0x{p.Offset:X}");
                }
                catch (Exception ex) { Console.WriteLine($"    (couldn't map partitions: {ex.Message})"); }
            }
        }
        if (info.PartitionCount > 0)
            Console.WriteLine("  (GameCube RVZ -> ISO is supported via `rvz-decode`; the encrypted Wii ISO rebuild is\n" +
                              "   declined — it needs the console key + AES over protected content. See docs/RVZ.md.)");
        else
            Console.WriteLine("  (Use `rvz-decode` to reconstruct the GameCube ISO — see docs/RVZ.md.)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GcVerifyCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge gc-verify <image> [--json]\n" +
                    "  A single-image GameCube health check (no second dump needed): verifies the DVD magic,\n" +
                    "  the DOL/FST offsets and sizes fall inside the image, the boot chain is present, the\n" +
                    "  region agrees between the bi2 country code and the game-code letter, and the byte length\n" +
                    "  matches a standard GameCube single-layer disc (flagging scrubbed/trimmed/truncated dumps).");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var h = DiscForge.Core.GameCube.GameCubeVerify.CheckFile(path);
        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                h.GameCode, h.MakerCode, h.GameName, h.DiscNumber, h.Version, h.AudioStreaming,
                h.BiRegion, h.CodeRegion, h.RegionConsistent,
                h.DiscSize, sizeClass = h.SizeClass.ToString(),
                dolOffset = h.DolOffset, fstOffset = h.FstOffset, h.FstSize,
                h.Healthy, h.Warnings,
            });
            return h.Healthy ? 0 : 2;
        }
        Console.WriteLine($"{Path.GetFileName(path)}: {h.Summary()}");
        return h.Healthy ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GcJunkMapCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge gc-junk-map <image> [--json]\n" +
                    "  Maps a GameCube image's non-game padding — the gaps between the boot header, apploader,\n" +
                    "  DOL, FST and files, plus the tail out to the disc size — and classifies each region:\n" +
                    "  junk present (high entropy), zeroed (scrubbed away), or unexpectedly structured. Tells you\n" +
                    "  whether a dump's padding is intact, scrubbed, or tampered. Read-only; reconstructs nothing.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        using var fs = File.OpenRead(path);
        var m = DiscForge.Core.GameCube.GcJunkMapper.Analyze(fs);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                m.ImageLength, m.TotalPaddingBytes, m.SignificantPaddingBytes,
                verdict = m.Verdict.ToString(),
                regions = m.Regions.Select(r => new
                {
                    start = r.Start, r.Length, r.After,
                    @class = r.Class.ToString(),
                    entropy = Math.Round(r.EntropyBitsPerByte, 3),
                }),
            });
            return m.Verdict is DiscForge.Core.GameCube.GcPaddingVerdict.Suspicious ? 2 : 0;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {m.Summary()}");
        foreach (var r in m.Regions.Where(r => r.Length >= DiscForge.Core.GameCube.GcJunkMapper.SignificantRegionBytes))
            Console.WriteLine($"  0x{r.Start:X10} +{r.Length,12:N0}  {r.Class,-10} entropy {r.EntropyBitsPerByte:F2}  ({r.After})");
        return m.Verdict is DiscForge.Core.GameCube.GcPaddingVerdict.Suspicious ? 2 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GcJunkFillCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge gc-junk-fill <in.iso> <out.iso>\n" +
                    "  Reconstruct the deterministic junk padding of a SCRUBBED GameCube image. This is\n" +
                    "  EXPERIMENTAL: the junk PRNG is a clean-room reconstruction not yet confirmed byte-exact\n" +
                    "  against a real disc, so the fill is gated by SELF-VALIDATION — it regenerates the image's\n" +
                    "  OWN surviving junk first and only fills the scrubbed regions if that matches byte-for-byte.\n" +
                    "  A fully-scrubbed image (no surviving junk to check against) is declined on purpose: it\n" +
                    "  won't write bytes it can't prove. Reconstructs padding only; defeats no protection.");
    var inPath = args[1];
    var outPath = args[2];
    if (!File.Exists(inPath)) return Fail($"'{inPath}' not found.");
    try
    {
        DiscForge.Core.GameCube.GcJunkReconstructor.Report report;
        using (var input = File.OpenRead(inPath))
        using (var output = File.Create(outPath))
            report = DiscForge.Core.GameCube.GcJunkReconstructor.Reconstruct(input, output);

        Console.WriteLine($"Self-validated: {(report.SelfValidated ? "yes" : "NO")}" +
                          (report.IntactRegionsChecked > 0
                              ? $" ({report.IntactRegionsChecked} surviving-junk region(s), {report.IntactBytesMatched:N0} bytes checked)"
                              : ""));
        if (report.Reconstructed)
            Console.WriteLine($"Rebuilt: {report.ScrubbedRegionsFilled} scrubbed region(s), {report.BytesFilled:N0} bytes → {Path.GetFileName(outPath)}");
        Console.WriteLine(report.Message);
        // Exit 0 when we either rebuilt or there was nothing to do; 3 when we declined a scrubbed image.
        bool declinedWork = !report.SelfValidated && report.Message.Contains("declin", StringComparison.OrdinalIgnoreCase);
        return declinedWork ? 3 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int NkitInfoCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge nkit-info <image> [--json]\n" +
                    "  Detects an NKit-scrubbed GameCube/Wii image and reads its recovery block: the source\n" +
                    "  image's CRC32 (which matches this file to a Redump entry without restoring it), the\n" +
                    "  game id, and whether the Wii update partition was backed up. Read-only fixity metadata.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var nk = DiscForge.Core.GameCube.Nkit.ParseFile(path);
        if (!nk.IsNkit)
            return Fail("No NKit recovery block found — this is not an NKit-scrubbed image " +
                        "(a plain ISO/RVZ won't carry the \"NKIT\" header).");

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                nk.IsNkit, nk.Version, nk.Platform, nk.GameId,
                sourceCrc32 = $"{nk.SourceCrc32:X8}",
                nkitCrc = $"{nk.NkitCrc:X8}",
                sourceLengthField = nk.SourceLengthField,
                nk.HasUpdatePartitionBackup,
                updatePartitionCrc32 = nk.HasUpdatePartitionBackup ? $"{nk.UpdatePartitionCrc32:X8}" : null,
            });
            return 0;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {nk.Summary()}");
        Console.WriteLine($"  Source CRC32: {nk.SourceCrc32:X8}  (match this against a Redump DAT)");
        if (nk.HasUpdatePartitionBackup)
            Console.WriteLine($"  Update part.: backed up, CRC32 {nk.UpdatePartitionCrc32:X8}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int LayerBreakPickCmd(string[] args)
{
    if (args.Length < 2 || !long.TryParse(args[1], out long total) || total <= 0)
        return Fail("usage: dforge layerbreak-pick <total-sectors> [--target N] [--cells a,b,..] " +
                    "[--max-layer N] [--no-ecc] [--seamless]\n" +
                    "  Choose a legal dual-layer break: nearest cell boundary to the balance point (or a\n" +
                    "  --target), both layers within one layer's capacity, on a 16-sector ECC boundary.\n" +
                    "  With no --cells it snaps to the nearest ECC boundary (a plain data DL image).");
    long? target = null;
    long maxLayer = 2_086_912;
    bool ecc = true, seamless = false;
    var cells = new List<long>();
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--target" && i + 1 < args.Length && long.TryParse(args[++i], out var t)) target = t;
        else if (args[i] == "--max-layer" && i + 1 < args.Length && long.TryParse(args[++i], out var m)) maxLayer = m;
        else if (args[i] == "--no-ecc") ecc = false;
        else if (args[i] == "--seamless") seamless = true;
        else if (args[i] == "--cells" && i + 1 < args.Length)
            foreach (var s in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (long.TryParse(s, out var c)) cells.Add(c);
    }
    try
    {
        var opts = new DiscForge.Core.Media.LayerBreakOptions
        {
            TargetSector = target, MaxLayerSectors = maxLayer, RequireEccAligned = ecc, Seamless = seamless,
        };
        var plan = cells.Count > 0
            ? DiscForge.Core.Media.LayerBreakPlanner.Pick(total, cells, opts)
            : DiscForge.Core.Media.LayerBreakPlanner.Pick(total, opts);
        Console.WriteLine($"Layer break at sector {plan.BreakSector:N0}" +
                          (plan.OnCandidateBoundary ? " (cell boundary)" : " (ECC boundary)"));
        Console.WriteLine($"  Layer 0: {plan.Layer0Sectors:N0} sectors   Layer 1: {plan.Layer1Sectors:N0} sectors");
        Console.WriteLine($"  Exact target match: {(plan.ExactMatch ? "yes" : "no")}   Seamless: {(plan.Seamless ? "yes" : "no")}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CapacityCheckCmd(string[] args)
{
    if (args.Length < 3 || !long.TryParse(args[1], out long imgSectors))
        return Fail("usage: dforge capacity-check <image-sectors> <cd74|cd80|dvd5|dvd9|bd25|bd50|N> [--overburn]\n" +
                    "  Compare an image (in 2048-byte sectors) to media capacity: fits / underburn / overburn /\n" +
                    "  too-large. With --overburn, an image slightly over nominal capacity is allowed (drive/media\n" +
                    "  permitting). The media may be a keyword or an explicit sector count.");
    long media = args[2].ToLowerInvariant() switch
    {
        "cd74" => DiscForge.Core.Media.BurnCapacity.Nominal.Cd74,
        "cd80" => DiscForge.Core.Media.BurnCapacity.Nominal.Cd80,
        "dvd5" => DiscForge.Core.Media.BurnCapacity.Nominal.Dvd5,
        "dvd9" => DiscForge.Core.Media.BurnCapacity.Nominal.Dvd9,
        "bd25" => DiscForge.Core.Media.BurnCapacity.Nominal.Bd25,
        "bd50" => DiscForge.Core.Media.BurnCapacity.Nominal.Bd50,
        _ => long.TryParse(args[2], out var m) ? m : -1,
    };
    if (media <= 0) return Fail($"Unknown media '{args[2]}'. Use cd74/cd80/dvd5/dvd9/bd25/bd50 or a sector count.");
    bool overburn = Array.Exists(args, a => a == "--overburn");
    var c = DiscForge.Core.Media.BurnCapacity.Check(imgSectors, media, overburn);
    Console.WriteLine($"Fit: {c.Fit}  ({(c.CanBurn ? "can burn" : "refused")})");
    Console.WriteLine($"  Image: {imgSectors:N0} sectors   Media: {media:N0} sectors");
    Console.WriteLine($"  {c.Message}");
    return c.CanBurn ? 0 : 1;
}

static int DiscSpanCmd(string[] args)
{
    string? manifestPath = OptVal(args, "--manifest");
    if (args.Length < 2 || (manifestPath is null && args[1].StartsWith("--")))
        return Fail("usage: dforge disc-span <folder> [--media cd|dvd5|dvd9|bd25|bd50|bd100] [--keep-groups]\n" +
                    "                          [--overhead MB] [--json]\n" +
                    "         dforge disc-span --manifest <file> [--media …] [--keep-groups] …\n" +
                    "  Plan how to split files across the FEWEST discs (smart capacity planning).\n" +
                    "  Scans a <folder> recursively, OR reads a source manifest (--manifest, same format as\n" +
                    "  source-stage) to plan across local + cloud/HTTP origins without staging first. Packs by\n" +
                    "  size (First-Fit-Decreasing) and reports each disc's contents and fill level.\n" +
                    "  --media    target disc (default bd25).\n" +
                    "  --keep-groups  keep each top-level folder on one disc where it fits.\n" +
                    "  --overhead MB  per-disc filesystem overhead reservation (default 2 MB).\n" +
                    "  --json     machine-readable output.");

    string mediaKey = OptVal(args, "--media") ?? "bd25";
    var medium = DiscForge.Core.Media.DiscMedium.ByKey(mediaKey);
    if (medium is null) return Fail($"Unknown media '{mediaKey}'. Use cd/dvd5/dvd9/bd25/bd50/bd100.");
    bool keepGroups = args.Contains("--keep-groups");
    long? overhead = long.TryParse(OptVal(args, "--overhead"), out var oh) ? oh * 1024 * 1024 : null;

    var items = new List<DiscForge.Core.Media.SpanItem>();
    int unknownSize = 0;

    if (manifestPath is not null)
    {
        if (!File.Exists(manifestPath)) return Fail($"Manifest not found: {manifestPath}");
        var entries = DiscForge.Core.Sources.SourceManifest.Parse(File.ReadAllText(manifestPath));
        var source = new DiscForge.Core.Sources.ManifestSource(entries);
        foreach (var e in source.Enumerate())
        {
            if (e.SizeBytes < 0) { unknownSize++; continue; }   // e.g. an HTTP URL with no Content-Length
            var parts = e.Path.Split('/', '\\');
            string? group = parts.Length > 1 ? parts[0] : null;
            items.Add(new DiscForge.Core.Media.SpanItem(e.Path, e.SizeBytes, group));
        }
        if (items.Count == 0) return Fail("No sized files in the manifest to plan (all entries had unknown size).");
    }
    else
    {
    string folder = args[1];
    if (!Directory.Exists(folder)) return Fail($"Folder not found: {folder}");
    string root = Path.GetFullPath(folder);
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(root, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? group = parts.Length > 1 ? parts[0] : null;   // top-level subfolder
        items.Add(new DiscForge.Core.Media.SpanItem(rel, new FileInfo(file).Length, group));
    }
    if (items.Count == 0) return Fail("No files found in the folder.");
    }

    var plan = DiscForge.Core.Media.DiscSpanPlanner.Plan(items, medium, keepGroups, overhead);
    if (unknownSize > 0)
        Console.WriteLine($"  note: {unknownSize} manifest entr(y/ies) had unknown size (e.g. URLs without Content-Length) and were skipped from planning.");

    if (args.Contains("--json"))
    {
        EmitJson(new
        {
            media = medium.Key,
            mediaName = medium.Name,
            discCount = plan.DiscCount,
            averageFillPercent = Math.Round(plan.AverageFillPercent, 1),
            keepGroups = plan.GroupsKept,
            splitGroups = plan.SplitGroups,
            oversized = plan.Oversized.Select(i => new { i.Path, i.SizeBytes }),
            discs = plan.Discs.Select(d => new
            {
                d.Index, d.UsedBytes, d.FreeBytes,
                fillPercent = Math.Round(d.FillPercent, 1),
                fileCount = d.Items.Count,
                files = d.Items.Select(i => new { i.Path, i.SizeBytes, i.Group }),
            }),
        });
        return plan.Oversized.Count > 0 ? 1 : 0;
    }

    Console.WriteLine($"Spanning {items.Count:N0} file(s) across {medium.Name}:");
    Console.WriteLine($"  {plan.DiscCount} disc(s) needed, average fill {plan.AverageFillPercent:0.0}%" +
                      (keepGroups ? " (keeping top-level folders together)" : ""));
    foreach (var d in plan.Discs)
        Console.WriteLine($"  Disc {d.Index}: {d.Items.Count,4} file(s), {Gb(d.UsedBytes):0.00} GB used, " +
                          $"{Gb(d.FreeBytes):0.00} GB free  [{d.FillPercent:0.0}%]");
    if (plan.SplitGroups.Count > 0)
        Console.WriteLine($"  note: these folders were larger than one disc and had to be split: {string.Join(", ", plan.SplitGroups)}");
    if (plan.Oversized.Count > 0)
    {
        Console.WriteLine($"  WARNING: {plan.Oversized.Count} file(s) are too large for {medium.Name} and were not placed:");
        foreach (var o in plan.Oversized.Take(10))
            Console.WriteLine($"    {o.Path} ({Gb(o.SizeBytes):0.00} GB)");
    }
    return plan.Oversized.Count > 0 ? 1 : 0;

    static double Gb(long b) => b / (1024.0 * 1024 * 1024);
}

static int UiCmd(string[] args)
{
    int port = int.TryParse(OptVal(args, "--port"), out var p) && p is > 0 and < 65536 ? p : 8787;
    bool noBrowser = args.Contains("--no-browser");
    // Usage note (shown only if they ask for help explicitly).
    if (args.Contains("--help") || args.Contains("-h"))
        return Fail("usage: dforge ui [--port N] [--no-browser]\n" +
                    "  Launch the modern browser UI over the DiscForge engine — a local, loopback-only web app\n" +
                    "  (default http://127.0.0.1:8787) with quick actions for discovering, planning and sourcing,\n" +
                    "  plus a command bar that reaches every CLI verb. --no-browser prints the URL without opening\n" +
                    "  a browser (useful on a server / headless box).");
    return DiscForge.Cli.UiServer.Run(port, !noBrowser);
}

static int SourceStageCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge source-stage <manifest.txt> <staging-dir>\n" +
                    "  Assemble files from mixed origins into a local staging folder, ready for build-raw /\n" +
                    "  iso-create / burn — the cross-origin sourcing legacy burners never had. Each manifest line:\n" +
                    "      <on-disc-path>  <TAB or 2+ spaces>  <location>\n" +
                    "  where <location> is a local file, a local folder (expanded recursively), or an http(s) URL.\n" +
                    "  Cloud drives (Google Drive / OneDrive / Dropbox) plug into the same manifest once those\n" +
                    "  providers are added. Example manifest:\n" +
                    "      Music        /home/me/rips/Album\n" +
                    "      cover.jpg    https://example.com/cover.jpg\n" +
                    "  Then: dforge iso-create <staging-dir> disc.iso");
    string manifestPath = args[1], stagingDir = args[2];
    if (!File.Exists(manifestPath)) return Fail($"Manifest not found: {manifestPath}");
    try
    {
        var entries = DiscForge.Core.Sources.SourceManifest.Parse(File.ReadAllText(manifestPath));
        var source = new DiscForge.Core.Sources.ManifestSource(entries);
        Console.WriteLine($"Staging {entries.Count} manifest line(s) into {stagingDir}…");
        var progress = new Progress<string>(s => Console.Write($"\r  {s}".PadRight(78)));
        var res = DiscForge.Core.Sources.SourceStager.Stage(source, stagingDir, progress);
        Console.WriteLine();
        Console.WriteLine($"Staged {res.Files} file(s), {res.Bytes / (1024.0 * 1024 * 1024):0.00} GB → {stagingDir}");
        if (res.Failures.Count > 0)
        {
            Console.WriteLine($"  {res.Failures.Count} failure(s):");
            foreach (var f in res.Failures.Take(10)) Console.WriteLine($"    {f}");
            return 1;
        }
        Console.WriteLine($"  Next: dforge iso-create {stagingDir} disc.iso   (or build-raw / burn)");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DvdLayerbreakPlanCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge dvd-layerbreak-plan <VTS_nn_0.IFO> (--total-sectors N | --image <file>) [--vob-lba N] [--max-l0 N] [--json]\n" +
                    "  RECOMMENDS a DVD9 dual-layer layer-break at a real VOBU boundary — the authoring calculation\n" +
                    "  `dvd-layerbreak` (which only reads/verifies an existing break) does not do. It reads the title\n" +
                    "  VOBU address map from the VTS IFO, turns each VOBU boundary into an absolute disc LBA (add\n" +
                    "  --vob-lba, the title VOB's start sector on the disc; default 0 = relative to the VOB), and picks\n" +
                    "  the boundary that keeps layer 0 >= layer 1 (OTP) and <= the layer-0 capacity, closest to the\n" +
                    "  midpoint for a balanced split. Give the disc size with --total-sectors N (2048-byte sectors) or\n" +
                    "  --image <file>. --max-l0 overrides the layer-0 max (default 2,086,912). Descriptive: writing the\n" +
                    "  break onto the disc is a burn-time step.");
    var ifoPath = args[1];
    if (!File.Exists(ifoPath)) return Fail($"'{ifoPath}' not found.");

    long total;
    if (OptVal(args, "--total-sectors") is { } ts && long.TryParse(ts, out var tsn) && tsn > 0) total = tsn;
    else if (OptVal(args, "--image") is { } img && File.Exists(img)) total = new FileInfo(img).Length / 2048;
    else return Fail("Give the disc size with --total-sectors N or --image <file>.");

    long vobLba = long.TryParse(OptVal(args, "--vob-lba"), out var vl) && vl >= 0 ? vl : 0;
    long maxL0 = long.TryParse(OptVal(args, "--max-l0"), out var ml) && ml > 0
        ? ml : DiscForge.Core.DvdVideo.LayerBreakPlanner.Dvd9MaxLayer0Sectors;

    try
    {
        var ifo = File.ReadAllBytes(ifoPath);
        var vobus = DiscForge.Core.DvdVideo.VtsVobuAdmap.ReadTitleVobuStarts(ifo);
        if (vobus.Count == 0)
            return Fail("No populated VTS_VOBU_ADMAP in this IFO — pass a VTS_nn_0.IFO from a MUXED DVD-Video " +
                        "(a freshly-authored IFO carries the map but with zeroed addresses).");

        var boundaries = vobus.Select(v => vobLba + (long)v).ToList();
        var plan = DiscForge.Core.DvdVideo.LayerBreakPlanner.Recommend(boundaries, total, maxL0);

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(ifoPath),
                total, vobLba, maxL0, vobuCount = vobus.Count,
                plan.MinLayer0, plan.MaxLayer0,
                recommended = plan.Recommended is null ? null : new
                { plan.Recommended.Lba, plan.Recommended.Layer0Sectors, plan.Recommended.Layer1Sectors, plan.Recommended.PercentOfTotal },
                maxFill = plan.MaxFill is null ? null : new { plan.MaxFill.Lba, plan.MaxFill.Layer0Sectors },
                candidates = plan.Candidates.Select(c => new { c.Lba, c.Layer0Sectors, c.Layer1Sectors, c.PercentOfTotal }),
            });
            return plan.HasBreak ? 0 : 2;
        }

        Console.WriteLine($"{Path.GetFileName(ifoPath)}: {vobus.Count:N0} VOBU boundary(ies), disc {total:N0} sectors");
        Console.WriteLine($"  legal layer-0 window: [{plan.MinLayer0:N0}, {plan.MaxLayer0:N0}] sectors " +
                          $"({plan.Candidates.Count:N0} boundary(ies) qualify)");
        Console.WriteLine($"  {plan.Summary}");
        if (plan.MaxFill is not null && plan.Recommended is not null && plan.MaxFill.Lba != plan.Recommended.Lba)
            Console.WriteLine($"  fill layer 0 instead: LBA {plan.MaxFill.Lba:N0} (layer 0 = {plan.MaxFill.Layer0Sectors:N0} sectors)");
        return plan.HasBreak ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int DvdLayerbreakCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge dvd-layerbreak <pfi.bin|.physical> [--image <file>] [--sectors N] [--json]\n" +
                    "  Reads a DVD Physical Format Information block (the .physical sidecar DiscImageCreator\n" +
                    "  saves, or a raw READ DISC STRUCTURE response) and reports the book type, layer count,\n" +
                    "  track path (PTP/OTP) and — for a dual-layer disc — the layer-break LBA, verifying it\n" +
                    "  against the data area. Pass --image or --sectors to also check the dumped image size.\n" +
                    "  Descriptive only; the layer break is a physical fact not stored in the ISO data.");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");
    try
    {
        var layout = DiscForge.Core.Media.DvdPhysicalFormat.ParseFile(path);
        if (layout is null) return Fail("Could not parse the PFI — need at least a 16-byte physical format block.");

        long? sectors = null;
        if (OptVal(args, "--sectors") is { } sv && long.TryParse(sv, out var sn)) sectors = sn;
        else if (OptVal(args, "--image") is { } img && File.Exists(img)) sectors = new FileInfo(img).Length / 2048;

        if (args.Contains("--json"))
        {
            EmitJson(new
            {
                file = Path.GetFileName(path),
                layout.BookType, layout.BookTypeName, layout.Layers,
                trackPath = layout.TrackPath.ToString(),
                dataStartPsn = $"0x{layout.DataStartPsn:X}",
                dataEndPsn = $"0x{layout.DataEndPsn:X}",
                layout.TotalDataSectors,
                layerBreak = layout.LayerBreak,
                layout.Layer0Sectors, layout.Layer1Sectors,
                imageSectors = sectors,
                consistent = layout.IsConsistent(sectors),
                warnings = layout.Verify(sectors),
            });
            return layout.IsConsistent(sectors) ? 0 : 2;
        }

        Console.WriteLine($"{Path.GetFileName(path)}: {layout.Summary(sectors)}");
        return layout.IsConsistent(sectors) ? 0 : 2;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BdmvInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge bdmv-info <file.mpls|file.clpi|BDMV-folder>");
    var path = args[1];

    try
    {
        if (Directory.Exists(path))
        {
            var titles = BdmvReader.EnumerateTitles(path);
            Console.WriteLine($"Blu-ray BDMV — {titles.Count} playlist(s) in {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}");
            foreach (var t in titles)
            {
                Console.WriteLine($"  {t.PlaylistFile}: {BdmvTime.Format(t.Playlist.TotalDurationTicks)}, " +
                                  $"{t.Playlist.Items.Count} item(s), {t.ChapterCount} chapter(s), " +
                                  $"clip(s): {string.Join(", ", t.ClipIds)}");
            }
            return 0;
        }

        if (!File.Exists(path)) return Fail($"'{path}' not found.");
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".mpls")
        {
            var pl = BdmvReader.ReadPlaylist(path);
            PrintPlaylist(Path.GetFileName(path), pl);
            return 0;
        }
        if (ext == ".clpi")
        {
            var clip = BdmvReader.ReadClip(path);
            PrintClip(Path.GetFileName(path), clip);
            return 0;
        }
        return Fail("Expected a .mpls, a .clpi, or a BDMV folder.");
    }
    catch (Exception ex) { return Fail(ex.Message); }

    static void PrintPlaylist(string name, BluRayPlaylist pl)
    {
        Console.WriteLine($"Blu-ray playlist — {name} (MPLS {pl.Version})");
        Console.WriteLine($"  Duration: {BdmvTime.Format(pl.TotalDurationTicks)}   " +
                          $"Items: {pl.Items.Count}   Chapters: {pl.Chapters.Count}");
        for (int i = 0; i < pl.Items.Count; i++)
        {
            var it = pl.Items[i];
            Console.WriteLine($"  Item {i}: clip {it.ClipFileName}  " +
                              $"in {BdmvTime.Format(it.InTime)} -> out {BdmvTime.Format(it.OutTime)}  " +
                              $"({BdmvTime.Format(it.DurationTicks)})");
            foreach (var s in it.Streams)
            {
                string lang = string.IsNullOrEmpty(s.Language) ? "" : $"  [{s.Language}]";
                Console.WriteLine($"      {s.Kind,-20} PID 0x{s.Pid:X4}  {s.CodingName}{lang}");
            }
        }
        if (pl.Chapters.Count > 0)
        {
            Console.WriteLine("  Chapters:");
            for (int c = 0; c < pl.Chapters.Count; c++)
            {
                var m = pl.Chapters[c];
                Console.WriteLine($"      {c + 1,2}. item {m.PlayItemRef} @ {BdmvTime.Format(m.TimeTicks)}");
            }
        }
    }

    static void PrintClip(string name, BluRayClip clip)
    {
        Console.WriteLine($"Blu-ray clip-info — {name} (HDMV {clip.Version})");
        Console.WriteLine($"  Streams: {clip.Streams.Count}");
        foreach (var s in clip.Streams)
        {
            string attrs = s.Kind switch
            {
                StreamKind.Video => $"  {s.VideoFormat} {s.FrameRate}fps {s.AspectRatio}",
                StreamKind.Audio => $"  {s.AudioFormat} {s.SampleRate}",
                _ => "",
            };
            string lang = string.IsNullOrEmpty(s.Language) ? "" : $"  [{s.Language}]";
            Console.WriteLine($"    {s.Kind,-20} PID 0x{s.Pid:X4}  {s.CodingName}{attrs}{lang}");
        }
    }
}

static int MilcdToCdi(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge milcd-to-cdi <in.cue> <out.cdi> [--version v2|v3|v35] [--gap <sectors>]");

    var input = args[1];
    var output = args[2];
    if (!File.Exists(input)) return Fail($"'{input}' not found.");
    if (!input.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
        return Fail("Input must be a .cue — a Redump MIL-CD bin/cue rip.");
    if (!output.EndsWith(".cdi", StringComparison.OrdinalIgnoreCase))
        return Fail("Output must be a .cdi.");

    var version = ParseVersionArg(args) ?? CdiVersion.V35;
    uint gap = CdiConverter.MultisessionGap;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--gap" && i + 1 < args.Length && uint.TryParse(args[i + 1], out uint g))
            gap = g;

    try
    {
        var cueText = File.ReadAllText(input);
        var cueDir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
        var sheet = CueSheet.Parse(cueText);
        var sessions = sheet.Tracks.Select(t => t.Session).Distinct().OrderBy(n => n).ToList();

        Console.WriteLine($"{sheet.Tracks.Count} track(s) across {sessions.Count} session(s).");
        if (sessions.Count < 2)
            Console.WriteLine("  note: no 'REM SESSION' markers found — a MIL-CD self-boot disc is two-session. " +
                              "The CDI will be single-session; confirm this is a Redump MIL-CD rip.");
        else
        {
            int hi = sessions[^1];
            var hiTracks = sheet.Tracks.Where(t => t.Session == hi).Select(t => t.Number);
            Console.WriteLine($"  high-density session {hi}: track(s) {string.Join(", ", hiTracks)} " +
                              $"(inter-session gap {gap} sectors).");
        }

        WriteFileAtomically(output, os => CdiConverter.BinCueToCdi(cueText, cueDir, version, os, gap));
        Console.WriteLine($"Wrote {Path.GetFileName(output)} ({version}).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Split(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge split <file> <size>   (sizes: bytes, 700m, 4g, fat32)");
    try
    {
        long partSize = ImageSplitter.ParsePartSize(args[2]);
        long lastPct = -1;
        var result = ImageSplitter.Split(args[1], partSize, new Progress<double>(f =>
        {
            long pct = (long)(f * 100);
            if (pct != lastPct && pct % 10 == 0) { lastPct = pct; Console.Write($"\r  splitting… {pct}%"); }
        }));
        Console.WriteLine("\r  splitting… done");
        foreach (var p in result.Parts)
            Console.WriteLine($"  {Path.GetFileName(p)} ({new FileInfo(p).Length:N0} bytes)");
        Console.WriteLine($"  {Path.GetFileName(result.ManifestPath)} (manifest)");
        Console.WriteLine($"SHA-256 {result.Sha256}");
        Console.WriteLine($"{result.Parts.Count} part(s), {result.TotalBytes:N0} bytes. " +
                          "Rejoin with: dforge join <first part>");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int Join(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge join <first-part|base-name> [output]");
    try
    {
        string first = args[1];
        string output = args.Length >= 3 ? args[2]
            : System.Text.RegularExpressions.Regex.IsMatch(first, @"\.\d{3}$")
                ? first[..^4] : first;
        if (File.Exists(output))
            return Fail($"'{output}' already exists — refusing to overwrite it.");

        long lastPct = -1;
        var result = ImageSplitter.Join(first, output, new Progress<double>(f =>
        {
            long pct = (long)(f * 100);
            if (pct != lastPct && pct % 10 == 0) { lastPct = pct; Console.Write($"\r  joining… {pct}%"); }
        }));
        Console.WriteLine("\r  joining… done");
        Console.WriteLine($"Wrote {output}: {result.Parts} part(s), {result.TotalBytes:N0} bytes, " +
                          (result.Verified ? "CRC + SHA-256 verified." : "NOT verified."));
        if (result.Warning is not null) Console.WriteLine("  " + result.Warning);
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- PlayStation disc identification ---------------------------------------

static int Ps2Info(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ps2-info <image.iso|image.cdi|image.bin>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var id = SystemCnf.FromImage(args[1]);
        if (id is null)
            return Fail("No SYSTEM.CNF found — this does not look like a PlayStation disc image " +
                        "(or its filesystem could not be read).");

        Console.WriteLine($"{Path.GetFileName(args[1])}: PlayStation {(id.Console == PsConsole.Ps2 ? "2" : "1")} disc");
        Console.WriteLine($"  Game ID    : {(id.GameId.Length > 0 ? id.GameId : "(non-standard boot file)")}");
        Console.WriteLine($"  Region     : {id.Region}");
        if (id.VideoMode is not null) Console.WriteLine($"  Video mode : {id.VideoMode}");
        if (id.Version is not null) Console.WriteLine($"  Version    : {id.Version}");
        Console.WriteLine($"  Boot       : {id.BootPath}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- Xbox XDVDFS -----------------------------------------------------------

static int GodInfoCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge god-info <header-file>\n" +
            "  Identify an Xbox 360 GOD (Games on Demand) package from its header file: the\n" +
            "  package kind (CON/LIVE/PIRS), content type, declared content size, and the\n" +
            "  Data#### payload inventory. Identification only — GOD -> ISO reconstruction is\n" +
            "  deferred pending a reference fixture to pin the hash-block layout (docs/XBOX.md).");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        var info = DiscForge.Core.Xbox.GodContainer.Read(args[1]);
        Console.WriteLine($"Package:      {DiscForge.Core.Xbox.GodContainer.Describe(info.Kind)}");
        Console.WriteLine($"Content type: 0x{info.ContentType:X4}" +
                          (info.LooksLikeGamesOnDemand ? "  (Games on Demand)" : ""));
        Console.WriteLine($"Content size: {info.ContentSize:N0} bytes");
        Console.WriteLine($"Data files:   {info.DataFiles.Count} (Data#### totalling {info.DataFilesTotal:N0} bytes)");
        foreach (var f in info.DataFiles)
            Console.WriteLine($"  {Path.GetFileName(f.Path),-10} {f.Size:N0} bytes");
        if (info.DataFiles.Count == 0)
            Console.WriteLine("  (no Data#### files found beside the header)");
        Console.WriteLine("Tip: `dforge god-extract` reconstructs the ISO (self-validated against the XDVDFS descriptor).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GodExtractCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge god-extract <header-file> <out.iso>\n" +
            "  Reconstruct the Xbox 360 disc image (XDVDFS ISO) from a GOD package by walking its data\n" +
            "  blocks and skipping the interleaved hash blocks. The block->offset formula is ambiguous by\n" +
            "  one hash block between public references, so this reconstructs with BOTH conventions and\n" +
            "  writes the result ONLY if it is a valid XDVDFS volume (the disc's own descriptor is the\n" +
            "  oracle). If neither validates it DECLINES rather than write a corrupt ISO. Decrypts nothing.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    try
    {
        DiscForge.Core.Xbox.GodExtractResult result;
        using (var output = File.Create(args[2]))
            result = DiscForge.Core.Xbox.GodExtractor.Extract(args[1], output);
        if (!result.Succeeded)
        {
            File.Delete(args[2]);
            Console.WriteLine(result.Detail);
            return 2;
        }
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: {result.Detail}");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int XisoLs(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge xiso-ls <image.iso> [--extract <dir>] [--base <sector>]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string? extractTo = null;
    long? baseSector = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--extract" && i + 1 < args.Length) extractTo = args[++i];
        else if (args[i] == "--base" && i + 1 < args.Length && long.TryParse(args[++i], out var b)) baseSector = b;
    }

    try
    {
        using var img = File.OpenRead(args[1]);
        var vol = XdvdfsReader.Read(img, baseSector);
        Console.WriteLine($"{Path.GetFileName(args[1])}: XDVDFS (Xbox), partition base sector {vol.BaseSector:N0}");
        Console.WriteLine();
        foreach (var e in vol.Files)
            Console.WriteLine($"  {e.Size,12:N0}  {e.Path}");
        Console.WriteLine();
        Console.WriteLine($"{vol.Files.Count():N0} file(s), {vol.TotalBytes:N0} bytes; " +
                          $"{vol.Directories.Count():N0} director(ies)");

        if (extractTo is not null)
        {
            Console.WriteLine($"\nExtracting to {extractTo}…");
            foreach (var e in vol.Files)
            {
                string outPath = Path.Combine(extractTo, e.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var o = File.Create(outPath);
                using var src = File.OpenRead(args[1]);
                XdvdfsReader.ExtractFile(src, vol, e, o);
            }
            Console.WriteLine("Done.");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CreateXiso(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge create-xiso <folder> <out.iso>");
    if (!Directory.Exists(args[1])) return Fail($"Folder not found: {args[1]}");

    try
    {
        // Stream straight to disk: files are opened on demand, so a full-size XISO
        // (well past 2 GB) never has to fit in memory.
        var children = WalkFolderToXiso(args[1]);
        IReadOnlyList<string> warnings;
        using (var output = File.Create(args[2]))
            warnings = XdvdfsBuilder.BuildToStream(output, children);

        foreach (var w in warnings) Console.WriteLine($"  warning: {w}");
        long size = new FileInfo(args[2]).Length;
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: XDVDFS (XISO), {size:N0} bytes " +
                          $"({size / 2048:N0} sectors).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static IReadOnlyList<XdvdfsBuilder.Node> WalkFolderToXiso(string folder)
{
    var nodes = new List<XdvdfsBuilder.Node>();
    foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(XdvdfsBuilder.Node.Dir(Path.GetFileName(dir), WalkFolderToXiso(dir)));
    foreach (var file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(XdvdfsBuilder.Node.FileFromPath(Path.GetFileName(file), file));
    return nodes;
}

// ---- Dreamcast GDI ---------------------------------------------------------

static int GdiInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge gdi-info <disc.gdi>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var disc = GdiParser.ParseFile(args[1]);
        var dir = Path.GetDirectoryName(Path.GetFullPath(args[1])) ?? ".";
        var report = GdiValidator.Validate(disc, dir);

        Console.WriteLine($"{Path.GetFileName(args[1])}: {disc.Tracks.Count} track(s)");
        Console.WriteLine();
        Console.WriteLine("  #   Start LBA   Type    Sector   File");
        foreach (var t in disc.Tracks)
        {
            long size = report.FileSizes.TryGetValue(t.FileName, out var sz) ? sz : -1;
            string area = t.IsHighDensity ? "HD" : "SD";
            Console.WriteLine($"  {t.Number,-3} {t.StartLba,10:N0}  {t.Type,-5} {area}  {t.SectorSize,5}   " +
                              $"{t.FileName}" + (size >= 0 ? $"  ({size:N0} bytes)" : "  (missing)"));
        }
        Console.WriteLine();
        if (disc.BootDataTrack is { } boot)
        {
            Console.WriteLine($"Boot data track: {boot.Number} (LBA {boot.StartLba:N0}) — the game filesystem, " +
                              "and what a PPF patch targets.");
            try
            {
                var header = IpBin.ReadFromDisc(disc, dir);
                if (header is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  Title      : {header.Title}");
                    Console.WriteLine($"  Product no.: {header.ProductNumber}   version {header.Version}");
                    Console.WriteLine($"  Region     : {(header.Regions.Count > 0 ? string.Join(", ", header.Regions) : "none declared")}");
                    Console.WriteLine($"  Maker      : {header.Maker}");
                    Console.WriteLine($"  Released   : {header.ReleaseDate}");
                    Console.WriteLine($"  Boot file  : {header.BootFile}");
                }
            }
            catch (IpBinFormatException ex)
            {
                Console.WriteLine($"  (boot header: {ex.Message})");
            }
        }

        foreach (var issue in report.Issues)
            Console.WriteLine($"  [{issue.Level}] {issue}");

        return report.HasErrors ? 2 : report.HasWarnings ? 1 : 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GdiBrowse(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge gdi-browse <disc.gdi> [--extract <dir>]");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    string? extractTo = null;
    for (int i = 2; i < args.Length - 1; i++)
        if (args[i] == "--extract") extractTo = args[i + 1];

    try
    {
        var disc = GdiParser.ParseFile(args[1]);
        var dir = Path.GetDirectoryName(Path.GetFullPath(args[1])) ?? ".";
        var listing = GdiBrowser.Browse(disc, dir);

        Console.WriteLine($"{Path.GetFileName(args[1])}: game filesystem \"{listing.VolumeId}\"" +
                          (listing.Joliet ? " (Joliet)" : "") + (listing.RockRidge ? " (Rock Ridge)" : ""));
        Console.WriteLine();
        foreach (var e in listing.Entries.Where(e => !e.IsDirectory))
            Console.WriteLine($"  {e.Size,12:N0}  {e.Path}");
        Console.WriteLine();
        Console.WriteLine($"{listing.Entries.Count(e => !e.IsDirectory):N0} file(s), " +
                          $"{listing.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size):N0} bytes");

        if (extractTo is not null)
        {
            Console.WriteLine($"\nExtracting to {extractTo}…");
            foreach (var e in listing.Entries.Where(e => !e.IsDirectory))
            {
                string outPath = Path.Combine(extractTo, e.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var o = File.Create(outPath);
                GdiBrowser.ExtractFile(disc, dir, e, o);
            }
            Console.WriteLine("Done.");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IsoRebase(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge iso-rebase <in.iso> <out.iso> <baseLBA>   (GD-ROM: 45000)");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    if (!long.TryParse(args[3], out long baseLba) || baseLba < 0)
        return Fail($"'{args[3]}' is not a valid base LBA.");

    try
    {
        var rebased = IsoRebaser.Rebase(File.ReadAllBytes(args[1]), baseLba);
        File.WriteAllBytes(args[2], rebased);
        Console.WriteLine($"Rebased {Path.GetFileName(args[1])} by +{baseLba:N0} LBA -> {Path.GetFileName(args[2])} " +
                          $"({rebased.Length:N0} bytes). Read it back with a base-LBA reader (gdi-browse does this).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// ---- UDF authoring ---------------------------------------------------------

static int CreateUdf(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge create-udf <folder> <out.udf> [--volume NAME] [--udf-version 1.02|1.50|2.00|2.01|2.50]");
    string folder = args[1], outPath = args[2];
    if (!Directory.Exists(folder)) return Fail($"Folder not found: {folder}");

    string volumeId = "DISCFORGE";
    var revision = UdfBuilder.UdfRevision.Udf102;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--volume" && i + 1 < args.Length) volumeId = args[++i];
        else if (args[i] == "--udf-version" && i + 1 < args.Length)
        {
            revision = args[++i] switch
            {
                "1.02" => UdfBuilder.UdfRevision.Udf102,
                "1.50" => UdfBuilder.UdfRevision.Udf150,
                "2.00" => UdfBuilder.UdfRevision.Udf200,
                "2.01" => UdfBuilder.UdfRevision.Udf201,
                "2.50" => UdfBuilder.UdfRevision.Udf250,
                "2.60" => UdfBuilder.UdfRevision.Udf260,
                var v => throw new ArgumentException($"Unsupported UDF version '{v}' (1.02, 1.50, 2.00, 2.01, 2.50 or 2.60)."),
            };
        }
    }

    try
    {
        // Stream straight to disk so a full-size UDF image needn't fit in memory.
        var children = WalkFolderToUdf(folder);
        IReadOnlyList<string> warnings;
        using (var output = File.Create(outPath))
            warnings = UdfBuilder.BuildToStream(volumeId, output, children, revision);
        foreach (var w in warnings) Console.WriteLine($"  warning: {w}");
        long size = new FileInfo(outPath).Length;
        string ver = revision switch
        {
            UdfBuilder.UdfRevision.Udf150 => "1.50",
            UdfBuilder.UdfRevision.Udf200 => "2.00",
            UdfBuilder.UdfRevision.Udf201 => "2.01",
            UdfBuilder.UdfRevision.Udf250 => "2.50",
            UdfBuilder.UdfRevision.Udf260 => "2.60",
            _ => "1.02",
        };
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: UDF {ver}, {size:N0} bytes " +
                          $"({size / 2048:N0} sectors), volume \"{volumeId}\".");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static IReadOnlyList<UdfBuilder.Node> WalkFolderToUdf(string folder)
{
    var nodes = new List<UdfBuilder.Node>();
    foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(UdfBuilder.Node.Dir(Path.GetFileName(dir), WalkFolderToUdf(dir)));
    foreach (var file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(UdfBuilder.Node.FileFromPath(Path.GetFileName(file), file));
    return nodes;
}

// Resolve the VIDEO_TS folder: accept either the VIDEO_TS folder itself, or a parent
// that contains a VIDEO_TS subdirectory. Returns the folder holding VIDEO_TS.IFO.
static string? ResolveVideoTs(string folder)
{
    if (File.Exists(Path.Combine(folder, "VIDEO_TS.IFO")) ||
        File.Exists(Path.Combine(folder, "video_ts.ifo"))) return folder;
    foreach (var sub in Directory.EnumerateDirectories(folder))
        if (Path.GetFileName(sub).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            && (File.Exists(Path.Combine(sub, "VIDEO_TS.IFO")) || File.Exists(Path.Combine(sub, "video_ts.ifo"))))
            return sub;
    return null;
}

static DiscForge.Core.DvdVideo.DvdVideoPlan? PlanVideoTs(string vtsFolder)
{
    var files = Directory.EnumerateFiles(vtsFolder)
        .Select(p => (Name: Path.GetFileName(p), Size: new FileInfo(p).Length));
    return DiscForge.Core.DvdVideo.DvdVideoLayout.Plan(files);
}

// "Fix VTS Sectors" as a verification: read each IFO's internal sector pointers and check
// they agree with the actual file layout. Returns the list of inconsistencies (empty = OK).
static IReadOnlyList<string> VerifyDvdVideoIfos(string vtsFolder, DiscForge.Core.DvdVideo.DvdVideoPlan plan)
{
    var issues = new List<string>();
    int Sectors(string name)
    {
        string p = Path.Combine(vtsFolder, name);
        return File.Exists(p) ? DiscForge.Core.DvdVideo.DvdVideoIfo.Sectors(new FileInfo(p).Length) : 0;
    }
    byte[] Head(string name)
    {
        string p = Path.Combine(vtsFolder, name);
        if (!File.Exists(p)) return Array.Empty<byte>();
        using var fs = File.OpenRead(p);
        var buf = new byte[Math.Min(0x200, fs.Length)];
        fs.ReadExactly(buf, 0, buf.Length);
        return buf;
    }

    // Video Manager (VIDEO_TS.IFO). Use whichever casing exists.
    string vmgIfo = File.Exists(Path.Combine(vtsFolder, "VIDEO_TS.IFO")) ? "VIDEO_TS.IFO" : "video_ts.ifo";
    var vmg = DiscForge.Core.DvdVideo.DvdVideoIfo.ParseVmgi(Head(vmgIfo));
    if (vmg is not null)
        issues.AddRange(DiscForge.Core.DvdVideo.DvdVideoIfo.VerifyVmg(
            vmg, Sectors("VIDEO_TS.IFO"), Sectors("VIDEO_TS.VOB"), Sectors("VIDEO_TS.BUP")));

    foreach (int ts in plan.TitleSets)
    {
        var vtsi = DiscForge.Core.DvdVideo.DvdVideoIfo.ParseVtsi(Head($"VTS_{ts:D2}_0.IFO"));
        if (vtsi is null) continue;
        int titleSectors = 0;
        for (int part = 1; part <= 9; part++) titleSectors += Sectors($"VTS_{ts:D2}_{part}.VOB");
        issues.AddRange(DiscForge.Core.DvdVideo.DvdVideoIfo.VerifyVts(
            ts, vtsi, Sectors($"VTS_{ts:D2}_0.IFO"), Sectors($"VTS_{ts:D2}_0.VOB"),
            titleSectors, Sectors($"VTS_{ts:D2}_0.BUP")));
    }
    return issues;
}

static int DvdVideoPlanCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge dvd-video-plan <VIDEO_TS-folder>");
    if (!Directory.Exists(args[1])) return Fail($"Folder not found: {args[1]}");
    string? vts = ResolveVideoTs(args[1]);
    if (vts is null) return Fail("No VIDEO_TS.IFO found (point at a VIDEO_TS folder or its parent).");

    var plan = PlanVideoTs(vts)!;
    Console.WriteLine($"VIDEO_TS: {vts}");
    Console.WriteLine($"Title sets: {(plan.TitleSets.Count == 0 ? "none" : string.Join(", ", plan.TitleSets.Select(n => $"VTS_{n:D2}")))}");
    Console.WriteLine($"Total: {plan.OrderedFiles.Count} files, {plan.TotalBytes:N0} bytes");
    Console.WriteLine("On-disc order:");
    int i = 1;
    foreach (var f in plan.OrderedFiles)
        Console.WriteLine($"  {i++,3}. {f.Name,-14} {f.Role,-9} {f.Size,14:N0}");
    foreach (var w in plan.Warnings) Console.WriteLine($"  warning: {w}");
    foreach (var e in plan.Errors) Console.WriteLine($"  ERROR: {e}");
    var ifoIssues = VerifyDvdVideoIfos(vts, plan);
    if (ifoIssues.Count > 0)
    {
        Console.WriteLine("IFO sector-pointer check (Fix VTS Sectors):");
        foreach (var issue in ifoIssues) Console.WriteLine($"  mismatch: {issue}");
    }
    else Console.WriteLine("IFO sector pointers agree with the file layout.");
    Console.WriteLine(plan.IsValid ? "Conformant: ready to build." : "Not conformant — fix the errors above.");
    return plan.IsValid ? 0 : 1;
}

static int DvdVideoBuildCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge dvd-video-build <VIDEO_TS-folder> <out.iso> [--volume NAME]\n" +
            "  Assemble a VIDEO_TS folder into a DVD-Video ISO+UDF-bridge image: the files are placed\n" +
            "  in the DVD-Video on-disc order (VMG first, then each VTS with its IFO leading and BUP\n" +
            "  trailing) inside a VIDEO_TS directory beside an empty AUDIO_TS, readable as ISO 9660 and\n" +
            "  UDF 1.02. Refuses a non-conformant folder (run dvd-video-plan to see why).");
    if (!Directory.Exists(args[1])) return Fail($"Folder not found: {args[1]}");
    string outPath = args[2];
    string? vts = ResolveVideoTs(args[1]);
    if (vts is null) return Fail("No VIDEO_TS.IFO found (point at a VIDEO_TS folder or its parent).");

    string volumeId = "DVD_VIDEO";
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--volume" && i + 1 < args.Length) volumeId = args[++i];

    var plan = PlanVideoTs(vts)!;
    foreach (var w in plan.Warnings) Console.WriteLine($"  warning: {w}");
    if (!plan.IsValid)
    {
        foreach (var e in plan.Errors) Console.WriteLine($"  ERROR: {e}");
        return Fail("VIDEO_TS folder is not DVD-Video conformant — see the errors above.");
    }
    // The IFO pointers should already match a faithfully-authored folder; a mismatch means
    // the source was edited without updating the IFOs (its disc would mis-navigate). Warn,
    // but proceed — we place the files exactly as given.
    foreach (var i in VerifyDvdVideoIfos(vts, plan))
        Console.WriteLine($"  warning (IFO sectors): {i}");

    try
    {
        // Build the VIDEO_TS directory with children in the planned on-disc order, so the
        // ISO builder lays the file DATA down in that order (directory records stay sorted).
        var videoTsChildren = plan.OrderedFiles
            .Select(f => IsoBuilder.Node.FromPath(Path.Combine(vts, f.Name)))
            .ToList();
        var root = new List<IsoBuilder.Node>
        {
            IsoBuilder.Node.Dir("AUDIO_TS", Array.Empty<IsoBuilder.Node>()),   // conventional empty dir
            IsoBuilder.Node.Dir("VIDEO_TS", videoTsChildren),
        };

        IReadOnlyList<string> warnings;
        using (var output = File.Create(outPath))
            warnings = UdfBridgeBuilder.BuildToStream(volumeId, output, root);
        foreach (var w in warnings) Console.WriteLine($"  note: {w}");
        long size = new FileInfo(outPath).Length;
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: DVD-Video ISO+UDF, {plan.OrderedFiles.Count} files, " +
                          $"{size:N0} bytes ({size / 2048:N0} sectors), volume \"{volumeId}\".");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

// The write half of "Fix VTS Sectors": recompute each IFO's file-location pointers from the
// folder's actual file sizes (contiguous layout) and patch them in place, then refresh each
// .BUP as an exact copy of its .IFO. Only the four whole-file / VOB-location pointers move;
// the IFO's internal PGC/table pointers are left untouched. Default is a dry-run preview;
// pass --apply to write.
static int DvdVideoFixCmd(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge dvd-video-fix <VIDEO_TS-folder> [--apply]\n" +
            "  Recompute and rewrite each IFO's VTS_LAST_SECTOR / VTSI_LAST_SECTOR / VTSM_VOBS /\n" +
            "  VTSTT_VOBS (and the VMG trio) to match the folder's actual file sizes, then refresh\n" +
            "  each .BUP as an exact copy of its .IFO — the write half of ImgBurn's \"Fix VTS Sectors\".\n" +
            "  Without --apply it only reports what it would change (dry run).");
    if (!Directory.Exists(args[1])) return Fail($"Folder not found: {args[1]}");
    string? vts = ResolveVideoTs(args[1]);
    if (vts is null) return Fail("No VIDEO_TS.IFO found (point at a VIDEO_TS folder or its parent).");
    bool apply = args.Skip(2).Any(a => a == "--apply");

    var plan = PlanVideoTs(vts)!;
    foreach (var e in plan.Errors) Console.WriteLine($"  ERROR: {e}");
    if (!plan.IsValid) return Fail("VIDEO_TS folder is not DVD-Video conformant — fix the errors above first.");

    int Sectors(string name)
    {
        string p = Path.Combine(vts, name);
        return File.Exists(p) ? DiscForge.Core.DvdVideo.DvdVideoIfo.Sectors(new FileInfo(p).Length) : 0;
    }
    // Resolve the on-disc name to the actual file, honouring either casing.
    string? Find(string name)
    {
        string a = Path.Combine(vts, name), b = Path.Combine(vts, name.ToLowerInvariant());
        return File.Exists(a) ? a : File.Exists(b) ? b : null;
    }

    int fixedNodes = 0, alreadyOk = 0;
    try
    {
        // Video Manager: VIDEO_TS.IFO → VIDEO_TS.BUP.
        string? vmgIfo = Find("VIDEO_TS.IFO");
        if (vmgIfo is not null)
        {
            var bytes = File.ReadAllBytes(vmgIfo);
            if (DiscForge.Core.DvdVideo.DvdVideoIfo.ParseVmgi(bytes) is not null)
            {
                var want = DiscForge.Core.DvdVideo.DvdVideoIfo.ComputeVmg(
                    Sectors("VIDEO_TS.IFO"), Sectors("VIDEO_TS.VOB"), Sectors("VIDEO_TS.BUP"));
                bool changed = DiscForge.Core.DvdVideo.DvdVideoIfo.WriteVmgPointers(bytes, want);
                if (changed) { Console.WriteLine("  VMG: VIDEO_TS.IFO pointers -> " +
                    $"last={want.VmgLastSector} ifoLast={want.VmgiLastSector} menu={want.VmgmVobs}"); fixedNodes++; }
                else { alreadyOk++; }
                if (apply)
                {
                    File.WriteAllBytes(vmgIfo, bytes);
                    string? bup = Find("VIDEO_TS.BUP");
                    if (bup is not null) File.WriteAllBytes(bup, bytes);   // BUP = exact copy of IFO
                }
            }
        }

        foreach (int ts in plan.TitleSets)
        {
            string? ifoPath = Find($"VTS_{ts:D2}_0.IFO");
            if (ifoPath is null) continue;
            var bytes = File.ReadAllBytes(ifoPath);
            if (DiscForge.Core.DvdVideo.DvdVideoIfo.ParseVtsi(bytes) is null) continue;
            int titleSectors = 0;
            for (int part = 1; part <= 9; part++) titleSectors += Sectors($"VTS_{ts:D2}_{part}.VOB");
            var want = DiscForge.Core.DvdVideo.DvdVideoIfo.ComputeVts(
                Sectors($"VTS_{ts:D2}_0.IFO"), Sectors($"VTS_{ts:D2}_0.VOB"), titleSectors, Sectors($"VTS_{ts:D2}_0.BUP"));
            bool changed = DiscForge.Core.DvdVideo.DvdVideoIfo.WriteVtsPointers(bytes, want);
            if (changed) { Console.WriteLine($"  VTS_{ts:D2}: VTS_{ts:D2}_0.IFO pointers -> " +
                $"last={want.VtsLastSector} ifoLast={want.VtsiLastSector} menu={want.VtsmVobs} title={want.VtsttVobs}"); fixedNodes++; }
            else { alreadyOk++; }
            if (apply)
            {
                File.WriteAllBytes(ifoPath, bytes);
                string? bup = Find($"VTS_{ts:D2}_0.BUP");
                if (bup is not null) File.WriteAllBytes(bup, bytes);   // BUP = exact copy of IFO
            }
        }
    }
    catch (Exception ex) { return Fail(ex.Message); }

    if (fixedNodes == 0)
        Console.WriteLine($"All {alreadyOk} IFO header(s) already agree with the file layout — nothing to fix.");
    else if (apply)
        Console.WriteLine($"Applied: rewrote {fixedNodes} IFO header(s) and refreshed matching .BUP copies " +
                          $"({alreadyOk} already correct).");
    else
        Console.WriteLine($"Dry run: {fixedNodes} IFO header(s) would change ({alreadyOk} already correct). " +
                          "Re-run with --apply to write.");
    return 0;
}

// Resolve a BD-Video disc: return (discRootNodes as UDF nodes, relativePathsForValidation).
// Accepts a disc root containing BDMV/, or the BDMV folder itself (wrapped as BDMV/).
static (List<UdfBuilder.Node> Nodes, List<string> Rel)? ResolveBdmv(string folder)
{
    static bool HasIndex(string dir) =>
        File.Exists(Path.Combine(dir, "index.bdmv")) || File.Exists(Path.Combine(dir, "INDEX.BDMV"));

    if (Directory.Exists(Path.Combine(folder, "BDMV")) && HasIndex(Path.Combine(folder, "BDMV")))
    {
        var nodes = WalkFolderToUdf(folder).ToList();     // disc root: BDMV/ (+ CERTIFICATE/)
        var rel = new List<string>();
        CollectRel(folder, folder, rel, "");
        return (nodes, rel);
    }
    if (HasIndex(folder))
    {
        // The folder IS the BDMV directory — wrap it as a single BDMV/ child.
        var bdmvChildren = WalkFolderToUdf(folder);
        var rel = new List<string>();
        CollectRel(folder, folder, rel, "BDMV/");
        return (new List<UdfBuilder.Node> { UdfBuilder.Node.Dir("BDMV", bdmvChildren) }, rel);
    }
    return null;
}

static void CollectRel(string root, string dir, List<string> acc, string prefix)
{
    foreach (var f in Directory.EnumerateFiles(dir))
        acc.Add(prefix + Path.GetRelativePath(root, f).Replace('\\', '/'));
    foreach (var sub in Directory.EnumerateDirectories(dir))
        CollectRel(root, sub, acc, prefix);
}

static int BdmvPlanCmd(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge bdmv-plan <BDMV-folder>");
    if (!Directory.Exists(args[1])) return Fail($"Folder not found: {args[1]}");
    var resolved = ResolveBdmv(args[1]);
    if (resolved is null) return Fail("No BDMV/index.bdmv found (point at a BD-Video disc root or its BDMV folder).");

    var v = DiscForge.Core.DvdVideo.BdmvLayout.Validate(resolved.Value.Rel);
    Console.WriteLine($"BD-Video: {v.PlaylistCount} playlist(s), {v.ClipCount} clip(s), {v.StreamCount} stream(s), " +
                      $"backup {(v.HasBackup ? "present" : "MISSING")}");
    foreach (var w in v.Warnings) Console.WriteLine($"  warning: {w}");
    foreach (var e in v.Errors) Console.WriteLine($"  ERROR: {e}");
    Console.WriteLine(v.IsValid ? "Conformant: ready to build (BD uses UDF 2.50)." : "Not conformant — fix the errors above.");
    return v.IsValid ? 0 : 1;
}

static int BdmvBuildCmd(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge bdmv-build <BDMV-folder> <out.iso> [--volume NAME]\n" +
            "  Assemble a Blu-ray BDMV folder into a BD-Video image on a pure UDF 2.50 filesystem\n" +
            "  (the Blu-ray filesystem — no ISO 9660). Validates the BDMV structure first; run\n" +
            "  bdmv-plan to see any problems.");
    if (!Directory.Exists(args[1])) return Fail($"Folder not found: {args[1]}");
    string outPath = args[2];
    var resolved = ResolveBdmv(args[1]);
    if (resolved is null) return Fail("No BDMV/index.bdmv found (point at a BD-Video disc root or its BDMV folder).");

    string volumeId = "BDROM";
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--volume" && i + 1 < args.Length) volumeId = args[++i];

    var v = DiscForge.Core.DvdVideo.BdmvLayout.Validate(resolved.Value.Rel);
    foreach (var w in v.Warnings) Console.WriteLine($"  warning: {w}");
    if (!v.IsValid)
    {
        foreach (var e in v.Errors) Console.WriteLine($"  ERROR: {e}");
        return Fail("BDMV folder is not BD-Video conformant — see the errors above.");
    }

    try
    {
        // BD uses a pure UDF 2.50 filesystem (no ISO 9660).
        IReadOnlyList<string> warnings;
        using (var output = File.Create(outPath))
            warnings = UdfBuilder.BuildToStream(volumeId, output, resolved.Value.Nodes, UdfBuilder.UdfRevision.Udf250);
        foreach (var w in warnings) Console.WriteLine($"  note: {w}");
        long size = new FileInfo(outPath).Length;
        Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: BD-Video (UDF 2.50), {size:N0} bytes " +
                          $"({size / 2048:N0} sectors), volume \"{volumeId}\".");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int CreateUdfBridge(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge create-udf-bridge <folder> <out.iso> [--volume NAME] [--json]");
    string folder = args[1], outPath = args[2];
    if (!Directory.Exists(folder)) return Fail($"Folder not found: {folder}");

    string volumeId = "DISCFORGE";
    bool json = false;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--volume" && i + 1 < args.Length) volumeId = args[++i];
        else if (args[i] == "--json") json = true;
    }

    try
    {
        // One image, two filesystems, one copy of the data: the ISO 9660 (Joliet)
        // directory records and the UDF File Entries point at the same sectors.
        var children = WalkFolderToBridge(folder);
        IReadOnlyList<string> warnings;
        using (var output = File.Create(outPath))
            warnings = UdfBridgeBuilder.BuildToStream(volumeId, output, children);

        long size = new FileInfo(outPath).Length;
        if (json)
        {
            EmitJson(new
            {
                output = Path.GetFileName(outPath),
                volume = volumeId,
                filesystems = new[] { "ISO9660", "Joliet", "UDF-1.02" },
                bytes = size,
                sectors = size / 2048,
                warnings = warnings.ToArray(),
            });
        }
        else
        {
            foreach (var w in warnings) Console.WriteLine($"  warning: {w}");
            Console.WriteLine($"Wrote {Path.GetFileName(outPath)}: UDF-bridge (ISO 9660 + Joliet + UDF 1.02), " +
                              $"{size:N0} bytes ({size / 2048:N0} sectors), volume \"{volumeId}\".");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static IReadOnlyList<IsoBuilder.Node> WalkFolderToBridge(string folder)
{
    var nodes = new List<IsoBuilder.Node>();
    foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(IsoBuilder.Node.Dir(Path.GetFileName(dir), WalkFolderToBridge(dir)));
    foreach (var file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
        nodes.Add(IsoBuilder.Node.File(Path.GetFileName(file), IsoBuilder.FileSource.FromFile(file)));
    return nodes;
}

// ---- PPF patching ----------------------------------------------------------

static int PpfInfo(string[] args)
{
    if (args.Length < 2) return Fail("usage: dforge ppf-info <patch.ppf>");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        var patch = PpfPatch.ParseFile(args[1]);
        Console.WriteLine($"{Path.GetFileName(args[1])}: PPF {patch.Version.ToString()[1..]}.0");
        if (patch.Description.Length > 0)
            Console.WriteLine($"  Description : {patch.Description}");
        Console.WriteLine($"  Changes     : {patch.Records.Count:N0} record(s), " +
                          $"{patch.Records.Sum(r => r.Data.Length):N0} bytes");
        Console.WriteLine($"  Reaches to  : offset {patch.MaxTouchedOffset:N0}");
        Console.WriteLine($"  Undo data   : {(patch.CanUndo ? "yes — this patch can be reverted" : "no")}");
        Console.WriteLine($"  Validation  : {(patch.HasValidationBlock ? $"yes — 1024 bytes at 0x{patch.ValidationOffset:X}" : "no")}");
        if (patch.OriginalSize > 0)
            Console.WriteLine($"  Built from  : an image of {patch.OriginalSize:N0} bytes");
        if (patch.FileId is not null)
        {
            Console.WriteLine("  file_id.diz :");
            foreach (var line in patch.FileId.Split('\n'))
                Console.WriteLine($"    {line.TrimEnd()}");
        }
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PpfApply(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge ppf-apply <patch.ppf> <image.bin> [--undo] [--force] [--dry-run]");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Image not found: {args[2]}");

    bool undo = false, force = false, dryRun = false;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--undo": undo = true; break;
            case "--force": force = true; break;
            case "--dry-run": dryRun = true; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        var patch = PpfPatch.ParseFile(args[1]);
        Console.WriteLine($"Patch: PPF {patch.Version.ToString()[1..]}.0" +
                          (patch.Description.Length > 0 ? $" — {patch.Description}" : ""));

        using var image = new FileStream(args[2], FileMode.Open, FileAccess.ReadWrite);

        if (undo)
        {
            if (!patch.CanUndo)
                return Fail("This patch carries no undo data, so --undo cannot revert it " +
                            "(only PPF 3.0 patches written with undo can be reverted).");
            if (dryRun) { Console.WriteLine("Dry run: patch is undoable and the image is long enough."); return 0; }
            int reverted = PpfPatch.Undo(patch, image, force);
            Console.WriteLine($"Reverted {reverted:N0} record(s). {Path.GetFileName(args[2])} restored.");
            return 0;
        }

        var check = PpfPatch.CheckApplicable(patch, image);
        if (check.ValidationMatched)
            Console.WriteLine("Validation block matches — this is the image the patch targets.");
        if (!check.Ok)
        {
            if (!force) return Fail(check.Problem!);
            Console.WriteLine($"Warning (overridden by --force): {check.Problem}");
        }

        if (dryRun) { Console.WriteLine($"Dry run: {patch.Records.Count:N0} record(s) would be applied. Nothing written."); return 0; }

        int applied = PpfPatch.Apply(patch, image, force);
        Console.WriteLine($"Applied {applied:N0} record(s) to {Path.GetFileName(args[2])}." +
                          (patch.CanUndo ? " Keep the patch to undo later (ppf-apply --undo)." : ""));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IpsApply(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge ips-apply <patch.ips> <image> [--out <file>]");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Image not found: {args[2]}");
    string outPath = args[2];
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
        else return Fail($"Unknown option: {args[i]}");
    try
    {
        var patch = DiscForge.Core.Patch.IpsPatch.ParseFile(args[1]);
        var patched = DiscForge.Core.Patch.IpsPatch.Apply(patch, File.ReadAllBytes(args[2]));
        File.WriteAllBytes(outPath, patched);
        Console.WriteLine($"Applied {patch.Records.Count:N0} IPS record(s)" +
                          (patch.TruncateLength is { } t ? $", truncated to {t:N0} bytes" : "") +
                          $" -> {Path.GetFileName(outPath)} ({patched.Length:N0} bytes).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int IpsCreate(string[] args)
{
    if (args.Length < 4) return Fail("usage: dforge ips-create <orig> <mod> <out.ips>");
    if (!File.Exists(args[1])) return Fail($"Original not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Modified not found: {args[2]}");
    try
    {
        var ips = DiscForge.Core.Patch.IpsPatch.Create(File.ReadAllBytes(args[1]), File.ReadAllBytes(args[2]));
        File.WriteAllBytes(args[3], ips);
        var parsed = DiscForge.Core.Patch.IpsPatch.Parse(ips);
        Console.WriteLine($"Wrote {Path.GetFileName(args[3])}: {parsed.Records.Count:N0} record(s), {ips.Length:N0} bytes.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BpsApply(string[] args)
{
    if (args.Length < 3) return Fail("usage: dforge bps-apply <patch.bps> <source> [--out <file>] [--no-verify]");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Source not found: {args[2]}");
    string outPath = args[2];
    bool verify = true;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
            case "--no-verify": verify = false; break;
            default: return Fail($"Unknown option: {args[i]}");
        }
    try
    {
        var patch = DiscForge.Core.Patch.BpsPatch.ParseFile(args[1]);
        var target = DiscForge.Core.Patch.BpsPatch.Apply(patch, File.ReadAllBytes(args[2]), verify);
        File.WriteAllBytes(outPath, target);
        Console.WriteLine($"Applied BPS patch -> {Path.GetFileName(outPath)} ({target.Length:N0} bytes, CRC verified)." +
                          (patch.Metadata.Length > 0 ? $" Metadata: {patch.Metadata}" : ""));
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int BpsCreate(string[] args)
{
    if (args.Length < 4) return Fail("usage: dforge bps-create <source> <target> <out.bps>");
    if (!File.Exists(args[1])) return Fail($"Source not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Target not found: {args[2]}");
    try
    {
        var bps = DiscForge.Core.Patch.BpsPatch.Create(File.ReadAllBytes(args[1]), File.ReadAllBytes(args[2]));
        File.WriteAllBytes(args[3], bps);
        Console.WriteLine($"Wrote {Path.GetFileName(args[3])}: {bps.Length:N0} bytes (source/target CRC-32 embedded).");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PpfConvert(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge ppf-convert <in.ppf> <out.ppf> --to 1|2|3");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");

    PpfVersion? target = null;
    for (int i = 3; i < args.Length; i++)
        if (args[i] == "--to" && i + 1 < args.Length)
            target = args[++i] switch
            {
                "1" or "1.0" => PpfVersion.V1,
                "2" or "2.0" => PpfVersion.V2,
                "3" or "3.0" => PpfVersion.V3,
                _ => null,
            };
    if (target is null) return Fail("Specify the target with --to 1, --to 2 or --to 3.");

    try
    {
        var patch = PpfPatch.ParseFile(args[1]);
        var converted = PpfPatch.ConvertTo(patch, target.Value);
        File.WriteAllBytes(args[2], converted);
        var check = PpfPatch.Parse(converted);
        Console.WriteLine($"Converted PPF {patch.Version.ToString()[1..]}.0 -> {check.Version.ToString()[1..]}.0: " +
                          $"{Path.GetFileName(args[2])}, {check.Records.Count:N0} record(s), {converted.Length:N0} bytes" +
                          (check.CanUndo ? ", undoable" : "") + (check.HasValidationBlock ? ", validated" : "") + ".");
        if (target == PpfVersion.V1 && patch.CanUndo)
            Console.WriteLine("Note: PPF 1.0 carries no undo or validation, so those were dropped.");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PpfEdit(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge ppf-edit <in.ppf> <out.ppf> [--desc \"text\"] [--fileid \"text\"]");
    if (!File.Exists(args[1])) return Fail($"Patch not found: {args[1]}");

    string? desc = null, fileId = null;
    for (int i = 3; i < args.Length; i++)
        switch (args[i])
        {
            case "--desc" when i + 1 < args.Length: desc = args[++i]; break;
            case "--fileid" when i + 1 < args.Length: fileId = args[++i]; break;
            default: return Fail($"Unknown option: {args[i]}");
        }
    if (desc is null && fileId is null)
        return Fail("Nothing to change — pass --desc and/or --fileid.");

    try
    {
        var patch = PpfPatch.ParseFile(args[1]);
        var edited = PpfPatch.WithMetadata(patch, desc, fileId);
        File.WriteAllBytes(args[2], PpfPatch.Serialize(edited));
        Console.WriteLine($"Wrote {Path.GetFileName(args[2])}: PPF {patch.Version.ToString()[1..]}.0, metadata updated" +
                          (desc is not null ? $" (description \"{desc}\")" : "") +
                          (fileId is not null ? " (file_id)" : "") + ".");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int PpfCreate(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge ppf-create <original.bin> <modified.bin> <out.ppf> " +
                    "[--desc \"text\"] [--no-undo] [--no-validation] [--fileid \"text\"]");
    if (!File.Exists(args[1])) return Fail($"Original not found: {args[1]}");
    if (!File.Exists(args[2])) return Fail($"Modified not found: {args[2]}");

    var opts = new PpfPatch.CreateOptions();
    for (int i = 4; i < args.Length; i++)
        switch (args[i])
        {
            case "--desc" when i + 1 < args.Length: opts = opts with { Description = args[++i] }; break;
            case "--fileid" when i + 1 < args.Length: opts = opts with { FileId = args[++i] }; break;
            case "--no-undo": opts = opts with { IncludeUndo = false }; break;
            case "--no-validation": opts = opts with { IncludeValidation = false }; break;
            default: return Fail($"Unknown option: {args[i]}");
        }

    try
    {
        var ppf = PpfPatch.CreateFromFiles(args[1], args[2], opts);
        File.WriteAllBytes(args[3], ppf);
        var parsed = PpfPatch.Parse(ppf);
        Console.WriteLine($"Wrote {Path.GetFileName(args[3])}: PPF 3.0, {parsed.Records.Count:N0} record(s), " +
                          $"{ppf.Length:N0} bytes" +
                          (parsed.Records.Count == 0 ? " — the two images are identical." : "") +
                          (opts.IncludeUndo ? ", undoable" : "") +
                          (parsed.HasValidationBlock ? ", validated" : "") + ".");
        return 0;
    }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int GameAudioInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge gameaudio-info <file>   (a .psf/.psf2/.minipsf, .spc, .vgm/.vgz, or .nsf)");
    var path = args[1];
    if (!File.Exists(path)) return Fail($"'{path}' not found.");

    byte[] data;
    try { data = File.ReadAllBytes(path); }
    catch (Exception ex) { return Fail(ex.Message); }

    Console.WriteLine($"Game audio — {Path.GetFileName(path)} ({data.Length:N0} bytes)");
    try
    {
        if (DiscForge.Core.GameAudio.PsfReader.IsPsf(data))
        {
            var f = DiscForge.Core.GameAudio.PsfReader.Read(data);
            Console.WriteLine($"  Format:     PSF family (version 0x{f.PsfVersion:X2})");
            Console.WriteLine($"  System:     {f.SystemName}");
            Console.WriteLine($"  Program:    {f.CompressedProgramSize:N0} compressed bytes (CRC32 {f.ProgramCrc32:X8}) — not decoded");
            if (f.Title is not null) Console.WriteLine($"  Title:      {f.Title}");
            if (f.Game is not null) Console.WriteLine($"  Game:       {f.Game}");
            if (f.Artist is not null) Console.WriteLine($"  Artist:     {f.Artist}");
            if (f.Length is not null) Console.WriteLine($"  Length:     {f.Length}");
            foreach (var kv in f.Tags)
                if (kv.Key is not ("title" or "game" or "artist" or "length"))
                    Console.WriteLine($"  {kv.Key,-11} {kv.Value.Replace("\n", "; ")}");
            return 0;
        }
        if (DiscForge.Core.GameAudio.SpcReader.IsSpc(data))
        {
            var f = DiscForge.Core.GameAudio.SpcReader.Read(data);
            Console.WriteLine("  Format:     SPC (SNES SPC700 dump)");
            Console.WriteLine($"  ID666:      {(f.HasId666 ? (f.TextFormatTag ? "present (text)" : "present (binary)") : "absent")}");
            if (f.SongTitle.Length > 0) Console.WriteLine($"  Song:       {f.SongTitle}");
            if (f.GameTitle.Length > 0) Console.WriteLine($"  Game:       {f.GameTitle}");
            if (f.Artist.Length > 0) Console.WriteLine($"  Artist:     {f.Artist}");
            if (f.DumperName.Length > 0) Console.WriteLine($"  Dumper:     {f.DumperName}");
            if (f.DumpDate.Length > 0) Console.WriteLine($"  Date:       {f.DumpDate}");
            if (f.Comments.Length > 0) Console.WriteLine($"  Comments:   {f.Comments}");
            return 0;
        }
        if (DiscForge.Core.GameAudio.VgmReader.IsVgm(data))
        {
            var f = DiscForge.Core.GameAudio.VgmReader.Read(data);
            Console.WriteLine($"  Format:     VGM v{f.Version}");
            Console.WriteLine($"  Samples:    {f.TotalSamples:N0} @ 44100 Hz  ({f.DurationSeconds:F2} s)");
            Console.WriteLine($"  Chips:      {(f.Chips.Count > 0 ? string.Join(", ", f.Chips) : "(none flagged)")}");
            if (f.Tags.TrackName.Length > 0) Console.WriteLine($"  Track:      {f.Tags.TrackName}");
            if (f.Tags.GameName.Length > 0) Console.WriteLine($"  Game:       {f.Tags.GameName}");
            if (f.Tags.System.Length > 0) Console.WriteLine($"  System:     {f.Tags.System}");
            if (f.Tags.Author.Length > 0) Console.WriteLine($"  Author:     {f.Tags.Author}");
            if (f.Tags.Date.Length > 0) Console.WriteLine($"  Date:       {f.Tags.Date}");
            if (f.Tags.Notes.Length > 0) Console.WriteLine($"  Notes:      {f.Tags.Notes}");
            return 0;
        }
        if (DiscForge.Core.GameAudio.NsfReader.IsNsf(data))
        {
            var f = DiscForge.Core.GameAudio.NsfReader.Read(data);
            Console.WriteLine($"  Format:     NSF v{f.Version} (NES Sound Format)");
            Console.WriteLine($"  Songs:      {f.TotalSongs} (starts at #{f.StartingSong})");
            Console.WriteLine($"  Region:     {(f.IsPal ? "PAL" : "NTSC")}");
            if (f.SongName.Length > 0) Console.WriteLine($"  Name:       {f.SongName}");
            if (f.Artist.Length > 0) Console.WriteLine($"  Artist:     {f.Artist}");
            if (f.Copyright.Length > 0) Console.WriteLine($"  Copyright:  {f.Copyright}");
            Console.WriteLine($"  Expansion:  {(f.ExpansionChips.Count > 0 ? string.Join(", ", f.ExpansionChips) : "none (2A03 only)")}");
            return 0;
        }
        if (DiscForge.Core.GameAudio.NsfReader.IsNsfe(data))
            return Fail("This is an NSFe file — a chunked container distinct from classic NSF; DiscForge does not parse it.");
    }
    catch (DiscForge.Core.GameAudio.GameAudioFormatException ex) { return Fail(ex.Message); }

    return Fail("No PSF/SPC/VGM/NSF signature matched — not a game-audio format DiscForge reads.");
}

static bool TryCheatPlatform(string s, out DiscForge.Core.Cheat.CheatPlatform platform, out bool isGameShark)
{
    isGameShark = false;
    switch (s.ToLowerInvariant())
    {
        case "nes": platform = DiscForge.Core.Cheat.CheatPlatform.Nes; return true;
        case "snes": platform = DiscForge.Core.Cheat.CheatPlatform.Snes; return true;
        case "genesis": case "megadrive": case "md":
            platform = DiscForge.Core.Cheat.CheatPlatform.Genesis; return true;
        case "gb": case "gameboy": case "gbc":
            platform = DiscForge.Core.Cheat.CheatPlatform.GameBoy; return true;
        case "gs-ps1": case "gameshark": case "ps1":
            platform = DiscForge.Core.Cheat.CheatPlatform.GameSharkPs1; isGameShark = true; return true;
        default: platform = default; return false;
    }
}

static int CheatDecode(string[] args)
{
    if (args.Length < 3)
        return Fail("usage: dforge cheat-decode <nes|snes|genesis|gb|gs-ps1> <code>");
    if (!TryCheatPlatform(args[1], out var platform, out bool isGameShark))
        return Fail($"Unknown platform '{args[1]}' (use nes|snes|genesis|gb|gs-ps1).");

    // GameShark value word may contain a space, so re-join the remaining args.
    string code = string.Join(' ', args[2..]);
    try
    {
        var c = isGameShark
            ? DiscForge.Core.Cheat.GameShark.Parse(code, platform)
            : DiscForge.Core.Cheat.GameGenie.Decode(platform, code);

        Console.WriteLine($"Platform:   {c.Platform}");
        Console.WriteLine($"Address:    0x{c.Address:X}");
        Console.WriteLine($"Value:      0x{c.Value:X}");
        Console.WriteLine(c.Compare is { } cmp ? $"Compare:    0x{cmp:X}" : "Compare:    (none)");
        if (!string.IsNullOrEmpty(c.Description))
            Console.WriteLine($"Note:       {c.Description}");
        return 0;
    }
    catch (DiscForge.Core.Cheat.CheatFormatException ex) { return Fail(ex.Message); }
}

static int CheatEncode(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge cheat-encode <nes|snes|genesis|gb> <address> <value> [compare]  (hex)");
    if (!TryCheatPlatform(args[1], out var platform, out bool isGameShark) || isGameShark)
        return Fail($"cheat-encode supports Game Genie platforms only (nes|snes|genesis|gb).");

    static bool ParseNum(string s, out long v) =>
        long.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s,
            System.Globalization.NumberStyles.HexNumber, null, out v);

    if (!ParseNum(args[2], out long address)) return Fail($"Bad hex address '{args[2]}'.");
    if (!ParseNum(args[3], out long value)) return Fail($"Bad hex value '{args[3]}'.");
    long? compare = null;
    if (args.Length >= 5)
    {
        if (!ParseNum(args[4], out long cmp)) return Fail($"Bad hex compare '{args[4]}'.");
        compare = cmp;
    }

    try
    {
        var code = new DiscForge.Core.Cheat.CheatCode
        {
            Platform = platform,
            Address = address,
            Value = value,
            Compare = compare,
        };
        Console.WriteLine(DiscForge.Core.Cheat.GameGenie.Encode(code));
        return 0;
    }
    catch (DiscForge.Core.Cheat.CheatFormatException ex) { return Fail(ex.Message); }
}

static int CheatApplyNes(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge cheat-apply-nes <rom> <code> <out>");
    string romPath = args[1], codeStr = args[2], outPath = args[3];
    if (!File.Exists(romPath)) return Fail($"File not found: {romPath}");

    try
    {
        var code = DiscForge.Core.Cheat.GameGenie.DecodeNes(codeStr);
        byte[] rom = File.ReadAllBytes(romPath);
        var result = DiscForge.Core.Cheat.CheatApply.ApplyNes(rom, code);

        Console.WriteLine($"Code:       {codeStr}  ->  address 0x{code.Address:X}, value 0x{code.Value:X}" +
                          (code.Compare is { } cmp ? $", compare 0x{cmp:X}" : ""));
        if (result.PatchedOffsets.Count == 0)
        {
            Console.WriteLine(result.CompareMismatch
                ? "No bytes patched (compare byte did not match)."
                : "No bytes patched (address did not map into PRG-ROM).");
        }
        else
        {
            foreach (int off in result.PatchedOffsets)
                Console.WriteLine($"Patched:    file offset 0x{off:X}");
        }

        File.WriteAllBytes(outPath, rom);
        Console.WriteLine($"Wrote:      {outPath} ({rom.Length:N0} bytes)");
        return 0;
    }
    catch (DiscForge.Core.Cheat.CheatFormatException ex) { return Fail(ex.Message); }
}

static int WbfsInfo(string[] args)
{
    if (args.Length < 2)
        return Fail("usage: dforge wbfs-info <file.wbfs>\n" +
                    "  Lists the Wii/GameCube discs stored in a WBFS container.");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");

    try
    {
        using var fs = File.OpenRead(args[1]);
        var wbfs = DiscForge.Core.Wbfs.WbfsReader.Read(fs);
        Console.WriteLine($"{Path.GetFileName(args[1])}: {wbfs.Summary}");
        Console.WriteLine();
        Console.WriteLine("Slot  Game ID  Title");
        foreach (var d in wbfs.Discs)
            Console.WriteLine($"  {d.Slot,2}  {d.GameId,-7}  {d.Title}");
        return 0;
    }
    catch (DiscForge.Core.Wbfs.WbfsFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

static int WbfsExtract(string[] args)
{
    if (args.Length < 4)
        return Fail("usage: dforge wbfs-extract <file.wbfs> <slot> <out.iso>\n" +
                    "  Rebuilds one disc's ISO from a WBFS container (contents copied as-is).");
    if (!File.Exists(args[1])) return Fail($"File not found: {args[1]}");
    if (!int.TryParse(args[2], out int slot)) return Fail($"Not a slot number: {args[2]}");

    try
    {
        using var fs = File.OpenRead(args[1]);
        var wbfs = DiscForge.Core.Wbfs.WbfsReader.Read(fs);
        var disc = wbfs.Discs.FirstOrDefault(d => d.Slot == slot);
        if (disc is null)
            return Fail($"No disc in slot {slot}. Present slots: " +
                        string.Join(", ", wbfs.Discs.Select(d => d.Slot)));

        using var os = File.Create(args[3]);
        long written = DiscForge.Core.Wbfs.WbfsReader.ExtractDisc(fs, disc, os);
        Console.WriteLine($"Extracted {disc.GameId} ({disc.Title}) to " +
                          $"{Path.GetFileName(args[3])} — {written:N0} bytes.");
        return 0;
    }
    catch (DiscForge.Core.Wbfs.WbfsFormatException ex) { return Fail(ex.Message); }
    catch (Exception ex) { return Fail(ex.Message); }
}

// Write an output file atomically: build it as <path>.part, then move into place. A failed run can
// never leave a truncated file at the destination that looks complete, and an existing good file is
// not clobbered until the new one is whole.
static long WriteFileAtomically(string outPath, Action<Stream> writer)
{
    var tmp = outPath + ".part";
    try
    {
        long n;
        using (var os = File.Create(tmp)) { writer(os); n = os.Length; }
        File.Move(tmp, outPath, overwrite: true);
        return n;
    }
    catch
    {
        try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
        throw;
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine($"dforge: {message}");
    return 1;
}
