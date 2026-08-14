# DiscForge — Feature Feasibility Research, round 2

*Prepared 2026-08-10. A deep dive into the next wave of candidate features, researched
against the current codebase, the public format specifications, and DiscForge's two hard
constraints: **clean-room** (reimplement from public documentation only — never copy GPL
source, never defeat protection or decrypt console content) and **provably correct or
declined** (ship a format only once it validates against an independent oracle).*

## What changed since the 2026-08-09 round

The previous research picked four features and they have largely shipped: **ECM** (decode/
encode, round-trip + independent EDC/ECC verify), **PS1 MDEC video** (decode to PNG),
and **Xbox 360 GOD** (identify/de-chunk). **RVZ/WIA** was deferred as the marquee-but-
hardest item and is still open. So this round does two things: it revisits RVZ now that we
have the full spec in hand, and — more importantly — it scans the *rest* of the
preservation landscape for the strongest genuinely-new candidates. The headline finding is
a strategic one: the single hardest component (the Wii partition engine) is shared between
RVZ and the best new format on the board, **NKit** — build it once, unlock both.

## The landscape scan

I read the authoritative specs for each candidate rather than working from memory:
Dolphin's own `WiaAndRvz.md` for RVZ/WIA, the Applesauce WOZ 2.1 reference, the WikiTemp
NKit format page, and the SuperCard Pro image spec. The candidates that survived the first
cut — public spec exists, no protection-defeat required, real preservation value, and a
plausible offline validation path — are below.

## Scorecard

| Feature | Value | Effort | Clean-room | Needs a policy call? | How much already exists |
|---|---|---|---|---|---|
| **NKit → ISO (GameCube)** | High | M | Safe | No | Nothing yet; but GC-disc reader + FST walker exist |
| **NKit → ISO (Wii)** | High | L | ⚠ re-encryption | **Yes** (common-key round-trip) | Shares the Wii engine with RVZ |
| **RVZ/WIA → ISO (GameCube)** | Very high | L | Safe | **Yes** (vendor zstd) | `rvz-info` metadata parse; decode stub |
| **RVZ/WIA → ISO (Wii)** | Very high | XL | ⚠ re-encryption | **Yes** (zstd + common-key) | Shares the Wii engine with NKit |
| **WOZ (Apple II) read + convert** | Medium-high | M | Safe (textbook fit) | No | HFS reader + Apple Partition Map already ship |
| **Flux import: SCP + KryoFlux stream** | Medium-high | M | Safe | No | Phase-1 `flux pack` container already ships |
| **CTDB (CUETools DB) verify** | Medium | S–M | Safe | No | AccurateRip + submission-info already ship |
| **PC-98 D88 floppy family** | Medium | S–M | Safe | No | Nothing; but FAT/partition infra exists |
| **CHD read v1–v4 + more track types** | Medium | M | Safe | No | v5 read/create ships |

## 1. NKit → ISO — the best new format on the board

**What it is.** NKit is the dominant "recover to Redump" preservation format for GameCube
and Wii. It stores the *bare minimum*: junk between files is dropped, scrubbed sectors are
collapsed to a fill pattern, and (on Wii) the encryption and hash trees are removed because
they are fully recreatable. From an NKit image it reconstructs a **byte-exact Redump ISO** —
verified by a stored source CRC32. It is the format an enormous amount of the GC/Wii library
is archived in, and DiscForge has *zero* coverage of it today.

**Why it fits DiscForge.** Recovery is **entirely self-contained — no external keys or
data** (the spec is explicit). The header sits at disc offset 0x200 with an `NKIT` magic, a
source-image CRC32, an NKit CRC, and the original image length. The reconstruction is: parse
the partition/FST headers, RLE-decode the gap encoding (junk / uniform-block / non-junk),
regenerate junk, and — for Wii — rebuild the per-2 MiB hash tree and re-apply encryption.
GameCube needs none of the crypto path, so a **GameCube-only milestone is clean, provable,
and self-contained** — an unambiguous M.

**The catch (Wii).** A byte-exact *Wii* Redump ISO is the **encrypted** original, so exact
reconstruction requires re-encrypting the stored (decrypted) partition data with the
partition title key, which is obtained via the Wii common key. That is the same policy
question RVZ-Wii raises (see §3) — flagged, not assumed.

**Validation.** Round-trip against the NKit's own stored source CRC32 (self-checking by
design), plus a real `NKit`+ISO fixture pair for GameCube to close framing questions. Store
the NKit bytes + expected ISO hash as a fixture, never a game asset.

