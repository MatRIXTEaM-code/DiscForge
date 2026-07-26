// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Devices;
using DiscForge.Core.Media;
using DiscForge.Core.Mmc;
using DiscForge.Devices;
using DiscForge.Devices.Media;
using DiscForge.Devices.Reading;
using DiscForge.Devices.Spti;

namespace DiscForge.App.Views;

/// <summary>
/// C2-guided sector recovery: read a range repeatedly and combine the attempts,
/// taking each byte from a read whose C2 pointers vouched for it, then repair
/// what remains from the sector's own Reed-Solomon parity.
///
/// Why this beats retrying. An ordinary reader that fails throws the attempt
/// away and tries again — but a failed read is mostly correct, and the drive
/// will say exactly which bytes it couldn't correct. Those bytes move between
/// attempts, because marginal damage isn't the same on every revolution. Keeping
/// every attempt and assembling from the good parts recovers sectors that no
/// single read produces. What re-reading can't fix, the parity often can:
/// knowing where the damage is doubles what Reed-Solomon can repair.
///
/// CD only. READ CD (0xBE) with C2 pointers is a Compact Disc command; on DVD
/// or Blu-ray it is rejected outright, and a run there produces hundreds of
/// refusals that look alarmingly like a destroyed disc. The media is therefore
/// checked before anything starts.
/// </summary>
internal sealed class RecoveryView : UserControl
{
    private readonly ComboBox _drives = new()
    {
        Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 13),
    };
    private readonly NumericUpDown _start = new()
    {
        Minimum = 0, Maximum = 500_000, Value = 0, Width = 84,
        Location = new Point(70, 52), Font = Theme.Ui,
    };
    private readonly NumericUpDown _count = new()
    {
        Minimum = 1, Maximum = 100_000, Value = 64, Width = 72,
        Location = new Point(220, 52), Font = Theme.Ui,
    };
    private readonly NumericUpDown _reads = new()
    {
        Minimum = 1, Maximum = 32, Value = 8, Width = 52,
        Location = new Point(372, 52), Font = Theme.Ui,
    };
    private readonly ComboBox _speed = new()
    {
        Width = 190, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 82),
    };
    private readonly Button _scan = new()
    {
        Text = "Recover", Location = new Point(478, 50), Width = 90, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _cancel = new()
    {
        Text = "Cancel", Location = new Point(574, 50), Width = 90, Height = 26,
        FlatStyle = FlatStyle.System, Visible = false,
    };
    private readonly Button _saveLog = new()
    {
        Text = "Save log…", Location = new Point(574, 80), Width = 90, Height = 24,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Label _elapsed = new()
    {
        AutoSize = false, Location = new Point(670, 55), Size = new Size(54, 16),
        Font = Theme.Ui, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleRight,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly Label _c2 = new()
    {
        AutoSize = false, Location = new Point(12, 112), Size = new Size(712, 16),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly ProgressBar _progress = new()
    {
        Location = new Point(12, 134), Size = new Size(712, 18), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _out = new()
    {
        Location = new Point(12, 162), Size = new Size(712, 286),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private readonly OperationRunner _runner;
    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private bool _mediaIsCd;
    private OperationLog? _log;

    public RecoveryView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label { Text = "From LBA:", AutoSize = true, Location = new Point(12, 55), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Sectors:", AutoSize = true, Location = new Point(162, 55), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Max reads:", AutoSize = true, Location = new Point(304, 55), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Speed:", AutoSize = true, Location = new Point(12, 85), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "A slower spin gives the laser longer per pit.",
            AutoSize = true, Location = new Point(268, 85), Font = Theme.Ui, ForeColor = Color.Gray,
        });

        foreach (var s in DriveSpeed.CdSpeeds) _speed.Items.Add(s);
        _speed.SelectedIndex = 0;

        var detect = new Button
        {
            Text = "Detect", Location = new Point(378, 12), Width = 74, FlatStyle = FlatStyle.System,
        };
        detect.Click += async (_, _) => await DetectAsync();

        var eject = new Button
        {
            Text = "Eject", Location = new Point(458, 12), Width = 66, FlatStyle = FlatStyle.System,
        };
        eject.Click += (_, _) => Eject();

        _scan.Click += async (_, _) => await RecoverAsync();
        _saveLog.Click += (_, _) => SaveLog();
        _drives.SelectedIndexChanged += async (_, _) => await CheckDriveAsync();

        _runner = new OperationRunner(_scan, _cancel, _elapsed);

        Controls.Add(_drives); Controls.Add(detect); Controls.Add(eject);
        Controls.Add(_start); Controls.Add(_count); Controls.Add(_reads); Controls.Add(_speed);
        Controls.Add(_scan); Controls.Add(_cancel); Controls.Add(_saveLog); Controls.Add(_elapsed);
        Controls.Add(_c2); Controls.Add(_progress); Controls.Add(_out);

        _out.Text =
            "Detect a drive, then choose a sector range to recover." + Environment.NewLine +
            Environment.NewLine +
            "This reads each sector as many times as needed, using the drive's C2" + Environment.NewLine +
            "error pointers to take every byte from a read that could correct it." + Environment.NewLine +
            "Damage that no single read survives is often recoverable across several," + Environment.NewLine +
            "because the uncorrectable bytes move between attempts." + Environment.NewLine +
            Environment.NewLine +
            "What re-reading cannot fix, the sector's own Reed-Solomon parity often" + Environment.NewLine +
            "can — knowing where the bad bytes are doubles what it can repair." + Environment.NewLine +
            Environment.NewLine +
            "On a badly damaged disc, try 4x. A drive at 48x has under a millisecond" + Environment.NewLine +
            "to resolve each pit; at 4x it has twelve times as long, and tracks a" + Environment.NewLine +
            "warped or scratched surface far more steadily." + Environment.NewLine +
            Environment.NewLine +
            "CD only: the command this relies on does not exist for DVD or Blu-ray." + Environment.NewLine +
            Environment.NewLine +
            "Damage sits mostly toward the outer edge of a disc — higher sector" + Environment.NewLine +
            "numbers. LBA 0 is the innermost track and reads cleanly on almost" + Environment.NewLine +
            "anything, so it is a poor place to look for trouble.";
    }

    private async Task DetectAsync()
    {
        _drives.Items.Clear();
        _scan.Enabled = false;
        _c2.Text = "Detecting…";
        _c2.ForeColor = Color.Gray;
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            foreach (var d in _detected) _drives.Items.Add(d.Summary());
            if (_drives.Items.Count > 0) _drives.SelectedIndex = 0;
            else _c2.Text = "No optical drives detected (raw access usually needs administrator).";
        }
        catch (Exception ex)
        {
            _c2.Text = "Detection failed: " + ex.Message;
            AppLog.WriteException("recovery detect", ex);
        }
    }

    private char? SelectedLetter()
    {
        if (_drives.SelectedIndex < 0 || _drives.SelectedIndex >= _detected.Count) return null;
        var path = _detected[_drives.SelectedIndex].DevicePath;
        int i = path.LastIndexOf(':');
        return i > 0 ? path[i - 1] : null;
    }

    private void Eject()
    {
        var letter = SelectedLetter();
        if (letter is null) return;
        try
        {
            DriveTray.Eject(letter.Value);
            _c2.Text = "Tray ejected. Insert a disc and press Detect again.";
            _c2.ForeColor = Color.Gray;
            _scan.Enabled = false;
        }
        catch (Exception ex)
        {
            _c2.Text = "Could not eject: " + ex.Message;
            _c2.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
        }
    }

    private void SaveLog()
    {
        if (_log is null) return;
        var path = _log.SaveWithDialog();
        if (path is not null) StatusBus.Report($"Log saved to {Path.GetFileName(path)}");
    }

    /// <summary>
    /// Establish three things before letting a run start: what media is loaded,
    /// what the drive claims about C2, and what it does when actually asked.
    ///
    /// The media check comes first because it dominates. READ CD is a CD command;
    /// on a DVD every call is refused no matter what the mode page advertises,
    /// and a run there is hundreds of pointless commands ending in a report that
    /// looks like a wrecked disc.
    /// </summary>
    private async Task CheckDriveAsync()
    {
        var letter = SelectedLetter();
        if (letter is null) { _scan.Enabled = false; return; }

        var drive = _detected[_drives.SelectedIndex];
        _c2.Text = "Asking the drive…";
        _c2.ForeColor = Color.Gray;
        _scan.Enabled = false;

        _mediaIsCd = drive.MediaProfile is MmcProfile.CdRom or MmcProfile.CdR or MmcProfile.CdRw;

        if (!_mediaIsCd)
        {
            _c2.Text = $"{drive.MediaProfile} loaded — C2 recovery is a CD-only feature. " +
                       "Insert a CD to use it.";
            _c2.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
            _out.Text =
                $"The drive holds {drive.MediaProfile} media." + Environment.NewLine +
                Environment.NewLine +
                "C2 error pointers come from READ CD, which is a Compact Disc command." + Environment.NewLine +
                "DVD and Blu-ray drives reject it, so there is nothing to recover with" + Environment.NewLine +
                "here — not because the disc is bad, but because the mechanism doesn't" + Environment.NewLine +
                "apply to this format." + Environment.NewLine +
                Environment.NewLine +
                "DVD error recovery works differently and isn't implemented yet.";
            return;
        }

        try
        {
            var (claims, works) = await Task.Run(() =>
            {
                var report = MediaInfoReader.Read(letter.Value);
                bool advertised = report.Capabilities?.C2Pointers ?? false;

                bool actual;
                try
                {
                    using var dev = new SptiDevice(letter.Value);
                    actual = C2SectorReader.SupportsC2(dev);
                }
                catch { actual = false; }

                return (advertised, actual);
            });

            _c2.Text = (claims, works) switch
            {
                (true, true) => "C2 error pointers: supported — recovery can use them.",
                (false, true) => "C2 error pointers: the drive didn't advertise them but accepts " +
                                 "the command. Using them.",
                (true, false) => "C2 error pointers: advertised but the command was refused. " +
                                 "Falling back to majority voting.",
                (false, false) => "C2 error pointers: not available. Recovery will re-read and " +
                                  "vote, but without pointers to guide it.",
            };
            _c2.ForeColor = works ? Color.FromArgb(0x20, 0x70, 0x20) : Color.FromArgb(0xA0, 0x60, 0x00);
            _scan.Enabled = true;
        }
        catch (Exception ex)
        {
            _c2.Text = "Could not query the drive: " + ex.Message;
            _c2.ForeColor = Color.FromArgb(0xA0, 0x20, 0x20);
            _scan.Enabled = false;
        }
    }

    private sealed record RunSummary(
        int Clean, int Recovered, int EccRepaired, int Uncertain, int Refused,
        long UncertainBytes, long TotalReads, uint Done, string? FirstRefusal, string Detail,
        bool Cancelled, bool SpeedSet, string SpeedLabel);

    private async Task RecoverAsync()
    {
        var letter = SelectedLetter();
        if (letter is null) return;

        uint start = (uint)_start.Value;
        uint count = (uint)_count.Value;
        int maxReads = (int)_reads.Value;
        var chosenSpeed = (ReadSpeed)_speed.SelectedItem!;
        var drive = _detected[_drives.SelectedIndex];

        _progress.Value = 0;
        _saveLog.Enabled = false;
        _out.Text = "Reading…";

        var summary = await _runner.RunAsync(cancel =>
        {
            var detail = new StringBuilder();
            using var dev = new SptiDevice(letter.Value);

            // Ask for the chosen speed. A drive that refuses still reads fine at
            // whatever speed it picked, so this is a preference rather than a
            // requirement — but on a damaged disc it is often the difference.
            bool speedSet = chosenSpeed.KilobytesPerSecond == 0xFFFF
                || DriveSpeed.TrySetReadSpeed(dev, chosenSpeed.KilobytesPerSecond);

            var opts = new C2ReadOptions { MaxReads = maxReads };

            int clean = 0, recovered = 0, ecc = 0, uncertain = 0, refused = 0;
            long uncertainBytes = 0, totalReads = 0;
            string? firstRefusal = null;
            uint done = 0;
            bool cancelled = false;

            try
            {
                for (uint i = 0; i < count; i++)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        cancelled = true;
                        detail.AppendLine($"  Stopped at LBA {start + i:N0} — cancelled.");
                        break;
                    }

                    uint lba = start + i;
                    var r = C2SectorReader.ReadSector(dev, lba, opts);
                    totalReads += r.ReadsUsed;
                    done = i + 1;

                    if (r.AllReadsRefused)
                    {
                        refused++;
                        firstRefusal ??= r.RefusalReason;

                        // Every read refused, repeatedly, from the very start:
                        // the command doesn't apply to this disc or drive.
                        // Grinding through hundreds more proves nothing.
                        if (refused >= 8 && clean == 0 && recovered == 0 && uncertain == 0)
                        {
                            detail.AppendLine("  Stopped: the drive refused the first 8 sectors outright.");
                            break;
                        }
                        if (refused <= 8) detail.AppendLine("  " + r.Describe());
                    }
                    else if (r.EccRepaired)
                    {
                        ecc++;
                        detail.AppendLine("  " + r.Describe());
                    }
                    else if (r.Complete && !r.NeededRecovery) clean++;
                    else if (r.Complete)
                    {
                        recovered++;
                        detail.AppendLine("  " + r.Describe());
                    }
                    else
                    {
                        uncertain++;
                        uncertainBytes += r.UncertainBytes.Count;
                        if (uncertain <= 40) detail.AppendLine("  " + r.Describe());
                    }

                    if ((i & 7) == 0)
                    {
                        int pct = (int)(100.0 * i / count);
                        BeginInvoke(() => _progress.Value = Math.Clamp(pct, 0, 100));
                    }
                }
            }
            finally
            {
                // Leave the drive as it was found: a speed set for recovery
                // would otherwise persist and make every later read slow for no
                // apparent reason.
                if (chosenSpeed.KilobytesPerSecond != 0xFFFF) DriveSpeed.TryResetSpeed(dev);
            }

            return new RunSummary(clean, recovered, ecc, uncertain, refused,
                                  uncertainBytes, totalReads, done, firstRefusal,
                                  detail.ToString(), cancelled, speedSet, chosenSpeed.Label);
        },
        ex =>
        {
            _out.Text = "Recovery failed: " + ex.Message;
            AppLog.WriteException("c2 recovery", ex);
        });

        if (summary is null) return;      // failed, or cancelled before any work

        _progress.Value = 100;
        string report = Render(summary, start, _runner.Elapsed);
        _out.Text = report;

        // Build a self-contained record: the hardware and media details are what
        // make a result interpretable later, or by anyone else.
        var log = new OperationLog("Sector recovery");
        try
        {
            var info = await Task.Run(() => MediaInfoReader.Read(letter.Value));
            log.Drive(drive, info.Capabilities);
            log.Media(drive, info.Identity);
        }
        catch
        {
            log.Drive(drive);
            log.Media(drive);
        }
        log.Settings(
            ("Range", $"LBA {start:N0} – {start + count - 1:N0} ({count:N0} sectors)"),
            ("Max reads", maxReads),
            ("Speed", chosenSpeed.Label + (summary.SpeedSet ? "" : " (drive refused)")),
            ("ECC correction", "enabled"));
        log.Result(report);
        _log = log;
        _saveLog.Enabled = true;

        AppLog.Write($"  c2 recovery LBA {start}-{start + summary.Done - 1} at {summary.SpeedLabel}: " +
                     $"{summary.Clean} clean, {summary.Recovered} recovered, " +
                     $"{summary.EccRepaired} ecc-repaired, {summary.Uncertain} uncertain, " +
                     $"{summary.Refused} refused, {summary.TotalReads} reads");
        StatusBus.Report(summary.Cancelled
            ? "Recovery cancelled"
            : summary.Refused > 0 && summary.Clean == 0
                ? "Recovery: the drive refused these reads"
                : $"Recovery complete: {summary.Recovered + summary.EccRepaired} recovered, " +
                  $"{summary.Uncertain} uncertain");
    }

    private static string Render(RunSummary s, uint start, TimeSpan elapsed)
    {
        var sb = new StringBuilder();

        if (s.Cancelled)
            sb.AppendLine("CANCELLED — the results below cover only what was read before stopping.");

        sb.AppendLine($"LBA {start:N0} to {start + s.Done - 1:N0} " +
                      $"({s.Done:N0} sector(s) attempted in {(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2})");
        if (!s.SpeedSet)
            sb.AppendLine($"  (the drive would not set {s.SpeedLabel} — it read at its own speed)");
        sb.AppendLine();
        sb.AppendLine($"  clean, first read      {s.Clean:N0}");
        sb.AppendLine($"  recovered by re-read   {s.Recovered:N0}");
        sb.AppendLine($"  rebuilt from parity    {s.EccRepaired:N0}");
        sb.AppendLine($"  bytes still uncertain  {s.Uncertain:N0} sector(s), {s.UncertainBytes:N0} byte(s)");
        sb.AppendLine($"  refused by the drive   {s.Refused:N0}");
        sb.AppendLine($"  reads issued           {s.TotalReads:N0}");
        sb.AppendLine();

        // The refusal case first: it invalidates every other reading, and
        // presenting it as damage is how a working disc gets condemned.
        if (s.Refused > 0 && s.Clean == 0 && s.Recovered == 0 && s.EccRepaired == 0)
        {
            sb.AppendLine("The drive refused every read. This is NOT disc damage —");
            sb.AppendLine("nothing was read, so nothing can be said about the disc's condition.");
            sb.AppendLine();
            if (s.FirstRefusal is not null)
                sb.AppendLine($"The drive said: {s.FirstRefusal}");
            sb.AppendLine();
            sb.AppendLine("The usual cause is media: READ CD with C2 pointers is a Compact");
            sb.AppendLine("Disc command, and DVD or Blu-ray drives reject it outright.");
        }
        else
        {
            if (s.EccRepaired > 0)
            {
                sb.AppendLine($"{s.EccRepaired:N0} sector(s) were rebuilt from the Reed-Solomon parity");
                sb.AppendLine("stored in the sector itself, and confirmed by EDC. Re-reading could not");
                sb.AppendLine("have fixed those — the damage doesn't move between attempts.");
                sb.AppendLine();
            }
            if (s.Recovered > 0)
            {
                sb.AppendLine($"{s.Recovered:N0} sector(s) were assembled from several reads —");
                sb.AppendLine("bytes one read couldn't correct were taken from a read that could.");
                sb.AppendLine();
            }
            if (s.Uncertain > 0)
            {
                sb.AppendLine($"{s.Uncertain:N0} sector(s) have bytes no read could vouch for and too");
                sb.AppendLine("much damage in one codeword for the parity to rebuild.");
                sb.AppendLine();
                sb.AppendLine("Worth trying: a slower speed if you haven't, cleaning the disc, or a");
                sb.AppendLine("different drive — drives vary considerably in what they can read from");
                sb.AppendLine("damaged media.");
                sb.AppendLine();
            }
            if (s.Refused > 0)
            {
                sb.AppendLine($"{s.Refused:N0} sector(s) were refused outright rather than read.");
                sb.AppendLine("Refusals are a different matter from damage — see the log for the reason.");
                sb.AppendLine();
            }
            if (s.Clean == s.Done && s.Done > 0)
            {
                sb.AppendLine("Every sector read cleanly first time. This range is undamaged.");
                sb.AppendLine();
                sb.AppendLine("Damage sits mostly toward the outer edge of a disc — higher sector");
                sb.AppendLine("numbers. LBA 0 is the innermost track and survives handling well,");
                sb.AppendLine("so a clean result there says little about the rest of the disc.");
                sb.AppendLine();
            }
        }

        sb.Append(s.Detail);
        return sb.ToString();
    }
}