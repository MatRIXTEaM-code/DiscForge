# Where DiscForge stands — a field comparison

*Written 2026-07-23, against the working tree as of the structure-analysis
rework (post-1.9.10). A point-in-time audit: DiscForge measured against the
classic CD/DVD tool landscape, app by app.*

How to read the verdicts:

| Mark | Meaning |
|------|---------|
| ✔ | Covered — DiscForge does what this tool was for, or the useful core of it |
| ◐ | Partial — the heart is there, named gaps remain |
| ✗ | Gap — not present, and it plausibly belongs on the roadmap |
| ⛔ | Out of scope by policy — the tool's purpose is protection circumvention, which DiscForge does not do and will not add |
| — | Different product category — not something DiscForge is trying to be |

Two standing caveats apply throughout. First, everything on the Devices side
(reading, burning, scanning real media) is implemented but **largely untested
against physical hardware** — parity claims there are claims about code, not
about burns. Second, RAW DAO-96 writing is stubbed pending a capable drive, and
that one stub is what separates DiscForge from "covered" on most of the
protected-imaging column.

---

## 1. Imaging and burning suites

*Alcohol 120%, CDRWIN (and the [D4Y] build), CloneCD, BlindWrite Suite, Nero
Burning ROM, ImgBurn, DiscJuggler, Easy CD/DVD/MEDIA Creator, DeepBurner,
Burning Studio, BurnYa!, CD Recording Suit, CopyToDVD, DropToCD, FunCD, NTI
CD&DVD-Maker, Sateira CD&DVD Burner, Dual-Burner / Data-Burner / MP3-Burner,
MP3 CD Burner, Feurio!, InstantCopy, PowerISO*

This is DiscForge's home ground. Reading a disc to an image (`ReadView`,
`DiscReader`, cooked/raw planning), building images from files (`IsoBuilder`
with Joliet, Rock Ridge and El Torito boot), converting between CDI, ISO,
BIN/CUE, MDS/MDF and CCD, burning data and ISO via IMAPI2, track-at-once audio
burning, erase and speed control, checksums, splitting, and verify-after-burn
are all present in both CLI and GUI.

Where the suites still win:

- **RAW DAO-96 writing** — the CDRWIN/CloneCD/Alcohol signature move. Planner
  done, engine stubbed pending hardware. The single biggest gap in the column.
- **Packet writing / drive-letter use** (Nero InCD, Roxio DirectCD) — out of
  scope by design; DiscForge's no-kernel-drivers rule is load-bearing.
- **Booktype / bitsetting** — reading the book type is done
  (`MediaIdentity.BookType`); *setting* it is not implemented.
- **Video-disc authoring and label printing** (Nero, Roxio, Burning Studio) —
  not present; DVD-Video *authoring* (building a playable VIDEO_TS from clips)
  is a different product than imaging it.
- **MP3 → audio CD in one step** — `AudioCdCreator` deliberately takes
  44.1/16 WAV only; MP3 must pass through `transcode` first. A convenience
  join, not a capability gap.
- **DiscJuggler's multi-drive duplication** — the burn planner models the
  every-drive-simultaneously case; the orchestration around real hardware is
  still ahead. This is the founding parity target, so it stays ◐ until it
  burns.

CDRWIN gets a footnote no other row gets: DiscForge ships a loving CDRWIN-era
retro skin (`RetroTheme`, `CdrwinLauncher` — see docs/RETRO_SKIN.md), so it is
the one competitor DiscForge can *impersonate*.

## 2. The CDI lineage

*DiscJuggler (format), CDIrip by DeXT*

The reason the project exists, and the strongest column. CDI parse, write,
extract (ISO / raw / audio→WAV), verify, compare, mode repair, and round-trip
conversion to BIN/CUE and ISO — validated byte-for-byte against a real cdi4dc
image and a synthetic matrix. CDIrip's job (pull tracks out of a CDI) is fully
covered. Remaining honesty: richer DiscJuggler-authored descriptor variants
still await a real DJ image to validate against (the "wild descriptor" TODO).
**✔** for CDIrip, **◐-high** for DiscJuggler itself (multi-drive burning, above).