**Recommendation.** Do the **GameCube milestone first** — highest value-per-effort among new
formats, self-contained, no policy call. It also builds the FST/gap machinery the Wii path
reuses.

## 2. The strategic insight — one Wii engine unlocks two formats

RVZ-Wii and NKit-Wii need the *same* hard component: rebuild the Wii partition's per-block
SHA-1 hash tree, apply the stored hash exceptions, and AES-re-encrypt with the title key —
plus regenerate disc junk from a Lagged-Fibonacci PRNG. This is the riskiest code in either
feature (every step fails *silently* — an off-by-one yields a plausible-but-wrong ISO). Built
once as a validated `WiiPartitionRecrypt` engine behind a single oracle, it serves **both**
RVZ and NKit. That changes the sequencing: don't think "RVZ vs NKit," think "GameCube paths
first (cheap, safe, independent), then one shared Wii engine that completes both."

The junk PRNG is now fully pinned from Dolphin's public spec: Lagged-Fibonacci (f=xor, j=32,
k=521), 68-byte seed filling the first 17 words, the documented state-advance recurrence,
four warm-up rounds, and 32 KiB output-alignment rule. That's the one piece of RVZ *and*
NKit reconstruction that is spec-complete and unit-testable on its own, with no disc — a
good first provable increment.

## 3. RVZ/WIA → ISO — the marquee, revisited with the full spec

**Now spec-complete on paper.** The Dolphin `WiaAndRvz.md` gives everything a reimplementer
needs: the `wia_file_head`/`wia_disc`/`wia_part`/`wia_raw_data`/`wia_group` layout, chunk
sizing (WIA ≥2 MiB; RVZ 32 KiB–2 MiB powers of two), the group `data_off4`/`data_size`
encoding (RVZ puts the compression-method flag in the size MSB), the packing/PRNG scheme,
and the Wii "0x8000→0x7C00 stored, hashes removed, exceptions listed" model.

**The two real obstacles are unchanged, and both are policy calls:**
1. **Zstandard.** Real RVZ is almost all zstd. Every codec in DiscForge is hand-rolled
   clean-room; zstd from scratch (FSE + Huffman + sequences) is an XL job. The pragmatic path
   is to vendor `ZstdSharp.Port` (pure-managed, MIT) — a conscious departure from the
   zero-dependency convention. WIA's older codecs (bzip2, LZMA, LZMA2) are *not* blockers
   (LZMA1 is already in-repo), so a **WIA-first / RVZ-NONE-and-LZMA-first** milestone can
   prove the whole group/junk/hash pipeline with *no new codec dependency at all*, deferring
   the zstd decision.
2. **Wii re-encryption** (the shared engine of §2) — the common-key policy call.

**Milestones, re-sequenced.** (a) GameCube, NONE/LZMA groups, junk PRNG — proves the
machinery, zero new dependencies, zero policy calls. (b) + zstd (vendor decision) for real
RVZ files. (c) + the shared Wii engine (common-key decision). Each milestone is independently
useful and independently gated.

**Validation.** DolphinTool / wit as black-box converters producing `.rvz`↔ISO oracle pairs
on discs we legally hold; assert byte-exact ISO out. Needs real tooling/discs — the classic
"can't prove without an oracle" situation, so it stays behind the oracle.

## 4. WOZ (Apple II) — the textbook clean-room fit

**What it is.** The gold-standard Apple II archival format (Applesauce / Library of Congress),
which captures a floppy's exact *bitstream* — including copy protection — **without defeating
it**. That phrasing is almost DiscForge's mission statement. It preserves weak/fake bits,
cross-track synchronization, and quarter-track alignment that ordinary copies destroy.

**What it takes.** A clean chunk parser: 12-byte header (`WOZ2` + CRC32), the 60-byte INFO
chunk (disk type, bit timing, flags), the 160-byte TMAP (quarter-track → track index), and
TRKS (512-byte-aligned bitstreams, MSB-first, with a per-track bit count). Reading and
identifying WOZ is an easy M; a Logic-State-Sequencer to turn the bitstream into 5.25"
nibbles enables `woz-info`, sector extraction, and lossy `WOZ→DSK/NIB` conversion. It
dovetails with the existing HFS reader and Apple Partition Map, and the optional FLUX chunk
connects to the flux work in §5.

