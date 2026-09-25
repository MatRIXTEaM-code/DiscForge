# DiscForge — the three differentiator initiatives

*What would make DiscForge not merely excellent but genuinely distinctive in disc preservation.
Drawn from a survey of the field (redumper, DiscImageCreator, MPF, IsoBuster, and Aaru — the most
ambitious modern suite) and DiscForge's own 191-command surface. Each initiative below has a concrete
design, a validation approach, and an explicit clean-room boundary. Ordered by feasibility.*

The field, in one paragraph: today's tools read **decoded** sectors from the drive's own chip and
wrap them in an image plus, at best, a metadata sidecar (Aaru's JSON) and a dump-diff. DiscForge
already matches that and adds a forensic/provenance layer nothing else has (`disc-genealogy`,
`consensus`, `disc-genome`, `disc-semdiff`, rot kinetics, completeness proofs). The three initiatives
turn that lead into a moat.

---

## 1. The Unified Preservation Master (DPM) — *achievable now*

### The gap
Aaru's headline is "AaruFormat": one open container holding all media data + comprehensive metadata +
full audit info + checksums. DiscForge has every ingredient — `preserve pack/verify`, `vault`
(Reed-Solomon self-heal), `lineage` (signed chain-of-custody), `chunk-manifest` (FastCDC + Merkle),
`completeness-check`, `disc-genome`, `submission-info` — but scattered across separate commands. No
single artifact says "this is the authoritative, provably-complete, self-healing, fully-audited master
of this disc."

### The design
A `preserve-master build|verify|open` command producing a **DPM** — a single self-describing bundle:

- **Payload:** the disc image(s) exactly as dumped (bin/cue, iso, gdi, …), untouched.
- **Sidecar (`.dpm.json`, open):** a *superset* of Aaru's schema so it interoperates, adding
  DiscForge's forensic fields. One document carrying:
  - identity (platform, serial, region, `disc-genome` offset-invariant fingerprint),
  - geometry (tracks, sessions, layer break, subchannel presence),
  - fixity (per-file CRC32/MD5/SHA-1/SHA-256, and the `chunk-manifest` Merkle root),
  - completeness certificate (`completeness-check` result — what is and isn't representable),
  - provenance (`lineage` signed chain + `dump-provenance` tool inference),
  - integrity ecology (`vault` Reed-Solomon parity descriptor for self-heal; rot-kinetics baseline),
  - protection profile (initiative 2, embedded by reference).
- **Audit block:** an append-only, signed log of every operation the master has undergone.

### Validation
Round-trip: `build` a master from a known image, `verify` it (every hash, the Merkle root, the parity,
the signature chain), then corrupt one payload byte and confirm `verify` localises it *and* `vault`
heals it. Cross-load the sidecar against Aaru's schema validator to prove interop. All doable in-cloud
with synthetic images — no hardware.

### Clean-room boundary
Fixity, provenance, completeness, self-heal. It stores what was dumped and proves things about it; it
strips and defeats nothing.

---

## 2. The Protection Preservation Profile — *achievable now*

### The gap
The community's most persistent pain is copy protection: SafeDisc, SecuROM, Ring Protech, LibCrypt are
"poorly supported," and the honest note in every guide is that preservationists fall back to closed
tools. The crucial distinction DiscForge can own: **preserving what a protection *looks like* is not
circumventing it.** Capturing the twin/weak sectors, the intentional error topology, the subchannel
anomalies, the ring position — as a faithful *fingerprint* — is preservation. Removing or defeating
them is not, and DiscForge never will.

### The design
A `protection-profile <image> [--json]` command that unifies the detectors already in the tree
(`libcrypt`, `subch`, `protection-scan`, twin-sector/EDC-cluster analysis, `disc-print` error
topology) into one clean-room fingerprint:

- **What it is:** named scheme(s) detected, with confidence and the evidence for each.
- **Where it lives:** exact sectors/ranges of the protection's physical signature (LibCrypt
  subchannel positions, SafeDisc weak-sector cluster, ring band LBAs), captured as coordinates.
- **What it looks like:** the measured characteristics (per-sector CRC deltas, EDC-failure pattern,
  angular ring position) — enough that two dumps of the same title can be compared for an authentic,
  matching protection signature.
- **What a plain image can't hold:** an explicit statement of the protection facets that only survive
  in subchannel/RAW/flux captures — guiding the dumper to the right capture mode.

Output folds into the DPM sidecar (initiative 1) as the `protection` block.

### Validation
Run against real discs in-repo (the LibCrypt/subchannel machinery already has fixtures; the RE2 PS1
disc in `samples/` is a real mixed-mode case), plus synthetic weak-sector/twin-sector fixtures with a
known topology, and confirm the profile reproduces the planted signature and a tampered copy diverges.

### Clean-room boundary
Characterise and locate only. The profile describes the protection so it can be preserved and matched;
it contains nothing that removes, patches, or bypasses it.

---

## 3. Flux / RF-level optical preservation — *the moonshot*

### The gap
The single genuinely-unsolved frontier. Every optical tool today trusts the drive's decode chip. There
is **no mature project for truly low-level CD dumps** — the optical equivalent of Domesday Duplicator
for LaserDisc or Greaseweazle/Applesauce for floppies. Preserve the disc as the raw RF signal off the
photodiode and you have captured the artefact itself, protections and marginal pits and all, decodable
forever by software as understanding improves.

### The design (phased; the early phases are software and buildable)
1. **A flux/RF container standard** — an open format for raw optical RF captures + calibration
   metadata (rotational speed, sample rate, drive/photodiode profile). Software-only; buildable now.
2. **An EFM software demodulator** — turn a raw RF/flux capture into the EFM bitstream, then EFM →
   14-to-8 → F1/F2 frames → CIRC → sectors. Every stage is a documented, deterministic algorithm that
   can be unit-tested against synthetic EFM and cross-checked with the existing CIRC/EDC code. This is
   the heart of the moonshot and it is *pure software* — validatable in the cloud without any hardware.
3. **Capture-hardware integration** — the RF tap off a drive's photodiode (the genuinely
   hardware-gated part; a research collaboration, not a sprint).

