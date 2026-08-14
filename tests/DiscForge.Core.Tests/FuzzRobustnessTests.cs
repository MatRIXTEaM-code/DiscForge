// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Adversarial-input robustness. A preservation tool ingests damaged and untrusted disc images, so every
/// parser must fail *gracefully* on garbage — it may throw, but it must never hang (infinite loop) or
/// exhaust memory (an unbounded allocation driven by a malformed length/count field). This throws empty,
/// truncated, all-zero, random, and magic-prefixed inputs at each reader and asserts the call completes
/// quickly and does not run the process out of memory. Any thrown exception counts as a clean failure.
/// </summary>
public class FuzzRobustnessTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    // Each entry parses a byte[] however that reader wants (span/stream/text created inside).
    private static readonly (string Name, Action<byte[]> Invoke)[] Parsers =
    {
        ("GcmReader", d => DiscForge.Core.GameCube.GcmReader.Read(new MemoryStream(d))),
        ("GcBanner", d => DiscForge.Core.GameCube.GcBannerReader.Parse(d)),
        ("Tpl", d => DiscForge.Core.GameCube.Tpl.Read(d)),
        ("WiiDisc", d => DiscForge.Core.GameCube.WiiDisc.Read(new MemoryStream(d))),
        ("IsoLint", d => DiscForge.Core.Forensics.IsoLint.Check(d)),
        ("IsoPathTable", d => DiscForge.Core.Iso.IsoPathTable.Read(d)),
        ("ElTorito", d => DiscForge.Core.Iso.ElTorito.Read(d)),
        ("RockRidge", d => DiscForge.Core.Iso.RockRidge.Parse(d)),
        ("UdfFreeSpace", d => DiscForge.Core.Udf.UdfFreeSpace.Analyze(d)),
        ("HfsReader", d => DiscForge.Core.Hfs.HfsReader.Read(d)),
        ("HfsFreeSpace", d => DiscForge.Core.Hfs.HfsFreeSpace.Analyze(d)),
        ("HfsResourceFork", d => DiscForge.Core.Hfs.HfsResourceFork.Parse(d)),
        ("FatReader", d => { if (DiscForge.Core.Fat.FatReader.IsFat(d)) DiscForge.Core.Fat.FatReader.Read(d); }),
        ("Hdcd", d => DiscForge.Core.Audio.Hdcd.ScanPcmBytes(d, 2)),
        ("OperaFs", d => DiscForge.Core.ThreeDo.OperaFs.Read(d)),
        ("SegaCd", d => DiscForge.Core.SegaCd.SegaCdDisc.Parse(d)),
        ("ApmDisk", d => DiscForge.Core.Partition.ApmDisk.Read(d)),
        ("RdbDisk", d => DiscForge.Core.Partition.RdbDisk.Read(d)),
        ("VcdPsd", d => DiscForge.Core.VideoCd.VcdPsd.Parse(d)),
        ("XaStreamMap", d => DiscForge.Core.PlayStation.XaStreamMap.Analyze(d)),
        ("CdTextReader", d => DiscForge.Core.Raw.CdTextReader.ReadPackStream(d)),
        ("Sbi", d => DiscForge.Core.PlayStation.Sbi.Parse(d)),
        ("LibcryptAnalyzer", d => DiscForge.Core.PlayStation.LibcryptAnalyzer.Scan(d)),
        ("EfmSpectrum", d => DiscForge.Core.Forensics.EfmSpectrum.Analyze(d)),
        ("NeoGeoCdIpl", d => DiscForge.Core.Rom.NeoGeoCdIpl.Parse(d)),
        ("N64Cic", d => DiscForge.Core.Rom.N64Cic.Analyze(d)),
        ("MdecHeader", d => DiscForge.Core.PlayStation.Mdec.ParseFrameHeader(d)),
        ("Bler", d => DiscForge.Core.Forensics.Bler.ParseCsv(Encoding.Latin1.GetString(d))),
        ("Dpm", d => DiscForge.Core.Forensics.Dpm.ParseCsv(Encoding.Latin1.GetString(d))),
    };

    // Adversarial inputs: empty, single byte, all-zero and random at several sizes, and a run of 0xFF
    // (max-value length fields). Sizes stay small so a *legitimate* parse is always fast.
    // Known magics, placed at the offset each reader looks for them, so the fuzz pushes PAST the
    // signature check into the length-field logic where hangs / unbounded allocations would live.
    private static readonly (int Offset, byte[] Magic)[] Magics =
    {
        (0, new byte[] { 0x53, 0x42, 0x49, 0x00 }),               // "SBI\0"
        (0, new byte[] { 0x01, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A }),   // "\x01ZZZZZ" (3DO Opera)
        (0, new byte[] { 0x52, 0x44, 0x53, 0x4B }),               // "RDSK" (Amiga)
        (0, new byte[] { 0x45, 0x52 }),                           // "ER"  (APM driver record)
        (0, Encoding.ASCII.GetBytes("SEGADISCSYSTEM  ")),         // Sega CD
        (0, new byte[] { 0x18, 0x00, 0x0F, 0x00 }),               // VCD PSD selection list, nos = 15
        (0x1C, new byte[] { 0xC2, 0x33, 0x9F, 0x3D }),            // GameCube magic at 0x1C
        (0x400, new byte[] { 0x42, 0x44 }),                       // "BD" (HFS MDB at 0x400)
    };

    private static IEnumerable<byte[]> Corpus()
    {
        yield return Array.Empty<byte>();
        yield return new byte[] { 0 };
        int[] sizes = { 16, 64, 300, 512, 2048, 4096, 9000 };
        var rng = new Random(1234);
        foreach (int n in sizes)
        {
            yield return new byte[n];                          // all zero
            var r = new byte[n]; rng.NextBytes(r); yield return r;   // random
            var ff = new byte[n]; Array.Fill(ff, (byte)0xFF); yield return ff;   // all 0xFF
        }

        // Magic-prefixed: a valid header followed by 0xFF and random tails (max-value length fields).
        foreach (var (off, magic) in Magics)
            foreach (int n in new[] { 512, 4096, 9000 })
            {
                if (off + magic.Length > n) continue;
                var ff = new byte[n]; Array.Fill(ff, (byte)0xFF); magic.CopyTo(ff, off); yield return ff;
                var rr = new byte[n]; rng.NextBytes(rr); magic.CopyTo(rr, off); yield return rr;
            }
    }

    [Fact]
    public void Every_parser_fails_gracefully_on_garbage()
    {
        var failures = new List<string>();

        foreach (var (name, invoke) in Parsers)
        {
            foreach (var input in Corpus())
            {
                var data = input;   // capture
                OutOfMemoryException? oom = null;
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    try { invoke(data); }
                    catch (OutOfMemoryException e) { oom = e; }   // unbounded allocation → a real bug
                    catch { /* any other exception is a clean failure */ }
                });

                // A bounded blocking wait is exactly the point here: detect a parser that
                // never returns on garbage input. WaitAsync would swap the timeout for an
                // exception and lose the clean HANG verdict, so suppress the async advice.
#pragma warning disable xUnit1031 // blocking task op — intentional bounded hang-detector
                if (!task.Wait(Budget))
#pragma warning restore xUnit1031
                    failures.Add($"{name}: HANG on {input.Length}-byte input");
                else if (oom != null)
                    failures.Add($"{name}: OUT-OF-MEMORY on {input.Length}-byte input");
            }
        }

        Assert.True(failures.Count == 0, "Robustness failures:\n  " + string.Join("\n  ", failures));
    }
}
