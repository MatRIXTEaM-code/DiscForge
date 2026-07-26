# GameCube / Wii / RVZ — scope and status

This is the honest scope note for DiscForge's Nintendo optical-disc support. Every
part here is derived only from the public GameCube/Wii disc-layout and WIA/RVZ
container descriptions; no third-party source (Dolphin, nod, wit, …) was copied.
As with the rest of the project, the rule is *provably correct or declined*: what
ships is validated by synthetic-fixture round trips, and what cannot be validated
cleanly here is deferred and named plainly.

## GameCube filesystem read — **shipped**

GameCube GCM/ISO images are **entirely unencrypted**, so reading them is fully
clean-room-safe and touches no copy protection. `GcmReader` (`src/DiscForge.Core/
GameCube/GcmReader.cs`) reads, all big-endian:

- **Boot header** (`boot.bin`, first 0x440 bytes): game code (0x00), maker code
  (0x04), disc id (0x06), version (0x07), the magic word `0xC2339F3D` at 0x1C that
  validates a GameCube disc, the game name (0x20), and the DOL / FST offsets and FST
  size (0x420/0x424/0x428).
- **FST**: 12-byte entries (flag; 24-bit name offset; file-offset-or-parent;
  length-or-next-index) followed by a string table. Entry 0 is the root and its
  length field is the total entry count. `GcmReader.Read` walks this into a full
  directory tree with paths, sizes and offsets, and `ExtractFile` writes any file's
  exact bytes back out.

CLI: `dforge gcm-info <image>` prints the header and file tree. Validated by a
synthetic-image round trip (build a header + FST + files, read the tree, extract
each file and compare bytes).

## Wii — **structure / partition-table only** (contents intentionally not read)

A Wii disc is **different from GameCube: its game-partition contents are
AES-encrypted** under a title key protected by the console's common key. DiscForge
reads **only the unencrypted structure** and stops at the protection boundary:

- **Volume header**: game code (0x00), the magic word `0x5D1C9EA3` at 0x18 that
  validates a Wii disc, and the game name (0x20).
- **Partition table** (at 0x40000): up to four partition groups, each giving a
  count and a table offset; each partition entry gives a data offset (stored `>> 2`)
  and a type (0 = DATA, 1 = UPDATE, 2 = CHANNEL, other = title-specific).

`WiiDisc` (`src/DiscForge.Core/GameCube/WiiDisc.cs`) reports the plaintext partition
table and **nothing inside a partition**. It does **not** decrypt partition data,
derive or use title keys, or read the encrypted partition FST — the reader never
even seeks to a partition's data offset. This is deliberately **not** a protection
tool: reading the plaintext partition *table* is fine; going inside an encrypted
partition would defeat console security and is out of scope. CLI: `dforge gcm-info`
prints the volume header and partition table when handed a Wii disc.

## RVZ / WIA — **identify + metadata shipped; full decode deferred**

RVZ and WIA share a container structure (RVZ adds zstd). `RvzReader`
(`src/DiscForge.Core/GameCube/RvzReader.cs`) parses the header for identification and
metadata only, all big-endian:

- the `WIA\x01` / `RVZ\x01` magic, version, and the disc-structure header:
  compression type (0 none, 1 purge, 2 bzip2, 3 lzma, 4 lzma2, 5 zstd), compression
  level, chunk size, the original ISO file size, and the **unencrypted 0x80-byte disc
  header** — which carries the game id and title, so a container can be identified
  without decompressing or decrypting anything.

CLI: `dforge rvz-info <image>` prints this metadata.

### Why full RVZ → ISO decompression is deferred (the honest reason)

Reconstructing the original ISO from an RVZ/WIA needs two things this build cannot
do cleanly:

1. **The compression codecs.** RVZ groups are zstd (and WIA uses bzip2/lzma); this
   cloud environment is **offline for NuGet** and .NET 8 ships **no built-in zstd or
   bzip2**, so the codecs simply are not available here. Shipping a half-wired
   decoder that cannot actually inflate a real group would fail the validated-or-
   declined bar.
2. **Wii encryption preservation.** For Wii images the container stores partition
   data with the encryption *removed for compression* and must **re-apply** the exact
   AES encryption and hash tables on rebuild to reproduce a byte-identical disc. That
   is squarely the console-security machinery this project stays out of.

`RvzReader.Decode` therefore throws a clear `GameCubeFormatException` rather than
pretend to decode. Finishing it, once unblocked, means: a zstd/bzip2 dependency (or a
managed implementation), the group/segment reader over the compressed table, and —
for Wii — the encryption-preservation step, plus a reference `.rvz`/`.iso` fixture to
validate the rebuild byte-for-byte.

## Summary

| Item | Status |
|------|--------|
| GameCube GCM/ISO filesystem read + extract | **Shipped** — unencrypted, round-trip validated |
| Wii volume header + partition table | **Shipped** — structure only; partition contents encrypted and intentionally not read |
| RVZ/WIA identify + metadata | **Shipped** — header parse, game id/name from the unencrypted disc header |
| Full RVZ/WIA → ISO decompression | **Deferred** — needs zstd/bzip2 (unavailable offline) + Wii encryption preservation |
