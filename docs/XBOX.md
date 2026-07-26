# Xbox XDVDFS support

XDVDFS is the filesystem on an Original Xbox game disc, and the format an "XISO"
carries. DiscForge reads it — lists and extracts the files — and writes a
trimmed XISO from a folder. This is plain filesystem work: XDVDFS is not
encrypted, so nothing here decrypts anything, and the Xbox's disc *security*
(which lives outside the filesystem) is neither read nor defeated. DiscForge
handles images and files a person's own backup already holds.

```
dforge xiso-ls game.iso                 # list the files
dforge xiso-ls game.iso --extract out/  # extract them
dforge xiso-ls game.iso --base 0        # force a partition base (auto by default)
dforge create-xiso folder game.iso      # build a trimmed XISO from a folder
```

## The format (clean-room, from public documentation)

An XDVDFS volume is a volume descriptor at a fixed sector, then a directory laid
out as a binary tree of entries.

```
Volume descriptor (sector 32 of the game partition):
  0x000  20  magic "MICROSOFT*XBOX*MEDIA"
  0x014   4  root directory table start sector (LE)
  0x018   4  root directory table size in bytes (LE)
  0x01C   8  creation timestamp
  0x7EC  20  magic "MICROSOFT*XBOX*MEDIA" (trailer)

Directory entry (offsets in 4-byte units within the table):
  0x00  2  left sub-tree offset  (0xFFFF = none)
  0x02  2  right sub-tree offset (0xFFFF = none)
  0x04  4  data start sector (LE)
  0x08  4  size in bytes (LE)
  0x0C  1  attributes (0x10 = directory)
  0x0D  1  filename length N
  0x0E  N  filename (ASCII), padded to 4 bytes
```

Sector addresses are relative to the game partition's base. A trimmed **XISO**
has base 0; a full **XGD1** redump places the partition at sector `0x30600`. The
reader auto-detects the base by finding the signature (checking both the leading
copy and the trailer, so a stray copy of the string in file data can't fool it),
or takes an explicit `--base`.

## Reading

`XdvdfsReader` (`dforge xiso-ls`) parses the descriptor, walks the entry tree —
robust to the two "no child" conventions and to malformed cyclic pointers, via a
visited-set and a depth guard — and lists or extracts files. Sector resolution
is base-aware, so both a trimmed XISO and a based XGD dump read correctly.

## Writing

`XdvdfsBuilder` (`dforge create-xiso`) writes a trimmed (base-0) XISO from a
folder tree: descriptor at sector 32, then each directory's entry table and file
data. Builds are deterministic (fixed timestamp), so the same tree yields
byte-identical output — the basis of the round-trip test.

## Validated by round-trip

XDVDFS has no external oracle here, so — as with UDF — the writer builds a volume
with known contents and the reader reads it back, asserting names, sizes and file
*bytes* match (nested directories, empty files, multi-sector files). A hand-built
volume descriptor and a non-zero-base image pin the signature check and base
resolution independently of the writer. `create-xiso` then `xiso-ls` confirms the
same end-to-end.

## Scope and limits — honest

- **Reading**: trimmed XISO (base 0), original-Xbox XGD1 dumps (base `0x30600`),
  and the Xbox 360 XGD2 (`0x1FB20`) and XGD3 (`0x4100`) bases — the documented
  extract-xiso offsets. The volume descriptor's signature *and* trailer must both
  match at (base + 32), so a wrong base is never chosen by accident; the XGD2/XGD3
  bases follow the documented values and a real 360 dump would confirm them.
- **Writing**: a *balanced* directory tree — children name-sorted, then built into
  a balanced binary search tree (mid element as each subtree root) laid out
  pre-order, the same O(log n) shape Microsoft's authoring produces. Multi-sector
  directory tables (a single directory with hundreds of entries past one sector)
  remain a refinement for exact-mastering use.
- **Streamed writing — done.** `XdvdfsBuilder.BuildToStream` writes straight to a
  seekable stream, with files supplied by path/stream factory and copied on demand,
  so a full-size XISO (well past 2 GB) is authored without holding it in memory.
  `dforge create-xiso` and the GUI use this path. The in-memory `Build` (byte[]) is
  kept for the round-trip tests and small images, and still guards the 2 GB limit;
  both paths produce byte-identical output.
- **Not touched**: the Xbox disc security mechanisms, which are outside the
  filesystem. DiscForge reads and writes the *filesystem* only — no security, no
  decryption, consistent with the clean-room rule (docs/COMPARISON.md §13).
