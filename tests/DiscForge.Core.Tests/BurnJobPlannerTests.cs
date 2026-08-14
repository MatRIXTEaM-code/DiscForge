using DiscForge.Core.Burning;
using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;
using Xunit;

namespace DiscForge.Core.Tests;

public class BurnJobPlannerTests
{
    // --- helpers -------------------------------------------------------------

    private static DriveCapabilities Drive(bool cdWrite = true, bool raw = false) => new()
    {
        DevicePath = @"\\.\D:",
        Vendor = "TEST",
        Model = raw ? "PLEXWRITER" : "MODERN-LG",
        FirmwareRevision = "1.0",
        CdRead = true,
        CdWrite = cdWrite,
        DvdRead = true,
        DvdWrite = true,
        BdRead = false,
        BdWrite = false,
        RawDao96 = raw,
    };

    private static CdiImage SingleDataImage() => BuildImage(
        (CdiTrackMode.Mode1, CdiSectorSize.S2048, 100u));

    private static CdiImage AudioDataImage() => BuildImage(
        (CdiTrackMode.Audio, CdiSectorSize.S2352, 50u),
        (CdiTrackMode.Mode1, CdiSectorSize.S2048, 100u));

    /// <summary>A plain audio CD with the customary 150-sector gaps.</summary>
    private static CdiImage AudioOnlyImage() => BuildImage(150u,
        (CdiTrackMode.Audio, CdiSectorSize.S2352, 50u),
        (CdiTrackMode.Audio, CdiSectorSize.S2352, 60u));

    /// <summary>A gapless mix: track 2 runs straight on from track 1.</summary>
    private static CdiImage GaplessAudioImage()
    {
        var inputs = new[]
        {
            new CdiWriter.TrackInput
            {
                Mode = CdiTrackMode.Audio, SectorSize = CdiSectorSize.S2352,
                PregapSectors = 150, LengthSectors = 50, StartLba = 0,
                Filename = "T1.BIN", Data = new byte[2352 * (150 + 50)],
            },
            new CdiWriter.TrackInput
            {
                Mode = CdiTrackMode.Audio, SectorSize = CdiSectorSize.S2352,
                PregapSectors = 0, LengthSectors = 60, StartLba = 200,
                Filename = "T2.BIN", Data = new byte[2352 * 60],
            },
        };
        var ms = new MemoryStream();
        CdiWriter.Write(ms, CdiVersion.V35, new[] { (IReadOnlyList<CdiWriter.TrackInput>)inputs });
        ms.Position = 0;
        return CdiParser.Parse(ms);
    }

    private static CdiImage BuildImage(params (CdiTrackMode mode, CdiSectorSize size, uint len)[] tracks)
        => BuildImage(0u, tracks);

    private static CdiImage BuildImage(uint pregap,
        params (CdiTrackMode mode, CdiSectorSize size, uint len)[] tracks)
    {
        var inputs = tracks.Select((t, i) => new CdiWriter.TrackInput
        {
            Mode = t.mode,
            SectorSize = t.size,
            PregapSectors = pregap,
            LengthSectors = t.len,
            StartLba = 0,
            Filename = $"T{i}.BIN",
            Data = new byte[(int)t.size * ((int)t.len + (int)pregap)],
        }).ToArray();

        var ms = new MemoryStream();
        CdiWriter.Write(ms, CdiVersion.V35, new[] { (IReadOnlyList<CdiWriter.TrackInput>)inputs });
        ms.Position = 0;
        return CdiParser.Parse(ms);
    }

    // --- actions -------------------------------------------------------------

    [Fact]
    public void No_actions_selected_is_refused()
    {
        var job = new BurnJob
        {
            Destination = new BurnDestination.Drive(Drive()),
            Test = false, Write = false, Verify = false,
        };
        Assert.Throws<BurnNotSupportedException>(() => BurnJobPlanner.Plan(SingleDataImage(), job));
    }

