// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Forensics;
using DiscForge.Core.Iso;
using Xunit;

namespace DiscForge.Core.Tests;

public class PrototypeScannerTests
{
    // ---- file-name residue --------------------------------------------------

    [Fact]
    public void SymbolAndMapFiles_AreFlaggedAsResidue()
    {
        var hits = PrototypeScanner.ScanFileNames(new[] { "/GAME.EXE;1", "/GAME.SYM;1", "/GAME.MAP;1" });
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(ResidueKind.DebugFile, h.Kind));
        Assert.Contains(hits, h => h.Detail.EndsWith(".SYM;1"));
        Assert.Contains(hits, h => h.Detail.EndsWith(".MAP;1"));
    }

    [Fact]
    public void ADebugOrDevkitDirectory_IsFlagged()
    {
        var hits = PrototypeScanner.ScanFileNames(new[] { "/DEBUG/LOG.TXT;1", "/DEVKIT/README.TXT;1" });
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void OrdinaryRetailFiles_AreNotFlagged()
    {
        var hits = PrototypeScanner.ScanFileNames(new[] { "/GAME.EXE;1", "/DATA/LEVEL1.DAT;1", "/README.TXT;1" });
        Assert.Empty(hits);
    }

    [Fact]
    public void APathFragmentThatMerelyContainsTheWord_StillMatches()
    {
        // A directory named "MYDEBUGSTUFF" still contains "debug" as a substring — real dev
        // trees are messy, and a substring match is deliberately generous here.
        var hits = PrototypeScanner.ScanFileNames(new[] { "/MYDEBUGSTUFF/NOTES.TXT;1" });
        Assert.Single(hits);
    }

    // ---- binary debug-string / embedded PDB residue --------------------------

    private static CopyProtectionCatalog.ScannedBinary Bin(string name, string containing)
        => new(name, Encoding.ASCII.GetBytes("....." + containing + "....."));

    [Fact]
    public void AnAssertionMessage_IsFlaggedAsDebugResidue()
    {
        var hits = PrototypeScanner.ScanBinaries(new[] { Bin("GAME.EXE", "ASSERTION FAILED: ptr != null") });
        Assert.Contains(hits, h => h.Kind == ResidueKind.DebugString && h.Detail == "ASSERTION FAILED");
    }

    [Fact]
    public void ADeveloperPerforcePath_IsFlaggedAsDebugResidue()
    {
        var hits = PrototypeScanner.ScanBinaries(new[] { Bin("GAME.EXE", @"E:\perforce\game\main\build") });
        Assert.Contains(hits, h => h.Detail == @":\perforce\");
    }

    [Fact]
    public void AnEmbeddedPdbReference_IsExtractedFromTheRsdsSignature()
    {
        // A CodeView debug-directory record: "RSDS" + 16-byte GUID + 4-byte age + a null-terminated path.
        var data = new List<byte>();
        data.AddRange(Encoding.ASCII.GetBytes("RSDS"));
        data.AddRange(new byte[20]);   // GUID + age
        data.AddRange(Encoding.ASCII.GetBytes(@"D:\build\obj\Release\game.pdb"));
        data.Add(0);

        var hits = PrototypeScanner.ScanBinaries(new[] { new CopyProtectionCatalog.ScannedBinary("GAME.EXE", data.ToArray()) });
        var pdbHit = Assert.Single(hits, h => h.Detail.StartsWith("embedded PDB reference"));
        Assert.Contains(@"game.pdb", pdbHit.Detail);
    }

    [Fact]
    public void ACleanRetailBinary_HasNoResidue()
    {
        var hits = PrototypeScanner.ScanBinaries(new[] { Bin("GAME.EXE", "ordinary retail code, nothing to see") });
        Assert.Empty(hits);
    }

    // ---- retail-baseline diff -------------------------------------------------

    [Fact]
    public void AFileNotInTheRetailBaseline_IsFlaggedAdded()
    {
        var here = new[] { ("GAME.EXE", 1000L, (string?)null), ("DEBUGTOOL.EXE", 500L, (string?)null) };
        var baseline = new[] { new BaselineEntry("GAME.EXE", 1000) };

        var diff = PrototypeScanner.DiffAgainstRetailBaseline(here, baseline);
        var added = Assert.Single(diff, d => d.Kind == BaselineDiffKind.AddedHere);
        Assert.Equal("debugtool.exe", added.Path);
    }

    [Fact]
    public void AFileMissingFromTheDiscButInRetail_IsFlaggedMissing()
    {
        var here = new[] { ("GAME.EXE", 1000L, (string?)null) };
        var baseline = new[] { new BaselineEntry("GAME.EXE", 1000), new BaselineEntry("EXTRA.DAT", 200) };

        var diff = PrototypeScanner.DiffAgainstRetailBaseline(here, baseline);
        var missing = Assert.Single(diff, d => d.Kind == BaselineDiffKind.MissingHere);
        Assert.Equal("extra.dat", missing.Path);
    }

    [Fact]
    public void ASizeMismatch_IsFlagged()
    {
        var here = new[] { ("GAME.EXE", 1234L, (string?)null) };
        var baseline = new[] { new BaselineEntry("GAME.EXE", 1000) };

        var diff = PrototypeScanner.DiffAgainstRetailBaseline(here, baseline);
        var mismatch = Assert.Single(diff);
        Assert.Equal(BaselineDiffKind.SizeMismatch, mismatch.Kind);
    }

    [Fact]
    public void AHashMismatch_IsFlaggedOnlyWhenBothSidesSupplyAHash()
    {
        var withHash = new[] { ("GAME.EXE", 1000L, (string?)"aaaa") };
        var baseline = new[] { new BaselineEntry("GAME.EXE", 1000, "bbbb") };
        var diff = PrototypeScanner.DiffAgainstRetailBaseline(withHash, baseline);
        Assert.Single(diff, d => d.Kind == BaselineDiffKind.HashMismatch);

        // Same size, no hash on one side — no false HashMismatch.
        var noHash = new[] { ("GAME.EXE", 1000L, (string?)null) };
        var diff2 = PrototypeScanner.DiffAgainstRetailBaseline(noHash, baseline);
        Assert.Empty(diff2);
    }

    [Fact]
    public void IdenticalFileLists_ProduceNoDiff()
    {
        var here = new[] { ("GAME.EXE", 1000L, (string?)"abc"), ("DATA.DAT", 500L, (string?)"def") };
        var baseline = new[] { new BaselineEntry("GAME.EXE", 1000, "abc"), new BaselineEntry("DATA.DAT", 500, "def") };
        Assert.Empty(PrototypeScanner.DiffAgainstRetailBaseline(here, baseline));
    }

    [Fact]
    public void PathMatchingIsCaseInsensitiveAndSlashNormalised()
    {
        var here = new[] { ("Data\\Level1.DAT", 500L, (string?)null) };
        var baseline = new[] { new BaselineEntry("DATA/LEVEL1.dat", 500) };
        Assert.Empty(PrototypeScanner.DiffAgainstRetailBaseline(here, baseline));
    }

    // ---- report-level behaviour -----------------------------------------------

    [Fact]
    public void AReportWithNoFindings_DoesNotLookLikeAPrototype()
    {
        var report = PrototypeScanner.Analyze("GAME", new[] { "/GAME.EXE;1" },
            Array.Empty<CopyProtectionCatalog.ScannedBinary>(), null, null, null);
        Assert.False(report.LooksLikePrototype);
        Assert.Contains("no debug/prototype residue", report.Summary());
    }

    [Fact]
    public void AReportWithResidue_LooksLikeAPrototype()
    {
        var report = PrototypeScanner.Analyze("GAME", new[] { "/GAME.EXE;1", "/GAME.SYM;1" },
            Array.Empty<CopyProtectionCatalog.ScannedBinary>(), null, null, null);
        Assert.True(report.LooksLikePrototype);
        Assert.Contains("residue finding", report.Summary());
    }

    [Fact]
    public void ASizeMismatchAlone_DoesNotByItselfClaimPrototype()
    {
        // A size-only baseline wobble (re-mastering, padding) is weaker evidence than an
        // added/missing file — LooksLikePrototype should not fire on that alone.
        var here = new[] { ("GAME.EXE", 1234L, (string?)null) };
        var baseline = new[] { new BaselineEntry("GAME.EXE", 1000) };
        var report = PrototypeScanner.Analyze("GAME", new[] { "GAME.EXE" },
            Array.Empty<CopyProtectionCatalog.ScannedBinary>(), here, baseline, null);
        Assert.False(report.LooksLikePrototype);
        Assert.Single(report.BaselineDiff);
    }

    // ---- end-to-end via FromIso -------------------------------------------------

    [Fact]
    public void FromIso_FindsResidueAndBuildsAUsableBaseline_EndToEnd()
    {
        var image = IsoBuilder.Build("PROTOGAME", new List<IsoBuilder.FileEntry>
        {
            new("GAME.EXE", Encoding.ASCII.GetBytes("retail code")),
            new("GAME.SYM", new byte[64]),
        }, joliet: false).Image;

        var report = PrototypeScanner.FromIso(image);
        Assert.Equal("PROTOGAME", report.VolumeId);
        Assert.True(report.LooksLikePrototype);
        Assert.Contains(report.Residue, r => r.Detail.EndsWith("GAME.SYM;1") || r.Detail.EndsWith("GAME.SYM"));

        // BuildBaseline should capture both files with real hashes, usable as a future retail
        // reference — a clean disc built the same way, diffed against this baseline, matches.
        var baseline = PrototypeScanner.BuildBaseline(image);
        Assert.Equal(2, baseline.Count);
        Assert.All(baseline, e => Assert.NotNull(e.Sha256));

        var clean = PrototypeScanner.FromIso(image, baseline);
        Assert.Empty(clean.BaselineDiff);
    }

    [Fact]
    public void FromIso_DetectsAnAddedDebugToolAgainstARetailBaseline()
    {
        var retailImage = IsoBuilder.Build("GAME", new List<IsoBuilder.FileEntry>
        {
            new("GAME.EXE", Encoding.ASCII.GetBytes("retail code")),
        }, joliet: false).Image;
        var baseline = PrototypeScanner.BuildBaseline(retailImage);

        var protoImage = IsoBuilder.Build("GAME", new List<IsoBuilder.FileEntry>
        {
            new("GAME.EXE", Encoding.ASCII.GetBytes("retail code")),
            new("EXTRA.EXE", Encoding.ASCII.GetBytes("dev-only tool")),   // 8.3-safe name (IsoBuilder truncates longer ones)
        }, joliet: false).Image;

        var report = PrototypeScanner.FromIso(protoImage, baseline);
        Assert.Contains(report.BaselineDiff, d => d.Kind == BaselineDiffKind.AddedHere && d.Path.Contains("extra.exe"));
        Assert.True(report.LooksLikePrototype);
    }
}
