using System.Globalization;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Planning;
/// <summary>
/// Builds recommendations from rule-engine output. Deterministic: same rules => same recs.
/// Every recommendation carries: action, category, priority, explanation (key+args),
/// expected benefit, duration, producing rule, confidence. Text stays in localization.
/// </summary>
public sealed class RecommendationService : IRecommendationService
{
    private readonly IRuleEngine _rules;

    public RecommendationService(IRuleEngine rules) => _rules = rules;

    public IReadOnlyList<Recommendation> BuildRecommendations(
        PersonalState state, UserProfile profile, IReadOnlyList<Goal> goals,
        IReadOnlyList<Habit> habits, DailyPlan? plan, DateTime now)
    {
        var fired = _rules.Evaluate(state, profile, goals, habits, now);

        // Cap visible output — LIVORA should not overwhelm: max 1 high + 2 medium/low.
        var visible = fired
            .Where(r => r.Action != RecommendationActionKind.None)
            .GroupBy(r => r.Action)                      // dedupe same action from multiple rules
            .Select(g => g.OrderByDescending(r => (int)r.Priority).First())
            .OrderByDescending(r => (int)r.Priority)
            .ThenBy(r => r.RuleKey, StringComparer.Ordinal)
            .ToList();

        var high = visible.Where(r => (int)r.Priority >= (int)RecommendationPriority.High).Take(1);
        var rest = visible.Where(r => (int)r.Priority < (int)RecommendationPriority.High).Take(2);
        var chosen = high.Concat(rest).ToList();

        // If literally nothing applies (great day), give a gentle positive anchor.
        if (chosen.Count == 0)
        {
            return new List<Recommendation>
            {
                new()
                {
                    ActionKind = RecommendationActionKind.KeepRoutine,
                    TextKey = "Rec.KeepRoutine",
                    ExplainKey = "Rule.Reason.AllNearBaseline",
                    Category = RecommendationCategory.General,
                    ScoredPriority = RecommendationPriority.Low,
                    Confidence = 0.6,
                    ProducedByRule = "Rule.PositiveMomentum",
                    CreatedAt = now,
                },
            };
        }

        return chosen.Select(r => new Recommendation
        {
            ActionKind = r.Action,
            TextKey = $"Rec.{r.Action}",
            ExplainKey = r.ConditionKey,
            ExplainArgs = r.ConditionArgs,
            ExpectedBenefitKey = BenefitKey(r.Action),
            DurationMinutes = DurationFor(r.Action),
            Category = r.Category,
            ScoredPriority = r.Priority,
            Confidence = r.Confidence,
            ProducedByRule = r.RuleKey,
            CreatedAt = now,
        }).ToList();
    }

    private static string BenefitKey(RecommendationActionKind action) => action switch
    {
        RecommendationActionKind.EarlierBedtime => "Benefit.SleepRecovery",
        RecommendationActionKind.ReduceTrainingIntensity => "Benefit.PreserveRecovery",
        RecommendationActionKind.ShortWalk => "Benefit.GentleMovement",
        RecommendationActionKind.TakeBreak => "Benefit.StressRelease",
        RecommendationActionKind.ModerateScreenTime => "Benefit.BetterSleepOnset",
        RecommendationActionKind.CompleteHabit => "Benefit.StreakMomentum",
        RecommendationActionKind.KeepRoutine => "Benefit.Stability",
        _ => "Benefit.General",
    };

    private static int DurationFor(RecommendationActionKind action) => action switch
    {
        RecommendationActionKind.ShortWalk => 15,
        RecommendationActionKind.TakeBreak => 10,
        RecommendationActionKind.CompleteHabit => 10,
        RecommendationActionKind.EarlierBedtime => 0, // timing change, not a duration
        _ => 0,
    };
}

/// <summary>
/// Daily plan assembly: schedule skeleton from profile rhythm + goals + habits + program,
/// then rule-engine adjustments resize it. Explains its own adaptations.
/// </summary>
public sealed class DailyPlanService : IDailyPlanService
{
    private readonly IRuleEngine _rules;
    private readonly IRepository<Bootcamp> _bootcamps;
    // WAVE3C-LANE05: optional second-pass engine; null (unregistered) keeps today's behavior.
    private readonly IPlanAdaptationEngine? _adaptationEngine;

