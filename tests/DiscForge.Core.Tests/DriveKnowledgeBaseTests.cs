// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

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
        Assert.Null(DriveKnowledgeBase.Find("HL-DT-ST", "DVDRAM ZZZ9999"));
        Assert.Null(DriveKnowledgeBase.Find("", ""));
    }

    [Fact]
    public void GrowthBatch_MatchesRealInquiryStrings()
    {
        // LG BD-RE units report the OEM chipset vendor "HL-DT-ST", not "LG Electronics".
        var wh16 = DriveKnowledgeBase.Find("HL-DT-ST", "BD-RE  WH16NS40 ");
        Assert.NotNull(wh16);
        Assert.Equal(6, wh16!.ReadOffsetSamples);

        var gh24 = DriveKnowledgeBase.Find("HL-DT-ST", "DVDRAM GH24NSC0");
        Assert.NotNull(gh24);
        Assert.Equal(6, gh24!.ReadOffsetSamples);

        var bdr209 = DriveKnowledgeBase.Find("PIONEER", "BD-RW    BDR-209D");
        Assert.NotNull(bdr209);
        Assert.Equal(667, bdr209!.ReadOffsetSamples);

        var asus = DriveKnowledgeBase.Find("ASUS", "DRW-24B1ST c");
        Assert.NotNull(asus);
        Assert.Equal(6, asus!.ReadOffsetSamples);

        var sh224 = DriveKnowledgeBase.Find("TSSTcorp", "CDDVDW SH-224DB");
        Assert.NotNull(sh224);
        Assert.Equal(6, sh224!.ReadOffsetSamples);

        var px716 = DriveKnowledgeBase.Find("PLEXTOR", "DVDR   PX-716A");
        Assert.NotNull(px716);
        Assert.Equal(30, px716!.ReadOffsetSamples);

        // Vendor-agnostic entry: Sony- and Optiarc-branded units of the identical drive both match.
        Assert.NotNull(DriveKnowledgeBase.Find("SONY", "DVD RW AD-7200A"));
        Assert.NotNull(DriveKnowledgeBase.Find("Optiarc", "DVD RW AD-7200S"));
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
