// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DiscForge.Core.Chd;
using DiscForge.Core.Files;
using DiscForge.Core.Forensics;
using DiscForge.Core.PlayStation;

namespace DiscForge.App.Views;

/// <summary>
/// One front door over DiscForge's verification and conformance checks: is this image structurally sound?
/// Pick a check, drop in an image, and read the verdict — the ISO 9660 / UDF / FAT / HFS linters, the
/// filesystem cross-check for bridge/hybrid discs, CHD archival integrity, and PS2 memory-card ECC. Each is
/// a thin shell over the same Core routines the <c>dforge</c> CLI uses (<c>iso-lint</c>, <c>udf-lint</c>,
/// <c>fat-lint</c>, <c>hfs-lint</c>, <c>fs-verify</c>, <c>chd-verify</c>, <c>ps2mc-ecc</c>). Read-only.
/// </summary>
internal sealed class VerifyView : UserControl
{
    private enum Check
    {
        IsoLint, UdfLint, FatLint, HfsLint, FsVerify, ChdVerify, Ps2CardEcc,
    }

    private readonly ComboBox _op = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui,
        Location = new Point(12, 34), Width = 260,
    };
    private readonly Label _path = new()
    {
        AutoSize = false, Location = new Point(284, 36), Size = new Size(440, 22), Font = Theme.Ui,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Text = "(no file chosen)",
    };
    private readonly TextBox _out = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.Mono,
        Location = new Point(12, 96), Size = new Size(712, 344), BackColor = Color.White,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };
    private string? _file;

    public VerifyView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) { _file = f[0]; _path.Text = _file; Run(); } };

        _op.Items.AddRange(new object[]
        {
            "ISO 9660 lint (iso-lint)", "UDF lint (udf-lint)", "FAT lint (fat-lint)", "HFS lint (hfs-lint)",
            "Filesystem cross-check (fs-verify)", "CHD verify (chd-verify)", "PS2 card ECC (ps2mc-ecc)",
        });
        _op.SelectedIndex = 0;

        var open = new Button { Text = "Choose image…", Location = new Point(12, 62), Width = 130, FlatStyle = FlatStyle.System };
        open.Click += (_, _) => Choose();
        var run = new Button { Text = "Run check", Location = new Point(150, 62), Width = 110, FlatStyle = FlatStyle.System };
        run.Click += (_, _) => Run();

        Controls.Add(new Label { Text = "Check:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(_op);
        Controls.Add(open);
        Controls.Add(run);
        Controls.Add(_path);
        Controls.Add(_out);
    }

    private void Choose()
    {
        using var dlg = new OpenFileDialog { Title = "Choose an image to check", Filter = "Disc images|*.iso;*.cue;*.bin;*.img;*.cdi;*.chd;*.udf;*.ps2;*.mc2|All files|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) { _file = dlg.FileName; _path.Text = _file; Run(); }
    }

    private void Run()
    {
        if (_file is null || !File.Exists(_file)) { _out.Text = "Choose an image first."; return; }
        try
        {
            var check = (Check)_op.SelectedIndex;
            _out.Text = check switch
            {
                Check.IsoLint => IsoLint.Render(IsoLint.Check(File.ReadAllBytes(_file))),
                Check.UdfLint => UdfLint.Render(UdfLint.Check(File.ReadAllBytes(_file))),
                Check.FatLint => FatLint.Render(FatLint.Check(File.ReadAllBytes(_file))),
                Check.HfsLint => HfsLint.Render(HfsLint.Check(File.ReadAllBytes(_file))),
                Check.FsVerify => ImageBrowser.CrossCheck(_file).Summary(),
                Check.ChdVerify => ChdVerify.Check(File.ReadAllBytes(_file)).Summary(),
                Check.Ps2CardEcc => Ps2CardEcc.Verify(File.ReadAllBytes(_file)).Summary(),
                _ => "Unknown check.",
            };
        }
        catch (Exception ex)
        {
            _out.Text = "Error: " + ex.Message;
        }
    }

    private static bool HasFiles(DragEventArgs e) => e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
}
