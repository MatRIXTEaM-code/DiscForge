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
/// The bundled drive knowledge base: INQUIRY-string matching must survive the padding
/// and punctuation real drives emit, every entry must carry provenance, and an unknown
/// drive must come back null — unknown, never guessed at.
/// </summary>
public class DriveKnowledgeBaseTests
{
    [Fact]
    public void Plextor5224_MatchesRealInquiryStrings()
    {
        // INQUIRY pads with spaces and the TA (ATAPI) unit reports the A model name.
        var r = DriveKnowledgeBase.Find("PLEXTOR ", "CD-R   PX-W5224A ");
        Assert.NotNull(r);
        Assert.Contains("PX-W5224", r!.DisplayName);
        Assert.Equal(30, r.ReadOffsetSamples);
        Assert.Equal(ProbeState.Yes, r.LeadInOverread);
        Assert.Equal(ProbeState.Yes, r.LeadOutOverread);
        Assert.Equal(PreferredReadCommand.PlextorD8, r.PreferredRead);
    }

    [Fact]
    public void Matching_IsPunctuationAndCaseBlind()
    {
        Assert.NotNull(DriveKnowledgeBase.Find("plextor", "px w5224a"));
        Assert.NotNull(DriveKnowledgeBase.Find("PLEXTOR", "PXW5224TA"));
        Assert.NotNull(DriveKnowledgeBase.Find("ASUS    ", "BW-16D1HT   3.10"));
    }

    [Fact]
    public void VendorAgnosticEntry_MatchesOnModelAlone()
    {
        // LiteOn units frequently report the generic "ATAPI" vendor string.
        var r = DriveKnowledgeBase.Find("ATAPI", "iHAS124   F");
        Assert.NotNull(r);
        Assert.Equal(6, r!.ReadOffsetSamples);
    }

    [Fact]
    public void UnknownDrive_IsNull_NotAGuess()
    {
        Assert.Null(DriveKnowledgeBase.Find("HL-DT-ST", "DVDRAM GH24NSC0"));
        Assert.Null(DriveKnowledgeBase.Find("", ""));
    }

    [Fact]
    public void WrongVendor_DoesNotMatchAVendorSpecificEntry()
    {
        Assert.Null(DriveKnowledgeBase.Find("SONY", "PX-W5224A"));
    }

    [Fact]
    public void EveryEntry_CarriesProvenance_AndRenders()
    {
        Assert.NotEmpty(DriveKnowledgeBase.All);
        foreach (var r in DriveKnowledgeBase.All)
        {
            Assert.NotEmpty(r.Sources);                 // reference data without provenance is rumour
            Assert.False(string.IsNullOrWhiteSpace(r.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(r.ModelContains));
            var text = r.Render();
            Assert.Contains("sources:", text);
            Assert.Contains("confirm", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Search_FindsByFamilyName_AndByModelFragment()
    {
        Assert.Contains(DriveKnowledgeBase.Search("plextor"), r => r.ModelContains == "PX-W5224");
        Assert.Single(DriveKnowledgeBase.Search("5224"));
        Assert.Empty(DriveKnowledgeBase.Search("zzz-no-such-drive"));
    }

    [Fact]
    public void Normalize_StripsPaddingAndPunctuation()
    {
        Assert.Equal("PXW5224A", DriveKnowledgeBase.Normalize(" px-w5224a "));
        Assert.Equal("LITEON", DriveKnowledgeBase.Normalize("LITE-ON"));
        Assert.Equal("", DriveKnowledgeBase.Normalize("  --  "));
    }
}
