# DiscForge — registry submission drafts

Two paste-ready drafts: the COPTR tool page and an awesome-list PR entry.
URLs point at the published repo (https://github.com/MatRIXTEaM-code/DiscForge);
everything else is accurate to the shipped feature set as of August 2026.

> Submission notes
> - COPTR (coptr.digipres.org) creates tool pages through its "Tool" form —
>   you paste the values below into the form fields (they match the labels on
>   existing pages such as Aaru's), and the Description prose into the
>   Description section. You need a (free) wiki account.
> - Publish the repo FIRST. Both registries expect a working homepage/source
>   link, and COPTR readers will check it.

---

## 1. COPTR tool page

**Page title:** DiscForge

**Infobox fields**

| Field | Value |
|---|---|
| Purpose (one-liner) | Preservation-grade optical disc imaging, verification and RAW-DAO burning toolkit with a "provably correct or declined" integrity model. |
| Homepage | `https://github.com/MatRIXTEaM-code/DiscForge` |
| Source Code | `https://github.com/MatRIXTEaM-code/DiscForge` |
| License | GPL-3.0-or-later |
| Platforms | Windows (GUI + full hardware I/O); Linux (CLI: imaging analysis, conversion, filesystems — SG_IO drive layer present, hardware-validation pending) |
| Function | Disk Imaging, Data Recovery, Metadata Extraction, Validation, Fixity |
| Content type | Disk Image, Audio, Software |

**Description section**

DiscForge is a toolkit for imaging, verifying, repairing, converting and
re-burning optical media (CD/DVD/BD), built around a strict integrity rule:
every output is proven correct against independent evidence (checksums the
format itself carries, multi-pass consensus, external databases) or the
operation is declined — the tool never silently emits possibly-corrupt data.

Capabilities relevant to preservation workflows:

* **Dumping and verification** — raw 2352+96 sub-channel reads with multi-pass
  consensus (majority-voted sub-channel Q that preserves LibCrypt-style
  intentional errors), C2-error mapping, per-sector provenance records, an
  unreadable-sector map carried as a sidecar through every later conversion so
  holes are never laundered by a checksum, and media-quality / read-stability
  scanning.
* **Format breadth** — reads and converts bin/cue, CDI, GDI, NRG, MDS, CCD,
  CHD (read, verify, extract *and* create, chdman-accepted), CSO/ZSO, RVZ/NKit
  (identify/decode), and AaruFormat (reads uncompressed, LZMA and FLAC images
  with every decoded block verified against its stored CRC-64; writes
  uncompressed AaruFormat; exports CICM metadata sidecars for interchange with
  Aaru-based workflows).
* **Filesystem access** — lists and extracts files from ISO 9660 / Joliet /
  Rock Ridge, UDF, FAT, exFAT, NTFS, ext2/3/4 and HFS volume images, with
  cross-filesystem verification of hybrid discs (divergent directory views are
  reported, not merged).
* **Community-database workflows** — Redump-style cuesheet/hash generation,
  Logiqx DAT verification (with explicit evidence-strength labelling:
  SHA-1 vs MD5 vs CRC-32), AccurateRip audio verification, submission
  packaging, collection triage and 1G1R set building.
* **Recovery** — a one-command damage assessment (`recover`) grading an image
  INTACT / RECOVERABLE / DAMAGED / UNREADABLE with concrete next steps and an
  HTML report; filesystem-constrained salvage planning; orphan-directory
  carving.
* **Authoring and burning** — ISO 9660/Joliet/Rock Ridge mastering with
  BIOS+UEFI hybrid El Torito boot, automatic DVD-9 layer-break planning (ECC
  or VOBU-aligned), and RAW DAO-96 burning that re-creates sub-channel data,
  verified after burn by consensus read-back against a rebuilt golden image.
  Burning and drive I/O are Windows (SPTI/IMAPI2); a Linux SG_IO passthrough
  layer exists with hardware validation in progress.

The codebase is clean-room (public documentation and observed behaviour only;
provenance notes per format ship in the repository), licensed GPL-3.0-or-later,
and carries an extensive automated test suite (2,400+ tests) including
reference-validated codec implementations (LZMA vs liblzma streams, FLAC vs
reference-encoder streams, fuzzy hashing byte- and score-exact against
ssdeep 2.14.1).

**Provenance / User Experiences:** leave empty at creation (COPTR convention —
filled by users).

---

## 2. Awesome-list PR (digipres/awesome-digital-preservation or similar)

**Entry line** (place under *Disk Imaging* / *Tools*):

```markdown
- [DiscForge](https://github.com/MatRIXTEaM-code/DiscForge) - Optical disc preservation toolkit: consensus dumping with per-sector provenance, format conversion (CHD, AaruFormat, bin/cue and more, all integrity-gated), filesystem extraction (ISO/UDF/FAT/exFAT/NTFS/ext), Redump/AccurateRip workflows, and verified RAW DAO-96 re-burning. GPL-3.0.
```

**PR title:** `Add DiscForge (optical disc preservation toolkit)`

**PR body:**

```markdown
Adds DiscForge, a GPL-3.0 toolkit for optical media preservation.

What it covers that existing entries don't, in one tool: preservation-grade
dumping (multi-pass sub-channel consensus, C2 mapping, per-sector provenance,
unreadable-sector sidecars that survive format conversion) combined with
verified RAW DAO-96 burning — read-back is compared against a rebuilt golden
image, sub-channel included. Also: CHD create/verify/extract, AaruFormat
read (uncompressed/LZMA/FLAC, CRC-64-gated) and write, CICM metadata export,
filesystem extraction across ISO 9660/UDF/FAT/exFAT/NTFS/ext2-4/HFS, Redump
DAT + AccurateRip workflows, and a one-command damage assessment with HTML
reporting.

Integrity model: "provably correct or declined" — unsupported or unverifiable
structures are refused rather than guessed at. Codec implementations are
clean-room and validated against reference implementations (liblzma, reference
FLAC encoder, ssdeep 2.14.1). 2,400+ automated tests.

Windows GUI + CLI; the analysis/conversion CLI also runs on Linux.
```

---

### Pre-submission checklist

- [ ] Repo pushed and public at https://github.com/MatRIXTEaM-code/DiscForge (branch `public-release` → `main`, tag `v1.0.0`)
- [ ] README present (the repo currently leads with code, not a front page)
- [ ] A tagged release (registries and list maintainers look for one)
- [ ] LICENSE (GPL-3.0) and NOTICE visible at the repo root — already done
- [ ] Optional but strong: link the head-to-head comparison doc
      (`docs/comparison_all_products.html`) from the README
