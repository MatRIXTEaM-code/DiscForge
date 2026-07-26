# Create path — folder to CDI, self-contained

DiscForge builds a data CDI from a folder of files with no external tools
(no mkisofs / genisoimage). Two pure, tested components in Core:

- `Iso/IsoBuilder` — a minimal ISO 9660 Level 1 image builder. 8.3 uppercase
  names with ";1" versions, single root directory, both-endian fields per
  ECMA-119, 2048-byte sectors. Validated by the third-party `isoinfo` tool:
  building an image, listing it, and extracting files back byte-identical all
  pass (docs/reference/iso_build.py).
- `Create/CdiCreator` — ties IsoBuilder to CdiWriter: files -> ISO -> single
  Mode 1 / 2048 data track CDI. That layout is exactly what the burn planner
  routes to the IMAPI2 engine, so a created image is directly burnable on any
  modern drive.

CLI: `dforge create <dir> <out.cdi> [--volume NAME] [--version v2|v3|v35]`.

## Validation

The full pipeline was checked end-to-end: files -> ISO -> CDI -> parse ->
extract -> `isoinfo`, confirming the files come back readable and byte-identical.
The C# side adds folder->CDI->extract round-trip tests and a create+verify pass
across all three CDI versions.

## Directory trees

Subdirectories are supported: `IsoBuilder.BuildTree` writes recursive directory
records and multi-entry Type-L/Type-M path tables with parent pointers, in
canonical path-table order (level, parent number, identifier).
`CdiCreator.CreateFromDirectory` now reads a folder recursively, so nested
folders image correctly. Validated end-to-end with `isoinfo`: a two-level tree
survives folder -> ISO -> CDI -> extract with every nested file byte-identical
(docs/reference/iso_build_tree.py).

## Joliet (long names)

Joliet is enabled by default: alongside the ISO 9660 8.3 hierarchy, the builder
writes a type-2 Supplementary Volume Descriptor and a parallel directory tree of
UCS-2 (UTF-16BE) long names over the SAME shared file extents. Real filenames
survive — "My Photos.jpg", spaces and mixed case intact — which is what Windows
reads; the 8.3 names remain as a fallback. Validated with `isoinfo -J` end to
end, including deep nesting, through the full create -> CDI -> extract pipeline.
Pass `joliet: false` for a pure ISO 9660 image. Builds are deterministic
(fixed timestamps) — identical input yields byte-identical output.

## El Torito (bootable discs)

`IsoBuilder.BuildTree(..., boot: new BootImage(bytes, media))` and
`CdiCreator.CreateBootableImage(...)` write a bootable image: a Boot Record
Volume Descriptor ("EL TORITO SPECIFICATION"), a boot catalog (checksummed
validation entry + bootable default entry), and the boot image at its own extent.
The boot image is caller-supplied — DiscForge embeds no boot code of its own,
so nothing copyrighted is baked in. Media types: no-emulation (default),
1.2/1.44/2.88 MB floppy, hard disk. Validated with `isoinfo -d` (reports the
El Torito VD + bootable catalog) end to end through create -> CDI -> extract.

Note: this is PC/BIOS El Torito, not Sega Dreamcast selfboot — Dreamcast booting
uses IP.BIN + a dual-session MSINFO layout (the old bootmake flow), which needs a
licensed bootstrap and is intentionally out of scope.

## Rock Ridge (POSIX long names)

Opt-in (`rockRidge: true`, or `dforge create ... --rock-ridge`). Appends SUSP/RRIP
System Use entries to the ISO hierarchy's directory records: SP (once, in the
root '.'), ER (RRIP_1991A), PX (mode/nlink/uid/gid), TF (timestamps) and NM (the
real POSIX name). Linux/macOS then see real long names and Unix permissions
(`-rw-r--r--`, `drwxr-xr-x`); the 8.3 names remain as a fallback.

Combine with Joliet (on by default) for a genuinely cross-platform disc: Windows
reads the Joliet names, Linux/macOS read the Rock Ridge ones, and both share the
same file extents. Rock Ridge touches only the ISO hierarchy — Joliet and El
Torito are unaffected.

NM names are capped so each directory record stays within the 255-byte limit
(reclen is a single byte); the cap is computed from the record's actual
identifier length. Validated with `isoinfo -R -l` end to end through
create -> CDI -> extract, including a pathological 200+ character name.

## Scope / follow-ups (correct-first)


## Large images (streaming)

Authoring is streamed end to end, so memory use is flat regardless of image size
— a DVD- or BD-sized tree is fine.

- `IsoBuilder.Plan(...)` computes the layout from file *lengths* only; no payload
  is read. It returns an `IsoLayout` with the final `VolumeSectors`/`ImageBytes`.
- `IsoLayout.WriteTo(stream)` emits the image. The layout is in strictly
  ascending sector order (system area, descriptors, path tables, directory
  records, boot catalog, then file payloads), so it writes sequentially with no
  seeking. Only the metadata region is buffered (kilobytes); payloads are copied
  through a 64 KB buffer.
- `IsoBuilder.Node.FromPath(path)` references a file on disk. `CdiCreator` uses
  it when reading a directory, so source files are never loaded whole.
- `CdiWriter.TrackInput.DataWriter` is the streaming counterpart to `Data`; the
  writer verifies the callback emitted exactly the declared byte count, since a
  short write would corrupt every offset in the descriptor.
- `CdiConverter.BinCueToCdi` streams BIN files too (it previously read each BIN
  and copied it again to prepend the pregap — twice the track size in RAM).

`IsoBuilder.Build`/`BuildTree` still return a `byte[]` for tests and small
images; they now wrap the streaming path and refuse (with a clear message) past
the 2 GB array ceiling, pointing at `Plan` + `WriteTo`.

### Verification streams too

`CdiVerifier` computes the user-data CRC through a streaming sink. It previously
extracted the whole track into a MemoryStream, which fails past 2 GB with
"Stream was too long" — so no DVD-sized image could be fully verified. Found on a
real 4.68 GB image.

### Known limit: 4 GiB per file

ISO 9660 stores a file's data length as a 32-bit value, so a single file cannot
exceed 4 GiB - 1. `Plan` refuses such a file with an explicit message rather than
silently truncating it. Multi-extent files (the usual workaround) are not
implemented.
