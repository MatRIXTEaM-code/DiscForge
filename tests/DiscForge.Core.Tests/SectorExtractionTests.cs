// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Cue;
using DiscForge.Core.Dumping;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The CDRWIN-style extraction engine, proven against a scripted fake drive: retries
/// recover what a flaky read misses, C2 gates what the drive flags, datatype
/// conversion demands structural proof (EDC) before handing out user data, the three
/// error-recovery policies do exactly what they claim — and every unproven sector is
/// on the record in the bad-sector map, never smoothed over.
/// </summary>
public class SectorExtractionTests
{
    private const int SS = SectorExtraction.RawSectorSize;

    // ---- a scripted drive ---------------------------------------------------

    /// <summary>A fake drive: per-LBA scripts of attempt outcomes. A script's last
    /// entry repeats forever; an LBA with no script serves its stored sector cleanly.</summary>
    private sealed class FakeDrive : IExtractionReader
    {
        public readonly Dictionary<long, byte[]> Sectors = new();
        public readonly Dictionary<long, Queue<SectorReadAttempt>> Script = new();
        public readonly Dictionary<long, SectorReadAttempt> Fallback = new();
        public readonly Dictionary<long, byte[]> Q = new();
        /// <summary>Per-LBA queue of Q frames served in order (last repeats) — for Q-retry tests.</summary>
        public readonly Dictionary<long, Queue<byte[]>> QScript = new();
        public int ReadsIssued;

        public long TotalSectors { get; init; } = 1000;

        public SectorReadAttempt Read(long lba, bool wantC2, bool wantSubcode)
        {
            ReadsIssued++;
            if (Script.TryGetValue(lba, out var q) && q.Count > 0)
            {
                var a = q.Count == 1 ? q.Peek() : q.Dequeue();
                return Decorate(a, lba, wantC2, wantSubcode);
            }
            if (Fallback.TryGetValue(lba, out var f)) return Decorate(f, lba, wantC2, wantSubcode);
            var main = Sectors.TryGetValue(lba, out var s) ? s : MakeAudio(lba);
            return Decorate(new SectorReadAttempt { Ok = true, Main = main }, lba, wantC2, wantSubcode);
        }

        private SectorReadAttempt Decorate(SectorReadAttempt a, long lba, bool wantC2, bool wantSubcode)
        {
            var c2 = a.C2 ?? (wantC2 && a.Ok ? new byte[294] : null);
            byte[]? q16 = a.Q16;
            if (q16 is null && wantSubcode)
            {
                if (QScript.TryGetValue(lba, out var qs) && qs.Count > 0)
                    q16 = qs.Count == 1 ? qs.Peek() : qs.Dequeue();
                else if (Q.TryGetValue(lba, out var qq))
                    q16 = qq;
            }
            return a with { C2 = wantC2 ? c2 : a.C2, Q16 = wantSubcode ? q16 : null };
        }
    }

    /// <summary>A deterministic pseudo-audio sector: 2352 bytes derived from the LBA.</summary>
    private static byte[] MakeAudio(long lba)
    {
        var b = new byte[SS];
        for (int i = 0; i < SS; i++) b[i] = (byte)((lba * 31 + i * 7) & 0xFF);
        return b;
    }

    /// <summary>A structurally valid raw Mode 1 sector for this LBA with patterned user data.</summary>
    private static byte[] MakeMode1(long lba)
    {
        var user = new byte[2048];
        for (int i = 0; i < user.Length; i++) user[i] = (byte)((lba + i) & 0xFF);
        var s = new byte[SS];
        RawSectorBuilder.BuildMode1(user, Msf.FromSectors(lba + 150), s);
        return s;
    }

    private static ExtractionResult Run(FakeDrive d, long start, long end, ExtractionOptions o,
                                        out byte[] output, Stream? sub = null)
    {
        using var ms = new MemoryStream();
        var r = SectorExtraction.Extract(d, start, end, o, ms, sub);
        output = ms.ToArray();
        return r;
    }

    // ---- the straightforward path ------------------------------------------

    [Fact]
    public void CleanRange_ExtractsExactBytes_AndGradesComplete()
    {
        var d = new FakeDrive();
        var o = new ExtractionOptions();               // raw 2352, abort, c2 on
        var r = Run(d, 10, 14, o, out var bytes);

        Assert.Equal("COMPLETE", r.Grade);
        Assert.True(r.Complete);
        Assert.Equal(5, r.SectorsWritten);
        Assert.Equal(5L * SS, r.BytesWritten);
        Assert.Equal(0, r.Recovered);
        Assert.True(r.BadSectors.Clean);
        for (int i = 0; i < 5; i++)
            Assert.Equal(MakeAudio(10 + i), bytes[(i * SS)..((i + 1) * SS)]);
    }

