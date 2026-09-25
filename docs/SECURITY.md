# DiscForge — licensing, trial, signing and distribution notes

> Looking for how to **report a security vulnerability**? That lives in
> [`SECURITY.md`](../SECURITY.md) at the repository root.

DiscForge is commercial software (see [LICENSE](../LICENSE)). This document
covers how the licence keys and the trial work, and how to ship a build.

## 1. Licence keys

Keys are ECDSA (P-256) signatures over the licence details (name, edition,
issue date, optional expiry, optional machine id), verified against the public
key embedded in `src/DiscForge.Core/Licensing/License.cs`
(`LicenseConfig.PublicKeyBase64`). Nobody can forge a key without the private
key, `keys\private.pem`, which is git-ignored and must **never** be committed or
shipped. Keep it offline and backed up — lose it and you can't issue keys that
existing installs accept.

```
dforge license keygen private.pem public.txt          # once; paste public.txt into LicenseConfig
dforge license issue --private keys\private.pem --name "Customer Name" --edition Standard
dforge license issue --private keys\private.pem --name "Customer Name" --machine <their machine id>
dforge license issue --private keys\private.pem --name "Reviewer" --days 60   # time-limited key
dforge license verify <key>
```

A customer activates in the app (About → Activate, or the dialog shown at
start-up) or with `dforge license activate <key>`. The key is stored at
`%APPDATA%\DiscForge\license.key` and covers both the app and the CLI.
`dforge license status` shows the state; `dforge license machine-id` prints the
id to put in a machine-locked key (the app's Activation dialog shows the same id).

## 2. The 30-day trial

Without a valid key, DiscForge runs fully for 30 days from its first run, then
asks for a key at start-up and closes without one (the CLI stops with exit
code 3 and a message; `license` and `version` commands always work).

- The start date is kept in two places — `%APPDATA%\DiscForge\trial.dat` and
  `HKCU\Software\DiscForge\TrialRecord` — each with an HMAC tied to the machine
  id, so an edited or copied record is rejected and deleting one copy doesn't
  reset the trial. Setting the clock back more than two days is treated as
  tampering until the clock is right again.
- This is a deterrent, not DRM: someone who deletes both copies gets a new
  trial, and any client-side check can be patched out of a .NET binary. Keep
  the key signing private and consider the steps in §3 to raise the bar.
- The logic is `DiscForge.Core.Licensing.Trial` (pure, unit-tested in
  `TrialTests`) and `TrialStore` (the file/registry I/O).

## 3. Protecting and signing releases

- **Code signing** (recommended): with a code-signing certificate,
  `.\installer\publish.ps1 -Sign -CertThumbprint <thumbprint>` signs the
  executables and DiscForge's own DLLs with a timestamp. It proves the build
  is yours, lets Windows detect tampering and helps with SmartScreen.
- **Obfuscation** (optional): `.\installer\publish.ps1 -Obfuscate -ConfuserCli
  <path to Confuser.CLI.exe>` hardens DiscForge's own assemblies against
  decompilation. Test the obfuscated build fully before shipping — it can break
  reflection-based code.
- **Source**: keep the GitHub repository private. Anyone with the source can
  build a copy without the checks.

## 4. The actual security posture

DiscForge's real security story is the integrity model: untrusted binary
formats are parsed defensively, and every decode is proven against independent
evidence or declined. Robustness findings in the parsers are security issues —
report them privately per the root [`SECURITY.md`](../SECURITY.md).
