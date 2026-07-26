# UDF support

UDF (ECMA-167 / OSTA) is the filesystem DVD-Video and Blu-ray use, and which ISO
9660 cannot describe. `dforge ls` and `dforge extract-files` read it, choosing the
filesystem automatically; `dforge create-udf` **writes** it.

```
dforge ls disc.cdi              # ISO 9660 or UDF, whichever is there
dforge ls disc.cdi --udf        # force UDF (a "UDF bridge" disc has both)
dforge ls disc.cdi --iso        # force ISO 9660
dforge extract-files disc.cdi out/
dforge create-udf folder out.udf --volume MYDVD   # build a UDF 1.02 image
```

## Authoring (writing) — `UdfBuilder` / `dforge create-udf`

`UdfBuilder` writes the plain, universally-mounted UDF 1.02 shape: a single
Type 1 (physical) partition, 2048-byte blocks, a File Set with a directory tree
of File Entries and File Identifier Descriptors. That baseline is exactly what
DVD-Video mandates, which is why it comes first — authoring a shrunk or rebuilt
DVD back to a burnable image needs a UDF writer, not just a reader.

Every descriptor carries a real ECMA-167 tag: a CRC-16/CCITT over its body and
the one-byte tag checksum, both of which the reader and real mounts validate.
Builds are deterministic (fixed timestamp), so the same tree always yields
byte-identical output — which is what makes the round-trip test meaningful.

The physical layout: reserved system area (0–15), Volume Recognition Sequence
(16–18: BEA01/NSR02/TEA01), main + reserve Volume Descriptor Sequences (32–47,
48–63), Logical Volume Integrity Descriptor (64), Anchor at 256 (mirrored at the
last sector), then the partition from 272 — File Set Descriptor, File Entries,
directory data and file data.

**Validated by round-trip**, the same way the reader is: `UdfBuilderTests` builds
a tree (nested directories, empty files, multi-block and block-aligned files),
reads it back with `UdfReader`, and asserts names, sizes and file *bytes* match.
`dforge create-udf` then `dforge browse` on the result confirms the same
end-to-end.

Scope of the writer, honestly: UDF 1.02 only (no 2.50 metadata partition — the
Blu-ray write path is later), short_ad extents, ASCII/Latin-1 names, 2048-byte
blocks. **Streamed writing is done:** `UdfBuilder.BuildToStream` writes to a
seekable stream, building the bounded metadata region (volume descriptors, File
Entries, directory data) in memory and streaming only the bulk file content from
its source, so a full DVD-9 rebuild past 2 GB is authored without holding it all in
memory. `dforge create-udf` and the GUI use this path; the in-memory `Build`
(byte[]) is kept for the round-trip tests and small images and still guards the
2 GB limit — both produce byte-identical output. The IFO side of a reauthored VIDEO_TS is
now covered by `IfoWriter` (see docs/DVD_VIDEO_SHRINK.md): the structural VMG/VTS
IFOs round-trip, though the navigation tables a player walks remain the dvdauthor
step.

### GUI — the **UDF Image** tile

The retro launcher's **UDF Image** tile (`UdfCreateView`) is a button over the
same Core code: choose a folder, set a volume label (the folder's own name is
offered as the default), and write a `.udf`. It surfaces `UdfBuilder`'s warnings
and its ~2 GB ceiling error verbatim rather than hiding them.

## The chain

UDF is a chain of pointers, each descriptor naming the next:

```
Anchor VDP (sector 256, mirrored at N-256 and N)
  -> Main Volume Descriptor Sequence
       Partition Descriptor      -> where the partition starts
       Logical Volume Descriptor -> block size + where the File Set is
  -> File Set Descriptor         -> root directory's ICB
  -> File Entry / Extended File Entry -> type, size, extents
  -> directory data = File Identifier Descriptors (name + child ICB)
```

## Gotchas, all confirmed against a real volume

- **Addresses in ICBs are logical blocks within a partition.** The physical sector
  is `partitionStart + logicalBlock`. Forget it and you read garbage.
- **The ICB flags' low 3 bits pick the allocation descriptor type**: 0 = short_ad,
  1 = long_ad, **3 = the data is embedded in the File Entry itself**. Tiny files
  often take the embedded path, and a reader that only handles extents silently
  returns nothing for them.
- **An extent's length has its top 2 bits as a type field** — mask with
  `0x3FFFFFFF`. A non-zero type means sparse/unrecorded, which reads as zeros.
- **FID names are OSTA compressed Unicode**: the first byte is a compression ID
  (8 = latin-1, 16 = UTF-16BE), *not* part of the name.
- **A FID is `38 + L_IU + L_FI` bytes, padded to 4.** Get the padding wrong and
  the whole directory shreds.
- **File Entry (261) and Extended File Entry (266) differ**: lengths at 0xA8/0xAC
  vs 0xD0/0xD4, headers 0xB0 vs 0xD8.
- **A dstring keeps its length in the LAST byte** of the field.

## Validated

There is no `isoinfo`-grade tool for UDF, so the oracle is a real volume with
known contents: `docs/reference/udf_read.py` builds one with `genisoimage -udf`,
reads it back, and compares. `tests/fixtures/udf/udf_test.iso` is that volume,
committed, and the C# tests assert the exact tree, sizes and file bytes.

## Scope — what is NOT supported

Honest limits:

- **UDF 2.50 metadata partition — supported.** Blu-ray uses it. The reader
  parses the Logical Volume Descriptor's partition maps, detects a Type 2
  metadata partition map, reads the Metadata File's extents, and resolves the
  File Set, directory tree, and file-content extents through them. So **Blu-ray
  discs are browsable and their files extractable.** Extraction routes every
  extent through the same block resolver, so metadata-partition and physical
  extents both resolve correctly. (The committed fixture is a Type 1 volume; the
  Type 1 read/extract path is regression-tested byte-for-byte after this change.)
- **Virtual and sparable partitions** (packet-written CD-RW/DVD-RW) — not resolved.
- **Extended File Entries (tag 266)** are implemented but untested: genisoimage
  writes plain File Entries, so the fixture doesn't cover them.
- **long_ad (type 1) and embedded (type 3)** descriptors are implemented but not
  exercised by the fixture, which uses short_ad throughout.
- Only 2048-byte logical blocks.
- **Authoring: UDF 1.02 write is implemented** (see above). UDF 2.50 metadata-
  partition *writing* (Blu-ray authoring) is not — reading it is.
