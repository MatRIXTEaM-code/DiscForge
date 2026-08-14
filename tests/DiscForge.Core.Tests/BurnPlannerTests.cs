using DiscForge.Core.Burning;
using DiscForge.Core.Cdi;
using DiscForge.Core.Devices;
using Xunit;

namespace DiscForge.Core.Tests;

public class BurnPlannerTests
{
    private static CdiTrack Track(int n, int session, CdiTrackMode mode, uint lba) => new()
    {
        Number = n, SessionIndex = session, Mode = mode,
        SectorSize = mode == CdiTrackMode.Audio ? CdiSectorSize.S2352 : CdiSectorSize.S2048,
        PregapSectors = 0, LengthSectors = 100, StartLba = lba, TotalSectors = 100, FileOffset = 0,
    };

    private static CdiImage Image(params (int session, CdiTrackMode mode)[] tracks)
    {
        var bySession = tracks
            .Select((t, i) => (t.session, track: Track(i + 1, t.session, t.mode, (uint)(i * 1000))))
            .GroupBy(x => x.session)
            .Select(g => new CdiSession { Index = g.Key, Tracks = g.Select(x => x.track).ToList() })
            .ToList();
        return new CdiImage
        {
            Version = CdiVersion.V35, FileLength = 1_000_000, DescriptorOffset = 900_000,
            Sessions = bySession,
        };
    }

    private static DriveCapabilities Drive(bool cd, bool dvd, bool bd, bool raw) => new()
    {
        DevicePath = "\\\\.\\E:", Vendor = "TEST", Model = "DRIVE", FirmwareRevision = "1.0",
        CdWrite = cd, DvdWrite = dvd, BdWrite = bd, RawDao96 = raw,
        CdRead = true, DvdRead = dvd, BdRead = bd,
    };

    [Fact]
    public void Single_data_track_on_any_writer_uses_imapi2()
    {
        var plan = BurnPlanner.Plan(Image((0, CdiTrackMode.Mode1)), Drive(cd: true, dvd: false, bd: false, raw: false));
        Assert.Equal(BurnMethod.Imapi2Data, plan.Method);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Single_data_track_on_bd_only_writer_uses_imapi2()
    {
        var plan = BurnPlanner.Plan(Image((0, CdiTrackMode.Mode2)), Drive(cd: false, dvd: false, bd: true, raw: false));
        Assert.Equal(BurnMethod.Imapi2Data, plan.Method);
    }

    [Fact]
    public void Mixed_mode_routes_to_raw_dao_with_or_without_the_mode_page_bit()
    {
        // Modern drive whose mode page doesn't advertise RAW: still planned as
        // RawDao96 — the bit under-reports what IMAPI2's raw writer can do (a
        // real TSSTcorp SE-208DB says False and negotiates fine), so the
        // planner warns and lets the engine ask the drive for real.
        var modern = Drive(cd: true, dvd: true, bd: true, raw: false);
        var warned = BurnPlanner.Plan(Image((0, CdiTrackMode.Audio), (0, CdiTrackMode.Mode1)), modern);
        Assert.Equal(BurnMethod.RawDao96, warned.Method);
        Assert.Contains(warned.Warnings, w => w.Contains("mode page"));

        // Drive advertising RAW: same plan, no capability warning.
        var vintage = Drive(cd: true, dvd: false, bd: false, raw: true);
        var plan = BurnPlanner.Plan(Image((0, CdiTrackMode.Audio), (0, CdiTrackMode.Mode1)), vintage);
        Assert.Equal(BurnMethod.RawDao96, plan.Method);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("mode page"));
    }

    [Fact]
    public void Multisession_is_refused_even_on_raw_drives()
    {
        // RAW DAO via the OS stack writes one closed session, so a
        // multisession image can't be reproduced faithfully — the planner
        // says so instead of burning a wrong disc.
        var vintage = Drive(cd: true, dvd: false, bd: false, raw: true);
        var ex = Assert.Throws<BurnNotSupportedException>(() =>
            BurnPlanner.Plan(Image((0, CdiTrackMode.Audio), (1, CdiTrackMode.Mode1)), vintage));
        Assert.Contains("session", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_writer_is_rejected()
    {
        var reader = Drive(cd: false, dvd: false, bd: false, raw: false);
        Assert.Throws<BurnNotSupportedException>(() =>
            BurnPlanner.Plan(Image((0, CdiTrackMode.Mode1)), reader));
    }

   [Fact]
    public void Multitrack_data_single_session_uses_raw_dao_with_a_caution()
    {
        // Two data tracks in one session still isn't the IMAPI2 single-track
        // path — it needs RAW DAO. The drive here doesn't advertise RAW DAO-96
        // in its mode page, and that used to be a refusal.
        //
        // It isn't any more, because the bit under-reports reality: a MATSHITA
        // UJ8E2 says False and writes RAW DAO perfectly well (verified against
        // a real SVCD burn). So the planner proceeds and warns, and the engine
        // asks the drive for real at PrepareMedia — failing there, before any
        // media is touched, if the answer is genuinely no.
        var modern = Drive(cd: true, dvd: true, bd: true, raw: false);

        var plan = BurnPlanner.Plan(
            Image((0, CdiTrackMode.Mode1), (0, CdiTrackMode.Mode2)), modern);

        Assert.Equal(BurnMethod.RawDao96, plan.Method);
        Assert.Contains(plan.Warnings, w => w.Contains("RAW DAO-96"));
    }
}