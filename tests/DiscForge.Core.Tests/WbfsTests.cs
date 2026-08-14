// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.Identify;
using DiscForge.Core.Wbfs;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the WBFS ("Wii Backup File System") container reader. A small
/// synthetic container is built to the documented layout — header, disc table,
/// per-disc header copy + wlba table, and the stored data sectors — and read
/// back. Geometry is chosen so the whole file stays small: 512-byte hd sectors
/// and 64 KiB WBFS sectors, which gives 71,716 wlba entries per disc and a
/// disc-info block of 143,872 bytes spanning the first three WBFS sectors.
/// </summary>
public class WbfsTests
{
    private const int HdSecSzS = 9;      // 512-byte hd sector
    private const int WbfsSecSzS = 16;   // 64 KiB WBFS sector
    private const int HdSecSz = 1 << HdSecSzS;
    private const int WbfsSecSz = 1 << WbfsSecSzS;
    private const long WiiDiscSize = 0x118240000L;
    private const int WbfsSecPerDisc = (int)(WiiDiscSize >> WbfsSecSzS);   // 71,716

    private static int DiscInfoSize()
    {
        long raw = 0x100 + (long)WbfsSecPerDisc * 2;
        return (int)((raw + HdSecSz - 1) & ~((long)HdSecSz - 1));
    }