## 3. Protected imaging and raw readers

*BlindRead, DDump, SubSonic Reader, The CloneR, DiabloLabs Replicator,
Alternate CDFS.VXD, CloneCD (read side)*

Raw 2352 reads with C2 pointers (`C2SectorReader`), sub-channel capture as a
sidecar pass (`SubchannelCapture`, `.sub`), CCD emit (`to-ccd`), weak-sector
preservation guidance, and the recovery view's retry ladder cover what the raw
readers did. Alternate CDFS.VXD was a Win9x kernel driver — permanently out of
scope by design rule. Verdict for the group: **◐/✔**, gated on hardware
validation rather than missing code.

## 4. Copy-protection identification

*ClonyXXL, Protection ID, TCCD, YaPS, CD Protection Detective / Detector /
Scout, CD Inquery, Copy Protection Detection, Scout Easyscan, CD Protection
databases (CloneCD Database, PC CopyBase), Perfect Suite*

`ProtectionScanner` (`dforge scan-protection`, `ProtectionView`) detects
LibCrypt, SafeDisc, SecuROM, Laserlock and SafeDisc-style weak-sector clusters
from public fingerprints, with evidence and preserve-verbatim guidance —
detection, never circumvention. That covers the core of what the scanners did.
Where the old tools go further: Protection ID also fingerprinted *executables*
(StarForce, Tagès and dozens more), and ClonyXXL leaned on a community
database; DiscForge has neither an exe scanner nor an online DB. **✔ on the
disc-level core, ◐ against the widest scanners.** The database "tools"
(CloneCD Database, PC CopyBase) were communities more than programs — marked —.

## 5. Drive and media information

*CD-Info, CDVDInfo, DISCInfo, DVDINFO, CD/DVD Capabilities Viewer, Plextor
CDVDInfo, WinXP CD-R/RW Properties, CDR Identifier, CD-R ATIP Reader, DVD
Identifier*

Fully in hand: GET CONFIGURATION + mode page 2Ah capability interrogation
(`DriveCapabilities`, `DriveCapabilityPage`), ATIP manufacturer decode with
dye table (`MediaIdentity.ParseAtip` — the 97m26s66f trick), DVD/BD media ID
and ADIP, book type, CSS presence reporting (reported, never touched), all
surfaced in `DrivesView`. **✔ across the group.**

## 6. Quality scanning and diagnostics

*DVDInfoPro, Opti Drive Control, CD-RW Diagnostic 2000, CD Bremse, CCD4-Profiler*

C2-based surface scanning with error maps (`DiscQualityScanner`, `C2ErrorMap`,
`QualityView`, log export) and drive speed control (`DriveSpeed` — CD Bremse's
whole reason to exist) are done. The gap: **PIE/PIF scanning for DVD media**
(the DVDInfoPro/Opti Drive Control headline) is not implemented — C2 is a CD
concept, and the DVD-equivalent scan commands are vendor-specific. Transfer
-rate benchmarking is also absent. **◐.**

## 7. Verification and comparison

*BurnCompare, CDCheck, CDCRC, CD Vergleich*

`verify` (structural + per-track CRC-32), `checksum` (MD5/SHA per image and
track), `compare` (image↔image, track-aware), plus verify-after-burn. **✔.**

## 8. Audio: extraction and authoring

*Feurio!, the audio half of every suite*

The EAC-shaped problem set is covered in code: DAE with overlapping-read
jitter correction (`JitterCorrection`, docs/AUDIO.md), AccurateRip checksum
verification against the database (`AccurateRip`, `accuraterip`), CD-TEXT
building, gap handling, Red Book compilation from WAV with capacity checks.
Feurio's track-editing/crossfade studio side is absent — DiscForge rips and
burns audio, it does not edit it. **◐-high, hardware caveat applies.**

## 9. Cue, sector and subcode utilities

*CDRCue Cuesheet Editor, Mode2 CD Maker, Subcode Analyzer, Burn to the Brim*

- Cue editing: `CueEditorView` with the explicit colour-coded Check button,
  backed by `CueValidator`'s against-the-file arithmetic. **✔** (stronger than
  CDRCue — it validates claims against the BIN, not just syntax).
