// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Cue;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;

namespace DiscForge.Core.PlayStation;

/// <summary>Which flavour of LibCrypt a disc carries.</summary>
public enum LibcryptVariant : byte
{
    /// <summary>No LibCrypt subchannel present.</summary>
    None = 0,
    /// <summary>First-generation: the Q address is altered but its CRC is left <i>valid</i>, so the
    /// tampering only shows as an absolute address that disagrees with where the sector physically is.
    /// Subtle — a CRC check alone misses it.</summary>
    AddressType = 1,
    /// <summary>Second-generation: the Q CRC is deliberately <i>broken</i>, so the frame fails its own
    /// checksum. The common, easily-spotted form.</summary>
    CrcType = 2,
    /// <summary>Both kinds of tampering appear on the one disc.</summary>
    Mixed = 3,
}

/// <summary>One LibCrypt-affected sector, with the disc's own (tampered) Q measured against the clean
/// value it should have held. The CRC delta is the LibCrypt key material for that sector.</summary>
public sealed record LibcryptSector(
    int Lba, Msf ExpectedAbs, Msf StoredAbs,
    ushort StoredCrc, ushort CorrectCrc, bool CrcValid, bool AddressAltered, byte[] Q10)
{
    /// <summary>storedCRC XOR the CRC the clean Q would have — the per-sector tamper signature.</summary>
    public ushort CrcDelta => (ushort)(StoredCrc ^ CorrectCrc);
}

/// <summary>The read-only LibCrypt verdict for a disc's subchannel.</summary>
public sealed record LibcryptReport
{
    public required IReadOnlyList<LibcryptSector> Sectors { get; init; }
    public required LibcryptVariant Variant { get; init; }

    /// <summary>LibCrypt corrupts sectors in close pairs; this counts affected sectors sitting within a
    /// few sectors of another. A high share is the LibCrypt shape rather than scattered read damage.</summary>
    public required int PairedSectors { get; init; }

    public bool Present => Sectors.Count > 0;
    public int Count => Sectors.Count;

    /// <summary>A stable 16-bit fingerprint of the tamper set — a CRC-16 over the sorted
    /// (relative-LBA, CRC-delta) records. Deterministic and reproducible, so two rips of the same disc
    /// fingerprint identically; it identifies the protection and lets a database match or de-duplicate it.
    /// This is DiscForge's fingerprint of the LibCrypt data, not a claim about the value the game's
    /// executable internally checks (that key can be derived from <see cref="LibcryptSector.CrcDelta"/>).</summary>
    public required ushort Fingerprint { get; init; }

    /// <summary>All per-sector CRC deltas XOR-folded — the combined LibCrypt key material.</summary>
    public required ushort CrcDeltaXor { get; init; }

    public string Summary()
    {
        if (!Present)
            return "No LibCrypt subchannel — all Q frames are clean; nothing to preserve.";
        string kind = Variant switch
        {
            LibcryptVariant.AddressType => "address-tampered (valid-CRC) LibCrypt",
            LibcryptVariant.CrcType => "broken-CRC LibCrypt",
            LibcryptVariant.Mixed => "mixed-form LibCrypt",
            _ => "LibCrypt",
        };
        return $"{kind}: {Count} affected sector(s) ({PairedSectors} paired), " +
               $"fingerprint {Fingerprint:X4}, key material {CrcDeltaXor:X4}. Preserve verbatim.";
    }
}

/// <summary>
/// libcrypt — the deep read of a PlayStation disc's LibCrypt protection. Where the raw-subchannel
/// analyser only asks "does the invalid-Q shape look like LibCrypt?" and the SBI writer only emits the
/// sidecar, this <i>characterises</i> the protection: it separates the two generations (first-gen alters
/// the Q address but keeps the CRC valid, so it only shows as an address that disagrees with the sector's
/// true position; second-gen breaks the CRC outright), measures each affected sector's Q against the
/// clean value it should have carried, and reconstructs the key material from the per-sector CRC deltas.
/// It also emits a stable 16-bit fingerprint so two rips of the same disc match, and de-duplication and
/// database lookup become possible.
///
/// This is preservation, the opposite of circumvention: it reads and describes the disc's own protection
/// data so a faithful reproduction carries exactly what the original did. It removes nothing, patches
/// nothing, and defeats nothing — a disc without LibCrypt yields an empty report. It bridges to the
/// existing <see cref="Sbi"/> writer for the emulator sidecar.
/// </summary>
public static class LibcryptAnalyzer
{
    /// <summary>Two-second lead-in: LBA 0 sits at absolute time 00:02:00.</summary>
    public const int LeadIn = 150;

    /// <summary>Sectors this close to another affected sector count as "paired".</summary>
    public const int PairWindow = 10;