Phases 1–2 make DiscForge the first tool that can *decode* an optical flux capture even before common
capture hardware exists — the same way flux tooling for floppies matured software-first.

### Validation
Phase 2 is self-validating: synthesise an EFM bitstream from known sector data with the standard
encoder, feed it through the demodulator, and require the recovered sectors to match — then verify EDC/
ECC with the existing `EdcEcc` code as an independent oracle. No disc required.

### Clean-room boundary
Capturing and decoding the disc's own physical signal is the purest form of preservation there is. It
reads; it never circumvents.

---

## Recommended build order

1. **Protection Preservation Profile** (initiative 2) — most self-contained, unifies code that already
   exists, immediately useful, real-disc validatable today.
2. **Unified Preservation Master** (initiative 1) — consumes initiative 2's output as a block; the
   flagship "why DiscForge" artifact.
3. **Flux phase 1–2** (initiative 3) — the EFM software demodulator and container; the moonshot, begun
   software-first while capture hardware remains a research question.

All three honour the same rule DiscForge has held throughout: identify, verify, preserve — never
circumvent.

---

## Shipped since this survey — the Redump-fidelity and flux tracks

The real-hardware run on the Plextor (a pressed PS1 disc, TOCA Touring Car, SLES-00376) turned the survey
above into concrete features. Three findings from that capture, and three follow-on differentiators, have
shipped and are validated in-cloud against synthetic + real data:

- **`subq-map`** — recover each track's true INDEX 00/01 and real pregap from a captured subchannel, the way
  Redump derives a disc's gaps (Q channel, not a guessed convention).
- **`redump-cue`** — byte-preserving re-cut of a split bin/cue at the subchannel's INDEX 00 boundaries, so a
  "gaps folded into the previous file" capture becomes Redump-conventional without touching the payload.
