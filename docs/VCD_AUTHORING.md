# VCD / SVCD authoring, DVD menus, and streamed writers — assessment

This document is the honest evaluation of the last three items on the "get these
done" list. Two of them are large, standalone projects; rather than pretend a
weekend's code finishes them, this scopes each one, ships the tractable clean-room
increment where there is one, and says plainly what is deferred and why.

## 1. VCD / SVCD authoring

### What DiscForge already has

Most of a Video CD *is* things DiscForge already builds cleanly: an ISO 9660
volume (the `Iso*` layer), CD track composition and raw DAO writing (`build-raw`,
the CD-ROM/XA Mode 2 sector work in the raw pipeline), and cue/TOC handling. A
Video CD is, structurally, an ISO 9660 data track plus one or more MPEG tracks
written as CD-ROM/XA Mode 2 Form 2, described by a small set of control files.

### What ships now — the control-file layer (`VcdControl`)

The missing VCD-specific piece is the control files, and the tractable, clean-room
half of those ships here: `VcdControl` reads and writes the two mandatory files —
`INFO.VCD` and `ENTRIES.VCD` (SVCD: `INFO.SVD` / `ENTRIES.SVD`). It carries the
disc identification (the `VIDEO_CD` / `SUPERVCD` magic, the version, the album and
volume fields) and the entry-point table (each entry a track and its BCD-coded MSF
address). Both VCD and SVCD spellings are handled.

```
dforge vcd-info INFO.VCD       # identify a VCD/SVCD disc header
dforge vcd-info ENTRIES.VCD    # list its entry points
```

Validated by round trip (writer ↔ reader), the same standard the NRG reader was
held to before a real sample: `VcdControlTests` asserts the magic, the big-endian
count, and — the field that bites — that MSF addresses survive as BCD, not raw
hex. **Clean-room provenance:** these layouts are written from the *public
description* of the Video CD control-file format, **not** ported from vcdimager's
GPL source, per DiscForge's rule (COMPARISON.md §13). **Honest scope:** this emits
the **pbc-less** profile (`vcdimager -t`'s "simple" output) — header identification
and the entry-point table. It does **not** emit the playback-control structures
(PSD, LOT) or the segment-item table, and it awaits validation against a control
file produced by a real authoring tool; a field a real file reads differently is
then a bug to fix against the sample, not a redesign.

### What full VCD authoring still needs (deferred)

To turn a set of MPEG files into a burnable VCD image, three more pieces are
needed on top of the control files:

1. **MPEG encode** — the video must be MPEG-1 (VCD) or MPEG-2 (SVCD) at the
   White-Book bitrates. This is FFmpeg's job, orchestrated the same way the
   transcode/reauthor layers already drive it — not reimplemented.
2. **XA Mode 2 Form 2 track muxing** — the encoded stream is written into
   2324-byte Form 2 sectors with the correct sub-header (real-time audio/video
   coding flags). DiscForge's raw layer writes Mode 2 sectors; the Form 2
   sub-header pattern for VCD is the specific new work.
3. **The VCD directory + PSD** — the exact `MPEGAV/`, `VCD/`, `SEGMENT/`, `CDI/`,
   `EXT/` tree, and (for menus/chapters) the PSD play-sequence descriptors that
   `VcdControl` deliberately does not yet emit.

None of this is blocked by the clean-room rule *except* insofar as the richer
control structures (PSD/LOT) need a non-GPL description to implement faithfully —
which is the real gate, not engineering effort. The pbc-less path above is the
part that clears that gate today.

## 2. DVD menu creation — deferred to the authoring runner

DVD menus are **out of scope for a native writer**, for the same reason
`IfoWriter` stops at the structural layer (see docs/DVD_VIDEO_SHRINK.md). A menu is
not a document; it is a program. Building one means composing:

- **PGCI navigation command tables** — a small bytecode (button jumps, GPRM/SPRM
  register logic) compiled per program chain;
- **subpicture button highlights** — the menu's buttons are subpicture (subtitle)
  overlays with per-button colour/contrast highlight maps;
- **PCI / DSI packets** — the per-VOBU navigation and seek information the player
  reads in real time, which must be interleaved correctly with the muxed video.

These must be generated in lock-step with the muxed menu VOB and are exactly what
a hardware player's decoder walks; getting them wrong produces a disc that looks
authored but dead-ends on a real player, and there is no way to validate that
without real-hardware testing. DiscForge's position is consistent and deliberate:
it reads DVD structure, plans the shrink/reauthor, and writes the *structural*
IFOs, and it drives **dvdauthor** (the established, validated tool) for the
navigable output. Menu authoring is dvdauthor's job by design, not a gap to close
with more code here.

## 3. Streamed writers — DONE

> **Update:** both streamed writers described below are now implemented.
> `XdvdfsBuilder.BuildToStream` and `UdfBuilder.BuildToStream` write to a seekable
> stream with files supplied by path/stream factory, so images past the 2 GB
> byte[] limit are authored without holding them in memory; the in-memory `Build`
> paths are kept for the round-trip tests and produce byte-identical output.
> `dforge create-xiso` / `create-udf` and the GUI use the streamed path. See
> docs/XBOX.md and docs/UDF.md. The original design note follows.



The `UdfBuilder` and `XdvdfsBuilder` build **in memory**, with a ~2 GB ceiling
(each surfaces that limit as an explicit error rather than silently truncating).
That is fine for the round-trip tests and for homebrew-sized images, but a full
DVD-9 rebuild needs a streamed writer.

The shape of the fix is clear, and both builders are already structured for it —
they plan the layout (assign every file and directory a start sector) in one pass
before writing bytes. A streamed writer keeps that planning pass unchanged and
replaces only the final "materialise into a `byte[]`" step with a two-pass write
to a `Stream`: pass one computes sizes and offsets (already done), pass two seeks
and writes each structure to the output stream in sector order. The determinism
property is preserved because the layout is computed identically; only the sink
changes.

Why it's deferred rather than shipped now: it is a genuine refactor of both
builders' write path (every `Array.Copy(... image ...)` becomes a
`stream.Seek/Write`), it wants its own large-image tests (which are slow and need
real disk, not the in-memory harness), and it touches code the round-trip tests
depend on — so it deserves to be its own increment with its own verification, not
a rider on this one. The ceiling is documented at every call site (UDF.md,
XBOX.md), and the error message names the streamed writer as the follow-up, so no
user hits it unaware.

## Summary

| Item | Status |
|------|--------|
| VCD/SVCD control files (`INFO`, `ENTRIES`) | **Shipped** — read/write, round-trip validated, pbc-less scope, clean-room from public description |
| Full VCD image authoring (MPEG encode, XA Form 2 mux, PSD tree) | Scoped; MPEG via FFmpeg orchestration; PSD gated on a non-GPL spec |
| DVD menu creation | Deferred to dvdauthor by design (PGCI/subpicture/PCI-DSI navigation) |
| Streamed UDF/XISO writers | **Shipped** — `UdfBuilder`/`XdvdfsBuilder.BuildToStream` write past the 2 GB ceiling to a seekable stream; in-memory `Build` kept for round-trip tests, byte-identical output |
