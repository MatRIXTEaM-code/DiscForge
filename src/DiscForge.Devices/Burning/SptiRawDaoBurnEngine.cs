// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

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
/// STATUS: built from the public MMC write model, validated incrementally on hardware. The
/// cheap, non-destructive first step is <see cref="TestCue"/> — it does the MODE SELECT and
/// SEND CUE SHEET only, so the drive validates our write parameters + cue-sheet format (and
/// returns sense data if they're wrong) WITHOUT writing a disc. The full <see cref="Burn"/>
/// write path (write start address, chunking, finalise) is refined once the cue sheet is
/// accepted.
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

        // The cue-sheet test necessarily uses Session-At-Once (Raw mode forbids a cue sheet).
        var modeResult = SetRawDaoWriteParameters(dev, layout, testWrite: false,
                                                  writeType: CdWriteType.SessionAtOnce);
        if (!modeResult.Success)
            return new CueTestResult(false, "MODE SELECT (raw DAO write parameters) rejected: " + modeResult.Describe(),
                                     0, 0);

        var cue = DaoCueSheet.Build(layout);
        var r = dev.SendCommand(MmcCommands.SendCueSheet(cue.Length), cue, SptiDataDirection.Out, 30);
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
    public static void Burn(char driveLetter, DiscLayout layout, IProgress<BurnProgress>? progress = null,
                            bool simulate = false)
    {
        using var dev = new SptiDevice(driveLetter);

        progress?.Report(new BurnProgress("prepare", 0.02,
            simulate ? "Setting RAW write parameters (SIMULATION — laser off)" : "Setting RAW write parameters"));
        var mode = SetRawDaoWriteParameters(dev, layout, testWrite: simulate, writeType: CdWriteType.Raw);
        if (!mode.Success) throw new IOException("MODE SELECT (Write Type = Raw) rejected: " + mode.Describe());

        // Set a write speed (4x) — some drives won't begin streaming write data until one is
        // set, which shows up as a WRITE(10) that never completes (timeout, no sense).
        try { dev.SendCommand(MmcCommands.SetCdSpeed(0xFFFF, 706), Array.Empty<byte>(), SptiDataDirection.None, 20); } catch { }

        // NO SEND CUE SHEET in Raw mode — the TOC lives in the lead-in sub-channel we write.

        // OPC (power calibration) — best effort. It leaves the drive "becoming ready"
        // (spinning up / calibrating), so we then wait for it to actually be ready before writing.
        try { dev.SendCommand(MmcCommands.SendOpc(true), Array.Empty<byte>(), SptiDataDirection.None, 90); } catch { }
        progress?.Report(new BurnProgress("prepare", 0.20, "Waiting for the drive to finish calibrating"));
        WaitReady(dev, maxSeconds: 90);

        // Ask the drive where the write should begin (READ TRACK INFORMATION) rather than assuming.
        // In Raw mode the next-writable-address should be the lead-in start (our generator's lead-in
        // begins at LBA −(22500+150) = −22650). If the drive instead reports the program pregap
        // (−150), it manages the lead-in itself and we must skip ours. Branch on the sign so the
        // drive stays the authority on the start address.
        var (nwaOk, driveNwa, nwaDetail) = ReadDriveNwa(dev);
        uint FirstWriteLba = nwaOk ? driveNwa
                                   : unchecked((uint)(-(RawImageGenerator.LeadInSectors + 150)));  // −22650
        progress?.Report(new BurnProgress("prepare", 0.05, "Next-writable-address: " + nwaDetail));

        long nwaSigned = (int)FirstWriteLba;                  // signed view of the start LBA
        int leadInSectors;
        long writeSectors;
        long skipBytes;
        if (nwaSigned <= -151)
        {
            // Drive gave its ATIP lead-in start: size the lead-in so the program lands at LBA 0
            // (lead-in spans NWA..−150), then write the WHOLE image (lead-in + program) from here.
            leadInSectors = (int)(-150 - nwaSigned);
            writeSectors = RawImageGenerator.TotalSectors(layout, leadInSectors);
            skipBytes = 0;
        }
        else
        {
            // Drive manages the lead-in (NWA at the program pregap −150 or later): write only the
            // program area, skipping our composed (default-length) lead-in.
            leadInSectors = RawImageGenerator.LeadInSectors;
            writeSectors = RawImageGenerator.ProgramSectors(layout);
            skipBytes = (long)leadInSectors * RawSectorBytes;
        }

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
            fs.Position = skipBytes;                          // lead-in included (0) or skipped

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
                while (true)
                {
                    w = dev.SendCommand(MmcCommands.Write10(lba, (ushort)want), buf.AsSpan(0, bytes),
                                        SptiDataDirection.Out, 40);
                    if (w.Success) break;

                    var (key, asc, ascq) = ReadSenseCodes(dev);
                    // ASC 0x04 = "logical unit not ready" (0x04/0x01 = becoming ready): the drive
                    // is still finishing write calibration / spin-up. The spec-defined initiator
                    // response is to wait and reissue the SAME command, not to fail. Do that a
                    // bounded number of times before giving up (mostly the first block only).
                    if (asc == 0x04 && readyTries < 6)
                    {
                        readyTries++;
                        progress?.Report(new BurnProgress("burn", 0.30 + 0.6 * written / writeSectors,
                            $"drive becoming ready (key 0x{key:X1}, ASC 0x04/0x{ascq:X2}); waiting, retry {readyTries}/6"));
                        System.Threading.Thread.Sleep(2000);
                        continue;
                    }
                    throw new IOException($"WRITE(10) failed at LBA {(int)lba} ({want} blocks × {RawSectorBytes} B, {bytes} B): "
                                          + w.Describe()
                                          + $"  [win32={w.Win32Error} scsiStatus=0x{w.ScsiStatus:X2} senseLen={w.SenseData?.Length ?? 0}]"
                                          + $"  REQUEST SENSE → key 0x{key:X1}, ASC 0x{asc:X2}, ASCQ 0x{ascq:X2}.");
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
        // blocks at write time (the WRITE(10) parks). Caller passes the write type explicitly.
        var page = new WriteParametersPage
        {
            WriteType = writeType,
            DataBlockType = DataBlockRawPw,          // raw + raw-interleaved P-W (2448 B/sector)
            TrackMode = (byte)((byte)layout.Tracks[0].Control & 0x0F),
            SessionFormat = layout.DiscType,         // 0x00 CD-DA/CD-ROM, 0x20 CD-ROM XA
            TestWrite = testWrite,                   // simulation (laser off) when true
        };
        var paramList = MmcCommands.ModeParameterList(page.Build());
        return dev.SendCommand(MmcCommands.ModeSelect10((ushort)paramList.Length), paramList,
                               SptiDataDirection.Out, 30);
    }
}
