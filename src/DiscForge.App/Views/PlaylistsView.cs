// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Frontend;
using DiscForge.Core.Library;

namespace DiscForge.App.Views;

/// <summary>
/// Turns a DiscForge-identified folder into the library files the popular front-ends
/// read — a RetroArch <c>.lpl</c> playlist or an EmulationStation / RetroBat
/// <c>gamelist.xml</c> — and builds the multi-disc <c>.m3u</c> that RetroArch,
/// DuckStation and PCSX2 use to swap discs and share one memory card. A thin shell
/// over <see cref="LibraryScanner"/> and <see cref="FrontendExport"/>. Cataloguing
/// only — it writes playlist text, never touches game data or protection.
/// </summary>
internal sealed class PlaylistsView : UserControl
{
    private static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
        { ".m3u", ".lpl", ".xml", ".txt", ".dat", ".nfo", ".sbi", ".sub", ".png", ".jpg", ".jpeg" };

    private readonly TextBox _folder = new() { ReadOnly = true, Location = new Point(70, 40), Width = 480, Font = Theme.Ui };
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(70, 72), Width = 300, Font = Theme.Ui };
    private readonly Button _export = new() { Text = "Scan && Export…", Location = new Point(452, 70), Width = 130, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Location = new Point(70, 104), Font = Theme.Ui, ForeColor = Color.Gray };

    private readonly ListBox _discs = new() { Location = new Point(70, 40), Size = new Size(480, 120), Font = Theme.Ui, SelectionMode = SelectionMode.MultiExtended };

    private string? _folderPath;

    public PlaylistsView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        _kind.Items.AddRange(new object[] { "RetroArch playlist (.lpl)", "EmulationStation / RetroBat (gamelist.xml)" });
        _kind.SelectedIndex = 0;

        var folderBox = new GroupBox { Text = "Front-end library from a folder", Location = new Point(12, 6), Size = new Size(710, 150), Font = Theme.UiBold };
        folderBox.Controls.Add(new Label { Text = "Folder:", AutoSize = true, Location = new Point(12, 43), Font = Theme.Ui });
        folderBox.Controls.Add(new Label { Text = "Format:", AutoSize = true, Location = new Point(12, 75), Font = Theme.Ui });
        var pick = new Button { Text = "…", Location = new Point(556, 39), Width = 30, FlatStyle = FlatStyle.System };
        pick.Click += (_, _) => ChooseFolder();
        _export.Click += async (_, _) => await ExportAsync();
        folderBox.Controls.AddRange(new Control[] { _folder, pick, _kind, _export, _status });

        var m3uBox = new GroupBox { Text = "Multi-disc M3U", Location = new Point(12, 164), Size = new Size(710, 230), Font = Theme.UiBold };
        m3uBox.Controls.Add(new Label { Text = "Discs (in order):", AutoSize = true, Location = new Point(12, 20), Font = Theme.Ui });
        var add = new Button { Text = "Add discs…", Location = new Point(560, 40), Width = 120, FlatStyle = FlatStyle.System };
        add.Click += (_, _) => AddDiscs();
        var up = new Button { Text = "Up", Location = new Point(560, 72), Width = 56, FlatStyle = FlatStyle.System };
        up.Click += (_, _) => MoveDisc(-1);
        var down = new Button { Text = "Down", Location = new Point(624, 72), Width = 56, FlatStyle = FlatStyle.System };
        down.Click += (_, _) => MoveDisc(1);
        var remove = new Button { Text = "Remove", Location = new Point(560, 104), Width = 120, FlatStyle = FlatStyle.System };
        remove.Click += (_, _) => RemoveSelected();
        var save = new Button { Text = "Save .m3u…", Location = new Point(560, 172), Width = 120, FlatStyle = FlatStyle.System };
        save.Click += (_, _) => SaveM3u();
        m3uBox.Controls.AddRange(new Control[] { _discs, add, up, down, remove, save });

        Controls.Add(folderBox);
        Controls.Add(m3uBox);
    }

    // ---- folder → front-end library ----

    private void ChooseFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Choose the games folder", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _folderPath = dlg.SelectedPath; _folder.Text = dlg.SelectedPath;
        _export.Enabled = true;
        _status.Text = "Ready to scan.";
        _status.ForeColor = Color.Gray;
    }

    private async Task ExportAsync()
    {
        if (_folderPath is null) return;
        bool retroarch = _kind.SelectedIndex == 0;

        string filter = retroarch ? "RetroArch playlist (*.lpl)|*.lpl" : "gamelist (*.xml)|*.xml";
        string suggested = retroarch ? "playlist.lpl" : "gamelist.xml";
        using var save = new SaveFileDialog { Filter = filter, FileName = suggested, InitialDirectory = _folderPath };
        if (save.ShowDialog() != DialogResult.OK) return;

        _export.Enabled = false;
        _status.Text = "Scanning…"; _status.ForeColor = Color.Black;
        try
        {
            string folder = _folderPath, outPath = save.FileName;
            int count = await Task.Run(() =>
            {
                var report = LibraryScanner.Scan(folder);
                var items = new List<PlaylistItem>();
                foreach (var e in report.Entries)
                {
                    if (SkipExt.Contains(Path.GetExtension(e.FileName))) continue;
                    string label = e.Match?.Game ?? Path.GetFileNameWithoutExtension(e.FileName);
                    string system = e.RomPlatform.Length > 0 ? e.RomPlatform : e.Format;
                    string rel = Path.GetRelativePath(folder, e.Path).Replace('\\', '/');
                    items.Add(new PlaylistItem
                    {
                        Path = retroarch ? e.Path : rel,
                        Label = label,
                        Crc32Hex = e.Crc32Hex,
                        System = system,
                    });
                }
                string text = retroarch
                    ? FrontendExport.BuildRetroArchLpl(Path.GetFileNameWithoutExtension(outPath), items)
                    : FrontendExport.BuildEmulationStationGamelist(items);
                File.WriteAllText(outPath, text);
                return items.Count;
            });

            _status.Text = $"Wrote {Path.GetFileName(save.FileName)} — {count} game(s)."; _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report($"Exported {count} game(s) to {Path.GetFileName(save.FileName)}");
        }
        catch (Exception ex)
        {
            _status.Text = "Export failed."; _status.ForeColor = Color.FromArgb(0xA0, 0x30, 0x30);
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("frontend-export", ex);
        }
        finally { _export.Enabled = true; }
    }

    // ---- multi-disc M3U ----

    private void AddDiscs()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Disc images (*.cue;*.chd;*.bin;*.iso;*.cdi;*.nrg)|*.cue;*.chd;*.bin;*.iso;*.cdi;*.nrg|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) _discs.Items.Add(f);
        if (dlg.FileNames.Length > 0) AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileNames[0]);
    }

    private void MoveDisc(int delta)
    {
        int i = _discs.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _discs.Items.Count) return;
        var item = _discs.Items[i];
        _discs.Items.RemoveAt(i);
        _discs.Items.Insert(j, item);
        _discs.SelectedIndex = j;
    }

    private void RemoveSelected()
    {
        for (int i = _discs.SelectedIndices.Count - 1; i >= 0; i--)
            _discs.Items.RemoveAt(_discs.SelectedIndices[i]);
    }

    private void SaveM3u()
    {
        if (_discs.Items.Count == 0) { RetroMessageBox.Show("Add the discs first."); return; }
        using var dlg = new SaveFileDialog { Filter = "M3U playlist (*.m3u)|*.m3u", FileName = "game.m3u" };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        string outDir = Path.GetDirectoryName(Path.GetFullPath(dlg.FileName)) ?? "";
        var lines = _discs.Items.Cast<string>().Select(d =>
        {
            string full = Path.GetFullPath(d);
            string discDir = Path.GetDirectoryName(full) ?? "";
            return string.Equals(discDir, outDir, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(full) : d;
        });
        try
        {
            File.WriteAllText(dlg.FileName, FrontendExport.BuildM3u(lines));
            StatusBus.Report($"Wrote {Path.GetFileName(dlg.FileName)} ({_discs.Items.Count} disc(s))");
        }
        catch (Exception ex) { RetroMessageBox.Show(ex.Message); }
    }
}
