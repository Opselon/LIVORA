namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// The server-side history row the baseline stage reads. Deliberately a *narrow* shape: the client
/// persists <c>DailyHistoryRecord</c> (Domain/Models/History/DailyHistoryRecord.cs) with plan
/// snapshots and insight topics; a baseline only needs the metric columns. Kept separate so the
/// server model never drifts into owning client history semantics.
/// A NaN (or null) field means "no value that day" — never 0. Zero-filling would fabricate a
/// measurement (client law: Application/State/Wave3b/PersonalStateProjector.cs:18-20).
/// </summary>
public sealed record HistoryDay(
    DateTime DateUtc,
    double? SleepMinutes = null,
    double? SleepQuality = null,
    double? BedtimeMinutesOfDay = null,
    double? Steps = null,
    double? ActiveMinutes = null,
    double? RecoveryScore = null,
    double? Stress = null,
    double? ScreenMinutes = null,
    double? MeetingMinutes = null);

/// <summary>
/// STAGE 3 — personal baseline. "Your normal matters more than generic normal" (client law,
/// Application/State/BaselineService.cs:7-10): every comparison downstream is against THIS user's
/// own history, and an early-learning user gets an honest refusal instead of a number.
/// <para>
/// PORTED FROM (attribution required — the server assembly cannot reference the MAUI project):
///  - window set 7/14/30 + coverage/density gates (7d≥5, 14d≥10, 30d≥21 valid days) and the
///    "longest trustworthy window IS the metric's trust level" rule:
///    Application/State/Wave3b/WindowedBaselineService.cs:22-29 + MinimumSamples (lines 41-47)
///  - refused-baseline honesty (NaN, never a computed-looking number):
///    Application/State/Wave3b/BaselineEngine.cs:118-121, 244-252
///  - recency-weighted median (newest half of a chronological series counts double) for sleep
///    length and bedtime: Application/State/Wave3b/BaselineEngine.cs:320-338
///  - bedtime wrap-around across midnight: Application/State/BaselineService.cs:56-64
///    (WrapAround/UnwrapBedtime) and BaselineEngine.cs:346-349
///  - the sample-count confidence ladder (&lt;3 None, &lt;7 Low, &lt;14 Medium, else High):
///    Domain/Models/State/PersonalState.cs:59-65 (Baseline.FromSamples)
///  - population standard deviation formula: PersonalState.cs:69
/// </para>
/// <para>
/// DEVIATION from the client (stated, not silent): the client carries TWO gate systems —
/// BaselineEngine's auto-downgrade gates (4/8/16) and WindowedBaselineService's coverage+density
/// gates (5/10/21). The server uses the STRICTER pair (5/10/21 + coverage), because a cloud-side
/// recommendation should under-claim rather than over-claim, and pairs with the window-capped
/// confidence so a 7-day-only user can never read "High".
/// </para>
/// </summary>
public static class BaselineStage
{
    public const string StageName = "baseline";

    public enum Statistic { Mean, RecencyWeightedMedian }

    private sealed record MetricSpec(string Key, Statistic Statistic, Func<HistoryDay, double?> Selector,
        bool WrapBedtimeScale = false, bool HigherIsBetter = true);

    private static readonly MetricSpec[] Specs =
    [
        new(FactKeys.SleepMinutes, Statistic.RecencyWeightedMedian, d => d.SleepMinutes),
        new(FactKeys.BedtimeMinutesOfDay, Statistic.RecencyWeightedMedian, d => d.BedtimeMinutesOfDay, WrapBedtimeScale: true),
        new(FactKeys.SleepQuality, Statistic.Mean, d => d.SleepQuality),
        new(FactKeys.Steps, Statistic.Mean, d => d.Steps),
        new(FactKeys.ActiveMinutes, Statistic.Mean, d => d.ActiveMinutes),
        new(FactKeys.RecoveryScore, Statistic.Mean, d => d.RecoveryScore),
        // Lower-is-better signals (client parity: PersonalStateProjector.cs:65 marks stress
        // higherIsBetter:false; the same polarity carries the server's screen/meeting load).
        new(FactKeys.Stress, Statistic.Mean, d => d.Stress, HigherIsBetter: false),
        new(FactKeys.ScreenMinutes, Statistic.Mean, d => d.ScreenMinutes, HigherIsBetter: false),
        new(FactKeys.MeetingMinutes, Statistic.Mean, d => d.MeetingMinutes, HigherIsBetter: false),
    ];