- Subcode: `SubcodeView`, `dforge subch` / `pq` — decode, edit, analyze. **✔.**
- Disc packing: `DiscPacker` / `PackView` — first-fit-decreasing with folder
  grouping. Burn to the Brim's exact niche. **✔** (landed yesterday).
- Mode 2 / XA authoring (Mode2 CD Maker, the VCD substrate): raw sector tools
  exist (`build-raw`, `fix-modes`, `RawSectorBuilder`) but there is no
  dedicated Mode2 Form2 image *authoring* path. **◐.**

## 10. Image browsing, extraction and recovery

*IsoBuster, PowerISO (reader half)*

Streamed ISO 9660 / Joliet / Rock Ridge / UDF browsing straight out of images
(`ImageBrowser`, `dforge ls` / `extract-files`, validated against isoinfo),
plus the recovery view's damaged-media handling. IsoBuster's long tail —
HFS/HFS+, exotic session-recovery forensics, per-session mounting of
multisession relics — is not matched. **◐-high.**

## 11. Mounting

*Alcohol / PowerISO virtual drives*

`VirtualDisc` / `MountView` mount an image *inside DiscForge* for browsing and
extraction. An OS-level drive letter needs a kernel storport driver — out of
scope by design, permanently. Windows itself mounts ISO; DiscForge covers CDI
and the rest in-app. **◐ by choice; the missing half is deliberate.**

## 12. DVD-Video: structure, shrink, transcode

*DVD2one, Movie Shrink & Burn, DVD95Copy, CloneDVD, Easy DVD Copy, IfoEdit,
IFOUpdate, DVDToolbox, TMPGEnc, DVDx, VidCoder, MKVToolNix, DVD2SVCD, VCDEasy,
SubRip, NUMenu4U*

The clean-room DVD Shrink parity work: IFO structure reading (`IfoReader`),
obfuscated-structure detection by capacity arithmetic (`StructureAnalysis` —
finished this session), reauthor selection (`ReauthorPlanner`), bit-budget
fit planning (`BitBudget` — the actual DVD Shrink arithmetic, with per-title
automatic/manual/untouched modes), and execution through an installed FFmpeg
(`TranscodePlanner` / `FfmpegRunner`, single-pass, two-pass, stream-copy,
H.264/HEVC/MPEG-2 to mp4/mkv/DVD-compliant streams). CSS-encrypted payload is
refused, always.

Honest gaps in the column:

