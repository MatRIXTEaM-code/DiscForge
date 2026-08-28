// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Buffers.Binary;
using System.Runtime.Versioning;
using DiscForge.Core.Burning;
using DiscForge.Core.Mmc;
using DiscForge.Core.Raw;
using DiscForge.Devices.Spti;

namespace DiscForge.Devices.Burning;

/// <summary>
/// RAW DAO-96 burning over direct SPTI/MMC — the ImgBurn approach, bypassing IMAPI2's raw-CD
/// writer (which rejects hand-built images). We put the drive in raw DAO mode with a MODE
/// SELECT of the Write Parameters page (write type = Raw, data block type = raw + P-W
/// sub-channel, 2448 B/sector), hand it the disc layout via SEND CUE SHEET, then stream our
/// exact <see cref="RawImageGenerator"/> bytes with WRITE(10) and finalise with CLOSE SESSION.
/// This is the path that writes DiscForge's byte-faithful image — exact gaps, ISRC/MCN,
/// verbatim sub-channel — to the disc.
///
/// STATUS: built from the public MMC write model, validated incrementally on hardware.
/// <see cref="Burn"/> is Write Type = Raw — full raw+P-W stream, lead-in included, NO cue
/// sheet (a cue sheet under Raw is a command-sequence error, ASC 0x2C/0x04) — see its own doc
/// comment for why. <see cref="TestCue"/> is a LEGACY diagnostic for a different, abandoned
/// setup (Session-At-Once + SEND CUE SHEET, data block type 0) that <see cref="Burn"/> no
/// longer uses at all. Five real-hardware attempts across a session (2026-08) to make
/// <see cref="TestCue"/>'s cue sheet accepted (ASC 0x26/0x00, "invalid field in parameter
/// list", every time — see docs/NEXT.md for the full history) were all spent hardening a path
/// the real burn doesn't call — do not resume that debugging without first re-reading NEXT.md.
/// The actual non-destructive validator for the path <see cref="Burn"/> uses is
/// <see cref="Burn"/> itself with <c>simulate: true</c> (laser off, full write loop, no disc
/// written) — that is the real test, and as of 2026-08 it had never actually been run.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SptiRawDaoBurnEngine
{
    /// <summary>Data Block Type 3: raw main + raw interleaved P-W sub-channel (2448 B/sector).</summary>
    private const byte DataBlockRawPw = 3;
    private const int RawSectorBytes = 2448;

    public sealed record CueTestResult(bool Accepted, string Detail, int CueEntries, int CueBytes);

    /// <summary>
    /// Non-destructive: put the drive in raw DAO mode and hand it the cue sheet, reporting
    /// whether the drive accepted both. Nothing is written to the disc — this is how we
    /// validate the write-parameters + cue-sheet format against real hardware for free.
    /// </summary>
    public static CueTestResult TestCue(char driveLetter, DiscLayout layout)
    {
        using var dev = new SptiDevice(driveLetter);
        Verbose = true;   // this diagnostic's whole job is showing the real bytes — always on here.

        // The cue-sheet test necessarily uses Session-At-Once (Raw mode forbids a cue sheet).
        var modeResult = SetRawDaoWriteParameters(dev, layout, testWrite: false,
                                                  writeType: CdWriteType.SessionAtOnce);
        if (!modeResult.Success)
            return new CueTestResult(false, "MODE SELECT (raw DAO write parameters) rejected: " + modeResult.Describe(),
                                     0, 0);

        // ORDERING FIX (2026-08-25): cdrdao's GenericMMC::startDao() runs MODE SELECT → power
        // calibration (OPC) → GET NEXT WRITABLE ADDRESS → SEND CUE SHEET, in that order — every
        // one of this session's five earlier fixes changed cue-sheet/mode-page CONTENT but never
        // noticed this file skipped straight from MODE SELECT to SEND CUE SHEET with neither OPC
        // nor an NWA read in between. Some drives won't accept a cue sheet until OPC has run (the
        // ASC 0x26/0x00 "invalid field in parameter list" this test kept hitting is a plausible,
        // if imprecise, way for firmware to report "you skipped a required step" too, not only
        // "a byte is wrong"). Match cdrdao's order here before trusting cue-sheet content is the
        // problem again.
        try { dev.SendCommand(MmcCommands.SendOpc(true), Array.Empty<byte>(), SptiDataDirection.None, 90); } catch { }
        WaitReady(dev, maxSeconds: 90);
        ReadDriveNwa(dev);   // best-effort, like cdrdao's getNWA(NULL) — result unused here

        var cue = DaoCueSheet.Build(layout);
        if (Verbose)
        {
            var entries = DaoCueSheet.BuildEntries(layout);
            Console.Error.WriteLine($"[diag] cue sheet: {entries.Count} entries, {cue.Length} bytes");
            Console.Error.WriteLine("[diag] CTL/ADR  TNO  IDX  FORM  SCMS  MIN  SEC  FRM");
            foreach (var e in entries)
                Console.Error.WriteLine($"[diag]   0x{e.CtlAdr:X2}   0x{e.TrackNumber:X2} 0x{e.IndexOrPoint:X2}  0x{e.DataForm:X2}  0x{e.Scms:X2}   {e.Min:X2}   {e.Sec:X2}   {e.Frame:X2}");
            Console.Error.WriteLine($"[diag] SEND CUE SHEET raw bytes: {Hex(cue)}");
        }
        var r = dev.SendCommand(MmcCommands.SendCueSheet(cue.Length), cue, SptiDataDirection.Out, 30);
        if (Verbose)
        {
            Console.Error.WriteLine($"[diag] SEND CUE SHEET result: success={r.Success} " +
                $"scsiStatus=0x{r.ScsiStatus:X2} sense=key0x{r.SenseKey:X1}/asc0x{r.Asc:X2}/ascq0x{r.Ascq:X2}");
            Console.Error.WriteLine($"[diag] full sense buffer ({r.SenseData?.Length ?? 0} bytes): {(r.SenseData is null ? "(none)" : Hex(r.SenseData))}");
            Console.Error.WriteLine($"[diag] field pointer: {DecodeFieldPointer(r.SenseData)}");
        }
        if (r.Success)
            return new CueTestResult(true,
                "MODE SELECT + SEND CUE SHEET accepted — the drive is happy with our raw DAO setup.",
                cue.Length / 8, cue.Length);

        return new CueTestResult(false,
            "SEND CUE SHEET rejected: " + r.Describe() + ".  " + RealSense(dev),
            cue.Length / 8, cue.Length);
    }

    /// <summary>Issue REQUEST SENSE and return the raw (key, ASC, ASCQ) triple, or (-1,-1,-1) if
    /// the drive returns nothing. Used in the write loop to classify a WRITE(10) failure (e.g.
    /// 0x04/0x01 "becoming ready" → retry) without re-formatting a human string each time.</summary>
    private static (int key, int asc, int ascq) ReadSenseCodes(SptiDevice dev)
    {
        var sense = new byte[32];
        var rs = dev.SendCommand(MmcCommands.RequestSense(32), sense, SptiDataDirection.In, 10);
        if (!rs.Success || sense.Length < 14) return (-1, -1, -1);
        return (sense[2] & 0x0F, sense[12], sense[13]);
    }

    /// <summary>Explicitly pull the drive's sense after a failure (some drives return CHECK
    /// CONDITION with empty auto-sense, so REQUEST SENSE is the only way to see the real code).</summary>
    private static string RealSense(SptiDevice dev)
    {
        var sense = new byte[32];
        var rs = dev.SendCommand(MmcCommands.RequestSense(32), sense, SptiDataDirection.In, 10);
        if (!rs.Success || sense.Length < 14) return "(REQUEST SENSE returned nothing.)";
        int key = sense[2] & 0x0F, asc = sense[12], ascq = sense[13];
        string meaning = (asc, ascq) switch
        {
            (0x24, 0x00) => "invalid field in CDB",
            (0x26, 0x00) => "invalid field in parameter list (a cue-sheet byte is wrong)",
            (0x26, 0x01) => "parameter not supported",
            (0x26, 0x02) => "parameter value invalid",
            (0x20, 0x00) => "the drive does not support this command",
            (0x30, 0x00) => "incompatible medium",
            (0x64, 0x00) => "illegal mode for this track",
            (0x2C, 0x00) => "command sequence error (write parameters not set as the drive expects)",
            (0x21, 0x00) => "logical block address out of range (write start LBA disagrees with the drive's next-writable-address)",
            (0x21, 0x02) => "invalid address for write (raw write must start at the drive's next-writable-address)",
            (0x0C, 0x00) => "write error",
            (0x0C, 0x09) => "write error — loss of streaming (buffer underrun)",
            (0x27, 0x00) => "write protected",
            (0x63, 0x00) => "end of user area on this track",
            (0x24, _) => "invalid field in CDB (WRITE(10) shape or transfer length)",
            _ => "see the MMC sense tables",
        };
        return $"REQUEST SENSE → key 0x{key:X1}, ASC 0x{asc:X2}, ASCQ 0x{ascq:X2} ({meaning}).";
    }

    /// <summary>
    /// Full RAW DAO-96 burn using MMC <b>Write Type = Raw</b>: MODE SELECT (Write Type Raw, data
    /// block type 3 = raw + raw P-W sub-channel) → (OPC) → WRITE(10) the WHOLE disc image,
    /// lead-in included → CLOSE SESSION → SYNCHRONIZE CACHE. There is deliberately <b>no SEND CUE
    /// SHEET</b> — in Raw mode the TOC is carried in the sub-channel of the lead-in we write
    /// ourselves, and a cue sheet in Raw mode is a command-sequence error (ASC 0x2C). This is the
    /// write type that matches our byte-faithful <see cref="RawImageGenerator"/> output (raw main
    /// + raw interleaved P-W). The alternative Session-At-Once + cue-sheet path validated at setup
    /// on hardware, but the drive will not consume raw-P-W blocks in a cooked write mode — the
    /// WRITE(10) parks (win32=121, no sense), which is exactly what pointed here.
    /// </summary>
    /// <param name="writeSpeedMultiplier">Requested CD write speed (e.g. 4 for 4x).
    /// Null lets the drive choose against the loaded media's descriptor. Whatever
    /// happens, the outcome of the request is REPORTED — the first hardware
    /// round-trip failed with a max-speed burn whose speed request had been
    /// silently swallowed, and that silence is not allowed to recur.</param>
    public static void Burn(char driveLetter, DiscLayout layout, IProgress<BurnProgress>? progress = null,
                            bool simulate = false, int? writeSpeedMultiplier = 4)
    {
        using var dev = new SptiDevice(driveLetter);
        // Turned on for the active MODE SELECT (Raw) byte-3/8 fix (2026-08-25) so a --simulate run
        // shows the real bytes/result directly, the same way TestCue() already does — don't need a
        // second round trip to see whether cdrdao's byte-3/8 values actually got this accepted.
        Verbose = true;

        progress?.Report(new BurnProgress("prepare", 0.02,
            simulate ? "Setting RAW write parameters (SIMULATION — laser off)" : "Setting RAW write parameters"));
        var mode = SetRawDaoWriteParameters(dev, layout, testWrite: simulate, writeType: CdWriteType.Raw);
        if (!mode.Success) throw new IOException("MODE SELECT (Write Type = Raw) rejected: " + mode.Describe());

        // Set the write speed — some drives won't begin streaming write data until one is
        // set, which shows up as a WRITE(10) that never completes (timeout, no sense).
        // A rejected speed request means the drive will burn at ITS choice (often maximum),
        // which on aged media is how a disc ends up unreadable past half-radius — so the
        // request's fate is always surfaced, never swallowed.
        ushort kbs = writeSpeedMultiplier is int m and > 0
            ? (ushort)Math.Min(ushort.MaxValue, m * 176 + 2)
            : DiscForge.Core.Mmc.SetCdSpeed.Max;
        var speedResult = dev.SendCommand(MmcCommands.SetCdSpeed(0xFFFF, kbs),
            Array.Empty<byte>(), SptiDataDirection.None, 20);
        progress?.Report(new BurnProgress("prepare", 0.04,
            speedResult.Success
                ? $"Write speed: requested {(writeSpeedMultiplier is int mm and > 0 ? $"{mm}x ({kbs} KB/s)" : "drive maximum")} — accepted"
                : $"WARNING: write-speed request ({kbs} KB/s) REJECTED ({speedResult.Describe()}) — " +
                  "the drive will pick its own speed, possibly maximum. On aged media consider aborting."));

        // NO SEND CUE SHEET in Raw mode — the TOC lives in the lead-in sub-channel we write.

        // OPC (power calibration) — best effort. It leaves the drive "becoming ready"
        // (spinning up / calibrating), so we then wait for it to actually be ready before writing.
        try { dev.SendCommand(MmcCommands.SendOpc(true), Array.Empty<byte>(), SptiDataDirection.None, 90); } catch { }
        progress?.Report(new BurnProgress("prepare", 0.20, "Waiting for the drive to finish calibrating"));
        WaitReady(dev, maxSeconds: 90);

        // Ask the drive where the write should begin (READ TRACK INFORMATION) rather than assuming.
        // In Raw mode the next-writable-address should be the lead-in start (our generator's lead-in
        // begins at LBA −(22500+150) = −22650).
        //
        // SKIPPED-LEAD-IN BUG (2026-08-27, found after two full hardware burns that both reported
        // success yet read back as a completely blank disc on every drive tried — including a
        // second, unrelated drive, ruling out a read-back quirk). The code used to believe that a
        // non-deeply-negative NWA (this drive reports a flat 0, even AFTER Write Type = Raw mode
        // select succeeds — the "the mode changes the answer" assumption below turned out false on
        // real hardware) meant "the drive manages the lead-in itself", and would then SKIP writing
        // our composed lead-in entirely, sending only the program area. Nothing else ever supplies
        // a lead-in in this raw+no-cue-sheet design (see the class doc comment: no SEND CUE SHEET
        // in Raw mode, the lead-in sub-channel IS the TOC) — so on a drive that reports NWA=0, the
        // disc's actual physical lead-in was NEVER transmitted at all. The visible symptoms matched
        // exactly: a full, real burn (dye genuinely changed across the whole program area) that
        // every drive — including one that had just proven itself capable of fresh reads by reading
        // a different disc in between — reported back as blank, because the one place a TOC lives
        // was empty. Real cdrdao's own raw driver (GenericMMCraw::startDao) has no such branch at
        // all: it unconditionally writes its own full lead-in every time, using an ATIP-derived
        // start address, never trusting NWA to mean "skip it". Match that here: a genuinely useful
        // negative NWA (<= -151) is honoured as the drive's own authority on the start address;
        // anything else (0, not-valid, or the read failing outright) now falls back to composing
        // and sending our OWN full lead-in from the safe default start (-22650) instead of ever
        // skipping it — never assume some other mechanism already supplied a TOC.
        var (nwaOk, driveNwa, nwaDetail) = ReadDriveNwa(dev);
        long nwaSigned = nwaOk ? (int)driveNwa : 0;
        uint FirstWriteLba;
        int leadInSectors;
        long writeSectors;
        if (nwaSigned <= -151)
        {
            // Drive gave its own ATIP lead-in start: size the lead-in so the program lands at LBA 0
            // (lead-in spans NWA..−150), then write the WHOLE image (lead-in + program) from here.
            FirstWriteLba = driveNwa;
            leadInSectors = (int)(-150 - nwaSigned);
            writeSectors = RawImageGenerator.TotalSectors(layout, leadInSectors);
        }
        else
        {
            // No usable negative NWA. Three consecutive real burns on this drive all failed at the
            // SAME LBA (−22300, ~5 seconds of media time into a 1x write) — deterministic, not
            // hardware flakiness. −22650 is a FIXED GUESS at where the lead-in should start; it was
            // never checked against this disc's real recordable-area boundary. If the true boundary
            // (the end of the Power Calibration Area) sits closer to LBA 0 than −22650, writing our
            // guessed start puts thousands of sectors of the "lead-in" INSIDE the reserved PCA the
            // drive won't let anything write to — a hard medium error at a fixed offset is exactly
            // what that looks like. Read the disc's own ATIP (READ TOC/PMA/ATIP format 4) for its
            // real lead-in start — the same field cdrdao's raw driver uses for this exact purpose
            // (GenericMMCraw::getMultiSessionInfo → atipLeadinStart) — instead of trusting a fixed
            // number that was only ever a placeholder.
            var (atipOk, atipLba, atipDetail) = ReadAtipLeadInLba(dev);
            FirstWriteLba = atipOk ? atipLba
                                   : unchecked((uint)(-(RawImageGenerator.LeadInSectors + 150)));  // −22650 fallback
            leadInSectors = (int)(-150 - (int)FirstWriteLba);
            if (leadInSectors < 1) leadInSectors = RawImageGenerator.LeadInSectors;  // sane fallback
            writeSectors = RawImageGenerator.TotalSectors(layout, leadInSectors);
            progress?.Report(new BurnProgress("prepare", 0.045, "ATIP lead-in start: " + atipDetail));
        }
        progress?.Report(new BurnProgress("prepare", 0.05, "Next-writable-address: " + nwaDetail));

        string tmp = Path.Combine(Path.GetTempPath(), "df_sptiraw_" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            progress?.Report(new BurnProgress("prepare", 0.06,
                $"Composing raw image ({writeSectors:N0} sectors to write, {leadInSectors:N0}-sector lead-in)"));
            using (var img = File.Create(tmp))
                RawImageGenerator.Generate(layout, RawSubcodeForm.Interleaved96, img, leadInSectors: leadInSectors);

            progress?.Report(new BurnProgress("burn", 0.30,
                simulate ? "Simulating raw write (laser off)" : "Writing raw image (Write Type = Raw) — cannot be cancelled safely"));
            using var fs = File.OpenRead(tmp);
            fs.Position = 0;                                  // always the full image — lead-in included, never skipped

            const int chunkSectors = 25;                     // 25 × 2448 = 61,200 B (< 64 KiB)
            var buf = new byte[chunkSectors * RawSectorBytes];
            long written = 0;
            uint lba = FirstWriteLba;                         // start at the drive's reported NWA
            while (written < writeSectors)
            {
                int want = (int)Math.Min(chunkSectors, writeSectors - written);
                int bytes = want * RawSectorBytes;
                fs.ReadExactly(buf, 0, bytes);                // read the block once; retry only the write

                SptiResult w;
                int readyTries = 0;
                int timeoutTries = 0;
                while (true)
                {
                    // TIMEOUT FIX (2026-08-27, found live on a real lead-in write — the FIRST time
                    // this engine ever actually wrote one; see the skipped-lead-in bug above): a
                    // 40-second per-command timeout is too tight for this drive during lead-in
                    // writing at slow speed — it deterministically failed at the exact same LBA on
                    // two separate real burns with `win32=121` (ERROR_SEM_TIMEOUT) and NO sense data
                    // at all, meaning the OS gave up on the IOCTL before the drive ever answered, not
                    // that the drive reported a real error. Give it much more room (180s).
                    w = dev.SendCommand(MmcCommands.Write10(lba, (ushort)want), buf.AsSpan(0, bytes),
                                        SptiDataDirection.Out, 180);
                    if (w.Success) break;

                    // BUG FIX (2026-08-25, found live via --simulate): this used to re-query sense
                    // with a SEPARATE REQUEST SENSE (ReadSenseCodes(dev)) after the failure. On this
                    // drive that came back (0,0,0) every time — the auto-sense the drive returned
                    // WITH the failing WRITE(10) itself (visible in the SPTI result's own sense
                    // buffer, and in the exception text below) was "Not ready: ASC 0x04 ASCQ 0x08"
                    // (LONG WRITE IN PROGRESS — the drive is mid buffer-flush and wants a retry), but
                    // by the time a follow-up REQUEST SENSE CDB was issued the contingent-allegiance
                    // condition had already cleared, so it legitimately reported "no sense". Read the
                    // sense straight off THIS command's own SptiResult instead of re-asking the drive.
                    byte key = w.SenseKey, asc = w.Asc, ascq = w.Ascq;
                    // ASC 0x04 = "logical unit not ready" (0x01 = becoming ready, 0x08 = long write
                    // in progress, etc.): the drive is transiently busy. The spec-defined initiator
                    // response is to wait and reissue the SAME command, not to fail. Do that a
                    // bounded number of times before giving up.
                    if (asc == 0x04 && readyTries < 10)
                    {
                        readyTries++;
                        progress?.Report(new BurnProgress("burn", 0.30 + 0.6 * written / writeSectors,
                            $"drive becoming ready (key 0x{key:X1}, ASC 0x04/0x{ascq:X2}); waiting, retry {readyTries}/10"));
                        System.Threading.Thread.Sleep(2000);
                        continue;
                    }
                    // TIMEOUT RETRY (2026-08-27): a driver-level failure with NO sense data at all
                    // (IsDriverLevelFailure — ScsiStatus 0, a Win32 error, e.g. 121/ERROR_SEM_TIMEOUT)
                    // means the OS gave up waiting, not that the drive reported a real failure — the
                    // spec-defined "not ready, reissue the same command" response applies just as much
                    // here as it does to an explicit ASC 0x04. Retry the SAME write a bounded number
                    // of times before giving up, same as the not-ready path above.
                    if (w.IsDriverLevelFailure && timeoutTries < 5)
                    {
                        timeoutTries++;
                        progress?.Report(new BurnProgress("burn", 0.30 + 0.6 * written / writeSectors,
                            $"drive did not answer in time (win32={w.Win32Error}); retrying, attempt {timeoutTries}/5"));
                        System.Threading.Thread.Sleep(2000);
                        continue;
                    }
                    // CLEANUP FIX (2026-08-25): cdrdao's abortDao() flushes the drive's write
                    // cache whenever a DAO write fails partway through — a half-open DAO session
                    // left completely unacknowledged, as this used to do (the exception just
                    // propagated straight out), can leave the drive's OWN internal negotiation
                    // state stuck in a way that a later fresh MODE SELECT — for an entirely new
                    // attempt, on a different or freshly reset disc — then rejects, looking like a
                    // content bug when it's really leftover state from the LAST failure never being
                    // acknowledged to the drive. Best-effort; failure here must never mask the real
                    // WRITE(10) failure being thrown below.
                    try { dev.SendCommand(MmcCommands.SynchronizeCache(), Array.Empty<byte>(), SptiDataDirection.None, 60); } catch { }

                    throw new IOException($"WRITE(10) failed at LBA {(int)lba} ({want} blocks × {RawSectorBytes} B, {bytes} B): "
                                          + w.Describe()
                                          + $"  [win32={w.Win32Error} scsiStatus=0x{w.ScsiStatus:X2} senseLen={w.SenseData?.Length ?? 0}]"
                                          + $"  auto-sense → key 0x{key:X1}, ASC 0x{asc:X2}, ASCQ 0x{ascq:X2}"
                                          + (readyTries > 0 ? $" (gave up after {readyTries} not-ready retries)." : "."));
                }

                lba += (uint)want;
                written += want;
                if ((written & 0x3FF) < chunkSectors)
                    progress?.Report(new BurnProgress("burn", 0.30 + 0.6 * written / writeSectors,
                        $"{(simulate ? "Simulating" : "Writing")} {written:N0}/{writeSectors:N0} sectors"));
            }

            // Finalise. In Write Type = Raw the whole disc (lead-in + program) is laid down as one
            // continuous raw stream, so the cooked CLOSE TRACK/SESSION does not apply — this drive
            // reports 2C/04 "current program area is empty" for it even though the raw data is on
            // the disc. SYNCHRONIZE CACHE is the real finaliser (flush the write); CLOSE is then
            // attempted best-effort (some drives finalise the lead-out on it) but its failure is
            // never fatal, since the raw stream is already complete.
            progress?.Report(new BurnProgress("finalize", 0.92, "Flushing write cache (SYNCHRONIZE CACHE)"));
            var sync = dev.SendCommand(MmcCommands.SynchronizeCache(), Array.Empty<byte>(), SptiDataDirection.None, 300);
            if (!sync.Success)
                progress?.Report(new BurnProgress("finalize", 0.95, "note: SYNCHRONIZE CACHE returned " + sync.Describe()));

            progress?.Report(new BurnProgress("finalize", 0.96, "Finalising (best-effort CLOSE SESSION)"));
            var close = dev.SendCommand(MmcCommands.CloseTrackSession(0x02), Array.Empty<byte>(), SptiDataDirection.None, 300);
            if (!close.Success)
                progress?.Report(new BurnProgress("finalize", 0.98,
                    "note: CLOSE SESSION not applicable in raw mode (" + close.Describe() +
                    ") — the raw stream is already complete; the disc is written."));

            progress?.Report(new BurnProgress("finalize", 1.0,
                simulate ? "SIMULATION complete — the full raw write path ran with the laser off."
                         : "RAW burn complete (raw DAO)"));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>
    /// Read the drive's real next-writable-address via READ TRACK INFORMATION (track 1). This is
    /// the ground truth for where WRITE(10) must start — the value that replaces the -150 guess
    /// that put the write 4 billion sectors out of range. Returns the NWA as an unsigned LBA plus
    /// a human-readable detail; ok=false (with the reason) if the drive won't answer or flags the
    /// NWA invalid, in which case the caller falls back to LBA 0.
    /// </summary>
    private static (bool ok, uint nwa, string detail) ReadDriveNwa(SptiDevice dev)
    {
        var buf = new byte[40];
        var r = dev.SendCommand(MmcCommands.ReadTrackInformation(1, 1, 40), buf, SptiDataDirection.In, 20);
        if (!r.Success)
            return (false, 0, "READ TRACK INFORMATION failed (" + r.Describe() + ") — falling back to LBA 0");

        bool nwaValid = (buf[7] & 0x01) != 0;                // byte 7 bit 0 = NWA_V
        long nwa = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(12, 4));   // signed
        if (!nwaValid)
            return (false, 0, $"drive reports NWA not valid (raw {nwa}) — falling back to LBA 0");

        // A NEGATIVE NWA is expected and correct here. Once the drive is in raw-DAO write mode
        // with our cue sheet loaded, the next writable address is the track-1 pregap at LBA -150
        // (MSF 00:00:00); as an unsigned WRITE(10) field that is 0xFFFFFF6A. The drive is the
        // authority on where the sequential DAO write must begin, so we honour its value verbatim
        // rather than second-guessing the sign. (Standalone `writeinfo` reports 0 because that is
        // the default-mode NWA, read before the raw-DAO write parameters are set — the mode
        // changes the answer.)
        uint lba = unchecked((uint)nwa);
        return (true, lba, $"drive reports NWA = {nwa} (0x{lba:X8})");
    }

    /// <summary>
    /// Read the disc's own ATIP lead-in start (READ TOC/PMA/ATIP, format 4) and convert it to the
    /// negative WRITE(10) LBA our raw stream should start at — the same field cdrdao's raw driver
    /// uses for this exact purpose (GenericMMCraw::getMultiSessionInfo → atipLeadinStart), instead
    /// of trusting a fixed guess. ATIP's M/S/F fields are plain binary (not BCD), measured from the
    /// disc's absolute MSF origin; converting to the negative-LBA-before-track-1 convention this
    /// engine uses elsewhere is `(M*60+S)*75+F - 150 - 450000` (450000 = the 100:00:00 MSF wrap;
    /// 150 = the standard 2-second pregap constant baked into all CD LBA math).
    /// </summary>
    private static (bool ok, uint lba, string detail) ReadAtipLeadInLba(SptiDevice dev)
    {
        var buf = new byte[32];
        var r = dev.SendCommand(MmcCommands.ReadTocFormat(MmcCommands.TocFormat.Atip, 32),
                                buf, SptiDataDirection.In, 15);
        if (!r.Success)
            return (false, 0, "READ TOC/PMA/ATIP (format 4) failed (" + r.Describe() + ") — falling back to the fixed default");

        if (buf.Length < 11 || (buf[8] == 0 && buf[9] == 0 && buf[10] == 0))
            return (false, 0, "ATIP lead-in start reads all-zero (pressed media, or the drive didn't answer meaningfully) — falling back to the fixed default");

        int min = buf[8], sec = buf[9], frame = buf[10];
        long frames = ((long)min * 60 + sec) * 75 + frame;
        long lbaSigned = frames - 150 - 450_000;
        if (lbaSigned > -151)
            return (false, 0, $"ATIP lead-in start {min:00}:{sec:00}:{frame:00} converts to a non-negative/too-small LBA ({lbaSigned}) — falling back to the fixed default");

        uint lba = unchecked((uint)lbaSigned);
        return (true, lba, $"disc's own ATIP lead-in start = {min:00}:{sec:00}:{frame:00} → LBA {lbaSigned} (0x{lba:X8})");
    }

    /// <summary>Poll TEST UNIT READY until the drive reports ready (it sits "becoming ready"
    /// while it spins up / calibrates after OPC).</summary>
    private static void WaitReady(SptiDevice dev, int maxSeconds)
    {
        for (int i = 0; i < maxSeconds * 2; i++)
        {
            var r = dev.SendCommand(MmcCommands.TestUnitReady(), Array.Empty<byte>(), SptiDataDirection.None, 5);
            if (r.Success) return;                       // ready
            System.Threading.Thread.Sleep(500);
        }
    }

    private static SptiResult SetRawDaoWriteParameters(SptiDevice dev, DiscLayout layout,
                                                       bool testWrite, CdWriteType writeType)
    {
        // Data block type 3 = raw main + raw interleaved P-W sub-channel (2448 B/sector). The
        // matching write type is Raw (3): the host provides the WHOLE disc, lead-in included, and
        // NO cue sheet. Session-At-Once (2) is a cooked mode where the drive generates the
        // sub-channel/lead-in from a cue sheet — it accepts the setup but will not consume raw-P-W
        // blocks at write time (the WRITE(10) parks).
        //
        // Data block type 0 (not 3) for Session-At-Once: cdrdao's GenericMMC::setWriteParameters
        // (dao/GenericMMC.cc) sets it to 0 (plain raw 2352, no host-supplied sub-channel) for SAO
        // — its own comment reads "Data Block Type: raw data, block size: 2352 (I think not used
        // for session at once writing)" — and only uses type 3 for CD-TEXT lead-in writing, which
        // DiscForge doesn't do here. Raw (the real full-disc write in Burn()) still needs type 3.
        byte dataBlockType = writeType == CdWriteType.Raw ? DataBlockRawPw : (byte)0;

        // READ-MODIFY-WRITE, not build-from-scratch. This is the other structural difference from
        // cdrdao that earlier attempts here missed: cdrdao's setWriteParameters starts from
        // getModePage() — the drive's OWN current Write Parameters page — and flips only the
        // specific bits it cares about (write type, test-write, data block type, session format),
        // leaving everything else (vendor/reserved fields, buffer hints, and notably the Track
        // Mode nibble in byte 3) exactly as the drive reported. DiscForge was building the whole
        // 52-byte page from a blank record instead, which zeroes fields cdrdao never touches and
        // unconditionally overwrites Track Mode to the first track's control nibble — a value the
        // drive was never asked whether it wanted overridden. A real drive rejected SEND CUE SHEET
        // (ASC 0x26/0x00) through three content/DataBlockType fixes already; this is the next most
        // concrete, source-grounded difference from a reference implementation known to work on
        // this exact drive (ImgBurn/SAO succeeded against it — see docs/NEXT.md).
        // BUFFER-SIZE FIX (2026-08-25, found by re-reading this function, not by guessing):
        // the reply is an 8-byte MODE SENSE(10) header, then the block-descriptor bytes (commonly
        // 8 for a CD-ROM device), then the 2+50 = 52-byte page itself — 8+8+52 = 68 bytes, over
        // the OLD 64-byte buffer. The bounds check below (`pageStart + total <= senseBuf.Length`)
        // then silently fell back to a blank default page instead of the drive's real one, on
        // EVERY call, on every drive that returns a block descriptor — nobody noticed because the
        // fallback page still let MODE SELECT through in five earlier attempts (a blank page can
        // still be structurally acceptable), so this never surfaced as the visible failure. Fixed
        // by giving the reply room to actually fit.
        var senseBuf = new byte[192];
        var sense = dev.SendCommand(MmcCommands.ModeSense10(0x05, (ushort)senseBuf.Length),
                                    senseBuf, SptiDataDirection.In, 20);
        byte[] mp;
        bool usedFallback;
        if (sense.Success)
        {
            // MODE SENSE(10) response: 8-byte header (bytes 6..7 = block descriptor length),
            // then that many descriptor bytes, then the page itself (byte0 = PS|PageCode,
            // byte1 = page length N, N further bytes).
            int blockDescLen = (senseBuf[6] << 8) | senseBuf[7];
            int pageStart = 8 + blockDescLen;
            int pageLen = pageStart < senseBuf.Length ? senseBuf[pageStart + 1] : 0;
            int total = 2 + pageLen;
            if (pageStart + total <= senseBuf.Length && pageLen > 0)
            {
                mp = new byte[total];
                Array.Copy(senseBuf, pageStart, mp, 0, total);
                usedFallback = false;
            }
            else
            {
                mp = new WriteParametersPage().Build();   // drive's answer didn't parse — fall back
                usedFallback = true;
            }
        }
        else
        {
            mp = new WriteParametersPage().Build();        // MODE SENSE unsupported — fall back
            usedFallback = true;
        }

        if (Verbose)
        {
            Console.Error.WriteLine($"[diag] MODE SENSE(0x05): success={sense.Success} " +
                $"scsiStatus=0x{sense.ScsiStatus:X2} sense=key0x{sense.SenseKey:X1}/asc0x{sense.Asc:X2}/ascq0x{sense.Ascq:X2} " +
                $"usedFallbackPage={usedFallback}");
            Console.Error.WriteLine($"[diag] MODE SENSE raw reply: {Hex(senseBuf)}");
            Console.Error.WriteLine($"[diag] write-parameters page BEFORE modification: {Hex(mp)}");
        }

        mp[0] &= 0x7F;                                      // clear PS
        mp[2] &= 0xE0;                                       // clear BUFE stays; write-type+test-write bits cleared
        mp[2] |= (byte)((byte)writeType & 0x0F);
        if (testWrite) mp[2] |= 1 << 4;
        // TRACK MODE FIX (2026-08-25, found from the actual bytes, not more source-reading): the
        // "leave Track Mode exactly as the drive reported it" policy (matching cdrdao's own code)
        // silently assumes the drive's reported value is sane for the disc actually loaded. A real
        // capture on this drive showed it reporting Track Mode = 0x5 (binary 0101: bit3=0 → AUDIO
        // track, bit0=1 → four-channel, bit2=1 → copy permitted) — nonsense for this PS1 disc,
        // whose first track is DATA. cdrdao gets away with blind preservation because ITS callers
        // apparently always see a sane value; this drive/session doesn't. DiscForge already knows
        // the real first-track control nibble from the layout (DaoCueSheet.CtlAdr(tracks[0]) uses
        // exactly this) — use it here too instead of trusting stale/wrong drive state.
        // RAW-MODE BYTE 3/8 FIX (2026-08-25, from a real cdrdao build on this exact drive): a real
        // cdrdao (dao/GenericMMCraw.cc, GenericMMCraw::setWriteParameters) got its own MODE SELECT
        // + SEND CUE SHEET accepted on this SH-224DB using the "generic-mmc-raw" driver — proof
        // this drive DOES support raw writing, just not the way TestCue()'s Session-At-Once path
        // asks for it. Its raw driver does NOT preserve or compute Track Mode into byte 3 at all —
        // it hardcodes byte 3 to 0 (no multi-session pointer, no FP/Copy, no Track Mode nibble) —
        // and hardcodes byte 8 (session format) to 0 as well, unconditionally, even for this XA
        // disc. That's a real, structural difference from the Track-Mode-preservation policy below,
        // which was reasoned out for the SAO path (TestCue) and may simply not apply to Raw: in
        // raw+P-W mode the drive gets track/mode information from the P-W sub-channel bytes
        // themselves, not from this mode page. Branch on write type so TestCue()'s SAO behavior is
        // unchanged and only Burn()'s Raw path adopts cdrdao's proven-on-this-drive byte 3/8 values.
        if (writeType == CdWriteType.Raw)
        {
            mp[3] = 0;
            mp[4] = (byte)((mp[4] & 0xF0) | (dataBlockType & 0x0F));
            mp[8] = 0;
        }
        else
        {
            byte firstTrackControl = (byte)layout.Tracks[0].Control;
            // MASK FIX: 0x3F keeps bits 5:0, which INCLUDES the Track Mode nibble (3:0) — ORing the
            // real value on top of stale bits that are already a superset does nothing (verified
            // live: the drive's stale 0x05 has bits 0 and 2 set, and Data=0x04 only adds bit 2,
            // already set, so the byte silently stayed 0x05). Must clear bits 5:0 down to just
            // FP/Copy (5:4) first.
            mp[3] = (byte)((mp[3] & 0x30) | (firstTrackControl & 0x0F));   // clear multi-session
                                                                            // (7:6) AND Track Mode
                                                                            // (3:0); set Track Mode
                                                                            // from the layout
            mp[4] = (byte)((mp[4] & 0xF0) | (dataBlockType & 0x0F));
            mp[8] = layout.DiscType;                         // session format: 0x00 CD-DA/CD-ROM, 0x20 CD-ROM XA
        }

        var paramList = MmcCommands.ModeParameterList(mp);

        if (Verbose)
        {
            Console.Error.WriteLine($"[diag] write-parameters page AFTER modification (writeType={writeType}, testWrite={testWrite}, dataBlockType={dataBlockType}, sessionFormat=0x{layout.DiscType:X2}): {Hex(mp)}");
            Console.Error.WriteLine($"[diag] MODE SELECT(10) parameter list ({paramList.Length} bytes): {Hex(paramList)}");
        }

        var result = dev.SendCommand(MmcCommands.ModeSelect10((ushort)paramList.Length), paramList,
                               SptiDataDirection.Out, 30);

        if (Verbose)
        {
            Console.Error.WriteLine($"[diag] MODE SELECT(10) result: success={result.Success} " +
                $"scsiStatus=0x{result.ScsiStatus:X2} sense=key0x{result.SenseKey:X1}/asc0x{result.Asc:X2}/ascq0x{result.Ascq:X2}");
            if (!result.Success)
                Console.Error.WriteLine($"[diag] field pointer: {DecodeFieldPointer(result.SenseData)}");
        }

        return result;
    }

    /// <summary>Set true (by <see cref="TestCue"/>) to print raw MODE SENSE/MODE SELECT bytes to
    /// stderr — the real byte-level capture this file's history kept saying was needed, without
    /// any external tooling.</summary>
    public static bool Verbose { get; set; }

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    /// <summary>Decode the SCSI Sense Key Specific field (fixed-format sense, bytes 15–17): when
    /// SKSV (byte15 bit7) is set and the sense key is ILLEGAL REQUEST, this is a FIELD POINTER —
    /// the exact byte offset (bytes 16–17, big-endian, low 11 bits) of the invalid field in the
    /// command (C/D=1, byte15 bit6) or the parameter data (C/D=0) the drive just rejected, plus
    /// which BIT within that byte (BPV set, bits 2:0) if it's bit-granular. This is the real,
    /// drive-reported "which byte is wrong" this investigation kept saying it needed — no sniffer
    /// required, it was already coming back in the sense data, just never decoded.</summary>
    private static string DecodeFieldPointer(byte[]? sense)
    {
        if (sense is not { Length: > 17 }) return "(sense buffer too short to carry one)";
        byte b15 = sense[15];
        if ((b15 & 0x80) == 0) return "(SKSV not set — drive didn't report a field pointer)";
        bool cd = (b15 & 0x40) != 0;              // 1 = error in the CDB, 0 = error in parameter data (our cue sheet)
        bool bpv = (b15 & 0x08) != 0;              // bit pointer valid
        int fieldByte = (sense[16] << 8) | sense[17];
        string where = cd ? "the CDB" : "the parameter data (our cue-sheet payload)";
        string bit = bpv ? $", bit {b15 & 0x07}" : "";
        string entryHint = !cd && fieldByte >= 0
            ? $"  → cue-sheet byte {fieldByte} is entry #{fieldByte / 8 + 1}, offset {fieldByte % 8} within it "
              + "(0=CTL/ADR,1=TNO,2=IDX,3=FORM,4=SCMS,5=MIN,6=SEC,7=FRAME)"
            : "";
        return $"SKSV set — byte {fieldByte}{bit} of {where} is what the drive rejected.{entryHint}";
    }
}
