// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;
using DiscForge.Core.Patch;

namespace DiscForge.App.Views;

/// <summary>
/// Apply, revert and build PlayStation Patch Files (PPF) — the format
/// PPF-O-Matic, the PPF Patch Engine and PAL region patchers use. This is the
/// GUI face of DiscForge.Core.Patch: point it at a .ppf and a disc image (a PS1
/// BIN, most often), and it applies the edit list a translation or region fix
/// ships as. Validation is on by default, so a patch built for the wrong dump
/// is refused before it can corrupt the image.
///
/// Deliberately a thin shell over Core — the format work, the validation and the
/// undo all live in <see cref="PpfPatch"/>, which is unit-tested; this view only
/// gathers paths and reports what Core did.
/// </summary>
internal sealed class PatchView : UserControl
{
    private readonly TextBox _patch = new() { ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(120, 14) };
    private readonly TextBox _image = new() { ReadOnly = true, Width = 470, Font = Theme.Ui, Location = new Point(120, 46) };
    private readonly Label _summary = new() { AutoSize = true, Font = Theme.UiBold, Location = new Point(12, 84) };
    private readonly CheckBox _force = new() { Text = "Skip validation (--force)", Location = new Point(12, 110), AutoSize = true, Font = Theme.Ui };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono,
        Location = new Point(12, 172), Size = new Size(712, 268),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };

    private string? _patchPath;
    private string? _imagePath;
    private PpfPatchFile? _parsed;

    private enum PatchKind { None, Ppf, Ips, Bps }
    private PatchKind _kind = PatchKind.None;
    private IpsPatchFile? _ips;
    private BpsPatchFile? _bps;

    public PatchView()
    {
        Size = new Size(736, 452);
        BackColor = Color.White;
        Padding = new Padding(12);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => AcceptDrop(e);

        var openPatch = new Button { Text = "Patch (.ppf)…", Location = new Point(12, 12), Width = 100, FlatStyle = FlatStyle.System };
        openPatch.Click += (_, _) => ChoosePatch();
        var openImage = new Button { Text = "Image (.bin)…", Location = new Point(12, 44), Width = 100, FlatStyle = FlatStyle.System };
        openImage.Click += (_, _) => ChooseImage();

        var apply = new Button { Text = "Apply patch", Location = new Point(600, 12), Width = 124, FlatStyle = FlatStyle.System };
        apply.Click += (_, _) => DoApply(undo: false);
        var undo = new Button { Text = "Revert (undo)", Location = new Point(600, 44), Width = 124, FlatStyle = FlatStyle.System };
        undo.Click += (_, _) => DoApply(undo: true);
        var convert = new Button { Text = "Convert…", Location = new Point(600, 76), Width = 124, FlatStyle = FlatStyle.System };
        convert.Click += (_, _) => DoConvert();
        var edit = new Button { Text = "Edit metadata…", Location = new Point(600, 108), Width = 124, FlatStyle = FlatStyle.System };
        edit.Click += (_, _) => DoEdit();
        var create = new Button { Text = "Create patch…", Location = new Point(600, 140), Width = 124, FlatStyle = FlatStyle.System };
        create.Click += (_, _) => DoCreate();

        Controls.AddRange(new Control[]
        {
            openPatch, openImage, apply, undo, convert, edit, create,
            _patch, _image, _summary, _force, _log,
        });
    }

    // ---- input --------------------------------------------------------------

    private static bool HasFiles(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };

    private void AcceptDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f);
            if (ext.Equals(".ppf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ips", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bps", StringComparison.OrdinalIgnoreCase)) SetPatch(f);
            else SetImage(f);
        }
    }

    private void ChoosePatch()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Patches (*.ppf;*.ips;*.bps)|*.ppf;*.ips;*.bps|PPF (*.ppf)|*.ppf|IPS (*.ips)|*.ips|BPS (*.bps)|*.bps|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == DialogResult.OK) SetPatch(dlg.FileName);
    }

    private void ChooseImage()
    {
        using var dlg = new OpenFileDialog { Filter = "Disc images (*.bin;*.img;*.iso)|*.bin;*.img;*.iso|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == DialogResult.OK) SetImage(dlg.FileName);
    }

    private void SetPatch(string path)
    {
        _patchPath = path;
        _patch.Text = path;
        _parsed = null; _ips = null; _bps = null; _kind = PatchKind.None;
        var ext = Path.GetExtension(path);
        try
        {
            if (ext.Equals(".ips", StringComparison.OrdinalIgnoreCase))
            {
                _ips = IpsPatch.ParseFile(path);
                _kind = PatchKind.Ips;
                _summary.Text = $"IPS · {_ips.Records.Count:N0} change(s)" +
                                (_ips.TruncateLength is { } t ? $" · truncates to {t:N0}" : "");
            }
            else if (ext.Equals(".bps", StringComparison.OrdinalIgnoreCase))
            {
                _bps = BpsPatch.ParseFile(path);
                _kind = PatchKind.Bps;
                _summary.Text = $"BPS · source {_bps.SourceSize:N0} → target {_bps.TargetSize:N0} bytes · CRC-verified" +
                                (_bps.Metadata.Length > 0 ? " · has metadata" : "");
            }
            else
            {
                _parsed = PpfPatch.ParseFile(path);
                _kind = PatchKind.Ppf;
                _summary.Text = Describe(_parsed);
                if (_parsed.FileId is not null) Log(_parsed.FileId);
            }
            Log($"Loaded {Path.GetFileName(path)} — {_summary.Text}");
        }
        catch (Exception ex)
        {
            _kind = PatchKind.None;
            _summary.Text = "Not a valid patch file.";
            Log($"Could not read patch: {ex.Message}");
        }
    }

    private void SetImage(string path)
    {
        _imagePath = path;
        _image.Text = path;
        Log($"Target image: {Path.GetFileName(path)} ({new FileInfo(path).Length:N0} bytes)");
    }

    private static string Describe(PpfPatchFile p) =>
        $"PPF {p.Version.ToString()[1..]}.0 · {p.Records.Count:N0} change(s) · " +
        (p.CanUndo ? "undoable" : "no undo") + " · " +
        (p.HasValidationBlock ? "validated" : "no validation") +
        (p.Description.Length > 0 ? $" · \"{p.Description}\"" : "");

    // ---- actions ------------------------------------------------------------

    private void DoApply(bool undo)
    {
        if (_kind is PatchKind.Ips or PatchKind.Bps)
        {
            ApplyIpsOrBps(undo);
            return;
        }
        if (_parsed is null) { RetroMessageBox.Show("Open a patch first."); return; }
        if (_imagePath is null) { RetroMessageBox.Show("Choose the image to patch."); return; }

        try
        {
            if (undo && !_parsed.CanUndo)
            {
                RetroMessageBox.Show("This patch has no undo data, so it cannot be reverted. " +
                                     "Only PPF 3.0 patches written with undo can be undone.");
                return;
            }

            using var image = new FileStream(_imagePath, FileMode.Open, FileAccess.ReadWrite);

            if (!undo)
            {
                var check = PpfPatch.CheckApplicable(_parsed, image);
                if (check.ValidationMatched) Log("Validation block matches — this is the patch's target image.");
                if (!check.Ok && !_force.Checked)
                {
                    Log("Refused: " + check.Problem);
                    RetroMessageBox.Show(check.Problem!);
                    return;
                }
                if (!check.Ok) Log("Warning (overridden): " + check.Problem);
            }

            int n = undo
                ? PpfPatch.Undo(_parsed, image, _force.Checked)
                : PpfPatch.Apply(_parsed, image, _force.Checked);

            string what = undo ? "Reverted" : "Applied";
            Log($"{what} {n:N0} record(s) to {Path.GetFileName(_imagePath)}.");
            StatusBus.Report($"{what} {n:N0} record(s) — {Path.GetFileName(_imagePath)}");
            AppLog.Write($"PPF {what.ToLowerInvariant()} {Path.GetFileName(_patchPath!)} " +
                         $"-> {Path.GetFileName(_imagePath)} ({n} records)");
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            AppLog.WriteException("ppf apply", ex);
        }
    }

    private void ApplyIpsOrBps(bool undo)
    {
        if (undo) { RetroMessageBox.Show("IPS and BPS patches carry no undo data. Keep a backup of the original image."); return; }
        if (_imagePath is null) { RetroMessageBox.Show("Choose the image to patch."); return; }
        try
        {
            var source = File.ReadAllBytes(_imagePath);
            byte[] output;
            string kindName;
            if (_kind == PatchKind.Ips)
            {
                output = IpsPatch.Apply(_ips!, source);
                kindName = "IPS";
            }
            else
            {
                output = BpsPatch.Apply(_bps!, source, verifySource: !_force.Checked);
                kindName = "BPS";
            }

            // IPS/BPS can change the file's size, so write the whole output. Offer to
            // write beside the source rather than overwrite it in place.
            using var save = new SaveFileDialog
            {
                Title = $"Save patched image ({kindName})",
                Filter = "Disc images (*.bin;*.img;*.iso)|*.bin;*.img;*.iso|All files (*.*)|*.*",
                FileName = Path.GetFileNameWithoutExtension(_imagePath) + "_patched" + Path.GetExtension(_imagePath),
            };
            if (save.ShowDialog() != DialogResult.OK) return;
            File.WriteAllBytes(save.FileName, output);
            Log($"Applied {kindName} patch → {Path.GetFileName(save.FileName)} ({output.Length:N0} bytes).");
            StatusBus.Report($"Applied {kindName} patch — {Path.GetFileName(save.FileName)}");
            AppLog.Write($"{kindName} apply {Path.GetFileName(_patchPath!)} -> {Path.GetFileName(save.FileName)}");
        }
        catch (Exception ex)
        {
            Log($"Apply failed: {ex.Message}");
            RetroMessageBox.Show(ex.Message);
        }
    }

    private void DoCreate()
    {
        using var origDlg = new OpenFileDialog { Title = "Original (unpatched) image", Filter = "Disc images (*.bin;*.img;*.iso)|*.bin;*.img;*.iso|All files (*.*)|*.*" };
        if (origDlg.ShowDialog() != DialogResult.OK) return;
        using var modDlg = new OpenFileDialog { Title = "Modified image", Filter = "Disc images (*.bin;*.img;*.iso)|*.bin;*.img;*.iso|All files (*.*)|*.*" };
        if (modDlg.ShowDialog() != DialogResult.OK) return;
        using var outDlg = new SaveFileDialog
        {
            Title = "Save patch as",
            Filter = "PPF patch (*.ppf)|*.ppf|IPS patch (*.ips)|*.ips|BPS patch (*.bps)|*.bps",
            FileName = "patch.ppf",
        };
        if (outDlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var ext = Path.GetExtension(outDlg.FileName);
            if (ext.Equals(".ips", StringComparison.OrdinalIgnoreCase))
            {
                var ips = IpsPatch.Create(File.ReadAllBytes(origDlg.FileName), File.ReadAllBytes(modDlg.FileName));
                File.WriteAllBytes(outDlg.FileName, ips);
                Log($"Wrote {Path.GetFileName(outDlg.FileName)}: IPS, {IpsPatch.Parse(ips).Records.Count:N0} change(s), {ips.Length:N0} bytes.");
            }
            else if (ext.Equals(".bps", StringComparison.OrdinalIgnoreCase))
            {
                var bps = BpsPatch.Create(File.ReadAllBytes(origDlg.FileName), File.ReadAllBytes(modDlg.FileName));
                File.WriteAllBytes(outDlg.FileName, bps);
                Log($"Wrote {Path.GetFileName(outDlg.FileName)}: BPS, {bps.Length:N0} bytes (source/target CRC-32 embedded).");
            }
            else
            {
                var ppf = PpfPatch.CreateFromFiles(origDlg.FileName, modDlg.FileName);
                File.WriteAllBytes(outDlg.FileName, ppf);
                Log($"Wrote {Path.GetFileName(outDlg.FileName)}: PPF 3.0, {PpfPatch.Parse(ppf).Records.Count:N0} change(s), {ppf.Length:N0} bytes.");
            }
            SetPatch(outDlg.FileName);
        }
        catch (Exception ex)
        {
            Log("Could not create patch: " + ex.Message);
            RetroMessageBox.Show(ex.Message);
        }
    }

    private void DoConvert()
    {
        if (_parsed is null) { RetroMessageBox.Show("Open a .ppf patch first."); return; }

        using var dlg = new PpfConvertDialog(_parsed.Version);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int v = VersionNumber(dlg.Target);
        using var save = new SaveFileDialog
        {
            Filter = "PPF patches (*.ppf)|*.ppf",
            FileName = Path.GetFileNameWithoutExtension(_patchPath ?? "patch") + $"_v{v}.ppf",
        };
        if (save.ShowDialog() != DialogResult.OK) return;

        try
        {
            var bytes = PpfPatch.ConvertTo(_parsed, dlg.Target);
            File.WriteAllBytes(save.FileName, bytes);
            var parsed = PpfPatch.Parse(bytes);
            Log($"Converted to PPF {v}.0 → {Path.GetFileName(save.FileName)} " +
                $"({bytes.Length:N0} bytes, {parsed.Records.Count:N0} change(s)).");
            StatusBus.Report($"Converted to PPF {v}.0 — {Path.GetFileName(save.FileName)}");
            AppLog.Write($"PPF convert {Path.GetFileName(_patchPath!)} -> v{v} ({Path.GetFileName(save.FileName)})");
        }
        catch (Exception ex)
        {
            Log("Convert failed: " + ex.Message);
            RetroMessageBox.Show(ex.Message);
        }
    }

    private void DoEdit()
    {
        if (_parsed is null) { RetroMessageBox.Show("Open a .ppf patch first."); return; }

        using var dlg = new PpfEditDialog(_parsed.Description, _parsed.FileId);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        using var save = new SaveFileDialog
        {
            Filter = "PPF patches (*.ppf)|*.ppf",
            FileName = Path.GetFileName(_patchPath ?? "patch.ppf"),
        };
        if (save.ShowDialog() != DialogResult.OK) return;

        try
        {
            var edited = PpfPatch.WithMetadata(_parsed, dlg.Description, dlg.FileId);
            var bytes = PpfPatch.Serialize(edited);
            File.WriteAllBytes(save.FileName, bytes);
            Log($"Saved metadata edit → {Path.GetFileName(save.FileName)} ({bytes.Length:N0} bytes).");
            StatusBus.Report($"Edited PPF metadata — {Path.GetFileName(save.FileName)}");
            AppLog.Write($"PPF edit metadata {Path.GetFileName(save.FileName)}");
            SetPatch(save.FileName);
        }
        catch (Exception ex)
        {
            Log("Edit failed: " + ex.Message);
            RetroMessageBox.Show(ex.Message);
        }
    }

    private static int VersionNumber(PpfVersion v) => v switch
    {
        PpfVersion.V1 => 1,
        PpfVersion.V2 => 2,
        _ => 3,
    };

    private void Log(string line) => _log.AppendText(line + Environment.NewLine);
}