**Clean-room.** Perfect — you preserve the protection bitstream faithfully and never
circumvent anything; even the lossy DSK conversion just drops timing, it doesn't crack.
**Validation.** WOZ ships with a CRC32 over the file body (self-checking); parse round-trips
and a couple of public WOZ test images (the Applesauce project publishes them) close it. No
hardware, no policy call.

## 5. Flux import (SCP + KryoFlux stream) — finish what `flux pack` started

**What it is.** DiscForge already has a *phase-1* `flux pack` container for raw optical
RF/flux captures. The natural completion is **reading the real flux formats** the community
captures with: **SuperCard Pro `.scp`** and **KryoFlux stream**. SCP is a clean parse — `SCP`
magic, a 168-entry track offset table, per-track headers (`TRK`, index duration, bitcell
count, data offset), and 16-bit flux timing values at a 25 ns base with a documented overflow
convention. That turns DiscForge's flux support from "we can wrap a blob" into "we can read
what people actually capture," and pairs with WOZ's FLUX chunk.

**Clean-room / validation.** Pure preservation, no protection concerns; SCP carries a header
checksum and both formats have public specs and sample captures. Effort **M** to parse +
analyze (flux histogram, RPM, revolution count); decoding flux → MFM/GCR sectors is a larger
optional follow-on. No policy call.

## 6. Smaller, safe wins

- **CTDB (CUETools DB) verification** — complements the AccurateRip support already in-tree
  with the parity-based CTDB check that catches errors AccurateRip can miss. S–M; gated only
  on a rip with a published CTDB CRC as a fixture.
- **PC-98 D88 floppy family** — an entire major Japanese platform with zero coverage; D88 is a
  near-trivial header + track table, all public, no protection concerns. Ship D88 first (S–M),
  then NFD/FDI/HDM.
- **CHD read v1–v4 + more track/hunk types** — DiscForge reads/creates CHD v5 today; broadening
  to the older versions and non-2352 cases removes a documented "not supported" throw and helps
  older MAME/redump sets. M.
- **Amiga RDB / Apple APM partition maps** — small parser additions that round out the
  partition-table family (MBR/GPT/APA already ship).

## Clean-room & provability at a glance

| Feature | Defeats protection? | Decrypts content? | Provable offline? |
|---|---|---|---|
| NKit GameCube | No | No | Yes (self-CRC + fixture) |
| NKit / RVZ Wii | No (reconstructs original) | **Re-encrypts via common key — policy call** | Only vs a real oracle |
| RVZ GameCube | No | No | Yes (vs DolphinTool oracle) |
| WOZ | No (preserves protection) | No | Yes (self-CRC + public images) |
| Flux SCP/KryoFlux | No | No | Yes (self-checksum + samples) |
| CTDB / D88 / CHD / RDB | No | No | Yes |

Two features carry a **policy call**, and they are the same two questions the previous round
already surfaced: whether to **vendor a pure-managed zstd** (RVZ) and whether the **Wii
common-key round-trip re-encryption** counts as preservation (byte-exact restoration of a
disc's own original encryption) or as decryption of console content (which DiscForge does not
do). Both deserve a conscious yes/no from you before any Wii code is written; every GameCube
milestone and every other feature here is clear of both.

## Recommended sequence

1. **NKit → ISO (GameCube)** — best new-format value, fully self-contained, no policy call,
   and it builds the FST/gap/junk machinery the Wii path reuses.
2. **WOZ read + convert** — textbook clean-room fit, easy win, complements the Apple stack.
3. **Flux import (SCP first, then KryoFlux stream)** — completes the `flux pack` story with
   the formats people actually capture.
4. **RVZ/WIA GameCube milestone (WIA/LZMA first, no new dependency)** — proves the group/junk
   pipeline and lets the zstd decision wait.
5. **The shared Wii engine** (hash-tree rebuild + exception patch + AES re-encrypt + junk PRNG)
   — *after* a yes on the common-key policy call — which completes **both** NKit-Wii and
   RVZ-Wii at once.
6. Smaller wins as fillers: **CTDB verify**, **PC-98 D88**, **CHD v1–v4**, **RDB/APM**.

Everything above stays inside the clean-room boundary: identify / verify / preserve / convert,
reimplemented from public documentation, validated against an independent oracle — and nothing
here defeats protection. The only two items that touch encrypted console content (NKit-Wii,
RVZ-Wii) are explicitly held behind a policy decision rather than assumed.
