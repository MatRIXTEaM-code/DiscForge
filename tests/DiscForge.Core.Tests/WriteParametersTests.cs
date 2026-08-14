// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Mmc;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The low-level write knobs — the MMC Write Parameters mode page (0x05) and the
/// MODE SELECT(10) / SEND OPC / RESERVE TRACK / CLOSE TRACK CDBs behind ImgBurn's
/// write-type / test-mode / BURN-Proof / OPC / link-size / reserve-track controls.
/// These lock the documented byte layout; execution over SPTI is Windows/hardware.
/// </summary>
public class WriteParametersTests
{
    [Fact]
    public void Write_parameters_page_encodes_the_control_bits()
    {
        var page = new WriteParametersPage
        {
            WriteType = CdWriteType.Raw,          // bits 3:0 = 3
            TestWrite = true,                      // bit 4
            BufferUnderrunFree = true,             // bit 6
            LinkSizeValid = true,                  // bit 5
            LinkSize = 7,
            TrackMode = 0x04,                      // data
            DataBlockType = 0,                     // raw 2352
            SessionFormat = 0x20,                  // CD-ROM XA
        }.Build();

        Assert.Equal(52, page.Length);
        Assert.Equal(0x05, page[0]);               // page code
        Assert.Equal(0x32, page[1]);               // page length = 50
        // byte 2 = BUFE(6)|LS_V(5)|Test(4)|WriteType(3:0) = 0x40|0x20|0x10|0x03
        Assert.Equal(0x73, page[2]);
        Assert.Equal(0x04, page[3] & 0x0F);        // track mode = data
        Assert.Equal(0x00, page[4] & 0x0F);        // data block type = raw 2352
        Assert.Equal(7, page[5]);                  // link size
        Assert.Equal(0x20, page[8]);               // session format = XA
        Assert.Equal(150, (page[14] << 8) | page[15]);  // default audio pause
    }

    [Fact]
    public void Default_page_is_session_at_once_mode1_data()
    {
        var page = new WriteParametersPage().Build();
        Assert.Equal((byte)CdWriteType.SessionAtOnce, (byte)(page[2] & 0x0F));  // write type 2
        Assert.Equal(0, page[2] & 0x70);           // no BUFE/LS_V/Test set
        Assert.Equal(0x04, page[3] & 0x0F);        // data track
        Assert.Equal(8, page[4] & 0x0F);           // Mode 1 / 2048
    }

    [Fact]
    public void Mode_select10_sets_pf_and_the_parameter_length()
    {
        var page = new WriteParametersPage().Build();
        var list = MmcCommands.ModeParameterList(page);
        var cdb = MmcCommands.ModeSelect10((ushort)list.Length);

        Assert.Equal(0x55, cdb[0]);
        Assert.Equal(0x10, cdb[1]);                // PF = 1, SP = 0
        Assert.Equal(list.Length, (cdb[7] << 8) | cdb[8]);
        Assert.Equal(8 + 52, list.Length);         // 8-byte header + page
        // The page sits after the 8-byte header, header zeroed.
        Assert.Equal(0x05, list[8]);
        for (int i = 0; i < 8; i++) Assert.Equal(0, list[i]);
    }

    [Fact]
    public void Send_opc_reserve_track_and_close_have_the_right_shape()
    {
        var opc = MmcCommands.SendOpc(doOpc: true);
        Assert.Equal(0x54, opc[0]);
        Assert.Equal(0x01, opc[1]);                // DoOpc

        var reserve = MmcCommands.ReserveTrack(0x0001_2345);
        Assert.Equal(0x53, reserve[0]);
        Assert.Equal(0x0001_2345u, (uint)((reserve[5] << 24) | (reserve[6] << 16) | (reserve[7] << 8) | reserve[8]));

        var close = MmcCommands.CloseTrackSession(closeFunction: 2, trackNumber: 1, immediate: true);
        Assert.Equal(0x5B, close[0]);
        Assert.Equal(0x01, close[1]);              // IMMED
        Assert.Equal(0x02, close[2] & 0x07);       // close session/disc
        Assert.Equal(1, (close[4] << 8) | close[5]);
    }
}
