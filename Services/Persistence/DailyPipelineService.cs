using LIVORA.Core.Constants;
using LIVORA.Core.Enums;
using LIVORA.Core.Interfaces;
using LIVORA.Core.Models;

namespace LIVORA.Services.Persistence;

/// <summary>
/// Aggregates data sources -> normalized snapshot -> daily state -> intelligence.
/// This is the internal pipeline: UI binds to TodayViewModel, never to raw provider data.
/// </summary>
public sealed class DailyPipelineService
{
    private readonly IHealthDataSource _healthSource;
    private readonly IIntelligenceService _intelligence;
    private readonly IRepository<Goal> _goals;
    private readonly IRepository<Habit> _habits;
    private readonly IDateTimeProvider _clock;

    public DailyPipelineService(
        IHealthDataSource healthSource,
        IIntelligenceService intelligence,
        IRepository<Goal> goals,
        IRepository<Habit> habits,
        IDateTimeProvider clock)
    {
        _healthSource = healthSource;
        _intelligence = intelligence;
        _goals = goals;
        _habits = habits;
        _clock = clock;
    }

    public async Task<DailyState> BuildDailyStateAsync(UserProfile? profile)
    {
        var today = _clock.Today;
        profile ??= new UserProfile();
        var snapshot = await _healthSource.GetSnapshotAsync(today, profile) ?? new HealthSnapshot { Date = today, Source = SourceType.Mock };

        var goals = (await _goals.GetAllAsync()).Where(g => !g.IsArchived).ToList();
        var habits = await _habits.GetAllAsync();

        // Composite readiness: sleep (vs target), recovery, activity — simple weighted mean, Phase 1.
        double sleepScore = Math.Clamp(snapshot.Sleep.DurationMinutes / (AppConstants.SleepTargetHours * 60.0), 0, 1);
        double activityScore = Math.Clamp(snapshot.Activity.ActiveMinutes / (double)AppConstants.DailyActiveMinutesTarget, 0, 1);
        double dailyScore = Math.Clamp(0.4 * sleepScore + 0.35 * snapshot.Recovery.RecoveryScore + 0.25 * activityScore, 0, 1);

        return new DailyState
        {
            Date = today,
            Health = snapshot,
            DailyScore = dailyScore,
            Goals = goals,
            Habits = habits,
        };
    }
}
