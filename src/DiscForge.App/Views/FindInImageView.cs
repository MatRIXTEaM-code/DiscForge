// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Iso;
using DiscForge.Core.Util;

namespace DiscForge.App.Views;

/// <summary>
/// Search a disc image (or any file) for text or a byte pattern. Each hit shows its offset, its
/// sector, a little of the surrounding data and — for an ISO 9660 image — which file on the disc it
/// is inside. Uses <see cref="ByteSearch"/> and <see cref="IsoReader"/>.
/// </summary>
internal sealed class FindInImageView : ToolViewBase
{
    private const int MaxHits = 2000;
    private readonly TextBox _file;
    private readonly TextBox _pattern = new() { Location = new Point(92, 42), Width = 380, Font = Theme.Mono };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Location = new Point(480, 42), Font = Theme.Ui };
    private readonly CheckBox _case = new() { Text = "Match case", AutoSize = true, Location = new Point(92, 70), Font = Theme.Ui, Checked = true };
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Font = Theme.Mono, Location = new Point(12, 150), Size = new Size(712, 276),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    public FindInImageView()
    {
        _file = AddFileRow("File:", 12, () => PickFile());
        Controls.Add(new Label { Text = "Find:", AutoSize = true, Location = new Point(12, 45), Font = Theme.Ui });
        _mode.Items.AddRange(new object[] { "Text", "Hex bytes" });
        _mode.SelectedIndex = 0;
        Controls.AddRange(new Control[] { _pattern, _mode, _case });
        var go = AddButton("Search", 628, 40, 96, SearchAsync);
        _pattern.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; go.PerformClick(); } };
        Status.Location = new Point(12, 104);
        Controls.Add(Status);
        Bar.Location = new Point(12, 126);
        Controls.Add(Bar);
        _list.Columns.Add("Offset", 110);
        _list.Columns.Add("Sector", 80);
        _list.Columns.Add("In file", 220);
        _list.Columns.Add("Nearby", 290);
        Controls.Add(_list);
        SetStatus("Type text (for example a game's serial number or a name) or hex bytes like 4D 5A 90 00.");
    }

    private async Task SearchAsync()
    {
        if (!RequireFile(_file, "a file")) return;
        string path = _file.Text.Trim(), pat = _pattern.Text;
        if (pat.Length == 0) { SetStatus("Type something to find.", Theme.Warn); return; }
        bool hex = _mode.SelectedIndex == 1, matchCase = _case.Checked;
        byte[] needle;
        try { needle = hex ? ByteSearch.ParseHex(pat) : Encoding.UTF8.GetBytes(pat); }
        catch (FormatException) { SetStatus("That isn't valid hex — use pairs of hex digits, like 4D 5A.", Theme.Warn); return; }
        if (needle.Length == 0) return;
        _list.Items.Clear();
        SetStatus("Searching…");

        var hits = await Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            var found = new List<long>(ByteSearch.FindAll(fs, needle).Take(MaxHits));
            if (!hex && !matchCase)
            {
                foreach (var variant in new[] { pat.ToUpperInvariant(), pat.ToLowerInvariant() })
                {
                    if (variant == pat) continue;
                    fs.Position = 0;
                    found.AddRange(ByteSearch.FindAll(fs, Encoding.UTF8.GetBytes(variant)).Take(MaxHits));
                }
                found = found.Distinct().OrderBy(x => x).Take(MaxHits).ToList();
            }
            // Which ISO 9660 file each hit is inside (plain 2048-byte images only).
            var files = new List<IsoEntry>();
            try
            {
                fs.Position = 0;
                files = IsoReader.Read(fs).Files.OrderBy(e => e.Extent).ToList();
            }
            catch (Exception ex) when (ex is IsoFormatException or IOException or ArgumentException or EndOfStreamException) { }
            var rows = new List<string[]>();
            var ctx = new byte[48];
            foreach (var off in found)
            {
                long lba = off / 2048;
                var inFile = files.LastOrDefault(e => e.Extent <= lba && lba <= e.LastSector && e.Size > 0);
                fs.Position = Math.Max(0, off - 16);
                int n = fs.Read(ctx, 0, ctx.Length);
                var sb = new StringBuilder(n);
                for (int i = 0; i < n; i++) sb.Append(ctx[i] is >= 32 and < 127 ? (char)ctx[i] : '·');
                rows.Add(new[] { $"0x{off:X}", lba.ToString("N0"), inFile is null ? "" : inFile.Path, sb.ToString() });
            }
            return (rows, files.Count > 0);
        });
        foreach (var r in hits.rows) _list.Items.Add(new ListViewItem(r));
        int count = hits.rows.Count;
        SetStatus(count == 0 ? "Not found." : $"{count:N0} match(es){(count >= MaxHits ? $" (showing the first {MaxHits:N0})" : "")}." +
                  (hits.Item2 ? " The \"In file\" column shows which file on the disc each match is inside." : ""),
                  count == 0 ? Theme.Warn : Theme.Good);
    }
}
