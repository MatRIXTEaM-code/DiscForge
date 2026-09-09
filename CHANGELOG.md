# Changelog

All notable changes to DiscForge are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

DiscForge is a clean-room disc-imaging and retro-preservation toolkit: it images,
identifies, converts, patches and verifies discs and cartridge dumps, and reads
and manages console saves. It **detects** copy protection but never circumvents
it, and never defeats console security or decrypts protected content.

## [Unreleased]

### Added — v1.101.0: the flux/RF moonshot's last blocker is gone — Efm.cs now carries the real ECMA-130 table

- Asked for a deep dive into something genuinely ambitious rather than another incremental addition.
  docs/DIFFERENTIATORS.md already named the single largest one on the table: DiscForge's software EFM
  demodulator (`FluxDemodulator`/`FluxDecoder`) has been complete and round-trip-tested since an earlier
  session, but `Efm.cs` used a *modelled* byte→codeword assignment instead of the real ECMA-130 Annex D
  table — meaning it could only ever decode its own encoder's output, never an actual disc's physical
  flux. That was flagged as "a pure data swap" once the real table was in hand. It's now in hand.
- The authoritative table (all 256 byte→codeword entries, plus the two frame-sync CONTROL patterns from
  the same standard) is transcribed from the GPL-licensed EFM dictionary in Sidney Cadot's `laser2wav`
  project — the software behind happycube's well-known `cd-decode` CD/RF archival tooling — itself
  derived from the published ECMA-130 standard: the one physical encoding every CD ever pressed actually
  uses, not something specific to any one tool. GPL-3.0-or-later throughout, so it's fully compatible
  with this project's own licensing.
- Verified three independent ways, not just "it compiles": (1) a static-constructor self-check now runs
  at startup, confirming all 256 entries individually satisfy EFM's own run-length rule and that none
  collide with each other or with the two sync patterns — a transcription mistake would fail loudly, not
  silently; (2) the existing `Efm`/`FluxDemodulator`/`FluxDecoder` xUnit suite passes unchanged against
  the real table; (3) a throwaway probe comparing the real table's channel statistics for constant-byte
  and scramble-defeating inputs against 200 synthetic sectors, used to ground the fourth item below in
  real numbers rather than assumption.
- One genuine, non-obvious finding fell out of this: `WeakSectorAnalyzer`'s test suite failed once the
  real table went in — not because anything broke, but because the *modelled* codebook's incidental
  behavior had been masking the real signature. Content chosen to defeat CD scrambling (data equal to the
  scramble sequence, so scrambling recovers all-zero) turns out, under the authentic table, to have an
  almost normal transition density but a Digital Sum Value excursion 50-100x any ordinary sector's —
  because scrambling exists specifically to keep content balanced on the channel, and that's exactly the
  case that balancing can't fix. `WeakSectorAnalyzer.Analyze` now flags either signature (density collapse
  OR DSV blowout) instead of density alone, and its tests check the DSV excursion directly. This is a
  more physically correct detector than existed before this change, discovered specifically by finally
  measuring the real channel instead of a stand-in for it.
- This is a `DiscForge.Core`-only change — no WinForms, no App code touched — so unlike every UI addition
  this session, it is REAL-BUILT AND REAL-TESTED, not just Roslyn-syntax-checked: `dotnet build` clean,
  and the full suite (`dotnet test`) passing 2694/2694 more than once. (One or two unrelated tests
  occasionally fail under this sandbox's memory pressure when the full 2694-test suite runs in parallel —
  confirmed by rerunning in isolation and by repeat full runs both failing differently and passing clean —
  entirely unrelated to this change; nothing in `Efm`, `FluxDemodulator`, or `WeakSectorAnalyzer` was ever
  among them.)
- What's still outside DiscForge's control: phase 3 of the moonshot — an actual RF/flux tap off a real
  optical drive's photodiode — remains real capture hardware DiscForge cannot build alone. What this
  version closes out is everything DiscForge's own code was ever blocking: given a real flux capture from
  any source, DiscForge can now decode it, not just its own synthetic round-trip.

### Added — v1.100.0: Pseudo Saturn Kai closes the one remaining save-acquisition gap, found on a deliberately skeptical re-audit

- Asked point-blank whether there was really anything left to add, rather than assuming the
  v1.96.0–v1.99.0 run had found everything — read back through every feature doc again
  specifically looking for a format DiscForge parses but stops short of acquiring or writing,
  the same shape as every tool added so far, while being honest about anything too fragmented,
  too niche, or across the detect-never-circumvent line to belong here.
- Found one real, previously-missed case: **Sega Saturn backup memory**. `SaturnSaveReader`
  parses a Saturn backup-memory image's save directory (names, comments, sizes — surfaced in
  Examine) but deliberately stops there: full save-data extraction via the block-link list was
  left unimplemented rather than risk returning wrong bytes, there's no writer at all, and
  nothing in DiscForge talks to a real Saturn's internal 32 KB RAM or a backup cartridge. Same
  acquire-and-write-back gap as the one MemcardRex/PS2 Save Builder/GCMM already fill for
  PS1/PS2/GameCube saves — Saturn was simply the one console family that slipped through those
  earlier passes.
- Added a **Pseudo Saturn Kai** button to `MemoryCardView`, alongside the other three save tools
  (own remembered path, `Settings.ExternalDumperPathPseudoSaturnKai`). Pseudo Saturn Kai is the
  established homebrew disc for dumping and restoring Saturn backup memory on real hardware — an
  actively maintained, well-known single tool for this job, not a fragmented multi-brand
  situation the way cartridge dumping was.
- Also confirmed, on the same pass, that nothing else qualifies right now: Amiga/C64/other
  floppy acquisition is already covered generically by the Floppy screen's KryoFlux/Greaseweazle
  buttons; MiniDisc, LaserDisc, VHS/Betamax and cassette/arcade-PCB formats have no native
  DiscForge support at all to build an escape hatch onto; and nothing else stops short of a
  single well-known companion tool the way this did.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of both changed files
  (`Settings.cs`, `MemoryCardView.cs`), both clean. `dforge` CLI rebuilt clean with the version
  bump; full suite `bash build.sh test`: 2694/2694 passing, unchanged (this version touches only
  App/WinForms code). **UNVERIFIED — awaiting the user's own build**, same caveat as every
  WinForms change this session.

### Added — v1.99.0: new PSP and Cartridges tiles — the two gaps flagged as needing a decision, not a guess