    [Fact]
    public void RangeOutsideDisc_IsRefusedUpFront()
    {
        var d = new FakeDrive { TotalSectors = 100 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Run(d, 90, 100, new ExtractionOptions(), out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Run(d, -1, 5, new ExtractionOptions(), out _));
    }

    // ---- retries and C2 gating ---------------------------------------------

    [Fact]
    public void FailedRead_IsRetried_AndCountsAsRecovered()
    {
        var d = new FakeDrive();
        d.Script[20] = new Queue<SectorReadAttempt>(new[]
        {
            new SectorReadAttempt { Ok = false, Main = [], Error = "medium error" },
            new SectorReadAttempt { Ok = true, Main = MakeAudio(20) },
        });

        var r = Run(d, 20, 20, new ExtractionOptions { ReadRetries = 2 }, out var bytes);
        Assert.Equal("COMPLETE", r.Grade);
        Assert.Equal(1, r.Recovered);
        Assert.Equal(MakeAudio(20), bytes);
    }

    [Fact]
    public void C2FlaggedRead_FailsAndRetries_WhenC2GateOn()
    {
        var d = new FakeDrive();
        var dirty = new byte[294]; dirty[0] = 0x80;    // one unreliable byte
        d.Script[5] = new Queue<SectorReadAttempt>(new[]
        {
            new SectorReadAttempt { Ok = true, Main = MakeAudio(5), C2 = dirty },
            new SectorReadAttempt { Ok = true, Main = MakeAudio(5) },
        });

        var r = Run(d, 5, 5, new ExtractionOptions { ReadRetries = 1 }, out _);
        Assert.Equal(1, r.Recovered);
        Assert.Equal("COMPLETE", r.Grade);
    }

    [Fact]
    public void C2FlaggedRead_Passes_WhenC2GateOff()
    {
        var d = new FakeDrive();
        var dirty = new byte[294]; dirty[0] = 0x80;
        d.Fallback[5] = new SectorReadAttempt { Ok = true, Main = MakeAudio(5), C2 = dirty };

        var r = Run(d, 5, 5, new ExtractionOptions { UseC2 = false, ReadRetries = 0 }, out _);
        Assert.Equal("COMPLETE", r.Grade);
        Assert.Equal(0, r.Recovered);
    }

    // ---- datatype conversion requires proof --------------------------------

    [Fact]
    public void Mode1UserData_IsExtracted_WhenEdcProves()
    {
        var d = new FakeDrive();
        for (long l = 0; l < 3; l++) d.Sectors[l] = MakeMode1(l);

        var r = Run(d, 0, 2, new ExtractionOptions { DataType = ExtractDataType.Mode1_2048 }, out var bytes);
        Assert.Equal("COMPLETE", r.Grade);
        Assert.Equal(3L * 2048, r.BytesWritten);
        for (int i = 0; i < 2048; i++)
            Assert.Equal((byte)((1 + i) & 0xFF), bytes[2048 + i]);   // sector 1's payload
    }

    [Fact]
    public void Mode1UserData_CorruptEdc_IsAReadError_NotAnExtraction()
    {
        var d = new FakeDrive();
        var broken = MakeMode1(0);
        broken[100] ^= 0xFF;                            // user byte flipped, EDC now lies
        d.Sectors[0] = broken;

        var r = Run(d, 0, 0, new ExtractionOptions
        {
            DataType = ExtractDataType.Mode1_2048,
            ReadRetries = 1,
        }, out var bytes);

        Assert.Equal("ABORTED", r.Grade);
        Assert.Equal(0L, r.AbortedAtLba);
        Assert.Contains("EDC", r.AbortReason);
        Assert.Empty(bytes);                            // nothing unproven was written
        Assert.Contains(0L, r.BadSectors.UnreadableLba);
    }

    [Fact]
    public void AudioDatatype_TakesBytesAsRead_NoStructuralDemand()
    {
        var d = new FakeDrive();
        var r = Run(d, 100, 101, new ExtractionOptions { DataType = ExtractDataType.Audio2352 }, out var bytes);
        Assert.Equal("COMPLETE", r.Grade);
        Assert.Equal(MakeAudio(100), bytes[..SS]);
    }

    // ---- the three recovery policies ---------------------------------------

    private static FakeDrive DeadSectorDrive(long deadLba)
    {
        var d = new FakeDrive();
        d.Fallback[deadLba] = new SectorReadAttempt { Ok = false, Main = [], Error = "unrecovered read error" };
        return d;
    }

    [Fact]
    public void Abort_StopsAtTheBadSector_AndSaysWhere()
    {
        var d = DeadSectorDrive(12);
        var r = Run(d, 10, 14, new ExtractionOptions { ErrorRecovery = ExtractErrorRecovery.Abort, ReadRetries = 1 }, out var bytes);

        Assert.Equal("ABORTED", r.Grade);
        Assert.Equal(12L, r.AbortedAtLba);
        Assert.Equal(2, r.SectorsWritten);              // 10 and 11 made it
        Assert.Equal(2 * SS, bytes.Length);
        Assert.Contains(12L, r.BadSectors.UnreadableLba);
    }

    [Fact]
    public void Ignore_WritesZeros_WhenTheDriveGaveNothing_AndRecordsTheHole()
    {
        var d = DeadSectorDrive(12);
        var r = Run(d, 10, 14, new ExtractionOptions { ErrorRecovery = ExtractErrorRecovery.Ignore, ReadRetries = 0 }, out var bytes);

        Assert.Equal("INCOMPLETE", r.Grade);
        Assert.Equal(5, r.SectorsWritten);
        Assert.Equal(1, r.IgnoredBad);
        Assert.Equal(new byte[SS], bytes[(2 * SS)..(3 * SS)]);
        Assert.Equal(new[] { 12L }, r.BadSectors.UnreadableLba);
        Assert.Equal(MakeAudio(13), bytes[(3 * SS)..(4 * SS)]);   // extraction carried on
    }

    [Fact]
    public void Ignore_PassesThroughUnprovenBytes_WhenTheDriveReturnedSome()
    {
        var d = new FakeDrive();
        var garbage = MakeAudio(999);
        var dirty = new byte[294]; dirty[10] = 0xFF;    // C2 says the bytes are bad
        d.Fallback[12] = new SectorReadAttempt { Ok = true, Main = garbage, C2 = dirty };

        var r = Run(d, 12, 12, new ExtractionOptions { ErrorRecovery = ExtractErrorRecovery.Ignore, ReadRetries = 0 }, out var bytes);
        Assert.Equal("INCOMPLETE", r.Grade);
        Assert.Equal(garbage, bytes);                   // as-read, exactly — and on the record:
        Assert.Equal(new[] { 12L }, r.BadSectors.UnreadableLba);
    }

    [Fact]
    public void Replace_BuildsAProvablyValidDummy_ForRawData()
    {
        var d = DeadSectorDrive(12);
        var r = Run(d, 12, 12, new ExtractionOptions { ErrorRecovery = ExtractErrorRecovery.Replace, ReadRetries = 0 }, out var bytes);

        Assert.Equal("INCOMPLETE", r.Grade);
        Assert.Equal(1, r.Replaced);
        Assert.Equal(SS, bytes.Length);

        // The dummy must be structurally beyond reproach: sync, THIS sector's
        // header address, and EDC/ECC that verify over zero user data.
        Assert.True(SectorExtraction.HasSync(bytes));
        var msf = Msf.FromSectors(12 + 150);
        Assert.Equal(Bcd.From(msf.Minutes), bytes[12]);
        Assert.Equal(Bcd.From(msf.Seconds), bytes[13]);
        Assert.Equal(Bcd.From(msf.Frames), bytes[14]);
        Assert.Equal(1, bytes[15]);
        var (edcOk, eccOk) = EdcEcc.VerifyMode1(bytes);
        Assert.True(edcOk); Assert.True(eccOk);
        Assert.All(bytes[16..2064], b => Assert.Equal(0, b));
        Assert.Equal(new[] { 12L }, r.BadSectors.UnreadableLba);
    }

    [Fact]
    public void Replace_IsSilence_ForAudio()
    {
        var d = DeadSectorDrive(12);
        var r = Run(d, 12, 12, new ExtractionOptions
        {
            DataType = ExtractDataType.Audio2352,
            ErrorRecovery = ExtractErrorRecovery.Replace,
            ReadRetries = 0,
        }, out var bytes);
        Assert.Equal(new byte[SS], bytes);
        Assert.Equal(1, r.Replaced);
    }

    // ---- audio jitter consensus --------------------------------------------

    [Fact]
    public void JitterConsensus_AcceptsOnlyTwoMatchingReads()
    {
        var d = new FakeDrive();                        // stable fake: every read identical
        var r = Run(d, 50, 52, new ExtractionOptions
        {
            DataType = ExtractDataType.Audio2352,
            JitterConsensus = true,
            ReadRetries = 2,
        }, out var bytes);

        Assert.Equal("COMPLETE", r.Grade);
        Assert.Equal(0, r.Recovered);                   // two reads is the consensus baseline, not a recovery
        Assert.Equal(6, d.ReadsIssued);                 // exactly two per sector
        Assert.Equal(MakeAudio(50), bytes[..SS]);
    }

    [Fact]
    public void JitterConsensus_NeverTwoAlike_IsAFailure_NotAGuess()
    {
        var d = new FakeDrive();
        var q = new Queue<SectorReadAttempt>();
        for (int i = 0; i < 8; i++)                     // every read different: pathological jitter
            q.Enqueue(new SectorReadAttempt { Ok = true, Main = MakeAudio(700 + i) });
        d.Script[50] = q;

        var r = Run(d, 50, 50, new ExtractionOptions
        {
            DataType = ExtractDataType.Audio2352,
            JitterConsensus = true,
            ReadRetries = 3,
        }, out var bytes);

        Assert.Equal("ABORTED", r.Grade);
        Assert.Contains("jitter", r.AbortReason);
        Assert.Empty(bytes);
    }

    [Fact]
    public void JitterConsensus_RecoversAfterOneMismatch()
    {
        var d = new FakeDrive();
        var truth = MakeAudio(60);
        d.Script[60] = new Queue<SectorReadAttempt>(new[]
        {
            new SectorReadAttempt { Ok = true, Main = MakeAudio(999) },  // jittered read
            new SectorReadAttempt { Ok = true, Main = truth },
            new SectorReadAttempt { Ok = true, Main = truth },           // confirmation
        });

        var r = Run(d, 60, 60, new ExtractionOptions
        {
            DataType = ExtractDataType.Audio2352,
            JitterConsensus = true,
            ReadRetries = 3,
        }, out var bytes);

        Assert.Equal("COMPLETE", r.Grade);
        Assert.Equal(1, r.Recovered);                   // needed more than the 2-read baseline
        Assert.Equal(truth, bytes);
    }

    // ---- subcode capture ----------------------------------------------------

    private static byte[] Q16For(long lba, bool corrupt = false)
    {
        var q12 = SubQ.Position(QControl.None, track: 1, index: 1,
            Msf.FromSectors(lba), Msf.FromSectors(lba + 150));
        var q16 = new byte[16];
        q12.CopyTo(q16, 0);
        if (corrupt) q16[3] ^= 0x01;                     // data changed, CRC now wrong
        return q16;
    }

    [Fact]
    public void Subcode_IsWrittenPerSector_AndCrcsAreCounted()
    {
        var d = new FakeDrive();
        d.Q[30] = Q16For(30);
        d.Q[31] = Q16For(31, corrupt: true);
        // LBA 32: drive returns no Q at all.

        using var sub = new MemoryStream();
        var r = Run(d, 30, 32, new ExtractionOptions { CaptureSubcode = true }, out _, sub);

        Assert.Equal(2, r.QFramesChecked);
        Assert.Equal(1, r.QCrcErrors);
        var subBytes = sub.ToArray();
        Assert.Equal(3 * 16, subBytes.Length);
        Assert.Equal(Q16For(30), subBytes[..16]);
        Assert.Equal(new byte[16], subBytes[32..48]);    // no Q → zeros, not invention
    }

    [Fact]
    public void QRetry_RecoversAValidFrame_MainChannelUntouched()
    {
        var d = new FakeDrive();
        d.QScript[40] = new Queue<byte[]>(new[]
        {
            Q16For(40, corrupt: true),                  // first read: noisy Q
            Q16For(40, corrupt: true),                  // still noisy
            Q16For(40),                                 // third lands clean
        });

        using var sub = new MemoryStream();
        var r = Run(d, 40, 40, new ExtractionOptions { CaptureSubcode = true, QRetries = 4 }, out var bytes, sub);

        Assert.Equal(1, r.QRecovered);
        Assert.Equal(0, r.QCrcErrors);
        Assert.Equal(Q16For(40), sub.ToArray());        // the valid frame is what's on disk
        Assert.Equal(MakeAudio(40), bytes);             // main channel from the FIRST read, untouched
        Assert.Equal(3, d.ReadsIssued);                 // 1 sector read + 2 Q-only re-reads
    }

    [Fact]
    public void QRetry_Exhausted_KeepsTheFreshestFrame_AndCountsTheError()
    {
        var d = new FakeDrive();
        d.Q[40] = Q16For(40, corrupt: true);            // every read returns the same bad frame

        using var sub = new MemoryStream();
        var r = Run(d, 40, 40, new ExtractionOptions { CaptureSubcode = true, QRetries = 3 }, out _, sub);

        Assert.Equal(0, r.QRecovered);
        Assert.Equal(1, r.QCrcErrors);                  // still on the record — retried, not hidden
        Assert.Equal(Q16For(40, corrupt: true), sub.ToArray());
        Assert.Equal(4, d.ReadsIssued);                 // 1 + 3 exhausted re-reads
    }

    [Fact]
    public void QRetry_Zero_IsTheOldSingleReadBehaviour()
    {
        var d = new FakeDrive();
        d.Q[40] = Q16For(40, corrupt: true);

        var r = Run(d, 40, 40, new ExtractionOptions { CaptureSubcode = true, QRetries = 0 }, out _);
        Assert.Equal(1, r.QCrcErrors);
        Assert.Equal(0, r.QRecovered);
        Assert.Equal(1, d.ReadsIssued);
    }

    [Fact]
    public void QRetry_DoesNotFire_WhenTheFirstFrameIsValid()
    {
        var d = new FakeDrive();
        d.Q[40] = Q16For(40);

        var r = Run(d, 40, 40, new ExtractionOptions { CaptureSubcode = true, QRetries = 4 }, out _);
        Assert.Equal(0, r.QCrcErrors);
        Assert.Equal(0, r.QRecovered);
        Assert.Equal(1, d.ReadsIssued);                 // no wasted reads on clean captures
    }

    [Fact]
    public void QCrcCheck_MatchesTheSubQBuilder()
    {
        Assert.True(SectorExtraction.QCrcOk(Q16For(1234)));
        Assert.False(SectorExtraction.QCrcOk(Q16For(1234, corrupt: true)));
    }

    /// <summary>
    /// Frames captured LIVE from a Plextor PX-W5224TA reading a pressed PS1 disc.
    /// In formatted-Q mode this drive family converts an ADR-1 frame's BCD fields
    /// to binary but passes through the CRC computed over the original BCD frame —
    /// so the check must prove the frame in either canonical form, and still reject
    /// genuinely corrupt frames in both.
    /// </summary>
    [Fact]
    public void QCrcCheck_AcceptsTheBinaryFormattedQForm_CapturedFromRealHardware()
    {
        // LBA 10: rel MSF 00:00:10, abs 00:02:10 — fields are binary (0x0a), CRC is over the BCD frame.
        Assert.True(SectorExtraction.QCrcOk(System.Convert.FromHexString("41010100000a0000020a3e5900000000")));
        // LBA 11, same convention.
        Assert.True(SectorExtraction.QCrcOk(System.Convert.FromHexString("41010100000b0000020b842900000000")));
        // LBA 0: BCD and binary coincide below ten — passes as-received.
        Assert.True(SectorExtraction.QCrcOk(System.Convert.FromHexString("41010100000000000200283200000000")));
        // A genuinely noisy frame off the same disc (track number flipped to 0x15 mid-track):
        // fails in BOTH forms — corruption is still corruption.
        Assert.False(SectorExtraction.QCrcOk(System.Convert.FromHexString("41150100012100000321baaf00000000")));
    }

    [Fact]
    public void QCrcCheck_BcdRestoration_OnlyAppliesToAdr1Frames()
    {
        // An ADR-2 (MCN) frame with binary-looking bytes must NOT be waved through
        // via BCD restoration — its fields are packed digits, not numbers.
        var mcn = Q16For(1234);                          // valid ADR-1 base…
        mcn[0] = (byte)((mcn[0] & 0xF0) | 0x02);         // …rebadged ADR-2: CRC no longer matches
        Assert.False(SectorExtraction.QCrcOk(mcn));
    }
}
