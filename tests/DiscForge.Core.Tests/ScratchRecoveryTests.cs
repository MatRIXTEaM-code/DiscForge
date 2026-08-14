using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class ScratchRecoveryTests
{
    [Fact]
    public void A_tiny_audio_burst_is_corrected_by_circ()
    {
        Assert.Equal(RecoveryOutlook.Corrected, ScratchRecovery.AssessAudioFrames(12));
    }

    [Fact]
    public void A_moderate_audio_burst_is_concealed()
    {
        Assert.Equal(RecoveryOutlook.Concealed, ScratchRecovery.AssessAudioFrames(ScratchRecovery.FramesPerSector));
    }

    [Fact]
    public void A_large_audio_burst_is_audibly_lost()
    {
        Assert.Equal(RecoveryOutlook.Lost, ScratchRecovery.AssessAudioFrames(10 * ScratchRecovery.FramesPerSector));
    }

    [Fact]
    public void A_data_scratch_is_data_recoverable_never_concealed()
    {
        var a = ScratchRecovery.Assess(1000, 1005, ErrorPatternKind.Scratch, isAudio: false);
        Assert.Equal(RecoveryOutlook.DataRecoverable, a.Outlook);
        Assert.Contains("reconstruct", a.Action);
    }

    [Fact]
    public void A_small_data_scratch_mentions_single_read_ecc()
    {
        var a = ScratchRecovery.Assess(500, 501, ErrorPatternKind.Scratch, isAudio: false);
        Assert.Equal(RecoveryOutlook.DataRecoverable, a.Outlook);
        Assert.Contains("RSPC", a.Action);
    }

    [Fact]
    public void A_deliberate_pattern_is_left_alone()
    {
        var a = ScratchRecovery.Assess(2000, 2050, ErrorPatternKind.DeliberatePattern, isAudio: false);
        Assert.Equal(RecoveryOutlook.Preserve, a.Outlook);
    }

    [Fact]
    public void Advise_maps_an_error_pattern_report_using_track_types()
    {
        // A data scratch (LBA 0..40) and an audio scratch (LBA 100000..100200).
        var bad = new bool[200000];
        for (int i = 0; i < 41; i++) bad[i] = true;
        for (int i = 100000; i <= 100200; i++) bad[i] = true;
        var pattern = ErrorPatternForensics.Classify(bad);

        // LBAs below 50000 are data; above are audio.
        var report = ScratchRecovery.Advise(pattern, lba => lba >= 50000);

        Assert.Contains(report.Advisories, a => !a.IsAudio && a.Outlook == RecoveryOutlook.DataRecoverable);
        Assert.Contains(report.Advisories, a => a.IsAudio &&
            (a.Outlook == RecoveryOutlook.Concealed || a.Outlook == RecoveryOutlook.Lost));
    }
}
