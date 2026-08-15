// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Preservation;
using DiscForge.Core.Raw;
using DiscForge.Core.Util;

namespace DiscForge.Core.Dumping;

/// <summary>The CDRWIN "Data Type" selector: what each extracted sector is written as.</summary>
public enum ExtractDataType
{
    /// <summary>Full 2352-byte raw sectors, exactly as read.</summary>
    Raw2352,
    /// <summary>Mode 1 user data, 2048 bytes/sector. The sector must BE Mode 1 and its
    /// EDC must verify — a cooked sector that cannot prove itself is a read error.</summary>
    Mode1_2048,
    /// <summary>Mode 2 Form 1 user data, 2048 bytes/sector (EDC-verified).</summary>
    Mode2Form1_2048,
    /// <summary>Mode 2 Form 2 user data, 2324 bytes/sector.</summary>
    Mode2Form2_2324,
    /// <summary>Mode 2 body including sub-header, 2336 bytes/sector.</summary>
    Mode2Mixed_2336,
    /// <summary>Audio, 2352 bytes/sector, written exactly as read.</summary>
    Audio2352,
}

/// <summary>
/// The CDRWIN "Error Recovery" policy for a sector that stays unreadable after
/// every retry. Whatever the choice, the sector is recorded in the extraction's
/// <see cref="BadSectorMap"/> — the hole is never silent.
/// </summary>
public enum ExtractErrorRecovery
{
    /// <summary>Stop the extraction at the first unrecoverable sector.</summary>
    Abort,
    /// <summary>Write whatever the drive returned (or zeros if it returned nothing)
    /// and continue. The bytes are unproven and the map says so.</summary>
    Ignore,
    /// <summary>Write a well-formed dummy sector (valid sync/header/EDC/ECC for data,
    /// silence for audio) and continue. Structurally clean, and the map says which
    /// sectors are dummies.</summary>
    Replace,
}

/// <summary>Read controls for one extraction — the CDRWIN dialog's checkboxes.</summary>
public sealed record ExtractionOptions
{
    public ExtractDataType DataType { get; init; } = ExtractDataType.Raw2352;
    public ExtractErrorRecovery ErrorRecovery { get; init; } = ExtractErrorRecovery.Abort;

    /// <summary>Extra read attempts after the first failed one (CDRWIN's read-retry count).</summary>
    public int ReadRetries { get; init; } = 2;

    /// <summary>Treat a read whose C2 block flags any byte as a FAILED read (retried,
    /// then subject to <see cref="ErrorRecovery"/>). A drive that returns no C2
    /// block leaves the read ungated — reported, never assumed clean.</summary>
    public bool UseC2 { get; init; } = true;

    /// <summary>Capture formatted Q per sector: analyse CRCs and (if a subcode
    /// stream is given) write 16 bytes/sector alongside the image.</summary>
    public bool CaptureSubcode { get; init; }

    /// <summary>
    /// Extra Q-only re-reads when the accepted sector's Q frame fails its CRC.
    /// Sub-channel data carries no error correction, so single reads are noisy on
    /// every drive; re-reading until a CRC-valid frame lands is how subcode is
    /// captured honestly. The main-channel data — already proven — is never
    /// re-read or replaced by these attempts.
    /// </summary>
    public int QRetries { get; init; } = 4;

    /// <summary>Audio jitter guard: a sector is accepted only after two reads
    /// return identical bytes (consensus), each mismatch consuming a retry.
    /// Only meaningful for <see cref="ExtractDataType.Audio2352"/>.</summary>
    public bool JitterConsensus { get; init; }

    /// <summary>
    /// Demand the 12-byte sector sync on every raw read (set this when the span
    /// being extracted is a DATA track). Raw extraction is otherwise the one
    /// datatype with no structural proof — and a drive that has been fought to a
    /// standstill by damage can start returning all-zero buffers WITH success
    /// status for every remaining sector. That is how a half-void dump once
    /// passed as "2 bad sectors": 135,417 polite lies, zero checks to catch
    /// them. A data sector with no sync is a failed read, whatever the drive's
    /// status byte says.
    /// </summary>
    public bool RequireDataSync { get; init; }
}

