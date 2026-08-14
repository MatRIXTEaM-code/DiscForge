// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Drawing;
using System.Windows.Forms;

namespace DiscForge.App.Views;

/// <summary>
/// The in-app manual: a searchable list of every tile on the left, and what it does /
/// how to use it on the right. Content comes from <see cref="HelpContent"/> so it stays
/// a single source of truth alongside the tiles. Laid out with docking so the body pane
/// always matches the window's width and wraps cleanly (never clipping on the right).
/// </summary>
internal sealed class HelpView : UserControl
{
    private readonly TextBox _search = new() { Dock = DockStyle.Top, Font = Theme.Ui };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, Font = Theme.Ui, IntegralHeight = false };
    private readonly Label _title = new()
    {
        Dock = DockStyle.Top, Height = 26, Font = Theme.UiBold, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
    };
    private readonly TextBox _body = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
        Font = Theme.Ui, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White,
    };

    // The entries currently shown in the list (after any search filter).
    private readonly List<HelpContent.Entry> _shown = new();

    public HelpView()
    {
        Size = new Size(736, 420);
        BackColor = Color.White;
        Padding = new Padding(12);

        _search.TextChanged += (_, _) => Populate(_search.Text);
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _search.PlaceholderText = "Search tiles…";

        // Left column: search box above a fill list.
        var left = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(0, 0, 8, 0) };
        left.Controls.Add(_list);      // Fill added first so the edge control docks over it
        left.Controls.Add(_search);

        // Right column: a title above the fill body pane.
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_body);     // Fill added first
        right.Controls.Add(_title);

        Controls.Add(right);           // Fill added first, then the left edge
        Controls.Add(left);

        Populate("");
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void Populate(string filter)
    {
        filter = filter.Trim();
        _shown.Clear();
        foreach (var e in HelpContent.Entries)
        {
            if (filter.Length == 0 ||
                e.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Body.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _shown.Add(e);
        }

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _shown) _list.Items.Add($"{e.Glyph}  {e.Title}");
        _list.EndUpdate();

        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        else { _title.Text = "No matching tile"; _body.Text = ""; }
    }

    private void ShowSelected()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _shown.Count) return;
        var e = _shown[i];
        _title.Text = $"{e.Glyph}  {e.Title} — {e.Summary}";
        _body.Text = e.Body.Replace("\n", Environment.NewLine);
    }
}
