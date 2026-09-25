// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Core.Reading;
using DiscForge.Devices;
using DiscForge.Devices.Reading;

namespace DiscForge.App.Views;

/// <summary>
/// Read a disc to a CDI image — the other half of a disc tool. Pick a drive,
/// read its TOC, review the plan, then rip.
///
/// The decisions (what sector size per track, whether raw is forced) come from
/// the pure ReadPlanner in Core; this view collects the request and reports.
/// Sector modes are probed from the disc once per TOC read and handed to the
/// planner, because the TOC alone cannot distinguish Mode 1 from Mode 2 — and
/// that difference decides whether a cooked read is even possible.
/// </summary>
internal sealed class ReadView : UserControl
{
    private readonly ComboBox _drives = new()
    {
        Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 13),
    };
    private readonly CheckBox _raw = new()
    {
        Text = "Read data tracks raw (2352 bytes/sector)", AutoSize = true,
        Location = new Point(12, 48), Font = Theme.Ui,
    };
    private readonly CheckBox _continueOnError = new()
    {
        Text = "Continue past unreadable sectors (image will be incomplete)", AutoSize = true,
        Location = new Point(12, 70), Font = Theme.Ui,
    };
    private readonly CheckBox _jitter = new()
    {
        Text = "Correct audio jitter (slower, more accurate)", AutoSize = true,
        Location = new Point(12, 92), Font = Theme.Ui, Enabled = false,
    };