/// <summary>One attempt at one sector, as the reader saw it.</summary>
public sealed record SectorReadAttempt
{
    public required bool Ok { get; init; }
    /// <summary>The raw 2352-byte main channel. May be shorter/empty on failure.</summary>
    public required byte[] Main { get; init; }
    /// <summary>294-byte C2 error-pointer block, or null if the drive gave none.</summary>
    public byte[]? C2 { get; init; }
    /// <summary>16-byte formatted Q (12-byte frame + CRC + pad), or null.</summary>
    public byte[]? Q16 { get; init; }
    /// <summary>Sense / reason when the read failed.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// A live source of raw sectors — a drive behind SCSI passthrough, or a fake in
/// tests. Each call is ONE attempt; the engine owns retries and policy.
/// </summary>
public interface IExtractionReader
{
    long TotalSectors { get; }
    SectorReadAttempt Read(long lba, bool wantC2, bool wantSubcode);
}

/// <summary>What one extraction did, sector by sector, with nothing hidden.</summary>
public sealed record ExtractionResult
{
    public required long SectorsRequested { get; init; }
    public required long SectorsWritten { get; init; }
    public required long BytesWritten { get; init; }
    /// <summary>Sectors that needed more than one attempt but were eventually proven.</summary>
    public required int Recovered { get; init; }
    /// <summary>Sectors written as unproven drive bytes under <see cref="ExtractErrorRecovery.Ignore"/>.</summary>
    public required int IgnoredBad { get; init; }
    /// <summary>Sectors written as well-formed dummies under <see cref="ExtractErrorRecovery.Replace"/>.</summary>
    public required int Replaced { get; init; }
    /// <summary>The LBA extraction stopped at under <see cref="ExtractErrorRecovery.Abort"/>, or null.</summary>
    public long? AbortedAtLba { get; init; }
    public string? AbortReason { get; init; }
    public required int QFramesChecked { get; init; }
    public required int QCrcErrors { get; init; }
    /// <summary>Q frames whose first read failed CRC but a re-read recovered — see
    /// <see cref="ExtractionOptions.QRetries"/>.</summary>
    public required int QRecovered { get; init; }
    /// <summary>Every bad sector, absolute LBA, whatever the recovery policy did with it.</summary>
    public required BadSectorMap BadSectors { get; init; }

    public bool Complete => AbortedAtLba is null && BadSectors.Clean;

    /// <summary>COMPLETE, INCOMPLETE (holes recorded) or ABORTED.</summary>
    public string Grade => AbortedAtLba is not null ? "ABORTED"
                         : BadSectors.Clean ? "COMPLETE" : "INCOMPLETE";
}

/// <summary>
/// The CDRWIN-style sector-range extractor: read a span of a disc (or any
/// <see cref="IExtractionReader"/>) with retries, optional C2 gating, optional
/// audio consensus, an explicit error-recovery policy, and per-sector Q capture —
/// and account for every sector that could not be proven. The engine never
/// invents success: a hole is aborted on, or written AND recorded, never smoothed
/// over.
/// </summary>
public static class SectorExtraction
{
    public const int RawSectorSize = 2352;
    public const int QBytesPerSector = 16;

    public static int PayloadSize(ExtractDataType t) => t switch
    {
        ExtractDataType.Mode1_2048 or ExtractDataType.Mode2Form1_2048 => 2048,
        ExtractDataType.Mode2Form2_2324 => 2324,
        ExtractDataType.Mode2Mixed_2336 => 2336,
        _ => RawSectorSize,
    };

