// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Core.Raw;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// Reads a disc's sub-channel into memory so it can be analysed.
///
/// The sub-channel is the eight bits per frame that sit alongside the audio or
/// data — P through W, 96 bytes for every 2352-byte sector. Q carries timing and
/// track position, and is the one that matters here: its CRC either validates or
/// it doesn't, and a handful of deliberate failures scattered across a disc is
/// the signature of LibCrypt and its relatives. Damage produces failures too,
/// but far more of them and in bursts rather than isolated frames.
///
/// Reading it live rather than from a sidecar matters because a sidecar only
/// exists if something captured one. Analysing a disc you have in your hand
/// shouldn't require having imaged it first.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SubchannelReader
{
    /// <summary>Sectors per read. Each carries 2352 main bytes plus 96 of
    /// sub-channel, so 20 keeps the transfer under 50 KB.</summary>
    private const uint SectorsPerRead = 20;

    private const int MainBytes = 2352;
    private const int SubBytes = 96;

    public sealed record ReadResult
    {
        /// <summary>The raw interleaved P–W frames, 96 bytes per sector.</summary>
        public required byte[] Subcode { get; init; }
        public required uint StartLba { get; init; }
        public required uint SectorsRead { get; init; }
        public required uint SectorsRefused { get; init; }
        /// <summary>Why the drive refused, when it did.</summary>
        public string? RefusalReason { get; init; }

        public bool Complete => SectorsRefused == 0;
    }

    /// <summary>
    /// Check whether the drive will return sub-channel at all.
    ///
    /// Worth asking before a long read: some drives refuse the raw P–W selector
    /// entirely, some return it but with the R–W channels zeroed, and the mode
    /// page's claim about sub-channel support is not always honoured. One sector
    /// settles it.
    /// </summary>
    public static bool SupportsRawSubchannel(SptiDevice dev, uint testLba = 0)
    {
        var buffer = new byte[MainBytes + SubBytes];
        var r = dev.SendCommand(
            MmcCommands.ReadCd(testLba, 1, MmcCommands.ExpectedSectorType.Any,
                               MmcCommands.SectorFields.Raw, MmcCommands.SubChannel.RawPw),
            buffer, SptiDataDirection.In, timeoutSeconds: 20);
        return r.Success;
    }

    /// <summary>Check whether the drive will return CORRECTED, de-interleaved R-W sub-channel
    /// (MMC sub-channel selector 100) — the drive's own firmware-corrected reading, in the same
    /// 96-bytes-per-sector shape <see cref="Core.Raw.RawSubcodeForm.Packed96"/> describes, as
    /// opposed to the as-physically-read <see cref="Core.Raw.RawSubcodeForm.Interleaved96"/> shape
    /// <see cref="Read"/> captures. Not every drive supports this selector even when it supports
    /// raw P-W — worth checking before a long read for the same reason as
    /// <see cref="SupportsRawSubchannel"/>.</summary>
    public static bool SupportsCorrectedSubchannel(SptiDevice dev, uint testLba = 0)
    {
        var buffer = new byte[MainBytes + SubBytes];
        var r = dev.SendCommand(
            MmcCommands.ReadCd(testLba, 1, MmcCommands.ExpectedSectorType.Any,
                               MmcCommands.SectorFields.Raw, MmcCommands.SubChannel.CorrectedRw),
            buffer, SptiDataDirection.In, timeoutSeconds: 20);
        return r.Success;
    }

    /// <summary>
    /// Read sub-channel for a range of sectors.
    ///
    /// Sectors the drive refuses are filled with zeros and counted rather than
    /// abandoning the read: a disc with a damaged region still has analysable
    /// sub-channel either side of it, and refusing to report any of it because
    /// some sectors failed would be unhelpful. Zero frames fail their CRC, so
    /// they are visible in the analysis as invalid rather than silently passing.
    /// </summary>
    public static ReadResult Read(SptiDevice dev, uint startLba, uint sectorCount,
                                  IProgress<double>? progress = null,
                                  CancellationToken cancel = default)
        => ReadCore(dev, startLba, sectorCount, MmcCommands.SubChannel.RawPw, progress, cancel);

    /// <summary>
    /// Read the drive's own CORRECTED, de-interleaved R-W sub-channel (MMC selector 100) for a
    /// range of sectors — the same firmware-corrected reading <see cref="SupportsCorrectedSubchannel"/>
    /// probes for. The returned bytes are in the PACKED shape
    /// (<see cref="Core.Raw.RawSubcodeForm.Packed96"/>: 12 bytes each of P, Q, R, S, T, U, V, W in
    /// turn), not the interleaved shape <see cref="Read"/> returns — decode with
    /// <c>SubcodeFrame.ExtractQ(sub, RawSubcodeForm.Packed96, ...)</c> (or ExtractRw) accordingly.
    ///
    /// Capturing both this and a raw read of the same range is the "most faithful capture": where
    /// the two disagree, either the drive's correction altered something a plain re-read would show
    /// as noise, or the raw capture itself hit a transient error the correction fixed — either way,
    /// worth a second look rather than trusting one reading blind. See
    /// <see cref="Core.Raw.RawSubchannel.CompareRawAndCorrected"/>.
    /// </summary>
    public static ReadResult ReadCorrected(SptiDevice dev, uint startLba, uint sectorCount,
                                           IProgress<double>? progress = null,
                                           CancellationToken cancel = default)
        => ReadCore(dev, startLba, sectorCount, MmcCommands.SubChannel.CorrectedRw, progress, cancel);

    private static ReadResult ReadCore(SptiDevice dev, uint startLba, uint sectorCount,
                                       MmcCommands.SubChannel selector,
                                       IProgress<double>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(dev);
        if (sectorCount == 0) throw new ArgumentException("No sectors requested.", nameof(sectorCount));

        var subcode = new byte[(long)sectorCount * SubBytes];
        var buffer = new byte[SectorsPerRead * (MainBytes + SubBytes)];
        uint refused = 0;
        string? refusalReason = null;

        uint done = 0;
        while (done < sectorCount)
        {
            cancel.ThrowIfCancellationRequested();

            uint chunk = Math.Min(SectorsPerRead, sectorCount - done);
            var span = buffer.AsSpan(0, (int)(chunk * (MainBytes + SubBytes)));

            var r = dev.SendCommand(
                MmcCommands.ReadCd(startLba + done, chunk, MmcCommands.ExpectedSectorType.Any,
                                   MmcCommands.SectorFields.Raw, selector),
                span, SptiDataDirection.In, timeoutSeconds: 60);

            if (r.Success)
            {
                // Sub-channel follows the main data within each sector, so it
                // has to be picked out one sector at a time rather than copied
                // as a block.
                for (uint i = 0; i < chunk; i++)
                {
                    int from = (int)(i * (MainBytes + SubBytes)) + MainBytes;
                    long to = (long)(done + i) * SubBytes;
                    span.Slice(from, SubBytes).CopyTo(subcode.AsSpan((int)to));
                }
            }
            else
            {
                // Narrow it down: one bad sector shouldn't cost twenty.
                refusalReason ??= r.Describe();
                for (uint i = 0; i < chunk; i++)
                {
                    cancel.ThrowIfCancellationRequested();

                    var one = buffer.AsSpan(0, MainBytes + SubBytes);
                    var single = dev.SendCommand(
                        MmcCommands.ReadCd(startLba + done + i, 1,
                                           MmcCommands.ExpectedSectorType.Any,
                                           MmcCommands.SectorFields.Raw,
                                           selector),
                        one, SptiDataDirection.In, timeoutSeconds: 30);

                    long to = (long)(done + i) * SubBytes;
                    if (single.Success)
                    {
                        one.Slice(MainBytes, SubBytes).CopyTo(subcode.AsSpan((int)to));
                    }
                    else
                    {
                        // Leave it zeroed. A zero frame fails its Q CRC, so it
                        // shows up as invalid in the analysis rather than
                        // quietly passing as good sub-channel.
                        refused++;
                    }
                }
            }

            done += chunk;
            progress?.Report((double)done / sectorCount);
        }

        return new ReadResult
        {
            Subcode = subcode,
            StartLba = startLba,
            SectorsRead = sectorCount - refused,
            SectorsRefused = refused,
            RefusalReason = refused > 0 ? refusalReason : null,
        };
    }
}