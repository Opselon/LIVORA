using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.State.Wave3b;

/// <summary>
/// Which statistic a longitudinal baseline uses for one metric.
/// Bedtime and sleep length carry night-to-night outliers (one 2-hour night must not
/// drag "my normal" away), so they take a recency-weighted MEDIAN; everything else is
/// close enough to additive-per-day that the plain mean is the honest summary.
/// </summary>
public enum BaselineStatistic
{
    Mean = 0,
    RecencyWeightedMedian = 1,
}

/// <summary>
/// One metric's longitudinal result: the baseline plus HOW it was earned.
/// <see cref="WindowUsed"/> is null when the sample gate refused the computation —
/// the Baseline carried alongside then has Value=NaN/StdDev=NaN/Confidence=None and
/// SampleDays reporting what WAS there. A refused window never produces a
/// computed-looking number.
/// </summary>
public sealed record BaselineEntry(string MetricKey, Baseline Baseline, BaselineWindow? WindowUsed)
{
    /// <summary>True only when a gate passed and the value is real.</summary>
    public bool Usable =>
        WindowUsed is not null
        && Baseline.Confidence != BaselineConfidence.None
        && !double.IsNaN(Baseline.Value);

    /// <summary>Clean sample days the (largest tried) window actually contained.</summary>
    public int SampleDays => Baseline.SampleDays;
}

/// <summary>
/// The result object BaselineEngine returns: per-metric Baseline + window used + sample
/// counts, in one honest bundle. Deliberately shaped so it can be degraded to the legacy
/// <c>Dictionary&lt;string, Baseline&gt;</c> the frozen IBaselineService contract returns
/// (see <see cref="ToLegacyDictionary"/>) — that is the delegation seam, documented below.
/// </summary>
public sealed class BaselineSet
{
    private readonly Dictionary<string, BaselineEntry> _byMetric;

    public BaselineSet(IEnumerable<BaselineEntry> entries, BaselineWindow? maxWindow = null)
    {
        _byMetric = new Dictionary<string, BaselineEntry>();
        foreach (var e in entries) _byMetric[e.MetricKey] = e;
        MaxWindow = maxWindow;
    }

    /// <summary>The largest window the engine was allowed to consider (auto-downgrade bound).</summary>
    public BaselineWindow? MaxWindow { get; }

    public IReadOnlyDictionary<string, BaselineEntry> Entries => _byMetric;

    public BaselineEntry? Entry(string metricKey) =>
        _byMetric.TryGetValue(metricKey, out var e) ? e : null;

    public Baseline? Get(string metricKey) => Entry(metricKey)?.Baseline;

    /// <summary>Which window earned this metric's baseline (null = every window refused).</summary>
    public BaselineWindow? WindowUsed(string metricKey) => Entry(metricKey)?.WindowUsed;

    /// <summary>Clean sample days per metric; 0 when the metric was never computed.</summary>
    public int SampleCount(string metricKey) => Entry(metricKey)?.SampleDays ?? 0;

    public IReadOnlyDictionary<string, int> SampleCountsByMetric =>
        _byMetric.ToDictionary(kv => kv.Key, kv => kv.Value.SampleDays);

    /// <summary>None for unknown metrics or gate refusals — never a fake level.</summary>
    public BaselineConfidence ConfidenceOf(string metricKey) =>
        Entry(metricKey) is { Usable: true } e ? e.Baseline.Confidence : BaselineConfidence.None;

    /// <summary>Non-null only for usable baselines (NaN refusals read as "no value").</summary>
    public double? Value(string metricKey) =>
        Entry(metricKey) is { Usable: true } e ? e.Baseline.Value : null;

    /// <summary>Worst confidence among the given metrics (None when any is missing/refused).</summary>
    public BaselineConfidence WorstConfidence(IEnumerable<string> metricKeys)
    {
        var worst = BaselineConfidence.High;
        bool saw = false;
        foreach (var k in metricKeys)
        {
            saw = true;
            var c = ConfidenceOf(k);
            if (c < worst) worst = c;
        }
        return saw ? worst : BaselineConfidence.None;
    }

