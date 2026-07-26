# Browsing an image (disc → files)

The mirror of the ISO builder: answer "what is actually on this disc?" without
mounting or burning it.

```
dforge ls disc.cdi                        # list the filesystem
dforge ls disc.cdi --iso                  # force the 8.3 view
dforge extract-files disc.cdi out/        # pull the files out
```

## Pieces

- **`Core/Iso/IsoReader.cs`** (pure) — walks the volume descriptors, picks the
  best name source, recurses the tree, extracts files.
- **`Core/Cdi/CdiUserDataStream.cs`** — a seekable view of one track's *cooked*
  user data, mapped onto the image on the fly. This is what lets a 4.7 GB DVD be
  browsed with no extraction to memory or a temp file. It agrees byte-for-byte
  with `CdiExtractor`, which remains the authority on sector cooking.

## Name preference

1. **Rock Ridge** NM entries in the ISO hierarchy — real POSIX names
2. **Joliet** SVD — UCS-2 long names (what Windows reads)
3. **ISO 9660** 8.3 — always present, the fallback

`--iso` forces the 8.3 view; `--joliet` forces Joliet and fails if absent.

## Traps this reader handles

- **A directory record with length 0 means "skip to the next sector boundary",
  not "end of directory".** Miss it and every file past the first sector of a
  large directory silently disappears. There's a 120-file test for exactly this.
- Rock Ridge names hide in the System Use area *after* the identifier, which is
  padded to an even boundary — the NM entry must be found by walking SUSP entries.
- The `;1` version suffix and a bare trailing dot are display artefacts and are
  stripped.
- Extraction refuses any path that escapes the output directory.

## Sector ranges

Each entry reports `Extent`, `SectorCount` and `LastSector`, so you can work out
whether a bad sector actually cost you a file — e.g. after a salvaged read that
reported unreadable sectors.

## Validated

- `docs/reference/iso_read.py` — reads back our own builders' Joliet and Rock
  Ridge output, extracts file bytes, checks the 8.3 fallback, and **cross-checks
  a real ISO against `isoinfo -f`**.
- C# round-trips against `IsoBuilder`: what we write, we read back — names,
  structure and bytes.
- The multi-sector directory case is checked against `isoinfo -J -f`.

## Not done yet

- **UDF is read** (see docs/UDF.md) — including Blu-ray's UDF 2.50 metadata
  partition, so BD directory trees are browsable.
- Multi-extent files (>4 GiB) are not reassembled.
- El Torito boot images are not surfaced as extractable entries.
