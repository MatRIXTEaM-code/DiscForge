// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Preservation;
using DiscForge.Core.Recovery;

namespace DiscForge.App.Views;

/// <summary>
/// Multi-read recovery with a signed, auditable paper trail: merge several imperfect rips of the
/// SAME disc the way <see cref="MergeView"/> does, but additionally honour each source's bad-sector
/// map (so a copy's known-unreadable sectors are excluded from the vote rather than treated as
/// evidence) and emit a <see cref="MergeCertificate"/> — a checkable account of how every sector was
/// decided, who supplied it, and (optionally) an ECDSA signature over the result. A thin shell over
/// <see cref="ProvenanceMerge"/>, mirroring the CLI's <c>merge-cert</c> command.
/// </summary>
internal sealed class MergeCertView : UserControl
{
    private readonly ListBox _sources = new()
    {
        Location = new Point(70, 40), Size = new Size(542, 106), Font = Theme.Ui,
        HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended,
    };
    private readonly Button _add = new()
    {
        Text = "Add…", Location = new Point(620, 40), Width = 104, Height = 26, FlatStyle = FlatStyle.System,
    };
    private readonly Button _remove = new()
    {
        Text = "Remove", Location = new Point(620, 72), Width = 104, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly TextBox _out = new()
    {
        ReadOnly = true, Location = new Point(70, 156), Width = 542, Font = Theme.Ui,
    };
    private readonly Button _outPick = new()
    {
        Text = "…", Location = new Point(618, 154), Width = 30, FlatStyle = FlatStyle.System,
    };
    private readonly ComboBox _sectorSize = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(70, 186), Width = 240, Font = Theme.Ui,
    };
    private readonly CheckBox _sign = new()
    {
        Text = "Sign the certificate", Location = new Point(70, 216), Width = 220, Font = Theme.Ui,
    };
    private readonly Button _merge = new()
    {
        Text = "Merge + Certify", Location = new Point(560, 214), Width = 164, Height = 28, FlatStyle = FlatStyle.System, Enabled = false,
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 252), Size = new Size(712, 200),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _outPath;

    public MergeCertView()
    {
        Size = new Size(736, 464);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Sources:", AutoSize = true, Location = new Point(12, 16), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "two or more rips of the SAME disc — a \"<source>.badsectors.json\" sidecar,\r\n" +
                   "if present next to a source, is honoured automatically",
            AutoSize = true, Location = new Point(70, 18), Font = Theme.Small, ForeColor = Color.Gray,
        });
        Controls.Add(new Label { Text = "Output:", AutoSize = true, Location = new Point(12, 159), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Sectors:", AutoSize = true, Location = new Point(12, 189), Font = Theme.Ui });

        _sectorSize.Items.AddRange(new object[] { "2352 raw (EDC-verified)", "2048 cooked (voting only)" });
        _sectorSize.SelectedIndex = 0;

        _add.Click += (_, _) => AddSources();
        _remove.Click += (_, _) => RemoveSelected();
        _outPick.Click += (_, _) => ChooseOutput();
        _merge.Click += (_, _) => DoMerge();
        _sources.SelectedIndexChanged += (_, _) => _remove.Enabled = _sources.SelectedItems.Count > 0;

        Controls.AddRange(new Control[]
        {
            _sources, _add, _remove, _out, _outPick, _sectorSize, _sign, _merge, _log,
        });

        _log.Text =
            "Add two or more rips of the same disc, choose an output file, and press Merge + Certify." + "\r\n\r\n" +
            "Beyond a plain merge, this records HOW each sector was decided (agreement, EDC-verified," + "\r\n" +
            "majority vote, single-source, or genuinely unrecovered) and who supplied it, then writes" + "\r\n" +
            "that account to \"<output>.dmc.json\" alongside the image. A bad-sector sidecar next to a" + "\r\n" +
            "source excludes its known-unreadable sectors from the vote instead of letting a zero-filled" + "\r\n" +
            "read count as evidence. Checking \"Sign\" adds a fresh ECDSA signature over the certificate" + "\r\n" +
            "so the reconstruction can be verified later with `dforge merge-cert verify`.";
    }

    private void AddSources()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Add rip(s) of the same disc",
            Filter = "Disc images (*.bin;*.iso;*.img)|*.bin;*.iso;*.img|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) _sources.Items.Add(f);
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileNames[0]);
        UpdateReady();
    }

    private void RemoveSelected()
    {
        for (int i = _sources.SelectedIndices.Count - 1; i >= 0; i--)
            _sources.Items.RemoveAt(_sources.SelectedIndices[i]);
        UpdateReady();
    }

    private void ChooseOutput()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save the merged, certified image",
            Filter = "Disc image (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = "merged.bin",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _outPath = dlg.FileName;
        _out.Text = dlg.FileName;
        UpdateReady();
    }

    private void UpdateReady() => _merge.Enabled = _sources.Items.Count >= 2 && _outPath is not null;

    private void DoMerge()
    {
        if (_outPath is null || _sources.Items.Count < 2) return;
        try
        {
            var paths = _sources.Items.Cast<string>().ToList();
            var images = paths.Select(File.ReadAllBytes).ToList();

            var holeMaps = new BadSectorMap?[paths.Count];
            var sidecarsUsed = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                var sidecar = BadSectorMap.SidecarPath(paths[i]);
                if (File.Exists(sidecar))
                {
                    try { holeMaps[i] = BadSectorMap.Load(sidecar); sidecarsUsed++; }
                    catch { /* an unparsable sidecar is skipped, not fatal — same as the CLI */ }
                }
            }

            int ss = _sectorSize.SelectedIndex == 1 ? 2048 : 2352;
            var result = ProvenanceMerge.Merge(images, holeMaps, ss);
            var cert = result.Certificate;

            if (_sign.Checked)
            {
                var (privB64, _) = DumpLineageLog.GenerateKey();
                using var priv = DumpLineageLog.LoadPrivateKey(privB64);
                cert = cert.Sign(priv);
            }

            File.WriteAllBytes(_outPath, result.Image);
            string certPath = _outPath + ".dmc.json";
            cert.Save(certPath);

            var sb = new StringBuilder();
            sb.AppendLine(cert.Summary());
            sb.AppendLine($"Wrote {Path.GetFileName(_outPath)} and {Path.GetFileName(certPath)}.");
            if (sidecarsUsed > 0)
                sb.AppendLine($"Honoured {sidecarsUsed} bad-sector sidecar(s) — their known-unreadable sectors were excluded from voting.");
            if (cert.FullyRecovered)
                sb.AppendLine("Fully recovered — every sector is either agreed, EDC-verified, or the sole surviving copy.");
            else
            {
                sb.AppendLine($"{cert.Unrecovered:N0} sector(s) could not be recovered from these copies.");
                sb.AppendLine("First unrecovered: " + string.Join(", ", cert.UnrecoveredSectors.Take(24))
                              + (cert.UnrecoveredSectors.Count > 24 ? " …" : ""));
            }
            if (_sign.Checked)
                sb.AppendLine("Signed — verify later with `dforge merge-cert verify " + Path.GetFileName(certPath) + "`.");
            _log.Text = sb.ToString();

            StatusBus.Report($"Merged + certified {images.Count} rip(s) → {Path.GetFileName(_outPath)}");
            AppLog.Write($"merge-cert {images.Count} sources -> {Path.GetFileName(_outPath)} ({cert.Summary()})");
        }
        catch (Exception ex)
        {
            _log.Text = "Merge + certify failed: " + ex.Message;
            AppLog.WriteException("merge-cert", ex);
        }
    }
}
