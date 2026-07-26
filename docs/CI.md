# Continuous integration & releases

Two GitHub Actions workflows (`.github/workflows/`), plus `global.json` pinning
the .NET 8 SDK for reproducible builds.

## CI (`ci.yml`) — on push / PR

- **Test Core (Linux)** — `ubuntu-latest`. Builds and tests
  `DiscForge.Core.Tests`, which references only the cross-platform Core. Runs
  the full test suite (parser, writer, extractor, converter, verifier, comparer,
  ISO builder, MMC parsers, burn planner) against the committed fixtures.
  Uploads a `.trx` test report artifact.
- **Build solution + test (Windows)** — `windows-latest`. Builds the *entire*
  solution, so the Windows-only projects (Devices: SPTI/IMAPI2; the WinForms App)
  are compile-checked too, then runs the tests again.

Why the split: Core and its tests are `net8.0` (run anywhere); Devices and App
are `net8.0-windows` and only build on Windows. The Linux job is the fast gate;
the Windows job proves the whole thing compiles.

## Release (`release.yml`) — on tag `v*`

- **Windows** — publishes the app and the CLI as single-file, self-contained
  win-x64 executables (no .NET install needed), zips them, and attaches them to
  the GitHub Release.
- **Linux** — publishes the cross-platform CLI (`dforge`) as a self-contained
  linux-x64 build and attaches the tarball.

Cut a release by pushing a tag:

```
git tag v0.12.0
git push origin v0.12.0
```

## Fixtures

Test fixtures (`tests/fixtures/`) are committed — the real cdi4dc image, its
source ISO, and the synthetic v2/v3/v3.5 matrix — so CI needs no Python step.
The Python reference oracles under `docs/reference/` are for local validation of
new format work (regenerate the synthetic suite with
`python3 docs/reference/gen_cdi.py --suite tests/fixtures/synthetic`).
