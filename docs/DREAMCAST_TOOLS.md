# Dreamcast toolchain — what maps into DiscForge

An audit of the classic Dreamcast app/patcher set against DiscForge, in the same
spirit as docs/COMPARISON.md. The question is not "can we clone each tool" but
"what function does it perform, and where does that sit relative to DiscForge —
already done, worth building, or out by the clean-room rule."

Verdicts: **✔** already covered · **◐** partial / a small in-scope addition ·
**✗** not present but in-scope and worth building · **⛔** out of scope by the
clean-room rule (boot-security or protection circumvention) · **—** different
product.

## The pattern

Strip the duplicates and front-ends and the 34 tools are really six jobs:

1. **PPF patching** — the largest group, and DiscForge's strongest column here.
2. **CDI / image handling** — rip, fix, convert, burn.
3. **Raw↔cooked and track conversion** — BIN/ISO, track merging.
4. **ISO base-LBA / header fixes** — the GD-ROM 45000-offset problem.
5. **Region / connectivity patches** — delivered as patch files.
6. **Dummy-file padding for self-boot CD-R** — the one whole category that is out.

## PPF patching — ✔ covered, with two small additions worth making

| Tool | Job | Verdict |
|------|-----|---------|
| PPF-O-Matic | Apply / create PPF | ✔ (`ppf-apply` / `ppf-create`) |
| PPF Patch Engine | Apply PPF | ✔ |
| PPF Utilities | Apply / inspect PPF | ✔ (`ppf-info`) |
| AmiPPF | PPF apply (Amiga port) | ✔ (same format) |
| BinPATCH | Apply a binary patch to a track | ✔ via PPF (`ppf-apply`) |
| PPF Converter | Convert between PPF 1.0 / 2.0 / 3.0 | ◐ — we read all three, write 3.0; a downgrade writer is a small add |
| PPF Editors | Edit a patch's description / records / file_id | ◐ — a metadata editor over our parser is a small add |

DiscForge's PPF engine already reads v1/v2/v3, writes v3 with undo + validation,
applies, reverts and creates. The only gaps the list reveals are **writing PPF
1.0/2.0** (a "convert down" for older tools) and a **PPF metadata editor** (change
the description or file_id.diz without rebuilding). Both are small, both are
in-scope, both would come free off the existing `PpfPatch` model. Worth adding.

## CDI and image handling

| Tool | Job | Verdict |
|------|-----|---------|
| CDIrip | Extract tracks from a DiscJuggler CDI | ✔ (`extract`, `extract-files`) |
| CDIfix | Repair a CDI's track descriptors | ✔ (`fix-modes`) |
| CDI Burner | Burn a CDI to disc | ◐ — IMAPI2 data/ISO burn done; RAW DAO stubbed pending hardware |
| CDI Suite | Bundle of the above | ✔ (covered by the parts) |
| CDI2NERO (+GUI) | Convert CDI → Nero NRG | ✔ — `NrgConverter` (`dforge convert .cdi .nrg` / `.nrg .cdi`), NER5 read/write, round-trip validated; real-Nero validation pending a sample |
| Generic Driver for DJ | DiscJuggler burn driver | — (DiscForge burns via SPTI/IMAPI2, not DJ) |

CDI is the format DiscForge was built around, so ripping and fixing are done.
The one genuine new capability here is **NRG (Nero) conversion** — DiscForge reads
CDI/ISO/BIN-CUE/MDS/CCD; adding NRG read/write would let it interop with the Nero
half of the scene. In scope (NRG is a documented container), a reasonable
roadmap item.

## Raw↔cooked and track conversion

| Tool | Job | Verdict |
|------|-----|---------|
| BIN2ISO | Raw 2352 BIN → cooked 2048 ISO | ✔ (`convert`; SectorAccess cooks Mode-1/2) |
| Raw2Iso (+GUI) | Same, raw → ISO | ✔ |
| TrackMerge | Combine separate track files into one | ◐ — feeds the GDI↔CDI conversion increment already queued |
| ISO2Mac & Mac2ISO | ISO ↔ Mac (MacBinary/HFS) container | — different product |

