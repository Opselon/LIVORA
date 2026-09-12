using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.HealthData;
/// <summary>
/// Normalization layer: sanity-checks provider output, marks stale feeds, clamps invalid
/// values. Intelligence must never trust raw provider data that skipped this stage.
/// Freshness windows encode "sleep data older than N days is history, not today".
/// </summary>
public sealed class DataNormalizer : IDataNormalizer
{
    public static readonly TimeSpan SleepMaxAge = TimeSpan.FromDays(2);   // nightly feed
    public static readonly TimeSpan ActivityMaxAge = TimeSpan.FromDays(1); // daily counters
    public static readonly TimeSpan RecoveryMaxAge = TimeSpan.FromDays(2);
    public static readonly TimeSpan WellnessMaxAge = TimeSpan.FromDays(3);

    public NormalizedDay Normalize(NormalizedDay raw, DateTime now)
    {
        var today = now.Date;

        return new NormalizedDay
        {
            Date = raw.Date,
            Origin = raw.Origin,
            SleepMinutes = Fix(raw.SleepMinutes, 0, 16 * 60, SleepMaxAge, today),
            SleepQuality = Fix(raw.SleepQuality, 0, 1, SleepMaxAge, today),
            SleepConsistency = Fix(raw.SleepConsistency, 0, 1, SleepMaxAge, today),
            BedtimeMinutesOfDay = Fix(raw.BedtimeMinutesOfDay, 0, 24 * 60, SleepMaxAge, today),
            WakeMinutesOfDay = Fix(raw.WakeMinutesOfDay, 0, 24 * 60, SleepMaxAge, today),
            Steps = Fix(raw.Steps, 0, 100_000, ActivityMaxAge, today),
            ActiveMinutes = Fix(raw.ActiveMinutes, 0, 16 * 60, ActivityMaxAge, today),
            RecoveryScore = Fix(raw.RecoveryScore, 0, 1, RecoveryMaxAge, today),
            RestingHeartRate = raw.RestingHeartRate is null ? null : Fix(raw.RestingHeartRate, 25, 150, RecoveryMaxAge, today),
            HrvMs = raw.HrvMs is null ? null : Fix(raw.HrvMs, 5, 300, RecoveryMaxAge, today),
            Stress = Fix(raw.Stress, 0, 1, WellnessMaxAge, today),
            Mood = Fix(raw.Mood, 0, 1, WellnessMaxAge, today),
            Energy = Fix(raw.Energy, 0, 1, WellnessMaxAge, today),
        };
    }

    private static DataPoint Fix(DataPoint p, double min, double max, TimeSpan maxAge, DateTime today)
    {
        if (p is null || double.IsNaN(p.Value)) return DataPoint.Missing(p?.Timestamp ?? today, p?.Origin ?? DataOrigin.Mock);

        // Impossible values are Invalid — they must be excluded from baselines and insights.
        if (p.Value < min || p.Value > max)
            return new DataPoint { Value = p.Value, Timestamp = p.Timestamp, Origin = p.Origin, Quality = DataQuality.Invalid, Confidence = 0, Unit = p.Unit };

        // Stale: value exists but the feed stopped updating.
        return p.WithMaxAge(today, maxAge);
    }
}
