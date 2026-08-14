# DiscForge — Feature Feasibility Research

*Prepared 2026-08-09. Four candidate features investigated against the codebase, the
public format specs, and DiscForge's two hard constraints: **clean-room** (reimplement
from public documentation only — never copy GPL source) and **offline pure-managed
.NET 8 build** (NuGet sources are cleared; every codec in the tree today is hand-rolled
clean-room, not vendored).*

## The one-line verdict

Do them in this order: **ECM first** (a near-free win — the hard part already exists),
**PS1 MDEC video next** (high value, half-built, fits the PlayStation work in flight),
then **Xbox 360 GOD** as the standout new format. **RVZ/WIA** is the marquee feature but
also the largest, riskiest lift and the only one that collides with the zero-dependency
build rule — worth doing, but with eyes open and last.

## Scorecard

| Feature | Feasibility | Effort | Clean-room | Biggest risk | How much already exists |
|---|---|---|---|---|---|
| **ECM decode/encode** | High | S–M | Safe | Silent container-framing bug that round-trips | The hard 90% (EDC + Reed-Solomon P/Q) is built, tested, in production |
| **PS1 MDEC video** | High | M (v2) → L (v3) | Safe | Silent bit-order / DC-prediction bugs (plausible-but-wrong image) | Demux + back half of pipeline (IDCT, dequant, colour) built & tested |
| **Xbox 360 GOD** | High | M | Safe (payload is plaintext) | Chunk/hash-table geometry off-by-one | XDVDFS reader already knows the XGD2/XGD3 offsets |
| **RVZ/WIA → ISO** | Medium | L (XL if no zstd dep) | Safe with discipline | Silent corruption in the Wii hash/re-encrypt/junk-regen path | `rvz-info` metadata parse only; decode is a stub |

## 1. ECM decode/encode — the quick win

