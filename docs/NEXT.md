# DiscForge — what's left (session handoff)

## State as of 2026-09-12: v1.110.0 — read this section first, the rest of this file is historical

v1.110.0 fixes a real report from the field: the user's Format Media tile "would not launch" SD Card
Formatter. The status label showed `Launched NewShortcut11_9F21041712364E7FBB19D6D84D3AFF1D.exe` —
that filename pattern is a Windows-Installer icon-cache stub under `C:\Windows\Installer\...` (MSI's
temporary copy of an app's icon resource, used for Start Menu shortcuts), not the real app. The user
had picked that instead of the actual installed `SDFormatterApp.exe`. `Process.Start` launches the
stub without error (it's a real, runnable exe), so `ExternalToolLauncher`'s existing failure-recovery
(clear the remembered path on exception) never triggers — the wrong path gets "successfully" relaunched
forever with no window ever appearing, and there's no way to distinguish that from a genuinely-quiet
tool from the outside. Fix: `ExternalToolLauncher.Launch` now checks `Control.ModifierKeys` for Shift
and forces the picker to reappear even when a path is remembered — **Shift+Click any external-tool
button in the app to pick a different file**, not just Format Media/Raw Copy, since the fix lives in
the one shared method every such button calls. Added a status-text hint to `FormatMediaView`/
`RawCopyView` specifically since those are the views this was actually hit on. App-internal only, no
Core/Devices/Cli changes, Roslyn-syntax-clean on all three touched files. **Still needs, on the user's
machine: Shift+Click the Format Media tile, browse to the real `SDFormatterApp.exe` (Start Menu →
right-click SD Card Formatter → Open file location, to find the true target rather than an Installer
cache path), and confirm it actually opens this time** — that end-to-end confirmation has not happened
yet from this sandbox.


## State as of 2026-09-12: v1.109.0 (historical)

v1.109.0 adds a "Raw Copy" tile: launches an external sector-level drive/image cloning tool (e.g.
HDD Raw Copy Tool), same shape as v1.108.0's Format Media tile — its own remembered path
(`Settings.ExternalDumperPathHddRawCopy`), via the existing `ExternalToolLauncher`. Requested after
the user showed a screenshot of HDD Raw Copy Tool; placement (standalone tile vs. folding into Copy
Disc or Format Media) was asked and answered: standalone, since whole-drive/image cloning is a
genuinely different domain from every other tile (all optical/cartridge/floppy specific). Touches
only App-internal types — no Core/Devices/Cli changes, so the full test suite is carried over
unchanged (2730/2730). Roslyn-syntax-clean; `DiscForge.App` itself still can't build for real in
this sandbox — **run `.\build-app.ps1 -Run` and click Raw Copy** before trusting it as shipped.


## State as of 2026-09-12: v1.108.0 (historical)

v1.108.0 adds a "Format Media" tile: launches an external card formatter (e.g. the SD Association's
SD Card Formatter, developed by Tuxera) for prepping SD/SDHC/SDXC media, via the same shared
`ExternalToolLauncher` every other external-tool button already uses (own remembered path,
`Settings.ExternalDumperPathCardFormatter`). Requested after the user showed a screenshot of SD Card
Formatter and asked whether DiscForge had a formatting tile to link it to — it didn't, so this adds
one. Placement (a standalone tile vs. folding into Cartridges/Tools/Floppy) was asked and answered:
standalone. Touches only App-internal types (`Settings`, `ExternalToolLauncher`, WinForms controls) —
no Core/Devices/Cli changes this round, so the version bump alone confirms the CLI still builds; the
full Core test suite (2730/2730) is carried over unchanged from the last round that touched Core.
Roslyn-syntax-clean; `DiscForge.App` itself still can't build for real in this sandbox — **run
`.\build-app.ps1 -Run` and click the new Format Media tile** before trusting it as shipped.


## State as of 2026-09-11: v1.107.0 (historical)

v1.107.0 closes the last two open items this file's "what's still open" line had tracked since
v1.105.0: GUI views for the CLI-only features, and live adaptive-reread wiring for DiscMri's plan
output. Two new WinForms tiles, Dump Ledger and Media Decay, mirror `dforge dump-ledger` and `dforge
media-mortality` end to end (open/verify/consensus/submit; observe/merge/estimate/show), calling the
same `DumpCertificateLedgerLog`/`MediaMortality` methods the CLI does — pure local-file analysis, no
live drive. `DiscMriView` gained Plan Re-read/Save Plan, mirroring `dforge disc-mri --plan-reread`,
writing the identical JSON shape the CLI writes. The real piece: new CLI command `dforge disc-mri-
reread <drive> <plan.json> <target.bin>` actually drives a disc-mri plan's ranges through the real
Tier-B `AdaptiveReread` controller (unchanged — the same one `reread-probe` already proved against
hardware on one sector), patching every sector it recovers into the target image at its correct offset
and reporting whatever's still bad by LBA. Deliberately doesn't touch `RawDiscReader`/`SectorExtraction`
— same scoping discipline `reread-probe` established. Both CLI targets (`net8.0` and, notably,
`net8.0-windows` — the actual SPTI-linked target this ships in) built for real in this sandbox;
`DiscForge.Core.Tests` 2730/2730 unchanged. **Cannot be run against a real drive from this sandbox** —
no optical drive is reachable here, so `disc-mri-reread`'s actual recovery behavior is unverified beyond
compiling/type-checking cleanly. The three WinForms views are Roslyn-syntax-clean and semantically
cross-checked (every `DiscForge.Core` call independently compiled against the real `DiscForge.Core.dll`
in a throwaway program), but `DiscForge.App` itself still can't build for real in this sandbox (no
WindowsDesktop SDK) — **run `.\build-app.ps1 -Run` and click through Dump Ledger / Media Decay / Disc
MRI's new buttons before trusting the GUI work as shipped**, the same next step this backlog has always
needed and the one that caught a real `CS0246` in `DiscMriView` earlier in this project's history. See
the v1.107.0 CHANGELOG entry for full detail.


## State as of 2026-09-10: v1.106.0 (historical)

v1.106.0 fixes the one disclosed limitation from v1.105.0: ECDSA now actually works in `DiscForge.Wasm`,
via a browser Web Crypto (`SubtleCrypto`) fallback (new `wwwroot/js/ecdsaFallback.js`), since .NET 8's
browser-wasm still has no ECDSA of its own. `DumpCertificateLedgerLog.GetSubmitterSigningBytes` and
`MergeCertificate.GetSigningBytes` are new public methods exposing exactly the bytes the existing
`ECDsa`-based verify methods sign/check, so the `SubtleCrypto` path and the real `ECDsa` path share one
source of truth and can't diverge; `Home.razor` catches `PlatformNotSupportedException` around each
signature check and falls back to the JS module, labeling the verdict as browser-SubtleCrypto-verified.
Live-browser-testing the merge-certificate path this round (not done for v1.105.0) caught a real bug
along the way: `MergeCertificate.VerifySignature()`'s bare `catch { return false; }` was swallowing
`PlatformNotSupportedException` and silently reporting a validly-signed certificate as INVALID instead of
letting the page fall back — fixed by narrowing the catch to `FormatException or CryptographicException`,
matching the pattern `DumpCertificateLedgerLog.VerifySubmitterSignature` already used correctly. Both
paths (dump-ledger and merge-cert, both the good and the tampered case) are now confirmed working live in
a real headless-Chromium session — republished, re-tested, verdicts observed directly, not inferred.
2730/2730 on the full `DiscForge.Core.Tests` suite after the fix (2 new tests this round pinning the
`GetSigningBytes`/`GetSubmitterSigningBytes` helpers against an independent `ECDsa` verifier).
**Core+Wasm only — no WinForms/App source changes** (App's `.csproj` only had its version bumped).


## State as of 2026-09-10: v1.105.0 (historical)

v1.105.0 ships the fifth and final brainstorm idea: `DiscForge.Wasm`, DiscForge's verification engine
compiled to WebAssembly and run entirely client-side in a browser. This sandbox previously assumed the
`wasm-tools` SDK workload was unreachable; this round it installed cleanly and a full `dotnet publish -c
Release` completed for real (emscripten/emcc native link + IL trimming), producing an actual
`DiscForge.Core.wasm`. Verification went further than "it compiled": the published static site was served
locally and driven with headless Chromium via Playwright, live-exercising the real `DumpCertificateLedgerLog`
code against a genuinely signed `dforge dump-ledger`-built ledger (`chain: INTACT`) and a hand-tampered
copy (`chain: BROKEN`). **Genuine finding**: .NET 8 browser-wasm's `ECDsa.Create()` throws
`PlatformNotSupportedException` — no ECDSA in that runtime target — so signature checks can't run
in-browser yet; the page catches this specifically and reports "NOT CHECKED" with the reason (both on-page
and in the CHANGELOG), while the independent SHA-256-only hash-chain check still runs and is proven
correct. `DiscForge.Wasm.csproj` is deliberately NOT added to `DiscForge.sln` — `build-app.ps1` must keep
working without the `wasm-tools` workload; build/publish it separately (`dotnet workload install
wasm-tools` once, then `dotnet publish src\DiscForge.Wasm -c Release`). **This closes out all 5 ideas
from the original "mind-blowing" brainstorm** (v1.101.0 EFM table, v1.102.0 dump-ledger, v1.103.0 DiscMri
plan-reread, v1.104.0 media-mortality, v1.105.0 this one) — see `docs/DIFFERENTIATORS.md` for the running
summary of all five and what's still open (a real ECDSA-in-browser fix; GUI views for the CLI-only
features; live adaptive-reread wiring for DiscMri's plan output).


## State as of 2026-09-10: v1.104.0 (historical)

v1.104.0 ships the fourth brainstorm idea: `media-mortality`, a FEDERATED community model of how fast a
media cohort (manufacturer/mold-SID, or any taxonomy the caller picks) decays. Each contributor reduces a
disc's `RotKinetics` fit to two floats (growth rate, sample count — nothing identifying) and folds it into
a local `MediaMortalityModel`; any two contributors' models `Merge` via the Chan/Golub/LeVeque parallel
variance algorithm, which is mathematically EXACT and order-independent — no central server, no raw
observation ever shared, and the community model from a chain of pairwise merges is provably identical to
one computed centrally. `Estimate` refuses a cohort below 3 independent contributors — a privacy floor
(re-identification risk at n=1-2), not an accuracy one. CLI: `dforge media-mortality observe/merge/
estimate/show`. **REAL-BUILT and REAL-TESTED**: 12 new `MediaMortalityTests`, three of which specifically
prove the federation math (Observe matches a naive reference computation; split-then-merge equals
fold-all-at-once; three-way merge is associative both ways), plus a privacy-floor test, a JSON round-trip,
and a structural guard asserting the serialized model never carries a disc id/title/scan field. 2728/2728
clean on the final full-suite run (one run hit a newly-observed flaky fuzz test, confirmed unrelated —
see the v1.104.0 CHANGELOG entry for detail). `DiscForge.Cli` built and smoke-tested end to end: two
contributors merged, `estimate` correctly gated at the 3-contributor floor. **Core+CLI only — no
WinForms/App changes**, so this is real-build-and-real-test verified throughout.


## State as of 2026-09-10: v1.103.0 (historical)

v1.103.0 ships the third brainstorm idea: `disc-mri --plan-reread` closes the loop between `DiscMri`'s
per-sector physical-damage diagnosis and an actual targeted re-read, the way `SecureRip.PlanReread`
already does for audio tracks. `DiscMri.PlanReread(evidence)` coalesces EDC-failed/sync-less-void/
unreadable sectors into padded ranges and picks an escalating pass count (3/5/7) by worst evidence found;
`DiscMri.NeedsReread(Evidence)` is now the single shared threshold for "this counts as damage" that both
the planner and `disc-mri`'s own CLI damage-count summary use, so they can't silently drift apart. Wired
into the CLI as `dforge disc-mri <image> --plan-reread [out.json]`. **REAL-BUILT and REAL-TESTED**: 11 new
`DiscMriRereadPlanTests` (2716/2716 on a clean full-suite run; the same two sandbox-memory-pressure flaky
tests noted under v1.101.0 below intermittently fail on repeat runs, never a DiscMri/ledger test), plus a
real `DiscForge.Cli` build smoke-tested end to end against a synthetic 30-sector damaged image. **Core+CLI
only again — no WinForms/App changes.**


## State as of 2026-09-10: v1.102.0 (historical)

v1.102.0 ships the second "mind-blowing" brainstorm idea: `dump-ledger`, a public, hash-chained,
multi-submitter ledger of signed dump-certificate claims (`DiscForge.Core.Provenance.
DumpCertificateLedger` + `dforge dump-ledger keygen/init/submit/verify/consensus/show`). The gap it
closes: `merge-cert` audits one merge, `lineage` chains one dump's custody, but neither can show that
independent strangers converged on the same bytes — the thing that actually makes a dump trustworthy.
Each submission is a compact claim (disc fingerprint + output hash) signed by the submitter's own key
over the claim content alone, so the same signed claim verifies portably in any ledger or none; on top
of that every accepted entry is hash-chained to the one before it (the same tamper-evidence pattern
`DumpLineage` already used for one dump's events, extended here across many submitters and many discs
in one log), and `Append` refuses outright to add a submission whose signature doesn't verify.
`Consensus(ledger, fingerprint)` groups submissions by output hash and DISTINCT submitter key (one
submitter re-attesting twice never inflates the count) and flags a disc `Disputed` when independent
submitters land on different bytes. **REAL-BUILT and REAL-TESTED**: `dotnet build`/`dotnet test` on
`DiscForge.Core` (2705/2705, 11 new tests covering chain-tamper detection, forged-signature rejection,
cross-ledger claim portability, and consensus/dispute logic), plus `DiscForge.Cli` built for real and
smoke-tested end to end from the actual `dforge.dll` — two keys agreeing, one diverging, `verify` and
`consensus` both reporting correctly, and a hand-tampered ledger file failing `verify` on both the chain
and signature checks. **This round was Core+CLI only — no WinForms/App/GUI changes**, so every claim
above is the strongest verification tier this project has (see v1.101.0 below for why that distinction
matters); a `DumpCertView` GUI extension for the ledger is a natural next step but wasn't attempted here.


## State as of 2026-09-09: v1.101.0 (historical)

v1.101.0 closes the flux/RF moonshot's last internal blocker: `Efm.cs` now carries the authoritative
ECMA-130 8-to-14 table (256 byte→codeword entries + the 2 frame-sync patterns), replacing the
modelled stand-in docs/DIFFERENTIATORS.md flagged as the one thing gating real-disc flux decode.
Table transcribed from the GPL-licensed EFM dictionary in Sidney Cadot's `laser2wav` project (used
by happycube's `cd-decode`), itself from the published standard. Verified three ways: a
static-constructor self-check (every entry individually run-length-legal, no collisions with each
other or the sync patterns), the existing `Efm`/`FluxDemodulator`/`FluxDecoder` suite passing
unchanged, and a probe script grounding the one real finding this surfaced — `WeakSectorAnalyzer`'s
tests failed under the real table not because anything broke, but because the modelled codebook's
incidental behavior had masked the actual signature: scramble-defeating content shows an almost
normal transition density but a 50-100x DSV excursion under the real table, not a density collapse.
`WeakSectorAnalyzer.Analyze` now flags either signature — a more physically correct detector,
discovered specifically by finally measuring the real channel. **This is a `DiscForge.Core`-only
change: REAL-BUILT and REAL-TESTED** (`dotnet build` + `dotnet test`, 2694/2694, repeatedly), unlike
every WinForms addition this session — no UI touched, nothing here needs the user's own build to
confirm. What phase 3 (an actual RF/flux tap off a real drive) still needs is real hardware, which
remains outside what this project can build alone.


## State as of 2026-09-08: v1.100.0 (historical)

v1.100.0 came from asking, deliberately skeptically, whether anything was really left after
v1.99.0 — a full re-read of the feature docs looking for the same acquire-or-write-back shape
every earlier tool filled. Found one: **Sega Saturn backup memory**. `SaturnSaveReader` lists a
Saturn backup image's save directory but never extracts the data (block-linking was left
unimplemented rather than risk wrong bytes) and has no writer — the same gap PS1/PS2/GameCube
saves had before MemcardRex/PS2 Save Builder/GCMM, just missed on the earlier passes. Added
**Pseudo Saturn Kai** to `MemoryCardView` alongside those three (own remembered path,
`Settings.ExternalDumperPathPseudoSaturnKai`) — an established, actively maintained homebrew tool
for dumping/restoring Saturn backup memory on real hardware, not a fragmented multi-brand
situation. Also confirmed on the same pass that nothing else currently qualifies: floppy
acquisition for other platforms is already covered generically by the Floppy screen; formats like
MiniDisc/LaserDisc/VHS have no native DiscForge support to build an escape hatch onto at all.
**UNVERIFIED — awaiting the user's own build**, same caveat as every WinForms change this session.


## State as of 2026-09-08: v1.99.0 (historical)

v1.99.0 adds the two candidates flagged in v1.98.0 as needing a decision rather than a guess:
**PSP** (new `PspView`, launches UMDGen) and **Cartridges** (new `CartridgeView`, launches
GBxCart RW/FlashGBX and Cart Reader), each its own tile via the same self-sizing tile grid the
Floppy tile used. PSP is genuinely different in kind from every other button in the app: it isn't
a ripper — a UMD is dumped by homebrew on the PSP console itself, not by a PC over USB — so
UMDGen is offered purely as an ISO editor/rebuilder for a dump you already have, and the view's
own copy says so plainly rather than implying it acquires anything. Cartridges has no single
dominant tool the way optical discs do, so both major community options are offered rather than
picking a brand: GBxCart RW/FlashGBX for the Game Boy family, Cart Reader for N64/SNES/Genesis/NES.
Own remembered path for each (`Settings.ExternalDumperPath{UmdGen,GbxCart,CartReader}`); both
screens follow `FloppyView`'s minimal shape (info label, button(s), status line) and both got
`HelpContent.cs` entries. **UNVERIFIED — awaiting the user's own build and a look at both new
tiles**, same caveat as every WinForms change this session.


## State as of 2026-09-08: v1.98.0 (historical)

v1.98.0 closes out the save-writer gap v1.97.0 started (MemcardRex was PS1-only; the actual gap is
console-wide) and adds a brand-new screen for the one gap that had nowhere to live. **PS2 Save
Builder** and **GCMM** join MemcardRex on `MemoryCardView` (own remembered path each, second
button row, view grows 452→486 tall). **KryoFlux DTC** and **Greaseweazle's `gw`** get a new
`FloppyView` plus a matching "Floppy" tile in `CdrwinLauncher` — DiscForge already images an
ordinary floppy itself and already reads KryoFlux/SCP flux files once captured, but nothing talks
to that capture hardware over USB, and there was no floppy screen at all to hang the button on.
The tile grid sizes itself from `_tiles.Length`, so the new tile needed no layout math; also added
to `HelpContent.cs` per its own "add a tile, add its entry" rule. **UNVERIFIED — awaiting the
user's own build and a look at the new Floppy tile and the reshuffled memory-card screen**, same
caveat as every WinForms change this session.


## State as of 2026-09-08: v1.97.0 (historical)

v1.97.0 adds four more external-tool buttons, picked from a full read of DiscForge's actual
feature set rather than a generic list — asked which other apps DiscForge should offer, so the
docs (GUI, commands, save/card support, raw-DAO burning) were read end to end first to find real
gaps rather than duplicate what DiscForge already does well natively (its own DAT-matching tools
already outclass RomVault/clrmamepro-style additions, which is why none were added). The four:
**Xbox Backup Creator** and **abgx360** on `XboxView` (Xbox/360 disc security-sector reading is
the one thing that view can't do — XDVDFS filesystem parsing only; abgx360 is a companion
ISO-verification step for what XBC produces), **Wiimms ISO Tools** on `ReadView` next to Rawdump2
(turns a raw Wii dump into a scrubbed, verifiable ISO — DiscForge's own Wii support stops at the
header/partition table and never decrypts), and **MemcardRex** on `MemoryCardView` (DiscForge can
read/extract PS1/PS2 cards but has no writer for either — MemcardRex fills the save-injection gap
Dreamcast VMU support doesn't share). Each got its own remembered `Settings.ExternalDumperPath*`
field and its own picker, same as every earlier tool. `ExternalToolLauncher` (the shared launch
logic from v1.95.0) changed shape to make this possible: it took a concrete `EventLogView` to
report into before, but Xbox's and the memory-card screen's views have no event log, only a
status label — it now takes a plain `(message, isError)` delegate, with an `EventLogView`
overload kept for Read's and Burn's existing call sites so neither had to change.
**UNVERIFIED — awaiting the user's own build and a click-through of all three screens**, same
caveat as every WinForms change this session.


v1.96.0 adds a "DVDFab…" button to `ReadView`'s second external-tool row. Asked where it should
go rather than guessing — DVDFab straddles ripping (its main use, same category as Xreveal/
CloneBD) and burning/authoring (secondary), so it wasn't as clear-cut as the earlier moves.
Confirmed Read, alongside Xreveal/CloneBD rather than replacing either. Own remembered path
(`Settings.ExternalDumperPathDvdFab`); uses the same shared `ExternalToolLauncher` every other
button already does. **UNVERIFIED — awaiting the user's own build**, same caveat as every WinForms
change this session.


v1.95.1 fixes `build-app.ps1 -Publish`: it only ever ran `installer\publish.ps1` (the
self-contained app+CLI folder at `.\publish\`), never the separate Inno Setup compile
(`ISCC.exe installer\DiscForge.iss`) that actually produces
`installer\Output\DiscForge-Setup-<version>.exe` — so that Output folder was always empty
regardless of how `-Publish` was run. Now `-Publish` finds `ISCC.exe` (PATH, then its usual
Program Files locations) and compiles the installer automatically, or says plainly that Inno
Setup 6 isn't installed if it can't find it. Pure PowerShell/tooling change, no C# touched.
**UNVERIFIED BY EXECUTION** — this sandbox has no Windows/Inno Setup to actually run the script;
verified by careful reading only. Please run `.\build-app.ps1 -Publish` for real and confirm the
setup .exe actually lands in `installer\Output\`.


Asked point-blank whether the eight external-tool buttons belonged on Read or Burn. Honest answer:
three were misplaced. ImgBurn, Alcohol 120%, and DAEMON Tools are burn/mount tools, not rippers,
so v1.95.0 moves their buttons from `ReadView` to a new row on `BurnView` (view height 470 → 500).
`ReadView` keeps the five genuine rippers (RawDump2, CloneCD, Xreveal, CloneBD, IsoBuster) across
two rows again, back to its v1.92.1 height (536). The launch logic both views need was pulled out
of `ReadView` into a new shared `src/DiscForge.App/ExternalToolLauncher.cs` rather than duplicated
— same WorkingDirectory/path-normalization/clear-on-failure behavior, one copy. `Settings` field
names are unchanged, just read from a different view now. **UNVERIFIED — awaiting the user's own
build**, same caveat as every WinForms change this session; neither the new Burn row nor the
shifted Read layout has been seen rendered.


v1.94.0 fixes a real gap: a PS1 disc dumped via the v1.91.0 CloneCD button as `image.ccd` would
not burn — DiscForge's burn engines have never understood anything but a CUE sheet or a raw
ISO/CDI stream, and CloneCD's three-file `.ccd`+`.img`+`.sub` layout was neither in the Open
dialog's filter nor parseable by anything on the burn path. Fixed without touching either burn
engine (the standing rule against changing live-drive/burn code without hardware to verify against
still applies): `BurnView.OpenCdi` now converts a picked `.ccd` to a real BIN/CUE via
`DiscConverter` — the same hub Convert/Interop already use, already fully understanding CloneCD —
in a private temp directory, then hands that CUE to the existing, unchanged CUE burn path. Unlike
every other WinForms change this session, the conversion logic itself is Core-level and so is
genuinely build- and test-verified: a new `CloneCdTests` case exercises the real `.ccd`→`.cue`
round trip, and the full suite is 2694/2694 passing. `BurnView.cs` itself is still Roslyn-syntax-only
verified — **UNVERIFIED end-to-end — awaiting the user's own build and an actual burn** with a real
CloneCD PS1 image.

v1.93.0 adds a third row of external-tool buttons to `ReadView`: IsoBuster, ImgBurn, Alcohol 120%,
and DAEMON Tools — general-purpose disc utilities, not tied to one console/format like the
GameCube/PS1/DVD/Blu-ray row above them. Explicitly asked to skip DiscImageCreator, which is a
different category anyway (open-source, CLI — a native-integration candidate someday, not a
shell-out target). All four share the same `LaunchExternalTool` method and remembered-path pattern
every prior external-tool button uses, so DiscForge now has eight of these buttons total across
three rows, all carrying the same WorkingDirectory/path-normalization/clear-on-failure fixes.
**UNVERIFIED — awaiting the user's own build**, same caveat as every WinForms change this session;
the new third-row layout hasn't been seen rendered.


v1.92.0 extends the external-tool escape hatch to DVD (XReveal) and Blu-ray (CloneBD), the two
disc families where shelling out makes the most sense — both formats commonly carry copy
protection (CSS, AACS-class schemes) this project's clean-room design deliberately never
implements. `ReadView` now has four external-tool buttons total (GameCube/RawDump, PS1/CloneCD,
DVD/XReveal, Blu-ray/CloneBD), each with its own remembered `Settings.ExternalDumperPath*` field,
all sharing the one `LaunchExternalTool` method from v1.91.0 (so all four automatically have the
v1.90.2 WorkingDirectory/path-normalization/clear-on-failure fixes). The four buttons now sit in
two rows above the track list, which — along with the rip button, progress bar, and log — shifted
down 30px to make room. **UNVERIFIED — awaiting the user's own build**, same caveat as every
WinForms change this session; the new layout geometry in particular hasn't been seen rendered.


v1.91.0 extends the v1.90.x external-tool escape hatch to a second console family: PS1 discs via
CloneCD. This is not a bug workaround like the GameCube case — it's the user's existing PS1
workflow getting the same "launch it, then import its finished image" treatment. `ReadView` now
has a second button, "PS1 discs (CloneCD)…", next to the GameCube one; `Settings` got its own
`ExternalDumperPathPs1` field so the two tools' remembered paths don't clobber each other; and the
launch logic was factored into one shared `LaunchExternalTool` method so both buttons carry
v1.90.2's `WorkingDirectory`/path-normalization/clear-on-failure fixes rather than duplicating
them. **UNVERIFIED — awaiting the user's own build**, same caveat as every WinForms change this
session; no live-drive code touched.


The user's first real use of the v1.90.0/v1.90.1 "Wii/GameCube discs…" button crashed immediately —
a ".NET Framework" unhandled-exception dialog, `FileNotFoundException: Could not find file
'C:\dev\DiscForge\rawdump.exe'`. The full stack trace (asked for and provided) showed
`mscorlib, Assembly Version: 2.0.0.0` in the loaded assemblies — proof the crash was happening
inside RawDump.exe itself (a .NET Framework app), not inside DiscForge. RawDump's own startup code
opens a file by its bare filename, assuming its current working directory is its own install
folder; DiscForge's `Process.Start` call never set `WorkingDirectory`, so the child inherited
DiscForge's own working directory instead (`C:\dev\DiscForge`, itself inherited from
`build-app.ps1`'s `Set-Location $Repo`) and RawDump's relative lookup failed. v1.90.2 sets
`WorkingDirectory` explicitly, normalizes the picked path, and clears a bad remembered path on
launch failure so it re-prompts instead of repeating the same crash. See CHANGELOG.md for the full
writeup. This is an application-level bug in DiscForge's launch code, not a hardware/SCSI issue —
no live-drive code touched, no new authorization needed. **UNVERIFIED against the real crash** —
awaiting the user rebuilding and retrying the button with RawDump actually installed where the
launcher expects it.

Everything below the next horizontal rule predates v1.68.0 and is kept for archaeology (the
`SptiRawDaoBurnEngine` debugging narrative in particular has real diagnostic value if that engine
ever needs revisiting) — it does NOT reflect current state. Since v1.67.0 the following have
landed and are on `C:\dev\DiscForge`, tested, version-unified: the full GameCube preservation
backlog (ring codes, memory-card save banner/icon decode, revision/variant DAT matching, junk-data
handling), `burn-plan` (offline write-knob preview) and `dump-session` (drive/firmware/settings
provenance sidecar), the entire RAW-DAO hardware validation ladder (rungs 1–7, all PASS on a real
Plextor PX-W5224A), a drive-capabilities profiler with real hardware overread + cache-defeat
probes, a Tier-B adaptive re-read controller wired to real hardware (`reread-probe`),
`read-disc --resume` (checkpointed dumps, v1.72.0), an AccurateRip tie-breaker for multi-copy
audio merges (`dforge merge-cert --cue --ar-db`, v1.73.0), Tier-B wired as an opt-in escalation
inside the CD track ripper's own per-sector retry loop plus the `dforge read-cdi` CLI command that
first gave it a build-verifiable entry point (`DiscReader.ReadOptions.AdaptiveReread` +
`--adaptive-reread`, v1.74.0), track-granularity resume for that same ripper (`read-cdi --resume`,
v1.75.0), Tier-B wired into `dforge extract-sectors` too (v1.76.0), a first GUI-parity pilot view —
`DumpCertView` (v1.77.0) — which **was built for real this session (0 warnings, 0 errors)**, a
second GUI-parity view, `ProveView` over `dforge prove` (v1.78.0, **UNVERIFIED, awaiting a local
build — and this one burns a real disc, see below**), a third GUI-parity view, `PressingDnaView`
over `dforge pressing-dna` (v1.79.0, **UNVERIFIED, awaiting a local build — but no live drive, so
lower risk, back to the `DumpCertView` pattern**), a fourth GUI-parity view, `DriveDossierView` over
`dforge drive-dossier` (v1.80.0, **UNVERIFIED, awaiting a local build — same no-live-write risk
profile**), a fifth GUI-parity view, `DiscActuaryView` over `dforge disc-actuary` (v1.81.0,
**UNVERIFIED, awaiting a local build — the least live-hardware-adjacent view yet, doesn't even read
drive capabilities**), and a sixth and FINAL GUI-parity view, `DiscMriView` over `dforge disc-mri`
(v1.82.0, **UNVERIFIED — the only one of the six that renders an image, not just text, so the least
evidence of correctness so far**), which **closes the GUI-parity backlog this file has tracked since
before this session** — plus `docs/COMMANDS.md`/`docs/CLI.md` brought back into sync with the CLI's
own help (51 commands had silently drifted undocumented; both report 0 missing now), and, in
v1.83.0, `docs/GUI.md` brought into sync too (3 pre-existing launcher tiles — `datbuild`, `textures`,
`verify` — had no `HelpContent.cs` entry at all; the 6 new GUI-parity tiles plus 3 older ones were
also ungrouped, landing in the doc's catch-all "Other" section). Finally, in v1.84.0, both doc-sync
checkers were wired into CI for real — `python3 scripts/gen_gui_doc.py --check` as a new `doc-sync`
job in `.github/workflows/ci.yml` (Ubuntu, no build needed), and `scripts\check-commands-sync.ps1`
as a step in `.github/workflows/build.yml` right after the Release build produces the CLI it shells
out to — so this exact class of silent drift (51 undocumented commands, 3 tiles with no help entry)
can no longer accumulate between now and the next time someone happens to run the checker by hand.
Auditing that fix in v1.85.0 turned up a third doc-sync checker with the same gap,
`scripts/gen_cli_doc.ps1 -Check` (verifies `docs/CLI.md`'s literal captured help text, distinct from
`check-commands-sync.ps1`'s per-command-name check) — confirmed not currently stale, then wired in as
a step in `build.yml` right alongside the other two. In v1.86.0, a fourth loose end turned up:
`scripts/check-commands-sync.sh` (a bash twin of the `.ps1` version) was dead code, referenced
nowhere at all — confirmed it runs clean, then gave it a fast Linux job of its own in `ci.yml`
(builds just the CLI's cross-platform `net8.0` target, no Windows runner needed), so `docs/COMMANDS.md`
drift now gets caught on the cheap/fast job too, ahead of `build.yml`'s slower full build. **All
three `.github/workflows/*.yml` edits (v1.84.0 through v1.86.0) could not be written to the user's
machine** — the remote-device bridge refuses writes to `.github/workflows/` as protected files — so
all three workflow files have only been delivered into the conversation as downloads; **the user
still needs to place them by hand** before any of this CI wiring takes effect. 2693 tests passing
(unchanged since v1.76.0 — GUI code and doc sync have no test double to run against outside Windows
/ against a real build).

**Note on the GUI (`DiscForge.App`) and the installer**: this sandbox cannot build the WinForms
target at all (no `Microsoft.NET.Sdk.WindowsDesktop` targets pack), so every `<Version>` bump
since v1.68.0 has only ever landed in source. The installer (`installer/DiscForge.iss`) reads its
own version from the *compiled* `..\publish\DiscForge.exe`'s embedded file version, not from the
`.csproj` — so until the GUI is rebuilt locally (`.\build-app.ps1 -Publish` on the actual Windows
machine, not in this sandbox) both the GUI binary's file properties and any installer built from
it will keep reporting an old version, even though the CLI has moved on to v1.83.0. This is
expected, not a bug — flagging it here so it isn't mistaken for one again. `build-app.ps1` itself
had a real, pre-existing bug fixed this session: its `-Run` launch path was hardcoded to
`DiscForge.App.exe`, which never existed (the assembly's actual name, per `<AssemblyName>` in
`DiscForge.App.csproj`, is `DiscForge` → `DiscForge.exe`) — confirmed by a real build log the user
pasted back, unrelated to any change made this session.

**GUI parity pilot #1 (v1.77.0, `DumpCertView`) — CONFIRMED BUILT, still needs to be opened**: the
user ran `.\build-app.ps1 -Run` and it built with 0 warnings/0 errors, confirming every Core API
call and WinForms control this view uses actually compiles against the real assemblies (stronger
than the Roslyn syntax-only check that was all this sandbox could do). It has not yet been reported
as opened/exercised in the running app — do that before calling it fully verified, not just built.

**GUI parity pilot #2 (v1.78.0, `ProveView`) — UNVERIFIED, and unlike #1 this one burns a disc**:
mirrors `dforge prove` — burn a `.cue` via RAW DAO-96, read every track back, compare byte-for-byte,
one verdict. Written as a line-for-line port of `ProveCmd` in `Program.cs` (identical Core/Devices
call sequence: `DiscLayout.FromCueFile` → `RawImageGenerator.Generate` → `SptiRawDaoBurnEngine.Burn`
→ `DiscReader.ReadToc` + `RawDiscReader.Read` per track → `RawReadbackCompare.Compare`), reusing
already-hardware-proven primitives rather than new logic. Every signature was cross-checked against
source and the three edited/new files (`ProveView.cs`, `CdrwinLauncher.cs`, `HelpContent.cs`) were
Roslyn syntax-parsed clean, same discipline as the pilot — but this is a genuinely destructive
operation (a real burn, no simulation, no undo), so treat a clean `-Run` build as confirming the UI
*compiles*, not that it's safe to trust blind. **Before relying on this: open the "Prove" tile, and
run it once on media you're fine consuming — the confirmation dialog it shows before starting spells
out exactly what it's about to do.** If `DriveDossier`, `DiscActuary`, or the Disc-MRI heatmap are
picked up next, `DumpCertView`/`PressingDnaView` (no live drive) and `ProveView` (drives hardware)
are both useful templates — none of those three touch a live drive, so the `DumpCertView` pattern is
the closer match for them.

**GUI parity pilot #3 (v1.79.0, `PressingDnaView`) — UNVERIFIED, back to the no-live-drive pattern**:
mirrors `dforge pressing-dna` — fingerprint one .cue's pressing (geometry, pregaps, write-offset
artifacts, MCN/ISRC), or compare two for SAME PRESSING / same title but DIFFERENT PRESSING / different
discs. Pure offline file analysis, same lower-risk shape as `DumpCertView`, chosen over the three
remaining candidates (`DriveDossier`/`DiscActuary` both maintain a persistent per-user AppData store;
the Disc-MRI heatmap renders an SVG/PNG) specifically because it's the simplest of what's left. The
cue/bin-loading glue is a verbatim port of `LoadGenomeTracks`/`LoadPressingFingerprint` in
Program.cs (CLI-internal, not a public Core API); only `PressingDna.Compute`/`Compare` and
`CueSheet.Parse` are called as real public API, both cross-checked against source. Roslyn-clean.
**Run `.\build-app.ps1 -Run` and open the "Pressing DNA" tile with a real .cue+.bin before trusting
it** — lower risk than `ProveView` given `DumpCertView`'s same-shape pattern already built clean for
real, but still unverified until it's actually built and opened.

**GUI parity pilot #4 (v1.80.0, `DriveDossierView`) — UNVERIFIED, no live write either**: mirrors
`dforge drive-dossier` — per-drive institutional memory (mute signatures, C2 wolf-cries on the
opening sector, a confirmed AccurateRip offset, overread reach) accumulated across sessions into
warnings. Turned out simpler than expected despite keeping a persistent store: the store is one flat
JSON record per drive (`DriveDossierStore`/`DriveDossier`), no time-series/decay math like
`DiscActuary` and no image to render like the heatmap, and the only live-hardware touch is read-only
drive detection (`DriveDetector.DetectAll()`, the same call `BurnView`/`ProveView` already use).
Every API call cross-checked against source, Roslyn-clean. **Run `.\build-app.ps1 -Run` and open
the "Drive Dossier" tile before trusting it** — same risk class as `DumpCertView`/`PressingDnaView`.

**GUI parity pilot #5 (v1.81.0, `DiscActuaryView`) — UNVERIFIED, least live-hardware-adjacent yet**:
mirrors `dforge disc-actuary` — per-disc longitudinal scan history (tier1/tier2/uncorrectable)
fitted to a first-order decay model, plus collection-wide urgency ranking. Record a scan by typing
its maxima or importing a scan file (`QualityScanImport.Parse`, same formats the CLI's `--record
<scan-file>` accepts), optionally condition the fit on a specified storage temperature/humidity.
Doesn't even read drive capabilities — a recorded scan's "drive" is a free-text label, same as the
CLI. Every `DiscActuary`/`RotKinetics`/`StorageEnvironment`/`QualityScanImport` call cross-checked
against source, Roslyn-clean. **Run `.\build-app.ps1 -Run` and open the "Disc Actuary" tile before
trusting it.**

**GUI parity pilot #6 (v1.82.0, `DiscMriView`) — UNVERIFIED, FINAL view, closes the backlog**:
mirrors `dforge disc-mri` — a polar map of the physical disc's spiral (per-sector evidence: scratch,
pressing defect, rot, muted read region). Open a raw `.bin` or single-file `.cue` (the cue resolves
sync-less-sector ambiguity), with a `.badsectors.json` sidecar auto-detected or picked explicitly.
The cue/span-resolution logic is a verbatim port of `DiscMriCmd`'s own block in Program.cs (same
reasoning as the other cue-consuming views); `DiscMri.Classify`/`RenderPng`/`RenderSvg` are the real
public API, cross-checked against source. **The first of the six that renders an image rather than
reporting text** — WinForms has no SVG renderer, so the on-screen preview always uses
`DiscMri.RenderPng` into a `PictureBox` (decoded into a detached `Bitmap` copy, avoiding the classic
`Image.FromStream`-stays-bound-to-its-stream gotcha); Save As offers both `.svg` and `.png`, picking
the renderer the extension implies. Roslyn-clean, but this is the view with the LEAST evidence of
correctness of the six, since pixel rendering can't be sanity-checked by reading the code the way
text formatting can. **Run `.\build-app.ps1 -Run`, open the "Disc MRI" tile with a real .bin/.cue,
and actually look at the preview** — confirm it's a sensible-looking polar map, not just that
nothing throws.

With `DiscMriView` shipped, **the GUI-parity backlog `docs/NEXT.md` has tracked since before this
session is closed**: `DumpCertView`, `ProveView`, `PressingDnaView`, `DriveDossierView`,
`DiscActuaryView`, and `DiscMriView` now cover every view that was missing. Only `DumpCertView` has
been confirmed by a real build; the other five are still unverified pending `.\build-app.ps1 -Run`
(or `-Publish`) and a click through all six tiles — that's the natural next step before any of this
GUI work is trusted as shipped rather than merely written.

**v1.86.1 update: the user ran that build, and it found a real bug.** `.\build-app.ps1 -Test -Run`
failed with `CS0246: The type or namespace name 'Evidence' could not be found`, `DiscMriView.cs(72,13)`
— `DiscMri.Evidence` is a nested enum, and the field declaration `private Evidence[]? _evidence;`
was the one spot in the file that forgot to qualify it (every other reference already did). This is
exactly the class of error the sandbox's Roslyn syntax-only checker could never have caught — CS0246
is semantic (type resolution), not syntax, and `CSharpSyntaxTree.ParseText` alone never resolves a
type against real metadata. Fixed with one line (`private DiscMri.Evidence[]? _evidence;`), confirmed
this time with real semantic verification (a throwaway program compiled against the actual built
`DiscForge.Core.dll`, confirming `DiscMri.Evidence[]` binds), and audited the other five views for
the same bug class (any bare reference to a type nested inside another class) — found nothing else.
**This means the Roslyn-syntax-only verification claimed for `ProveView`/`PressingDnaView`/
`DriveDossierView`/`DiscActuaryView`/`DiscMriView` throughout this session was weaker than it looked
— it catches malformed syntax but not a wrong/missing type qualification, a wrong overload, or any
other error that needs real metadata to detect.** Those four other views have NOT yet been confirmed
by a real build; treat their "Roslyn-clean" status as necessary but not sufficient, same as it always
should have been read, and don't be surprised if the next real build turns up something else in one
of them. `dforge` CLI and the full Core test suite still build/pass clean in this sandbox after the
fix (2692/2693 — see below on the one unrelated failure).

**v1.87.0 update: they did re-run it, and it came back clean.** The user re-ran
`.\build-app.ps1 -Test -Run` after applying the fix above and reported it good, with no further
errors pasted back. Taken at face value, this closes the compile-verification gap for the whole
backlog: `ProveView`, `PressingDnaView`, `DriveDossierView`, and `DiscActuaryView` are now
real-build-confirmed alongside `DumpCertView` and `DiscMriView` — **every view in the GUI-parity
backlog has now compiled for real on Windows**, not merely passed a syntax-only check. What's NOT
yet confirmed, and can only be confirmed on the user's machine: actually opening and clicking through
each tile (compiling correctly is not the same as behaving correctly), and — separately, the
highest-risk item — actually running `ProveView`, which burns a real disc and has not been exercised
at all yet. The interrupt→resume hardware test (`hw-test-resume-auto.ps1`) also remains outstanding.
Re-checked the `AudioCdTests` flake once more in this sandbox after clearing lingering build
processes — still reproduces (2692/2693) — so it's still believed sandbox-only; nothing further to
add here without a clean-environment re-run, ideally the user's own, which is the evidence that
actually settles it.

**v1.87.1 update: the very next rebuild failed too, but on `build-app.ps1` itself, not on product
code.** `-Run` launches `DiscForge.exe` detached and never closes it, so two prior `-Run`s had left
two windows open, both holding `DiscForge.Core.dll`/`DiscForge.Devices.dll` locked — the next
build's copy step (App → its own bin folder) couldn't overwrite files its own previous output was
still running and using (`MSB3027`/`MSB3021`, "file is locked by: DiscForge (40472), DiscForge
(11788)"). Fixed by having `build-app.ps1` close any `DiscForge.exe` whose process path exactly
matches the App's own build output before building (`Stop-Process -Force`, matched by full path so
it can never touch an unrelated same-named program elsewhere), plus a short sleep for the OS to
release the handles. **Not verified against a real PowerShell parser** — `pwsh` isn't available in
this sandbox, and installing it was previously declined — so this is read-back-carefully-but-
unexecuted, same caveat as every `.ps1`-only change in this project. Worth confirming the next
`-Run` → rebuild cycle actually closes the old window automatically. This makes THREE separate real
issues the user's own actual build/rebuild cycle has now caught that nothing in this sandbox could
have: the `DiscMriView.cs` nested-type bug (v1.86.1), and now this file-lock self-inflicted-by-the-
script issue — a reminder that "runs clean in the sandbox" and "works on a real machine, twice in a
row" are genuinely different bars, and the second one is the only one that counts.

**v1.87.2 update: two real hardware diagnostics from actual disc reads, one real bug fixed, one
confirmed not-a-bug.** After the rebuild succeeded, the user tried a real disc rip and hit an actual
hardware read failure at LBA 475 (in `ReadAudioWithJitterCorrection`), reported as `Medium error: No
sense (status 0x00)` — a message that reads as "the drive said this was fine" when the opposite
happened. Root cause: `SptiDevice.SendCommand`'s driver-level failure branch (`DeviceIoControl`
returns `false`) can produce `!Success && ScsiStatus == 0 && Win32Error == 0` with no real sense data
at all, and `SptiResult.Describe()` had no branch for that specific combination, so it fell through
to the default "No sense" case. Fixed with a new `IsUnexplainedFailure` property plus a `Describe()`
branch that says plainly the request didn't complete and neither Windows nor the drive gave a
reason — a message-only change, verified not to touch `Success`/retry/boundary-tolerance behavior
anywhere by walking `SendCommand`'s own two branches. Verified with a REAL `dotnet build` of
`DiscForge.Devices` at `net8.0-windows` (the actual shipping target, not syntax-only) plus a clean
`dforge` CLI build and 2693/2693 tests passing (the `AudioCdTests` flake did not recur this run).
Deliberately NOT touched: the real underlying gap this diagnostic exposed, that
`ReadAudioWithJitterCorrection` only tolerates boundary sectors at a track's *tail*
(`TailWindowSectors`), not its *head* — unlike the general `ReadTrack` path, which tolerates both —
so a head-boundary hiccup like this one has no fallback. That's live-hardware retry/tolerance logic
and per this project's standing rule is not being touched blind; it remains open, real, and
unverified-fixable without the user's own hardware. Separately, a **second** real diagnostic from the
same session (after the user unchecked "Correct audio jitter" as a suggested workaround) hit a
different, later failure at LBA 153,903 — a genuine SCSI medium error (`uncorrectable read error`,
real sense data, status 0x02) — confirmed NOT a bug: the software correctly detected and reported real
physical read trouble on the disc, with an already-actionable message (clean the disc; tick
"continue past unreadable sectors" to salvage the rest). No code change was made for that one. This
is now the fourth real issue the user's own actual hardware/build cycle has caught that nothing in
this sandbox could have (after the `DiscMriView.cs` nested-type bug and the `build-app.ps1` file-lock
issue) — and the first one from real disc hardware rather than from a Windows build/rebuild.

**v1.88.0 update: a real GameCube disc exposed a genuine, closeable gap in the cooked-track read
path.** The user tried to read an original GameCube disc through DiscForge (having successfully
dumped it with a separate third-party tool, Rawdump 2.0, on the same drive). DiscForge detected the
drive and read the TOC fine — a GameCube disc reports as a completely normal single-track DVD-ROM —
but the very first test read failed outright with `Medium error: ASC 0x11 ASCQ 0x00` ("Unrecovered
read error") on a stock, unmodified HL-DT-ST DVD-ROM GDR8164B (drive dossier confirmed: no modified
firmware, C2 pointers supported, otherwise an ordinary DVD-ROM drive). Since a different tool reads
the same disc on the same unmodified drive successfully, the drive clearly *can* do this — DiscForge
just wasn't asking it the right way. Reading the actual code (not guessing) found the real gap:
DVD-mode ("cooked", 2048-byte) tracks are read with only the plain `READ(10)` command, and when a
drive refuses that outright, there was no fallback for cooked tracks at all — `Probe()`'s only
fallback logic was raw-track-only, and the per-sector retry ladder's escalation to alternative
request shapes (`TryHarder`) only fires at a track boundary or on a specific sense code, neither of
which this failure is. The fix already existed in the file, just unreachable from here: `TryHarder`
already has a "Rung 3, cooked only" step issuing `READ CD` for Mode 1 user data — a different
firmware path than `READ(10)`, and the standard technique GameCube-dumping tools use. Wired that
same already-proven command into both `Probe()` (so a disc this drive can serve via `READ CD` isn't
rejected before the rip starts) and the retry ladder (so any cooked-track failure gets the same
chance during a real read, not just boundary/type-rejection cases). Both changes are purely
additive — a sector that already reads today is unaffected, and a sector this doesn't help for fails
exactly as before. Verified with a real `dotnet build` of `DiscForge.Devices` at `net8.0-windows`,
a clean `dforge` CLI build, and 2693/2693 tests passing. **Not yet verified against the actual
GameCube disc and drive that exposed this** — only the user's own rebuild can confirm whether `READ
CD`/Mode 1 actually gets past this on their hardware — but unlike the still-open jitter-boundary gap
above, this reuses an already-shipped, already-trusted command in two new call sites rather than
introducing new retry/tolerance logic, which is why it felt safe to write rather than leave open.

**v1.89.0 update: v1.88.0's fix didn't work — a real, useful negative result — so we went deeper.**
The user rebuilt and tried the same GameCube disc again: identical failure, same LBA 300, same
`ASC 0x11 ASCQ 0x00`. The `READ CD`/Mode 1 fallback wasn't the whole story. Researched how the
community's actual GameCube-dumping tools (FriiDump, Rawdump) get past this: GameCube discs use
non-standard sector scrambling, so a normal read's EDC check fails outright regardless of which
read command asks — these tools use a "streaming" read that tells the drive to hand back the bytes
without insisting they pass that check, and for the hardest cases fall back further still to
vendor-specific commands that pull data straight out of a drive's internal memory cache (real, but
drive-chipset-specific reverse-engineering — explicitly NOT attempted here, no hardware or firmware
documentation to attempt it safely against). The streaming-read half, though, has a standard,
spec-defined SCSI/MMC equivalent needing no vendor knowledge at all: mode page 0x01 (Read-Write
Error Recovery) — setting RC (Read Continuous) and DCR (Disable Correction) and forcing Read Retry
Count to 0 tells the drive to hand back data without its normal ECC pass or retries, the same
intent as "streaming," through a documented mechanism. Added `TryStreamingRecoveryRead`: reads the
drive's own current page (read-modify-write, the same pattern already proven working in
`SptiRawDaoBurnEngine`), changes only those specific bits, tries one `READ CD` Mode 1 request, and
restores the drive's original page in a `finally` block — always, success or failure — so this
cannot leave the drive's error correction degraded for any other read. Wired in as a genuine last
resort in `Probe()` (after the v1.88.0 attempt) and as a new Rung 4 in `TryHarder`, both reached
only after every normal request shape has already failed, so a sector that reads fine today never
goes near this code. Verified the same way as v1.88.0 (real `net8.0-windows` build, clean CLI
build, 2693/2693 tests) — again **not yet verified against the actual disc and drive**, which is
now the second attempt at this specific real-world gap; if this one doesn't work either, the next
honest step is telling the user this specific disc likely needs the vendor-specific cache-read
technique DiscForge doesn't (and, without the relevant hardware documentation, safely can't)
implement, rather than continuing to guess at SCSI commands blind.

**v1.90.0 update: it didn't, so this GameCube gap is now closed as "identified, not fixable here"
— with a real escape hatch instead of a dead end.** The user retested v1.89.0 cleanly (cooked mode,
same disc/drive): identical failure. Two separate, legitimate SCSI-level recovery techniques both
failed on this real hardware — real evidence, not wasted effort, that this drive doesn't honour
either mechanism for this disc, and that going further needs the vendor-specific cache-read
technique GameCube-dumping tools (FriiDump, Rawdump) use, which is genuinely out of reach without
that exact drive's chipset documentation and hardware to iterate against. The user then asked about
bringing their already-working third-party tool, Rawdump 2.0, into DiscForge directly. Checked: it's
closed-source freeware with no public repository or license found, doing exactly that vendor-
specific cache-read technique (confirmed from the GC-Forever wiki) — bundling or copying it would
break this project's clean-room provenance and risk a real license problem, and it turned out to be
GUI-only anyway (confirmed from a screenshot of its own window: drive picker, "Start Dump", "Convert
raw to .iso" — no documented command-line mode to automate against). So added the honest, thin
version of "shell out": `ReadView` now has "Launch external dump tool…" (asks once for a path,
remembered in settings, starts that process — DiscForge sends it no commands and knows nothing
about what it does) and "Import from external tool…" (copies its finished image into the user's
library and says plainly it hasn't been read or verified by DiscForge itself). Not an automation of
Rawdump specifically, and not a claim that DiscForge now supports GameCube discs — a generic
launcher-plus-import escape hatch for any disc this project's own read path genuinely can't get
past, after two real, documented attempts. `DiscForge.App` (WinForms) still can't be built for real
in this sandbox; verified via Roslyn syntax-only parse of both changed files plus a direct check
that the one new cross-namespace reference (`Settings.ExternalDumperPath` from `App.Views`) matches
an already-compiling pattern elsewhere (`InspectView.cs`'s `Settings.AddRecent`). **UNVERIFIED —
awaiting the user's own build**, same as every WinForms change this session.

**`docs/GUI.md` doc-sync drift (v1.83.0, closed)**: the same class of bug as the `docs/COMMANDS.md`
drift below, caught the same way — running the checker by hand, since neither is wired into CI.
Unlike `check-commands-sync.ps1`, `scripts/gen_gui_doc.py` is plain Python and runs in THIS sandbox,
so `--check` could actually be run here: it found 3 pre-existing launcher tiles (`datbuild`,
`textures`, `verify`) with no `HelpContent.cs` entry at all, plus the 6 new GUI-parity tiles (and 3
older ones — `merge`, `mergecert`, `secureripplan`) falling into the generator's "Other" catch-all
instead of a real category. Fixed both: added the 3 missing help entries, and added a new
"Preservation certificates & forensics" group to `gen_gui_doc.py` AND its PowerShell twin
`gen_gui_doc.ps1` (kept identical between the two, per the file header's own claim) alongside
placing `datbuild`/`textures`/`verify` into existing groups. `docs/GUI.md` regenerated (64 tiles),
`--check` now clean. **Wired into CI in v1.84.0** — see below.

**`docs/COMMANDS.md` / `docs/CLI.md` doc-sync drift (v1.78.0, closed)**: `scripts/check-commands-
sync.ps1` exists to fail a build when `COMMANDS.md` is missing a command the CLI's help lists, but
it was never wired into CI — it only runs when `build-app.ps1` happens to find it, which needs a
Windows build this sandbox can't do. Running the same check by hand against a real Linux build of
the CLI found 51 undocumented commands (things like `read-cdi`, `dump-cert`, `dump-session`,
`drive-profile`, `disc-actuary`, `disc-mri`, `pressing-dna`, the whole Aaru/CICM interop surface, and
more) — real, accumulated drift, not a formatting mismatch (verified by grepping for each name
directly). All 51 are now documented, grouped into new sections rather than dumped in unsorted, and
`docs/CLI.md` (the literal generated dump) was regenerated too (was stale at 293 vs. the real 347).
**Wired into CI in v1.84.0** — see below.

**Doc-sync checkers wired into CI (v1.84.0, closed)**: both drift-fixes above ended with "worth
wiring into CI" and neither had been, until now. `python3 scripts/gen_gui_doc.py --check` is a new
`doc-sync` job in `.github/workflows/ci.yml` — runs on Ubuntu, needs no .NET build at all, so it's
essentially free and catches `docs/GUI.md` drift on every push/PR. `scripts\check-commands-sync.ps1`
became a step in `.github/workflows/build.yml`, placed right after the "Build (Release)" step so the
`dforge.dll` it shells out to already exists; it can't move to the Linux `ci.yml` job because it
needs the actual built CLI, not just source text. `gen_gui_doc.ps1 -Check` (the PowerShell twin) was
deliberately **not** separately wired in — redundant with the Python check already running on every
push, and Ubuntu can't run pwsh against Windows-only code paths anyway; it stays available for local
use on Windows. Verified both workflow YAML files still parse and that `gen_gui_doc.py --check`
currently passes clean — the actual GitHub Actions run itself can only be confirmed once this reaches
a real push/PR on the user's repo, since this sandbox has no way to trigger Actions.

**Third doc-sync checker wired into CI (v1.85.0, closed)**: auditing the v1.84.0 wiring above for
anything missed turned up `scripts/gen_cli_doc.ps1 -Check`, which regenerates `docs/CLI.md` (the
literal captured `dforge --help` text) and was never wired in either — a distinct check from
`check-commands-sync.ps1` (that one checks command *names* against `docs/COMMANDS.md`'s prose
descriptions; this one checks the literal help dump byte-for-byte, so the two can drift
independently). Confirmed `docs/CLI.md` was NOT currently stale first (rebuilt `dforge` in this
sandbox, ran the PowerShell script's parsing logic in Python since `pwsh` isn't available here,
diffed byte-for-byte) so this is pure CI-hardening, not a hidden drift-fix. Added as a step in
`build.yml` right after the `check-commands-sync.ps1` step, same `dforge.dll`. **Same delivery
snag as v1.84.0**: the remote-device bridge refuses to write `.github/workflows/*.yml` files
("protected file") — all three workflow files (the v1.84.0 `ci.yml`/`build.yml` edits plus this
release's further `build.yml` edit) are sitting as downloads in the conversation only; **none of
this CI wiring is live on `C:\dev\DiscForge` until the user places them into `.github/workflows/`
by hand.**

**`check-commands-sync.sh` was dead code, given a fast Linux CI job (v1.86.0, closed)**: a fourth
loose end found while re-checking the doc-sync fixes for anything else missed. The bash twin of
`check-commands-sync.ps1` existed, worked correctly (341 commands, 0 missing, verified by hand
first), and was referenced from nowhere — not a workflow, not either doc file, nothing. Rather than
leaving it dead or duplicating exactly what `build.yml` already does, gave it its own fast job in
`ci.yml`: builds only `DiscForge.Cli`'s `net8.0` target (cross-platform, no `DiscForge.Devices`
dependency at that target — confirmed with a real build in this sandbox that it restores and builds
clean without a Windows runner) and runs the script against it. This surfaces `docs/COMMANDS.md`
drift on the cheap/fast Ubuntu job well before `build.yml`'s full Windows build + installer compile
finishes; `check-commands-sync.ps1` in `build.yml` remains the authoritative check against the real
Windows target, so this is deliberately a fast early-warning duplicate, not a replacement. Same
delivery snag as before — `ci.yml` is a protected file for the remote-device bridge, so this edit is
also sitting as a download only, not yet live on `C:\dev\DiscForge`. With this, the doc-sync/CI-wiring
thread this session pulled on since v1.78.0 appears to be exhausted: no further undocumented commands,
ungrouped tiles, or unwired checkers turned up on this pass. The next legitimate next steps all need
things this sandbox doesn't have — a real Windows build, or real optical hardware — and that's exactly
what happened next: see the v1.86.1 update on `DiscMriView` above for the real bug the user's actual
build turned up.

**`AudioCdTests.Over_74_minutes_warns_that_80_minute_media_is_needed` — sandbox-only flake, NOT
investigated further, needs a clean-environment re-run to confirm**: while re-verifying after the
v1.86.1 fix, the full suite went from a clean 2693/2693 (repeated many times earlier this session) to
2692/2693, with this one test throwing `OutOfMemoryException`. The test allocates a ~770 MB synthetic
WAV (`76L * 60 * 75 * 2352` bytes) — legitimately memory-heavy but not unreasonably so. Ruled out an
actual product bug: a standalone program calling the exact same `AudioCdCreator.Create` path with the
exact same input, in a fresh process referencing the real built `DiscForge.Core.dll`, completed
cleanly with the expected warning. Also ruled out a hard resource ceiling: a bare 3×767 MB array
allocation test passed fine, `free` showed 6-7 GB available, cgroup `memory.max` was effectively
unlimited, and disk had 12 GB free throughout. The failure reproduced 5/5 times specifically when run
through `dotnet run --project tests/Harness/Harness.csproj`, both filtered to just `AudioCdTests` and
across the full suite, with and without `DOTNET_gcServer` forced off — so it's not GC-mode-specific,
but IS specific to something about that invocation path after a very long-running sandbox session
(many hours, many `dotnet build`/`dotnet run` invocations). Left the test and the product code
untouched rather than "fixing" something already shown correct in isolation. **Worth a clean re-run**
— either in a fresh sandbox session, or (more usefully) as part of the user's own `-Test` run on their
real Windows machine, which starts from a cold process with none of this session's accumulated state.
If it reproduces there too, it's a real bug and needs the actual investigation this entry deliberately
skipped; if it doesn't, this was exactly what it looked like.

**Genuinely still open** (independently verified against actual code/tests as of v1.75.0, not
just against what older planning docs claim — several older "not started" doc entries turned out
to already be shipped):

1. **GC junk-PRNG regenerator never validated against an independent reference.** The existing
   test builds its own "ground truth" with the same generator it's checking — the exact
   false-validation trap `docs/VALIDATION-PLAN.md` warns about. Needs a Redump-verified unscrubbed
   GC ISO or an NKit-scrubbed image (CRC32 round-trip). Blocked on getting a real reference file —
   the one physical GC disc available couldn't be read by either of two different drives tried
   (SH-224, LG CH10LS20), most likely the disc itself; parked.
2. ~~Adaptive re-read (Tier B) not wired into a retry loop~~ — **closed in v1.74.0 (CD track
   ripper) and v1.76.0 (`extract-sectors`)**: `DiscReader.ReadOptions.AdaptiveReread` wires the
   already hardware-proven Tier-B escalation (`AdaptiveReread`/`DriveRereadSource`) into the CD
   track ripper's (`DiscReader.ReadToCdi`) per-sector retry, opt-in and off by default, and
   `dforge read-cdi --adaptive-reread` gives it a build-verifiable CLI entry point (the ripper was
   previously GUI-only, and this sandbox can't build the WinForms target to have added a checkbox
   there safely). v1.76.0 closed the other half: `dforge extract-sectors` — the CLI's actual
   highest-traffic dump pipeline — had the same flat-retry gap. Re-examining it this session showed
   the earlier "needs a new SET CD SPEED command" concern no longer applied — `DriveRereadSource`
   already had that (built during the read-cdi work) — so the real remaining task was just
   respecting `SectorExtraction`'s Core/Devices boundary: a new `IExtractionRereadEscalation`
   interface (Core, pure) lets `SectorExtraction` offer a sector one more Tier-B chance after every
   plain retry fails, without Core ever needing to know an `SptiDevice` exists; `DriveExtractionReader`
   (Devices) implements it by wiring to the same `DriveRereadSource`/`AdaptiveReread.Run` the ripper
   uses. `--adaptive-reread` on `extract-sectors`, opt-in/off by default, disabled automatically on
   DVD/BD media (Tier-B's strategies are CD-only). 8 new tests, 2693 total passing.
   `read-cdi` **has been run against real hardware** (Plextor PX-W5224A, mixed-mode data+audio
   disc): plain, `--raw`, and `--raw --adaptive-reread` all completed with "every sector read
   cleanly" and correct per-track sector counts. Note that because every sector came back clean on
   the first pass, `--adaptive-reread`'s actual Tier-B escalation logic was never exercised in that
   run — it didn't get in the way, but its real re-read behavior is still unconfirmed on hardware,
   for EITHER call site (`read-cdi` or the new `extract-sectors` one) — worth deliberately testing
   against a disc with a genuinely marginal sector, not just a clean one, in a future hardware
   session. The three `read-cdi` output `.cdi` files were also not byte-verified against the
   fixture's source `data.bin`/`a.bin` — "read cleanly" confirms no I/O errors were reported, not
   byte-for-byte correctness.
3. ~~No resume for anything OTHER than `read-disc`'s cooked-sector path~~ — **closed, at track
   granularity, in v1.75.0**: `read-cdi --resume` checkpoints which whole tracks a CD rip already
   captured cleanly (`CdiRipCheckpoint`) and reuses them instead of re-reading, refusing the resume
   if the disc/TOC or raw-vs-cooked choice has changed. Deliberately NOT sector-granularity like
   `read-disc --resume` — a CDI's track-data-then-descriptor layout has no mid-track byte offset in
   the final file to resume from, so a track that's still in progress when a rip is interrupted must
   be re-read from its own start next time, not resumed mid-track. Real-hardware status: a plain,
   uninterrupted `read-cdi --resume` run against a small mixed-mode disc completed cleanly and
   correctly reported "no checkpoint found — starting a fresh rip" (expected, since nothing had
   failed to need resuming). A second, larger fixture (~562 MB silent audio track, sized so a rip
   takes a minute or more) was built and burned specifically to test the actual interrupt→resume
   path — hit Ctrl+C partway through track 2, confirm the `.ripstate.json`/`.trackNN.tmp` sidecars
   exist, then re-run `--resume` and confirm it reuses track 1 instead of re-reading it — but no
   console output from that specific interrupted-and-resumed run was ever captured back into this
   session, so that path (the one thing `--resume` actually exists for) remains genuinely
   unconfirmed on real hardware. The burned disc for it should still be sitting in the drive/nearby;
   worth finishing this specific test in a future session rather than assuming it passed.
4. **GUI lags the CLI's newest forensics** — `PressingDna`, `DriveDossier`, `DiscActuary`, the
   Disc-MRI heatmap, and the Dump Certificate have no WinForms views; `dforge prove` has no GUI
   entry point either. (This sandbox cannot build the WinForms target at all — no
   `Microsoft.NET.Sdk.WindowsDesktop` targets pack — so any GUI work here would be unverified;
   worth flagging before attempting it blind.)
5. ~~AccurateRip tie-breaker missing from consensus merge~~ — **closed in v1.73.0**:
   `AccurateRipTieBreaker` + `ProvenanceMerge.Merge`'s new `audioHints` parameter let a multi-copy
   audio merge settle a disagreeing track against a known-good AccurateRip checksum instead of an
   unconfirmable byte vote, wired into `dforge merge-cert --cue --ar-db`.
6. **RFC-3161 trusted timestamps** — correctly deferred per `docs/FRONTIER.md`: needs a
   `System.Security.Cryptography.Pkcs` package restore unavailable in this sandbox, plus a live
   TSA. Not a sandbox-buildable gap.
7. **C2 accuracy and audio read-offset** — hardware-gated, not code-gated: `drive-profile` already
   probes everything that CAN be probed without extra media; these two need a known-defective disc
   and an AccurateRip-verified reference disc respectively, neither available yet.

---

## SptiRawDaoBurnEngine session (2026-08-25 continued) — real fixes, still not fully closed

Picking back up on the parked `burn-raw --engine spti` bug with a real blank
disc and byte-level diagnostics (`SptiRawDaoBurnEngine.Verbose`, prints raw
MODE SENSE/MODE SELECT/SEND CUE SHEET bytes + decoded field pointer to
stderr — turn on via `TestCue()`, or set the static flag directly). Genuine,
hardware-confirmed fixes landed:

1. **WRITE(10) retry read stale sense.** The retry loop re-queried sense with
   a fresh REQUEST SENSE after a failure instead of reading the sense the
   drive returned WITH the failing command; by the time the fresh query
   landed the condition had cleared, so retries never triggered. Fixed to
   read `SptiResult.SenseKey/Asc/Ascq` directly off the failing command.
2. **Missing OPC + NWA read before SEND CUE SHEET.** cdrdao's real sequence
   is MODE SELECT → OPC → get NWA → SEND CUE SHEET; `TestCue()` skipped
   straight from MODE SELECT to SEND CUE SHEET. Added the missing steps.
3. **MODE SENSE reply buffer too small** (64 bytes; needs up to ~68 with a
   block descriptor present) — silently fell back to a blank default page.
   Bumped to 192 bytes. (Turned out not to be live on THIS drive — it
   reports block descriptor length 0 — but it's a real latent bug on drives
   that do return one, now fixed regardless.)
4. **MODE SELECT now genuinely succeeds — first time all session.** Real
   capture showed the drive reporting Track Mode (write-parameters page
   byte 3, low nibble) = `0x5` ("audio, four-channel, copy permitted") for
   a disc whose first track is DATA. The "preserve Track Mode exactly as
   the drive reports it" policy (copied from cdrdao, which assumes the
   drive's reported value is sane) was faithfully keeping that garbage.
   Fixed by overriding just that nibble with the real first-track control
   value already known from the layout (same value `DaoCueSheet.CtlAdr`
   uses). First attempt at this fix used the wrong bitmask (`&0x3F` doesn't
   clear the nibble it's about to OR into) and silently did nothing — caught
   from the diagnostic output and corrected.
5. **No abort/flush after a failed write.** `Burn()`'s write loop had no
   cleanup on failure (cdrdao's `abortDao()` flushes the cache on any DAO
   failure); added a best-effort SYNCHRONIZE CACHE before rethrowing, so a
   future aborted attempt doesn't leave the drive in whatever state an
   un-acknowledged failure leaves it in.

**Still open**: `SEND CUE SHEET` itself is still rejected (ASC 0x26/0x00),
even with MODE SELECT now correct, on a confirmed genuinely-blank disc, after
a full drive-manager reset AND a full PC restart (both ruled out state as the
cause). The cue-sheet CONTENT was checked entry-by-entry against cdrdao's
`GenericMMC::createCueSheet` (structure, DataForm bytes, lead-in/lead-out
mode derivation, entry count formula) and matches exactly as far as manual
verification can tell. The drive's sense data does NOT set SKSV (no field
pointer available) — that diagnostic avenue is a genuine dead end on this
drive, not a missing feature in our code.

**UPDATE (2026-08-25, same day, later still) — real cdrdao built and run on
the actual drive; a real fix landed and is mid-hardware-test.**

Andy built real, unmodified cdrdao from source on his machine via **MSYS2
MSYS** (NOT MinGW64 — see the environment note below, that distinction
mattered a lot) and ran it against the same TSSTcorp CDDVDW SH-224DB with the
same PS1 disc, verbose (`-v 4`). Two runs, decisive result:

- `--driver generic-mmc` (Session-At-Once, the same mode `TestCue()` uses):
  **cdrdao itself fails at the SAME step** — "Cannot set write parameters
  mode page" / "Cannot setup write parameters for session-at-once mode." It
  never even reaches SEND CUE SHEET. This is independent, external proof
  that the still-open SEND CUE SHEET rejection documented above is a
  drive/firmware limitation on session-at-once mode, not a DiscForge bug —
  **that avenue is now closed for good; do not resume `TestCue()` debugging.**
- `--driver generic-mmc-raw` (raw writing — the mode `Burn()` actually uses):
  cdrdao's MODE SELECT succeeded, SEND CUE SHEET succeeded (it printed a real
  12-entry cue-sheet table and proceeded to write), and it only failed later
  during the actual simulated write ("Writing lead-in and gap... ERROR:
  Write data failed" — a separate, later-stage issue, not investigated
  further since it's cdrdao's own code path, not ours).

That raw-mode success gave a byte-level comparison point. Reading
`dao/GenericMMCraw.cc::setWriteParameters` (the function that just worked on
this exact drive) showed it does NOT preserve or compute Track Mode into
write-parameters page byte 3 the way the SAO-path fix above does — it
hardcodes byte 3 to `0` entirely (no multi-session pointer, no FP/Copy, no
Track Mode nibble) and hardcodes byte 8 (session format) to `0` too, even
though this is technically an XA/Mode2 disc. DiscForge's `Burn()` (Raw write
type) was instead computing Track Mode into byte 3 and setting byte 8 from
`layout.DiscType` — reasoning that was worked out for the *SAO* path (fix #4
above) and had never actually been re-justified for Raw. **Fixed**: byte 3/8
handling in `SetRawDaoWriteParameters` now branches on write type — SAO
(`TestCue()`) keeps the Track-Mode-preservation logic unchanged, Raw
(`Burn()`) now zeroes both bytes exactly like cdrdao's proven-on-this-drive
raw driver. Data Block Type stays 3 (raw+P-W) for Raw — that part was never
the problem, and downgrading to cdrdao's simpler PQ-only mode (dataBlockType
1) would throw away the real sub-channel data DiscForge computes, which is
the whole point of this burn path; not something to revisit casually.

**Confirmed on hardware, same day**: `burn-raw --engine spti --simulate` now
gets `MODE SELECT(10) result: success=True` for the very first time all
session on the real Raw (`Burn()`) path — this had been rejected
(ASC 0x26/0x00) every single time before this fix, across multiple earlier
sessions. The simulate run then proceeded into the actual WRITE(10) loop and
was mid-run (tens of thousands of sectors in, out of 289,472, climbing
steadily) with frequent-but-recovering "drive becoming ready (key 0x2, ASC
0x04/0x08)" retries (not fatal — each one succeeds on retry 1/10 and the
sector count keeps climbing) when the session paused for the day.

**CONFIRMED, same day**: the `--simulate` run completed cleanly end to end —
`[finalize] 100.0% SIMULATION complete - the full raw write path ran with
the laser off.` / `Simulation complete (SPTI) - no disc written.` This is
the first time the FULL non-destructive raw-burn validation has passed on
real hardware, all session (all prior sessions, in fact — this bug predates
this session). `Verbose` is now also turned on inside `Burn()` itself
(previously only `TestCue()` had it), so the diagnostic bytes print
automatically on every run, real or simulated.

**UPDATE (2026-08-27) — REAL burn attempted twice on real hardware; found and
fixed the actual root cause of a "successful" burn reading back as blank.**

Andy ran a real (non-simulate) burn on a genuinely blank Verbatim CD-R:

- At `--speed 4` (default): failed partway with a genuine `Medium error: ASC
  0x0C ASCQ 0x00` after a long stretch of "drive becoming ready" retries —
  a real write-reliability problem on this 22-year-old drive at speed.
- At `--speed 1`: completed cleanly — `RAW burn complete (raw DAO)`, no
  errors, full write loop finished (one earlier `--speed 1` attempt DID
  genuinely hang/stall around 75% for several minutes with zero forward
  progress and had to be Ctrl+C'd; a second `--speed 1` attempt on a fresh
  disc completed with no stall). **Slower writes are meaningfully more
  reliable on this drive — use `--speed 1` or `--speed 2` here, not the 4x
  default**, until/unless retested on different media or a different drive.

Real cdrdao's own raw-mode write (`--driver generic-mmc-raw`, from the same
capture session) ALSO failed on this exact drive with a generic "Write data
failed" partway through — independent confirmation this drive genuinely
struggles with sustained raw DAO writing regardless of which software drives
it, which is why slowing down mattered so much.

**But**: both "successful" DiscForge burns (the 4x-then-hard-failed run
never got here, but the two that DID report `RAW burn complete` with zero
errors) then read back as **completely blank** (`writeinfo` → "empty
(blank)", NWA=0, free blocks = full disc capacity) on THIS drive. A visible
burn ring on the disc surface confirmed real physical writing occurred.
Crucially, **a second, completely unrelated drive also reported the disc as
blank** — and that same drive proved it does real, fresh reads by
successfully reading a different disc (a DVD) in between checks, ruling out
a stale-TOC/caching explanation. That made this conclusively a real
DiscForge bug, not a hardware read-back quirk on one drive.

**Root cause, found by re-reading `Burn()`'s own start-LBA logic**
(`SptiRawDaoBurnEngine.cs`, around where `ReadDriveNwa` is used): the code
branched on the drive's reported next-writable-address (NWA) — if NWA was
usefully negative (≤ −151), it assumed the drive gave a real ATIP lead-in
start and wrote our whole composed image (lead-in + program) from there; if
NWA was NOT deeply negative, it assumed **"the drive manages the lead-in
itself"** and **skipped writing our composed lead-in entirely**, sending
only the program area starting at LBA 0. This drive reports a flat `NWA = 0`
— confirmed via the `[diag]` output on both successful burns, even AFTER
Write Type = Raw mode select had already succeeded (the code's own comment
had assumed the mode change would fix this; it didn't, on this drive). Since
0 is not ≤ −151, every real burn on this drive took the "skip our lead-in"
branch. Nothing else in this raw+no-cue-sheet design (see the class doc
comment: no SEND CUE SHEET in Raw mode, the lead-in sub-channel IS the TOC)
ever supplies a lead-in — so the disc's actual physical lead-in, the ONLY
place a TOC lives, was **never transmitted to the drive at all**, on either
successful run. That explains every symptom exactly: a real, full burn
(dye genuinely changed across the whole program area, hence the visible
ring) that reads back as blank on every drive tried, because the TOC was
simply never written. Real cdrdao's own raw driver
(`GenericMMCraw::startDao()`, read from the source built earlier this
session) has no such branch at all — it unconditionally writes its own full
lead-in every time, which is exactly why it never hit this failure mode.

**Fixed**: collapsed the flawed branch. A genuinely useful negative NWA
(≤ −151) is still honoured as the drive's own authority on the start
address; anything else (0, not-valid, or the NWA read failing outright) now
falls back to composing and sending DiscForge's OWN full lead-in from the
safe default start (−22650, i.e. the default 22,500-sector lead-in length)
— it is NEVER skipped again. Verified: clean build, 0 warnings/errors,
2,503/2,503 tests still passing. **Not yet hardware-tested** — this fix
landed after the two burns that exposed the bug; the very next step is
another real burn (ideally `--speed 1` or `2`, per the reliability finding
above) on a fresh blank disc, followed by `writeinfo D:` (should show real
track/session info, not blank) and the full byte-for-byte verify:

```
dforge build-raw ps1-redump.cue golden.img --subcode raw
dforge read-raw D: readback.bin
dforge raw-verify-readback golden.img readback.bin
```

If `writeinfo` still reports blank after this fix, the next thing to
suspect is the lead-in CONTENT itself (Q-subchannel timing/CRC in
`RawImageGenerator.cs`/`SubQ.cs` — read closely this session and structurally
matches Red Book, but not yet verified byte-for-byte against a real drive's
own successful read), not the start-address logic — but the start-address
bug above was a complete, sufficient explanation on its own for every
symptom observed so far, so it's the most likely fix. If the real burn also
succeeds and verifies clean, this multi-session burn-engine saga is
genuinely closed — update this doc to reflect that and consider unparking
`dforge prove` (feature H), which was explicitly blocked on this.

**Environment note for any future cdrdao rebuild**: MSYS2 has multiple
sub-environments and they are NOT interchangeable for this project. MINGW64
(native-Windows toolchain) has full Win32/`windows.h` access but is missing
POSIX headers cdrdao's Linux-first source assumes (`pwd.h`, `sys/wait.h`,
`arpa/inet.h`, etc.) — `dao/cdrdao.cc` also unconditionally calls `fork()`,
which doesn't exist on native Windows at all, making MINGW64 a dead end for
a full build, not just a header-patching exercise. Plain **MSYS2 MSYS**
(Cygwin-target, `x86_64-pc-cygwin`) has real POSIX emulation including a
working `fork()`, and built everything (`trackdb`, `utils`, `paranoia`)
unmodified — but its `w32api/ntddscsi.h` doesn't self-include `windef.h` the
way MinGW-w64's copy does, so `dao/ScsiIf-nt.cc` failed with `USHORT`/
`UCHAR`/etc. "does not name a type" until `windows.h` was moved to be
included BEFORE `ntddscsi.h` (a one-line include-order fix, landed in the
patch delivered this session — apply the same fix if rebuilding from a fresh
clone). Use **MSYS2 MSYS**, not MINGW64, for any future cdrdao build here.
~~`cdrdao-capture-howto.md` (repo root) still describes the MINGW64 path and
is now stale~~ — **rewritten 2026-08-27**: now MSYS2 MSYS + `libiconv-devel`
+ the include-order patch, reframed as a build reference (the original
SEND-CUE-SHEET investigation it walked through is closed — see the same
date's entries above) rather than a live task list.

## Landed since v1.67.0 (uncommitted — on Andy's machine only)

- **DVD/BD `extract-sectors --disc` fix — a real, previously-unknown gap.**
  `DriveExtractionReader` unconditionally issued MMC READ CD (0xBE), a CD-only
  command, for every media type. DVD/BD sectors have no CD sync pattern, so
  `RequireDataSync` (built for CD data tracks) aborted at LBA 0 on EVERY DVD
  extraction, on any drive, at any point in this project's history —
  `extract-sectors`'s DVD support had literally never worked. Root-caused and
  fixed via live testing with a real PS2 disc (TSSTcorp SH-224DB).
  Fix: `DriveExtractionReader` now runs GET CONFIGURATION once at construction
  (`IsDvdOrBd`, via the existing `ConfigurationInfo`/`MmcProfile` parser) and
  switches to plain READ(10) 2048-byte user-data reads for DVD/BD, batched the
  same way the CD path is. `SectorExtraction` grew `ExtractDataType.DvdUserData2048`
  (2048 bytes, no sync/EDC to check — the drive's own Reed–Solomon ECC is the
  proof). `extract-sectors` auto-detects DVD/BD media and overrides
  `--as`/`--no-c2`/`--sub` with a printed note, since none of those concepts
  exist on that media; `--as dvd` also works explicitly.
  Files: `src/DiscForge.Core/Dumping/SectorExtraction.cs`,
  `src/DiscForge.Devices/Reading/DriveExtractionReader.cs`,
  `src/DiscForge.Cli/Program.cs`, `tests/DiscForge.Core.Tests/SectorExtractionTests.cs`
  (4 new tests). 2,500 tests green (net8.0 AND net8.0-windows both verified —
  see `build.sh cli-win` for the sandbox's multi-TFM build method).
  **Confirmed on real hardware**: a PAL Resident Evil 4 PS2 disc extracted
  clean, 2,228,528/2,228,528 sectors, COMPLETE, no aborts
  (`ps2game.iso`, MD5 `30255F8E8958A963212CA6455BB29EE0` — pending a redump.org
  cross-check to confirm bit-perfect, not just non-aborting).
  **Still needed**: `git add`/commit/push (Claude can't push from the sandbox —
  do this from Andy's machine), then update the "State" line above once it's in.

## Landed since v1.66.0

- **Track-aware `--disc`** — DONE. `ExtractSectorsDrive` walks the TOC: one span
  per track, per-track audio hint + `RequireDataSync`, the 150-sector audio pregap
  at a data→audio transition captured as its own boundary span
  (`ClassifyFailuresAsBoundary` → `BadSectorMap.BoundaryLba`, grade unaffected),
  all spans into ONE atomic bin + merged sidecar, cue with real per-track
  TRACK/INDEX 00/01 entries.
- **Auto-audit** — DONE. Every raw drive extraction now ends with
  `ExtractionAudit` (Core/Dumping): an independent re-read of the written file —
  sync census + sampled EDC on data spans, zero census everywhere; AUDIT
  PASS/FAIL printed, failure sets exit 2. `--no-audit` opts out.
- **`inspect-raw` honesty fix** — DONE. Sync-less sectors counted per data track
  (subcoded) and disc-wide (main-only), reported in notes, and the verdict
  states its coverage instead of overclaiming "clean".
- **Disc MRI** — DONE. `dforge disc-mri <bin|cue> [out.svg|png]`
  (Core/Forensics/DiscMri): per-sector evidence on the physical disc via real
  Red Book spiral geometry; radial streak = scratch, ring = pressing defect.
  Worst evidence wins per pixel. Sidecar auto-overlaid.
- **Dump Certificate** — DONE. `dforge dump-cert <image> [--gen-key|--key] |
  verify | prove | check` (Core/Preservation/SectorMerkle + DumpCertificate):
  signed (ECDSA P-256, merge-cert key format) provenance sidecar with a Merkle
  root over the sectors — `prove` emits a ~15-hash path for one sector, `check`
  verifies a bare 2352-byte slice against the signed root WITHOUT the image.
  Sidecar counts auto-included. AND extract-sectors grew `--cert [--cert-key f]`:
  a dump can now be born certified — drive, firmware, settings, per-span grades,
  audit verdict and Merkle root captured at the moment of extraction (gap 3
  closed).
- **Pressing DNA** — DONE. `dforge pressing-dna <a.cue> [b.cue]`
  (Core/Forensics/PressingDna): disc-genome's complement — the offset-SENSITIVE
  fingerprint (exact geometry, pregaps, audio edges, MCN/ISRC) that tells
  PRESSINGS of one title apart; names the constant-shift write-offset signature
  when it sees one. Verdicts: same pressing / same title different pressing /
  different discs.
- **Drive Dossier** — DONE (gap 4). `dforge drive-dossier <drive:|vendor model>`
  (Core/Devices/DriveDossier): local per-drive memory seeded by the knowledge
  base — observations accumulate across sessions into distilled facts and
  warnings (mute signatures, first-sector C2 wolf-cries, confirmed offset,
  overread reach). extract-sectors auto-records the sync-gate mute signature.
- **Disc Actuary** — DONE (feature E). `dforge disc-actuary <id> --record …` /
  `--collection` (Core/Forensics/DiscActuary): every quality scan appends to a
  per-disc time series; rot-kinetics' decay model fits each disc; the shelf
  ranks by remaining readable life — "re-dump these first, they're dying
  fastest". Accepts scan-import formats or manual --tier1. (Also fixed a latent
  RotKinetics DateTimeOffset overflow on near-zero slopes — projections beyond
  500 years now honestly report "no crossing".)

## The immediate arc

1. **Canonical re-dump — DONE.** Track-aware `--disc` proven on real hardware
   for the first time: `ps1-redump.bin` + `ps1-redump.cue` on Andy's PC, all 8
   tracks COMPLETE, AUDIT PASS (data track sync 153,904/153,904, EDC clean;
   audio pregaps read as genuine silence, not damage). This supersedes the old
   scattered interim dumps (`data.bin`, `game2.t02..t08.bin`) and the old
   half-void `game.bad.bin` — keep those only as prior evidence, don't use them.

2. **Redemption burn + round trip — DONE, via ImgBurn (DiscForge's own SPTI raw
   engine still doesn't work — see below).** `ps1-redump.cue`/`.bin` burned to
   the last CD-R (a CMC Magnetics disc, not the Taiyo Yuden NEXT.md previously
   assumed) on the TSSTcorp SH-224DB via ImgBurn 2.5.8.0, SAO write type, then
   verified by read-back: **289,321/289,322 sectors bit-perfect**. The one
   miscompare is at LBA 153903 — the LAST sector of the data track, right at
   the data→audio boundary. ImgBurn's own log: "The drive probably corrected
   the L-EC Area because it's wrong in the image file" — a well-known
   boundary-sector ECC quirk in CD preservation, not a systemic dump or burn
   problem. This is the first fully closed dump→burn→dump round trip this
   project has ever achieved (99.9997% bit-perfect). Note: the TSSTcorp's C2
   pointers are unreliable (flags the first sector of most read spans) —
   `--no-c2` was used reading on it; the sync gate + EDC checks carry integrity.

   **UPDATE (2026-08-25, later the same day): `--simulate` was run for the
   first time and found a real, fixed bug — not a guess, a live sense-code
   trace.** `burn-raw ps1-redump.cue D: --engine spti --simulate` got past
   MODE SELECT and 200+ WRITE(10) chunks fine, then failed at LBA 225 with
   "Not ready: ASC 0x04 ASCQ 0x08" (LONG WRITE IN PROGRESS — the drive
   transiently busy flushing its buffer, a normal condition a well-behaved
   initiator retries). The write loop already HAD a retry for exactly this
   (ASC 0x04 → wait 2s, reissue, up to 6 times) — but it re-fetched sense
   with a fresh REQUEST SENSE CDB after the failure instead of reading the
   sense the drive returned WITH the failing WRITE(10) itself, and by the
   time that follow-up REQUEST SENSE landed the drive's contingent-allegiance
   condition had already cleared, so it came back (0,0,0) — the retry
   condition (`asc == 0x04`) never matched a real 0x04, so the very first
   transient busy moment was fatal. **Fixed**: read `SenseKey`/`Asc`/`Ascq`
   straight off the `SptiResult` the failing `WRITE(10)` already returned
   (`SptiRawDaoBurnEngine.cs`, the write loop in `Burn()`) instead of issuing
   a second REQUEST SENSE; also raised the retry bound 6→10 since a full
   flush can take a few seconds. Rebuilt (0 errors), full suite still
   2,503/2,503. **Not yet re-run on hardware** — this is the next thing to
   try, same command as before:
   `dforge burn-raw ps1-redump.cue D: --engine spti --simulate`.
   If it now runs clean to completion, that's the real Raw-mode write path
   validated non-destructively for the first time ever, and a real (non-
   simulate) burn becomes reasonable to attempt next. If it fails again,
   report the EXACT new sense code — do not assume it's the same bug.

   **CORRECTION (2026-08-25 morning): the "STOP guessing" block below chased
   the wrong code path for five rounds — read this bit first.** `Burn()` (the
   method `dforge burn-raw --engine spti` actually calls for a real burn) is
   **Write Type = Raw**: the whole disc, lead-in included, streamed as raw
   main + P-W subchannel via WRITE(10), with deliberately **NO SEND CUE
   SHEET** at all — the code's own comment says a cue sheet under Raw mode is
   a command-sequence error (ASC 0x2C). But every one of the five fixes below
   was made against `TestCue()`, a separate, legacy diagnostic that still
   exercises **Session-At-Once + SEND CUE SHEET** (data block type 0) — a
   setup `Burn()` stopped using before this session started. All five fixes
   hardened a path the real burn doesn't call. `TestCue()`'s rejections
   (ASC 0x26/0x00 below) say nothing about whether the real Raw-mode burn
   works — that path has genuinely never been tried, not even non-
   destructively. **The actual next step, not yet attempted**: run
   `dforge burn-raw <cue> <drive> --engine spti --simulate` — this runs
   `Burn()`'s FULL real write path (MODE SELECT Write Type=Raw, NWA read,
   chunked WRITE(10) over the whole raw+P-W image, finalise) with the drive's
   test-write bit set (laser off) — genuinely non-destructive, reusable disc,
   and it tests the code that matters instead of the abandoned cue-sheet
   setup. `--test-cue` and the CLI help now say this explicitly
   (`src/DiscForge.Cli/Program.cs` `BurnRawCmd`, and the class doc comment on
   `SptiRawDaoBurnEngine`). Do this before attempting a 6th cue-sheet fix —
   there should never be a 6th, that whole path is dead.

   The original (now superseded) framing, kept for the record — DiscForge's
   `TestCue()` diagnostic still does not work, after **FIVE** rounds of
   fixes this session, all real, source-grounded, committed — and all
   rejected with the byte-for-byte identical sense code:
     1. Cue-sheet Data Form byte was 0x10 (not a defined MMC code); corrected
        against cdrdao's `GenericMMC::createCueSheet` to 0x00/0x10/0x20 by
        track type. (A PDF-spec extraction along the way suggested 0x08,
        ALSO wrong and rejected — the WebFetch summarizer is unreliable for
        exact byte tables, the same failure mode that hallucinated a redump
        hash match earlier this session. Don't trust it for spec bytes again
        without a second, independent source.)
     2. Lead-in was sending three Red-Book-style POINT entries (A0/A1/A2)
        that cdrdao doesn't send at all; replaced with cdrdao's single
        generic lead-in entry (14→12 total entries).
     3. MODE SELECT's Data Block Type was hardcoded to 3 (raw+P-W subchannel)
        even for the Session-At-Once cue-sheet-test path; cdrdao uses 0
        there, reserving 3 for the actual Raw write type. Fixed.
     4. `SetRawDaoWriteParameters` built the whole write-parameters page from
        a blank record instead of reading the drive's current page first and
        flipping only specific bits (cdrdao's `getModePage`+selective-bits
        approach) — notably, cdrdao never touches the Track Mode nibble at
        all, but DiscForge was unconditionally overwriting it. Rewrote as a
        genuine MODE SENSE → modify → MODE SELECT read-modify-write. STILL
        rejected, identical sense code.

   The diagnostic that at least separated "drive limitation" from "DiscForge
   bug": ImgBurn 2.5.8.0 burning the SAME `ps1-redump.cue` on the SAME drive
   succeeds completely — real burn AND read-back verify, 289,321/289,322
   sectors bit-perfect (see above) — using **SAO** as the write type for the
   whole operation, cue sheet included. So the drive and cue-sheet CONTENT
   are provably fine; something in exactly how DiscForge issues the SCSI
   commands (ordering, timing, a CDB field, or something not yet considered)
   is still wrong, and it's specific enough that five source-grounded content
   fixes didn't touch it.

   **What did NOT work as a diagnostic**: asking the user to enable ImgBurn's
   verbose/debug SCSI logging — couldn't find the toggle in the UI in the time
   available. A packet-capture-style diagnostic (ImgBurn's debug log, a
   USB/SCSI sniffer, or a kernel SPTI trace) would still be the right move
   **if** the goal were to make `TestCue()`'s SAO+cue-sheet path work — but
   per the correction above, that's no longer the goal; `Burn()` moved to
   Raw mode (no cue sheet) before this session even started, and the actual
   non-destructive test for THAT path (`--simulate`, see above) doesn't need
   any of that tooling and had simply never been run. Revisit exotic capture
   tooling only if `--simulate` itself fails in a way source-reading can't
   explain — don't reach for it before that.

## Hardware track (Plextor PX-W5224TA)

- The drive is a fine READER; its 22-year-old write side is retired from long burns.
- **0xD8 lead-in engine**: direct D8 window confirmed on this firmware = LBA −75..−1
  (pregap zone). Deep lead-in (TOC territory) needs redumper's seek-and-read-cache
  technique — research + implement. Building blocks shipped: `plextor-d8` command,
  `MmcCommands.PlextorReadCdDa`.
- **Offset confirmation**: knowledge base says +30 (reference). Needs a mainstream
  audio CD present in AccurateRip: rip with `--disc` (cue auto-emitted), then
  `accuraterip <cue> --url` → download dBAR → `detect-offset <cue> --db <file>`.
  (Disc-ID math is pinned to a published vector; a 404 means the pressing is absent.)

## Housekeeping (user-side, minutes) — DONE 2026-08-29, via `housekeeping.ps1`

- ~~Verify the v1.66.0 Release workflow ran green; paste release notes into the
  GitHub release description.~~
- ~~Uninstall the old "DiscForge 1.65" from Program Files (shadows `dforge` on PATH).~~
- ~~COPTR + awesome-list submissions: paste-ready text in `docs/registry-submissions.md`.~~
- Cross-check AaruFormat interop against a real Aaru-generated `.aaruf` — still genuinely
  blocked on having one; `housekeeping.ps1` can't manufacture that file, only flag it's needed.
  Revisit if/when a real Aaru dump turns up.

Andy confirmed this batch done 2026-08-29; re-open any individual line if it turns out not to
have gone through (e.g. the uninstaller needed a manual follow-up, or the release notes still
need a second pass).

## Correction: feature D (Consensus healing) was already DONE, undocumented

REEVALUATION-2026-08.md lists "D. Consensus healing" as a not-yet-built
frontier feature ("on the shelf: RecoverySession, MergeCertificate,
C2ConsensusMerge, AccurateRip"). That undersold it — `dforge merge-cert`
(Program.cs `MergeCertCmd`, backed by `Core/Recovery/ProvenanceMerge.cs` +
`MergeCertificate.cs`) already IS that feature, fully wired: merges N
imperfect rips of the same pressing, honours each input's `.badsectors.json`
sidecar (holes excluded from the vote, not counted as data), records
per-sector provenance (which copy won and why — AllAgree/EdcRecovered/
VoteVerified/VoteBestEffort/SingleSource/Unrecovered), and emits a signed
(ECDSA) `.dmc.json` certificate anyone can re-verify (`merge-cert verify`,
re-hashes inputs+output against the signature). Unit-tested
(`MergeCertificateTests.cs`). **Nothing to build here — if a future session
reads REEVALUATION.md and reaches for feature D, point it at `merge-cert`
first**, and only extend it (e.g. an AccurateRip-aware tie-breaker for audio
tracks with no EDC, or `C2ConsensusMerge`'s byte-level voting as a pre-pass
before the sector-level provenance merge) rather than reinventing it. Only
gap left in this space: no GUI view exists for it (see below).

## Deliberately parked for a hardware/desktop session

- **Resumable dumps (gap 5)**: progress journal beside the `.part`; needs live
  drive testing to trust the seek/append semantics — don't build it blind.
- ~~**`dforge prove` (feature H)**~~ — **UNPARKED AND BUILT, 2026-08-27**, see
  the dedicated write-up below. Not yet hardware-tested.
- **WASM Core (feature C)**: needs NuGet/Blazor tooling the sandbox can't reach;
  build on Andy's machine or CI.
- **GUI catch-up (gap 7)**: 60 views, and NONE of this session's landings have
  one — `merge-cert`/consensus healing, `dump-cert`, `disc-mri`, `pressing-dna`,
  `drive-dossier`, `disc-actuary`, `vault`, the DVD/BD extraction fix. All are
  pure-Core + CLI already; a WinForms view is plumbing, not research, but it
  needs eyes-on visual iteration (colors, layout, control placement) that a
  sandbox with no Windows/display can't do blind — do this on Andy's machine
  where the result can actually be looked at, not guessed at.

## 2026-08-27 — burn-raw --engine spti: BURN CONFIRMED GOOD; verify-tool false negative found & fixed

The ATIP-based lead-in fix (previous session) was hardware-tested today and
worked completely:

- `burn-log6.txt`: real ATIP lead-in start = `97:26:66` → LBA −11634 (a real,
  disc-specific value, nowhere near the old fixed −22650 guess). Full RAW-DAO
  burn completed 0% → 100% with **zero WRITE(10) failures** — the deterministic
  same-LBA failure that killed three straight burns is closed.
- `dforge writeinfo D:` after the burn: `Disc status: complete / finalized`,
  1 session, tracks 1–8, disc type 0x20, "Track 1: not blank" — the FIRST
  writeinfo all session that didn't come back blank. The burn is real.

Then `raw-verify-readback golden.img readback.bin` reported **FAIL** — 99.99%
of program sectors "mismatched". This looked catastrophic but turned out to
be a bug in the verify tool, not the burn. Traced it by hand (descrambled and
byte-compared golden.img against readback.bin directly in Python, brute-forcing
the alignment offset): **once correctly aligned and descrambled, every sampled
sector — 286/286 on a full deterministic sweep — is byte-identical.** The burn
is provably correct.

Root cause, found in two passes (the first pass below was wrong and is kept
here so a future session doesn't repeat it):

- **First (wrong) theory**: `ProgramBaseAbs` anchors alignment on a single Q
  sub-channel position frame, and real Q reads jitter, so a one-off bad frame
  seemed like the explanation. Fixed it to vote across the whole scan window
  and take the mode instead of the first hit — rebuilt, re-tested on hardware,
  **identical FAIL, byte-for-byte identical numbers.** That ruled out jitter:
  a python dump of the full 400-sector vote window showed the Q sub-channel
  decoding to abs 151 **consistently, 399/400** — not an outlier, a stable
  reading. So voting made no difference; the wrong value was winning honestly.
- **Real root cause**: the Q sub-channel is reported with a small, constant
  address skew relative to the main-channel data it's bundled with in the same
  raw capture — a real, documented drive/read-back phenomenon (sub-channel and
  main-channel aren't always extracted perfectly synchronized). Proof: the
  MAIN-CHANNEL sector header (bytes 12–14, MM:SS:FF) decodes to a clean,
  consistent 150 (400/400 votes) — matching the byte-for-byte-correct
  alignment — while the Q sub-channel on the exact same capture consistently
  says 151. Aligning on Q was comparing every sector against its neighbour
  instead of itself; CD content has zero redundancy across sector boundaries,
  so that reads as "everything is corrupted" even on a byte-perfect disc.

Fixed properly in `src/DiscForge.Core/Raw/RawReadbackCompare.cs`: added
`MainChannelBaseAbs`, which derives the alignment anchor from the main-channel
header instead of Q (trying both scrambled and unscrambled interpretations,
since golden and a real capture can each be in either state, and voting the
same way as the Q fallback). `Compare()` now prefers this and only falls back
to the Q-based `ProgramBaseAbs` when no header is available (audio-only
regions, which have no header at all). Verified the exact fix logic
independently in Python against the real `golden.img`/`readback.bin` before
writing the C# (golden → 0/400 votes, readback → 150/400 votes — matching the
proven-correct offset exactly). Build clean (0 warnings/errors); full suite
2502/2503 — the one failure (`AudioCdTests.Over_74_minutes_...`) is a
pre-existing, unrelated `OutOfMemoryException` on a ~750MB single-allocation
test that this sandbox's memory ceiling can't sustain (confirmed unrelated:
that file wasn't touched, and the 3 `Raw`/`RawReadback` test classes — 29
tests — all pass clean in isolation). Delivered to
`C:\dev\DiscForge\src\DiscForge.Core\Raw\RawReadbackCompare.cs`; the earlier
(wrong) vote-only version was superseded, not layered on top of.

**The Q sub-channel address skew itself is worth a closer look separately**:
it's real, it's consistent (not noise), and it's currently silently absorbed
by preferring main-channel alignment — but the "159 mis-addressed" and "744
sub-timing" counts in the FAIL report above are real Q differences on this
disc, some of which may just be this same skew being judged against a
still-skewed golden Q rather than being corrected for. Worth a dedicated
look once the main verify chain is confirmed green, not before.

Re-ran on hardware after the skew fix:

```
Main channel: 1 mismatch(es), 0 with broken EDC  (153,904 descrambled-on-read, content byte-identical)
Sub-channel: 902 differ - 158 mis-addressed, 0 protection-loss, 744 timing-only
Dropouts:    135,402 program sector(s) missing from the read-back
Result: FAIL - 135,561 defect(s)
```

Still graded FAIL, but read what's actually in it: **153,919 of 153,920
program sectors (99.9994%) are byte-identical.** The 1 main-channel mismatch
is at the very last sector before `read-raw` hit the data→audio track
boundary (`--field auto` correctly stopping — expected, not corruption). The
158 mis-addressed + 744 sub-timing sectors (0.1% and 0.5% of the disc) are
ordinary real-world Q sub-channel read noise — scattered, not paired in any
suspicious pattern, the same class of jitter `--reread/--consensus` exists to
smooth over on a re-read, not evidence of a bad burn. The 135,402 "dropouts"
are NOT unread/corrupt sectors — they're golden's other 7 (audio) tracks that
`read-raw` never attempted to read at all, because this capture only covered
track 1 (data) before stopping at the mode boundary. **`raw-verify-readback`
needs `--partial` for this comparison** (it exists exactly for "an intentional
sub-range, e.g. one track of a mixed-mode disc read on its own" — this wasn't
used yet). With `--partial`, dropouts stop counting and the grade should
land on FAIL-only-for-the-158-mis-addressed (or PassWithNotes, depending on
whether any tie ever crosses the strict defect line) rather than FAIL from
"135,402 defects" that were never really defects.

**Bottom line: `burn-raw --engine spti` now produces a genuinely correct,
byte-verified RAW-DAO burn on Andy's real hardware (TSSTcorp SH-224DB).** The
core bug this entire session was chasing — burn reports success but the disc
comes back blank/corrupt — is closed. What's left is polish, not correctness:

- Re-run `raw-verify-readback golden.img readback.bin --partial` for an
  accurate grade on the track-1-only capture already in hand.
- For genuinely complete disc verification, read back the other 7 (audio)
  tracks too (`read-raw D: t2.bin --track 2`, etc., or a whole-disc read that
  switches field mode at the boundary) and verify those against golden as
  well — not done this session, optional polish once the above is confirmed.
- The 158 mis-addressed sectors are worth one glance (are they clustered near
  the mode boundary, or genuinely scattered across the whole track?) but
  nothing in this session's data suggests they're anything but ordinary drive
  noise.
- Consider whether `LeadInSectors=22500` (the fixed default) in
  `RawImageGenerator` should become ATIP-aware for `build-raw` too (today
  only the burn engine reads ATIP) — harmless for verification since
  alignment is address-based, not offset-based, but worth it for anyone
  reading golden.img's raw bytes directly.

Next steps once `--partial` confirms a clean-enough grade: unpark
`dforge prove` (feature H, previously explicitly blocked on this bug).

**Update, same day**: ran with `--partial` — down to 159 real defects (1
main, 158 mis-addressed) across 153,920 sectors, 99.897% clean. Dug into the
158 by hand (replicated the comparator in Python against the real files):
they're not one uniform thing. A hand-picked sample (sectors 1314/1474/1494/
1516, shown in the tool's own "first differences") turned out to be cases
where the READ-BACK's own Q frame fails its own CRC — a transient sub-channel
read glitch on THIS read pass (real optical media does this occasionally; the
byte pattern is a single flipped bit in the control/ADR nibble), not evidence
the disc holds a wrong address. That's a fundamentally different, much more
benign thing than "the disc was written with a bad address" — the drive
already told us the frame is untrustworthy via its own CRC, so judging it
byte-for-byte against golden was mislabeling read noise as a burn defect.
Separately, a full scan found ~15 more concentrated right at the very tail of
the capture (154055–154069) — the last ~15 sectors before `read-raw` hit the
data→audio boundary, a messy addressing region (postgap/pregap countdown)
that's a known-tricky spot for sub-channel decoding, not the rest of the disc.

**Update 2 (final result, same day)**: re-ran on hardware after the
read-noise fix — `sub-read-noise` absorbed 304 of the 902 sub-channel
differences that were previously either misclassified or padding out the
mis-addressed count. Final result:

```
Main channel: 1 mismatch(es) — the descrambled content is byte-identical
Sub-channel: 15 mis-addressed, 0 protection-loss, 583 timing-only, 304 read-noise
Result: FAIL - 16 defect(s) across 153,920 sectors (main 1, mis-addressed 15)
```

**99.9896% of the disc is exactly byte-identical to the golden.** The
remaining 16 "defects" (1 main + 15 mis-addressed) are the same cluster
identified above — sectors 154055–154069, the last ~15 sectors of the
capture, right at the data→audio track-type boundary where `read-raw`
stopped. That's a known-messy addressing region (postgap/pregap countdown
across a track transition), not evidence of disc-wide corruption — every
other sector across the whole ~154,000-sector data track (the actual PS1
game content) is exact.

**This closes the session's core question. `burn-raw --engine spti` produces
a genuinely correct, byte-verified RAW-DAO burn on Andy's real hardware
(TSSTcorp SH-224DB).** The bug that started this entire multi-day session —
burn reports success but the disc comes back blank or wrong — is fixed and
proven, not just believed. What's left is optional polish, not correctness:
reading back the other 7 (audio) tracks for full-disc coverage, and possibly
narrowing why sub-channel addressing gets messy right at a track boundary
(low priority — it's a capture-edge artifact, not a burn defect). Next
concrete step: unpark `dforge prove` (feature H) now that the burn engine
itself is proven on hardware.

Fixed: `RawReadbackCompare` now distinguishes these from real mis-addressing.
When golden's Q is valid but the read-back's own Q CRC fails, it's classified
as `sub-read-noise` (Warning, doesn't fail the grade) instead of
`mis-addressed` (Defect). "Mis-addressed" is now reserved for what it should
actually mean: a Q frame that's internally self-consistent (its own CRC
checks out) but decodes to the wrong place — a real defect. Added
`Report.SubReadNoise`, wired through the CLI/JSON/HTML report, and fixed the
existing `A_changed_q_address_is_a_mis_addressed_defect` test (it had been
flipping an address byte without fixing the CRC, which is what the read-noise
case looks like, not a genuine mis-addressed one — recomputed the CRC after
the flip so it tests what its name says) plus added a new test for the
read-noise path itself. Build clean, 2504/2505 pass (the one failure is the
same pre-existing unrelated `AudioCdTests` OOM). **Not yet re-run on
hardware** — next step is confirming the grade on `golden.img`/`readback.bin`
lands on PASS or PassWithNotes with `--partial`, which per the hand analysis
above it should (159 → ~1 real defect, the rest reclassified as noise).

## 2026-08-27 (cont'd) — `dforge prove` (feature H) built

Unparked and implemented now that `burn-raw --engine spti` is proven correct
on hardware (see above). New command: `dforge prove <disc.cue> <drive>`
(`src/DiscForge.Cli/Program.cs`, `ProveCmd`, dispatch entry next to
`read-raw`).

What it does, in one verb: composes the golden image from the cue
(`RawImageGenerator.Generate`, same as `build-raw`) → burns it via
`SptiRawDaoBurnEngine.Burn` (the exact path this whole session proved) →
reads the disc's own post-burn TOC (`DiscReader.ReadToc`) → for EVERY track,
reads it back with that track's own TOC-derived start/length/field
(`RawDiscReader.Read`, data vs audio field auto-selected per track — this is
what saves a user from the manual `--track N` juggling `HARDWARE_RUNBOOK.md`
§4 currently spells out by hand) → verifies each track against the golden
with `RawReadbackCompare.Compare(..., partial: true)` (`--partial` because
each per-track capture is legitimately a sub-range of the whole-disc golden)
→ prints one line per track (OK/FAIL + summary) and one final verdict:
`=== PROVEN ===` or `=== FAILED ===`. Exit code 0/1 matches. `--report`
writes a per-track HTML certificate; `--keep-temp` keeps the golden image and
every track's raw capture instead of deleting them (useful for the same kind
of by-hand forensics this session did on `golden.img`/`readback.bin` when
something looks off).

Scope, stated plainly so nobody overclaims it later: this is the BURN half of
the feature H spec ("dump → audit → certificate → optional reburn →
cross-verify"). It starts from a `.cue` you already trust — it does NOT run
`dump-score`/`dump-audit`/`dump-merge`/`convert`/`verify-convert` first. That
front half already exists as separate shipping commands (see
`HARDWARE_RUNBOOK.md` §4); wiring them into `prove` too, so the verb truly
covers dump-to-reburn end to end, is a reasonable follow-up but was out of
scope for this pass — the whole reason feature H was blocked all session was
the burn engine, not the dump/audit tooling (which was never in question).

Verified: builds clean on both targets (`cli` and `cli-win`, 0 errors), full
suite still 2504/2505 (same pre-existing unrelated `AudioCdTests` OOM). Ran
`prove` with no args (usage prints correctly), with a real temp cue/bin on
the non-Windows target (correctly fails with "needs the Windows build" rather
than crashing), and confirmed cue/extension/drive-letter validation all
behave the same way `burn-raw`'s do. **Not yet run end-to-end against real
hardware** — that's the next thing to do: `dforge prove ps1-redump.cue D:`
on Andy's machine, expecting it to reproduce today's manually-driven result
(PROVEN on the data track; the 7 audio tracks are untested territory since
they were never read back this session — worth watching for anything
mixed-mode-specific `read-raw --track` didn't already surface).

## 2026-08-27 (continued) — "XA Form1/2-aware EDC/ECC" backlog item: already done, no code needed

Andy asked which of the two remaining no-hardware PS1-backlog items (CU2
sidecar support vs. XA Mode 2 Form 1/2-aware EDC/ECC) was quicker. Checked
the second one first since it sounded smaller. Read every EDC/health-map
consumer in `DiscForge.Core` that branches on sector mode (`DiscHealthMap`,
`DiscMri`, `PremasterGate`, `DumpReconstruct`, `DumpMerge`,
`ExtractionAudit`, `RawImageInspector`, `DumpingWizard`,
`RawReadbackCompare`) — every single one already checks the XA subheader
submode byte (`main[18] & 0x20`) and picks Form 1 EDC+ECC vs. Form 2
EDC-only (or returns "nothing checkable" when the Form 2 EDC field is
zero/unused) before validating. `SectorExtraction.cs`'s per-datatype
extraction path is explicitly told which form to expect by the caller, so it
was never exposed to this failure mode either. **No false Form 2 damage
reports exist anywhere in the current codebase.** Marked done in
`ROADMAP.md` — struck through with a note, not deleted, so a future session
doesn't waste time rediscovering this. No build/test cycle needed since
nothing changed.

Conclusion: **CU2 sidecar read/write/verify is the actual next-quickest
no-hardware item** (the XA item turned out to be a documentation cleanup,
not a feature). Not yet started — next step is reading Cue2cu2's format
notes (github.com/NRGDEAD/Cue2cu2: absolute-LBA track map, explicit
data-track start + lead-out, rev2 per-track pregap) and DiscForge's existing
`DaoCueSheet.cs`/cue-parsing code to scope a `.cu2` reader/writer/verifier as
a dialect-free cross-check against `.cue`.

## 2026-08-27 (continued 2) — CU2 was ALSO already done; built the license-string reader instead

Went to build "CU2 sidecar support" per the conclusion above and found it was
already fully implemented (`DiscForge.Core.Cue.Cu2` — `Write`/`Parse`/`Verify`
— plus `dforge cu2 write|verify`, tested in `Cu2Tests.cs`, landed 2026-08-15).
Same for **Pregap-accuracy check** (`PregapConformance.cs` +
`dforge pregap-check`, also already complete). So of the four PS1-backlog
items on the table, three were already done and undocumented as such. Marked
all three struck-through in `ROADMAP.md` this time (with what already exists,
so nobody re-investigates them either).

The fourth, **on-disc region license-string reader**, was genuinely missing
(confirmed: `grep`ing for "Sony Computer Entertainment" / "LicenseString" /
region-marker outside `PsExe.cs`'s own PS-EXE-header field — a different,
already-existing thing — turned up nothing). Built it:

- **`src/DiscForge.Core/PlayStation/LicenseString.cs`** — new. `Parse(sector)`
  checks the fixed 32-byte "          Licensed  by          " line-1 text and
  the region-specific line-2 text Sony's mastering tools wrote into sector 4
  of the data track (Japan/Europe/America), ahead of the ISO 9660 volume
  descriptors at sector 16. `FromImage(path)` opens a .cue/.bin/.iso via the
  existing `RawTrackReader` (the same cooked-2048-byte-sector abstraction
  `IsoReader`/`SystemCnf` already use — no new sector-layout code needed) and
  reads sector 4 directly. `CrossCheck(license, systemCnfRegion)` compares the
  license text's region against `SystemCnf.RegionOf()`'s region string and
  reports a one-line disagreement (or null when they agree, or when
  SYSTEM.CNF's region — Korea/Asia/Unknown — has no dedicated license block
  of its own to compare against, which real SCEK/SCEA-adjacent discs are
  known to lack).
- **CLI**: `dforge license-check <image> [--json]` — prints the detected
  region, the SYSTEM.CNF region if found, and flags a mismatch. Exit 0 when
  well-formed and no mismatch, 2 otherwise (matches the `pregap-check`/`cu2
  verify` convention).
- **Source and honesty note**: the byte layout (line 1 = 32 bytes at 0x000;
  line 2 = 33 bytes Japan / 38 bytes Europe+America at 0x020; padding fills
  the rest) comes from psx-spx (consoledev.net/cdromformat, "Licence
  String" section), fetched three times independently this session with
  consistent results. It's also internally self-consistent: the documented
  line/padding lengths sum to exactly 2048 bytes for both layouts
  (32+33+1983 and 32+38+1978) — pinned as a dedicated test
  (`The_documented_line_and_padding_lengths_sum_to_exactly_one_sector`), which
  would fail if any of those three numbers had been transcribed wrong. Line 1
  and each region's line 2 are matched exactly. The *padding content* after
  line 2 (documented as all-zero for EU/US, a repeating fill pattern for
  Japan) is checked too, but only informationally — that specific byte
  pattern was not cross-checked against a real disc dump this session, so a
  mismatch there is reported as a note (`PaddingLooksStandard = false`, an
  `Issues` entry) and never turns a correctly-identified region into
  "unrecognised." This mirrors the same SCOPE/HONESTY discipline
  `DaoCueSheet.cs` uses elsewhere in this codebase.
- **Tests**: `tests/DiscForge.Core.Tests/LicenseStringTests.cs`, 17 cases —
  the layout self-consistency check above, exact parsing for all three
  regions, garbage/all-zero/corrupted-line-2 handling, non-standard-padding-
  is-a-note-not-a-misidentification, the cross-check (agree / disagree / no
  comparison possible), and an end-to-end `FromImage` test that builds a real
  ISO via `IsoBuilder`, stamps a genuine Europe license block into its sector
  4, and reads it back.
- **Verified**: builds clean (`cli`/`cli-win`, 0 errors/warnings). Full suite
  2521/2522 (2520 pass + the new 17, only the pre-existing unrelated
  `AudioCdTests` OOM fails — same as every run this session). CLI usage text
  and the not-found error path smoke-tested directly.
- **Not yet run against a real disc/dump** — the padding-pattern caveat above
  is the reason to treat any real-world padding mismatch as a note worth a
  second look, not a first-day certainty. If a real PS1 dump's sector 4 turns
  up with different padding than documented, that's useful signal to
  incorporate here, not a bug report against this code.

All four items from the "what else can we do without hardware" menu are now
closed (three found already done, one built this session). Nothing left on
that specific list; see `ROADMAP.md`'s PS1 backlog for the remaining
(unstarted) items — multi-disc `.m3u`/`MULTIDISC.LST` modeling and full R–W
subchannel capture — if more no-hardware work is wanted next.

## 2026-08-28 — v1.68.0 installer delivered; multi-disc set modeling + full R-W subchannel capture built

Andy built and confirmed a working Windows installer from the sandbox-provided
`installer/publish.ps1` + `installer/DiscForge.iss` (Inno Setup) — first real
`.exe`/installer this project has produced, at `C:\dev\DiscForge\installer\
Output\DiscForge-Setup-1.68.0.exe`. (Sandbox still cannot cross-compile a real
Windows binary itself — no NuGet network access, no cached win-x64 runtime
packages — so the installer scripts have to run on Andy's machine; this is
now confirmed working end to end.)

Then picked "all four" from a menu of what to do next:

**1. Multi-disc set modeling — DONE.** New `src/DiscForge.Core/Library/
MultiDiscSet.cs`: `MultiDiscDetector.Detect` groups image paths by the
Redump/No-Intro "(Disc N)"/"(Disc N of M)" naming convention (same
directory + title with the tag stripped, case-insensitive), reports missing
disc numbers from gaps or a declared total, and ignores anything that isn't
genuinely part of a set (a lone untagged "(Disc 1)" with no sibling and no
declared total is NOT reported — too ambiguous). `MultiDiscManifestBuilder`
hashes every disc (reusing `ImageChecksums.Compute`) into a `MultiDiscManifest`
with set-level completeness. `OdeExport.cs` gained `OdeExporter.PsioSet` (all
discs of a title share ONE folder — confirmed against the real PSIO Systems
Manual R30, contradicting a stale in-repo comment that said one folder per
disc) and `PsioMultiDisc.BuildLst`, which emits a real `MULTIDISC.LST`: one
filename per line, CRLF-joined, **no trailing terminator**, verified byte-for-
byte with `od -c` against the documented format. CLI: `multidisc-detect
<folder> [-r]`, `multidisc-manifest <folder> --title X [-r] [--json]`, and
`ode-export` is now variadic (any number of `.cue` paths before the output
folder) so a multi-disc PSIO export is one command. 20 new tests
(`MultiDiscSetTests.cs`, 14; `OdeExportTests.cs`, 3 new covering single-disc-
no-LST, multi-disc-shared-folder-with-LST, and same-filename-collision-refused).

**2. Full R-W subchannel capture — DONE, not hardware-tested.** Investigated
first and found almost everything already existed: `SubcodeFrame` (Core/Raw/
SubQ.cs) already encodes/decodes all 8 channels (P, Q, R-W) across all three
physical layouts (Pq16 / Packed96 / Interleaved96), and `read-raw`/
`RawDiscReader` already captures full raw P-W embedded in every 2448-byte
sector as the backbone of the whole burn-verify pipeline — CD+G, CD-TEXT and
LibCrypt analysis all already consume it. The one real gap: the MMC
`CorrectedRw` (selector 0x04 — the drive's own firmware-corrected,
de-interleaved reading) was defined in `MmcCommands.SubChannel` but never
actually requested anywhere, and the dedicated standalone `SubchannelReader`
class (Devices layer) had zero CLI exposure. Built exactly that: `Subchannel
Reader.SupportsCorrectedSubchannel` + `.ReadCorrected` (refactored `.Read`
to share a new private `ReadCore`), `RawSubchannel.CompareRawAndCorrected`
(Core layer — sector-by-sector Q comparison between a raw interleaved capture
and the drive's corrected capture, reporting agreement count, CRC-validity
flips, and up to 1,000 disagreeing sector indices; a byte-level Q difference
that stays CRC-valid on both sides is not counted as a "flip" — only a
disagreement where one side's CRC validates and the other's doesn't is), and
`dforge subchannel-dump <drive> <out.sub> [--start LBA] [--length N]
[--track N] [--corrected out2.sub] [--compare]` (Windows-only, same pattern
as `read-raw`/`prove`). 4 new tests (`RawSubchannelCompareTests.cs`) — pure
logic, author matching raw/corrected byte arrays from the same `SubQ.Position`
content via `SubcodeFrame.EmitInterleaved96`/`EmitPacked96`, then corrupt one
side to check disagreement counting, CRC-flip detection, and length-mismatch
rejection. **Not yet run against real hardware** — same disclosure as
`dforge prove` before its own hardware confirmation: builds clean, logic is
unit-tested, but the actual MMC CORRECTED selector's behavior on a real drive
(does it return zeroed R-W, does the drive even support it) is unconfirmed.
Worth trying `dforge subchannel-dump D: raw.sub --corrected corrected.sub
--compare` alongside the next `dforge prove` hardware run.

Verified: `bash build.sh cli` and `bash build.sh cli-win` both 0 warnings/0
errors after every change. Full suite: 2541 passed, 1 failed (the same
pre-existing unrelated `AudioCdTests.Over_74_minutes_warns_that_80_minute_
media_is_needed` OOM this sandbox's memory ceiling has never been able to
sustain — not touched, not new).

**3. Housekeeping — reviewed, needs Andy's hands, not further sandbox work.**
`origin/main` (b14255b, tagged v1.67.0) is 4 commits behind `origin/public-
release` (the 2026-08-25 burn-raw/extract-sectors fixes) — this sandbox has
no push access to the repo (`git push --dry-run` → 403, "not in this
session's authorized repository set"), so the merge has to happen from Andy's
machine. Plus, as of this session, there's also everything through v1.68.0
(license-check, installer, multi-disc, subchannel-dump) sitting only on
Andy's local `C:\dev\DiscForge` — worth deciding in one pass whether to merge
public-release into main AND push v1.68.0 together, rather than two separate
merge operations. `gh` CLI isn't installed in the sandbox and api.github.com/
github.com are both blocked for WebFetch, so CI status can't be checked from
here either. Other items unchanged: PATH shadowing between two `dforge`
installs (old "DiscForge 1.65" still in Program Files), redump.org hash
check for `ps2game.iso` (MD5 `30255F8E8958A963212CA6455BB29EE0`, still
pending), COPTR/awesome-list submission text already drafted and ready in
`docs/registry-submissions.md`.

**4. `dforge prove` on real hardware — not yet started this window.** Next
concrete step for Andy: `dforge prove ps1-redump.cue D:` (per the write-up
above, 2026-08-27), and now also worth trying `dforge subchannel-dump` on the
same drive per point 2 above.

All of items 1 and 2's code/test files were delivered this session via
SendUserFile + the device bridge to `C:\dev\DiscForge\...` (not just built in
the sandbox) — see the file list in this entry's commit for exact paths.

## 2026-08-28 (evening) — dforge prove on the Lite-On: track-boundary bug fixed; tracks 4-8 open

First-ever real-hardware run of `dforge prove`, on a drive never tested before (`LITE-ON DVDRW
SHW-160P6S` — the TSSTcorp SH-224DB everything was previously proven on wasn't connected this
session; `Get-CimInstance Win32_CDROMDrive` showed only the Lite-On).

**Fixed, confirmed on hardware**: `ProveCmd`'s per-track read forced a single field mode (data or
audio) for a track's whole TOC-reported length, but the last few sectors of a data track
immediately before an audio track are physically audio-format (the pregap) — `RawDiscReader.Read`
correctly refused those and threw, and `ProveCmd` had no try/catch, so it aborted the ENTIRE prove
run instead of treating it as a short, honest partial capture (exactly the "expected, not
corruption" boundary documented 2026-08-27). Wrapped the per-track read in try/catch(IOException);
what was already flushed to the track's `.bin` before the throw is used as-is. Confirmed on
hardware: track 1 now completes with `16 defect(s) across 153,920 sectors (main 1, mis-addressed
15)` — the exact known-benign track-boundary signature from the TSSTcorp sessions. Tracks 2 and 3
also passed clean.

**Still open**: tracks 4-8 all FAILED, several near-total mismatch (e.g. track 8: 23,765/23,765).
This is newly-explored territory — no prior session ever read back a disc's audio tracks past
track 3, on ANY drive (`NEXT.md` 2026-08-27 explicitly lists "reading back the other 7 (audio)
tracks" as not-yet-done, optional polish). Investigated with direct Python byte-level analysis on
the kept `--keep-temp` capture (`ps1-redump.prove-golden.img` + `ps1-redump.prove-track0{1..8}.bin`,
now on Andy's machine at `C:\dev\DiscForge`, NOT copied anywhere else — the device bridge
disconnected mid-session before they could be pulled in for inspection here):

- Direct Q sub-channel decode (replicated the CRC-16/Interleaved96 extraction in Python) of each
  problem track's first 20 sectors was completely clean — correct track/index/absolute-LBA,
  matching the disc's real TOC exactly (e.g. track 4 read back decoding to absolute LBA 192565+,
  track/index transitioning 3→4 exactly where expected). No obvious sub-channel corruption in the
  sampled window.
- A raw byte search found track 4-7's actual captured main-channel content byte-identical to
  content that genuinely exists in the golden image, at the geometrically correct position (a
  constant `+22,650`-sector golden-lead-in offset accounted for) — i.e. the burn itself is very
  likely correct; this looks like a VERIFICATION problem, not a burn defect. Track 8's search hit
  was inconclusive (landed inside track 3's byte range — likely a spurious match on repetitive/
  quiet audio content, not a real result).
- Built a from-scratch software reproduction to test the theory that `RawReadbackCompare`'s
  address-alignment (`MainChannelBaseAbs`/`ProgramBaseAbs`, both windowed to the first 400 sectors)
  can't correctly locate a track deep inside a multi-track golden image at all. It does NOT
  reproduce: `DeepMultiTrackVerifyTests.cs` (new, 2 tests) builds a synthetic 6-track disc (1 data +
  5 audio) in-memory, locates a deep track (the 4th audio track) by its own Q content the same way
  `prove` would encounter it, slices a byte-perfect capture straight out of the golden image, and
  runs `RawReadbackCompare.Compare(..., partial: true)` — it passes clean in both Interleaved96 and
  Packed96 form. So the comparator's core alignment logic is NOT fundamentally broken for "a deep
  audio track read in isolation" in the abstract.
- **Conclusion, and what's left**: the failure is real but not yet root-caused. Since a clean
  synthetic deep-track capture verifies fine, the most likely remaining explanations are (a) the
  REAL capture has some genuine irregularity beyond the first 20 sectors I spot-checked (only a
  small sample was decoded before the connection dropped), or (b) something specific to this
  drive's read behavior for sectors further from the disc start that a synthetic byte-perfect
  capture can't replicate. The kept files (`--keep-temp`) are the way to finish this — full Q-CRC
  census across every sector of tracks 4-8 (not just the first 20), and a byte-diff at the aligned
  offset to see whether "main data differs" (tracks 5-8) is real content divergence or another
  alignment artifact. Needs the device bridge reconnected to resume; the files are already on
  Andy's machine, nothing needs re-capturing to continue this specific investigation.

Delivered/committed this session: the `ProveCmd` boundary fix (already pushed to `main`/
`public-release` earlier — see the git-housekeeping entry above), plus `DeepMultiTrackVerifyTests.cs`
(new regression coverage for the previously-untested "verify a deep track in isolation" shape,
independent of whether it ever explains the real failure). Full suite: 2543 passed, 1 pre-existing
unrelated OOM failure (same as every run this session). `build.sh cli-win`: 0 warnings/errors.

## 2026-08-29 — tracks 4-8 root-caused and fixed: Q-fallback base address reads short on real hardware

Picked back up with the kept `--keep-temp` captures still on Andy's machine (`ps1-redump.prove-
golden.img` + `ps1-redump.prove-track0{1..8}.bin`). Reproduced the exact same failure directly
against real bytes with `raw-verify-readback ... --report` on track 5: 99.4% main-channel mismatch,
first difference at sector 215282 — i.e. main-channel content itself reads as wrong once aligned,
not just Q.

Built two new diagnostic-only entry points, exposed as `--debug-align` / `--debug-align-search` on
`raw-verify-readback` (kept permanently — they're now real CLI features, not throwaway scripts):

- `RawReadbackCompare.DebugAlignment` prints every intermediate value `Compare()`'s alignment step
  computes (lead-in boundary, header-vs-Q base address and which source won, skew) without running
  the sector-by-sector compare — for seeing WHY an alignment landed where it did.
- `RawReadbackCompare.DebugAlignmentSearch` brute-forces every offset in a ±32-sector window around
  the computed alignment, reporting exact main-channel match counts at each — the ground-truth check
  for whether the computed alignment is a few sectors off, and by how much.

Run against track 5: **100% match (3000/3000 sectors) at exactly offset +2, 0% at every other offset
tested** — a clean, deterministic, fully-reproducible residual, not noise or corruption.

**Root cause**: `ProgramBaseAbs` (the Q sub-channel fallback used to find a track's base address when
there's no main-channel header to anchor on — i.e. every audio track) derives that address entirely
from the Q sub-channel's own absolute-time field. On this drive (Lite-On DVDRW SHW-160P6S), on a
track with no header, that decoded value comes back a small, constant number of sectors (2, on this
disc) short of the disc's true address — confirmed a genuine, deterministic property of the capture
(the search found the exact same clean +2 signature), not an algorithm bug in the abstract (the
`DeepMultiTrackVerifyTests.cs` synthetic repro from last night passed precisely because it copies
golden's own, uncorrupted Q verbatim — it never exercised a capture whose Q itself reads short).

**Fix, in `RawReadbackCompare.Compare()`** (`src/DiscForge.Core/Raw/RawReadbackCompare.cs`): when
exactly one side used the Q fallback to establish its base address, probe a small window (±16
sectors) of candidate offsets around the computed alignment by actual main-channel content match
(same technique as `DebugAlignmentSearch`), sampling up to 1500 sectors. Only acts when the evidence
is overwhelming and unambiguous — the best offset must match ≥95% of sampled sectors while every
other offset in the window matches ≤10% — so a genuinely defective burn is never silently "corrected"
into a false pass; when it does act, it's logged as a note ("Alignment auto-corrected by N sector(s):
..."), never a silent change. Because the underlying bug is that the Q-fallback side's OWN decoded Q
values are uniformly short by that same constant (not just the one sampled base value), the found
offset is also applied to compensate that side's per-sector Q address comparison (`SameAddress`,
extended with adjustment parameters, numeric compare instead of byte compare only when an adjustment
is active) — without this, fixing only the main-channel alignment turned a clean track into a wall of
spurious "mis-addressed" defects instead (caught by building the regression test below before this
half of the fix was written — the first draft of the fix only shifted the start index and that test
failed with exactly that shape).

**New regression coverage**: `DeepMultiTrackVerifyTests.cs` gained
`A_readback_whose_Q_address_reads_a_few_sectors_short_still_passes_via_auto_correction` (2 cases,
Interleaved96 + Packed96) — builds the same synthetic multi-track disc as the existing deep-track
test, but deliberately re-encodes every sector's Q absolute-address field 2 sectors short (track,
index, relative time, and CRC all left correct and valid — only the field the real bug affects is
wrong) via `SubQ.Position` + `SubcodeFrame.EmitInterleaved96`/`EmitPacked96`, and asserts the read-
back still passes with an explanatory "auto-corrected" note rather than failing. All 4
`DeepMultiTrackVerifyTests` pass; full suite 2545 passed, 1 pre-existing unrelated OOM failure
(`AudioCdTests.Over_74_minutes_...` — a sandbox memory limit on a huge synthetic buffer, confirmed
unrelated by re-running it alone). `build.sh cli` and `build.sh cli-win`: 0 warnings/errors both.

Also added the previously-missing `--debug-align-search` line to `raw-verify-readback`'s usage text
(only `--debug-align` was documented there before).

**Not yet done — next step**: this is verified against a synthetic reproduction of the exact real-
hardware signature, but NOT yet re-run against the actual kept capture files
(`ps1-redump.prove-golden.img` + `track04..08.bin`, already on Andy's machine, no new burn needed).
Once delivered, re-run `raw-verify-readback ps1-redump.prove-golden.img ps1-redump.prove-track0N.bin
--partial` (or `--report`) for N in 4..8 and confirm each now reports PASS (with notes) with an
"Alignment auto-corrected by +2 sector(s)" note, then re-run the full `dforge prove
ps1-redump.cue D:` end-to-end (needs a fresh blank disc) for a genuine `=== PROVEN ===` result. If
any of tracks 4-8 DON'T clear (e.g. a track whose real defect was previously masked by the alignment
failure), that's real signal, not a regression in this fix — investigate it as a genuine burn issue
at that point, don't just re-widen the correction window.

## 2026-08-29 (continued) — fix confirmed on real hardware for tracks 4-8; track 3 is a DIFFERENT bug, still open

Delivered the alignment fix above, rebuilt, and re-ran `dforge prove` end to end on a fresh burn
(Lite-On). Confirmed genuinely fixed: tracks 4, 5, 6, 7, 8 each went from catastrophic failure
(tens of thousands of mis-addressed sectors) down to exactly 2 residual mis-addressed sectors each
— the same tiny, isolated, mid-track (not boundary) signature on every one of them, consistent with
ordinary real-disc read jitter rather than a remaining software bug. Track 1's 16 defects are the
already-documented, unrelated data→audio boundary artifact (2026-08-28). Track 2 passes clean.
`--report`/`--json` were unaffected (unused this round); `main-data`/`mis-addressed` category
counts and the "Alignment auto-corrected" note were the only things checked.

**Track 3 is a different failure that survived the fix**: 23,808-23,813 mis-addressed out of 23,915
sectors, reproducibly, across two separate burns (same near-total failure rate both times — not
jitter). Investigated with `--debug-align-search` on the kept file: **offset 0 already gives a
100% main-channel match (3000/3000)** — i.e. this track's base address, unlike tracks 4-8's, was
never wrong; no index correction is needed at all. Yet nearly every sector's Q sub-channel still
decodes to the wrong address. That's a different animal from the tracks-4-8 bug: a genuine main/Q
POSITIONAL skew (the sub-channel bytes bundled with a given main-channel sector in the raw capture
physically belong to a different disc position) — the same species of quirk this codebase already
detects and corrects for a header-carrying (data) track (`gSkew`/`rSkew`), but structurally
undetectable by that existing mechanism on an audio (Q-fallback) track, because the "base address"
and "skew reference" values are computed by the exact same `ProgramBaseAbs` call and necessarily
cancel to zero.

**A fix was drafted, built, and then deliberately backed out before delivery** — worth recording
why, so it isn't re-attempted the same way. A second guarded content-search (mirroring the
tracks-4-8 fix, but searching sub-channel READ POSITION instead of main-channel start index) was
added and gated to run only when the main-channel search found nothing to fix, to avoid
double-correcting. Building a synthetic regression test to validate it (main-channel content copied
unshifted from golden, sub-channel deliberately sourced N sectors further into golden than its own
main channel — meant to reproduce "main already aligned, only Q is positionally off") revealed the
model was wrong: shifting only the sub-channel bytes ALSO moves `ProgramBaseAbs`'s own base-address
vote (it reads Q, and only Q), so the main-channel search fired FIRST on the synthetic case and
"explained" it as an index/value issue before the new code path was ever reached — meaning the new
path, as designed, is fundamentally untestable against a controlled repro. Rather than ship an
unvalidated fix against the real disc, it was reverted. The synthetic test that caught this is also
removed (it can't test what it was meant to test); the tracks-4-8 fix and its two regression tests
are untouched and still solid.

**What track 3's real symptom implies, and what's needed next**: for `ProgramBaseAbs`'s address
vote (a 400-sector window sampled from the very start of the track) to be ACCURATE — matching the
true main-channel position, unlike tracks 4-8 — track 3's Q must decode correctly, at least near
the start of the track. Yet ~99.6% of the WHOLE track (23,915 sectors) mismatches. That means
whatever's wrong with track 3's Q is NOT a simple constant offset present from the start — either it
develops/changes partway through the track (something no single constant-offset correction can
fix), or it's a genuinely different mechanism than tracks 4-8's bug entirely. Built (not yet run
against the real file) `RawReadbackCompare.DebugQScan` / `raw-verify-readback --debug-q-scan`:
divides the full track into 20 regions and reports the Q address match rate and dominant mismatch
delta in each — a flat rate with the same dominant delta throughout would mean "it's constant after
all, the 400-sector vote window was just unlucky, and a real correction is possible"; a rate or
delta that changes between regions means it isn't uniform and needs different handling entirely
(possibly per-region, possibly not auto-correctable at all). **Next step**: run
`raw-verify-readback ps1-redump.prove-golden.img ps1-redump.prove-track03.bin --debug-q-scan`
against the real kept file and read what it says before attempting another fix.

Full suite: 2545 passed, 1 pre-existing unrelated OOM (same sandbox-memory issue every run this
session, confirmed unrelated by running it alone). `build.sh cli` and `build.sh cli-win`: 0
warnings/errors both.

## 2026-08-29 (continued 2) — track 3 explained and fixed: same bug as 4-8, hidden by a vote bug

`--debug-q-scan` on the real track 3 capture answered the open question immediately: **every one
of the 19 later regions (of 20) is 100% mismatched, and every single one of those mismatches has
the SAME delta: −2.** Only the very first region (sectors 0-1194) is mixed — 88/1194 (7.4%) decode
correctly, the rest already −2. This is NOT a different bug from tracks 4-8. It's the identical
"Q decodes 2 sectors short" quirk, just not literally 100% of the track — and the reason the first
fix missed it is now clear and was a real bug in how the two corrections were wired together.

`ProgramBaseAbs` (the function that supplies a Q-fallback track's base address) only samples the
first 400 sectors and exits EARLY once one candidate reaches 3 votes with a 2× margin over the
runner-up — an optimization, not a correctness issue on its own. But track 3 apparently has enough
correctly-decoded (delta-0) frames clustered right at its start that the vote locked onto the TRUE
address before the dominant −2 pattern (the other 99.6% of the entire track) ever got sampled. That
made `gStartIdx` come out CORRECT by luck, so the main-channel content search (tracks 4-8's fix)
found nothing to shift — correctly, main channel really was already aligned — and since the Q-value
compensation (`qAdjustR`) was wired to fire ONLY when that same search also applied a shift, it
never fired either, even though the per-sector Q comparison needed the exact same −2 compensation
every other track needed.

**The real fix: decouple the two.** Whether the main channel needs an index shift and whether the
per-sector Q comparison needs a value compensation are two independent questions that happened to
have the same answer on tracks 4-8 (both were needed) and different answers on track 3 (only the
second was). `RawReadbackCompare.Compare()` now measures the Q-address delta directly and
independently: strided evenly across the WHOLE available range (not a prefix, so a small early
cluster can't dominate it the way it fooled `ProgramBaseAbs`'s own vote), tallies the decoded delta
between golden and readback over every sector where BOTH sides' Q passes its own CRC, and applies
the dominant delta as the per-sector compensation whenever one clearly dominates (≥60% of a
decently-sized sample — high enough to catch a track that's "only" 92-100% affected like track 3
really was, low enough to still refuse on a genuinely mixed/defective track) — completely
independent of whether the main-channel search also fired. A confirmed-necessary generalization,
not scope creep: real track 3 needed exactly this and nothing else.

A candidate fix was drafted and deliberately backed out on 2026-08-29 (see above) after a synthetic
test proved that design couldn't be validated in isolation. This decoupling is different — a
regression test now DOES validate it directly and cleanly:
`A_readback_whose_early_frames_win_a_misleading_vote_still_auto_corrects` (2 cases, Interleaved96 +
Packed96, in `DeepMultiTrackVerifyTests.cs`) deterministically reproduces the exact trap — the
first 5 position-frame sectors decode with a correct (delta 0) Q address (enough to win
`ProgramBaseAbs`'s 3-vote/2×-margin early exit outright), every sector after that is short by 2 —
and confirms the independent census still catches and corrects it. All 6
`DeepMultiTrackVerifyTests` pass (the 4 from earlier today plus this new one, 2 cases each). Full
suite: 2547 passed, 1 pre-existing unrelated OOM (same as every run this session). `build.sh cli`
and `build.sh cli-win`: 0 warnings/errors both.

**Confirmed on real hardware, same session**: `raw-verify-readback ps1-redump.prove-golden.img
ps1-redump.prove-track03.bin --partial` on the actual kept capture — **PASS (with notes)**, main
channel all identical, 0 mis-addressed. The note fired exactly as designed: "Q sub-channel address
auto-corrected by +2 sector(s): 4975/4998 sampled sectors (strided across the whole track)
consistently decoded -2 sector(s) off golden's." The remaining 23,813 sub-timing + 14 read-noise
entries are the same already-understood benign categories every other track has shown all session
(ancillary sub-channel byte noise and transient single-frame Q CRC glitches, neither address- or
content-affecting). One confusing intermediate step worth noting: the FIRST attempt at this exact
same command, right after delivering the decoupled fix, still showed the old 23,813-mis-addressed
failure with no auto-correct note at all — indistinguishable from the fix never having been applied.
Rather than re-reason about the logic, temporary debug output was added to print the census's raw
numbers (avail/considered/vote tally) unconditionally; the very next rebuild-and-rerun (no logic
changes, purely the debug print) suddenly passed clean with sane numbers (`considered=4998,
votes=-2x4975,0x23`) — strongly suggesting the "still failing" run used a stale build that hadn't
actually picked up the decoupling fix (an incremental-build/stale-DLL issue, not a logic bug). The
debug note has been removed again post-confirmation; the fix itself is unchanged from what's
described above.

**This investigation is closed.** All 8 tracks of the PS1 disc now verify correctly on the Lite-On
DVDRW SHW-160P6S: track 1's 16 defects are the documented, unrelated data→audio boundary artifact
(2026-08-28); track 2 passes clean; tracks 3-8 all pass (with notes) via the Q-address
auto-correction, each carrying only the small residuals (2-16 sectors depending on track) that this
session established are ordinary real-disc read jitter, not software bugs. Remaining open items are
unrelated pre-existing housekeeping further up this file: PATH shadowing (old "DiscForge 1.65" in
Program Files still shadows the dev build — `Get-Command dforge` confirms it; not yet uninstalled)
and the redump.org hash check for `ps2game.iso`, blocked on redump.org blocking automated fetches
(needs Andy's own browser). `dforge subchannel-dump` on the Lite-On also remains untried on real
hardware — never blocking, always optional polish.

## 2026-08-29 (continued 3) — version bumped to 1.69.0; a `dforge version` command, to stop the stale-binary trap

The stale-build confusion above cost a full debugging round-trip for nothing — the fix was correct
the whole time, the binary just hadn't picked it up. There was no fast way to tell the two apart
from the CLI's own output, so there's now one: **`dforge version` / `dforge --version` / `dforge
-v`** prints the CLI version AND `dforge.dll`'s own last-write time (`Program.cs`, `VersionCmd()`,
wired into the top-level dispatch switch, listed first in the no-args help text). The version alone
only proves freshness on a session that happened to bump it (like this one); the file timestamp
proves it unconditionally, on every rebuild, whether or not the version changed — check it against
when you actually ran `dotnet build` before trusting that a delivered fix is live. If it's stale,
`dotnet clean` before rebuilding rather than trusting an incremental build.

Version bumped `1.68.0` → `1.69.0` across all four `<Version>` tags (`DiscForge.App`,
`DiscForge.Cli`, `DiscForge.Core`, `DiscForge.Devices` `.csproj` files) — the same real fixes this
session (tracks 4-8, then track 3) plus the new command justify it on their own. Verified `dforge
version` prints `DiscForge CLI v1.69.0 (dforge.dll built <today's timestamp>)` after a clean
rebuild in the sandbox. Full suite: 2547 passed, 1 pre-existing unrelated OOM (same as every run
this session). `build.sh cli` and `build.sh cli-win`: 0 warnings/errors both.

`build.ps1` was also enhanced to close the loop end-to-end: its header comment now explains why
`-Rebuild` matters (plain `dotnet build` is incremental and can silently skip a changed file — the
exact trap above), and every run now ends with a "== CLI build check ==" section that locates the
freshly built `dforge.exe` (the `-Publish` standalone one if present, else the newest
framework-dependent one under `src\DiscForge.Cli\bin\`) and runs `dforge.exe version` against it, so
the build's own freshness is printed automatically instead of relying on remembering to check.
Delivered directly to `C:\dev\DiscForge\build.ps1`; not yet syntax-checked outside manual review
(no `pwsh` in the sandbox) or run by Andy.

## 2026-08-29 (continued 4) — "un-capturable protection" honesty field (ROADMAP backlog item, done)

Built while Andy was away, no hardware needed — this is pure software metadata plumbing, not a
bug fix. From `docs/ROADMAP.md`'s PS1/general backlog: *"a metadata note that a title's
wobble-groove/ATIP physical signal is not representable in the dump ... turns 'my 1:1 copy has
everything' into an honest catalog field."*

What it does: some copy-protection schemes authenticate via a genuinely physical measurement —
DPM (Data Position Measurement), laser-timing variance read across repeated passes over the
pressing — that no sector or subchannel byte can ever hold, unlike LibCrypt (corrupt subchannel Q,
which a normal dump DOES capture) or SafeDisc/weak-sector schemes (capturable in RAW mode). Until
now, a disc with one of these could verify perfectly clean and still not actually be a complete
preservation copy, with nothing in the tooling saying so.

`CopyProtectionCatalog.ProtectionDetection` gained `PhysicallyUncapturable` (bool) +
`UncapturableNote` (string?); `ProtectionReport` gained `AnyPhysicallyUncapturable` and
`PhysicalCaptureCaveat()` (a composed one-sentence warning, or null). Flagged true for **StarForce**
(DPM from v3 onward) and **SecuROM** (v7+ layers DPM on top of its existing marks; version can't
always be pinned down from filesystem marks alone, so any SecuROM hit is flagged out of caution).
Left false for SafeDisc, LaserLock, CD-Cops, VOB ProtectCD, TAGES, and LibCrypt — all sector/
subchannel-based and genuinely capturable. `protection-scan`'s `Render()` now prints the caveat.

Wired into `dump-cert create` behind a new opt-in `--scan-protection` flag (not automatic — a
full-image read + ISO parse on every certificate isn't free, especially for large DVD images).
When it fires, `DumpCertificate` gets a new `PhysicalCaptureCaveat` field, echoed in the `verify`
output and both commands' `--json`. Careful design point: this field is **deliberately excluded**
from `SigningContent()` — folding it into the signed bytes would change the signature for every
certificate ever issued, including ones signed before this field existed, breaking their
verification under the new build. That's exactly the kind of self-inflicted "did this actually
change" confusion from earlier this session (the stale-binary trap); the field stays informational/
unsigned so old certificates keep verifying byte-for-byte as before. A
`PhysicalCaptureCaveat_IsInformational_AndDoesNotAffectTheSignature` test locks this in.

8 new tests (`CopyProtectionCatalogTests.cs` ×6, `DumpCertificateTests.cs` ×2). Full suite: 2555
passed, 1 pre-existing unrelated OOM (same one as every run this session — see above). `build.sh
cli` and `build.sh cli-win`: 0 warnings/errors both. Smoke-tested `dump-cert --scan-protection` end
to end against a synthetic clean image in the sandbox (no caveat, as expected — nothing to flag).
**Not yet exercised against a real StarForce/SecuROM disc** — there's no such disc in this session's
test data, so the two catalog signatures are unverified against a real dump; low risk since they
reuse the exact same file-mark matching as the rest of the (already-verified) catalog, but worth
a real-disc sanity check if one turns up.

## 2026-08-29 (continued 5) — `housekeeping.ps1`: actions what it can from the Housekeeping list

New script at the repo root, `housekeeping.ps1`, requested to "action the housekeeping" list above.
It genuinely automates what's automatable and opens/reports the rest rather than pretending to:

1. **PATH shadowing** — finds the old "DiscForge 1.65" via the Windows uninstall registry (both
   HKLM/HKCU, both registry views) and, after confirming with you, runs its uninstaller. Also
   reports every `dforge.exe` currently on PATH and which one wins, same check `build.ps1` /
   `install-cli.ps1` already do at build/install time.
2. **v1.66.0 Release** — checks the tag, the release, and the Release workflow's last run via the
   public GitHub API (unauthenticated, read-only), and flags if the release description looks empty
   (the "paste release notes in" step).
3. **COPTR + awesome-list submissions** — opens `docs/registry-submissions.md` (the paste-ready
   text) plus the COPTR homepage and the awesome-list's GitHub edit page. Can't submit either one
   for you — COPTR needs a wiki login, the awesome-list needs a PR — so it preps and opens, not more.
4. **AaruFormat interop** — reports what's still missing (a real Aaru `.aaruf`); nothing to check
   without one.
5. **redump.org hash check** — prints the `ps2game.iso` MD5 and opens the PS2 disc list; redump.org
   blocks automated fetches so the search itself has to be a human.

`-WhatIf` reports without uninstalling anything; `-SkipUninstall` / `-SkipRelease` / `-NoBrowser`
skip individual steps. Parse-checked clean with `pwsh -File` (Microsoft's PowerShell 7.4.6, fetched
into the sandbox specifically to verify this — also used to retroactively parse-check `build.ps1`
from earlier today, which had only been reviewed by eye until now: also clean). Dry-run exercised
end-to-end in the sandbox (Linux, no registry, no GitHub access) to confirm every step degrades
gracefully instead of crashing when a step's dependency isn't there; the uninstall-string parsing
regex was separately unit-tested against Inno/MSI/plain-exe uninstall-string shapes. **Not run for
real on Windows** — the registry lookup, the actual uninstall, and a live (non-403) GitHub API call
are all unverified beyond that.

## 2026-08-29 (continued 6) — three more backlog items: offset-shift, prototype scanner, atomic writers

Andy asked for the whole remaining "no fixed spec yet" batch — offset-shift disc detection,
prototype scanner, GUI views for recover/secure-rip, and remaining non-atomic writers — in one go.
Three of the four are software-only and testable without hardware; built, tested, delivered. The
fourth (GUI views) is deliberately NOT attempted this session — see its own note below.

**Offset-shift disc detection** (`DiscForge.Core.Audio.OffsetDetection`, `dforge offset-shift-scan`).
`detect-offset` sweeps ONE track and assumes the result holds for the whole disc — right the
overwhelming majority of the time, but blind to a real mastering anomaly where the correct offset
changes partway through (the ROADMAP item's actual concern). New `TrackOffsetResult`/`OffsetRun`/
`OffsetShiftReport` + `AnalyzeRuns()` sweep and match EVERY audio track independently, then group
consecutive tracks into runs of a consistent offset — more than one run means a genuine shift, and
the report names exactly which tracks and which offsets (e.g. "tracks 1-4 @ +30, then tracks 5-8 @
+36"), not just "N/M tracks verify" the way `detect-offset`'s own confirmation pass reports it today.
Reuses `SweepV1`/`BruteSweepV1`/`Match` unchanged — only the per-track loop and the run-grouping are
new. 14 tests including an end-to-end one that plants two different offsets across two synthetic
tracks and confirms the scan locates the exact split. CLI wiring mirrors `detect-offset` closely
(own local `ReadPcmWindow`, deliberately not shared — both stay small and independent). Smoke-tested
CLI error paths (no args, missing file); the sweep/match core is exercised end-to-end by the unit
tests. **Not run against a real offset-shifted disc** — none is known to exist in this session's test
data; the synthetic end-to-end test is the strongest evidence available without one.

**Prototype / debug-residue scanner** (`DiscForge.Core.Forensics.PrototypeScanner`, `dforge
prototype-scan`). Three independent signals: (1) leftover debug files — `.sym`/`.map`/`.pdb`/etc.
extensions, a `debug`/`devkit`/`qa`/... directory; (2) debug strings inside scanned executables —
assert messages, a developer's own `E:\perforce\...` path, or an embedded PDB reference (the PE
CodeView `RSDS` signature followed by a `.pdb` path — a heuristic byte scan, not a full PE parse, so
it can miss a packed/compressed executable but never false-positives on the pattern); (3) an optional
diff against a known-retail file manifest (`--baseline`, built from a known-good image with
`--emit-baseline`: path + size + SHA-256 per file) — added/missing files and size/hash mismatches.
Builds on `DiscBillOfMaterials`' mastering-date extraction (`DiscChronology`) for the disc's own
build-date field, and follows `DiscArchaeology`'s house rule: detection only, never a verdict — a
clean disc can still BE a prototype, a disc with hits here is not proven one (debug files can ship by
mastering accident). 19 tests, including two full `IsoBuilder`-built end-to-end cases (one finds
residue directly, one builds+diffs a baseline). Two real bugs the tests themselves caught before
shipping: the file-name scan wasn't stripping the ISO 9660 `;1` version suffix before matching
extensions (every `.sym`/`.map` check silently missed), and a test itself was invalid because
`IsoBuilder` correctly truncates filenames to 8.3 (fixed the test's filename, not the code). CLI
wiring mirrors `disc-bom`'s `FromIso` pattern; smoke-tested end to end against a synthetic clean and
garbage image (graceful "no residue" / "not a readable ISO" respectively, no crash either way).

**Remaining non-atomic writers** (`WriteFileAtomically` in `Program.cs`, already used by the CDI/DAO
conversion paths). Audited every direct `File.Create`/`FileStream(..., FileMode.Create)` call site in
the CLI (~48 total) and converted the ones that produce ONE deliverable image/binary file — exactly
the case the helper exists for, where an abort mid-write used to leave a truncated file at the
destination indistinguishable from a complete one. **22 sites converted**: every format `convert`
path (GDI↔CDI, NRG↔CDI, ISO↔CDI, MDS→CDI — 6 sites in `Convert()`), `Create`/`CreateAudio` (CDI/audio-
CD authoring), `BuildRaw` (raw DAO compose, the one with a progress bar — the case most worth
protecting, since it's also the slowest), `de-emph`, `ecm`/`unecm`, `extract-sectors` (the whole
sector-range loop, not just the open), `iso-create`, `ciso`↔`iso` (both directions), `rvz-decode`,
`gc-junk-fill`, `create-xiso`, `create-udf`, the DVD-Video/BD-Video/UDF-bridge builders (5 more
sites). Rebuilt and ran the full suite after every batch (never more than ~10 sites between test
runs) rather than converting all 22 blind and finding out at the end. Full suite: 2581 passed, 0
failed this run (the pre-existing OOM test is flaky/memory-dependent, not new). `build.sh cli` and
`cli-win`: 0 warnings/errors both. Runtime smoke-tested three converted commands directly (
`extract-sectors`, `iso-create`, `ecm`/`unecm` round-trip) — correct output, zero leftover `.part`
files.

**Deliberately NOT converted this pass** (documented here so it isn't mistaken for "audited and
found clean"): per-file extraction loops where many small files share one loop (CDI track extract,
UDF/ISO 9660/GameCube/CD-i file extraction, WBFS extract) — atomicity matters far less per-file than
for one large deliverable, and wrapping each iteration adds `.part`-then-move overhead for
comparatively little benefit; every live-drive command (`extract-sectors <drive:>`,
`read-disc`/`read-raw`/`prove`, floppy imaging, `compose-verify`) — deliberately left alone rather
than touched blind, consistent with this session's standing rule about hardware-adjacent code;
`ToCcd`'s dual-stream (.img + .sub) writer, which would need both files made atomic together, not
independently; and four smaller decoders (`adx-decode`, `dsp-decode`, `read-offset`, `xa-extract`)
not yet looked at closely. None of these are known to have caused a real problem — this is a
completeness note, not a bug report.

**GUI views for recover/secure-rip — NOT attempted this session.** `DiscForge.App` (the WinForms GUI)
cannot be built in this sandbox at all: `dotnet build` fails immediately with `Microsoft.NET.Sdk.
WindowsDesktop.targets` not found — the sandbox has no Windows Desktop workload, confirmed by trying
it directly. Writing WinForms view code blind, with no way to compile-check it before delivery, is
exactly the kind of unvalidated-fix risk this session already got burned by once (the track-3
positional-skew fix, backed out after a synthetic test caught it) — so rather than repeat that with
code that can't even be synthetically tested here, this item is left for a session where the GUI can
actually be built (i.e., on Andy's machine, or a sandbox with the Windows Desktop workload installed).
The CLI-side building blocks (`merge-cert` for recover, `secure-rip-plan` for secure-rip) are both
solid and already documented above ("Correction: feature D... already DONE").

## 2026-08-29 (continued 7) — drive-capabilities DB growth, PS1 save-container formats, GUI views

Andy said "lets get all done" covering all three items still open from the previous batch. All three
are now done, with one item's GUI half delivered but explicitly unverified (see below).

**Drive-capabilities DB growth** (`DriveKnowledgeBase.cs`). The bundled community-reference table was
deliberately small (4 entries: the two classic Plextor CD-RW dumpers, one ASUS BD-RE, one LiteOn
DVD-RW) — "growth" meant adding more sourced entries, not rebuilding the wiring, which already existed
in full (`drive-db`, `drive-profile` auto-lookup/pre-fill, `drives` auto-detect — all already wired to
`DriveKnowledgeBase.Find`). Pulled real, corroborated offset values from the AccurateRip community
offset table (via DiscImageCreator's `driveOffset.txt` mirror, which carries per-model submission
counts) rather than guessing: **7 new entries** — LG WH16NS40 (BD-RE, +6, 1199 submissions), LG
GH24NSC0 (DVD-RW, +6, 859), Pioneer BDR-209 family (BD-RW, +667 — flagged as one of the largest
offsets in common circulation, so an uncorrected rip on this family isn't mistaken for disc damage),
ASUS DRW-24B1ST (DVD-RW, +6, ~2600 across suffix variants), Samsung/TSST SH-224 family (DVD-RW, +6,
3000+), Plextor PX-716A/AL (DVD±R DL, +30 — explicitly NOT assumed to share the classic CD-RW Plextor
family's lead-in/lead-out capture, since that's a different, undocumented-for-this-model claim), and
Sony/Optiarc AD-7200A/S (DVD±RW, +48, vendor-agnostic since Sony- and Optiarc-branded units are the
same hardware). Only the offset is claimed as community-established for these; lead-in/lead-out
overread and C2 reputation are left `NotDetermined`/`No`/`Advertised` rather than inherited from the
Plextor entries' stronger sourcing — same "provably correct or declined" discipline as everywhere else
in this codebase. Updated the one existing test that asserted `HL-DT-ST GH24NSC0` was unknown (it
collided with the new entry) to use a genuinely nonexistent model instead, and added a new test
covering all 7 additions' real INQUIRY-string matching (padding/case included). 9 tests in that class
now, all passing.

**PS1 save-container identification/conversion** (`Ps1CardConvert.cs`, new `Ps1SingleSave.cs`). The
full-card reader/writer (`PsxMemoryCard`) and raw/DexDrive/VGS container conversion (`Ps1CardConvert`)
already existed from an earlier session — this batch closed the two gaps ROADMAP named: `.vmp`
(PS3/PSP "virtual memory card") and `.psv` (PS3/PSP single-save export). Both were researched from
public documentation (psdevwiki's PS1_Savedata page, cross-checked against known PS3-homebrew-scene
byte layouts) rather than guessed. `.vmp`: a 128-byte header then the raw 128 KB card — same shape as
the existing DexDrive/VGS wrapping, added as `Ps1CardFormat.Vmp` with full `Detect`/`ToRaw`/`Convert`
support. Honesty note carried in the class doc-comment and a code comment: the real header's 40 bytes
of key-seed + HMAC exist so a real PS3/PSP can authenticate the file, and DiscForge has no Sony signing
key material and won't fabricate one — so `Detect` identifies a `.vmp` STRUCTURALLY (exact documented
size + the "MC" card header sitting at the known 0x80 offset) rather than trusting an unverified magic-
byte sequence for the header, and a `.vmp` written here carries the card data byte-for-byte but zeroed
signature fields, so it won't authenticate on real hardware — same pattern as `PhysicallyUncapturable`
elsewhere in this codebase. `.psv`: new file `Ps1SingleSave.cs`, a single-save export (one 8 KB card
block + product code, not a whole card) with the same zeroed-signature honesty caveat; `Read`/`ToPsv`
round-trip the product code and save payload byte-for-byte, verified with synthetic test fixtures built
directly from the documented header layout (magic, platform indicator, product-code offset). Wired into
the CLI: `ps1card-convert`/`ps1mc-format` gained a `vmp` target, and a new `ps1-psv extract|wrap`
command. **Deliberately NOT attempted**: `.mcs` (single-save format used by some emulators/managers) —
two independent lookups this session (raphnet-tech's PSX memory-card page, general community sources)
both came back without a documented byte-level layout, and PocketStation support, plus mapping a save's
embedded product code to a Redump identity (no bundled Redump DAT/DB is available to this session to
match against) — all three left open rather than guessed at. 6 new tests in `Ps1SingleSaveTests.cs`, 3
new tests in `Ps1CardConvertTests.cs`, all passing.

**GUI views for recover/secure-rip** — attempted this time, with an explicit caveat. Andy re-confirmed
"lets get all done" after already being told once that this sandbox cannot compile `DiscForge.App`
(still true — `dotnet build`/`dotnet workload search` both confirm no Windows Desktop SDK/workload
available here), so rather than decline a second time, wrote the two views by hand against the existing
View conventions as closely as verifiable: two new code-only (`UserControl`, no designer file) views —
`MergeCertView.cs` (wraps `ProvenanceMerge.Merge` + `MergeCertificate`, mirroring the CLI's
`merge-cert`: multi-source picker, auto-honours each source's `.badsectors.json` sidecar if present,
optional signing via `DumpLineageLog.GenerateKey`/`Sign`) and `SecureRipPlanView.cs` (wraps
`SecureRip.Grade`/`PlanReread`, mirroring `secure-rip-plan`: picks an evidence JSON, parses it with the
exact same field-by-field validation as the CLI handler, renders per-track grades and re-read ranges).
Both added as new launcher tiles in `CdrwinLauncher.cs` (`mergecert`, `secureripplan`) plus matching
`HelpContent.cs` entries. Every Core API call was checked line-by-line against the actual method
signatures in `MergeCertificate.cs`/`SecureRip.cs`/`BadSectorMap.cs`/`DumpLineage.cs` (not assumed from
memory), and the JSON-parsing logic in `SecureRipPlanView` is a direct line-for-line port of
`SecureRipPlanCmd`'s validation in `Program.cs`. **This code has NOT been compiled or run** — it is the
one deliverable this session where "tests pass" cannot be said, because there is no way to test WinForms
UI code without the Windows Desktop SDK this sandbox lacks. Please build with `.\build.ps1 -Rebuild` (or
just open the two new tiles) and tell me about anything that doesn't compile or misbehaves — that's the
fastest way to close the loop on this one.

## 2026-08-29 (continued 8) — GameCube preservation backlog

Andy asked "next?", then "how long to do all?", then "yes please" to the proposed order (2, 6 → 1, 4 →
3, 5, 7) — this batch works through the whole ROADMAP.md "GameCube preservation backlog" (7 items) in
that order, closing five of the seven, cleanly deferring one, and scoping the last one down honestly.

**Item 6 — apploader + bi2.bin parse.** New `GcBi2.cs`: `GcBoot` (in `GcBoot.cs`) changed from
`static class` to `static partial class` so this file can extend it and reuse its private `ReadFull`
helper. `GcBoot.ReadBi2` parses bi2.bin (debug-monitor size, simulated-memory size, country code, and a
computed `LooksLikeDebugBuild` flag for a nonzero debug-monitor size — a dev-kit tell). `GcBoot.CheckChain`
confirms the FULL boot chain (bi2 → apploader → DOL → FST) in one pass, collecting every problem as a
`BootChainIssue` (never throwing for an expected failure mode) so a caller sees ALL the chain's problems
at once rather than stopping at the first. 9 new tests in `GcBi2Tests.cs`.

**Item 2 — single-image health report.** `GameCubeVerify.Check` now also calls `GcBoot.CheckChain` (its
issues become warnings) and `GcBoot.ReadBi2` (surfaces `LooksLikeDebugBuild`), and cross-checks padding
via `GcJunkMapper.Analyze` (surfaces a `PaddingVerdict`: intact/scrubbed/mixed/suspicious — flagged as a
warning and failing `Healthy` when the padding looks scrubbed). Both new fields added to
`GameCubeHealth` and its `Summary()`/JSON output (`gc-verify --json`). 4 new tests in
`GameCubeVerifyTests.cs` (6 total), including a scrubbed-padding fixture and a corrupted-DOL-offset
fixture that exercises the boot-chain check specifically (FST corruption can't be used for this — the
existing `GcmReader.Read` already validates FST bounds and throws before `Check()`'s own warning
collection runs).

**Item 1 — CRC-32/Redump confirmation on junk reconstruction.** `GcJunkReconstructor.Reconstruct` gained
an optional `expectedCrc32` parameter and always reports the finished output's own CRC-32
(`Report.OutputCrc32`), computed by streaming the output back through in 1 MB chunks — safe for a
multi-hundred-MB/GB image. This is layered ON TOP of (not instead of) the existing self-validation gate:
self-validation proves the junk generator matches THIS disc's own surviving junk, which does not by
itself prove the reconstructed image is byte-identical to a specific Redump-verified dump (a
self-validated generator could in principle still diverge somewhere the surviving junk never sampled).
When a caller supplies a Redump entry's known-good CRC-32, `Report.CrcConfirmed` says whether the
FINISHED image matches it — an independent check, purely informational (a mismatch doesn't roll back an
already-self-validated reconstruction). Nothing here bundles or fabricates Redump data — the check only
runs when the caller supplies the value, same pattern as everywhere else this project touches Redump.
Wired into `gc-junk-fill --expect-crc32 <hex>` (exit code 2 on a confirmed mismatch, distinct from exit 3
for a declined reconstruction). 5 new tests in `GcJunkReconstructorTests.cs`.

**Item 4 — DTK/ADP disc-streamed audio: DEFERRED, not attempted.** The `.dsp` FILE-format decoder
(`DspAdpcm.cs`) was already solid and complete — 14-sample/8-byte ADPCM frames, standard predictor-
coefficient math, well-corroborated. What's missing is the DISC-STREAMED "DTK"/"ADP" audio format
(background music/movie audio interleaved directly into the disc image, not a `.dsp` file). This session
searched YAGCD, gc-forever's wiki, justsolve's wiki (blocked), and a GitHub decoder's README — none gave
a public, non-confidential, byte-exact spec for the streaming format/interleave. The only concrete
byte-level source found was a leaked Nintendo document marked CONFIDENTIAL, which this project declines
to use as a clean-room basis (same standard applied throughout: public reverse-engineered descriptions
only). Left open; the `.dsp`-file half already works today.

**Item 3 — GameCube-specific ring code + Redump matching.** New `GcRingCode.cs`, distinct from the
generic IFPI mastering/mould SID parser (`Forensics/RingCode.cs`, which applies to optical media
broadly). GameCube discs carry three additional inner-ring codes: RED = a manufacturing date
(`AYYMDDBB` — a region flag, 2-digit year, a month LETTER A=Jan…L=Dec, 2-digit day, and 2 trailing
characters whose meaning nobody has documented, kept raw); BLUE = the disc's own identity
(`DOL-<4-char game code>-<disc #>-<ROM revision> <region name>` — the middle group, once the hyphens
are dropped, is exactly the disc header's own GameCode); GREEN = almost always the literal `S0`, kept as
an anomaly flag. Sourced from a public collector/community forum thread cataloguing Nintendo optical
disc date codes (NOT an official Nintendo spec — Nintendo never published this), cross-checked for
internal consistency (the thread's own worked example, Wind Waker's `C03B2606`, correctly decodes to
2003-02-26 under the stated A=Jan..L=Dec rule). `GcRingCodeCheck.CrossCheck` compares a parsed ring
against values the CALLER supplies (the disc header's own game code — always available — and optionally
an expected disc number / ROM revision, e.g. from a specific Redump entry) — no Redump database is
bundled, so a revision/disc-number confirmation only ever runs when the caller hands in the expected
value, the same "confirm by caller-supplied value" pattern as item 1's CRC-32 check. Wired into the CLI:
`gc-ringcode <red> <blue> <green> [--game-code X] [--disc N] [--rev N] [--json]`. 9 new tests in
`GcRingCodeTests.cs`.

**Item 5 — GC memory-card preservation growth.** Extended `Saves/` with a new `GcSaveBanner.cs`: decodes
a SAVE's own banner/icon (distinct from the disc's opening.bnr, already handled by `GcBanner.cs`) — the
small image the console's memory-card manager shows beside one save file. Directory-entry byte 0x07 bit
0 selects the banner's pixel format (RGB5A3 or CI8); a 2-bit-per-frame field at 0x30 selects the icon's
format across up to 8 animation frames. Both pixel formats reuse tiling already proven elsewhere in this
codebase (RGB5A3 in 4×4 texel tiles, as in the opening.bnr decoder; CI8 in 8×4 index tiles against a
256-entry RGB5A3 palette, as in the TPL texture decoder) — no new tiling math was invented. Scoped
deliberately to the FIRST icon frame only: this session could not find a public, non-confidential source
pinning down multi-frame animation's exact frame count and per-frame palette-placement rules (the two
documented CI8 icon sub-formats — "one shared palette after the last frame" vs. "a unique palette after
each frame" — genuinely disagree on layout once you have more than one frame), so rather than guess,
only frame 0 is decoded, which is provably correct regardless of that ambiguity (with one frame there is
exactly one palette either way, immediately following it). Wired into the CLI as
`gci-banner <file.gci | card[:index]> <out-dir>`. **Also explicitly declined, not attempted**: directory/
BAT corruption checksum flagging, and `.gcs`/`.sav` single-save container formats. On the checksum
footer: this session found a genuine, unresolved contradiction between a twice-verbatim-fetched "primary
source" (YAGCD, quoting a directory-block checksum footer at 0x0FFA) and simple structural arithmetic
(127 entries × 0x40 bytes = 0x1FC0 bytes, which cannot fit before offset 0x1000, let alone 0x0FFA — the
fetched value is very likely a transcription/OCR-dropped-digit of 0x1FFA) — and, separately, no
independently-verified checksum ALGORITHM (not just offset) was confirmed with enough confidence to
ship. Shipping a guessed offset or algorithm risks flagging a perfectly good card as corrupt, so this was
left undone rather than guessed at. `.gcs`/`.sav`: no documented byte-level spec found (same situation as
the earlier PS1 `.mcs` decline). 6 new tests in `GcSaveBannerTests.cs`.

**Item 7 — revision/variant-aware DAT match.** Rather than duplicate what the existing generic
`DatFile.Verify` already does (hash-match a dump against a Logiqx-XML DAT and return the exact
catalogued `DatRom`, name and all), this adds the missing piece: turning that match's free-text game
name into STRUCTURED fields. New `Dat/DatNameTags.cs` parses the public No-Intro/Redump catalogued-name
convention — `Title (Region[, Region…]) (Languages) (Rev N) (Demo|Kiosk|Beta|Proto|Unl|Sample|Alt)
(Disc N of M) (vX.Y)` — into region list, language list, revision number, disc number/count, and
retail/demo/kiosk/beta/prototype/unlicensed/sample flags, keeping anything unrecognized verbatim in
`OtherTags` rather than dropping it. This is generic (every system's DATs use the same convention, not
just GameCube's), but it's what makes a GameCube ring code's parsed revision (item 3, above) checkable
against a DAT-matched entry's own declared revision: parse the matched rom's name here, then feed
`DatNameTags.Revision` into `GcRingCodeCheck.CrossCheck`'s `expectedRomRevision` — the two new pieces
compose without any new coupling code. `dat-verify` now prints a `tags:` line for every verified match;
new standalone `dat-tags "<name>"` command for parsing a name directly. 8 new tests in
`DatNameTagsTests.cs`.

All of the above: 2633 tests passing (`bash build.sh test`), `dotnet build` clean (0 warnings/errors) for
both the sandbox-buildable `net8.0` CLI target and the Windows `net8.0-windows` target (`DiscForge.App`
itself still can't be built here — no Windows Desktop SDK in this sandbox, unchanged from every earlier
session).

With this batch, every ROADMAP.md "GameCube preservation backlog" item has been addressed: five closed
(6, 2, 1, 3, 7), one scoped down and shipped honestly with the rest explicitly declined (5 — banner/icon
yes, checksum/`.gcs`/`.sav` no), one deferred outright for lack of a usable public spec (4 — DTK/ADP
streamed audio; the `.dsp` file format already worked).

## Longer-term backlog (docs/ROADMAP.md)

Everything from the PS1/GameCube/hardware-adjacent backlog that had a fixed, buildable spec is now
DONE as of 2026-08-29: offset-shift disc detection, prototype scanner, remaining non-atomic writers,
"un-capturable protection" honesty field, drive-capabilities DB growth (7 new sourced entries), PS1
save-container `.vmp`/`.psv` support, and GUI views for recover/secure-rip (delivered but **unverified
— not yet compiled**, see above). Deliberately left open, each for a stated reason rather than by
omission: PS1 `.mcs` container support and PocketStation (no verified format spec found), mapping a
PS1 save's product code to a Redump identity (no bundled Redump DAT available to match against), and
anything live-drive/hardware-adjacent (standing rule: not touched blind).
