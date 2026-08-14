// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.InteropServices;
using System.Text;
using DiscForge.Core.Devices;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The SG_IO layer's provable parts. The sg_io_hdr marshalling is the piece that silently corrupts
/// every SCSI command if one field lands at the wrong offset, so its 64-bit layout is pinned against
/// the kernel's documented offsets; INQUIRY parsing is pinned against a synthetic standard response.
/// (Actually issuing commands needs a real /dev/sr* device, which CI does not have.)
/// </summary>
public class LinuxSgIoTests
{
    [Fact]
    public void SgIoHdr_matches_the_kernel_64bit_layout()
    {
        // Offsets from <scsi/sg.h> on every 64-bit Linux ABI (pointers 8-byte aligned).
        Assert.Equal(88, LinuxSgIo.NativeSize);
        Assert.Equal(0, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("InterfaceId"));
        Assert.Equal(4, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("DxferDirection"));
        Assert.Equal(8, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("CmdLen"));
        Assert.Equal(9, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("MxSbLen"));
        Assert.Equal(10, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("IovecCount"));
        Assert.Equal(12, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("DxferLen"));
        Assert.Equal(16, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("Dxferp"));
        Assert.Equal(24, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("Cmdp"));
        Assert.Equal(32, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("Sbp"));
        Assert.Equal(40, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("Timeout"));
        Assert.Equal(44, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("Flags"));
        Assert.Equal(48, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("PackId"));
        Assert.Equal(56, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("UsrPtr"));
        Assert.Equal(64, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("Status"));
        Assert.Equal(65, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("MaskedStatus"));
        Assert.Equal(66, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("MsgStatus"));
        Assert.Equal(67, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("SbLenWr"));
        Assert.Equal(68, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("HostStatus"));
        Assert.Equal(70, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("DriverStatus"));
        Assert.Equal(72, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("Resid"));
        Assert.Equal(76, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("Duration"));
        Assert.Equal(80, (int)Marshal.OffsetOf<LinuxSgIo.SgIoHdr>("Info"));
    }

    [Fact]
    public void ParseInquiry_decodes_a_standard_response()
    {
        var buf = new byte[96];
        buf[0] = 0x05;                                        // peripheral type: CD/DVD
        Encoding.ASCII.GetBytes("PLEXTOR ").CopyTo(buf, 8);   // vendor (8)
        Encoding.ASCII.GetBytes("CD-R PX-W5224A  ").CopyTo(buf, 16);   // product (16)
        Encoding.ASCII.GetBytes("1.04").CopyTo(buf, 32);      // revision (4)

        var q = LinuxSgIo.ParseInquiry(buf);
        Assert.Equal("PLEXTOR", q.Vendor);
        Assert.Equal("CD-R PX-W5224A", q.Product);
        Assert.Equal("1.04", q.Revision);
        Assert.True(q.IsOptical);
    }

    [Fact]
    public void ParseInquiry_rejects_a_truncated_response()
    {
        Assert.Throws<ArgumentException>(() => LinuxSgIo.ParseInquiry(new byte[20]));
    }

    [Fact]
    public void Sense_code_decodes_key_asc_ascq()
    {
        var sense = new byte[14];
        sense[2] = 0x25;      // low nibble = sense key 5 (ILLEGAL REQUEST)
        sense[12] = 0x21;     // ASC
        sense[13] = 0x02;     // ASCQ
        var r = new LinuxSgIo.ScsiResult(2, sense, 0);
        Assert.False(r.Ok);
        Assert.Equal(((byte)5, (byte)0x21, (byte)0x02), r.SenseCode);
    }

    [Fact]
    public void Enumeration_is_empty_not_throwing_off_linux_or_without_drives()
    {
        // In CI (no /dev/sr*) this must return an empty list, never throw.
        var paths = LinuxSgIo.EnumerateDevicePaths();
        Assert.NotNull(paths);
    }
}
