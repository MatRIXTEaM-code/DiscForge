# Verifying dumps against a redump DAT

The disc-preservation databases (redump.org and the like) publish **DAT files** —
Logiqx-XML catalogues that list every known-good dump with its size and CRC-32 /
MD5 / SHA-1. DiscForge already computes those hashes, so it can close the loop:
point it at a DAT and a file and ask *is this exactly the preserved dump, and
which disc is it?*

```
dforge dat-verify system.dat  game.bin              # verify one track/file
dforge dat-verify system.dat  track1.bin track2.bin game.cue   # verify a set
```

For each file DiscForge hashes it once (size + CRC-32 + MD5 + SHA-1, the same pass
`checksum` uses) and looks it up in the DAT:

- **✓ verified** — the size and CRC-32 match a catalogued entry, confirmed by SHA-1
  where the DAT carries one. The disc/title is named.
- **✗ flagged** — a CRC-32 hit whose SHA-1 or size disagrees. Reported rather than
  trusted (a hash collision, or a subtly altered file).
- **✗ not found** — no catalogued dump matches; this file isn't the preserved one
  (a bad dump, a different revision, or a system whose DAT you don't have).

## Notes

Redump DATs list one `rom` entry per **track** (the individual `.bin` files) plus
the `.cue`, so verifying a multi-track disc means checking each track file. SHA-1
is preferred when present (collision-resistant); CRC-32 + size is the fallback,
which is what most DATs key on. This is pure verification — DiscForge reads the DAT
and hashes the file, and changes nothing.
