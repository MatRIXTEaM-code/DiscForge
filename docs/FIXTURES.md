# Validation fixtures — drop a sample, it self-validates

Several features are proven internally by synthetic tests and are only waiting on a **real
third-party sample** to upgrade to oracle-validation (the "last mile" for a clean-room decoder).
The harnesses are already written and **inert by default** — they run only when you point the
environment variable `DFORGE_FIXTURES` at a directory containing the sample(s). Nothing needs to be
checked into the repo; a reference file is data (a test vector), not code, so it stays inside the
clean-room boundary.

```
export DFORGE_FIXTURES=/path/to/fixtures
dotnet test              # or the harness — the matching tests now assert against your samples
```

## Slots (any subset may be present)

| Drop this | Unblocks / validates | What the harness asserts |
|---|---|---|
| `ecm/reference.ecm` + `ecm/reference.bin` | ECM codec interop | `EcmCodec.Decode(ecm)` equals the original `.bin` byte-for-byte |
| `mdec/reference.str` | PlayStation MDEC video | every frame of a real `.str` decodes without a VLC error |
| `gamecube/reference.iso` | **the GameCube junk generator** (`gc-junk-fill`) | `GcJunkReconstructor` regenerates the disc's own surviving junk and `SelfValidated == true` — i.e. the clean-room LFG reproduces a real Nintendo disc exactly. A **false** result is the precise signal that the LFG constants still need correcting against this oracle. This is the single highest-leverage sample: an un-scrubbed GameCube ISO (or any disc with junk intact) validates the whole junk path. |
| `rvz/reference.rvz` + `rvz/reference.iso` | **the RVZ → ISO decoder** (`rvz-decode`) | decoding the real `.rvz` matches the known-good ISO in every **data** region (our output zero-fills Nintendo junk by design, so the check compares where our output is non-zero) — validating the container walk, zstd, offset math and RVZ-packed unpack against a real Dolphin file |

All slots live in `tests/DiscForge.Core.Tests/InteropFixtureTests.cs`. When a directory or a file is
absent the corresponding test returns without asserting, so CI stays green with no fixtures present.

## Notes

- The GameCube ISO is the one to find first — per `docs/VALIDATION-PLAN.md` it is **not**
  drive-gated, and it validates the junk generator end to end. An NKit-scrubbed image works too as a
  future extension (its recovery-block CRC32 is an independent oracle).
- These harnesses failing when a fixture is present is the **intended** behaviour: it means our
  clean-room implementation does not yet match the real tool, and points at exactly what to fix.
