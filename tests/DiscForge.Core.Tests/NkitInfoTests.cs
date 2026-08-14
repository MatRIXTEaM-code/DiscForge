// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Text;
using DiscForge.Core.GameCube;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Tests for the NKit recovery-block reader: an NKit-scrubbed GC/Wii image carries an "NKIT" block at
/// disc-header offset 0x200 with the source image's CRC32 (for Redump matching) and a backed-up update
/// partition CRC. Headers are built byte for byte so the offsets are pinned.
/// </summary>
public class NkitInfoTests
{
    private static byte[] Header(bool wii, uint sourceCrc, uint updateCrc, string gameId = "GALE01")
    {
        var h = new byte[0x21C];
        Encoding.ASCII.GetBytes(gameId).CopyTo(h, 0);
        if (wii) BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x18), WiiDisc.Magic);
        else BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x1C), GcmReader.Magic);
        Encoding.ASCII.GetBytes("NKIT").CopyTo(h, 0x200);
        Encoding.ASCII.GetBytes("v01").CopyTo(h, 0x204);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x208), sourceCrc);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x20C), 0x12345678);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x210), 0x0AABBCCD);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0x218), updateCrc);
        return h;
    }

    [Fact]
    public void Reads_the_recovery_block_of_a_scrubbed_Wii_image()
    {
        var nk = Nkit.Parse(Header(wii: true, sourceCrc: 0xDEADBEEF, updateCrc: 0xCAFEBABE));
        Assert.True(nk.IsNkit);
        Assert.Equal("Wii", nk.Platform);
        Assert.Equal("GALE01", nk.GameId);
        Assert.Equal("v01", nk.Version);
        Assert.Equal(0xDEADBEEFu, nk.SourceCrc32);
        Assert.True(nk.HasUpdatePartitionBackup);
        Assert.Equal(0xCAFEBABEu, nk.UpdatePartitionCrc32);
    }

    [Fact]
    public void Detects_a_scrubbed_GameCube_image_without_an_update_partition()
    {
        var nk = Nkit.Parse(Header(wii: false, sourceCrc: 0x11223344, updateCrc: 0));
        Assert.True(nk.IsNkit);
        Assert.Equal("GameCube", nk.Platform);
        Assert.Equal(0x11223344u, nk.SourceCrc32);
        Assert.False(nk.HasUpdatePartitionBackup);
    }

    [Fact]
    public void A_plain_image_without_the_NKIT_block_is_not_flagged_as_NKit()
    {
        var nk = Nkit.Parse(new byte[0x300]);
        Assert.False(nk.IsNkit);
    }
}