    /// <summary>
    /// DELEGATION SEAM (documented, NOT wired — BaselineService is frozen to edits):
    /// BaselineService.GetBaselinesAsync builds exactly a Dictionary&lt;string, Baseline&gt;
    /// from a 28-day window with Baseline.FromSamples (mean-only, 3-sample floor). A Wave 3d
    /// change can keep the whole method body of BaselineService and swap the dictionary
    /// construction for <c>BaselineEngine.Compute(await _history.GetAllAsync(), today)
    /// .ToLegacyDictionary()</c>: same keys, same type, strictly more honest refusals
    /// (gates are higher, values may be NaN where FromSamples would have printed a mean of 2
    /// nights). Nothing in this lane modifies or subclasses BaselineService; it is consumed
    /// read-only via its static UnwrapBedtime.
    /// </summary>
    public Dictionary<string, Baseline> ToLegacyDictionary() =>
        _byMetric.ToDictionary(kv => kv.Key, kv => kv.Value.Baseline);
}

/// <summary>
/// Pure longitudinal baseline engine (Wave 3c lane 04). No IO, no wall clock — callers pass
/// history and the as-of date. Windows 7/14/30 with honest minimum-sample gates:
///   Days7  needs ≥ 4 clean samples
///   Days14 needs ≥ 8 clean samples
///   Days30 needs ≥ 16 clean samples
/// Below the gate the metric REFUSES: Confidence=None, Value=NaN, StdDev=NaN, and the
/// reported SampleDays says how little data there actually was. A refused baseline NEVER
/// looks like a number. When a metric's largest window fails, the engine downgrades to the
/// next smaller window and only refuses when even Days7 fails.
/// </summary>
public static class BaselineEngine
{
    /// <summary>Minimum-sample gates — the whole point of this engine.</summary>
    public const int GateDays7 = 4;
    public const int GateDays14 = 8;
    public const int GateDays30 = 16;

    public static int WindowDays(BaselineWindow window) => window switch
    {
        BaselineWindow.Days7 => 7,
        BaselineWindow.Days14 => 14,
        _ => 30,
    };

    public static int MinimumSamples(BaselineWindow window) => window switch
    {
        BaselineWindow.Days7 => GateDays7,
        BaselineWindow.Days14 => GateDays14,
        BaselineWindow.Days30 => GateDays30,
        _ => throw new ArgumentOutOfRangeException(nameof(window)),
    };

    /// <summary>Largest-first evaluation order for auto-downgrade.</summary>
    public static IReadOnlyList<BaselineWindow> WindowsDescending { get; } =
        new[] { BaselineWindow.Days30, BaselineWindow.Days14, BaselineWindow.Days7 };

    // ---- metric configuration ---------------------------------------------

    /// <summary>How one metric reads its samples out of a history record (double.NaN = absent).
    /// Bedtime ≤ 0 is treated as absent: a date-only history row defaults to 0, and 0 is
    /// indistinguishable from "never recorded" — guessing midnight would fabricate data.</summary>
    private sealed record MetricSpec(string Key, BaselineStatistic Statistic, Func<DailyHistoryRecord, double> Selector, bool WrapBedtimeScale = false);

    private static readonly MetricSpec[] Specs =
    {
        new(Metrics.SleepMinutes, BaselineStatistic.RecencyWeightedMedian, r => r.SleepMinutes),
        new(Metrics.BedtimeMinutes, BaselineStatistic.RecencyWeightedMedian, r => r.BedtimeMinutesOfDay, WrapBedtimeScale: true),
        new(Metrics.SleepQuality, BaselineStatistic.Mean, r => r.SleepQuality),
        new(Metrics.SleepConsistency, BaselineStatistic.Mean, r => r.SleepConsistency),
        new(Metrics.Steps, BaselineStatistic.Mean, r => r.Steps),
        new(Metrics.ActiveMinutes, BaselineStatistic.Mean, r => r.ActiveMinutes),
        new(Metrics.RecoveryScore, BaselineStatistic.Mean, r => r.RecoveryScore),
        new(Metrics.RestingHeartRate, BaselineStatistic.Mean, r => r.RestingHeartRate ?? double.NaN),
        new(Metrics.HrvMs, BaselineStatistic.Mean, r => r.HrvMs ?? double.NaN),
        new(Metrics.Stress, BaselineStatistic.Mean, r => r.Stress),
        new(Metrics.Mood, BaselineStatistic.Mean, r => r.Mood),
        new(Metrics.Energy, BaselineStatistic.Mean, r => r.Energy),
    };

