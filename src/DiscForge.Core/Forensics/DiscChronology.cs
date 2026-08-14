// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using System.Text;
using DiscForge.Core.Iso;

namespace DiscForge.Core.Forensics;

/// <summary>A decoded ISO 9660 timestamp (volume descriptor or directory record).</summary>
public sealed record IsoDateTime(int Year, int Month, int Day, int Hour, int Minute, int Second, int GmtQuarterHours)
{
    /// <summary>The field was all zeros or blanks — "no date recorded".</summary>
    public bool Blank { get; init; }

    public bool IsValid => !Blank
        && Year is >= 1 and <= 9999 && Month is >= 1 and <= 12 && Day is >= 1 and <= 31
        && Hour < 24 && Minute < 60 && Second < 62;

    /// <summary>As a UTC-anchored instant (applying the recorded GMT offset), or null if invalid.</summary>
    public DateTimeOffset? ToInstant()
    {
        if (!IsValid) return null;
        try
        {
            var off = TimeSpan.FromMinutes(15 * GmtQuarterHours);
            return new DateTimeOffset(Year, Month, Day, Hour, Minute, Math.Min(Second, 59), off);
        }
        catch { return null; }
    }

    public override string ToString() =>
        Blank ? "(none)"
        : IsValid ? $"{Year:D4}-{Month:D2}-{Day:D2} {Hour:D2}:{Minute:D2}:{Second:D2} (GMT{(GmtQuarterHours >= 0 ? "+" : "")}{GmtQuarterHours * 0.25:0.##})"
        : "(invalid)";
}

/// <summary>What the disc's embedded dates and identifiers say about when and by whom it was mastered.</summary>
public sealed record MasteringReport
{
    public required string SystemId { get; init; }
    public required string VolumeId { get; init; }
    public required string Publisher { get; init; }
    public required string DataPreparer { get; init; }
    public required string Application { get; init; }

    public required IsoDateTime VolumeCreated { get; init; }
    public required IsoDateTime VolumeModified { get; init; }
    public required IsoDateTime VolumeExpires { get; init; }
    public required IsoDateTime VolumeEffective { get; init; }

    public required int FileCount { get; init; }
    public required IsoDateTime? EarliestFile { get; init; }
    public required IsoDateTime? LatestFile { get; init; }
    public required int FilesAfterVolume { get; init; }

    /// <summary>Contradictions worth a human's attention — silent re-mastering, tampering, or
    /// simply a disc mastered by tooling that didn't set dates honestly.</summary>
    public required IReadOnlyList<string> Anomalies { get; init; }

    public bool LooksTampered => Anomalies.Count > 0;

    public string Summary()
    {
        string created = VolumeCreated.IsValid ? VolumeCreated.ToString() : "no volume date";
        return LooksTampered
            ? $"Mastered {created}; {Anomalies.Count} anomaly(ies) — possible re-mastering or tampering."
            : $"Mastered {created}; dates are internally consistent.";
    }
}

/// <summary>
/// Temporal / mastering fingerprinting: read the timestamps and identifiers an ISO 9660
/// image was pressed with — the volume creation/modification dates, every file's recording
/// date, and the system/publisher/preparer/application strings — and flag the contradictions
/// that reveal a disc was quietly altered after it was mastered. The signature tell is a file
/// dated <i>after</i> the volume was created: on an untouched master every file predates the
/// volume, so a newer file means someone rebuilt part of the disc. Detection-only forensics —
/// it reads what is there and reports; it changes and defeats nothing.
///
/// Expects a cooked ISO 9660 image (2048-byte sectors).
/// </summary>
public static class DiscChronology
{
    private const int SS = 2048;
    private const int MaxDepth = 24;
    private const int MaxRecords = 200_000;   // guard against a malformed tree

    public static MasteringReport Analyze(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length < 17 * SS)
            throw new IsoFormatException("Image is too small to hold an ISO 9660 primary volume descriptor.");

