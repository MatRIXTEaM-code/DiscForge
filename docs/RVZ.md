# RVZ / WIA decode — assessment and status

RVZ (and its predecessor WIA) is Dolphin's compressed GameCube/Wii disc format.
DiscForge already **identifies** an RVZ/WIA file and reads its metadata (format,
version, compression type, chunk size, ISO size, and the game id/name from the
unencrypted disc header) — see `RvzReader.ReadInfo` (task #95). What is **not**
shipped is full **RVZ → ISO decompression**, and this note explains why, in the
same "provably correct or declined" spirit as `docs/ECM.md`.

## Why full decode is deferred

Two independent blockers, either of which alone would be enough:

**1. The compression codecs aren't available in this build.** RVZ's headline
codec is **zstd**; WIA/RVZ also use **bzip2**, **lzma**, and **lzma2**. .NET 8
ships none of zstd/bzip2/lzma2, and this project builds **offline** (the NuGet
sources are cleared), so a package can't be added here. The only RVZ codec
DiscForge could decode today is LZMA, via the existing clean-room `ChdLzma`
decoder — but a real RVZ is almost always zstd, so an LZMA-only decoder would
decode almost nothing in practice.

**2. The format is high-stakes and unvalidatable here.** RVZ is not a simple
block container. Decoding correctly means walking the disc structure, the raw-data
and partition-data descriptors, and a **group table**, then — for Wii partitions —
applying per-group **exception lists** that reconstruct the partition's encryption
hashes so the rebuilt image verifies. A one-field mistake in that machinery does
not fail loudly; it silently produces a corrupt disc image. Validating it needs a
real `.rvz` + its known-good ISO as an oracle (the same way CHD is checked against
chdman). None is available in this environment, and a self-built fixture only
proves the decoder agrees with itself — not with Dolphin.

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
| RVZ/WIA identify + metadata (format, compression, chunk size, ISO size, game id/name) | **Shipped** (`RvzReader.ReadInfo`, task #95) |
| RVZ → ISO decompression | **Deferred** — needs zstd/bzip2 codecs (unavailable in the offline build) **and** a real `.rvz`+ISO oracle to validate the group/exception-list machinery byte-for-byte |
