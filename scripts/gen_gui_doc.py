#!/usr/bin/env python3
# DiscForge — proprietary. Copyright (c) 2026 Andy. All rights reserved.
#
# Regenerate docs/GUI.md from the in-app manual (HelpContent.cs), so the GUI tile
# reference is a single source of truth and can't drift. Kept in step with the
# PowerShell generator (scripts/gen_gui_doc.ps1) — both emit identical output.
#
# Usage:  python3 scripts/gen_gui_doc.py        # writes docs/GUI.md
#         python3 scripts/gen_gui_doc.py --check # non-zero exit if GUI.md is stale

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HELP = os.path.join(ROOT, "src", "DiscForge.App", "Views", "HelpContent.cs")
LAUNCHER = os.path.join(ROOT, "src", "DiscForge.App", "CdrwinLauncher.cs")
OUT = os.path.join(ROOT, "docs", "GUI.md")

STR = re.compile(r'"((?:[^"\\]|\\.)*)"')

GROUPS = [
    ("Disc imaging & burning",
     ["record", "copy", "read", "create", "convert", "inspect", "rawlab",
      "sectors", "bincue", "cue", "browse", "udfcreate"]),
    ("Hardware & devices (need raw access)",
     ["drives", "mount", "recovery", "quality"]),
    ("Audio", ["accuraterip", "ripaudio"]),
    ("Protection & interop", ["protect", "subcode", "interop"]),
    ("DVD & video", ["dvdshrink", "dvdinfo", "transcode", "pack"]),
    ("Identify, verify & catalogue",
     ["identify", "examine", "library", "submit", "tools"]),
    ("Console & cartridge preservation",
     ["patch", "dreamcast", "milcd", "dcid", "xbox", "memcard", "psxasset",
      "psxbuild", "compimg", "scummvm"]),
    ("Extract, cheats & game media", ["extract", "cheat", "media"]),
    ("Collection & front-end", ["playlists", "sets"]),
    ("Utility", ["help", "settings", "about", "exit"]),
]

HEADER = """\
<!-- GENERATED FILE - do not edit by hand.
     Regenerate with:  .\\scripts\\gen_gui_doc.ps1   (or python3 scripts/gen_gui_doc.py)
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
dotnet publish src/DiscForge.App -c Release -r win-x64 --self-contained \\
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`DiscForge.exe` then needs no .NET install. Most views run as a normal user;
**Drives**, **Record**, **Copy**, **Read**, **Mount**, **Recovery** and **Disc
Quality** need raw device access, which generally means running as administrator.

## Tiles

The launcher shows **{count} tiles**, defined by the `_tiles` array in
`CdrwinLauncher.cs`. Each is described below; the full write-ups are in the Help
tile (source: `HelpContent.cs`)."""

NOTES = """\
## Notes

- Long-running work (create / verify / detect / burn) runs on a background thread;
  the window stays responsive and failures surface as messages, never as false success.
- The manifest requests `asInvoker` (no forced UAC) with visual styles; per-monitor
  DPI awareness is set in code.
- Views are hand-built in code (no `.Designer.cs`), so the source is fully reviewable as text.
- Diagnostics: `AppLog` writes a session log under `%APPDATA%\\DiscForge\\logs`; the
  About tile opens that folder. Nothing is transmitted anywhere."""


def parse_help(text):
    order, by_key = [], {}
    for chunk in text.split("new(")[1:]:
        chunk = chunk.split("};")[0]
        lits = STR.findall(chunk)
        if len(lits) < 5:
            continue
        key = lits[0]
        by_key[key] = dict(key=key, glyph=lits[1], title=lits[2],
                           summary=lits[3], body="".join(lits[4:]))
        order.append(key)
    return order, by_key


def build(order, by_key):
    grouped = {k for _, keys in GROUPS for k in keys}
    lines = [HEADER.format(count=len(order)), ""]
    for name, keys in GROUPS:
        rows = [by_key[k] for k in keys if k in by_key]
        if not rows:
            continue
        lines += [f"### {name}", "", "| Tile | What it does |", "|------|--------------|"]
        lines += [f"| **{e['title']}** | {e['summary']}. |" for e in rows]
        lines.append("")
    ungrouped = [by_key[k] for k in order if k not in grouped]
    if ungrouped:
        lines += ["### Other", "", "| Tile | What it does |", "|------|--------------|"]
        lines += [f"| **{e['title']}** | {e['summary']}. |" for e in ungrouped]
        lines.append("")
    lines.append(NOTES)
    return "\n".join(lines).rstrip() + "\n"


def main():
    help_text = open(HELP, encoding="utf-8").read()
    launcher_text = open(LAUNCHER, encoding="utf-8").read()
    order, by_key = parse_help(help_text)

    launcher_keys = re.findall(r'new\("([a-z0-9]+)",', launcher_text)
    missing = [k for k in launcher_keys if k not in by_key]
    if missing:
        print(f"WARNING: launcher tiles with no HelpContent entry: {missing}", file=sys.stderr)

    doc = build(order, by_key)

    if "--check" in sys.argv:
        current = open(OUT, encoding="utf-8").read() if os.path.exists(OUT) else ""
        if current.rstrip() != doc.rstrip():
            print("docs/GUI.md is stale — run: python3 scripts/gen_gui_doc.py", file=sys.stderr)
            sys.exit(1)
        print("docs/GUI.md is up to date.")
        return

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(doc)
    print(f"Wrote {OUT}: {len(order)} tiles.")


if __name__ == "__main__":
    main()
