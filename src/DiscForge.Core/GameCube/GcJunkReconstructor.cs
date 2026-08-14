// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.GameCube;

/// <summary>
/// Rebuilds the deterministic junk padding of a SCRUBBED GameCube image — but only when it can
/// first prove, byte-for-byte, that <see cref="GcJunkGenerator"/> reproduces the image's OWN
/// surviving junk. This is the "provably correct or declined" contract applied to a padding
/// regenerator whose underlying PRNG is not yet confirmed against Nintendo's:
///
///   1. Map the padding with <see cref="GcJunkMapper"/>.
///   2. For every region that STILL carries junk, regenerate it and compare. This is a free,
///      per-disc oracle: the disc's own intact junk is the ground truth.
///   3. If every intact region matches, the generator is proven for THIS disc → fill the scrubbed
///      (zeroed) regions with regenerated junk and write the reconstructed image.
///   4. If any intact region does NOT match, or there is no surviving junk to validate against,
///      DECLINE — copy the input through unchanged and report why. A wrong PRNG constant can then
///      only cause a decline, never a silent corruption.
///
/// So a fully-scrubbed image (no surviving junk) is intentionally declined until a real
/// Redump/NKit oracle confirms the generator; a partially-scrubbed image is self-validating and
/// can be completed today. Clean-room; defeats no protection and reconstructs only padding.
/// </summary>
public static class GcJunkReconstructor
{
    /// <summary>Bytes validated per surviving-junk region (bounded; spans block seams).</summary>
    public const int ValidationSampleBytes = 0x80000;   // 512 KiB → crosses 0x40000 block seams

    public sealed record Report
    {
        public required bool SelfValidated { get; init; }
        public required bool Reconstructed { get; init; }
        public required int IntactRegionsChecked { get; init; }
        public required long IntactBytesMatched { get; init; }
        public required int ScrubbedRegionsFilled { get; init; }
        public required long BytesFilled { get; init; }
        public required string Message { get; init; }
    }

    /// <summary>Copy <paramref name="input"/> to <paramref name="output"/>, filling scrubbed junk
    /// only if the generator self-validates against the image's surviving junk.</summary>
    public static Report Reconstruct(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        // The disc id is the first 4 bytes (e.g. "GALE").
        var discId = new byte[4];
        input.Seek(0, SeekOrigin.Begin);
        input.ReadExactly(discId, 0, 4);

        var map = GcJunkMapper.Analyze(input);

        var intact = map.Regions
            .Where(r => r.Class == JunkClass.Junk && r.Length >= GcJunkMapper.SignificantRegionBytes)
            .ToList();
        var scrubbed = map.Regions
            .Where(r => r.Class == JunkClass.Zeroed && r.Length >= GcJunkMapper.SignificantRegionBytes)
            .ToList();

        // Always produce a full copy first; we only overwrite scrubbed regions if validated.
        input.Seek(0, SeekOrigin.Begin);
        input.CopyTo(output);

        if (intact.Count == 0)
        {
            return new Report
            {
                SelfValidated = false,
                Reconstructed = false,
                IntactRegionsChecked = 0,
                IntactBytesMatched = 0,
                ScrubbedRegionsFilled = 0,
                BytesFilled = 0,
                Message = scrubbed.Count == 0
                    ? "No scrubbed padding to rebuild — nothing to do."
                    : "This image has no SURVIVING junk to validate the generator against (it looks " +
                      "fully scrubbed). The junk regenerator is unconfirmed, so filling is declined to " +
                      "avoid writing bytes that can't be proven correct. A partially-scrubbed dump, or a " +
                      "Redump/NKit oracle for this title, would unblock it.",
            };
        }

        // ---- Self-validate against every surviving-junk region. ----
        long matchedBytes = 0;
        var actual = new byte[ValidationSampleBytes];
        foreach (var r in intact)
        {
            int sample = (int)Math.Min(r.Length, ValidationSampleBytes);
            input.Seek(r.Start, SeekOrigin.Begin);
            input.ReadExactly(actual, 0, sample);
            var expected = GcJunkGenerator.Generate(discId, r.Start, sample);

            if (!actual.AsSpan(0, sample).SequenceEqual(expected))
            {
                return new Report
                {
                    SelfValidated = false,
                    Reconstructed = false,
                    IntactRegionsChecked = intact.Count,
                    IntactBytesMatched = matchedBytes,
                    ScrubbedRegionsFilled = 0,
                    BytesFilled = 0,
                    Message = $"The junk generator does not match this disc's surviving junk (mismatch in " +
                              $"the region at 0x{r.Start:X}). Reconstruction declined — the padding PRNG is " +
                              "not yet confirmed for this title, and a guess must not be written.",
                };
            }
            matchedBytes += sample;
        }

        // ---- Proven for this disc: fill the scrubbed regions. ----
        long filled = 0;
        foreach (var r in scrubbed)
        {
            long remaining = r.Length;
            long pos = r.Start;
            var buf = new byte[Math.Min(remaining, GcJunkGenerator.BlockSize)];
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, buf.Length);
                GcJunkGenerator.Fill(discId, pos, buf.AsSpan(0, chunk));
                output.Seek(pos, SeekOrigin.Begin);
                output.Write(buf, 0, chunk);
                pos += chunk;
                remaining -= chunk;
                filled += chunk;
            }
        }

        return new Report
        {
            SelfValidated = true,
            Reconstructed = scrubbed.Count > 0,
            IntactRegionsChecked = intact.Count,
            IntactBytesMatched = matchedBytes,
            ScrubbedRegionsFilled = scrubbed.Count,
            BytesFilled = filled,
            Message = scrubbed.Count > 0
                ? $"Generator self-validated against {intact.Count} surviving-junk region(s) " +
                  $"({matchedBytes:N0} bytes); rebuilt {scrubbed.Count} scrubbed region(s), {filled:N0} bytes."
                : $"Generator self-validated against {intact.Count} surviving-junk region(s); no scrubbed " +
                  "padding needed rebuilding.",
        };
    }
}
