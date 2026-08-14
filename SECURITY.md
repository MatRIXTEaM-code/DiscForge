# Security Policy

## Reporting a vulnerability

DiscForge parses dozens of untrusted binary formats, so parser robustness is a
security surface: a crafted disc image that causes memory exhaustion, an
infinite loop, a crash with attacker-influenced state, or — worst — output that
bypasses the integrity gates ("provably correct or declined") is a security
issue, not just a bug.

**Please report vulnerabilities privately** rather than in a public issue:

- Use GitHub's **"Report a vulnerability"** (Security tab → Advisories) on this
  repository, or
- email the maintainer (address on the GitHub profile).

Include the smallest input that reproduces the problem — a synthetic or
truncated file is ideal. **Never attach copyrighted disc content**; a crafted
fixture built with the generators in `docs/reference/` almost always suffices.

You can expect an acknowledgement within a few days. Fixes are released as
patch versions; credit is given in the release notes unless you prefer
otherwise.

## Scope notes

- The integrity model is a deliberate defence: decoders verify against
  format-carried checksums (CRC-64, SHA-1, EDC/ECC) or decline. A finding that
  defeats one of these gates is high severity.
- The licence-key code in `DiscForge.Core/Licensing` is **not** a security
  boundary under the GPL (anyone may build without it); findings there are
  ordinary bugs.
- DiscForge never decrypts protected content and contains no circumvention
  code; reports requesting such functionality are out of scope by policy
  (see [NOTICE](NOTICE)).

## Supported versions

The latest release and the current `main` branch receive fixes.
