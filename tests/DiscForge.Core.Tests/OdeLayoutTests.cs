// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Convert;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// ode-layout arranges a set of converted games into each optical-drive emulator's SD-card convention. The tests
/// pin the researched, device-specific rules: GDEMU and Rhea/Phoebe number folders from 02 (folder 01 is reserved
/// for the menu) and write the per-game sidecars each menu manager reads (GDEMU name.txt; Rhea Name.txt+Disc.txt
/// plus a root Rhea.ini); MODE uses free-form named folders and writes no index. The tool never builds the boot
/// menu itself — that is left to the device's own utility.
/// </summary>
public class OdeLayoutTests
{
    private static string BuildGames(string root)
    {
        var games = Path.Combine(root, "games");
        foreach (var (name, files) in new[]
        {
            ("Sonic Adventure", new[] { "disc.gdi", "track01.bin" }),
            ("Crazy Taxi", new[] { "disc.gdi" }),
            ("Shenmue Disc 1", new[] { "disc.gdi" }),
        })
        {
            var d = Path.Combine(games, name);
            Directory.CreateDirectory(d);
            foreach (var f in files) File.WriteAllText(Path.Combine(d, f), "x");
        }
        return games;
    }

    [Fact]
    public void Gdemu_numbers_from_02_and_writes_name_sidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_ode_" + Guid.NewGuid().ToString("N"));
        try
        {
            var games = BuildGames(root);
            var outDir = Path.Combine(root, "gdemu");
            var r = OdeLayout.Build(OdeTarget.Gdemu, games, outDir);

            Assert.Equal(3, r.Games.Count);
            Assert.Equal(new[] { "02", "03", "04" }, r.Games.Select(g => g.Folder).OrderBy(x => x).ToArray());
            Assert.False(Directory.Exists(Path.Combine(outDir, "01")));   // 01 reserved for the menu
            var first = r.Games.First(g => g.Folder == "02");
            Assert.True(File.Exists(Path.Combine(outDir, "02", "name.txt")));
            Assert.Equal(first.Title, File.ReadAllText(Path.Combine(outDir, "02", "name.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Rhea_writes_root_ini_and_per_game_name_and_disc_sidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_ode_" + Guid.NewGuid().ToString("N"));
        try
        {
            var games = BuildGames(root);
            var outDir = Path.Combine(root, "rhea");
            OdeLayout.Build(OdeTarget.Rhea, games, outDir);

            Assert.True(File.Exists(Path.Combine(outDir, "Rhea.ini")));
            Assert.True(File.Exists(Path.Combine(outDir, "02", "Name.txt")));
            Assert.True(File.Exists(Path.Combine(outDir, "02", "Disc.txt")));
            Assert.False(Directory.Exists(Path.Combine(outDir, "01")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Mode_uses_free_form_named_folders_with_no_index()
    {
        var root = Path.Combine(Path.GetTempPath(), "dforge_ode_" + Guid.NewGuid().ToString("N"));
        try
        {
            var games = BuildGames(root);
            var outDir = Path.Combine(root, "mode");
            OdeLayout.Build(OdeTarget.Mode, games, outDir);

            Assert.True(Directory.Exists(Path.Combine(outDir, "Sonic Adventure")));
            Assert.True(File.Exists(Path.Combine(outDir, "Sonic Adventure", "disc.gdi")));
            Assert.False(File.Exists(Path.Combine(outDir, "Sonic Adventure", "name.txt")));   // MODE writes no sidecar
            Assert.False(Directory.Exists(Path.Combine(outDir, "02")));                       // no numbering
        }
        finally { Directory.Delete(root, true); }
    }
}