**What it is.** The classic `.ecm` sector-reduction format (Neill Corlett's ecm/unecm):
strips the regenerable bytes from each CD sector (sync, header, EDC, ECC) so a `.bin`
shrinks, and rebuilds them exactly on decode.

**Why it's nearly free.** The correctness-critical machinery — the ECMA-130 EDC (32-bit
bit-reversed CRC) and the Reed-Solomon P/Q ECC — is **already implemented, independently
verified, and in production** in `src/DiscForge.Core/Raw/EdcEcc.cs` (`FillMode1`,
`FillMode2Form1`, and independent `VerifyMode1`/`VerifyMode2Form1` that evaluate syndromes
rather than re-run the encoder, so they're a genuine oracle). ECM is essentially container
plumbing over machinery DiscForge owns: a varint reader/writer, sector classification (the
existing verifiers *are* the classifier), and one small new `FillMode2Form2` helper
(EDC-only, ~10 lines). The format is pinned by two independent public prose specs (nocash
PSXSPX and qeedquan's `format.txt`) that agree on every field — and one long-open question,
"is the Mode-1 address stored or re-derived?", is now answered: **stored**.

**Validation.** Round-trip (`encode` then `decode` == original, byte-for-byte) plus feeding
every decoder-emitted sector back through the independent `VerifyMode1/Form1` syndrome check,
plus **one** real external `.ecm`+`.bin` fixture to close the container-framing question.
Using a third-party-produced `.ecm` purely as a binary test vector is data, not code — it
stays clean-room.

**Recommendation.** Do this first. It closes a documented deferred item (`docs/ECM.md`) at
S–M effort with no new hard algorithms, and it's a natural companion to the existing bin/cue
tooling. The only residual risk (a shared-wrong-convention that round-trips silently) is
retired by the single external fixture.

## 2. PS1 MDEC video decode — high value, half-built, on-theme

**What it is.** Decode PlayStation `.STR` full-motion video (the MDEC bitstream) into actual
frames — the missing pixel-decode half of the FMV pipeline.

**What already exists.** `StrDemuxer` fully demuxes STR into the MDEC frame bitstream + XA
audio and is validated. `Mdec.cs` already has the **back half** of the decode — the standard
PSX quantization table, zig-zag scan, `Dequantize`, an 8×8 IDCT, and YCbCr→RGB — all unit
tested (7 tests). The gap is precise and self-contained: a bit reader, the DC/AC
variable-length-code decoder (the AC table is the **standard MPEG-1 intra table** from
ISO/IEC 11172-2 — publicly documented, not a jPSXdec invention, which is what makes it cleanly
reimplementable), and macroblock assembly (6 blocks → 16×16, 4:2:0) that feeds the stages
already present.

**Scope options.** Phase it: **(a) v2 single-frame → PNG** as `dforge str-frames` is the
smallest provable increment (**M**); **(b)** all frames → PNG sequence; **(c)** full clip
with v3 differential-DC support and muxed XA audio (**L**). Version 3's differential DC
prediction (separate luma/chroma VLC tables, running per-component DC state) is the main extra
work in the L tier.

**Validation.** Use jPSXdec as a **black-box** PNG oracle (run it as an external tool; never
read its GPL source) on a homebrew or self-generated `.str` we can legally keep as a fixture,
asserting per-pixel match within a small tolerance (IDCT rounding makes byte-exact
unrealistic). Store the bitstream + expected RGB as the fixture, never a game asset.

**Recommendation.** Strong second. High preservation/enthusiast value, thematically continues
the PlayStation work we've been doing, and half the pipeline is already done and tested. The
risk is the classic silent-image-corruption failure mode (bit order is little-endian 16-bit
words but MSB-first within each word; v3 DC sign extension) — fully retired by the oracle test,
so we hold to "provably correct or declined."

## 3. Xbox 360 GOD — the best new format to add

**What it is.** "Games on Demand" — the chunked container (`Data0000/Data0001…`) that is the
dominant preservation and backup format for the digital 360 library. Convert GOD → XISO/ISO.

**Why it fits.** DiscForge already parses XDVDFS *and already carries the XGD2 (`0x1FB20`) /
XGD3 (`0x4100`) base offsets* — it just can't open the GOD wrapper around them. The game
payload inside GOD is **plaintext XDVDFS**; GOD's crypto is integrity hashing plus an RSA
signature, **not content encryption**, so GOD↔ISO works fully offline with no console keys.
The work is purely structural: parse the CON header + Title/Media-ID directory, walk the
block/sub-part/part geometry, skip the interleaved SHA-1 hash tables, and hand the reassembled
XDVDFS stream to the existing reader / `create-xiso`. Clean-room-safe as long as we only
de-chunk and never validate or forge the signature.

**Recommendation.** Best value-per-effort among genuinely *new* formats (**M**). Two strong
runners-up if you'd rather go pure-preservation or fill a platform gap:
- **Apple II WOZ** (+ 2MG/DSK/NIB) — the gold-standard Apple II archival format with an openly
  published spec (Applesauce / Library of Congress). Captures the exact bitstream *including*
  copy protection **without defeating it** — a textbook fit for DiscForge's philosophy.
  Dovetails with the existing HFS reader + Apple Partition Map. Effort **M**.
- **PC-98 floppy family** (D88 + NFD + FDI + HDM) — an entire major Japanese platform with zero
  current coverage; all public byte-level specs, no protection concerns, D88 is a near-trivial
  header + track-table parse. Ship D88 first. Effort **S–M**.

(Skipped as already-covered or out-of-bounds: Sega CD, PC-FX, 3DO, PC Engine CD, Neo Geo CD are
already touched; Wii U WUD/WUX, Amiga IPF, and anything PS3/Vita/PSN/CSS/AACS are excluded —
they need content decryption or a license-encumbered library.)

## 4. RVZ/WIA → ISO — the marquee feature, and the hardest

**What it is.** Full decode of Dolphin's RVZ format (and its parent WIA) back to a byte-exact
GameCube/Wii ISO — the single most broadly useful preservation gap.

**What exists.** `src/DiscForge.Core/GameCube/RvzReader.cs` parses the file/disc headers and
game id/title for `rvz-info`; `Decode()` is a deliberate stub that throws. The partition/raw/
group tables, the compressed group data, the RVZ "junk" regeneration (a Lagged-Fibonacci PRNG),
and — for Wii — the per-sector hash-tree rebuild + exception patching + **AES re-encryption**
are all unbuilt.

**The two real obstacles.**
1. **It collides with the build rule.** Real RVZ files are almost all **Zstandard**-compressed.
   Every codec in DiscForge today is hand-rolled clean-room (deflate, LZMA1, FLAC live in
   `Chd/`), and the NuGet sources are cleared. zstd from scratch (FSE + Huffman + sequence
   decode) is an XL job on its own. The pragmatic path is to **vendor `ZstdSharp.Port`** (a
   pure-managed, MIT-licensed port) into the local package cache — but that's a deliberate
   departure from the project's zero-dependency convention and should be a conscious decision.
   bzip2 (SharpCompress/SharpZipLib, MIT) and LZMA2 (thin framing over the in-repo LZMA1) are
   not blockers.
2. **The Wii path is unforgiving.** Hash-tree rebuild + exception offset mapping + AES-CBC
   re-encryption + LFSR junk regeneration each fail *silently* — a single off-by-one yields a
   plausible-but-wrong ISO that no structural self-check catches. It's only provable against a
   real `.rvz`+ISO oracle (Dolphin's `DolphinTool` and/or wit/wiimms converting known ISOs both
   ways), which requires real discs and tooling we don't have in this environment.

**Milestones.** GameCube-only decode (no encryption, simpler regions) is a reasonable **M**
first milestone that proves the group/codec/junk machinery; full Wii support pushes to **L**.

**Recommendation.** Highest ceiling, but do it last and deliberately. It's the one feature that
needs a policy call (vendor zstd vs. reimplement) and the one whose correctness can't be proven
without a hardware/tooling oracle. Ship the GameCube milestone first behind the oracle, then
Wii.

## Suggested sequence

1. **ECM** — fast, low-risk, closes a deferred item. Warm-up win.
2. **MDEC v2 → PNG** (`str-frames`) — high value, half-built, continues the PS1 thread.
3. **Xbox 360 GOD** — best strategic new-format add; reuses the XDVDFS reader.
4. **RVZ/WIA** — GameCube milestone first (needs the zstd-dependency decision + an oracle),
   then the Wii path.

Every one of these stays inside the clean-room boundary: identify / verify / preserve /
convert, reimplemented from public documentation, validated against an independent oracle — and
none of them decrypts protected content or defeats console security.
