// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Forensics;
using Xunit;

namespace DiscForge.Core.Tests;

public class MediaMortalityTests
{
    // Reference implementation: the textbook weighted mean/variance over the flat observation list,
    // computed with no Welford/parallel tricks at all — what Observe/Merge must agree with regardless of
    // how the same observations were grouped or in what order they were folded/merged.
    private static (double Mean, double WeightedM2, double WeightSum) Reference(IEnumerable<CohortObservation> obs)
    {
        var list = obs.ToList();
        double wSum = list.Sum(o => o.Weight);
        double mean = list.Sum(o => o.Weight * o.GrowthPerYear) / wSum;
        double m2 = list.Sum(o => o.Weight * (o.GrowthPerYear - mean) * (o.GrowthPerYear - mean));
        return (mean, m2, wSum);
    }

    private static readonly CohortObservation[] Sample =
    {
        new(0.10, 2), new(0.15, 3), new(0.05, 2), new(0.20, 4), new(0.12, 3), new(0.30, 2),
    };

    [Fact]
    public void Observing_one_by_one_matches_the_textbook_weighted_mean_and_variance()
    {
        var model = new MediaMortalityModel();
        foreach (var o in Sample) MediaMortality.Observe(model, "cohort-a", o);

        var (mean, m2, wSum) = Reference(Sample);
        var s = model.Cohorts["cohort-a"];

        Assert.Equal(Sample.Length, s.ContributorCount);
        Assert.Equal(wSum, s.WeightSum, 9);
        Assert.Equal(mean, s.MeanGrowthPerYear, 9);
        Assert.Equal(m2, s.WeightedM2, 6);
    }

    [Fact]
    public void Merging_two_partial_models_gives_the_identical_result_as_one_combined_model()
    {
        var combined = new MediaMortalityModel();
        foreach (var o in Sample) MediaMortality.Observe(combined, "cohort-a", o);

        var partA = new MediaMortalityModel();
        var partB = new MediaMortalityModel();
        for (int i = 0; i < Sample.Length; i++)
            MediaMortality.Observe(i % 2 == 0 ? partA : partB, "cohort-a", Sample[i]);
        var merged = MediaMortality.Merge(partA, partB);

        var expected = combined.Cohorts["cohort-a"];
        var actual = merged.Cohorts["cohort-a"];
        Assert.Equal(expected.ContributorCount, actual.ContributorCount);
        Assert.Equal(expected.WeightSum, actual.WeightSum, 9);
        Assert.Equal(expected.MeanGrowthPerYear, actual.MeanGrowthPerYear, 9);
        Assert.Equal(expected.WeightedM2, actual.WeightedM2, 6);
    }

    [Fact]
    public void Merge_is_associative_regardless_of_how_observations_are_split_three_ways()
    {
        var m1 = new MediaMortalityModel();
        var m2 = new MediaMortalityModel();
        var m3 = new MediaMortalityModel();
        for (int i = 0; i < Sample.Length; i++)
        {
            var target = i % 3 == 0 ? m1 : i % 3 == 1 ? m2 : m3;
            MediaMortality.Observe(target, "cohort-a", Sample[i]);
        }

        var leftFirst = MediaMortality.Merge(MediaMortality.Merge(m1, m2), m3);
        var rightFirst = MediaMortality.Merge(m1, MediaMortality.Merge(m2, m3));

        var a = leftFirst.Cohorts["cohort-a"];
        var b = rightFirst.Cohorts["cohort-a"];
        Assert.Equal(a.MeanGrowthPerYear, b.MeanGrowthPerYear, 9);
        Assert.Equal(a.WeightedM2, b.WeightedM2, 6);
        Assert.Equal(a.ContributorCount, b.ContributorCount);
    }

    [Fact]
    public void Merge_never_mutates_either_input_model()
    {
        var a = new MediaMortalityModel();
        MediaMortality.Observe(a, "cohort-a", Sample[0]);
        var b = new MediaMortalityModel();
        MediaMortality.Observe(b, "cohort-a", Sample[1]);

        var aBefore = a.Cohorts["cohort-a"];
        var bBefore = b.Cohorts["cohort-a"];
        MediaMortality.Merge(a, b);

        Assert.Equal(aBefore, a.Cohorts["cohort-a"]);
        Assert.Equal(bBefore, b.Cohorts["cohort-a"]);
    }

