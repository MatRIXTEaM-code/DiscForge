// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cdi;

namespace DiscForge.Core.Nrg;

/// <summary>
/// Converts between Nero NRG and DiscJuggler CDI. Both keep raw track data at the
/// front of the file and a descriptor of the layout; the conversion re-containers
/// without altering a sector. Track mode, sector size and start LBA carry across;
/// multiple CDI sessions flatten to one track list in NRG (NRG has no session
/// concept), and their absolute LBAs are preserved so nothing about the layout is
/// silently lost.
/// </summary>
public static class NrgConverter
{
    private static readonly Dictionary<NrgTrackMode, CdiTrackMode> ToCdiMode = new()
    {
        [NrgTrackMode.Audio] = CdiTrackMode.Audio,
        [NrgTrackMode.Mode1] = CdiTrackMode.Mode1,
        [NrgTrackMode.Mode2] = CdiTrackMode.Mode2,
    };

    private static readonly Dictionary<CdiTrackMode, NrgTrackMode> ToNrgMode = new()
    {
        [CdiTrackMode.Audio] = NrgTrackMode.Audio,
        [CdiTrackMode.Mode1] = NrgTrackMode.Mode1,
        [CdiTrackMode.Mode2] = NrgTrackMode.Mode2,
    };

    // ---- CDI -> NRG ---------------------------------------------------------

    public static void CdiToNrg(Stream cdi, CdiImage image, Stream output)
    {
        ArgumentNullException.ThrowIfNull(cdi);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);

        var inputs = new List<NrgWriter.TrackInput>();
        foreach (var t in image.AllTracks)
        {
            long contentOffset = t.FileOffset + (long)t.PregapSectors * (int)t.SectorSize;
            uint length = t.LengthSectors;
            int sectorSize = (int)t.SectorSize;
            var contentTrack = t with
            {
                PregapSectors = 0,
                TotalSectors = length,
                FileOffset = contentOffset,
            };

            inputs.Add(new NrgWriter.TrackInput
            {
                Mode = ToNrgMode[t.Mode],
                SectorSize = sectorSize,
                StartLba = t.StartLba,
                LengthSectors = length,
                Filename = $"track{t.Number:D2}",
                DataWriter = os => CdiExtractor.ExtractRaw(cdi, contentTrack, os),
            });
        }

        NrgWriter.Write(output, inputs);
    }

    // ---- NRG -> CDI ---------------------------------------------------------

    public static void NrgToCdi(Stream nrg, NrgImage image, CdiVersion version, Stream output)
    {
        ArgumentNullException.ThrowIfNull(nrg);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);

        var inputs = new List<CdiWriter.TrackInput>();
        foreach (var t in image.Tracks)
        {
            long offset = t.DataOffset;
            long bytes = t.StoredBytes;
            inputs.Add(new CdiWriter.TrackInput
            {
                Mode = ToCdiMode[t.Mode],
                SectorSize = (CdiSectorSize)t.SectorSize,
                PregapSectors = 0,
                LengthSectors = t.LengthSectors,
                StartLba = (uint)t.StartLba,
                Filename = $"track{t.Number:D2}",
                DataWriter = os =>
                {
                    nrg.Seek(offset, SeekOrigin.Begin);
                    CopyExactly(nrg, os, bytes);
                },
            });
        }

        // NRG has no sessions; write a single-session CDI (absolute LBAs kept).
        CdiWriter.Write(output, version, new[] { (IReadOnlyList<CdiWriter.TrackInput>)inputs });
    }

    private static void CopyExactly(Stream src, Stream dst, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            int want = (int)Math.Min(buffer.Length, count);
            int n = src.Read(buffer, 0, want);
            if (n <= 0) throw new EndOfStreamException("The NRG track data ended earlier than expected.");
            dst.Write(buffer, 0, n);
            count -= n;
        }
    }
}
