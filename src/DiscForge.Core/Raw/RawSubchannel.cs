// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Util;

namespace DiscForge.Core.Raw;

/// <summary>
/// A verbatim raw subchannel sidecar: 96 bytes per sector, interleaved P-W,
/// exactly as a drive returned it — deliberate errors and all. This is the
/// piece that makes faithful console backups possible.
///
/// Why verbatim matters: some discs (PlayStation's LibCrypt, and similar
/// schemes) hide protection in intentionally CORRUPTED sub-channel Q — a
/// handful of sectors whose Q-CRC is deliberately wrong. Tools that
/// "repair" the sub-channel on write destroy the protection and produce a
/// disc the console rejects; the ones that mattered (CloneCD's "don't repair
/// sub-channel data", BlindWrite) wrote the captured sub-channel byte for
/// byte. DiscForge's generated Q is always CORRECT, which is exactly wrong
/// for these discs — so a faithful copy has to carry, and re-emit, the
/// source's own sub-channel unchanged.
///
/// The file is a bare stream of 96-byte frames, LBA-ordered from the track's
/// first sector — the same on-disk convention CloneCD's .sub uses, so the
/// files interoperate. Analysis (CRC validity, LibCrypt fingerprinting) is a
/// separate read-only pass; the bytes are never altered.
/// </summary>
public static class RawSubchannel
{
    public const int FrameSize = 96;

    /// <summary>Extract the 12-byte Q frame from a raw interleaved 96-byte
    /// sub frame (bit 6 of each byte, MSB-first into Q).</summary>
    public static void ExtractQ(ReadOnlySpan<byte> sub96, Span<byte> q12)
    {
        q12.Clear();
        for (int i = 0; i < 96; i++)
            if ((sub96[i] & 0x40) != 0)
                q12[i >> 3] |= (byte)(0x80 >> (i & 7));
    }

    /// <summary>True when the Q frame's stored CRC matches its contents.</summary>
    public static bool QCrcValid(ReadOnlySpan<byte> q12)
        => Crc16.ComputeInverted(q12[..10]) == (ushort)((q12[10] << 8) | q12[11]);

    public sealed record Analysis
    {
        public required long Frames { get; init; }
        public required int QValid { get; init; }
        public required int QInvalid { get; init; }
        /// <summary>Absolute LBAs (relative to the sidecar start) whose Q-CRC
        /// is wrong — the candidate protection sectors.</summary>
        public required IReadOnlyList<long> InvalidLbas { get; init; }
        /// <summary>True when the invalid-Q pattern looks like deliberate
        /// protection rather than a bad rip: a small number of corrupt Q
        /// frames scattered across an otherwise valid stream.</summary>
        public required bool LooksLikeLibCrypt { get; init; }
        /// <summary>How many invalid Q frames sit within a few sectors of another
        /// invalid frame. LibCrypt corrupts sectors in close pairs, so a high
        /// proportion here strengthens the protection reading; scattered damage
        /// leaves this low.</summary>
        public int PairedInvalid { get; init; }
        /// <summary>True when the invalid frames are predominantly paired — the
        /// LibCrypt shape rather than random media damage.</summary>
        public bool Paired { get; init; }
        public string Summary => LooksLikeLibCrypt
            ? $"{QInvalid} deliberately-corrupt Q frame(s) among {Frames:N0} — " +
              "consistent with LibCrypt-style protection; preserve verbatim."
            : QInvalid == 0
                ? $"all {Frames:N0} Q frames valid — no sub-channel protection detected."
                : $"{QInvalid} invalid Q frame(s) of {Frames:N0} — likely read errors, " +
                  "not a protection pattern.";
    }

    /// <summary>
    /// Read-only analysis of a captured sidecar: count Q-CRC validity and
    /// decide whether the invalid frames form a LibCrypt-like fingerprint.
    /// </summary>
    public static Analysis Analyse(Stream subcode)
    {
        long frames = subcode.Length / FrameSize;
        var frame = new byte[FrameSize];
        var q = new byte[12];
        int valid = 0, invalid = 0;
        var invalidLbas = new List<long>();

        subcode.Position = 0;
        for (long i = 0; i < frames; i++)
        {
            subcode.ReadExactly(frame, 0, FrameSize);
            ExtractQ(frame, q);
            if (QCrcValid(q)) valid++;
            else
            {
                invalid++;
                if (invalidLbas.Count < 1000) invalidLbas.Add(i);
            }
        }

        // LibCrypt fingerprint: a small, non-zero number of corrupt Q frames
        // (single-digit to low tens) in an otherwise-valid stream. A rip with
        // hundreds of invalid frames is damage, not protection; zero is a
        // plain disc. The real LibCrypt schemes corrupt ~16 sectors in two
        // clusters, but we don't require the exact count — just the shape.
        bool fingerprint = invalid is > 0 and <= 64 && frames > 1000 &&
                           invalid < frames / 100;

        // LibCrypt corrupts sectors in close pairs. Count how many invalid
        // frames have another invalid frame within a few sectors: a high share
        // is the protection shape, a low one is scattered damage. Purely
        // informational — it sharpens the report without changing the verdict.
        const int PairWindow = 10;
        int paired = 0;
        for (int i = 0; i < invalidLbas.Count; i++)
        {
            bool near =
                (i > 0 && invalidLbas[i] - invalidLbas[i - 1] <= PairWindow) ||
                (i + 1 < invalidLbas.Count && invalidLbas[i + 1] - invalidLbas[i] <= PairWindow);
            if (near) paired++;
        }
        bool predominantlyPaired = invalid >= 2 && paired * 2 >= invalid;

        return new Analysis
        {
            Frames = frames,
            QValid = valid,
            QInvalid = invalid,
            InvalidLbas = invalidLbas,
            LooksLikeLibCrypt = fingerprint,
            PairedInvalid = paired,
            Paired = predominantlyPaired,
        };
    }

    /// <summary>
    /// Validate that a sidecar matches a track's sector count before it's
    /// trusted — a length mismatch means the wrong file or a different dump
    /// format, and must fail loudly rather than produce garbage sub-channel.
    /// </summary>
    public static void ValidateLength(long sidecarBytes, long trackSectors)
    {
        if (sidecarBytes != trackSectors * FrameSize)
            throw new InvalidDataException(
                $"The subchannel sidecar is {sidecarBytes:N0} bytes but the track has " +
                $"{trackSectors:N0} sectors, needing {trackSectors * FrameSize:N0} " +
                "(96 bytes per sector). Wrong file, or a different dump format.");
    }
}