- **`bad-sectors`** + the `.badsectors.json` sidecar — the unreadable-sector map now flows capture → convert →
  preservation master, so a holed dump reads as INCOMPLETE instead of a zero-filled hole hashing as data. This
  is the fixity gap no checksum can close.
- **`redump-diff`** — explains WHY a dump does or doesn't match Redump (split / padding / offset / bad sector),
  where every other tool stops at yes/no.
- **`redump-prep`** — one-step submission prep: re-cut + carry holes + conformance checks + submission text +
  DAT diff, returning a single SUBMISSION-READY / NOT-READY checklist.
- **`merge-cert`** — bad-sector-aware multi-copy merge that emits a *signed, checkable* per-sector provenance
  certificate (which copy each sector came from, how it was verified), hash-bound to inputs and output. No tool
  emits an auditable reconstruction.

### Initiative 3 (flux) — phase-2 demodulator now landed, software-first

`FluxContainer` (phase 1) already existed. The stage it deferred — **flux/RF transition timing → EFM channel
bitstream** — has shipped as `FluxDemodulator` / `FluxDecoder` (CLI `flux-demod`): channel-cell clock recovery
from transition timing (robust to jitter up to the half-cell ambiguity limit), NRZI, and 3T–11T run-length
quantisation, chained into the existing `Efm` decoder. It is validated by round-tripping the whole
bytes→EFM→flux→EFM→bytes pipeline against DiscForge's own encoder.

**The remaining block has landed.** `Efm.cs` now carries the authoritative ECMA-130 8-to-14 table (byte
index 0..255, plus the two frame-sync CONTROL patterns from the same standard) — taken from the
published standard itself (ECMA-130, 2nd edition, Annex D, Table D.1), with all 256 entries checked one by
one against it. Verified three ways: (1) a static-constructor self-check that
every one of the 256 entries individually satisfies EFM's own run-length rule and that no two collide with
each other or with the sync patterns; (2) the full existing `Efm`/`FluxDemodulator`/`FluxDecoder` xUnit
suite (round-trip, per-byte coverage, run-length/DSV bounds) passing unchanged against the real table; (3)
a probe script comparing the real table's channel statistics for constant-byte and scramble-defeating
inputs against 200 synthetic "normal" scrambled sectors, confirming the weak-sector signature described
below. **Decoding a real disc's flux is no longer gated on anything internal to this project** — the
demodulation architecture and the codebook are both complete now; only real capture hardware (phase 3)
remains a research question.

One genuine finding fell out of landing the real table: the *dominant* real weak-sector signature is not
what the project's earlier modelled codebook suggested. Content chosen to defeat CD scrambling (data equal
to the scramble sequence, so scrambling recovers all-zero) turns out, under the authentic table, to have an
almost normal transition density — but a Digital Sum Value (DC balance) excursion 50-100x any ordinary
sector's, because scrambling exists specifically to keep content balanced on the channel and this is
exactly the case that balancing can't fix. `WeakSectorAnalyzer` now flags either signature (density
collapse OR DSV blowout), rather than density alone — a direct, concrete consequence of no longer modelling
the channel, but measuring it.

> Note for CI: the xUnit suites for all of the above ship for Windows CI but are not run in the cloud build
> (xunit is absent from the offline NuGet cache). In-cloud validation is done via the CLI on synthetic and real
> captures. Run `dotnet test` on Windows to execute the ~24 pinning tests added across these features.

### A second "mind-blowing" idea shipped: `dump-ledger`, a public multi-submitter certificate ledger

Beyond the three surveyed initiatives, a deliberately ambitious brainstorm (what would be genuinely
mind-blowing, not just incremental) surfaced a gap none of the three initiatives above cover: `merge-cert`
signs one merge's audited reconstruction, `DumpLineage` chains one dump's custody — but nothing makes it
checkable, by a stranger, that *independent* people dumping the same disc actually got the same bytes.
Redump answers this today with a human maintainer counting submissions; `dforge dump-ledger` makes the
answer a cryptographic, self-verifying public artifact instead.

