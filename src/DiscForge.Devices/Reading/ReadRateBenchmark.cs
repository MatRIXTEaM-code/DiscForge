// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Diagnostics;
using System.Runtime.Versioning;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// Reads a disc sequentially in timed chunks to measure transfer RATE across the surface — the
/// read-speed half of a "Discovery"-style scan. Each chunk is a raw READ CD (main channel only, no
/// sub-channel) wrapped in a stopwatch; the samples feed <see cref="ReadRateProfile"/>. Read-only,
/// and it stops at the first region the drive refuses (that boundary is itself a useful signal).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ReadRateBenchmark
{
    private const int RawSectorBytes = 2352;   // main channel only (SubChannel.None)

    public static IReadOnlyList<ReadRateProfile.Sample> Run(SptiDevice dev, uint startLba, uint totalSectors,
                                                            int chunkSectors, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(dev);
        chunkSectors = Math.Max(1, chunkSectors);

        var samples = new List<ReadRateProfile.Sample>();
        var buffer = new byte[chunkSectors * RawSectorBytes];
        var sw = new Stopwatch();

        // Data sectors read raw; a pure-audio disc rejects Raw, so we fall back to UserData. Field
        // mode, once it works, is sticky.
        var fields = MmcCommands.SectorFields.Raw;

        // Try to read one chunk, timing ONLY the successful attempt. Returns false if neither field
        // mode reads it — the caller then shrinks the chunk (many drives cap a raw read well below a
        // big request) and retries, so a drive's transfer cap slows the benchmark rather than aborting it.
        bool TryRead(uint lba, uint chunk, out double ms)
        {
            var span = buffer.AsSpan(0, (int)chunk * RawSectorBytes);
            sw.Restart();
            var r = dev.SendCommand(
                MmcCommands.ReadCd(lba, chunk, MmcCommands.ExpectedSectorType.Any, fields, MmcCommands.SubChannel.None),
                span, SptiDataDirection.In, 30);
            sw.Stop();
            if (r.Success) { ms = sw.Elapsed.TotalMilliseconds; return true; }

            var alt = fields == MmcCommands.SectorFields.Raw
                ? MmcCommands.SectorFields.UserData : MmcCommands.SectorFields.Raw;
            sw.Restart();
            var r2 = dev.SendCommand(
                MmcCommands.ReadCd(lba, chunk, MmcCommands.ExpectedSectorType.Any, alt, MmcCommands.SubChannel.None),
                span, SptiDataDirection.In, 30);
            sw.Stop();
            if (r2.Success) { fields = alt; ms = sw.Elapsed.TotalMilliseconds; return true; }

            ms = 0;
            return false;
        }

        uint end = startLba + totalSectors;
        uint pos = startLba;
        int cur = chunkSectors;

        while (pos < end)
        {
            uint chunk = Math.Min((uint)cur, end - pos);
            if (TryRead(pos, chunk, out double ms))
            {
                samples.Add(new ReadRateProfile.Sample(pos, (int)chunk, ms));
                pos += chunk;
                progress?.Report(totalSectors == 0 ? 1.0 : (double)(pos - startLba) / totalSectors);
            }
            else if (chunk > 1)
            {
                cur = Math.Max(1, cur / 2);        // drive likely caps the raw read — shrink, retry same LBA
            }
            else
            {
                break;                              // a single sector won't read — genuine end / unreadable region
            }
        }

        return samples;
    }
}
