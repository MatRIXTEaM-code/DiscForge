# DiscForge v1.0.0 — first public release

DiscForge is a clean-room optical-disc preservation toolkit: it dumps, verifies,
repairs, converts and re-burns CD/DVD/BD media under one strict rule —
**provably correct or declined**. Every output is proven against independent
evidence (format-carried checksums, multi-pass consensus, reference
implementations, external databases) or the operation is refused. This is the
first release under the GPL-3.0-or-later license.

## Highlights

**Preservation-grade dumping.** Raw 2352+96 sub-channel reads with multi-pass
consensus — sub-channel Q is majority-voted across passes so a transient
mis-read can't corrupt a dump, while LibCrypt-style intentional errors are
preserved verbatim. C2 error mapping, per-sector provenance, media-quality and
read-stability scanning, and an unreadable-sector sidecar that survives every
later format conversion.

**Verified RAW DAO-96 burning.** Burns raw discs sub-channel included
(protection re-creation), then verifies by rebuilding the golden image and
comparing the disc's consensus read-back byte-for-byte. Validated end-to-end on
real hardware (Plextor PX-W5224A).

**Format breadth, integrity-gated.** BIN/CUE, CDI (native read/write), GDI,
NRG, MDS, CCD, ISO, CSO/ZSO, WBFS, XISO, ECM, RVZ/WIA decode; CHD read /
verify / extract / create (chdman-accepted, SHA-1 self-verified); AaruFormat
read (uncompressed, LZMA and FLAC — clean-room decoders validated against
liblzma and reference-encoder streams, every block gated by its stored CRC-64)
and uncompressed write; CICM metadata export; a universal any-to-any converter.

**Filesystems.** ISO 9660/Joliet/Rock Ridge, UDF read (1.02–2.50) and write
(1.02), El Torito (including BIOS+UEFI hybrid boot mastering), XDVDFS, and
read-only extraction from FAT, exFAT, NTFS, ext2/3/4 and HFS images — with
hybrid-disc cross-checking that reports divergent directory views instead of
merging them.

**Recovery.** `dforge recover` — a one-command damage assessment grading an
image INTACT / RECOVERABLE / DAMAGED / UNREADABLE with concrete next steps and
an HTML report — plus salvage planning, orphan carving and disc health mapping.

**Audio.** Secure-rip planning with conservative confidence grading
(self-consistency can never earn "Verified"; only an independent AccurateRip
match can), drive offset detection, C2-guided re-read strategies, and
ssdeep-compatible fuzzy hashing (byte- and score-exact vs ssdeep 2.14.1).

**Community workflows.** Redump-style submission info and packaging, Logiqx DAT
verification with evidence-strength labels (SHA-1 / MD5 / CRC-32), 1G1R,
TorrentZip, collection triage and library management.

**DVD/BD authoring.** DVD-Video and BDMV build/plan, automatic DVD-9
layer-break planning at ECC or VOBU boundaries, ISO mastering with automatic
dual-layer detection.

## Platforms

- **Windows:** GUI + full hardware I/O (SPTI raw MMC, IMAPI2 burning).
- **Linux/macOS:** the full analysis/conversion/filesystem CLI runs natively;
  burning via external tools; a native Linux SG_IO SCSI layer ships in this
  release with real-hardware validation still pending.

## Numbers

380 CLI commands · 2,400+ automated tests · ~113k lines across four projects ·
clean-room throughout (per-format derivation notes in `docs/`).

## Known limitations

The rare AaruFormat LZMA-subchannel-transform variant is declined pending a
real fixture; NTFS-compressed/encrypted and ext4 inline/encrypted files are
listed but not extracted; RVZ junk regions are zero-filled (data-exact, not
hash-identical); MDEC pixel decode, full VCD authoring and DVD menu authoring
are deferred. Details and rationale in the README.

## License

GPL-3.0-or-later. DiscForge detects and preserves copy protection; it never
circumvents it, and never decrypts encrypted content — a deliberate, permanent
limitation (see NOTICE).
