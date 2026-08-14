// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Convert;

namespace DiscForge.App.Views;

/// <summary>
/// The universal disc-image converter: pick an input image and an output name, and
/// DiscForge reads it into its canonical model and writes it back out in the target
/// format — any of the formats it can read into any of the formats it can write
/// (BIN/CUE, CHD, ISO, CDI, NRG). A thin shell over <see cref="DiscConverter"/>.
/// </summary>
internal sealed class ConvertView : UserControl
{
    private readonly TextBox _in = new() { ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(90, 14) };
    private readonly TextBox _out = new() { ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(90, 44) };
    private readonly Label _status = new() { AutoSize = true, Font = Theme.UiBold, Location = new Point(12, 84) };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 112), Size = new Size(712, 292),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Color.White,
    };
    private readonly Button _go = new() { Text = "Convert", Location = new Point(600, 78), Width = 124, FlatStyle = FlatStyle.System };

    private string? _inPath, _outPath;

    public ConvertView()
    {
        Size = new Size(736, 416);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) SetInput(f[0]); };

        Controls.Add(new Label { Text = "Input:", AutoSize = true, Location = new Point(12, 17), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Output:", AutoSize = true, Location = new Point(12, 47), Font = Theme.Ui });
        var pickIn = new Button { Text = "…", Location = new Point(566, 13), Width = 30, FlatStyle = FlatStyle.System };
        pickIn.Click += (_, _) => ChooseInput();
        var pickOut = new Button { Text = "…", Location = new Point(566, 43), Width = 30, FlatStyle = FlatStyle.System };
        pickOut.Click += (_, _) => ChooseOutput();
        _go.Click += async (_, _) => await ConvertAsync();

        Controls.AddRange(new Control[] { _in, _out, pickIn, pickOut, _go, _status, _log });
        _status.Text = "Choose an input image and an output name.";
        _status.ForeColor = Color.Gray;
    }

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void ChooseInput()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Disc images (*.cue;*.bin;*.chd;*.iso;*.cdi;*.nrg;*.mds;*.gdi;*.ccd;*.cso;*.zso;*.wbfs)|" +
                     "*.cue;*.bin;*.chd;*.iso;*.cdi;*.nrg;*.mds;*.gdi;*.ccd;*.cso;*.zso;*.wbfs|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() == DialogResult.OK) SetInput(dlg.FileName);
    }

    private void SetInput(string path)
    {
        _inPath = path; _in.Text = path;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(path);
    }

    private void ChooseOutput()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "BIN/CUE (*.cue)|*.cue|CHD (*.chd)|*.chd|ISO (*.iso)|*.iso|CDI (*.cdi)|*.cdi|NRG (*.nrg)|*.nrg",
            FileName = _inPath is not null ? Path.GetFileNameWithoutExtension(_inPath) : "output",
        };
        if (dlg.ShowDialog() == DialogResult.OK) { _outPath = dlg.FileName; _out.Text = dlg.FileName; }
    }

    private async Task ConvertAsync()
    {
        if (_inPath is null) { RetroMessageBox.Show("Choose an input image."); return; }
        if (_outPath is null) { RetroMessageBox.Show("Choose an output name."); return; }
        _go.Enabled = false;
        _status.Text = "Converting…"; _status.ForeColor = Color.Black;
        _log.AppendText($"{Path.GetFileName(_inPath)}  ->  {Path.GetFileName(_outPath)}" + Environment.NewLine);
        try
        {
            string inPath = _inPath, outPath = _outPath;
            await Task.Run(() => DiscConverter.Convert(inPath, outPath));
            long size = new FileInfo(_outPath).Length;
            _status.Text = "Done."; _status.ForeColor = Color.FromArgb(0x20, 0x70, 0x20);
            _log.AppendText($"Wrote {Path.GetFileName(_outPath)} ({size:N0} bytes)." + Environment.NewLine);
            StatusBus.Report($"Converted -> {Path.GetFileName(_outPath)}");
            AppLog.Write($"Convert {Path.GetFileName(_inPath)} -> {Path.GetFileName(_outPath)}");
        }
        catch (Exception ex)
        {
            _status.Text = "Conversion failed."; _status.ForeColor = Color.FromArgb(0xA0, 0x30, 0x30);
            _log.AppendText("Error: " + ex.Message + Environment.NewLine);
            RetroMessageBox.Show(ex.Message);
            AppLog.WriteException("convert", ex);
        }
        finally { _go.Enabled = true; }
    }
}
