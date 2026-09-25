// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

namespace DiscForge.Core.Mmc;

/// <summary>
/// The manual write knobs a burn can be configured with — DiscForge's answer to ImgBurn's Write
/// settings tab. One field per bit ImgBurn exposes there: write type (DAO/TAO/RAW/packet), the
/// test-write (laser-off simulation) bit, buffer-underrun-free recording (BURN-Proof/JustLink), link
/// size, whether to run OPC (power calibration) before writing, and the write speed.
/// </summary>
public sealed record BurnKnobs
{
    public CdWriteType WriteType { get; init; } = CdWriteType.SessionAtOnce;
    public bool TestWrite { get; init; }
    public bool BurnProof { get; init; }
    /// <summary>Null = LS_V (link-size-valid) left unset, drive picks its own default.</summary>
    public byte? LinkSize { get; init; }
    public bool RequestOpc { get; init; } = true;
    /// <summary>Null = drive maximum (<see cref="SetCdSpeed.Max"/>).</summary>
    public int? WriteSpeedMultiplier { get; init; }
    /// <summary>DVD+R/-R only: pre-reserve this many sectors of track before writing.</summary>
    public uint? ReserveTrackSectors { get; init; }
}

/// <summary>One step of a planned burn's SCSI command sequence.</summary>
public sealed record BurnCommandStep(string Name, byte Opcode, string Cdb, string Description);

/// <summary>
/// A burn's exact command sequence, computed WITHOUT touching a drive — every CDB in it comes from
/// the same pure, unit-tested builders (<see cref="WriteParametersPage"/>, <see cref="MmcCommands"/>,
/// <see cref="SetCdSpeed"/>) the live burn engines use, so what this shows is genuinely what would be
/// sent, not a separate approximation.
///
/// ImgBurn does not have an equivalent to this: it burns and gives you a log of what happened, but
/// there is no way to see, diff, or archive the exact planned command sequence for a given set of
/// write settings before a disc is ever in the drive. For a preservation tool that already carries a
/// signed chain-of-custody lineage (<c>DiscForge.Core.Preservation.DumpLineageLog</c>), a burn plan's
/// hash is a natural thing to fold into that record — proof of not just what was burned, but exactly
/// how it was asked to be burned, auditable and reproducible offline, on any machine, without hardware.
/// </summary>
public sealed record BurnCommandPlan
{
    public required BurnKnobs Knobs { get; init; }
    public required string WriteParametersPageHex { get; init; }
    public required IReadOnlyList<BurnCommandStep> Steps { get; init; }

    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Burn plan — write type {Knobs.WriteType}, " +
                      $"{(Knobs.TestWrite ? "TEST WRITE (laser off)" : "live write")}, " +
                      $"BURN-Proof {(Knobs.BurnProof ? "on" : "off")}, " +
                      $"link size {(Knobs.LinkSize is { } ls ? ls.ToString() : "drive default")}, " +
                      $"speed {(Knobs.WriteSpeedMultiplier is { } m ? $"{m}x" : "drive maximum")}");
        sb.AppendLine($"Write Parameters mode page (0x05): {WriteParametersPageHex}");
        foreach (var s in Steps)
            sb.AppendLine($"  {s.Name} (0x{s.Opcode:X2})  CDB={s.Cdb}  — {s.Description}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Computes a <see cref="BurnCommandPlan"/> from a set of <see cref="BurnKnobs"/> — pure,
/// offline, no device I/O. Mirrors the sequence the live SPTI burn engines issue (MODE SELECT the
/// write parameters, optionally reserve a track, request OPC, set the write speed, stream WRITE(10),
/// close the track/session) so the plan is a faithful preview, not a guess at what they do.</summary>
public static class BurnCommandPlanner
{
    public static BurnCommandPlan Plan(BurnKnobs knobs)
    {
        ArgumentNullException.ThrowIfNull(knobs);

        var page = new WriteParametersPage
        {
            WriteType = knobs.WriteType,
            TestWrite = knobs.TestWrite,
            BufferUnderrunFree = knobs.BurnProof,
            LinkSizeValid = knobs.LinkSize is not null,
            LinkSize = knobs.LinkSize ?? 0,
        };
        byte[] pageBytes = page.Build();
        byte[] paramList = MmcCommands.ModeParameterList(pageBytes);

        var steps = new List<BurnCommandStep>();

        var modeSelectCdb = MmcCommands.ModeSelect10((ushort)paramList.Length);
        steps.Add(new BurnCommandStep("MODE SELECT(10) — Write Parameters", modeSelectCdb[0], Hex(modeSelectCdb),
            $"write type={knobs.WriteType}, test-write={knobs.TestWrite}, BURN-Proof={knobs.BurnProof}, " +
            $"link size={(knobs.LinkSize is { } ls2 ? ls2.ToString() : "unset (LS_V=0)")}"));

        if (knobs.ReserveTrackSectors is uint sectors)
        {
            var reserveCdb = MmcCommands.ReserveTrack(sectors);
            steps.Add(new BurnCommandStep("RESERVE TRACK", reserveCdb[0], Hex(reserveCdb),
                $"pre-reserve {sectors:N0} sectors (DVD+R/-R incremental track)"));
        }

        ushort writeKbs = knobs.WriteSpeedMultiplier is int mult and > 0
            ? SetCdSpeed.KbsForMultiplier(mult)
            : SetCdSpeed.Max;
        var speedCdb = SetCdSpeed.BuildCdb(SetCdSpeed.Max, writeKbs);
        steps.Add(new BurnCommandStep("SET CD SPEED", speedCdb[0], Hex(speedCdb),
            knobs.WriteSpeedMultiplier is int mm ? $"write speed {mm}x ({writeKbs} KB/s), read speed drive maximum"
                                                   : "write speed drive maximum"));

        if (knobs.RequestOpc)
        {
            var opcCdb = MmcCommands.SendOpc(true);
            steps.Add(new BurnCommandStep("SEND OPC INFORMATION", opcCdb[0], Hex(opcCdb),
                "optimum power calibration (DoOpc=1) before streaming write data"));
        }

        steps.Add(new BurnCommandStep("WRITE(10)", 0x2A, "(one per chunk, see the image)",
            "streams the image's sectors at the block size the Write Parameters page's Data Block Type selected"));

        if (knobs.WriteType is CdWriteType.SessionAtOnce or CdWriteType.Raw)
        {
            var closeCdb = MmcCommands.CloseTrackSession(0x02);
            steps.Add(new BurnCommandStep("CLOSE TRACK/SESSION", closeCdb[0], Hex(closeCdb),
                "close function=0x02 (close session/finalise disc)"));
        }

        return new BurnCommandPlan
        {
            Knobs = knobs,
            WriteParametersPageHex = Hex(pageBytes),
            Steps = steps,
        };
    }

    private static string Hex(byte[] b) => System.Convert.ToHexString(b);
}