    [Fact]
    public void Distinct_cohorts_never_mix()
    {
        var model = new MediaMortalityModel();
        MediaMortality.Observe(model, "cohort-a", new CohortObservation(0.50, 3));
        MediaMortality.Observe(model, "cohort-b", new CohortObservation(0.01, 3));

        Assert.Equal(2, model.Cohorts.Count);
        Assert.True(model.Cohorts["cohort-a"].MeanGrowthPerYear > model.Cohorts["cohort-b"].MeanGrowthPerYear);
    }

    [Fact]
    public void Estimate_refuses_a_cohort_below_the_privacy_floor()
    {
        var model = new MediaMortalityModel();
        MediaMortality.Observe(model, "cohort-a", new CohortObservation(0.20, 3));
        MediaMortality.Observe(model, "cohort-a", new CohortObservation(0.22, 3));   // 2 contributors < floor of 3

        Assert.Null(MediaMortality.Estimate(model, "cohort-a"));
    }

    [Fact]
    public void Estimate_reports_once_the_privacy_floor_is_met()
    {
        var model = new MediaMortalityModel();
        for (int i = 0; i < MediaMortality.MinContributorsToReport; i++)
            MediaMortality.Observe(model, "cohort-a", new CohortObservation(0.20, 3));

        var estimate = MediaMortality.Estimate(model, "cohort-a");
        Assert.NotNull(estimate);
        Assert.Equal(MediaMortality.MinContributorsToReport, estimate!.ContributorCount);
        Assert.Equal(0.20, estimate.MeanGrowthPerYear, 9);
    }

    [Fact]
    public void Estimate_returns_null_for_an_unknown_cohort()
    {
        var model = new MediaMortalityModel();
        MediaMortality.Observe(model, "cohort-a", new CohortObservation(0.20, 3));
        Assert.Null(MediaMortality.Estimate(model, "cohort-nonexistent"));
    }

    [Fact]
    public void A_two_scan_fit_is_weighted_less_than_a_six_scan_fit()
    {
        // Weight is degrees of freedom (SampleCount - 1), floored at 1 — not R², which is meaningless
        // (always 1.0) for a straight line through exactly two points.
        Assert.Equal(1, new CohortObservation(0.1, 2).Weight);
        Assert.Equal(5, new CohortObservation(0.1, 6).Weight);
        Assert.True(new CohortObservation(0.1, 6).Weight > new CohortObservation(0.1, 2).Weight);
    }

    [Fact]
    public void FromKinetics_carries_only_growth_rate_and_sample_count()
    {
        var fit = RotKinetics.Fit(new[]
        {
            new RotSample(DateTimeOffset.Parse("2020-01-01"), 5),
            new RotSample(DateTimeOffset.Parse("2021-01-01"), 10),
            new RotSample(DateTimeOffset.Parse("2022-01-01"), 20),
        });
        var obs = MediaMortality.FromKinetics(fit);
        Assert.Equal(fit.GrowthPerYear, obs.GrowthPerYear);
        Assert.Equal(fit.SampleCount, obs.SampleCount);
    }

    [Fact]
    public void A_round_tripped_model_through_json_still_estimates_identically()
    {
        var model = new MediaMortalityModel();
        foreach (var o in Sample) MediaMortality.Observe(model, "cohort-a", o);

        string json = MediaMortality.ToJson(model);
        var reloaded = MediaMortality.FromJson(json);

        var before = MediaMortality.Estimate(model, "cohort-a");
        var after = MediaMortality.Estimate(reloaded, "cohort-a");
        Assert.Equal(before!.MeanGrowthPerYear, after!.MeanGrowthPerYear, 9);
        Assert.Equal(before.ContributorCount, after.ContributorCount);
    }

    [Fact]
    public void The_shared_json_artifact_carries_no_disc_identifying_fields()
    {
        // A structural guard against regressions that would leak identity: the serialized model must
        // only ever contain cohort keys and the four summary numbers, nothing per-disc.
        var model = new MediaMortalityModel();
        foreach (var o in Sample) MediaMortality.Observe(model, "cohort-a", o);
        string json = MediaMortality.ToJson(model);

        Assert.DoesNotContain("DiscId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Title", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Scan", json, StringComparison.OrdinalIgnoreCase);
    }
}
