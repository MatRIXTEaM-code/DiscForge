// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Audio;

namespace DiscForge.Core.PlayStation;

/// <summary>
/// Pulls CD-ROM XA ADPCM audio out of a raw disc image and decodes it to PCM —
/// the "Playstation XA Copier" job. It walks the sectors, keeps the ones whose
/// XA subheader marks them as audio (optionally for one channel), takes each
/// sector's 2304-byte data area, and hands the sequence to
/// <see cref="XaAdpcm"/>. It reads a person's own game's audio — no protection is
/// touched.
/// </summary>
public static class XaExtract
{
    /// <summary>Where the subheader and the 2304-byte data area sit within a
    /// sector, for the two raw layouts that carry XA.</summary>
    public sealed record SectorLayout(int SectorSize, int SubheaderOffset, int DataOffset)
    {
        /// <summary>Full raw sector: 12 sync + 4 header + 8 subheader + data.</summary>
        public static readonly SectorLayout Raw2352 = new(2352, 16, 24);
        /// <summary>Mode 2 form (no sync/header): 8 subheader + data.</summary>
        public static readonly SectorLayout Mode2_2336 = new(2336, 0, 8);
    }

    public sealed record Result(short[] Pcm, int SampleRate, int Channels, int SectorsUsed);

    private const byte SubmodeAudio = 0x04;   // XA subheader submode bit 2

    /// <summary>Extract and decode. If <paramref name="channel"/> is given, only
    /// that XA channel's audio sectors are used.</summary>
    public static Result Extract(Stream image, SectorLayout layout, int? channel = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.DataOffset + XaAdpcm.DataAreaSize > layout.SectorSize)
            throw new ArgumentException("Data area does not fit the sector layout.", nameof(layout));

        var areas = new List<byte[]>();
        bool stereo = false;
        int rate = 37800;
        bool any = false;

        var sector = new byte[layout.SectorSize];
        while (ReadFull(image, sector))
        {
            byte channelByte = sector[layout.SubheaderOffset + 1];
            byte submode = sector[layout.SubheaderOffset + 2];
            byte coding = sector[layout.SubheaderOffset + 3];

            if ((submode & SubmodeAudio) == 0) continue;
            if (channel is int c && channelByte != c) continue;

            var info = XaAdpcm.CodingInfo.Parse(coding);
            if (info.EightBit) continue;   // 8-bit XA not decoded
            if (!any) { stereo = info.Stereo; rate = info.SampleRate; any = true; }

            var area = new byte[XaAdpcm.DataAreaSize];
            Array.Copy(sector, layout.DataOffset, area, 0, XaAdpcm.DataAreaSize);
            areas.Add(area);
        }

        if (!any)
            throw new InvalidOperationException(
                "No XA audio sectors found — the image has no XA ADPCM audio (or a different sector layout).");

        var pcm = XaAdpcm.DecodeSectors(areas, stereo);
        return new Result(pcm, rate, stereo ? 2 : 1, areas.Count);
    }

    private static bool ReadFull(Stream s, byte[] buffer)
    {
        int off = 0;
        while (off < buffer.Length)
        {
            int n = s.Read(buffer, off, buffer.Length - off);
            if (n <= 0) return false;   // clean EOF (partial trailing sector ignored)
            off += n;
        }
        return true;
    }
}
