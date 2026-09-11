using LIVORA.Core.Enums;
using LIVORA.Core.Interfaces;
using LIVORA.Core.Models;

namespace LIVORA.Services.DataSources;

/// <summary>
/// Phase 1 mock health source. Generates deterministic, gently-varying sample data derived from
/// the user profile and the date — no randomness that flickers between visits. Always reports
/// SourceType.Mock so the UI can label it honestly.
/// </summary>
public sealed class MockHealthDataSource : IHealthDataSource
{
    public string Id => "livora.mock.health";
    public SourceType SourceType => Core.Enums.SourceType.Mock;
    public ConnectionState State => ConnectionState.Mock;

    public Task<HealthSnapshot?> GetSnapshotAsync(DateTime date, UserProfile profile)
    {
        var day = date.Date;
        int seed = StableSeed(day, profile.Id);
        var rnd = new Random(seed);

        // Weekly rhythm: weekends lighter, mid-week peaks. Deterministic per (day, user).
        double weekly = 1.0 + 0.06 * Math.Sin((day.DayOfWeek - DayOfWeek.Monday) * Math.PI / 3.5);
        double slowDrift = 1.0 + 0.04 * Math.Sin(day.DayOfYear * 0.35);
        double noise = 0.94 + rnd.NextDouble() * 0.12;

        var wake = profile.PreferredWakeTime + TimeSpan.FromMinutes((rnd.NextDouble() - 0.5) * 40);
        var sleepMinutes = (int)Math.Round(440 * weekly * slowDrift * noise);
        var bedtime = day.AddHours(wake.TotalHours).AddMinutes(-sleepMinutes);

        var snapshot = new HealthSnapshot
        {
            Date = day,
            Source = SourceType.Mock,
            Sleep = new SleepData
            {
                Date = day,
                DurationMinutes = sleepMinutes,
                Quality = Math.Clamp(0.72 + 0.18 * noise + 0.04 * Math.Sin(day.DayOfYear * 0.2), 0, 1),
                Bedtime = bedtime,
                WakeTime = day + wake,
                Consistency = Math.Clamp(0.78 + 0.12 * Math.Sin(day.DayOfYear * 0.11), 0, 1),
            },
            Activity = new ActivityData
            {
                Date = day,
                Steps = (int)Math.Round(AppConstants.DailyStepTarget * weekly * noise * (profile.ActivityLevel switch
                {
                    ActivityLevel.Sedentary => 0.6,
                    ActivityLevel.Light => 0.8,
                    ActivityLevel.Moderate => 1.0,
                    ActivityLevel.Active => 1.2,
                    _ => 1.35,
                })),
                ActiveMinutes = (int)Math.Round(AppConstants.DailyActiveMinutesTarget * weekly * noise),
                LoadEstimate = Math.Clamp(weekly * noise * 0.9, 0, 1.2),
            },
            Recovery = new RecoveryData
            {
                Date = day,
                RecoveryScore = Math.Clamp(0.62 + 0.2 * noise + 0.08 * Math.Sin(day.DayOfYear * 0.27), 0, 1),
                // Placeholders — real providers fill these later. Nulls keep the UI honest.
                RestingHeartRate = null,
                HrvMilliseconds = null,
            },
            Wellness = new WellnessState
            {
                Date = day,
                Stress = Math.Clamp(0.42 + 0.2 * Math.Sin(day.DayOfYear * 0.19) + (rnd.NextDouble() - 0.5) * 0.15, 0, 1),
                Mood = Math.Clamp(0.68 + 0.15 * Math.Sin(day.DayOfYear * 0.13) + (rnd.NextDouble() - 0.5) * 0.1, 0, 1),
                Energy = Math.Clamp(0.6 + 0.22 * noise + 0.06 * Math.Sin(day.DayOfYear * 0.31), 0, 1),
            },
        };

        return Task.FromResult<HealthSnapshot?>(snapshot);
    }

    private static int StableSeed(DateTime day, string userId)
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
