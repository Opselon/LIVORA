using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.State.Wave3b;

/// <summary>How much of today's expected signal actually arrived, split honestly.</summary>
public enum OriginMix
{
    /// <summary>No data at all (nothing to trust, nothing to distrust).</summary>
    NoData = 0,
    /// <summary>Everything present is user-entered (Manual origin).</summary>
    AllManual = 1,
    /// <summary>Everything present came from a device/provider feed.</summary>
    AllDevice = 2,
    /// <summary>Some manual, some device — trust sits between the two.</summary>
    Mixed = 3,
}

/// <summary>
/// Deterministic state-confidence result: the score plus every factor that produced it, so the
/// UI can explain "why only 62%" instead of asserting a magic number.
/// </summary>
public sealed record StateConfidenceResult(
    double Score,
    double Completeness,
    double BaselineFactor,
    double StalenessFactor,
    double OriginFactor,
    int MaxStalenessDays,
    BaselineConfidence WorstBaselineConfidence,
    OriginMix OriginMix)
{
    /// <summary>Score bucketed onto the enum the UI already renders.</summary>
    public BaselineConfidence ConfidenceLevel => Score switch
    {
        < 0.20 => BaselineConfidence.None,
        < 0.45 => BaselineConfidence.Low,
        < 0.75 => BaselineConfidence.Medium,
        _ => BaselineConfidence.High,
    };
}

/// <summary>
/// Pure state-confidence calculator (Wave 3c lane 04). Deterministic: same inputs ⇒ same
/// score, no clock, no IO, no randomness.
///
///   score = clamp(completeness × baselineFactor × stalenessFactor × originFactor, 0, 1)
///
/// Monotonicity is a contract, not an accident: raising completeness, improving the worst
/// baseline, or shrinking staleness can never lower the score; adding manual-only origin can
/// never raise it above a device-grade answer (the cap below encodes that).
/// </summary>
public static class StateConfidenceCalculator
{
    // ---- pinned factor tables (public so tests pin the exact boundaries) ----

    /// <summary>Multiplier for the WORST baseline confidence among the metrics used.</summary>
    public const double FactorNone = 0.0;      // no usable baseline ⇒ no claim to be confident in
    public const double FactorLow = 0.45;
    public const double FactorMedium = 0.75;
    public const double FactorHigh = 1.0;

    public static double BaselineFactorOf(BaselineConfidence confidence) => confidence switch
    {
        BaselineConfidence.None => FactorNone,
        BaselineConfidence.Low => FactorLow,
        BaselineConfidence.Medium => FactorMedium,
        BaselineConfidence.High => FactorHigh,
        _ => FactorNone,
    };

    /// <summary>
    /// Staleness decay per day-since-freshest-record: fresh (0 days) keeps everything, day 1
    /// keeps 0.90, day 2 keeps 0.75, day 3 keeps 0.60, day 4+ keeps 0.45 and floor at 0.25
    /// for anything older (history still speaks about the past, just not about "now").
    /// </summary>
    public const double StalenessDay1 = 0.90;
    public const double StalenessDay2 = 0.75;
    public const double StalenessDay3 = 0.60;
    public const double StalenessDay4Plus = 0.45;
    public const double StalenessFloor = 0.25;

    public static double StalenessFactorOf(int stalenessDays)
    {
        if (stalenessDays <= 0) return 1.0;
        return stalenessDays switch
        {
            1 => StalenessDay1,
            2 => StalenessDay2,
            3 => StalenessDay3,
            4 => StalenessDay4Plus,
            _ => StalenessFloor,
        };
    }

    /// <summary>
    /// Origin trust. A device-grade feed is the reference (1.0). Manual entries are honest but
    /// coarse and self-reported, so an all-manual day is HARD-CAPPED below device grade
    /// (<see cref="OriginCapManual"/>); mixed days scale by the manual share. NoData is 0 —
    /// nothing arrived, so nothing is trustworthy.
    /// </summary>
    public const double OriginCapManual = 0.80;   // strictly below 1.0 = below device-grade
    public const double OriginMixed = 0.92;

    public static double OriginFactorOf(OriginMix mix) => mix switch
    {
        OriginMix.NoData => 0.0,
        OriginMix.AllDevice => 1.0,
        OriginMix.Mixed => OriginMixed,
        OriginMix.AllManual => OriginCapManual,
        _ => 0.0,
    };

    // ---- main entry points ---------------------------------------------------

    /// <summary>
    /// Compute the confidence for a state built from <paramref name="completeness"/> of
    /// expected fields, whose used metrics have worst baseline confidence
    /// <paramref name="worstBaselineConfidence"/>, whose freshest record is
    /// <paramref name="maxStalenessDays"/> old, and whose origin mix is
    /// <paramref name="originMix"/>.
    /// </summary>
    public static StateConfidenceResult Compute(
        double completeness,
        BaselineConfidence worstBaselineConfidence,
        int maxStalenessDays,
        OriginMix originMix)
    {
        // Completeness is clamped (an upstream bug must not be able to push confidence > 1);
        // staleness is floored at 0 so a clock skew cannot INFLATE confidence.
        double c = Math.Clamp(completeness, 0, 1);
        int stale = Math.Max(0, maxStalenessDays);

        double bf = BaselineFactorOf(worstBaselineConfidence);
        double sf = StalenessFactorOf(stale);
        double of = OriginFactorOf(originMix);

        double score = Math.Clamp(c * bf * sf * of, 0, 1);
        return new StateConfidenceResult(score, c, bf, sf, of, stale, worstBaselineConfidence, originMix);
    }

    /// <summary>
    /// Convenience overload that derives every factor from a projected state: completeness from
    /// the state's own field, worst confidence across the metrics actually consulted, staleness
    /// from the sleep freshness marker, and origin mix from the metric origins.
    /// </summary>
    public static StateConfidenceResult ComputeFromState(
        Domain.Models.State.PersonalState state, IReadOnlyCollection<string> usedMetricKeys)
    {
        var used = usedMetricKeys
            .Select(k => state.Metrics.TryGetValue(k, out var m) ? m : null)
            .Where(m => m is not null)
            .ToList();

        var worst = BaselineConfidence.High;
        int present = 0, manual = 0;
        foreach (var m in used)
        {
            var conf = m!.BaselineConfidence;
            if (conf < worst) worst = conf;
            if (m.Quality is DataQuality.Missing or DataQuality.Invalid) continue;
            present++;
            if (m.Origin == DataOrigin.Manual) manual++;
        }

        OriginMix mix = present == 0 ? OriginMix.NoData
            : manual == 0 ? OriginMix.AllDevice
            : manual == present ? OriginMix.AllManual
            : OriginMix.Mixed;

        int staleness = Math.Clamp(state.Sleep.DaysSinceFreshData, 0, 99);
        if (staleness >= 99) mix = OriginMix.NoData;   // nothing fresh at all

        return Compute(state.DataCompleteness, worst, staleness, mix);
    }
}
