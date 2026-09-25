// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Files;

namespace DiscForge.App.Views;

/// <summary>
/// Browse and extract the files inside an image, without burning it or mounting
/// it — the IsoBuster job.
///
/// A disc image is a filesystem in a wrapper, and often the only thing wanted
/// from it is one file. Everything needed was already here: ISO 9660 and UDF
/// readers that the CLI has used for months. This is the window onto them.
///
/// Extraction is deliberately per-file as well as wholesale. Recovering one
/// document from a failing disc shouldn't mean writing out four hundred
/// megabytes to find it.
/// </summary>
internal sealed class BrowseView : UserControl
{
    private readonly TextBox _path = new()
    {
        ReadOnly = true, Width = 400, Font = Theme.Ui, Location = new Point(70, 13),
    };
    private readonly Button _extractSelected = new()
    {
        Text = "Extract selected", Location = new Point(12, 48), Width = 120, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _extractAll = new()
    {
        Text = "Extract all…", Location = new Point(140, 48), Width = 100, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Label _summary = new()
    {
        AutoSize = false, Location = new Point(252, 53), Size = new Size(472, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly ListView _files = new()
    {
        Location = new Point(12, 82), Size = new Size(712, 330),
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

    private string? _imagePath;
    private ImageBrowser.Listing? _listing;

    public BrowseView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });

        var open = new Button
        {
            Text = "Open…", Location = new Point(478, 12), Width = 80, FlatStyle = FlatStyle.System,
        };
        open.Click += async (_, _) => await OpenAsync();

        foreach (var (name, w) in new[] { ("Size", 110), ("Path", 580) })
            _files.Columns.Add(new ColumnHeader { Text = name, Width = w });

        _files.SelectedIndexChanged += (_, _) =>
            _extractSelected.Enabled = _files.SelectedItems.Count > 0;
        _files.DoubleClick += async (_, _) => await ExtractSelectedAsync();

        _extractSelected.Click += async (_, _) => await ExtractSelectedAsync();
        _extractAll.Click += async (_, _) => await ExtractAllAsync();

        Controls.Add(_path); Controls.Add(open);
        Controls.Add(_extractSelected); Controls.Add(_extractAll);
        Controls.Add(_summary); Controls.Add(_files);
        Controls.Add(_progress); Controls.Add(_status);

        _status.Text = "Open a .cdi or .iso image to see what's inside it.";
    }

    private async Task OpenAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Disc images (*.cdi;*.iso;*.cue;*.bin;*.img)|*.cdi;*.iso;*.cue;*.bin;*.img|" +
                     "CDI images (*.cdi)|*.cdi|ISO images (*.iso)|*.iso|" +
                     "Raw bin/cue (*.cue;*.bin;*.img)|*.cue;*.bin;*.img|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _imagePath = dlg.FileName;
        _path.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);

        _files.Items.Clear();
        _extractSelected.Enabled = false;
        _extractAll.Enabled = false;
        _summary.Text = "Reading…";
        _status.Text = "";

        try
        {
            var path = _imagePath;
            _listing = await Task.Run(() => ImageBrowser.List(path));

            if (_listing.Error is not null)
            {
                _summary.Text = _listing.Error;
                _summary.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
                return;
            }

            foreach (var f in _listing.Files)
            {
                var item = new ListViewItem(f.Size.ToString("N0")) { Tag = f };
                item.SubItems.Add(f.Path);
                _files.Items.Add(item);
            }

            _summary.Text = $"{_listing.Filesystem}: {_listing.Files.Count:N0} file(s), " +
                            $"{_listing.TotalBytes / (1024.0 * 1024.0):N1} MB" +
                            (_listing.VolumeId is not null ? $"  —  \"{_listing.VolumeId}\"" : "");
            _summary.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            _extractAll.Enabled = _listing.Files.Count > 0;
            _status.Text = "Select files and press Extract, or double-click one.";
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not read the image: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("browse open", ex);
        }
    }

    private async Task ExtractSelectedAsync()
    {
        if (_imagePath is null || _listing is null || _files.SelectedItems.Count == 0) return;

        var chosen = _files.SelectedItems.Cast<ListViewItem>()
            .Select(i => (ImageBrowser.FileEntry)i.Tag!).ToList();

        // One file gets a Save As dialog with its own name; several get a folder.
        if (chosen.Count == 1)
        {
            var only = chosen[0];
            using var save = new SaveFileDialog
            {
                FileName = Path.GetFileName(only.Path.Replace('/', Path.DirectorySeparatorChar)),
                Filter = "All files (*.*)|*.*",
                InitialDirectory = AppSettings.LastExtractDirectory ?? "",
            };
            if (save.ShowDialog() != DialogResult.OK) return;
            await RunExtraction(chosen, Path.GetDirectoryName(save.FileName)!, save.FileName);
            return;
        }

        using var folder = new FolderBrowserDialog
        {
            Description = "Extract the selected files to…",
            SelectedPath = AppSettings.LastExtractDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        await RunExtraction(chosen, folder.SelectedPath, null);
    }

    private async Task ExtractAllAsync()
    {
        if (_imagePath is null || _listing is null) return;

        using var folder = new FolderBrowserDialog
        {
            Description = "Extract everything to…",
            SelectedPath = AppSettings.LastExtractDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        await RunExtraction(_listing.Files, folder.SelectedPath, null);
    }

    private async Task RunExtraction(IReadOnlyList<ImageBrowser.FileEntry> files,
                                     string outDir, string? singleTarget)
    {
        AppSettings.LastExtractDirectory = outDir;

        _extractSelected.Enabled = false;
        _extractAll.Enabled = false;
        _progress.Value = 0;
        _status.Text = $"Extracting {files.Count:N0} file(s)…";

        try
        {
            var path = _imagePath!;
            var result = await Task.Run(() =>
                ImageBrowser.Extract(path, files, outDir, singleTarget,
                    new Progress<double>(f =>
                        BeginInvoke(() => _progress.Value = Math.Clamp((int)(f * 100), 0, 100)))));

            _progress.Value = 100;
            _status.Text = result.Failed == 0
                ? $"Extracted {result.Extracted:N0} file(s), {result.BytesWritten / (1024.0 * 1024.0):N1} MB " +
                  $"to {outDir}"
                : $"Extracted {result.Extracted:N0} file(s); {result.Failed:N0} failed — see the log.";
            _status.ForeColor = result.Failed == 0
                ? Color.FromArgb(0x20, 0x70, 0x20) : Color.FromArgb(0xA0, 0x60, 0x00);

            foreach (var problem in result.Problems) AppLog.Write("    " + problem);
            StatusBus.Report(_status.Text);
        }
        catch (Exception ex)
        {
            _status.Text = "Extraction failed: " + ex.Message;
            _status.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("browse extract", ex);
        }
        finally
        {
            _extractSelected.Enabled = _files.SelectedItems.Count > 0;
            _extractAll.Enabled = _listing?.Files.Count > 0;
        }
    }
}