    public static IReadOnlyList<string> MetricKeys { get; } = Specs.Select(s => s.Key).ToList();

    /// <summary>Window length in days.</summary>
    public static int WindowDays(BaselineWindow window) => window switch
    {
        BaselineWindow.Days7 => 7,
        BaselineWindow.Days14 => 14,
        BaselineWindow.Days30 => 30,
        _ => throw new ArgumentOutOfRangeException(nameof(window)),
    };

    /// <summary>Density gate: valid real days required inside the window
    /// (ported from WindowedBaselineService.MinimumSamples).</summary>
    public static int MinimumSamples(BaselineWindow window) => window switch
    {
        BaselineWindow.Days7 => 5,
        BaselineWindow.Days14 => 10,
        BaselineWindow.Days30 => 21,
        _ => throw new ArgumentOutOfRangeException(nameof(window)),
    };

    /// <summary>Confidence ceiling a window can earn (the longest observable window defines how
    /// well "your normal" is known — PrimaryWindowSelector.cs:6-9).</summary>
    public static BaselineConfidence WindowConfidenceCeiling(BaselineWindow window) => window switch
    {
        BaselineWindow.Days7 => BaselineConfidence.Low,
        BaselineWindow.Days14 => BaselineConfidence.Medium,
        BaselineWindow.Days30 => BaselineConfidence.High,
        _ => BaselineConfidence.None,
    };

    /// <summary>Windows considered, longest first (auto-downgrade order — BaselineEngine.cs:145).</summary>
    public static IReadOnlyList<BaselineWindow> WindowsDescending { get; } =
        [BaselineWindow.Days30, BaselineWindow.Days14, BaselineWindow.Days7];

    /// <summary>
    /// Compute every metric's baseline from <paramref name="history"/> as of
    /// <paramref name="asOfDate"/>. The window must be *observable*: history has to reach back to
    /// its first day (coverage), otherwise the metric downgrades or refuses.
    /// </summary>
    public static BaselineResult Compute(IReadOnlyList<HistoryDay> history, DateTime asOfDate)
    {
        ArgumentNullException.ThrowIfNull(history);
        var rows = DedupedByDate(history);
        var entries = new Dictionary<string, MetricBaseline>(StringComparer.Ordinal);
        var trail = new List<TrailEntry>();

        foreach (var spec in Specs)
        {
            var entry = ComputeForMetric(spec, rows, asOfDate);
            entries[spec.Key] = entry;
            trail.Add(entry.Usable
                ? TrailEntry.Of(StageName, $"p1e.baseline.{WindowDays(entry.WindowUsed!.Value)}d", "earned",
                    [EngineMath.Factor("key", spec.Key), EngineMath.Factor("samples", entry.SampleDays.ToString()),
                     EngineMath.Factor("value", entry.Value), EngineMath.Factor("stddev", entry.StdDev),
                     EngineMath.Factor("confidence", entry.Confidence.ToString().ToLowerInvariant())])
                : TrailEntry.Of(StageName, "p1e.baseline.refused", "refused",
                    [EngineMath.Factor("key", spec.Key), EngineMath.Factor("samples", entry.SampleDays.ToString()),
                     EngineMath.Factor("reason", entry.RefusalReason ?? "unknown")]));
        }
        return new BaselineResult(asOfDate.Date, entries, trail);
    }

    /// <summary>Single-metric compute; unknown key throws (a typo must fail loudly, not refuse quietly).</summary>
    public static MetricBaseline ComputeForMetric(string metricKey, IReadOnlyList<HistoryDay> history, DateTime asOfDate)
    {
        var spec = Specs.FirstOrDefault(s => s.Key == metricKey)
                   ?? throw new ArgumentException($"unknown baseline metric '{metricKey}'", nameof(metricKey));
        return ComputeForMetric(spec, DedupedByDate(history), asOfDate);
    }

