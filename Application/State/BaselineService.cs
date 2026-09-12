using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.State;
/// <summary>
/// Personal baseline engine: rolling window of the user's OWN history, confidence-gated.
/// Never compares to population averages. Recalculates from whatever history exists.
/// </summary>
public sealed class BaselineService : IBaselineService
{
    private readonly IHistoryRepository _history;
    private Dictionary<string, Baseline>? _cache;
    private DateTime _cacheDay = DateTime.MinValue;

    public const int WindowDays = 28;

    public BaselineService(IHistoryRepository history) => _history = history;

    public async Task<Dictionary<string, Baseline>> GetBaselinesAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        if (_cache is not null && _cacheDay == today) return _cache;

        var history = await _history.GetAllAsync();
        var window = history
            .Where(r => r.Date.Date <= today && r.Date.Date > today.AddDays(-WindowDays))
            .ToList();

        _cache = new Dictionary<string, Baseline>
        {
            [Metrics.SleepMinutes] = Baseline.FromSamples(Metrics.SleepMinutes, Valid(window.Select(r => r.SleepMinutes), DataQuality.Complete)),
            [Metrics.SleepQuality] = Baseline.FromSamples(Metrics.SleepQuality, Valid(window.Select(r => r.SleepQuality), DataQuality.Complete)),
            [Metrics.SleepConsistency] = Baseline.FromSamples(Metrics.SleepConsistency, Valid(window.Select(r => r.SleepConsistency), DataQuality.Complete)),
            [Metrics.BedtimeMinutes] = Baseline.FromSamples(Metrics.BedtimeMinutes, WrapAround(window.Select(r => r.BedtimeMinutesOfDay))),
            [Metrics.Steps] = Baseline.FromSamples(Metrics.Steps, Valid(window.Select(r => (double)r.Steps), DataQuality.Complete)),
            [Metrics.ActiveMinutes] = Baseline.FromSamples(Metrics.ActiveMinutes, Valid(window.Select(r => (double)r.ActiveMinutes), DataQuality.Complete)),
            [Metrics.RecoveryScore] = Baseline.FromSamples(Metrics.RecoveryScore, Valid(window.Select(r => r.RecoveryScore), DataQuality.Complete)),
            [Metrics.RestingHeartRate] = Baseline.FromSamples(Metrics.RestingHeartRate, Valid(window.Where(r => r.RestingHeartRate.HasValue).Select(r => r.RestingHeartRate!.Value), DataQuality.Complete)),
            [Metrics.HrvMs] = Baseline.FromSamples(Metrics.HrvMs, Valid(window.Where(r => r.HrvMs.HasValue).Select(r => r.HrvMs!.Value), DataQuality.Complete)),
            [Metrics.Stress] = Baseline.FromSamples(Metrics.Stress, Valid(window.Select(r => r.Stress), DataQuality.Complete)),
            [Metrics.Mood] = Baseline.FromSamples(Metrics.Mood, Valid(window.Select(r => r.Mood), DataQuality.Complete)),
            [Metrics.Energy] = Baseline.FromSamples(Metrics.Energy, Valid(window.Select(r => r.Energy), DataQuality.Complete)),
        };
        _cacheDay = today;
        return _cache;
    }

    /// <summary>Sync accessor for rule evaluation; returns null until first async load (UI always awaits).</summary>
    public Baseline? Get(string metricKey) =>
        _cache is not null && _cache.TryGetValue(metricKey, out var b) && b.Confidence != BaselineConfidence.None ? b : null;

    private static List<double> Valid(IEnumerable<double> values, DataQuality _) =>
        values.Where(v => !double.IsNaN(v) && v >= 0).ToList();

    /// <summary>Bedtimes near midnight break naive averaging (23:50 vs 00:40): map to a
    /// post-noon scale, average, unwrap. Deterministic and small.</summary>
    private static List<double> WrapAround(IEnumerable<double> minutesOfDay) =>
        minutesOfDay
            .Where(v => !double.IsNaN(v))
            .Select(v => v < 12 * 60 ? v + 1440 : v) // early-morning bedtimes belong to the previous night
            .ToList();

    public static double UnwrapBedtime(double avg) => avg >= 1440 ? avg - 1440 : avg;
}
