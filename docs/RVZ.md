# RVZ / WIA decode — assessment and status

## Update (2026-08-11): GameCube RVZ → ISO now ships (`rvz-decode`)

Both blockers below have moved. The zstd one is **solved**: DiscForge now has a clean-room,
zero-dependency `ZstdDecoder` (validated against 120+ reference streams), so RVZ's headline codec
is decoded natively. On top of it, `dforge rvz-decode <in.rvz> <out.iso>` reconstructs a
**GameCube** ISO — the full container walk (raw-data + group tables, group decompression, offset
math, the RVZ-packed data/junk unpack, reassembly), validated byte-exact in tests against
hand-built zstd RVZ containers (single-group is bit-exact; multi-group with a packed junk run is
data-exact). Two honest limits remain: the RVZ **junk** (Nintendo's LFG disc padding) is
**zero-filled** — output is data-exact and mountable but not Redump-bit-exact where the disc was
scrubbed (finishing the LFG needs a real RVZ+ISO fixture to validate, and DiscForge won't copy
Dolphin's GPL code); and **Wii** discs plus the rarer **bzip2/lzma/lzma2** group codecs are
declined with clear messages. The rest of this note is the original pre-2026-08-11 assessment.

## Update (2026-08-12): Wii RVZ structure now reads (no decryption); encrypted rebuild declined

A Wii `.rvz` can now be **understood** without any keys: `RvzDecoder.ReadWiiStructure` decodes only the
**unencrypted** raw-data prefix (disc header + the partition-group tables at 0x40000) and parses it with the
existing clean-room `WiiDisc`, so `rvz-info` lists a Wii disc's partitions (DATA / UPDATE / CHANNEL) and their
offsets. It never decodes a partition-data region, derives a title key, or decrypts anything.

Reconstructing an **encrypted Wii ISO** remains **declined by design** — not merely deferred. It would require
the console common key and AES re-encryption over protected content (plus rebuilding the encrypted hash tree),
which is outside this toolkit's clean-room, no-circumvention boundary. GameCube RVZ → ISO (`rvz-decode`) is
unaffected and fully supported.

## Update (2026-08-12): a gated, self-validating junk regenerator now exists

The RVZ "junk" (Nintendo's LFG disc padding) is still **zero-filled** in `rvz-decode` — that path
is unchanged and honest. But the junk PRNG itself now has a clean-room implementation
(`GcJunkGenerator`: lagged-Fibonacci, taps k=521/j=32, XOR) plus a **self-validating reconstructor**
(`GcJunkReconstructor`, surfaced as `gc-junk-fill`) that regenerates a GameCube *disc image's* own
surviving junk and only fills scrubbed regions if it matches byte-for-byte — declining otherwise, so
a wrong constant can never corrupt output. It is deliberately **not** wired into `rvz-decode` yet:
the generator is unconfirmed against a real disc, and RVZ's per-group packed-junk mapping needs the
same byte-exact proof first. Once a Redump/NKit oracle confirms the generator, routing RVZ junk runs
through it (instead of zero-fill) becomes a small, validated change. See
[docs/COMPLETION_PLAN.md](COMPLETION_PLAN.md).

---

RVZ (and its predecessor WIA) is Dolphin's compressed GameCube/Wii disc format.
DiscForge **identifies** an RVZ/WIA file and reads its metadata (format, version,
compression type, chunk size, ISO size, and the game id/name from the unencrypted
disc header), and now also parses the **disc-structure directory** — the partition
count, raw-data-region count, group ("chunk") count and compressor-data length —
which `rvz-info` surfaces (GameCube vs Wii layout, how many groups the body holds).
That directory is stored uncompressed, so it reads without any codec. See
`RvzReader.ReadInfo`. What is **not** shipped is full **RVZ → ISO decompression**,
and this note explains why, in the same "provably correct or declined" spirit as
`docs/ECM.md`.

## Why full decode is deferred

Two independent blockers, either of which alone would be enough:

**1. zstd is a maintainer policy decision, not just missing code.** RVZ's headline
codec is **zstd**; WIA/RVZ also use **bzip2**, **lzma**, and **lzma2**. LZMA is
already in-repo (the clean-room `ChdLzma`), and LZMA2 is a thin chunked framing over
it; bzip2 is small. **zstd is the one that matters** — real RVZ files are almost all
zstd — and DiscForge's convention is *zero NuGet dependencies with the package
sources cleared* (every codec in the tree is hand-rolled). So enabling zstd means a
deliberate choice between two options, and it should be made explicitly:

- **Vendor a managed zstd** (e.g. `ZstdSharp.Port`, MIT — pure-managed, no native
  deps) into the local package cache. Fastest path, but it's the first third-party
  package in the build and breaks the zero-dependency rule on purpose.
- **Clean-room reimplement zstd** (FSE + Huffman + sequence decode) in the `Chd/`
  codec style. Keeps the rule intact, but it's a large (XL) undertaking.

Either unblocks the common case; neither should be picked silently.

**2. The format is high-stakes and unvalidatable here.** RVZ is not a simple
block container. Decoding correctly means walking the disc structure, the raw-data
and partition-data descriptors, and a **group table** (with RVZ's per-group
"packing", where junk/padding is regenerated from a 68-byte seed via a **Lagged
Fibonacci PRNG**, f=xor, j=32, k=521), then — for Wii partitions — applying per-group
**exception lists** that rebuild the partition's hash tree and **AES-re-encrypt**
each sector so the image verifies. A one-field mistake anywhere in that machinery
(an exception offset, the LFSR seeding, the hash-tree layout) does not fail loudly;
it silently produces a corrupt disc image. Validating it needs a real `.rvz` + its
known-good ISO as an oracle (the way CHD is checked against chdman), cross-checked
against two encoders (Dolphin's `DolphinTool` and wit/wiimms) to guard encoder
quirks. None is available in this environment, and a self-built fixture only proves
the decoder agrees with itself — not with Dolphin.

A sensible first milestone once unblocked is **GameCube-only** decode (unencrypted
raw-data regions + junk regeneration, no partition hashes/AES) — the group/codec/LFSR
machinery, proven against a real GameCube RVZ, before taking on the Wii path.

A round-trip against a self-made fixture would therefore give false confidence, and
shipping an unvalidated disc-image decoder that can silently corrupt output is
exactly what this codebase's standard exists to prevent.

## What finishing it needs

1. A zstd (and ideally bzip2) decoder available to the build — either a NuGet
   package once the project builds with network access, or a clean-room in-repo
   implementation (zstd is a substantial undertaking: FSE + Huffman + sequence
   decoding).
2. A real `.rvz` (or `.wia`) file plus its reference ISO, used as an oracle:
   `decode(reference.rvz)` must equal the reference ISO byte-for-byte. The LZMA
   path can reuse `ChdLzma`; only zstd/bzip2 are new codec work.
3. Wii-partition hash reconstruction (the exception lists) — only exercisable, and
   only worth trusting, against that reference pair.

## Summary

| Item | Status |
|------|--------|
| RVZ/WIA identify + metadata (format, compression, chunk size, ISO size, game id/name) | **Shipped** (`RvzReader.ReadInfo`) |
| RVZ/WIA disc-structure summary (partition / raw-data / group counts, GameCube vs Wii) | **Shipped** — parsed from the uncompressed disc directory, shown by `rvz-info` |
| RVZ → ISO decompression | **Deferred** on two blockers: (1) zstd is a maintainer policy call (vendor a managed package vs clean-room reimplement); (2) a real `.rvz`+ISO oracle is needed to validate the group/junk/exception machinery byte-for-byte. GameCube-only is the sensible first milestone. |
