using LIVORA.Application.Abstractions;
using LIVORA.Domain.Constants;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.HealthData;
/// <summary>
/// Wave 2 canonical mock provider. Produces NormalizedDay (provider-neutral) with DataPoints
/// carrying origin/quality/confidence. Deterministic per (user, date) — no flicker, testable.
/// This is the SINGLE generator; the Wave 1 adapter (MockHealthDataSource) derives from it.
/// </summary>
public sealed class SampleHealthProvider : IDataProvider
{
    public string Id => "livora.mock.provider";
    public SourceType SourceType => Domain.Enums.SourceType.Mock;
    public ConnectionState State => ConnectionState.Mock;
    public string DisplayNameKey => "Profile.DataSource.Sample";
    public DataOrigin Origin => DataOrigin.Mock;

    /// <summary>Honest: the mock provides everything except device-grade HR/HRV (no fake values).</summary>
    public DataSourceCapabilities Capabilities =>
        DataSourceCapabilities.Sleep | DataSourceCapabilities.Steps | DataSourceCapabilities.ActiveMinutes |
        DataSourceCapabilities.Recovery | DataSourceCapabilities.Wellness;

    public Task<NormalizedDay?> GetNormalizedDayAsync(DateTime date, UserProfile profile, CancellationToken ct = default)
    {
        var day = date.Date;
        var rnd = new Random(StableSeed(day, profile.Id));

        // Weekly rhythm + slow drift + per-day noise — same math as Wave 1, now typed.
        double weekly = 1.0 + 0.06 * Math.Sin((day.DayOfWeek - DayOfWeek.Monday) * Math.PI / 3.5);
        double slowDrift = 1.0 + 0.04 * Math.Sin(day.DayOfYear * 0.35);
        double noise = 0.94 + rnd.NextDouble() * 0.12;

        var wakeMinutes = profile.PreferredWakeTime.TotalMinutes + (rnd.NextDouble() - 0.5) * 40;
        double sleepMinutes = 440 * weekly * slowDrift * noise;
        double bedtimeMinutes = wakeMinutes - sleepMinutes; // may wrap past midnight; normalizer handles
        if (bedtimeMinutes < 0) bedtimeMinutes += 1440;

        double activityScale = profile.ActivityLevel switch
        {
            ActivityLevel.Sedentary => 0.6,
            ActivityLevel.Light => 0.8,
            ActivityLevel.Moderate => 1.0,
            ActivityLevel.Active => 1.2,
            _ => 1.35,
        };

        var ts = day.AddHours(12); // normalized-day timestamp semantics: measured over that calendar day

        return Task.FromResult<NormalizedDay?>(new NormalizedDay
        {
            Date = day,
            Origin = Origin,
            SleepMinutes = Point(sleepMinutes, "minutes", ts, conf: 0.95),
            SleepQuality = Point(Math.Clamp(0.72 + 0.18 * noise + 0.04 * Math.Sin(day.DayOfYear * 0.2), 0, 1), "score01", ts, conf: 0.9),
            SleepConsistency = Point(Math.Clamp(0.78 + 0.12 * Math.Sin(day.DayOfYear * 0.11), 0, 1), "score01", ts, conf: 0.85),
            BedtimeMinutesOfDay = Point(bedtimeMinutes, "minutesOfDay", ts, conf: 0.95),
            WakeMinutesOfDay = Point(wakeMinutes, "minutesOfDay", ts, conf: 0.95),
            Steps = Point(Math.Round(AppConstants.DailyStepTarget * weekly * noise * activityScale), "steps", ts),
            ActiveMinutes = Point(Math.Round(AppConstants.DailyActiveMinutesTarget * weekly * noise), "minutes", ts),
            RecoveryScore = Point(Math.Clamp(0.62 + 0.2 * noise + 0.08 * Math.Sin(day.DayOfYear * 0.27), 0, 1), "score01", ts, conf: 0.85),
            RestingHeartRate = null,   // device-grade signal — mock must not fabricate
            HrvMs = null,              // same
            Stress = Point(Math.Clamp(0.42 + 0.2 * Math.Sin(day.DayOfYear * 0.19) + (rnd.NextDouble() - 0.5) * 0.15, 0, 1), "score01", ts, conf: 0.7),
            Mood = Point(Math.Clamp(0.68 + 0.15 * Math.Sin(day.DayOfYear * 0.13) + (rnd.NextDouble() - 0.5) * 0.1, 0, 1), "score01", ts, conf: 0.7),
            Energy = Point(Math.Clamp(0.6 + 0.22 * noise + 0.06 * Math.Sin(day.DayOfYear * 0.31), 0, 1), "score01", ts, conf: 0.7),
        });
    }

    private static DataPoint Point(double v, string unit, DateTime ts, double conf = 1.0) => new()
    {
        Value = v, Timestamp = ts, Origin = DataOrigin.Mock, Quality = DataQuality.Complete, Unit = unit, Confidence = conf,
    };

    internal static int StableSeed(DateTime day, string userId)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + day.Year;
            hash = hash * 31 + day.Month;
            hash = hash * 31 + day.Day;
            foreach (var c in userId) hash = hash * 31 + c;
            return hash;
        }
    }
}
