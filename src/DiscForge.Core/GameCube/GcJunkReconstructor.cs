// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Util;

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
///
/// The self-validation above proves the generator against THIS disc's own surviving junk — it does
/// NOT by itself prove the reconstructed image is byte-identical to a specific Redump-verified
/// dump (a self-validated generator could in principle still diverge somewhere the surviving junk
/// never sampled). <see cref="Reconstruct"/> therefore always reports the finished output's CRC-32,
/// and a caller who has a Redump entry's known-good CRC-32 for this exact title can pass it in as
/// <c>expectedCrc32</c> for a genuine independent confirmation — the "confirm by CRC32" step the
/// roadmap asks for. Nothing here fabricates or bundles Redump data; this session has no such
/// database to draw from, so the check only runs when the caller supplies the value themselves.
/// </summary>
public static class GcJunkReconstructor
{
    /// <summary>Bytes validated per surviving-junk region (bounded; spans block seams).</summary>
    public const int ValidationSampleBytes = 0x80000;   // 512 KiB → crosses 0x40000 block seams

    /// <summary>Chunk size used to stream-hash the finished output for its CRC-32.</summary>
    private const int HashChunkBytes = 1024 * 1024;

    public sealed record Report
    {
        public required bool SelfValidated { get; init; }
        public required bool Reconstructed { get; init; }
        public required int IntactRegionsChecked { get; init; }
        public required long IntactBytesMatched { get; init; }
        public required int ScrubbedRegionsFilled { get; init; }
        public required long BytesFilled { get; init; }
        public required string Message { get; init; }
        /// <summary>CRC-32 of the finished output, always computed regardless of whether anything
        /// was reconstructed — a plain declined-and-copied output still gets one, so it can be
        /// checked against a Redump entry even when this call didn't need to fill anything.</summary>
        public uint OutputCrc32 { get; init; }
        /// <summary>The CRC-32 the caller expected, when one was supplied.</summary>
        public uint? ExpectedCrc32 { get; init; }
        /// <summary>True/false only when <see cref="ExpectedCrc32"/> was supplied — an independent
        /// confirmation against a caller-known-good value, separate from the self-validation above.</summary>
        public bool? CrcConfirmed => ExpectedCrc32 is { } e ? e == OutputCrc32 : null;
    }

    /// <summary>Copy <paramref name="input"/> to <paramref name="output"/>, filling scrubbed junk
    /// only if the generator self-validates against the image's surviving junk. <paramref name="output"/>
    /// must support seeking and reading back (a <see cref="FileStream"/> or <see cref="MemoryStream"/>) so
    /// the finished image's CRC-32 can be computed after writing. When <paramref name="expectedCrc32"/> is
    /// supplied, the report's <see cref="Report.CrcConfirmed"/> says whether it matches — an independent
    /// check against a known-good value, on top of (not instead of) the self-validation gate.</summary>
    public static Report Reconstruct(Stream input, Stream output, uint? expectedCrc32 = null)
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
                OutputCrc32 = HashOutput(output),
                ExpectedCrc32 = expectedCrc32,
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
                    OutputCrc32 = HashOutput(output),
                    ExpectedCrc32 = expectedCrc32,
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
            OutputCrc32 = HashOutput(output),
            ExpectedCrc32 = expectedCrc32,
        };
    }

    /// <summary>Stream-hash the finished output for its CRC-32 without loading it into memory —
    /// safe for a multi-hundred-MB/GB disc image. Leaves the stream positioned at its end.</summary>
    private static uint HashOutput(Stream output)
    {
        output.Seek(0, SeekOrigin.Begin);
        var crc = new Crc32();
        var buf = new byte[HashChunkBytes];
        int n;
        while ((n = output.Read(buf, 0, buf.Length)) > 0)
            crc.Update(buf.AsSpan(0, n));
        return crc.Value;
    }
}
