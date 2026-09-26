// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Preservation;

namespace DiscForge.App.Views;

/// <summary>
/// Watch a collection folder for silent corruption: the first check records every file's SHA-256,
/// later checks report what changed — and flag suspected bit-rot, where a file's content changed but
/// its date didn't (nothing wrote to it, so the disk did). Uses <see cref="LibraryWatch"/>; the baseline
/// lives in the folder as .dfwatch.json, the same file `dforge library-watch` uses.
/// </summary>
internal sealed class BitRotWatchView : ToolViewBase
{
    private readonly TextBox _folder;
    private readonly Label _baseline = new() { AutoSize = false, Location = new Point(92, 40), Size = new Size(632, 18), Font = Theme.Small, ForeColor = Theme.TextMuted };
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, Font = Theme.Ui, Location = new Point(12, 136), Size = new Size(712, 290),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };
    private WatchSnapshot? _latest;

    public BitRotWatchView()
    {
        _folder = AddFileRow("Folder:", 12, () => PickFolder("Choose the folder of images to watch"));
        Controls.Add(_baseline);
        _folder.TextChanged += (_, _) => ShowBaseline();
        AddButton("Check now", 12, 64, 120, CheckAsync);
        AddButton("Accept changes as new baseline", 140, 64, 220, AcceptAsync);
        Status.Location = new Point(12, 102);
        Controls.Add(Status);
        Bar.Location = new Point(12, 122);
        Controls.Add(Bar);
        _list.Columns.Add("What", 110);
        _list.Columns.Add("File", 380);
        _list.Columns.Add("Detail", 200);
        Controls.Add(_list);
        SetStatus("Choose a folder, then press Check now. The first check records a baseline; later checks compare against it.");
    }

    private string StatePath => Path.Combine(_folder.Text.Trim(), ".dfwatch.json");

    private void ShowBaseline()
    {
        try
        {
            if (!Directory.Exists(_folder.Text.Trim())) { _baseline.Text = ""; return; }
            if (!File.Exists(StatePath)) { _baseline.Text = "No baseline yet — the first check will record one."; return; }
            var snap = LibraryWatch.FromJson(File.ReadAllText(StatePath));
            _baseline.Text = $"Baseline: {snap.Entries.Count:N0} file(s), recorded {Stamp(snap.CreatedUtc)}.";
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { _baseline.Text = "The baseline file can't be read: " + ex.Message; }
    }

    private static string Stamp(string? utc) =>
        DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime().ToString("d MMM yyyy HH:mm") : "earlier";

    private HashSet<string> Exclude() => new(StringComparer.OrdinalIgnoreCase) { ".dfwatch.json" };

    private async Task CheckAsync()
    {
        string dir = _folder.Text.Trim();
        if (!Directory.Exists(dir)) { RetroMessageBox.Show("Choose a folder first."); return; }
        _list.Items.Clear();
        SetStatus("Reading every file and working out its checksum — this takes a while for a big collection…");
        Bar.Style = ProgressBarStyle.Marquee;
        Bar.Visible = true;
        try
        {
            var cur = await Task.Run(() => LibraryWatch.ScanDirectory(dir, DateTime.UtcNow.ToString("o"), Exclude()));
            _latest = cur;
            if (!File.Exists(StatePath))
            {
                await File.WriteAllTextAsync(StatePath, LibraryWatch.ToJson(cur));
                SetStatus($"Baseline recorded: {cur.Entries.Count:N0} file(s). Check again any time to see what has changed.", Theme.Good);
                ShowBaseline();
                return;
            }
            var prev = LibraryWatch.FromJson(await File.ReadAllTextAsync(StatePath));
            var report = LibraryWatch.Compare(prev, cur);
            foreach (var kind in new[] { DriftKind.SuspectedRot, DriftKind.Removed, DriftKind.Modified, DriftKind.Added })
                foreach (var c in report.Changes.Where(c => c.Kind == kind))
                {
                    var item = new ListViewItem(new[] { Label(c.Kind), c.Path, c.Detail });
                    if (c.Kind == DriftKind.SuspectedRot) item.ForeColor = Theme.Bad;
                    else if (c.Kind == DriftKind.Removed) item.ForeColor = Theme.Warn;
                    _list.Items.Add(item);
                }
            SetStatus(report.Summary(), report.RotDetected ? Theme.Bad : report.AnyChange ? Theme.Warn : Theme.Good);
            if (report.RotDetected)
                RetroMessageBox.Show($"{report.SuspectedRot} file(s) changed without being written to — that's what bit-rot looks like.\n\n" +
                    "Restore them from a backup, or repair them with their parity file (Protect Image) if you made one. " +
                    "Don't accept these changes as the new baseline.");
        }
        finally
        {
            Bar.Style = ProgressBarStyle.Continuous;
        }
    }

    private static string Label(DriftKind k) => k switch
    {
        DriftKind.SuspectedRot => "Possible rot",
        DriftKind.Removed => "Missing",
        DriftKind.Modified => "Changed",
        DriftKind.Added => "New",
        _ => k.ToString(),
    };

    private async Task AcceptAsync()
    {
        string dir = _folder.Text.Trim();
        if (!Directory.Exists(dir)) { RetroMessageBox.Show("Choose a folder first."); return; }
        if (_latest is null) { RetroMessageBox.Show("Run a check first, so you can see what you're accepting."); return; }
        if (RetroMessageBox.Show("Make the current state of the folder the new baseline?\n\nOnly do this for changes you made on purpose.",
                "DiscForge", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        await File.WriteAllTextAsync(StatePath, LibraryWatch.ToJson(_latest));
        _list.Items.Clear();
        SetStatus($"New baseline recorded: {_latest.Entries.Count:N0} file(s).", Theme.Good);
        ShowBaseline();
    }
}