    /// <summary>Every metric key the engine knows about (single source of truth for tests).</summary>
    public static IReadOnlyList<string> MetricKeys { get; } = Specs.Select(s => s.Key).ToList();

    // ---- public compute -----------------------------------------------------

    /// <summary>
    /// Compute the full baseline set from history as of <paramref name="asOf"/>.
    /// <paramref name="maxWindow"/> bounds how far back a baseline may reach; per metric the
    /// engine then uses the window it can honestly earn (see <see cref="ComputeForMetric"/>).
    /// </summary>
    public static BaselineSet Compute(
        IReadOnlyList<DailyHistoryRecord> history,
        DateTime asOf,
        BaselineWindow maxWindow = BaselineWindow.Days30,
        bool allowDowngrade = true)
    {
        var entries = new List<BaselineEntry>(Specs.Length);
        foreach (var spec in Specs)
            entries.Add(Evaluate(spec, history, asOf, maxWindow, allowDowngrade));
        return new BaselineSet(entries, maxWindow);
    }

    /// <summary>Single-metric compute (used by tests and future call sites).</summary>
    public static BaselineEntry ComputeForMetric(
        string metricKey, IReadOnlyList<DailyHistoryRecord> history, DateTime asOf,
        BaselineWindow maxWindow = BaselineWindow.Days30, bool allowDowngrade = true)
    {
        var spec = Specs.FirstOrDefault(s => s.Key == metricKey)
                   ?? throw new ArgumentException($"unknown metric {metricKey}", nameof(metricKey));
        return Evaluate(spec, history, asOf, maxWindow, allowDowngrade);
    }

    /// <summary>
    /// STRICT single-window evaluation: asks for exactly one window and refuses when its gate
    /// fails — no silent fallback. This is the "30-day window from 2 days of data refuses" path.
    /// </summary>
    public static BaselineEntry ComputeWindow(
        string metricKey, IReadOnlyList<DailyHistoryRecord> history, DateTime asOf, BaselineWindow window)
    {
        var spec = Specs.FirstOrDefault(s => s.Key == metricKey)
                   ?? throw new ArgumentException($"unknown metric {metricKey}", nameof(metricKey));
        var chrono = WindowSamples(history, asOf, spec, WindowDays(window));
        return chrono.Count >= MinimumSamples(window)
            ? Build(spec, chrono, window)
            : Refused(spec.Key, chrono.Count);
    }

    private static BaselineEntry Evaluate(
        MetricSpec spec, IReadOnlyList<DailyHistoryRecord> history, DateTime asOf,
        BaselineWindow maxWindow, bool allowDowngrade)
    {
        var chrono = WindowSamples(history, asOf, spec, WindowDays(maxWindow));
        if (chrono.Count >= MinimumSamples(maxWindow)) return Build(spec, chrono, maxWindow);
        int observedAtMax = chrono.Count;

        if (allowDowngrade)
        {
            // Walk the remaining windows (already below maxWindow) largest→smallest; a metric
            // with only a strong recent week still earns a week baseline — honestly labeled.
            foreach (var w in WindowsDescending)
            {
                if (w >= maxWindow) continue;
                var smaller = WindowSamples(history, asOf, spec, WindowDays(w));
                if (smaller.Count >= MinimumSamples(w))
                    return Build(spec, smaller, w);
            }
        }
        // Refused: SampleDays reports the clean samples that DID exist inside the requested
        // window (the honest "how much data exists" number); Value/StdDev stay NaN,
        // Confidence stays None.
        return Refused(spec.Key, observedAtMax);
    }

    private static BaselineEntry Refused(string metricKey, int sampleDays) =>
        new(metricKey, new Baseline
        {
            MetricKey = metricKey,
            Value = double.NaN,   // NEVER a computed-looking number
            StdDev = double.NaN,
            SampleDays = sampleDays,
            Confidence = BaselineConfidence.None,
        }, WindowUsed: null);