    /// <summary>One real day = one record: duplicate dates (a repo glitch, not a second
    /// observation) collapse to the most-complete row, so the density gate counts REAL DAYS and
    /// a duplicated day cannot double its statistical weight (client parity:
    /// WindowedBaselineService.cs:81-84 + DedupedByDate at :181-185).</summary>
    private static List<HistoryDay> DedupedByDate(IReadOnlyList<HistoryDay> history) =>
        history.GroupBy(d => d.DateUtc.Date)
            .Select(g => g.OrderByDescending(DayCompleteness).ThenByDescending(d => d.DateUtc).First())
            .OrderBy(d => d.DateUtc.Date)
            .ToList();

    private static int DayCompleteness(HistoryDay d) =>
        new[] { d.SleepMinutes, d.SleepQuality, d.BedtimeMinutesOfDay, d.Steps, d.ActiveMinutes,
                d.RecoveryScore, d.Stress, d.ScreenMinutes, d.MeetingMinutes }
            .Count(v => v.HasValue);

    private static MetricBaseline ComputeForMetric(MetricSpec spec, List<HistoryDay> chronologicalRows, DateTime asOfDate)
    {
        var to = asOfDate.Date;
        foreach (var window in WindowsDescending)
        {
            int days = WindowDays(window);
            var from = to.AddDays(-days);
            // Coverage: the window was only observable if history reaches its first day
            // (WindowedBaselineService.cs:23-25: earliest record <= today - (windowDays - 1)).
            var inWindow = chronologicalRows
                .Where(r => r.DateUtc.Date <= to && r.DateUtc.Date > from)
                .ToList();
            if (chronologicalRows.Count == 0 || chronologicalRows[0].DateUtc.Date > to.AddDays(-(days - 1)))
                continue;

            var samples = inWindow
                .Select(spec.Selector)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .Where(v => SampleValid(spec, v))
                .Select(v => spec.WrapBedtimeScale ? WrapBedtime(v) : v)
                .ToList();

            if (samples.Count < MinimumSamples(window)) continue;

            double value = spec.Statistic == Statistic.RecencyWeightedMedian
                ? RecencyWeightedMedian(samples)
                : samples.Average();
            if (spec.WrapBedtimeScale) value = UnwrapBedtime(value);

            double stddev = EngineMath.StdDev(samples);
            var confidence = Worst(ConfidenceFromSamples(samples.Count), WindowConfidenceCeiling(window));

            return new MetricBaseline(spec.Key, value, stddev, samples.Count, window, confidence,
                spec.HigherIsBetter, RefusalReason: null);
        }

        // Refused: report how much data actually existed inside the LARGEST attempted window
        // (the honest "how little" number — client parity: BaselineEngine.cs:238-241), Value=NaN.
        int observed = chronologicalRows
            .Where(r => r.DateUtc.Date <= to && r.DateUtc.Date > to.AddDays(-WindowDays(WindowsDescending[0])))
            .Select(spec.Selector)
            .Count(v => v.HasValue && SampleValid(spec, v.Value));
        return new MetricBaseline(spec.Key, double.NaN, double.NaN, observed, WindowUsed: null,
            BaselineConfidence.None, spec.HigherIsBetter, "insufficient_samples_or_coverage");
    }

    private static bool SampleValid(MetricSpec spec, double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return false;
        // Bedtime default-zero rows are absent, not midnight (BaselineEngine.cs:309-312).
        if (spec.Key == FactKeys.BedtimeMinutesOfDay) return v > 0;
        if (spec.Key == FactKeys.SleepQuality || spec.Key == FactKeys.RecoveryScore || spec.Key == FactKeys.Stress)
            return v is >= 0 and <= 1;
        return v >= 0;
    }

    /// <summary>Sample-count ladder, identical to the client's Baseline.FromSamples
    /// (Domain/Models/State/PersonalState.cs:59-65).</summary>
    public static BaselineConfidence ConfidenceFromSamples(int n) => n switch
    {
        < 3 => BaselineConfidence.None,
        < 7 => BaselineConfidence.Low,
        < 14 => BaselineConfidence.Medium,
        _ => BaselineConfidence.High,
    };

