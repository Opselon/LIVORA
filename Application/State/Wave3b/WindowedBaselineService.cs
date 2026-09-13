using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.State.Wave3b;

/// <summary>
/// Wave 3b lane 05: windowed longitudinal baselines (7/14/30 days) computed from
/// <see cref="IHistoryRepository"/>, LAYERED beside — not replacing — the 28-day
/// <see cref="BaselineService"/>. The old engine stays the app's default; this one answers
/// "how solid is my 'normal' over the last week / two weeks / month?".
///
/// SAMPLE GATES (documented honesty; a window is trustworthy only when BOTH pass):
///   coverage — the user's history reaches back to the window's first day
///              (earliest record date &lt;= today - (windowDays - 1)); and
///   density  — at least <see cref="MinimumSamples"/> VALID REAL DAYS inside the window:
///              7d ≥ 5, 14d ≥ 10, 30d ≥ 21 (≈70% logged — the same density that earns High
///              confidence for the 28-day engine's "2+ weeks: real baseline" rule).
/// Coverage exists so a 25-day-old account can never show a "30-day baseline" — the window
/// was never observable. Density exists so "no 30-day baseline from 2 days of data" holds even
/// once the calendar reaches 30 days. A failed gate yields <c>null</c> for that window —
/// never a fake number (honesty is product law).
///
/// SEMANTICS MATCH THE EXISTING ENGINE: each window's baseline is produced by the SAME
/// <see cref="Baseline.FromSamples(string, IReadOnlyList&lt;double&gt;)"/> math and the SAME
/// per-metric sample extraction (validity filters, bedtime wrap-around) as
/// <see cref="BaselineService"/>; a pinned test asserts value/std-dev/confidence equality
/// against both <c>Baseline.FromSamples</c> and the 28-day engine on identical sample sets.
/// </summary>
public sealed class WindowedBaselineService
{
    private readonly IHistoryRepository _history;
    private Dictionary<string, WindowedBaselineResult>? _cache;
    private DateTime _cacheDay = DateTime.MinValue;

    public WindowedBaselineService(IHistoryRepository history) => _history = history;

    /// <summary>Minimum valid REAL days per window (density gate). Deterministic, read-only.</summary>
    public static readonly IReadOnlyDictionary<BaselineWindow, int> MinimumSamples =
        new Dictionary<BaselineWindow, int>
        {
            [BaselineWindow.Days7] = 5,
            [BaselineWindow.Days14] = 10,
            [BaselineWindow.Days30] = 21,
        };

    /// <summary>The 12 metrics the 28-day engine tracks, in BaselineService's own declaration
    /// order — the canonical iteration order for every list this lane emits (determinism).
    /// BaselineService.cs is read-only for this lane, so the list is mirrored here and pinned
    /// against the engine by a test (GetBaselinesAsync keys must equal this set).</summary>
    public static readonly IReadOnlyList<string> MetricKeys = new[]
    {
        Metrics.SleepMinutes,
        Metrics.SleepQuality,
        Metrics.SleepConsistency,
        Metrics.BedtimeMinutes,
        Metrics.Steps,
        Metrics.ActiveMinutes,
        Metrics.RecoveryScore,
        Metrics.RestingHeartRate,
        Metrics.HrvMs,
        Metrics.Stress,
        Metrics.Mood,
        Metrics.Energy,
    };

    public static int MinimumSamplesFor(BaselineWindow window) => MinimumSamples[window];

    /// <summary>True when BOTH gates passed for this (metric, window) pair.</summary>
    public static bool PassesGate(WindowedBaselineResult result, BaselineWindow window) =>
        result.ByWindow.TryGetValue(window, out var b) && b is not null;

    /// <summary>Baselines for every metric × every declared window. Cached per calendar day,
    /// same convention as <see cref="BaselineService"/>.</summary>
    public async Task<Dictionary<string, WindowedBaselineResult>> GetAllAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        if (_cache is not null && _cacheDay == today) return _cache;

        var history = await _history.GetAllAsync();
        // One real day = one record: duplicate dates (a repo glitch, not a second observation)
        // collapse to the most-complete record so gates count REAL days, not rows.
        var days = DedupedByDate(history);
        // Coverage anchor: how far back the user's history actually reaches.
        var earliest = days.Count == 0 ? (DateTime?)null : days.Min(r => r.Date.Date);