private readonly CheckBox _captureSub = new()
    {
        Text = "Also capture sub-channel to a .sub sidecar", AutoSize = true,
        Location = new Point(12, 114), Font = Theme.Ui,
    };
    // Escape hatch for discs DiscForge's own read path cannot get past (a GameCube disc on
    // an unmodified PC DVD drive is the known real case — its sector encoding needs
    // drive-vendor-specific commands that are reverse-engineered per drive chipset, which
    // isn't something to implement here without that exact hardware to develop against).
    // DiscForge never bundles or ships any such tool — this just remembers the path to one
    // the user already has, launches it, and can bring its finished image into the library
    // afterward. See docs/NEXT.md for the real diagnostic history behind this. There's a
    // second, identically-shaped button below (_externalDumpPs1) for PS1 discs via CloneCD.
    private readonly Button _externalDump = new()
    {
        Text = "Wii/GameCube discs (Rawdump2)…", Location = new Point(12, 140), Width = 220, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // Same escape hatch as _externalDump, for the other console family this project has seen
    // it asked for: PS1 discs via CloneCD (an established tool in the PS1 preservation
    // community, not something DiscForge bundles or knows anything about). Kept as a separate
    // button and a separate remembered path (Settings.ExternalDumperPathPs1) rather than
    // reusing _externalDump's, so having both tools set up doesn't make one overwrite the other.
    private readonly Button _externalDumpPs1 = new()
    {
        Text = "PS1 discs (CloneCD)…", Location = new Point(240, 140), Width = 170, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // Same shape again, for DVD via Xreveal (also known as DVD-Xreveal) — a CSS-aware DVD
    // backup tool, which is exactly the kind of thing DiscForge's own clean-room reading path
    // has no business reimplementing (see the clean-room provenance note on
    // ExternalDumperPathBluray below; the same reasoning applies here). Own remembered path
    // (ExternalDumperPathDvd), same reason as PS1/GameCube: one tool's setting shouldn't
    // clobber another's.
    private readonly Button _externalDumpDvd = new()
    {
        Text = "DVD discs (Xreveal)…", Location = new Point(418, 140), Width = 160, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // General-purpose disc utility, not tied to one console/format the way the buttons above
    // are — nobody reported DiscForge failing to read anything IsoBuster is aimed at, it's just
    // a tool the user already has and asked to wire up the same way. Tucked onto the end of
    // row 1 since it's a small button and there's room; see ExternalToolLauncher for the shared
    // launch logic every button on this view (and Burn's own external-tool row) now uses.
    private readonly Button _externalDumpIsoBuster = new()
    {
        Text = "IsoBuster…", Location = new Point(586, 140), Width = 110, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // Same shape again, for Blu-ray via CloneBD. Neither Xreveal nor CloneBD is bundled,
    // inspected, or understood by DiscForge — same posture as every other button on this row:
    // DiscForge starts the process the user points it at and nothing more. Deliberately not
    // something to reimplement here: both commercial tools exist specifically to handle
    // industry copy protection (CSS/AACS-class schemes) that this project's clean-room,
    // detect-but-never-circumvent design explicitly stays out of.
    private readonly Button _externalDumpBluray = new()
    {
        Text = "Blu-ray discs (CloneBD)…", Location = new Point(12, 170), Width = 190, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly Button _importExternal = new()
    {
        Text = "Import from external tool…", Location = new Point(210, 170), Width = 190, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // DVDFab: the all-in-one version of what Xreveal and CloneBD already do separately — its
    // main job is ripping/copying protected DVDs AND Blu-rays to an image, covering both formats
    // in one tool rather than two. Added alongside those two, not in place of them, since someone
    // may still prefer a lighter single-purpose tool for a specific disc. Own remembered path
    // (ExternalDumperPathDvdFab) for the same reason every other button here has one: so setting
    // up one tool never clobbers another's.
    private readonly Button _externalDumpDvdFab = new()
    {
        Text = "DVDFab…", Location = new Point(408, 170), Width = 110, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // Fills the gap right after _externalDump's job ends: Rawdump2 pulls a raw Wii dump off the
    // drive, but DiscForge's own Wii support only reads the header/partition table — it never
    // decrypts a Wii disc — so it can't turn that raw dump into a scrubbed, verifiable ISO the
    // way Wiimms ISO Tools can. Sits on row 2 since there's room after DVDFab; own remembered
    // path (ExternalDumperPathWit) for the same reason every button here has one.
    private readonly Button _externalDumpWit = new()
    {
        Text = "Wiimms ISO Tools…", Location = new Point(530, 170), Width = 180, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // ImgBurn, Alcohol 120%, and DAEMON Tools used to live here too, but they're burn/mount
    // tools rather than rippers — burning is Burn's job, not Read's — so as of the version that
    // added this comment they moved to BurnView's own external-tool row instead. See BurnView.cs.
    //
    // Deliberately generic rather than named after one product (unlike every button above it):
    // added after a request to wire up AnyDVD specifically, which this project declined — AnyDVD's
    // entire purpose is stripping copy protection (CSS/AACS/region) system-wide, which is a
    // different thing from the rippers above (they read/image a disc; protection handling is
    // incidental to that job for the ones that need it). This button carries no assumption about
    // what the user points it at, same launch-and-forget posture as every other button here, for
    // whatever disc-reading tool isn't already covered by a named button above.
    private readonly Button _externalDumpOther = new()
    {
        Text = "Other tool…", Location = new Point(12, 200), Width = 200, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    // The two dumpers Redump actually asks submissions to come from: redumper (the low-level
    // dumper) and MPF (the SabreTools front-end that drives it and fills in the submission).
    // Both are open source and read the disc as-is — nothing here strips protection — and the
    // Submit screen and the log importer were already built around their output, so these were
    // the most conspicuous gap on this row. "Import from external tool…" reads a redumper or
    // DiscImageCreator .log sitting beside the imported image.
    private readonly Button _externalDumpRedumper = new()
    {
        Text = "redumper…", Location = new Point(220, 200), Width = 130, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly Button _externalDumpMpf = new()
    {
        Text = "MPF (Redump front-end)…", Location = new Point(358, 200), Width = 190, Height = 26,
        FlatStyle = FlatStyle.System,
    };
    private readonly ListView _tracks = new()
    {
        // Sits below the four option checkboxes and the three external-tool rows (which end near
        // Y=226); a grid any higher overlaps them.
        Location = new Point(12, 236), Size = new Size(712, 100),
        View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Font = Theme.Ui, BackColor = Color.White,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Button _rip = new()
    {
        Text = "Read to CDI…", Location = new Point(12, 346), Width = 110, Height = 28,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(132, 349), Size = new Size(592, 22), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly EventLogView _log = new()
    {
        Location = new Point(12, 382), Size = new Size(712, 170),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private DiscToc? _toc;
    private bool _suppressPlanRebuild;
    private ReadPlan? _plan;

    /// <summary>What each track's sectors actually are, probed once per TOC read.
    /// Cached because toggling a checkbox shouldn't send the drive back to the
    /// disc — and because the answer cannot change without the disc changing.</summary>
    private IReadOnlyDictionary<int, TrackSectorMode>? _modes;

    public ReadView()
    {
        // Establish a realistic size before adding anchored children (see InspectView).
        Size = new Size(736, 566);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });

        var detect = new Button { Text = "Detect", Location = new Point(378, 12), Width = 74, FlatStyle = FlatStyle.System };
        detect.Click += async (_, _) => await DetectAsync();

        var readToc = new Button { Text = "Read TOC", Location = new Point(458, 12), Width = 84, FlatStyle = FlatStyle.System };
        readToc.Click += async (_, _) => await ReadTocAsync();

        _raw.CheckedChanged += (_, _) => { if (_toc is not null && !_suppressPlanRebuild) BuildPlan(); };

        foreach (var (name, w) in new[]
        {
            ("Track", 60), ("Type", 130), ("Start LBA", 90), ("Sectors", 100), ("Size", 100), ("Stored", 120),
        })
            _tracks.Columns.Add(name, w);

        _rip.Click += async (_, _) => await RipAsync();
        _externalDump.Click += (_, _) => LaunchExternalDumper();
        _externalDumpPs1.Click += (_, _) => LaunchExternalDumperPs1();
        _externalDumpDvd.Click += (_, _) => LaunchExternalDumperDvd();
        _externalDumpBluray.Click += (_, _) => LaunchExternalDumperBluray();
        _externalDumpIsoBuster.Click += (_, _) => LaunchExternalDumperIsoBuster();
        _externalDumpDvdFab.Click += (_, _) => LaunchExternalDumperDvdFab();
        _externalDumpWit.Click += (_, _) => LaunchExternalDumperWit();
        _externalDumpOther.Click += (_, _) => LaunchExternalDumperOther();
        // redumper is command-line only: opened in a console that stays up, showing its usage, so
        // its output doesn't vanish the moment a bare launch finishes.
        _externalDumpRedumper.Click += (_, _) => ExternalToolLauncher.Launch(
            () => Settings.ExternalDumperPathRedumper,
            p => Settings.ExternalDumperPathRedumper = p,
            "Locate redumper (redumper.exe)",
            "A console has opened in redumper's folder with its usage shown — run your dump there " +
            "(e.g. redumper --drive=E: --image-name=mygame). When it's done, use \"Import from " +
            "external tool…\" and pick the image; its .log beside it is read automatically.",
            _log,
            path => ExternalToolLauncher.ConsoleWindow(path, "--help"));
        _externalDumpMpf.Click += (_, _) => LaunchExternalTool(
            () => Settings.ExternalDumperPathMpf,
            p => Settings.ExternalDumperPathMpf = p,
            "Locate MPF (MPF.UI.exe)");
        _importExternal.Click += async (_, _) => await ImportExternalDumpAsync();

        Controls.Add(_drives); Controls.Add(detect); Controls.Add(readToc);
        Controls.Add(_raw);
        Controls.Add(_continueOnError);
        Controls.Add(_jitter);
        Controls.Add(_captureSub);
        Controls.Add(_externalDump); Controls.Add(_externalDumpPs1);
        Controls.Add(_externalDumpDvd); Controls.Add(_externalDumpIsoBuster);
        Controls.Add(_externalDumpBluray); Controls.Add(_importExternal); Controls.Add(_externalDumpDvdFab);
        Controls.Add(_externalDumpWit);
        Controls.Add(_externalDumpOther);
        Controls.Add(_externalDumpRedumper); Controls.Add(_externalDumpMpf);
        Controls.Add(_tracks);
        Controls.Add(_rip); Controls.Add(_progress);
        Controls.Add(_log);

        _log.Add("Detect a drive, insert a disc, then read its TOC.");
    }

    private async Task DetectAsync()
    {
        _drives.Items.Clear();
        _log.Add("Detecting drives…");
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            foreach (var d in _detected)
            {
                _drives.Items.Add(d.Summary());
                // Record the full capability picture: it's the first thing needed
                // to diagnose a read that misbehaves.
                AppLog.Write($"  drive {d.DevicePath}: '{d.Vendor}' '{d.Model}' fw '{d.FirmwareRevision}' " +
                             $"CD r/w={d.CdRead}/{d.CdWrite} DVD r/w={d.DvdRead}/{d.DvdWrite} " +
                             $"BD r/w={d.BdRead}/{d.BdWrite} rawDAO96={d.RawDao96} media={d.MediaProfile}");
            }
            if (_drives.Items.Count > 0)
            {
                _drives.SelectedIndex = 0;
                _log.Add($"{_detected.Count} drive(s) detected.", EventLogView.Level.Good);
            }
            else
            {
                _log.Add("No optical drives detected (raw access usually needs administrator).",
                    EventLogView.Level.Warn);
            }
        }
        catch (Exception ex)
        {
            _log.Add("Detection failed: " + ex.Message, EventLogView.Level.Error);
        }
    }

    private char? SelectedDriveLetter()
    {
        if (_drives.SelectedIndex < 0 || _drives.SelectedIndex >= _detected.Count) return null;
        var path = _detected[_drives.SelectedIndex].DevicePath;   // e.g. \\.\D:
        var idx = path.LastIndexOf(':');
        return idx > 0 ? path[idx - 1] : null;
    }

    private async Task ReadTocAsync()
    {
        var letter = SelectedDriveLetter();
        if (letter is null) { _log.Add("Detect and select a drive first.", EventLogView.Level.Warn); return; }

        _tracks.Items.Clear();
        _rip.Enabled = false;
        _modes = null;
        _log.Add($"Reading TOC from {letter}:…");
        try
        {
            _toc = await Task.Run(() => DiscReader.ReadToc(letter.Value));
            var media = _detected[_drives.SelectedIndex];
            _log.Add($"Media: {media.MediaProfile}");
            _log.Add($"TOC: tracks {_toc.FirstTrack}–{_toc.LastTrack}, lead-out at LBA {_toc.LeadOutLba:N0}.",
                EventLogView.Level.Good);

            // Ask the disc what its sectors actually are. The TOC's control nibble
            // separates audio from data and nothing more, so without this a Mode 2
            // Form 2 track — SVCD, VCD, CD-i — looks identical to plain Mode 1 and
            // gets planned as a cooked read the drive will refuse outright.
            if (!media.MediaIsDvdOrBd)
            {
                var toc = _toc;
                _modes = await Task.Run(() => TrackModeProber.Probe(letter.Value, toc));
                foreach (var kv in _modes)
                    AppLog.Write($"    probe: track {kv.Key} sectors are {kv.Value}");
            }

            bool anyForm2 = _modes is not null &&
                            _modes.Values.Any(m => m == TrackSectorMode.Mode2Form2);

            // Raw 2352 sectors are a CD concept; DVD/BD have no raw form.
            // Adjusting the checkbox here fires CheckedChanged, whose handler
            // also calls BuildPlan — suppress it so the plan is built (and
            // logged) exactly once per TOC read.
            _suppressPlanRebuild = true;
            try
            {
                if (media.MediaIsDvdOrBd)
                {
                    _raw.Checked = false;
                    _raw.Enabled = false;
                    _raw.Text = "Read data tracks raw (CD only — not available for DVD/BD)";
                }
                else if (anyForm2 || _toc.HasAudio)
                {
                    // Not a preference on this disc: there is no 2048-byte user
                    // data to read. Tick it, lock it, and say why.
                    _raw.Checked = true;
                    _raw.Enabled = false;
                    _raw.Text = anyForm2
                        ? "Read data tracks raw (required — disc has Mode 2 Form 2 tracks)"
                        : "Read data tracks raw (required — disc has audio tracks)";
                }
                else
                {
                    _raw.Enabled = true;
                    _raw.Text = "Read data tracks raw (2352 bytes/sector)";
                }
            }
            finally { _suppressPlanRebuild = false; }

            if (_toc.IsMixedMode) _log.Add("Mixed-mode disc (audio + data).", EventLogView.Level.Warn);
            BuildPlan();
        }
        catch (Exception ex)
        {
            _toc = null;
            _modes = null;
            _log.Add("Read TOC failed: " + ex.Message, EventLogView.Level.Error);
        }
    }

    /// <summary>Human label for the Type column: what the track is, and — where it
    /// differs from the obvious — what its sectors turned out to be.</summary>
    private static string DescribeType(ReadTrackPlan t) => t.IsAudio
        ? "Audio"
        : t.Detected switch
        {
            TrackSectorMode.Mode1 => "Data (Mode 1)",
            TrackSectorMode.Mode2Form1 => "Data (Mode 2 Form 1)",
            TrackSectorMode.Mode2Form2 => "Data (Mode 2 Form 2)",
            _ => "Data",
        };

    private void BuildPlan()
    {
        if (_toc is null) return;
        var drive = _detected[_drives.SelectedIndex];
        _tracks.Items.Clear();
        try
        {
            _plan = ReadPlanner.Plan(_toc, drive, _raw.Checked, _modes);
            AppLog.Write($"  plan: raw={_plan.RawMode} rawRequested={_raw.Checked} " +
                         $"rawRequired={_plan.RawRequired} " +
                         $"tracks={_plan.Tracks.Count} totalBytes={_plan.TotalBytes:N0}");
            foreach (var t in _plan.Tracks)
                AppLog.Write($"    track {t.Number}: {(t.IsAudio ? "audio" : "data")} " +
                             $"lba={t.StartLba} sectors={t.LengthSectors} " +
                             $"sectorSize={(int)t.SectorSize} mode={t.Mode} detected={t.Detected}");
            foreach (var t in _plan.Tracks)
            {
                var item = new ListViewItem(t.Number.ToString());
                item.SubItems.Add(DescribeType(t));
                item.SubItems.Add(t.StartLba.ToString("N0"));
                item.SubItems.Add(t.LengthSectors.ToString("N0"));
                item.SubItems.Add($"{(int)t.SectorSize} b/sector");
                item.SubItems.Add($"{t.StoredBytes / (1024.0 * 1024.0):N1} MB");
                _tracks.Items.Add(item);
            }
            bool anyAudio = _plan.Tracks.Any(t => t.IsAudio);
            _jitter.Enabled = anyAudio;
            if (!anyAudio)
            {
                _jitter.Checked = false;
                _jitter.Text = "Correct audio jitter (audio discs only)";
            }
            else
            {
                _jitter.Text = "Correct audio jitter (slower, more accurate)";
            }

            foreach (var w in _plan.Warnings) _log.Add(w, EventLogView.Level.Warn);
            _log.Add($"Plan: {_plan.Tracks.Count} track(s), {_plan.TotalBytes / (1024.0 * 1024.0):N1} MB total" +
                     (_plan.RawMode ? " (raw)" : " (cooked)"));
            _rip.Enabled = true;
        }
        catch (ReadNotSupportedException ex)
        {
            _plan = null;
            _log.Add("Cannot read this disc: " + ex.Message, EventLogView.Level.Error);
        }
    }

    private async Task RipAsync()
    {
        var letter = SelectedDriveLetter();
        if (letter is null || _plan is null) return;

        // Pre-flight: try one sector of each track. Finding out the drive can't
        // honour the plan takes a moment now, versus a failed part-file later.
        _log.Add("Testing the drive can read this disc as planned…");
        var problem = await Task.Run(() => DiscReader.Probe(letter.Value, _plan));
        if (problem is not null)
        {
            _log.Add(problem, EventLogView.Level.Error);
            return;
        }
        _log.Add("Test read OK.", EventLogView.Level.Good);

        using var save = new SaveFileDialog { Filter = "CDI image (*.cdi)|*.cdi", FileName = "disc.cdi" };
        if (save.ShowDialog() != DialogResult.OK) return;

        _rip.Enabled = false;
        _progress.Value = 0;
        _log.Add($"Reading to {Path.GetFileName(save.FileName)}…");

        // A DVD-Shrink-style window carries the live readout — stage, transfer
        // rate, sectors done and time remaining — and its Cancel stops the read.
        var dlg = new RipProgressDialog($"Reading {Path.GetFileName(save.FileName)}");
        dlg.FormClosed += (_, _) => dlg.Dispose();
        using var cts = new CancellationTokenSource();
        dlg.CancelRequested += () => cts.Cancel();
        dlg.Show(this);                 // create the window (and its handle) first
        dlg.SetTitleBase("Reading disc");
        bool ok = false;

        // Progress is reported per track, so map each track number to how many
        // sectors precede it — otherwise the bar restarts on every track.
        var offsets = new Dictionary<int, uint>();
        uint acc = 0;
        foreach (var t in _plan.Tracks) { offsets[t.Number] = acc; acc += t.LengthSectors; }
        uint totalSectors = Math.Max(1u, acc);
        double totalBytes = Math.Max(1.0, _plan.TotalBytes);
        long startTick = Environment.TickCount64;

        var progress = new Progress<ReadProgress>(p =>
        {
            uint overall = (offsets.TryGetValue(p.TrackNumber, out var before) ? before : 0) + p.SectorsDone;
            double frac = Math.Clamp((double)overall / totalSectors, 0, 1);
            int pct = (int)(100.0 * frac);
            _progress.Value = pct;
            dlg.SetPercent(pct);
            double secs = (Environment.TickCount64 - startTick) / 1000.0;
            dlg.SetStats(
                string.IsNullOrEmpty(p.Detail) ? "Reading" : p.Detail,
                Rate(frac * totalBytes, secs),
                $"{overall:N0} / {totalSectors:N0}",
                Eta(frac, secs));
        });

        try
        {
            var plan = _plan;
            var file = save.FileName;
            var drv = letter.Value;
            var ct = cts.Token;

            var options = new ReadOptions
            {
                ContinueOnError = _continueOnError.Checked,
                CorrectJitter = _jitter.Checked,
            };

            // Write to a .partial file and only name it .cdi once the trailer is
            // written. A CDI's version magic lives at EOF, so a rip that dies
            // part-way leaves a file that LOOKS like an image but has no trailer
            // — and every tool, ours included, then reports a confusing
            // "not a CDI image" for what is really a truncated read.
            var partial = file + ".partial";

            ReadReport report;
            try
            {
                report = await Task.Run(() =>
                {
                    using var os = File.Create(partial);
                    return DiscReader.ReadToCdi(drv, plan, CdiVersion.V35, os, progress, options, ct);
                }, ct);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            if (File.Exists(file)) File.Delete(file);
            File.Move(partial, file);

            _progress.Value = 100;
// Sub-channel is a second pass, after the image is safely written.
            // Some discs carry meaning there that the main data does not —
            // LibCrypt corrupts specific Q frames deliberately, and software
            // checks those exact positions — so an image without the sidecar can
            // be byte-perfect and still refuse to run.
            //
            // A drive that won't return raw P–W is a refusal rather than a
            // failure: the image is already complete and unaffected.
            if (_captureSub.Checked)
            {
                _log.Add("Reading sub-channel…");
                dlg.SetTitleBase("Reading sub-channel");
                try
                {
                    long subStart = Environment.TickCount64;
                    var subProgress = new Progress<double>(f =>
                    {
                        int sp = Math.Clamp((int)(f * 100), 0, 100);
                        _progress.Value = sp;
                        dlg.SetPercent(sp);
                        double ss = (Environment.TickCount64 - subStart) / 1000.0;
                        dlg.SetStats("Sub-channel", "—",
                            $"{(uint)(f * totalSectors):N0} / {totalSectors:N0}", Eta(f, ss));
                    });

                    var capture = await Task.Run(() =>
                        SubchannelCapture.Capture(drv, file, 0, totalSectors, subProgress, ct));

                    if (capture is null)
                    {
                        _log.Add("This drive will not return raw sub-channel, so no sidecar was " +
                                 "written. The image itself is unaffected.", EventLogView.Level.Warn);
                    }
                    else
                    {
                        _log.Add(SubchannelCapture.Describe(capture),
                            capture.Analysis.LooksLikeLibCrypt
                                ? EventLogView.Level.Warn
                                : EventLogView.Level.Good);

                        if (!capture.Complete)
                            _log.Add($"{capture.SectorsRefused:N0} sector(s) of sub-channel could " +
                                     "not be read and are zeroed in the sidecar.",
                                EventLogView.Level.Warn);

                        AppLog.Write($"  subchannel: {capture.SectorsWritten:N0} frames, " +
                                     $"{capture.Analysis.QInvalid} invalid Q, " +
                                     $"libcrypt={capture.Analysis.LooksLikeLibCrypt}");

                        // When the disc carries LibCrypt, also write the compact,
                        // emulator-ready .sbi beside the image — the same
                        // preservation data as the .sub, in the portable form.
                        if (capture.Analysis.LooksLikeLibCrypt)
                            TryWriteSbi(capture.Path, file);
                    }
                }
                catch (Exception ex)
                {
                    _log.Add("Sub-channel capture failed: " + ex.Message + " — the image is " +
                             "complete regardless.", EventLogView.Level.Warn);
                    AppLog.WriteException("subchannel capture", ex);
                }
                _progress.Value = 100;
            }

            foreach (var note in report.Notes) AppLog.Write("    " + note);

            if (report.Complete)
            {
                _log.Add($"Read complete: {Path.GetFileName(file)}", EventLogView.Level.Good);
                _log.Add("Every sector read cleanly. Tip: run Inspect ▸ Verify to CRC-check it.");
            }
            else if (report.CompleteExceptBoundaries)
            {
                // Pregap and run-out sectors are padding: the drive won't position
                // against them on some hardware. Distinguish that from damage,
                // because the payload here is intact and the image is usable.
                _log.Add($"Read complete: {Path.GetFileName(file)}", EventLogView.Level.Good);
                _log.Add($"{report.BoundarySectors.Count:N0} track-boundary sector(s) could not be " +
                         "positioned and were zero-filled. These are pregap or run-out padding, " +
                         "not disc damage — the data is intact.", EventLogView.Level.Warn);
                foreach (var lba in report.BoundarySectors.Take(20))
                    _log.Add($"  boundary sector at LBA {lba:N0}", EventLogView.Level.Warn);
            }
            else
            {
                // Be blunt: a partial image must never look like a clean one.
                _log.Add($"Read finished with {report.BadSectors.Count:N0} UNREADABLE sector(s).",
                    EventLogView.Level.Error);
                _log.Add("This image is INCOMPLETE — unreadable sectors were zero-filled.",
                    EventLogView.Level.Error);
                foreach (var lba in report.BadSectors.Take(20))
                    _log.Add($"  bad sector at LBA {lba:N0}", EventLogView.Level.Warn);
                if (report.BadSectors.Count > 20)
                    _log.Add($"  … and {report.BadSectors.Count - 20:N0} more", EventLogView.Level.Warn);
            }

            // Persist the unreadable-sector map beside the image so the holes travel with the dump — through a
            // bin/cue conversion and into the preservation master. A checksum can't reveal them (a zero-filled
            // hole hashes like data), so without this sidecar the incompleteness would be lost the moment capture
            // ended. Written whenever anything was zero-filled, including boundary-only holes.
            if (report.BadSectors.Count > 0)
            {
                try
                {
                    var map = new DiscForge.Core.Preservation.BadSectorMap
                    {
                        Image = Path.GetFileName(file),
                        TotalSectors = (int)report.SectorsRead,
                        UnreadableLba = report.BadSectors.Select(x => (long)x).ToList(),
                        BoundaryLba = report.BoundarySectors.Select(x => (long)x).ToList(),
                        Note = report.CompleteExceptBoundaries ? "holes are track-boundary padding only" : null,
                    };
                    var sidecar = DiscForge.Core.Preservation.BadSectorMap.SidecarPath(file);
                    map.Save(sidecar);
                    _log.Add($"Wrote {Path.GetFileName(sidecar)} — the unreadable-sector map now travels with the dump.");
                }
                catch (Exception ex) { AppLog.WriteException("bad-sector sidecar", ex); }
            }

            ok = true;
        }
        catch (OperationCanceledException)
        {
            _log.Add("Read cancelled — no image was written.", EventLogView.Level.Warn);
        }
        catch (Exception ex)
        {
            _log.Add("Read failed: " + ex.Message, EventLogView.Level.Error);
            _log.Add("No image was written — the incomplete data has been discarded.",
                EventLogView.Level.Warn);
            AppLog.WriteException("disc read", ex);
        }
        finally
        {
            dlg.Finish(ok);
            _rip.Enabled = true;
        }
    }

    /// <summary>
    /// Launch a user-supplied external dumping tool (asked for once, then remembered) — the
    /// escape hatch for discs whose sector encoding no generic SCSI/MMC command DiscForge issues
    /// can get past on this drive (a GameCube disc on an unmodified PC DVD drive is the known
    /// real case: two separate, legitimate recovery attempts were tried here and neither got
    /// past it — see docs/NEXT.md). DiscForge does not bundle, invoke undocumented commands
    /// for, or know anything about what the external tool does; it only starts the process the
    /// user points it at and lets that tool's own window take it from there.
    /// </summary>
    private void LaunchExternalDumper() => LaunchExternalTool(
        () => Settings.ExternalDumperPath,
        p => Settings.ExternalDumperPath = p,
        "Locate Rawdump2 (or your Wii/GameCube dumping tool)");

    /// <summary>
    /// Same idea as <see cref="LaunchExternalDumper"/>, for PS1 discs via CloneCD (or whatever
    /// the user actually points this at — DiscForge doesn't check or care which tool it is,
    /// same as the GameCube button). Kept as its own method/remembered path rather than folded
    /// into <see cref="LaunchExternalDumper"/> so the two tools don't clobber each other's
    /// setting when a user has both configured.
    /// </summary>
    private void LaunchExternalDumperPs1() => LaunchExternalTool(
        () => Settings.ExternalDumperPathPs1,
        p => Settings.ExternalDumperPathPs1 = p,
        "Locate CloneCD (or your PS1 dumping tool)");

    /// <summary>
    /// Same idea again, for DVD discs via Xreveal — a commercial tool built specifically to
    /// handle CSS-protected DVDs, which this project's clean-room, detect-but-never-circumvent
    /// design deliberately does not reimplement itself. DiscForge treats it exactly like every
    /// other button here: launch what the user points it at, know nothing else about it.
    /// </summary>
    private void LaunchExternalDumperDvd() => LaunchExternalTool(
        () => Settings.ExternalDumperPathDvd,
        p => Settings.ExternalDumperPathDvd = p,
        "Locate Xreveal (or your DVD dumping tool)");

    /// <summary>
    /// Same idea again, for Blu-ray discs via CloneBD — same reasoning as
    /// <see cref="LaunchExternalDumperDvd"/>, one rung up the copy-protection ladder (AACS-class
    /// schemes instead of CSS). Kept as its own remembered path
    /// (<see cref="Settings.ExternalDumperPathBluray"/>) so configuring it doesn't disturb the
    /// GameCube/PS1/DVD paths, same as every other tool on this row.
    /// </summary>
    private void LaunchExternalDumperBluray() => LaunchExternalTool(
        () => Settings.ExternalDumperPathBluray,
        p => Settings.ExternalDumperPathBluray = p,
        "Locate CloneBD (or your Blu-ray dumping tool)");

    /// <summary>
    /// Same idea again, for IsoBuster — a general-purpose optical-disc utility (CD/DVD/BD and
    /// several less common formats), not tied to one console/format the way the four buttons
    /// above are. Nothing here diagnosed a DiscForge failure IsoBuster is working around; it's
    /// wired up the same way purely so a user who already trusts it can reach for it from here.
    /// </summary>
    private void LaunchExternalDumperIsoBuster() => LaunchExternalTool(
        () => Settings.ExternalDumperPathIsoBuster,
        p => Settings.ExternalDumperPathIsoBuster = p,
        "Locate IsoBuster (or your general disc-dumping tool)");

    /// <summary>
    /// Same idea again, for DVDFab — the all-in-one version of what <see cref="LaunchExternalDumperDvd"/>
    /// (Xreveal) and <see cref="LaunchExternalDumperBluray"/> (CloneBD) already do separately:
    /// ripping/copying protected DVDs AND Blu-rays to an image, one tool covering both formats.
    /// Added alongside those two rather than replacing either — someone may still prefer a
    /// lighter single-purpose tool for a specific disc. Own remembered path
    /// (<see cref="Settings.ExternalDumperPathDvdFab"/>) so configuring it doesn't disturb them.
    /// </summary>
    private void LaunchExternalDumperDvdFab() => LaunchExternalTool(
        () => Settings.ExternalDumperPathDvdFab,
        p => Settings.ExternalDumperPathDvdFab = p,
        "Locate DVDFab (or your general DVD/Blu-ray dumping tool)");

    /// <summary>
    /// Same idea again, for Wiimms ISO Tools (WIT) — the natural next step after
    /// <see cref="LaunchExternalDumper"/> (Rawdump2) for a Wii disc: DiscForge's own Wii support
    /// only reads the header/partition table and never decrypts a disc, so WIT is what actually
    /// turns a raw Rawdump2 dump into a scrubbed, verifiable ISO. Own remembered path
    /// (<see cref="Settings.ExternalDumperPathWit"/>) so configuring it doesn't disturb Rawdump2's.
    /// </summary>
    private void LaunchExternalDumperWit() => LaunchExternalTool(
        () => Settings.ExternalDumperPathWit,
        p => Settings.ExternalDumperPathWit = p,
        "Locate Wiimms ISO Tools (wit.exe)");

    /// <summary>
    /// Same idea again, but deliberately not named after one product — a general "point this at
    /// whatever disc-reading tool you use" escape hatch for anything not already covered by a
    /// named button on this row. Own remembered path
    /// (<see cref="Settings.ExternalDumperPathOther"/>) so configuring it doesn't disturb any of
    /// the named tools' paths. Not a place for a tool whose sole purpose is stripping copy
    /// protection system-wide (see the comment on <see cref="_externalDumpOther"/>) — this is for
    /// disc-reading/imaging tools, same posture as every other button here.
    /// </summary>
    private void LaunchExternalDumperOther() => LaunchExternalTool(
        () => Settings.ExternalDumperPathOther,
        p => Settings.ExternalDumperPathOther = p,
        "Locate your disc-reading tool");

    /// <summary>
    /// Launch a user-supplied external dumping tool (asked for once via <paramref name="getPath"/>/
    /// <paramref name="setPath"/>, then remembered) — the escape hatch for discs whose sector
    /// encoding no generic SCSI/MMC command DiscForge issues can get past on this drive (a
    /// GameCube disc on an unmodified PC DVD drive is the known real case: two separate,
    /// legitimate recovery attempts were tried here and neither got past it — see
    /// docs/NEXT.md). Delegates to <see cref="ExternalToolLauncher"/>, the same shared logic
    /// BurnView's own external-tool row uses — DiscForge does not bundle, invoke undocumented
    /// commands for, or know anything about what the external tool does; it only starts the
    /// process the user points it at and lets that tool's own window take it from there.
    /// </summary>
    private void LaunchExternalTool(Func<string?> getPath, Action<string?> setPath, string pickerTitle) =>
        ExternalToolLauncher.Launch(getPath, setPath, pickerTitle,
            "Run the dump in its own window; when it's done, come back here and use " +
            "\"Import from external tool…\" to bring the finished image into your library.",
            _log);

    /// <summary>
    /// Copy what an external tool produced into wherever the user keeps their library. Picking a
    /// <c>.cue</c> or <c>.gdi</c> brings the whole set — the sheet, every track file it names, and the
    /// dumper's log and subchannel sidecars — into a chosen folder (redumper, for one, writes a
    /// .bin per track); picking a lone image copies just that (plus its log). DiscForge did not read
    /// this disc itself — it's a plain file copy, not a rip — so the log says as much, and any
    /// verification comes from the dumper's own log (see <see cref="ReportDumpLogAsync"/>).
    /// </summary>
    private async Task ImportExternalDumpAsync()
    {
        using var open = new OpenFileDialog
        {
            Title = "Select the image — or the .cue/.gdi — the external tool produced",
            Filter = "Disc image or sheet (*.cue;*.gdi;*.iso;*.cdi;*.bin;*.img)|*.cue;*.gdi;*.iso;*.cdi;*.bin;*.img|All files (*.*)|*.*",
        };
        if (open.ShowDialog() != DialogResult.OK) return;

        DiscForge.Core.Dumping.DumpSet set;
        try { set = DiscForge.Core.Dumping.DumpSet.Resolve(open.FileName); }
        catch (Exception ex)
        {
            _log.Add($"Could not read {Path.GetFileName(open.FileName)}: {ex.Message}", EventLogView.Level.Error);
            return;
        }
        foreach (var m in set.Missing)
            _log.Add($"The sheet names {Path.GetFileName(m)}, which isn't there — the set is incomplete.", EventLogView.Level.Warn);

        string destDir;
        if (set.Files.Count == 1)
        {
            using var save = new SaveFileDialog
            {
                Title = "Save into your library as",
                Filter = "Same as source (*.*)|*.*",
                FileName = Path.GetFileName(open.FileName),
            };
            if (save.ShowDialog() != DialogResult.OK) return;
            try
            {
                _log.Add($"Copying {Path.GetFileName(open.FileName)} into your library…");
                await Task.Run(() => File.Copy(open.FileName, save.FileName, overwrite: true));
                _log.Add($"Imported {Path.GetFileName(save.FileName)}. This came from an external " +
                         "tool, not a DiscForge read, so nothing here has verified it yet — run " +
                         "Inspect ▸ Verify on it before relying on it.", EventLogView.Level.Good);
            }
            catch (Exception ex)
            {
                _log.Add($"Import failed: {ex.Message}", EventLogView.Level.Error);
                AppLog.WriteException("import external dump", ex);
                return;
            }
            await ReportDumpLogAsync(set, Path.GetDirectoryName(Path.GetFullPath(save.FileName))!);
            return;
        }

        using (var folder = new FolderBrowserDialog
        {
            Description = $"Copy the {set.Files.Count}-file dump set into…",
            UseDescriptionForTitle = true,
        })
        {
            if (folder.ShowDialog() != DialogResult.OK) return;
            destDir = folder.SelectedPath;
        }
        if (string.Equals(Path.GetFullPath(destDir).TrimEnd('\\'),
                          Path.GetDirectoryName(set.Primary)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            _log.Add("That's the folder the dump is already in — nothing to copy.", EventLogView.Level.Warn);
            await ReportDumpLogAsync(set, destDir);
            return;
        }

        try
        {
            long total = set.Files.Sum(f => new FileInfo(f).Length);
            _log.Add($"Copying {set.Files.Count} files ({total / (1024.0 * 1024.0):0.0} MB) into {destDir}…");
            await Task.Run(() =>
            {
                foreach (var f in set.Files)
                    File.Copy(f, Path.Combine(destDir, Path.GetFileName(f)), overwrite: true);
            });
            _log.Add($"Imported {Path.GetFileName(set.Primary)} and its {set.Files.Count - 1} companion file(s). " +
                     "This came from an external tool, not a DiscForge read — run Inspect ▸ Verify on it " +
                     "before relying on it.", EventLogView.Level.Good);
        }
        catch (Exception ex)
        {
            _log.Add($"Import failed: {ex.Message}", EventLogView.Level.Error);
            AppLog.WriteException("import external dump set", ex);
            return;
        }
        await ReportDumpLogAsync(set, destDir);
    }

    /// <summary>
    /// If the dumper left a log beside the dump (redumper writes <c>&lt;name&gt;.log</c>; so does a
    /// DiscImageCreator run driven by MPF), read it and say what it recorded: tool version, drive,
    /// error counts. For a redumper log, whose <c>dat:</c> block carries a SHA-1 per file, every
    /// imported file the dat names is hashed (the copy, in <paramref name="importedDir"/>) and checked
    /// against its entry — a real check of the copies against what the dumper itself recorded, not a
    /// claim the log is right about the disc. Best-effort: a missing or unreadable log just means
    /// nothing extra is reported.
    /// </summary>
    private async Task ReportDumpLogAsync(DiscForge.Core.Dumping.DumpSet set, string importedDir)
    {
        try
        {
            string? logPath = set.Log;
            if (logPath is null) return;
            string text = await File.ReadAllTextAsync(logPath);

            if (DiscForge.Core.Dumping.RedumperLogParser.LooksLikeRedumperLog(text))
            {
                var r = DiscForge.Core.Dumping.RedumperLogParser.ParseText(text, logPath);
                _log.Add($"Found {Path.GetFileName(logPath)}: {r.Summary()}.",
                    r.LooksClean ? EventLogView.Level.Info : EventLogView.Level.Warn);
                if (r.WriteOffset is { Length: > 0 }) _log.Add($"  Write offset recorded by redumper: {r.WriteOffset}.");

                int checkedCount = 0, matched = 0;
                foreach (var entry in r.Roms)
                {
                    if (entry.Sha1 is not { Length: > 0 } expected) continue;
                    string copy = Path.Combine(importedDir, entry.Name);
                    if (!File.Exists(copy)) continue;
                    checkedCount++;
                    string actual = await Task.Run(() =>
                    {
                        using var fs = File.OpenRead(copy);
                        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(fs));
                    });
                    if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) matched++;
                    else
                        _log.Add($"  {entry.Name}: SHA-1 does NOT match the log — file {actual.ToLowerInvariant()}, " +
                                 $"log {expected.ToLowerInvariant()}.", EventLogView.Level.Error);
                }
                if (checkedCount > 0 && matched == checkedCount)
                    _log.Add($"  All {matched} file(s) match the SHA-1s redumper recorded.", EventLogView.Level.Good);
                else if (checkedCount == 0 && r.Roms.Count > 0)
                    _log.Add($"  The log's dat lists {r.Roms.Count} file(s), none of them among the imported files by name, " +
                             "so no hashes were checked.");
            }
            else
            {
                var d = DiscForge.Core.Dumping.DicLogParser.ParseText(text, logPath);
                if (d.DicVersion is null && d.Tracks.Count == 0) return; // Some other tool's log — say nothing.
                _log.Add($"Found {Path.GetFileName(logPath)}: {d.Summary()}.",
                    d.LooksClean ? EventLogView.Level.Info : EventLogView.Level.Warn);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"dump log beside import: {ex.Message}");
        }
    }

    private static string Rate(double bytesDone, double secs)
        => secs > 0.7 ? $"{bytesDone / (1024.0 * 1024.0) / secs:0.0} MB/s" : "—";

    private static string Eta(double frac, double secs)
    {
        if (frac <= 0.001 || secs < 0.7) return "—";
        double left = Math.Max(0, secs / frac - secs);
        var ts = TimeSpan.FromSeconds(left);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    /// <summary>Best-effort: turn a freshly-captured LibCrypt .sub into a portable
    /// .sbi beside the image. Preservation, not circumvention — it copies the
    /// disc's own subchannel so an emulator reproduces it faithfully.</summary>
    private void TryWriteSbi(string subPath, string imagePath)
    {
        try
        {
            var sub = File.ReadAllBytes(subPath);
            if (sub.Length % DiscForge.Core.Raw.RawSubchannel.FrameSize != 0) return;
            var doc = DiscForge.Core.PlayStation.Sbi.FromSubchannel(sub);
            if (doc.IsEmpty) return;
            string sbiPath = Path.ChangeExtension(imagePath, ".sbi");
            File.WriteAllBytes(sbiPath, DiscForge.Core.PlayStation.Sbi.Write(doc));
            _log.Add($"Wrote {Path.GetFileName(sbiPath)} — {doc.Entries.Count} LibCrypt entry(ies) " +
                     "for emulators that read SBI.", EventLogView.Level.Good);
        }
        catch (Exception ex)
        {
            AppLog.Write($"sbi from capture: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Write($"could not delete '{path}': {ex.Message}"); }
    }
}