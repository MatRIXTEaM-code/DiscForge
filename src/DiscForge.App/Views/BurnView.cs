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
using DiscForge.Core.Raw;
using DiscForge.Core.Reading;
using DiscForge.Devices;
using DiscForge.Devices.Burning;
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

    private readonly ListView _destinations = new()
    {
        Location = new Point(12, 60), Size = new Size(712, 92),
        View = View.Details, CheckBoxes = true, FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Theme.Ui, BackColor = Color.White,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    // Actions
    private readonly CheckBox _test = new() { Text = "Test", AutoSize = true, Location = new Point(24, 184), Font = Theme.Ui };
    private readonly CheckBox _write = new() { Text = "Write", AutoSize = true, Location = new Point(24, 206), Checked = true, Font = Theme.Ui };
    private readonly CheckBox _verify = new() { Text = "Verify", AutoSize = true, Location = new Point(24, 228), Font = Theme.Ui };
    private readonly NumericUpDown _copies = new()
    {
        Minimum = 1, Maximum = 99, Value = 1, Width = 52,
        Location = new Point(96, 252), Font = Theme.Ui,
    };

    // Methods
    private readonly RadioButton _auto = new() { Text = "DAO/SAO", AutoSize = true, Location = new Point(200, 184), Checked = true, Font = Theme.Ui };
    private readonly RadioButton _tao = new() { Text = "TAO", AutoSize = true, Location = new Point(200, 206), Font = Theme.Ui };
    private readonly RadioButton _raw = new() { Text = "RAW", AutoSize = true, Location = new Point(200, 228), Font = Theme.Ui };

    // Speed. Items are (label, sectors/sec) pairs; index 0 is always "Max"
    // (null = let the drive run at its default). Populated per-media on detect.
    private readonly ComboBox _speed = new()
    {
        Location = new Point(420, 181), Width = 160, Font = Theme.Ui,
        DropDownStyle = ComboBoxStyle.DropDownList,
    };

    private readonly Button _erase = new()
    {
        Text = "Erase disc…", Location = new Point(420, 218), Width = 110, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly Button _start = new()
    {
        Text = "Start", Location = new Point(12, 278), Width = 100, Height = 28,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(124, 281), Size = new Size(600, 22), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };

    private readonly EventLogView _log = new()
    {
        Location = new Point(12, 316), Size = new Size(712, 144),
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

    public BurnView()
    {
        // Establish a realistic size before adding anchored children (see InspectView).
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        var open = new Button { Text = "Open…", Location = new Point(498, 12), Width = 80, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => OpenCdi();

        Controls.Add(new Label { Text = "Destination:", AutoSize = true, Location = new Point(12, 42), Font = Theme.UiBold });

        var detect = new Button
        {
            Text = "Detect drives", Location = new Point(536, 38), Width = 100, FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        detect.Click += async (_, _) => await DetectAsync();

        var addFile = new Button
        {
            Text = "Image file…", Location = new Point(642, 38), Width = 82, FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        addFile.Click += (_, _) => AddImageDestination();

        _destinations.Columns.Add("Destination", 460);
        _destinations.Columns.Add("Capabilities", 235);
        // Several destinations may be ticked: burning every drive at once is the
        // whole point of a checkbox list.
        _destinations.ItemChecked += (_, _) => UpdateStartEnabled();

        Controls.Add(GroupLabel("Actions", new Point(12, 164)));
        Controls.Add(GroupLabel("Methods", new Point(188, 164)));
        Controls.Add(GroupLabel("Speed / Media", new Point(408, 164)));
        Controls.Add(new Label { Text = "Copies:", AutoSize = true, Location = new Point(24, 254), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "DAO/SAO is used unless TAO or RAW is chosen.",
            AutoSize = true, Location = new Point(200, 254), Font = Theme.Ui, ForeColor = Color.Gray,
        });

        _speed.Items.Add(new SpeedItem("Max (drive default)", null));
        _speed.SelectedIndex = 0;
        _erase.Click += async (_, _) => await EraseAsync();
        _destinations.SelectedIndexChanged += (_, _) => UpdateEraseEnabled();

        _start.Click += async (_, _) => await StartAsync();
        foreach (var cb in new[] { _test, _write, _verify }) cb.CheckedChanged += (_, _) => UpdateStartEnabled();

        Controls.Add(_cdiPath); Controls.Add(open);
        Controls.Add(detect); Controls.Add(addFile);
        Controls.Add(_destinations);
        Controls.Add(_test); Controls.Add(_write); Controls.Add(_verify); Controls.Add(_copies);
        Controls.Add(_auto); Controls.Add(_tao); Controls.Add(_raw);
        Controls.Add(_speed); Controls.Add(_erase);
        Controls.Add(_start); Controls.Add(_progress);
        Controls.Add(_log);

        _log.Add("Open an image and choose a destination.");
    }

    private static Label GroupLabel(string text, Point at) => new()
    {
        Text = text, AutoSize = true, Location = at, Font = Theme.UiBold, ForeColor = Theme.Accent,
    };

    // --- destinations --------------------------------------------------------

    private void OpenCdi()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Disc images (*.cdi;*.iso;*.cue)|*.cdi;*.iso;*.cue|CDI images (*.cdi)|*.cdi|" +
                     "ISO images (*.iso)|*.iso|CUE sheets (*.cue)|*.cue|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _openCdi = dlg.FileName;
        var ext = Path.GetExtension(dlg.FileName);
        _sourceIsIso = ext.Equals(".iso", StringComparison.OrdinalIgnoreCase);
        _sourceIsCue = ext.Equals(".cue", StringComparison.OrdinalIgnoreCase);
        _cdiPath.Text = dlg.FileName;

        if (_sourceIsCue)
        {
            try
            {
                var cue = CueSheet.Parse(File.ReadAllText(dlg.FileName));
                _log.Add($"Image: {Path.GetFileName(dlg.FileName)} — CUE sheet, " +
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
            }
            UpdateStartEnabled();
            return;
        }

        var size = new FileInfo(dlg.FileName).Length;
        _log.Add($"Image: {Path.GetFileName(dlg.FileName)} " +
                 $"({size / (1024.0 * 1024.0):N1} MB, {(_sourceIsIso ? "ISO" : "CDI")})");
        if (_sourceIsIso && size % 2048 != 0)
            _log.Add($"This file is {size:N0} bytes — not a whole number of 2048-byte sectors. " +
                     "It may be truncated, or a raw BIN rather than an ISO.", EventLogView.Level.Warn);

        UpdateStartEnabled();
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
        _start.Enabled = _openCdi is not null
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

    private async Task StartAsync()
    {
        if (_openCdi is null) return;
        var destTags = CheckedDestinations();
        if (destTags.Count == 0) { _log.Add("Choose a destination.", EventLogView.Level.Warn); return; }

        _log.Clear();
        _progress.Value = 0;

        // Plan first: impossible work is refused before any media is touched.
        MultiBurnPlan plan;
        try
        {
            ImageShape shape;
            if (_sourceIsIso)
            {
                // A plain ISO is by definition one Mode 1 data track, one session,
                // no audio — its shape is known without parsing anything, and
                // parsing it as a CDI would (correctly) fail: it has no trailer.
                long size = new FileInfo(_openCdi).Length;
                shape = new ImageShape(TrackCount: 1, SessionCount: 1,
                                       HasAudio: false, HasData: true, NonStandardGaps: false);
                _log.Add($"Image: ISO, {size / 2048:N0} sectors, {size / (1024.0 * 1024.0):N1} MB");
            }
            else if (_sourceIsCue)
            {
                // A CUE sheet is a demand for EXACT layout — that's what the
                // format is for — so its shape declares non-standard gaps and
                // the planner routes it to RAW DAO. TAO would silently rewrite
                // the very things the sheet specifies.
                var cue = CueSheet.Parse(File.ReadAllText(_openCdi));
                bool audio = cue.Tracks.Any(t => t.Type == CueTrackType.Audio);
                bool data = cue.Tracks.Any(t => t.Type != CueTrackType.Audio);
                shape = new ImageShape(cue.Tracks.Count, SessionCount: 1,
                                       HasAudio: audio, HasData: data, NonStandardGaps: true);
                _log.Add($"Image: CUE, {cue.Tracks.Count} track(s), exact layout (RAW DAO)");
            }
            else
            {
                using var fs = File.OpenRead(_openCdi);
                var image = CdiParser.Parse(fs);
                shape = ImageShape.Of(image);
                _log.Add($"Image: {image.TrackCount} track(s), {image.Sessions.Count} session(s)");
            }

            plan = BurnJobPlanner.PlanAll(shape, BuildJob(destTags));
        }
        catch (BurnNotSupportedException ex)
        {
            _log.Add("Job refused: " + ex.Message, EventLogView.Level.Error);
            return;
        }
        catch (Exception ex)
        {
            _log.Add("Error: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("burn plan", ex);
            return;
        }

        // A destination that can't take this job says so and sits it out; the
        // rest still run.
        foreach (var d in plan.Refused)
            _log.Add($"{d.Label}: SKIPPED — {d.Refusal}", EventLogView.Level.Error);

        foreach (var d in plan.Runnable)
        {
            _log.Add($"Destination: {d.Label}");
            foreach (var w in d.Warnings) _log.Add($"  {w}", EventLogView.Level.Warn);
            foreach (var st in d.Steps)
                _log.Add($"  planned: {st.Kind} via {st.Method}" +
                         (d.TotalCopies > 1 ? $" (copy {st.CopyNumber}/{d.TotalCopies})" : ""));
        }

        int discs = plan.Runnable.Count(d => !d.IsImageFile);
        if (discs > 0)
        {
            var prompt = discs == 1
                ? "Insert media. Begin the job?"
                : $"Insert blank media in all {discs} drives. They will be burned simultaneously. Begin?";
            if (RetroMessageBox.Show(prompt, "DiscForge",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                _log.Add("Cancelled.", EventLogView.Level.Warn);
                return;
            }
        }

        _start.Enabled = false;
        try { await RunAllAsync(plan); }
        finally { UpdateStartEnabled(); }
    }

    /// <summary>
    /// Run every runnable destination CONCURRENTLY — burning several drives at
    /// once is the point of a multi-destination job. Each gets its own engine and
    /// its own handle on the source image (they must not share a Stream), and one
    /// failing doesn't abort the others: you get a per-destination verdict.
    /// </summary>
    private async Task RunAllAsync(MultiBurnPlan plan)
    {
        var targets = plan.Runnable.ToList();
        var fractions = new double[targets.Count];

        var tasks = targets.Select((d, index) =>
        {
            // Progress<T> captures the SynchronizationContext where it is BUILT.
            // Constructing it inside Task.Run would leave it with none, and its
            // callback would then touch the progress bar from a worker thread.
            // Build it here, on the UI thread; run the work below.
            // The bar shows progress; the log tells the story. Logging every
            // report turned a 2 GB verify into 30,000+ lines and made the
            // diagnostics useless.
            string lastPhase = "";
            var progress = new Progress<BurnProgress>(p =>
            {
                fractions[index] = p.Fraction;
                // Overall progress is the mean across destinations; they run at
                // their own speeds.
                _progress.Value = Math.Clamp((int)(fractions.Average() * 100), 0, 100);

                if (!string.IsNullOrEmpty(p.Detail) && p.Phase != lastPhase)
                {
                    lastPhase = p.Phase;
                    _log.Add($"[{Short(d.Label)}] {p.Phase}: {p.Detail}");
                }
                else
                {
                    StatusBus.Report($"{Short(d.Label)} {p.Phase}: {p.Detail}");
                }
            });

            return Task.Run(async () =>
            {
                try
                {
                    foreach (var step in d.Steps)
                    {
                        _log.Add($"[{Short(d.Label)}] {step.Kind}" +
                                 (d.TotalCopies > 1 ? $" (copy {step.CopyNumber}/{d.TotalCopies})" : "") + "…");

                        if (d.IsImageFile) await RunFileStepAsync(step, d.Label);
                        else await RunDriveStepAsync(step,
                                ((BurnDestination.Drive)d.Destination).Capabilities, progress);

                        _log.Add($"[{Short(d.Label)}] {step.Kind} completed.", EventLogView.Level.Good);
                    }
                    fractions[index] = 1.0;
                    return (d, Ok: true, Error: (string?)null);
                }
                catch (NotImplementedException ex)
                {
                    _log.Add($"[{Short(d.Label)}] unavailable: {ex.Message}", EventLogView.Level.Error);
                    return (d, Ok: false, Error: ex.Message);
                }
                catch (Exception ex)
                {
                    _log.Add($"[{Short(d.Label)}] failed: {ex.Message}", EventLogView.Level.Error);
                    AppLog.WriteException($"burn to {d.Label}", ex);
                    return (d, Ok: false, Error: ex.Message);
                }
            });
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int good = results.Count(r => r.Ok);
        int bad = results.Length - good;
        _progress.Value = 100;

        if (bad == 0)
            _log.Add($"Job complete: {good} destination(s) succeeded.", EventLogView.Level.Good);
        else
            _log.Add($"Job finished: {good} succeeded, {bad} FAILED.",
                bad == results.Length ? EventLogView.Level.Error : EventLogView.Level.Warn);
    }

    /// <summary>Short label for log lines — the device path or file name.</summary>
    /// <summary>Byte-compare two streams without holding either in memory.</summary>
    private static bool StreamsEqual(Stream a, Stream b)
    {
        var ba = new byte[64 * 1024];
        var bb = new byte[64 * 1024];
        while (true)
        {
            int na = a.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            int nb = b.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
        }
    }

    private static string Short(string label)
    {
        int open = label.LastIndexOf('(');
        if (open > 0 && label.EndsWith(')'))
            return label[(open + 1)..^1];
        return Path.GetFileName(label);
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
        for (int i = 0; i < _speed.Items.Count; i++)
            if ((_speed.Items[i] as SpeedItem)?.SectorsPerSecond == keep) { restore = i; break; }
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
                _log.Add($"[{Short(drive.DevicePath)}] {p.Phase}: {p.Detail}");
                StatusBus.Report($"{Short(drive.DevicePath)} {p.Phase}: {p.Detail}");
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

    private async Task RunDriveStepAsync(BurnStep step, DriveCapabilities drive,
                                         IProgress<BurnProgress> progress)
    {
        // Each step kind is a different operation. Treating them alike meant
        // Verify called the burn engine — i.e. it would have re-burned the disc.
        switch (step.Kind)
        {
            case BurnStepKind.Verify:
                if (_sourceIsCue)
                    throw new NotImplementedException(
                        "Verify of a RAW burn needs raw read-back (sub-channels included), " +
                        "which isn't wired up yet. The burn itself reports failure if the " +
                        "drive rejects any sector; untick Verify for CUE sources.");
                await VerifyDiscAsync(drive, progress);
                return;

            case BurnStepKind.Test:
                // IMAPI2's data path exposes no simulated burn, and pretending
                // otherwise would be worse than saying so.
                throw new NotImplementedException(
                    "Test (simulated burn) isn't available through the IMAPI2 data path. " +
                    "Untick Test, or use a rewritable disc so a real write costs nothing.");

            case BurnStepKind.Write:
                break;

            default:
                throw new NotSupportedException($"Unknown step {step.Kind}.");
        }

        var burnPlan = new BurnPlan
        {
            Method = step.Method,
            DevicePath = drive.DevicePath,   // the real path, not the display label
            Warnings = Array.Empty<string>(),
            WriteSpeedSectorsPerSecond = SelectedSpeed(),
        };

        // An ISO goes straight to the burner: it already IS the cooked data, so
        // there's nothing to extract and no staging copy to wait for.
        if (_sourceIsIso)
        {
            await Task.Run(() => new Imapi2BurnEngine().BurnIso(_openCdi!, burnPlan, progress));
            return;
        }

        // A CUE sheet carries the full layout; the RAW engine composes and
        // writes the whole disc from it.
        if (_sourceIsCue)
        {
            await Task.Run(() =>
            {
                using var layout = DiscLayout.FromCueFile(_openCdi!);
                new RawDaoBurnEngine().BurnLayout(layout, burnPlan, progress);
            });
            return;
        }

        IBurnEngine engine = step.Method switch
        {
            BurnMethod.Imapi2Data => new Imapi2BurnEngine(),
            BurnMethod.Imapi2TrackAtOnce => new Imapi2TrackAtOnceBurnEngine(),
            _ => new RawDaoBurnEngine(),
        };

        await Task.Run(() =>
        {
            using var fs = File.OpenRead(_openCdi!);
            var img = CdiParser.Parse(fs);
            engine.Burn(fs, img, burnPlan, progress);
        });
    }

    /// <summary>
    /// Verify a burn by reading the disc back and comparing it against the source,
    /// sector for sector. This is what Verify always should have been: the burn
    /// engines write, so calling one here re-burned the disc instead of checking it.
    /// </summary>
    private async Task VerifyDiscAsync(DriveCapabilities drive, IProgress<BurnProgress> progress)
    {
        var letter = DriveLetterOf(drive)
            ?? throw new InvalidOperationException($"No drive letter in '{drive.DevicePath}'.");

        await Task.Run(() =>
        {
            // Read the disc's own TOC rather than assuming it matches the source:
            // if the burn went wrong, the difference is exactly what we're after.
            var toc = DiscReader.ReadToc(letter);
            var plan = ReadPlanner.Plan(toc, drive);

            var track = plan.Tracks.FirstOrDefault(t => !t.IsAudio)
                ?? throw new InvalidDataException("The disc has no data track to verify.");

            using var source = OpenSourceUserData();
            long expected = source.Length;
            long onDisc = (long)track.LengthSectors * (int)track.SectorSize;

            if (onDisc < expected)
                throw new InvalidDataException(
                    $"The disc holds {onDisc:N0} bytes but the image is {expected:N0} — " +
                    "the burn is short.");

            // A burner may pad the last few sectors; compare only what we wrote.
            using var dev = new DiscSectorStream(letter, track.StartLba, (int)track.SectorSize);
            long same = CompareStreams(source, dev, expected, progress);

            if (same != expected)
                throw new InvalidDataException(
                    $"The disc differs from the image at byte {same:N0}.");
        });

        _log.Add("Verify: the disc matches the image byte for byte.", EventLogView.Level.Good);
    }

    /// <summary>The source's cooked user data, whether it's an ISO or a CDI.</summary>
    private Stream OpenSourceUserData()
    {
        if (_sourceIsIso) return File.OpenRead(_openCdi!);

        var fs = File.OpenRead(_openCdi!);
        var image = CdiParser.Parse(fs);
        var track = image.AllTracks.First(t => t.Mode != CdiTrackMode.Audio);
        return new CdiUserDataStream(fs, track);
    }

    /// <summary>Compare two streams, reporting progress. Returns bytes matched.</summary>
    private static long CompareStreams(Stream a, Stream b, long length,
                                       IProgress<BurnProgress> progress)
    {
        var ba = new byte[64 * 1024];
        var bb = new byte[64 * 1024];
        long done = 0;
        long lastReported = 0;

        while (done < length)
        {
            int want = (int)Math.Min(ba.Length, length - done);
            int na = a.ReadAtLeast(ba, want, throwOnEndOfStream: false);
            int nb = b.ReadAtLeast(bb, want, throwOnEndOfStream: false);
            if (na == 0 || nb == 0) break;

            int n = Math.Min(na, nb);
            for (int i = 0; i < n; i++)
                if (ba[i] != bb[i]) return done + i;

            done += n;

            // Report about every 32 MB, not every 64 KB block: a 2 GB verify
            // otherwise fires 32,768 progress reports, and each one used to
            // become a log line.
            if (done - lastReported >= 32L * 1024 * 1024 || done == length)
            {
                lastReported = done;
                progress.Report(new BurnProgress("verify", done / (double)length,
                    $"verified {done / (1024.0 * 1024.0):N0} MB of {length / (1024.0 * 1024.0):N0}"));
            }
        }
        return done;
    }

    private static char? DriveLetterOf(DriveCapabilities drive)
    {
        var path = drive.DevicePath;                 // \\.\E:
        int i = path.LastIndexOf(':');
        return i > 0 ? path[i - 1] : null;
    }

    /// <summary>Image-file destination: Write copies the image; Verify compares
    /// the copy against the source with CdiComparer (structure + per-track CRC).
    /// Works with no hardware at all.</summary>
    private async Task RunFileStepAsync(BurnStep step, string destPath)
    {
        if (_sourceIsCue)
            throw new NotSupportedException(
                "A CUE sheet describes a disc, not a single image file. Burn it to a " +
                "drive, or use the CLI's build-raw command to generate a raw image file.");

        if (step.Kind == BurnStepKind.Write)
        {
            // An ISO destined for a .cdi file must be wrapped, not just copied —
            // otherwise the result has no descriptor and isn't a CDI at all.
            if (_sourceIsIso)
            {
                await Task.Run(() =>
                {
                    using var os = File.Create(destPath);
                    IsoConverter.IsoToCdi(_openCdi!, CdiVersion.V35, os);
                });
                _log.Add($"Wrapped the ISO into {Path.GetFileName(destPath)}");
            }
            else
            {
                await Task.Run(() => File.Copy(_openCdi!, destPath, overwrite: true));
                _log.Add($"Wrote {Path.GetFileName(destPath)}");
            }
            return;
        }

        if (step.Kind == BurnStepKind.Verify)
        {
            if (_sourceIsIso)
            {
                // Compare the wrapped copy's data track against the source ISO.
                var same = await Task.Run(() =>
                {
                    using var b = File.OpenRead(destPath);
                    var ib = CdiParser.Parse(b);
                    using var extracted = new MemoryStream();
                    using var source = File.OpenRead(_openCdi!);
                    var track = ib.AllTracks.Single();
                    using var view = new CdiUserDataStream(b, track);
                    return StreamsEqual(source, view);
                });
                if (same) { _log.Add("Verify: the wrapped image matches the source ISO.", EventLogView.Level.Good); return; }
                throw new InvalidDataException("Verify failed: the wrapped image differs from the ISO.");
            }

            var report = await Task.Run(() =>
            {
                using var a = File.OpenRead(_openCdi!);
                using var b = File.OpenRead(destPath);
                var ia = CdiParser.Parse(a);
                var ib = CdiParser.Parse(b);
                return CdiComparer.Compare(a, ia, b, ib);
            });

            if (report.Equal)
            {
                _log.Add("Verify: images are equivalent (structure + CRC-32).", EventLogView.Level.Good);
                return;
            }

            foreach (var s in report.StructuralDifferences) _log.Add("  " + s, EventLogView.Level.Error);
            foreach (var t in report.TrackDifferences)
                _log.Add($"  track {t.TrackNumber} {t.Field}: {t.ValueA} vs {t.ValueB}", EventLogView.Level.Error);
            foreach (var n in report.ContentMismatchTracks)
                _log.Add($"  track {n} content differs", EventLogView.Level.Error);
            throw new InvalidDataException("Verify failed: images differ.");
        }
    }
}