        var result = new Dictionary<string, WindowedBaselineResult>();
        foreach (var metric in MetricKeys)
        {
            var byWindow = new Dictionary<BaselineWindow, Baseline?>();
            var samplesByWindow = new Dictionary<BaselineWindow, int>();
            foreach (var window in Wave3bWindows.Declared)
            {
                int days_w = Wave3bWindows.Days(window);
                var windowRecords = days
                    .Where(r => r.Date.Date <= today && r.Date.Date > today.AddDays(-days_w))
                    .ToList();
                var samples = SamplesFor(metric, windowRecords);
                samplesByWindow[window] = samples.Count;

                bool coverage = earliest is not null && earliest.Value <= today.AddDays(-(days_w - 1));
                bool density = samples.Count >= MinimumSamplesFor(window);
                byWindow[window] = coverage && density
                    // VERBATIM existing math — same static the 28-day engine calls.
                    ? Baseline.FromSamples(metric, samples)
                    : null;
            }
            result[metric] = new WindowedBaselineResult
            {
                MetricKey = metric,
                ByWindow = byWindow,
                SampleDaysByWindow = samplesByWindow,
            };
        }

        _cache = result;
        _cacheDay = today;
        return result;
    }

    /// <summary>Convenience: one metric's windowed baselines.</summary>
    public async Task<WindowedBaselineResult> GetForMetricAsync(string metricKey, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.TryGetValue(metricKey, out var r)
            ? r
            : new WindowedBaselineResult
            {
                MetricKey = metricKey,
                ByWindow = Wave3bWindows.Declared.ToDictionary(w => w, _ => (Baseline?)null),
                SampleDaysByWindow = Wave3bWindows.Declared.ToDictionary(w => w, _ => 0),
            };
    }

    /// <summary>Sync accessor mirroring <see cref="BaselineService.Get"/>: returns a gated
    /// window's baseline only when trustworthy; null until the first async load (UI always awaits).</summary>
    public Baseline? Get(string metricKey, BaselineWindow window) =>
        _cache is not null && _cache.TryGetValue(metricKey, out var r) && Wave3bTrust.IsTrustworthy(r[window])
            ? r[window]
            : null;

    /// <summary>Per-metric sample extraction — the SAME filters BaselineService applies per
    /// metric key, so a window's sample list here is identical to what the 28-day engine would
    /// derive from the same records.</summary>
    internal static List<double> SamplesFor(string metricKey, IReadOnlyList<DailyHistoryRecord> window) =>
        metricKey switch
        {
            Metrics.SleepMinutes => Valid(window.Select(r => r.SleepMinutes)),
            Metrics.SleepQuality => Valid(window.Select(r => r.SleepQuality)),
            Metrics.SleepConsistency => Valid(window.Select(r => r.SleepConsistency)),
            Metrics.BedtimeMinutes => WrapAround(window.Select(r => r.BedtimeMinutesOfDay)),
            Metrics.Steps => Valid(window.Select(r => (double)r.Steps)),
            Metrics.ActiveMinutes => Valid(window.Select(r => (double)r.ActiveMinutes)),
            Metrics.RecoveryScore => Valid(window.Select(r => r.RecoveryScore)),
            Metrics.RestingHeartRate => Valid(window.Where(r => r.RestingHeartRate.HasValue).Select(r => r.RestingHeartRate!.Value)),
            Metrics.HrvMs => Valid(window.Where(r => r.HrvMs.HasValue).Select(r => r.HrvMs!.Value)),
            Metrics.Stress => Valid(window.Select(r => r.Stress)),
            Metrics.Mood => Valid(window.Select(r => r.Mood)),
            Metrics.Energy => Valid(window.Select(r => r.Energy)),
            _ => new List<double>(),
        };

    private static List<double> Valid(IEnumerable<double> values) =>
        values.Where(v => !double.IsNaN(v) && v >= 0).ToList();

    /// <summary>Pointer: identical math to <c>BaselineService.WrapAround</c> (private there; the
    /// abstraction is frozen, so the two lines are restated instead of inventing a new rule).
    /// Bedtimes near midnight break naive averaging (23:50 vs 00:40) — map to a post-noon scale;
    /// the consumer unwraps via <see cref="UnwrapBedtime"/>.</summary>
    private static List<double> WrapAround(IEnumerable<double> minutesOfDay) =>
        minutesOfDay
            .Where(v => !double.IsNaN(v))
            .Select(v => v < 12 * 60 ? v + 1440 : v) // early-morning bedtimes belong to the previous night
            .ToList();

    /// <summary>Bedtime metrics are stored wrapped (see above); unwrap to minutes-of-day for
    /// display by delegating to the existing engine's public static — one rule, not two.</summary>
    public static double UnwrapBedtime(double wrappedAverage) => BaselineService.UnwrapBedtime(wrappedAverage);

    private static List<DailyHistoryRecord> DedupedByDate(IReadOnlyList<DailyHistoryRecord> records) =>
        records
            .GroupBy(r => r.Date.Date)
            .Select(g => g.OrderByDescending(r => r.Completeness).First())
            .ToList();
}