    [Fact]
    public void Test_runs_once_then_write_and_verify_per_copy()
    {
        var job = new BurnJob
        {
            Destination = new BurnDestination.Drive(Drive()),
            Test = true, Write = true, Verify = true, Copies = 2,
        };
        var plan = BurnJobPlanner.Plan(SingleDataImage(), job);

        Assert.Equal(BurnStepKind.Test, plan.Steps[0].Kind);
        Assert.Single(plan.Steps, s => s.Kind == BurnStepKind.Test);
        Assert.Equal(2, plan.Steps.Count(s => s.Kind == BurnStepKind.Write));
        Assert.Equal(2, plan.Steps.Count(s => s.Kind == BurnStepKind.Verify));
        Assert.Equal(2, plan.TotalCopies);

        // Within a copy, Write must precede Verify.
        var steps = plan.Steps.ToList();
        int w1 = steps.FindIndex(s => s.Kind == BurnStepKind.Write && s.CopyNumber == 1);
        int v1 = steps.FindIndex(s => s.Kind == BurnStepKind.Verify && s.CopyNumber == 1);
        Assert.True(w1 < v1);
    }

    [Fact]
    public void Zero_copies_is_refused()
    {
        var job = new BurnJob { Destination = new BurnDestination.Drive(Drive()), Copies = 0 };
        Assert.Throws<BurnNotSupportedException>(() => BurnJobPlanner.Plan(SingleDataImage(), job));
    }

    // --- method choice -------------------------------------------------------

    [Fact]
    public void Auto_uses_the_planners_choice()
    {
        var job = new BurnJob { Destination = new BurnDestination.Drive(Drive()) };
        var plan = BurnJobPlanner.Plan(SingleDataImage(), job);
        Assert.All(plan.Steps, s => Assert.Equal(BurnMethod.Imapi2Data, s.Method));
    }

    [Fact]
    public void Raw_requested_on_incapable_drive_is_refused_naming_the_drive()
    {
        var job = new BurnJob
        {
            Destination = new BurnDestination.Drive(Drive(raw: false)),
            Method = BurnMethodChoice.RawDao96,
        };
        var ex = Assert.Throws<BurnNotSupportedException>(
            () => BurnJobPlanner.Plan(SingleDataImage(), job));
        Assert.Contains("RAW", ex.Message);
        Assert.Contains("MODERN-LG", ex.Message);
    }

    [Fact]
    public void Raw_requested_on_capable_drive_is_honoured()
    {
        var job = new BurnJob
        {
            Destination = new BurnDestination.Drive(Drive(raw: true)),
            Method = BurnMethodChoice.RawDao96,
        };
        var plan = BurnJobPlanner.Plan(SingleDataImage(), job);
        Assert.All(plan.Steps, s => Assert.Equal(BurnMethod.RawDao96, s.Method));
    }

    [Fact]
    public void Tao_requested_for_a_mixed_mode_image_is_refused()
    {
        // Mixed mode genuinely cannot be written track-at-once.
        var job = new BurnJob
        {
            Destination = new BurnDestination.Drive(Drive(raw: true)),
            Method = BurnMethodChoice.Tao,
        };
        var ex = Assert.Throws<BurnNotSupportedException>(
            () => BurnJobPlanner.Plan(AudioDataImage(), job));
        Assert.Contains("TAO", ex.Message);
    }

    [Fact]
    public void A_plain_audio_cd_burns_track_at_once_on_a_drive_without_raw_dao()
    {
        // The correction that matters: IMAPI2's track-at-once path is how
        // Windows burns audio, and it works on ANY CD writer. Audio does NOT
        // require RAW DAO — only exact gaps do.
        var job = new BurnJob { Destination = new BurnDestination.Drive(Drive(raw: false)) };

        var plan = BurnJobPlanner.Plan(AudioOnlyImage(), job);

        Assert.All(plan.Steps, s => Assert.Equal(BurnMethod.Imapi2TrackAtOnce, s.Method));
        Assert.Contains(plan.Warnings, w => w.Contains("two-second gap"));
    }

    [Fact]
    public void Gapless_audio_needs_raw_dao_and_says_why()
    {
        // TAO always writes the standard gap, so a gapless mix can't survive it.
        var job = new BurnJob { Destination = new BurnDestination.Drive(Drive(raw: false)) };

        var ex = Assert.Throws<BurnNotSupportedException>(
            () => BurnJobPlanner.Plan(GaplessAudioImage(), job));

        Assert.Contains("non-standard gaps", ex.Message);
        Assert.Contains("two seconds", ex.Message);
    }