    private static BaselineConfidence Worst(BaselineConfidence a, BaselineConfidence b) => a <= b ? a : b;

    /// <summary>Weighted median: the newest half of a chronological series carries double weight
    /// (ported from BaselineEngine.cs:320-338 — same algorithm, so the two engines can be
    /// cross-checked on identical input). Empty/all-NaN returns NaN, never 0.</summary>
    public static double RecencyWeightedMedian(IReadOnlyList<double> chronological)
    {
        var v = chronological.Where(x => !double.IsNaN(x)).ToList();
        if (v.Count == 0) return double.NaN;

        var weighted = v.Select((x, i) => (Value: x, Weight: i < v.Count / 2 ? 1 : 2))
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

    /// <summary>Pre-noon bedtimes belong to the previous night: map to a post-noon scale before
    /// averaging (BaselineService.cs:58-62). Zero/NaN stays NaN — never midnight by default.</summary>
    public static double WrapBedtime(double minutesOfDay) =>
        double.IsNaN(minutesOfDay) || minutesOfDay <= 0
            ? double.NaN
            : minutesOfDay < 12 * 60 ? minutesOfDay + 1440 : minutesOfDay;

    /// <summary>PORTED: BaselineService.UnwrapBedtime (Domain client, line 64) — the single
    /// implementation of this unwrap; this lane must not fork the math.</summary>
    public static double UnwrapBedtime(double average) => average >= 1440 ? average - 1440 : average;
}

/// <summary>Which longitudinal window a baseline was computed over (client vocabulary
/// LIVORA.Domain.Enums.BaselineWindow mirrored — the server cannot reference the MAUI project, so
/// the shape is ported; values match so a synced payload reads identically).</summary>
public enum BaselineWindow { Days7 = 0, Days14 = 1, Days30 = 2 }

/// <summary>Client vocabulary port 1:1 from LIVORA.Domain.Enums.BaselineConfidence.</summary>
public enum BaselineConfidence { None = 0, Low = 1, Medium = 2, High = 3 }

/// <summary>
/// One metric's earned baseline. <see cref="WindowUsed"/> null + NaN value = refused; the
/// <see cref="SampleDays"/> still reports how much data existed, which is what lets the UI say
/// "learning (4 days logged)" instead of showing nothing.
/// </summary>
public sealed record MetricBaseline(
    string Key,
    double Value,
    double StdDev,
    int SampleDays,
    BaselineWindow? WindowUsed,
    BaselineConfidence Confidence,
    bool HigherIsBetter,
    string? RefusalReason)
{
    public bool Usable => WindowUsed is not null && !double.IsNaN(Value) && Confidence != BaselineConfidence.None;

    /// <summary>(value - baseline) / baseline, or null when the comparison cannot be honestly drawn
    /// (client parity: MetricState.RelativeDeviation, PersonalState.cs:14-15).</summary>
    public double? DeviationOf(double? value)
    {
        if (!Usable || value is null || double.IsNaN(value.Value)) return null;
        if (Math.Abs(Value) < 1e-9) return null;
        return (value.Value - Value) / Math.Abs(Value);
    }
}

/// <summary>Stage 3 output: everything the state stage is allowed to compare against.</summary>
public sealed record BaselineResult(
    DateTime AsOfDateUtc,
    IReadOnlyDictionary<string, MetricBaseline> ByMetric,
    IReadOnlyList<TrailEntry> Trail)
{
    public MetricBaseline? Find(string key) => ByMetric.TryGetValue(key, out var b) ? b : null;

    public BaselineConfidence ConfidenceOf(string key) =>
        Find(key) is { Usable: true } b ? b.Confidence : BaselineConfidence.None;

    /// <summary>Weakest link over the keys a rule actually used
    /// (PersonalState.cs:139-140 "a chain is as strong as its weakest link").</summary>
    public BaselineConfidence WorstConfidence(IEnumerable<string> keys)
    {
        var worst = BaselineConfidence.High;
        bool saw = false;
        foreach (var k in keys)
        {
            saw = true;
            var c = ConfidenceOf(k);
            if (c < worst) worst = c;
        }
        return saw ? worst : BaselineConfidence.None;
    }
}
