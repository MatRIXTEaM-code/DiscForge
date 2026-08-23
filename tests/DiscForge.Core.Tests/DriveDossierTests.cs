// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Devices;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The drive dossier examined against this campaign's actual lessons: the
/// Plextor's mute signature and the Samsung's first-sector C2 wolf-cries must
/// accumulate into counters, survive save/load, and surface as warnings; a
/// bench-confirmed offset must outrank the knowledge-base reference.
/// </summary>
public class DriveDossierTests
{
    private static DriveObservation Obs(string category, string detail, long? value = null)
        => new("2026-08-23T09:00:00Z", category, detail, value, "test");

    [Fact]
    public void Observations_DistilIntoFacts()
    {
        var d = new DriveDossier { Vendor = "PLEXTOR", Model = "CD-R PX-W5224A" }
            .Observe(Obs(DriveDossier.CategoryMute, "sync gate fired", 153905))
            .Observe(Obs(DriveDossier.CategoryMute, "sync gate fired again", 153905))
            .Observe(Obs(DriveDossier.CategoryOffset, "AccurateRip-confirmed", 30))
            .Observe(Obs(DriveDossier.CategoryLeadInReach, "0xD8 served", -75))
            .Observe(Obs(DriveDossier.CategoryLeadOutOverread, "lead-out read", 289322));

        Assert.Equal(2, d.MuteSignatureCount);
        Assert.Equal(30, d.ConfirmedOffsetSamples);
        Assert.Equal(-75, d.MinLeadInLba);
        Assert.Equal(289322, d.MaxLeadOutOverreadLba);
        Assert.Equal(5, d.Observations.Count);
    }

    [Fact]
    public void MuteAndC2Lessons_BecomeWarnings()
    {
        var d = new DriveDossier { Vendor = "TSSTcorp", Model = "CDDVDW SH-224DB" }
            .Observe(Obs(DriveDossier.CategoryC2FirstSector, "span open flagged"))
            .Observe(Obs(DriveDossier.CategoryC2FirstSector, "span open flagged"))
            .Observe(Obs(DriveDossier.CategoryC2FirstSector, "span open flagged"))
            .Observe(Obs(DriveDossier.CategoryMute, "zero-fill with SUCCESS"));

        var warnings = d.Warnings();
        Assert.Contains(warnings, w => w.Contains("zero-mutes"));
        Assert.Contains(warnings, w => w.Contains("--no-c2"));
    }

    [Fact]
    public void TwoC2Flags_AreNotYetAPattern()
    {
        var d = new DriveDossier { Vendor = "X", Model = "Y" }
            .Observe(Obs(DriveDossier.CategoryC2FirstSector, "once"))
            .Observe(Obs(DriveDossier.CategoryC2FirstSector, "twice"));
        Assert.DoesNotContain(d.Warnings(), w => w.Contains("--no-c2"));
    }

    [Fact]
    public void ConfirmedOffset_DisagreeingWithSeed_IsCalledOut()
    {
        var seed = DriveKnowledgeBase.Find("PLEXTOR", "CD-R   PX-W5224A");
        Assert.NotNull(seed);
        Assert.Equal(30, seed!.ReadOffsetSamples);      // the reference this test leans on

        var agree = new DriveDossier { Vendor = "PLEXTOR", Model = "CD-R PX-W5224A" }
            .Observe(Obs(DriveDossier.CategoryOffset, "confirmed", 30));
        Assert.DoesNotContain(agree.Warnings(seed), w => w.Contains("disagrees"));

        var disagree = agree.Observe(Obs(DriveDossier.CategoryOffset, "re-confirmed", 6));
        Assert.Contains(disagree.Warnings(seed), w => w.Contains("disagrees"));
    }

    [Fact]
    public void Store_RoundTrips_AndAccumulatesAcrossLoads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dforge_dossier_" + Guid.NewGuid().ToString("N"));
        try
        {
            var d1 = DriveDossierStore.LoadOrNew(dir, "PLEXTOR", "CD-R PX-W5224A", "1.03");
            Assert.Empty(d1.Observations);
            DriveDossierStore.Save(dir, d1.Observe(Obs(DriveDossier.CategoryMute, "first lesson", 153905)));

            // A later session loads the same drive and adds the next lesson.
            var d2 = DriveDossierStore.LoadOrNew(dir, "PLEXTOR", "CD-R PX-W5224A", "1.03")
                .Observe(Obs(DriveDossier.CategoryOffset, "confirmed", 30));
            DriveDossierStore.Save(dir, d2);

            var final = DriveDossierStore.LoadOrNew(dir, "PLEXTOR", "CD-R PX-W5224A");
            Assert.Equal(2, final.Observations.Count);
            Assert.Equal(1, final.MuteSignatureCount);
            Assert.Equal(30, final.ConfirmedOffsetSamples);
            Assert.Equal("1.03", final.Firmware);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PathFor_NormalizesLikeTheKnowledgeBase()
    {
        string p = DriveDossierStore.PathFor("/x", "PLEXTOR ", "CD-R   PX-W5224A");
        Assert.EndsWith(".json", p);
        Assert.DoesNotContain("  ", Path.GetFileName(p));
        // Same drive, differently spaced INQUIRY strings → same dossier file.
        Assert.Equal(p, DriveDossierStore.PathFor("/x", "PLEXTOR", "CD-R PX-W5224A"));
    }
}
