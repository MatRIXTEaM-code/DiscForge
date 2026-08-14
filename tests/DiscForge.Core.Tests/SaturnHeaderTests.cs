// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Identify;
using DiscForge.Core.Saturn;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The Sega Saturn disc-header reader, validated against a hand-built header laid out to
/// the documented field offsets (there is no external oracle in this codebase, so — as
/// with the Dreamcast IP.BIN reader — a known-bytes header proves the field positions,
/// region letters and peripheral decoding). The format identifier is shown to recognise
/// the Saturn signature at both a cooked (offset 0) and a raw Mode 1 (offset 16) track.
/// </summary>
public class SaturnHeaderTests
{
    private static byte[] BuildHeader()
    {
        var h = new byte[0x100];
        h.AsSpan().Fill((byte)' ');
        void Put(int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(h.AsSpan(at));
        Put(0x00, "SEGA SEGASATURN ");
        Put(0x10, "SEGA ENTERPRISES");
        Put(0x20, "T-1810G   ");
        Put(0x2A, "V1.000");
        Put(0x30, "19960705");
        Put(0x38, "CD-1/1  ");
        Put(0x40, "JUE       ");
        Put(0x50, "JM              ");
        Put(0x60, "NIGHTS INTO DREAMS");
        return h;
    }

    [Fact]
    public void A_hand_built_header_parses_all_fields()
    {
        var header = SaturnDisc.Parse(BuildHeader());
        Assert.Equal("SEGA SEGASATURN", header.HardwareId);
        Assert.Equal("SEGA ENTERPRISES", header.MakerId);
        Assert.Equal("T-1810G", header.ProductNumber);
        Assert.Equal("V1.000", header.Version);
        Assert.Equal("19960705", header.ReleaseDate);
        Assert.Equal("CD-1/1", header.DeviceInfo);
        Assert.Equal("NIGHTS INTO DREAMS", header.Title);
    }

    [Fact]
    public void Region_letters_decode_to_names()
    {
        var header = SaturnDisc.Parse(BuildHeader());
        Assert.Equal(new[] { "Japan", "USA", "Europe" }, header.Regions);
        Assert.Equal("JUE", header.AreaSymbols);
    }

    [Fact]
    public void Peripheral_letters_decode_to_devices()
    {
        var header = SaturnDisc.Parse(BuildHeader());
        Assert.Contains("Control Pad", header.SupportedPeripherals);
        Assert.Contains("Mouse", header.SupportedPeripherals);
    }

    [Fact]
    public void A_buffer_without_the_signature_is_refused() =>
        Assert.Throws<SaturnFormatException>(() => SaturnDisc.Parse(new byte[0x100]));

    [Fact]
    public void IsHeader_is_true_only_with_the_signature()
    {
        Assert.True(SaturnDisc.IsHeader(BuildHeader()));
        Assert.False(SaturnDisc.IsHeader(new byte[0x100]));
    }

    [Fact]
    public void The_format_identifier_recognises_a_cooked_saturn_track()
    {
        var image = new byte[0x200];
        BuildHeader().CopyTo(image, 0);        // signature at offset 0 (cooked 2048/ISO)
        Assert.Equal("Saturn", FormatIdentifier.Identify(image).Name);
    }

    [Fact]
    public void The_format_identifier_recognises_a_raw_mode1_saturn_track()
    {
        var image = new byte[0x200];
        BuildHeader().CopyTo(image, 16);       // signature 16 bytes in (raw Mode 1 2352)
        var id = FormatIdentifier.Identify(image);
        Assert.Equal("Saturn", id.Name);
        Assert.Equal("disc image", id.Category);
    }
}