Cooking raw sectors to an ISO is something DiscForge already does throughout its
convert and extract paths. **TrackMerge** is interesting because merging a
GD-ROM's separate track files is exactly a step in the **GDI ↔ CDI conversion**
increment I flagged as next — so that tool's job lands naturally there.

## ISO base-LBA and header fixes — the useful GD-ROM-specific ones

| Tool | Job | Verdict |
|------|-----|---------|
| ISO LBA Fix Utility | Rebase an ISO's LBAs to the GD-ROM 45000 offset | ✗ — directly enables the browse increment; worth building |
| ISO Header Extractor | Pull the IP.BIN / boot header out of an image | ✗ — a small in-scope read feature (display the disc's IP.BIN metadata) |
| ISO Header Extractor (as bootstrap reader) | Read region, product no., title from IP.BIN | ✗ — genuinely useful, in scope |

**These two are the most actionable finds on the list.** The **ISO LBA Fix** job
is the precise thing standing between us and browsing a GD-ROM's game filesystem:
the high-density data track's ISO is addressed from LBA 45000, and handling that
rebase is the browse increment. And an **IP.BIN header reader** — showing a
Dreamcast image's region, product number and title from its boot header — is a
small, purely-read feature that fits `gdi-info` / a new `dc-info` perfectly. I'd
build both.

## Region and connectivity patches

| Tool | Job | Verdict |
|------|-----|---------|
| Dreamcast PAL-2-NTSC Patcher | Change the video-mode / region flag | ◐ — as an edit list it is a PPF (`ppf-apply`); a built-in "flip region" needs care |
| PAL Patcher | Region patch | ◐ (same) |
| A4Patcher | Region + self-boot patcher | ⛔/◐ — the region edit is fine; the self-boot half is out |
| Adrenalin Patcher | Region / self-boot patcher | ⛔/◐ — same split |
| Internet Fixer | Repoint a game's DNS/ISP to revival servers | ✔ via PPF — a community patch DiscForge applies |

Region and connectivity changes are legitimate edits to a disc you own, and they
are overwhelmingly distributed **as PPF** — which DiscForge already applies. So
"apply a region patch" and "apply an Internet-Fixer patch" are covered today. The
line is only crossed when a tool **bundles self-boot conversion** (A4Patcher,
Adrenalin do): the region byte-flip is fine, the boot-security bypass is not, so
DiscForge would apply such a patch but never generate the self-boot half.

## Dummy-file padding — ⛔ the one category that stays out

| Tool | Job | Verdict |
|------|-----|---------|
| AutoDummy, Dummy Add (+FrontEnd), Dummy Calc, Dummy File Creator / Maker, DummyFile, NewFile, ZeroTools, DC-Cdr | Create zero-filled padding files to push real data to a CD-R's outer edge for a self-booting burn | ⛔ |

Every one of these exists to make a **self-booting CD-R copy**: they pad the image
with dummy data so the game lands at the disc radius the MIL-CD boot exploit
needs. That is boot-security circumvention, which DiscForge does not do — the
same clean-room line held everywhere else (docs/COMPARISON.md §13). Note the
padding *file* itself is innocuous (a run of zeros); it is the **purpose** —
enabling unsigned self-boot — that puts the whole category out. DiscForge images,
validates and patches; it does not lay out self-booting media.

## What this list tells us to build

Filtering to in-scope and genuinely additive, the list surfaces a tidy shortlist,
most of it already on the roadmap:

1. **ISO base-LBA rebase → browse the GD-ROM game filesystem.** The single most
   useful item (ISO LBA Fix). Already the next Dreamcast increment; the list
   confirms its value.
2. **GDI ↔ CDI conversion** (TrackMerge's job). Already queued.
3. **IP.BIN header reader** — region / product / title from a Dreamcast image.
   Small, purely read, in scope. New, worth adding to `gdi-info`.
4. **PPF down-conversion (write 1.0/2.0) and a PPF metadata editor** (PPF
   Converter, PPF Editors). Small additions over the existing engine.
5. **NRG (Nero) read/write** (CDI2NERO). A larger but legitimate interop add.

Everything else is either already done (the PPF core, CDIrip, CDIfix, BIN2ISO) or
out by design (the entire dummy-file / self-boot set).
