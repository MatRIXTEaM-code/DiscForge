// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Cdi;
using DiscForge.Core.Gdi;
using DiscForge.Core.Iso;

namespace DiscForge.App.Views;

/// <summary>
/// Work with a Dreamcast GD-ROM image: open a .gdi, see its track layout and boot
/// header, browse and extract the game's files, and convert it to CDI. The
/// window onto the GDI toolchain the CLI already carries — a GD-ROM carries no
/// encryption, so this reads and copies files a backup already holds and decrypts
/// nothing.
/// </summary>
internal sealed class DreamcastView : UserControl
{
    private readonly TextBox _path = new()
    {
        ReadOnly = true, Width = 400, Font = Theme.Ui, Location = new Point(70, 13),
    };
    private readonly Label _summary = new()
    {
        AutoSize = false, Location = new Point(12, 46), Size = new Size(712, 34),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Button _extractSelected = new()
    {
        Text = "Extract selected", Location = new Point(12, 84), Width = 120, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _extractAll = new()
    {
        Text = "Extract all…", Location = new Point(140, 84), Width = 100, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _toCdi = new()
    {
        Text = "Convert to CDI…", Location = new Point(600, 84), Width = 124, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly ListView _files = new()
    {
        Location = new Point(12, 118), Size = new Size(712, 296),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        View = View.Details, FullRowSelect = true, MultiSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Theme.Ui, BackColor = Color.White,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(12, 420), Size = new Size(712, 18), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Location = new Point(12, 442), Size = new Size(712, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private string? _gdiPath;
    private string? _gdiDir;
    private GdiDisc? _disc;
    private IsoDirectory? _listing;

    public DreamcastView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "GDI:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });

        var open = new Button { Text = "Open…", Location = new Point(478, 12), Width = 80, FlatStyle = FlatStyle.System };
        open.Click += async (_, _) => await OpenAsync();

        foreach (var (name, w) in new[] { ("Size", 110), ("Path", 580) })
            _files.Columns.Add(new ColumnHeader { Text = name, Width = w });

        _files.SelectedIndexChanged += (_, _) => _extractSelected.Enabled = _files.SelectedItems.Count > 0;
        _files.DoubleClick += async (_, _) => await ExtractSelectedAsync();
        _extractSelected.Click += async (_, _) => await ExtractSelectedAsync();
        _extractAll.Click += async (_, _) => await ExtractAllAsync();
        _toCdi.Click += (_, _) => ConvertToCdi();

        Controls.Add(_path); Controls.Add(open);
        Controls.Add(_summary);
        Controls.Add(_extractSelected); Controls.Add(_extractAll); Controls.Add(_toCdi);
        Controls.Add(_files); Controls.Add(_progress); Controls.Add(_status);

        _status.Text = "Open a Dreamcast .gdi to see its tracks, boot header and game files.";
    }

    private async Task OpenAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Dreamcast GDI (*.gdi)|*.gdi|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _gdiPath = dlg.FileName;
        _path.Text = dlg.FileName;
        _gdiDir = Path.GetDirectoryName(Path.GetFullPath(dlg.FileName));
        AppSettings.LastImageDirectory = _gdiDir;

        _files.Items.Clear();
        _extractSelected.Enabled = false;
        _extractAll.Enabled = false;
        _toCdi.Enabled = false;
        _summary.Text = "Reading…";
        _status.Text = "";

        try
        {
            var path = _gdiPath;
            _disc = await Task.Run(() => GdiParser.ParseFile(path));
            _toCdi.Enabled = true;

            // Boot header summary (region / title), if the game track is present.
            string header = "";
            try
            {
                var ip = IpBin.ReadFromDisc(_disc, _gdiDir!);
                if (ip is not null)
                    header = $"{ip.Title}   ·   {ip.ProductNumber} {ip.Version}   ·   " +
                             (ip.Regions.Count > 0 ? string.Join("/", ip.Regions) : "no region");
            }
            catch (IpBinFormatException) { }

            // Browse the game filesystem.
            _listing = await Task.Run(() => GdiBrowser.Browse(_disc!, _gdiDir!));

            foreach (var f in _listing.Entries.Where(e => !e.IsDirectory))
            {
                var item = new ListViewItem(f.Size.ToString("N0")) { Tag = f };
                item.SubItems.Add(f.Path);
                _files.Items.Add(item);
            }

            int fileCount = _listing.Entries.Count(e => !e.IsDirectory);
            _summary.Text = $"{_disc.Tracks.Count} track(s)" +
                            (header.Length > 0 ? "   ·   " + header : "") +
                            $"\r\nGame filesystem \"{_listing.VolumeId}\": {fileCount:N0} file(s)";
            _summary.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            _extractAll.Enabled = fileCount > 0;
            _status.Text = "Select files and press Extract, or double-click one.";
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not read the GDI: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("dreamcast open", ex);
        }
    }

    private async Task ExtractSelectedAsync()
    {
        if (_files.SelectedItems.Count == 0) return;
        var chosen = _files.SelectedItems.Cast<ListViewItem>().Select(i => (IsoEntry)i.Tag!).ToList();

        using var folder = new FolderBrowserDialog
        {
            Description = "Extract the selected game files to…",
            SelectedPath = AppSettings.LastExtractDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        await RunExtraction(chosen, folder.SelectedPath);
    }

    private async Task ExtractAllAsync()
    {
        if (_listing is null) return;
        var files = _listing.Entries.Where(e => !e.IsDirectory).ToList();

        using var folder = new FolderBrowserDialog
        {
            Description = "Extract every game file to…",
            SelectedPath = AppSettings.LastExtractDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        await RunExtraction(files, folder.SelectedPath);
    }

    private async Task RunExtraction(IReadOnlyList<IsoEntry> files, string outDir)
    {
        if (_disc is null || _gdiDir is null) return;
        AppSettings.LastExtractDirectory = outDir;

        _extractSelected.Enabled = false;
        _extractAll.Enabled = false;
        _progress.Value = 0;
        _status.Text = $"Extracting {files.Count:N0} file(s)…";

        try
        {
            var disc = _disc;
            var dir = _gdiDir;
            await Task.Run(() =>
            {
                int done = 0;
                foreach (var e in files)
                {
                    string outPath = Path.Combine(outDir, e.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    using var os = File.Create(outPath);
                    GdiBrowser.ExtractFile(disc, dir, e, os);
                    done++;
                    int pct = (int)(100.0 * done / Math.Max(1, files.Count));
                    BeginInvoke(() => _progress.Value = Math.Clamp(pct, 0, 100));
                }
            });

            _progress.Value = 100;
            _status.Text = $"Extracted {files.Count:N0} file(s) to {outDir}";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report(_status.Text);
        }
        catch (Exception ex)
        {
            _status.Text = "Extraction failed: " + ex.Message;
            _status.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("dreamcast extract", ex);
        }
        finally
        {
            _extractSelected.Enabled = _files.SelectedItems.Count > 0;
            _extractAll.Enabled = _listing?.Entries.Any(e => !e.IsDirectory) ?? false;
        }
    }

    private void ConvertToCdi()
    {
        if (_gdiPath is null) return;
        using var save = new SaveFileDialog
        {
            Filter = "CDI image (*.cdi)|*.cdi",
            FileName = Path.GetFileNameWithoutExtension(_gdiPath) + ".cdi",
        };
        if (save.ShowDialog() != DialogResult.OK) return;

        try
        {
            using (var os = File.Create(save.FileName))
                GdiConverter.GdiToCdi(_gdiPath, CdiVersion.V35, os);
            _status.Text = $"Converted to {Path.GetFileName(save.FileName)}.";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report(_status.Text);
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("dreamcast convert", ex);
        }
    }
}
