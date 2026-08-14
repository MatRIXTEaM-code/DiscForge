// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Buffers.Binary;
using System.Runtime.Versioning;
using DiscForge.Core.Mmc;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Reading;

/// <summary>
/// Images a <em>data</em> disc — DVD, Blu-ray, or a data CD/DVD authored as one
/// ISO 9660 / UDF volume — to a flat <c>.iso</c> by copying every 2048-byte
/// sector via READ(10). This is the sibling of <see cref="DiscReader"/>: that one
/// walks a CD's track TOC and emits a track-based CDI (needed for audio and mixed
/// discs); this one treats the disc as a single run of cooked sectors, which is
/// exactly what a DVD/BD data volume is (there is no track TOC to walk).
///
/// The result is a byte-faithful copy suitable for re-burning with
/// <c>dforge burn</c> — so <c>read-disc</c> + <c>burn</c> clones a personal,
/// unencrypted disc.
///
/// Clean-room boundary, enforced here: before reading a single sector it asks the
/// drive whether the disc declares a copy-protection system (CSS / CPRM / AACS)
/// and refuses if it does. DiscForge images unencrypted discs only — it neither
/// authenticates nor decrypts protected content. A drive returning a
/// copy-protected sense mid-read also stops the copy immediately.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDiscImager
{
    /// <summary>Sectors per READ(10). 64 × 2048 = 128 KB — comfortably inside the
    /// SPTI single-transfer window on every drive, and big enough to keep a fast
    /// BD reader fed.</summary>
    private const uint SectorsPerRead = 64;

    /// <summary>The only block length this path handles. A pure audio or mixed CD
    /// reports 2352 (or has no cooked volume at all) and must go through the
    /// track-aware ripper instead.</summary>
    private const int CookedSectorBytes = 2048;

    private const byte CopyProtectedAsc = 0x6F;

    /// <summary>The disc's recorded size, as the drive reports it.</summary>
    public sealed record Capacity(uint Sectors, int BlockLengthBytes)
    {
        public long TotalBytes => (long)Sectors * BlockLengthBytes;
    }

    /// <summary>READ CAPACITY(10): last addressable LBA + block length. The sector
    /// count is last-LBA + 1.</summary>
    public static Capacity ReadCapacity(char driveLetter)
    {
        using var dev = new SptiDevice(driveLetter);
        return ReadCapacity(dev);
    }

    private static Capacity ReadCapacity(SptiDevice dev)
    {
        var buf = new byte[8];
        var r = dev.SendCommand(MmcCommands.ReadCapacity10(), buf, SptiDataDirection.In, timeoutSeconds: 30);
        if (!r.Success)
            throw new IOException($"READ CAPACITY failed: {r.Describe()}. Is a disc loaded and spun up?");

        uint lastLba = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0));
        uint blockLen = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4));
        if (blockLen == 0) blockLen = CookedSectorBytes;   // some drives answer 0; assume cooked
        // last-LBA + 1 sectors, guarding the (impossible in practice) all-ones case.
        uint sectors = lastLba == uint.MaxValue ? lastLba : lastLba + 1;
        return new Capacity(sectors, (int)blockLen);
    }

    /// <summary>
    /// Ask the drive whether the disc declares a copy-protection system. Returns a
    /// human sentence if protection is present (so the caller can refuse), or null
    /// if the disc is unprotected — or if the drive won't answer, which is not by
    /// itself proof of protection (a plain data disc and many drives simply have
    /// no copyright structure to report).
    /// </summary>
    public static string? ProtectionReason(char driveLetter)
    {
        using var dev = new SptiDevice(driveLetter);
        return ProtectionReason(dev);
    }

    private static string? ProtectionReason(SptiDevice dev)
    {
        // Try the DVD copyright structure (mediaType 0) and the BD one (mediaType 1).
        // Response: a 4-byte header (2-byte data length, 2 reserved) then, for the
        // copyright-info format, byte0 = Copyright Protection System Type (CPST),
        // byte1 = Region Management. CPST != 0 means CSS/CPRM/AACS is asserted.
        foreach (byte mediaType in new byte[] { 0x00, 0x01 })
        {
            var buf = new byte[8];
            var r = dev.SendCommand(
                MmcCommands.ReadDiscStructure(MmcCommands.DiscStructureFormat.CopyrightInfo,
                                              allocationLength: 8, mediaType: mediaType),
                buf, SptiDataDirection.In, timeoutSeconds: 15);
            if (!r.Success) continue;

            byte cpst = buf[4];
            if (cpst != 0)
                return $"The disc declares a copy-protection system (CPST 0x{cpst:X2} — " +
                       "CSS / CPRM / AACS). DiscForge images unencrypted discs only: it does " +
                       "not authenticate to, or decrypt, protected content. Nothing was read.";
        }
        return null;
    }

    /// <summary>
    /// Copy the whole data volume to <paramref name="output"/> as a flat ISO.
    /// Refuses a disc that declares copy protection, and a disc whose block length
    /// is not 2048 (an audio/mixed CD — use the track-aware ripper for those).
    /// Check <see cref="ReadReport.Complete"/> before trusting the image.
    /// </summary>
    public static ReadReport ReadToIso(char driveLetter, Stream output,
                                       IProgress<ReadProgress>? progress = null,
                                       ReadOptions? options = null,
                                       CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        options ??= new ReadOptions();

        using var dev = new SptiDevice(driveLetter);

        // Clean-room gate first: never read a sector off a protected disc.
        var protection = ProtectionReason(dev);
        if (protection is not null) throw new DiscReadException(protection);

        var cap = ReadCapacity(dev);
        if (cap.BlockLengthBytes != CookedSectorBytes)
            throw new DiscReadException(
                $"This disc reports {cap.BlockLengthBytes}-byte sectors, not {CookedSectorBytes}. " +
                "read-disc images cooked data volumes (DVD/BD/data-CD). For an audio or mixed-mode " +
                "CD, rip it track-by-track in the GUI (Read Disc) so the audio tracks are handled correctly.");
        if (cap.Sectors == 0)
            throw new DiscReadException("The drive reports a zero-length disc. Is it blank, or still spinning up?");

        var badSectors = new List<uint>();
        var notes = new List<string>();
        var buffer = new byte[SectorsPerRead * CookedSectorBytes];

        uint done = 0;
        while (done < cap.Sectors)
        {
            cancel.ThrowIfCancellationRequested();

            uint chunk = Math.Min(SectorsPerRead, cap.Sectors - done);
            var span = buffer.AsSpan(0, (int)(chunk * CookedSectorBytes));
            var r = dev.SendCommand(MmcCommands.Read10(done, (ushort)chunk), span,
                                    SptiDataDirection.In, timeoutSeconds: 60);

            if (r.Success)
            {
                output.Write(span);
            }
            else if (r.Asc == CopyProtectedAsc)
            {
                throw new DiscReadException(
                    $"Stopped at LBA {done} of {cap.Sectors:N0}: {r.Describe()}.{Environment.NewLine}{Environment.NewLine}" +
                    "The drive reported copy protection on this sector. DiscForge images " +
                    "unencrypted discs only and will not read past a protection response.");
            }
            else
            {
                // A read error somewhere in this chunk. Narrow it to the offending
                // sectors so one bad sector costs one sector, not the whole chunk.
                ReadChunkSectorBySector(dev, output, done, chunk, options, badSectors, notes, cancel);
            }

            done += chunk;
            progress?.Report(new ReadProgress(1, done, cap.Sectors,
                $"{done:N0}/{cap.Sectors:N0} sectors ({(double)done / cap.Sectors * 100:0.0}%)"));
        }

        return new ReadReport
        {
            BadSectors = badSectors,
            SectorsRead = cap.Sectors,
            Notes = notes,
        };
    }

    private static void ReadChunkSectorBySector(
        SptiDevice dev, Stream output, uint startLba, uint count, ReadOptions options,
        List<uint> badSectors, List<string> notes, CancellationToken cancel)
    {
        var one = new byte[CookedSectorBytes];
        for (uint i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            uint lba = startLba + i;

            SptiResult last = default;
            bool got = false;
            for (int attempt = 0; attempt <= Math.Max(0, options.RetriesPerSector); attempt++)
            {
                last = dev.SendCommand(MmcCommands.Read10(lba, 1), one, SptiDataDirection.In, timeoutSeconds: 60);
                if (last.Success) { got = true; break; }
                if (last.Asc == CopyProtectedAsc) break;   // protection: retrying is pointless
            }

            if (last.Asc == CopyProtectedAsc)
                throw new DiscReadException(
                    $"Stopped at LBA {lba}: {last.Describe()}. DiscForge images unencrypted discs only.");

            if (got)
            {
                output.Write(one);
                continue;
            }

            if (!options.ContinueOnError)
                throw new DiscReadException(
                    $"Read failed at LBA {lba} after {options.RetriesPerSector + 1} attempts: {last.Describe()}.{Environment.NewLine}{Environment.NewLine}" +
                    "Try cleaning the disc (soft cloth, centre outwards). To salvage the rest, pass " +
                    "--continue-on-error — the image will then be incomplete, and every missing sector is listed.");

            // Permitted to continue: zero-fill the hole and record it. The image is
            // explicitly partial and the caller is told exactly where.
            Array.Clear(one);
            output.Write(one);
            badSectors.Add(lba);
        }
    }
}
