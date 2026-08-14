// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Raw;

namespace DiscForge.Core.Forensics;

/// <summary>One sector whose header address is anomalous.</summary>
public sealed record SectorAddressAnomaly(int Position, int DeclaredLba, string Kind);

/// <summary>What a scan of the sector headers found.</summary>
public sealed record TwinSectorReport
{
    public required int SectorsScanned { get; init; }
    /// <summary>Sectors that share their header address with another sector (SafeDisc "twins").</summary>
    public required int TwinSectors { get; init; }
    /// <summary>Sectors whose header address breaks the linear progression (deliberately re-addressed).</summary>
    public required int MisaddressedSectors { get; init; }
    public required IReadOnlyList<SectorAddressAnomaly> Samples { get; init; }
    public required bool LooksProtected { get; init; }

    public string Summary()
    {
        if (TwinSectors == 0 && MisaddressedSectors == 0)
            return $"No header-address anomalies in {SectorsScanned:N0} sector(s) — addresses are contiguous.";
        return $"{TwinSectors} twin sector(s) and {MisaddressedSectors} re-addressed sector(s) of " +
               $"{SectorsScanned:N0} scanned — {(LooksProtected ? "consistent with an address-based protection (preserve as-is)" : "isolated, likely read noise")}.";
    }
}

/// <summary>
/// Twin-sector / header-address forensics — detect copy protection written into the sector <i>headers</i>
/// themselves, straight from a raw image, without needing the filesystem. Where the filesystem catalog
/// spots a scheme by the files it drops and the weak-sector scan spots it by intentionally-invalid EDC,
/// this spots the address tricks: SafeDisc-style <b>twin sectors</b> (two physical sectors that claim the
/// very same logical address) and <b>re-addressed</b> sectors (a header address that jumps off the
/// contiguous progression). Ordinary rot corrupts a sector's data and EDC, not its short header, so a
/// cluster of deliberately-wrong addresses is a protection signature, not damage. It establishes the
/// image's own base offset first, so a legitimately shifted dump is never mistaken for tampering.
/// Detection only — it flags what to preserve verbatim and defeats nothing.
/// </summary>
public static class TwinSectorScan
{
    private const int RawSectorSize = 2352;
    private const int MaxSamples = 32;

    public static TwinSectorReport Analyze(byte[] rawImage)
    {
        ArgumentNullException.ThrowIfNull(rawImage);
        int count = rawImage.Length / RawSectorSize;

        var declaredAt = new Dictionary<int, List<int>>();   // declared LBA -> physical positions
        var positions = new List<(int Pos, int Declared)>();
        int scanned = 0;
        int? offset0 = null;

        for (int i = 0; i < count; i++)
        {
            var sec = rawImage.AsSpan(i * RawSectorSize, RawSectorSize);
            if (!HasSync(sec)) continue;
            byte mode = sec[15];
            if (mode != 1 && mode != 2) continue;          // data sectors carry a header address

            int declared = MsfToLba(Bcd.To(sec[12]), Bcd.To(sec[13]), Bcd.To(sec[14]));
            scanned++;
            offset0 ??= declared - i;                        // the image's own base offset
            positions.Add((i, declared));
            (declaredAt.TryGetValue(declared, out var l) ? l : declaredAt[declared] = new()).Add(i);
        }

        var samples = new List<SectorAddressAnomaly>();
        int twins = 0, misaddressed = 0;
        int baseOffset = offset0 ?? 0;

        foreach (var (pos, declared) in positions)
        {
            bool isTwin = declaredAt[declared].Count > 1;
            bool isMisaddressed = declared - pos != baseOffset;
            if (isTwin)
            {
                twins++;
                if (samples.Count < MaxSamples) samples.Add(new SectorAddressAnomaly(pos, declared, "twin"));
            }
            else if (isMisaddressed)
            {
                misaddressed++;
                if (samples.Count < MaxSamples) samples.Add(new SectorAddressAnomaly(pos, declared, "re-addressed"));
            }
        }

        // Deliberate header re-addressing is not a rot artefact: a couple is already suspicious.
        bool looksProtected = twins > 0 || misaddressed >= 2;

        return new TwinSectorReport
        {
            SectorsScanned = scanned,
            TwinSectors = twins,
            MisaddressedSectors = misaddressed,
            Samples = samples,
            LooksProtected = looksProtected,
        };
    }

    public static string Render(TwinSectorReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var a in r.Samples)
            sb.AppendLine($"  sector {a.Position}: header claims LBA {a.DeclaredLba} ({a.Kind})");
        return sb.ToString().TrimEnd();
    }

    // ---- internals ----------------------------------------------------------

    private static int MsfToLba(int m, int s, int f) => (m * 60 + s) * 75 + f - 150;

    private static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s[0] != 0x00 || s[11] != 0x00) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }
}
