using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class DiscRotTriageTests
{
    private static readonly System.DateTimeOffset Epoch = new(2024, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
    private static System.DateTimeOffset Year(double y) => Epoch.AddDays(365.25 * y);

    // A scan whose peak interval has the given C1/C2/CU, padded with quiet intervals so the
    // average stays realistically low.
    private static ErrorScan Scan(string id, System.DateTimeOffset t, int maxC1, int c2 = 0, int cu = 0)
    {
        var samples = new List<ScanSample> { new(maxC1, c2, cu) };
        for (int i = 0; i < 99; i++) samples.Add(new(0, 0, 0));
        return ErrorScan.FromSamples(id, t, samples);
    }

    [Fact]
    public void From_samples_computes_the_summary_statistics()
    {
        var s = ErrorScan.FromSamples("disc", Epoch, new List<ScanSample>
        {
            new(10, 0, 0), new(30, 2, 0), new(5, 0, 0),
        });
        Assert.Equal(3, s.SampleCount);
        Assert.Equal(30, s.MaxC1);
        Assert.Equal(45, s.TotalC1);
        Assert.Equal(15.0, s.AvgC1, 3);
        Assert.Equal(2, s.MaxC2);
        Assert.Equal(2, s.TotalC2);
        Assert.Equal(0, s.TotalCu);
    }

    [Theory]
    [InlineData(15, 0, 0, DiscHealthGrade.Pristine)]
    [InlineData(30, 0, 0, DiscHealthGrade.Good)]
    [InlineData(70, 0, 0, DiscHealthGrade.Fair)]
    [InlineData(150, 0, 0, DiscHealthGrade.Poor)]
    [InlineData(250, 0, 0, DiscHealthGrade.Failing)]   // over Red Book BLER
    [InlineData(10, 30, 0, DiscHealthGrade.Poor)]      // some C2
    [InlineData(10, 100, 0, DiscHealthGrade.Failing)]  // heavy C2
    [InlineData(10, 0, 5, DiscHealthGrade.Failing)]    // uncorrectable
    public void Grade_uses_the_standard_thresholds(int maxC1, int c2, int cu, DiscHealthGrade expected)
    {
        Assert.Equal(expected, Scan("d", Epoch, maxC1, c2, cu).Grade);
    }

    [Fact]
    public void A_single_scan_has_no_trend()
    {
        var f = DiscRotTriage.Forecast(new[] { Scan("d", Epoch, 30) });
        Assert.Equal(1, f.ScanCount);
        Assert.Null(f.DaysToCritical);
        Assert.Contains("No trend yet", f.Assessment);
    }

    [Fact]
    public void Rising_bler_projects_a_failure_date()
    {
        // 100 → 200 peak BLER over one year: +100/yr, 20 to go to the 220 ceiling → ~0.2 yr.
        var f = DiscRotTriage.Forecast(new[]
        {
            Scan("d", Year(0), 100),
            Scan("d", Year(1), 200),
        });
        Assert.True(f.BlerPerYear > 90 && f.BlerPerYear < 110, $"slope {f.BlerPerYear}");
        Assert.NotNull(f.DaysToCritical);
        Assert.True(f.DaysToCritical < 120, $"days {f.DaysToCritical}");
        Assert.Equal(RotUrgency.Urgent, f.Urgency);
    }

    [Fact]
    public void An_already_uncorrectable_disc_is_critical_now()
    {
        var f = DiscRotTriage.Forecast(new[]
        {
            Scan("d", Year(0), 40),
            Scan("d", Year(1), 40, cu: 3),
        });
        Assert.True(f.AlreadyCritical);
        Assert.Equal(RotUrgency.Critical, f.Urgency);
        Assert.Null(f.DaysToCritical);
        Assert.Contains("immediately", f.Assessment);
    }

    [Fact]
    public void A_stable_healthy_disc_is_not_urgent()
    {
        var f = DiscRotTriage.Forecast(new[]
        {
            Scan("d", Year(0), 18),
            Scan("d", Year(1), 18),
            Scan("d", Year(2), 18),
        });
        Assert.Equal(RotUrgency.None, f.Urgency);
        Assert.Null(f.DaysToCritical);
        Assert.Equal(DiscHealthGrade.Pristine, f.CurrentGrade);
    }

    [Fact]
    public void Emerging_c2_pulls_the_failure_date_in()
    {
        // BLER barely rising, but C2 climbing from 0 — the C2 onset is the nearer threshold.
        var f = DiscRotTriage.Forecast(new[]
        {
            Scan("d", Year(0), 40, c2: 0),
            Scan("d", Year(1), 45, c2: 0),
        });
        // No C2 yet and BLER slope small → distant; now compare with a C2-rising disc.
        var g = DiscRotTriage.Forecast(new[]
        {
            Scan("e", Year(0), 40, c2: 0),
            Scan("e", Year(1), 45, c2: 8),   // C2 emerging fast
        });
        Assert.True(g.C2PerYear > 0);
        Assert.NotNull(g.DaysToCritical);
        Assert.True((g.DaysToCritical ?? double.MaxValue) < (f.DaysToCritical ?? double.MaxValue));
    }

    [Fact]
    public void Prioritize_orders_by_urgency_then_soonest()
    {
        var dying = new[] { Scan("DYING", Year(0), 40), Scan("DYING", Year(1), 40, cu: 2) };     // Critical
        var urgent = new[] { Scan("URGENT", Year(0), 100), Scan("URGENT", Year(1), 200) };       // Urgent ~0.2yr
        var stable = new[] { Scan("STABLE", Year(0), 15), Scan("STABLE", Year(1), 15) };          // None

        var order = DiscRotTriage.Prioritize(new[] { stable, urgent, dying });

        Assert.Equal("DYING", order[0].DiscId);
        Assert.Equal("URGENT", order[1].DiscId);
        Assert.Equal("STABLE", order[2].DiscId);
    }

    [Fact]
    public void Prioritize_skips_empty_histories_and_renders()
    {
        var order = DiscRotTriage.Prioritize(new[]
        {
            new List<ErrorScan>(),                                   // empty — skipped
            new List<ErrorScan> { Scan("X", Year(0), 30) },
        });
        Assert.Single(order);
        string text = DiscRotTriage.Render(order);
        Assert.Contains("dump order", text);
        Assert.Contains("X", text);
    }

    [Fact]
    public void Forecast_requires_at_least_one_scan()
    {
        bool threw = false;
        try { DiscRotTriage.Forecast(System.Array.Empty<ErrorScan>()); }
        catch (System.ArgumentException) { threw = true; }
        Assert.True(threw);
    }
}