- **IFO *editing*** (IfoEdit's other half, IFOUpdate) — DiscForge reads and
  plans but does not yet rewrite IFO tables, which a full reauthor-and-burn
  needs. ◐, and probably next on this front.
- **UDF authoring** — DiscForge *reads* UDF but builds only ISO 9660 family
  filesystems; burning back a shrunk, playable DVD-Video needs a UDF bridge
  build. ✗, roadmap-worthy.
- SVCD/VCD authoring (DVD2SVCD, VCDEasy) ✗; subtitle OCR (SubRip) — (ffmpeg
  stream extraction only); DVD menu creation (NUMenu4U) ✗ and likely stays so.
- TMPGEnc/VidCoder/MKVToolNix as *encoders/muxers* — the transcode pipeline
  covers the shrink-relevant slice via FFmpeg; it does not try to be a video
  editor. ◐ and content to stay there.

## 13. Ripping and decryption — out of scope, permanently

*DeCSS, vStrip, VobDec, DecodeVOB, SmartRipper, DVD Decrypter, cladDVD, DOD
DVD Speed Ripper, DVDCopy, DVDrip, DVD:Reaper, DVD-RIPP, DVD-Finaly, DVD
Master, Power Ripper, RipItAll, RipIt4Me, Rurouni Grabber, Quintuplets 2000,
EasyDivX (rip half), DeMacroVision, DeMPAA, X-Copy, 1Click DVD Copy, CloneDVD
+ AnyDVD, DVDFab, DVDFab Passkey, DUP-DVD, DVD-Cloner, Super DVD Copy,
VideoMatrix, VOBrator*

These exist to defeat CSS, Macrovision, region coding or structural
protection. DiscForge's clean-room rule is explicit: it detects protection,
reports it, preserves it verbatim in faithful backups where lawful, and never
bypasses it. The *unencrypted* slice of what the copy apps did — copy a disc
you authored, shrink an unprotected DVD, image and duplicate — is covered by
sections 1 and 12. Everything else here is **⛔** and stays that way. (DVD
Decrypter's descendant is ImgBurn, whose non-decryption feature set is the
section-1 comparison.)

## 13a. PlayStation patching — PPF

*PPF-O-Matic, PPF Patch Engine, PAL4U (and PAL region patchers generally)*

Added after the first audit. `PpfPatch` (`dforge ppf-apply` / `ppf-create` /
`ppf-info`, and the PPF Patch GUI view) reads all three PPF revisions, writes
PPF 3.0 with undo data and a validation block, applies and reverts patches, and
refuses an image whose validation fingerprint does not match. That covers
PPF-O-Matic (apply + create) and the PPF Patch Engine directly; PAL region
patchers like PAL4U ship their edits as PPF, so applying those patches is the
same path. This is patch *application* — an edit list the user already holds,
applied to a backup they own — never protection circumvention. See
docs/PS1_BACKUP.md for the full read-patch-verify workflow. **✔.**

The PS1 *backup* half (raw 2352 Mode 2 imaging, CD-DA tracks, LibCrypt
subchannel capture and detection, BIN/CUE) was already covered by sections 1, 3
and 4; PS1_BACKUP.md ties it to the patch step.

## 13b. Console backups — Dreamcast (started), PS2, Xbox

Dreamcast is complete on the image side: `GdiParser`/`GdiValidator`
(`dforge gdi-info`) read and validate the .gdi index, `GdiBrowser`
(`dforge gdi-browse`) browses and extracts the high-density game filesystem via
base-LBA reading, `IpBin` shows the boot header (region/title), PPF patches the
game track, and `GdiConverter` (`dforge convert`) converts GDI ↔ CDI preserving
the two-session GD-ROM layout byte-for-byte. CDI was already native. Honest limit:
no PC drive can read a GD-ROM's high-density area, so dumping from the physical
disc needs the console; self-boot authoring is out of scope by the clean-room
rule.

**Xbox**: `XdvdfsReader`/`XdvdfsBuilder` (`dforge xiso-ls` / `create-xiso`) read
and write the XDVDFS filesystem (XISO and XGD1-based dumps), with a GUI tile.
**PS2**: a DVD/CD image, imaged and PPF-patched with the existing paths, plus
`SystemCnf` (`dforge ps2-info`) to identify a PS1/PS2 disc — console, serial,
region, video mode — from its SYSTEM.CNF (see docs/PS2_BACKUP.md). None of this
decrypts protection —
a GD-ROM carries none, XDVDFS carries none, and the imaging/patch work sits on the
preservation side of section 13's line; Xbox disc *security* (outside the
filesystem) is neither read nor defeated.

## 14. Different products altogether

*NTI Backup NOW! (scheduled backup), RSJ CD Writer (packet-writing
filesystem), DVD Decoder (playback codec), cladMdec (PSX MDEC decoder),
CD-i/authoring curios (Traction CD Menu Creator ✗-minor, DropToCD covered
above), CCD4-Profiler (CloneCD tweaker)*

Marked — : being a backup scheduler, a packet-writing driver or a software
DVD player is not on DiscForge's map. Traction-style autorun menu generation
for data discs would be a small, fittingly retro feature if ever wanted; noted
as the one ✗ here that is even plausible.

---

## The roll-call

