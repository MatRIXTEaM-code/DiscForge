# DiscForge vs. everyone — a consolidated comparison (2026-09-12)

*This pulls together and refreshes four earlier documents — `imgburn-comparison.md`,
`COMPARISON_FREE_BURNERS.md`, `COMPARISON.md`, and `comparison_all_products.html` — into one
current answer to "how does DiscForge stack up against the field." Facts below were re-verified
by web search on 2026-09-12; where something changed since the last pass (2026-08-10), it's
flagged. DiscForge's own feature set reflects v1.112.0, the latest release.*

## The one-paragraph answer

DiscForge occupies a category almost nobody else does: it's simultaneously a preservation-grade
disc dumper (rivaling Aaru and Redumper), a RAW DAO-96 burner (rivaling only K3b/cdrdao on Linux
and dead tools like CloneCD/Alcohol on Windows), a format converter across a dozen console and PC
image formats, and a competent everyday CD/DVD/BD burner (rivaling ImgBurn, AnyBurn, BurnAware).
No other single tool spans all four of those roles. The honest trade-off: tools that specialize
in just one of those roles are more polished at that one thing — ImgBurn's UI friction for a
one-off burn is lower, AnyBurn/BurnAware have more Windows-native drive-detection polish, and
Aaru/Redumper have a bigger community of contributed drive quirks and disc profiles than
DiscForge's curated set. DiscForge's advantage is breadth plus a cryptographic-provenance layer
(signed dump certificates, Merkle proofs, a public ledger) that literally nothing else in this
comparison has at all.

## Status check: what changed in the competitive field since 2026-08-10

| Tool | Then (Aug 2026) | Now (Sep 2026) | Changed? |
|---|---|---|---|
| ImgBurn | 2.5.8.0, dead since 2013 | Unchanged; only unofficial mirrors remain | No |
| AnyBurn | 6.9 | 6.9 (Jul 28 release confirmed) | No |
| BurnAware | 19.x | 19.3, Sep 1 2026 — added a DVD-Video/PAL-NTSC converter and Windows 11 fixes | Minor update |
| K3b | 25.x | 25.12.0 (Dec 2025) | No |
| Nero Burning ROM | 2026 line | "Nero 2026" (~28.5.2.12) | No |
| CDBurnerXP | dissolved Mar 2025, site down ~Apr 2026 | Confirmed fully offline/dead | No (confirmed) |
| InfraRecorder | dead since 2012 | Still no release since 0.53; can't confirm any activity either way | No |
| CloneCD | dead since SlySoft closed (2014) | Unchanged | No |
| Alcohol 120% | 2.1.1.2201, Jan 2023 | Unchanged | No |
| cdrdao | active | 1.2.6, Dec 2025 | No |
| Aaru | active, v6.0.0-alpha.19 | Unchanged (nothing newer found) | No |
| Redumper | active, build b726 | Unchanged (nothing newer found) | No |
| Exact Audio Copy | 1.8, Jul 2024 | Unchanged | No |

Nothing in the field materially moved in the last month. This is a mature, mostly-stagnant
competitive landscape — which is itself a data point: DiscForge is one of a small handful of
these tools under active development at all.

## ImgBurn, in depth

ImgBurn (2005, last updated 2013) remains the reference for "just burn this image correctly" on
Windows, and it's the tool DiscForge gets compared to most because it's the one most people
already have. The full mode-by-mode breakdown lives in `imgburn-comparison.md`; the short
version, updated for v1.112.0:

- **Read**: DiscForge wins decisively — protection-aware dumping, adaptive re-read, interrupt/
  resume, RAW+subchannel reading, per-sector provenance. ImgBurn does none of this.
- **Build**: roughly even on DVD/BD-Video authoring; DiscForge ahead on IFO-repair tooling;
  ImgBurn's only edge is legacy HD DVD-Video, which isn't worth chasing.
- **Write**: DiscForge ahead on RAW DAO-96 burning and offline burn-plan preview, which ImgBurn's
  own docs say it can't do at all. The two gaps that used to favor ImgBurn here — a burn queue,
  and automatic write-speed-by-media-ID — **both shipped in v1.112.0** and are confirmed on real
  Plextor hardware.
- **Verify**: no contest — DiscForge's signed dump certificates and Merkle-provable ledger have
  no ImgBurn equivalent whatsoever.
- **Discovery**: DiscForge matches ImgBurn+DVDInfoPro's scanning and adds longitudinal
  degradation tracking (Disc Actuary) that neither tool has.
- **Audio CD authoring**: this was ImgBurn's clearest remaining edge (it accepts a dozen
  compressed source formats). **Closed in v1.112.0** — DiscForge now decodes FLAC in-process and
  MP3/AAC/OGG/WMA/APE/MPC/WV via optional FFmpeg, matching ImgBurn's format breadth.

What's left where ImgBurn still wins on pure convenience: a smaller, single-EXE footprint with
zero dependencies, and over a decade of UI polish for the single most common task (burn one ISO
and be done). Those are real but narrow.

## The free/paid burner field (AnyBurn, BurnAware, K3b, Nero)

These four are the tools people actually reach for as ImgBurn's living replacements. None of them
attempts DiscForge's preservation or format-conversion territory — they're burners, full stop.

- **AnyBurn** (Windows, free/Pro) — the best "just burn or rip an ISO" experience today: small,
  portable, ad-free. No raw/subchannel, no preservation surface, no AccurateRip.
- **BurnAware** (Windows, free/Premium/Pro) — polished and Windows-11-native, M-Disc and BDXL
  support, but gates disc-to-disc copy, spanning, recovery, and audio extraction behind paid
  tiers — several things DiscForge (and AnyBurn/K3b) give away free.
