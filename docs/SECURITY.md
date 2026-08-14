# DiscForge — the licence system, signing, and distribution notes

> **Historical note.** This document originally described how to protect a
> proprietary DiscForge build from copying. DiscForge is now free software under
> **GPL-3.0-or-later**, which changes the meaning of everything below: anyone may
> copy, modify and redistribute the program, and the licence-key code is **no
> longer a protection mechanism and does not pretend to be**. What remains
> useful is kept here; obsolete advice has been removed.
>
> Looking for how to **report a security vulnerability**? That now lives in
> [`SECURITY.md`](../SECURITY.md) at the repository root.

## 1. The licence-key system under the GPL

The ECDSA (P-256) licence infrastructure in `src/DiscForge.Core/Licensing/`
still exists and still works — but under the GPL it cannot restrict anyone,
because every recipient has the right to build and run the software without it.
Its legitimate remaining uses:

- **Supporter / patron builds** — issue signed keys as a thank-you that unlocks
  a supporter badge or About-box credit. Cosmetic, honest, GPL-compatible.
- **Dual licensing** — if you (as sole copyright holder via the CLA) offer a
  separately-licensed commercial edition, signed keys can identify entitled
  customers of *that* edition. The GPL edition must remain fully functional.
- **Authenticity of issued keys** — the cryptography is sound: nobody can forge
  a key without `private.pem`. What changed is only what a key may gate.

What is **not** legitimate: gating GPL-edition functionality behind a key, or
presenting the key check as copy protection. Both would conflict with the
freedoms the GPL grants and with the project's own NOTICE.

Key management is unchanged:

```
dforge license keygen private.pem public.txt
dforge license issue --private private.pem --name "Acme Studio" --edition Pro
dforge license verify <key>
```

Keep `private.pem` offline and backed up if you use the system at all.

## 2. Code signing (still recommended)

Authenticode signing is unrelated to copy protection and remains fully
worthwhile: it proves a release binary genuinely comes from you, lets Windows
detect tampering, and calms SmartScreen for users. With a code-signing
certificate (OV, or EV for immediate SmartScreen reputation):

```
.\installer\publish.ps1 -Sign -CertThumbprint <your-cert-thumbprint>
```

This signs the executables and DiscForge's own DLLs with a timestamp, so
signatures outlive the certificate. Signing GPL software is normal practice —
it authenticates *origin*, it does not restrict *use*.

## 3. What was removed, and why

- **Obfuscation guidance** (ConfuserEx et al.) — obfuscating a GPL program's
  own source-available assemblies serves no purpose and works against the
  licence's spirit; the `publish.ps1 -Obfuscate` hook is considered deprecated.
- **EULA/proprietary-licence advice** — superseded entirely by
  [LICENSE](../LICENSE) (GPL-3.0-or-later) and [NOTICE](../NOTICE).
- **"Raising the cost of copying"** — copying is now a granted right, not a
  threat model.

## 4. The actual security posture

DiscForge's real security story is the integrity model: untrusted binary
formats are parsed defensively, and every decode is proven against independent
evidence or declined. Robustness findings in the parsers are security issues —
report them privately per the root [`SECURITY.md`](../SECURITY.md).
