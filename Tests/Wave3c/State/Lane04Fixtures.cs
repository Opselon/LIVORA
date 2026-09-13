using LIVORA.Application.State;
using LIVORA.Application.State.Wave3b;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;

namespace LIVORA.Tests.Wave3c;

/// <summary>Fixture helpers shared by the lane-04 Wave 3c suites.</summary>
internal static class Lane04Fixtures
{
    public static readonly DateTime Epoch = new(2026, 9, 1);

    /// <summary>One day of history with sane non-zero defaults (everything present, flat).</summary>
    public static DailyHistoryRecord Day(int offset, double sleep = 450, double bedtime = 1380,
        int steps = 8000, int active = 30, double recovery = 0.7, double energy = 0.8,
        double stress = 0.2, double mood = 0.7, double quality = 0.75, double consistency = 0.8)
        => new()
        {
            Date = Epoch.AddDays(offset),
            Origin = nameof(DataOrigin.HealthConnect),
            Completeness = 1,
            SleepMinutes = sleep,
            SleepQuality = quality,
            SleepConsistency = consistency,
            BedtimeMinutesOfDay = bedtime,
            Steps = steps,
            ActiveMinutes = active,
            RecoveryScore = recovery,
            RestingHeartRate = 58,
            HrvMs = 42,
            Stress = stress,
            Mood = mood,
            Energy = energy,
        };

    /// <summary><paramref name="n"/> consecutive flat days starting at Epoch.</summary>
    public static List<DailyHistoryRecord> Flat(int n) =>
        Enumerable.Range(0, n).Select(i => Day(i)).ToList();

    public static BaselineEntry Usable(string key, double value, int samples,
        BaselineConfidence confidence = BaselineConfidence.High,
        BaselineWindow window = BaselineWindow.Days14)
        => new(key, new Baseline
        {
            MetricKey = key, Value = value, StdDev = value * 0.05,
            SampleDays = samples, Confidence = confidence,
        }, window);

    public static BaselineEntry Refused(string key, int samples)
        => new(key, new Baseline
        {
            MetricKey = key, Value = double.NaN, StdDev = double.NaN,
            SampleDays = samples, Confidence = BaselineConfidence.None,
        }, WindowUsed: null);

    /// <summary>A BaselineSet with High-confidence baselines for every engine-known metric.</summary>
    public static BaselineSet AllHighSet(double sleepMinutes = 450) => new BaselineSet(
        BaselineEngine.MetricKeys.Select(k => Usable(k, k switch
        {
            Metrics.SleepMinutes => sleepMinutes,
            Metrics.BedtimeMinutes => 1380,
            Metrics.Steps => 8000,
            Metrics.ActiveMinutes => 30,
            _ => 0.7,
        }, 14)));
}
