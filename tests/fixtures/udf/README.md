# UDF test fixture

`udf_test.iso` is a real UDF volume produced by `genisoimage -udf -V UDFTEST`.

Unlike ISO 9660 there is no `isoinfo`-grade tool to check a UDF listing against,
so the honest substitute is: build a volume with known contents, commit it, and
assert the reader returns exactly those contents.

| Path                     | Bytes | Content                              |
|--------------------------|-------|--------------------------------------|
| `/readme.txt`            | 16    | `hello udf world\n`                   |
| `/data.bin`              | 5000  | `(i*13+7) & 0xFF` for i in 0..4999    |
| `/deep/inner.txt`        | 21    | `nested file contents\n`              |
| `/deep/deeper/tiny.txt`  | 1     | `x`                                   |

Structure: partition starts at sector 257, root ICB at logical block 2.

## What this fixture does NOT cover

Every file here uses **short_ad** allocation descriptors (ad type 0) — including
the 1-byte `tiny.txt`, which genisoimage still gives an extent rather than
embedding.

So these paths are implemented but **not exercised by this fixture**:

- **ad type 1 (long_ad)** — used by some mastering tools.
- **ad type 3 (embedded)** — a file's data stored inside its File Entry. Common
  for tiny files from other tools; a reader that only handles extents returns
  nothing for them.
- **Extended File Entry (tag 266)** — genisoimage writes plain File Entries
  (tag 261). UDF 2.x mastering tools commonly write EFEs, which put their
  lengths at different offsets (0xD0/0xD4 vs 0xA8/0xAC).
- **UDF 2.50 metadata partition** — used by Blu-ray. Not resolved at all.

Regenerate with `docs/reference/udf_read.py`, which builds an equivalent volume
and validates the algorithm end to end.
