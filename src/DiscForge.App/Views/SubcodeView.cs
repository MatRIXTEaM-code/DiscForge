// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Core.Raw;
using DiscForge.Devices;
using DiscForge.Devices.Reading;
using DiscForge.Devices.Spti;

namespace DiscForge.App.Views;

/// <summary>
/// Analyse a disc's sub-channel — the eight bits per frame that ride alongside
/// the audio or data, carrying timing, track position and, on some discs,
/// deliberate corruption used as copy protection.
///
/// Q is the channel that matters. Every frame carries a CRC over its own
/// contents, so a frame is either self-consistent or it isn't. On a healthy disc
/// essentially all of them validate. A scattered handful that don't — a few
/// dozen across a whole disc, in isolated frames rather than bursts — is the
/// signature of LibCrypt and its relatives: the corruption is deliberate, the
/// pattern is the key, and a copy that "repairs" it destroys the protection and
/// the disc's ability to authenticate itself.
///
/// Damage looks different. It comes in runs, follows the physical geometry of a
/// scratch, and there is usually far more of it. Telling the two apart is the
/// whole point of looking.
/// </summary>
internal sealed class SubcodeView : UserControl
{
    private readonly RadioButton _fromFile = new()
    {
        Text = "From a .sub sidecar", AutoSize = true, Location = new Point(12, 14),
        Font = Theme.Ui, Checked = true,
    };
    private readonly RadioButton _fromDisc = new()
    {
        Text = "From a disc", AutoSize = true, Location = new Point(170, 14), Font = Theme.Ui,
    };

    private readonly TextBox _path = new()
    {
        ReadOnly = true, Width = 330, Font = Theme.Ui, Location = new Point(12, 42),
    };
    private readonly Button _browse = new()
    {
        Text = "Open…", Location = new Point(350, 41), Width = 70, Height = 24,
        FlatStyle = FlatStyle.System,
    };

    private readonly ComboBox _drives = new()
    {
        Width = 260, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(12, 42), Visible = false,
    };
    private readonly Button _detect = new()
    {
        Text = "Detect", Location = new Point(280, 41), Width = 66, Height = 24,
        FlatStyle = FlatStyle.System, Visible = false,
    };
    private readonly NumericUpDown _start = new()
    {
        Minimum = 0, Maximum = 500_000, Value = 0, Width = 80,
        Location = new Point(404, 42), Font = Theme.Ui, Visible = false,
    };
    private readonly NumericUpDown _count = new()
    {
        Minimum = 1, Maximum = 400_000, Value = 5000, Width = 80,
        Location = new Point(544, 42), Font = Theme.Ui, Visible = false,
    };
    private readonly Label _startLabel = new()
    {
        Text = "From:", AutoSize = true, Location = new Point(362, 45), Font = Theme.Ui, Visible = false,
    };
    private readonly Label _countLabel = new()
    {
        Text = "Sectors:", AutoSize = true, Location = new Point(490, 45), Font = Theme.Ui, Visible = false,
    };

