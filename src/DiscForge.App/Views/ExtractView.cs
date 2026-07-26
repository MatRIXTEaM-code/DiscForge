// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Floppy;
using DiscForge.Core.PlayStation;
using DiscForge.Core.Psp;
using DiscForge.Core.Saves;
using DiscForge.Core.Vmu;
using DiscForge.Core.Wbfs;

namespace DiscForge.App.Views;

/// <summary>
/// The universal container extractor: drop a WBFS container, a floppy image
/// (D64 / ADF / FAT12), a memory card (PS1 .mcr, GameCube card, Dreamcast VMU),
/// or a PSP EBOOT.PBP, and DiscForge lists what is inside so you can pull one
/// item — or all of them — back out to disk. It is the write-side companion to
/// the Examine tile: Examine tells you what a file holds, Extract gets it out.
/// A thin shell over the same Core readers the <c>dforge</c> *-extract commands
/// use (WbfsReader, D64/Adf/Fat12Reader, PsxMemoryCard, GcMemoryCardReader,
/// VmuImage, PbpFile). DATA.PSP, when present in a PBP, is written raw and is
/// never decrypted.
/// </summary>
internal sealed class ExtractView : UserControl
{
    // Formats other than WBFS are read whole into memory; guard against someone
    // dropping a huge disc image here (WBFS is handled stream-only, before this).
    private const long MaxInMemoryBytes = 512L * 1024 * 1024;

    private sealed record Item(string Display, string Detail, string SuggestedName, Action<string> ExtractTo);

