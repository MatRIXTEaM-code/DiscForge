// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.
//
// A STANDALONE vendor tool for issuing DiscForge licence keys. It is not part of the
// shipped product. The tool's code is GPL like the rest of the tree; the SIGNING KEY is not
// part of the source and stays private to the vendor.

using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;
using DiscForge.Core.Licensing;

namespace DiscForge.LicenseGen;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new GeneratorForm());
    }
}

internal sealed class GeneratorForm : Form
{
    private readonly TextBox _keyPath = new() { Location = new Point(130, 14), Width = 200, ReadOnly = true };
    private readonly TextBox _pubKey = new()
    {
        Location = new Point(130, 44), Width = 360, Height = 44, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8f),
    };
    private readonly TextBox _name = new() { Location = new Point(130, 108), Width = 360 };
    private readonly TextBox _edition = new() { Location = new Point(130, 138), Width = 160, Text = "Standard" };
    private readonly NumericUpDown _days = new() { Location = new Point(130, 168), Width = 80, Minimum = 0, Maximum = 36500 };
    private readonly TextBox _machine = new() { Location = new Point(130, 198), Width = 200 };
    private readonly TextBox _out = new()
    {
        Location = new Point(16, 262), Width = 474, Height = 60, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f),
    };
    private readonly Label _status = new() { Location = new Point(16, 330), Width = 474, Height = 20, ForeColor = Color.Gray };

    private byte[]? _privatePkcs8;

    public GeneratorForm()
    {
        Text = "DiscForge License Generator";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(506, 360);
        Font = new Font("Segoe UI", 9f);

        Label L(string t, int y) => new() { Text = t, AutoSize = true, Location = new Point(16, y + 3) };

        var browse = new Button { Text = "Open…", Location = new Point(336, 12), Width = 72, Height = 24, FlatStyle = FlatStyle.System };
        var newKey = new Button { Text = "New key…", Location = new Point(412, 12), Width = 78, Height = 24, FlatStyle = FlatStyle.System };
        browse.Click += (_, _) => OpenKey();
        newKey.Click += (_, _) => NewKey();

        var copyPub = new Button { Text = "Copy public key", Location = new Point(130, 90), Width = 120, Height = 22, FlatStyle = FlatStyle.System };
        copyPub.Click += (_, _) => Copy(_pubKey.Text, "Public key copied — paste it into LicenseConfig.PublicKeyBase64.");

        var generate = new Button { Text = "Generate licence key", Location = new Point(130, 226), Width = 200, Height = 28, FlatStyle = FlatStyle.System };
        generate.Click += (_, _) => Generate();

        var copyKey = new Button { Text = "Copy key", Location = new Point(340, 226), Width = 74, Height = 28, FlatStyle = FlatStyle.System };
        copyKey.Click += (_, _) => Copy(_out.Text, "Licence key copied.");
        var saveKey = new Button { Text = "Save…", Location = new Point(418, 226), Width = 72, Height = 28, FlatStyle = FlatStyle.System };
        saveKey.Click += (_, _) => SaveKey();

        Controls.AddRange(new Control[]
        {
            L("Signing key:", 14), _keyPath, browse, newKey,
            L("Public key:", 44), _pubKey, copyPub,
            L("Licensee:", 108), _name,
            L("Edition:", 138), _edition,
            L("Valid days:", 168), _days, new Label { Text = "(0 = perpetual)", AutoSize = true, Location = new Point(220, 171), ForeColor = Color.Gray },
            L("Machine id:", 198), _machine, new Label { Text = "(blank = any machine)", AutoSize = true, Location = new Point(340, 201), ForeColor = Color.Gray },
            generate, copyKey, saveKey,
            _out, _status,
        });

        TryLoadDefaultKey();
    }

    private void TryLoadDefaultKey()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "keys", "private.pem"),
                     Path.Combine(Environment.CurrentDirectory, "keys", "private.pem"),
                 })
        {
            if (File.Exists(candidate)) { LoadKey(candidate); return; }
        }
        SetStatus("No signing key loaded — click “New key…” to create one, or “Open…” to load private.pem.", warn: true);
    }

    private void OpenKey()
    {
        using var dlg = new OpenFileDialog { Filter = "Private key (*.pem)|*.pem|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadKey(dlg.FileName);
    }

    private void LoadKey(string path)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(File.ReadAllText(path));
            _privatePkcs8 = ec.ExportPkcs8PrivateKey();
            _keyPath.Text = path;
            _pubKey.Text = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
            SetStatus($"Loaded signing key: {Path.GetFileName(path)}", warn: false);
        }
        catch (Exception ex) { SetStatus("Could not load key: " + ex.Message, warn: true); }
    }

    private void NewKey()
    {
        using var dlg = new SaveFileDialog { Filter = "Private key (*.pem)|*.pem", FileName = "private.pem" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(dlg.FileName, ec.ExportPkcs8PrivateKeyPem());
            string pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
            File.WriteAllText(Path.ChangeExtension(dlg.FileName, ".public.txt"), pub);
            _privatePkcs8 = ec.ExportPkcs8PrivateKey();
            _keyPath.Text = dlg.FileName;
            _pubKey.Text = pub;
            MessageBox.Show(this,
                "New signing key created.\r\n\r\n" +
                "1. Copy the public key (button below) into LicenseConfig.PublicKeyBase64 and rebuild DiscForge.\r\n" +
                "2. Back up the private .pem OFFLINE. Keep it secret — anyone with it can mint keys.",
                "New key pair", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetStatus("New signing key created — now embed the public key and rebuild.", warn: false);
        }
        catch (Exception ex) { SetStatus("Could not create key: " + ex.Message, warn: true); }
    }

    private void Generate()
    {
        if (_privatePkcs8 is null) { SetStatus("Load or create a signing key first.", warn: true); return; }
        if (_name.Text.Trim().Length == 0) { SetStatus("Enter a licensee name.", warn: true); return; }
        try
        {
            int days = (int)_days.Value;
            var info = new LicenseInfo
            {
                Name = _name.Text.Trim(),
                Edition = _edition.Text.Trim().Length == 0 ? "Standard" : _edition.Text.Trim(),
                IssuedUtc = DateTime.UtcNow,
                ExpiresUtc = days > 0 ? DateTime.UtcNow.AddDays(days) : null,
                MachineId = _machine.Text.Trim().Length == 0 ? null : _machine.Text.Trim(),
            };
            _out.Text = License.Issue(info, _privatePkcs8);
            SetStatus($"Generated {(days > 0 ? days + "-day" : "perpetual")} {info.Edition} key for {info.Name}.", warn: false);
        }
        catch (Exception ex) { SetStatus("Generate failed: " + ex.Message, warn: true); }
    }

    private void SaveKey()
    {
        if (_out.Text.Length == 0) { SetStatus("Generate a key first.", warn: true); return; }
        using var dlg = new SaveFileDialog { Filter = "Licence key (*.key)|*.key|Text (*.txt)|*.txt", FileName = "licence.key" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dlg.FileName, _out.Text); SetStatus("Saved " + Path.GetFileName(dlg.FileName), warn: false); }
        catch (Exception ex) { SetStatus("Save failed: " + ex.Message, warn: true); }
    }

    private void Copy(string text, string ok)
    {
        if (text.Length == 0) { SetStatus("Nothing to copy.", warn: true); return; }
        try { Clipboard.SetText(text); SetStatus(ok, warn: false); } catch { }
    }

    private void SetStatus(string text, bool warn)
    {
        _status.Text = text;
        _status.ForeColor = warn ? Color.FromArgb(0xB0, 0x20, 0x20) : Color.FromArgb(0x1C, 0x7C, 0x34);
    }
}
