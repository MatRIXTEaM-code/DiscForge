// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Mmc;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The Plextor vendor READ CD-DA (0xD8) CDB, byte-exact. The layout is pinned to
/// what two independent public implementations agree on — DiscImageCreator and
/// redumper: opcode at 0, SIGNED big-endian LBA at 2–5 (negative reaches the
/// lead-in), big-endian 32-bit transfer length at 6–9, sub-code selection at 10.
/// </summary>
public class PlextorD8Tests
{
    [Fact]
    public void Cdb_IsTwelveBytes_WithTheAgreedLayout()
    {
        var cdb = MmcCommands.PlextorReadCdDa(0x00123456, 27, MmcCommands.PlextorD8SubCode.RawPw96);
        Assert.Equal(12, cdb.Length);
        Assert.Equal(0xD8, cdb[0]);
        Assert.Equal(0x00, cdb[1]);
        Assert.Equal(new byte[] { 0x00, 0x12, 0x34, 0x56 }, cdb[2..6]);   // LBA, big-endian
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x1B }, cdb[6..10]);  // length, big-endian
        Assert.Equal(0x02, cdb[10]);                                       // raw P–W sub-code
        Assert.Equal(0x00, cdb[11]);
    }

    [Fact]
    public void NegativeLba_EncodesAsTwosComplement_BigEndian()
    {
        // LBA -150 (the classic first lead-in probe): 0xFFFFFF6A.
        var cdb = MmcCommands.PlextorReadCdDa(-150, 1, MmcCommands.PlextorD8SubCode.Q16);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x6A }, cdb[2..6]);
        Assert.Equal(0x01, cdb[10]);
    }

    [Fact]
    public void PerSectorSizes_MatchTheSubCodeSelection()
    {
        Assert.Equal(2352, MmcCommands.PlextorD8BytesPerSector(MmcCommands.PlextorD8SubCode.None));
        Assert.Equal(2368, MmcCommands.PlextorD8BytesPerSector(MmcCommands.PlextorD8SubCode.Q16));
        Assert.Equal(2448, MmcCommands.PlextorD8BytesPerSector(MmcCommands.PlextorD8SubCode.RawPw96));
    }
}
