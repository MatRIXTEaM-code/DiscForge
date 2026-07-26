// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App;

/// <summary>
/// A numbered event log — the classic burner's running commentary of what a job
/// is doing. Each line carries a sequence number and a severity, so a read or a
/// burn leaves an auditable trail rather than a progress bar and a shrug.
/// </summary>
internal sealed class EventLogView : UserControl
{
    public enum Level { Info, Good, Warn, Error }

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Font = Theme.Ui,
        BackColor = Color.White,
    };

    private int _seq;

    public EventLogView()
    {
        _list.Columns.Add("Event", 60, HorizontalAlignment.Left);
        _list.Columns.Add("Description", 620, HorizontalAlignment.Left);
        Controls.Add(_list);
    }

    public void Clear()
    {
        _list.Items.Clear();
        _seq = 0;
    }

    /// <summary>Append a line. Thread-safe: long jobs report from a worker thread.</summary>
    public void Add(string message, Level level = Level.Info)
    {
        // Marshal FIRST. Writing to AppLog before this ran twice for every line
        // reported from a worker thread: once here, then again when BeginInvoke
        // re-entered this method on the UI thread.
        if (InvokeRequired) { BeginInvoke(() => Add(message, level)); return; }

        AppLog.Write($"[{level}] {message}");

        var item = new ListViewItem((++_seq).ToString("D3"));
        item.SubItems.Add(message);
        item.ForeColor = level switch
        {
            Level.Good => Color.FromArgb(0x1E, 0x6B, 0x3A),
            Level.Warn => Color.FromArgb(0xA0, 0x60, 0x00),
            Level.Error => Color.FromArgb(0xA0, 0x20, 0x20),
            _ => Color.Black,
        };
        _list.Items.Add(item);
        item.EnsureVisible();
    }

    /// <summary>The whole log as text, for copying into a bug report.</summary>
    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (ListViewItem i in _list.Items)
            sb.AppendLine($"{i.Text}  {i.SubItems[1].Text}");
        return sb.ToString();
    }
}
