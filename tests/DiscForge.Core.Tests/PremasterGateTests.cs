using DiscForge.Core.Convert;
using DiscForge.Core.Cue;
using DiscForge.Core.Forensics;
using DiscForge.Core.Raw;
using Xunit;

namespace DiscForge.Core.Tests;

public class PremasterGateTests
{
    // ---- builders -----------------------------------------------------------

    private static CueTrack CueAudio(int n, long start, string? isrc = null) => new()
    {
        Number = n, Type = CueTrackType.Audio, File = "disc.bin",
        Indices = new[] { new CueIndex(1, Msf.FromSectors(start)) }, Isrc = isrc,
    };

    private static CueTrack CueData(int n, long start) => new()
    {
        Number = n, Type = CueTrackType.Mode1_2352, File = "disc.bin",
        Indices = new[] { new CueIndex(1, Msf.FromSectors(start)) },
    };

    private static DiscModelTrack AudioTrack(int n, int sectors, int pregap = 0) => new()
    {
        Number = n, Type = CueTrackType.Audio, SectorSize = 2352,
        PregapSectors = pregap, Data = new byte[sectors * 2352],
    };

    private static DiscModelTrack DataTrack(int n, int sectors, int corruptSector = -1)
    {
        var data = new byte[sectors * 2352];
        for (int s = 0; s < sectors; s++)
        {
            var user = new byte[2048];
            for (int i = 0; i < user.Length; i++) user[i] = (byte)(s + i);
            RawSectorBuilder.BuildMode1(user, Msf.FromSectors(150 + s), data.AsSpan(s * 2352, 2352));
        }
        if (corruptSector >= 0) data[corruptSector * 2352 + 100] ^= 0xFF;   // break EDC on one sector
        return new DiscModelTrack
        {
            Number = n, Type = CueTrackType.Mode1_2352, SectorSize = 2352,
            Data = data,
        };
    }

    private static CueSheet Cue(IEnumerable<CueTrack> t, string? catalog = null)
        => new() { Tracks = t.ToList(), Catalog = catalog };
    private static DiscModel Model(params DiscModelTrack[] t)
        => new() { Tracks = t.ToList() };

    // ---- tests --------------------------------------------------------------

    [Fact]
    public void A_clean_audio_disc_is_master_ready()
    {
        var cue = Cue(new[]
        {
            CueAudio(1, 0, "USRC17607839"),
            CueAudio(2, 20_000, "USRC17607840"),
        }, catalog: "0123456789012");
        var model = Model(AudioTrack(1, 20_000), AudioTrack(2, 20_000));

        var r = PremasterGate.Check(cue, model);
        Assert.True(r.ReadyToMaster, PremasterGate.Render(r));
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void A_data_track_with_a_bad_sector_is_blocked()
    {
        var cue = Cue(new[] { CueData(1, 0) });
        var model = Model(DataTrack(1, 4, corruptSector: 2));

        var r = PremasterGate.Check(cue, model);
        Assert.False(r.ReadyToMaster);
        Assert.Contains(r.Findings, x => x.Severity == LintSeverity.Error && x.Message.Contains("EDC/ECC"));
    }

    [Fact]
    public void A_clean_data_track_passes_integrity()
    {
        var cue = Cue(new[] { CueData(1, 0) });
        var model = Model(DataTrack(1, 4));   // all sectors valid
        var r = PremasterGate.Check(cue, model);
        Assert.DoesNotContain(r.Findings, x => x.Message.Contains("EDC/ECC"));
    }

    [Fact]
    public void An_over_80_minute_program_is_blocked()
    {
        // Use a large pregap to simulate a long program without allocating the audio.
        var cue = Cue(new[] { CueAudio(1, 0, "USRC17607839") }, catalog: "0123456789012");
        var model = Model(AudioTrack(1, 400, pregap: PremasterGate.Capacity80Min + 1));

        var r = PremasterGate.Check(cue, model);
        Assert.False(r.ReadyToMaster);
        Assert.Contains(r.Findings, x => x.Where == "capacity" && x.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Over_74_minutes_is_a_warning_not_a_blocker()
    {
        var cue = Cue(new[] { CueAudio(1, 0, "USRC17607839") }, catalog: "0123456789012");
        // Between 74:00 and 80:00.
        var model = Model(AudioTrack(1, 400, pregap: PremasterGate.Capacity74Min + 1000));

        var r = PremasterGate.Check(cue, model);
        Assert.True(r.ReadyToMaster);
        Assert.Contains(r.Findings, x => x.Where == "capacity" && x.Severity == LintSeverity.Warning);
    }

    [Fact]
    public void Missing_mcn_and_isrc_are_advisory_only()
    {
        var cue = Cue(new[] { CueAudio(1, 0), CueAudio(2, 20_000) });   // no catalog, no ISRC
        var model = Model(AudioTrack(1, 20_000), AudioTrack(2, 20_000));

        var r = PremasterGate.Check(cue, model);
        Assert.True(r.ReadyToMaster);   // hygiene items never block
        Assert.Contains(r.Findings, x => x.Where == "hygiene" && x.Message.Contains("MCN"));
        Assert.Contains(r.Findings, x => x.Where == "hygiene" && x.Message.Contains("ISRC"));
    }

    [Fact]
    public void Structural_problems_propagate_from_the_audit()
    {
        var cue = Cue(new[] { CueAudio(1, 0), CueAudio(3, 20_000) });   // skips track 2
        var r = PremasterGate.Check(cue);
        Assert.False(r.ReadyToMaster);
        Assert.Contains(r.Findings, x => x.Where.StartsWith("structure/") && x.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Cue_only_check_notes_that_the_image_was_not_supplied()
    {
        var cue = Cue(new[] { CueAudio(1, 0, "USRC17607839") }, catalog: "0123456789012");
        var r = PremasterGate.Check(cue);
        Assert.Contains(r.Findings, x => x.Where == "capacity" && x.Severity == LintSeverity.Info);
    }

    [Fact]
    public void Runtime_is_reported_from_the_model()
    {
        var cue = Cue(new[] { CueAudio(1, 0, "USRC17607839") }, catalog: "0123456789012");
        var model = Model(AudioTrack(1, 45_000));   // 45000 sectors = 10:00:00
        var r = PremasterGate.Check(cue, model);
        Assert.Equal(45_000, r.ProgramSectors);
        Assert.Equal(new Msf(10, 0, 0), r.Runtime);
    }
}
