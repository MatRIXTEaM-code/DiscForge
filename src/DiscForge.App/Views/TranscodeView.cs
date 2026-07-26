// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.DvdVideo;
using DiscForge.Core.Transcode;

namespace DiscForge.App.Views;

/// <summary>
/// Re-encode video to fit a target size — the DVD Shrink job, finished.
///
/// DiscForge already worked out the arithmetic: given a source and a target
/// size, BitBudget decides what compression ratio each title needs and
/// TranscodePlanner turns that into ffmpeg arguments. What was missing was
/// anything that ran them, so the planning ended in a paragraph telling the user
/// to go and do it themselves.
///
/// Unencrypted sources only, which is not a limitation so much as a
/// consequence: re-encoding needs readable video, and DiscForge does not
/// decrypt. Home-made discs, camcorder footage, anything authored yourself —
/// all fine. A commercial DVD would need decrypting first, and that is a line
/// DiscForge does not cross.
///
/// FFmpeg does the encoding. It is not bundled: it is a large, separately
/// licensed program, and shipping a copy inside a disc tool would be both rude
/// and a maintenance burden. If it is on the PATH it is found automatically.
/// </summary>
internal sealed class TranscodeView : UserControl
{
    private readonly TextBox _input = new()
    {
        ReadOnly = true, Width = 400, Font = Theme.Ui, Location = new Point(70, 13),
    };
    private readonly Button _browse = new()
    {
        Text = "Open…", Location = new Point(478, 12), Width = 74, Height = 24,
        FlatStyle = FlatStyle.System,
    };
    private readonly TextBox _output = new()
    {
        ReadOnly = true, Width = 400, Font = Theme.Ui, Location = new Point(70, 43),
    };
    private readonly Button _saveAs = new()
    {
        Text = "Save as…", Location = new Point(478, 42), Width = 74, Height = 24,
        FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly ComboBox _target = new()
    {
        Width = 170, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(70, 76),
    };
    private readonly ComboBox _codec = new()
    {
        Width = 130, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(300, 76),
    };
    private readonly CheckBox _twoPass = new()
    {
        Text = "Two-pass (slower, better)", AutoSize = true,
        Location = new Point(444, 78), Font = Theme.Ui,
    };

    private readonly Button _encode = new()
    {
        Text = "Encode", Location = new Point(560, 12), Width = 80, Height = 26,
        FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly Button _cancel = new()
    {
        Text = "Cancel", Location = new Point(646, 12), Width = 78, Height = 26,
        FlatStyle = FlatStyle.System, Visible = false,
    };
    private readonly Label _elapsed = new()
    {
        AutoSize = false, Location = new Point(560, 44), Size = new Size(164, 16),
        Font = Theme.Ui, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleRight,
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };

    private readonly ProgressBar _progress = new()
    {
        Location = new Point(12, 108), Size = new Size(712, 18), Minimum = 0, Maximum = 100,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Label _status = new()
    {
        AutoSize = false, Location = new Point(12, 132), Size = new Size(712, 18),
        Font = Theme.Ui, ForeColor = Color.Gray,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly TextBox _out = new()
    {
        Location = new Point(12, 156), Size = new Size(712, 292),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
    };

    private readonly OperationRunner _runner;
    private string? _ffmpeg;
    private string? _inputPath;
    private string? _outputPath;
    private long _inputBytes;
    private double _durationSeconds;

    // Throttle for the live preview-frame grab (one in flight, at most every ~2s).
    private int _previewBusy;
    private long _lastPreviewTick;

    public TranscodeView()
    {
        Size = new Size(736, 470);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Source:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Output:", AutoSize = true, Location = new Point(12, 46), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Fit onto:", AutoSize = true, Location = new Point(12, 79), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Codec:", AutoSize = true, Location = new Point(250, 79), Font = Theme.Ui });

        foreach (var t in new[]
        {
            new TargetChoice("Keep as is (copy)", 0),
            new TargetChoice("CD-R 700 MB", 737_280_000L),
            new TargetChoice("DVD±R 4.7 GB", 4_700_372_992L),
            new TargetChoice("DVD±R DL 8.5 GB", 8_547_991_552L),
            new TargetChoice("Half the original", -1),
            new TargetChoice("A quarter of the original", -2),
        })
            _target.Items.Add(t);
        _target.SelectedIndex = 2;

        foreach (var c in new[]
        {
            new CodecChoice("H.264 (widest support)", TranscodePlanner.VideoCodec.H264,
                            TranscodePlanner.Container.Mp4),
            new CodecChoice("H.265 (smaller, slower)", TranscodePlanner.VideoCodec.Hevc,
                            TranscodePlanner.Container.Mkv),
            new CodecChoice("MPEG-2 (DVD-compatible)", TranscodePlanner.VideoCodec.Mpeg2,
                            TranscodePlanner.Container.DvdVideoMpeg2),
        })
            _codec.Items.Add(c);
        _codec.SelectedIndex = 0;

        _browse.Click += async (_, _) => await OpenAsync();
        _saveAs.Click += (_, _) => ChooseOutput();
        _encode.Click += async (_, _) => await EncodeAsync();
        _codec.SelectedIndexChanged += (_, _) => SuggestOutput();

        _runner = new OperationRunner(_encode, _cancel, _elapsed);

        Controls.Add(_input); Controls.Add(_browse);
        Controls.Add(_output); Controls.Add(_saveAs);
        Controls.Add(_target); Controls.Add(_codec); Controls.Add(_twoPass);
        Controls.Add(_encode); Controls.Add(_cancel); Controls.Add(_elapsed);
        Controls.Add(_progress); Controls.Add(_status); Controls.Add(_out);

        CheckFfmpeg();
    }

    private sealed record TargetChoice(string Name, long Bytes)
    {
        public override string ToString() => Name;
    }

    private sealed record CodecChoice(string Name, TranscodePlanner.VideoCodec Codec,
                                      TranscodePlanner.Container Container)
    {
        public override string ToString() => Name;
        public string Extension => Container switch
        {
            TranscodePlanner.Container.Mp4 => ".mp4",
            TranscodePlanner.Container.Mkv => ".mkv",
            _ => ".mpg",
        };
    }

    /// <summary>
    /// Find ffmpeg, and say plainly what to do if it isn't there.
    ///
    /// Not bundling it is deliberate — it is a large program under its own
    /// licence, and a disc tool carrying a copy would be both presumptuous and
    /// a maintenance burden. But a feature that silently does nothing because a
    /// dependency is absent is worse than one that explains itself.
    /// </summary>
    private void CheckFfmpeg()
    {
        _ffmpeg = FfmpegRunner.Locate();

        if (_ffmpeg is not null)
        {
            _status.Text = $"FFmpeg: {_ffmpeg}";
            _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            _out.Text =
                "Open a video file, choose what to fit it onto, and press Encode." + Environment.NewLine +
                Environment.NewLine +
                "DiscForge works out the bitrate needed to hit the target size and hands" + Environment.NewLine +
                "the encoding to FFmpeg. Two-pass takes roughly twice as long and spends" + Environment.NewLine +
                "the bits where they matter — worth it when squeezing hard, unnecessary" + Environment.NewLine +
                "when barely compressing." + Environment.NewLine +
                Environment.NewLine +
                "Unencrypted sources only. Re-encoding needs readable video, and DiscForge" + Environment.NewLine +
                "does not decrypt — so home-made discs, camcorder footage and anything you" + Environment.NewLine +
                "authored yourself, but not a commercial DVD.";
            return;
        }

        _status.Text = "FFmpeg not found — encoding is unavailable.";
        _status.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
        _out.Text =
            "FFmpeg does the actual encoding, and it isn't installed — or at least" + Environment.NewLine +
            "isn't on the PATH where DiscForge can find it." + Environment.NewLine +
            Environment.NewLine +
            "It isn't bundled deliberately: it's a large program under its own licence," + Environment.NewLine +
            "and shipping a copy inside a disc tool would be presumptuous and a" + Environment.NewLine +
            "maintenance burden nobody asked for." + Environment.NewLine +
            Environment.NewLine +
            "To install it:" + Environment.NewLine +
            Environment.NewLine +
            "  winget install Gyan.FFmpeg" + Environment.NewLine +
            Environment.NewLine +
            "then restart DiscForge. Or download a build from ffmpeg.org and put the" + Environment.NewLine +
            "folder containing ffmpeg.exe on your PATH." + Environment.NewLine +
            Environment.NewLine +
            "Everything else in DiscForge works without it — this one view is all that" + Environment.NewLine +
            "depends on it.";
    }

    private async Task OpenAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Video files (*.vob;*.mpg;*.mpeg;*.mp4;*.mkv;*.avi;*.m2ts)|" +
                     "*.vob;*.mpg;*.mpeg;*.mp4;*.mkv;*.avi;*.m2ts|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _inputPath = dlg.FileName;
        _input.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        _inputBytes = new FileInfo(dlg.FileName).Length;

        _status.Text = "Reading the file's duration…";
        _status.ForeColor = Color.Gray;

        // Duration decides the bitrate, so it has to come from the file rather
        // than being guessed. ffprobe would be tidier, but ffmpeg reports it on
        // stderr during a zero-length decode and that avoids a second dependency.
        _durationSeconds = await Task.Run(() => ProbeDuration(dlg.FileName));

        if (_durationSeconds <= 0)
        {
            _status.Text = "Could not determine the duration — encoding to a size needs it.";
            _status.ForeColor = Color.FromArgb(0xA0, 0x60, 0x00);
            _out.Text =
                "FFmpeg did not report a duration for this file." + Environment.NewLine +
                Environment.NewLine +
                "Without it there is no way to work out what bitrate hits a target size," +
                Environment.NewLine +
                "so only \"keep as is\" would be meaningful. The file may be a raw stream" +
                Environment.NewLine +
                "with no container, or damaged.";
            _encode.Enabled = false;
            return;
        }

        SuggestOutput();

        var span = TimeSpan.FromSeconds(_durationSeconds);
        _out.Text =
            $"{Path.GetFileName(dlg.FileName)}" + Environment.NewLine +
            $"  {Format(_inputBytes)}, {(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}" +
            Environment.NewLine +
            $"  average {Format((long)(_inputBytes * 8 / _durationSeconds / 8))}/s " +
            $"({_inputBytes * 8.0 / _durationSeconds / 1_000_000:N1} Mbit/s)" +
            Environment.NewLine + Environment.NewLine +
            "Choose a target and press Encode.";

        _status.Text = $"FFmpeg: {_ffmpeg}";
        _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
        _saveAs.Enabled = true;
        _encode.Enabled = _ffmpeg is not null && _outputPath is not null;
    }

    /// <summary>
    /// Ask ffmpeg how long the file is by decoding nothing and reading what it
    /// says on the way past.
    /// </summary>
    private double ProbeDuration(string path)
    {
        if (_ffmpeg is null) return 0;

        double seconds = 0;
        try
        {
            var runner = new FfmpegRunner(_ffmpeg);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _ffmpeg,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return 0;

            string? line;
            while ((line = p.StandardError.ReadLine()) is not null)
            {
                // "  Duration: 01:23:45.67, start: ..., bitrate: ..."
                int at = line.IndexOf("Duration:", StringComparison.Ordinal);
                if (at < 0) continue;

                var rest = line[(at + 9)..].TrimStart();
                int comma = rest.IndexOf(',');
                if (comma > 0) rest = rest[..comma];

                if (TimeSpan.TryParse(rest.Trim(), out var span))
                {
                    seconds = span.TotalSeconds;
                    break;
                }
            }
            p.WaitForExit(15_000);
        }
        catch
        {
            // A file ffmpeg won't open at all reports no duration, which the
            // caller already handles.
        }
        return seconds;
    }

    private void SuggestOutput()
    {
        if (_inputPath is null) return;
        var codec = (CodecChoice)_codec.SelectedItem!;

        string dir = Path.GetDirectoryName(_inputPath) ?? "";
        string name = Path.GetFileNameWithoutExtension(_inputPath) + "-shrunk" + codec.Extension;
        _outputPath = Path.Combine(dir, name);
        _output.Text = _outputPath;

        _encode.Enabled = _ffmpeg is not null && _durationSeconds > 0;
    }

    private void ChooseOutput()
    {
        if (_inputPath is null) return;
        var codec = (CodecChoice)_codec.SelectedItem!;

        using var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileName(_outputPath ?? ""),
            InitialDirectory = Path.GetDirectoryName(_outputPath ?? "") ?? "",
            Filter = codec.Container switch
            {
                TranscodePlanner.Container.Mp4 => "MP4 (*.mp4)|*.mp4",
                TranscodePlanner.Container.Mkv => "Matroska (*.mkv)|*.mkv",
                _ => "MPEG program stream (*.mpg)|*.mpg",
            },
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _outputPath = dlg.FileName;
        _output.Text = dlg.FileName;
    }

    private async Task EncodeAsync()
    {
        if (_ffmpeg is null || _inputPath is null || _outputPath is null) return;

        if (string.Equals(_inputPath, _outputPath, StringComparison.OrdinalIgnoreCase))
        {
            _out.Text = "The output would overwrite the source. Choose a different name.";
            return;
        }

        var target = (TargetChoice)_target.SelectedItem!;
        var codec = (CodecChoice)_codec.SelectedItem!;

        // Work out the ratio the target implies. A negative "bytes" is a
        // shorthand for a fraction of the original, which is easier to reason
        // about than a size when you just want it smaller.
        double ratio = target.Bytes switch
        {
            0 => 1.0,
            -1 => 0.5,
            -2 => 0.25,
            _ => Math.Min(1.0, (double)target.Bytes / Math.Max(1, _inputBytes)),
        };

        long plannedBytes = target.Bytes > 0
            ? target.Bytes
            : (long)(_inputBytes * ratio);

        if (ratio >= 0.999 && target.Bytes > 0)
        {
            _out.Text =
                $"{Path.GetFileName(_inputPath)} is {Format(_inputBytes)}, which already fits" +
                Environment.NewLine +
                $"{target.Name} ({Format(target.Bytes)}). Nothing needs re-encoding." +
                Environment.NewLine + Environment.NewLine +
                "Choosing \"keep as is\" would copy the streams into a new container without" +
                Environment.NewLine +
                "touching the video, which is fast and lossless. Re-encoding anyway would" +
                Environment.NewLine +
                "only lose quality for no benefit.";
            return;
        }

        var plan = new BitBudget.TitlePlan
        {
            Name = Path.GetFileNameWithoutExtension(_inputPath),
            VideoRatio = ratio,
            PlannedVideoBytes = plannedBytes,
            PlannedTotalBytes = plannedBytes,
            Mode = BitBudget.Mode.CustomRatio,
        };

        var encode = TranscodePlanner.ForTitle(
            plan, _inputPath, _outputPath, _durationSeconds,
            codec.Codec, codec.Container, _inputBytes, _twoPass.Checked);

        var args = TranscodePlanner.BuildArgs(encode,
            Path.Combine(Path.GetTempPath(), "discforge-pass-" + Guid.NewGuid().ToString("N")[..8]));

        _progress.Value = 0;
        var log = new StringBuilder();

        _out.Text =
            $"Encoding {Path.GetFileName(_inputPath)}" + Environment.NewLine +
            $"  {Format(_inputBytes)} → about {Format(plannedBytes)} " +
            $"({ratio:P0} of the original)" + Environment.NewLine +
            $"  {encode.Codec}, {(encode.TwoPass ? "two passes" : "one pass")}, " +
            $"{encode.VideoBitrate / 1000:N0} kbit/s" + Environment.NewLine +
            Environment.NewLine;

        var ffmpeg = _ffmpeg;
        var outputPath = _outputPath;
        var inputPath = _inputPath;
        double duration = _durationSeconds;

        // A DVD-Shrink-style window: live preview of the frame being encoded, the
        // progress bar, and the rate / fps / time-remaining. Cancel here cancels
        // the encode.
        var dlg = new ShrinkProgressDialog($"Encoding {Path.GetFileName(inputPath)}");
        dlg.CancelRequested += () => { if (_cancel.Enabled) _cancel.PerformClick(); };
        dlg.Show(this);

        bool ok = await _runner.RunAsync(cancel =>
        {
            var runner = new FfmpegRunner(ffmpeg);
            return runner.Run(encode, args,
                onProgress: p =>
                {
                    if (p.Percent is { } pct)
                    {
                        BeginInvoke(() => _progress.Value = Math.Clamp((int)pct, 0, 100));
                        dlg.SetPercent((int)pct);
                    }

                    string status = $"{p.OutTimeSeconds:N0}s encoded";
                    BeginInvoke(() => _status.Text = status +
                        (p.Fps is { } ff ? $", {ff:N0} fps" : "") +
                        (p.SpeedX is { } ss ? $", {ss:N1}× realtime" : ""));

                    dlg.SetStats(
                        status: "Encoding",
                        rate: p.SpeedX is { } s ? $"{s:N2}× realtime" : "—",
                        fps: p.Fps is { } f ? f.ToString("N0") : "—",
                        timeRemaining: TimeRemaining(p.OutTimeSeconds, duration, p.SpeedX));

                    if (dlg.PreviewEnabled && p.OutTimeSeconds is { } t)
                        MaybeGrabPreview(ffmpeg, inputPath!, t, dlg);
                },
                onLog: line => { lock (log) log.AppendLine(line); },
                ct: cancel);
        },
        ex =>
        {
            _out.Text += "Encoding failed: " + ex.Message;
            AppLog.WriteException("transcode", ex);
        });

        dlg.Finish(ok);

        if (!ok)
        {
            // Either cancelled or ffmpeg returned non-zero. A half-written file
            // is worse than none — it plays for a while and then stops, which
            // looks like corruption rather than an interrupted job.
            TryDelete(outputPath);

            _progress.Value = 0;
            _status.Text = "Stopped.";
            _status.ForeColor = Color.Gray;
            _out.Text +=
                "Encoding did not complete, and the partial output has been deleted." +
                Environment.NewLine + Environment.NewLine +
                "A truncated video file plays for a while and then stops, which looks" +
                Environment.NewLine +
                "like corruption rather than an interrupted job — better to have nothing." +
                Environment.NewLine + Environment.NewLine +
                Tail(log.ToString(), 30);
            return;
        }

        _progress.Value = 100;

        long actual = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
        _status.Text = "Done.";
        _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);

        _out.Text +=
            "Finished." + Environment.NewLine + Environment.NewLine +
            $"  Source  {Format(_inputBytes)}" + Environment.NewLine +
            $"  Target  {Format(plannedBytes)}" + Environment.NewLine +
            $"  Actual  {Format(actual)}" +
            (plannedBytes > 0 ? $"  ({(double)actual / plannedBytes:P0} of target)" : "") +
            Environment.NewLine + Environment.NewLine +
            $"  {outputPath}" + Environment.NewLine + Environment.NewLine +
            "Bitrate targeting is approximate — the encoder aims for an average and" +
            Environment.NewLine +
            "the result lands within a few percent either way. If it overshot and the" +
            Environment.NewLine +
            "target was a disc, pick the next size down and run it again.";

        AppLog.Write($"  transcode: {Path.GetFileName(_inputPath)} → " +
                     $"{Format(actual)} ({encode.Codec}, {encode.VideoBitrate / 1000} kbit/s)");
        StatusBus.Report($"Encoded to {Format(actual)}");
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n');
        if (all.Length <= lines) return text;
        return "FFmpeg's last words:" + Environment.NewLine +
               string.Join(Environment.NewLine, all.Skip(all.Length - lines).Select(l => "  " + l.TrimEnd()));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Estimate wall-clock time left from how much of the video is still to encode
    // and how fast it is going relative to real time.
    private static string TimeRemaining(double? outTime, double duration, double? speed)
    {
        if (outTime is not { } t || duration <= 0 || speed is not { } s || s <= 0.01) return "—";
        double remainingWall = (duration - t) / s;
        if (remainingWall < 0) remainingWall = 0;
        var span = TimeSpan.FromSeconds(remainingWall);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
             : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s"
             : $"{span.Seconds}s";
    }

    // Grab a preview frame at the current encode position, throttled to one in
    // flight and at most every ~2 seconds, off the progress thread.
    private void MaybeGrabPreview(string ffmpeg, string input, double seconds, ShrinkProgressDialog dlg)
    {
        long now = Environment.TickCount64;
        if (now - _lastPreviewTick < 2000) return;
        if (Interlocked.CompareExchange(ref _previewBusy, 1, 0) != 0) return;
        _lastPreviewTick = now;

        _ = Task.Run(() =>
        {
            try
            {
                var img = VideoPreview.Grab(ffmpeg, input, seconds);
                if (img is not null) dlg.SetPreview(img);
            }
            catch { /* preview is best-effort */ }
            finally { Interlocked.Exchange(ref _previewBusy, 0); }
        });
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N2} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):N0} MB",
        >= 1024 => $"{bytes / 1024.0:N0} KB",
        _ => $"{bytes} bytes",
    };
}