# Dreamcast 1ST_READ.BIN scramble (the bin2boot transform)

On a Dreamcast **CD** (not a GD-ROM) the main binary `1ST_READ.BIN` is not loaded
into memory contiguously — the boot ROM scatter-loads it — so it is stored
"scrambled": its 32-byte slices are permuted by a seeded shuffle. This is the
transform the `bin2boot` tool applies. DiscForge implements it both ways:

```
dforge dc-descramble 1ST_READ.BIN prog.bin   # scattered -> plain binary (to inspect/extract)
dforge dc-scramble   prog.bin 1ST_READ.BIN   # plain -> the scattered form the boot ROM expects
```

## What it is — and isn't

It is a plain, documented **byte-slice permutation**, not encryption and not copy
protection: scrambling only reorders 32-byte slices, so the byte histogram is
unchanged. DiscForge implements the **transform only**. It does **not** build a
self-boot (MIL-CD) disc — that bootstrap trick is a console-security matter that
stays outside DiscForge's clean-room rule (docs/COMPARISON.md §13). What this adds
is the ability to get a Dreamcast image's real main binary out for inspection, and
to repack a modified one.

## The algorithm (clean-room, from public documentation)

```
seed = fileSize & 0xFFFF
rand():  seed = (seed*2109 + 9273) & 0x7FFF;  return (seed + 0xC000) & 0xFFFF
```

The file's length, rounded down to a multiple of 32, is split into chunks of
decreasing size (2 MB, 1 MB, … 32 bytes); each chunk's 32-byte slices are shuffled
by a Fisher-Yates pass driven by `rand()`. A tail of under 32 bytes is copied
straight through.

## Validated

Scramble and descramble are exact inverses — the same permutation applied in
opposite copy directions — so the guarantee the tests pin is **round-trip
identity** across sizes that exercise multiple chunk sizes and a sub-32-byte tail,
plus that a scramble is a true reordering (same multiset of bytes, different
order) and deterministic. Matching a specific real retail `1ST_READ.BIN` byte-for-
byte would confirm the direction labelling against hardware; the transform itself
follows the documented algorithm exactly.
