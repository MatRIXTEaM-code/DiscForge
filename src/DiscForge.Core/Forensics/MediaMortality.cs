// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscForge.Core.Forensics;

/// <summary>
/// One local disc's <see cref="RotKinetics"/> fit, reduced to the two numbers a federated mortality model
/// needs. Deliberately carries nothing that could re-identify the disc or its owner — no disc id, no
/// title, no scan dates, no storage location, no error-rate history — only the fitted growth rate and how
/// many scans supported it. This is the whole privacy design: a contribution is two floats, not a record.
/// </summary>
public readonly record struct CohortObservation(double GrowthPerYear, int SampleCount)
{
    /// <summary>Weight = degrees of freedom (SampleCount − 1), deliberately NOT sample count and NOT
    /// <see cref="RotKineticsResult.RSquared"/>: a 2-scan fit is a real observation but has zero residual
    /// (a line always fits two points exactly, so its R² is meaningless as a confidence signal), so it
    /// should count for less than a 6-scan fit that actually had room to disagree with itself, and R²
    /// can't tell the two apart. Floored at 1 so even a 2-scan fit still contributes something.</summary>
    public double Weight => Math.Max(SampleCount - 1, 1);
}

/// <summary>Aggregate mortality statistics for one cohort (e.g. a manufacturer/mold-SID code, or a media
/// type + brand — DiscForge doesn't prescribe the taxonomy, callers choose the cohort key). Everything here
/// is a running weighted mean/variance in Welford's PARALLEL form, chosen specifically because it is exact
/// and order-independent when two partial summaries are combined — the property that makes federation
/// possible without a central coordinator or any raw observation ever leaving the machine it was fit on.</summary>
public sealed record CohortSummary
{
    public required string Cohort { get; init; }
    /// <summary>Number of DISCS folded into this summary — never a count of scans, and never the identity
    /// of any of them.</summary>
    public required int ContributorCount { get; init; }
    public required double WeightSum { get; init; }
    public required double MeanGrowthPerYear { get; init; }
    /// <summary>Weighted sum of squared deviations from the running mean (Chan et al.'s parallel-merge
    /// M2) — combine with <see cref="MediaMortality.Merge"/>, never recompute from scratch, or a later
    /// merge will silently double-count.</summary>
    public required double WeightedM2 { get; init; }

    [JsonIgnore] public double Variance => WeightSum > 1e-12 ? WeightedM2 / WeightSum : 0;
    [JsonIgnore] public double StdDev => Math.Sqrt(Math.Max(Variance, 0));
}

/// <summary>A shareable, aggregate-only mortality model: one <see cref="CohortSummary"/> per cohort. This
/// IS the artifact meant to be published/exchanged — by construction it holds no disc identities, no scan
/// timestamps, no per-disc error histories, nothing but running statistics.</summary>
public sealed class MediaMortalityModel
{
    public string FormatVersion => "dmm/1";
    public Dictionary<string, CohortSummary> Cohorts { get; set; } = new();
}

/// <summary>A community-informed estimate for one cohort, returned only when enough independent
/// contributors back it (see <see cref="MediaMortality.MinContributorsToReport"/>).</summary>
public sealed record CohortEstimate(string Cohort, int ContributorCount, double MeanGrowthPerYear, double StdDev)
{
    public string Summary() =>
        $"{Cohort}: {MeanGrowthPerYear * 100:0.#}%/yr mean growth (±{StdDev * 100:0.#} pp, {ContributorCount} contributor(s)).";
}

