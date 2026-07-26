# DiscForge — protecting the software from copying & theft

An honest starting point: **a distributable .NET application cannot be made
copy-proof.** Its IL decompiles cleanly (dnSpy, ILSpy), and WinForms cannot use
Native AOT, so any client-side check can eventually be bypassed by someone
determined. What the measures below do is *raise the cost*, *deter casual copying and
sharing*, and give you a legitimate licensing and authenticity story. They are layers,
not a lock.

## 1. Licensing (built in)

DiscForge has a public-key licence system. You hold an ECDSA (P-256) **private** key
and sign each licence; the app embeds only the matching **public** key and verifies.
Because the private key never ships, nobody can forge a valid key — an attacker can
only patch the check out, which is what the obfuscation layer then makes harder.

**One-time setup**

```
dforge license keygen private.pem public.txt
```

Keep `private.pem` secret (offline, backed up). Copy the printed public key into
`LicenseConfig.PublicKeyBase64` in `src/DiscForge.Core/Licensing/License.cs`, replacing
the placeholder, and rebuild. Until you do this, **every copy is "unlicensed"** — the
safe default.

**Issuing a licence to a customer**

```
dforge license issue --private private.pem --name "Acme Studio" --edition Pro
dforge license issue --private private.pem --name "Acme Studio" --days 365           # time-limited
dforge license issue --private private.pem --name "Acme" --machine 2879-A832-3A9B-5682 # machine-locked
```

The customer opens **About ▸ Activate…** (or the activation prompt at launch), pastes
the key, and it is stored at `%APPDATA%\DiscForge\license.key`. For a machine-locked
key, the customer reads their machine id from that same dialog and sends it to you.

**Enforcement** is deliberately soft (per your choice): an unlicensed copy still runs
but shows an "UNLICENSED (evaluation)" title/banner and an Activate button. To make it
stricter later, gate features on `LicenseGate.IsLicensed` (e.g. disable Record/Burn, or
block past the activation dialog). Verify a key yourself with `dforge license verify`.

## 2. Obfuscation (recommended, external tool)

Obfuscation renames symbols, encrypts string constants and mangles control flow so a
decompile yields far less. `installer\publish.ps1` has a hook for **ConfuserEx** (free):

```
.\installer\publish.ps1 -Obfuscate -ConfuserCli "C:\tools\ConfuserEx\Confuser.CLI.exe"
```

It obfuscates only DiscForge's own assemblies (`DiscForge.dll`, `DiscForge.Core.dll`,
`DiscForge.Devices.dll`, `dforge.dll`) — never the .NET runtime DLLs. Download ConfuserEx
separately; test the obfuscated build thoroughly (aggressive presets can break
reflection). Commercial options with stronger protection and support: **Eazfuscator.NET**,
**.NET Reactor** (also bundles its own licensing), **Dotfuscator**.

## 3. Code signing (recommended, needs a certificate)

Authenticode signing proves the binary is genuinely yours and lets Windows detect
tampering; it also calms SmartScreen. It is *not* anti-copy. Buy a code-signing
certificate (OV, or EV for immediate SmartScreen trust) and:

```
.\installer\publish.ps1 -Sign -CertThumbprint <your-cert-thumbprint>
```

That signs both executables and DiscForge's own DLLs with a timestamp (so signatures
survive the certificate's expiry). Run it *after* `-Obfuscate` if you use both.

## 4. Legal (the enforceable layer)

The real protection is the proprietary `LICENSE`/EULA already in the repo — it grants no
rights by mere possession, and use is only permitted under a licence you issue. Keep it
shipped (the installer shows it, and `publish.ps1` copies it), keep the copyright headers
in place, and register the copyright if your jurisdiction supports it. Technical measures
deter; the licence is what you enforce.

## What NOT to rely on

- A hard "won't run without a key" block is no more secure than the soft check (both are
  one patched branch away) and risks locking out paying users on a hiccup.
- Home-grown crypto or a secret embedded in the client. The only secret that helps is the
  **private signing key, which never ships** — that is exactly what the licence system uses.
- Obfuscation alone. It slows analysis; it doesn't stop it.

The pragmatic posture: ship licensed + obfuscated + signed, keep the EULA current, and
treat cracking as a business/legal problem for the rare determined actor rather than a
technical problem you can fully solve on the client.