- The last two candidates from the feature-gap audit (v1.97.0/v1.98.0) both needed a call this
  project hadn't made yet, rather than an obvious next button on an existing screen — so each got
  its own new tile, following the same tile-grid pattern the Floppy tile added in v1.98.0 (the
  grid sizes itself from the tile count, so adding one is still a one-line change to
  `CdrwinLauncher.cs`, plus its entry in `HelpContent.cs`).
  - **PSP → new `PspView`, launches UMDGen.** Different in kind from every other external-tool
    button in the app: it isn't a ripper. A physical UMD is dumped by homebrew running on the PSP
    console itself, not by a PC talking to drive or flashcart hardware over USB, so there's no
    "capture" step for DiscForge to hand off. DiscForge already reads a UMD's filesystem and
    PARAM.SFO without decrypting anything (`psp-info`/`pbp-info`/`pbp-extract` on the CLI) but has
    no ISO editor/rebuilder of its own — UMDGen fills that gap once a dump already exists. Called
    out plainly in the view's own doc comment and its UI copy so this doesn't read as another
    dumping button by mistake.
  - **Cartridges → new `CartridgeView`, launches GBxCart RW/FlashGBX and Cart Reader.** DiscForge
    reads N64/SNES/Genesis/GB/GBC/GBA/NES ROM dumps once they exist but has no cartridge-reading
    hardware of its own — every one of those consoles is read through a flashcart's own USB
    device. Unlike optical discs there's no single dominant tool here, so rather than pick one
    flashcart brand over another, both major community options are offered: GBxCart RW/FlashGBX
    for the Game Boy family, Cart Reader (sanni's open-source Arduino dumper) for
    N64/SNES/Genesis/NES — the families GBxCart RW doesn't cover.
- Both screens are deliberately minimal — an explanatory label, the button(s), a status line —
  same shape as v1.98.0's `FloppyView`, since neither is trying to give its external tool(s) a
  DiscForge-native front end. Own remembered path for each new tool
  (`Settings.ExternalDumperPath{UmdGen,GbxCart,CartReader}`), same as every field before them, and
  the same shared `ExternalToolLauncher` for the launch itself.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of all five changed/new
  files (`Settings.cs`, `PspView.cs`, `CartridgeView.cs`, `CdrwinLauncher.cs`, `HelpContent.cs`),
  all clean. `dforge` CLI rebuilt clean with the version bump; full suite `bash build.sh test`:
  2694/2694 passing, unchanged (this version touches only App/WinForms code).
  **UNVERIFIED — awaiting the user's own build and a look at both new tiles**, same caveat as
  every WinForms change this session.

### Added — v1.98.0: PS2 Save Builder and GCMM close out the save-writer gap; a new Floppy tile for KryoFlux/Greaseweazle

- Continuing the same feature-gap audit as v1.97.0, two more clear gaps and one murkier one:
  - **PS2 Save Builder** and **GCMM** — added to `MemoryCardView`, next to MemcardRex. MemcardRex
    only ever covered PS1; DiscForge's save-writer gap is actually console-wide (Dreamcast VMU is
    still the only card format this project's own code can write to), so PS1 was never the whole
    story. PS2 Save Builder closes the same gap for PS2 saves, GCMM for GameCube `.gci` saves —
    DiscForge reads and decodes GameCube saves fine, it just never writes one back onto a card.
    Own remembered path each (`Settings.ExternalDumperPathPs2SaveBuilder`,
    `Settings.ExternalDumperPathGcmm`), same as every field before them. Added as a second button
    row (`MemoryCardView` grows from 452 to 486 tall) since the first row was already full.
  - **KryoFlux DTC** and **Greaseweazle's host software (`gw`)** — this one didn't have anywhere
    to go. DiscForge already images an ordinary floppy from a standard drive itself, and already
    reads/inspects a KryoFlux raw stream or SuperCard Pro flux file once one exists — but nothing
    in this project talks to KryoFlux or Greaseweazle capture hardware over USB, and there was no
    GUI screen for floppy work at all (floppy support has been CLI-only). Rather than bolt an
    unrelated escape hatch onto an existing screen, added a small new `FloppyView` and a matching
    "Floppy" launcher tile — the CDRWIN-style tile grid sizes itself from the tile count, so this
    was a one-line addition to `CdrwinLauncher.cs`, no layout math needed. Also added its entry to
    `HelpContent.cs` per that file's own "add a tile, add its entry" rule. `FloppyView` is
    deliberately minimal: two buttons and nothing else — it is not an attempt to give either
    vendor tool a DiscForge-native front end, only a way to launch what the user already has.
- All four buttons use the same shared `ExternalToolLauncher` from v1.95.0/v1.97.0, and the same
  contract as every external-tool button before them: DiscForge never bundles, inspects, or knows
  anything else about what they do; it starts the process the user points it at and nothing more.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of all five changed/new
  files (`Settings.cs`, `MemoryCardView.cs`, `FloppyView.cs`, `CdrwinLauncher.cs`,
  `HelpContent.cs`), all clean; hand-checked the tile grid's row/column math is computed from
  `_tiles.Length` rather than hardcoded, so adding a tile needed no other changes. `dforge` CLI
  rebuilt clean with the version bump; full suite `bash build.sh test`: 2694/2694 passing,
  unchanged (this version touches only App/WinForms code). **UNVERIFIED — awaiting the user's own
  build and a look at the new Floppy tile and the reshuffled memory-card screen**, same caveat as
  every WinForms change this session.

### Added — v1.97.0: four more external tools, chosen from a full feature audit rather than guessed at

- Asked, broadly, which other external tools the app should offer given everything it actually
  does — not "what's popular", but "where does DiscForge's own feature set genuinely stop short."
  Read through the feature docs (GUI, commands, save/card support, raw-DAO burning) end to end
  before proposing anything, specifically to avoid suggesting a tool that would just duplicate
  something DiscForge already does natively — its own DAT-matching/verification tooling
  (`dat-build`/`dat-diff`/`dat-verify`/`redump-diff`, etc.) is already strong enough that adding
  RomVault- or clrmamepro-style tools was explicitly recommended against, not added.
- Four real gaps came out of that read, each with an obvious tool and an obvious screen:
  - **Xbox Backup Creator** (`XboxView`) — DiscForge's Xbox support only understands the XDVDFS
    filesystem inside an image that already exists; it never reads an Xbox or Xbox 360 disc's
    security sectors, which live outside that filesystem and need drive-specific handling this
    project doesn't reimplement (the same posture as the CSS/AACS tools already on Read).
  - **abgx360** (`XboxView`, next to Xbox Backup Creator) — a companion step, not a replacement:
    verifies/repairs the Xbox 360 ISO the Backup Creator produces against known-good hashes.
  - **Wiimms ISO Tools (WIT)** (`ReadView`, alongside Rawdump2) — the step right after Rawdump2's
    job ends. Rawdump2 pulls a raw Wii dump off the drive, but DiscForge's own Wii support only
    reads the header/partition table — it never decrypts a Wii disc — so it can't turn that raw
    dump into a scrubbed, verifiable ISO the way WIT can.
  - **MemcardRex** (`MemoryCardView`, the memory-card screen) — DiscForge can read and extract
    PS1/PS2 cards fully but has no writer for either format (Dreamcast VMU is the only card
    format this project's own code can write to); injecting or editing a PS1 save is exactly
    MemcardRex's job.
- All four are wired up exactly like every earlier external-tool button this session: their own
  remembered path (`Settings.ExternalDumperPath{Wit,Xbc,Abgx360,MemcardRex}`, so configuring one
  never disturbs another), a picker that only asks once, and the shared `ExternalToolLauncher`
  for the actual launch. `ExternalToolLauncher` itself changed shape slightly to make this
  possible: it used to require an `EventLogView` to report into, which Read and Burn both have
  but the Xbox and memory-card screens don't (they report through a single status label instead).
  It now takes a plain `(message, isError)` delegate, with an overload that adapts an
  `EventLogView` automatically so Read's and Burn's own call sites didn't need to change at all.
- DiscForge never bundles, inspects, or knows anything else about what any of these four tools
  do — same contract as every external-tool button before them: it starts the process the user
  points it at and nothing more.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of all six changed
  files (`Settings.cs`, `ExternalToolLauncher.cs`, `ReadView.cs`, `BurnView.cs`, `XboxView.cs`,
  `MemoryCardView.cs`), all clean; hand-checked that the new fields/buttons/methods are
  referenced consistently and that the `EventLogView` overload keeps Read's and Burn's existing
  calls compiling unchanged. `dforge` CLI rebuilt clean with the version bump; full suite
  `bash build.sh test`: 2694/2694 passing, unchanged (this version touches only App/WinForms
  code, nothing Core/Devices/Cli). **UNVERIFIED — awaiting the user's own build and a
  click-through of all three screens**, same caveat as every WinForms change this session.

### Added — v1.96.0: DVDFab added to Read, alongside Xreveal and CloneBD

- Asked to add DVDFab, and where it belonged wasn't obvious enough to guess at: DVDFab is
  essentially the all-in-one version of what Xreveal (DVD) and CloneBD (Blu-ray) already do
  separately — ripping/copying protected discs to an image — but it also has a burning/authoring
  module as a secondary feature. Asked which screen it should go on; confirmed Read, alongside
  Xreveal/CloneBD rather than replacing either, since someone may still prefer a lighter
  single-purpose tool for a specific disc.
- Added a "DVDFab…" button to `ReadView`'s second external-tool row, after "Import from external
  tool…" (row 2 had room; row 1 didn't). Own remembered path,
  `Settings.ExternalDumperPathDvdFab`, so configuring DVDFab doesn't disturb Xreveal's or
  CloneBD's paths. Calls the same shared `ExternalToolLauncher` every other external-tool button
  on this view (and Burn's) already uses — no new launch logic, no layout shift needed for
  anything else on the screen.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of the two changed
  files (`ReadView.cs`, `Settings.cs`), both clean; hand-checked with `grep` that the new field/
  button/method names are used consistently everywhere they're referenced. `dforge` CLI rebuilt
  clean with the version bump; full suite `bash build.sh test`: 2694/2694 passing (unaffected —
  this version touches only App/WinForms code). **UNVERIFIED — awaiting the user's own build**,
  same caveat as every WinForms change this session.

### Fixed — v1.95.1: `build-app.ps1 -Publish` now actually produces the installer .exe, not just the payload folder

- Reported: after asking for the built app to land in "the output folder (installer)", it wasn't
  there. Root cause, found by reading `build-app.ps1` and the installer scripts end to end:
  `-Publish` only ever ran `installer\publish.ps1`, which produces the self-contained app+CLI
  folder at `.\publish\` — it never went on to compile `installer\DiscForge.iss` into the actual
  packaged installer (`installer\Output\DiscForge-Setup-<version>.exe`). That second step
  (`ISCC.exe installer\DiscForge.iss`, per `installer\README.md`) needs Inno Setup 6 and was
  always a separate manual command `build-app.ps1` never ran. So `installer\Output\` was never
  going to have anything in it no matter how many times `-Publish` was run.
- Fixed: `-Publish` now looks for Inno Setup's `ISCC.exe` (on PATH, then its two usual
  `Program Files` locations) after `publish.ps1` finishes, and if found, compiles
  `installer\DiscForge.iss` automatically — so `installer\Output\DiscForge-Setup-<version>.exe`
  now actually gets produced by a single `.\build-app.ps1 -Publish` run. If Inno Setup 6 isn't
  installed, it now says so plainly (with a link) instead of silently leaving the Output folder
  empty the way it always has.
- Updated `installer\README.md` to lead with the one-command `-Publish` path, keeping the manual
  two-step version underneath for anyone who wants to run Inno Setup separately (e.g. to open the
  `.iss` in the Inno Setup IDE instead).
- This is pure PowerShell/build-tooling, not application code — no `.csproj` source changed, only
  `build-app.ps1` and a doc. `dforge` CLI still builds clean (proves the version bump didn't
  break anything it touches, though this change doesn't touch C# at all). This sandbox has no way
  to actually run a PowerShell script end to end (no Windows, no Inno Setup, and installing
  PowerShell Core here was explicitly declined earlier this session), so the script was verified
  by careful line-by-line reading rather than execution — brace/quote balance, and that
  `Get-Command`/`Test-Path`/`Get-ChildItem` are used the way the rest of this script (and
  PowerShell generally) already uses them elsewhere in the same file. **UNVERIFIED BY EXECUTION —
  please run `.\build-app.ps1 -Publish` for real and confirm `installer\Output\` gets the setup
  .exe** before relying on this for a real release.

### Changed — v1.95.0: ImgBurn/Alcohol 120%/DAEMON Tools moved from Read to Burn — they're burners, not rippers

- Asked directly whether the eight external-tool buttons belonged on Read, Burn, or both.
  Answered honestly: five of them (RawDump2, CloneCD, Xreveal, CloneBD, IsoBuster) are rippers —
  Read is right. The other three were misplaced: ImgBurn's whole purpose is writing an image to
  disc (its "create image from disc" mode is secondary), Alcohol 120% is a burning/mounting suite
  as much as a ripper, and DAEMON Tools is primarily virtual-drive mounting with burning as a
  secondary feature — it doesn't rip discs at all. None of the three belonged on a "rip a disc"
  screen. Recommended moving them to Burn as an escape hatch for burns DiscForge's own engine
  can't do, and that's what this version does.
- `ReadView` lost the "ImgBurn…"/"Alcohol 120%…"/"DAEMON Tools…" row entirely. IsoBuster (a
  genuine general-purpose ripper/extractor, correctly placed) moved onto the end of row 1 instead
  of sitting alone on its own row, so `ReadView` is back to two external-tool rows and its
  original v1.92.1 height (536, down from 566).
- `BurnView` gained a new row of the same three buttons, right below the Image field and above
  Destination — everything below shifted down 30px to make room (view height 470 → 500).
- Extracted the launch logic both views now share — ask once for a path, remember it, launch with
  the tool's own folder as its working directory, clear a bad path on failure — into a new shared
  `ExternalToolLauncher` class (`src/DiscForge.App/ExternalToolLauncher.cs`), rather than
  duplicating `ReadView`'s existing method a second time for `BurnView`. `ReadView`'s five
  rip-tool buttons and `BurnView`'s three burn-tool buttons all call the same code now; the
  `WorkingDirectory`/path-normalization/clear-on-failure behavior from v1.90.2/v1.91.0 is
  unchanged, just no longer copy-pasted. Each button's follow-up log message differs slightly by
  context (Read's says to come back and use "Import from external tool…"; Burn's says the tool
  itself performed the burn).
- No `Settings` field names changed — `ExternalDumperPathImgBurn`/`…Alcohol120`/`…DaemonTools`
  still exist, just read from `BurnView` now instead of `ReadView`. Their doc comments were
  updated to say so.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of all four changed
  files (`ReadView.cs`, `BurnView.cs`, `Settings.cs`, the new `ExternalToolLauncher.cs`), all
  clean; cross-checked by hand that no leftover reference to the removed ReadView fields/methods
  remained (`grep` came back empty) and that `ExternalToolLauncher`/`EventLogView`/`AppLog` all
  sit in the same `DiscForge.App` namespace so no new `using` is needed. `dforge` CLI rebuilt
  clean with the version bump; full suite `bash build.sh test`: 2694/2694 passing (unaffected —
  this version touches only App/WinForms code). **UNVERIFIED — awaiting the user's own build**,
  same caveat as every WinForms change this session; the new Burn-screen row and the shifted
  Read-screen layout in particular haven't been seen rendered.

### Fixed — v1.94.0: BurnView now converts a CloneCD .ccd to BIN/CUE before burning, instead of refusing it

- Reported: a PS1 disc dumped via the new CloneCD button (v1.91.0) as `image.ccd` would not burn
  — DiscForge's Burn screen had no idea what to do with it. Root cause, confirmed by reading the
  burn path end to end: `BurnView`'s "Open…" dialog filter didn't even list `.ccd`, and more
  fundamentally, neither burn engine (`RawDaoBurnEngine`, `SptiRawDaoBurnEngine`, nor the plain
  ISO path) has ever understood anything but a CUE sheet or a raw ISO/CDI byte stream — there is
  no `DiscLayout.FromCcd(...)`, and CloneCD's three-file `.ccd`+`.img`+`.sub` layout doesn't fit
  the single-file model those engines assume regardless.
- Fixed WITHOUT touching either burn engine or teaching any live-drive code a new format — the
  standing rule against changing hardware-facing code without hardware to verify against applies
  just as much to burn as to read. Instead, `BurnView.OpenCdi` now recognizes a picked `.ccd`,
  converts it to a real BIN/CUE pair in a private temp directory via `DiscConverter` — the same
  conversion hub the existing Convert/Interop feature already uses, which already fully
  understands CloneCD on the read side (`DiscModel.cs`'s `".ccd" => ReadCloneCd(path)`, backed by
  the well-tested `CloneCdReader`) — and then hands that CUE to the exact same already-working
  CUE burn path every other CUE has always used. The burn engines never see anything but a CUE
  sheet; nothing about how they work changed at all.
- The "Open…" dialog filter now lists `.ccd` alongside the existing `.cdi`/`.iso`/`.cue` entries.
  The temp staging directory this creates is cleaned up automatically the next time an image is
  opened, and on view Dispose — best-effort, never throws.
- Because this is Core-level conversion logic, not WinForms, it's build- and test-verified for
  real in this sandbox, not just Roslyn-syntax-checked: added
  `Converts_a_ccd_image_to_bin_cue_via_the_hub` to `CloneCdTests.cs`, exercising the exact
  `DiscConverter.Read(".ccd")` → `Write(".cue")` round trip `BurnView` now calls, with a synthetic
  two-track `.ccd`/`.img` fixture. `dotnet build`/`dotnet test` on `DiscForge.Core.Tests`: builds
  clean, the new test and all 7 existing `CloneCdTests` pass; full suite `bash build.sh test`:
  2694/2694 passing (up from 2693). `dforge` CLI rebuilds clean.
- `BurnView.cs` itself (WinForms) is Roslyn syntax-only verified, same caveat as every WinForms
  change this session — the file-picker interaction and the resulting burn have not been run for
  real. **UNVERIFIED end-to-end — awaiting the user's own build and an actual burn attempt** with
  a real CloneCD-dumped PS1 image.

### Added — v1.93.0: four more general-purpose external tools — IsoBuster, ImgBurn, Alcohol 120%, DAEMON Tools

- Asked to add the same escape hatch for a further four tools, everything from the earlier
  shortlist except DiscImageCreator (explicitly excluded — it's open-source and CLI, a genuinely
  different category from the four commercial/freeware GUI tools this whole feature has wired up
  so far, and worth thinking about separately as a native-integration candidate rather than a
  shell-out target, if that's ever wanted).
- Unlike the GameCube/PS1/DVD/Blu-ray row, none of these four are tied to one console or disc
  format — IsoBuster and the others are general optical-disc utilities people already have and
  trust for various reasons, not a workaround for something DiscForge itself failed to read. Wired
  up with the identical shape regardless: a button, a remembered path
  (`Settings.ExternalDumperPathIsoBuster`/`…ImgBurn`/`…Alcohol120`/`…DaemonTools`), and a call
  through the same `LaunchExternalTool` method every other external-tool button already uses — so
  all eight buttons now share the exact same `WorkingDirectory`/path-normalization/
  clear-on-failure behavior from v1.90.2/v1.91.0.
- `ReadView` now has a third row of buttons (Y=200) below the existing two: "IsoBuster…",
  "ImgBurn…", "Alcohol 120%…", "DAEMON Tools…". The track list, rip button, progress bar, and log
  shifted down another 30px to make room (view height 536 → 566).
- "Import from external tool…" again needed no change — same reasoning as every prior round.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of both changed files
  (`ReadView.cs`, `Settings.cs`), both reporting clean. `dforge` CLI rebuilt clean with the version
  bump (0 warnings/errors); Core/Devices/Cli are unaffected — nothing here touches drive/SCSI code.
  **UNVERIFIED — awaiting the user's own build**, same caveat as every WinForms change this
  session; the third row's layout in particular is unverified pixel geometry until seen rendered.

### Changed — v1.92.1: two spelling/naming corrections on the v1.92.0 buttons

- "XReveal" corrected to "Xreveal" (the tool's actual capitalization, also known as DVD-Xreveal)
  everywhere it appeared: the button text, the file-picker title, and the code comments.
- "Wii/GameCube discs…" now names the actual tool, matching the pattern of the other three
  buttons: "Wii/GameCube discs (Rawdump2)…", and its file-picker title now says "Locate Rawdump2
  (or your Wii/GameCube dumping tool)" to match. Widening that button's text pushed the PS1 and
  DVD buttons over by 20px each on the same row (still fits comfortably within the view).
- Cosmetic only — no behavior, settings-field names, or layout logic changed. Verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of `ReadView.cs`
  (clean); `dforge` CLI rebuilt clean with the version bump. **UNVERIFIED — awaiting the user's
  own build**, same caveat as every WinForms change this session.

### Added — v1.92.0: the same external-tool escape hatch again, for DVD (XReveal) and Blu-ray (CloneBD)

- Extends the v1.90.0/v1.91.0 pattern (GameCube via RawDump, PS1 via CloneCD) to the two disc
  families where the "escape hatch" reasoning is strongest yet: DVD and Blu-ray commonly carry
  industry copy protection (CSS on DVD, AACS-class schemes on Blu-ray) that this project's
  clean-room, detect-but-never-circumvent design explicitly does not implement or reverse-engineer.
  XReveal (DVD) and CloneBD (Blu-ray) already do this legitimately as their whole purpose; shelling
  out to them is the same honest "launch it, then import its finished image" arrangement as the
  other two buttons, not DiscForge quietly gaining decryption capability of its own.
- `ReadView` now has four external-tool buttons in total: "Wii/GameCube discs…", "PS1 discs
  (CloneCD)…", "DVD discs (XReveal)…", and "Blu-ray discs (CloneBD)…", each with its own
  remembered path (`Settings.ExternalDumperPath`, `…Ps1`, `…Dvd`, `…Bluray`) so configuring one
  tool never overwrites another's — a user who dumps GameCube, PS1, DVD, and Blu-ray discs across
  different sessions needs all four remembered independently. All four call the same
  `LaunchExternalTool(getPath, setPath, pickerTitle)` method added in v1.91.0, so they all carry
  v1.90.2's `WorkingDirectory`/path-normalization/clear-on-failure fixes automatically.
- The four buttons no longer fit on one row, so `ReadView` now has two rows of them
  (Y=140 and Y=170) above the track list; the track list, rip button, progress bar and log were
  all shifted down 30px to make room (view height 506 → 536). Nothing else in the layout changed.
- "Import from external tool…" again needed no change — it already asks for whatever file the
  user picks and doesn't care which of the four tools produced it.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of both changed files
  (`ReadView.cs`, `Settings.cs`), both reporting clean. `dforge` CLI rebuilt clean with the version
  bump (0 warnings/errors); Core/Devices/Cli are unaffected — nothing here touches drive/SCSI code
  or any copy-protection scheme. **UNVERIFIED — awaiting the user's own build**, same caveat as
  every WinForms change this session; the new two-row button layout in particular is unverified
  pixel geometry until seen in a real running window.

### Added — v1.91.0: the same external-tool escape hatch, for PS1 discs via CloneCD

- v1.90.0 added "Wii/GameCube discs…" (launch a user-supplied external tool) and "Import from
  external tool…" (bring its finished image into the library) as an honest workaround for the one
  disc family this session found DiscForge's own read path genuinely cannot get past on real
  hardware. Asked to add the same thing for PS1 discs, with CloneCD as the external tool.
- No new diagnostic history behind this one — unlike the GameCube case, nothing here reports
  DiscForge failing on a PS1 disc. This is the user's existing PS1 workflow (CloneCD is a
  long-established tool in that community, in particular for exact P–W subchannel capture) getting
  the same shell-out treatment as GameCube, not a bug being routed around.
- Added a second button, "PS1 discs (CloneCD)…", right next to the existing GameCube one in
  `ReadView`. It is not a copy sharing the same remembered path — that would make configuring one
  tool silently clobber the other's setting for a user who dumps both console families — so
  `Settings` now has `ExternalDumperPathPs1` alongside the existing `ExternalDumperPath`, and
  `ReadView`'s launch logic was pulled out into one shared `LaunchExternalTool(getPath, setPath,
  pickerTitle)` method that both buttons call. "Import from external tool…" needed no change: it
  already asks for whatever file the user picks and says nothing about which tool produced it, so
  it already covered CloneCD's output the same as RawDump's.
- The shared method carries forward everything v1.90.2 fixed for the GameCube button — the
  `WorkingDirectory` fix (so a launched tool that assumes it's running from its own folder, as
  CloneCD does, doesn't crash the way RawDump did), the `Path.GetFullPath` normalization on the
  picked path, and clearing the remembered path back to `null` on a launch failure so a bad path
  re-prompts instead of repeating forever. Same DiscForge-doesn't-bundle-or-know-anything-about-it
  posture as before, same clean-room-provenance reasoning for not touching CloneCD's own code.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same
  way as every WinForms-only change this session: Roslyn syntax-only parse of both changed files
  (`ReadView.cs`, `Settings.cs`), both reporting clean. `dforge` CLI rebuilt clean with the version
  bump (0 warnings/errors); Core/Devices/Cli are unaffected by this change — nothing here touches
  drive/SCSI code. **UNVERIFIED — awaiting the user's own build**, same caveat as every WinForms
  change this session.

### Fixed — v1.90.2: the launched external dump tool crashed immediately — bug was in DiscForge's launch code, not the tool

- First real use of v1.90.0/v1.90.1's "Wii/GameCube discs…" button crashed on the user's machine:
  a ".NET Framework" unhandled-exception dialog, `System.IO.FileNotFoundException: Could not find
  file 'C:\dev\DiscForge\rawdump.exe'`. DiscForge's own log showed "Launched rawdump.exe..." right
  before the crash, meaning the `Process.Start` call itself had already succeeded — so the failure
  had to be happening somewhere else. Asked for the full exception details rather than guessing.
- The pasted stack trace's "Loaded Assemblies" section named `mscorlib, Assembly Version: 2.0.0.0`
  — a .NET Framework 2.0/3.5 assembly DiscForge (.NET 8, `System.Private.CoreLib`) never loads.
  That's conclusive: the crash was happening **inside RawDump.exe itself**, not in DiscForge.
  RawDump's own startup code (`Form.OnLoad` → obfuscated `i.b()`/`i.a(string)`) opens a file using
  just its bare filename `"rawdump.exe"`, which only resolves correctly if the process's current
  working directory happens to be RawDump's own folder.
- It wasn't. `LaunchExternalDumper()` called `Process.Start(new ProcessStartInfo(path) {
  UseShellExecute = true })` without setting `WorkingDirectory`, so the child process inherited
  DiscForge's own working directory instead — `C:\dev\DiscForge`, itself inherited from
  `build-app.ps1`'s `Set-Location $Repo`. RawDump's relative file lookup then looked for
  `rawdump.exe` in the DiscForge repo folder instead of its own, and failed immediately.
- Fixed by setting `WorkingDirectory = Path.GetDirectoryName(path)` on the `ProcessStartInfo`, so
  any tool launched this way gets its own folder as its working directory — the assumption most
  small utilities like RawDump make. Also normalized the picked path via `Path.GetFullPath` before
  storing it, and now clear the remembered `Settings.ExternalDumperPath` back to `null` if a launch
  throws, so a bad remembered path re-prompts the file picker next time instead of repeating the
  same failure forever.
- This was a DiscForge bug, but not a hardware/SCSI one — no live-drive code touched. Verified via
  the same Roslyn syntax-only parse used for every WinForms-only change this session (`ReadView.cs:
  syntax OK`); `DiscForge.App` still cannot be built for real in this sandbox. `dforge` CLI and the
  buildable projects (Core/Devices/Cli) are unaffected. **UNVERIFIED against the real crash** —
  awaiting the user rebuilding (`.\build-app.ps1 -Test -Run`) and retrying the button with RawDump.

### Changed — v1.90.1: renamed the v1.90.0 button to say what it's actually for

- "Launch external dump tool…" was accurate but generic. Renamed to "Wii/GameCube discs…", since
  that's the specific, known case this button exists for. Cosmetic only — no behavior change.

### Added — v1.90.0: an escape hatch to an external dump tool, for discs neither v1.88.0 nor v1.89.0 could get past

- The user retested v1.89.0's streaming-recovery attempt cleanly (cooked mode, same disc, same
  drive) and it did not help either — identical `Medium error: ASC 0x11 ASCQ 0x00` at LBA 300.
  Two separate, legitimate recovery techniques (an alternative read command in v1.88.0, and the
  standard SCSI streaming/no-error-correction mechanism in v1.89.0) both failed on this real
  hardware. That's a real, informative result, not a wasted two versions: it means this specific
  drive doesn't honour those standard mechanisms for this disc, and getting further would require
  the vendor-specific technique GameCube-dumping tools (FriiDump, Rawdump) fall back to — reading
  data straight out of a drive's own internal memory cache via commands reverse-engineered per
  drive chipset. That's real, undocumented, drive-model-specific territory this project has no
  documentation for and no way to develop against safely without the exact hardware in hand for
  every iteration — not something to attempt from diagnostic logs alone.
- Asked whether to bring the (separate, already-working) third-party tool the user was already
  using — Rawdump 2.0 — into DiscForge directly. Confirmed it's closed-source freeware with no
  public repository or stated license, and does exactly the vendor-specific cache-read technique
  above (GC-Forever wiki: "RawDump uses vendor commands... which allows the read error buffer to
  be dumped"). Bundling or copying it would break this project's clean-room provenance and could
  be a real license/redistribution problem; reimplementing its technique from scratch would need
  the same hardware-in-hand iteration this project doesn't have for this drive. Rawdump also turned
  out to be GUI-only — no documented command-line mode — confirmed directly from a screenshot of
  its own window (drive picker, "Start Dump", "Convert raw to .iso").
- Given all of that, added the option the user actually asked for and that fits the tool's own
  GUI-only nature: `ReadView` now has "Launch external dump tool…" (asks once for the tool's path,
  remembered in settings, then just starts that process — DiscForge sends it no commands and knows
  nothing about what it does) and "Import from external tool…" (copies whatever image the tool
  produced into the user's chosen library location, and says plainly that the image hasn't been
  read or verified by DiscForge itself, unlike a real rip). This is a deliberately thin, honest
  integration: a launcher and a file copy, not an automation of a closed tool DiscForge doesn't
  control or understand — appropriate for a disc format this project has now made two genuine,
  documented attempts to support natively and hit a real, identified hardware wall on both times.
- `DiscForge.App` (WinForms) still cannot be built for real in this sandbox — verified the same way
  as every other GUI-only change this session: Roslyn syntax-only parse of the two changed files
  (`ReadView.cs`, `Settings.cs`), cross-checked that the one new cross-namespace reference
  (`Settings.ExternalDumperPath` from `DiscForge.App.Views`) matches an already-compiling pattern
  elsewhere in the same file family (`InspectView.cs`'s `Settings.AddRecent`, same namespace
  nesting). `dforge` CLI still builds clean; Core/Devices/Cli are unaffected by this change.
  **UNVERIFIED — awaiting the user's own build**, same caveat as every WinForms change this
  session.

### Added — v1.89.0: a standard "streaming read" recovery attempt for cooked-track sectors nothing else can read

- v1.88.0's `READ CD`/Mode 1 fallback (added for the same real GameCube disc report) did not get past
  the failure on the user's actual hardware — same disc, same drive, same `Medium error: ASC 0x11
  ASCQ 0x00` at LBA 300. A genuine negative result, not a wasted change: it ruled out "the drive
  just needs a different read command" as the whole story.
- Researched how community GameCube/Wii-dumping tools (FriiDump, Rawdump) actually get past this:
  GameCube discs use non-standard sector scrambling, so a normal read's error-detection check (EDC)
  fails outright and the drive reports exactly this error. These tools use a "streaming" read that
  tells the drive to hand back the bytes without insisting they pass that check — for the toughest
  cases they go further still, into vendor-specific commands that read a drive's internal memory
  cache directly, which is real but drive-chipset-specific reverse-engineering, not something to
  attempt without hardware and firmware documentation this project doesn't have.
- The streaming-read half, though, has a standard, spec-defined SCSI/MMC equivalent that doesn't
  need any vendor-specific knowledge: mode page 0x01 (Read-Write Error Recovery). Setting RC (Read
  Continuous) and DCR (Disable Correction), and forcing Read Retry Count to 0, tells the drive to
  hand back data without its normal ECC correction pass or retries — the same intent as a
  "streaming" read, through a documented, standard mechanism.
- Added `TryStreamingRecoveryRead`: reads the drive's own current error-recovery page (MODE
  SENSE), changes only the specific bits above (the same read-modify-write pattern already proven
  working elsewhere in this codebase, in `SptiRawDaoBurnEngine`'s write-parameters handling — not
  a hand-built page), issues one `READ CD` Mode 1 request, and — in a `finally` block, so it always
  runs, success or failure — writes the drive's original page straight back. Wired in as a genuine
  last resort in two places: `Probe()`'s cooked-track fallback (after the v1.88.0 `READ CD`
  attempt), and `TryHarder`'s cooked-track Rung 3 (as a new Rung 4) — both only reached after every
  normal request shape has already failed.
- Why this is safe to ship despite touching real drive state: it only ever runs on a sector that
  has already failed every existing recovery attempt, so a sector that reads fine today never goes
  near this code. And because the change is temporary and explicitly restored immediately
  afterwards, it cannot leave the drive's error correction degraded for any other read — this
  track, this disc, or the next one — the way permanently disabling ECC correction for an entire
  read would (which was deliberately NOT done, since that would make an ordinary scratched DVD
  *more* likely to lose data, not less, for every read after the first genuine failure).
- Verified with a real `dotnet build` of `DiscForge.Devices` at `net8.0-windows` (0 warnings, 0
  errors), a clean `dforge` CLI build, and 2693/2693 tests passing. **Not yet verified against the
  actual GameCube disc and drive that reported this failure** — same as v1.88.0, that can only
  happen on the user's own machine. Unlike the still-open jitter-boundary gap from v1.87.2, this
  reuses a standard, documented SCSI mechanism and an already-proven read-modify-write pattern from
  elsewhere in this codebase, with an unconditional restore, which is why it felt responsible to
  write and ship for testing rather than leave as a described-but-unbuilt idea.

### Added — v1.88.0: a READ CD fallback for DVD-mode tracks a plain READ(10) refuses — closes a real gap on GameCube-style discs

- Real hardware report: an original GameCube disc detects fine (drive enumerates, TOC reads
  cleanly as a normal single-track DVD-ROM, ~1.39 GB) but the very first test read fails outright
  — `Medium error: ASC 0x11 ASCQ 0x00` ("Unrecovered read error") — on a stock, unmodified
  HL-DT-ST DVD-ROM GDR8164B. A separate third-party tool (Rawdump 2.0) reads the same disc on the
  same unmodified drive successfully, which rules out "this drive physically cannot do this" —
  the drive can; DiscForge just wasn't asking it the right way.
- Root cause, found by reading the actual read path rather than guessing: GameCube discs report a
  completely normal-looking DVD-ROM TOC (which is why detection and the TOC read succeed), but
  their sector data uses a non-standard encoding underneath. DiscForge reads DVD-mode ("cooked",
  2048-byte) tracks using only the plain `READ(10)` command, and when a drive's firmware refuses
  that command outright for a given sector, there was no fallback for cooked tracks at all —
  `DiscReader.Probe()`'s only fallback logic was `!cooked`-gated (raw/CD tracks only), and the
  per-sector retry ladder in `ReadChunkSectorBySector` only escalates to its alternative-request-
  shape logic (`TryHarder`) at a track boundary or on a specific "type rejection" sense code —
  neither of which this failure is. So a cooked track hitting this exact failure, anywhere, had
  no escalation whatsoever between "retry N times identically" and "give up."
- The interesting part: the fix already existed in the codebase, just unreachable here. `TryHarder`
  already has a "Rung 3, cooked only" step that issues `READ CD` asking for Mode 1 user data — a
  different firmware code path than `READ(10)`, and the standard technique GameCube-dumping tools
  use to get past exactly this wall. It was written for a different call site and never wired into
  either the initial probe or triggered by this particular failure code.
- Fixed by reusing that same already-proven command in two places: (1) `Probe()` now tries it for
  cooked tracks before reporting the test read as failed, so a disc this drive can actually serve
  via `READ CD` isn't rejected before the rip even starts; (2) the per-sector retry ladder now also
  reaches `TryHarder` for any cooked-track failure, not just boundary/type-rejection cases, so the
  same fallback is available during the real read too. Both changes are purely additive — they only
  ever run after the existing path has already failed, so a sector that already reads successfully
  today is completely unaffected, and a sector this doesn't help for reports the exact same failure
  as before.
- Verified with a real `dotnet build` of `DiscForge.Devices` at `net8.0-windows` (the actual
  shipping target — 0 warnings, 0 errors), a clean `dforge` CLI build, and the full suite passing
  2693/2693. **Not yet verified against the actual GameCube disc and drive that reported this
  failure** — that can only happen on the user's own machine, and per this project's standing rule
  the underlying read-command logic wasn't touched blind: this reuses an already-shipped, already-
  trusted command (`TryHarder`'s existing Rung 3) in two new call sites, rather than introducing
  anything new or unproven.

### Fixed — v1.87.2: a real hardware read failure was reported as a misleading "No sense (status 0x00)"

- First real diagnostic from actual hardware (a Plextor PX-W5224A CD-R burner) hit a read failure at
  LBA 475 during jitter-corrected audio reading. The error surfaced as `Medium error: No sense
  (status 0x00)` — which, read literally, means the drive said everything was fine, the opposite of
  what happened. Root cause traced to `SptiDevice.SendCommand`: when `DeviceIoControl` itself returns
  `false` (a driver-level failure with no SCSI sense data at all) *and* `Marshal.GetLastWin32Error()`
  also reads back as 0, `SptiResult.Describe()` had no branch for that case and fell through to the
  default `SenseKey` switch, which defaults to 0 ("No sense") and prints "No sense (status 0x00)" —
  a message that actively says the opposite of the truth.
- Fixed by adding a new `IsUnexplainedFailure` property to `SptiResult` (`!Success && ScsiStatus ==
  0 && Win32Error == 0 && no real sense data`) and a matching branch in `Describe()`, checked right
  after the existing `IsDriverLevelFailure` case (which at least has a Win32 error to show) and
  before the `SenseKey` switch. The new message says plainly that the request didn't complete and
  neither Windows nor the drive gave a reason, explicitly notes this is *not* a real "no sense"
  response from the drive, and that it often clears on retry and isn't by itself evidence of damaged
  media.
- This is a message-only change. It does not alter `Success`, retry counts, or any read/silence/
  boundary-tolerance behavior anywhere in `DiscReader` — proved by walking `SendCommand`'s own two
  branches: `!Success && ScsiStatus == 0` can only arise from the `ok=false` (driver-level) path, and
  `IsDriverLevelFailure`/`IsUnexplainedFailure` are a clean, non-overlapping split of that same path
  by whether a Win32 error code came back with it.
- Verified with a real `dotnet build` of `DiscForge.Devices` at `net8.0-windows` — the actual
  shipping target for this file, not a syntax-only check — "Build succeeded, 0 Warnings, 0 Errors."
  `dforge` CLI also builds clean, and the full suite passed 2693/2693 in this sandbox (the earlier
  `AudioCdTests` sandbox-only flake did not recur this run). Not yet exercised against the real
  drive that produced the original failure — that's the one verification this sandbox can never
  provide.
- **What this does NOT fix**: a second, separate real diagnostic from the same session (after
  unchecking "Correct audio jitter" as a workaround) hit a different, later failure at LBA 153,903 —
  a genuine SCSI medium error (`Medium error: uncorrectable read error`, real sense data, status
  0x02). That one is not a bug: it's the software correctly reporting real physical read trouble on
  the disc, with an already-useful message (clean the disc, or tick "continue past unreadable
  sectors" to salvage the rest). No code change was made for it.
- Also, deliberately NOT fixed: the first diagnostic's underlying gap is that
  `ReadAudioWithJitterCorrection` only tolerates boundary sectors at the *tail* of a track (via
  `TailWindowSectors`), not the *head* — so a real head-boundary read hiccup like this one has no
  fallback path the way the general (non-jitter) `ReadTrack` path does. That's a real, live-hardware
  retry/tolerance change, and per this project's own rule, it is not being touched without hardware
  to verify it against. This message fix makes the failure honest and actionable; it does not make
  it go away.

### Fixed — v1.87.1: `build-app.ps1` failed on its own leftover process — `-Run` never closes what it launches

- Second real build attempt (this time with a fresh rebuild after v1.87.0) failed — not a code bug
  this time, but `build-app.ps1` tripping over itself: `MSB3027`/`MSB3021`, "Could not copy
  `DiscForge.Core.dll`/`DiscForge.Devices.dll`... The file is locked by: DiscForge (40472), DiscForge
  (11788)". Root cause: every `-Run` launches `DiscForge.exe` as a detached process and never closes
  it, so two prior `-Run` invocations had left two `DiscForge.exe` windows open, both holding the
  App's own copy of `DiscForge.Core.dll`/`DiscForge.Devices.dll` locked — the next build's copy step
  (App → its own bin folder) couldn't overwrite files the previous build's own output was still
  running and using. Ten retries, then a hard failure, same behavior any time a previous run's window
  is left open across a rebuild.
- Fixed in `build-app.ps1`: before the solution build, look for a running `DiscForge.exe` whose
  process path exactly matches the App's own build output path for the configuration being built,
  and stop it first (`Stop-Process -Force`, plus a short sleep for the OS to release the file
  handles). Matched by full path, not just process name, so this can never touch an unrelated
  program that happens to also be called `DiscForge.exe` elsewhere on the machine.
- Also fixed a latent PowerShell gotcha in the same new code: a single matching process is a scalar
  in PowerShell, not an array, and `.Count` on a bare scalar is unreliable across PowerShell 5.1 vs.
  7+; wrapped the match in `@(...)` so `.Count` is always correct regardless of 0/1/N matches.
- Not independently verified against a real PowerShell parser — `pwsh` isn't available in this
  sandbox (and installing it was previously declined) — but the change is a small, self-contained
  block using only patterns already proven elsewhere in this same file (`Get-Process`,
  `Where-Object`, `Stop-Process`), and was read back carefully for syntax. **Worth confirming the
  next `-Run` → rebuild cycle actually closes the old window automatically before trusting this
  fully.** `dforge` CLI still builds clean in this sandbox after the edit. Version bumped across all
  four projects for consistency (no Core/Devices/Cli/App product code touched).

### Confirmed — v1.87.0: the user rebuilt after the v1.86.1 fix and reported it clean — GUI-parity backlog is now build-confirmed for real, not just Roslyn-syntax-checked

- The user re-ran `.\build-app.ps1 -Test -Run` on `C:\dev\DiscForge` after the one-line
  `DiscMriView.cs` fix and reported it good — no further errors pasted back. Taken at face value:
  the whole solution (Core, CLI net8.0 + net8.0-windows, App, tests) now compiles for real on
  Windows, meaning `ProveView`, `PressingDnaView`, `DriveDossierView`, and `DiscActuaryView` — the
  four views that had never been through anything stronger than the sandbox's syntax-only Roslyn
  check — are now real-build-confirmed too, alongside `DumpCertView` (confirmed earlier) and
  `DiscMriView` (confirmed by this same run, after the fix). This is a genuine milestone: every view
  in the GUI-parity backlog this file has tracked since before this session has now compiled for
  real, not merely been argued to be syntactically well-formed.
- **What's still open, and can only be closed on the user's machine**: compiling is not the same as
  correct. Each tile still needs to actually be opened and exercised in the running app — every view
  was written as a port of already-hardware-proven CLI logic and cross-checked against real Core/
  Devices signatures, but none of the five newer ones have been clicked through yet. `ProveView`
  additionally burns a real disc and has not been run at all. The interrupt→resume hardware test
  (`hw-test-resume-auto.ps1`) is also still outstanding.
- No code change this release — a documentation/status update recording the user's confirmation.
  Version bumped across all four projects for the usual consistency; `dforge` still builds clean in
  this sandbox. The `AudioCdTests` flake reported in v1.86.1 was re-checked once more in this sandbox
  (still reproduces, 2692/2693, after clearing lingering build processes) — still believed to be
  sandbox-only; the user's own `-Test` run (if they ran it) is the evidence that actually matters.

### Fixed — v1.86.1: `DiscMriView.cs` failed the FIRST real Windows build of the GUI-parity views

- **The user ran `.\build-app.ps1 -Test -Run` for real** — the first genuine Windows compile of all
  six new GUI-parity views — and it found a real bug: `error CS0246: The type or namespace name
  'Evidence' could not be found`, `DiscMriView.cs(72,13)`. `Evidence` is a nested enum inside the
  `DiscMri` static class (`DiscForge.Core.Forensics.DiscMri.Evidence`), not a top-level type in the
  `DiscForge.Core.Forensics` namespace — `using DiscForge.Core.Forensics;` doesn't bring a nested
  type into unqualified scope, only `using static DiscMri` or explicit qualification would. Every
  other reference to it in the file (`DiscMri.Evidence.EdcFailed`, `DiscMri.Classify`,
  `DiscMri.RenderPng`/`RenderSvg`) was already correctly qualified — only the field declaration
  `private Evidence[]? _evidence;` was missed.
- **This is exactly the blind spot the sandbox's Roslyn syntax-only checker (used for every GUI file
  this session, since the sandbox can't build `net8.0-windows`/WinForms) could never catch**: CS0246
  is a semantic/type-resolution error, not a syntax error, and `CSharpSyntaxTree.ParseText` alone
  never resolves a type against real metadata. Confirmed the fix for real anyway, without a Windows
  build: compiled a throwaway one-line program referencing `DiscMri.Evidence[]` against the actual
  built `DiscForge.Core.dll` and confirmed it binds — real semantic verification, just not through
  WinForms.
- Fixed: `private Evidence[]? _evidence;` → `private DiscMri.Evidence[]? _evidence;` — one line.
  Audited all five other GUI-parity views for the same class of bug (any bare reference to a type
  nested inside another class, cross-checked against every nested `public enum/record/class` in
  `DiscForge.Core`/`DiscForge.Devices` that these views' `using` directives could reach) — found
  nothing else; `DiscMri.Evidence` was the only nested type any of the six views touch.
- Re-ran the sandbox's own verification for good measure: `DiscForge.Cli` still builds clean, and the
  full Core test suite passes (2692/2693 — the one failure, `AudioCdTests.Over_74_minutes_warns_
  that_80_minute_media_is_needed`, is an `OutOfMemoryException` in a ~770 MB synthetic-audio test that
  reproduces only inside this specific long-running sandbox session and NOT in an isolated fresh
  process calling the exact same `AudioCdCreator.Create` path with the exact same input — almost
  certainly sandbox memory/process state accumulated over a long session, not a real regression, and
  unrelated to this release's one-line GUI fix. Left the test untouched rather than "fixing" code
  that's already been shown correct in isolation; worth a clean re-run outside this sandbox to confirm
  it's not real).
- Version bumped across all four projects (patch bump, `1.86.0` → `1.86.1` — this is a real bug fix,
  unlike the last three CI-only releases).

### Added — v1.86.0: `scripts/check-commands-sync.sh` was dead code — give it a fast Linux CI job

- Found a fourth loose end while auditing the doc-sync fixes so far: `scripts/check-commands-sync.sh`
  (a bash twin of `check-commands-sync.ps1`) existed in the repo, worked correctly, and was
  referenced from **nowhere** — not `docs/NEXT.md`, not `CHANGELOG.md`, not any workflow. Confirmed
  it runs clean (341 commands, 0 missing) against a real Linux build of the CLI before wiring it in.
- **`.github/workflows/ci.yml`**: the `doc-sync` job now also builds just `DiscForge.Cli`'s `net8.0`
  target (cross-platform — no `DiscForge.Devices`/WinForms dependency at that target, so no Windows
  runner needed) and runs `check-commands-sync.sh` against it. This gives `docs/COMMANDS.md` drift
  detection on the fast/cheap Ubuntu job, well before `build.yml`'s full Windows build + installer
  compile finishes — `check-commands-sync.ps1` keeps running there too as the authoritative check
  against the real Windows target; this is an early-warning duplicate, not a replacement.
- No Core/Devices/Cli/App code change this release — CI config only, continuing the v1.84.0/v1.85.0
  pattern. Version bumped across all four projects for consistency; full test suite (2693 tests)
  still green.
- **Same delivery snag as the last two releases**: `.github/workflows/ci.yml` can't be written to the
  user's machine via the remote-device bridge ("protected file"). Delivered into the conversation as
  a download; the user still needs to place all three workflow files by hand before any of this CI
  wiring (v1.84.0 through v1.86.0) takes effect.

### Added — v1.85.0: wire the third doc-sync checker, `gen_cli_doc.ps1 -Check`, into CI too

- Auditing the two checkers just wired into CI in v1.84.0 turned up a third one that had the exact
  same problem and had been missed: `scripts/gen_cli_doc.ps1 -Check` regenerates `docs/CLI.md` (the
  literal captured `dforge` help text) and fails if it's stale, but nothing was running it either.
  It's a distinct check from `check-commands-sync.ps1` — that one verifies every command *name* is
  documented in `docs/COMMANDS.md`'s per-command descriptions; this one verifies the literal help
  dump in `docs/CLI.md` matches byte-for-byte, so a wording or formatting change in the CLI's own
  `--help` output can go stale here without tripping the other check at all.
- Verified `docs/CLI.md` is currently NOT stale (rebuilt `dforge` in this sandbox, ran the same
  parsing logic in Python since `pwsh` isn't available here, confirmed byte-for-byte match) before
  wiring the check in, so this release adds CI protection without also being a drift-fix itself.
- **`.github/workflows/build.yml`**: new step "docs/CLI.md matches the CLI's own help", right after
  the `check-commands-sync.ps1` step and using the same already-built `dforge.dll`, running
  `scripts\gen_cli_doc.ps1 -Check`.
- No Core/Devices/Cli/App code change this release — CI config only, same as v1.84.0. Version
  bumped across all four projects for consistency; full test suite (2693 tests) still green.
- **Delivery note**: `.github/workflows/*.yml` files are blocked from being written to the user's
  machine via the remote-device bridge ("protected file") — this affected v1.84.0's two workflow
  edits too. All three workflow files (`ci.yml`, `build.yml` — now with three doc-sync steps total
  — and this release's further edit to `build.yml`) have been delivered into the conversation as
  downloadable files; the user needs to place them into `.github/workflows/` by hand.

### Added — v1.84.0: wire the two doc-sync checkers into CI, so drift can no longer accumulate silently

- Both doc-sync checkers fixed by hand this session (`check-commands-sync.ps1` in v1.78.0,
  `gen_gui_doc.py --check` in v1.83.0) existed for months without ever running automatically — they
  only helped when someone remembered to invoke them, which is exactly how 51 undocumented CLI
  commands and 3 launcher tiles with no help entry accumulated in the first place. Both `CHANGELOG.md`
  and `docs/NEXT.md` flagged this as worth fixing after each drift-fix; this closes that loop.
- **`.github/workflows/ci.yml`**: new `doc-sync` job (Ubuntu, no .NET build needed) runs
  `python3 scripts/gen_gui_doc.py --check` on every push/PR — `docs/GUI.md` now fails CI the moment
  it drifts from `HelpContent.cs`/`CdrwinLauncher.cs`, instead of waiting for someone to notice.
- **`.github/workflows/build.yml`**: new step "docs/COMMANDS.md matches the CLI's own help", right
  after the Release build (which produces the `dforge.dll` the script shells out to), running
  `scripts\check-commands-sync.ps1`. This one had to live in the Windows build job rather than
  `ci.yml`'s Linux job because the script needs the actual built CLI, not just source text — it
  invokes `dforge` and diffs its live `--help` output against `docs/COMMANDS.md`.
- `scripts/gen_gui_doc.ps1 -Check` (the PowerShell twin of the one now wired into `ci.yml`) is
  **not** separately wired in: it would be redundant with the Python version doing the same check
  on every push, and `ci.yml`'s Linux runner can't execute pwsh scripts against Windows-only code
  paths anyway. It stays available for local use on Windows, kept in step per its own file header.
- No Core/Devices/Cli/App code change this release — CI config only. Version bumped across all four
  projects for the usual cross-project consistency; full test suite (2693 tests) still green.

### Fixed — v1.83.0: `docs/GUI.md` doc-sync drift, same class of bug as `docs/COMMANDS.md` in v1.78.0

- **`scripts/gen_gui_doc.py --check` found 3 launcher tiles with NO `HelpContent.cs` entry at all**
  (`datbuild`, `textures`, `verify`) — real, pre-existing drift unrelated to this session's own
  6 new GUI-parity tiles, caught only because `gen_gui_doc.py` is plain Python and runs in this
  sandbox (unlike `check-commands-sync.ps1`, which needs a Windows build). Added all three entries
  to `HelpContent.cs`, describing what `DatBuildView`/`TextureView`/`VerifyView` actually do.
- The 6 GUI-parity tiles added this session (`dumpcert`, `prove`, `pressingdna`, `drivedossier`,
  `discactuary`, `discmri`) were also undocumented in the *grouping* sense — present in
  `HelpContent.cs` so the drift check passed, but falling into the generator's catch-all "Other"
  section rather than a real category. Added a new "Preservation certificates & forensics" group
  (also picking up the pre-existing `merge`/`mergecert`/`secureripplan`, which had the same issue)
  to both `scripts/gen_gui_doc.py` and its PowerShell twin `scripts/gen_gui_doc.ps1` — kept
  identical between the two, as the file header for each promises. `datbuild` joined "Collection &
  front-end", `textures` joined "Console & cartridge preservation", `verify` joined "Identify,
  verify & catalogue".
- Regenerated `docs/GUI.md` (64 tiles, up from a stale count); `gen_gui_doc.py --check` now reports
  clean with zero ungrouped-launcher-tile warnings. `docs/GUI.md`'s remaining "Other" section (VOB
  Demux, Video CD, IFO Editor) is a legitimate small miscellany, left as-is.
- No Core/Devices/Cli change this release — version bumped across all four projects for the usual
  cross-project consistency, same as every doc-only release before it.

### Added — v1.82.0: `DiscMriView` — sixth and final GUI parity view; the GUI-parity backlog is now closed

- **`DiscMriView`** (new, `DiscForge.App.Views`): a WinForms view over `dforge disc-mri` — renders
  per-sector evidence as a polar map of the PHYSICAL disc (real Red Book spiral geometry), so damage
  shows its true shape (a radial streak is a scratch, a ring is a pressing defect, a bloom from the
  hub is rot, a solid outer band is a muted/failed region). Open a raw `.bin` or a single-file
  `.cue` (a cue supplies per-track audio/data knowledge, resolving the sync-less-sector ambiguity a
  bare bin has); a dump's `.badsectors.json` sidecar is auto-detected next to the image or picked
  explicitly. The cue/span-resolution logic is a verbatim port of `DiscMriCmd`'s own block in
  `Program.cs` (CLI-internal, not a public API); `DiscMri.Classify`/`RenderPng`/`RenderSvg` are
  called as the real public API, all cross-checked against source.
- **The first GUI-parity view that renders an image rather than just reporting text.** WinForms has
  no native SVG renderer, so the on-screen preview always calls `DiscMri.RenderPng` (the CLI's own
  PNG path) into a `PictureBox`; Save As offers both `.svg` (map + legend, the CLI's default) and
  `.png` (bare map), picking the renderer the chosen extension implies — the same branch the CLI
  takes. The PNG bytes are decoded into a `Bitmap` copy and the source `MemoryStream`/`Image` are
  disposed immediately after, avoiding the classic WinForms `Image.FromStream` gotcha where the
  image stays lazily bound to a stream that's about to go out of scope.
- **This has NOT been built or run** — same standing caveat as every GUI change in this sandbox, and
  the least evidence of correctness of the six so far given it's the only one with actual pixel
  rendering to get right, not just text formatting. **Run `.\build-app.ps1 -Run` and open the
  "Disc MRI" tile with a real .bin/.cue before trusting it** — check that the preview actually shows
  a sensible-looking polar map, not just that nothing throws.
- **This closes the GUI-parity backlog** that `docs/NEXT.md` has tracked since before this session:
  `DumpCertView` (v1.77.0, confirmed built), `ProveView` (v1.78.0), `PressingDnaView` (v1.79.0),
  `DriveDossierView` (v1.80.0), `DiscActuaryView` (v1.81.0), and now `DiscMriView` (v1.82.0) cover
  every view `docs/NEXT.md` had flagged as missing. Five of the six remain unverified pending a real
  Windows build — running `.\build-app.ps1 -Run` (or `-Publish`) and clicking through all six tiles
  is the natural next step before any of this is trusted as shipped, not just written.

### Added — v1.81.0: `DiscActuaryView` — fifth GUI parity view

- **`DiscActuaryView`** (new, `DiscForge.App.Views`): a WinForms view over `dforge disc-actuary` —
  per-disc longitudinal scan history (tier1/tier2/uncorrectable — C1/C2/CU on CD, PIE/PIF/POF on
  DVD) fitted to a first-order decay model, so it can say how long a disc has LEFT and, across a
  whole collection, rank which discs to re-dump first. Record a scan by typing its maxima directly
  or by importing a scan file (`QualityScanImport.Parse` — Nero DiscSpeed, opti-drive, csv, and
  more, exactly as the CLI's `--record <scan-file>` path does), optionally condition the trend fit
  on a specified storage temperature/humidity, and rank an entire collection's urgency in one click.
  This is the least live-hardware-adjacent of the five views so far — unlike `DriveDossierView`, it
  doesn't even read drive capabilities; "drive" on a recorded scan is just a free-text label, same
  as the CLI treats it. New "Disc Actuary" tile on the launcher and a matching `HelpContent.cs`
  entry.
- Every `DiscActuary`/`DiscScanHistory`/`ActuaryScan`/`RotKinetics`/`StorageEnvironment`/
  `QualityScanImport` API call was cross-checked against source (same discipline as the other four
  views), and the three edited/new files (`DiscActuaryView.cs`, `CdrwinLauncher.cs`,
  `HelpContent.cs`) Roslyn-parsed clean.
- **This has NOT been built or run** — same standing caveat as every GUI change in this sandbox.
  **Run `.\build-app.ps1 -Run` and open the "Disc Actuary" tile before trusting it.**
- Remaining GUI-parity gap after this release: only the Disc-MRI heatmap (SVG/PNG rendering) — the
  first of the six candidate views that needs to draw an image rather than report text, and likely
  the most involved of the batch for exactly that reason.

### Added — v1.80.0: `DriveDossierView` — fourth GUI parity view

- **`DriveDossierView`** (new, `DiscForge.App.Views`): a WinForms view over `dforge drive-dossier` —
  the per-drive institutional memory that accumulates observed behaviour across every operation on
  one physical drive (mute signatures, C2 pointers crying wolf on the opening sector, a confirmed
  AccurateRip offset, overread reach), distilled into warnings the next dump can see before it
  repeats a hard lesson. Detect a drive (read-only `DriveDetector.DetectAll()`, the same call
  `BurnView`/`ProveView` already use for their destination lists) or type a vendor/model directly to
  load or start its dossier, then optionally add an observation by hand (category/detail/value,
  mirroring `--observe`). Backed by `DriveDossierStore`/`DriveDossier`/`DriveKnowledgeBase` exactly
  as the CLI uses them — local JSON under `%AppData%\DiscForge\drives` by default, a `--dir`-
  equivalent folder override in the view. New "Drive Dossier" tile on the launcher and a matching
  `HelpContent.cs` entry.
- **Why this one next**: of the three GUI-parity views still missing after v1.79.0
  (`DriveDossier`, `DiscActuary`, the Disc-MRI heatmap), this one turned out to be the simplest
  despite maintaining a persistent store — its store is a single flat JSON record per drive with no
  time-series/decay math (`DiscActuary`) and no image rendering (the heatmap), and drive
  identification only ever reads capabilities, never writes anything to the drive itself. Same
  discipline as the other three views: every `DriveDossier`/`DriveDossierStore`/`DriveKnowledgeBase`
  API call cross-checked against source, file Roslyn-parsed clean.
- **This has NOT been built or run** — same standing caveat as every GUI change in this sandbox.
  **Run `.\build-app.ps1 -Run` and open the "Drive Dossier" tile before trusting it** — same
  no-live-write risk profile as `DumpCertView`/`PressingDnaView`, but still unverified until built.
- Remaining GUI-parity gap after this release: `DiscActuary` (time-series/decay math, persistent
  per-disc history) and the Disc-MRI heatmap (SVG/PNG rendering) — both more involved than the four
  views shipped so far.

### Added — v1.79.0: `PressingDnaView` — third GUI parity view, back to the low-risk (no live drive) pattern

- **`PressingDnaView`** (new, `DiscForge.App.Views`): a WinForms view over `dforge pressing-dna` —
  fingerprint a pressing from a .cue (exact track geometry/pregaps, write-offset artifacts, MCN/ISRC),
  and with a second cue, say SAME PRESSING / same title but a DIFFERENT PRESSING (naming every
  differing trait) / different discs. Pure offline analysis of local files: no live drive, no
  persistent store, no image rendering — deliberately the same lower-risk profile as `DumpCertView`
  (v1.77.0), picked over the other three remaining GUI-parity candidates (`DriveDossier` maintains a
  per-user AppData store, `DiscActuary` the same plus longitudinal history, the Disc-MRI heatmap
  renders an SVG/PNG) specifically because it's the simplest to get right on the first try. The
  cue/bin-loading glue (`LoadGenomeTracks`/`LoadPressingFingerprint` in `ProveCmd`'s neighborhood in
  `Program.cs`) is CLI-internal, not a public Core API, so it's ported verbatim into the view rather
  than reimplemented from scratch; only `DiscForge.Core.Forensics.PressingDna.Compute`/`Compare` and
  `CueSheet.Parse` are called as the real public API. New "Pressing DNA" tile on the launcher and a
  matching `HelpContent.cs` entry.
- **This has NOT been built or run** — same standing caveat as every GUI change in this sandbox
  (no `Microsoft.NET.Sdk.WindowsDesktop` targets pack here). `PressingDnaView.cs` plus the
  `CdrwinLauncher.cs`/`HelpContent.cs` edits were Roslyn syntax-parsed clean and every Core API call
  cross-checked against real signatures, same discipline as the other two GUI-parity views. Given
  `DumpCertView` (v1.77.0) already came back from a real build with 0 errors using the exact same
  pattern (offline file analysis, no live drive), this one carries a correspondingly lower risk than
  `ProveView` (v1.78.0) — but "lower risk" still isn't "verified"; **run `.\build-app.ps1 -Run` and
  open the "Pressing DNA" tile with a real .cue+.bin before trusting it.**
- Remaining GUI-parity gap after this release: `DriveDossier`, `DiscActuary`, and the Disc-MRI
  heatmap. All three are more involved than the three views shipped so far (a persistent per-user
  store for the first two, SVG/PNG rendering for the third) — worth tackling one at a time rather
  than all at once, same reasoning as before.

### Added — v1.78.0: `ProveView` (GUI parity for `dforge prove`) + `docs/COMMANDS.md` fully synced

- **`docs/COMMANDS.md` was 51 commands behind the CLI's own help**, accumulated silently across many
  releases because it's hand-curated into thematic sections rather than mechanically regenerated
  like `docs/CLI.md` — `scripts/check-commands-sync.ps1` only ever ran manually, not in CI. Ran the
  exact same check the script performs (parse `dforge`'s help, diff against `COMMANDS.md`'s
  `` `command `` entries) and added every missing command's own help text, grouped into seven new
  sections (hardware capture/drive intelligence, forensics/scoring/recovery, Aaru/CICM interop,
  GameCube extras, multi-disc sets, certification/provenance, and a catch-all) rather than a wall of
  ungrouped bullets. `docs/CLI.md` (the literal-dump reference) was also stale (293 vs. the real 347)
  and has been regenerated verbatim from help output. Both docs now report 0 missing commands
  against a real build; the command count blurb in `COMMANDS.md`'s header was corrected too
  (272 → 341, the actual unique top-level command count).
- **`ProveView`** (new, `DiscForge.App.Views`): a WinForms view over `dforge prove` — the burn +
  read-back + byte-for-byte-verify round trip, one verdict (PROVEN/FAILED). Deliberately written as
  a line-for-line port of `ProveCmd` in `Program.cs` (same Core/Devices calls — `DiscLayout.FromCueFile`,
  `RawImageGenerator.Generate`/`ProgramSectors`, `SptiRawDaoBurnEngine.Burn`, `SptiDevice`,
  `DiscReader.ReadToc`, `RawDiscReader.Read`, `RawReadbackCompare.Compare`, `RawReadbackReport.Html`
  — in the same order) rather than new logic, since the CLI path is the one already exercised
  against real hardware (the RAW DAO ladder, `read-cdi`). Every one of those signatures was
  cross-checked against source, same discipline as `DumpCertView`. Unlike `DumpCertView`, this is
  genuinely destructive — it burns a real disc, no simulation — so the view gates Start behind a
  confirmation dialog spelling that out (mirrors `BurnView`'s existing "insert media, begin?" prompt
  for the same reason) and defaults the dialog's focus to Cancel. Reuses `DriveDetector.DetectAll()`
  filtered to `CdWrite` drives for drive selection instead of free-typing a letter, to cut the risk
  of hitting the wrong drive. New "Prove" tile on the launcher (placed right after Dump Certificate)
  and a matching `HelpContent.cs` entry.
- **This has NOT been built or run** — same caveat as `DumpCertView` in v1.77.0, and a stronger one
  here: this sandbox still cannot build or run the WinForms target, AND this view drives a real burn,
  so a mistake here costs a disc, not just a wasted screen. `ProveView.cs`, the `CdrwinLauncher.cs`
  tile addition, and the `HelpContent.cs` entry were all Roslyn syntax-parsed (errors only) and every
  Core/Devices call cross-checked against real signatures, but that is NOT a substitute for actually
  running it. **Run `.\build-app.ps1 -Run` and open the "Prove" tile before trusting the UI wiring —
  and treat the first real Prove run itself as a hardware test, on media you're fine consuming, not
  as something to trust on faith just because the pilot (DumpCertView) came back clean.**
- Good news on that front: `DumpCertView` (v1.77.0) DID get built for real this session — 0 warnings,
  0 errors — confirming the pilot's Core API usage and WinForms layout compile cleanly. That's
  stronger evidence than the Roslyn syntax check alone, though it confirms compilation, not runtime
  behavior; the "Dump Certificate" tile still needs to actually be opened and exercised to call it
  fully verified.
- `hw-test-resume-auto.ps1` (the fully-automated interrupt→resume hardware test) was fixed this
  session too: it was resolving `dforge` via a bare PATH lookup, which silently found a stale
  `dforge.exe` predating the `read-cdi` command and made the script wrongly report "the rip finished
  early" instead of "the wrong binary doesn't know this command." Now prefers the freshly-built CLI
  next to the script and fails loudly on the same mistake if it recurs.

### Added — v1.77.0: `DumpCertView` — the first GUI parity pilot (built successfully — see v1.78.0 note above)
- **`DumpCertView`** (new, `DiscForge.App.Views`): a WinForms view over `DumpCertificate` — create a
  signed certificate (SHA-256 + Merkle root over the sectors, drive/firmware/settings/note context,
  optional copy-protection scan, optional ECDSA signing) for a single already-dumped image, and
  verify an existing `*.dcert.json` certificate's signature and (if the image is still alongside it)
  its hash/Merkle root. Mirrors `dforge dump-cert` (create) and `dump-cert verify`. Added as a new
  "Dump Certificate" tile on the launcher and a matching entry in `HelpContent.cs`.
- **Why this one first**: `docs/NEXT.md` has flagged "GUI lags the CLI's newest forensics" for a
  while (`PressingDna`, `DriveDossier`, `DiscActuary`, the Disc-MRI heatmap, and the Dump
  Certificate all lack WinForms views), but this sandbox cannot build or run the WinForms target at
  all, so every GUI change here is genuinely unverified until built on a real Windows machine. Rather
  than write all five views blind in one pass, this release is a single pilot — the Dump Certificate
  view, chosen because it's the simplest (a local hash/JSON operation, no live drive, no multi-step
  wizard) — so any WinForms-specific mistake (layout, event wiring, a control property that doesn't
  exist the way it's used) surfaces on one small, easy-to-fix surface before the rest are attempted.
- **Verification actually performed here**: every `DiscForge.Core.Preservation` API call
  (`DumpCertificate.Create/Sign/VerifySignature/VerifyImage/Save/Load/SidecarPath`,
  `BadSectorMap.SidecarPath/Load/UnreadableLba/BoundaryLba`, `DumpLineageLog.GenerateKey/LoadPrivateKey`,
  `CopyProtectionCatalog.FromIso`/`PhysicalCaptureCaveat`) was cross-checked directly against its real
  signature in source, not assumed from memory. The file was also parsed with Roslyn
  (`CSharpSyntaxTree.ParseText`, error-diagnostics only) to catch syntax mistakes a plain read could
  miss — real, but only a syntax check, not a semantic/type check against the actual WinForms
  assemblies, since those aren't available to compile against in this sandbox. **This has NOT been
  built or run** — that step needs `.\build-app.ps1 -Run` (or `-Publish`) on the actual Windows
  machine. Until that happens and comes back clean, treat this view as unverified, not shipped.
- No change to `DiscForge.Core`, `DiscForge.Devices`, or `DiscForge.Cli` this release — version
  bumped across all four projects only for the usual cross-project consistency.

### Added — v1.76.0: Tier-B adaptive re-read wired into `dforge extract-sectors`
- **`IExtractionRereadEscalation`** (new interface, `DiscForge.Core.Dumping`): the seam that lets
  `SectorExtraction` — pure, hardware-agnostic, fake-testable — offer a sector one more escalation
  after every plain retry (and, for audio, every jitter-consensus attempt) has already failed it,
  without `SectorExtraction` ever needing to know an `SptiDevice` exists. Mirrors the shape of
  `IExtractionReader` itself: Core defines the seam, `DiscForge.Devices` supplies the live
  implementation.
- **`ExtractionOptions.AdaptiveReread`** (new, opt-in, default `false`): when set, and only when
  `Extract` is also given a non-null `IExtractionRereadEscalation`, a sector that has exhausted
  every ordinary retry gets one more chance via Tier-B before it counts as failed. Setting the flag
  with no escalation supplied is a deliberate no-op — same shape as
  `DiscReader.ReadOptions.AdaptiveReread` (v1.74.0) for the CD track ripper. Whatever the escalation
  returns is re-run through the exact same structural proof (EDC, sync, Form check) every other
  attempt goes through — recovering bytes is a candidate, never an automatic accept.
- **`DriveExtractionReader`** now also implements `IExtractionRereadEscalation`: `TryRecover` wires
  a stubborn sector to `DriveRereadSource`/`AdaptiveReread.Run` — the same Tier-B escalation already
  proven against real hardware via `reread-probe` and wired into `read-cdi --adaptive-reread`, now
  reachable from the CLI's actual highest-traffic dump pipeline. This was the one piece the fresh
  audit had flagged as needing a real drive to validate against rather than a blind interface
  change — `DriveRereadSource` (including its own `SET CD SPEED` escalation) already existed from
  earlier hardware work, so wiring it through was a pure additive seam, not new live-drive surface.
- **`dforge extract-sectors <drive:> <out> --adaptive-reread`**: new flag, off by default. Disabled
  automatically (with a console note, same pattern as `--no-c2`/`--sub`) on DVD/BD media, since
  Tier-B's escalation strategies are CD-only (`READ CD`, C2 pointers, `SET CD SPEED`) and have no
  equivalent for a DVD/BD `READ(10)` sector.
- 8 new tests (`SectorExtractionTests`, scripted `FakeEscalation`), 2693 total passing. Verified by
  the Core test suite plus `cli`/`cli-win` build success — `DriveExtractionReader`'s new method has
  no fake-drive test double (unlike `SectorExtraction`'s own engine logic, which is fully
  unit-tested), consistent with the rest of `DiscForge.Devices`. Not yet exercised against real
  hardware — the escalation itself (`DriveRereadSource`) was already hardware-proven via
  `reread-probe` and `read-cdi --adaptive-reread`, but this specific new call site
  (`extract-sectors --adaptive-reread`) hasn't had its own burn-day pass yet.

### Added — v1.75.0: `read-cdi --resume` (track-granularity checkpointed CD rips)
- **`CdiRipCheckpoint`/`CdiRipCheckpointRecorder`** (`DiscForge.Core.Preservation`, pure/unit-tested):
  a `<out.cdi>.ripstate.json` sidecar recording which track numbers a rip already captured cleanly,
  plus a deterministic signature of the read plan (`ComputePlanSignature`: every track's number,
  start LBA, sector count, CDI mode and sector size) that a resume must match before any previous
  capture is trusted — a different disc, or a different `--raw` choice, refuses the resume outright
  rather than risk splicing sectors from two different reads into one image.
- **`DiscReader.ReadTrack`** made `public` (visibility only — the body `ReadToCdi` has always called
  internally is unchanged): lets a caller capture one track at a time instead of only through
  `ReadToCdi`'s all-tracks-in-one-call loop, which a track-granularity resume needs.
- **`dforge read-cdi --resume`**: each track is now captured to its own `<out.cdi>.trackNN.tmp`
  before the final CDI is assembled; `--resume` reuses a temp file whose checkpoint entry AND actual
  size on disk both agree with the plan, and only (re)reads the tracks that don't. This is
  track-granularity, not the sector-granularity resume `read-disc --resume` (v1.72.0) has for a flat
  ISO — a CDI's layout writes every track's data first and only builds its descriptor once every
  track is in hand (see `CdiWriter.Write`), so there is no mid-track byte offset in the final file to
  resume from the way there is for a flat ISO; what's cheap to reuse is whichever WHOLE tracks already
  read cleanly, which is exactly the common case where one stubborn track stalls an otherwise-fine
  multi-track rip. On success every temp file and the checkpoint are deleted; on a mid-rip failure the
  checkpoint reflects exactly the tracks that finished, ready for the next `--resume`.
- 11 new tests (`CdiRipCheckpointTests`), 2685 total passing. Verified by the Core test suite plus
  `cli`/`cli-win` build success (the hardware-facing pieces — `DiscReader.ReadTrack`'s visibility
  change, the CLI's per-track loop — have no fake-drive test double, same as the rest of
  `DiscForge.Devices`).
- `dforge read-cdi` also now writes a `DumpSessionInfo` sidecar (`.dumpsession.json`) after a
  successful rip, for parity with `read-disc` — drive/firmware/retry-policy provenance alongside
  the image, not just the checkpoint.

### Added — v1.74.0: Tier-B adaptive re-read wired into the CD track ripper, plus `dforge read-cdi`
- **`DiscReader.ReadOptions.AdaptiveReread`** (new, opt-in, default `false`): when a raw (2352-byte)
  sector's flat per-sector retries are exhausted, and before falling through to the existing
  boundary/type-rejection shape ladder, try one more escalation — Tier B adaptive re-read
  (`AdaptiveReread` + `DriveRereadSource`, already proven against real hardware via the standalone
  `reread-probe` diagnostic): plain re-reads, then C2-assisted reads, then a deliberately slow (4x)
  C2-assisted read, accepting the instant a data sector's own EDC validates or (audio) every byte
  reaches cross-read consensus. This is the gap the fresh audit named directly: a plain marginal
  sector that isn't at a track boundary and isn't a type rejection previously had NO escalation at
  all between "retry N times identically" and "give up" — `TryHarder`'s alternative-shape ladder is
  only ever reached for boundary/type-rejection cases. Purely additive: off by default, and it only
  changes what happens after the existing retry loop has already failed, so every existing behaviour
  (including every hardware-validated RAW-DAO/mixed-mode fix) is untouched when the flag is unset.
- **`dforge read-cdi <drive> <out.cdi>`** (new CLI command): `DiscReader.ReadToCdi` was previously
  reachable only from the WinForms GUI (`ReadView`/`CopyView`), which this sandbox cannot build or
  verify (no `Microsoft.NET.Sdk.WindowsDesktop` targets pack installed here) — so `AdaptiveReread`
  above had no way to actually be exercised outside a GUI this session can't compile. This command
  mirrors the GUI's own flow (detect drive → read TOC → probe track sector modes → `ReadPlanner.Plan`
  → `DiscReader.ReadToCdi`) as a plain, build-verifiable CLI path, with `--raw`, `--continue-on-error`,
  `--retries`, `--jitter` and the new `--adaptive-reread` all exposed. Also closes a small piece of
  the "GUI ahead of CLI" gap in the other direction: audio and mixed-mode CDs previously had no CLI
  ripping path at all (`read-disc`'s own help text pointed users at the GUI specifically for this).
- No new unit tests: `DiscReader`/`DriveRereadSource`/`TrackModeProber` are hardware-only code with
  no fake-drive test double (unlike `SectorExtraction`'s `IExtractionReader`, which is fully
  unit-tested), so this change is verified by `cli`/`cli-win` build success (both targets, the
  `#if WINDOWS` branch included) plus the full 2674-test Core suite staying green.
- **Still open**: the CLI's actual highest-traffic dump pipeline, `dforge extract-sectors`
  (`DiscForge.Core.Dumping.SectorExtraction`), has the same flat-retry gap `read-cdi` just closed for
  the track ripper, but wiring Tier B in there means widening `IExtractionReader`'s hardware-facing
  contract (a new SET CD SPEED command, an escalation-strategy parameter) — real live-drive surface,
  not a safe blind edit without a drive to validate against. Left for a dedicated hardware session,
  same discipline as `reread-probe` itself.

### Added — v1.73.0: AccurateRip tie-breaker for multi-copy audio merges
- **`AccurateRipTieBreaker`** (`DiscForge.Core.Recovery`, pure/unit-tested): closes the gap the fresh
  audit flagged — a data sector can prove itself correct via its own EDC, but an audio sector carries
  no such check, so a disagreement between two rips of an audio track had no way to be *confirmed*,
  only voted on (`MergeMethod.VoteBestEffort`). Given several candidate copies of one track and a
  parsed AccurateRip database record, `Resolve` computes each candidate's whole-track checksum and
  reports which one (if any) matches a known-good value — a real external check against thousands of
  other people's drives, not a vote. On more than one match, the higher database confidence wins; on
  an exact tie, the earliest candidate wins, mirroring the byte-vote's own existing tie convention.
- **`ProvenanceMerge.Merge`** gained an optional `audioHints` parameter (`AudioTrackHint` per audio
  track: its sector span, first/last-track flags, and the database entries to check it against).
  Before the existing per-sector vote runs, each hint's track is tried against the tie-breaker; a
  confirmed track is written whole from the winning copy and tagged the new `MergeMethod.AccurateRipConfirmed`
  in the certificate, and its sectors never reach the byte vote. A track with no confirmed match, or a
  merge given no hints at all, behaves exactly as before this change — nothing here alters the existing
  sector-vote path.
- **`MergeCertificate.AccurateRipConfirmed`** (new tally, default 0) and `MergeMethod.AccurateRipConfirmed`
  (new enum value) record how many sectors were settled this way. The new field is deliberately excluded
  from `SigningContent()`, so a certificate signed before this version still verifies unchanged, and it's
  optional (not `required`) so an old `.dmc.json` without the field still loads.
- **`dforge merge-cert`** gained optional `--cue <layout.cue> --ar-db <dBAR.bin>` flags (must be given
  together): given the disc's track layout and a downloaded AccurateRip record, builds the tie-break
  hints automatically and reports how many audio tracks were confirmed.
- 10 new tests (`AccurateRipTieBreakerTests`), 2674 total passing. Pure logic — no hardware dependency,
  no network (the database fetch remains a separate, caller-performed step, as `dforge accuraterip`
  already does).

### Added — v1.72.0: read-disc --resume (checkpointed dumps)
- **`DumpCheckpoint`/`DumpCheckpointRecorder`** (`DiscForge.Core.Preservation`, pure/unit-tested):
  a small JSON sidecar (`<image>.resume.json`) recording how far a sequential dump got — the next
  unread LBA, the disc's total sector count and block length (so a resume can refuse a checkpoint
  that doesn't match the disc actually in the drive, rather than silently splicing two different
  images together), and the retry/continue-on-error settings in force.
- **`DataDiscImager.ReadToIso`** gained an optional `startLba` parameter: identical read logic,
  retry behaviour and error handling, just a different loop starting point — so resuming never
  touches the actual READ(10)/retry machinery, only where it begins.
- **`read-disc --resume`**: on an interrupted dump (Ctrl+C, a crash, a drive that needed a reboot
  mid-read), continuing no longer means re-reading the whole disc from LBA 0. The checkpoint is
  written every ~500ms during a dump (synchronously, so an abrupt stop always leaves the last
  reported position intact, never a half-write) and deleted automatically once a dump finishes,
  whether complete or a deliberate partial with `--continue-on-error`. Without `--resume`, an
  existing checkpoint is noted but not used, so a fresh read is always the safe default.
- 6 new unit tests (`DumpCheckpointTests`): JSON round-trip, sidecar write/read/delete, capacity
  matching (refuses both a different sector count and a different block length). 2664 tests
  passing, 0 failures, clean build on `cli`, `cli-win`.
- This was the highest-value item from a fresh, independently-verified audit of what's genuinely
  left in DiscForge (as opposed to what the planning docs merely claim is left — several were
  stale). See the audit notes below for the full picture, including what's correctly deferred
  (RFC-3161 timestamps, GC junk-regenerator validation) versus genuinely still open.

### Hardware validation — cache-defeat, overread, Tier-B reread confirmed (PX-W5224A, 2026-08-29)
- **Cache-defeat: genuinely re-reads** — cold/warm read timing ~0.7 ms both ways (ratio 0.98) at
  LBA 0, no cache short-circuit detected. Matches this drive family's community reputation.
- **Overread: yes** — matches the bundled knowledge-base reference for the PX-W5224A exactly.
- **`reread-probe` at LBA 0: RECOVERED in 1 read** — correctly identified track 1 as data (control
  0x4) and validated it via real EDC/ECC against the disc. This confirms the plumbing end to end but
  only exercises the "accept on first read" path; a genuinely marginal/scratched sector is needed to
  prove the SwitchStrategy -> GiveUp escalation ladder for real.

### Added — v1.71.0: remaining hardware-validation backlog closed
- **`DriveCacheDefeatProbe`** (`DiscForge.Devices.Reading`): non-destructive timing-based
  cache-defeat probe — read a sector, read it again immediately, compare (median-of-5) timing to
  tell whether the drive genuinely re-reads the media on a repeat request or silently serves its
  own cache. Wired into `drive-profile`, which now empirically probes both OVERREAD and
  CACHE-DEFEAT (previously advertised-only/`NotProbed`) alongside the advertised capability set.
- **`RereadEvidence`** (`DiscForge.Core.Recovery`, pure/unit-tested) + **`DriveRereadSource`**
  (`DiscForge.Devices.Reading`): the correctness signals behind Tier B adaptive re-read — byte-level
  consensus across repeated real reads of a sector, reinforced by the drive's own C2 error-pointer
  bits, plus EDC/ECC verification for data sectors (Mode 1 / Mode 2 Form 1). `DriveRereadSource`
  implements `AdaptiveReread.IRereadSource` over real hardware with three escalating strategies
  (plain re-read → C2-guided → slow/careful + C2), wiring the already-proven Tier-A decision logic
  from `docs/VALIDATION-PLAN.md` to an actual drive for the first time.
- **`reread-probe`** (new CLI command): drives one real sector through the Tier-B controller to
  Recovered/GiveUp, reporting the full read history (per-attempt EDC validity, uncertain-byte
  count, strategy used). Deliberately scoped to a single sector — a validation/diagnostic tool that
  proves the real-hardware path works, not (yet) wired into `read-disc`'s own retry loop.
- Corrected a stale claim in `docs/HARDWARE_RUNBOOK.md`: the `BurnView` GUI's Verify/Test buttons
  were described as throwing `NotImplementedException` outright; in fact both are fully implemented
  for plain-ISO verify and RAW/CUE verify+test. The one remaining `NotImplementedException` is Test
  on a plain ISO via the IMAPI2 data path specifically, which has no simulated-write mode in Windows'
  own IMAPI2 API — an honest OS limitation, not missing wiring.
- 9 new unit tests (`RereadEvidenceTests`) proving the consensus/C2/EDC logic independent of any
  drive: agreement requires ≥2 reads, C2 flags override agreement, C2 bit layout (MSB-first per
  byte), valid/corrupted/unrecognized-mode EDC verification. 2658 tests passing, 0 failures.

### Hardware validation — RAW-DAO ladder complete (rungs 1–7, PX-W5224A)
- **Rung 7 (mixed-mode CUE) closed 2026-08-29**, the last open item on the RAW-DAO burn-day
  checklist. A fresh `mixed.cue` disc (data track + audio track) was burned on a Plextor
  PX-W5224A and both tracks read back and verified independently via `read-raw --track N`
  (which pulls each track's start LBA/length/field-mode from the TOC, skipping the audio
  track's unreadable pregap automatically). Data track: PASS, main channel byte-identical
  across 450 sectors. Audio track: PASS, main channel byte-identical across 350 sectors. Both
  carried only benign, drive-introduced notes (descrambled-on-read, sub-timing) — no
  addressing, protection or user-data defects.
- This closes the entire RAW-DAO/SAO hardware validation ladder end to end: plain audio,
  gapless audio, CD-TEXT, ISRC/MCN, MODE1 data, and mixed-mode discs are all now confirmed
  PASS on real hardware. Details and full console output in `docs/RAW_DAO.md` and
  `docs/HARDWARE_RUNBOOK.md`.
- No code changes — this section records a validation result, not a feature.

### Added — dump-session (drive/firmware/settings provenance sidecar)
- **`dump-session`** (`DiscForge.Core.Preservation.DumpSessionInfo`/`DumpSessionRecorder`): a small JSON
  sidecar (`<image>.dumpsession.json`) recording the "how" next to a hash manifest's "what" — the exact
  drive vendor/model/firmware, engine, retry policy, offset/C2/jitter settings, and outcome that produced
  a dump. `read-disc` now writes one automatically alongside every image; `dump-session <image>` reads it
  back. `DumpSessionInfo.ToLineageData()` flattens it into the shape `DumpLineageLog.Append(data: ...)`
  already expects, so a session folds straight into an existing signed chain-of-custody lineage instead
  of only living in its own file. Pure metadata — never touches drive I/O, so recording it alongside a
  real read/write is safe to add without risking the operation it describes. ImgBurn has no equivalent:
  its log window is ephemeral and per-session, never carried with the image it produced. 7 new tests in
  `DumpSessionInfoTests.cs`.

### Added — burn-plan (offline write-knob preview)
- **`burn-plan`** (`DiscForge.Core.Mmc.BurnCommandPlanner`, `BurnKnobs`): computes and prints a burn's
  EXACT SCSI/MMC command sequence — the Write Parameters mode page (0x05) byte-for-byte, SET CD SPEED,
  SEND OPC, RESERVE TRACK, CLOSE TRACK/SESSION — for a given set of write knobs (write type, BURN-Proof,
  link size, test-write, speed, reserve-track), entirely offline, no drive needed. Built from the same
  pure CDB builders (`WriteParametersPage`, `MmcCommands`, `SetCdSpeed`) the live SPTI burn engines use,
  so the plan is a faithful preview, not a re-derivation. This is deliberately NOT a change to the
  hardware-proven `SptiRawDaoBurnEngine` (its byte-level write-parameters logic was debugged against a
  real drive over multiple burns and is left untouched, per the standing "no blind hardware-code edits"
  rule) — it's a new, safe, no-drive-required capability: exact-command transparency and auditability
  ImgBurn has no equivalent of (it burns and logs after the fact; there's no pre-flight command preview
  to see, diff or archive before a disc is in the drive). 9 new tests in `BurnCommandPlanTests.cs`.

### Added — v1.70.0 GameCube preservation backlog
- **GC boot-chain confirmation** (`GcBi2.cs`, `GcBoot.CheckChain`): parses bi2.bin (country code,
  debug-monitor size → `LooksLikeDebugBuild`) and confirms the full boot chain — bi2 → apploader →
  DOL → FST — in one pass, collecting every problem rather than stopping at the first.
- **`gc-verify` gained padding + debug-build checks**: cross-checks padding via `GcJunkMapper`
  (surfaces intact/scrubbed/mixed/suspicious as `PaddingVerdict`, failing `Healthy` on scrubbed
  padding) and surfaces `LooksLikeDebugBuild` from bi2.bin.
- **CRC-32/Redump confirmation on junk reconstruction** (`GcJunkReconstructor`): always reports the
  finished output's own CRC-32; an optional caller-supplied `expectedCrc32` (e.g. from a Redump
  entry) gives an independent confirmation layered on top of the existing self-validation gate.
  Wired into `gc-junk-fill --expect-crc32 <hex>`.
- **GameCube-specific ring codes** (`GcRingCode.cs`, `gc-ringcode`): decodes the red manufacturing-
  date code (`AYYMDDBB`), the blue disc-identity code (embeds the disc's own 4-char game code, disc
  number, and ROM revision), and the green anomaly flag; cross-checks against caller-supplied values
  (disc header game code, expected disc number/revision) — no bundled Redump database.
- **Save banner/icon decode** (`GcSaveBanner.cs`, `gci-banner`): decodes a GameCube save's own
  banner (96×32) and first icon frame (32×32) in RGB5A3 or CI8, reusing the tiling already proven in
  the opening.bnr and TPL texture decoders. Multi-frame icon animation, directory/BAT checksum
  flagging, and `.gcs`/`.sav` containers are explicitly declined — no confidently-sourced spec found.
- **Revision/variant-aware DAT name parsing** (`DatNameTags.cs`, `dat-tags`): parses the public
  No-Intro/Redump catalogued-name convention (region, revision, disc number, demo/kiosk/beta/proto/
  unlicensed/sample flags) out of a DAT match's free-text name; composes with the GC ring-code check
  above. `dat-verify` now prints a `tags:` line for every verified match.
- **Explicitly deferred**: GameCube DTK/ADP disc-streamed audio — no public, non-confidential
  byte-level spec found (the only concrete source was a leaked Nintendo document marked
  CONFIDENTIAL, declined on clean-room grounds). The `.dsp` file-format decoder already works.

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