    private static BaselineEntry Build(MetricSpec spec, List<double> chronological, BaselineWindow window)
    {
        int n = chronological.Count;
        // Confidence thresholds mirror the frozen Baseline.FromSamples learning curve:
        // 4–6 → Low, 7–13 → Medium, 14+ → High (each gate already guarantees the floor).
        var confidence = n switch
        {
            < 7 => BaselineConfidence.Low,
            < 14 => BaselineConfidence.Medium,
            _ => BaselineConfidence.High,
        };

        double mean = chronological.Average();
        double variance = chronological.Sum(x => (x - mean) * (x - mean)) / n;

        double value = spec.Statistic switch
        {
            BaselineStatistic.RecencyWeightedMedian when spec.Key == Metrics.BedtimeMinutes =>
                // Samples arrive pre-wrapped (post-noon scale); unwrap back to minutes-of-day
                // via the ONE existing implementation — this engine must not fork that math.
                BaselineService.UnwrapBedtime(RecencyWeightedMedian(chronological)),
            BaselineStatistic.RecencyWeightedMedian => RecencyWeightedMedian(chronological),
            _ => mean,
        };

        return new BaselineEntry(spec.Key, new Baseline
        {
            MetricKey = spec.Key,
            Value = value,
            StdDev = Math.Sqrt(variance),
            SampleDays = n,
            Confidence = confidence,
        }, window);
    }

    // ---- sampling / statistics ----------------------------------------------

    /// <summary>Clean samples for one spec inside the last <paramref name="days"/> days,
    /// chronological (the same window convention as the frozen BaselineService:
    /// date ≤ asOf AND date &gt; asOf−days). Bedtime samples are mapped to the post-noon
    /// scale before any statistic sees them (cross-midnight averaging).</summary>
    private static List<double> WindowSamples(
        IReadOnlyList<DailyHistoryRecord> history, DateTime asOf, MetricSpec spec, int days)
    {
        var from = asOf.Date.AddDays(-days);
        var to = asOf.Date;
        return history
            .Where(r => r.Date.Date <= to && r.Date.Date > from)
            .OrderBy(r => r.Date)
            .Select(spec.Selector)
            .Where(SampleValid(spec))
            .Select(v => spec.WrapBedtimeScale ? WrapBedtime(v) : v)
            .ToList();
    }

    private static Func<double, bool> SampleValid(MetricSpec spec) => spec.Key switch
    {
        Metrics.BedtimeMinutes => v => !double.IsNaN(v) && v > 0, // default-zero rows are absent
        _ => v => !double.IsNaN(v) && v >= 0,                     // parity with Baseline.FromSamples
    };

    /// <summary>
    /// Weighted median over a chronological series: the NEWEST HALF of the samples carries
    /// double weight, so "my recent normal" wins over month-old noise without discarding it.
    /// Deterministic, O(n log n), no IO. Empty/all-NaN input returns NaN (never 0).
    /// </summary>
    public static double RecencyWeightedMedian(IReadOnlyList<double> chronological)
    {
        var v = chronological.Where(x => !double.IsNaN(x)).ToList();
        if (v.Count == 0) return double.NaN;

        var weighted = v
            .Select((x, i) => (Value: x, Weight: i < v.Count / 2 ? 1 : 2)) // newest half → ×2
            .OrderBy(t => t.Value)
            .ToList();

        double total = weighted.Sum(t => t.Weight);
        double acc = 0;
        foreach (var (value, weight) in weighted)
        {
            acc += weight;
            if (acc * 2 >= total) return value;
        }
        return weighted[^1].Value;
    }

    /// <summary>
    /// Bedtimes near midnight break naive averaging (23:50 vs 00:40). Map pre-noon values to
    /// the post-noon scale — the same mapping idea as BaselineService.WrapAround, with the
    /// default-zero rows dropped — so medians/means average across midnight correctly.
    /// Always unwrap with BaselineService.UnwrapBedtime before presenting.
    /// </summary>
    public static double WrapBedtime(double minutesOfDay) =>
        double.IsNaN(minutesOfDay) || minutesOfDay <= 0
            ? double.NaN
            : minutesOfDay < 12 * 60 ? minutesOfDay + 1440 : minutesOfDay;
}
