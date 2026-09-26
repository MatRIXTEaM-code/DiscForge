// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Files;
using DiscForge.Core.Forensics;

namespace DiscForge.App.Views;

/// <summary>
/// Compare two disc images: file by file (what was added, removed, changed or moved — for two
/// pressings, a patched and an original image, or two revisions), or byte by byte, finding where
/// they differ even when data has shifted. Uses <see cref="DiscDiff"/> and <see cref="DiscRegionDiff"/>.
/// </summary>
internal sealed class CompareImagesView : ToolViewBase
{
    private const long ByteCompareLimit = 1_500_000_000;
    private readonly TextBox _a, _b;
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Font = Theme.Ui, Location = new Point(12, 150), Size = new Size(712, 276),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    public CompareImagesView()
    {
        const string filter = "Disc images (*.iso;*.bin;*.img;*.cue;*.cdi;*.mdf;*.nrg;*.gdi)|*.iso;*.bin;*.img;*.cue;*.cdi;*.mdf;*.nrg;*.gdi|All files (*.*)|*.*";
        _a = AddFileRow("First:", 12, () => PickFile(filter));
        _b = AddFileRow("Second:", 42, () => PickFile(filter));
        AddButton("Compare files", 12, 76, 130, CompareFilesAsync);
        AddButton("Compare bytes", 150, 76, 130, CompareBytesAsync);
        Status.Location = new Point(12, 112);
        Controls.Add(Status);
        Bar.Location = new Point(12, 132);
        Controls.Add(Bar);
        _list.Columns.Add("Change", 100);
        _list.Columns.Add("Path / place", 420);
        _list.Columns.Add("Size", 170);
        Controls.Add(_list);
        SetStatus("Compare files lists what's different inside the images; Compare bytes shows where the raw data differs.");
    }

    private bool Ready() => RequireFile(_a, "the first image") && RequireFile(_b, "the second image");

    private async Task CompareFilesAsync()
    {
        if (!Ready()) return;
        string a = _a.Text.Trim(), b = _b.Text.Trim();
        _list.Items.Clear();
        SetStatus("Reading both images' files…");
        var r = await Task.Run(() => DiscDiff.Compare(a, b));
        if (r.Error is not null) { SetStatus(r.Error, Theme.Bad); return; }
        foreach (var f in r.Removed) Row("Only in first", f.Path, Human(f.Size), Theme.Warn);
        foreach (var f in r.Added) Row("Only in second", f.Path, Human(f.Size), Theme.Warn);
        foreach (var f in r.Changed) Row("Different", f.Path, f.SizeA == f.SizeB ? Human(f.SizeA) : $"{Human(f.SizeA)} → {Human(f.SizeB)}", Theme.Bad);
        foreach (var f in r.Moved) Row("Moved", $"{f.PathA} → {f.PathB}", Human(f.Size), Theme.Text);
        SetStatus(r.Identical ? $"The images hold the same files ({r.Unchanged:N0})." : r.Summary(), r.Identical ? Theme.Good : Theme.Warn);
    }

    private async Task CompareBytesAsync()
    {
        if (!Ready()) return;
        string a = _a.Text.Trim(), b = _b.Text.Trim();
        if (new FileInfo(a).Length > ByteCompareLimit || new FileInfo(b).Length > ByteCompareLimit)
        {
            SetStatus("Byte comparison works on images up to 1.5 GB (CD size). Use Compare files for DVDs and Blu-rays.", Theme.Warn);
            return;
        }
        _list.Items.Clear();
        SetStatus("Comparing the raw data…");
        var r = await Task.Run(() => DiscRegionDiff.CompareFiles(a, b));
        if (r.Identical) { SetStatus("The images are byte-for-byte identical.", Theme.Good); return; }
        foreach (var g in r.RegionsA.Take(500)) Row("Differs in first", $"offset 0x{g.Offset:X} (sector {g.Offset / 2048:N0})", Human(g.Length), Theme.Warn);
        foreach (var g in r.RegionsB.Take(500)) Row("Differs in second", $"offset 0x{g.Offset:X} (sector {g.Offset / 2048:N0})", Human(g.Length), Theme.Warn);
        SetStatus($"{r.SimilarityA * 100:0.00}% of the first image is also in the second. {r.Summary()}", Theme.Warn);
    }

    private void Row(string what, string path, string size, Color colour)
    {
        var item = new ListViewItem(new[] { what, path, size }) { ForeColor = colour };
        _list.Items.Add(item);
    }
}
