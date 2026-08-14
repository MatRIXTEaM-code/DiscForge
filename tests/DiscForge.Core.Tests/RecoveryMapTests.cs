using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class RecoveryMapTests
{
    private static RecoveryReport Report(params LesionAdvisory[] a)
        => new() { Advisories = a.ToList() };

    private static LesionAdvisory Adv(int start, int end, bool isAudio, RecoveryOutlook o)
        => new(start, end, end - start + 1, isAudio, o, "test");

    [Fact]
    public void Paint_marks_only_the_damaged_span()
    {
        var r = Report(Adv(10, 12, isAudio: true, RecoveryOutlook.Lost));
        var cells = RecoveryMap.Paint(r, 20);

        Assert.Null(cells[9]);
        Assert.Equal(RecoveryOutlook.Lost, cells[10]);
        Assert.Equal(RecoveryOutlook.Lost, cells[12]);
        Assert.Null(cells[13]);
    }

    [Fact]
    public void Paint_clips_a_region_that_runs_past_the_disc()
    {
        var r = Report(Adv(8, 100, isAudio: false, RecoveryOutlook.DataRecoverable));
        var cells = RecoveryMap.Paint(r, 10);
        Assert.Equal(RecoveryOutlook.DataRecoverable, cells[9]);   // last sector painted, no overflow
        Assert.Equal(10, cells.Length);
    }

    [Fact]
    public void Overlap_keeps_the_more_attention_worthy_outlook()
    {
        // A corrected audio region and a preserve (protection) region overlap at sector 5.
        var r = Report(
            Adv(0, 6, isAudio: true, RecoveryOutlook.Corrected),
            Adv(5, 9, isAudio: false, RecoveryOutlook.Preserve));
        var cells = RecoveryMap.Paint(r, 10);

        Assert.Equal(RecoveryOutlook.Corrected, cells[0]);
        Assert.Equal(RecoveryOutlook.Preserve, cells[5]);   // preserve outranks corrected
        Assert.Equal(RecoveryOutlook.Preserve, cells[9]);
    }

    [Fact]
    public void Lost_outranks_everything_in_a_shared_cell()
    {
        var r = Report(
            Adv(0, 3, isAudio: false, RecoveryOutlook.DataRecoverable),
            Adv(2, 5, isAudio: true, RecoveryOutlook.Lost));
        var cells = RecoveryMap.Paint(r, 6);
        Assert.Equal(RecoveryOutlook.Lost, cells[2]);       // Lost (rank 5) beats DataRecoverable (rank 4)
    }

    [Fact]
    public void RenderSvg_produces_a_document_with_a_legend()
    {
        var r = Report(
            Adv(100, 140, isAudio: true, RecoveryOutlook.Concealed),
            Adv(500, 502, isAudio: false, RecoveryOutlook.DataRecoverable));
        string svg = RecoveryMap.RenderSvg(r, 1000, "Test disc");

        Assert.StartsWith("<svg", svg);
        Assert.EndsWith("</svg>\n", svg);
        Assert.Contains("Test disc", svg);
        Assert.Contains("clean", svg);
        Assert.Contains("concealed 1", svg);       // one concealed region
        Assert.Contains("data-recover 1", svg);    // one data-recoverable region
        Assert.DoesNotContain("preserve", svg);    // no preserve region present → its chip absent
    }

    [Fact]
    public void An_empty_report_renders_an_all_clean_map()
    {
        string svg = RecoveryMap.RenderSvg(Report(), 500, "Pristine");
        Assert.Contains("clean", svg);
        Assert.DoesNotContain("audibly-lost", svg);
    }

    [Fact]
    public void Large_disc_aggregates_and_notes_the_block_size()
    {
        var r = Report(Adv(1_000_000, 1_000_050, isAudio: true, RecoveryOutlook.Lost));
        string svg = RecoveryMap.RenderSvg(r, 3_000_000, "Big", maxCells: 4096);
        Assert.Contains("each cell =", svg);        // aggregation footnote present
        Assert.Contains("audibly-lost 1", svg);     // damage survived aggregation
    }
}
