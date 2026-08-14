// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Redump;

namespace DiscForge.App.Views;

/// <summary>
/// The redump.org submission-info generator (software half): pick a dump in any format
/// DiscForge can read and it produces the per-track and whole-image CRC-32 / MD5 / SHA-1,
/// sizes, cuesheet and (when a .sub sidecar is present) a LibCrypt/sub-channel summary —
/// with the physical fields left blank for the submitter. A thin shell over
/// <see cref="SubmissionInfoGenerator"/>.
/// </summary>
internal sealed class SubmitView : UserControl
{
    private readonly TextBox _image = new() { ReadOnly = true, Width = 440, Font = Theme.Ui, Location = new Point(90, 14) };
    private readonly TextBox _text = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.Mono,
        Location = new Point(12, 52), Size = new Size(712, 352),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Color.White,
    };
    private readonly Button _bundle = new() { Text = "Bundle…", Location = new Point(568, 12), Width = 70, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly Button _save = new() { Text = "Save…", Location = new Point(644, 12), Width = 70, FlatStyle = FlatStyle.System, Enabled = false };

    private string? _imagePath;
    private string? _generated;
    private SubmissionInfo? _info;

    public SubmitView()
    {
        Size = new Size(736, 416);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) Generate(f[0]); };

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 17), Font = Theme.Ui });
        var pick = new Button { Text = "…", Location = new Point(536, 13), Width = 26, FlatStyle = FlatStyle.System };
        pick.Click += (_, _) => Choose();
        _save.Click += (_, _) => Save();
        _bundle.Click += (_, _) => BuildBundle();

        Controls.AddRange(new Control[] { _image, pick, _bundle, _save, _text });
    }

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void Choose()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Disc images (*.cue;*.bin;*.chd;*.iso;*.cdi;*.nrg)|*.cue;*.bin;*.chd;*.iso;*.cdi;*.nrg|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() == DialogResult.OK) Generate(dlg.FileName);
    }

    private void Generate(string path)
    {
        _imagePath = path; _image.Text = path;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(path);
        try
        {
            var info = SubmissionInfoGenerator.Generate(path);
            _info = info;
            _generated = info.ToRedumpText();
            _text.Text = _generated;
            _save.Enabled = true;
            _bundle.Enabled = true;
            StatusBus.Report($"Submission info: {info.FileName} ({info.Tracks.Count} track(s))");
        }
        catch (Exception ex)
        {
            _generated = null; _info = null; _save.Enabled = false; _bundle.Enabled = false;
            _text.Text = "Error: " + ex.Message;
            AppLog.WriteException("submission-info", ex);
        }
    }

    // Assemble a submission-ready folder: the dump file(s) + info .txt + Logiqx DAT + cuesheet.
    private void BuildBundle()
    {
        if (_info is null || _imagePath is null) return;
        using var folder = new FolderBrowserDialog
        {
            Description = "Build the submission bundle in…",
            SelectedPath = AppSettings.LastExtractDirectory ?? "",
        };
        if (folder.ShowDialog() != DialogResult.OK) return;
        AppSettings.LastExtractDirectory = folder.SelectedPath;

        try
        {
            string game = Path.GetFileNameWithoutExtension(_imagePath);
            var art = SubmissionPackage.Build(_info, game);
            string outDir = folder.SelectedPath;

            // Copy the dump file, and — for a .cue — every FILE it references.
            void CopyInto(string src)
            {
                string dest = Path.Combine(outDir, Path.GetFileName(src));
                if (Path.GetFullPath(src) != Path.GetFullPath(dest)) File.Copy(src, dest, overwrite: true);
            }
            CopyInto(_imagePath);
            if (_imagePath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(_imagePath)) ?? ".";
                foreach (var line in File.ReadAllLines(_imagePath))
                {
                    var t = line.TrimStart();
                    if (!t.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;
                    int a = t.IndexOf('"'), b = t.LastIndexOf('"');
                    if (a >= 0 && b > a)
                    {
                        string refPath = Path.Combine(dir, t.Substring(a + 1, b - a - 1));
                        if (File.Exists(refPath)) CopyInto(refPath);
                    }
                }
            }

            File.WriteAllText(Path.Combine(outDir, game + ".txt"), art.InfoText);
            File.WriteAllText(Path.Combine(outDir, game + ".dat"), art.Dat);
            if (art.Cuesheet is not null) File.WriteAllText(Path.Combine(outDir, game + ".cue"), art.Cuesheet);

            StatusBus.Report($"Submission bundle for \"{game}\" written to {outDir}");
            RetroMessageBox.Show($"Bundle for \"{game}\" written to:\n{outDir}\n\n  {game}.txt, {game}.dat" +
                                 (art.Cuesheet is not null ? $", {game}.cue" : "") + ", and the dump file(s).");
        }
        catch (Exception ex)
        {
            AppLog.WriteException("submission-pack", ex);
            RetroMessageBox.Show("Could not build the bundle: " + ex.Message);
        }
    }

    private void Save()
    {
        if (_generated is null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = (_imagePath is not null ? Path.GetFileNameWithoutExtension(_imagePath) : "submission") + "_submission.txt",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, _generated);
            StatusBus.Report($"Saved {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) { RetroMessageBox.Show(ex.Message); }
    }
}