    [Fact]
    public void Gapless_audio_on_a_raw_capable_drive_uses_raw_dao()
    {
        var job = new BurnJob { Destination = new BurnDestination.Drive(Drive(raw: true)) };
        var plan = BurnJobPlanner.Plan(GaplessAudioImage(), job);
        Assert.All(plan.Steps, s => Assert.Equal(BurnMethod.RawDao96, s.Method));
    }

    [Fact]
    public void Tao_warns_about_run_in_run_out()
    {
        var job = new BurnJob
        {
            Destination = new BurnDestination.Drive(Drive()),
            Method = BurnMethodChoice.Tao,
        };
        var plan = BurnJobPlanner.Plan(SingleDataImage(), job);
        Assert.Contains(plan.Warnings, w => w.Contains("run-in"));
    }

    [Fact]
    public void Mixed_image_on_non_raw_drive_proceeds_with_a_caution()
    {
        // A mixed-mode image needs RAW DAO, and this drive doesn't advertise
        // RAW DAO-96. That used to be refused here; it no longer is, because
        // the mode-page bit under-reports what the drive can actually do — a
        // MATSHITA UJ8E2 reports False and writes RAW DAO correctly (verified
        // against a real SVCD burn).
        //
        // The planner therefore proceeds and says so plainly. The engine asks
        // the drive for real at PrepareMedia, so a drive that genuinely can't
        // write raw still fails before any media is touched — the refusal moved
        // to where the truth is known, rather than being guessed from a bit
        // that lies.
        var job = new BurnJob { Destination = new BurnDestination.Drive(Drive(raw: false)) };

        var plan = BurnJobPlanner.Plan(AudioDataImage(), job);

        Assert.Contains(plan.Warnings, w => w.Contains("RAW DAO-96"));
        Assert.All(plan.Steps, s => Assert.Equal(BurnMethod.RawDao96, s.Method));
    }

    // --- image file destination ---------------------------------------------

    [Fact]
    public void Image_file_write_then_verify()
    {
        var job = new BurnJob
        {
            Destination = new BurnDestination.ImageFile(@"C:\out\copy.cdi"),
            Write = true, Verify = true,
        };
        var plan = BurnJobPlanner.Plan(SingleDataImage(), job);

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(BurnStepKind.Write, plan.Steps[0].Kind);
        Assert.Equal(BurnStepKind.Verify, plan.Steps[1].Kind);
    }

    [Fact]
    public void Test_to_an_image_file_is_refused()
    {
        var job = new BurnJob
        {
            Destination = new BurnDestination.ImageFile(@"C:\out\copy.cdi"),
            Test = true, Write = true,
        };
        Assert.Throws<BurnNotSupportedException>(() => BurnJobPlanner.Plan(SingleDataImage(), job));
    }

    [Fact]
    public void Multiple_copies_to_an_image_file_is_refused()
    {
        var job = new BurnJob
        {
            Destination = new BurnDestination.ImageFile(@"C:\out\copy.cdi"),
            Copies = 3,
        };
        Assert.Throws<BurnNotSupportedException>(() => BurnJobPlanner.Plan(SingleDataImage(), job));
    }

    [Fact]
    public void Method_choice_is_noted_as_pointless_for_a_file_destination()
    {
        var job = new BurnJob
        {
            Destination = new BurnDestination.ImageFile(@"C:\out\copy.cdi"),
            Method = BurnMethodChoice.RawDao96,
        };
        var plan = BurnJobPlanner.Plan(SingleDataImage(), job);
        Assert.Contains(plan.Warnings, w => w.Contains("no effect"));
    }

    // --- several destinations at once (duplication) ---------------------------

    private static DriveCapabilities NamedDrive(string path, bool raw = false) =>
        Drive(raw: raw) with { DevicePath = path };

    [Fact]
    public void Plans_the_same_job_for_every_chosen_destination()
    {
        var job = new MultiBurnJob
        {
            Destinations = new BurnDestination[]
            {
                new BurnDestination.Drive(NamedDrive(@"\\.\D:")),
                new BurnDestination.Drive(NamedDrive(@"\\.\E:")),
                new BurnDestination.Drive(NamedDrive(@"\\.\F:")),
            },
            Write = true,
        };

        var plan = BurnJobPlanner.PlanAll(SingleDataImage(), job);

        Assert.Equal(3, plan.Destinations.Count);
        Assert.All(plan.Destinations, d => Assert.True(d.CanRun));
        Assert.All(plan.Destinations, d => Assert.Single(d.Steps));
    }