    private readonly TextBox _path = new() { ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(90, 14) };
    private readonly Label _kind = new() { AutoSize = true, Font = Theme.UiBold, Location = new Point(12, 46) };
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, Font = Theme.Ui,
        Location = new Point(12, 72), Size = new Size(712, 300),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };
    private readonly Button _selected = new() { Text = "Extract selected…", Location = new Point(12, 380), Width = 150, FlatStyle = FlatStyle.System, Enabled = false, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
    private readonly Button _all = new() { Text = "Extract all…", Location = new Point(170, 380), Width = 120, FlatStyle = FlatStyle.System, Enabled = false, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };

    private readonly List<Item> _items = new();

    public ExtractView()
    {
        Size = new Size(736, 416);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) LoadFile(f[0]); };

        _list.Columns.Add("Item", 260);
        _list.Columns.Add("Detail", 280);
        _list.Columns.Add("Saves as", 150);

        Controls.Add(new Label { Text = "File:", AutoSize = true, Location = new Point(12, 17), Font = Theme.Ui });
        var pick = new Button { Text = "…", Location = new Point(566, 13), Width = 30, FlatStyle = FlatStyle.System };
        pick.Click += (_, _) => Choose();
        _selected.Click += (_, _) => ExtractSelected();
        _all.Click += (_, _) => ExtractAll();
        _list.SelectedIndexChanged += (_, _) => _selected.Enabled = _list.SelectedIndices.Count > 0;

        Controls.AddRange(new Control[] { _path, pick, _kind, _list, _selected, _all });
        _kind.Text = "Drop a WBFS, floppy image, memory card, or EBOOT.PBP to list its contents.";
        _kind.ForeColor = Color.Gray;
    }

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void Choose()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Extractable containers (*.wbfs;*.d64;*.adf;*.img;*.mcr;*.mc;*.raw;*.bin;*.vmu;*.pbp)|" +
                     "*.wbfs;*.d64;*.adf;*.img;*.mcr;*.mc;*.raw;*.bin;*.vmu;*.pbp|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() == DialogResult.OK) LoadFile(dlg.FileName);
    }

    private void LoadFile(string path)
    {
        AppSettings.LastImageDirectory = Path.GetDirectoryName(path);
        _path.Text = path;
        _items.Clear();
        _list.Items.Clear();
        _selected.Enabled = false;
        _all.Enabled = false;

        try
        {
            if (!Detect(path))
            {
                _kind.Text = "Nothing to extract — not a WBFS, floppy, memory card, or PBP DiscForge can open.";
                _kind.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
                return;
            }

            foreach (var it in _items)
                _list.Items.Add(new ListViewItem(new[] { it.Display, it.Detail, it.SuggestedName }));
            _all.Enabled = _items.Count > 0;
            _kind.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report($"{Path.GetFileName(path)}: {_items.Count} item(s) to extract");
        }
        catch (Exception ex)
        {
            _kind.Text = "Could not read this file.";
            _kind.ForeColor = Color.FromArgb(0xA0, 0x30, 0x30);
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("extract", ex);
        }
    }

    // Populate _items and set _kind. Returns false when nothing matched.
    private bool Detect(string path)
    {
        // WBFS first: it is stream-only so a multi-gigabyte container never lands in memory.
        using (var fs = File.OpenRead(path))
        {
            if (WbfsReader.IsWbfs(fs))
            {
                fs.Position = 0;
                var wbfs = WbfsReader.Read(fs);
                _kind.Text = $"WBFS container — {wbfs.Discs.Count} disc(s)";
                foreach (var d in wbfs.Discs)
                {
                    var disc = d;
                    string name = Sanitize($"{disc.GameId}_{disc.Title}") + ".iso";
                    _items.Add(new Item($"Slot {disc.Slot}: {disc.GameId}", disc.Title, name, dest =>
                    {
                        using var src = File.OpenRead(path);
                        var w = WbfsReader.Read(src);
                        var target = w.Discs.First(x => x.Slot == disc.Slot);
                        using var os = File.Create(dest);
                        WbfsReader.ExtractDisc(src, target, os);
                    }));
                }
                return true;
            }
        }

        var info = new FileInfo(path);
        if (info.Length > MaxInMemoryBytes) return false;
        byte[] data = File.ReadAllBytes(path);

        if (PbpFile.IsPbp(data))
        {
            var pbp = PbpFile.Parse(data);
            _kind.Text = $"PSP EBOOT.PBP (version 0x{pbp.Version:X8}) — {pbp.Sections.Count(s => !s.IsEmpty)} section(s)";
            foreach (var s in pbp.Sections)
            {
                if (s.IsEmpty) continue;
                string sectionName = s.Name;
                string note = sectionName == "DATA.PSP" ? "raw — not decrypted" : $"{s.Size:N0} bytes";
                _items.Add(new Item(sectionName, note, sectionName,
                    dest => File.WriteAllBytes(dest, PbpFile.GetSection(data, sectionName))));
            }
            return true;
        }

        if (PsxMemoryCard.IsPsxMemoryCard(data))
        {
            var vol = PsxMemoryCard.Read(data);
            _kind.Text = $"PlayStation memory card — {vol.Saves.Count} save(s)";
            int n = 0;
            foreach (var s in vol.Saves)
            {
                var save = s;
                string safe = Sanitize(save.Name); if (safe.Length == 0) safe = $"save{n}";
                _items.Add(new Item(save.Name, (save.Title.Length > 0 ? save.Title + "  " : "") + $"{save.Blocks.Count} block(s)",
                    safe + ".mcs", dest => File.WriteAllBytes(dest, PsxMemoryCard.Extract(data, save))));
                n++;
            }
            return true;
        }

        if (GcMemoryCardReader.IsGcMemoryCard(data))
        {
            var card = GcMemoryCardReader.Read(data);
            _kind.Text = $"GameCube memory card — {card.Saves.Count} save(s)";
            int n = 0;
            foreach (var s in card.Saves)
            {
                var save = s;
                string safe = Sanitize(save.FileName); if (safe.Length == 0) safe = $"save{n}";
                _items.Add(new Item(save.FileName, $"{save.GameCode}  {save.Comment}", safe + ".gci",
                    dest => File.WriteAllBytes(dest, GcMemoryCardReader.ExtractSaveToGci(data, save))));
                n++;
            }
            return true;
        }

        if (VmuImage.IsVmu(data))
        {
            var vmu = VmuImage.Read(data);
            _kind.Text = $"Dreamcast VMU — {vmu.Files.Count} save(s)";
            int n = 0;
            foreach (var f in vmu.Files)
            {
                var file = f;
                string name = file.Name.Length > 0 ? file.Name : $"save{n}";
                _items.Add(new Item(name, file.CopyProtected ? "copy-protected" : $"{file.SizeBlocks} block(s)",
                    Sanitize(name) + ".VMS", dest => File.WriteAllBytes(dest, VmuImage.Extract(data, file, force: true))));
                n++;
            }
            return true;
        }

        if (D64Reader.IsD64(data))
        {
            var disk = D64Reader.Read(data);
            _kind.Text = $"C64 D64 — \"{disk.DiskName}\", {disk.Files.Count} file(s)";
            foreach (var e in disk.Files)
            {
                var entry = e;
                _items.Add(new Item(entry.Name, $"{entry.Type}  {entry.SizeBlocks} blk", Sanitize(entry.Name),
                    dest => File.WriteAllBytes(dest, D64Reader.ExtractFile(data, entry))));
            }
            return true;
        }

        if (AdfReader.IsAdf(data))
        {
            var disk = AdfReader.Read(data);
            _kind.Text = $"Amiga ADF — \"{disk.DiskName}\" ({(disk.Ffs ? "FFS" : "OFS")})";
            foreach (var e in disk.Entries)
            {
                if (e.IsDirectory) continue;
                var entry = e;
                _items.Add(new Item(entry.Path, $"{entry.Size:N0} B", Sanitize(entry.Name),
                    dest => File.WriteAllBytes(dest, AdfReader.ExtractFile(data, entry))));
            }
            return true;
        }

        if (Fat12Reader.IsFat12(data))
        {
            var disk = Fat12Reader.Read(data);
            _kind.Text = $"DOS FAT12 — \"{disk.VolumeLabel}\"";
            foreach (var e in disk.Entries)
            {
                if (e.IsDirectory) continue;
                var entry = e;
                string leaf = entry.Path.TrimStart('/').Replace('/', '_');
                _items.Add(new Item(entry.Path, $"{entry.Size:N0} B", Sanitize(leaf),
                    dest => File.WriteAllBytes(dest, Fat12Reader.ExtractFile(data, entry))));
            }
            return true;
        }

        return false;
    }

    private void ExtractSelected()
    {
        var picked = _list.SelectedIndices.Cast<int>().Select(i => _items[i]).ToList();
        if (picked.Count == 0) return;

        if (picked.Count == 1)
        {
            var item = picked[0];
            using var dlg = new SaveFileDialog { FileName = item.SuggestedName, Filter = "All files (*.*)|*.*" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            RunExtract(new[] { (item, dlg.FileName) });
        }
        else
        {
            string? dir = ChooseFolder();
            if (dir is null) return;
            RunExtract(picked.Select(it => (it, Path.Combine(dir, it.SuggestedName))).ToList());
        }
    }

    private void ExtractAll()
    {
        string? dir = ChooseFolder();
        if (dir is null) return;
        RunExtract(_items.Select(it => (it, Path.Combine(dir, it.SuggestedName))).ToList());
    }

    private void RunExtract(IReadOnlyList<(Item Item, string Dest)> jobs)
    {
        int ok = 0; string? firstError = null;
        foreach (var (item, dest) in jobs)
        {
            try { item.ExtractTo(dest); ok++; }
            catch (Exception ex) { firstError ??= $"{item.Display}: {ex.Message}"; AppLog.WriteException("extract", ex); }
        }
        if (firstError is not null)
            RetroMessageBox.Show($"Extracted {ok} of {jobs.Count}. First error — {firstError}");
        StatusBus.Report($"Extracted {ok} item(s).");
    }

    private static string? ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose a destination folder", UseDescriptionForTitle = true };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
    }

    private static string Sanitize(string name)
    {
        var cleaned = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '_')).Trim();
        return cleaned.Length == 0 ? "item" : cleaned;
    }
}
