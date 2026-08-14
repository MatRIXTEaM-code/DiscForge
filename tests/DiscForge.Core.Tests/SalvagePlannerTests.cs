// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Collection;
using DiscForge.Core.Preservation;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// salvage-plan finds where several unreadable dumps can rescue each other. This test builds a collection with
/// four titles — two copies whose holes are complementary (fully salvageable), two copies whose holes overlap
/// (partial, one sector lost in both), a title with a clean copy present, and a lone holed copy — and asserts
/// the planner groups same-title copies, intersects their hole maps, and reaches the right verdict for each.
/// </summary>
public class SalvagePlannerTests
{
    private const int SS = 2352;

    private static byte[] Program(int sectors)
    {
        var b = new byte[(long)sectors * SS];
        for (long i = 0; i < b.Length; i++) b[i] = (byte)((i * 7 + 3) % 251);
        return b;
    }

    [Fact]
    public void Finds_complementary_overlapping_complete_and_lone_copies()
    {
        var prog = Program(900);
        byte[] Slice(int s, int c) => prog.AsSpan(s * SS, c * SS).ToArray();

        var root = Path.Combine(Path.GetTempPath(), "dforge_sv_" + Guid.NewGuid().ToString("N"));
        try
        {
            void Copy(string folder, byte[] bin, long[]? holes)
            {
                var d = Path.Combine(root, folder);
                Directory.CreateDirectory(d);
                File.WriteAllBytes(Path.Combine(d, "t.bin"), bin);
                var cue = Path.Combine(d, "t.cue");
                File.WriteAllText(cue, "FILE \"t.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
                if (holes is not null)
                    new BadSectorMap { Image = "t.cue", TotalSectors = bin.Length / SS, UnreadableLba = holes }
                        .Save(BadSectorMap.SidecarPath(cue));
            }

            var a = Slice(0, 200); Copy("A1", a, new long[] { 10, 11 }); Copy("A2", a, new long[] { 50, 51 });
            var b = Slice(200, 200); Copy("B1", b, new long[] { 10, 20 }); Copy("B2", b, new long[] { 20, 30 });
            var c = Slice(400, 200); Copy("C1", c, new long[] { 5 }); Copy("C2", c, null);
            var d2 = Slice(600, 200); Copy("D1", d2, new long[] { 7 });

            var report = SalvagePlanner.Analyze(root);

            // Four titles each have an incomplete copy, so four groups.
            Assert.Equal(4, report.Groups.Count);
            Assert.Equal(1, report.FullySalvageable);

            // Group them by which copy names they contain.
            SalvageGroup Group(string copyName) => report.Groups.First(g => g.Copies.Any(cp => cp.RelPath.StartsWith(copyName)));

            var ga = Group("A1");
            Assert.True(ga.FullySalvageable);
            Assert.Equal(0, ga.UnrecoverableSectors);
            Assert.Equal(2, ga.RecoveredBySalvage);          // both holes of the best copy are covered by the other

            var gb = Group("B1");
            Assert.False(gb.FullySalvageable);
            Assert.Equal(1, gb.UnrecoverableSectors);        // sector 20 is holed in both
            Assert.Equal(1, gb.RecoveredBySalvage);

            var gc = Group("C1");
            Assert.Equal("t.cue", Path.GetFileName(gc.CompleteCopy!));
            Assert.False(gc.HasOpportunity);

            var gd = Group("D1");
            Assert.Single(gd.Copies);
            Assert.False(gd.FullySalvageable);
        }
        finally { Directory.Delete(root, true); }
    }
}