Each submission is a signed claim ("disc X, by a stable offset-invariant fingerprint, dumps to output hash
Y") that a submitter signs once, independent of any ledger — the claim itself is portable, and verifies the
same whether it's appended to this ledger, a fork of it, or none at all. Accepted claims are then
hash-chained into an append-only public log the same way `DumpLineage` chains one dump's events, except
here the chain spans many submitters and many discs, so no host of the ledger file can quietly edit,
reorder or drop a past entry. `Consensus(ledger, fingerprint)` groups a disc's submissions by output hash
and by DISTINCT submitter key, and flags a genuine dispute (independent submitters, different bytes) rather
than just counting raw submissions, which a single dishonest or buggy re-submitter could otherwise skew.

Shipped in v1.102.0 as `DiscForge.Core.Provenance.DumpCertificateLedger` + `dforge dump-ledger`
(`keygen`/`init`/`submit`/`verify`/`consensus`/`show`) — Core+CLI only this round, real-built and
real-tested (2705/2705, 11 new tests, plus an end-to-end CLI smoke test against the actual built binary
including hand-tampering a ledger file to confirm `verify` catches it). No GUI view yet — see `docs/NEXT.md`
for the current state.

### A third idea shipped: closing DiscMri's own diagnosis-to-action loop

The third brainstorm idea, and a direct instance of the pattern initiative 3's flux work also follows:
DiscMri (above, `## 1`'s sibling forensic tooling) has always been able to SHOW where a disc is physically
damaged — a polar map of the real Red Book spiral, worst evidence per pixel — but nothing turned that
diagnosis into an actual targeted re-read. `DiscForge.Core.Audio.SecureRip` already had this shape for
audio tracks (`PlanReread`: coalesce bad sectors into padded ranges, escalate pass count by severity);
`DiscMri.PlanReread`, shipped in v1.103.0, gives DiscMri's own whole-disc evidence the same treatment —
`dforge disc-mri <image> --plan-reread` now turns "this sector's EDC failed" directly into "re-read sectors
N..M, 3 passes, cache-defeating seeks." Real-built and real-tested (11 new tests, including one that pins
the planner's damage threshold against the CLI's own damage-count logic so the two can never silently
diverge) — see `docs/NEXT.md` for the current state.

**Update, v1.107.0 — the live wiring above now exists.** `dforge disc-mri-reread <drive> <plan.json>
<target.bin>` loads a `--plan-reread` plan and drives every sector in its ranges through the real
Tier-B `AdaptiveReread` controller — the same one `reread-probe` already proved against hardware on a
single diagnostic sector, unchanged, just looped across a whole plan — patching every sector it
recovers into the target image and reporting whatever's still bad by LBA. Deliberately reuses
`DriveRereadSource`/`AdaptiveReread` as-is rather than touching `RawDiscReader`/`SectorExtraction`, the
same scoping discipline `reread-probe`'s own design already established. `DiscMriView` also gained a
Plan Re-read/Save Plan button pair, so the whole loop — diagnose (polar map) → plan (coalesced ranges)
→ act (real drive re-read, patched into the image) — is reachable end to end, CLI and GUI both. Built
for real in this sandbox on both CLI targets including the actual `net8.0-windows` SPTI-linked one; not
run against a real drive (none reachable here) — see the v1.107.0 CHANGELOG entry for the full
verification-tier breakdown.

### A fourth idea shipped: `media-mortality`, a federated (not just aggregated) decay model

The fourth brainstorm idea is arguably the most genuinely novel of the four, because it needed no network,
server, or protocol to actually be federated. `disc-actuary`/`RotKinetics` fit a per-disc decay rate from
that disc's own scan history, but any one collection has thin evidence for any one manufacturer/mold — the
gap is that nothing lets independent collections' experience of the SAME cohort combine, without either
centralizing raw disc data (a privacy non-starter this project has never been willing to accept) or
trusting one party's numbers as authoritative.

`MediaMortality` reduces a disc's fit to two numbers (growth rate, sample count — no identity, no
timestamp, no error history) and combines any two contributors' running statistics with the
Chan/Golub/LeVeque parallel-variance algorithm: EXACT and order-independent, so the community model that
falls out of a chain of pairwise merges is provably identical to one a central server would have computed
from all the raw observations, and no server, and no raw observation, was ever required. A hard privacy
floor (`MinContributorsToReport = 3`) refuses to report a cohort thin enough that a recipient could
reverse-engineer roughly how fast one specific contributor's specific disc decays.