    [Fact]
    public void An_incapable_drive_is_refused_without_sinking_the_others()
    {
        // The point of per-destination planning: a drive that can't take a RAW
        // image bows out with a reason while the capable ones still run.
        var job = new MultiBurnJob
        {
            Destinations = new BurnDestination[]
            {
                new BurnDestination.Drive(NamedDrive(@"\\.\D:", raw: true)),
                new BurnDestination.Drive(NamedDrive(@"\\.\E:", raw: false)),
            },
            Method = BurnMethodChoice.RawDao96,
        };

        var plan = BurnJobPlanner.PlanAll(SingleDataImage(), job);

        Assert.True(plan.AnyRunnable);
        Assert.Single(plan.Runnable);
        var refused = Assert.Single(plan.Refused);
        Assert.Contains("RAW", refused.Refusal!);
        Assert.Contains(@"\\.\E:", refused.Label);
    }

    [Fact]
    public void A_job_no_destination_can_run_is_refused_listing_every_reason()
    {
        var job = new MultiBurnJob
        {
            Destinations = new BurnDestination[]
            {
                new BurnDestination.Drive(NamedDrive(@"\\.\D:", raw: false)),
                new BurnDestination.Drive(NamedDrive(@"\\.\E:", raw: false)),
            },
            Method = BurnMethodChoice.RawDao96,
        };

        var ex = Assert.Throws<BurnNotSupportedException>(
            () => BurnJobPlanner.PlanAll(SingleDataImage(), job));

        Assert.Contains(@"\\.\D:", ex.Message);
        Assert.Contains(@"\\.\E:", ex.Message);
    }

    [Fact]
    public void Drives_and_an_image_file_can_be_targets_of_one_job()
    {
        var job = new MultiBurnJob
        {
            Destinations = new BurnDestination[]
            {
                new BurnDestination.Drive(NamedDrive(@"\\.\D:")),
                new BurnDestination.ImageFile(@"C:\out\copy.cdi"),
            },
        };

        var plan = BurnJobPlanner.PlanAll(SingleDataImage(), job);

        Assert.Equal(2, plan.Runnable.Count());
        Assert.Single(plan.Destinations, d => d.IsImageFile);
    }

    [Fact]
    public void The_same_drive_twice_is_refused()
    {
        var job = new MultiBurnJob
        {
            Destinations = new BurnDestination[]
            {
                new BurnDestination.Drive(NamedDrive(@"\\.\D:")),
                new BurnDestination.Drive(NamedDrive(@"\\.\D:")),
            },
        };

        var ex = Assert.Throws<BurnNotSupportedException>(
            () => BurnJobPlanner.PlanAll(SingleDataImage(), job));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void The_same_image_file_twice_is_refused()
    {
        // Two writers racing for one file would corrupt it.
        var job = new MultiBurnJob
        {
            Destinations = new BurnDestination[]
            {
                new BurnDestination.ImageFile(@"C:\out\copy.cdi"),
                new BurnDestination.ImageFile(@"C:\out\copy.cdi"),
            },
        };

        var ex = Assert.Throws<BurnNotSupportedException>(
            () => BurnJobPlanner.PlanAll(SingleDataImage(), job));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void No_destinations_is_refused()
    {
        var job = new MultiBurnJob { Destinations = Array.Empty<BurnDestination>() };
        Assert.Throws<BurnNotSupportedException>(() => BurnJobPlanner.PlanAll(SingleDataImage(), job));
    }

    [Fact]
    public void Per_destination_copies_are_planned_independently()
    {
        var job = new MultiBurnJob
        {
            Destinations = new BurnDestination[]
            {
                new BurnDestination.Drive(NamedDrive(@"\\.\D:")),
                new BurnDestination.Drive(NamedDrive(@"\\.\E:")),
            },
            Write = true, Verify = true, Copies = 2,
        };

        var plan = BurnJobPlanner.PlanAll(SingleDataImage(), job);

        // Each drive burns 2 copies -> 6 discs' worth of work in total.
        Assert.All(plan.Runnable, d => Assert.Equal(2, d.TotalCopies));
        Assert.All(plan.Runnable, d => Assert.Equal(4, d.Steps.Count));   // W+V per copy
    }
}