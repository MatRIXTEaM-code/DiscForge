// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

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

    /// <summary>What comparing a raw (as-physically-read, interleaved) capture against the
    /// drive's own corrected (de-interleaved) capture of the same sectors found.</summary>
    public sealed record RwCaptureComparison
    {
        public required long SectorsCompared { get; init; }
        public required int QAgree { get; init; }
        public required int QDisagree { get; init; }
        /// <summary>Q was CRC-valid in the raw capture but not the corrected one, or vice versa —
        /// the more actionable half of a disagreement (a byte-level mismatch that leaves both
        /// CRC-valid is usually just which of two legitimate re-reads happened to land).</summary>
        public required int ValidityFlips { get; init; }
        public required int RawQValid { get; init; }
        public required int CorrectedQValid { get; init; }
        /// <summary>Sector indices (relative to the capture start) where the two disagreed,
        /// capped at 1,000 for a report that stays readable.</summary>
        public required IReadOnlyList<long> DisagreeingSectors { get; init; }

        public string Summary => QDisagree == 0
            ? $"raw and corrected agree on all {SectorsCompared:N0} sector(s) — one faithful reading, not two guesses."
            : $"{QDisagree:N0} of {SectorsCompared:N0} sector(s) disagree between raw and corrected " +
              $"({ValidityFlips:N0} of those flip whether Q's CRC even validates) — worth a closer look " +
              "before trusting either capture alone.";
    }

    /// <summary>
    /// Compare a raw interleaved capture (<see cref="RawSubcodeForm.Interleaved96"/> —
    /// <c>SubchannelReader.Read</c>) against the drive's own corrected capture of the same LBA
    /// range (<see cref="RawSubcodeForm.Packed96"/> — <c>SubchannelReader.ReadCorrected</c>),
    /// sector by sector, on their decoded Q content. This is the "most faithful capture" check:
    /// a drive's on-the-fly correction can mask a transient read error (harmless) or it can quietly
    /// paper over content a protection scheme deliberately corrupted (not harmless — see this
    /// class's LibCrypt note) — the two captures agreeing is itself the evidence either way.
    /// </summary>
    public static RwCaptureComparison CompareRawAndCorrected(
        ReadOnlySpan<byte> rawInterleaved, ReadOnlySpan<byte> correctedPacked, long sectors)
    {
        if (rawInterleaved.Length != sectors * FrameSize)
            throw new ArgumentException(
                $"Raw capture is {rawInterleaved.Length:N0} bytes, expected {sectors * FrameSize:N0} " +
                $"for {sectors:N0} sector(s).", nameof(rawInterleaved));
        if (correctedPacked.Length != sectors * FrameSize)
            throw new ArgumentException(
                $"Corrected capture is {correctedPacked.Length:N0} bytes, expected {sectors * FrameSize:N0} " +
                $"for {sectors:N0} sector(s).", nameof(correctedPacked));

        Span<byte> qRaw = stackalloc byte[12];
        Span<byte> qCorrected = stackalloc byte[12];
        int agree = 0, disagree = 0, flips = 0, rawValid = 0, correctedValid = 0;
        var mismatches = new List<long>();

        for (long i = 0; i < sectors; i++)
        {
            var rawFrame = rawInterleaved.Slice((int)(i * FrameSize), FrameSize);
            var correctedFrame = correctedPacked.Slice((int)(i * FrameSize), FrameSize);
            SubcodeFrame.ExtractQ(rawFrame, RawSubcodeForm.Interleaved96, qRaw);
            SubcodeFrame.ExtractQ(correctedFrame, RawSubcodeForm.Packed96, qCorrected);

            bool rawOk = QCrcValid(qRaw);
            bool correctedOk = QCrcValid(qCorrected);
            if (rawOk) rawValid++;
            if (correctedOk) correctedValid++;

            if (qRaw.SequenceEqual(qCorrected)) agree++;
            else
            {
                disagree++;
                if (rawOk != correctedOk) flips++;
                if (mismatches.Count < 1000) mismatches.Add(i);
            }
        }

        return new RwCaptureComparison
        {
            SectorsCompared = sectors,
            QAgree = agree,
            QDisagree = disagree,
            ValidityFlips = flips,
            RawQValid = rawValid,
            CorrectedQValid = correctedValid,
            DisagreeingSectors = mismatches,
        };
    }
}
