# DiscForge re-evaluation — August 2026 (post-hardware campaign)

Companion to `FRONTIER.md` (the five-pillar research dossier). Where that
document reasons from first principles, this one reasons from bruises: the
extract-sectors bring-up on real drives, the three-coaster burn investigation,
and the mixed-mode discovery. Gaps first, ranked by how much they actually
mattered; then frontier features scored for wow *and* feasibility against code
that exists today (~70 Core subsystems, 469 documented commands).

---

## Part 1 — Honest gaps, ranked by evidence

### 1. Verification is a tool, not a habit *(the gap that bit us)*
The half-void dump lived for days because nothing audits a dump BY DEFAULT.
`inspect-raw --deep` existed the whole time; nothing made anyone run it. Every
extraction should END with an automatic audit — sync census, EDC sweep, zero-run
detection, Q coverage — and refuse to grade a dump COMPLETE without it. Twin
honesty bug: `inspect-raw` silently skips sync-less sectors and printed "clean"
on a file that was 47% empty. In a project whose motto is proof, both are bugs
in the motto.

### 2. Mixed-mode discs *(top of NEXT.md)*
`--disc` treats a disc as one span; an 8-track PS1 disc disagreed, expensively.
Track-aware spans, per-track types and sync gating, pregap capture, true cue
emission, BoundaryLba classification for transition sectors.

### 3. No dump provenance
A dump records WHAT was read, not HOW: drive, firmware, settings, engine
version, retry policy, per-span grades. That context died in terminal
scrollback and had to be reconstructed by archaeology. It belongs in a sidecar
manifest, always. (FRONTIER Pillar 3 goes further — tamper-evident ledgers;
this gap is the humble prerequisite.)

### 4. Drive knowledge doesn't accumulate
This week we learned the SH-224DB's C2 pointers cry wolf on span-opening reads
and the PX-W5224TA zero-mutes audio-read-as-data with SUCCESS status. Both
lessons live in a chat log. Operations should append observed behaviour to a
local per-drive dossier automatically; the static KnowledgeBase becomes the seed,
not the ceiling.

### 5. No resume for interrupted dumps
Ctrl+C on a 289k-sector dump = start over. A progress journal alongside the
`.part` file could make dumps resumable without giving up the no-partial-output
guarantee.

### 6. Sub-channel capture is Q-only
`.subq` (16 B/sector) serves analysis; the interchange world speaks 96-byte raw
P–W (CloneCD `.sub`). `SubChannel.RawPw` already exists in MmcCommands —
capturing it closes an interop hole and enables CD+G and protection *detection*
work on live rips.

### 7. GUI lags the CLI's best engineering
recover, secure-rip, extract-sectors, detect-offset — CLI-only. 59 views, none
showing the newest capability.

### 8. The orchestrated round trip
dump → audit → score → merge → convert → burn → compare exists as pieces; the
redump-grade workflow is still hand-run. One wizard verb should drive it.

---

## Part 2 — Frontier features (new ground, honestly costed)

Cross-references: provenance ideas extend FRONTIER Pillar 3; consensus healing
is Pillar 4's most buildable slice. The rest below is new since the hardware
campaign.

### A. The Dump Certificate *(flagship differentiator)*
Every dump ends with a signed, machine-readable certificate: disc identity,
drive + firmware, settings, per-span grades, bad/boundary maps, tool version —
and a **Merkle tree over the sectors**, root in the certificate. Anyone can
later verify *any 2 KB slice* of a 700 MB image against the original dump event
without rehashing the file. Chain of custody for preservation; no mainstream
tool has it. Poetic bonus: the vestigial ECDSA licensing code becomes the
signer — reborn under GPL with an honest job.
*On the shelf already:* hashing infra, BadSectorMap, CICM writer, ECDSA.

### B. Disc MRI — the polar damage map *(highest wow-per-effort)*
Extraction already collects per-sector evidence: retries, C2 counts, Q validity,
recovery outcomes. Render it as a **polar heatmap of the physical disc**
(LBA→radius/angle is arithmetic): the scratch you feel with a fingernail
appears as an arc; pressing defects ring; rot blooms from the hub. Live view
while dumping; PNG in the report. Diagnosis becomes something you *see*.
*On the shelf:* all the data; this is purely a view.

### C. DiscForge in the browser *(groundbreaking distribution)*
Core is pure net8.0 — it compiles to WebAssembly nearly as-is. Drag a bin/cue
onto a page: identify, EDC-audit, DAT-verify, chd/aaruf inspect — entirely
client-side, nothing uploaded, nothing installed. The community gets DiscForge's
verification engine as a URL; drives stay native, verification goes universal.
*On the shelf:* the whole Core; needed: a Blazor WASM shell.

### D. Consensus healing — two broken discs, one proven image
Given N dumps of the same pressing, reconstruct per-sector from whichever source
carries proof (EDC-valid, AccurateRip-matched), emitting a certificate that
records every sector's provenance. Damaged collections heal each other.
*On the shelf:* RecoverySession, MergeCertificate, C2ConsensusMerge, AccurateRip.

### E. The Disc Actuary — predicting media death
BLER scanning + collection triage exist. Add longitudinal storage: every scan
appends to a disc's history; trend correctable-error growth; rank a collection
by *estimated remaining readable life*. "Re-dump these nine first — they're
dying fastest" is a sentence no other tool can say.
*On the shelf:* Bler, CollectionTriage, SalvagePlanner; needed: a time series.

### F. The Drive Dossier — institutional memory for hardware
Formalize this week's hand-learned lessons: operations append observed
behaviour (C2 trust per context, mute signatures, confirmed offsets, real
overread reach) to a per-drive dossier that pre-fills settings and warns on
known quirks. Exportable — a community drive database grown from evidence.
*On the shelf:* DriveKnowledgeBase, DriveProfile; needed: persistence + hooks.

### G. Pressing DNA — identity beyond the hash
Fuse data hash, TOC geometry, pregap lengths, subcode features and offset
artifacts into a compact fingerprint distinguishing *pressings* of one title —
the logical cousin of Redump's physical ring codes, answerable offline.

### H. `dforge prove` — the ethos as one verb
Dump → audit → certificate → optional reburn → cross-verify, one command, one
verdict: **PROVEN**. The round trip this campaign fought for, productized.

---

## Suggested order

1. **Gaps 1 + 2 together** (auto-audit + track-aware `--disc`): they close this
   campaign's wounds and everything else stands on them.
2. **B — Disc MRI**: maximal visible payoff, minimal risk, revives the GUI.
3. **A — Dump Certificate**: the flagship; E and F adopt its sidecar habits.
4. **C — WASM** for the distribution splash; **D / G / H** as the community
   features mature alongside FRONTIER's deeper pillars.
