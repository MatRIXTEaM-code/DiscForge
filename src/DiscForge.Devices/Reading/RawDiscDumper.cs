// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Runtime.Versioning;
using System.Text;
using DiscForge.Core.Devices;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// Windows/SPTI drive-and-media DIAGNOSTIC for the Hitachi-LG GDR-816x DVD-ROM
/// family (see <see cref="RawDumpDrive"/>). It identifies the drive, reports what a
/// standard read returns off the loaded disc, and can confirm a standard
/// READ(12)-with-streaming read where a plain read is refused.
///
/// It is read-only and reports bytes AS-IS. For a console disc the returned data is
/// DVD-scrambled; DiscForge does not descramble console disc formats and does not
/// produce or decode console game images. Nothing here circumvents a protection
/// measure — it is a low-level read/diagnostic surface only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RawDiscDumper
{
    /// <summary>What a single standard READ(10) at one LBA returned — the signal
    /// that tells us whether a disc reads as plaintext, as scrambled DVD data, or
    /// not at all.</summary>
    public sealed record ReadProbe
    {
        public required uint Lba { get; init; }
        public required bool Ok { get; init; }
        public required string Detail { get; init; }
        /// <summary>Shannon entropy of the sector, bits/byte (0..8). ~8.0 means
        /// high-entropy data — consistent with the DVD data-scramble; a low value
        /// means structured/plaintext bytes.</summary>
        public double Entropy { get; init; }
        /// <summary>The GameCube boot magic 0xC2339F3D at offset 0x1C of LBA 0 —
        /// present only if the drive handed back UNSCRAMBLED disc data.</summary>
        public bool GameCubeMagic { get; init; }
        public string? HeadHex { get; init; }
    }

    /// <summary>Everything Phase 1 can learn about a drive and its disc.</summary>
    public sealed record ProbeReport
    {
        public required DriveCapabilities Drive { get; init; }
        public required bool Supported { get; init; }
        public DataDiscImager.Capacity? Capacity { get; init; }
        public IReadOnlyList<ReadProbe> Reads { get; init; } = [];
        public IReadOnlyList<string> Notes { get; init; } = [];

        public string Render()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Drive : {Drive.DevicePath}  {Drive.Vendor} {Drive.Model} (fw {Drive.FirmwareRevision})");
            sb.AppendLine($"Family: {RawDumpDrive.Describe(Drive.Vendor, Drive.Model)}");
            sb.AppendLine($"Verdict: {(Supported ? "recognised GDR-816x DVD-ROM" : "not a recognised GDR-816x DVD-ROM")}");
            sb.AppendLine($"Read  : CD {YesNo(Drive.CdRead)}, DVD {YesNo(Drive.DvdRead)}, BD {YesNo(Drive.BdRead)}");
            sb.AppendLine($"Write : CD {YesNo(Drive.CdWrite)}, DVD {YesNo(Drive.DvdWrite)}, BD {YesNo(Drive.BdWrite)}   (a DVD-ROM is read-only — burning needs a separate writer)");
            sb.AppendLine($"Media : {Drive.MediaProfile}");
            if (Capacity is { } c)
                sb.AppendLine($"Capacity (READ CAPACITY): {c.Sectors:N0} sectors × {c.BlockLengthBytes} bytes = {c.TotalBytes / (1024.0 * 1024.0):N1} MiB");

            if (Reads.Count > 0)
            {
                sb.AppendLine("Standard READ(10) probe:");
                foreach (var p in Reads)
                {
                    if (!p.Ok) { sb.AppendLine($"  LBA {p.Lba,10:N0}: REFUSED — {p.Detail}"); continue; }
                    string tag = p.GameCubeMagic ? " [GameCube boot magic — UNSCRAMBLED]"
                               : p.Entropy >= 7.5 ? " [high entropy — looks scrambled]"
                               : " [low entropy — looks structured/plaintext]";
                    sb.AppendLine($"  LBA {p.Lba,10:N0}: ok  entropy={p.Entropy:F2} bpb  head={p.HeadHex}{tag}");
                }
            }

            foreach (var n in Notes) sb.AppendLine($"Note  : {n}");
            return sb.ToString().TrimEnd();
        }

        private static string YesNo(bool b) => b ? "yes" : "no";
    }

    /// <summary>Identify the drive, read its capacity, and probe a few sectors with
    /// standard READ(10) to characterise what it returns (Phase 1).</summary>
    public static ProbeReport Probe(char driveLetter)
    {
        var caps = DriveDetector.Detect(driveLetter);
        bool supported = RawDumpDrive.IsSupported(caps.Vendor, caps.Model);
        var notes = new List<string>();

        DataDiscImager.Capacity? cap = null;
        try
        {
            cap = DataDiscImager.ReadCapacity(driveLetter);
        }
        catch (Exception ex)
        {
            notes.Add($"READ CAPACITY did not answer ({ex.Message}). Normal for some non-standard-layout discs.");
        }

        var reads = ProbeReads(driveLetter, cap?.Sectors ?? 0, notes);

        if (!supported)
            notes.Add("This is not a GDR-816x DVD-ROM, but it can still image ordinary data discs with `read-disc`.");

        return new ProbeReport { Drive = caps, Supported = supported, Capacity = cap, Reads = reads, Notes = notes };
    }

    /// <summary>The outcome of one streaming READ(12) — the raw-read primitive.</summary>
    public sealed record StreamReadResult
    {
        public required bool Ok { get; init; }
        public required string Detail { get; init; }
        public int Bytes { get; init; }
        public double Entropy { get; init; }
        public string? HeadHex { get; init; }
        public string? Sha256 { get; init; }
    }

    /// <summary>
    /// Read <paramref name="blocks"/> sectors from <paramref name="lba"/> with the
    /// standard READ(12) <em>Streaming</em> bit set, to confirm the drive returns raw
    /// sector data where a plain read is refused. The bytes are returned AS-IS (for a
    /// console disc, still DVD-scrambled — DiscForge does not descramble them); this
    /// only reports what it got (length, entropy, head, hash) as a diagnostic.
    /// </summary>
    public static StreamReadResult StreamRead(char driveLetter, uint lba, uint blocks)
    {
        using var dev = new SptiDevice(driveLetter);
        var buf = new byte[blocks * 2048];
        var r = dev.SendCommand(MmcCommands.Read12(lba, blocks, streaming: true), buf,
                                SptiDataDirection.In, timeoutSeconds: 60);
        if (!r.Success)
            return new StreamReadResult { Ok = false, Detail = r.Describe() };

        string sha = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(buf)).ToLowerInvariant();
        return new StreamReadResult
        {
            Ok = true,
            Detail = "streaming read ok",
            Bytes = buf.Length,
            Entropy = ShannonEntropy(buf),
            HeadHex = System.Convert.ToHexString(buf.AsSpan(0, 16)).ToLowerInvariant(),
            Sha256 = sha,
        };
    }

    /// <summary>
    /// Probe a handful of sectors with the plain, universally-supported READ(10).
    /// Purely diagnostic: does the drive hand back this disc's bytes, and if so are
    /// they high-entropy (scrambled/non-standard) or structured? Reports only.
    /// </summary>
    private static IReadOnlyList<ReadProbe> ProbeReads(char driveLetter, uint totalSectors, List<string> notes)
    {
        var lbas = new List<uint> { 0, 16, 256, 100_000 };
        if (totalSectors > 8) lbas.Add(totalSectors - 8);

        var results = new List<ReadProbe>();
        try
        {
            using var dev = new SptiDevice(driveLetter);
            var buf = new byte[2048];
            foreach (var lba in lbas)
            {
                if (totalSectors != 0 && lba >= totalSectors) continue;
                var r = dev.SendCommand(MmcCommands.Read10(lba, 1), buf, SptiDataDirection.In, timeoutSeconds: 30);
                if (!r.Success)
                {
                    results.Add(new ReadProbe { Lba = lba, Ok = false, Detail = r.Describe() });
                    continue;
                }
                bool magic = lba == 0 &&
                             buf[0x1C] == 0xC2 && buf[0x1D] == 0x33 && buf[0x1E] == 0x9F && buf[0x1F] == 0x3D;
                results.Add(new ReadProbe
                {
                    Lba = lba,
                    Ok = true,
                    Detail = "read ok",
                    Entropy = ShannonEntropy(buf),
                    GameCubeMagic = magic,
                    HeadHex = System.Convert.ToHexString(buf.AsSpan(0, 16)),
                });
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Read probe could not run: {ex.Message}");
        }
        return results;
    }

    private static double ShannonEntropy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        Span<int> counts = stackalloc int[256];
        foreach (var b in data) counts[b]++;
        double e = 0;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            double p = (double)c / data.Length;
            e -= p * Math.Log2(p);
        }
        return e;
    }
}
