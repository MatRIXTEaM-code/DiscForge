// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Provenance;

namespace DiscForge.App.Views;

/// <summary>
/// GUI mirror of <c>dforge dump-ledger</c>: a public, hash-chained log of independently signed claims
/// ("this disc dumps to these exact bytes"), so strangers can see for themselves how many independent
/// submitters agree — without trusting DiscForge or any single submitter. Pure local-file analysis, no
/// live drive; the only cryptography here is ECDSA key generation and signing, both offline. Mirrors
/// <see cref="MergeCertView"/>'s shape: open/verify/inspect a certificate-like artifact, plus (unique
/// to a ledger) append a new independently-signed submission and check consensus for a disc fingerprint.
/// </summary>
internal sealed class DumpCertificateLedgerView : UserControl
{
    private readonly TextBox _ledgerPath = new() { ReadOnly = true, Location = new Point(112, 14), Width = 398, Font = Theme.Ui };
    private readonly Button _open = new() { Text = "Open…", Location = new Point(516, 12), Width = 80, FlatStyle = FlatStyle.System };
    private readonly Button _new = new() { Text = "New…", Location = new Point(600, 12), Width = 70, FlatStyle = FlatStyle.System };
    private readonly Button _verify = new()
    {
        Text = "Verify", Location = new Point(12, 46), Width = 90, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly TextBox _fingerprint = new() { Location = new Point(220, 48), Width = 200, Font = Theme.Ui };
    private readonly Button _consensus = new()
    {
        Text = "Consensus", Location = new Point(428, 46), Width = 90, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly TextBox _keyPath = new() { ReadOnly = true, Location = new Point(112, 80), Width = 398, Font = Theme.Ui };
    private readonly Button _keyPick = new() { Text = "…", Location = new Point(516, 78), Width = 30, FlatStyle = FlatStyle.System };
    private readonly Button _keyGen = new() { Text = "Generate Key…", Location = new Point(552, 78), Width = 118, FlatStyle = FlatStyle.System };

    private readonly TextBox _outputSha = new() { Location = new Point(112, 110), Width = 278, Font = Theme.Mono };
    private readonly TextBox _label = new() { Location = new Point(500, 110), Width = 138, Font = Theme.Ui };
    private readonly Button _submit = new()
    {
        Text = "Submit…", Location = new Point(12, 140), Width = 90, Height = 26, FlatStyle = FlatStyle.System, Enabled = false,
    };

    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Location = new Point(12, 176), Size = new Size(712, 320),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        Font = Theme.Mono, BackColor = Color.White,
    };

    private string? _ledgerFile;
    private string? _keyFile;

    public DumpCertificateLedgerView()
    {
        Size = new Size(736, 520);
        BackColor = Color.White;
        Padding = new Padding(12);

        Controls.Add(new Label { Text = "Ledger:", AutoSize = true, Location = new Point(12, 18), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Fingerprint:", AutoSize = true, Location = new Point(140, 52), Font = Theme.Ui });
        Controls.Add(new Label { Text = "Submitter key:", AutoSize = true, Location = new Point(12, 84), Font = Theme.Ui, ForeColor = Color.Gray });
        Controls.Add(new Label { Text = "Output SHA-256:", AutoSize = true, Location = new Point(12, 114), Font = Theme.Ui, ForeColor = Color.Gray });
        Controls.Add(new Label { Text = "Label (optional):", AutoSize = true, Location = new Point(398, 114), Font = Theme.Ui, ForeColor = Color.Gray });

        _open.Click += (_, _) => DoOpen();
        _new.Click += (_, _) => DoNew();
        _verify.Click += (_, _) => DoVerify();
        _consensus.Click += (_, _) => DoConsensus();
        _keyPick.Click += (_, _) => DoPickKey();
        _keyGen.Click += (_, _) => DoGenerateKey();
        _submit.Click += (_, _) => DoSubmit();

        Controls.AddRange(new Control[]
        {
            _ledgerPath, _open, _new, _verify, _fingerprint, _consensus,
            _keyPath, _keyPick, _keyGen, _outputSha, _label, _submit, _log,
        });

        _log.Text =
            "Open an existing ledger.json, or start a New one.\r\n\r\n" +
            "A dump-certificate ledger is a public, hash-chained log of independently signed claims — " +
            "\"this disc dumps to these exact bytes\" — so strangers can see for themselves how many " +
            "independent submitters agree, without trusting DiscForge or any single submitter.\r\n\r\n" +
            "Verify checks the whole chain is intact and every entry's signature is genuine. Consensus " +
            "groups every submission for one disc fingerprint by distinct submitter key, so a lone bad " +
            "actor re-submitting the same wrong hash can never look like agreement. Submit appends a new, " +
            "independently signed claim — generate a key first if you don't already have one (keep the " +
            "private key file secret; it's what proves a submission is really yours).";
    }

    private void DoOpen()
    {
        using var dlg = new OpenFileDialog { Filter = "Dump-certificate ledger (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        LoadLedgerFile(dlg.FileName);
    }

    private void DoNew()
    {
        using var dlg = new SaveFileDialog { Filter = "Dump-certificate ledger (*.json)|*.json", FileName = "dump-ledger.json" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var ledger = new DumpCertificateLedger();
            File.WriteAllText(dlg.FileName, DumpCertificateLedgerLog.ToJson(ledger));
            LoadLedgerFile(dlg.FileName);
            _log.Text = $"Started {Path.GetFileName(dlg.FileName)}: an empty dump-certificate ledger.";
            AppLog.Write($"dump-ledger init {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            _log.Text = "Could not start a new ledger: " + ex.Message;
            AppLog.WriteException("dump-ledger init", ex);
        }
    }

    private void LoadLedgerFile(string path)
    {
        _ledgerFile = path;
        _ledgerPath.Text = path;
        _verify.Enabled = true;
        _consensus.Enabled = true;
        _submit.Enabled = _keyFile is not null;
        _log.Text = $"Loaded {Path.GetFileName(path)}. Press Verify to check it, or Consensus for one disc fingerprint.";
    }

    private void DoVerify()
    {
        if (_ledgerFile is null) return;
        try
        {
            var ledger = DumpCertificateLedgerLog.FromJson(File.ReadAllText(_ledgerFile));
            bool chain = DumpCertificateLedgerLog.VerifyChain(ledger);
            bool sigs = DumpCertificateLedgerLog.VerifyAllSubmitterSignatures(ledger);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{Path.GetFileName(_ledgerFile)}: {ledger.Entries.Count} entr{(ledger.Entries.Count == 1 ? "y" : "ies")}");
            sb.AppendLine($"  chain       : {(chain ? "INTACT" : "BROKEN — the ledger was altered")}");
            sb.AppendLine($"  signatures  : {(sigs ? "ALL VALID" : "AT LEAST ONE INVALID — an unattested claim is present")}");
            if (chain && sigs)
                sb.AppendLine("  => Verified: every entry is intact, in order, and genuinely attested by the key it names.");
            sb.AppendLine();
            foreach (var e in ledger.Entries)
            {
                sb.AppendLine($"  [{e.Seq}] {e.DiscFingerprint}  {e.Utc}");
                sb.AppendLine($"       -> {e.OutputSha256}");
                if (e.Label is { Length: > 0 }) sb.AppendLine($"       label: {e.Label}");
                sb.AppendLine($"       submitter: {e.SubmitterPublicKey[..Math.Min(20, e.SubmitterPublicKey.Length)]}…");
            }
            _log.Text = sb.ToString();

            StatusBus.Report($"Dump ledger: {Path.GetFileName(_ledgerFile)} — chain {(chain ? "intact" : "BROKEN")}, signatures {(sigs ? "valid" : "INVALID")}");
            AppLog.Write($"dump-ledger verify {Path.GetFileName(_ledgerFile)}: chain={chain} sigs={sigs}");
        }
        catch (Exception ex)
        {
            _log.Text = "Verify failed: " + ex.Message;
            AppLog.WriteException("dump-ledger verify", ex);
        }
    }

    private void DoConsensus()
    {
        if (_ledgerFile is null) return;
        string fingerprint = _fingerprint.Text.Trim();
        if (fingerprint.Length == 0) { _log.Text = "Type a disc fingerprint first."; return; }
        try
        {
            var ledger = DumpCertificateLedgerLog.FromJson(File.ReadAllText(_ledgerFile));
            var consensus = DumpCertificateLedgerLog.Consensus(ledger, fingerprint);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(consensus.Summary());
            foreach (var g in consensus.Groups)
                sb.AppendLine($"  {g.OutputSha256[..Math.Min(16, g.OutputSha256.Length)]}…  {g.SubmitterCount} submitter(s)");
            _log.Text = sb.ToString();

            StatusBus.Report($"Dump ledger consensus for '{fingerprint}': {(consensus.Disputed ? "DISPUTED" : "not disputed")}");
            AppLog.Write($"dump-ledger consensus {fingerprint}: disputed={consensus.Disputed}");
        }
        catch (Exception ex)
        {
            _log.Text = "Consensus failed: " + ex.Message;
            AppLog.WriteException("dump-ledger consensus", ex);
        }
    }

    private void DoPickKey()
    {
        using var dlg = new OpenFileDialog { Filter = "Private key (*.txt;*.key)|*.txt;*.key|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _keyFile = dlg.FileName;
        _keyPath.Text = dlg.FileName;
        _submit.Enabled = _ledgerFile is not null;
    }

    private void DoGenerateKey()
    {
        using var dlg = new SaveFileDialog { Filter = "Private key (*.txt)|*.txt", FileName = "dump-ledger-key.txt" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var (priv, pub) = DumpCertificateLedgerLog.GenerateKey();
            File.WriteAllText(dlg.FileName, priv);
            _keyFile = dlg.FileName;
            _keyPath.Text = dlg.FileName;
            _submit.Enabled = _ledgerFile is not null;
            _log.Text = $"Wrote private key: {Path.GetFileName(dlg.FileName)}  (keep it secret; it signs your submissions).\r\n" +
                       $"Public key (embedded automatically when you submit):\r\n  {pub}";
            AppLog.Write($"dump-ledger keygen {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            _log.Text = "Key generation failed: " + ex.Message;
            AppLog.WriteException("dump-ledger keygen", ex);
        }
    }

    private void DoSubmit()
    {
        if (_ledgerFile is null || _keyFile is null) return;
        string fingerprint = _fingerprint.Text.Trim();
        string outputSha = _outputSha.Text.Trim();
        if (fingerprint.Length == 0) { _log.Text = "Type a disc fingerprint first."; return; }
        if (outputSha.Length == 0) { _log.Text = "Type the dump's output SHA-256 first."; return; }
        try
        {
            var ledger = DumpCertificateLedgerLog.FromJson(File.ReadAllText(_ledgerFile));
            LedgerEntry submission;
            using (var key = DumpCertificateLedgerLog.LoadPrivateKey(File.ReadAllText(_keyFile).Trim()))
                submission = DumpCertificateLedgerLog.CreateSubmission(
                    fingerprint, outputSha, key, _label.Text.Trim() is { Length: > 0 } l ? l : null);
            var entry = DumpCertificateLedgerLog.Append(ledger, submission);
            File.WriteAllText(_ledgerFile, DumpCertificateLedgerLog.ToJson(ledger));

            _log.Text = $"Submitted to {Path.GetFileName(_ledgerFile)}: entry [{entry.Seq}] for {fingerprint} " +
                       $"-> {outputSha[..Math.Min(12, outputSha.Length)]}…, head {ledger.HeadHash?[..12]}…";
            StatusBus.Report($"Dump ledger: submitted entry [{entry.Seq}] to {Path.GetFileName(_ledgerFile)}");
            AppLog.Write($"dump-ledger submit {fingerprint} -> {Path.GetFileName(_ledgerFile)}");
        }
        catch (Exception ex)
        {
            _log.Text = "Submit failed: " + ex.Message;
            AppLog.WriteException("dump-ledger submit", ex);
        }
    }
}
