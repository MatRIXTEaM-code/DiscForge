// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

namespace DiscForge.Core.Floppy;

/// <summary>How hard to try, and what to do when a sector will not read.</summary>
public sealed record FloppyReadOptions
{
    /// <summary>Re-reads of a failing sector before giving up on it.</summary>
    public int RetriesPerSector { get; init; } = 4;

    /// <summary>Opt-in: on an unreadable sector, zero-fill it and carry on rather
    /// than stopping. The image is then explicitly partial, and every filled sector
    /// is listed. Off by default — a silent hole is worse than a clear stop.</summary>
    public bool ContinueOnError { get; init; }
}

/// <summary>The outcome of imaging a floppy disk.</summary>
public sealed record FloppyImageReport
{
    /// <summary>Total bytes written to the image.</summary>
    public required long Bytes { get; init; }
    /// <summary>Whole 512-byte sectors written.</summary>
    public required long Sectors { get; init; }
    /// <summary>The recognised disk geometry for <see cref="Bytes"/>, or a
    /// "non-standard" note when the size matches no common floppy.</summary>
    public required string Geometry { get; init; }
    /// <summary>True when the final read was short of a full sector.</summary>
    public bool ShortFinalRead { get; init; }
    /// <summary>Set when a sector could not be read and ContinueOnError was off: the
    /// copy stopped here and the image holds everything before this sector.</summary>
    public long StoppedAtSector { get; init; } = -1;
    /// <summary>Sectors that were unreadable and zero-filled (ContinueOnError on).</summary>
    public IReadOnlyList<long> ZeroFilled { get; init; } = [];

    /// <summary>True if every sector read cleanly to a normal end of disk.</summary>
    public bool Complete => StoppedAtSector < 0 && ZeroFilled.Count == 0;
}

/// <summary>
/// Copies a floppy disk to a flat <c>.img</c> — the raw sectors, in order, with no
/// filesystem interpretation. Pairs with the floppy readers (<c>floppy-info</c>,
/// <c>fat-ls</c>, <c>fat-lint</c>, ADF/D64) which work on the resulting image.
///
/// The transport is deliberately just a seekable <see cref="Stream"/>: a floppy is
/// an ordinary block device, so the caller opens it (<c>\\.\A:</c> on Windows,
/// <c>/dev/fd0</c> on Unix) and this copies it. Reads are 512-byte aligned. Each
/// sector is retried before it is treated as bad; an unreadable sector then either
/// stops the copy (keeping everything read so far) or, with ContinueOnError, is
/// zero-filled and recorded. The copy ends when the device returns end-of-stream —
/// for a healthy disk that lands exactly on a known geometry.
///
/// Preservation only — a floppy carries no protection to defeat.
/// </summary>
public static class FloppyImager
{
    public const int SectorBytes = 512;

    /// <summary>A safety ceiling so a disk that errors on every sector can't spin
    /// forever in continue mode: the largest common floppy is 2.88 MB.</summary>
    private const long MaxSectors = 2_949_120 / SectorBytes;   // 5760

    /// <summary>Name the disk geometry for a raw image size.</summary>
    public static string DescribeSize(long bytes) => bytes switch
    {
        163_840 => "160 KB, 5.25\" SS/DD",
        184_320 => "180 KB, 5.25\" SS/DD",
        327_680 => "320 KB, 5.25\" DS/DD",
        368_640 => "360 KB, 5.25\" DS/DD",
        737_280 => "720 KB, 3.5\" DS/DD",
        1_228_800 => "1.2 MB, 5.25\" DS/HD",
        1_474_560 => "1.44 MB, 3.5\" DS/HD",
        1_720_320 => "1.68 MB, 3.5\" DMF",
        2_949_120 => "2.88 MB, 3.5\" DS/ED",
        _ => $"{bytes / 1024.0:N1} KB (non-standard size)",
    };

    /// <summary>Copy <paramref name="src"/> to <paramref name="dst"/> sector by
    /// sector until end-of-stream (or, on a read error, per the options).</summary>
    public static FloppyImageReport Copy(Stream src, Stream dst,
                                         IProgress<long>? progress = null,
                                         FloppyReadOptions? options = null,
                                         CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        options ??= new FloppyReadOptions();

        var buf = new byte[SectorBytes];
        var zeros = new byte[SectorBytes];
        var zeroFilled = new List<long>();
        long total = 0, sectors = 0, index = 0;
        bool shortRead = false, aborted = false;
        long stoppedAt = -1;

        while (index < MaxSectors)
        {
            cancel.ThrowIfCancellationRequested();
            long offset = index * SectorBytes;

            var (got, failed) = TryReadSector(src, offset, buf, options.RetriesPerSector);

            if (failed)
            {
                if (options.ContinueOnError)
                {
                    dst.Write(zeros, 0, SectorBytes);
                    zeroFilled.Add(index);
                    total += SectorBytes;
                    index++;
                    progress?.Report(total);
                    continue;
                }
                aborted = true;
                stoppedAt = index;
                break;
            }

            if (got == 0) break;                 // clean end of disk

            dst.Write(buf, 0, got);
            total += got;
            if (got == SectorBytes) { sectors++; index++; }
            else { shortRead = true; break; }    // a short (non-zero) read is the tail

            progress?.Report(total);
        }

        return new FloppyImageReport
        {
            Bytes = total,
            Sectors = sectors,
            Geometry = DescribeSize(total),
            ShortFinalRead = shortRead,
            StoppedAtSector = aborted ? stoppedAt : -1,
            ZeroFilled = zeroFilled,
        };
    }

    /// <summary>Read one sector at <paramref name="offset"/>, retrying on I/O error.
    /// Returns (bytesRead, failed): failed=true means it never read after retries;
    /// bytesRead=0 with failed=false is a clean end-of-stream.</summary>
    private static (int Got, bool Failed) TryReadSector(Stream s, long offset, byte[] buf, int retries)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                s.Seek(offset, SeekOrigin.Begin);
                int got = 0;
                while (got < buf.Length)
                {
                    int n = s.Read(buf, got, buf.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                return (got, false);
            }
            catch (IOException)
            {
                if (attempt >= Math.Max(0, retries)) return (0, true);
                System.Threading.Thread.Sleep(40 * (attempt + 1));   // brief back-off, then re-read
            }
        }
    }
}
