<!-- GENERATED FILE - do not edit by hand.
     Regenerate with:  .\scripts\gen_gui_doc.ps1   (or python3 scripts/gen_gui_doc.py)
     Source of truth:  src/DiscForge.App/Views/HelpContent.cs -->

# DiscForge - the WinForms application

`DiscForge.App` is the standalone Windows GUI. It is a thin shell over the tested
Core: the shell owns no disc logic; every view calls into `DiscForge.Core` /
`DiscForge.Devices`. The App targets `net8.0-windows` with WinForms and is
Windows-only - the Core, CLI and test harness build anywhere .NET 8 does, but the
GUI does not.

The front door is `CdrwinLauncher` - a grid of large flat icon tiles in the CDRWIN
4 idiom. Each tile opens its own task window; the hovered tile's blurb shows on the
status line. The in-app **Help** tile is the searchable version of this reference.

## Launching

On Windows, with the .NET 8 SDK:

```
dotnet run --project src/DiscForge.App
```

Single double-click executable:

```
dotnet publish src/DiscForge.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`DiscForge.exe` then needs no .NET install. Most views run as a normal user;
**Drives**, **Record**, **Copy**, **Read**, **Mount**, **Recovery** and **Disc
Quality** need raw device access, which generally means running as administrator.

## Tiles

The launcher shows **49 tiles**, defined by the `_tiles` array in
`CdrwinLauncher.cs`. Each is described below; the full write-ups are in the Help
tile (source: `HelpContent.cs`).

### Disc imaging & burning

| Tile | What it does |
|------|--------------|
| **Record Disc** | Write an image to a recorder. |
| **Copy Disc** | Duplicate a disc. |
| **Read Disc** | Rip a disc to an image. |
| **Create Image** | Build an image from files. |
| **Convert** | Any image format to any other. |
| **Inspect** | Read and verify a CDI image. |
| **Raw Lab** | Compose / analyse raw DAO. |
| **Sector Viewer** | Annotated hex of any sector. |
| **Bin/Cue** | Merge or split bin/cue. |
| **Cue Editor** | Check and repair a cuesheet. |
| **Browse Files** | List and extract files. |
| **UDF Image** | Build a UDF 1.02 image. |

### Hardware & devices (need raw access)

| Tile | What it does |
|------|--------------|
| **Drives** | Detected recorders. |
| **Mount** | Mount an image as a drive. |
| **Recovery** | Recover damaged sectors. |
| **Disc Quality** | Measure surface errors. |

### Audio

| Tile | What it does |
|------|--------------|
| **AccurateRip** | Verify an audio rip. |
| **Rip Audio** | Rip an audio CD to WAV. |

### Protection & interop

| Tile | What it does |
|------|--------------|
| **Protection** | Scan for copy-protection. |
| **Sub-channel** | Analyse Q sub-channel. |
| **CloneCD** | Read / write CloneCD .ccd. |

### DVD & video

| Tile | What it does |
|------|--------------|
| **DVD Shrink** | Shrink DVD-Video to fit. |
| **DVD Structure** | Titles, chapters, streams. |
| **Shrink Video** | Re-encode video to fit. |
| **Pack Discs** | Fit files across discs. |

### Identify, verify & catalogue

| Tile | What it does |
|------|--------------|
| **Identify File** | Say what any file is. |
| **Examine** | Identify and show parsed detail. |
| **Library** | Scan, verify and rename a collection. |
| **Submit** | redump.org submission info. |
| **Tools** | Checksums, split / join. |

### Console & cartridge preservation

| Tile | What it does |
|------|--------------|
| **PPF Patch** | Apply or build a PPF/IPS/BPS patch. |
| **Dreamcast** | Browse / extract / convert a GD-ROM. |
| **MIL-CD → CDI** | Convert a MIL-CD to a two-session CDI. |
| **Identify DC** | Read a Dreamcast boot header. |
| **Xbox** | Browse / extract / build an XISO. |
| **Memory Cards** | Read console saves. |
| **PSX Assets** | TIM/VAG/TMD/PS-EXE. |
| **PSX Build** | Build a Mode 2 bin/cue. |
| **Compressed** | CSO/ZSO ↔ ISO, identify CHD. |
| **ScummVM** | Fingerprint or export for ScummVM. |

### Extract, cheats & game media

| Tile | What it does |
|------|--------------|
| **Extract** | Pull files/saves out of a container. |
| **Cheat Codes** | Decode / encode cheat codes. |
| **Game Media** | Decode ADX→WAV, render CD+G→PNG. |

### Collection & front-end

| Tile | What it does |
|------|--------------|
| **Playlists** | Export front-end library files. |
| **Sets** | 1G1R filter and rebuild a set. |

### Utility

| Tile | What it does |
|------|--------------|
| **Help** | What each tile does and how to use it. |
| **Settings** | Preferences and diagnostics. |
| **About** | Version, licence and diagnostics. |
| **Exit** | Close DiscForge. |

## Notes

- Long-running work (create / verify / detect / burn) runs on a background thread;
  the window stays responsive and failures surface as messages, never as false success.
- The manifest requests `asInvoker` (no forced UAC) with visual styles; per-monitor
  DPI awareness is set in code.
- Views are hand-built in code (no `.Designer.cs`), so the source is fully reviewable as text.
- Diagnostics: `AppLog` writes a session log under `%APPDATA%\DiscForge\logs`; the
  About tile opens that folder. Nothing is transmitted anywhere.
