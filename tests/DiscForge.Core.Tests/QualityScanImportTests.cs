// DiscForge — Copyright (C) 2026 MaTRIX TeAm.
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY;
// see the GNU General Public License (LICENSE at the repository root) for details.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class QualityScanImportTests
{
    private const string OdcDvd =
        "Opti Drive Control 1.80\n" +
        "Drive : PIONEER BD-RW BDR-209D\n" +
        "Media ID : MKM-003-00\n" +
        "Book type : DVD+R DL\n" +
        "Scan speed : 8x\n" +
        "Date : 2024-03-15\n" +
        "Position (MB),PI Errors,PI Failures,PO Failures\n" +
        "0,42,1,0\n" +
        "400,88,2,0\n" +
        "800,270,3,0\n" +
        "1200,150,1,0\n";

    [Fact]
    public void Reads_tool_medium_and_provenance_from_an_ODC_DVD_export()
    {
        var s = QualityScanImport.Parse(OdcDvd);
        Assert.Equal(ScanTool.OptiDriveControl, s.Tool);
        Assert.Equal(DiscFamily.Dvd, s.Family);
        Assert.Equal(4, s.Count);
        Assert.Equal("PIONEER BD-RW BDR-209D", s.Drive);
        Assert.Equal("MKM-003-00", s.MediaId);
        Assert.Equal("DVD+R DL", s.BookType);
        Assert.Equal("8x", s.WriteSpeed);
        Assert.Equal("2024-03-15", s.ScannedAt?.ToString("yyyy-MM-dd"));
        Assert.Equal("mb", s.PositionUnit);
    }

    [Fact]
    public void Computes_DVD_maxima_and_passes_a_within_guideline_disc()
    {
        var s = QualityScanImport.Parse(OdcDvd);
        Assert.Equal(270, s.MaxPie);
        Assert.Equal(3, s.MaxPif);
        Assert.Equal(0, s.TotalPof);
        Assert.True(s.Pass);
    }

    [Fact]
    public void Maps_DVD_tiers_onto_the_C1_C2_CU_samples_disc_rot_consumes()
    {
        var samples = QualityScanImport.Parse(OdcDvd).ToSamples();
        // PIE -> C1, PIF -> C2, POF -> CU (by correction tier).
        Assert.Equal(270, samples[2].C1);
        Assert.Equal(3, samples[2].C2);
        Assert.Equal(0, samples[2].Cu);
    }

    [Fact]
    public void A_DVD_with_outer_parity_failures_fails_and_maps_POF_to_uncorrectable()
    {
        var s = QualityScanImport.Parse("DVD\nTime,PIE,PIF,POF\n0,10,0,0\n1,300,20,5\n");
        Assert.Equal(DiscFamily.Dvd, s.Family);
        Assert.Equal(5, s.TotalPof);
        Assert.False(s.Pass);
        Assert.Equal("F", s.Grade());
        Assert.Equal(5, s.ToSamples()[1].Cu);
    }

    [Fact]
    public void Grades_a_within_spec_DVD_by_its_PIE_and_PIF()
    {
        // Pristine: PIF max 1, PIE max 30 -> A.
        Assert.Equal("A", QualityScanImport.Parse("DVD\nPIE,PIF\n30,1\n12,0\n").Grade());
        // Marginal but in spec: PIE 270, PIF 3 -> C.
        Assert.Equal("C", QualityScanImport.Parse("DVD\nPIE,PIF\n270,3\n").Grade());
        // A CD scan defers its grade to `bler`.
        Assert.Equal("-", QualityScanImport.Parse("Interval,C1,C2\n0,5,0\n").Grade());
    }

    [Fact]
    public void Parses_a_tab_separated_KProbe_CD_scan()
    {
        var s = QualityScanImport.Parse("KProbe 2.5.2\nDrive\t: LITE-ON LTR-52327S\nInterval\tC1\tC2\n0\t3\t0\n1\t12\t0\n2\t55\t0\n");
        Assert.Equal(ScanTool.KProbe, s.Tool);
        Assert.Equal(DiscFamily.Cd, s.Family);
        Assert.Equal(55, s.MaxC1);
        Assert.Equal(0, s.MaxC2);
        Assert.True(s.Pass);   // no C2 — the CD hard line; bler grades the rest
    }

    [Fact]
    public void Parses_Blu_ray_LDC_BIS_and_fails_when_BIS_exceeds_the_guideline()
    {
        var s = QualityScanImport.Parse("Opti Drive Control\nMB,LDC,BIS\n0,8,2\n100,20,18\n200,5,1\n");
        Assert.Equal(DiscFamily.BluRay, s.Family);
        Assert.Equal(20, s.MaxLdc);
        Assert.Equal(18, s.MaxBis);
        Assert.False(s.Pass);
    }

    [Fact]
    public void Reads_a_headerless_file_under_a_medium_hint_and_notes_the_assumption()
    {
        var s = QualityScanImport.Parse("0,5,0\n1,9,0\n2,3,0\n", DiscFamily.Cd);
        Assert.Equal(DiscFamily.Cd, s.Family);
        Assert.Equal(3, s.Count);
        Assert.Equal(9, s.MaxC1);
        Assert.False(string.IsNullOrEmpty(s.Assumption));
    }

    [Fact]
    public void Infers_DVD_from_short_PI_PIF_headers_with_no_position_column()
    {
        var s = QualityScanImport.Parse("PI,PIF\n40,1\n280,4\n");
        Assert.Equal(DiscFamily.Dvd, s.Family);
        Assert.Equal(280, s.MaxPie);
        Assert.Equal(4, s.MaxPif);
        Assert.True(s.Pass);   // boundary: PIE 280, PIF 4
    }

    [Fact]
    public void Recognises_a_Nero_DiscSpeed_signature_and_PO_Failures_column()
    {
        var s = QualityScanImport.Parse("Nero DiscSpeed disc quality\nPosition (MB),PI Errors,PI Failures,PO Failures\n0,20,0,0\n50,120,3,0\n");
        Assert.Equal(ScanTool.NeroDiscSpeed, s.Tool);
        Assert.Equal(DiscFamily.Dvd, s.Family);
        Assert.Equal("mb", s.PositionUnit);
        Assert.Equal(120, s.MaxPie);
    }

    [Fact]
    public void An_empty_or_junk_input_yields_no_rows_rather_than_throwing()
    {
        var s = QualityScanImport.Parse("this file has\nno tabular scan data\n");
        Assert.Equal(0, s.Count);
    }
}
