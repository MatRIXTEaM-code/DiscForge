using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiscForge.Core.Dat;
using DiscForge.Core.Library;
using Xunit;

namespace DiscForge.Core.Tests;

public class SetRebuilderTests
{
    private static LibraryEntry Verified(string path, string game, string canonical) => new()
    {
        Path = path, FileName = Path.GetFileName(path), Size = 1, Format = "ISO 9660",
        Crc32 = 1, Md5 = "m", Sha1 = "s", Status = LibraryStatus.Verified,
        Match = new DatRom { Game = game, Name = canonical, Size = 1 },
        SuggestedName = canonical,
    };

    private static LibraryEntry Unknown(string path) => new()
    {
        Path = path, FileName = Path.GetFileName(path), Size = 1, Format = "",
        Crc32 = 0, Md5 = "m", Sha1 = "s", Status = LibraryStatus.Unknown,
    };

    private static LibraryReport Report(IEnumerable<LibraryEntry> entries, IEnumerable<DatRom>? missing = null) => new()
    {
        Root = "/src", Entries = entries.ToList(), Missing = (missing ?? Enumerable.Empty<DatRom>()).ToList(),
    };

    [Fact]
    public void Flat_plan_names_each_verified_file_canonically()
    {
        var report = Report(new[]
        {
            Verified("/src/messy name.iso", "Cool Game (USA)", "Cool Game (USA).iso"),
            Unknown("/src/random.dat"),
        });

        var plan = SetRebuilder.Plan(report, "/out", RebuildLayout.Flat);

        Assert.Single(plan.Actions);
        Assert.Equal(Path.GetFullPath("/out/Cool Game (USA).iso"), Path.GetFullPath(plan.Actions[0].DestPath));
        Assert.Single(plan.UnknownFiles);
        Assert.Equal(1, plan.Unknown);
    }

    [Fact]
    public void Per_game_layout_puts_each_game_in_its_own_folder()
    {
        var report = Report(new[]
        {
            Verified("/src/a.bin", "Disc Game (USA)", "Disc Game (USA) (Track 1).bin"),
            Verified("/src/b.cue", "Disc Game (USA)", "Disc Game (USA).cue"),
        });

        var plan = SetRebuilder.Plan(report, "/out", RebuildLayout.PerGameFolder);

        Assert.Equal(2, plan.Actions.Count);
        Assert.All(plan.Actions, a =>
            Assert.Equal(Path.GetFullPath("/out/Disc Game (USA)"),
                         Path.GetFullPath(Path.GetDirectoryName(a.DestPath)!)));
    }

    [Fact]
    public void A_file_already_at_its_canonical_path_needs_no_action()
    {
        var report = Report(new[] { Verified("/out/Cool Game (USA).iso", "Cool Game (USA)", "Cool Game (USA).iso") });
        var plan = SetRebuilder.Plan(report, "/out", RebuildLayout.Flat);

        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.AlreadyInPlace);
    }

    [Fact]
    public void Missing_roms_are_carried_from_the_scan()
    {
        var missing = new[] { new DatRom { Game = "Rare Game (USA)", Name = "Rare Game (USA).iso", Size = 1 } };
        var plan = SetRebuilder.Plan(Report(System.Array.Empty<LibraryEntry>(), missing), "/out");

        Assert.Equal(1, plan.Missing);
        Assert.Equal("Rare Game (USA).iso", plan.MissingRoms[0].Name);
    }

    [Fact]
    public void Apply_copies_files_to_their_canonical_names()
    {
        var src = Directory.CreateTempSubdirectory();
        var dst = Directory.CreateTempSubdirectory();
        try
        {
            string messy = Path.Combine(src.FullName, "messy.iso");
            File.WriteAllBytes(messy, new byte[] { 1, 2, 3, 4 });

            var report = Report(new[] { Verified(messy, "Cool Game (USA)", "Cool Game (USA).iso") });
            var plan = SetRebuilder.Plan(report, dst.FullName, RebuildLayout.Flat);

            int placed = SetRebuilder.Apply(plan, move: false);

            Assert.Equal(1, placed);
            string dest = Path.Combine(dst.FullName, "Cool Game (USA).iso");
            Assert.True(File.Exists(dest));
            Assert.True(File.Exists(messy));                       // copy left the original
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(dest));

            // Re-running is idempotent: the dest already exists, so nothing new is placed.
            Assert.Equal(0, SetRebuilder.Apply(plan, move: false));
        }
        finally { src.Delete(true); dst.Delete(true); }
    }

    [Fact]
    public void Apply_can_move_instead_of_copy()
    {
        var src = Directory.CreateTempSubdirectory();
        var dst = Directory.CreateTempSubdirectory();
        try
        {
            string messy = Path.Combine(src.FullName, "messy.iso");
            File.WriteAllBytes(messy, new byte[] { 9 });

            var report = Report(new[] { Verified(messy, "Game (USA)", "Game (USA).iso") });
            var plan = SetRebuilder.Plan(report, dst.FullName, RebuildLayout.Flat);

            SetRebuilder.Apply(plan, move: true);

            Assert.False(File.Exists(messy));                      // moved away
            Assert.True(File.Exists(Path.Combine(dst.FullName, "Game (USA).iso")));
        }
        finally { src.Delete(true); dst.Delete(true); }
    }
}
