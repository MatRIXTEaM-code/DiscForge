// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Core.Reading;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// Reads one sector from each data track and reports what mode it actually is.
///
/// Worth the four seconds it costs: the TOC's control nibble distinguishes audio
/// from data and nothing more, so a planner working from the TOC alone cannot
/// tell Mode 1 from Mode 2, or Form 1 from Form 2. That matters, because a Mode 2
/// Form 2 sector has no 2048-byte user-data field at all — a cooked read of one
/// is not slow or lossy, it is impossible, and the drive rejects it with
/// "illegal mode for this track".
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrackModeProber
{
    /// <summary>Offsets into the track to try, in order. Zero is the track's first
    /// sector, which on many discs is pregap and reads differently from the body
    /// — or not at all — so a failure there proves nothing about the track.</summary>
    private static readonly uint[] ProbeOffsets = [16, 0, 150, 300];

    /// <summary>
    /// Probe every data track. Returns track number → detected mode. Tracks that
    /// could not be probed are absent from the result, and the planner treats
    /// absence as "unknown" and keeps its existing behaviour.
    /// </summary>
    public static IReadOnlyDictionary<int, TrackSectorMode> Probe(char driveLetter, DiscToc toc)
    {
        ArgumentNullException.ThrowIfNull(toc);
        var found = new Dictionary<int, TrackSectorMode>();

        try
        {
            using var dev = new SptiDevice(driveLetter);
            var raw = new byte[2352];

            foreach (var t in toc.Tracks)
            {
                if (t.LengthSectors == 0) continue;

                if (t.IsAudio)
                {
                    found[t.Number] = TrackSectorMode.Audio;
                    continue;
                }

                foreach (uint off in ProbeOffsets)
                {
                    if (off >= t.LengthSectors) continue;

                    var r = dev.SendCommand(
                        MmcCommands.ReadCd(t.StartLba + off, 1,
                            MmcCommands.ExpectedSectorType.Any,
                            MmcCommands.SectorFields.Raw),
                        raw, SptiDataDirection.In, timeoutSeconds: 20);

                    if (!r.Success) continue;

                    var mode = Classify(raw);
                    if (mode != TrackSectorMode.Unknown)
                    {
                        found[t.Number] = mode;
                        break;
                    }
                }
            }
        }
        catch
        {
            // A drive that won't do raw reads can't be probed. Not an error: the
            // planner falls back to its TOC-only behaviour, as it did before.
        }

        return found;
    }

    /// <summary>
    /// Classify a raw 2352-byte sector from its own header. Byte 15 carries the
    /// mode; for Mode 2, bit 5 of the sub-header (byte 18) selects the form.
    /// </summary>
    public static TrackSectorMode Classify(ReadOnlySpan<byte> raw2352)
    {
        if (raw2352.Length < 24) return TrackSectorMode.Unknown;

        // Sync pattern 00 FF×10 00 — without it this isn't a data sector we can
        // reason about, and reading byte 15 as a mode would be meaningless.
        if (raw2352[0] != 0x00 || raw2352[1] != 0xFF || raw2352[11] != 0x00)
            return TrackSectorMode.Unknown;

        return (raw2352[15] & 0x03) switch
        {
            1 => TrackSectorMode.Mode1,
            2 => (raw2352[18] & 0x20) != 0
                    ? TrackSectorMode.Mode2Form2
                    : TrackSectorMode.Mode2Form1,
            _ => TrackSectorMode.Unknown,
        };
    }
}