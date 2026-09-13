using System.Globalization;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.State.Wave3b;

/// <summary>
/// Wave 3b lane 05 (pure): explicit state-confidence from the windowed baselines plus a
/// completeness input. No I/O, no clock, no MAUI — same inputs always give identical output.
///
/// WEAKEST-LINK RULE (documented; matches <see cref="Domain.Models.State.PersonalState"/>'s
/// "overall confidence = min(domain confidences) — a chain is as strong as its weakest link"):
/// the overall level is the MINIMUM of (per-metric trust level..., completeness link), where a
/// metric's trust level is the <c>Baseline.Confidence</c> of its PRIMARY window (the
/// longest trustworthy one — the selector's answer), or <see cref="BaselineConfidence.None"/>
/// when no window is trustworthy. The completeness link maps the 0..1 input onto the same
/// confidence ladder with inclusive thresholds:
///   >= 0.80 High, >= 0.50 Medium, >= 0.25 Low, else None
/// (same shape as <c>Baseline.FromSamples</c>' sample-count ladder).
///
/// Drivers are ordered machine tags (never prose): per-(metric,window) sample shortages
/// ('sleep.minutes:window30:insufficient_samples'), metrics with no trustworthy window
/// ('sleep.minutes:no_trustworthy_window'), the completeness input
/// ('completeness:0.42'), and 'completeness:weakest-link' when completeness alone decided the
/// level. Iteration order = Wave3bWindows/declared metric order => byte-deterministic.
/// </summary>
public sealed class StateConfidenceModel
{
    /// <summary>Completeness thresholds for its confidence link (inclusive, on the ladder).</summary>
    public static readonly IReadOnlyDictionary<BaselineConfidence, double> CompletenessThresholds =
        new Dictionary<BaselineConfidence, double>
        {
            [BaselineConfidence.High] = 0.80,
            [BaselineConfidence.Medium] = 0.50,
            [BaselineConfidence.Low] = 0.25,
        };

    public StateConfidenceInfo Evaluate(
        IReadOnlyDictionary<string, WindowedBaselineResult> baselines,
        double completeness)
    {
        var drivers = new List<string>();
        var metricLevels = new List<BaselineConfidence>();

        // Deterministic iteration: canonical metric order first, any extras sorted ordinally.
        foreach (var metric in OrderMetrics(baselines.Keys))
        {
            var result = baselines[metric];

            foreach (var window in Wave3bWindows.Declared)
            {
                if (result.SampleDays(window) < WindowedBaselineService.MinimumSamplesFor(window))
                    drivers.Add(Wave3bTags.Insufficient(metric, Wave3bWindows.Days(window)));
            }

            BaselineWindow? primary = Wave3bTrust.PrimaryWindow(result);
            if (primary is null)
            {
                drivers.Add($"{metric}:{Wave3bTags.NoTrustworthyWindow}");
                metricLevels.Add(BaselineConfidence.None);
            }
            else
            {
                // The longest trustworthy window IS the metric's trust level: shorter windows may
                // carry a higher sample count on the FromSamples ladder, but honesty says we trust
                // the longest observable window we have — the selector's own pick.
                metricLevels.Add(result[primary.Value]!.Confidence);
            }
        }

        double clamped = Math.Clamp(completeness, 0, 1);
        var completenessLink = LevelFromCompleteness(clamped);
        drivers.Add($"completeness:{clamped.ToString("0.##", CultureInfo.InvariantCulture)}");
        if (baselines.Count == 0) drivers.Add(Wave3bTags.NoMetrics);

        // Weakest link over the whole chain: metrics + completeness. No metrics at all => None,
        // never "confident because the calendar is full": a state with zero baselines is not
        // trustworthy no matter what the completeness input says.
        var chain = metricLevels.Append(completenessLink).ToList();
        var level = metricLevels.Count == 0 ? BaselineConfidence.None : chain.Min();
        if (metricLevels.Count > 1 && metricLevels.Min() > completenessLink)
            drivers.Add(Wave3bTags.CompletenessWeakestLink);

        return new StateConfidenceInfo { Level = level, Drivers = drivers };
    }

    /// <summary>Completeness -> its confidence link (public for tests + consuming lanes).</summary>
    public static BaselineConfidence LevelFromCompleteness(double completeness) => completeness switch
    {
        >= 0.80 => BaselineConfidence.High,
        >= 0.50 => BaselineConfidence.Medium,
        >= 0.25 => BaselineConfidence.Low,
        _ => BaselineConfidence.None,
    };

    internal static IEnumerable<string> OrderMetrics(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var m in WindowedBaselineService.MetricKeys)
            if (set.Remove(m)) yield return m;
        var rest = set.ToList();
        rest.Sort(StringComparer.Ordinal);
        foreach (var m in rest) yield return m;
    }
}
