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
/// Clean-room bitsetting: DiscForge never fabricates vendor book-type command
/// bytes. It decodes a trace captured from the user's own drive and learns a
/// verbatim replay recipe. These tests cover the trace parser, the (honest)
/// analyzer, and the round-trip that guarantees a learned recipe reproduces the
/// captured command byte-for-byte.
/// </summary>
public class BookTypeBitsettingTests
{
    [Fact]
    public void Parses_cdb_and_data_across_commands_and_hex_styles()
    {
        string trace = """
            # captured from an LG writer
            CDB: BF 00 00 00 00 00 00 A1 00 04 00 00
            DATA: 00 02 00 00 00 00 00 00
            ---
            CDB 28,00,00,00,00,10,00,00,01,00
            """;
        var r = MmcTrace.Parse(trace);

        Assert.Empty(r.Errors);
        Assert.Equal(2, r.Commands.Count);
        Assert.Equal(0xBF, r.Commands[0].Opcode);
        Assert.Equal(12, r.Commands[0].Cdb.Length);
        Assert.Equal(8, r.Commands[0].DataOut.Length);
        Assert.Equal(0x28, r.Commands[1].Opcode);           // comma-separated hex still parses
        Assert.Empty(r.Commands[1].DataOut);
    }

    [Fact]
    public void Recognises_send_disc_structure_as_a_booktype_command()
    {
        // payload[4] high nibble 0 → DVD-ROM candidate (a +R set to look like ROM).
        var cmd = new MmcCommand(
            new byte[] { 0xBF, 0, 0, 0, 0, 0, 0, 0xA1, 0, 4, 0, 0 },
            new byte[] { 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        var f = BookTypeBitsetting.Analyze(cmd, 0);

        Assert.True(f.LooksLikeBitsetting);
        Assert.Equal(BookType.DvdRom, f.CandidateBookType);
        Assert.Contains("book type", f.Explanation);
    }

    [Fact]
    public void Flags_a_vendor_opcode_as_a_candidate_and_a_read_as_not()
    {
        var vendor = BookTypeBitsetting.Analyze(new MmcCommand(new byte[] { 0xE7, 0, 0, 0 }, Array.Empty<byte>()), 0);
        Assert.True(vendor.LooksLikeBitsetting);

        var read = BookTypeBitsetting.Analyze(new MmcCommand(new byte[] { 0x28, 0, 0, 0, 0, 0, 0, 0, 1, 0 }, Array.Empty<byte>()), 1);
        Assert.False(read.LooksLikeBitsetting);
        Assert.Equal("READ(10)", read.CommandName);
    }

    [Fact]
    public void Detects_a_vendor_mode_select_page()
    {
        // MODE SELECT(10): 8-byte header, block-descriptor length 0, then page 0x30.
        var data = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0x30, 0x06, 0, 0, 0, 0, 0, 0 };
        var cmd = new MmcCommand(new byte[] { 0x55, 0x10, 0, 0, 0, 0, 0, 0, 0x10, 0 }, data);

        var f = BookTypeBitsetting.Analyze(cmd, 0);

        Assert.True(f.LooksLikeBitsetting);              // page 0x30 is vendor range
        Assert.Contains("0x30", f.Explanation);
    }

    [Fact]
    public void A_learned_recipe_round_trips_and_reproduces_the_bytes()
    {
        var cmd = new MmcCommand(
            new byte[] { 0xBF, 0, 0, 0, 0, 0, 0, 0xA1, 0, 4, 0, 0 },
            new byte[] { 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        var recipe = BookTypeRecipe.Learn(cmd, "HL-DT-ST", "BH16NS55", "set +R to DVD-ROM", BookType.DvdRom);

        // Re-emitting reproduces the captured command byte-for-byte.
        Assert.True(recipe.ToCommand().Cdb.AsSpan().SequenceEqual(cmd.Cdb));
        Assert.True(recipe.ToCommand().DataOut.AsSpan().SequenceEqual(cmd.DataOut));

        // JSON survives a round trip.
        var back = BookTypeRecipe.FromJson(recipe.ToJson());
        Assert.Equal("HL-DT-ST", back.DriveVendor);
        Assert.Equal("BH16NS55", back.DriveModel);
        Assert.Equal(BookType.DvdRom, back.Target);
        Assert.True(back.Cdb.AsSpan().SequenceEqual(cmd.Cdb));
        Assert.True(back.DataOut.AsSpan().SequenceEqual(cmd.DataOut));
    }

    [Theory]
    [InlineData("DVD-ROM", BookType.DvdRom)]
    [InlineData("+R", BookType.DvdPlusR)]
    [InlineData("0xA", BookType.DvdPlusR)]
    [InlineData("DVD+R DL", BookType.DvdPlusRDl)]
    public void Parses_book_type_names_and_codes(string input, BookType expected)
        => Assert.Equal(expected, BookTypes.Parse(input));

    [Fact]
    public void Book_type_nibbles_map_to_names()
    {
        Assert.Equal("DVD-ROM", BookTypes.FromNibble(0x0).Name());
        Assert.Equal("DVD+R", BookTypes.FromNibble(0xA).Name());
        Assert.Equal(BookType.Unknown, BookTypes.FromNibble(0x8));
    }
}