    // Build a one-disc container. ISO sector 0 -> file WBFS-sector 3, ISO sector 1
    // sparse (wlba 0), ISO sector 2 -> file WBFS-sector 4. Returns the container
    // bytes and the ISO the reader should reconstruct from it.
    private static (byte[] Container, byte[] ExpectedIso) BuildOneDisc(
        string gameId = "RMCE01", string title = "Mario Kart Wii")
    {
        int discInfoSize = DiscInfoSize();
        int totalSectors = 5;                       // sectors 0..2 = management, 3 & 4 = data
        var buf = new byte[totalSectors * WbfsSecSz];

        // --- wbfs_head (big-endian) ---
        Encoding.ASCII.GetBytes("WBFS").CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), (uint)(buf.Length / HdSecSz)); // n_hd_sec
        buf[8] = HdSecSzS;
        buf[9] = WbfsSecSzS;

        // --- disc table @0x0C: slot 0 in use ---
        buf[0x0C] = 1;

        // --- disc-info block for slot 0 @ hd_sec_sz + 0*disc_info_sz = 512 ---
        int infoOff = HdSecSz;
        Encoding.ASCII.GetBytes(gameId).CopyTo(buf, infoOff + 0x00);
        Encoding.ASCII.GetBytes(title).CopyTo(buf, infoOff + 0x20);

        // wlba table right after the 0x100 header copy.
        int wlbaOff = infoOff + 0x100;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(wlbaOff + 0 * 2), 3);  // ISO sec 0 -> file sec 3
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(wlbaOff + 1 * 2), 0);  // ISO sec 1 -> sparse
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(wlbaOff + 2 * 2), 4);  // ISO sec 2 -> file sec 4

        // --- data sectors with recognisable content ---
        var secA = new byte[WbfsSecSz];
        var secB = new byte[WbfsSecSz];
        for (int i = 0; i < WbfsSecSz; i++)
        {
            secA[i] = (byte)(0x41 + (i % 26));
            secB[i] = (byte)(0x61 + (i % 26));
        }
        secA.CopyTo(buf, 3 * WbfsSecSz);
        secB.CopyTo(buf, 4 * WbfsSecSz);

        // Expected reconstruction: present, sparse (zeros), present. Trailing
        // sectors after the last stored one are trimmed.
        var iso = new byte[3 * WbfsSecSz];
        secA.CopyTo(iso, 0 * WbfsSecSz);
        // sector 1 stays zero
        secB.CopyTo(iso, 2 * WbfsSecSz);

        _ = discInfoSize; // documented; equals the reader's derivation
        return (buf, iso);
    }

    // ---- detection -----------------------------------------------------------

    [Fact]
    public void IsWbfs_true_for_the_magic()
    {
        var (container, _) = BuildOneDisc();
        using var ms = new MemoryStream(container);
        Assert.True(WbfsReader.IsWbfs(ms));
        Assert.Equal(0, ms.Position);   // non-destructive
    }

    [Fact]
    public void IsWbfs_false_for_other_bytes()
    {
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes("NOT A WBFS FILE!"));
        Assert.False(WbfsReader.IsWbfs(ms));
    }

    [Fact]
    public void FormatIdentifier_names_it_wbfs()
    {
        var (container, _) = BuildOneDisc();
        Assert.Equal("WBFS", FormatIdentifier.Identify(container).Name);
    }

    // ---- header / disc table -------------------------------------------------

    [Fact]
    public void Read_reports_container_geometry()
    {
        var (container, _) = BuildOneDisc();
        using var ms = new MemoryStream(container);
        var wbfs = WbfsReader.Read(ms);

        Assert.Equal(HdSecSz, wbfs.HdSectorSize);
        Assert.Equal(WbfsSecSz, wbfs.WbfsSectorSize);
    }

    [Fact]
    public void Read_reports_gameid_and_title()
    {
        var (container, _) = BuildOneDisc("RMCE01", "Mario Kart Wii");
        using var ms = new MemoryStream(container);
        var wbfs = WbfsReader.Read(ms);

        var disc = Assert.Single(wbfs.Discs);
        Assert.Equal(0, disc.Slot);
        Assert.Equal("RMCE01", disc.GameId);
        Assert.Equal("Mario Kart Wii", disc.Title);
    }

    // ---- reconstruction ------------------------------------------------------

    [Fact]
    public void ExtractDisc_rebuilds_iso_copying_present_and_zeroing_sparse()
    {
        var (container, expected) = BuildOneDisc();
        using var ms = new MemoryStream(container);
        var wbfs = WbfsReader.Read(ms);

        using var iso = new MemoryStream();
        long written = WbfsReader.ExtractDisc(ms, wbfs.Discs[0], iso);

        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, iso.ToArray());
    }

    [Fact]
    public void ExtractDisc_zero_fills_the_sparse_middle_sector()
    {
        var (container, _) = BuildOneDisc();
        using var ms = new MemoryStream(container);
        var wbfs = WbfsReader.Read(ms);

        using var iso = new MemoryStream();
        WbfsReader.ExtractDisc(ms, wbfs.Discs[0], iso);
        var bytes = iso.ToArray();

        // ISO sector 1 (the sparse one) must be all zero.
        var middle = bytes.AsSpan(WbfsSecSz, WbfsSecSz);
        foreach (var b in middle) Assert.Equal(0, b);
        // ...while sector 0 is not.
        Assert.NotEqual(0, bytes[0]);
    }

    // ---- malformed input -----------------------------------------------------

    [Fact]
    public void Short_file_is_rejected()
    {
        using var ms = new MemoryStream(new byte[] { 0x57, 0x42 });   // "WB", truncated
        Assert.Throws<WbfsFormatException>(() => WbfsReader.Read(ms));
    }

    [Fact]
    public void Wrong_magic_is_rejected()
    {
        var bytes = new byte[HdSecSz];
        Encoding.ASCII.GetBytes("NOTW").CopyTo(bytes, 0);
        using var ms = new MemoryStream(bytes);
        var ex = Assert.Throws<WbfsFormatException>(() => WbfsReader.Read(ms));
        Assert.Contains("WBFS", ex.Message);
    }

    [Fact]
    public void Invalid_wbfs_sector_shift_is_rejected()
    {
        var (container, _) = BuildOneDisc();
        container[9] = 8;   // wbfs_sec_sz_s < hd_sec_sz_s (9) — impossible
        using var ms = new MemoryStream(container);
        Assert.Throws<WbfsFormatException>(() => WbfsReader.Read(ms));
    }

    [Fact]
    public void Disc_info_beyond_end_of_file_is_rejected()
    {
        // Mark a high slot as used but give no data for it.
        var (container, _) = BuildOneDisc();
        container[0x0C + 400] = 1;   // slot 400's disc-info block is far past EOF
        using var ms = new MemoryStream(container);
        Assert.Throws<WbfsFormatException>(() => WbfsReader.Read(ms));
    }
}
