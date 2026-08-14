// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.DvdVideo;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The DVD-Video IFO sector-pointer parse + verify (ImgBurn's "Fix VTS Sectors" as a
/// check): read the VTS-relative pointers a title-set IFO carries and confirm they agree
/// with the actual file layout.
/// </summary>
public class DvdVideoIfoTests
{
    private static byte[] Vtsi(uint vtsLast, uint vtsiLast, uint vtsmVobs, uint vtsttVobs)
    {
        var h = new byte[0x100];
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x0C), vtsLast);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x1C), vtsiLast);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0xC0), vtsmVobs);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0xC4), vtsttVobs);
        return h;
    }

    [Fact]
    public void Parses_the_vtsi_pointers()
    {
        var p = DvdVideoIfo.ParseVtsi(Vtsi(7179, 5, 6, 1030));
        Assert.NotNull(p);
        Assert.Equal(7179u, p!.VtsLastSector);
        Assert.Equal(5u, p.VtsiLastSector);
        Assert.Equal(6u, p.VtsmVobs);
        Assert.Equal(1030u, p.VtsttVobs);
    }

    [Fact]
    public void A_consistent_title_set_reports_no_issues()
    {
        // ifo=6, menu=1024, title=6144, bup=6 sectors.
        //   VTSI_LAST = 5, VTSM_VOBS = 6, VTSTT_VOBS = 1030, VTS_LAST = 7179.
        var p = Vtsi(7179, 5, 6, 1030);
        var issues = DvdVideoIfo.VerifyVts(1, DvdVideoIfo.ParseVtsi(p)!,
            ifoSectors: 6, menuVobSectors: 1024, titleVobSectors: 6144, bupSectors: 6);
        Assert.Empty(issues);
    }

    [Fact]
    public void A_no_menu_title_set_expects_vtsm_vobs_zero()
    {
        // No menu VOB → VTSM_VOBS must be 0, VTSTT_VOBS = ifo sectors.
        var p = Vtsi(vtsLast: (6 + 6144 + 6) - 1, vtsiLast: 5, vtsmVobs: 0, vtsttVobs: 6);
        var issues = DvdVideoIfo.VerifyVts(1, DvdVideoIfo.ParseVtsi(p)!,
            ifoSectors: 6, menuVobSectors: 0, titleVobSectors: 6144, bupSectors: 6);
        Assert.Empty(issues);
    }

    [Fact]
    public void A_wrong_title_pointer_is_flagged()
    {
        // VTSTT_VOBS is wrong (should be 1030) — the disc would seek the title to the
        // wrong sector.
        var p = Vtsi(7179, 5, 6, 999);
        var issues = DvdVideoIfo.VerifyVts(1, DvdVideoIfo.ParseVtsi(p)!,
            ifoSectors: 6, menuVobSectors: 1024, titleVobSectors: 6144, bupSectors: 6);
        Assert.Contains(issues, i => i.Contains("VTSTT_VOBS"));
    }

    [Fact]
    public void A_mismatched_bup_size_is_flagged()
    {
        var p = Vtsi(7179, 5, 6, 1030);
        var issues = DvdVideoIfo.VerifyVts(1, DvdVideoIfo.ParseVtsi(p)!,
            ifoSectors: 6, menuVobSectors: 1024, titleVobSectors: 6144, bupSectors: 7);   // BUP ≠ IFO
        Assert.Contains(issues, i => i.Contains("backup"));
    }

    [Fact]
    public void The_vmg_check_works_too()
    {
        var h = new byte[0x100];
        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(h, 0);
        // ifo=6, menu=512, bup=6 → VMGI_LAST=5, VMGM_VOBS=6, VMG_LAST=6+512+6-1=523.
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x0C), 523);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x1C), 5);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0xC0), 6);

        var p = DvdVideoIfo.ParseVmgi(h);
        Assert.NotNull(p);
        Assert.Empty(DvdVideoIfo.VerifyVmg(p!, ifoSectors: 6, menuVobSectors: 512, bupSectors: 6));
    }

    [Fact]
    public void A_non_ifo_returns_null()
    {
        Assert.Null(DvdVideoIfo.ParseVtsi(new byte[0x100]));   // no magic
        Assert.Null(DvdVideoIfo.ParseVmgi(Encoding.ASCII.GetBytes("not an ifo header........")));
    }

    // ── Write half of "Fix VTS Sectors" ──────────────────────────────────────────────────

    [Fact]
    public void ComputeVts_matches_what_verify_expects()
    {
        // Same layout as the consistent-title-set test: ifo=6, menu=1024, title=6144, bup=6.
        var want = DvdVideoIfo.ComputeVts(ifoSectors: 6, menuVobSectors: 1024, titleVobSectors: 6144, bupSectors: 6);
        Assert.Equal(7179u, want.VtsLastSector);
        Assert.Equal(5u, want.VtsiLastSector);
        Assert.Equal(6u, want.VtsmVobs);
        Assert.Equal(1030u, want.VtsttVobs);
    }

    [Fact]
    public void ComputeVts_zeroes_vtsm_when_there_is_no_menu()
    {
        var want = DvdVideoIfo.ComputeVts(ifoSectors: 6, menuVobSectors: 0, titleVobSectors: 6144, bupSectors: 6);
        Assert.Equal(0u, want.VtsmVobs);
        Assert.Equal(6u, want.VtsttVobs);                     // title starts right after the IFO
        Assert.Equal((uint)(6 + 6144 + 6) - 1, want.VtsLastSector);
    }

    [Fact]
    public void Rewriting_stale_pointers_makes_verify_clean()
    {
        // Start from a garbled IFO, rewrite it for a known layout, and confirm the verifier
        // (the independently-tested read half) then finds nothing wrong — a closed round trip.
        const int ifo = 6, menu = 1024, title = 6144, bup = 6;
        var ifoBytes = Vtsi(vtsLast: 111, vtsiLast: 222, vtsmVobs: 333, vtsttVobs: 444);   // all wrong

        var want = DvdVideoIfo.ComputeVts(ifo, menu, title, bup);
        bool changed = DvdVideoIfo.WriteVtsPointers(ifoBytes, want);
        Assert.True(changed);

        var issues = DvdVideoIfo.VerifyVts(1, DvdVideoIfo.ParseVtsi(ifoBytes)!, ifo, menu, title, bup);
        Assert.Empty(issues);
    }

    [Fact]
    public void Rewriting_touches_only_the_four_pointer_fields()
    {
        // Fill the header with a marker byte, stamp the magic, then rewrite — every byte
        // outside the four 4-byte pointer slots must be untouched (ImgBurn's narrow scope).
        var before = new byte[0x100];
        for (int i = 0; i < before.Length; i++) before[i] = 0xAB;
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(before, 0);
        var after = (byte[])before.Clone();

        DvdVideoIfo.WriteVtsPointers(after, DvdVideoIfo.ComputeVts(6, 1024, 6144, 6));

        var touched = new HashSet<int>();
        foreach (int off in new[] { 0x0C, 0x1C, 0xC0, 0xC4 })
            for (int k = 0; k < 4; k++) touched.Add(off + k);
        for (int i = 0; i < before.Length; i++)
            if (!touched.Contains(i))
                Assert.Equal(before[i], after[i]);
    }

    [Fact]
    public void Rewriting_an_already_correct_ifo_reports_no_change()
    {
        var ifoBytes = Vtsi(7179, 5, 6, 1030);                // already the right values
        bool changed = DvdVideoIfo.WriteVtsPointers(ifoBytes, DvdVideoIfo.ComputeVts(6, 1024, 6144, 6));
        Assert.False(changed);
    }

    [Fact]
    public void ComputeAndWrite_vmg_round_trips_through_verify()
    {
        var h = new byte[0x100];
        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x0C), 9);      // wrong
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x1C), 9);      // wrong
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0xC0), 9);      // wrong

        var want = DvdVideoIfo.ComputeVmg(ifoSectors: 6, menuVobSectors: 512, bupSectors: 6);
        Assert.True(DvdVideoIfo.WriteVmgPointers(h, want));
        Assert.Empty(DvdVideoIfo.VerifyVmg(DvdVideoIfo.ParseVmgi(h)!, 6, 512, 6));
    }

    [Fact]
    public void Writing_pointers_into_a_non_ifo_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DvdVideoIfo.WriteVtsPointers(new byte[0x100], DvdVideoIfo.ComputeVts(6, 0, 6144, 6)));
    }
}
