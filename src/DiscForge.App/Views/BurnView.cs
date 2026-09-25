// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Burning;
using DiscForge.Core.Cdi;
using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using DiscForge.Core.Devices;
using DiscForge.Core.Media;
using DiscForge.Core.Raw;
using DiscForge.Core.Reading;
using DiscForge.Devices;
using DiscForge.Devices.Burning;
using DiscForge.Devices.Media;
using DiscForge.Devices.Reading;

namespace DiscForge.App.Views;

/// <summary>
/// The burn job screen, laid out the way the classic tools did it: pick a
/// Destination (a drive — or an image file, which is a first-class destination),
/// tick Actions (Test / Write / Verify), optionally force a Method, set Copies,
/// press Start, and watch a numbered event log.
///
/// All the decision-making lives in Core: BurnJobPlanner validates the job and
/// expands it into steps, refusing what a drive can't honour BEFORE any media is
/// touched. This view only collects the request and reports what happened.
/// </summary>
internal sealed class BurnView : UserControl
{
    private readonly TextBox _cdiPath = new()
    {
        Width = 420, ReadOnly = true, Font = Theme.Ui, Location = new Point(70, 13),
    };

    // Escape hatch for burns DiscForge's own engines can't do (or that a user would simply
    // rather run through a tool they already trust) — the burn-side counterpart to Read's own
    // external-tool row. These three specifically moved here FROM ReadView: they're burn/mount
    // tools first (ImgBurn's whole purpose is writing an image to disc; Alcohol 120% and DAEMON
    // Tools are burning/mounting suites, not rippers), so they belong on the screen that burns,
    // not the screen that reads. Each remembers its own path via Settings, and all three share
    // ExternalToolLauncher — the same launch logic (WorkingDirectory fix, path normalization,
    // clear-on-failure) ReadView's rip-tool buttons already use, pulled out once both views
    // needed it rather than copied a second time. DiscForge never bundles, inspects, or knows
    // anything else about what these tools do.
    private readonly Button _externalBurnImgBurn = new()
    {
        Text = "ImgBurn…", Location = new Point(12, 42), Width = 100, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly Button _externalBurnAlcohol120 = new()
    {
        Text = "Alcohol 120%…", Location = new Point(120, 42), Width = 140, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly Button _externalBurnDaemonTools = new()
    {
        Text = "DAEMON Tools…", Location = new Point(268, 42), Width = 140, Height = 26,
        FlatStyle = FlatStyle.System,
    };

    private readonly ListView _destinations = new()
    {
        Location = new Point(12, 90), Size = new Size(712, 92),
        View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Theme.Ui, BackColor = Color.White,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    // Actions
    private readonly CheckBox _test = new() { Text = "Test", AutoSize = true, Location = new Point(24, 214), Font = Theme.Ui };
    private readonly CheckBox _write = new() { Text = "Write", AutoSize = true, Location = new Point(24, 236), Checked = true, Font = Theme.Ui };
    private readonly CheckBox _verify = new() { Text = "Verify", AutoSize = true, Location = new Point(24, 258), Font = Theme.Ui };
    private readonly NumericUpDown _copies = new()
    {
        Minimum = 1, Maximum = 99, Value = 1, Width = 52,
        Location = new Point(96, 282), Font = Theme.Ui,
    };

    // Methods
    private readonly RadioButton _auto = new() { Text = "DAO/SAO", AutoSize = true, Location = new Point(200, 214), Checked = true, Font = Theme.Ui };
    private readonly RadioButton _tao = new() { Text = "TAO", AutoSize = true, Location = new Point(200, 236), Font = Theme.Ui };
    private readonly RadioButton _raw = new() { Text = "RAW", AutoSize = true, Location = new Point(200, 258), Font = Theme.Ui };

    // Speed. Items are (label, sectors/sec) pairs; index 0 is always "Max"
    // (null = let the drive run at its default). Populated per-media on detect.
    private readonly ComboBox _speed = new()
    {
        Location = new Point(420, 211), Width = 160, Font = Theme.Ui,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };

    private readonly Button _erase = new()
    {
        Text = "Erase disc…", Location = new Point(420, 248), Width = 110, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };

    // Burn queue: several DIFFERENT images burned one after another to the same
    // destination(s)/settings, unattended between discs except for the media
    // swap itself — the classic tools' "build queue" job. Optional: an empty
    // queue leaves Start burning the single open image exactly as before.
    private readonly ListBox _queue = new()
    {
        Location = new Point(12, 326), Size = new Size(560, 80), Font = Theme.Ui,
        SelectionMode = SelectionMode.MultiExtended,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Button _queueAdd = new()
    {
        Text = "Add…", Location = new Point(580, 326), Width = 132, Height = 24,
        FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly Button _queueRemove = new()
    {
        Text = "Remove", Location = new Point(580, 354), Width = 132, Height = 24,
        FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly Button _queueClear = new()
    {
        Text = "Clear", Location = new Point(580, 382), Width = 132, Height = 24,
        FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };

    private readonly Button _start = new()
    {
        Text = "Start", Location = new Point(12, 416), Width = 100, Height = 28,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(124, 419), Size = new Size(600, 22), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    private readonly EventLogView _log = new()
    {
        Location = new Point(12, 454), Size = new Size(712, 144),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private string? _openCdi;
    /// <summary>True when the source is a plain .iso rather than a .cdi. An ISO is
    /// already the cooked data a burner wants, so it needs no staging.</summary>
    private bool _sourceIsIso;
    /// <summary>True when the source is a CUE sheet. A CUE burns via RAW DAO —
    /// exact indexes, gaps, flags, ISRC/MCN and CD-TEXT are the point of it.</summary>
    private bool _sourceIsCue;
    /// <summary>Set when the opened image was a CloneCD .ccd — the burn engines only know
    /// CUE/ISO/raw-CDI, not CloneCD's own .ccd+.img+.sub layout, so <see cref="OpenCdi"/>
    /// converts it to a BIN/CUE pair in this temp directory first (via the same
    /// <see cref="DiscConverter"/> hub — and the CloneCD reader it already uses for
    /// Convert/Interop — so nothing new was taught to the burn engines themselves) and burns
    /// that instead. Cleaned up the next time a new image is opened, and on Dispose.</summary>
    private string? _ccdTempDir;

    /// <summary>The shared planner/execution engine — see <see cref="BurnExecutor"/>
    /// for why this isn't reimplemented here.</summary>
    private readonly BurnExecutor _executor;

    public BurnView()
    {
        // Establish a realistic size before adding anchored children (see InspectView).
        Size = new Size(736, 608);
        BackColor = Color.White;
        Padding = new Padding(12);
        _executor = new BurnExecutor(_log, _progress, SelectedSpeed);

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        var open = new Button { Text = "Open…", Location = new Point(498, 12), Width = 80, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => OpenCdi();

        Controls.Add(new Label { Text = "Destination:", AutoSize = true, Location = new Point(12, 72), Font = Theme.UiBold });

        var detect = new Button
        {
            Text = "Detect drives", Location = new Point(536, 68), Width = 100, FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        detect.Click += async (_, _) => await DetectAsync();

        var addFile = new Button
        {
            Text = "Image file…", Location = new Point(642, 68), Width = 82, FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        addFile.Click += (_, _) => AddImageDestination();

        _destinations.Columns.Add("Destination", 460);
        _destinations.Columns.Add("Capabilities", 235);
        // Several destinations may be ticked: burning every drive at once is the
        // whole point of a checkbox list.
        _destinations.ItemChecked += (_, _) => UpdateStartEnabled();

        Controls.Add(GroupLabel("Actions", new Point(12, 194)));
        Controls.Add(GroupLabel("Methods", new Point(188, 194)));
        Controls.Add(GroupLabel("Speed / Media", new Point(408, 194)));
        Controls.Add(new Label { Text = "Copies:", AutoSize = true, Location = new Point(24, 284), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "DAO/SAO is used unless TAO or RAW is chosen.",
            AutoSize = true, Location = new Point(200, 284), Font = Theme.Ui, ForeColor = Color.Gray,
        });

        _speed.Items.Add(new SpeedItem("Max (drive default)", null));
        _speed.SelectedIndex = 0;
        _erase.Click += async (_, _) => await EraseAsync();
        _destinations.SelectedIndexChanged += (_, _) => UpdateEraseEnabled();

        Controls.Add(GroupLabel("Burn queue (optional) — several images, one after another",
            new Point(12, 308)));
        _queueAdd.Click += (_, _) => AddToQueue();
        _queueRemove.Click += (_, _) => RemoveSelectedQueueItems();
        _queueClear.Click += (_, _) => { _queue.Items.Clear(); UpdateStartEnabled(); };
        _queue.SelectedIndexChanged += (_, _) =>
            _queueRemove.Enabled = _queue.SelectedItems.Count > 0;
        _queueRemove.Enabled = false;

        _start.Click += async (_, _) => await StartAsync();
        foreach (var cb in new[] { _test, _write, _verify }) cb.CheckedChanged += (_, _) => UpdateStartEnabled();

        _externalBurnImgBurn.Click += (_, _) => LaunchExternalBurnerImgBurn();
        _externalBurnAlcohol120.Click += (_, _) => LaunchExternalBurnerAlcohol120();
        _externalBurnDaemonTools.Click += (_, _) => LaunchExternalBurnerDaemonTools();

        Controls.Add(_cdiPath); Controls.Add(open);
        Controls.Add(_externalBurnImgBurn); Controls.Add(_externalBurnAlcohol120); Controls.Add(_externalBurnDaemonTools);
        Controls.Add(detect); Controls.Add(addFile);
        Controls.Add(_destinations);
        Controls.Add(_test); Controls.Add(_write); Controls.Add(_verify); Controls.Add(_copies);
        Controls.Add(_auto); Controls.Add(_tao); Controls.Add(_raw);
        Controls.Add(_speed); Controls.Add(_erase);
        Controls.Add(_queue); Controls.Add(_queueAdd); Controls.Add(_queueRemove); Controls.Add(_queueClear);
        Controls.Add(_start); Controls.Add(_progress);
        Controls.Add(_log);

        _log.Add("Open an image and choose a destination.");
    }

    // --- burn queue ------------------------------------------------------------

    private void AddToQueue()
    {
        using var dlg = new OpenFileDialog { Filter = BurnExecutor.ImageFileDialogFilter, Multiselect = true };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) _queue.Items.Add(f);
        _log.Add($"Queue: added {dlg.FileNames.Length} image(s) — now {_queue.Items.Count} queued.");
        UpdateStartEnabled();
    }

    private void RemoveSelectedQueueItems()
    {
        foreach (var item in _queue.SelectedItems.Cast<object>().ToList())
            _queue.Items.Remove(item);
        UpdateStartEnabled();
    }

    private static Label GroupLabel(string text, Point at) => new()
    {
        Text = text, AutoSize = true, Location = at, Font = Theme.UiBold, ForeColor = Theme.Accent,
    };

    // --- destinations --------------------------------------------------------

    private void OpenCdi()
    {
        using var dlg = new OpenFileDialog { Filter = BurnExecutor.ImageFileDialogFilter };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        LoadImage(dlg.FileName);
    }

    /// <summary>
    /// Load an image as the "currently open" source (<see cref="_openCdi"/> and
    /// friends) — everything <see cref="OpenCdi"/> used to do once past the file
    /// dialog, pulled out so a queued burn (<see cref="RunQueueAsync"/>) can load
    /// each of several images in turn the exact same way a manual Open… does.
    /// Returns false (having already logged why) when the image can't be used.
    /// </summary>
    private bool LoadImage(string originalPath)
    {
        // A previous open may have left a CCD->CUE staging directory behind — done with it now.
        CleanupCcdTempDir();

        var pickedPath = originalPath;
        var pickedExt = Path.GetExtension(pickedPath);
        var displayName = Path.GetFileName(originalPath);

        if (pickedExt.Equals(".ccd", StringComparison.OrdinalIgnoreCase))
        {
            // The burn engines below only understand a CUE sheet or a raw ISO/CDI stream — they
            // have no notion of CloneCD's three-file .ccd+.img+.sub layout, and adding one to
            // RawDaoBurnEngine/SptiRawDaoBurnEngine directly would mean teaching live-burn code
            // a new format. Converting first through DiscConverter — the same hub Convert and
            // Interop already use, and which already fully understands CloneCD on the read side
            // — sidesteps that: the burn engines never see anything but the CUE sheet they
            // already handle, unchanged.
            try
            {
                _ccdTempDir = Path.Combine(Path.GetTempPath(), "DiscForge_ccd_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_ccdTempDir);
                var cuePath = Path.Combine(_ccdTempDir, Path.GetFileNameWithoutExtension(pickedPath) + ".cue");
                DiscConverter.Convert(pickedPath, cuePath);
                _log.Add($"Converted {displayName} (CloneCD) to a BIN/CUE for burning.");
                pickedPath = cuePath;
                pickedExt = ".cue";
            }
            catch (Exception ex)
            {
                _log.Add($"Could not read '{displayName}' as a CloneCD image: {ex.Message}", EventLogView.Level.Error);
                CleanupCcdTempDir();
                return false;
            }
        }

        _openCdi = pickedPath;
        var ext = pickedExt;
        _sourceIsIso = ext.Equals(".iso", StringComparison.OrdinalIgnoreCase);
        _sourceIsCue = ext.Equals(".cue", StringComparison.OrdinalIgnoreCase);
        _cdiPath.Text = originalPath;

        if (_sourceIsCue)
        {
            try
            {
                var cue = CueSheet.Parse(File.ReadAllText(pickedPath));
                _log.Add($"Image: {displayName} — CUE sheet, " +
                         $"{cue.Tracks.Count} track(s)" +
                         (cue.Catalog is not null ? ", MCN" : "") +
                         (cue.Title is not null ? ", CD-TEXT" : ""));
                _log.Add("A CUE sheet burns via RAW DAO: exact gaps, index points, flags, " +
                         "ISRC/MCN and CD-TEXT are written as declared.");
            }
            catch (Exception ex)
            {
                _log.Add("Could not parse the CUE sheet: " + ex.Message, EventLogView.Level.Error);
                _openCdi = null;
                _sourceIsCue = false;
                CleanupCcdTempDir();
                UpdateStartEnabled();
                return false;
            }
            UpdateStartEnabled();
            return true;
        }

        var size = new FileInfo(pickedPath).Length;
        _log.Add($"Image: {displayName} " +
                 $"({size / (1024.0 * 1024.0):N1} MB, {(_sourceIsIso ? "ISO" : "CDI")})");
        if (_sourceIsIso && size % 2048 != 0)
            _log.Add($"This file is {size:N0} bytes — not a whole number of 2048-byte sectors. " +
                     "It may be truncated, or a raw BIN rather than an ISO.", EventLogView.Level.Warn);

        UpdateStartEnabled();
        return true;
    }

    /// <summary>
    /// Launch ImgBurn — a general-purpose CD/DVD/BD burning tool whose "create image from disc"
    /// mode some also use as a lightweight ripper, but whose whole reason for being on THIS
    /// screen rather than Read's is that burning is its primary job. Delegates to
    /// <see cref="ExternalToolLauncher"/>, the shared logic ReadView's rip-tool buttons use too.
    /// </summary>
    private void LaunchExternalBurnerImgBurn() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathImgBurn,
        p => Settings.ExternalDumperPathImgBurn = p,
        "Locate ImgBurn",
        "Use it to burn your image — DiscForge did not perform this burn.",
        _log);

    /// <summary>Same idea again, for Alcohol 120% — a CD/DVD imaging AND burning/mounting suite,
    /// still in use by some. Same reasoning as <see cref="LaunchExternalBurnerImgBurn"/> for why
    /// it lives here rather than on Read.</summary>
    private void LaunchExternalBurnerAlcohol120() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathAlcohol120,
        p => Settings.ExternalDumperPathAlcohol120 = p,
        "Locate Alcohol 120%",
        "Use it to burn your image — DiscForge did not perform this burn.",
        _log);

    /// <summary>Same idea again, for DAEMON Tools — primarily a virtual-drive/mounting tool with
    /// burning as a secondary feature, which still puts it closer to Burn than to Read (it
    /// doesn't rip discs at all). Same reasoning as
    /// <see cref="LaunchExternalBurnerImgBurn"/>.</summary>
    private void LaunchExternalBurnerDaemonTools() => ExternalToolLauncher.Launch(
        () => Settings.ExternalDumperPathDaemonTools,
        p => Settings.ExternalDumperPathDaemonTools = p,
        "Locate DAEMON Tools",
        "Use it to burn or mount your image — DiscForge did not perform this burn.",
        _log);

    /// <summary>Best-effort delete of the CCD-&gt;CUE staging directory from a previous open, if
    /// any. Never throws — a leftover temp directory is harmless clutter, not a reason to fail
    /// whatever the caller was doing.</summary>
    private void CleanupCcdTempDir()
    {
        if (_ccdTempDir is null) return;
        try { if (Directory.Exists(_ccdTempDir)) Directory.Delete(_ccdTempDir, recursive: true); }
        catch (Exception ex) { AppLog.Write($"could not delete CCD staging dir '{_ccdTempDir}': {ex.Message}"); }
        _ccdTempDir = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CleanupCcdTempDir();
        base.Dispose(disposing);
    }

    private async Task DetectAsync()
    {
        _log.Add("Detecting drives…");
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());

            foreach (ListViewItem i in _destinations.Items.Cast<ListViewItem>().ToList())
                if (i.Tag is DriveCapabilities) _destinations.Items.Remove(i);

            foreach (var d in _detected)
            {
                var item = new ListViewItem($"{d.Vendor} {d.Model} ({d.DevicePath})") { Tag = d };

                // Say what's in the drive AND whether it can be written — a
                // written DVD+R DL and a blank one otherwise look identical.
                string media = d.Disc is null
                    ? $"{d.MediaProfile} — no disc / unknown"
                    : $"{d.MediaProfile} — {d.Disc.Describe()}";
                item.SubItems.Add(media);
                if (d.Disc is { IsBlank: false })
                    item.ForeColor = d.Disc.IsSpent
                        ? Color.FromArgb(0xA0, 0x20, 0x20)
                        : Color.FromArgb(0xA0, 0x60, 0x00);

                _destinations.Items.Insert(0, item);
                AppLog.Write($"  drive {d.DevicePath}: rawDAO96={d.RawDao96} media={d.MediaProfile} " +
                             $"disc={(d.Disc is null ? "unknown" : $"{d.Disc.Status}, erasable={d.Disc.Erasable}")}");

                if (d.Disc is { IsSpent: true })
                    _log.Add($"{d.Vendor} {d.Model}: the disc is {d.Disc.Describe()}. " +
                             "Insert a blank one to burn.", EventLogView.Level.Warn);
                else if (d.Disc is { NeedsErasing: true })
                    _log.Add($"{d.Vendor} {d.Model}: the disc is {d.Disc.Describe()}.",
                             EventLogView.Level.Warn);
            }

            _log.Add(_detected.Count == 0
                    ? "No optical drives detected (raw access usually needs administrator)."
                    : $"{_detected.Count} drive(s) detected.",
                _detected.Count == 0 ? EventLogView.Level.Warn : EventLogView.Level.Good);

            await PopulateSpeedsAsync();
            UpdateEraseEnabled();
        }
        catch (Exception ex)
        {
            _log.Add("Drive detection failed: " + ex.Message, EventLogView.Level.Error);
        }
    }

    private void AddImageDestination()
    {
        using var dlg = new SaveFileDialog { Filter = "CDI image (*.cdi)|*.cdi", FileName = "copy.cdi" };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        foreach (ListViewItem i in _destinations.Items.Cast<ListViewItem>().ToList())
            if (i.Tag is string) _destinations.Items.Remove(i);

        var item = new ListViewItem(dlg.FileName) { Tag = dlg.FileName };
        item.SubItems.Add("Image file");
        _destinations.Items.Add(item);
        item.Checked = true;
        _log.Add($"Destination: image file {Path.GetFileName(dlg.FileName)}");
    }

    private List<object> CheckedDestinations() =>
        _destinations.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag!)
            .Where(t => t is not null)
            .ToList();

    private void UpdateStartEnabled() =>
        _start.Enabled = (_openCdi is not null || _queue.Items.Count > 0)
                         && CheckedDestinations().Count > 0
                         && (_test.Checked || _write.Checked || _verify.Checked);

    // --- the job -------------------------------------------------------------

    private static BurnDestination ToDestination(object tag) => tag switch
    {
        DriveCapabilities d => new BurnDestination.Drive(d),
        string path => new BurnDestination.ImageFile(path),
        _ => throw new InvalidOperationException("Unknown destination."),
    };

    private MultiBurnJob BuildJob(IEnumerable<object> destTags) => new()
    {
        Destinations = destTags.Select(ToDestination).ToList(),
        Test = _test.Checked,
        Write = _write.Checked,
        Verify = _verify.Checked,
        Copies = (int)_copies.Value,
        Method = _raw.Checked ? BurnMethodChoice.RawDao96
               : _tao.Checked ? BurnMethodChoice.Tao
               : BurnMethodChoice.Auto,
    };

    /// <summary>
    /// Plan a job against whichever image is currently loaded as <see cref="_openCdi"/>,
    /// logging its shape the same way for every source kind. Failures are logged
    /// here (refusal or exception) and reported back as null rather than thrown,
    /// so both a single-image Start and a queue item can treat "couldn't plan
    /// this one" the same way — log it, move on — without duplicating the
    /// try/catch/log dance.
    /// </summary>
    private MultiBurnPlan? PlanCurrentImage(IReadOnlyList<object> destTags) =>
        _openCdi is null ? null
            : BurnExecutor.PlanImage(_openCdi, _sourceIsIso, _sourceIsCue, BuildJob(destTags), _log);

    private async Task StartAsync()
    {
        if (_queue.Items.Count > 0) { await RunQueueAsync(); return; }
        if (_openCdi is null) return;
        var destTags = CheckedDestinations();
        if (destTags.Count == 0) { _log.Add("Choose a destination.", EventLogView.Level.Warn); return; }

        _log.Clear();
        _progress.Value = 0;

        var plan = PlanCurrentImage(destTags);
        if (plan is null) return;
        if (!BurnExecutor.LogPlanAndConfirm(plan, _log)) return;

        _start.Enabled = false;
        try { await _executor.RunAllAsync(plan, _openCdi!, _sourceIsIso, _sourceIsCue); }
        finally { UpdateStartEnabled(); }
    }

    /// <summary>
    /// Burn every queued image, one after another, to the same checked
    /// destination(s) with the same Actions/Methods/Speed settings — the
    /// classic tools' "build queue": several DIFFERENT sources run back to
    /// back rather than the same source duplicated to several drives at once
    /// (that's what the destination checkboxes above already do). A disc
    /// destination pauses between items so the user can swap media; an image
    /// file destination just moves straight on. One item failing to open,
    /// plan, or burn is logged and skipped — it doesn't abort the rest of the
    /// queue, same "one bad destination doesn't sink the job" stance
    /// <see cref="BurnJobPlanner"/> already takes.
    /// </summary>
    private async Task RunQueueAsync()
    {
        var destTags = CheckedDestinations();
        if (destTags.Count == 0) { _log.Add("Choose a destination.", EventLogView.Level.Warn); return; }

        var paths = _queue.Items.Cast<string>().ToList();
        _log.Clear();
        _log.Add($"Burn queue: {paths.Count} image(s) to the same destination(s), one after another.");

        _start.Enabled = false;
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                _log.Add($"--- Queue {i + 1}/{paths.Count}: {Path.GetFileName(path)} ---");

                if (!LoadImage(path))
                {
                    _log.Add("Could not open this image — skipping it.", EventLogView.Level.Error);
                    continue;
                }

                var plan = PlanCurrentImage(destTags);
                if (plan is null) continue;
                if (!plan.AnyRunnable)
                {
                    _log.Add("No destination could take this image — skipping it.", EventLogView.Level.Error);
                    continue;
                }
                if (!BurnExecutor.LogPlanAndConfirm(plan, _log, $" (queue item {i + 1}/{paths.Count})"))
                {
                    _log.Add("Queue stopped.", EventLogView.Level.Warn);
                    break;
                }

                _progress.Value = 0;
                await _executor.RunAllAsync(plan, _openCdi!, _sourceIsIso, _sourceIsCue);
            }
            _log.Add("Queue finished.", EventLogView.Level.Good);
        }
        finally
        {
            CleanupCcdTempDir();
            UpdateStartEnabled();
        }
    }

    // --- speed & erase -------------------------------------------------------

    /// <summary>A combo entry: display label + IMAPI2 sectors/sec (null = max).</summary>
    private sealed record SpeedItem(string Label, int? SectorsPerSecond)
    {
        public override string ToString() => Label;
    }

    private int? SelectedSpeed() => (_speed.SelectedItem as SpeedItem)?.SectorsPerSecond;

    /// <summary>
    /// Ask each detected drive what speeds it supports for its loaded media and
    /// offer the fastest set found. Speeds are a property of drive+media
    /// together, so this runs after detection, and an empty answer (no disc,
    /// pressed disc) just leaves "Max" as the only choice — never an error.
    /// </summary>
    private async Task PopulateSpeedsAsync()
    {
        var keep = SelectedSpeed();     // try to preserve the user's choice

        var reports = await Task.Run(() =>
            _detected.Select(d => (Drive: d, Report: Imapi2MediaTools.GetWriteSpeeds(d.DevicePath)))
                     .Where(x => x.Report is not null)
                     .ToList());

        // Auto write-speed-by-media-ID: ask each drive what disc it's actually
        // holding (ATIP for CD-R, the physical-format/media-ID descriptor for
        // DVD/BD) and see whether that identity names a certified speed rating
        // (see RecommendedMaxSpeedX — CD-R's ATIP never does; DVD/BD media IDs
        // often do, e.g. "Taiyo Yuden 16× DVD-R"). The lowest rating across every
        // detected drive wins, the same "don't sink the slow drive" logic the
        // burn planner already uses for capability mismatches.
        (int Cap, string Reason)? recommended = null;
        var identities = await Task.Run(() => _detected
            .Select(BurnExecutor.DriveLetterOf)
            .Where(l => l is not null)
            .Select(l => MediaInfoReader.Read(l!.Value).Identity)
            .Where(id => id is not null)
            .ToList());
        foreach (var id in identities)
        {
            // Log the diagnosis even when it doesn't lead to a speed cap — a silent "Max, no reason
            // given" leaves no way to tell "this drive won't report ADIP" apart from "ADIP came back
            // but the media ID isn't in DvdMediaIds yet". Both are real outcomes (most +R/+RW drives
            // refuse ADIP outright — see MediaInfoReader.ReadDiscStructure), and the second one is
            // exactly the kind of gap a user can help close by reporting the code they saw.
            if (id!.Manufacturer is null)
            {
                if (id.MediaId is { Length: > 0 } rawId)
                    _log.Add($"media ID read: \"{rawId}\" — not in the speed-rating table yet " +
                             "(no rated speed known for it; defaulting to Max is correct, not a bug). " +
                             "If you know this disc's real rated speed, this code is worth reporting.");
                else if (id.AtipCode is null)
                    _log.Add("no media ID could be read for this disc (the drive doesn't report ADIP, " +
                             "or this is -R/-RW without one) — defaulting to Max.");
                continue;
            }
            if (MediaIdentityParser.RecommendedMaxSpeedX(id.Manufacturer) is not int cap) continue;
            if (recommended is null || cap < recommended.Value.Cap)
                recommended = (cap, $"{id.Manufacturer} ({id.MediaId ?? id.AtipCode})");
        }

        _speed.Items.Clear();
        _speed.Items.Add(new SpeedItem("Max (drive default)", null));

        // One combo, possibly several drives: offer the union of speeds and let
        // each engine snap to what its drive actually supports.
        var seen = new HashSet<int>();
        foreach (var (drive, report) in reports)
        {
            _log.Add($"{drive.Vendor} {drive.Model}: {report!.MediaName}, speeds " +
                     string.Join(", ", report.SectorsPerSecond.Select(report.DescribeSpeed)));
            foreach (var sps in report.SectorsPerSecond)
                if (seen.Add(sps))
                    _speed.Items.Add(new SpeedItem(report.DescribeSpeed(sps), sps));
        }

        int restore = 0;
        if (keep is int wanted)
        {
            for (int i = 0; i < _speed.Items.Count; i++)
                if ((_speed.Items[i] as SpeedItem)?.SectorsPerSecond == wanted) { restore = i; break; }
        }
        else if (recommended is { } rec)
        {
            // The fastest available speed AT OR UNDER the media's rating — never
            // above it, since a drive can report speeds the media itself isn't
            // certified for. If every available speed is faster than the rating
            // (a very conservative rating on a fast drive), fall back to the
            // slowest available rather than an uncapped "Max".
            int bestIndex = -1, bestX = -1, slowestIndex = -1, slowestX = int.MaxValue;
            for (int i = 1; i < _speed.Items.Count; i++)
            {
                if (MediaIdentityParser.RecommendedMaxSpeedX(((SpeedItem)_speed.Items[i]).Label) is not int x) continue;
                if (x <= rec.Cap && x > bestX) { bestX = x; bestIndex = i; }
                if (x < slowestX) { slowestX = x; slowestIndex = i; }
            }
            int chosen = bestIndex >= 0 ? bestIndex : slowestIndex;
            if (chosen >= 0)
            {
                restore = chosen;
                _log.Add($"Media identified as {rec.Reason}, rated {rec.Cap}x — defaulting write speed " +
                         $"to {((SpeedItem)_speed.Items[chosen]).Label} instead of the drive's max. " +
                         "Pick a different speed from the list to override.");
            }
        }
        _speed.SelectedIndex = restore;
    }

    private void UpdateEraseEnabled() => _erase.Enabled = SelectedDriveForErase() is not null;

    /// <summary>The drive Erase acts on: the highlighted one, or the only one.</summary>
    private DriveCapabilities? SelectedDriveForErase()
    {
        var selected = _destinations.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<DriveCapabilities>().FirstOrDefault();
        if (selected is not null) return selected;

        var drives = _destinations.Items.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<DriveCapabilities>().ToList();
        return drives.Count == 1 ? drives[0] : null;
    }

    private async Task EraseAsync()
    {
        var drive = SelectedDriveForErase();
        if (drive is null)
        {
            _log.Add("Select the drive to erase in the destination list.", EventLogView.Level.Warn);
            return;
        }

        // Quick vs full, spelled out — MessageBox buttons carry the choice.
        var choice = RetroMessageBox.Show(
            $"Erase the disc in {drive.Vendor} {drive.Model} ({drive.DevicePath})?\n\n" +
            "Yes — QUICK erase: blanks the disc's table of contents so it reads as " +
            "empty. Takes seconds. The right choice almost always.\n\n" +
            "No — FULL erase: overwrites the entire disc surface. Takes as long as " +
            "a burn. Use for media that's been misbehaving, or when the old " +
            "contents must be unrecoverable.\n\n" +
            "Either way the disc's current contents are gone.",
            "DiscForge — erase disc", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);
        if (choice == DialogResult.Cancel) return;
        bool full = choice == DialogResult.No;

        _erase.Enabled = false;
        _start.Enabled = false;
        try
        {
            var progress = new Progress<BurnProgress>(p =>
            {
                _log.Add($"[{BurnExecutor.Short(drive.DevicePath)}] {p.Phase}: {p.Detail}");
                StatusBus.Report($"{BurnExecutor.Short(drive.DevicePath)} {p.Phase}: {p.Detail}");
            });

            await Task.Run(() => Imapi2MediaTools.Erase(drive.DevicePath, full, progress));
            _log.Add("Erase complete.", EventLogView.Level.Good);

            // The media state just changed under us — re-detect so the list
            // stops warning about a disc that is now blank.
            await DetectAsync();
        }
        catch (Exception ex)
        {
            _log.Add("Erase failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException($"erase {drive.DevicePath}", ex);
        }
        finally
        {
            UpdateEraseEnabled();
            UpdateStartEnabled();
        }
    }

}
