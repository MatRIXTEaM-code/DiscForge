using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscBillOfMaterialsTests
{
    private static CopyProtectionCatalog.ScannedBinary Bin(string name, string contains)
        => new(name, Encoding.ASCII.GetBytes("__" + contains + "__"));

    private static DiscBom Analyze(IEnumerable<string> paths, params CopyProtectionCatalog.ScannedBinary[] bins)
        => DiscBillOfMaterials.Analyze("VOL", paths, bins, null);

    [Fact]
    public void RenderWare_is_found_from_its_asset_extensions()
    {
        var bom = Analyze(new[] { "/MODELS/CAR.DFF;1", "/TEX/CAR.TXD;1", "/GAME.EXE;1" });
        Assert.Contains(bom.Components, c => c.Name == "RenderWare" && c.Category == BomCategory.Engine);
    }

    [Fact]
    public void Miles_sound_system_is_found_from_its_dll()
    {
        var bom = Analyze(new[] { "/MSS32.DLL;1", "/GAME.EXE;1" });
        Assert.Contains(bom.Components, c => c.Name == "Miles Sound System" && c.Category == BomCategory.Audio);
    }

    [Fact]
    public void The_msvc_runtime_version_is_reported()
    {
        var bom = Analyze(new[] { "/MSVCR71.DLL;1", "/GAME.EXE;1" });
        var c = Assert.Single(bom.Components, x => x.Name.Contains("Visual C++"));
        Assert.Equal("7.1 (VC2003)", c.Version);
    }

    [Fact]
    public void A_playstation_disc_is_recognised_by_boot_and_assets()
    {
        var bom = Analyze(new[] { "/SYSTEM.CNF;1", "/SOUND/MUSIC.VAG;1", "/MOVIE.STR;1" });
        Assert.Contains(bom.Components, c => c.Name == "Sony PlayStation" && c.Category == BomCategory.Platform);
        Assert.Contains(bom.Components, c => c.Category == BomCategory.AssetPipeline);
    }

    [Fact]
    public void An_engine_is_found_by_an_executable_signature()
    {
        var bom = Analyze(new[] { "/GAME.EXE;1" }, Bin("GAME.EXE", "built with UnityEngine core"));
        Assert.Contains(bom.Components, c => c.Name == "Unity");
    }

    [Fact]
    public void A_disc_with_no_signatures_reports_nothing()
    {
        var bom = Analyze(new[] { "/DATA.BIN;1", "/README.TXT;1" });
        Assert.Empty(bom.Components);
        Assert.Contains("no recognised", bom.Summary());
    }

    [Fact]
    public void From_iso_identifies_middleware_and_reads_the_dates_end_to_end()
    {
        var image = IsoBuilder.Build("PSXGAME", new List<IsoBuilder.FileEntry>
        {
            new("SYSTEM.CNF", Encoding.ASCII.GetBytes("BOOT=cdrom:\\SLUS_000.01;1")),
            new("MSS32.DLL", new byte[64]),
            new("MOVIE.BIK", new byte[128]),
            new("GAME.EXE", new byte[256]),
        }, joliet: false).Image;

        var bom = DiscBillOfMaterials.FromIso(image);
        Assert.Equal("PSXGAME", bom.VolumeId);
        Assert.Contains(bom.Components, c => c.Name == "Miles Sound System");
        Assert.Contains(bom.Components, c => c.Name == "Bink Video (RAD)");
        Assert.Contains(bom.Components, c => c.Name == "Sony PlayStation");
        Assert.NotNull(bom.EarliestFile);          // dates read from the ISO by DiscChronology
    }
}
