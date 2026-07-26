# CHD v5 compressed hunk-map — reverse-engineering notes

> **STATUS: SOLVED and shipped.** The compressed hunk map is fully decoded and
> integrated — see `ChdMap` in `src/DiscForge.Core/Chd/ChdMap.cs`, driven by
> `ChdExtractor`. It resolves every hunk type including SELF and PARENT, and
> self-verifies against the map's own CRC-16. Validated against chdman-produced CD,
> hard-disk and parent/child images (each map's CRC-16) and by byte-identical CD
> extraction. The final piece — the tree encoding — was pinned with the maintainer's
> approval to consult the *public CHD map format* (the documented bitstream layout of
> `import_tree_rle` / the map decode), then re-implemented independently and confirmed
> by the CRC oracle. The historical reverse-engineering notes below are kept for
> context; the **Resolution** section at the end is the authoritative description.

These are DiscForge's own findings on the layout of the CHD v5 **compressed
hunk map**. The bulk were derived by observing real `chdman`-produced files and
diffing against ground truth; the last detail (the type-tree encoding) was
finished from the public format description and verified against the CRC oracle.

## Why this matters

`ChdExtractor` today walks the hunk stream by **probing each hunk's codec** and
falls back to an uncompressed (NONE) hunk when the probe fails, with the CHD's
stored SHA-1 arbitrating the result. That handles every hunk type `chdman`
normally emits for a CD image — the four base codecs and NONE — **without**
reading the map at all. The only hunk kinds it can't resolve are **SELF** and
**PARENT** references (a hunk that says "I'm identical to hunk N"), because those
carry no codec bytes to probe — they only exist in the map. Decoding the map is
what would close that last gap.

## Header (at the map offset, big-endian)

| Offset | Size | Field |
|-------:|-----:|-------|
| 0 | 4 | length in bytes of the compressed map that follows the header |
| 4 | 6 | first-hunk file offset (48-bit) |
| 10 | 2 | CRC-16 of the *decompressed* map (CRC-16/CCITT-FALSE, poly 0x1021, init 0xFFFF, MSB-first) |
| 12 | 1 | `lengthbits` — bit-width of a compressed hunk's length field |
| 13 | 1 | `selfbits` — bit-width of a SELF reference |
| 14 | 1 | `parentbits` — bit-width of a PARENT reference |
| 15 | 1 | reserved |
| 16 | … | compressed map bitstream (MSB-first) |

Observed: `lengthbits` = 10 for the zlib/LZMA CD samples (hunk 19 584 bytes) and
14 for the FLAC sample; `selfbits`/`parentbits` = 0 when no such hunks exist.

## Compressed map bitstream (MSB-first)

The bitstream is, in order:

1. A **16-symbol canonical Huffman tree**, RLE-encoded (see *Open item* below).
   The 16 symbols are the compression-type codes: 0–3 = base codecs, 4 = NONE,
   5 = SELF, 6 = PARENT, 7 = RLE-small, 8 = RLE-large.
2. The **per-hunk compression types**, Huffman-decoded with that tree, with two
   run-length escapes:
   - symbol 7 (RLE-small): repeat previous type `2 + decode()` more times;
   - symbol 8 (RLE-large): repeat previous type `2 + 16 + (decode() << 4) + decode()` more times.
3. The **per-hunk data**, walked in hunk order with a running offset that starts
   at the header's first-hunk offset:
   - type 0–3: read `lengthbits` → hunk length; offset += length; read 16-bit CRC.
   - type 4 (NONE): length = `hunkbytes`; offset += length; read 16-bit CRC.
   - type 5 (SELF): read `selfbits` → the referenced hunk offset.
   - type 6 (PARENT): read `parentbits` → the referenced parent offset.

### The decompressed rawmap (what the CRC covers)

12 bytes per hunk, big-endian: `[type:1][length:3][offset:6][crc:2]`. Reconstructing
this from a decode and running CRC-16/CCITT-FALSE over it reproduces the header's
stored CRC — a **ground-truth-free oracle** for a full map decode (verified: the
`test-cdzl` rawmap rebuilt from its known fields gives 0x8DE6, matching the header).

### Validation

Against `test-cdzl.chd` (150 hunks, all type 0): the length/offset section begins
at bit 55 of the bitstream and reproduces **all 150** ground-truth hunk lengths and
accumulated offsets exactly. Against `test-mixed.chd` (40 hunks, types 0/2/4 — made
by `make-mixed-chd.ps1`): the section begins at bit 98 and reproduces **all 40**.
Together these confirm item (3), the header widths, the rawmap layout and the CRC
algorithm. Only the tree bit-format (item 1) and the exact type-RLE constants
(item 2) remain.

## Open item — the tree bit-format and type-RLE constants

Two coupled low-level details remain, and they must be exactly right together (a
wrong tree mis-decodes the types, so neither can be validated without the other):

1. **The 16-symbol tree encoding.** The bitstream opens with a compact encoding of
   the main tree's code lengths. The mechanism is MAME's *huffman-of-lengths*: a
   small length-tree is read first, then used to decode the 16 main code-lengths
   (with a run escape). The precise small-tree field format could not be aligned to
   the sample bytes — the trial read overran the known tree/length boundary (bit 55
   for `test-cdzl`, bit 98 for `test-mixed`), so a field width, the code count, the
   canonical-code assignment direction, or the run escape is still off by a detail.
2. **The type-RLE constants.** The type stream uses two run escapes (small/large).
   The run-length arithmetic assumed here does not reproduce the multi-symbol type
   sequence of `test-mixed`, so those constants need correcting alongside (1).

Everything downstream of these two — types → per-hunk length/offset/CRC → rawmap →
CRC check — is implemented and validated, so once (1) and (2) are correct the
CRC oracle will confirm the whole decode immediately.

**What would unblock it:** a sample whose map also contains SELF/PARENT hunks
(`selfbits`/`parentbits` > 0) would add independent constraints; `make-mixed-chd.ps1`
aimed for this via long identical runs but `chdman` deduplicated them within a codec
rather than emitting SELF refs, so a larger multi-track source (or a real game image)
is the better source. Until then `ChdExtractor` resolves every non-SELF/PARENT hunk
via the SHA-1-guarded codec probe, so no correctness is lost — only the ability to
resolve SELF/PARENT hunks, which fail safe (declined) rather than corrupt.

## Progress update — an on-demand oracle and confirmed rawmap layout

Two things changed since the notes above, both still pure-observation (no third-party
source):

**SELF hunks are now producible on demand.** Building a raw disk image whose data
region is *copied* (one megabyte of random bytes, then the very same megabyte again)
and running `chdman createhd -c zlib,lzma,huff` makes `chdman` emit SELF references
for the duplicated hunks — the sample header reports `selfbits = 10`, `parentbits = 0`.
So the missing SELF/PARENT sample no longer depends on finding a real game image; a
controlled generator produces trees of any chosen shape (1-symbol, 2-symbol, mixed +
SELF), which is exactly what differential bit-analysis of the tree needs.

**The rawmap entry layout and the hunk-CRC are now pinned against the CRC oracle.**
Rebuilding an all-NONE sample's rawmap and matching the header CRC-16 confirms, exactly:

- the 12-byte entry is `[type:1][length:3][offset:6][crc:2]`, big-endian;
- for a **NONE** hunk the stored **length field is `hunkbytes`** (not 0), and the
  offset accumulates by `hunkbytes`, starting at the header's first-hunk offset;
- the per-hunk **`crc` field is CRC-16/CCITT-FALSE of the _raw_ (decompressed) hunk
  data** — i.e. it is *computable* from hunk contents, not only readable from the map.

That last point matters: a full map decode can *derive* every hunk's CRC from its
decompressed bytes and check the whole reconstructed rawmap against the header CRC —
a complete, ground-truth-free validator for a candidate tree/type-stream decode.

**Differential tree data.** Two single-symbol samples that differ only in *which*
type is used (all-NONE vs all-zlib) keep an identical ~40-bit middle region in the
tree bitstream (the length-histogram is the same: one symbol of length 1, fifteen of
length 0) and differ only in a short early region and the type-stream tail — so the
tree encoding is `[length-value tree, histogram-dependent][16 code-lengths, position-
dependent]`, consistent with a huffman-of-lengths. Cracking the exact field widths of
the length-value tree is the remaining step; the generator + CRC oracle make it a
finite search rather than a hunt for a sample.

Harness (not shipped in the product; kept for the next session): a Python CHD-v5 map
parser + MSB-first bit reader + CRC-16 oracle, plus the `chdman` recipes above.

## Resolution — the authoritative decode

The compressed map at the map offset is: a 16-byte header (as documented above),
then a bitstream (MSB-first) of:

1. **The type tree.** A 16-code, max-8-bit Huffman tree read by the *run-length*
   scheme (`import_tree_rle`), **not** the huffman-of-lengths that earlier notes
   chased. Field width is 4 bits (3 if maxbits<8, 5 if ≥16). Reading a value: a
   non-1 value is a literal code length; a `1` is an escape — a following `1` means
   a single length-1 code, otherwise the next field is a length repeated
   `read()+3` times. Canonical codes are then assigned **longest-to-shortest**
   (code lengths 32→1; within a length, by symbol index) — this direction was the
   crux the earlier attempts had backwards.

2. **The per-hunk types**, one `decode_one` per hunk, with two run escapes on the
   *previous* type: symbol 7 (RLE-small) repeats it `2 + decode_one()` more times;
   symbol 8 (RLE-large) `2 + 16 + (decode_one() << 4) + decode_one()` more.

3. **A second pass** over the hunks reading each one's data reference: codecs 0–3
   read `lengthbits` then a 16-bit CRC (offset accumulates by length); NONE reads
   only a 16-bit CRC (offset accumulates by hunkbytes); SELF reads `selfbits` (a
   hunk number); PARENT reads `parentbits`. Pseudo-types resolve without reading:
   SELF_0/SELF_1 reuse/`++` the last self hunk; PARENT_SELF/PARENT_0/PARENT_1
   compute from `hunknum*hunkbytes/unitbytes` or the last parent.

The 16 type codes: 0–3 codecs, 4 NONE, 5 SELF, 6 PARENT, 7 RLE-small, 8 RLE-large,
9 SELF_0, 10 SELF_1, 11 PARENT_SELF, 12 PARENT_0, 13 PARENT_1.

Each hunk becomes a 12-byte rawmap entry `[type:1][length:3][offset:6][crc:2]` (BE);
CRC-16/CCITT-FALSE over all of them must equal the header CRC — `ChdMap.Decode`
checks this and refuses a decode that does not match, so extraction is either
provably correct or declined.

**SELF support in extraction.** `ChdExtractor` now reads the map: a SELF hunk copies
the already-produced logical bytes of the hunk it references (always an earlier one).
PARENT hunks are declined with a clear message (they need the parent CHD file). The
CHD's stored SHA-1 remains the final proof of a byte-exact reconstruction.
