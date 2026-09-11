using LIVORA.Core.Enums;
using LIVORA.Core.Interfaces;
using LIVORA.Core.Models;

namespace LIVORA.Services.Intelligence;

/// <summary>
/// Phase 1 rule-based intelligence. Reads DailyState (normalized, language-independent) and
/// produces semantic insight topics + recommendation actions. The UI localizes them.
/// A future OpenAI/local-model service implements IIntelligenceService and drops in unchanged.
/// </summary>
public sealed class MockIntelligenceService : IIntelligenceService
{
    private readonly IDateTimeProvider _clock;

    public MockIntelligenceService(IDateTimeProvider clock) => _clock = clock;

    public Task<DailyInsight> GenerateDailyInsightAsync(DailyState state, UserProfile profile, IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits)
    {
        var sleepHours = state.Health.Sleep.DurationMinutes / 60.0;
        var targetHours = AppConstants.SleepTargetHours;
        var recovery = state.Health.Recovery.RecoveryScore;
        var stress = state.Health.Wellness.Stress;

        InsightTopic topic;
        double confidence;

        if (sleepHours < targetHours - 1.0)
        {
            topic = InsightTopic.SleepDebt;
            confidence = 0.82;
        }
        else if (recovery < 0.55)
        {
            topic = InsightTopic.LowRecovery;
            confidence = 0.74;
        }
        else if (stress > 0.65)
        {
            topic = InsightTopic.HighStress;
            confidence = 0.7;
        }
        else if (habits.Count > 0 && habits.Min(h => h.CurrentStreak) >= 3)
        {
            topic = InsightTopic.PositiveMomentum;
            confidence = 0.68;
        }
        else
        {
            topic = InsightTopic.BalancedDay;
            confidence = 0.6;
        }

        var insight = new DailyInsight
        {
            Topic = topic,
            Priority = topic == InsightTopic.BalancedDay || topic == InsightTopic.PositiveMomentum
                ? InsightPriority.Normal : InsightPriority.High,
            Confidence = confidence,
            Source = SourceType.Mock,
            RelatedMetricKeys = topic switch
            {
                InsightTopic.SleepDebt => new List<string> { "Health.Sleep" },
                InsightTopic.LowRecovery => new List<string> { "Health.Recovery" },
                InsightTopic.HighStress => new List<string> { "Health.Stress" },
                _ => new List<string> { "Health.Sleep", "Health.Activity", "Health.Recovery" },
            },
        };

        switch (topic)
        {
            case InsightTopic.SleepDebt:
                insight.TitleKey = "Insight.Title.SleepDebt";
                insight.SummaryKey = "Insight.Reason.SleepHours";
                insight.SummaryArgs = new object[] { FormatHoursMinutes(sleepHours), FormatHoursMinutes(targetHours) };
                break;
            case InsightTopic.LowRecovery:
                insight.TitleKey = "Insight.Title.LowRecovery";
                insight.SummaryKey = "Insight.Reason.RecoveryScore";
                insight.SummaryArgs = new object[] { Percent(recovery), Percent(0.72) };
                break;
            case InsightTopic.HighStress:
                insight.TitleKey = "Insight.Title.HighStress";
                insight.SummaryKey = "Insight.Reason.HighStress";
                break;
            case InsightTopic.PositiveMomentum:
                insight.TitleKey = "Insight.Title.PositiveMomentum";
                insight.SummaryKey = "Insight.Reason.PositiveStreak";
                insight.SummaryArgs = new object[] { habits.Max(h => h.CurrentStreak) };
                break;
            default:
                insight.TitleKey = "Insight.Title.BalancedDay";
                insight.SummaryKey = "Insight.Reason.Balanced";
                break;
        }

        insight.Recommendations = GenerateRecommendations(state, profile);
        return Task.FromResult(insight);
    }

    public IReadOnlyList<Recommendation> GenerateRecommendations(DailyState state, UserProfile profile)
    {
        var recs = new List<Recommendation>();
        var sleepHours = state.Health.Sleep.DurationMinutes / 60.0;
        var recovery = state.Health.Recovery.RecoveryScore;
        var stress = state.Health.Wellness.Stress;

        if (sleepHours < AppConstants.SleepTargetHours - 1.0)
        {
            recs.Add(Make(RecommendationAction.EarlierBedtime, InsightPriority.High));
            recs.Add(Make(RecommendationAction.ReduceTrainingIntensity, InsightPriority.Normal));
        }
        if (recovery < 0.55)
            recs.Add(Make(RecommendationAction.ShortWalk, InsightPriority.Normal));

        if (stress > 0.65)
        {
            recs.Add(Make(RecommendationAction.TakeBreak, InsightPriority.High));
            recs.Add(Make(RecommendationAction.ModerateScreenTime, InsightPriority.Normal));
        }

        if (state.Health.Activity.ActiveMinutes >= AppConstants.DailyActiveMinutesTarget &&
            recovery >= 0.55 && stress <= 0.65)
            recs.Add(Make(RecommendationAction.KeepRoutine, InsightPriority.Normal));

        if (recs.Count == 0)
            recs.Add(Make(RecommendationAction.MorningLight, InsightPriority.Normal));

        return recs.Take(4).ToList();
    }

    public BootcampDay AdaptProgramDay(Bootcamp bootcamp, BootcampDay plannedDay, DailyState state)
    {
        // Adaptation rule: low recovery or high stress converts intense days into recovery days.
        var recovery = state.Health.Recovery.RecoveryScore;
        var stress = state.Health.Wellness.Stress;

        if (plannedDay.IsAdapted) return plannedDay;

        if (recovery < 0.55 || stress > 0.65)
        {
            return new BootcampDay
            {
                DayNumber = plannedDay.DayNumber,
                PlanTitleKey = "Bootcamp.Plan.LightWalk",
                PlanDescriptionKey = "Bootcamp.Plan.LightWalk.Desc",
                TargetMinutes = 10,
                IsAdapted = true,
                IsCompleted = plannedDay.IsCompleted,
            };
        }
        return plannedDay;
    }

    public string ExplainRecommendationKey(Recommendation recommendation) => recommendation.ReasonKey;

    private static Recommendation Make(RecommendationAction action, InsightPriority priority) => new()
    {
        Action = action,
        TextKey = $"Rec.{action}",
        Priority = priority,
    };

    private static string FormatHoursMinutes(double hours)
    {
        int h = (int)hours;
        int m = (int)Math.Round((hours - h) * 60);
        return $"{h}h {m:00}m";
    }

    private static string Percent(double v) => $"{(int)Math.Round(v * 100)}%";
}
