using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class ProtectionCrossCheckTests
{
    private static ProtectionReport SafeDisc() =>
        CopyProtectionCatalog.Identify(new[] { "/00000001.TMP;1", "/GAME.ICD;1" });
    private static ProtectionReport CleanFs() =>
        CopyProtectionCatalog.Identify(new[] { "/GAME.EXE;1", "/DATA.BIN;1" });

    private static ErrorPatternReport DeliberateBand()
    {
        var bad = new bool[30000];
        for (int i = 0; i < 30; i++) bad[10000 + i * 50] = true;   // a periodic comb
        return ErrorPatternForensics.Classify(bad);
    }
    private static ErrorPatternReport Scratch()
    {
        var bad = new bool[20000];
        for (int i = 0; i < 40; i++) bad[5000 + i] = true;         // a solid burst
        return ErrorPatternForensics.Classify(bad);
    }

    private static TwinSectorReport Twins(bool protectedLike) => new()
    {
        SectorsScanned = 1000,
        TwinSectors = protectedLike ? 2 : 0,
        MisaddressedSectors = 0,
        Samples = System.Array.Empty<SectorAddressAnomaly>(),
        LooksProtected = protectedLike,
    };

    [Fact]
    public void Filesystem_scheme_plus_a_deliberate_band_is_corroborated()
    {
        var f = ProtectionCrossCheck.Fuse(SafeDisc(), DeliberateBand(), Twins(false));
        Assert.Equal(ProtectionStanding.Corroborated, f.Standing);
        Assert.Contains(f.Schemes, s => s.StartsWith("SafeDisc"));
        Assert.True(f.PhysicalSignature);
        Assert.Contains("verbatim", f.Guidance);
    }

    [Fact]
    public void Filesystem_scheme_plus_twin_sectors_is_corroborated()
    {
        var f = ProtectionCrossCheck.Fuse(SafeDisc(), null, Twins(true));
        Assert.Equal(ProtectionStanding.Corroborated, f.Standing);
    }

    [Fact]
    public void Loader_files_but_only_physical_damage_is_filesystem_only()
    {
        var f = ProtectionCrossCheck.Fuse(SafeDisc(), Scratch(), Twins(false));
        Assert.Equal(ProtectionStanding.FilesystemOnly, f.Standing);
        Assert.False(f.PhysicalSignature);
        Assert.Contains("reproduction", f.Guidance);
    }

    [Fact]
    public void A_signature_with_no_scheme_is_physical_only()
    {
        var f = ProtectionCrossCheck.Fuse(CleanFs(), null, Twins(true));
        Assert.Equal(ProtectionStanding.PhysicalOnly, f.Standing);
        Assert.Empty(f.Schemes);
        Assert.True(f.PhysicalSignature);
    }

    [Fact]
    public void Nothing_anywhere_is_none()
    {
        var f = ProtectionCrossCheck.Fuse(CleanFs(), Scratch(), Twins(false));
        Assert.Equal(ProtectionStanding.None, f.Standing);
        Assert.False(f.AnyProtection);
    }

    [Fact]
    public void All_null_inputs_are_handled()
    {
        var f = ProtectionCrossCheck.Fuse(null, null, null);
        Assert.Equal(ProtectionStanding.None, f.Standing);
    }
}
