// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

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
        // Long lines clip with an ellipsis in Details view; a hover tooltip makes the
        // full text reachable so nothing is silently lost.
        ShowItemToolTips = true,
    };

    private ColumnHeader _descriptionColumn = null!;
    private int _seq;

    public EventLogView()
    {
        _list.Columns.Add("Event", 60, HorizontalAlignment.Left);
        _descriptionColumn = _list.Columns.Add("Description", 620, HorizontalAlignment.Left);
        Controls.Add(_list);

        // Keep the Description column filling the available width, so most lines never
        // need to clip, and re-fit whenever the panel is resized.
        _list.ClientSizeChanged += (_, _) => FitDescriptionColumn();
        FitDescriptionColumn();
    }

    // Grow the Description column to use all the space the Event column doesn't, leaving
    // room for the vertical scrollbar so text isn't hidden underneath it.
    private void FitDescriptionColumn()
    {
        int available = _list.ClientSize.Width - _list.Columns[0].Width
                        - SystemInformation.VerticalScrollBarWidth - 4;
        if (available > 120) _descriptionColumn.Width = available;
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
        item.ToolTipText = message;   // full text on hover, even when the cell clips it
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
