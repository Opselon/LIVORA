using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;

namespace LIVORA.Application.Insights;
/// <summary>
/// Weekly review built purely from persisted history + the simple trend engine.
/// Honest about sample size: fewer than 7 days of history lowers confidence and bullets.
/// </summary>
public sealed class WeeklySummaryService : IWeeklySummaryService
{
    private readonly IHistoryRepository _history;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Goal> _goals;
    private readonly ITrendService _trends;
    private readonly IDateTimeProvider _clock;

    public WeeklySummaryService(
        IHistoryRepository history,
        IRepository<Habit> habits,
        IRepository<Goal> goals,
        ITrendService trends,
        IDateTimeProvider clock)
    {
        _history = history;
        _habits = habits;
        _goals = goals;
        _trends = trends;
        _clock = clock;
    }

    public async Task<WeeklySummary?> BuildLastWeekAsync(CancellationToken ct = default)
    {
        var today = _clock.Today;
        var weekEnd = today.AddDays(-1);          // yesterday
        var weekStart = weekEnd.AddDays(-6);      // 7-day window that just closed
        var priorStart = weekStart.AddDays(-7);

        var history = await _history.GetAllAsync();
        var thisWeek = history.Where(r => r.Date >= weekStart && r.Date <= weekEnd).OrderBy(r => r.Date).ToList();
        var priorWeek = history.Where(r => r.Date >= priorStart && r.Date < weekStart).OrderBy(r => r.Date).ToList();

        if (thisWeek.Count < 3) return null;     // not enough to say anything honest

        var sleepTrend = _trends.Compute(thisWeek.Select(r => r.SleepMinutes).ToList(), higherIsBetter: true);
        var activityTrend = _trends.Compute(thisWeek.Select(r => (double)r.Steps).ToList(), higherIsBetter: true);
        var recoveryTrend = _trends.Compute(thisWeek.Select(r => r.RecoveryScore).ToList(), higherIsBetter: true);
        var stressTrend = _trends.Compute(thisWeek.Select(r => r.Stress).ToList(), higherIsBetter: false);

        var improvements = new List<string>();
        var declines = new List<string>();
        void Categorize(TrendDirection t, string improvingKey, string declineKey)
        {
            switch (t)
            {
                case TrendDirection.Improving: improvements.Add(improvingKey); break;
                case TrendDirection.Declining: declines.Add(declineKey); break;
            }
        }
        Categorize(sleepTrend, "Weekly.Up.Sleep", "Weekly.Down.Sleep");
        Categorize(activityTrend, "Weekly.Up.Activity", "Weekly.Down.Activity");
        Categorize(recoveryTrend, "Weekly.Up.Recovery", "Weekly.Down.Recovery");
        Categorize(stressTrend, "Weekly.Up.Stress", "Weekly.Down.Stress");

        var habits = await _habits.GetAllAsync();
        double habitConsistency = habits.Count == 0 ? 0 :
            habits.Average(h => h.Completions.Count(d => d.Date >= weekStart && d.Date <= weekEnd) / 7.0);

        var goals = (await _goals.GetAllAsync()).Where(g => !g.IsArchived).ToList();
        double goalFraction = goals.Count == 0 ? 0 : goals.Average(g => g.Fraction);

        // Longest active streak in the window (single most motivating number).
        int streak = habits.Select(h => h.CurrentStreak).DefaultIfEmpty(0).Max();

        // Focus for next week: worst domain, or momentum protection if nothing is declining.
        var (focusKey, focusArgs) = (stressTrend, sleepTrend) switch
        {
            (TrendDirection.Declining, _) => ("Weekly.Focus.Stress", Array.Empty<object>()),
            (_, TrendDirection.Declining) => ("Weekly.Focus.Sleep", Array.Empty<object>()),
            _ => habitConsistency >= 0.6
                ? ("Weekly.Focus.Momentum", new object[] { streak })
                : ("Weekly.Focus.OneHabit", Array.Empty<object>()),
        };

        BaselineConfidence confidence = thisWeek.Count >= 7 && priorWeek.Count >= 7
            ? BaselineConfidence.High
            : thisWeek.Count >= 6 ? BaselineConfidence.Medium : BaselineConfidence.Low;

        return new WeeklySummary
        {
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            SleepTrend = sleepTrend,
            ActivityTrend = activityTrend,
            RecoveryTrend = recoveryTrend,
            StressTrend = stressTrend,
            HabitConsistency = Math.Clamp(habitConsistency, 0, 1),
            GoalProgressFraction = Math.Clamp(goalFraction, 0, 1),
            StreakDays = streak,
            ImprovementKeys = improvements,
            DeclineKeys = declines,
            FocusKey = focusKey,
            FocusArgs = focusArgs,
            Confidence = confidence,
        };
    }
}
