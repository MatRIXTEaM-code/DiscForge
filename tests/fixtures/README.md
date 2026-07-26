# Test fixtures

## cdi4dc_audiodata_v35.cdi + source.iso

A **real** CDI image, not a synthetic mock. `source.iso` was built with
genisoimage from known-contents files; `cdi4dc_audiodata_v35.cdi` is that ISO
run through cdi4dc 0.3b (SiZiOUS) as an Audio/Data image.

Properties (all confirmed by docs/reference/validate_cdi.py):
- Format: CDI v3.5 (magic 0x80000006)
- Sessions: 2 (audio, then data)
- Data track: Mode 2 / Form 1, 2336-byte sectors, user data at +8
- Known payload strings: "DiscForge CDI parser validation", "0123456789ABCDEF"
- Volume name: OJTEST

Use this as the golden image for parser + extractor round-trip tests. The
eventual C# test should: parse → locate the data track → extract user data →
reconstruct the ISO → byte-compare against source.iso.

## Wanted: a genuine DiscJuggler-authored image

cdi4dc images are simpler than what DiscJuggler itself wrote. A real DJ .cdi
(especially multi-track, or a v2/v3) is the next validation priority.


## The old name inside these files

`source.iso` and the synthetic images contain the string `OPENJUGGLER` — the
project's former name, stamped into ISO 9660 publisher/preparer fields when they
were built.

**Do not "fix" this.** These fixtures are historical artifacts whose exact bytes
are asserted by CRC-32 tests. Rewriting the string would change their checksums
and break the tests that prove the parser reads real images correctly.

The builder now stamps `DISCFORGE`; that only affects newly created images.
