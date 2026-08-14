# The MDI shell (v1.11.0) — full DiscJuggler idiom

DiscForge's front end is now a DiscJuggler-style MDI application: one main
frame with a menu bar, a toolbar, and task windows opening as child windows
inside it. This replaces the flat launcher.

## Structure

- **`MdiShell`** — the main frame (`IsMdiContainer`). Menu bar (File / Edit /
  View / Window / Help), a flat toolbar of small drawn icon buttons, a grey
  MDI client area, and a status strip fed by `StatusBus`. The Window menu
  auto-lists open children and offers Cascade / Tile / Close All.
- **`TaskChildForm`** — an MDI child hosting a view under a tabbed panel
  ("Source & Destination" / "Advanced"), owner-drawn grey tabs, sized to fit
  the view. The Advanced tab carries each task's secondary-option notes.
- **`RetroNewTaskDialog`** — File ▸ New Task: a scrolling owner-drawn task
  list (source→target disc icons per row, navy selection), a description line,
  and a "Disclaimer" etched group box, matching Padus's New Task dialog.
- **`RetroMenuColors`** — flat grey colours for the menu bar and toolbar.

## Opening tasks

Three ways, all landing in the same child windows: the toolbar buttons, the
File/View menus, and File ▸ New Task. Each opens a `TaskChildForm` with the
matching view (Read, Create, Record, Copy, Inspect, Sector Viewer, Raw Lab,
Tools, Drives). Several can be open at once, cascaded or tiled.

## Removed

The flat `CdrwinLauncher` and its `CdrwinTaskWindow` are gone; the MDI shell
supersedes both. `RetroTheme`, `RetroStyler`, `RetroMessageBox` and the views
are unchanged.

## Unchanged

Everything below the App — Core, Devices, the `dforge` CLI — is untouched.
This is a front-end rebuild only; the 117 Core tests still pass.
