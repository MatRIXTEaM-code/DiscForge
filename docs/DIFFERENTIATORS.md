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

**The one remaining block is external:** `Efm.cs` uses a *modelled* codebook, not the authoritative ECMA-130
8-to-14 table. Decoding a real disc's flux is a pure data swap once that table is dropped in (from ECMA-130
Annex D, or an open-source `efm.c` such as cdrdao's). The demodulation architecture — the genuinely unsolved,
hardware-independent part — is complete and proven now; only the table gates real-disc decode.

> Note for CI: the xUnit suites for all of the above ship for Windows CI but are not run in the cloud build
> (xunit is absent from the offline NuGet cache). In-cloud validation is done via the CLI on synthetic and real
> captures. Run `dotnet test` on Windows to execute the ~24 pinning tests added across these features.