/// <summary>
/// The federated media-mortality model — the community-scale counterpart to <see cref="RotKinetics"/> and
/// <see cref="DiscActuary"/>. A single collection rarely has more than a handful of scans of any one disc,
/// so a per-disc fit is often too noisy to trust (or, with fewer than 3 scans, <see cref="DiscActuary"/>
/// declines to fit one at all — "need 3+ for a trend"). The gap: nothing lets one collection's experience
/// of how a mold/manufacturer's discs actually decay inform another's, without either pooling raw disc
/// data (privacy-hostile and needs infrastructure nobody here can stand up) or trusting one party's model
/// as authoritative (the opposite of DiscForge's clean-room, trust-nobody ethos).
///
/// The design: each contributor reduces every disc they've fit to a <see cref="CohortObservation"/> — two
/// numbers, nothing identifying — and folds it into a local <see cref="MediaMortalityModel"/> with
/// <see cref="Observe"/>. Any two models, from any two contributors, combine with <see cref="Merge"/> using
/// the weighted parallel-variance algorithm (Chan, Golub &amp; LeVeque 1979): this is mathematically EXACT
/// — merging two partial summaries produces the identical mean/variance as fitting all the underlying
/// observations centrally would have, without a single one of those observations ever being shared. Models
/// merge in any order, any number of times, with no coordinator: that associativity and commutativity is
/// what makes this "federated" rather than just "aggregated by someone" — the community model that results
/// from everyone merging their neighbours' models pairwise is the same model a central server would have
/// computed, and no server, and no raw data exchange, was ever required.
///
/// <see cref="Estimate"/> refuses to report a cohort backed by fewer than <see cref="MinContributorsToReport"/>
/// independent discs — not as an accuracy safeguard but a privacy one: a "cohort" of one or two contributors
/// would let a recipient reverse-engineer roughly how fast a specific person's specific disc is decaying,
/// which is exactly the kind of re-identification a two-number-per-disc design is supposed to prevent.
///
/// This is forecasting from shared statistics, nothing else: it recommends, it re-identifies nobody, and it
/// never touches a disc's own content or a collection's actual holdings.
/// </summary>
public static class MediaMortality
{
    /// <summary>Below this many independent contributing discs, <see cref="Estimate"/> returns null rather
    /// than a number — a privacy floor, not an accuracy one (see the class doc comment).</summary>
    public const int MinContributorsToReport = 3;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Fold one disc's local kinetics fit into a cohort — mutates and returns <paramref
    /// name="model"/> for chaining. Equivalent to (and implemented as) merging the model with a
    /// single-contributor model built from <paramref name="observation"/>.</summary>
    public static MediaMortalityModel Observe(MediaMortalityModel model, string cohort, CohortObservation observation)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(cohort)) throw new ArgumentException("A cohort key is required.", nameof(cohort));

        var single = new CohortSummary
        {
            Cohort = cohort,
            ContributorCount = 1,
            WeightSum = observation.Weight,
            MeanGrowthPerYear = observation.GrowthPerYear,
            WeightedM2 = 0,
        };
        model.Cohorts[cohort] = model.Cohorts.TryGetValue(cohort, out var existing)
            ? CombineCohort(existing, single)
            : single;
        return model;
    }

    /// <summary>Combine two independently-built models — from any two contributors, in any order, any
    /// number of times — into one. Never mutates either input.</summary>
    public static MediaMortalityModel Merge(MediaMortalityModel a, MediaMortalityModel b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var merged = new MediaMortalityModel();
        foreach (var kv in a.Cohorts) merged.Cohorts[kv.Key] = kv.Value;
        foreach (var kv in b.Cohorts)
            merged.Cohorts[kv.Key] = merged.Cohorts.TryGetValue(kv.Key, out var existing)
                ? CombineCohort(existing, kv.Value)
                : kv.Value with { };   // copy, don't alias b's record into the merged model
        return merged;
    }

    /// <summary>The community-informed estimate for a cohort, or null if too few independent discs back it
    /// (see <see cref="MinContributorsToReport"/>) or the cohort is unknown to this model.</summary>
    public static CohortEstimate? Estimate(MediaMortalityModel model, string cohort)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Cohorts.TryGetValue(cohort, out var s) || s.ContributorCount < MinContributorsToReport)
            return null;
        return new CohortEstimate(s.Cohort, s.ContributorCount, s.MeanGrowthPerYear, s.StdDev);
    }

    /// <summary>Chan/Golub/LeVeque's parallel combination of two weighted running mean/variance
    /// aggregates — exact regardless of how the underlying observations were split between them.</summary>
    private static CohortSummary CombineCohort(CohortSummary x, CohortSummary y)
    {
        double wSum = x.WeightSum + y.WeightSum;
        if (wSum <= 1e-12)
            return new CohortSummary
            {
                Cohort = x.Cohort, ContributorCount = x.ContributorCount + y.ContributorCount,
                WeightSum = 0, MeanGrowthPerYear = 0, WeightedM2 = 0,
            };
        double delta = y.MeanGrowthPerYear - x.MeanGrowthPerYear;
        double mean = x.MeanGrowthPerYear + delta * (y.WeightSum / wSum);
        double m2 = x.WeightedM2 + y.WeightedM2 + delta * delta * (x.WeightSum * y.WeightSum / wSum);
        return new CohortSummary
        {
            Cohort = x.Cohort,
            ContributorCount = x.ContributorCount + y.ContributorCount,
            WeightSum = wSum,
            MeanGrowthPerYear = mean,
            WeightedM2 = m2,
        };
    }

    /// <summary>Build a single-disc observation directly from a <see cref="RotKinetics"/> fit, so a caller
    /// never has to touch <see cref="CohortObservation"/>'s fields by hand.</summary>
    public static CohortObservation FromKinetics(RotKineticsResult fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        return new CohortObservation(fit.GrowthPerYear, fit.SampleCount);
    }

    public static string ToJson(MediaMortalityModel model) => JsonSerializer.Serialize(model, JsonOpts);

    public static MediaMortalityModel FromJson(string json) =>
        JsonSerializer.Deserialize<MediaMortalityModel>(json, JsonOpts)
        ?? throw new ArgumentException("Empty or invalid media-mortality model.");

    public static void Save(MediaMortalityModel model, string path) => File.WriteAllText(path, ToJson(model));

    public static MediaMortalityModel Load(string path) => FromJson(File.ReadAllText(path));
}
