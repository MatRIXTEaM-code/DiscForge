# DiscForge vs the alternatives landscape — free, commercial and specialist

*A researched head-to-head against the disc tools people name as ImgBurn alternatives, as a
companion to `COMPARISON_IMGBURN.md`. Covers the free burners (CDBurnerXP, AnyBurn, BurnAware,
K3b, InfraRecorder), the commercial heavyweight (Nero), and the specialists (CloneCD, Sony
DoStudio). Prepared 2026-08-10; all version and maintenance facts web-verified on that date.*

## The single biggest finding: most of the field is abandonware

When people list "ImgBurn alternatives," the majority of the names are no longer maintained.
Verified release / status as of 2026-08-10:

| Tool | Last release | Status |
|---|---|---|
| **ImgBurn** | 2.5.8.0, **Jun 2013** | Abandonware |
| **CDBurnerXP** | 4.5.8.x, **Nov 2019** | Abandonware — developer dissolved Mar 2025, site offline ~Apr 2026 |
| **InfraRecorder** | 0.53, **Sep 2012** | Abandonware (GPL; cdrtools front-end) |
| **CloneCD** | 5.3.4.0, **May 2016** | Abandonware — RedFox stopped selling it ~Jun 2024 |
| **Sony DoStudio** | (Sony Creative bought Netblender 2011) | Effectively discontinued pro BD-authoring tool |
| **AnyBurn** | 6.9, **Jul 2026** | ✅ Active |
| **BurnAware** | 19.x, **2026** | ✅ Active |
| **K3b** | KDE-Gear 25.x, **2025** | ✅ Active (KDE) |
| **Nero Burning ROM** | 2026 (3.0.2.25) | ✅ Active (commercial) |
| **DiscForge** | active (this repo) | ✅ Active |

So five of the nine third-party tools are dead. The living competition is really **AnyBurn,
BurnAware, K3b** (free) and **Nero** (paid) — plus DiscForge. Two of the dead ones (CloneCD,
DoStudio) still matter to the analysis because they're the only two that ever overlapped
DiscForge's *specialist* territory, so they're covered below.

## The one-paragraph answer

For a casual "burn this ISO on Windows" job, AnyBurn or BurnAware are excellent, and Nero is the
mature paid option; on Linux, K3b is the standard. **But all of them are disc *burners* (or, for
DoStudio, a BD *author*).** None images, identifies, converts, verifies and catalogues discs and
cartridge dumps the way DiscForge does; none does AccurateRip, C2 re-reads, read-offset
correction, Redump submission info, DAT/PAR2 verification, or the console-preservation universe.
Only two ever touched raw/sub-channel — K3b (via the external GPL `cdrdao`) and CloneCD — and
CloneCD did it to *circumvent* copy protection, which is the exact line DiscForge does not cross.
DiscForge is also the only tool of the whole set that runs one engine on Windows, macOS and Linux.

## The living free field (verified)

| | AnyBurn | BurnAware Free | K3b | DiscForge |
|---|---|---|---|---|
| **Latest release** | 6.9, Jul 2026 | 19.x, 2026 | 25.x, 2025 | active |
| **Platforms** | Windows only | Windows only | **Linux/Unix (KDE) only** | **Win / macOS / Linux** (CLI) |
| **Licence** | Freeware (Pro paid), closed | Freeware (Premium/Pro paid), closed | **GPL** | Proprietary, source-visible |
| **Free-tier catch** | Pro adds only 2 minor things | Copy/spanning/recovery/audio-extract are **paid** | none | — |
| **Adware history** | clean | clean | clean | none |
| **Surface** | GUI | GUI | KDE GUI | **CLI (286 cmds)** + Win GUI |

- **AnyBurn** — the best pure "just burn/rip an ISO" pick and genuinely maintained: tiny,
  portable, ad-free, Free edition free for personal *and* business use (Pro only adds audio-format
  conversion and "install Windows to USB"). No preservation/identify/convert/retro surface, no
  raw/sub-channel, no AccurateRip.
- **BurnAware Free** — polished, Windows-11-native, M-Disc and BDXL — but the free tier is
  deliberately gated: disc-to-disc copy and spanning are Premium; file recovery and audio
  extraction are Professional. Several things that are free in AnyBurn or K3b cost money here.
- **K3b** — the mature KDE burner and the only free tool that can drive **raw DAO / sub-channel**,
  which it does by shelling out to external backends (`cdrecord`/`cdrkit`, **`cdrdao`**,
  `growisofs`); **Linux-only**, GPL. It's the closest free competitor to DiscForge's RAW ambition,
  but only on Linux and only over an aging external toolchain that DiscForge implements natively
  in-process over SPTI/MMC. Because K3b and cdrdao are **GPL**, DiscForge's clean-room rule means
  we do not read or lift their code — the raw engine is reimplemented from the public MMC model.