    /// <summary>
    /// Extract sectors [startLba, endLba] inclusive to <paramref name="output"/>.
    /// <paramref name="subOutput"/>, when given with <see cref="ExtractionOptions.CaptureSubcode"/>,
    /// receives 16 bytes of formatted Q per sector (zeros where the drive gave none).
    /// </summary>
    public static ExtractionResult Extract(
        IExtractionReader reader, long startLba, long endLba, ExtractionOptions options,
        Stream output, Stream? subOutput = null, Action<long, long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (startLba < 0 || endLba < startLba || endLba >= reader.TotalSectors)
            throw new ArgumentOutOfRangeException(nameof(endLba),
                $"Range {startLba}..{endLba} is outside the readable area (0..{reader.TotalSectors - 1}).");
        if (options.ReadRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Retry count cannot be negative.");

        long total = endLba - startLba + 1;
        long written = 0, bytes = 0;
        int recovered = 0, ignored = 0, replaced = 0, qChecked = 0, qBad = 0, qRecovered = 0;
        var bad = new List<long>();
        long? abortedAt = null;
        string? abortReason = null;

        // With audio consensus on, every sector takes at least two reads by design —
        // "recovered" means it needed more than that baseline.
        int baselineAttempts =
            options.JitterConsensus && options.DataType == ExtractDataType.Audio2352 ? 2 : 1;

        // The mode of the last PROVEN sector (1, 2, or 0 for audio/no-sync). Replacement
        // dummies follow it, so a hole in a Mode 2 track is patched with a Mode 2 sector —
        // a Mode 1 dummy mid-XA-track is structurally alien, and the burn round-trip
        // showed a real drive losing tracking at exactly that boundary.
        byte? lastGoodMode = null;

        for (long lba = startLba; lba <= endLba; lba++)
        {
            var (good, attempt, attemptsUsed, why) = ReadProven(reader, lba, options);

            if (good && attemptsUsed > baselineAttempts) recovered++;

            byte[] payload;
            if (good)
            {
                payload = Convert(attempt!.Main, options.DataType);
                if (attempt.Main.Length == RawSectorSize)
                    lastGoodMode = HasSync(attempt.Main) ? attempt.Main[15] : (byte)0;
            }
            else
            {
                bad.Add(lba);
                switch (options.ErrorRecovery)
                {
                    case ExtractErrorRecovery.Abort:
                        abortedAt = lba;
                        abortReason = why;
                        goto done;
                    case ExtractErrorRecovery.Ignore:
                        payload = IgnorePayload(attempt, options.DataType);
                        ignored++;
                        break;
                    default:
                        payload = ReplacementPayload(lba, options.DataType, lastGoodMode);
                        replaced++;
                        break;
                }
            }

            output.Write(payload, 0, payload.Length);
            written++;
            bytes += payload.Length;

            if (options.CaptureSubcode)
            {
                var q = attempt?.Q16;
                bool qValid = q is { Length: QBytesPerSector } && QCrcOk(q);

                // Sub-channel has no error correction; a CRC-failed (or absent) Q on an
                // otherwise-proven sector earns Q-only re-reads until a valid frame lands.
                if (!qValid && good && options.QRetries > 0)
                {
                    for (int extra = 0; extra < options.QRetries && !qValid; extra++)
                    {
                        var re = reader.Read(lba, wantC2: false, wantSubcode: true);
                        if (re.Q16 is { Length: QBytesPerSector } rq)
                        {
                            q = rq;                       // keep the freshest frame, valid or not
                            qValid = QCrcOk(rq);
                        }
                    }
                    if (qValid) qRecovered++;
                }

                if (q is { Length: QBytesPerSector })
                {
                    qChecked++;
                    if (!qValid) qBad++;
                    subOutput?.Write(q, 0, QBytesPerSector);
                }
                else
                {
                    subOutput?.Write(new byte[QBytesPerSector], 0, QBytesPerSector);
                }
            }

            progress?.Invoke(lba - startLba + 1, total);
        }

    done:
        return new ExtractionResult
        {
            SectorsRequested = total,
            SectorsWritten = written,
            BytesWritten = bytes,
            Recovered = recovered,
            IgnoredBad = ignored,
            Replaced = replaced,
            AbortedAtLba = abortedAt,
            AbortReason = abortReason,
            QFramesChecked = qChecked,
            QCrcErrors = qBad,
            QRecovered = qRecovered,
            BadSectors = new BadSectorMap
            {
                Image = "",
                TotalSectors = (int)Math.Min(reader.TotalSectors, int.MaxValue),
                UnreadableLba = bad,
                Note = $"extract-sectors {startLba}..{endLba}, recovery={options.ErrorRecovery}, " +
                       $"retries={options.ReadRetries}, c2={(options.UseC2 ? "on" : "off")}",
            },
        };
    }

    // ---- proving one sector -------------------------------------------------

    private static (bool good, SectorReadAttempt? last, int attempts, string? why) ReadProven(
        IExtractionReader reader, long lba, ExtractionOptions o)
    {
        int maxAttempts = 1 + o.ReadRetries;
        SectorReadAttempt? last = null;
        byte[]? pendingConsensus = null;   // first clean read awaiting a matching second
        string? why = null;
        bool consensus = o.JitterConsensus && o.DataType == ExtractDataType.Audio2352;

        for (int i = 0; i < maxAttempts; i++)
        {
            var a = reader.Read(lba, o.UseC2, o.CaptureSubcode);
            last = a ?? throw new InvalidOperationException("Reader returned null attempt.");

            why = Failure(a, o);
            if (why is not null) { pendingConsensus = null; continue; }

            if (!consensus) return (true, a, i + 1, null);

            if (pendingConsensus is null)
            {
                pendingConsensus = a.Main;
                why = "awaiting consensus (one clean read, no confirmation yet)";
                continue;
            }
            if (pendingConsensus.AsSpan().SequenceEqual(a.Main))
                return (true, a, i + 1, null);

            // The two reads disagree — jitter. Start over with this read as candidate.
            pendingConsensus = a.Main;
            why = "reads disagree (jitter): no two attempts returned the same bytes";
        }
        return (false, last, maxAttempts, why);
    }

    /// <summary>Why this attempt cannot be trusted, or null if it proves out.</summary>
    private static string? Failure(SectorReadAttempt a, ExtractionOptions o)
    {
        if (!a.Ok) return a.Error is null ? "the read failed" : $"the read failed ({a.Error})";
        if (a.Main.Length != RawSectorSize)
            return $"the drive returned {a.Main.Length} bytes, not a raw {RawSectorSize}-byte sector";
        if (o.UseC2 && a.C2 is not null && CountC2Bits(a.C2) is int n and > 0)
            return $"C2 flagged {n} unreliable byte(s)";
        if (o.RequireDataSync && !HasSync(a.Main))
            return "no sector sync on a data track — the drive returned unstructured " +
                   "(likely muted/zero-filled) data despite claiming success";

        // Datatype-specific structural proof.
        switch (o.DataType)
        {
            case ExtractDataType.Mode1_2048:
                if (!HasSync(a.Main)) return "no sector sync — not a data sector";
                if (a.Main[15] != 1) return $"sector is Mode {a.Main[15]}, not Mode 1";
                if (!EdcEcc.VerifyMode1(a.Main).EdcOk) return "Mode 1 EDC mismatch — user data unproven";
                break;
            case ExtractDataType.Mode2Form1_2048:
                if (!HasSync(a.Main)) return "no sector sync — not a data sector";
                if (a.Main[15] != 2) return $"sector is Mode {a.Main[15]}, not Mode 2";
                if ((a.Main[18] & 0x20) != 0) return "sector is Form 2, not Form 1";
                if (!EdcEcc.VerifyMode2Form1(a.Main).EdcOk) return "Mode 2 Form 1 EDC mismatch — user data unproven";
                break;
            case ExtractDataType.Mode2Form2_2324:
                if (!HasSync(a.Main)) return "no sector sync — not a data sector";
                if (a.Main[15] != 2) return $"sector is Mode {a.Main[15]}, not Mode 2";
                if ((a.Main[18] & 0x20) == 0) return "sector is Form 1, not Form 2";
                break;
            case ExtractDataType.Mode2Mixed_2336:
                if (!HasSync(a.Main)) return "no sector sync — not a data sector";
                if (a.Main[15] != 2) return $"sector is Mode {a.Main[15]}, not Mode 2";
                break;
        }
        return null;
    }

    // ---- payloads -----------------------------------------------------------

    private static byte[] Convert(byte[] raw, ExtractDataType t) => t switch
    {
        ExtractDataType.Mode1_2048      => raw[16..2064],
        ExtractDataType.Mode2Form1_2048 => raw[24..2072],
        ExtractDataType.Mode2Form2_2324 => raw[24..2348],
        ExtractDataType.Mode2Mixed_2336 => raw[16..2352],
        _ => raw,
    };

    /// <summary>Ignore policy: pass through what the drive returned, sized for the
    /// datatype; zeros where the drive returned nothing usable.</summary>
    private static byte[] IgnorePayload(SectorReadAttempt? a, ExtractDataType t)
    {
        int size = PayloadSize(t);
        if (a is { Main.Length: RawSectorSize })
        {
            var c = Convert(a.Main, t);
            return c.Length == size ? c : new byte[size];
        }
        return new byte[size];
    }

    /// <summary>Replace policy: a structurally valid dummy — real sync, header for
    /// THIS LBA, valid EDC/ECC over zero user data; silence for audio; zeros for
    /// cooked payloads (which carry no structure of their own). For raw extraction
    /// the dummy's MODE follows the surrounding proven sectors (<paramref name="contextMode"/>):
    /// a Mode 2 XA track gets a Mode 2 Form 1 dummy, an audio span gets silence,
    /// and only a Mode 1 context (or no context at all) gets the Mode 1 dummy.</summary>
    private static byte[] ReplacementPayload(long lba, ExtractDataType t, byte? contextMode = null)
    {
        var msf = Msf.FromSectors(lba + 150);
        switch (t)
        {
            case ExtractDataType.Raw2352 when contextMode == 0:
                return new byte[RawSectorSize];            // audio neighbourhood: silence
            case ExtractDataType.Raw2352 when contextMode == 2:
            {
                // Mode 2 Form 1 dummy: sync, header, data-submode sub-header
                // (duplicated per spec), zero user data, valid EDC/ECC.
                var s = new byte[RawSectorSize];
                RawSectorBuilder.WriteSync(s);
                RawSectorBuilder.WriteHeader(s, msf, mode: 2);
                s[18] = 0x08; s[22] = 0x08;                // submode: Data, Form 1
                EdcEcc.FillMode2Form1(s);
                return s;
            }
            case ExtractDataType.Raw2352:
            {
                var s = new byte[RawSectorSize];
                RawSectorBuilder.BuildMode1(new byte[2048], msf, s);
                return s;
            }
            case ExtractDataType.Audio2352:
                return new byte[RawSectorSize];
            default:
                return new byte[PayloadSize(t)];
        }
    }

    // ---- small proofs -------------------------------------------------------

    public static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s.Length < 12 || s[0] != 0 || s[11] != 0) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    internal static int CountC2Bits(ReadOnlySpan<byte> c2)
    {
        int n = 0;
        foreach (var b in c2) n += System.Numerics.BitOperations.PopCount(b);
        return n;
    }

