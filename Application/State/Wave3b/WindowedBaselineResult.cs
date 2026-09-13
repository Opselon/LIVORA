using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.State.Wave3b;

/// <summary>
/// Shared DTOs for Wave 3b lane 05 (windowed longitudinal baselines). Deliberately minimal:
/// the service produces <see cref="WindowedBaselineResult"/>, the pure layers turn it into a
/// <see cref="PrimaryWindowSelection"/> and a <see cref="StateConfidenceInfo"/>.
/// Every string here is a MACHINE TAG (stable, dot/colon grammar, no prose) — the layers emit
/// keys+tags only, per the Wave 3b bilingual rule; UI labels belong to the presentation lanes.
/// </summary>
public sealed record WindowedBaselineResult
{
    public required string MetricKey { get; init; }

    /// <summary>
    /// Every declared window is always present as a key. A <c>null</c> value means the
    /// minimum-sample gate for that window was NOT met — the honest answer is "no baseline",
    /// never a number computed from too little data.
    /// </summary>
    public required IReadOnlyDictionary<BaselineWindow, Baseline?> ByWindow { get; init; }

    /// <summary>Valid real sample-days counted per window (what the gate was compared against).</summary>
    public required IReadOnlyDictionary<BaselineWindow, int> SampleDaysByWindow { get; init; }

    public Baseline? this[BaselineWindow window] =>
        ByWindow.TryGetValue(window, out var b) ? b : null;

    public int SampleDays(BaselineWindow window) =>
        SampleDaysByWindow.TryGetValue(window, out var n) ? n : 0;
}

/// <summary>Which window a metric's trustworthy baseline actually comes from (lane 05 selector output).</summary>
public sealed record PrimaryWindowSelection
{
    public required string MetricKey { get; init; }
    /// <summary>Null when NO window passed its gate — no trustworthy baseline exists yet.</summary>
    public BaselineWindow? Window { get; init; }
    public Baseline? Baseline { get; init; }
    /// <summary>Machine tags, e.g. "fallback:30->14" (longest window unavailable, we stepped down).</summary>
    public IReadOnlyList<string> FallbackTags { get; init; } = Array.Empty<string>();
    /// <summary>Machine tag when Window is null, e.g. "no_trustworthy_window". Null otherwise.</summary>
    public string? NullReason { get; init; }
}

/// <summary>Explicit state-confidence: a level plus the machine-readable WHY (drivers).</summary>
public sealed record StateConfidenceInfo
{
    public required BaselineConfidence Level { get; init; }
    /// <summary>Stable, ordered machine tags: per-window sample shortages, no-window metrics,
    /// and the completeness input ('completeness:0.42'). Never display prose.</summary>
    public required IReadOnlyList<string> Drivers { get; init; }
}

/// <summary>Machine-tag vocabulary for this lane (single source so tests and lanes never typo apart).</summary>
public static class Wave3bTags
{
    /// <summary>Emitted per (metric, window) whose sample-count gate failed.</summary>
    public const string InsufficientSamples = "insufficient_samples";
    /// <summary>Emitted per metric with no trustworthy window at all — covers both a failed
    /// sample gate AND a failed coverage gate (a 25-day-old account has no observable 30-day
    /// window); the shortage tags below distinguish the sample-gate case when they appear.</summary>
    public const string NoTrustworthyWindow = "no_trustworthy_window";
    /// <summary>Confidence input contained zero metrics.</summary>
    public const string NoMetrics = "input:no_metrics";
    /// <summary>The completeness link was the weakest one in the chain.</summary>
    public const string CompletenessWeakestLink = "completeness:weakest-link";

    /// <summary>'sleep.minutes:window30:insufficient_samples' shaped per-metric tag.</summary>
    public static string Insufficient(string metricKey, int windowDays) =>
        $"{metricKey}:window{windowDays}:{InsufficientSamples}";
}

/// <summary>
/// The one trustworthiness rule, shared by the selector and the confidence model so the two
/// can never drift (a baseline is trustworthy only if it exists AND carries real confidence).
/// </summary>
internal static class Wave3bTrust
{
    public static bool IsTrustworthy(Baseline? baseline) =>
        baseline is not null && baseline.Confidence != BaselineConfidence.None;

    /// <summary>Highest declared window with a trustworthy baseline, or null. Deterministic.</summary>
    public static BaselineWindow? PrimaryWindow(WindowedBaselineResult result)
    {
        for (int i = Wave3bWindows.Declared.Length - 1; i >= 0; i--)
        {
            var w = Wave3bWindows.Declared[i];
            if (IsTrustworthy(result[w])) return w;
        }
        return null;
    }
}

/// <summary>Window vocabulary for the longitudinal layer: declaration order = ascending length.</summary>
public static class Wave3bWindows
{
    /// <summary>Ascending. The gate table lives on WindowedBaselineService.MinimumSamples.</summary>
    public static readonly BaselineWindow[] Declared =
    {
        BaselineWindow.Days7,
        BaselineWindow.Days14,
        BaselineWindow.Days30,
    };

    public static int Days(BaselineWindow window) => window switch
    {
        BaselineWindow.Days7 => 7,
        BaselineWindow.Days14 => 14,
        BaselineWindow.Days30 => 30,
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, null),
    };
}
