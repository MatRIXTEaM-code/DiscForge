// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Core.Recovery;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// Reads a disc back as full raw 2448-byte sectors (2352 main + 96-byte raw interleaved P-W
/// sub-channel) via READ CD (0xBE). This is the read-back half of the RAW-DAO burn proof: dump
/// the program area to a file, then feed it to <c>raw-verify-readback</c> against the golden
/// image to show the burn is byte-faithful down to the sub-channel — which is the whole point of
/// writing RAW DAO-96 in the first place.
///
/// The main-channel field selection is auto-probed because it depends on the track type: a data
/// sector's full 2352 bytes come from <see cref="MmcCommands.SectorFields.Raw"/>, but CD-DA has
/// no sync/header/EDC/ECC, so asking for Raw on audio is an illegal field combination the drive
/// rejects — audio's 2352-byte frame comes from <see cref="MmcCommands.SectorFields.UserData"/>.
/// Either way the on-wire sector is 2352 + 96 = 2448 bytes.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RawDiscReader
{
    private const int MainBytes = 2352;
    private const int SubBytes = 96;
    private const int RawSectorBytes = MainBytes + SubBytes;   // 2448
    private const int SectorsPerRead = 20;                     // 20 × 2448 = 48,960 B

    public sealed record Result
    {
        public required long SectorsRead { get; init; }
        public required long BytesWritten { get; init; }
        public required string FieldMode { get; init; }        // "Raw (data)" or "UserData (audio)"
        public string? StoppedReason { get; init; }            // why an open-ended read ended
    }

    /// <summary>Which main-channel field mode to read a track in — forced, or auto-probed.</summary>
    public enum FieldSelect { Auto, Data, Audio }

    /// <summary>
    /// Read raw sectors from <paramref name="startLba"/> into <paramref name="output"/>. If
    /// <paramref name="maxSectors"/> is null the read runs until the drive refuses a sector (the
    /// end of the recorded program area); otherwise it reads exactly that many.
    ///
    /// The main-channel field mode is auto-probed by default (data uses Raw, CD-DA uses UserData —
    /// Raw is an illegal field combination on audio). At a data→audio track boundary that probe
    /// can mis-pick, so <paramref name="field"/> lets a caller FORCE the mode for a single-track
    /// read (e.g. reading the audio track of a mixed-mode disc on its own).
    /// </summary>
    public static Result Read(SptiDevice dev, int startLba, long? maxSectors, Stream output,
                              IProgress<double>? progress = null, FieldSelect field = FieldSelect.Auto)
    {
        ArgumentNullException.ThrowIfNull(dev);
        ArgumentNullException.ThrowIfNull(output);

        var probe = new byte[RawSectorBytes];
        MmcCommands.SectorFields fields;
        if (field == FieldSelect.Data)
            fields = MmcCommands.SectorFields.Raw;          // forced data — no probe
        else if (field == FieldSelect.Audio)
            fields = MmcCommands.SectorFields.UserData;     // forced audio — no probe
        else
        {
            // Auto: try data-raw first, fall back to audio user-data.
            fields = MmcCommands.SectorFields.Raw;
            var pr = dev.SendCommand(
                MmcCommands.ReadCd((uint)startLba, 1, MmcCommands.ExpectedSectorType.Any,
                                   MmcCommands.SectorFields.Raw, MmcCommands.SubChannel.RawPw),
                probe, SptiDataDirection.In, 20);
            if (!pr.Success)
            {
                var pr2 = dev.SendCommand(
                    MmcCommands.ReadCd((uint)startLba, 1, MmcCommands.ExpectedSectorType.Any,
                                       MmcCommands.SectorFields.UserData, MmcCommands.SubChannel.RawPw),
                    probe, SptiDataDirection.In, 20);
                if (!pr2.Success)
                    throw new IOException($"The drive refused a raw read at LBA {startLba} in both data " +
                                          $"(Raw) and audio (UserData) modes: {pr.Describe()}");
                fields = MmcCommands.SectorFields.UserData;
            }
        }
        string fieldMode = fields == MmcCommands.SectorFields.Raw ? "Raw (data)" : "UserData (audio)";

        var buffer = new byte[SectorsPerRead * RawSectorBytes];
        long read = 0;
        int lba = startLba;
        string? stopped = null;

        while (maxSectors is null || read < maxSectors.Value)
        {
            int chunk = SectorsPerRead;
            if (maxSectors is not null)
                chunk = (int)Math.Min(chunk, maxSectors.Value - read);

            var span = buffer.AsSpan(0, chunk * RawSectorBytes);
            var r = dev.SendCommand(
                MmcCommands.ReadCd((uint)lba, (uint)chunk, MmcCommands.ExpectedSectorType.Any,
                                   fields, MmcCommands.SubChannel.RawPw),
                span, SptiDataDirection.In, 60);

            if (!r.Success)
            {
                // Narrow down: salvage whole sectors before the first bad one in this chunk.
                int good = 0;
                for (; good < chunk; good++)
                {
                    var one = buffer.AsSpan(good * RawSectorBytes, RawSectorBytes);
                    var single = dev.SendCommand(
                        MmcCommands.ReadCd((uint)(lba + good), 1, MmcCommands.ExpectedSectorType.Any,
                                           fields, MmcCommands.SubChannel.RawPw),
                        one, SptiDataDirection.In, 30);
                    if (!single.Success) break;
                }
                if (good > 0)
                {
                    output.Write(buffer.AsSpan(0, good * RawSectorBytes));
                    read += good;
                    lba += good;
                }
                stopped = r.Describe();
                if (maxSectors is null) break;                 // reached the end of the program area
                // With an explicit length, a mid-read refusal is a real error.
                throw new IOException($"Raw read failed at LBA {lba} after {read:N0} sectors: {r.Describe()}");
            }

            output.Write(span);
            read += chunk;
            lba += chunk;
            if (maxSectors is not null) progress?.Report((double)read / maxSectors.Value);
        }

        return new Result
        {
            SectorsRead = read,
            BytesWritten = read * RawSectorBytes,
            FieldMode = fieldMode,
            StoppedReason = stopped,
        };
    }

    /// <summary>
    /// Read the SAME <paramref name="maxSectors"/>-sector range <paramref name="passes"/> times and
    /// emit the per-sector consensus (see <see cref="SubchannelConsensus"/>) to
    /// <paramref name="output"/>. This is the read-back cure for transient sub-channel Q jitter: a
    /// one-sector Q mis-read that wanders between reads is out-voted, while a stable on-disc Q —
    /// including a deliberately-corrupt (LibCrypt) one — is preserved verbatim.
    ///
    /// A bounded length is required: every pass must cover the identical sectors, so an open-ended
    /// "read to the end of the program area" cannot be voted. Each pass must read the full range;
    /// a pass that stops short is a hard error (a partial read cannot be safely merged).
    /// </summary>
    public static Result ReadConsensus(SptiDevice dev, int startLba, long maxSectors, Stream output,
                                       int passes, IProgress<double>? progress, FieldSelect field,
                                       out SubchannelConsensus.Report report)
    {
        ArgumentNullException.ThrowIfNull(dev);
        ArgumentNullException.ThrowIfNull(output);
        if (maxSectors <= 0)
            throw new ArgumentException("Consensus needs an explicit, bounded sector count.", nameof(maxSectors));
        int n = Math.Max(2, passes);

        var reads = new byte[n][];
        string fieldMode = "";
        for (int p = 0; p < n; p++)
        {
            using var ms = new MemoryStream(checked((int)(maxSectors * RawSectorBytes)));
            var res = Read(dev, startLba, maxSectors, ms, null, field);
            if (res.SectorsRead != maxSectors)
                throw new IOException(
                    $"Consensus pass {p + 1}/{n} read {res.SectorsRead:N0} of {maxSectors:N0} sectors — " +
                    "every pass must cover the same fully-readable range.");
            reads[p] = ms.ToArray();
            fieldMode = res.FieldMode;
            progress?.Report((double)(p + 1) / n);
        }

        report = SubchannelConsensus.Merge(reads, maxSectors, output);
        return new Result
        {
            SectorsRead = maxSectors,
            BytesWritten = maxSectors * RawSectorBytes,
            FieldMode = fieldMode,
            StoppedReason = null,
        };
    }
}