| App | Category | Verdict |
|-----|----------|---------|
| Alcohol 120% | Imaging suite | ◐ |
| CDIrip (DeXT) | CDI | ✔ |
| BlindWrite Suite | RAW imaging | ◐ |
| Burning Studio | Burning suite | ◐ |
| BurnYa! DataCD/AudioCD | Burner | ✔ |
| CD Recording Suit | Burner | ✔ |
| CDRWIN / CDRWIN [D4Y] | DAO burning | ◐ (RAW write stub; skin homage ✔) |
| CloneCD | Protected imaging | ◐-high |
| CloneDVD | DVD copy | ◐ (unencrypted only) |
| CopyToDVD | Burner | ✔ |
| DeepBurner | Burner | ✔ |
| DiscJuggler | CDI suite | ◐-high (multi-drive burn pending) |
| DropToCD | Burner | ✔ |
| DVD2one | DVD shrink | ◐ |
| Dual-/Data-/MP3-Burner | Burner | ◐ (MP3 via transcode) |
| Easy CD/DVD/MEDIA Creator | Suite | ◐ |
| Easy DVD Copy | DVD copy | ◐/⛔ |
| Feurio! | Audio | ◐-high |
| FunCD | Burner | ✔ |
| ImgBurn | Imaging | ◐-high (bitsetting ✗) |
| InstantCopy/CD/DVD | Copy | ◐ |
| Movie Shrink & Burn | DVD shrink | ◐ (UDF authoring ✗) |
| MP3 CD Burner | Audio burn | ◐ |
| Nero Burning ROM | Suite | ◐ |
| NTI Backup NOW! | Backup | — |
| NTI CD&DVD-Maker | Burner | ✔ |
| RSJ CD Writer | Packet writing | — (by design) |
| Sateira CD&DVD Burner | Burner | ✔ |
| X-Copy | DVD decrypt-copy | ⛔ |
| CD Inquery | Protection scan | ✔ |
| CD Protection Detective | Protection scan | ✔ |
| CD Protection Detector | Protection scan | ✔ |
| CD Protection Scout | Protection scan | ✔ |
| CD-Info | Drive/media info | ✔ |
| CD/DVD Capabilities Viewer | Drive info | ✔ |
| CDR Identifier | ATIP | ✔ |
| CD-R ATIP Reader | ATIP | ✔ |
| CDVDInfo | Drive/media info | ✔ |
| CloneCD Database | Community DB | — |
| ClonyXXL | Protection scan | ◐ (no exe scan / DB) |
| Copy Protection Detection | Protection scan | ✔ |
| DISCInfo | Drive/media info | ✔ |
| DVD Identifier | Media ID | ✔ |
| DVDInfoPro | DVD info/scan | ◐ (PIE/PIF ✗) |
| PC CopyBase | Community DB | — |
| Perfect Suite | Protection tools | ◐ |
| Plextor CDVDInfo | Drive info | ✔ |
| Protection ID | Protection scan | ◐ (disc-level only) |
| Scout Easyscan | Protection scan | ✔ |
| TCCD | Protection scan | ✔ |
| WinXP CD-R/RW Properties | Drive info | ✔ |
| YaPS | Protection scan | ✔ |
| BurnCompare | Compare | ✔ |
| CDCheck | Verify | ✔ |
| CDCRC | Checksum | ✔ |
| CD Vergleich | Compare | ✔ |
| CD Bremse | Drive speed | ✔ |
| Burn to the Brim | Disc packing | ✔ |
| CD-RW Diagnostic 2000 | Diagnostics | ◐ |
| CCD4-Profiler | CloneCD tweaker | — |
| CDRCue Cuesheet Editor | Cue editing | ✔ |
| Mode2 CD Maker | Mode2 authoring | ◐ |
| Subcode Analyzer | Subcode | ✔ |
| Traction CD Menu Creator | Autorun menus | ✗ (minor) |
| cladDVD | Ripper | ⛔ |
| cladMdec | PSX decoder | — |
| DecodeVOB | Decrypter | ⛔ |
| DeCSS | Decrypter | ⛔ |
| DeMacroVision | Circumvention | ⛔ |
| DeMPAA | Circumvention | ⛔ |
| DOD DVD Speed Ripper | Ripper | ⛔ |
| DVDCopy | Decrypt-copy | ⛔ |
| DVDFab | Decrypt-copy | ⛔ |
| DVDrip | Ripper | ⛔ |
| DVDToolbox | DVD utilities | ◐ |
| DVD Decoder | Playback codec | — |
| DVD Decrypter | Decrypter | ⛔ (unencrypted imaging half: see ImgBurn) |
| DVD Master | Ripper | ⛔ |
| DVD:Reaper | Ripper | ⛔ |
| DVD-Finaly | Ripper | ⛔ |
| DVD-RIPP | Ripper | ⛔ |
| EasyDivX | Rip+encode | ⛔ (encode slice ◐) |
| IfoEdit | IFO read/edit | ◐ (read ✔, edit ✗) |
| IFOUpdate | Reauthor IFO patch | ◐ (planner only) |
| ImgBurn (listed twice) | — | see above |
| NUMenu4U | DVD menus | ✗ |
| Power Ripper | Ripper | ⛔ |
| Quintuplets 2000 | Ripper | ⛔ |
| RipItAll | Ripper | ⛔ |
| RipIt4Me | Ripper | ⛔ |
| Rurouni Grabber | Ripper | ⛔ |
| SmartRipper | Ripper | ⛔ |
| SubRip | Subtitle OCR | — (stream extract via ffmpeg ◐) |
| TMPGEnc | MPEG encoder | ◐ (via ffmpeg) |
| VCDEasy | VCD authoring | ✗ |
| VideoMatrix | VOB tools | ⛔ |
| VobDec | Decrypter | ⛔ |
| VOBrator | VOB tools | — |
| vStrip | CSS strip | ⛔ |
| Alternate CDFS.VXD | Kernel driver | — (by design) |
| BlindRead | Raw reader | ◐/✔ |
| DDump | Raw reader | ✔ |
| DiabloLabs Replicator | Copier | ◐ |
| IsoBuster | Browse/recover | ◐-high |
| SubSonic Reader | Subchannel read | ✔ |
| The CloneR | Copier | ◐ |
| 1Click DVD Copy | Decrypt-copy | ⛔ |
| CloneDVD + AnyDVD | Decrypt-copy | ⛔ |
| DUP-DVD | Decrypt-copy | ⛔ |
| DVD-Cloner | Decrypt-copy | ⛔ |
| DVD95Copy | DVD shrink | ◐ (unencrypted) |
| Super DVD Copy | Decrypt-copy | ⛔ |
| DVDINFO | Media info | ✔ |
| Opti Drive Control | Quality/benchmark | ◐ |
| DVDFab Passkey | Driver decrypter | ⛔ |
| PowerISO | Image suite | ◐-high |
| DVDx | Transcoder | ◐ |
| DVD2SVCD | SVCD pipeline | ✗ |
| MKVToolNix | MKV muxer | ◐ (via ffmpeg) |
| VidCoder | Transcoder | ◐ |
| PPF-O-Matic | PPF apply/create | ✔ |
| PPF Patch Engine | PPF apply | ✔ |
| PAL4U Patcher | PAL region patch (PPF) | ✔ |

