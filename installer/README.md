# DiscForge installer

Builds a single `DiscForge-Setup-<version>.exe` for Windows, using
[Inno Setup](https://jrsoftware.org/isdl.php) (free). The app ships
**self-contained** — the target PC needs no .NET install.

## Build it

On a Windows machine with the .NET 8 SDK and Inno Setup 6 installed:

```powershell
# 1. From the repo root — publish self-contained win-x64 binaries:
powershell -ExecutionPolicy Bypass .\installer\publish.ps1

# 2. Compile the installer:
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\DiscForge.iss
```

The setup executable lands in `installer\Output\`.

(Step 2 can also be done by opening `installer\DiscForge.iss` in the Inno
Setup IDE and pressing F9.)

## What the installer does

- Installs the GUI (`DiscForge.exe`) and the CLI (`dforge.exe`) to
  `Program Files\DiscForge`, self-contained.
- Start Menu group: DiscForge, a **DiscForge Command Prompt** (opens a shell
  in the install dir, ready for `dforge`), Documentation, Licence, Uninstall.
- Optional **desktop icon** (unticked by default).
- Optional **add `dforge` to PATH** (ticked by default) so the CLI works from
  any terminal. The PATH entry is added idempotently and removed on uninstall.
- Registers a proper uninstaller in Add/Remove Programs.
- Requests admin **at install time** (writing to Program Files + PATH).

## Elevation at run time

The app's manifest (`src/DiscForge.App/app.manifest`) is set to
`requireAdministrator`: DiscForge always launches elevated, so raw SPTI disc
access, burning and erase never fail partway and ask the user to relaunch.
Installed via this package, that means a single UAC prompt at launch.

The `dforge` CLI is intentionally NOT manifested to elevate — run it from an
already-elevated terminal when a command needs raw access, so it stays usable
for inspect/checksum/convert work without forcing UAC on every invocation.

## Code signing (optional, recommended for distribution)

Unsigned, Windows SmartScreen will warn on first run. If you have a
code-signing certificate, sign both executables before step 2 and sign the
finished setup after:

```powershell
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 `
  publish\DiscForge.exe publish\dforge.exe
# …compile installer…
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 `
  installer\Output\DiscForge-Setup-*.exe
```

Not required for personal or in-house use.
