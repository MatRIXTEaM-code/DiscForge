// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// Forensic test from the first hardware burn round-trip: a Mode 2 track whose
/// image contains two Mode 1 replacement dummies (what the old Replace policy
/// produced) must still generate a correct raw stream — every sector after the
/// dummies scrambled and byte-faithful. This pins the burn-side content path as
/// innocent of the "everything after sector N is unreadable" failure, leaving
/// write speed / calibration as the remaining suspect for the physical disc.
/// </summary>
public class RawImageGeneratorDummyTests
{
    [Fact]
    public void Mode1DummiesInsideAMode2Track_DoNotDerailTheStream()
    {
        // A 40-sector Mode 2 "disc": sectors 0..19 real, 20..21 the alien Mode 1
        // dummies, 22..39 real again — the shape of the burned image, miniaturised.
        const int sectors = 40;
        var bin = new byte[sectors * 2352];
        for (int s = 0; s < sectors; s++)
        {
            var sec = bin.AsSpan(s * 2352, 2352);
            var msf = Msf.FromSectors(s + 150);
            if (s is 20 or 21)
            {
                RawSectorBuilder.BuildMode1(new byte[2048], msf, sec);   // the old dummies
            }
            else
            {
                RawSectorBuilder.WriteSync(sec);
                RawSectorBuilder.WriteHeader(sec, msf, mode: 2);
                sec[18] = 0x08; sec[22] = 0x08;
                for (int i = 24; i < 2072; i++) sec[i] = (byte)((s * 31 + i) & 0xFF);
                EdcEcc.FillMode2Form1(sec);
            }
        }

        string dir = Path.Combine(Path.GetTempPath(), "df_gen_dummy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "t.bin"), bin);
            File.WriteAllText(Path.Combine(dir, "t.cue"),
                "FILE \"t.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");

            using var layout = DiscLayout.FromCueFile(Path.Combine(dir, "t.cue"));
            using var img = new MemoryStream();
            const int leadIn = 10;
            RawImageGenerator.Generate(layout, RawSubcodeForm.Interleaved96, img, leadInSectors: leadIn);

            // Locate the program area and check EVERY stored sector, with special
            // attention to the ones after the dummies: descrambling the generated
            // frame must give back exactly the stored sector (headers re-addressed
            // is a pass-through here since addresses already match).
            var frames = img.ToArray();
            const int frameSize = 2352 + 96;
            long programStart = (long)(leadIn + layout.Tracks[0].PregapGeneratedSectors) * frameSize;

            for (int s = 0; s < sectors; s++)
            {
                var frame = frames.AsSpan((int)(programStart + (long)s * frameSize), 2352).ToArray();
                CdScrambler.ScrambleInPlace(frame);          // scramble is an involution: this descrambles
                Assert.True(frame.AsSpan().SequenceEqual(bin.AsSpan(s * 2352, 2352)),
                    $"sector {s} was not carried byte-faithfully through the generator");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
