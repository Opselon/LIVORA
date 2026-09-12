using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Rules;
/// <summary>
/// Deterministic rules engine — the safety layer under any AI interpretation.
/// Every rule: pure function of (PersonalState vs personal baseline) -> structured result.
/// Same input => same output (testable, explainable). Thresholds are deliberate and few.
/// </summary>
public sealed class RuleEngine : IRuleEngine
{
    // Threshold constants (named for tests + docs)
    public const double SleepDeficitHours = 1.5;     // below personal baseline by this => sleep debt
    public const double RecoveryBelow = 0.55;        // absolute floor for "low recovery"
    public const double StressAbove = 0.65;          // absolute ceiling for "high stress"
    public const double ActivityDeficitFraction = 0.45; // steps < 55% of personal baseline => deficit
    public const double StaleDaysAllowance = 2;      // feed older than this => stale handling

    public IReadOnlyList<RuleResult> Evaluate(PersonalState state, UserProfile profile,
        IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits, DateTime now)
    {
        var results = new List<RuleResult>();
        var m = state.Metrics;

        // R1: Sleep debt vs personal baseline (only when the baseline is usable).
        var sleep = m.GetValueOrDefault(Metrics.SleepMinutes);
        if (sleep is { BaselineValue: > 0, BaselineConfidence: not BaselineConfidence.None } && sleep.Quality == DataQuality.Complete
            && (sleep.BaselineValue.Value - sleep.Value) / 60.0 >= SleepDeficitHours)
        {
            double deficitHours = (sleep.BaselineValue.Value - sleep.Value) / 60.0;
            results.Add(new RuleResult
            {
                RuleKey = "Rule.SleepDebt",
                ConditionKey = "Rule.Reason.SleepBelowBaseline",
                ConditionArgs = new object[] { deficitHours },
                Action = RecommendationActionKind.EarlierBedtime,
                Category = RecommendationCategory.Sleep,
                Priority = deficitHours >= 2.5 ? RecommendationPriority.High : RecommendationPriority.Medium,
                PlanAdjustments = new[] { "bedtime:-30min" },
                Confidence = sleep.BaselineConfidence switch
                {
                    BaselineConfidence.High => 0.9, BaselineConfidence.Medium => 0.8, _ => 0.6,
                },
            });
            results.Add(new RuleResult
            {
                RuleKey = "Rule.SleepDebtReduceIntensity",
                ConditionKey = "Rule.Reason.SleepBelowBaseline",
                ConditionArgs = new object[] { deficitHours },
                Action = RecommendationActionKind.ReduceTrainingIntensity,
                Category = RecommendationCategory.Activity,
                Priority = RecommendationPriority.Medium,
                PlanAdjustments = new[] { "exercise:*0.5" },
                Confidence = 0.85,
            });
        }

        // R2: Low recovery vs personal baseline or absolute floor.
        var rec = m.GetValueOrDefault(Metrics.RecoveryScore);
        if (rec is not null && rec.Quality == DataQuality.Complete &&
            (rec.Value < RecoveryBelow || (rec.BaselineValue > 0 && rec.RelativeDeviation < -0.18)))
        {
            results.Add(new RuleResult
            {
                RuleKey = "Rule.LowRecoveryReduce",
                ConditionKey = "Rule.Reason.RecoveryBelowBaseline",
                ConditionArgs = new object[] { rec.Value },
                Action = RecommendationActionKind.ShortWalk,
                Category = RecommendationCategory.Recovery,
                Priority = RecommendationPriority.Medium,
                PlanAdjustments = new[] { "exercise:*0.5", "recovery:+15min" },
                Confidence = 0.8,
            });
        }

        // R3: Elevated stress.
        var stress = m.GetValueOrDefault(Metrics.Stress);
        if (stress is not null && stress.Quality == DataQuality.Complete && stress.Value > StressAbove)
        {
            results.Add(new RuleResult
            {
                RuleKey = "Rule.HighStress",
                ConditionKey = "Rule.Reason.StressAboveUsual",
                ConditionArgs = new object[] { stress.Value },
                Action = RecommendationActionKind.TakeBreak,
                Category = RecommendationCategory.Stress,
                Priority = stress.Value > 0.8 ? RecommendationPriority.High : RecommendationPriority.Medium,
                PlanAdjustments = new[] { "focus:-1block", "recovery:+10min" },
                Confidence = 0.75,
            });
            results.Add(new RuleResult
            {
                RuleKey = "Rule.HighStressScreens",
                ConditionKey = "Rule.Reason.StressAboveUsual",
                ConditionArgs = new object[] { stress.Value },
                Action = RecommendationActionKind.ModerateScreenTime,
                Category = RecommendationCategory.Sleep,
                Priority = RecommendationPriority.Low,
                PlanAdjustments = new[] { "winddown:+30min" },
                Confidence = 0.7,
            });
        }

        // R4: Activity deficit vs personal baseline.
        var steps = m.GetValueOrDefault(Metrics.Steps);
        if (steps is { BaselineValue: > 0 } && steps.Quality == DataQuality.Complete
            && steps.Value < steps.BaselineValue * (1 - ActivityDeficitFraction) && rec?.Value >= RecoveryBelow)
        {
            results.Add(new RuleResult
            {
                RuleKey = "Rule.ActivityDeficit",
                ConditionKey = "Rule.Reason.StepsBelowBaseline",
                ConditionArgs = Array.Empty<object>(),
                Action = RecommendationActionKind.ShortWalk,
                Category = RecommendationCategory.Activity,
                Priority = RecommendationPriority.Low,
                PlanAdjustments = new[] { "walk:+15min" },
                Confidence = 0.75,
            });
        }

        // R5: Protecting momentum (positive rule — keeps the day from feeling like an intervention).
        if (sleep?.Level == StateLevel.Normal && rec is { Value: >= 0.65 } && stress is { Value: < 0.5 })
        {
            results.Add(new RuleResult
            {
                RuleKey = "Rule.PositiveMomentum",
                ConditionKey = "Rule.Reason.AllNearBaseline",
                ConditionArgs = Array.Empty<object>(),
                Action = RecommendationActionKind.KeepRoutine,
                Category = RecommendationCategory.General,
                Priority = RecommendationPriority.Low,
                PlanAdjustments = Array.Empty<string>(),
                Confidence = 0.7,
            });
        }

        // R6: Habit at-risk detection — streak >=3 but nothing logged yet today after 18:00.
        if (now.Hour >= 18)
        {
            var atRisk = habits.Where(h => h.CurrentStreak >= 3 && !h.IsCompletedOn(now.Date)).ToList();
            foreach (var h in atRisk)
            {
                results.Add(new RuleResult
                {
                    RuleKey = "Rule.HabitAtRisk",
                    ConditionKey = "Rule.Reason.HabitStreakAtRisk",
                    ConditionArgs = new object[] { h.Name, h.CurrentStreak },
                    Action = RecommendationActionKind.CompleteHabit,
                    Category = RecommendationCategory.Habit,
                    Priority = RecommendationPriority.Medium,
                    PlanAdjustments = new[] { $"habit:{h.Id}:prompt" },
                    Confidence = 0.85,
                });
            }
        }

        // R7: Stale data guard — tells intelligence not to pretend the feed is fresh.
        if (state.Sleep.DaysSinceFreshData > StaleDaysAllowance)
        {
            results.Add(new RuleResult
            {
                RuleKey = "Rule.StaleSleep",
                ConditionKey = "Rule.Reason.SleepDataStale",
                ConditionArgs = new object[] { state.Sleep.DaysSinceFreshData },
                Action = RecommendationActionKind.None,
                Category = RecommendationCategory.General,
                Priority = RecommendationPriority.Optional,
                PlanAdjustments = Array.Empty<string>(),
                Confidence = 1.0,
            });
        }

        return results
            .OrderByDescending(r => (int)r.Priority)
            .ThenBy(r => r.RuleKey, StringComparer.Ordinal) // deterministic tie-break
            .ToList();
    }
}
