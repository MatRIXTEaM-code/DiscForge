# The physical-dump half of a Redump-grade rip

DiscForge already produces the *software* half of a redump.org submission: given a
finished dump in any format it reads, `submission-info` emits the per-track and
whole-image CRC-32 / MD5 / SHA-1, the cuesheet, and the sub-channel / LibCrypt
summary, with the physical fields left for the submitter (see the Submit tile and
`docs/DAT_VERIFY.md`). This document designs the other half: producing a dump that
is *correct enough to submit* straight off a physical drive — the read-offset
handling, the guard-band over-read, the C2 re-read loop, and the verbatim
sub-channel capture that separate a Redump-grade rip from a casual one.

The pure arithmetic and analysis this needs is now implemented and tested in Core
(`ReadOffset`, `Silence`, and the existing `AccurateRip` / `JitterCorrection` /
`RawSubchannel`). The parts that must talk to a real optical drive belong in the
Windows `DiscForge.Devices` layer (SPTI + MMC), and run on the user's machine —
they cannot be exercised in the offline build.

## Why a raw rip is not yet a Redump dump

A drive does not hand back Red Book audio at the exact sample the disc stores it.
Two independent shifts stack up:

| Shift | Cause | Sign |
|-------|-------|------|
| Drive **read offset** | Every model reads audio a fixed number of samples early or late; a per-model constant. | model-specific, e.g. +6, +667, −24 |
| Disc **write offset** | The pressing plant cut the audio a few samples off the nominal LBA. | per-pressing |

Redump keys every dump to the **combined read offset** = drive read offset + disc
write offset, expressed in stereo samples. `ReadOffset.Combine(drive, disc)` is that
sum. Until the rip is slid by −(combined offset) so sample 0 sits where the disc's
logical sector 0 begins, the track boundaries, the AccurateRip checksums and the
final hashes will all disagree with the database — even though every byte was read
without error.

Sliding the stream exposes samples at the very start and end that the nominal read
never covered. A casual ripper pads them with silence; a Redump-grade one *over-reads
the lead-in and lead-out* so the exposed samples are the disc's true neighbours.
`ReadOffset.OverreadSectors(offset)` gives how many extra sectors each edge needs
(`ceil(|offset| / 588)`), and `ReadOffset.ShiftDiscardsOnlySilence` flags the case
where an in-memory slide would throw away real audio instead of silence — the signal
that the guard band must actually be read rather than assumed.

## What Core already provides (software, tested offline)

`ReadOffset` — sample/byte/sector geometry (`588` samples, `2352` bytes per sector),
`Combine`, `OverreadSectors`, `Apply(pcm, offsetSamples)` (the signed slide, same
length out, silence where the guard band isn't supplied), and
`ShiftDiscardsOnlySilence`.

`Silence` — `IsSilent`, `LeadingSilenceSamples`, `TrailingSilenceSamples`, `Peak`.
Used to locate the silent guard band at the disc edges and to sanity-check an
applied offset (a correct offset on a normal disc drops silence at one edge).

`JitterCorrection` — correlates overlapping reads and stitches them without the
clicks that blind concatenation causes; also the primitive an offset auto-detector
uses to align a candidate read against a reference.

`AccurateRip` — the V1/V2 track checksums, the disc-ID triple, and `Verify` against
a database response. The offset-guard sample handling at the first/last track edges
is already built in.

`RawSubchannel` / `ProtectionScanner` — verbatim Q sub-channel storage and LibCrypt
fingerprinting, so a protected PlayStation disc's deliberately-corrupt Q is preserved
rather than "repaired".

`SubmissionInfo` — assembles the redump text from a finished image.

The `read-offset` CLI command surfaces the arithmetic and, given a CD-DA WAV, applies
a slide end-to-end (`dforge read-offset <samples> [in.wav out.wav]`).

## What the Windows drive layer must add (physical, on the user's machine)

**A drive-offset table.** The community keeps a table of per-model read offsets; the
dumper looks the drive up by its INQUIRY model string to seed the drive half of the
combined offset. This is a data table plus a lookup — no drive I/O — and could live in
Core, but it is only meaningful next to a real drive, so it ships with Devices.

**Offset auto-detection.** With no table entry (or to confirm one), read a run of
audio near a track that AccurateRip knows, then find the sample shift at which the
computed checksum matches the database — `JitterCorrection`-style correlation drives
the search, `AccurateRip.Verify` confirms the winner. The disc write offset falls out
of the same match once the drive offset is known.

**The dump loop.** `DiscReader` / `AudioRipper` / `C2SectorReader` / `SubchannelReader`
already exist in `DiscForge.Devices/Reading`. The Redump-grade orchestration on top of
them: read the full TOC (including lead-in/lead-out where the drive allows); rip each
track with C2 error pointers and raw Q sub-channel; re-read any C2-flagged sector until
two passes agree or a cap is hit (`C2SectorReader` already does the per-sector re-read);
over-read the guard band by `OverreadSectors`; slide the assembled stream by the
combined offset with `ReadOffset.Apply` using the real guard samples; then hand the
result to `SubmissionInfo`.

**Descrambled-vs-raw and the data-track detail** that Redump records (e.g. the write
offset it prints, the "Combined read offset" field, EDC/ECM recompute checks) are then
filled from values the loop measured, rather than left blank as `submission-info` does
today for a pure software input.

## Clean-room boundary

Everything here is faithful reading and sample bookkeeping of Red Book audio and the
disc's own sub-channel. DiscForge slides samples, over-reads the lead-in/lead-out,
counts C2 errors and preserves protection fingerprints verbatim — it does not defeat,
strip or forge any protection, and it does not decrypt anything. LibCrypt Q is captured
as-is precisely so the dump stays faithful, never to circumvent the check.

## Status

Shipped and tested offline: the read-offset arithmetic, the silence/guard analysis,
and the `read-offset` command, on top of the pre-existing AccurateRip, jitter and
sub-channel machinery. Deferred to the user's Windows machine (needs a real drive):
the drive-offset table lookup, offset auto-detection against AccurateRip, and the
C2-plus-sub-channel dump-loop orchestration that fills the physical submission fields.
