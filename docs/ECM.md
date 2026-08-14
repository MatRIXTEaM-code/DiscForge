# ECM (Error Code Modeler) — shipped

ECM is a lossless pre-compression transform for CD images: it strips the fully
deterministic bytes of each CD sector (sync, EDC, ECC, and — depending on the
sector type — the address), leaving only the payload, so the result compresses
better with a general packer. `unecm` reverses it, regenerating the stripped
bytes. It is ubiquitous for distributing PlayStation `.bin` tracks.

DiscForge implements both directions:

- `dforge ecm <in.bin> [out.ecm]` — strip to ECM.
- `dforge unecm <in.ecm> [out.bin]` — rebuild the raw image (whole-file EDC verified).

Both are thin wrappers over `DiscForge.Core.Raw.EcmCodec`, which builds on the same
`EdcEcc` sector-reconstruction machinery DiscForge already uses (and independently
verifies) for raw imaging.

## The two byte-level facts, resolved

The correctness of ECM decode hinges on which bytes each sector type stores and
whether the sector address is stored or re-derived. Both are now pinned from the
authoritative public format description (nocash PSXSPX and the qeedquan `format.txt`,
which agree field-for-field):

| Type | Sector | Stored per sector | Address |
|------|--------|-------------------|---------|
| 0 | literal | N raw bytes, verbatim | n/a |
| 1 | Mode 1 | 3 address + 2048 data | **stored** (Mode 1 ECC covers the header, so it can't be re-derived) |
| 2 | Mode 2 Form 1 | 4 subheader + 2048 data | **reconstructed** from the running sector index (Form 1 EDC/ECC exclude the header) |
| 3 | Mode 2 Form 2 | 4 subheader + 2324 data | **reconstructed** (Form 2 has EDC only, no ECC, and it excludes the header) |

The stream ends with a type-0 record whose encoded count is `0xFFFFFFFF`, followed by
a 4-byte EDC over the whole reconstructed file (the same CD EDC polynomial), which
`unecm` checks.

The one remaining subtlety — the Mode 2 address — is reconstructed as the absolute
MSF of `runningLba + 150` (the 2-second lead-in). Because the **encoder only emits a
sector as type 1/2/3 when its full 2352-byte reconstruction matches the original
byte-for-byte**, and otherwise falls back to a literal, a DiscForge round trip is
exact for *any* input regardless of how a given disc was addressed.

## Validation

- **Round-trip, byte-exact:** a mixed image (Mode 1 + Form 1 + Form 2 sectors plus a
  non-sector audio-like literal tail) survives `Encode` → `Decode` identically, and a
  purely non-sector input round-trips entirely as literals.
- **Independent oracle:** every rebuilt Mode 1 / Form 1 sector is fed through
  `EdcEcc.VerifyMode1` / `VerifyMode2Form1`, which evaluate the Reed-Solomon syndromes
  rather than re-running the encoder — so the regenerated EDC/ECC is proven
  algebraically valid, not merely equal to what the codec assumed.
- **Corruption caught:** a damaged trailing EDC is detected on decode.

## Honest caveat (interop)

The round-trip + independent-syndrome checks prove DiscForge's ECM is internally
correct and produces algebraically valid sectors. Full cross-tool interop — decoding a
`.ecm` produced by the original `ecm` tool, and having the original `unecm` accept
DiscForge's output — is pinned by the two agreeing published specs but not yet
regression-tested against an external fixture in CI. The common case (a data track
addressed from the disc start, the overwhelming majority of real `.ecm` files) is
covered by the documented convention above.

The last-mile check is already wired: point the `DFORGE_FIXTURES` environment variable
at a directory containing `ecm/reference.ecm` and its exact `ecm/reference.bin` (from
the original ecm tool), and `InteropFixtureTests.Ecm_decodes_a_reference_file_byte_for_byte`
asserts the decode matches byte-for-byte. The test is inert when no fixture is present.

## Summary

| Item | Status |
|------|--------|
| ECM decode/encode | **Shipped** — `ecm` / `unecm`, byte-exact round-trip + independent EDC/ECC syndrome verification. Cross-tool interop follows the two agreeing public specs; an external `.ecm` fixture would add a regression guard. |
