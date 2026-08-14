# DiscForge — Groundbreaking Features Dossier

*A deep frontier scan: five research lenses (signal/physics, machine learning, cryptographic
provenance, distributed/swarm, formal methods) pushed for genuinely novel mechanisms — not forum
practice — then gap-checked against DiscForge's existing ~179 commands. Everything here stays inside the
clean-room boundary: identify / verify / preserve / convert / recover, never circumvent, strip, decrypt
or distribute protected content.*

---

## What already exists (so these are deliberately *beyond* it)

The gap-check found four of the boldest mechanisms already present in embryo. The frontier ideas below
are scoped to go past them, not duplicate them:

- **`dump-merge`** — already merges several imperfect rips of the *same* disc at the sector/block level.
  → Frontier goes **below** the sector (sub-CIRC symbol voting) and **across the network** (swarm).
- **`collection-archive`** — already dedups a library to unique **whole-file** blobs + rebuild recipes.
  → Frontier moves to **content-defined chunking** so near-identical files (differing by a few sectors)
  also collapse, with cryptographic reconstruction proofs.
- **`recover-oracle` / `CircRecovery`** — already models CIRC recovery capacity.
  → Frontier adds **soft-decision** decoding using C2/analog confidence.
- **`disc-delta`** — already does a **file-level** delta.
  → Frontier does a **semantic, structure-level** diff after subtracting derivable layers.

Genuinely absent (all new): Merkle transparency log, trusted timestamps, proof-of-retrievability,
emulation-readiness, differential parser fuzzer, information-theoretic minimal descriptor, provably-lossless
conversion certificates, cross-title asset-reuse graph, and the entire swarm/distributed layer.

---

## Pillar 1 — A formal-methods spine (the deepest moat, all buildable now, offline)

The single biggest differentiator: no preservation tool makes *provable* claims. This pillar turns
DiscForge's operations into certified ones. It's fully offline, needs no hardware, and reuses existing
internals.

1. **Provably-lossless conversion certificates.** Model each converter (`bin/cue↔chd↔iso+sub`, `gdi→chd`,
   `cdi→bin`, scramble/descramble, EDC/ECC regen, mode remux) as pure `enc`/`dec` functions over a
   symbolic sector/track/subchannel model, and *prove* `dec(enc(x)) ≡ x` bit-for-bit — exhaustively for
   the small-input transforms (scramble seed, EDC/ECC, mode), via an embedded SMT solver (Z3 `.NET`
   bindings) for the gap/index logic. A hash proves one instance survived; a certificate proves the
   *converter itself* cannot lose information for that layout class. **Start with the exhaustive transforms
   — cheap, decidable, shippable now.**

