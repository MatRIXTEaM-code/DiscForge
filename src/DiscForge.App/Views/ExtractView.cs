// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Ace;
using DiscForge.Core.OldArchives;
using DiscForge.Core.Fat;
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
/// never decrypted. Old archives — ACE, LHA/LZH, ARJ and ZOO, including multi-volume, self-extracting
/// and password-protected ones — are read with OldArchive; "Extract all" keeps their folder structure.
/// A disc image lists the old archives stored on it, and "Check a folder…" tests every old archive in
/// a folder tree and can unpack the good ones.
/// </summary>
internal sealed class ExtractView : UserControl
{
    // Formats other than WBFS are read whole into memory; guard against someone
    // dropping a huge disc image here (WBFS is handled stream-only, before this).
    private const long MaxInMemoryBytes = 512L * 1024 * 1024;

    /// <param name="Folder">true when the item extracts into a folder (an archive) rather than to one file.</param>
    private sealed record Item(string Display, string Detail, string SuggestedName, Action<string> ExtractTo, bool Folder = false);

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

    private readonly Button _sweep = new() { Text = "Check a folder…", Location = new Point(298, 380), Width = 130, FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
    private readonly Button _report = new() { Text = "Save report…", Location = new Point(436, 380), Width = 110, FlatStyle = FlatStyle.System, Visible = false, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };

    private readonly List<Item> _items = new();

    // What is loaded, when it isn't a plain container: an old archive, a disc image holding old
    // archives, or the result of a folder check. "Extract all" handles each its own way.
    private string? _archivePath;
    private string? _imagePath;
    private string? _sweepFolder;
    private List<SweepItem>? _sweepItems;
    private string? _password;

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
        _sweep.Click += (_, _) => CheckFolder();
        _report.Click += (_, _) => SaveReport();
        _list.SelectedIndexChanged += (_, _) => _selected.Enabled = _list.SelectedIndices.Count > 0;

        Controls.AddRange(new Control[] { _path, pick, _kind, _list, _selected, _all, _sweep, _report });
        _kind.Text = "Drop a WBFS, floppy, memory card, EBOOT.PBP, old archive (ACE/LHA/ARJ/ZOO) or disc image.";
        _kind.ForeColor = Color.Gray;
    }

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void Choose()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Extractable files (*.wbfs;*.d64;*.adf;*.img;*.mcr;*.mc;*.raw;*.bin;*.vmu;*.pbp;*.ace;*.lzh;*.lha;*.arj;*.zoo;*.iso;*.cue;*.cdi)|" +
                     "*.wbfs;*.d64;*.adf;*.img;*.mcr;*.mc;*.raw;*.bin;*.vmu;*.pbp;*.ace;*.lzh;*.lha;*.arj;*.zoo;*.iso;*.cue;*.cdi|" +
                     "Old archives (*.ace;*.lzh;*.lha;*.lzs;*.arj;*.zoo;*.c00;*.a01;*.exe)|*.ace;*.lzh;*.lha;*.lzs;*.arj;*.zoo;*.c??;*.a??;*.exe|" +
                     "Disc images (*.iso;*.cue;*.bin;*.img;*.cdi)|*.iso;*.cue;*.bin;*.img;*.cdi|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() == DialogResult.OK) LoadFile(dlg.FileName);
    }

    /// <summary>Open a file as if it had been dropped here (used for "Open with DiscForge").</summary>
    public void OpenFile(string path) => LoadFile(path);

    private void LoadFile(string path)
    {
        AppSettings.LastImageDirectory = Path.GetDirectoryName(path);
        _path.Text = path;
        _items.Clear();
        _list.Items.Clear();
        _selected.Enabled = false;
        _all.Enabled = false;
        ResetModes();

        try
        {
            if (!Detect(path))
            {
                _kind.Text = "Nothing to extract — not a container, old archive or disc image DiscForge can open.";
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

        // Old archives (ACE, LHA/LZH, ARJ, ZOO): streamed, since they can be large and self-extractors
        // are .exe files.
        if (OldArchive.IsNumberedVolume(path) || OldArchive.Detect(path) is not null)
        {
            using var arc = OldArchive.Open(path);
            _archivePath = path;
            int files = arc.Entries.Count(e => !e.IsDirectory);
            _kind.Text = $"{arc.Description} — {files} file(s)" + (arc.Warnings.Count > 0 ? "  (see note)" : "");
            if (arc.Comment.Length > 0 || arc.Warnings.Count > 0)
                RetroMessageBox.Show(string.Join("\n\n", arc.Warnings.Concat(arc.Comment.Length > 0 ? new[] { "Archive comment:\n" + arc.Comment } : Array.Empty<string>())));
            foreach (var e in arc.Entries)
            {
                if (e.IsDirectory) continue;
                int index = e.Index;
                string leaf = e.Name.Contains('/') ? e.Name[(e.Name.LastIndexOf('/') + 1)..] : e.Name;
                string detail = $"{e.Size:N0} B, {e.Method}" + (e.IsEncrypted ? ", password" : "") + (e.VolumeCount > 1 ? $", {e.VolumeCount} volumes" : "");
                _items.Add(new Item(e.Name, detail, leaf, dest => ExtractArchiveEntry(path, index, dest)));
            }
            return true;
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

        // FAT16 / FAT32 (a boot image, a hybrid disc's FAT partition, card media). Checked before FAT12
        // because Fat12Reader.IsFat12 also accepts a FAT16 BPB — FAT12 falls through to the branch below.
        if (FatReader.IsFat(data))
        {
            var vol = FatReader.Read(data);
            if (vol.Type != FatType.Fat12)
            {
                _kind.Text = $"{vol.Type} — \"{vol.VolumeLabel}\"";
                foreach (var e in vol.Files)
                {
                    var entry = e;
                    string leaf = entry.Path.TrimStart('/').Replace('/', '_');
                    _items.Add(new Item(entry.Path, $"{entry.Size:N0} B", Sanitize(leaf),
                        dest => File.WriteAllBytes(dest, FatReader.ExtractFile(data, entry))));
                }
                return true;
            }
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

        return DetectDiscImage(path);
    }

    private static readonly string[] ImageExtensions = { ".iso", ".cue", ".bin", ".img", ".cdi", ".mdf", ".nrg" };

    /// <summary>A disc image with old archives on it: one item per archive (or volume set).</summary>
    private bool DetectDiscImage(string path)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return false;
        var (found, error) = DiscImageArchives.Find(path);
        if (error is not null || found.Count == 0) return false;
        _imagePath = path;
        _kind.Text = $"Disc image — {found.Count} old archive(s) on it (ACE/LHA/ARJ/ZOO). Each extracts into its own folder.";
        foreach (var f in found)
        {
            var archive = f;
            string leaf = archive.PathInImage.TrimStart('/');
            string folder = Path.GetFileNameWithoutExtension(leaf);
            string detail = $"{archive.Size:N0} B" + (archive.VolumePathsInImage.Count > 1 ? $", {archive.VolumePathsInImage.Count} volumes" : "");
            _items.Add(new Item(leaf, detail, Sanitize(folder), dest => ExtractArchiveFromImage(path, archive, dest), Folder: true));
        }
        return true;
    }

    private void ExtractSelected()
    {
        var picked = _list.SelectedIndices.Cast<int>().Select(i => _items[i]).ToList();
        if (picked.Count == 0) return;

        if (_sweepItems is not null) { ExtractSweep(picked); return; }
        if (picked.Count == 1 && !picked[0].Folder)
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

    private void ResetModes()
    {
        _archivePath = null;
        _imagePath = null;
        _sweepFolder = null;
        _sweepItems = null;
        _password = null;
        _report.Visible = false;
        _list.Columns[2].Text = "Saves as";
    }

    private void ExtractArchiveEntry(string path, int index, string dest)
    {
        using var arc = OldArchive.Open(path);
        var entry = arc.Entries[index];
        string? pw = entry.IsEncrypted ? AskPassword() : null;
        if (entry.IsEncrypted && pw is null) throw new OperationCanceledException("No password entered.");
        try { SafeExtract.WriteFile(dest, entry.Modified, s => arc.Extract(entry, s, pw)); }
        catch (OldArchivePasswordException) { _password = null; throw; }
    }

    private void ExtractArchiveFromImage(string imagePath, DiscImageArchives.Found archive, string destFolder)
    {
        string? temp = null;
        try
        {
            string local = DiscImageArchives.CopyOut(imagePath, archive, out temp);
            using var arc = OldArchive.Open(local);
            string? pw = arc.AnyEncrypted ? AskPassword() : null;
            var r = arc.ExtractAll(destFolder, pw);
            if (!r.AllOk)
                throw new IOException($"{r.Failed} of {r.Entries.Count} item(s) failed — first: {r.Entries.First(e => !e.Ok).Entry.Name}: {r.Entries.First(e => !e.Ok).Error}");
        }
        finally
        {
            if (temp is not null) try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string? AskPassword()
    {
        if (_password is not null) return _password;
        using var form = new Form
        {
            Text = "Password", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(340, 110), Font = Theme.Ui,
        };
        var label = new Label { Text = "This archive has password-protected files.\nPassword:", AutoSize = true, Location = new Point(12, 10) };
        var box = new TextBox { UseSystemPasswordChar = true, Location = new Point(12, 46), Width = 316 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(172, 76), Width = 75, FlatStyle = FlatStyle.System };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(253, 76), Width = 75, FlatStyle = FlatStyle.System };
        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        if (form.ShowDialog(this) != DialogResult.OK) return null;
        return _password = box.Text;
    }

    private void SetBusy(bool busy)
    {
        _all.Enabled = !busy && _items.Count > 0;
        _selected.Enabled = !busy && _list.SelectedIndices.Count > 0;
        _sweep.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void ShowFailures(string headline, IEnumerable<string> problems)
    {
        var list = problems.ToList();
        if (list.Count == 0) return;
        RetroMessageBox.Show(headline + "\n\n" + string.Join("\n", list.Take(12)) + (list.Count > 12 ? $"\n… and {list.Count - 12} more." : ""));
    }

    private async void ExtractArchiveAll(string path)
    {
        string? dir = ChooseFolder();
        if (dir is null) return;
        string? pw = null;
        using (var probe = OldArchive.Open(path))
            if (probe.AnyEncrypted && (pw = AskPassword()) is null) return;
        SetBusy(true);
        StatusBus.Report($"Extracting {Path.GetFileName(path)}…");
        try
        {
            var result = await Task.Run(() =>
            {
                using var arc = OldArchive.Open(path);
                return arc.ExtractAll(dir, pw);
            });
            var failed = result.Entries.Where(e => !e.Ok).ToList();
            if (failed.Any(f => f.Error?.Contains("password", StringComparison.OrdinalIgnoreCase) == true)) _password = null;
            StatusBus.Report($"Extracted {result.Ok} item(s) to {dir}.");
            ShowFailures($"Extracted {result.Ok} of {result.Entries.Count}.", failed.Select(f => $"{f.Entry.Name}: {f.Error}"));
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("extract", ex);
        }
        finally { SetBusy(false); }
    }

    private async void ExtractImageAll(string imagePath)
    {
        string? dir = ChooseFolder();
        if (dir is null) return;
        SetBusy(true);
        StatusBus.Report($"Extracting the archives on {Path.GetFileName(imagePath)}…");
        try
        {
            string? pw = _password;
            var items = await Task.Run(() => DiscImageArchives.Process(imagePath, dir, pw, overwrite: false));
            StatusBus.Report(ArchiveSweep.Summary(items));
            ShowFailures(ArchiveSweep.Summary(items), items.Where(i => !i.Ok).Select(i => $"{i.Path}: {i.Status} — {i.Detail}"));
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("extract", ex);
        }
        finally { SetBusy(false); }
    }

    // ---- folder check -------------------------------------------------------------------------

    private async void CheckFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose a folder to check for old archives (ACE, LHA/LZH, ARJ, ZOO)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        string folder = dlg.SelectedPath;
        _path.Text = folder;
        _items.Clear();
        _list.Items.Clear();
        ResetModes();
        _kind.ForeColor = Theme.Text;
        _kind.Text = "Checking…";
        SetBusy(true);
        try
        {
            var progress = new Progress<(int Done, int Total, string Name)>(p =>
            {
                if (p.Total > 0) _kind.Text = $"Checking {Math.Min(p.Done + 1, p.Total)} of {p.Total}: {Path.GetFileName(p.Name)}";
            });
            var items = await Task.Run(() => ArchiveSweep.Run(folder, new SweepOptions(), progress));
            _sweepFolder = folder;
            _sweepItems = items;
            _list.Columns[2].Text = "Status";
            foreach (var i in items)
            {
                var item = i;
                string rel = Path.GetRelativePath(folder, item.Path);
                string leaf = Sanitize(Path.GetFileNameWithoutExtension(item.Path));
                _items.Add(new Item(rel, $"{item.Format ?? "?"}, {item.Files} file(s) — {item.Detail}", leaf, _ => { }, Folder: true));
                var lvi = new ListViewItem(new[] { rel, $"{item.Format ?? "?"}, {item.Files} file(s) — {item.Detail}", item.Status });
                if (!item.Ok) lvi.ForeColor = Theme.Bad;
                _list.Items.Add(lvi);
            }
            _kind.Text = items.Count == 0 ? "No ACE, LHA/LZH, ARJ or ZOO archives in that folder." : ArchiveSweep.Summary(items);
            _kind.ForeColor = items.All(i => i.Ok) ? Theme.Good : Theme.Warn;
            _report.Visible = items.Count > 0;
            StatusBus.Report(_kind.Text);
        }
        catch (Exception ex)
        {
            _kind.Text = "The folder check stopped: " + ex.Message;
            _kind.ForeColor = Theme.Bad;
            AppLog.WriteException("extract", ex);
        }
        finally { SetBusy(false); }
    }

    private async void ExtractSweep(IReadOnlyList<Item> picked)
    {
        if (_sweepItems is null || _sweepFolder is null) return;
        string? dir = ChooseFolder();
        if (dir is null) return;
        var chosen = picked.Select(p => _sweepItems[_items.IndexOf(p)]).ToList();
        string folder = _sweepFolder;
        SetBusy(true);
        try
        {
            string? pw = chosen.Any(c => c.Status == SweepItem.StatusPassword) ? AskPassword() : _password;
            var opt = new SweepOptions { Password = pw };
            var results = await Task.Run(() => chosen.Select(c =>
            {
                string rel = Path.GetRelativePath(folder, Path.GetDirectoryName(c.Path)!);
                string dest = Path.Combine(dir, rel == "." ? "" : rel, Path.GetFileNameWithoutExtension(c.Path));
                return ArchiveSweep.Check(c.Path, opt, dest);
            }).ToList());
            StatusBus.Report($"Extracted {results.Count(r => r.Ok)} of {results.Count} archive(s) to {dir}.");
            ShowFailures($"Extracted {results.Count(r => r.Ok)} of {results.Count} archive(s).",
                results.Where(r => !r.Ok).Select(r => $"{Path.GetFileName(r.Path)}: {r.Status} — {r.Detail}"));
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("extract", ex);
        }
        finally { SetBusy(false); }
    }

    private void SaveReport()
    {
        if (_sweepItems is null) return;
        using var dlg = new SaveFileDialog { FileName = "archive-check.csv", Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, ArchiveSweep.ToCsv(_sweepItems, _sweepFolder));
        StatusBus.Report($"Report saved to {dlg.FileName}");
    }

    private void ExtractAll()
    {
        if (_sweepItems is not null) { ExtractSweep(_items.ToList()); return; }
        if (_archivePath is not null) { ExtractArchiveAll(_archivePath); return; }
        if (_imagePath is not null) { ExtractImageAll(_imagePath); return; }
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
