// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DiscForge.App;

/// <summary>
/// The DiscForge front door, in the CDRWIN 4 idiom: a small fixed window that
/// is nothing but a grid of large flat icon buttons, each opening its own task
/// window. This IS the application — there is no modern shell, no sidebar, no
/// tabbed content. Golden Hawk's CDRWIN opened exactly this way: a wall of big
/// buttons (Record Disc, Copy Disc, Tools, Settings, Exit), each launching a
/// dedicated dialog. DiscForge follows that structure faithfully.
/// </summary>
internal sealed class CdrwinLauncher : Form
{
    private readonly record struct Tile(
        string Key, string Label, Color A, Color B, string Glyph, Func<Control>? Make, string Blurb);

    private readonly Tile[] _tiles;
    private int _hot = -1;

    private const int TileW = 116, TileH = 96, Gap = 8, Pad = 12;
    private const int TopStrip = 60;

    // Columns are chosen at construction so the grid fits the screen height.
    private readonly int _cols = 4;

    public CdrwinLauncher()
    {
        _tiles = new Tile[]
        {
            new("record",  "Record Disc",   C(0xC0,0x51,0xE8), C(0x6E,0x1F,0xA1), "🔥",
                () => new Views.BurnView(), "Write an image to a recorder"),
            new("copy",    "Copy Disc",     C(0xE8,0xA5,0x3C), C(0xA1,0x66,0x0F), "⇄",
                () => new Views.CopyView(), "Duplicate a disc"),
            new("read",    "Read Disc",     C(0x53,0xC0,0x6A), C(0x1E,0x7A,0x39), "📀",
                () => new Views.ReadView(), "Rip a disc to an image"),
            new("create",  "Create Image",  C(0x4C,0x8B,0xE8), C(0x1F,0x45,0xA1), "💿",
                null, "Build an image from files"),
            new("rawlab",  "Raw Lab",       C(0x8B,0x8B,0x9C), C(0x45,0x45,0x55), "⚙",
                () => new Views.RawLabView(), "Compose / analyse raw DAO"),
            new("inspect", "Inspect",       C(0xE8,0x51,0x51), C(0xA1,0x1F,0x1F), "🔍",
                null, "Read and verify an image"),
            new("sectors", "Sector Viewer", C(0x3C,0xB8,0xC0), C(0x0F,0x6E,0x76), "▦",
                () => new Views.SectorView(), "Annotated hex of any sector"),
            new("tools",   "Tools",         C(0xC0,0xB0,0x3C), C(0x76,0x68,0x0F), "🧰",
                () => new Views.ToolsView(), "Checksums, split / join"),
            new("drives",  "Drives",        C(0x6A,0x9C,0xE8), C(0x2A,0x50,0xA1), "🖥",
                () => new Views.DrivesView(), "Detected recorders"),
            new("protect", "Protection",     C(0xE8,0x6A,0x3C), C(0xA1,0x3C,0x0F), "🛡",
                () => new Views.ProtectionView(), "Scan for copy-protection"),
            new("dvdshrink","DVD Shrink",    C(0x3C,0xC0,0x8B), C(0x0F,0x76,0x4E), "🎬",
                () => new Views.DvdShrinkView(), "Shrink DVD-Video to fit"),
            new("accuraterip","AccurateRip", C(0x3C,0x8B,0xC0), C(0x0F,0x4E,0x76), "🎵",
                () => new Views.AccurateRipView(), "Verify an audio rip"),
            new("mount",   "Mount",         C(0xC0,0x9C,0x3C), C(0x76,0x5E,0x0F), "💾",
                () => new Views.MountView(), "Mount an image as a drive"),
            new("interop", "CloneCD",        C(0x9C,0x6A,0xC0), C(0x5E,0x2A,0x76), "🔗",
                () => new Views.InteropView(), "Read / write CloneCD .ccd"),
           new("recovery","Recovery",      C(0x50,0xB0,0x90), C(0x1E,0x6E,0x55), "🩹",
                () => new Views.RecoveryView(), "Recover damaged sectors using C2"),
            new("merge",   "Merge Rips",    C(0x50,0xB0,0x70), C(0x1E,0x6E,0x40), "🧩",
                () => new Views.MergeView(), "Merge several rips of the same disc into one verified image"),
            new("vobdemux","VOB Demux",     C(0xB0,0x80,0x50), C(0x70,0x48,0x1E), "✂",
                () => new Views.VobDemuxView(), "Split an unencrypted VOB/MPG into elementary streams"),
            new("vcd",     "Video CD",      C(0x50,0x90,0xB0), C(0x1E,0x50,0x70), "📼",
                () => new Views.VcdView(), "Write the INFO.VCD / ENTRIES.VCD control sectors"),
            new("ifoedit", "IFO Editor",    C(0xA0,0x70,0x90), C(0x60,0x38,0x54), "🗺",
                () => new Views.IfoEditView(), "Dump, edit and rebuild DVD-Video IFO structure"),
new("quality", "Disc Quality",  C(0x50,0x90,0xB0), C(0x1E,0x55,0x6E), "📊",
                () => new Views.QualityView(), "Measure surface errors and disc health"),
new("browse",  "Browse Files",  C(0x60,0xA0,0x60), C(0x28,0x60,0x28), "📁",
                () => new Views.BrowseView(), "List and extract files from an image"),
new("ripaudio","Rip Audio",     C(0xB0,0x60,0xA0), C(0x6E,0x28,0x60), "🎧",
                () => new Views.RipAudioView(), "Rip an audio CD to WAV with AccurateRip"),
new("cue",     "Cue Editor",    C(0x80,0x90,0xB0), C(0x40,0x50,0x70), "📝",
                () => new Views.CueEditorView(), "Check and repair a cuesheet"),
new("subcode", "Sub-channel",   C(0x90,0x80,0xB0), C(0x50,0x40,0x70), "〰",
                () => new Views.SubcodeView(), "Analyse Q sub-channel and LibCrypt fingerprints"),
new("dvdinfo", "DVD Structure", C(0xA0,0x70,0x50), C(0x60,0x38,0x20), "🎬",
                () => new Views.DvdInfoView(), "Titles, chapters, audio and subtitle streams"),
new("pack",    "Pack Discs",    C(0x70,0xA0,0x80), C(0x30,0x60,0x48), "📦",
                () => new Views.PackView(), "Fit files across discs with the least waste"),
            new("transcode","Shrink Video", C(0xB0,0x70,0x70), C(0x70,0x30,0x30), "🎞",
                () => new Views.TranscodeView(), "Re-encode video to fit a target size"),
            new("patch",   "PPF Patch",     C(0xB0,0x90,0x50), C(0x70,0x50,0x1E), "🩹",
                () => new Views.PatchView(), "Apply or build a PlayStation PPF patch"),
            new("dreamcast","Dreamcast",     C(0x5A,0x7A,0xC0), C(0x24,0x38,0x76), "🎮",
                () => new Views.DreamcastView(), "Browse, extract and convert a GD-ROM (.gdi)"),
            new("xbox",    "Xbox",          C(0x4C,0xA0,0x50), C(0x18,0x60,0x24), "🟢",
                () => new Views.XboxView(), "Browse, extract and build an Xbox XISO"),
            new("udfcreate","UDF Image",     C(0x5C,0x86,0xB0), C(0x24,0x40,0x70), "🗂",
                () => new Views.UdfCreateView(), "Build a UDF 1.02 image from a folder"),
            new("identify","Identify File",  C(0x6C,0x9C,0x6C), C(0x28,0x54,0x28), "🔎",
                () => new Views.IdentifyView(), "Say what any file is"),
            new("examine", "Examine",        C(0x3C,0x9C,0xB8), C(0x0F,0x56,0x76), "🔬",
                () => new Views.ExamineView(), "Identify any file and show its parsed details — incl. Rock Ridge, HFS resource forks, FAT16/32, N64 CIC, TPL and HDCD"),
            new("library", "Library",        C(0xC8,0x8A,0x3C), C(0x86,0x53,0x10), "📚",
                () => new Views.LibraryView(), "Scan, verify vs a DAT, and rename a whole collection"),
            new("convert", "Convert",        C(0x5A,0x8A,0xD0), C(0x22,0x48,0x86), "🔀",
                () => new Views.ConvertView(), "Convert any disc image to any other format"),
            new("submit",  "Submit",         C(0x6C,0xA8,0x6C), C(0x2A,0x60,0x2A), "📤",
                () => new Views.SubmitView(), "Generate redump.org-style submission info for a dump"),
            new("extract", "Extract",        C(0x4C,0xA0,0x8B), C(0x18,0x60,0x50), "🗃",
                () => new Views.ExtractView(), "Pull files/saves out of a WBFS, floppy, memory card or PBP"),
            new("cheat",   "Cheat Codes",    C(0x8B,0x5C,0xC0), C(0x50,0x24,0x86), "🎯",
                () => new Views.CheatView(), "Decode / encode Game Genie and GameShark codes"),
            new("media",   "Game Media",     C(0xC0,0x6C,0x5C), C(0x76,0x28,0x22), "🔊",
                () => new Views.MediaView(), "Decode ADX→WAV and render CD+G→PNG"),
            new("playlists","Playlists",      C(0x5C,0x8C,0x6C), C(0x22,0x52,0x30), "📋",
                () => new Views.PlaylistsView(), "Export RetroArch .lpl, EmulationStation gamelist, and multi-disc M3U"),
            new("sets",    "Sets",           C(0xC8,0x70,0x50), C(0x86,0x38,0x18), "🗂",
                () => new Views.SetsView(), "1G1R region-filter a DAT and rebuild a clean, DAT-named set"),
            new("datbuild","DAT Build",      C(0xB0,0x90,0x40), C(0x6E,0x54,0x14), "🏷",
                () => new Views.DatBuildView(), "Hash a folder of dumps into a Redump-style DAT"),
            new("memcard", "Memory Cards",   C(0xB0,0x6C,0x9C), C(0x6E,0x28,0x54), "💳",
                () => new Views.MemoryCardView(), "Read PS1 / PS2 / Dreamcast VMU saves"),
            new("psxasset","PSX Assets",     C(0xC0,0x8B,0x4C), C(0x76,0x50,0x18), "🎨",
                () => new Views.PsxAssetView(), "TIM→PNG, VAG→WAV, TMD→DXF, PS-EXE"),
            new("textures","Textures",       C(0x8B,0xC0,0x4C), C(0x50,0x76,0x18), "🖼",
                () => new Views.TextureView(), "Decode GameCube/Wii TPL textures to PNG (preview + extract)"),
            new("compimg", "Compressed",     C(0x4C,0x9C,0xA0), C(0x18,0x5E,0x62), "🗜",
                () => new Views.CompressedImageView(), "CSO/ZSO ↔ ISO, and identify CHD"),
            new("bincue",  "Bin/Cue",        C(0x9C,0xA0,0x4C), C(0x5E,0x62,0x18), "🧩",
                () => new Views.BinCueView(), "Merge per-track bin/cue into one, or split it back"),
            new("psxbuild","PSX Build",      C(0xC0,0x6C,0x8B), C(0x76,0x28,0x4E), "🛠",
                () => new Views.PsxBuildView(), "Build a Mode 2/2352 bin/cue from a folder"),
            new("scummvm", "ScummVM",        C(0x5C,0xA0,0x9C), C(0x22,0x60,0x5C), "🕹",
                () => new Views.ScummVmView(), "Fingerprint a game or export a disc to a ScummVM folder"),
            new("milcd",   "MIL-CD → CDI",  C(0x50,0x6C,0xC0), C(0x1E,0x30,0x76), "💿",
                () => new Views.MilCdToCdiView(), "Convert a Dreamcast MIL-CD bin/cue to a two-session CDI"),
            new("dcid",    "Identify DC",   C(0x4C,0x6C,0xA0), C(0x1A,0x2E,0x60), "🔷",
                () => new Views.IdentifyDreamcastView(), "Read a Dreamcast disc's IP.BIN boot header"),
            new("help",    "Help",           C(0x50,0x78,0xB0), C(0x1E,0x3C,0x70), "📖",
                () => new Views.HelpView(), "What each tile does and how to use it"),
            new("settings","Settings",      C(0x9C,0x9C,0x9C), C(0x50,0x50,0x50), "🛠",
                null, "Preferences and diagnostics"),
            new("verify",  "Verify & Lint", C(0x50,0xA0,0x78), C(0x1E,0x66,0x48), "✓",
                () => new Views.VerifyView(), "Check an image: ISO/UDF/FAT/HFS conformance, filesystem cross-check, CHD integrity, PS2 card ECC"),
            new("about",   "About",         C(0x70,0x90,0xB0), C(0x30,0x48,0x60), "ℹ",
                null, "About DiscForge"),
            new("exit",    "Exit",          C(0xB0,0x50,0x50), C(0x60,0x20,0x20), "✕",
                null, "Close DiscForge"),
        };

        Text = LicenseGate.IsLicensed ? "DiscForge" : "DiscForge — UNLICENSED (evaluation)";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = RetroTheme.Face;
        DoubleBuffered = true;
        Font = RetroTheme.Ui;
        RetroTheme.Enabled = true;

        // Widen the grid (more columns → fewer rows) until it fits the working
        // area, so the window never runs off the bottom of the screen and fills
        // the height it has. Leave room for the title bar and taskbar.
        int available = (Screen.PrimaryScreen?.WorkingArea.Height ?? 900) - 56;
        _cols = 4;
        while (_cols < _tiles.Length)
        {
            int r = (_tiles.Length + _cols - 1) / _cols;
            int h = TopStrip + Pad + r * TileH + (r - 1) * Gap + 28;
            if (h <= available) break;
            _cols++;
        }

        int rows = (_tiles.Length + _cols - 1) / _cols;
        ClientSize = new Size(
            Pad * 2 + _cols * TileW + (_cols - 1) * Gap,
            TopStrip + Pad + rows * TileH + (rows - 1) * Gap + 28);

        if (RetroTheme.AppIcon is { } ic) Icon = ic;

        MouseMove += (_, e) => { int h = HitTest(e.Location); if (h != _hot) { _hot = h; Invalidate(); } };
        MouseLeave += (_, _) => { if (_hot != -1) { _hot = -1; Invalidate(); } };
        MouseClick += (_, e) => { int h = HitTest(e.Location); if (h >= 0) Launch(_tiles[h]); };
    }

