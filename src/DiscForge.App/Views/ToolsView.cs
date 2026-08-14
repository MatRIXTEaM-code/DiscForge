// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Files;

namespace DiscForge.App.Views;

/// <summary>
/// Image housekeeping: checksums (CRC-32 + MD5 + SHA-1 + SHA-256 in one
/// pass, with md5sum-compatible sidecars) and split/join with the verified
/// SFV manifest. The same Core code the CLI uses; this is just buttons.
/// </summary>
internal sealed class ToolsView : UserControl
{
    // --- checksums -----------------------------------------------------------
    private readonly TextBox _sumPath = new() { ReadOnly = true, Width = 420, Font = Theme.Ui };
    private readonly Button _sumBrowse = new() { Text = "Browse…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly Button _sumGo = new() { Text = "Compute", Width = 90, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _sumOut = new()
    {
        Multiline = true, ReadOnly = true, Font = Theme.Mono, ScrollBars = ScrollBars.Vertical,
        Size = new Size(660, 72),
    };
    private readonly ComboBox _sumAlgo = new()
    {
        Width = 100, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
    };
    private readonly Button _sumWrite = new() { Text = "Write sidecar", Width = 100, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly Button _sumVerify = new() { Text = "Verify sidecar", Width = 100, FlatStyle = FlatStyle.System, Enabled = false };

    // --- split / join --------------------------------------------------------
    private readonly TextBox _splitPath = new() { ReadOnly = true, Width = 420, Font = Theme.Ui };
    private readonly Button _splitBrowse = new() { Text = "Browse…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly ComboBox _splitSize = new() { Width = 90, Font = Theme.Ui };
    private readonly Button _splitGo = new() { Text = "Split", Width = 60, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _joinPath = new() { ReadOnly = true, Width = 420, Font = Theme.Ui };
    private readonly Button _joinBrowse = new() { Text = "Browse…", Width = 80, FlatStyle = FlatStyle.System };
    private readonly Button _joinGo = new() { Text = "Join", Width = 90, FlatStyle = FlatStyle.System, Enabled = false };

    private readonly ProgressBar _progress = new() { Size = new Size(660, 14), Style = ProgressBarStyle.Continuous };
    private readonly EventLogView _log = new() { Size = new Size(660, 120) };

    private ImageChecksums.ChecksumSet? _sums;
    private bool _busy;

    public ToolsView()
    {
        Size = new Size(736, 560);
        BackColor = Color.White;
        Padding = new Padding(12);
        AutoScroll = true;

        // ---- checksums group -------------------------------------------------
        var sums = new GroupBox
        {
            Text = "Checksums", Font = Theme.UiBold, ForeColor = Theme.Text,
            Location = new Point(12, 12), Size = new Size(692, 190),
        };
        _sumPath.Location = new Point(12, 26);
        _sumBrowse.Location = new Point(438, 24);
        _sumGo.Location = new Point(524, 24);
        _sumOut.Location = new Point(12, 56);
        _sumOut.Width = 668;
        _sumAlgo.Location = new Point(12, 138);
        _sumAlgo.Items.AddRange(new object[] { "SHA-256", "MD5", "SHA-1", "SFV (CRC-32)", "All" });
        _sumAlgo.SelectedIndex = 0;
        _sumWrite.Location = new Point(120, 136);
        _sumVerify.Location = new Point(228, 136);

        foreach (Control c in new Control[] { _sumPath, _sumBrowse, _sumGo, _sumOut, _sumAlgo, _sumWrite, _sumVerify })
        { c.Font = c == _sumOut ? Theme.Mono : Theme.Ui; sums.Controls.Add(c); }

        _sumBrowse.Click += (_, _) => Pick(_sumPath, () =>
        {
            _sumGo.Enabled = true;
            _sumVerify.Enabled = true;
            _sums = null;
            _sumWrite.Enabled = false;
            _sumOut.Text = "";
        });
        _sumGo.Click += async (_, _) => await ComputeAsync(verifyAfter: false);
        _sumVerify.Click += async (_, _) => await ComputeAsync(verifyAfter: true);
        _sumWrite.Click += (_, _) => WriteSidecar();

        // ---- split / join group ----------------------------------------------
        var split = new GroupBox
        {
            Text = "Split / Join", Font = Theme.UiBold, ForeColor = Theme.Text,
            Location = new Point(12, 212), Size = new Size(692, 130),
        };
        _splitPath.Location = new Point(12, 26);
        _splitBrowse.Location = new Point(438, 24);
        _splitSize.Location = new Point(524, 24);
        _splitSize.Items.AddRange(new object[] { "fat32", "700m", "4g", "1g" });
        _splitSize.Text = "fat32";
        _splitGo.Location = new Point(620, 24);
        _joinPath.Location = new Point(12, 62);
        _joinBrowse.Location = new Point(438, 60);
        _joinGo.Location = new Point(524, 60);
        var hint = new Label
        {
            Text = "Split writes name.001/.002/… plus an .sfv manifest; Join verifies every part's " +
                   "CRC and the final SHA-256 against it.",
            AutoSize = true, Location = new Point(12, 96), Font = Theme.Small, ForeColor = Theme.TextMuted,
        };

        foreach (Control c in new Control[] { _splitPath, _splitBrowse, _splitSize, _splitGo,
                                             _joinPath, _joinBrowse, _joinGo, hint })
        {
            if (c != hint) c.Font = Theme.Ui;      // hint keeps Theme.Small
            split.Controls.Add(c);
        }

        _splitBrowse.Click += (_, _) => Pick(_splitPath, () => _splitGo.Enabled = true);
        _joinBrowse.Click += (_, _) => Pick(_joinPath, () => _joinGo.Enabled = true,
            "Split parts (*.001)|*.001|All files (*.*)|*.*");
        _splitGo.Click += async (_, _) => await SplitAsync();
        _joinGo.Click += async (_, _) => await JoinAsync();

        _progress.Location = new Point(24, 352);
        _log.Location = new Point(24, 376);

        Controls.Add(sums);
        Controls.Add(split);
        Controls.Add(_progress);
        Controls.Add(_log);
    }

    private static void Pick(TextBox target, Action onPicked, string filter = "All files (*.*)|*.*")
    {
        using var dlg = new OpenFileDialog { Filter = filter };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        target.Text = dlg.FileName;
        onPicked();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        // Recompute every button from actual state.
        _sumGo.Enabled = !busy && _sumPath.Text.Length > 0;
        _sumVerify.Enabled = !busy && _sumPath.Text.Length > 0;
        _sumWrite.Enabled = !busy && _sums is not null;
        _splitGo.Enabled = !busy && _splitPath.Text.Length > 0;
        _joinGo.Enabled = !busy && _joinPath.Text.Length > 0;
        if (!busy) _progress.Value = 0;
    }

    private IProgress<double> Bar() => new Progress<double>(f =>
        _progress.Value = Math.Clamp((int)(f * 100), 0, 100));

    private async Task ComputeAsync(bool verifyAfter)
    {
        if (_busy || _sumPath.Text.Length == 0) return;
        var path = _sumPath.Text;
        SetBusy(true);
        try
        {
            _progress.Maximum = 100;
            var bar = Bar();
            var sums = await Task.Run(() => ImageChecksums.ComputeFile(path, bar));
            _sums = sums;
            _sumOut.Text =
                $"CRC-32  {sums.Crc32}\r\n" +
                $"MD5     {sums.Md5}\r\n" +
                $"SHA-1   {sums.Sha1}\r\n" +
                $"SHA-256 {sums.Sha256}";
            _log.Add($"Checksums computed for {Path.GetFileName(path)} ({sums.Length:N0} bytes).",
                EventLogView.Level.Good);

            if (verifyAfter)
            {
                var sidecar = ImageChecksums.FindSidecar(path);
                if (sidecar is null)
                    _log.Add("No sidecar (.sha256/.sha1/.md5/.sfv) found next to the file.",
                        EventLogView.Level.Warn);
                else
                {
                    string got = ImageChecksums.ValueFor(sums, sidecar.Algorithm);
                    bool ok = got.Equals(sidecar.ExpectedHex, StringComparison.OrdinalIgnoreCase);
                    _log.Add(ok
                        ? $"VERIFIED against {Path.GetFileName(sidecar.SidecarPath)} ({sidecar.Algorithm})."
                        : $"MISMATCH: {Path.GetFileName(sidecar.SidecarPath)} says " +
                          $"{sidecar.ExpectedHex}, file is {got}.",
                        ok ? EventLogView.Level.Good : EventLogView.Level.Error);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Add("Checksum failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("tools checksum", ex);
        }
        finally { SetBusy(false); }
    }

    private void WriteSidecar()
    {
        if (_sums is null || _sumPath.Text.Length == 0) return;
        try
        {
            var algos = _sumAlgo.SelectedItem?.ToString() switch
            {
                "MD5" => new[] { "md5" },
                "SHA-1" => new[] { "sha1" },
                "SFV (CRC-32)" => new[] { "crc32" },
                "All" => new[] { "sha256", "md5", "sha1", "crc32" },
                _ => new[] { "sha256" },
            };
            foreach (var a in algos)
                _log.Add("Wrote " + ImageChecksums.WriteSidecar(_sumPath.Text, _sums, a),
                    EventLogView.Level.Good);
        }
        catch (Exception ex)
        {
            _log.Add("Sidecar write failed: " + ex.Message, EventLogView.Level.Error);
        }
    }

    private async Task SplitAsync()
    {
        if (_busy || _splitPath.Text.Length == 0) return;
        var path = _splitPath.Text;
        long size;
        try { size = ImageSplitter.ParsePartSize(_splitSize.Text); }
        catch (Exception ex) { _log.Add(ex.Message, EventLogView.Level.Error); return; }

        SetBusy(true);
        try
        {
            var bar = Bar();
            var result = await Task.Run(() => ImageSplitter.Split(path, size, bar));
            _log.Add($"Split into {result.Parts.Count} part(s), {result.TotalBytes:N0} bytes; " +
                     $"manifest {Path.GetFileName(result.ManifestPath)}.", EventLogView.Level.Good);
            _log.Add($"SHA-256 {result.Sha256}");
        }
        catch (Exception ex)
        {
            _log.Add("Split failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("tools split", ex);
        }
        finally { SetBusy(false); }
    }

    private async Task JoinAsync()
    {
        if (_busy || _joinPath.Text.Length == 0) return;
        var first = _joinPath.Text;
        string suggested = System.Text.RegularExpressions.Regex.IsMatch(first, @"\.\d{3}$")
            ? first[..^4] : first + ".joined";

        using var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested),
            Filter = "All files (*.*)|*.*",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        SetBusy(true);
        try
        {
            var bar = Bar();
            var outPath = dlg.FileName;
            if (File.Exists(outPath)) File.Delete(outPath);   // user confirmed overwrite
            var result = await Task.Run(() => ImageSplitter.Join(first, outPath, bar));
            _log.Add($"Joined {result.Parts} part(s), {result.TotalBytes:N0} bytes — " +
                     (result.Verified ? "CRC + SHA-256 verified." : "NOT verified."),
                result.Verified ? EventLogView.Level.Good : EventLogView.Level.Warn);
            if (result.Warning is not null) _log.Add(result.Warning, EventLogView.Level.Warn);
        }
        catch (Exception ex)
        {
            _log.Add("Join failed: " + ex.Message, EventLogView.Level.Error);
            AppLog.WriteException("tools join", ex);
        }
        finally { SetBusy(false); }
    }
}
