# The CDRWIN look (v1.9.2) — now the only look

DiscForge's front end was rebuilt around CDRWIN 4's structure. There is no
modern shell, no sidebar, no skin toggle: the app IS the launcher.

## Structure

- **`CdrwinLauncher`** is the application window — a small fixed grid of large
  glossy buttons (Record Disc, Copy Disc, Read Disc, Create Image, Raw Lab,
  Inspect, Sector Viewer, Tools, Drives, Settings, About, Exit), a navy
  product strip, and a sunken status line that shows the hovered task's blurb.
  This mirrors how Golden Hawk's CDRWIN opened: a wall of big buttons, each
  launching a dedicated window.
- **`CdrwinTaskWindow`** is what a button opens: a resizable window in period
  chrome (navy title, sunken content well, a status strip fed by the view's
  own progress reports, and Close) hosting one of the existing views. Several
  can be open at once; closing one leaves the launcher and the others alone.
- **`RetroStyler`** restyles each hosted view's controls to the classic look
  (MS Sans Serif, grey button face, sunken white fields) so the views — which
  still do all the real work unchanged — match the frame.

## What was removed

The modern shell (`MainForm`), the modern home screen (`HomeView`), the
DiscJuggler-style `RetroLauncherView`, the sidebar, the toolbar, and the
retro/modern skin toggle. `Settings.RetroSkin` is gone; there is nothing to
toggle. `RetroTheme` (palette, fonts, bevels) and the views remain.

## Unchanged

Everything below the App: `DiscForge.Core`, `DiscForge.Devices`, and the
`dforge` CLI are untouched. This was purely a front-end restructure — the
imaging, burning, inspection and validation engines are exactly as they were,
still covered by the 117 passing Core tests.

## v1.9.4 — all-grey, CDRWIN-faithful

Reference: CDRWIN's own windows are entirely `#C0C0C0` grey — group boxes,
backgrounds and buttons all grey; the only white is inside sunken edit fields
and list/output boxes. DiscForge now matches that:

- `RetroStyler` forces the grey face on every container (panels, tab pages,
  split containers, group boxes, the view itself) and restyles buttons to the
  system flat look. White survives only on text-entry fields, list boxes,
  list views and multi-line output wells — the sunken places it belongs.
- Drop-downs (`ComboBox`) render as classic sunken fields with a system
  button, matching the Disc Type / Track Mode / Speed pickers of the era.
- Tab controls and tab pages (Advanced-Options-style dialogs) are greyed too.
- The flat launcher tiles lost their gloss and gradients: solid raised
  bevels, flat coloured icon plates, the busy-little-square CDRWIN grid.
- The About window was rebuilt off the modern theme into the same grey chrome.

## v1.9.5 — retro message boxes

Windows draws its own message boxes in the current system theme, which no
app-side styling can touch — so DiscForge's confirmations and alerts were
still coming up in the modern white dialog. `RetroMessageBox` is a drop-in
replacement with the same `Show(...)` overloads and `DialogResult` returns as
`MessageBox`, but it draws its own grey window: classic face, MS Sans Serif,
system buttons in the standard right-aligned order, and a drawn warning /
information / error glyph. Every in-app prompt (erase confirmation, licence,
help, save results, error notices) now uses it; only the last-resort crash
handler in `Program.cs` keeps the OS box, since it may fire when the app's own
UI can't be trusted.


## v1.9.7 — less white

Feedback: task windows still read as too white. In the modern layout the list
and output areas (Destination list, event log, report wells) are large and
edge-to-edge, so their white dominated. Now:

- List and grid interiors (`ListBox`, `ListView`, `DataGridView`) take the
  grey face with a sunken border — CDRWIN sits its lists on grey, not white.
- Read-only and multiline `TextBox` output wells go grey; only editable
  single-line fields (image path, disc name) stay white, as CDRWIN's own edit
  fields do.

The result is a predominantly grey window with white confined to the few
places you actually type — matching the reference.

## v1.9.9 — application icon

DiscForge had been using the default .NET application icon (the generic
window/gear). It now has its own: a silver optical disc with a faint rainbow
data-side shimmer, centre hub and hole, on a rounded navy backdrop —
`src/DiscForge.App/DiscForge.ico`, a multi-resolution icon (16 → 256 px).

- `ApplicationIcon` in the csproj embeds it in `DiscForge.exe`, so Explorer,
  the taskbar and Alt-Tab show it.
- `RetroTheme.AppIcon` loads it from the running exe; the launcher and every
  task window use it for their window/taskbar icon.
- The installer stamps the same icon on `setup.exe` (`SetupIconFile`), ships
  the `.ico` into the install folder, and uses it for the CLI-prompt shortcut;
  the uninstall entry already points at the icon-bearing exe.
