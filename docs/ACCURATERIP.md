# AccurateRip verification

Turns "I made a rip" into "I made a *verified-correct* rip". AccurateRip is a
public database of audio-CD checksums submitted by thousands of drives; if your
rip's checksum matches, your rip is bit-identical to everyone else's — a
confirmed-good, error-free rip, not merely internally consistent.

## Engine — `AccurateRip` (Core)

Pure arithmetic over PCM samples and TOC data:
- **`Compute(pcm, isFirst, isLast)`** — the position-weighted v1 and v2
  checksums for a track, with the 5-sector guard band trimmed at the start of
  track 1 and the end of the last track (the drive-offset boundary).
- **`DiscIds(trackOffsets)`** — the AccurateRip disc identifiers (two
  TOC-derived IDs plus the FreeDB/CDDB id) used to key the database.
- **`Verify(computed, database)`** — matches computed checksums against parsed
  database records, reporting per-track status (v1/v2 match or not found) and
  the confidence of the match.

No network is involved in the maths, so it is fully unit-tested (13 tests):
known-value checksum, guard trimming, determinism, disc-ID fields, and the
verify/confidence logic.

## CLI — `dforge accuraterip <image.cue>`

Reads the audio tracks, prints the disc IDs and each track's v1/v2 checksums.
The database lookup itself is an online step (an HTTP fetch of the AccurateRip
binary record), done on the user's machine; the CLI computes the values to
compare. A future online mode can fetch and call `Verify` directly.

## Boundary

Computing an AccurateRip checksum reveals nothing protected and enables nothing
but verification — it is the opposite of circumvention.

## Database record parsing & automatic verify

`AccurateRipDatabase` parses the AccurateRip binary record (the `dBAR-*.bin`
blob the service returns) into the entries `Verify` consumes, closing the loop
from "compute checksums" to automatic pass/fail:

- **`Parse(blob)`** — decodes the chunked format (one chunk per pressing:
  track count + three disc IDs, then per-track confidence + CRC + CRC450). All
  little-endian; a truncated blob throws a clear error.
- **`ToEntries(chunks, filterDiscIds)`** — converts chunks to `DbEntry` list,
  optionally filtering to pressings whose disc IDs match, so a stray record
  can't produce a false match.
- **`LookupUrl(...)`** — builds the canonical AccurateRip URL (sharded by id1)
  for the caller to fetch.

Only the HTTP GET is machine-side; parse and verify are offline and unit-tested
(12 tests including an end-to-end synthesised-record verify).

## CLI verify

```
dforge accuraterip album.cue --url            # show IDs + lookup URL
# (fetch the .bin from that URL on your machine)
dforge accuraterip album.cue --db record.bin  # verify against it
```

The verify prints per-track ACCURATE (v1/v2, with confidence) or mismatch, a
summary, and exits non-zero if any track fails — usable in scripts.
