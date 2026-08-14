// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Devices;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// The per-drive profile consolidates advertised capabilities and reports the empirical fidelity
/// probes honestly. The test that matters: a property the drive only CLAIMS (C2 pointers) must not
/// masquerade as measured, and a probe that needs a disc we don't have (C2 accuracy, overread,
/// cache-defeat, read offset) must render as unprobed/undetermined — never a fabricated value.
/// </summary>
public class DriveProfileTests
{
    private static DriveCapabilities Plextor() => new()
    {
        DevicePath = @"\\.\D:",
        Vendor = "PLEXTOR",
        Model = "PX-W5224A",
        FirmwareRevision = "1.03",
        CdRead = true,
        CdWrite = true,
        TrackAtOnce = true,
        DiscAtOnce = true,
        RawDao96 = true,
        RawReadSubchannel = true,
        C2ErrorReporting = true,
        BufferUnderrunProtection = false,
    };

    [Fact]
    public void Advertised_capabilities_carry_over_and_empirical_probes_stay_honest()
    {
        var p = DriveProfile.FromCapabilities(Plextor());

        Assert.Equal("PLEXTOR", p.Vendor);
        Assert.True(p.CdWrite);
        Assert.True(p.RawDao96);
        Assert.True(p.RawReadSubchannel);

        // C2 pointers are ADVERTISED (mode page 2Ah) — not empirically verified.
        Assert.Equal(ProbeState.Advertised, p.C2ErrorPointers);
        // ...and advertising them says nothing about their ACCURACY, which needs a bad disc.
        Assert.Equal(ProbeState.NotDetermined, p.C2Accuracy);
        Assert.Equal(ProbeState.NotProbed, p.Overread);
        Assert.Equal(ProbeState.NotProbed, p.CacheDefeat);
        Assert.Null(p.ReadOffsetSamples);

        string text = p.Render();
        Assert.Contains("RAW-DAO-96 yes", text);
        Assert.Contains("not determined", text);   // C2 accuracy + read offset
        Assert.Contains("not probed", text);        // overread + cache defeat

        string json = p.Json();
        Assert.Contains("\"rawDao96\": true", json);
        Assert.Contains("\"c2Accuracy\": \"NotDetermined\"", json);
        Assert.Contains("\"readOffsetSamples\": null", json);
    }

    [Fact]
    public void No_C2_advertised_reads_no_and_a_supplied_offset_is_recorded()
    {
        var caps = Plextor() with { C2ErrorReporting = false };
        var p = DriveProfile.FromCapabilities(caps, readOffsetSamples: 30);

        Assert.Equal(ProbeState.No, p.C2ErrorPointers);
        Assert.Equal(ProbeState.No, p.C2Accuracy);   // can't be inaccurate if it isn't there
        Assert.Equal(30, p.ReadOffsetSamples);

        Assert.Contains("read offset (samples): 30", p.Render());
        Assert.Contains("\"readOffsetSamples\": 30", p.Json());
    }

    [Fact]
    public void A_supplied_overread_result_is_carried_into_the_profile()
    {
        var yes = DriveProfile.FromCapabilities(Plextor(), overread: ProbeState.Yes);
        Assert.Equal(ProbeState.Yes, yes.Overread);
        Assert.Contains("overread (lead-out)  : yes", yes.Render());

        var no = DriveProfile.FromCapabilities(Plextor(), overread: ProbeState.No);
        Assert.Equal(ProbeState.No, no.Overread);
        Assert.Contains("\"overread\": \"No\"", no.Json());

        // Default (probe not run) stays honestly unprobed.
        Assert.Equal(ProbeState.NotProbed, DriveProfile.FromCapabilities(Plextor()).Overread);
    }
}
