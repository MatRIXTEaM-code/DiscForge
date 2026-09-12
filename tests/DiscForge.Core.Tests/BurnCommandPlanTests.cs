// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Mmc;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>Tests for the offline burn-plan preview: the exact SCSI/MMC command sequence a set of
/// write knobs would produce, computed without touching a drive.</summary>
public class BurnCommandPlanTests
{
    [Fact]
    public void Default_knobs_plan_a_session_at_once_burn_with_OPC_and_a_session_close()
    {
        var plan = BurnCommandPlanner.Plan(new BurnKnobs());
        Assert.Contains(plan.Steps, s => s.Name.StartsWith("MODE SELECT"));
        Assert.Contains(plan.Steps, s => s.Name == "SEND OPC INFORMATION");
        Assert.Contains(plan.Steps, s => s.Name == "CLOSE TRACK/SESSION");
        Assert.DoesNotContain(plan.Steps, s => s.Name == "RESERVE TRACK");
    }

    [Fact]
    public void The_write_parameters_page_reflects_every_requested_knob()
    {
        var plan = BurnCommandPlanner.Plan(new BurnKnobs
        {
            WriteType = CdWriteType.Raw,
            TestWrite = true,
            BurnProof = true,
            LinkSize = 7,
        });
        var pageBytes = System.Convert.FromHexString(plan.WriteParametersPageHex);
        Assert.Equal(0x05, pageBytes[0]);           // page code
        Assert.Equal(3, pageBytes[2] & 0x0F);        // write type = Raw (3)
        Assert.NotEqual(0, pageBytes[2] & (1 << 4)); // test-write bit set
        Assert.NotEqual(0, pageBytes[2] & (1 << 6)); // BUFE (BURN-Proof) bit set
        Assert.NotEqual(0, pageBytes[2] & (1 << 5)); // LS_V set
        Assert.Equal(7, pageBytes[5]);                // link size
    }

    [Fact]
    public void No_test_write_or_burn_proof_leaves_those_bits_clear()
    {
        var plan = BurnCommandPlanner.Plan(new BurnKnobs { WriteType = CdWriteType.TrackAtOnce });
        var pageBytes = System.Convert.FromHexString(plan.WriteParametersPageHex);
        Assert.Equal(0, pageBytes[2] & (1 << 4));
        Assert.Equal(0, pageBytes[2] & (1 << 6));
        Assert.Equal(0, pageBytes[2] & (1 << 5));
    }

    [Fact]
    public void RequestOpc_false_omits_the_SEND_OPC_step()
    {
        var plan = BurnCommandPlanner.Plan(new BurnKnobs { RequestOpc = false });
        Assert.DoesNotContain(plan.Steps, s => s.Name == "SEND OPC INFORMATION");
    }

    [Fact]
    public void A_reserve_track_request_adds_the_RESERVE_TRACK_step_with_the_right_sector_count()
    {
        var plan = BurnCommandPlanner.Plan(new BurnKnobs { ReserveTrackSectors = 12345 });
        var step = Assert.Single(plan.Steps, s => s.Name == "RESERVE TRACK");
        var cdb = System.Convert.FromHexString(step.Cdb);
        Assert.Equal(0x53, cdb[0]);
        uint sectors = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(5));
        Assert.Equal(12345u, sectors);
    }

    [Fact]
    public void A_write_speed_multiplier_is_reflected_in_the_SET_CD_SPEED_CDB()
    {
        var plan = BurnCommandPlanner.Plan(new BurnKnobs { WriteSpeedMultiplier = 4 });
        var step = Assert.Single(plan.Steps, s => s.Name == "SET CD SPEED");
        var cdb = System.Convert.FromHexString(step.Cdb);
        ushort writeKbs = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(4));
        Assert.Equal(SetCdSpeed.KbsForMultiplier(4), writeKbs);
    }

    [Fact]
    public void Omitting_the_speed_requests_drive_maximum()
    {
        var plan = BurnCommandPlanner.Plan(new BurnKnobs());
        var step = Assert.Single(plan.Steps, s => s.Name == "SET CD SPEED");
        var cdb = System.Convert.FromHexString(step.Cdb);
        ushort writeKbs = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(4));
        Assert.Equal(SetCdSpeed.Max, writeKbs);
    }

    [Fact]
    public void Track_at_once_does_not_plan_a_session_close()
    {
        var plan = BurnCommandPlanner.Plan(new BurnKnobs { WriteType = CdWriteType.TrackAtOnce });
        Assert.DoesNotContain(plan.Steps, s => s.Name == "CLOSE TRACK/SESSION");
    }

    [Fact]
    public void Render_includes_the_headline_knob_summary()
    {
        var plan = BurnCommandPlanner.Plan(new BurnKnobs { WriteType = CdWriteType.Raw, BurnProof = true });
        var text = plan.Render();
        Assert.Contains("Raw", text);
        Assert.Contains("BURN-Proof on", text);
    }
}
