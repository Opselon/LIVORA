namespace Livora.Server.Infrastructure.Engines.Decision;

/// <summary>
/// PURPOSE: server restatement of Application/Insights/WeeklySummaryService.cs — the honest weekly
///          look-back. The refusal rule is the product-law reference for the whole lane: fewer than
///          <see cref="NumericRules.WeeklyMinDays"/> days in the closed window => the engine returns
///          UNAVAILABLE and says nothing. Same for trends: &lt;5 samples is InsufficientData, never a
///          direction guessed from noise.
/// OWNER: Agent 10+11 (lane w4-p1e-engines). Pure; the caller injects the as-of instant.
/// INVARIANTS:
///   - window = the 7 days that just CLOSED (yesterday back 6), prior window for the confidence
///     ladder (7+7 => High, 6+ => Medium, else Low) — verbatim client rule
///   - stress trend is inverted (lower stress = improving): same higherIsBetter flag as client
///   - improvement/decline bullets are localization keys, never prose
///   - focus choice ladder (stress declining > sleep declining > momentum/one-habit) ported verbatim
/// </summary>
public static class EngineWeeklyReview
{
    public static WeeklyReviewResult Review(DecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var asOf = input.AsOfUtc.Date;
        var weekEnd = asOf.AddDays(-1);
        var weekStart = weekEnd.AddDays(-(NumericRules.WeeklyWindowDays - 1));
        var priorStart = weekStart.AddDays(-NumericRules.WeeklyWindowDays);

        var all = input.History.Where(r => r.DateUtc.Date <= weekEnd).OrderBy(r => r.DateUtc.Date).ToList();
        var thisWeek = all.Where(r => r.DateUtc.Date >= weekStart && r.DateUtc.Date <= weekEnd).ToList();
        var priorWeek = all.Where(r => r.DateUtc.Date >= priorStart && r.DateUtc.Date < weekStart).ToList();

        if (thisWeek.Count < NumericRules.WeeklyMinDays)
        {
            return new WeeklyReviewResult(
                Available: false, RefusalReason: "insufficient_data", DaysPresentInWindow: thisWeek.Count,
                weekStart, weekEnd,
                EngineTrend.InsufficientData, EngineTrend.InsufficientData,
                EngineTrend.InsufficientData, EngineTrend.InsufficientData,
                0, 0, Array.Empty<string>(), Array.Empty<string>(),
                FocusKey: "Weekly.NoData", FocusArgs: Array.Empty<object>(),
                Confidence: EngineBaselineConfidence.None);
        }

        double? Sel(EngineDayRecord r, string key) => key switch
        {
            EngineStateComputer.EngineMetrics.SleepMinutes => r.SleepMinutes,
            EngineStateComputer.EngineMetrics.Steps => r.Steps,
            EngineStateComputer.EngineMetrics.RecoveryScore => r.RecoveryScore,
            EngineStateComputer.EngineMetrics.Stress => r.Stress,
            _ => null,
        };
        var sleepTrend = NumericRules.ClassifyTrend(
            thisWeek.Select(r => Sel(r, EngineStateComputer.EngineMetrics.SleepMinutes) ?? double.NaN).ToList(), true);
        var activityTrend = NumericRules.ClassifyTrend(
            thisWeek.Select(r => Sel(r, EngineStateComputer.EngineMetrics.Steps) ?? double.NaN).ToList(), true);
        var recoveryTrend = NumericRules.ClassifyTrend(
            thisWeek.Select(r => Sel(r, EngineStateComputer.EngineMetrics.RecoveryScore) ?? double.NaN).ToList(), true);
        var stressTrend = NumericRules.ClassifyTrend(
            thisWeek.Select(r => Sel(r, EngineStateComputer.EngineMetrics.Stress) ?? double.NaN).ToList(), false);

        var improvements = new List<string>();
        var declines = new List<string>();
        void Categorize(EngineTrend t, string up, string down)
        {
            if (t == EngineTrend.Improving) improvements.Add(up);
            else if (t == EngineTrend.Declining) declines.Add(down);
        }
        Categorize(sleepTrend, "Weekly.Up.Sleep", "Weekly.Down.Sleep");
        Categorize(activityTrend, "Weekly.Up.Activity", "Weekly.Down.Activity");
        Categorize(recoveryTrend, "Weekly.Up.Recovery", "Weekly.Down.Recovery");
        Categorize(stressTrend, "Weekly.Up.Stress", "Weekly.Down.Stress"); // inverted polarity already applied

        var completedExpected = thisWeek.Sum(r => r.CompletedHabitIds?.Count ?? 0);
        int habitCount = Math.Max(1, input.Habits.Count);
        double habitConsistency = input.Habits.Count == 0 ? 0
            : Math.Clamp((double)completedExpected / (habitCount * (double)NumericRules.WeeklyWindowDays), 0, 1);
        int streak = input.Habits.Count == 0 ? 0 : input.Habits.Max(h => h.Streak);

        string focusKey;
        IReadOnlyList<object> focusArgs;
        if (stressTrend == EngineTrend.Declining) { focusKey = "Weekly.Focus.Stress"; focusArgs = Array.Empty<object>(); }
        else if (sleepTrend == EngineTrend.Declining) { focusKey = "Weekly.Focus.Sleep"; focusArgs = Array.Empty<object>(); }
        else if (habitConsistency >= 0.6) { focusKey = "Weekly.Focus.Momentum"; focusArgs = new object[] { streak }; }
        else { focusKey = "Weekly.Focus.OneHabit"; focusArgs = Array.Empty<object>(); }

        var confidence = thisWeek.Count >= 7 && priorWeek.Count >= 7 ? EngineBaselineConfidence.High
            : thisWeek.Count >= 6 ? EngineBaselineConfidence.Medium
            : EngineBaselineConfidence.Low;

        return new WeeklyReviewResult(
            Available: true, RefusalReason: null, DaysPresentInWindow: thisWeek.Count,
            weekStart, weekEnd,
            sleepTrend, activityTrend, recoveryTrend, stressTrend,
            habitConsistency, streak, improvements, declines, focusKey, focusArgs, confidence);
    }
}