    /// <summary>
    /// Formatted-Q CRC check: the stored CRC-16 must vouch for the frame. Two
    /// canonical forms are accepted, both proven against the disc's own CRC:
    /// the frame exactly as received (BCD, as recorded on disc), and — for ADR-1
    /// frames — the binary form some drives return in formatted-Q mode (the
    /// classic Plextors among them: they convert the BCD fields to binary but
    /// pass the CRC of the original BCD frame through, verified live against a
    /// PX-W5224TA). The BCD restoration is deterministic, and a frame is only
    /// ever accepted because a stored CRC matches; nothing is waved through.
    /// </summary>
    public static bool QCrcOk(ReadOnlySpan<byte> q16)
    {
        if (q16.Length < 12) return false;
        if (QCrcMatches(q16)) return true;

        if ((q16[0] & 0x0F) == 1)                       // ADR 1: position frame, fields are pure numbers
        {
            Span<byte> bcd = stackalloc byte[12];
            q16[..12].CopyTo(bcd);
            for (int i = 1; i <= 9; i++)
            {
                int v = bcd[i];
                if (v > 99) return false;               // not representable in BCD — not this form
                bcd[i] = (byte)(((v / 10) << 4) | (v % 10));
            }
            return QCrcMatches(bcd);
        }
        return false;
    }

    private static bool QCrcMatches(ReadOnlySpan<byte> q12)
    {
        ushort crc = Crc16.ComputeInverted(q12[..10]);
        return q12[10] == (byte)(crc >> 8) && q12[11] == (byte)crc;
    }
}
