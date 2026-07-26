# CHD corpus verification

Cross-checks DiscForge's CHD read/write paths against **chdman** (the MAME
reference tool) as an oracle. This is the repeatable form of the one-off
verification done when the CHD reader/writer was built.

## What it checks

For a matrix of five data patterns (`zeros`, `ramp`, `random`, `dup`, `mixed`)
and six codec configs — a wholly uncompressed CHD (`none`, flat offset map) plus
five compressed configs (`zlib`, `zlib,flac`, `lzma`, `flac,zlib,huff`,
`huff,zlib`):

- **READ** — chdman creates a CHD, DiscForge extracts it, result must be
  byte-identical to the source.
- **WRITE (HD)** — DiscForge creates a hard-disk CHD; `chdman verify` must pass
  (both stored SHA-1s) and `chdman extractraw` must reproduce the source.
- **WRITE (CD)** — DiscForge creates a CD CHD from a bin/cue; `chdman verify`
  passes and DiscForge reads it back to the original bin.

The `random` pattern is incompressible, so chdman emits **NONE hunks inside the
compressed map** for it — that map path is covered without a separate config.

## Running

```
tools/chd-corpus-verify/verify.sh
```

Requires the .NET 8 SDK and `chdman` on `PATH` (MAME tools; developed against
0.264). Exits non-zero if any check fails. Expected result: `pass=36 fail=0 /
ALL CLEAN`.
