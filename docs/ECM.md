# ECM (Error Code Modeler) — assessment and status

ECM is a lossless pre-compression transform for CD images: it strips the fully
deterministic bytes of each CD sector (sync, EDC, ECC, and — depending on the
sector type — the address), leaving only the payload, so the result compresses
better with a general packer. `unecm` reverses it, regenerating the stripped
bytes. It is ubiquitous for distributing PlayStation `.bin` tracks.

## Why this is not shipped yet (the honest reason)

DiscForge's rule for format work is **provably correct or declined**: every
reader/writer is validated against an oracle — chdman for CHD, a second
independent implementation for CDI, the reader for UDF/XISO/NRG round trips.
ECM's decode is a *sector reconstruction*: for each 2352-byte sector it must
regenerate the exact original bytes. The correctness of that hinges on a precise,
byte-level fact for each of the three reconstructable sector types:

- **which bytes each type stores** (Mode 1, Mode 2 Form 1, Mode 2 Form 2 strip
  different fields), and
- **whether the sector address is stored or re-derived from the sector's
  position** in the output stream.

Getting this wrong by a single byte does not fail loudly — it misaligns every
subsequent sector, silently corrupting the output. DiscForge already has the hard
part (`EdcEcc.FillMode1` / `FillMode2Form1` regenerate valid EDC/ECC, validated
elsewhere), so the engineering is small. What is missing is the **oracle**: a real
`.ecm` file (or an authoritative byte-level spec) to pin the stored-field layout
and confirm the decoder reproduces a reference tool's output byte-for-byte.

A round-trip test (DiscForge encodes a valid bin, then decodes it back) would pass
even if the encoder and decoder *shared* a wrong assumption — so it proves internal
consistency but **not** interoperability with `.ecm` files other tools produced,
which is the whole point of supporting the format. Shipping an unvalidated sector
codec would violate the project's standard.

## What finishing it needs (small, once unblocked)

1. One reference `.ecm` + its original `.bin` (from any established ECM tool), used
   as a fixture: `decode(reference.ecm)` must equal `reference.bin` byte-for-byte.
   That single fixture pins the number encoding, the per-type stored sizes, and the
   address question in one shot.
2. `EcmCodec.Decode` / `EcmCodec.Encode` over the existing `EdcEcc` reconstruction,
   wired into the bin/cue tooling and a `dforge ecm`/`unecm` CLI pair.
3. Round-trip test **plus** the reference-fixture test, so both internal consistency
   and cross-tool interop are locked in — the same bar every other format here meets.

## Summary

| Item | Status |
|------|--------|
| ECM decode/encode | **Deferred pending a reference `.ecm` fixture** — reconstruction machinery (EDC/ECC) already exists; blocked only on an oracle to validate byte-for-byte, per the project's validated-or-declined rule |
