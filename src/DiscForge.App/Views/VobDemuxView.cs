// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Mpeg;

namespace DiscForge.App.Views;

/// <summary>
/// Splits an unencrypted MPEG program stream (a VOB or MPG) into its elementary
/// video, audio and DVD private (AC3/DTS/LPCM/subpicture) streams. A thin shell
/// over <see cref="MpegProgramStream"/>. Does not decrypt CSS-scrambled content.
/// </summary>
internal sealed class VobDemuxView : UserControl
{
    private readonly TextBox _in = new() { ReadOnly = true, Location = new Point(70, 13), Width = 470, Font = Theme.Ui };
    private readonly TextBox _outDir = new() { ReadOnly = true, Location = new Point(70, 43), Width = 470, Font = Theme.Ui };
    private readonly Button _inPick = new() { Text = "…", Location = new Point(546, 11), Width = 30, FlatStyle = FlatStyle.System };
    private readonly Button _outPick = new() { Text = "…", Location = new Point(546, 41), Width = 30, FlatStyle = FlatStyle.System };
    private readonly Button _go = new() { Text = "Demux", Location = new Point(600, 40), Width = 124, FlatStyle = FlatStyle.System, Enabled = false };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 84), Size = new Size(712, 356),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _inPath, _outPath;

    public VobDemuxView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Input:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Out dir:", AutoSize = true, Location = new Point(12, 46), Font = Theme.Ui });

        _inPick.Click += (_, _) => ChooseIn();
        _outPick.Click += (_, _) => ChooseOut();
        _go.Click += (_, _) => Demux();

        Controls.AddRange(new Control[] { _in, _outDir, _inPick, _outPick, _go, _log });

        _log.Text =
            "Choose an unencrypted VOB or MPG and an output folder, then Demux." + "\r\n\r\n" +
            "Splits the program stream into elementary video/audio/subpicture files." + "\r\n" +
            "A CSS-scrambled VOB stays scrambled — DiscForge does not decrypt it.";
    }

    private void ChooseIn()
    {
        using var d = new OpenFileDialog
        {
            Filter = "MPEG program stream (*.vob;*.mpg;*.mpeg)|*.vob;*.mpg;*.mpeg|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (d.ShowDialog() != DialogResult.OK) return;
        _inPath = d.FileName; _in.Text = d.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(d.FileName);
        UpdateReady();
    }

    private void ChooseOut()
    {
        using var d = new FolderBrowserDialog { Description = "Write elementary streams to…", SelectedPath = AppSettings.LastExtractDirectory ?? "" };
        if (d.ShowDialog() != DialogResult.OK) return;
        _outPath = d.SelectedPath; _outDir.Text = d.SelectedPath;
        AppSettings.LastExtractDirectory = d.SelectedPath;
        UpdateReady();
    }

    private void UpdateReady() => _go.Enabled = _inPath is not null && _outPath is not null;

    private void Demux()
    {
        if (_inPath is null || _outPath is null) return;
        try
        {
            MpegPsDemuxResult r;
            using (var f = File.OpenRead(_inPath)) r = MpegProgramStream.Demux(f);

            var sb = new StringBuilder();
            int written = 0;
            foreach (var st in r.Streams)
            {
                if (st.Data.Length == 0) continue;
                string name = Path.Combine(_outPath, st.SuggestedName());
                File.WriteAllBytes(name, st.Data);
                written++;
                string sub = st.SubStreamId >= 0 ? $"/0x{st.SubStreamId:X2}" : "";
                sb.AppendLine($"{st.Kind,-9} id 0x{st.StreamId:X2}{sub}  {st.Data.Length,12:N0} bytes  ->  {Path.GetFileName(name)}");
            }
            sb.AppendLine();
            sb.AppendLine($"Demuxed {r.PackCount:N0} pack(s), {r.PesPacketCount:N0} PES packet(s), " +
                          $"{written} stream(s) ({(r.SawMpeg2 ? "MPEG-2" : "MPEG-1")}).");
            _log.Text = sb.ToString();
            StatusBus.Report($"Demuxed {Path.GetFileName(_inPath)} → {written} stream(s)");
            AppLog.Write($"vob-demux {Path.GetFileName(_inPath)} -> {written} stream(s)");
        }
        catch (Exception ex)
        {
            _log.Text = "Demux failed: " + ex.Message;
            AppLog.WriteException("vob-demux", ex);
        }
    }
}