    public DailyPlanService(IRuleEngine rules, IRepository<Bootcamp> bootcamps,
        IPlanAdaptationEngine? adaptationEngine = null)
    {
        _rules = rules;
        _bootcamps = bootcamps;
        _adaptationEngine = adaptationEngine;
    }

    public async Task<DailyPlan> BuildPlanAsync(PersonalState state, UserProfile profile,
        IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits, DateTime now, CancellationToken ct = default)
    {
        var items = new List<PlanItem>();

        // 1) Active program's day
        var all = await _bootcamps.GetAllAsync();
        var enrolled = all.FirstOrDefault(b => b.IsEnrolled && b.Today is not null);
        if (enrolled?.Today is { } programDay)
        {
            items.Add(new PlanItem
            {
                Action = RecommendationActionKind.DoProgramDay,
                TitleKey = programDay.PlanTitleKey,
                DetailKey = programDay.PlanDescriptionKey,
                BaseMinutes = programDay.TargetMinutes,
                PlannedMinutes = programDay.TargetMinutes,
                LinkedId = enrolled.Id,
                Category = RecommendationCategory.Program,
            });
        }

        // 2) Habits not yet done today become scheduled prompts (best-window aware)
        foreach (var h in habits.Where(h => !h.IsCompletedOn(now.Date)).Take(2))
        {
            var snap = state.HabitSnapshots.FirstOrDefault(s => s.HabitId == h.Id);
            items.Add(new PlanItem
            {
                Action = RecommendationActionKind.CompleteHabit,
                TitleKey = "Plan.Item.Habit",
                DetailArgs = new object[] { h.Name },
                BaseMinutes = 10,
                PlannedMinutes = 10,
                PreferredWindowStart = snap?.BestCompletionWindow,
                LinkedId = h.Id,
                Category = RecommendationCategory.Habit,
            });
        }

        // 3) Focus blocks scaled by state (high stress => fewer blocks)
        var stress = state.Metrics.GetValueOrDefault(Metrics.Stress);
        int focusBlocks = stress?.Value > 0.65 ? 2 : 3;
        items.Add(new PlanItem
        {
            Action = RecommendationActionKind.ProtectFocusBlocks,
            TitleKey = "Plan.Item.FocusBlocks",
            DetailArgs = new object[] { focusBlocks },
            BaseMinutes = focusBlocks * 50,
            PlannedMinutes = focusBlocks * 50,
            Category = RecommendationCategory.Focus,
        });

        // 4) Apply rule-engine adaptations to the skeleton
        var fired = _rules.Evaluate(state, profile, goals, habits, now);
        var adaptationKeys = new List<string>();
        foreach (var r in fired)
        {
            if (r.PlanAdjustments.Count == 0) continue;
            foreach (var adj in r.PlanAdjustments)
            {
                Apply(items, adj, r.RuleKey);
            }
            adaptationKeys.Add(r.RuleKey);
        }

        var assembled = new DailyPlan
        {
            Date = now.Date,
            Items = items,
            AdaptationRuleKeys = adaptationKeys,
        };
        // WAVE3C-LANE05: route through the adaptive engine only when one is registered; it is
        // idempotent and skips rule keys the assembly pass already applied.
        return _adaptationEngine?.Adapt(assembled, state, now).Plan ?? assembled;
    }

