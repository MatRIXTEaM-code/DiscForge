// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DiscForge.Core.Preservation;

namespace DiscForge.App.Views;

/// <summary>
/// A signed, machine-readable account of one dump event: image identity (SHA-256 + a Merkle root
/// over the sectors), the drive/settings context, and the audit result — a thin shell over
/// <see cref="DumpCertificate"/>, mirroring the CLI's <c>dforge dump-cert</c> (create) and
/// <c>dump-cert verify</c>. This is a pure hash/JSON operation over a local file — no live drive
/// involved — which is exactly why it was chosen as the first WinForms view added for the newer
/// forensics tooling: it exercises the "GUI lags the CLI" gap (see docs/NEXT.md) with the lowest
/// possible risk of a mistake that could only show up once actually built and run.
/// </summary>
internal sealed class DumpCertView : UserControl
{
    // ---- create --------------------------------------------------------------
    private readonly TextBox _image = new() { ReadOnly = true, Location = new Point(90, 40), Width = 522, Font = Theme.Ui };
    private readonly Button _imagePick = new() { Text = "…", Location = new Point(618, 38), Width = 30, FlatStyle = FlatStyle.System };
    private readonly TextBox _drive = new() { Location = new Point(90, 70), Width = 170, Font = Theme.Ui };
    private readonly TextBox _firmware = new() { Location = new Point(340, 70), Width = 132, Font = Theme.Ui };
    private readonly TextBox _settings = new() { Location = new Point(90, 100), Width = 382, Font = Theme.Ui };
    private readonly TextBox _note = new() { Location = new Point(90, 130), Width = 382, Font = Theme.Ui };
    private readonly ComboBox _sectorSize = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(90, 160), Width = 160, Font = Theme.Ui,
    };
    private readonly CheckBox _scanProtection = new()
    {
        Text = "Scan for copy protection (best-effort)", Location = new Point(280, 162), Width = 260, Font = Theme.Ui,
    };
    private readonly CheckBox _sign = new()
    {
        Text = "Sign (generates a new key next to the image)", Location = new Point(90, 188), Width = 320, Font = Theme.Ui,
    };
    private readonly Button _create = new()
    {
        Text = "Create Certificate", Location = new Point(560, 186), Width = 164, Height = 28, FlatStyle = FlatStyle.System, Enabled = false,
    };

    // ---- verify ----------------------------------------------------------------
    private readonly TextBox _cert = new() { ReadOnly = true, Location = new Point(90, 244), Width = 522, Font = Theme.Ui };
    private readonly Button _certPick = new() { Text = "…", Location = new Point(618, 242), Width = 30, FlatStyle = FlatStyle.System };
    private readonly Button _verify = new()
    {
        Text = "Verify Certificate", Location = new Point(560, 268), Width = 164, Height = 28, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 312), Size = new Size(712, 140),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _imagePath;
    private string? _certPath;

    public DumpCertView()
    {
        Size = new Size(736, 464);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Image:", AutoSize = true, Location = new Point(12, 42), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Drive:", AutoSize = true, Location = new Point(12, 72), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Firmware:", AutoSize = true, Location = new Point(270, 72), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Settings:", AutoSize = true, Location = new Point(12, 102), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Note:", AutoSize = true, Location = new Point(12, 132), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Sectors:", AutoSize = true, Location = new Point(12, 162), Font = Theme.Ui });
        Controls.Add(new Label
        {
            Text = "Verify an existing certificate:", AutoSize = true, Location = new Point(12, 222), Font = Theme.Ui,
        });
        Controls.Add(new Label { Text = "Certificate:", AutoSize = true, Location = new Point(12, 246), Font = Theme.Ui });

        _sectorSize.Items.AddRange(new object[] { "2352 raw", "2048 cooked" });
        _sectorSize.SelectedIndex = 0;

        _imagePick.Click += (_, _) => PickImage();
        _certPick.Click += (_, _) => PickCert();
        _create.Click += (_, _) => DoCreate();
        _verify.Click += (_, _) => DoVerify();

        Controls.AddRange(new Control[]
        {
            _image, _imagePick, _drive, _firmware, _settings, _note, _sectorSize, _scanProtection, _sign, _create,
            _cert, _certPick, _verify, _log,
        });

        _log.Text =
            "A Dump Certificate is a signed, machine-readable account of one dump event: the image's" + "\r\n" +
            "SHA-256, a Merkle root over every sector (so any single sector can later be proven" + "\r\n" +
            "byte-identical without rehashing the whole image), and the drive/settings context." + "\r\n\r\n" +
            "To create one: pick the dumped image, optionally fill in the drive/settings/note fields" + "\r\n" +
            "a dump-session sidecar would already have, and press Create Certificate. It writes" + "\r\n" +
            "\"<image>.dcert.json\" alongside the image." + "\r\n\r\n" +
            "To verify: pick a \"*.dcert.json\" certificate and press Verify Certificate. If the" + "\r\n" +
            "certified image is still sitting next to it, its hash and Merkle root are re-checked too.";
    }

    private void PickImage()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose the dumped image to certify",
            Filter = "Disc images (*.bin;*.iso;*.img;*.cdi)|*.bin;*.iso;*.img;*.cdi|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _imagePath = dlg.FileName;
        _image.Text = dlg.FileName;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        _create.Enabled = true;
    }

    private void PickCert()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose a dump certificate",
            Filter = "Dump certificates (*.dcert.json)|*.dcert.json|All files (*.*)|*.*",
            InitialDirectory = AppSettings.LastImageDirectory ?? "",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _certPath = dlg.FileName;
        _cert.Text = dlg.FileName;
        _verify.Enabled = true;
    }

    private void DoCreate()
    {
        if (_imagePath is null) return;
        try
        {
            int sectorSize = _sectorSize.SelectedIndex == 1 ? 2048 : 2352;
            DumpCertificate cert;
            using (var fs = File.OpenRead(_imagePath))
                cert = DumpCertificate.Create(fs, Path.GetFileName(_imagePath),
                    DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), sectorSize);

            int unreadable = 0, boundary = 0;
            string sidecar = BadSectorMap.SidecarPath(_imagePath);
            if (File.Exists(sidecar))
            {
                try
                {
                    var map = BadSectorMap.Load(sidecar);
                    unreadable = map.UnreadableLba.Count - map.BoundaryLba.Count;
                    boundary = map.BoundaryLba.Count;
                }
                catch { /* an unparsable sidecar shouldn't block certification, same as the CLI */ }
            }

            string? physicalCaveat = null;
            if (_scanProtection.Checked)
            {
                try
                {
                    var scan = DiscForge.Core.Forensics.CopyProtectionCatalog.FromIso(File.ReadAllBytes(_imagePath));
                    physicalCaveat = scan.PhysicalCaptureCaveat();
                }
                catch { /* best-effort — an unreadable/non-ISO image just yields no caveat */ }
            }

            cert = cert with
            {
                Drive = string.IsNullOrWhiteSpace(_drive.Text) ? null : _drive.Text.Trim(),
                Firmware = string.IsNullOrWhiteSpace(_firmware.Text) ? null : _firmware.Text.Trim(),
                Settings = string.IsNullOrWhiteSpace(_settings.Text) ? null : _settings.Text.Trim(),
                Note = string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim(),
                ToolVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
                UnreadableCount = Math.Max(0, unreadable),
                BoundaryCount = boundary,
                PhysicalCaptureCaveat = physicalCaveat,
            };

            string? keyPath = null;
            if (_sign.Checked)
            {
                var (privB64, _) = DumpLineageLog.GenerateKey();
                keyPath = _imagePath + ".key";
                File.WriteAllText(keyPath, privB64);
                using var priv = DumpLineageLog.LoadPrivateKey(privB64);
                cert = cert.Sign(priv);
            }

            string certOut = DumpCertificate.SidecarPath(_imagePath);
            cert.Save(certOut);

            var sb = new StringBuilder();
            sb.AppendLine($"{cert.Image}: {cert.SectorCount:N0} sector(s) of {cert.SectorSize}");
            sb.AppendLine($"  sha256:  {cert.ImageSha256}");
            sb.AppendLine($"  merkle:  {cert.MerkleRoot}");
            if (unreadable > 0 || boundary > 0)
                sb.AppendLine($"  sectors: {unreadable:N0} unreadable, {boundary:N0} boundary (from the bad-sector sidecar)");
            if (physicalCaveat is { Length: > 0 }) sb.AppendLine($"  caveat:  {physicalCaveat}");
            sb.AppendLine($"Wrote {Path.GetFileName(certOut)}.");
            if (keyPath is not null) sb.AppendLine($"Signed — private key saved to {Path.GetFileName(keyPath)} (keep it if you'll sign more certificates the same way).");
            _log.Text = sb.ToString();

            _certPath = certOut;
            _cert.Text = certOut;
            _verify.Enabled = true;

            StatusBus.Report($"Certified {Path.GetFileName(_imagePath)} → {Path.GetFileName(certOut)}");
            AppLog.Write($"dump-cert create {Path.GetFileName(_imagePath)} -> {Path.GetFileName(certOut)}");
        }
        catch (Exception ex)
        {
            _log.Text = "Create certificate failed: " + ex.Message;
            AppLog.WriteException("dump-cert create", ex);
        }
    }

    private void DoVerify()
    {
        if (_certPath is null) return;
        try
        {
            var cert = DumpCertificate.Load(_certPath);
            bool sigOk = cert.VerifySignature();

            bool? imageOk = null;
            string candidateImage = Path.Combine(
                Path.GetDirectoryName(_certPath) is { Length: > 0 } d ? d : ".", cert.Image);
            if (File.Exists(candidateImage))
            {
                using var fs = File.OpenRead(candidateImage);
                imageOk = cert.VerifyImage(fs);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{Path.GetFileName(_certPath)}: {cert.Image}, {cert.SectorCount:N0} sector(s) of {cert.SectorSize}, dumped {cert.CreatedUtc}");
            if (cert.Drive is not null) sb.AppendLine($"  drive:      {cert.Drive}{(cert.Firmware is not null ? $" fw {cert.Firmware}" : "")}");
            if (cert.Settings is not null) sb.AppendLine($"  settings:   {cert.Settings}");
            if (cert.AuditGrade is not null) sb.AppendLine($"  audit:      {cert.AuditGrade}");
            if (cert.UnreadableCount > 0 || cert.BoundaryCount > 0)
                sb.AppendLine($"  sectors:    {cert.UnreadableCount:N0} unreadable, {cert.BoundaryCount:N0} boundary");
            sb.AppendLine($"  merkle:     {cert.MerkleRoot}");
            sb.AppendLine(!cert.Signed ? "  signature:  (unsigned)" : $"  signature:  {(sigOk ? "VALID" : "INVALID")}");
            sb.AppendLine(imageOk switch
            {
                null => "  image:      (not found next to the certificate — hash/Merkle not re-checked)",
                true => "  image:      matches the certificate (file hash + Merkle root)",
                false => "  image:      DOES NOT match the certificate",
            });
            if (cert.PhysicalCaptureCaveat is { Length: > 0 }) sb.AppendLine($"  caveat:     {cert.PhysicalCaptureCaveat}");
            _log.Text = sb.ToString();

            bool overallOk = (cert.Signed ? sigOk : true) && imageOk != false;
            StatusBus.Report(overallOk
                ? $"Certificate OK — {Path.GetFileName(_certPath)}"
                : $"Certificate FAILED verification — {Path.GetFileName(_certPath)}");
            AppLog.Write($"dump-cert verify {Path.GetFileName(_certPath)} -> {(overallOk ? "OK" : "FAILED")}");
        }
        catch (Exception ex)
        {
            _log.Text = "Verify certificate failed: " + ex.Message;
            AppLog.WriteException("dump-cert verify", ex);
        }
    }
}
