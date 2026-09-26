// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Recovery;

namespace DiscForge.App.Views;

/// <summary>
/// Build the best possible image from several raw dumps of the same CD, sector by sector: where the
/// copies agree the sector is used; where they don't, a copy whose EDC checks out wins, or one repaired
/// from its own parity, or a byte-by-byte majority vote that then passes its EDC. Every sector's source
/// is reported. Uses <see cref="DumpReconstruct"/>. (Merge Rips fills holes; this settles disagreements.)
/// </summary>
internal sealed class BestOfDumpsView : ToolViewBase
{
    private const long MaxTotalBytes = 6_000_000_000;
    private readonly ListBox _sources = new() { Location = new Point(92, 12), Size = new Size(516, 96), Font = Theme.Ui, HorizontalScrollbar = true };
    private readonly TextBox _out = new() { Location = new Point(92, 118), Width = 516, Font = Theme.Ui };

    public BestOfDumpsView()
    {
        Controls.Add(new Label { Text = "Dumps:", AutoSize = true, Location = new Point(12, 14), Font = Theme.Ui });
        Controls.Add(_sources);
        var add = new Button { Text = "Add…", Location = new Point(616, 12), Width = 108, FlatStyle = FlatStyle.System };
        add.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Filter = "Raw CD images (*.bin;*.img;*.raw)|*.bin;*.img;*.raw|All files (*.*)|*.*", InitialDirectory = AppSettings.LastImageDirectory ?? "" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            foreach (var f in dlg.FileNames) if (!_sources.Items.Contains(f)) _sources.Items.Add(f);
            if (_out.Text.Length == 0 && _sources.Items.Count > 0)
            {
                string first = (string)_sources.Items[0];
                _out.Text = Path.Combine(Path.GetDirectoryName(first)!, Path.GetFileNameWithoutExtension(first) + ".best" + Path.GetExtension(first));
            }
        };
        var remove = new Button { Text = "Remove", Location = new Point(616, 44), Width = 108, FlatStyle = FlatStyle.System };
        remove.Click += (_, _) => { if (_sources.SelectedIndex >= 0) _sources.Items.RemoveAt(_sources.SelectedIndex); };
        Controls.AddRange(new Control[] { add, remove });
        Controls.Add(new Label { Text = "Save as:", AutoSize = true, Location = new Point(12, 121), Font = Theme.Ui });
        Controls.Add(_out);
        AddButton("Build best image", 12, 150, 150, BuildAsync);
        AddOutputArea(188);
        Output.Text =
            "Add two or more raw dumps (2352-byte sectors, same length) of the same CD — ideally from different\r\n" +
            "drives or reading sessions. Each sector is taken from wherever it can be proved correct:\r\n\r\n" +
            "  • all copies agree\r\n" +
            "  • one copy passes the sector's own checksum (EDC)\r\n" +
            "  • a copy repaired from the sector's own parity (ECC) until its checksum passes\r\n" +
            "  • a byte-by-byte vote across the copies that then passes the checksum\r\n\r\n" +
            "Audio sectors have no checksum, so for those the vote is a best effort and is reported as such.";
    }

    private async Task BuildAsync()
    {
        var paths = _sources.Items.Cast<string>().ToList();
        if (paths.Count < 2) { RetroMessageBox.Show("Add at least two dumps of the same disc."); return; }
        string outPath = _out.Text.Trim();
        if (outPath.Length == 0) { RetroMessageBox.Show("Choose where to save the result."); return; }
        if (paths.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase)))
        { SetStatus("Save the result under a new name — the dumps are never overwritten.", Theme.Warn); return; }
        var lengths = paths.Select(p => new FileInfo(p).Length).ToList();
        if (lengths.Distinct().Count() > 1) { SetStatus("The dumps must all be the same size. " + string.Join(", ", paths.Select((p, i) => $"{Path.GetFileName(p)} {lengths[i]:N0}")), Theme.Warn); return; }
        if (lengths[0] % 2352 != 0) { SetStatus("These aren't raw images (2352-byte sectors). ISO images don't carry the per-sector checks this needs.", Theme.Warn); return; }
        if (lengths.Sum() > MaxTotalBytes) { SetStatus("That's more data than this tool holds in memory at once (6 GB). Use fewer dumps.", Theme.Warn); return; }
        SetStatus("Reading the dumps and settling every sector…");
        Bar.Style = ProgressBarStyle.Marquee;
        Bar.Visible = true;
        try
        {
            var result = await Task.Run(() =>
            {
                var images = paths.Select(File.ReadAllBytes).ToList();
                var r = DumpReconstruct.Reconstruct(images);
                File.WriteAllBytes(outPath, r.Image);
                return r.Report;
            });
            Output.Text =
                $"Sectors                          {result.SectorCount:N0}\r\n" +
                $"All copies agreed                {result.Agreed:N0}\r\n" +
                $"Taken from a copy that checks out{result.EdcVerifiedCopy,10:N0}\r\n" +
                $"Repaired from a copy's parity    {result.EccRepairedCopy:N0}\r\n" +
                $"Voted, then checked              {result.VoteVerified + result.VoteEccRepaired:N0}\r\n" +
                $"Voted, no checksum (audio)       {result.VoteBestEffort:N0}\r\n" +
                $"Not recovered                    {result.Unrecovered:N0}" +
                (result.UnrecoveredSectors.Count > 0 ? "\r\n\r\nFirst unrecovered sectors: " + string.Join(", ", result.UnrecoveredSectors.Take(15).Select(x => x.ToString("N0"))) : "");
            SetStatus(result.FullyRecovered ? $"Saved {Path.GetFileName(outPath)} — every data sector checks out." : $"Saved {Path.GetFileName(outPath)} — {result.Unrecovered:N0} sector(s) couldn't be proved correct.",
                      result.FullyRecovered ? Theme.Good : Theme.Warn);
        }
        finally { Bar.Style = ProgressBarStyle.Continuous; }
    }
}
