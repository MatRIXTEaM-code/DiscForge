// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscGenealogyTests
{
    private static GenealogyReport Sample() => DiscGenealogy.Build(new List<DiscGenomeRecord>
    {
        new() { Id = "A", Title = "Game", MasteringSid = "IFPI L863", MouldSid = "IFPI 0271", Media = DiscMedia.Pressed, Fingerprint = new[] { 10, 2, 0, 5, 1, 0, 8 } },
        new() { Id = "B", Title = "Game", MasteringSid = "L863", MouldSid = "IFPI 0271", Media = DiscMedia.Pressed, Fingerprint = new[] { 10, 2, 0, 5, 1, 0, 8 } },
        new() { Id = "C", Title = "Game", MasteringSid = "IFPI L863", MouldSid = "IFPI 9503", Media = DiscMedia.Pressed },
        new() { Id = "D", Title = "Game", Media = DiscMedia.Recordable },
        new() { Id = "E", Title = "Old", Matrix = "SN12345-01", Media = DiscMedia.Pressed },
        new() { Id = "F", Title = "Weird", MasteringSid = "X999", Media = DiscMedia.Pressed },
        new() { Id = "G", Title = "Mystery", Media = DiscMedia.Unknown },
    });

    private static Authenticity V(GenealogyReport r, string id) => r.Verdicts.Single(v => v.Id == id).Authenticity;

    [Fact]
    public void Groups_a_master_family_into_plant_branches()
    {
        var fam = Assert.Single(Sample().Families);
        Assert.Equal("L863", fam.MasteringSid);
        Assert.Equal(3, fam.Members.Count);
        Assert.Equal(2, fam.Plants.Count);
        Assert.Contains(fam.Plants, p => p.MouldSid == "0271" && p.Members.Count == 2);
        Assert.Contains(fam.Plants, p => p.MouldSid == "9503" && p.Members.Count == 1);
    }

    [Fact]
    public void Links_two_copies_with_near_identical_error_maps()
    {
        var links = Sample().SameCopyLinks;
        Assert.Contains(links, l => (l.A == "A" && l.B == "B") || (l.A == "B" && l.B == "A"));
    }

    [Fact]
    public void Recordable_media_is_flagged_as_likely_counterfeit()
        => Assert.Equal(Authenticity.LikelyCounterfeit, V(Sample(), "D"));

    [Fact]
    public void A_valid_mastering_sid_reads_as_an_authentic_pressing()
        => Assert.Equal(Authenticity.AuthenticPressing, V(Sample(), "A"));

    [Fact]
    public void A_matrix_only_pressing_is_authentic()
        => Assert.Equal(Authenticity.AuthenticPressing, V(Sample(), "E"));

    [Fact]
    public void A_malformed_mastering_sid_is_suspect()
        => Assert.Equal(Authenticity.Suspect, V(Sample(), "F"));

    [Fact]
    public void No_evidence_reads_as_unknown()
        => Assert.Equal(Authenticity.Unknown, V(Sample(), "G"));

    [Fact]
    public void Unique_discs_are_singletons_not_families()
    {
        var r = Sample();
        Assert.Contains("E", r.Singletons);
        Assert.Contains("G", r.Singletons);
        Assert.DoesNotContain("A", r.Singletons);
    }
}
