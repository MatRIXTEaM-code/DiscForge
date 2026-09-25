# DiscForge — licensing, trial, signing and distribution notes

> Looking for how to **report a security vulnerability**? That lives in
> [`SECURITY.md`](../SECURITY.md) at the repository root.

DiscForge is freeware (see [LICENSE](../LICENSE)): free to use, closed source,
with optional donations. This document covers signing and shipping a build.

## 1. Donations

The app's only link to money is the optional donation button in the About box.
Its target is one constant, `Support.DonateUrl` in
`src/DiscForge.App/Support.cs`, currently https://paypal.me/DiscForgeUK.
If it's ever emptied, the button hides itself. A donation unlocks nothing.

## 2. The old licence-key code

`src/DiscForge.Core/Licensing/License.cs` (ECDSA P-256 keys) and the
`dforge license keygen|issue|verify|machine-id` commands remain as developer
tooling, but nothing in the app or CLI checks a key or runs a trial any more.
The signing key in `keys\private.pem` stays git-ignored; never commit or ship
it.

## 3. Signing releases

- **Code signing** (recommended): with a code-signing certificate,
  `.\installer\publish.ps1 -Sign -CertThumbprint <thumbprint>` signs the
  executables and DiscForge's own DLLs with a timestamp. It proves the build
  is yours, lets Windows detect tampering and helps with SmartScreen.
- **Obfuscation** (optional): `.\installer\publish.ps1 -Obfuscate -ConfuserCli
  <path to Confuser.CLI.exe>` hardens DiscForge's own assemblies against
  decompilation. Test the obfuscated build fully before shipping — it can break
  reflection-based code.
- **Source**: keep the GitHub repository private.

## 4. The actual security posture

DiscForge's real security story is the integrity model: untrusted binary
formats are parsed defensively, and every decode is proven against independent
evidence or declined. Robustness findings in the parsers are security issues —
report them privately per the root [`SECURITY.md`](../SECURITY.md).