    private bool _nagShown;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // One-time evaluation reminder per launch (soft enforcement — never blocks).
        if (!_nagShown && !LicenseGate.IsLicensed)
        {
            _nagShown = true;
            using var a = new ActivationForm();
            a.ShowDialog(this);
            Text = LicenseGate.IsLicensed ? "DiscForge" : "DiscForge — UNLICENSED (evaluation)";
            Invalidate();
        }
    }

    private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

    private Rectangle TileRect(int i)
    {
        int col = i % _cols, row = i / _cols;
        return new Rectangle(
            Pad + col * (TileW + Gap),
            TopStrip + row * (TileH + Gap),
            TileW, TileH);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _tiles.Length; i++)
            if (TileRect(i).Contains(p)) return i;
        return -1;
    }

    private void Launch(Tile tile)
    {
        switch (tile.Key)
        {
            case "exit":
                Close();
                return;
            case "about":
                using (var a = new AboutForm()) a.ShowDialog(this);
                return;
            case "settings":
                CdrwinTaskWindow.ShowSettings(this);
                return;
            case "create":
                // CDRWIN's "Record Disc" flow starts by choosing a disc type.
                using (var dlg = new Views.RetroDiscTypeDialog())
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var win = new CdrwinTaskWindow(tile.Label, MakeCreateView());
                    win.Show(this);
                }
                return;
            case "inspect":
                new CdrwinTaskWindow(tile.Label, MakeInspectView()).Show(this);
                return;
            default:
                if (tile.Make is not null)
                    new CdrwinTaskWindow(tile.Label, tile.Make()).Show(this);
                return;
        }
    }

    private static Control MakeCreateView()
    {
        try { return new Views.CreateView(); }
        catch { return Placeholder("Create Image"); }
    }

    private static Control MakeInspectView()
    {
        try { return new Views.InspectView(); }
        catch { return Placeholder("Inspect / Verify"); }
    }

    private static Control Placeholder(string name)
        => new Label { Text = name + " — not available.", AutoSize = true, Padding = new Padding(16) };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(RetroTheme.Face);

        // Title strip: solid navy with a raised bevel and the product name.
        var strip = new Rectangle(Pad, 10, ClientSize.Width - Pad * 2, 40);
        using (var brush = new SolidBrush(RetroTheme.TitleActive))
            g.FillRectangle(brush, strip);
        RetroTheme.DrawBevel(g, strip, RetroTheme.Bevel.Raised);

        DrawDisc(g, strip.X + 10, strip.Y + 8, 24, Color.FromArgb(0xC8, 0xD8, 0xF0));
        using (var title = new Font("MS Sans Serif", 14f, FontStyle.Bold))
            TextRenderer.DrawText(g, "DiscForge", title,
                new Rectangle(strip.X + 44, strip.Y, strip.Width - 48, strip.Height),
                Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        string ver = "CD / DVD / Blu-ray  v" + typeof(CdrwinLauncher).Assembly.GetName().Version?.ToString(3);
        bool licensed = LicenseGate.IsLicensed;
        using (var sub = new Font("MS Sans Serif", 8f))
            TextRenderer.DrawText(g, licensed ? ver : ver + "   •   UNLICENSED", sub,
                new Rectangle(strip.X, strip.Y, strip.Width - 8, strip.Height),
                licensed ? Color.FromArgb(0xC8, 0xD8, 0xF0) : Color.FromArgb(0xFF, 0xD8, 0x60),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

        for (int i = 0; i < _tiles.Length; i++)
            DrawTile(g, TileRect(i), _tiles[i], i == _hot);

        // Status line: the hovered tile's blurb.
        string hint = _hot >= 0 ? _tiles[_hot].Blurb : "Choose a task.";
        var line = new Rectangle(Pad, ClientSize.Height - 24, ClientSize.Width - Pad * 2, 18);
        RetroTheme.DrawBevel(g, line, RetroTheme.Bevel.Sunken);
        TextRenderer.DrawText(g, hint, RetroTheme.Ui,
            new Rectangle(line.X + 6, line.Y, line.Width - 10, line.Height),
            RetroTheme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    private static void DrawTile(Graphics g, Rectangle r, Tile tile, bool hot)
    {
        // Flat 90s icon button: a solid raised bevel, a flat two-tone icon
        // plate, and a label — no gloss, no gradient. This matches CDRWIN's
        // dense grid of little coloured squares rather than XP-era tiles.
        using (var face = new SolidBrush(RetroTheme.Face))
            g.FillRectangle(face, r);
        RetroTheme.DrawBevel(g, r, hot ? RetroTheme.Bevel.Sunken : RetroTheme.Bevel.Raised);

        // Icon plate: a flat coloured square with a thin dark border, the
        // era's busy-little-icon look.
        var plate = new Rectangle(r.X + (r.Width - 44) / 2, r.Y + 8, 44, 40);
        using (var fill = new SolidBrush(tile.A))
            g.FillRectangle(fill, plate);
        // A simple two-tone accent inside so it reads as an "icon", not a swatch.
        using (var accent = new SolidBrush(tile.B))
            g.FillRectangle(accent, plate.X + 4, plate.Y + plate.Height - 12, plate.Width - 8, 8);
        using (var edge = new Pen(Color.FromArgb(0x30, 0x30, 0x30)))
            g.DrawRectangle(edge, plate);
        using (var lite = new Pen(Color.FromArgb(120, 255, 255, 255)))
            g.DrawLine(lite, plate.X + 1, plate.Y + 1, plate.Right - 1, plate.Y + 1);

        using var glyphFont = new Font("Segoe UI Emoji", 16f);
        TextRenderer.DrawText(g, tile.Glyph, glyphFont, plate, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var labelRect = new Rectangle(r.X + 2, r.Bottom - 26, r.Width - 4, 20);
        using var labelFont = new Font("MS Sans Serif", 8f, FontStyle.Bold);
        TextRenderer.DrawText(g, tile.Label, labelFont, labelRect, RetroTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private static void DrawDisc(Graphics g, int x, int y, int d, Color tint)
    {
        using var body = new SolidBrush(tint);
        g.FillEllipse(body, x, y, d, d);
        using var rim = new Pen(Color.FromArgb(80, 0, 0, 0));
        g.DrawEllipse(rim, x, y, d, d);
        using var hole = new SolidBrush(RetroTheme.TitleActive);
        g.FillEllipse(hole, x + d / 2 - 3, y + d / 2 - 3, 6, 6);
    }
}
