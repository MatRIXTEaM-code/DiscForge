# Contributing to DiscForge

Thanks for your interest. DiscForge accepts contributions — with three
house rules that are stricter than most projects. Read them first; they are
the reason the project is trustworthy, and PRs that don't follow them will be
declined regardless of quality.

## The three house rules

**1. Provably correct or declined.**
DiscForge never emits possibly-corrupt output. A decoder ships only when it can
be validated — against a checksum the format itself carries, a reference
implementation, a reference-generated test vector, or real-hardware read-back.
If a code path can't prove its output, it must throw a clear "declined" error
instead of guessing. PRs that add a "best effort" decode of an unverified
structure will be asked to convert the guess into an honest decline.

**2. Clean-room only.**
Every format implementation must derive from public documentation, published
specifications, or observation of files/hardware you lawfully own. Do not
contribute anything derived from disassembling or decompiling third-party
software, leaked documentation, or NDA material. State your sources in the PR
description (a link to the spec, the reference implementation consulted, or the
`docs/reference/` script used to generate fixtures). One poisoned contribution
compromises the whole codebase's provenance, so we ask even when it feels
obvious.

**3. Detection, never circumvention.**
DiscForge detects and preserves copy protection; it never defeats it, and never
decrypts encrypted content. This boundary is permanent (see [NOTICE](NOTICE)).
PRs that cross it will be closed, however technically interesting.

## Practical workflow

- **Build:** .NET 8 SDK; `dotnet build DiscForge.sln -c Release` (full, Windows)
  or `dotnet build src/DiscForge.Cli/DiscForge.Cli.csproj -c Release -f net8.0`
  (cross-platform CLI).
- **Test:** `dotnet test` — the suite must stay green, and new behavior needs
  new tests. For format code, the gold standard is a reference-generated
  vector (see `tests/DiscForge.Core.Tests/assets/` and `docs/reference/` for
  examples of how existing fixtures were produced).
- **Scope:** one logical change per PR. Core logic belongs in
  `DiscForge.Core` (pure, no platform calls); anything touching drives goes in
  `DiscForge.Devices` (Windows) or the SG_IO layer.
- **Style:** match the surrounding code. Long doc-comments explaining *why* a
  format works the way it does are the house style and are welcome.

## Licensing of contributions

DiscForge is GPL-3.0-or-later. By submitting a contribution you agree to the
Contributor License Agreement in [CLA.md](CLA.md) — in short: your contribution
is licensed to the project under GPL-3.0-or-later like everything else, **and**
you grant the maintainer the additional right to relicense or dual-license it.
This keeps future licensing decisions (for example, offering commercial
exceptions) possible without tracking down every past contributor. You keep
your copyright.

To signal agreement, add this line to each commit message (git's standard
sign-off, `git commit -s`):

```
Signed-off-by: Your Name <your@email>
```

and include this sentence once in your first PR description:

> I have read CLA.md and I agree to its terms for this and my future
> contributions to DiscForge.

PRs without a sign-off can't be merged.

## Reporting bugs

Open an issue with: the command or GUI action, the full output (use `--json`
where available), the OS, and — for format bugs — either the file (if small and
freely redistributable) or the output of `dforge identify` plus the relevant
`*-info` command on it. **Never attach copyrighted disc images.** A
`.badsectors.json` sidecar or an `entropy`/`fuzzy-hash` report usually carries
enough signal without the content.

## Security

If you find a flaw with security consequences (e.g., a crafted image that
escapes a parser), email the maintainer rather than opening a public issue
first.
