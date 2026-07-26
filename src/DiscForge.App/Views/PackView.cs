// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Packing;

namespace DiscForge.App.Views;

/// <summary>
/// Works out which files go on which disc so as to waste the least space —
/// the job "Burn to the Brim" existed to do, and which nothing modern bothers
/// with because cloud storage made it feel unnecessary.
///
/// It isn't. Archiving a photo collection or a music library to optical media
/// still means deciding what goes where, and doing it by hand either wastes a
/// third of every disc or leaves you shuffling folders between piles. The
/// arithmetic is a bin-packing problem and a computer is much better at it.
///
/// The grouping option is the one that matters in practice. Files that belong
/// together — an album, a project, a year's photographs — are usually worth
/// keeping on one disc even at the cost of space, because a set split across
/// two discs is a nuisance for as long as the archive exists.
/// </summary>
internal sealed class PackView : UserControl
{
    private readonly ListView _files = new()
    {
        Location = new Point(12, 76), Size = new Size(712, 148),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        View = View.Details, FullRowSelect = true, MultiSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Theme.Ui, BackColor = Color.White,
    };
    private readonly ComboBox _media = new()
    {
        Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 12),
    };
    private readonly CheckBox _groups = new()
    {
        Text = "Keep folders together on one disc", AutoSize = true,
        Location = new Point(266, 14), Font = Theme.Ui, Checked = true,
    };
    private readonly CheckBox _overhead = new()
    {
        Text = "Allow for filesystem overhead", AutoSize = true,
        Location = new Point(266, 36), Font = Theme.Ui, Checked = true,
    };
    private readonly Button _addFiles = new()
    {
        Text = "Add files…", Location = new Point(12, 44), Width = 86, Height = 24,
        FlatStyle = FlatStyle.System,
    };
    private readonly Button _addFolder = new()
    {
        Text = "Add folder…", Location = new Point(104, 44), Width = 92, Height = 24,
        FlatStyle = FlatStyle.System,
    };
    private readonly Button _remove = new()
    {
        Text = "Remove", Location = new Point(202, 44), Width = 74, Height = 24,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _pack = new()
    {
        Text = "Pack", Location = new Point(552, 12), Width = 80, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _saveLog = new()
    {
        Text = "Save plan…", Location = new Point(638, 12), Width = 86, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Label _total = new()
    {
        AutoSize = false, Location = new Point(552, 44), Size = new Size(172, 16),
        Font = Theme.Ui, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleRight,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly Label _verdict = new()
    {
        AutoSize = false, Location = new Point(12, 232), Size = new Size(712, 22),
        Font = new Font(Theme.Ui.FontFamily, 10f, FontStyle.Bold),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _out = new()
    {
        Location = new Point(12, 260), Size = new Size(712, 188),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private readonly List<PackItem> _items = new();
    private OperationLog? _log;

    public PackView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Media:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });

        foreach (var (name, bytes) in DiscCapacity.Common)
            _media.Items.Add(new MediaChoice(name, bytes));
        _media.SelectedIndex = 1;      // CD-R 700 MB

        foreach (var (name, w) in new[] { ("Size", 100), ("Folder", 260), ("Name", 330) })
            _files.Columns.Add(new ColumnHeader { Text = name, Width = w });

        _addFiles.Click += (_, _) => AddFiles();
        _addFolder.Click += (_, _) => AddFolder();
        _remove.Click += (_, _) => RemoveSelected();
        _pack.Click += (_, _) => Pack();
        _saveLog.Click += (_, _) => SaveLog();
        _files.SelectedIndexChanged += (_, _) => _remove.Enabled = _files.SelectedItems.Count > 0;

        Controls.Add(_media); Controls.Add(_groups); Controls.Add(_overhead);
        Controls.Add(_addFiles); Controls.Add(_addFolder); Controls.Add(_remove);
        Controls.Add(_pack); Controls.Add(_saveLog); Controls.Add(_total);
        Controls.Add(_files); Controls.Add(_verdict); Controls.Add(_out);

        _out.Text =
            "Add the files you want to archive, choose your media, and press Pack." + Environment.NewLine +
            Environment.NewLine +
            "Deciding what goes on which disc by hand either wastes a third of every" + Environment.NewLine +
            "disc or has you shuffling folders between piles. It is a bin-packing" + Environment.NewLine +
            "problem, and a computer is markedly better at it." + Environment.NewLine +
            Environment.NewLine +
            "\"Keep folders together\" is usually worth leaving on: an album or a" + Environment.NewLine +
            "project split across two discs is a nuisance for as long as the archive" + Environment.NewLine +
            "exists, and a disc left 80% full is not." + Environment.NewLine +
            Environment.NewLine +
            "Capacities used here are the real ones — a \"700 MB\" CD holds 681 MiB of" + Environment.NewLine +
            "data. Packing to the number on the box produces a plan that doesn't fit.";
    }

    private sealed record MediaChoice(string Name, long Bytes)
    {
        public override string ToString() => Name;
    }

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastPackDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        AppSettings.LastPackDirectory = Path.GetDirectoryName(dlg.FileNames.FirstOrDefault());

        foreach (var path in dlg.FileNames) AddOne(path, group: null);
        Refresh_();
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Add a folder — its files are kept together unless you untick grouping.",
            SelectedPath = AppSettings.LastPackDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        AppSettings.LastPackDirectory = dlg.SelectedPath;

        try
        {
            // Each immediate subfolder becomes a group; loose files at the top
            // level are grouped by the folder itself. That matches how people
            // actually organise things — one folder per album, per project, per
            // year — without needing to explain a grouping syntax.
            var root = new DirectoryInfo(dlg.SelectedPath);

            foreach (var file in root.GetFiles())
                AddOne(file.FullName, root.Name);

            foreach (var sub in root.GetDirectories())
                foreach (var file in sub.GetFiles("*", SearchOption.AllDirectories))
                    AddOne(file.FullName, sub.Name);

            Refresh_();
        }
        catch (Exception ex)
        {
            _verdict.Text = "Could not read that folder.";
            _verdict.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            _out.Text = ex.Message;
        }
    }

    private void AddOne(string path, string? group)
    {
        if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;

        try
        {
            var info = new FileInfo(path);
            _items.Add(new PackItem
            {
                Path = path,
                Name = info.Name,
                Bytes = info.Length,
                Group = group,
            });
        }
        catch
        {
            // A file that vanished between listing and reading, or one we can't
            // see. Skipping it silently is right — the alternative is a dialog
            // per file on a folder that has one locked item in it.
        }
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem item in _files.SelectedItems)
            if (item.Tag is PackItem p) _items.Remove(p);
        Refresh_();
    }

    private void Refresh_()
    {
        _files.BeginUpdate();
        _files.Items.Clear();
        foreach (var i in _items.OrderBy(x => x.Group ?? "").ThenBy(x => x.Name))
        {
            var row = new ListViewItem(DiscPacker.Format(i.Bytes)) { Tag = i };
            row.SubItems.Add(i.Group ?? "—");
            row.SubItems.Add(i.Name);
            _files.Items.Add(row);
        }
        _files.EndUpdate();

        long total = _items.Sum(i => i.Bytes);
        _total.Text = $"{_items.Count:N0} file(s), {DiscPacker.Format(total)}";
        _pack.Enabled = _items.Count > 0;
        _saveLog.Enabled = false;
    }

    private void SaveLog()
    {
        if (_log is null) return;
        var path = _log.SaveWithDialog();
        if (path is not null) StatusBus.Report($"Plan saved to {Path.GetFileName(path)}");
    }

    private void Pack()
    {
        if (_items.Count == 0) return;
        var media = (MediaChoice)_media.SelectedItem!;

        try
        {
            var result = DiscPacker.Pack(_items, new DiscPacker.Options
            {
                CapacityBytes = media.Bytes,
                RespectGroups = _groups.Checked,
                AccountForOverhead = _overhead.Checked,
            });

            _verdict.Text = result.Discs.Count == 0
                ? "Nothing could be packed."
                : $"{result.Discs.Count} × {media.Name} — " +
                  $"{result.AverageFill:P0} full on average, " +
                  $"{DiscPacker.Format(result.WastedBytes)} left over";
            _verdict.ForeColor = result.Oversized.Count > 0
                ? Color.FromArgb(0xA0, 0x60, 0x00)
                : Color.FromArgb(0x20, 0x70, 0x20);

            string text = Render(result, media);
            _out.Text = text;

            var log = new OperationLog("Disc packing plan");
            log.Settings(
                ("Media", media.Name),
                ("Capacity", DiscPacker.Format(media.Bytes)),
                ("Files", _items.Count),
                ("Total size", DiscPacker.Format(_items.Sum(i => i.Bytes))),
                ("Keep folders together", _groups.Checked ? "yes" : "no"),
                ("Filesystem overhead", _overhead.Checked ? "allowed for" : "ignored"),
                ("Discs needed", result.Discs.Count));
            log.Result(text);
            _log = log;
            _saveLog.Enabled = true;

            AppLog.Write($"  packing: {_items.Count} file(s) across {result.Discs.Count} " +
                         $"{media.Name}, {result.AverageFill:P0} average fill");
            StatusBus.Report(_verdict.Text);
        }
        catch (Exception ex)
        {
            _verdict.Text = "Could not work out a plan.";
            _verdict.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            _out.Text = ex.Message;
            AppLog.WriteException("disc packing", ex);
        }
    }

    private static string Render(PackResult r, MediaChoice media)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"{r.Discs.Count} × {media.Name}   " +
                      $"({DiscPacker.Format(media.Bytes)} each)");
        sb.AppendLine();

        foreach (var disc in r.Discs)
        {
            // A twenty-cell bar makes the fill legible at a glance, which
            // matters when comparing a plan across a dozen discs.
            int filled = (int)Math.Round(disc.FillFraction * 20);
            string bar = new string('#', Math.Clamp(filled, 0, 20))
                       + new string('.', Math.Clamp(20 - filled, 0, 20));

            sb.AppendLine($"DISC {disc.Number}   [{bar}]  {disc.FillFraction:P0}   " +
                          $"{DiscPacker.Format(disc.UsedBytes)} used, " +
                          $"{DiscPacker.Format(disc.FreeBytes)} free");

            // Grouped files are listed by group rather than one line per file:
            // "Album A (14 files, 62 MB)" says more than fourteen filenames do.
            var grouped = disc.Items.Where(i => i.Group is not null)
                                    .GroupBy(i => i.Group!, StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var g in grouped)
                sb.AppendLine($"    {g.Key}  —  {g.Count():N0} file(s), " +
                              $"{DiscPacker.Format(g.Sum(i => i.Bytes))}");

            var loose = disc.Items.Where(i => i.Group is null)
                                  .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                                  .ToList();
            foreach (var f in loose.Take(20))
                sb.AppendLine($"    {f.Name}  —  {DiscPacker.Format(f.Bytes)}");
            if (loose.Count > 20)
                sb.AppendLine($"    … and {loose.Count - 20:N0} more file(s)");

            sb.AppendLine();
        }

        if (r.Oversized.Count > 0)
        {
            sb.AppendLine("TOO LARGE FOR ONE DISC");
            foreach (var f in r.Oversized.Take(20))
                sb.AppendLine($"  {f.Name}  —  {DiscPacker.Format(f.Bytes)}");
            if (r.Oversized.Count > 20)
                sb.AppendLine($"  … and {r.Oversized.Count - 20:N0} more");
            sb.AppendLine();
            sb.AppendLine("These are left out of the plan. Larger media would take them, or a");
            sb.AppendLine("split archive — but splitting a file across discs makes it useless");
            sb.AppendLine("without every part, which is a poor property for an archive.");
            sb.AppendLine();
        }

        if (r.Notes.Count > 0)
        {
            sb.AppendLine("NOTES");
            foreach (var n in r.Notes)
            {
                sb.AppendLine();
                foreach (var line in Wrap(n, 72)) sb.AppendLine("  " + line);
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Total: {DiscPacker.Format(r.TotalBytes)} across {r.Discs.Count} disc(s), " +
                      $"{DiscPacker.Format(r.WastedBytes)} unused.");
        sb.AppendLine();
        sb.AppendLine("Packed largest-first, which is what makes the difference: small files");
        sb.AppendLine("fill the gaps left by large ones, whereas the reverse leaves large files");
        sb.AppendLine("with nowhere to go and starts a fresh disc for each.");

        return sb.ToString();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var words = text.Split(' ');
        var line = new StringBuilder();
        foreach (var w in words)
        {
            if (line.Length > 0 && line.Length + 1 + w.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(w);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}