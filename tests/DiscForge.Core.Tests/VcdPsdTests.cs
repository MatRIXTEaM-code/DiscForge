// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.VideoCd;
using Xunit;

namespace DiscForge.Core.Tests;

public class VcdPsdTests
{
    private static void Be16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }

    // A menu (LID 1) with two selections → two play lists (LID 2 @unit3, LID 3 @unit5),
    // each playing one item and linking to an end list (@unit7).
    private static byte[] BuildPsd()
    {
        var b = new byte[64];

        // Selection list @ byte 0 (unit 0), nos = 2.
        b[0] = 0x18; b[1] = 0x00; b[2] = 2; b[3] = 1;   // type, flags, nos, bsn
        Be16(b, 4, 1);                                   // lid
        Be16(b, 6, VcdPsd.OffsetNone);                   // prev
        Be16(b, 8, VcdPsd.OffsetNone);                   // next
        Be16(b, 10, VcdPsd.OffsetNone);                  // return
        Be16(b, 12, VcdPsd.OffsetNone);                  // default
        Be16(b, 14, VcdPsd.OffsetNone);                  // timeout
        b[16] = 0; b[17] = 0;                            // totime, loop
        Be16(b, 18, 0);                                  // itemid
        Be16(b, 20, 3);                                  // selection 0 → unit 3
        Be16(b, 22, 5);                                  // selection 1 → unit 5

        // Play list @ byte 24 (unit 3), LID 2.
        b[24] = 0x10; b[25] = 1;                         // type, noi
        Be16(b, 24 + 2, 2);                              // lid
        Be16(b, 24 + 4, VcdPsd.OffsetNone);              // prev
        Be16(b, 24 + 6, 7);                              // next → end list (unit 7)
        Be16(b, 24 + 8, 0);                              // return → menu (unit 0)
        Be16(b, 24 + 10, 0);                             // ptime
        b[24 + 12] = 5; b[24 + 13] = 0;                  // wait, autoPause
        Be16(b, 24 + 14, 100);                           // item 0

        // Play list @ byte 40 (unit 5), LID 3.
        b[40] = 0x10; b[41] = 1;
        Be16(b, 40 + 2, 3);
        Be16(b, 40 + 4, VcdPsd.OffsetNone);
        Be16(b, 40 + 6, 7);
        Be16(b, 40 + 8, 0);
        Be16(b, 40 + 10, 0);
        b[40 + 12] = 5; b[40 + 13] = 0;
        Be16(b, 40 + 14, 200);

        // End list @ byte 56 (unit 7).
        b[56] = 0x1F;

        return b;
    }

    [Fact]
    public void Parses_all_descriptors()
    {
        var doc = VcdPsd.Parse(BuildPsd());
        Assert.Equal(4, doc.Descriptors.Count);
        Assert.Equal(2, doc.PlayLists);
        Assert.Equal(1, doc.Menus);
    }

    [Fact]
    public void Decodes_the_selection_menu()
    {
        var doc = VcdPsd.Parse(BuildPsd());
        var menu = doc.Descriptors[0];
        Assert.Equal(PsdDescriptorType.SelectionList, menu.Type);
        Assert.NotNull(menu.Selection);
        Assert.Equal(1, menu.Selection!.Lid);
        Assert.Equal(2, menu.Selection.NumberOfSelections);
        Assert.Equal(1, menu.Selection.BaseSelectionNumber);
        Assert.Equal(new[] { 3, 5 }, menu.Selection.SelectionOffsets);
    }

    [Fact]
    public void Resolves_offsets_across_descriptors()
    {
        var doc = VcdPsd.Parse(BuildPsd());
        var menu = doc.Descriptors[0].Selection!;

        int i0 = doc.Resolve(menu.SelectionOffsets[0]);
        int i1 = doc.Resolve(menu.SelectionOffsets[1]);
        Assert.Equal(2, doc.Descriptors[i0].Lid);   // first selection → LID 2
        Assert.Equal(3, doc.Descriptors[i1].Lid);   // second selection → LID 3
        Assert.Equal(-1, doc.Resolve(VcdPsd.OffsetNone));
    }

    [Fact]
    public void Play_list_items_and_links_decode()
    {
        var doc = VcdPsd.Parse(BuildPsd());
        var pl = doc.Descriptors.First(d => d.Type == PsdDescriptorType.PlayList).PlayList!;
        Assert.Equal(new[] { 100 }, pl.Items);
        Assert.Equal(5, pl.WaitTime);

        int next = doc.Resolve(pl.NextOffset);
        int ret = doc.Resolve(pl.ReturnOffset);
        Assert.Equal(PsdDescriptorType.EndList, doc.Descriptors[next].Type);
        Assert.Equal(1, doc.Descriptors[ret].Lid);   // returns to the menu
    }

    [Fact]
    public void Reads_the_lot_offset_table()
    {
        var lot = new byte[16];
        Be16(lot, 0, 0);                    // LID 1 → unit 0
        Be16(lot, 2, 3);                    // LID 2 → unit 3
        Be16(lot, 4, 5);                    // LID 3 → unit 5
        for (int p = 6; p < 16; p += 2) Be16(lot, p, VcdPsd.OffsetNone);   // unused padding

        var offsets = VcdPsd.ReadLot(lot);
        Assert.Equal(new[] { 0, 3, 5 }, offsets);
    }

    [Fact]
    public void An_empty_psd_has_no_descriptors()
    {
        var doc = VcdPsd.Parse(new byte[32]);   // all zero → unused
        Assert.Empty(doc.Descriptors);
        Assert.Contains("Empty PSD", doc.Summary());
    }

    [Fact]
    public void Render_names_the_links_by_lid()
    {
        var text = VcdPsd.Render(VcdPsd.Parse(BuildPsd()));
        Assert.Contains("Menu LID 1", text);
        Assert.Contains("PlayList LID 2", text);
        Assert.Contains("EndList", text);
    }
}