Shipped in v1.104.0 as `DiscForge.Core.Forensics.MediaMortality` + `dforge media-mortality
observe/merge/estimate/show` — Core+CLI only, real-built and real-tested (12 new tests, three of which
specifically verify the federation claims — not just the arithmetic — by cross-checking against a naive
reference computation and proving three-way merge associativity). See `docs/NEXT.md` for the current
state.

### A fifth and final idea shipped: the verification engine as WebAssembly — with a real finding

The fifth brainstorm idea (initiative 3's "Phase — feature C: WASM Core" ambition, generalized beyond the
flux decoder to the verification engine broadly) asked whether anyone should ever have to install DiscForge,
or trust a server, just to check whether a `dump-ledger.json` or `.dmc.json` certificate is genuine. The
answer: no — `src/DiscForge.Wasm`, a standalone Blazor WebAssembly app referencing `DiscForge.Core` only,
compiles the real `DumpCertificateLedgerLog`/`MergeCertificate` verification code to `.wasm` and runs it
entirely in the visitor's own browser tab, no upload, no backend.

What made this worth calling a genuine differentiator rather than a demo: it was verified further than "it
compiled." A full `dotnet publish -c Release` (real emscripten/AOT native linking) succeeded, and the
resulting static site was driven with headless Chromium (Playwright) to actually exercise the compiled
code — a genuinely signed ledger came back chain-intact, a hand-tampered one came back chain-broken, live,
in a real browser engine. That same live test also surfaced a genuine limitation rather than papering over
it: .NET 8's browser-wasm runtime has no ECDSA (`ECDsa.Create()` throws `PlatformNotSupportedException`),
so today's page can check hash-chain integrity in-browser but not ECDSA signatures — disclosed directly on
the page, not hidden, with the independent hash-chain check still fully verified. Fixing that needs either
a `SubtleCrypto` JS interop shim or a future .NET runtime with browser ECDSA support — left as explicit
future work rather than attempted under time pressure.

Deliberately kept out of `DiscForge.sln` so `build-app.ps1` never silently requires the `wasm-tools` SDK
workload — build/publish it separately. Shipped in v1.105.0; this closes out all five ideas from the
original brainstorm. See `docs/NEXT.md` for the current state.

**Update, v1.106.0 — the ECDSA gap above is now closed, not just documented.** A `SubtleCrypto` JS
interop shim (`wwwroot/js/ecdsaFallback.js`) verifies the same ECDSA P-256/SHA-256 signatures via the
browser's own Web Crypto API, using two format compatibilities confirmed empirically against
DiscForge.Core's real output (SPKI import needs no reformatting; .NET's default raw-`r||s` signature
format is exactly what `SubtleCrypto.verify` expects). `DumpCertificateLedgerLog` and `MergeCertificate`
each gained a public `Get*SigningBytes` method so the fallback signs/verifies the identical bytes the
real `ECDsa` path uses — one source of truth, not a parallel reimplementation that could quietly drift.
Live-testing the merge-certificate path specifically (left untested in v1.105.0) caught a genuine bug:
`MergeCertificate.VerifySignature()`'s catch-all was swallowing `PlatformNotSupportedException` and
reporting a validly-signed certificate as INVALID rather than falling back — fixed to match the pattern
the ledger path already used correctly. Both paths, both the valid and the tampered case, are now
confirmed working end to end in a live headless-browser session. See the v1.106.0 CHANGELOG entry and
`docs/NEXT.md` for the full detail.
