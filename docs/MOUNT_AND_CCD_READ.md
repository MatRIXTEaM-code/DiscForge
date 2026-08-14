# Virtual mounting & CloneCD reading (deepening the imaging lead)

Two additions that broaden DiscForge's imaging reach into territory its (now
frozen) competitors held.

## CloneCD reading — `CloneCdReader` / `dforge ccd-info`

DiscForge already *wrote* CloneCD `.ccd` control files; it now *reads* them too,
so it consumes CloneCD images (`.ccd` + `.img` + optional `.sub`) as well as
producing them. `CloneCdReader.Parse` inverts the writer: it parses the INI
structure ([CloneCD]/[Disc]/[Session]/[Entry]) into a validated `CcdToc` —
sessions, tracks, start LBAs, control bytes, MCN, and per-track ISRC where
present. Verified by a full round-trip (write a layout → read it back → TOC
matches) and against hand-written `.ccd` text.

```
dforge ccd-info game.ccd     # shows the CloneCD TOC + sidecar presence
```

This puts DiscForge at full read/write parity with CloneCD's on-disc format —
notable because CloneCD itself is frozen (RedFox, 2024).

## Virtual mounting — `VirtualDisc` / `dforge mount`

"Mount this image as a drive" splits into two layers, and DiscForge is honest
about the boundary:

1. **The emulation model** (`VirtualDisc`, pure and tested): describes any image
   as a uniform mountable disc — media type, sectors, tracks, audio/subchannel
   makeup — and resolves the right *mount strategy*.
2. **The OS binding**: exposing it as a drive letter. A faithful optical mount
   (audio, subchannel, multi-track) needs a kernel-mode virtual-drive driver —
   as Alcohol's and Daemon Tools' signed drivers do. DiscForge does not ship one
   yet; that is future Windows-side work.

What works **today, with no driver**: Windows' own native ISO mount. `dforge
mount` routes each image:
- **Plain `.iso` data image** → prints the `Mount-DiskImage` command; mount now.
- **Single cooked data track** in a `.cdi`/`.bin`/`.img` → export to `.iso`
  (`dforge convert`), then native-mount.
- **Audio / subchannel / multi-track** → reports that the virtual-drive driver
  is required, and points at inspect/verify/extract/convert instead.

So DiscForge delivers the genuinely-doable half of mounting immediately, and
models the rest honestly rather than shipping an unvalidated kernel driver.

## Boundary

Both features handle unprotected or personally-authored images only; neither
decrypts anything.

## GUI

All three surface as launcher tiles, over the same Core code as the CLI:
- **AccurateRip** (`AccurateRipView`) — pick an audio CUE; computes v1/v2
  checksums and disc IDs, and verifies against a downloaded record if supplied.
- **Mount** (`MountView`) — describe an image's mount strategy; for
  ISO-compatible images, a Mount button triggers Windows' native mount.
- **CloneCD** (`InteropView`) — read a `.ccd` TOC, or write a `.ccd` from a CUE.

The launcher now hosts 17 tiles (5 rows). The views follow the existing
async/StatusBus/Theme patterns; the WinForms App is validated on Windows.
