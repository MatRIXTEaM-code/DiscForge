// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.PlayStation;
using DiscForge.Core.Vmu;

namespace DiscForge.App.Views;

/// <summary>
/// Read a console memory card — PlayStation 1 (.mcr), PlayStation 2 (.ps2) or
/// Dreamcast VMU — list its saves, and extract them. The card type is detected
/// from the file, so one view covers all three. Plain filesystem work on a
/// person's own card; nothing is decrypted, and the per-file copy-protect flag is
/// reported, not defeated.
/// </summary>
internal sealed class MemoryCardView : UserControl
{
    private enum CardKind { None, Psx, Ps2, Vmu }

    private readonly TextBox _path = new()
    {
        ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(70, 13),
    };
    private readonly Label _summary = new()
    {
        AutoSize = false, Location = new Point(12, 48), Size = new Size(712, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Button _extractAll = new()
    {
        Text = "Extract all…", Location = new Point(604, 84), Width = 120, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly Button _newCard = new()
    {
        Text = "Format new PS1 card…", Location = new Point(12, 84), Width = 150, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // Escape hatch for the one thing this view can't do: DiscForge reads and extracts PS1/PS2
    // cards fully but cannot write a save back onto one (Dreamcast VMU is the only format this
    // project's own code can write) — injecting or editing a PS1 save is exactly the gap
    // MemcardRex fills. DiscForge never bundles or ships it; same launch-what-you-point-it-at
    // contract as every other external-tool button in the app.
    private readonly Button _memcardRex = new()
    {
        Text = "MemcardRex (edit/inject saves)…", Location = new Point(174, 84), Width = 220, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // Same missing-writer gap as MemcardRex, one console family over: DiscForge extracts PS2
    // saves fully but has no writer for that format either. Own row (row 1 was already full)
    // and own remembered path so configuring it doesn't disturb MemcardRex's.
    private readonly Button _ps2SaveBuilder = new()
    {
        Text = "PS2 Save Builder…", Location = new Point(12, 118), Width = 160, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // Same idea again, for GameCube: DiscForge reads/decodes .gci saves but never writes one
    // back onto a card — GCMM is the established tool for that step. Own remembered path.
    private readonly Button _gcmm = new()
    {
        Text = "GCMM…", Location = new Point(184, 118), Width = 90, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly ListView _saves = new()
    {
        // Sits below both button rows (row 2 added for PS2 Save Builder/GCMM ends near Y=144);
        // a grid any higher overlaps them.
        Location = new Point(12, 152), Size = new Size(712, 322),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Font = Theme.Ui, BackColor = Color.White,
    };

    private string? _path0;
    private byte[]? _card;
    private CardKind _kind = CardKind.None;

    public MemoryCardView()
    {
        Size = new Size(736, 486);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Card:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        var open = new Button { Text = "Open…", Location = new Point(548, 12), Width = 80, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => Open();
        _extractAll.Click += (_, _) => ExtractAll();
        _newCard.Click += (_, _) => FormatNewCard();
        _memcardRex.Click += (_, _) => LaunchExternalMemcardRex();
        _ps2SaveBuilder.Click += (_, _) => LaunchExternalPs2SaveBuilder();
        _gcmm.Click += (_, _) => LaunchExternalGcmm();

        foreach (var (name, w) in new[] { ("Save", 300), ("Type", 90), ("Size", 100), ("Detail", 210) })
            _saves.Columns.Add(new ColumnHeader { Text = name, Width = w });

        Controls.Add(_path); Controls.Add(open); Controls.Add(_summary);
        Controls.Add(_extractAll); Controls.Add(_newCard); Controls.Add(_memcardRex);
        Controls.Add(_ps2SaveBuilder); Controls.Add(_gcmm); Controls.Add(_saves);

        _summary.Text = "Open a PS1 (.mcr), PS2 (.ps2) or Dreamcast VMU memory-card image.";
    }

    private void FormatNewCard()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save a new formatted PS1 memory card",
            Filter = "PS1 memory card (*.mcr)|*.mcr|DexDrive (*.gme)|*.gme|VGS (*.vgs)|*.vgs",
            FileName = "blank.mcr",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var raw = PsxMemoryCard.Format();
            string ext = Path.GetExtension(dlg.FileName).TrimStart('.').ToLowerInvariant();
            var target = ext switch
            {
                "gme" => Ps1CardFormat.DexDrive,
                "vgs" or "mem" => Ps1CardFormat.Vgs,
                _ => Ps1CardFormat.Raw,
            };
            var bytes = target == Ps1CardFormat.Raw ? raw : Ps1CardConvert.Convert(raw, target);
            File.WriteAllBytes(dlg.FileName, bytes);
            AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);

            // Load the fresh card into the view so the user sees an empty 15-block card.
            _path0 = dlg.FileName;
            _path.Text = dlg.FileName;
            _card = raw;
            _saves.Items.Clear();
            _extractAll.Enabled = false;
            LoadCard();
            _summary.Text = $"Wrote a formatted {target} card: {Path.GetFileName(dlg.FileName)} — 15/15 blocks free.";
            _summary.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report(_summary.Text);
            AppLog.Write($"Formatted new PS1 card -> {Path.GetFileName(dlg.FileName)} ({target})");
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not write the card: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("memory card format", ex);
        }
    }

    private void Open()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Memory cards (*.mcr;*.mcd;*.ps2;*.bin;*.vmu;*.vms)|*.mcr;*.mcd;*.ps2;*.bin;*.vmu;*.vms|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _path0 = dlg.FileName;
        _path.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        _saves.Items.Clear();
        _extractAll.Enabled = false;

        try
        {
            _card = File.ReadAllBytes(dlg.FileName);
            LoadCard();
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not read the card: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("memory card open", ex);
        }
    }

    private void LoadCard()
    {
        var card = _card!;
        if (Ps2MemoryCard.IsPs2MemoryCard(card))
        {
            _kind = CardKind.Ps2;
            var vol = Ps2MemoryCard.Read(card);
            foreach (var s in vol.Saves)
            {
                var files = vol.Files.Where(f => f.Path.StartsWith(s.Path + "/")).ToList();
                Add(s.Path.TrimStart('/'), "PS2 save", $"{files.Count} file(s)", "");
            }
            _summary.Text = $"PlayStation 2 memory card — {vol.Saves.Count()} save(s), {vol.Files.Count()} file(s).";
        }
        else if (PsxMemoryCard.IsPsxMemoryCard(card))
        {
            _kind = CardKind.Psx;
            var vol = PsxMemoryCard.Read(card);
            foreach (var s in vol.Saves)
                Add(s.Name, "PS1 save", $"{s.Blocks.Count} block(s)", s.Title);
            _summary.Text = $"PlayStation 1 memory card — {vol.Saves.Count} save(s), {vol.FreeBlocks}/15 free.";
        }
        else if (VmuImage.IsVmu(card) || card.Length == VmuImage.ImageSize)
        {
            _kind = CardKind.Vmu;
            var vol = VmuImage.Read(card);
            foreach (var f in vol.Files)
                Add(f.Name, f.IsGame ? "VMU game" : "VMU data", $"{f.SizeBlocks} block(s)",
                    (f.LongDescription ?? "") + (f.CopyProtected ? "  [protected]" : ""));
            _summary.Text = $"Dreamcast VMU — {vol.Files.Count} save(s), {vol.FreeBlocks}/{vol.UserBlocks} free.";
        }
        else
        {
            _kind = CardKind.None;
            _summary.Text = "Not a recognised memory card (PS1 .mcr, PS2 .ps2 or Dreamcast VMU).";
            _summary.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
            return;
        }

        _summary.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
        _extractAll.Enabled = _saves.Items.Count > 0;
    }

    private void Add(string name, string type, string size, string detail)
    {
        var item = new ListViewItem(name);
        item.SubItems.Add(type);
        item.SubItems.Add(size);
        item.SubItems.Add(detail);
        _saves.Items.Add(item);
    }

    private void ExtractAll()
    {
        if (_card is null || _kind == CardKind.None) return;
        using var folder = new FolderBrowserDialog
        {
            Description = "Extract every save to…",
            SelectedPath = AppSettings.LastExtractDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastExtractDirectory = folder.SelectedPath;

        try
        {
            int n = _kind switch
            {
                CardKind.Psx => ExtractPsx(folder.SelectedPath),
                CardKind.Ps2 => ExtractPs2(folder.SelectedPath),
                CardKind.Vmu => ExtractVmu(folder.SelectedPath),
                _ => 0,
            };
            _summary.Text = $"Extracted {n} item(s) to {folder.SelectedPath}";
            _summary.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report(_summary.Text);
        }
        catch (Exception ex)
        {
            _summary.Text = "Extraction failed: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("memory card extract", ex);
        }
    }

    private int ExtractPsx(string dir)
    {
        var vol = PsxMemoryCard.Read(_card!);
        int n = 0;
        foreach (var s in vol.Saves)
        {
            string safe = Safe(s.Name, $"save{n}");
            File.WriteAllBytes(Path.Combine(dir, safe + ".mcs"), PsxMemoryCard.Extract(_card!, s));
            n++;
        }
        return n;
    }

    private int ExtractPs2(string dir)
    {
        var vol = Ps2MemoryCard.Read(_card!);
        int n = 0;
        foreach (var f in vol.Files)
        {
            string outPath = Path.Combine(dir, f.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, Ps2MemoryCard.Extract(_card!, vol, f));
            n++;
        }
        return n;
    }

    private int ExtractVmu(string dir)
    {
        var vol = VmuImage.Read(_card!);
        int n = 0, skipped = 0;
        foreach (var f in vol.Files)
        {
            try
            {
                File.WriteAllBytes(Path.Combine(dir, Safe(f.Name, $"save{n}") + ".VMS"), VmuImage.Extract(_card!, f));
                n++;
            }
            catch (InvalidOperationException) { skipped++; }
        }
        if (skipped > 0) AppLog.Write($"  VMU extract: {skipped} copy-protected save(s) skipped");
        return n;
    }

    /// <summary>
    /// Launch a user-supplied MemcardRex (asked for once, then remembered) — the escape hatch
    /// for the one thing this view can't do: write a save back onto a card. Delegates to
    /// <see cref="ExternalToolLauncher"/>, the same shared logic every other external-tool button
    /// in the app uses; this view has no event log, so it reports through <see cref="_summary"/>
    /// instead. DiscForge does not bundle, invoke undocumented commands for, or know anything
    /// about what the tool does; it only starts the process the user points it at.
    /// </summary>
    private void LaunchExternalMemcardRex() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathMemcardRex,
        p => Settings.ExternalDumperPathMemcardRex = p,
        "Locate MemcardRex",
        "Use it to edit or inject saves — DiscForge did not write to the card.",
        ReportToSummary);

    /// <summary>Same idea as <see cref="LaunchExternalMemcardRex"/>, for PS2 saves — the same
    /// missing-writer gap, one console family over. Own remembered path so configuring it
    /// doesn't disturb MemcardRex's.</summary>
    private void LaunchExternalPs2SaveBuilder() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathPs2SaveBuilder,
        p => Settings.ExternalDumperPathPs2SaveBuilder = p,
        "Locate a PS2 save-editing tool (e.g. PS2 Save Builder)",
        "Use it to build or inject a save — DiscForge did not write to the card.",
        ReportToSummary);

    /// <summary>Same idea again, for GameCube: DiscForge reads/decodes .gci saves but never
    /// writes one back onto a card. GCMM is the established tool for that step.</summary>
    private void LaunchExternalGcmm() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathGcmm,
        p => Settings.ExternalDumperPathGcmm = p,
        "Locate GCMM (GameCube Memory Manager)",
        "Use it to write the save onto a card or SD adapter — DiscForge did not write to it.",
        ReportToSummary);

    /// <summary>Shared report sink for this view's external-tool buttons — this screen has no
    /// event log the way Read/Burn do, so every one of them reports through <see cref="_summary"/>.</summary>
    private void ReportToSummary(string message, bool isError)
    {
        _summary.Text = message;
        _summary.ForeColor = isError ? Color.FromArgb(0xA0, 0x20, 0x20) : Color.FromArgb(0x20, 0x70, 0x20);
        if (!isError) StatusBus.Report(message);
    }

    private static string Safe(string name, string fallback)
    {
        string s = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return s.Length == 0 ? fallback : s;
    }
}