## The commercial heavyweight: Nero Burning ROM

Nero (2026 line, actively sold) is the serious paid competitor — the most feature-complete
Windows burner of the set. Full CD/DVD/BD/**BDXL** burning, **SecurDisc 4.0** (256-bit
encryption + password protection), the native **NRG** image format, DiscSpan spanning, overburn,
audio ripping with Gracenote, LightScribe/LabelFlash disc labelling, and decades of
hardware-proven reliability. It is **Windows-only, paid** (sold standalone and in the Nero
suite), and closed source. Against DiscForge: Nero is the more mature, more hardware-proven
*burner and consumer suite*, with polished authoring and labelling DiscForge doesn't attempt. But
it does none of DiscForge's section-5 preservation surface — no AccurateRip, sub-channel-as-data,
C2 re-reads, read-offset correction, Redump submission info, DAT/PAR2 verify, any-to-any
conversion, CHD/CSO/WBFS/XISO, or the console/retro universe — and it's single-platform. Nero's
SecurDisc *encrypts* user data; DiscForge deliberately does no content encryption (that's the
protected-content side of the clean-room line).

## The specialists (both now abandonware), and the clean-room line

**CloneCD** is the one tool that ever went head-to-head with DiscForge's sub-channel fidelity.
It stored subcode data and made exact, protection-carrying copies — but its headline features
were **"Amplify Weak Sectors"** and protected-game copying, i.e. defeating/normalising copy
protection so a burned copy plays. That is *circumvention*, and it is exactly what DiscForge does
**not** do. DiscForge *preserves* protection fingerprints (LibCrypt, sub-channel, weak sectors)
faithfully **without** amplifying, weakening or bypassing them — a preservation act, not a
circumvention one. CloneCD (Elaborate Bytes → SlySoft → RedFox) last shipped 5.3.4.0 in May 2016
and RedFox stopped selling it around June 2024, so it's also abandonware. The distinction matters
more than the overlap: even where both write sub-channel, they are ethically and legally
different tools, and DiscForge stays on the clean-room side by design.

**Sony DoStudio** is a different category again — professional **Blu-ray authoring** (BD-ROM spec
compliance, **AACS**, BDCMF mastering output, **BD-J** Java menus), not a burner. Netblender built
it; Sony Creative Software acquired Netblender in 2011; it's since effectively discontinued (its
own user community asks whether it's "dead forever"). It overlaps DiscForge only at the edge:
DiscForge's `bdmv-build` assembles a compliant `BDMV/` folder into a UDF 2.50 Blu-ray *image*, but
it deliberately does **not** author BD-J menus or apply **AACS encryption** — AACS is
protected-content territory the clean-room rule excludes. So DoStudio and DiscForge aren't really
competitors: one masters encrypted commercial BD titles, the other preserves and images discs.

## Feature reality check (all tools)

Legend: ✓ full · ◐ partial / backend- or tier-dependent · ✗ none

### Burning (the overlap axis)

| Capability | CDBurnerXP | InfraRec. | AnyBurn | BurnAware Free | K3b | Nero | DiscForge |
|---|---|---|---|---|---|---|---|
| Data CD/DVD/BD burn | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ (**hardware-proven** 2026-08-10) |
| Audio-CD burn | ✓ | ✓ | ✓ | ✓ | ✓ (+CD-Text) | ✓ | ◐ (IMAPI2 TAO) |
| Copy / clone disc | ✓ | ✓ | ✓ | ✗ (paid) | ✓ | ✓ | ◐ (image→burn) |
| Verify after write | ✓ | ◐ | ◐ | ✓ | ◐ | ✓ | ✓ (CRC/MD5 + read-back) |
| BDXL | ◐ | ✗ | ◐ | ✓ | ◐ | ✓ | ◐ |
| Bitsetting / book-type set | ✗ | ✗ | ✗ | ✗ | ◐ (backend) | ✓ | ✗ (reads only) |
| **RAW DAO-96 + sub-channel** | ✗ | ✗ | ✗ | ✗ | ◐ (cdrdao) | ✗ | ✓ (**hardware-proven** 2026-08-10, direct-SPTI Write-Type-Raw: write → read-back → PASS, main + sub) |
| Encrypt user data (SecurDisc etc.) | ✗ | ✗ | ✗ | ✗ | ✗ | ✓ | ✗ (clean-room: excluded) |
| Defeat/normalise copy protection | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ (clean-room: excluded) |

*(CloneCD would be the only ✓ in that last row — which is precisely why it's out of scope for a
clean-room tool.)*

### Everything that isn't burning (DiscForge's centre of gravity)

| Capability | Any of CDBurnerXP / InfraRec. / AnyBurn / BurnAware / Nero | K3b | DiscForge |
|---|---|---|---|
| **AccurateRip** | ✗ | ✗ | ✓ |
| **Sub-channel / LibCrypt capture (preserve, not circumvent)** | ✗ | ◐ (cdrdao read) | ✓ |
| **C2 re-reads / read-offset / jitter** | ✗ | ✗ | ✓ |
| **Redump submission info** | ✗ | ✗ | ✓ (`submission-info`) |
| **DAT verify / PAR2** | ✗ | ✗ | ✓ |
| Image identify (hundreds of formats) | ✗ | ✗ | ✓ (`identify`) |
| Any-to-any image conversion | ◐ (AnyBurn: image formats) | ✗ | ✓ (`disc-convert`) |
| CHD / CSO / WBFS / XISO / ECM | ✗ | ✗ | ✓ |
| **Console / retro disc + cartridge + saves** | ✗ | ✗ | ✓ |
| Filesystem conformance linting (ISO/UDF/FAT/HFS) | ✗ | ✗ | ✓ |
| Cross-platform CLI / scriptable pipeline | ✗ | ✗ (Linux GUI) | ✓ |

None of the burners attempt any of the bottom rows. That is the point: they burn; DiscForge is a
preservation toolkit that also burns.

## Where DiscForge wins, in one screen

| Axis | Winner | Why |
|---|---|---|
| Burn an ISO on Windows, minimal fuss | **AnyBurn** | Tiny, modern, free, hardware-proven |
| Most complete paid Windows burner/suite | **Nero** | BDXL, SecurDisc, labelling, decades of proof |
| Linux burning | **K3b** | The mature KDE standard |
| Professional encrypted BD-title authoring (BD-J/AACS) | **DoStudio**-class tools | Out of clean-room scope for DiscForge |
| **Cross-platform, one engine everywhere** | **DiscForge** | The only tool of the set on Win *and* macOS *and* Linux |
| **Imaging / identify / convert / catalogue** | **DiscForge** | None of the others attempt it |
| **Redump-grade preservation** (sub-channel, C2, offsets, AccurateRip, submission info) | **DiscForge** | K3b touches sub-channel via cdrdao; the rest have none |
| **Retro / console disc + cartridge + saves** | **DiscForge** | Exclusive |
| **Preserve protection fingerprints *without* circumventing** | **DiscForge** | CloneCD amplified/defeated them; DiscForge keeps them intact |
| Still-maintained | AnyBurn / BurnAware / K3b / Nero / DiscForge | ImgBurn, CDBurnerXP, InfraRecorder, CloneCD, DoStudio are all dead |

## Honest caveats

- **For a one-off burn, these tools are easier than DiscForge today.** AnyBurn/BurnAware/Nero are
  point-and-click and battle-tested; DiscForge is CLI-first and its burn engine is only freshly
  hardware-proven on the data path (RAW-DAO still in bring-up). If the whole task is "burn this
  ISO," recommend AnyBurn (free) or Nero (paid).
- **K3b's raw/sub-channel is real but backend- and Linux-bound**, not a from-scratch raw writer.
- **Nero out-features DiscForge as a consumer burner/suite** (labelling, SecurDisc, BDXL proven),
  but is Windows-only, paid, and does none of the preservation surface.
- **CloneCD and DiscForge look similar on sub-channel but aren't the same kind of tool** —
  CloneCD circumvents protection; DiscForge preserves fingerprints without circumventing. That is
  a deliberate design line, not a missing feature.

## Bottom line

Across the *whole* alternatives landscape, over half the named tools are abandonware, and the
living ones — AnyBurn, BurnAware, K3b, Nero — are all *burners* (or, for the dead DoStudio, a BD
author). DiscForge shares only the burn axis with any of them and wins decisively on
cross-platform reach, the imaging/identify/convert/preserve/catalogue surface, Redump-grade
fidelity, and the retro/console universe — while staying on the clean-room side of the line that
CloneCD (circumvention) and Nero/DoStudio (encryption) cross. For the narrow job of burning a disc
on Windows this afternoon, AnyBurn (free) or Nero (paid) is still the friendlier pick — the same
honest split the ImgBurn comparison reaches, now measured against the full field.
