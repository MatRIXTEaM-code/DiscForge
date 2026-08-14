# DVD-Video authoring assembler

DiscForge assembles an already-authored `VIDEO_TS` folder into a burnable, conformant
DVD-Video image — the job ImgBurn's Build mode does for DVD-Video. It does **not**
transcode or author menus (supply a compliant `VIDEO_TS` from dvdauthor, a ripper, etc.);
it takes that folder and lays it out correctly on the disc.

```
dforge dvd-video-plan  VIDEO_TS/                 # validate + show the on-disc order
dforge dvd-video-build VIDEO_TS/ movie.iso --volume MY_MOVIE
```

Either command accepts the `VIDEO_TS` folder itself or a parent containing one.

## What it does

- **Validates** the file set (`DvdVideoLayout`): `VIDEO_TS.IFO` is mandatory; every Video
  Title Set needs `VTS_nn_0.IFO`; title VOBs (`VTS_nn_1.VOB`…) must be contiguous from 1;
  no VOB may exceed the 1 GB DVD ceiling. A missing `.BUP` is a warning, not an error.
- **Orders** the files in the exact sequence the DVD-Video spec requires: the Video
  Manager first (`VIDEO_TS.IFO`, its optional menu VOB, then `VIDEO_TS.BUP`), then each
  title set 01..99 — `VTS_nn_0.IFO`, the optional menu `VTS_nn_0.VOB`, the title VOBs
  `VTS_nn_1.VOB`…`VTS_nn_9.VOB`, then `VTS_nn_0.BUP`. The control IFO leads and its backup
  BUP trails, separated by the VOBs so a surface defect can't destroy both.
- **Assembles** an ISO 9660 + UDF 1.02 **bridge** image (via `UdfBridgeBuilder`), with the
  files in a `VIDEO_TS` directory beside an empty `AUDIO_TS`. Because the ISO builder lays
  file *data* down in the order the children are supplied, the on-disc data order is the
  DVD-Video order above even though the directory *records* stay alphabetically sorted (an
  ISO 9660 requirement).

## Validation

`DvdVideoLayout` is covered by unit tests (classification, ordering, multi-title-set
ordering, and the validation rules). The assembled image is checked end-to-end against
`udfinfo` (valid UDF 1.02, correct file/dir counts) and `isoinfo` (ISO 9660 listing), and
the file **data** order is confirmed by starting-LBA to match the DVD-Video sequence — IFO
before VOB before BUP within the Video Manager, and each title set's IFO well separated
from its BUP.

## "Fix VTS Sectors" — IFO sector-pointer verification

Both `dvd-video-plan` and `dvd-video-build` read each IFO's internal sector pointers and
check them against the actual file layout — the consistency ImgBurn's **Fix VTS Sectors**
guards (`DvdVideoIfo`). For each title set it verifies `VTSI_LAST_SECTOR` (the IFO size),
that the BUP is an exact-size copy, `VTSM_VOBS` (menu VOB right after the IFO),
`VTSTT_VOBS` (title VOB after IFO + menu) and `VTS_LAST_SECTOR` (IFO + menu + title + BUP −
1); the Video Manager IFO is checked the same way. A faithfully-authored folder passes; a
mismatch means the source was edited without updating its IFOs (its disc would
mis-navigate) — `plan` reports it, `build` warns and proceeds (the files are placed exactly
as given).

`dforge dvd-video-fix VIDEO_TS/` is the **write** half: it recomputes each IFO's four
file-location pointers (`VTS_LAST_SECTOR`, `VTSI_LAST_SECTOR`, `VTSM_VOBS`, `VTSTT_VOBS`, and
the `VMG` trio) from the folder's actual file sizes and rewrites them in place, then refreshes
every `.BUP` as an exact byte-for-byte copy of its `.IFO`. Only those whole-file / VOB-location
pointers move; the IFO's internal PGC and table pointers (relative to the IFO's own start) are
left untouched, matching the narrow scope of ImgBurn's "Fix VTS Sectors". It is a dry-run
preview by default — pass `--apply` to write. Because the rewrite sets the pointers to exactly
what the (independently tested) verifier expects, a `dvd-video-fix --apply` followed by
`dvd-video-plan` always reports "pointers agree with the file layout".

## BD-Video (BDMV)

`dforge bdmv-plan` / `bdmv-build` do the Blu-ray equivalent. Blu-ray uses a **pure UDF 2.50
filesystem** (no ISO 9660), which DiscForge's writer now produces, so `bdmv-build`
validates the `BDMV/` structure — `index.bdmv`, `MovieObject.bdmv`, `PLAYLIST/*.mpls`,
`CLIPINF/*.clpi`, `STREAM/*.m2ts`, and a `BACKUP/` of the control files (`BdmvLayout`) —
and assembles it into a UDF 2.50 image (validated against `udfinfo`: `udfrev=2.50`, no
warnings). It accepts a disc root containing `BDMV/` or the `BDMV/` folder itself.

## Scope and follow-ups (honest)

- **This is assembly, not authoring.** No transcoding, no menu generation, no playlist/clip
  parsing. The input `VIDEO_TS` / `BDMV` folder must already be compliant.
- **Rewriting** the IFO sector pointers (the write half of "Fix VTS Sectors", for *edited*
  title sets) is now shipped as `dvd-video-fix`; both verify and rewrite are done.
- **IFO/BUP ECC-block padding** (aligning each file to a 16-sector ECC block, as a mastered
  disc does) is still pending — it is deliberately *not* guessed here: the padding is coupled to
  the VTS-relative pointer values, and getting that coupling right needs validation against a
  real mastered DVD-Video fixture, which is hardware/disc-bound. DiscForge's current layout is
  contiguous with self-consistent pointers.
- **Player-verified playback** can't be checked in CI; the images are validated
  structurally (filesystem + on-disc order + IFO pointer consistency). A real player/drive
  confirms playback.