/// <summary>Pick a target PPF revision for a conversion.</summary>
internal sealed class PpfConvertDialog : Form
{
    private readonly ComboBox _target = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Font = Theme.Ui, Location = new Point(90, 16),
    };

    public PpfVersion Target => _target.SelectedIndex switch
    {
        0 => PpfVersion.V1,
        1 => PpfVersion.V2,
        _ => PpfVersion.V3,
    };

    public PpfConvertDialog(PpfVersion current)
    {
        Text = "Convert PPF";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 120);
        Font = Theme.Ui;

        _target.Items.AddRange(new object[] { "PPF 1.0", "PPF 2.0", "PPF 3.0" });
        _target.SelectedIndex = current switch { PpfVersion.V1 => 0, PpfVersion.V2 => 1, _ => 2 };

        Controls.Add(new Label { Text = "Target:", AutoSize = true, Location = new Point(12, 19) });
        Controls.Add(new Label
        {
            Text = "1.0 = records only · 2.0 needs a validation block · 3.0 adds undo",
            AutoSize = true, Location = new Point(12, 52), ForeColor = Color.Gray, Font = Theme.Ui,
        });

        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK,
            Location = new Point(208, 82), Width = 80, FlatStyle = FlatStyle.System,
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(294, 82), Width = 80, FlatStyle = FlatStyle.System,
        };

        Controls.Add(_target); Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>Edit a patch's description and file_id.diz in place.</summary>
internal sealed class PpfEditDialog : Form
{
    private readonly TextBox _desc = new()
    {
        Width = 340, Font = Theme.Ui, Location = new Point(90, 16), MaxLength = 50,
    };
    private readonly TextBox _fileId = new()
    {
        Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
        Width = 340, Height = 90, Font = Theme.Mono, Location = new Point(90, 46),
    };

    public string Description => _desc.Text.Trim();
    public string? FileId => string.IsNullOrWhiteSpace(_fileId.Text) ? null : _fileId.Text;

    public PpfEditDialog(string description, string? fileId)
    {
        Text = "Edit PPF metadata";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(450, 190);
        Font = Theme.Ui;

        _desc.Text = description ?? "";
        _fileId.Text = fileId ?? "";

        Controls.Add(new Label { Text = "Description:", AutoSize = true, Location = new Point(12, 19) });
        Controls.Add(new Label { Text = "file_id.diz:", AutoSize = true, Location = new Point(12, 49) });

        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK,
            Location = new Point(278, 152), Width = 80, FlatStyle = FlatStyle.System,
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(364, 152), Width = 80, FlatStyle = FlatStyle.System,
        };

        Controls.Add(_desc); Controls.Add(_fileId); Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
