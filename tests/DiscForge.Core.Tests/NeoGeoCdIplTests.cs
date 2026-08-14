// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Rom;
using Xunit;

namespace DiscForge.Core.Tests;

public class NeoGeoCdIplTests
{
    private const string Sample =
        "PROG.PRG,00,0000\r\n" +
        "FIX.FIX,01,2000\r\n" +
        "SPR.SPR,02,0000\r\n" +
        "// a comment line\r\n" +
        "Z80.Z80,0F,F000\r\n" +
        ",00,0000\r\n";   // terminator

    [Fact]
    public void Parses_the_boot_file_list()
    {
        var boot = NeoGeoCdIpl.Parse(Sample);
        Assert.True(boot.IsBoot);
        Assert.Equal(4, boot.Entries.Count);            // terminator + comment ignored
        Assert.Equal("PROG.PRG", boot.Entries[0].FileName);
        Assert.Equal("Z80.Z80", boot.Entries[3].FileName);
    }

    [Fact]
    public void Decodes_bank_and_offset_as_hex()
    {
        var boot = NeoGeoCdIpl.Parse(Sample);
        var z80 = boot.Entries[3];
        Assert.Equal(0x0F, z80.Bank);
        Assert.Equal(0xF000, z80.Offset);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var boot = NeoGeoCdIpl.Parse("\n\n# comment\n;also\nPROG.PRG,00,0000\n");
        Assert.Single(boot.Entries);
    }

    [Fact]
    public void Parses_from_bytes()
    {
        var boot = NeoGeoCdIpl.Parse(Encoding.Latin1.GetBytes(Sample));
        Assert.Equal(4, boot.Entries.Count);
    }

    [Fact]
    public void Looks_like_ipl_heuristic()
    {
        Assert.True(NeoGeoCdIpl.LooksLikeIpl(Encoding.ASCII.GetBytes(Sample)));
        Assert.False(NeoGeoCdIpl.LooksLikeIpl(new byte[] { 0x00, 0x01, 0xFF, 0x02 }));   // binary
    }

    [Fact]
    public void An_empty_ipl_reports_no_boot()
    {
        var boot = NeoGeoCdIpl.Parse("// only comments\n,00,0000\n");
        Assert.False(boot.IsBoot);
        Assert.Contains("No Neo Geo CD boot", boot.Summary());
    }
}