- **K3b** (Linux/KDE, GPL) — the only free tool besides DiscForge that can drive RAW DAO/
  subchannel writing, but only by shelling out to external GPL backends (`cdrecord`/`cdrkit`,
  `cdrdao`, `growisofs`) rather than implementing the SCSI/MMC layer itself, and only on Linux.
  DiscForge implements the same class of write natively over SPTI/MMC, cross-platform.
- **Nero Burning ROM** (Windows, paid) — the mature commercial suite: SecurDisc encryption,
  native NRG format, disc spanning, LightScribe/LabelFlash labelling, Gracenote-backed ripping.
  The best-polished consumer burner in the set, but single-platform and zero preservation
  features — no AccurateRip, no C2/PIE scanning, no cryptographic provenance, no CHD/GDI/WBFS.

Verdict: for a casual burn-and-walk-away user on Windows, AnyBurn or Nero are genuinely better
choices than DiscForge today. None of them is a substitute for DiscForge's preservation or
conversion work, because none of them tries to be.

## The abandonware tier (CDBurnerXP, InfraRecorder, CloneCD, Alcohol 120%)

All four are dead or effectively dead — no releases in years, in CDBurnerXP's case the developer
company itself dissolved and the site is offline. They're worth naming only because people still
find them in search results and old forum threads. CloneCD and Alcohol 120% are the two that ever
touched DiscForge's RAW/subchannel territory, and both did it partly to circumvent copy
protection — a line DiscForge's clean-room policy deliberately does not cross. None of the four
should be recommended to anyone today; DiscForge, AnyBurn, BurnAware, K3b, or Nero all cover their
use cases with an actively maintained tool.

## The preservation-tool competitors: Aaru, Redumper, DiscImageChef, Exact Audio Copy

This is the field DiscForge's read/verify/provenance work actually competes in, and it's the
comparison ImgBurn-focused writeups tend to skip.

- **Aaru Data Preservation Suite** (cross-platform, open source, active) — the closest thing to a
  peer. Strong on preservation-grade dumping, protection-aware CD handling, filesystem reading
  (including ISO 9660/UDF/FAT/NTFS/ext/HFS, which DiscForge also does), and its own AaruFormat
  image type, which DiscForge can read/write/verify natively (CRC-64-gated, LZMA/FLAC-compressed)
  for interop. Aaru does **not** do RAW DAO-96 *burning* (it's fundamentally a reading/analysis
  suite), CHD creation, or cryptographic dump certificates — DiscForge's Merkle-provable
  provenance ledger has no Aaru equivalent. DiscImageChef, incidentally, is not a separate
  competitor: it was renamed to Aaru years ago.
- **Redumper** (cross-platform, open source, active) — purpose-built for Redump-quality CD/DVD
  dumping specifically, with strong protection handling. Narrower in scope than DiscForge (no
  burning, no format conversion beyond its own pipeline, no CHD/GDI/WBFS/console-cartridge work),
  but its CD subchannel/protection handling is mature and community-vetted in a way DiscForge's
  is not yet, since Redumper has years of Redump-community disc coverage behind it.
- **Exact Audio Copy** (Windows, freeware) — the reference for audio CD ripping specifically:
  AccurateRip, drive offset correction, C2 error reporting, secure-mode re-reads. DiscForge's
  audio-CD read path covers similar ground but EAC's audio-specific tuning (per-drive offset
  databases contributed by a huge user base) is deeper than DiscForge's for that one job.

Verdict: DiscForge's preservation work is competitive with, not clearly ahead of, this tier on
CD-specific dumping maturity — Aaru and Redumper both have a longer track record and larger
contributed drive/disc databases. DiscForge's differentiators here are breadth (one tool doing
dumping, RAW burning, conversion, and cataloguing instead of three or four), CHD creation (none
of Aaru/Redumper/EAC can create CHD, only DiscForge can), and the cryptographic provenance layer,
which is unique to DiscForge across this entire comparison.

## The broader imaging-suite landscape (Alcohol 120%, CDRWIN, DiscJuggler, BlindWrite, etc.)

`COMPARISON.md` covers this ground in detail; the short version holds unchanged since July: this
is DiscForge's home turf for general imaging/burning (read-to-image, build-from-files, format
conversion, checksums, verify-after-burn), all of it now hardware-confirmed rather than
code-only as it was in July. The one item that document still marks open is DiscJuggler-style
simultaneous multi-drive duplication, which remains unbuilt.

## Bottom line

| Dimension | Best-in-class today | Where DiscForge stands |
|---|---|---|
| Casual "burn this ISO" convenience | AnyBurn / Nero | Capable but not the friction-optimized choice |
| RAW DAO-96 / subchannel writing | DiscForge (native, cross-platform) | Leads — K3b needs external Linux-only tools, CloneCD/Alcohol are dead |
| Preservation-grade CD dumping | Aaru / Redumper (tied with DiscForge) | Competitive, not clearly ahead |
| Audio CD ripping (AccurateRip, offset DB) | Exact Audio Copy | Behind on drive-offset database depth |
| Format conversion breadth (CHD, GDI, CSO, WBFS) | DiscForge | Leads — no other tool here creates CHD |
| Cryptographic dump provenance | DiscForge | Unique — no other tool in this comparison has it |
| Cross-platform single engine | DiscForge (CLI) | Leads — Aaru is the only other genuinely cross-platform peer |
| Active development | DiscForge, Aaru, Redumper, AnyBurn, BurnAware, K3b, Nero | Tied with a handful of others; most of the field is dead |

No single competitor threatens DiscForge across the board, and DiscForge doesn't threaten any
single specialist on their own narrowest turf either. The honest pitch is breadth: it's the only
tool that would let you retire ImgBurn, Aaru, and a CHD-creation utility all at once, in exchange
for giving up some of each one's individual polish.
