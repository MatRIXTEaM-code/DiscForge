// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App.Views;

/// <summary>Shared layout helpers for the simpler tool screens: labelled file pickers, a busy state,
/// a status line and a monospace output box.</summary>
internal abstract class ToolViewBase : UserControl
{
    protected readonly Label Status = new() { AutoSize = false, Font = Theme.UiBold, Size = new Size(712, 20) };
    protected readonly ProgressBar Bar = new() { Size = new Size(712, 14), Minimum = 0, Maximum = 1000, Visible = false };
    protected readonly TextBox Output = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.Mono, BackColor = Color.White,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
    };
    private readonly List<Control> _busyDisabled = new();

    protected ToolViewBase()
    {
        Size = new Size(736, 440);
        BackColor = Color.White;
    }

    /// <summary>"Label: [path……] […]" on one row; returns the text box.</summary>
    protected TextBox AddFileRow(string label, int y, Func<string?> pick, int labelWidth = 80)
    {
        Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(12, y + 3), Font = Theme.Ui });
        var box = new TextBox { Location = new Point(12 + labelWidth, y), Width = 596 - labelWidth, Font = Theme.Ui,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        var btn = new Button { Text = "Browse…", Location = new Point(616, y - 1), Width = 108, FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btn.Click += (_, _) => { var p = pick(); if (p is not null) box.Text = p; };
        Controls.Add(box);
        Controls.Add(btn);
        _busyDisabled.Add(btn);
        return box;
    }

    protected Button AddButton(string text, int x, int y, int width, Func<Task> run)
    {
        var b = new Button { Text = text, Location = new Point(x, y), Width = width, Height = 28, FlatStyle = FlatStyle.System };
        b.Click += async (_, _) => await RunBusy(run);
        Controls.Add(b);
        _busyDisabled.Add(b);
        return b;
    }

    /// <summary>Place status, progress bar and output below <paramref name="y"/>.</summary>
    protected void AddOutputArea(int y)
    {
        Status.Location = new Point(12, y);
        Bar.Location = new Point(12, y + 22);
        Output.Location = new Point(12, y + 40);
        Output.Size = new Size(712, Math.Max(80, Height - y - 48));
        Controls.AddRange(new Control[] { Status, Bar, Output });
    }

    protected async Task RunBusy(Func<Task> run)
    {
        foreach (var c in _busyDisabled) c.Enabled = false;
        UseWaitCursor = true;
        try { await run(); }
        catch (Exception ex)
        {
            SetStatus(ex.Message, Theme.Bad);
            AppLog.WriteException(GetType().Name, ex);
        }
        finally
        {
            foreach (var c in _busyDisabled) c.Enabled = true;
            UseWaitCursor = false;
            Bar.Visible = false;
        }
    }

    protected void SetStatus(string text, Color? colour = null)
    {
        Status.Text = text;
        Status.ForeColor = colour ?? Theme.Text;
        StatusBus.Report(text);
    }

    protected IProgress<double> BarProgress()
    {
        Bar.Value = 0;
        Bar.Visible = true;
        return new Progress<double>(f => Bar.Value = (int)Math.Clamp(f * 1000, 0, 1000));
    }

    protected static string? PickFile(string filter = "All files (*.*)|*.*")
    {
        using var dlg = new OpenFileDialog { Filter = filter, InitialDirectory = AppSettings.LastImageDirectory ?? "" };
        if (dlg.ShowDialog() != DialogResult.OK) return null;
        AppSettings.LastImageDirectory = Path.GetDirectoryName(dlg.FileName);
        return dlg.FileName;
    }

    protected static string? PickFolder(string description)
    {
        using var dlg = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
    }

    protected static string Human(long b)
    {
        string[] u = { "B", "kB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {u[i]}";
    }

    protected static bool RequireFile(TextBox box, string what)
    {
        if (File.Exists(box.Text.Trim())) return true;
        RetroMessageBox.Show($"Choose {what} first.");
        return false;
    }
}