    /// <summary>Analyse a raw 96-byte/sector subchannel sidecar (LBA-ordered from
    /// <paramref name="startLba"/>). <paramref name="maxAnomalies"/> caps the affected count — beyond it
    /// the pattern is read damage, not protection, and the scan throws rather than mislabel a bad rip.</summary>
    public static LibcryptReport Scan(ReadOnlySpan<byte> subcode, uint startLba = 0, int maxAnomalies = 100)
    {
        long frames = subcode.Length / RawSubchannel.FrameSize;
        Span<byte> q = stackalloc byte[12];
        var sectors = new List<LibcryptSector>();

        for (long i = 0; i < frames; i++)
        {
            var frame = subcode.Slice((int)(i * RawSubchannel.FrameSize), RawSubchannel.FrameSize);
            RawSubchannel.ExtractQ(frame, q);
            int lba = (int)(startLba + i);

            ushort correct = Crc16.ComputeInverted(q[..10]);
            ushort stored = (ushort)((q[10] << 8) | q[11]);
            bool crcValid = correct == stored;

            long trueAbs = lba + LeadIn;
            var expected = Msf.FromSectors(trueAbs);

            bool addressAltered = false;
            Msf storedMsf = expected;
            int adr = q[0] & 0x0F;
            if (adr == 1)
            {
                int m = TryBcd(q[7]), s = TryBcd(q[8]), f = TryBcd(q[9]);
                if (m < 0 || s < 0 || f < 0)
                {
                    addressAltered = true;                 // non-decimal BCD is itself tampering
                }
                else
                {
                    storedMsf = new Msf(m, s, f);
                    if (storedMsf.ToSectors() != trueAbs) addressAltered = true;
                }
            }

            if (crcValid && !addressAltered) continue;     // a clean frame

            var q10 = q[..10].ToArray();
            sectors.Add(new LibcryptSector(lba, expected, storedMsf, stored, correct, crcValid,
                                           addressAltered, q10));

            if (sectors.Count > maxAnomalies)
                throw new InvalidDataException(
                    $"Over {maxAnomalies} anomalous Q frames — this reads as disc damage, not LibCrypt. " +
                    "Refusing to characterise it as protection.");
        }

        return Build(sectors);
    }

    /// <summary>Build a report from an already-collected set of affected sectors (also the unit-test seam).</summary>
    public static LibcryptReport Build(IReadOnlyList<LibcryptSector> sectors)
    {
        ArgumentNullException.ThrowIfNull(sectors);
        if (sectors.Count == 0)
            return new LibcryptReport
            {
                Sectors = sectors, Variant = LibcryptVariant.None,
                PairedSectors = 0, Fingerprint = 0, CrcDeltaXor = 0,
            };

        bool anyCrc = sectors.Any(s => !s.CrcValid);
        bool anyAddr = sectors.Any(s => s.CrcValid && s.AddressAltered);
        var variant = (anyCrc, anyAddr) switch
        {
            (true, true) => LibcryptVariant.Mixed,
            (true, false) => LibcryptVariant.CrcType,
            (false, true) => LibcryptVariant.AddressType,
            _ => LibcryptVariant.CrcType,   // address-altered with broken CRC still lands here
        };

        // Pairing: affected sectors within PairWindow of a neighbour.
        var lbas = sectors.Select(s => s.Lba).OrderBy(x => x).ToList();
        int paired = 0;
        for (int i = 0; i < lbas.Count; i++)
        {
            bool near = (i > 0 && lbas[i] - lbas[i - 1] <= PairWindow) ||
                        (i + 1 < lbas.Count && lbas[i + 1] - lbas[i] <= PairWindow);
            if (near) paired++;
        }

        // Fingerprint: CRC-16 over the sorted (relativeLba, crcDelta) records, so it is
        // translation-invariant (independent of where the sidecar starts) and reproducible.
        int baseLba = lbas[0];
        var ordered = sectors.OrderBy(s => s.Lba).ToList();
        var buf = new byte[ordered.Count * 4];
        ushort xor = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            int rel = ordered[i].Lba - baseLba;
            buf[i * 4 + 0] = (byte)(rel >> 8);
            buf[i * 4 + 1] = (byte)rel;
            buf[i * 4 + 2] = (byte)(ordered[i].CrcDelta >> 8);
            buf[i * 4 + 3] = (byte)ordered[i].CrcDelta;
            xor ^= ordered[i].CrcDelta;
        }
        ushort fingerprint = Crc16.ComputeInverted(buf);

        return new LibcryptReport
        {
            Sectors = ordered, Variant = variant, PairedSectors = paired,
            Fingerprint = fingerprint, CrcDeltaXor = xor,
        };
    }

    /// <summary>Bridge to the emulator sidecar: build an <see cref="Sbi.Document"/> from this report's
    /// affected sectors (their true position keys the disc's own tampered Q, preserved verbatim).</summary>
    public static Sbi.Document ToSbi(LibcryptReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var entries = new List<Sbi.Entry>(report.Sectors.Count);
        foreach (var s in report.Sectors)
        {
            long abs = s.Lba + LeadIn;
            int f = (int)(abs % 75);
            long s2 = abs / 75;
            entries.Add(new Sbi.Entry((int)(s2 / 60), (int)(s2 % 60), f, Sbi.TypeQ10, (byte[])s.Q10.Clone()));
        }
        return new Sbi.Document(entries);
    }

    public static string Render(LibcryptReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var sb = new StringBuilder();
        sb.AppendLine(r.Summary());
        foreach (var s in r.Sectors.Take(40))
        {
            string why = !s.CrcValid ? $"CRC {s.StoredCrc:X4}≠{s.CorrectCrc:X4} (Δ{s.CrcDelta:X4})"
                                     : $"addr {s.StoredAbs}≠{s.ExpectedAbs}";
            sb.AppendLine($"  LBA {s.Lba} @ {s.ExpectedAbs}: {why}");
        }
        if (r.Sectors.Count > 40) sb.AppendLine($"  … and {r.Sectors.Count - 40} more");
        return sb.ToString().TrimEnd();
    }

    // ---- helpers ------------------------------------------------------------

    private static int TryBcd(byte b)
    {
        int hi = b >> 4, lo = b & 0x0F;
        if (hi > 9 || lo > 9) return -1;
        return hi * 10 + lo;
    }
}
