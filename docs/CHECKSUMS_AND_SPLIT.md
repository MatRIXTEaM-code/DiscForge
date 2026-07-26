# Image checksums and split/join (v1.3.0)

Two self-contained utilities, no hardware involved — the parity items from
ImgBurn's toolbox that make images portable and provably intact.

## Checksums

```
dforge checksum <file>                       # show all four digests
dforge checksum <file> --write               # also write <file>.sha256
dforge checksum <file> --write all           # .sha256 + .md5 + .sha1 + .sfv
dforge checksum <file> --verify              # check against an existing sidecar
```

One streaming pass computes **CRC-32, MD5, SHA-1 and SHA-256** together — a
4.7 GB image is read once, not four times. The values and sidecar formats
interoperate with the everyday tools:

- `.md5` / `.sha1` / `.sha256` are md5sum-family format (`<hex>  <name>`),
  verified here against `md5sum -c`, `sha1sum -c` and `sha256sum -c`.
- `.sfv` is standard SFV (`<name> <HEX8>`); CRC-32 matches zlib and
  DiscForge's own per-track CRCs.
- Digest values are tested against the published vectors (MD5/SHA of "abc",
  CRC-32 of "123456789").

`--verify` picks the strongest sidecar present (sha256 → sha1 → md5 → sfv)
and exits non-zero on mismatch, so it slots into scripts. MD5 and SHA-1 are
provided for identity and interchange with the wider imaging world, not as
security primitives.

## Split / join

```
dforge split <file> <size>        # sizes: bytes, 700m, 4g, or fat32
dforge join <first-part> [out]    # out defaults to the base name
```

Splitting produces `name.cdi.001`, `.002`, … plus `name.cdi.sfv` — a
manifest with a CRC-32 per part and the whole file's byte count and SHA-256
in a comment, all computed during the same single read. `fat32` is the
useful alias: 4 GiB − 1, the largest file FAT32 allows.

Joining finds the parts by counting up from `.001`, refuses to overwrite an
existing output, and — when the manifest is present — verifies every part's
CRC-32 while copying and the final SHA-256 at the end. The failure modes are
loud and specific:

- a corrupt part is named (`'name.cdi.002' fails its CRC-32 check`);
- a missing part is caught by count before any copying starts;
- a byte-count or SHA-256 mismatch on the result is an error, not a warning.

Because parts are plain slices, `cat name.cdi.0* > name.cdi` or
`copy /b` recovers the image without DiscForge — the manifest just makes the
verified path the easy path.

## Tests

`FilesTests.cs` (xunit) and the in-container harness cover: published digest
vectors, sidecar write→find round-trips and strongest-first selection,
split/join byte-identity, corrupt-part and missing-part detection,
manifest-less join (works, flagged unverified), and size parsing.
