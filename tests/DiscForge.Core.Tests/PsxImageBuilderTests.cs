// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Files;
using DiscForge.Core.Iso;
using DiscForge.Core.PlayStation;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// psxbuild — building a raw Mode 2/2352 image from files. Two independent
/// guarantees: every cooked sector's EDC and ECC verify algebraically (not by
/// re-running the encoder), and the built image browses back to the exact files
/// that went in, proving the whole psxbuild → psxrip loop.
/// </summary>
public class PsxImageBuilderTests
{
    private sealed class Temp : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "df-psxbuild-" + Guid.NewGuid().ToString("N")[..8]);
        public Temp() => Directory.CreateDirectory(Dir);
        public string P(string n) => Path.Combine(Dir, n);
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void EveryCookedSector_HasValidEdcAndEcc()
    {
        var iso = IsoBuilder.Build("PSXTEST", new[]
        {
            new IsoBuilder.FileEntry("DATA.BIN", Enumerable.Range(0, 9000).Select(i => (byte)i).ToArray()),
        }).Image;

        var bin = PsxImageBuilder.FromIso(iso);
        int sectors = bin.Length / 2352;
        Assert.True(sectors > 16);   // system area + PVD + data

        for (int k = 0; k < sectors; k++)
        {
            var (edcOk, eccOk) = EdcEcc.VerifyMode2Form1(bin.AsSpan(k * 2352, 2352));
            Assert.True(edcOk, $"sector {k} EDC");
            Assert.True(eccOk, $"sector {k} ECC");
        }
    }

    [Fact]
    public void HeaderCarriesTheSectorAddress()
    {
        var iso = IsoBuilder.Build("PSXTEST", new[]
        {
            new IsoBuilder.FileEntry("A.TXT", Encoding.ASCII.GetBytes("hi")),
        }).Image;
        var bin = PsxImageBuilder.FromIso(iso);

        // Sector 16 sits at absolute 16 + 150 = 166 = 00:02:16 → BCD 00 02 16.
        var s16 = bin.AsSpan(16 * 2352, 16);
        Assert.Equal(0x00, s16[0]);          // sync start
        Assert.Equal(0xFF, s16[1]);
        Assert.Equal(0x00, s16[12]);         // M
        Assert.Equal(0x02, s16[13]);         // S
        Assert.Equal(0x16, s16[14]);         // F (BCD 16)
        Assert.Equal(0x02, s16[15]);         // mode 2
    }

    [Fact]
    public void BuiltImage_BrowsesBackToTheSameFiles()
    {
        using var t = new Temp();
        var payload = Encoding.ASCII.GetBytes("psxbuild → psxrip round trip must be byte-exact.");
        var iso = IsoBuilder.Build("PSXTEST", new[]
        {
            new IsoBuilder.FileEntry("HELLO.TXT", payload),
        }).Image;

        var bin = PsxImageBuilder.FromIso(iso);
        string binPath = t.P("game.bin");
        File.WriteAllBytes(binPath, bin);
        File.WriteAllText(t.P("game.cue"), PsxImageBuilder.CueFor("game.bin"));

        var listing = ImageBrowser.List(binPath);
        Assert.Null(listing.Error);
        Assert.Contains(listing.Files, f => f.Path.EndsWith("HELLO.TXT", StringComparison.OrdinalIgnoreCase));

        string outDir = t.P("out");
        var result = ImageBrowser.Extract(binPath, listing.Files, outDir);
        Assert.Equal(0, result.Failed);

        var extracted = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
            .First(p => p.EndsWith("HELLO.TXT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(payload, File.ReadAllBytes(extracted));
    }

    [Fact]
    public void BuildFromFolder_EndToEnd()
    {
        using var t = new Temp();
        string src = t.P("src");
        Directory.CreateDirectory(Path.Combine(src, "SUB"));
        File.WriteAllBytes(Path.Combine(src, "ROOT.DAT"), new byte[] { 1, 2, 3, 4, 5 });
        File.WriteAllBytes(Path.Combine(src, "SUB", "NESTED.DAT"), new byte[] { 9, 8, 7 });

        int sectors = PsxImageBuilder.BuildFromFolder(src, "PSXFOLDER", t.P("out.bin"), t.P("out.cue"));
        Assert.True(sectors > 16);

        var listing = ImageBrowser.List(t.P("out.bin"));
        Assert.Null(listing.Error);
        Assert.Contains(listing.Files, f => f.Path.EndsWith("ROOT.DAT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(listing.Files, f => f.Path.EndsWith("NESTED.DAT", StringComparison.OrdinalIgnoreCase));
    }
}