2. **Information-theoretic Minimal Disc Descriptor (MDD).** Split a dump into *essential entropy* vs
   *derivable structure*, using generators DiscForge already owns (ECC/EDC from RS, scrambling from LBA,
   sync/address marks, RLE gaps/padding, subchannel-Q from TOC). The MDD is the minimal residual + the
   generator list to reinflate, chosen by an MDL objective `min L(model)+L(residual)`. Two payoffs: (a)
   lossless compression *below* general compressors (which don't know ECC is derivable), and (b) a
   **canonical identity** — two byte-different dumps with identical MDDs are provably information-equivalent.

3. **Physical completeness proof — a "nothing left on the disc" certificate.** Reconcile three independent
   accounts of a disc's extent (TOC/PMA declaration vs physical lead-out sector count vs dump byte-length),
   plus full P–W subchannel coverage and per-session TOC-vs-dumped tracks, and emit a proof-carrying map:
   these physical regions exist, these were captured, these provably were *not* — distinguishing "disc has
   nothing there" from "our dumper missed a hidden session / subchannel / overburn area."

4. **Semantic disc diff.** Diff two dumps at the structure tree (sessions→tracks→filesystem→protection),
   not the byte array (scramble+ECC turn one logical change into megabytes of churn). Subtract derivable
   layers first (MDD), then tree-diff the residual: *"track 3 pregap grew 150 sectors; /BOOT.BIN changed 12
   bytes at the build-timestamp field; protection signature moved."* Extends `disc-delta`.

5. **Differential parser-hardening fuzzer.** Coverage-guided, structure-aware differential fuzzing of
   DiscForge's ~30 format parsers against reference tools (libcdio, cdrtools, isoinfo, MAME chd) via
   `SharpFuzz`; a divergence (different file list / sector count / a crash) is a minimized bug ticket. With
   179 commands of binary parsing over *untrusted* disc images, this is a real security surface — and the
   highest ROI-per-effort item in the whole dossier. **Build-now, no dependencies.**

6. **DiscSpec DSL (stretch).** A declarative grammar (Kaitai/EverParse lineage) with *coverage* and
   *non-overlap* contracts, from which parsers+serializers are generated and well-formedness proven — the
   spec, parser, fuzz-grammar and doc become one machine-checked artifact.

7. **Emulation-readiness analyzer.** Statically predict what a faithful emulator will need and flag gaps at
   dump time: subchannel-dependent protection present but subchannel not dumped → FAIL; twin-sector/intentional-error
   protection stored as plain ISO → downgrade risk; referenced BIOS/peripheral absent from the manifest →
   incomplete. Names what's missing; never strips anything.

---

## Pillar 2 — Recovery below the single-disc physical floor

Recover bits that *no drive firmware will return* and, ultimately, that *no single surviving disc holds*.

8. **Sub-CIRC multi-copy symbol voting.** Extends `dump-merge` from block level to the *channel symbol*
   level: for each C1/C2 codeword position across M copies/re-reads, take a maximum-likelihood vote weighted
   by per-symbol confidence (C2 pointers / re-read agreement / analog eye-height if RF), then feed the voted
   stream + residual erasures into RS. Fixes sectors where *every* copy is individually unrecoverable but
   their errors are uncorrelated. **A first cut needs only C2 pointers — no new hardware.**

9. **Soft-decision CIRC decoding.** Replace hard erasure-and-error RS with a Chase/Koetter-Vardy soft
   decoder using C2 flags (and analog confidence where available) — exceeding the classic "t errors or 2t
   erasures" bound as a re-runnable offline recovery pass. Upgrades `recover-oracle`.

10. **Filesystem-constrained erasure solving.** A damaged sector isn't unconstrained noise — the filesystem
    fixes many of its bytes (magic numbers, directory framing, zero padding) and files carry their own CRCs
    (zlib/PNG, MPEG sync, per-file hashes). Inject those as *known symbols* into the RS solver (often tipping
    uncorrectable→correctable) and rank candidate reconstructions by whether the file's own CRC validates.
    Joint source-channel decoding that couples DiscForge's two halves (FS parsing + error correction).

11. **Physics-grounded rot kinetics.** Upgrade `disc-rot` from linear extrapolation to an Arrhenius/Eyring
    dual-stress rate law `k = A·exp(−Ea/RT)·f(RH)`, calibrated per media class (from ATIP/dye fingerprint)
    against the NIST/LoC accelerated-aging datasets and Bayesian-updated with the disc's own error history.
    Output: a survival curve with confidence bands and a per-disc "read-me-before" date. **Uses data
    DiscForge already collects.**

12. **RF-native decode stack (hardware-gated, the boldest).** Ingest a raw RF/EFM capture (DomesDay-class
    ADC at the drive's RF test point) and make DiscForge the *software* decoder — PLL clock recovery, EFM/EFMPlus
    demod, then existing CIRC/RS — preserving the raw capture as the archival master so it can be re-decoded
    forever, re-sliced at many thresholds (PRML/Viterbi), and fused with #8. Moves the decode boundary out
    of opaque firmware into inspectable code. Needs external capture hardware to *produce* files; DiscForge
    only consumes them.

13. **Adaptive re-read controller — perfect for the incoming Plextor.** Treat the drive as a tunable
    instrument: per stubborn LBA, sweep spin speed, direction, dwell/retry (and Plextor-specific read-quality
    knobs), modelled as a bandit that allocates the next re-read to maximise expected C2-clean yield
    (Thompson sampling), feeding every variant into the #8 voting pool. **This is the natural companion to
    `extract-sectors` when the hardware lands.**

---

## Pillar 3 — Verifiable, tamper-evident provenance (beyond the existing ledger)

DiscForge already has a signed provenance chain + federated consensus ledger. This pillar makes provenance
*globally auditable* and *content-blind*.

14. **Certificate-Transparency-for-dumps.** An RFC-6962 Merkle log of dump descriptors with Signed Tree
    Heads, serving inclusion proofs ("this hash is in the log") and consistency proofs ("the log never
    rewrote history"); gossiped STHs catch a forking log; a Wesolowski VDF chain makes back-dating
    ("I preserved it in 2005") un-forgeable. Turns "who dumped it first" from social trust into
    math-checkable, archive-death-surviving fact. Client is buildable now; full value needs a light log server.

15. **RFC-3161 trusted timestamps + web-of-trust receipts.** Cheap, standards-based, legally-recognised
    "existed by date D" (multi-TSA), wrapped in preservation receipts that dumpers counter-sign — upgrading
    the flat consensus ledger to *trust-weighted*, Sybil-resistant-by-social-graph. **Easiest quick win in
    this pillar.**

16. **Content-defined chunking + Merkle-DAG dedup.** FastCDC (Gear-hash rolling window) + a Merkle DAG so
    the whole preservation universe dedups to unique *chunks* (not whole files), collapsing regional variants
    and shared audio/middleware, with a provable reconstruction (replay manifest, verify each chunk, verify
    root = published image hash). Beyond `collection-archive`'s whole-file model. **Buildable now, offline.**

17. **Trustless re-derivation proofs.** Run deterministic conversions in a pinned environment and emit an
    in-toto/SLSA-style signed attestation `{input_root, output_root, pipeline_id, param_hash}` so anyone can
    re-derive B from A bit-for-bit — conversions become as trustworthy as originals. Pairs with #1.

18. **Proof-of-retrievability audits of cold copies.** Shacham-Waters compact PoR (homomorphic BLS tags,
    constant-size challenge/response) to get a cryptographic heartbeat that a *remote/untrusted* mirror still
    holds a whole image — downloading nothing. Extends bit-rot watch beyond your own disks. (A sentinel-block
    fallback is a trivial first cut.)

19. **Crypto-agility layer.** Dual-hash (SHA-256 + BLAKE3/SHA-3) and hybrid signatures (Ed25519 + hash-based
    SLH-DSA/SPHINCS+, FIPS 205) with a re-anchoring migration transcript — because preservation is
    century-scale and a chain that dies when SHA-256 falls is a liability. Design in from day one.

---

## Pillar 4 — Collective / swarm preservation (needs a network to matter)

The unifying novel primitive: a **proof-of-possession-of-the-complement gate** — a peer only ever receives
the specific damaged/missing sectors of a title it can cryptographically prove it *already substantially
holds*, so collectors heal each other into a bit-exact canonical image while a non-owner can never bootstrap
a playable copy. That gate is what keeps the whole family clean-room.

20. **SWARM sector-wise recovery mesh.** Peers exchange per-sector *health vectors* (not data) over a
    Kademlia DHT keyed on the title's genome ID, reconcile them difference-only via minisketch/RIBLT, and a
    damaged collector pulls exactly its missing sectors (gated by the possession proof) to assemble a
    bit-exact mosaic with per-sector signed provenance — a master that provably never existed on any single
    readable disc.

21. **Swarm physical-layer interpolation (deepest).** For a sector where *every* copy fails ECC, peers pool
    their raw noisy sub-threshold reads + per-bit soft confidence; belief-propagation across the combined
    evidence lets the sector's own RS finally close (independent discs fail on *different* bits) — then the
    real EDC/ECC must verify it. Resurrects data that is, copy-by-copy, genuinely lost.

22. **Fountain-coded rot insurance.** The swarm holds a title as RaptorQ repair symbols spread thinly across
    N collectors; any collector who later rots reconstructs their *own* erased sectors from slightly-more-than-erased
    symbols (gated by a proof-of-erasure). Herd immunity at ~5% overhead each.

23. **The Fingerprint Web + Orphan Triage.** A gossiped CRDT graph of signed physical-fingerprint/ring/rot
    tuples (never content) → a live **preservation coverage map** ("only 2 readable copies of this pressing
    survive, both degrading"), feeding an extinction-risk queue and a set-cover solver that tells the
    community exactly which discs to dump first to close the largest survival gaps.

24. **Living DATs + distributed canonicalization.** DATs as CRDT-backed live consensus objects that ingest
    verified dumps, auto-increment agreement, and fork genuine variants into minority reports; disagreeing
    dumps of one title are canonicalised by a verifiable vote weighted by *distinct physical discs* (error-map
    identity), so 100 dumps from 1 disc ≠ 100 votes.

25. **Server-free ledger + proof-of-preservation reputation.** Replicate the ledger as a δ-CRDT over gossip
    (survives sneakernet, can't be shut down); reputation is earned by *provably storing rare, at-risk data
    over time* (PoR audits weighted by title rarity) — Sybil-resistant without coins, aligning status with
    actually keeping rare things alive.

---

## Pillar 5 — Machine learning that keeps ground truth sacred

The invariant across every ML idea: outputs are **ranked hypotheses with calibrated confidence** (conformal
prediction / one-class scores) that are either **verified by the disc's own EDC/ECC/hash before acceptance**
or **quarantined as provenance-tagged derivatives** — the preservation master is *never* overwritten by a
model guess. Most have a classical, fully-offline .NET first version.

26. **DAT-less structural identification.** Metric-learning embedding over structural features (session/TOC
    layout, FS topology, volume strings, sector-density) → nearest known titles for a disc that matches *no*
    hash, with a principled "this is unlike anything in the DB" novelty signal that flags **undumped**
    releases. Labelled "UNVERIFIED candidate," never promoted without a real hash match. Classical kNN/HNSW
    v1 is offline. (Training data = every DAT-matched dump already in-house.)

27. **Unsupervised discovery of *unknown* protection schemes.** HDBSCAN over on-disc anomaly morphology
    (twin-sector geometry, DPM residuals, weak-sector distribution, EFM outliers); a tight cluster matching
    no known signature = a candidate new/variant protection for human confirmation. A discovery engine for
    the long tail, versus today's signature arms race. Characterises, never defeats.

28. **Ring-code / IFPI-SID computer-vision reader.** Dewarp the disc's mirror band (polar→Cartesian unroll),
    enhance the stamped glyphs, OCR the matrix + mastering/mould SID with per-glyph confidence (low-confidence
    chars masked for human confirmation). Automates the most laborious plant-level provenance metadata.

29. **Pre-dump quality / drive-suitability prediction.** Gradient-boosted model over a fast probe (quick BLER
    sweep, spin-up, reflectivity, media age) → expected error rate, likely bad zones, best drive in your
    stable. Advisory triage for a shelf of 500 rotting discs; never grades the actual dump. Lowest-effort ML
    win, fully offline in ML.NET.

30. **Damage-type diagnosis + counterfeit detection.** CNN/texture-features over the error-map *image* to
    separate radial scratch vs rot vs manufacturing defect vs *intentional protection* (and defensively lock
    protection regions against any reconstruction); one-class "authenticity manifold" per plant/stamper to
    flag burned-as-pressed and reprint-from-scan counterfeits — protecting DAT-submission provenance integrity.

31. **Redundancy-anchored reconstruction (EchoFill) & provenance-split audio restoration.** ML *proposes*
    bytes for a gap; the sector's own EDC/ECC is judge — a fill is accepted only if it makes the CRC validate,
    and every filled byte is tagged `measured / parity-recovered / model-proposed+EDC-confirmed`. For audio,
    any learned inpainting ships as a *separate* provenance-tagged listening derivative with a hard gap-length
    cap; the master always keeps the hole. ML is made mathematically incapable of poisoning the archive.

---

## Clean-room excludes (bright lines to enforce in design)

- **Anything that strips/regenerates protection to make a playable image, decrypts protected content,
  derives disc/console keys, or "normalises away" protection during conversion.** The MDD/SMT generator set
  must be whitelisted to *published, non-protection* standards (ECC, scramble) only.
- **Proof-of-possession / commitments degraded into a content-transfer channel** (returning raw sectors, or
  enough openings to reconstruct a disc) — hard per-epoch sector caps, byte-free responses.
- **Swarm configured as an on-demand piracy fabric** where zero-holdings strangers assemble playable content
  — the possession-of-the-complement gate is load-bearing; reconstruction authority stays with the owner.
- **K-of-N custody / time-lock "embargo release" of protected titles** — custody (K−1 shares reveal nothing)
  is fine; auto-releasing playable protected content is distribution.
- **ML that writes into ground-truth artifacts without independent EDC/ECC/hash verification** — out of bounds.

---

## Recommended build order

**Buildable now, offline, no hardware — do these while waiting on the Plextor:**
1. ✅ **SHIPPED** — Differential parser-hardening fuzzer (`fuzz-parsers`, `Util.ParserFuzz`): mutates a seed
   and runs every parser, flagging unclean crashes/hangs vs clean format rejections; validated (catches a
   real IndexOutOfRange bug in a test probe; the shipped PVR/PVM/NKit/DVD/IP.BIN/MPEG parsers survive 3,000+
   mutations cleanly).
2. ✅ **SHIPPED** — Semantic (region-level, shift-tolerant) disc diff (`disc-semdiff`, `Forensics.DiscRegionDiff`):
   CDC-based region diff that localizes where two dumps diverge and survives insertions (99.6% shared after a
   front insert). *Physical completeness proof (1.3) still to do.*
3. ✅ **SHIPPED** — Content-defined chunking + Merkle-DAG dedup (`chunk-manifest`, `Preservation.ContentChunking`):
   FastCDC + Merkle root + reconstruction proof; 99% of chunks survive a 1-byte prepend (vs 0 for fixed-block).
4. ✅ **SHIPPED** — Physics-grounded rot kinetics (`rot-kinetics`, `Forensics.RotKinetics`): first-order
   (Arrhenius/Eyring) decay fit + survival forecast with confidence band and storage-environment acceleration;
   recovers a known growth constant to 2 dp.
5. ✅ **SHIPPED** — Physical completeness proof (`completeness-check`, `Forensics.DumpCompleteness`):
   reconciles cue layout, data-file size and subchannel sector count into a coverage certificate, and
   states what a bin/cue inherently cannot hold (lead-in/out/PMA/ATIP).
6. ⏸ **DEFERRED** — RFC-3161 trusted timestamps: written correctly against the native
   `System.Security.Cryptography.Pkcs` API, but that assembly isn't in the base framework (needs a NuGet
   `PackageReference` that can't be restored in the cloud sandbox) *and* validation needs a live TSA — so it's
   held out of the build until it can be added + validated on the Windows side. The Core file is ready to drop in.
7. *Still to do:* Provably-lossless conversion certificates (1.1), sub-CIRC symbol voting (2.8),
   filesystem-constrained solving (2.10), emulation-readiness analyzer (1.7), Minimal Disc Descriptor (1.2).

**When the Plextor arrives:** Adaptive re-read controller (2.13) — pairs directly with `extract-sectors`.

**Frontier flagships (bigger, network/hardware/compute-gated):** the RF-native decode stack (2.12), the
SWARM mesh + physical-layer interpolation (4.20/4.21), and Certificate-Transparency-for-dumps (3.14) — the
three deepest moats, worth a dedicated push each.

**The three deepest differentiators overall:** the formal-methods spine (proofs of losslessness/completeness),
collective recovery below the single-disc floor (swarm symbol/soft-decision fusion), and content-blind
verifiable provenance (transparency log + PoR) — no preservation tool has any of the three today.