        var pvd = image.AsSpan(16 * SS, SS);
        if (pvd[0] != 1 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' || pvd[3] != (byte)'0' ||
            pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
            throw new IsoFormatException(
                "No ISO 9660 primary volume descriptor at sector 16 — disc-date needs a cooked ISO 9660 image.");

        string systemId = Ascii(pvd, 8, 32);
        string volumeId = Ascii(pvd, 40, 32);
        string publisher = Ascii(pvd, 318, 128);
        string preparer = Ascii(pvd, 446, 128);
        string application = Ascii(pvd, 574, 128);

        var created = ParseVolumeDate(pvd.Slice(813, 17));
        var modified = ParseVolumeDate(pvd.Slice(830, 17));
        var expires = ParseVolumeDate(pvd.Slice(847, 17));
        var effective = ParseVolumeDate(pvd.Slice(864, 17));

        uint rootExtent = ReadU32Le(pvd.Slice(156 + 2, 4));
        uint rootSize = ReadU32Le(pvd.Slice(156 + 10, 4));

        var fileDates = new List<IsoDateTime>();
        int fileCount = 0;
        CollectDates(image, rootExtent, rootSize, 0, fileDates, ref fileCount);

        var valid = fileDates.Where(d => d.IsValid && d.ToInstant() is not null).ToList();
        IsoDateTime? earliest = valid.OrderBy(d => d.ToInstant()!.Value).FirstOrDefault();
        IsoDateTime? latest = valid.OrderBy(d => d.ToInstant()!.Value).LastOrDefault();

        int filesAfterVolume = 0;
        var anomalies = new List<string>();

        if (created.Blank)
            anomalies.Add("The volume carries no creation date — mastering tooling left it blank.");
        else if (!created.IsValid)
            anomalies.Add("The volume creation date is malformed.");

        if (modified.IsValid && created.IsValid &&
            modified.ToInstant() < created.ToInstant())
            anomalies.Add("The volume modification date precedes its creation date.");

        if (created.IsValid && created.ToInstant() is { } vol)
        {
            filesAfterVolume = valid.Count(d => d.ToInstant()!.Value > vol.AddSeconds(2));
            if (filesAfterVolume > 0)
                anomalies.Add($"{filesAfterVolume} file(s) are dated AFTER the volume was created — " +
                              "on an untouched master every file predates the volume, so this points to " +
                              "re-mastering or tampering.");
        }

        if (earliest?.ToInstant() is { } lo && latest?.ToInstant() is { } hi)
        {
            double years = (hi - lo).TotalDays / 365.25;
            if (years > 8)
                anomalies.Add($"File dates span {years:0.#} years — a range that wide suggests files from " +
                              "mixed sources rather than a single mastering.");
        }

        foreach (var d in new[] { created, modified, effective })
            if (d.IsValid && d.Year > 2100)
            {
                anomalies.Add($"An implausible future date ({d}) is recorded.");
                break;
            }

        return new MasteringReport
        {
            SystemId = systemId,
            VolumeId = volumeId,
            Publisher = publisher,
            DataPreparer = preparer,
            Application = application,
            VolumeCreated = created,
            VolumeModified = modified,
            VolumeExpires = expires,
            VolumeEffective = effective,
            FileCount = fileCount,
            EarliestFile = earliest,
            LatestFile = latest,
            FilesAfterVolume = filesAfterVolume,
            Anomalies = anomalies,
        };
    }

    // ---- date parsing (exposed for tests) -----------------------------------

    /// <summary>Parse a 17-byte volume-descriptor date ("YYYYMMDDHHMMSScc" + GMT-offset byte).</summary>
    public static IsoDateTime ParseVolumeDate(ReadOnlySpan<byte> b)
    {
        if (b.Length < 17) return Blank();
        bool blank = true;
        for (int i = 0; i < 16; i++)
            if (b[i] is not ((byte)'0' or (byte)' ' or 0)) { blank = false; break; }
        if (blank) return Blank();

        int Y = Digits(b, 0, 4), Mo = Digits(b, 4, 2), D = Digits(b, 6, 2);
        int H = Digits(b, 8, 2), Mi = Digits(b, 10, 2), S = Digits(b, 12, 2);
        return new IsoDateTime(Y, Mo, D, H, Mi, S, (sbyte)b[16]);
    }

    /// <summary>Parse a 7-byte directory-record date (years-since-1900, month, day, h, m, s, GMT).</summary>
    public static IsoDateTime ParseRecordDate(ReadOnlySpan<byte> b)
    {
        if (b.Length < 7) return Blank();
        bool blank = true;
        for (int i = 0; i < 6; i++) if (b[i] != 0) { blank = false; break; }
        if (blank) return Blank();
        return new IsoDateTime(1900 + b[0], b[1], b[2], b[3], b[4], b[5], (sbyte)b[6]);
    }

    private static IsoDateTime Blank() => new(0, 0, 0, 0, 0, 0, 0) { Blank = true };

    private static int Digits(ReadOnlySpan<byte> b, int off, int len)
    {
        int v = 0;
        for (int i = 0; i < len; i++)
        {
            byte c = b[off + i];
            if (c is < (byte)'0' or > (byte)'9') return -1;
            v = v * 10 + (c - '0');
        }
        return v;
    }

    // ---- directory walk -----------------------------------------------------

    private static void CollectDates(byte[] image, uint extent, uint size, int depth,
                                     List<IsoDateTime> dates, ref int fileCount)
    {
        if (depth > MaxDepth || fileCount > MaxRecords) return;
        long start = (long)extent * SS;
        if (start < 0 || start >= image.Length) return;
        int len = (int)Math.Min(size, image.Length - start);

        int p = 0;
        while (p < len)
        {
            int recLen = image[start + p];
            if (recLen == 0)
            {
                int next = (p / SS + 1) * SS;
                if (next <= p) break;
                p = next;
                continue;
            }
            if (recLen < 34 || start + p + recLen > image.Length) break;

            var rec = image.AsSpan((int)start + p, recLen);
            uint childExtent = ReadU32Le(rec.Slice(2, 4));
            uint childSize = ReadU32Le(rec.Slice(10, 4));
            byte flags = rec[25];
            int idLen = rec[32];

            if (idLen >= 1 && !(idLen == 1 && (rec[33] == 0x00 || rec[33] == 0x01)))
            {
                var date = ParseRecordDate(rec.Slice(18, 7));
                dates.Add(date);
                fileCount++;

                if ((flags & 0x02) != 0 && childExtent != extent && depth < MaxDepth)
                    CollectDates(image, childExtent, childSize, depth + 1, dates, ref fileCount);
            }
            p += recLen;
        }
    }

    // ---- helpers ------------------------------------------------------------

    private static string Ascii(ReadOnlySpan<byte> b, int off, int len)
    {
        if (off + len > b.Length) len = b.Length - off;
        return Encoding.ASCII.GetString(b.Slice(off, len)).TrimEnd(' ', '\0');
    }

    private static uint ReadU32Le(ReadOnlySpan<byte> b) =>
        (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
}