    private readonly Button _analyse = new()
    {
        Text = "Analyse", Location = new Point(440, 12), Width = 86, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _cancel = new()
    {
        Text = "Cancel", Location = new Point(532, 12), Width = 82, Height = 26,
        FlatStyle = FlatStyle.System, Visible = false,
    };
    private readonly Button _saveLog = new()
    {
        Text = "Save log…", Location = new Point(620, 12), Width = 86, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    // File-mode only, so it never shares screen space with the disc-mode range
    // controls at the same Y.
    private readonly Button _saveSbi = new()
    {
        Text = "Save SBI…", Location = new Point(430, 41), Width = 90, Height = 24,
        FlatStyle = FlatStyle.System, Enabled = false, Visible = false,
    };
    private readonly Label _elapsed = new()
    {
        AutoSize = false, Location = new Point(640, 45), Size = new Size(84, 16),
        Font = Theme.Ui, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleRight,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };

    private readonly ProgressBar _progress = new()
    {
        Location = new Point(12, 74), Size = new Size(712, 18), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Label _verdict = new()
    {
        AutoSize = false, Location = new Point(12, 100), Size = new Size(712, 22),
        Font = new Font(Theme.Ui.FontFamily, 10f, FontStyle.Bold),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _out = new()
    {
        Location = new Point(12, 128), Size = new Size(712, 320),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private readonly OperationRunner _runner;
    private IReadOnlyList<DriveCapabilities> _detected = Array.Empty<DriveCapabilities>();
    private string? _subPath;
    private OperationLog? _log;

    public SubcodeView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        _fromFile.CheckedChanged += (_, _) => SwitchSource();
        _browse.Click += (_, _) => Browse();
        _detect.Click += async (_, _) => await DetectAsync();
        _analyse.Click += async (_, _) => await AnalyseAsync();
        _saveLog.Click += (_, _) => SaveLog();
        _saveSbi.Click += (_, _) => ExportSbi();
        _drives.SelectedIndexChanged += (_, _) => _analyse.Enabled = _drives.SelectedIndex >= 0;

        _runner = new OperationRunner(_analyse, _cancel, _elapsed);

        Controls.Add(_fromFile); Controls.Add(_fromDisc);
        Controls.Add(_path); Controls.Add(_browse);
        Controls.Add(_drives); Controls.Add(_detect);
        Controls.Add(_startLabel); Controls.Add(_start);
        Controls.Add(_countLabel); Controls.Add(_count);
        Controls.Add(_analyse); Controls.Add(_cancel); Controls.Add(_saveLog); Controls.Add(_elapsed);
        Controls.Add(_saveSbi);
        Controls.Add(_progress); Controls.Add(_verdict); Controls.Add(_out);

        _out.Text =
            "Sub-channel is the eight bits per frame that ride alongside the audio" + Environment.NewLine +
            "or data — 96 bytes for every sector. The Q channel carries timing and" + Environment.NewLine +
            "track position, and every frame includes a CRC over its own contents." + Environment.NewLine +
            Environment.NewLine +
            "On a healthy disc essentially all of them validate. A scattered handful" + Environment.NewLine +
            "that don't — a few dozen across a whole disc, isolated rather than in" + Environment.NewLine +
            "runs — is the signature of LibCrypt and its relatives: the corruption is" + Environment.NewLine +
            "deliberate and the pattern is the key. A copy that \"repairs\" it destroys" + Environment.NewLine +
            "both the protection and the disc's ability to authenticate itself." + Environment.NewLine +
            Environment.NewLine +
            "Damage looks different: runs rather than isolated frames, following the" + Environment.NewLine +
            "geometry of a scratch, and usually far more of it." + Environment.NewLine +
            Environment.NewLine +
            "Analyse a .sub sidecar, or read the sub-channel straight off a disc.";
    }

    private void SwitchSource()
    {
        bool file = _fromFile.Checked;

        _path.Visible = _browse.Visible = file;
        _drives.Visible = _detect.Visible = !file;
        _startLabel.Visible = _start.Visible = !file;
        _countLabel.Visible = _count.Visible = !file;
        _saveSbi.Visible = file;

        _analyse.Enabled = file
            ? _subPath is not null
            : _drives.SelectedIndex >= 0;
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Sub-channel sidecars (*.sub)|*.sub|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _subPath = dlg.FileName;
        _path.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);

        long size = new FileInfo(dlg.FileName).Length;
        if (size % RawSubchannel.FrameSize != 0)
        {
            _verdict.Text = "This does not look like a sub-channel sidecar.";
            _verdict.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
            _out.Text =
                $"{size:N0} bytes is not a whole number of {RawSubchannel.FrameSize}-byte frames " +
                $"({size / (double)RawSubchannel.FrameSize:N2})." + Environment.NewLine +
                Environment.NewLine +
                "A .sub file holds exactly 96 bytes for every sector of its track. A file" + Environment.NewLine +
                "that doesn't divide evenly is either truncated or a different format —" + Environment.NewLine +
                "some tools write 16-byte formatted-Q sidecars instead of the full 96.";
            _analyse.Enabled = false;
            return;
        }

        _verdict.Text = "";
        _out.Text = $"{size / RawSubchannel.FrameSize:N0} frames " +
                    $"({size / RawSubchannel.FrameSize / 75.0 / 60:N1} minutes of disc). " +
                    "Press Analyse.";
        _analyse.Enabled = true;
        _saveSbi.Enabled = true;
    }

    /// <summary>
    /// Write an SBI beside the .sub: the compact, emulator-ready form of the
    /// disc's LibCrypt subchannel. This is preservation — it copies the disc's
    /// own protection data verbatim so a faithful reproduction passes the game's
    /// check because the data genuinely is there. A disc without LibCrypt yields
    /// an empty SBI and nothing is written.
    /// </summary>
    private void ExportSbi()
    {
        if (_subPath is null) return;
        try
        {
            var sub = File.ReadAllBytes(_subPath);
            if (sub.Length % RawSubchannel.FrameSize != 0)
            {
                RetroMessageBox.Show("This .sub is not a whole number of 96-byte frames.",
                    "DiscForge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var doc = Core.PlayStation.Sbi.FromSubchannel(sub);
            if (doc.IsEmpty)
            {
                RetroMessageBox.Show(
                    "No LibCrypt subchannel was found, so there is nothing to write — the .sub " +
                    "already preserves everything on this disc.",
                    "DiscForge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var save = new SaveFileDialog
            {
                Filter = "SBI file (*.sbi)|*.sbi",
                FileName = Path.GetFileNameWithoutExtension(_subPath) + ".sbi",
                InitialDirectory = Path.GetDirectoryName(_subPath) ?? "",
            };
            if (save.ShowDialog() != DialogResult.OK) return;

            File.WriteAllBytes(save.FileName, Core.PlayStation.Sbi.Write(doc));
            StatusBus.Report($"Wrote {Path.GetFileName(save.FileName)} ({doc.Entries.Count} entries)");
            RetroMessageBox.Show(
                $"Wrote {doc.Entries.Count} LibCrypt entry(ies) to {Path.GetFileName(save.FileName)}.",
                "DiscForge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            RetroMessageBox.Show("Could not write the SBI: " + ex.Message,
                "DiscForge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppLog.WriteException("sbi export", ex);
        }
    }

    private async Task DetectAsync()
    {
        _drives.Items.Clear();
        _analyse.Enabled = false;
        _out.Text = "Detecting…";
        try
        {
            _detected = await Task.Run(() => DriveDetector.DetectAll());
            foreach (var d in _detected) _drives.Items.Add(d.Summary());
            if (_drives.Items.Count > 0)
            {
                _drives.SelectedIndex = 0;
                _out.Text = "Choose a sector range and press Analyse.\r\n\r\n" +
                            "Five thousand sectors is about a minute of disc — enough to see the\r\n" +
                            "pattern without reading the whole thing.";
            }
            else
            {
                _out.Text = "No optical drives detected (raw access usually needs administrator).";
            }
        }
        catch (Exception ex)
        {
            _out.Text = "Detection failed: " + ex.Message;
            AppLog.WriteException("subcode detect", ex);
        }
    }

    private char? SelectedLetter()
    {
        if (_drives.SelectedIndex < 0 || _drives.SelectedIndex >= _detected.Count) return null;
        var path = _detected[_drives.SelectedIndex].DevicePath;
        int i = path.LastIndexOf(':');
        return i > 0 ? path[i - 1] : null;
    }

    private void SaveLog()
    {
        if (_log is null) return;
        var path = _log.SaveWithDialog();
        if (path is not null) StatusBus.Report($"Log saved to {Path.GetFileName(path)}");
    }

    private sealed record Outcome(RawSubchannel.Analysis Analysis, string Source,
                                  uint Refused, string? RefusalReason);

    private async Task AnalyseAsync()
    {
        _progress.Value = 0;
        _saveLog.Enabled = false;
        _verdict.Text = "";
        _out.Text = "Analysing…";

        Outcome? outcome;

        if (_fromFile.Checked)
        {
            if (_subPath is null) return;
            var path = _subPath;

            outcome = await _runner.RunAsync(cancel =>
            {
                using var fs = File.OpenRead(path);
                var analysis = RawSubchannel.Analyse(fs);
                return new Outcome(analysis, Path.GetFileName(path), 0, null);
            },
            ex =>
            {
                _out.Text = "Could not analyse: " + ex.Message;
                AppLog.WriteException("subcode analyse file", ex);
            });
        }
        else
        {
            var letter = SelectedLetter();
            if (letter is null) return;

            uint start = (uint)_start.Value;
            uint count = (uint)_count.Value;

            outcome = await _runner.RunAsync(cancel =>
            {
                using var dev = new SptiDevice(letter.Value);

                if (!SubchannelReader.SupportsRawSubchannel(dev, start))
                    throw new NotSupportedException(
                        "This drive will not return raw P–W sub-channel. Some drives only " +
                        "offer formatted Q, and some none at all — it is one of the more " +
                        "variably supported features. A .sub sidecar captured by a drive " +
                        "that can do it will still analyse here.");

                var progress = new Progress<double>(f =>
                    BeginInvoke(() => _progress.Value = Math.Clamp((int)(f * 100), 0, 100)));

                var read = SubchannelReader.Read(dev, start, count, progress, cancel);

                using var ms = new MemoryStream(read.Subcode);
                var analysis = RawSubchannel.Analyse(ms);
                return new Outcome(analysis, $"{char.ToUpperInvariant(letter.Value)}: " +
                                             $"LBA {start:N0}–{start + count - 1:N0}",
                                   read.SectorsRefused, read.RefusalReason);
            },
            ex =>
            {
                _out.Text = ex.Message;
                AppLog.WriteException("subcode analyse disc", ex);
            });
        }

        if (outcome is null) return;      // failed or cancelled

        _progress.Value = 100;

        var a = outcome.Analysis;
        (_verdict.Text, _verdict.ForeColor) = Verdict(a, outcome.Refused);

        string text = Render(outcome);
        _out.Text = text;

        var log = new OperationLog("Sub-channel analysis");
        if (!_fromFile.Checked && _drives.SelectedIndex >= 0)
            log.Drive(_detected[_drives.SelectedIndex]);
        log.Settings(
            ("Source", outcome.Source),
            ("Frames", a.Frames),
            ("Q valid", a.QValid),
            ("Q invalid", a.QInvalid));
        log.Result(text);
        _log = log;
        _saveLog.Enabled = true;

        AppLog.Write($"  subcode analysis {outcome.Source}: {a.QValid:N0} valid, " +
                     $"{a.QInvalid:N0} invalid, libcrypt={a.LooksLikeLibCrypt}");
        StatusBus.Report(_verdict.Text);
    }

    private static (string, Color) Verdict(RawSubchannel.Analysis a, uint refused)
    {
        if (a.Frames == 0)
            return ("Nothing to analyse.", Color.Gray);

        if (a.LooksLikeLibCrypt)
            return ($"LibCrypt-style protection: {a.QInvalid} deliberately corrupt Q frame(s).",
                    Color.FromArgb(0xA0, 0x60, 0x00));

        if (a.QInvalid == 0)
            return ("Every Q frame validates — clean sub-channel, no protection detected.",
                    Color.FromArgb(0x20, 0x70, 0x20));

        double rate = (double)a.QInvalid / a.Frames;
        if (rate > 0.02)
            return ($"{a.QInvalid:N0} invalid Q frame(s) ({rate:P1}) — that is damage, not protection.",
                    Color.FromArgb(0xA0, 0x20, 0x20));

        return ($"{a.QInvalid:N0} invalid Q frame(s) — too few for damage, too many to ignore.",
                Color.FromArgb(0xA0, 0x60, 0x00));
    }

    private static string Render(Outcome o)
    {
        var a = o.Analysis;
        var sb = new StringBuilder();

        sb.AppendLine($"Source: {o.Source}");
        sb.AppendLine();
        sb.AppendLine($"  Frames analysed   {a.Frames:N0}  ({a.Frames / 75.0 / 60:N1} minutes)");
        sb.AppendLine($"  Q frames valid    {a.QValid:N0}");
        sb.AppendLine($"  Q frames invalid  {a.QInvalid:N0}" +
                      (a.Frames > 0 ? $"  ({(double)a.QInvalid / a.Frames:P2})" : ""));
        if (o.Refused > 0)
            sb.AppendLine($"  Sectors refused   {o.Refused:N0}  (read as zero frames, counted invalid)");
        sb.AppendLine();

        if (a.QInvalid > 0)
        {
            sb.AppendLine("Invalid frames at:");
            var preview = a.InvalidLbas.Take(60).ToList();
            for (int i = 0; i < preview.Count; i += 10)
                sb.AppendLine("  " + string.Join(", ",
                    preview.Skip(i).Take(10).Select(x => x.ToString("N0"))));
            if (a.InvalidLbas.Count > preview.Count)
                sb.AppendLine($"  … and {a.InvalidLbas.Count - preview.Count:N0} more");
            sb.AppendLine();

            // The clustering is what distinguishes the two cases, so it's worth
            // computing rather than leaving the reader to eyeball a list.
            var runs = CountRuns(a.InvalidLbas);
            sb.AppendLine($"  Distributed across {runs} run(s) of consecutive frames.");
            sb.AppendLine();

            if (a.LooksLikeLibCrypt)
            {
                sb.AppendLine("This pattern reads as deliberate.");
                sb.AppendLine();
                sb.AppendLine("LibCrypt and similar schemes corrupt a small number of Q frames in");
                sb.AppendLine("fixed positions. The game reads those positions back and checks they");
                sb.AppendLine("are still wrong in exactly the right way — so a copy that regenerates");
                sb.AppendLine("clean sub-channel fails to authenticate, however perfect the data is.");
                sb.AppendLine();
                sb.AppendLine("To copy this disc faithfully the sub-channel must be written back");
                sb.AppendLine("verbatim, which needs a drive that supports RAW DAO-96 writing.");
            }
            else if (runs < a.QInvalid / 2)
            {
                sb.AppendLine("These failures come in runs, which points at damage rather than");
                sb.AppendLine("protection — a scratch corrupts consecutive frames, while protection");
                sb.AppendLine("schemes corrupt isolated ones in chosen positions.");
            }
            else
            {
                sb.AppendLine("These failures are scattered rather than clustered, which is the shape");
                sb.AppendLine("protection takes — but there are too few to be certain, and marginal");
                sb.AppendLine("media produces isolated failures too. Worth reading again to see");
                sb.AppendLine("whether the same frames fail: deliberate corruption is identical every");
                sb.AppendLine("time, while marginal reads vary.");
            }
        }
        else
        {
            sb.AppendLine("Every Q frame's CRC validates.");
            sb.AppendLine();
            sb.AppendLine("No protection of this kind is present, and the sub-channel is undamaged");
            sb.AppendLine("across the range examined.");
        }

        return sb.ToString();
    }

    /// <summary>How many runs of consecutive frames the failures form. One long
    /// run is a scratch; fifty isolated ones are a pattern.</summary>
    private static int CountRuns(IReadOnlyList<long> lbas)
    {
        if (lbas.Count == 0) return 0;
        int runs = 1;
        for (int i = 1; i < lbas.Count; i++)
            if (lbas[i] != lbas[i - 1] + 1) runs++;
        return runs;
    }
}