    /// <summary>Adjustment grammar: "exercise:*0.5" / "bedtime:-30min" / "focus:-1block" / "recovery:+15min" / "walk:+15min" / "winddown:+30min" / "habit:&lt;id&gt;:prompt"</summary>
    private static void Apply(List<PlanItem> items, string adjustment, string ruleKey)
    {
        var parts = adjustment.Split(':');
        switch (parts[0])
        {
            case "exercise" when parts.Length > 1 && parts[1].StartsWith('*'):
                // Snapshot first: ScaleDown replaces items in place, which invalidates a live enumerator.
                foreach (var it in items.Where(i => i.Category == RecommendationCategory.Program).ToList())
                    ScaleDown(items, it, double.Parse(parts[1][1..], CultureInfo.InvariantCulture), ruleKey);
                break;
            case "recovery":
                Add(items, new PlanItem
                {
                    Action = RecommendationActionKind.None,
                    TitleKey = "Plan.Item.Recovery",
                    BaseMinutes = int.Parse(parts[1].TrimStart('+').Replace("min", "")),
                    PlannedMinutes = int.Parse(parts[1].TrimStart('+').Replace("min", "")),
                    AdaptedByRule = ruleKey,
                    Category = RecommendationCategory.Recovery,
                });
                break;
            case "walk":
                Add(items, new PlanItem
                {
                    Action = RecommendationActionKind.ShortWalk,
                    TitleKey = "Rec.ShortWalk",
                    BaseMinutes = int.Parse(parts[1].TrimStart('+').Replace("min", "")),
                    PlannedMinutes = int.Parse(parts[1].TrimStart('+').Replace("min", "")),
                    AdaptedByRule = ruleKey,
                    Category = RecommendationCategory.Activity,
                });
                break;
            case "focus" when parts[1].StartsWith('-'):
                foreach (var it in items.Where(i => i.Action == RecommendationActionKind.ProtectFocusBlocks).ToList())
                    ScaleDown(items, it, Math.Max(0.5, 1 - int.Parse(parts[1][1..].Replace("block", "")) * 0.25), ruleKey);
                break;
            case "bedtime" when parts[1].StartsWith('-'):
                Add(items, new PlanItem
                {
                    Action = RecommendationActionKind.EarlierBedtime,
                    TitleKey = "Rec.EarlierBedtime",
                    BaseMinutes = 0,
                    PlannedMinutes = 0,
                    AdaptedByRule = ruleKey,
                    Category = RecommendationCategory.Sleep,
                });
                break;
            case "winddown":
                Add(items, new PlanItem
                {
                    Action = RecommendationActionKind.WindDownBeforeBed,
                    TitleKey = "Plan.Item.WindDown",
                    BaseMinutes = int.Parse(parts[1].TrimStart('+').Replace("min", "")),
                    PlannedMinutes = int.Parse(parts[1].TrimStart('+').Replace("min", "")),
                    AdaptedByRule = ruleKey,
                    Category = RecommendationCategory.Sleep,
                });
                break;
            // "bedtime:-30min" handled as earlier-bedtime prompt; habit prompts are already scheduled above.
        }
    }

    private static void ScaleDown(List<PlanItem> items, PlanItem existing, double factor, string ruleKey)
    {
        int idx = items.IndexOf(existing);
        if (idx < 0) return;
        items[idx] = new PlanItem
        {
            Action = existing.Action,
            TitleKey = existing.TitleKey,
            DetailKey = existing.DetailKey,
            DetailArgs = existing.DetailArgs,
            BaseMinutes = existing.BaseMinutes,
            PlannedMinutes = (int)Math.Round(existing.BaseMinutes * factor),
            PreferredWindowStart = existing.PreferredWindowStart,
            PreferredWindowEnd = existing.PreferredWindowEnd,
            AdaptedByRule = ruleKey,
            LinkedId = existing.LinkedId,
            Category = existing.Category,
        };
    }

    private static void Add(List<PlanItem> items, PlanItem item)
    {
        if (!items.Any(i => i.Action == item.Action && i.Category == item.Category && i.TitleKey == item.TitleKey))
            items.Add(item);
    }

    public string AdaptationReasonKey(DailyPlan plan) => plan.AdaptationRuleKeys.Count switch
    {
        0 => "Plan.NotAdapted",
        1 => plan.AdaptationRuleKeys[0] switch
        {
            "Rule.SleepDebt" or "Rule.SleepDebtReduceIntensity" => "Plan.Adapted.Sleep",
            "Rule.LowRecoveryReduce" => "Plan.Adapted.Recovery",
            "Rule.HighStress" or "Rule.HighStressScreens" => "Plan.Adapted.Stress",
            "Rule.ActivityDeficit" => "Plan.Adapted.Activity",
            _ => "Plan.Adapted.Generic",
        },
        _ => "Plan.Adapted.Multiple",
    };
}