## The score

Of the ~120 tools listed: roughly **35 ✔ covered**, **35 ◐ partial**, **30 ⛔
out of scope by policy** (the entire decryption economy), **10 —** different
products, and only **4 ✗ genuine gaps** (UDF authoring for DVD-Video burn-back,
VCD/SVCD authoring, DVD menu creation, autorun menu creation).

Reading it as a roadmap rather than a scoreboard, the pattern is clear:

1. **RAW DAO-96 engine** converts a dozen ◐ rows (CDRWIN, CloneCD, Alcohol,
   BlindWrite, DiscJuggler) to ✔ in one stroke — blocked on hardware, not code.
2. **UDF bridge authoring + IFO rewrite** completes the DVD Shrink story
   end-to-end (sections 12's two named gaps) — pure code, no hardware needed.
   *Status: UDF 1.02 authoring is now implemented (`UdfBuilder` / `dforge
   create-udf`, round-trip validated) — the first half. Remaining: a streamed
   writer for full DVD-9 sizes, and IFO rewriting so a reauthored VIDEO_TS is
   internally consistent.*
3. **PIE/PIF DVD quality scanning** and **booktype/bitsetting** are the two
   drive-feature gaps the info-tool crowd would notice.
4. Hardware validation of the read/burn/rip paths is what turns this whole
   document's "in code" caveats into claims.

Everything ⛔ stays ⛔. That is not a gap; it is the boundary that lets the
rest of the project exist.
