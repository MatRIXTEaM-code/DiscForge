// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Xbox;

namespace DiscForge.App.Views;

/// <summary>
/// Work with an Original Xbox game image (XDVDFS / XISO): open one, browse and
/// extract its files, or build a trimmed XISO from a folder. Filesystem work
/// only — XDVDFS carries no encryption, and the Xbox disc's security, which lives
/// outside the filesystem, is neither read nor touched.
/// </summary>
internal sealed class XboxView : UserControl
{
    private readonly TextBox _path = new()
    {
        ReadOnly = true, Width = 400, Font = Theme.Ui, Location = new Point(70, 13),
    };
    private readonly Label _summary = new()
    {
        AutoSize = false, Location = new Point(12, 48), Size = new Size(712, 16),
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
    private readonly Button _create = new()
    {
        Text = "Create XISO from folder…", Location = new Point(540, 84), Width = 184, Height = 26,
        FlatStyle = FlatStyle.System,
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

    private string? _imagePath;
    private XdvdfsVolume? _volume;

    public XboxView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });

        var open = new Button { Text = "Open…", Location = new Point(478, 12), Width = 80, FlatStyle = FlatStyle.System };
        open.Click += async (_, _) => await OpenAsync();

        foreach (var (name, w) in new[] { ("Size", 110), ("Path", 580) })
            _files.Columns.Add(new ColumnHeader { Text = name, Width = w });

        _files.SelectedIndexChanged += (_, _) => _extractSelected.Enabled = _files.SelectedItems.Count > 0;
        _files.DoubleClick += async (_, _) => await ExtractSelectedAsync();
        _extractSelected.Click += async (_, _) => await ExtractSelectedAsync();
        _extractAll.Click += async (_, _) => await ExtractAllAsync();
        _create.Click += (_, _) => CreateFromFolder();

        Controls.Add(_path); Controls.Add(open); Controls.Add(_summary);
        Controls.Add(_extractSelected); Controls.Add(_extractAll); Controls.Add(_create);
        Controls.Add(_files); Controls.Add(_progress); Controls.Add(_status);

        _status.Text = "Open an Xbox game image (XDVDFS / XISO), or build one from a folder.";
    }

    private async Task OpenAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Xbox images (*.iso;*.xiso)|*.iso;*.xiso|All files (*.*)|*.*",
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
            _volume = await Task.Run(() =>
            {
                using var img = File.OpenRead(path);
                return XdvdfsReader.Read(img);
            });

            foreach (var f in _volume.Files)
            {
                var item = new ListViewItem(f.Size.ToString("N0")) { Tag = f };
                item.SubItems.Add(f.Path);
                _files.Items.Add(item);
            }

            _summary.Text = $"XDVDFS (Xbox), partition base sector {_volume.BaseSector:N0}: " +
                            $"{_volume.Files.Count():N0} file(s), {_volume.TotalBytes / (1024.0 * 1024.0):N1} MB";
            _summary.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            _extractAll.Enabled = _volume.Files.Any();
            _status.Text = "Select files and press Extract, or double-click one.";
        }
        catch (Exception ex)
        {
            _summary.Text = "Could not read the image: " + ex.Message;
            _summary.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            AppLog.WriteException("xbox open", ex);
        }
    }

    private async Task ExtractSelectedAsync()
    {
        if (_files.SelectedItems.Count == 0) return;
        var chosen = _files.SelectedItems.Cast<ListViewItem>().Select(i => (XdvdfsEntry)i.Tag!).ToList();

        using var folder = new FolderBrowserDialog
        {
            Description = "Extract the selected files to…",
            SelectedPath = AppSettings.LastExtractDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        await RunExtraction(chosen, folder.SelectedPath);
    }

    private async Task ExtractAllAsync()
    {
        if (_volume is null) return;
        using var folder = new FolderBrowserDialog
        {
            Description = "Extract every file to…",
            SelectedPath = AppSettings.LastExtractDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        await RunExtraction(_volume.Files.ToList(), folder.SelectedPath);
    }

    private async Task RunExtraction(IReadOnlyList<XdvdfsEntry> files, string outDir)
    {
        if (_imagePath is null || _volume is null) return;
        AppSettings.LastExtractDirectory = outDir;

        _extractSelected.Enabled = false;
        _extractAll.Enabled = false;
        _progress.Value = 0;
        _status.Text = $"Extracting {files.Count:N0} file(s)…";

        try
        {
            var path = _imagePath;
            var vol = _volume;
            await Task.Run(() =>
            {
                int done = 0;
                foreach (var e in files)
                {
                    string outPath = Path.Combine(outDir, e.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    using var os = File.Create(outPath);
                    using var src = File.OpenRead(path);
                    XdvdfsReader.ExtractFile(src, vol, e, os);
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
            AppLog.WriteException("xbox extract", ex);
        }
        finally
        {
            _extractSelected.Enabled = _files.SelectedItems.Count > 0;
            _extractAll.Enabled = _volume?.Files.Any() ?? false;
        }
    }

    private void CreateFromFolder()
    {
        using var folder = new FolderBrowserDialog { Description = "Folder to build the XISO from…" };
        if (folder.ShowDialog() != DialogResult.OK) return;

        using var save = new SaveFileDialog { Filter = "Xbox ISO (*.iso)|*.iso", FileName = "game.iso" };
        if (save.ShowDialog() != DialogResult.OK) return;

        try
        {
            // Stream to disk so a full-size XISO doesn't have to fit in memory.
            var children = WalkFolder(folder.SelectedPath);
            IReadOnlyList<string> warnings;
            using (var output = File.Create(save.FileName))
                warnings = XdvdfsBuilder.BuildToStream(output, children);
            foreach (var w in warnings) AppLog.Write("    " + w);
            _status.Text = $"Built {Path.GetFileName(save.FileName)} " +
                           $"({new FileInfo(save.FileName).Length / (1024.0 * 1024.0):N1} MB).";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            StatusBus.Report(_status.Text);
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("xbox create", ex);
        }
    }

    private static IReadOnlyList<XdvdfsBuilder.Node> WalkFolder(string folder)
    {
        var nodes = new List<XdvdfsBuilder.Node>();
        foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.Ordinal))
            nodes.Add(XdvdfsBuilder.Node.Dir(Path.GetFileName(dir), WalkFolder(dir)));
        foreach (var file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
            nodes.Add(XdvdfsBuilder.Node.FileFromPath(Path.GetFileName(file), file));
        return nodes;
    }
}
