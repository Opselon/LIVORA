using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;

namespace LIVORA.Infrastructure.IntelligenceProviders;
/// <summary>
/// Mock (rule-driven) intelligence provider — the default. It does NOT call any AI API.
/// It picks an insight topic/headline from the state via the rule engine, deterministically.
/// Same state in => same insight out (testable, offline-safe). A future OpenAI/local-LLM
/// provider can replace it by implementing IIntelligenceProvider; the UI never knows which.
/// </summary>
public sealed class SampleIntelligenceProvider : IIntelligenceProvider
{
    private readonly IRuleEngine _rules;

    public SampleIntelligenceProvider(IRuleEngine rules) => _rules = rules;

    public bool IsAvailable => true;
    public DataOrigin Origin => DataOrigin.Mock;

    public Task<InsightInterpretation> InterpretAsync(
        PersonalState state,
        IReadOnlyList<Recommendation> deterministicRecommendations,
        UserProfile profile,
        CancellationToken ct = default)
    {
        var fired = _rules.Evaluate(state, profile, Array.Empty<Goal>(), Array.Empty<Habit>(), state.GeneratedAt);
        var top = fired.OrderByDescending(r => (int)r.Priority).FirstOrDefault();

        InsightTopic topic = top?.RuleKey switch
        {
            "Rule.SleepDebt" or "Rule.SleepDebtReduceIntensity" => InsightTopic.SleepDebt,
            "Rule.LowRecoveryReduce" => InsightTopic.LowRecovery,
            "Rule.HighStress" or "Rule.HighStressScreens" => InsightTopic.HighStress,
            "Rule.PositiveMomentum" => InsightTopic.PositiveMomentum,
            _ => InsightTopic.BalancedDay,
        };

        // If data is stale, override to a "low data" balanced day (never fabricate certainty).
        if (fired.Any(r => r.RuleKey == "Rule.StaleSleep"))
            topic = InsightTopic.BalancedDay;

        var interp = new InsightInterpretation
        {
            HeadlineKey = $"Insight.Title.{topic}",
            BodyKey = BodyKeyFor(topic, state),
            BodyArgs = BodyArgsFor(topic, state),
            Priority = topic switch
            {
                InsightTopic.BalancedDay or InsightTopic.PositiveMomentum => InsightPriority.Normal,
                _ => InsightPriority.High,
            },
            Confidence = ConfidenceFor(topic, state),
        };
        return Task.FromResult(interp);
    }

    private static string BodyKeyFor(InsightTopic topic, PersonalState state) => topic switch
    {
        InsightTopic.SleepDebt => "Insight.Reason.SleepHours",
        InsightTopic.LowRecovery => "Insight.Reason.RecoveryScore",
        InsightTopic.HighStress => "Insight.Reason.HighStress",
        InsightTopic.PositiveMomentum => "Insight.Reason.PositiveStreak",
        _ => "Insight.Reason.Balanced",
    };

    private static object[] BodyArgsFor(InsightTopic topic, PersonalState state) => topic switch
    {
        InsightTopic.SleepDebt => new object[] { state.Sleep.Duration.Value, state.Sleep.Duration.BaselineValue ?? 0 },
        InsightTopic.LowRecovery => new object[] { state.Recovery.Score.Value, state.Recovery.Score.BaselineValue ?? 0 },
        InsightTopic.PositiveMomentum => new object[] { state.HabitSnapshots.Select(h => h.Streak).DefaultIfEmpty(0).Max() },
        _ => Array.Empty<object>(),
    };

    private static double ConfidenceFor(InsightTopic topic, PersonalState state)
    {
        double baseConf = topic switch
        {
            InsightTopic.SleepDebt when state.Sleep.Duration.BaselineConfidence == BaselineConfidence.High => 0.88,
            InsightTopic.SleepDebt => 0.72,
            InsightTopic.LowRecovery => 0.78,
            InsightTopic.HighStress => 0.7,
            InsightTopic.PositiveMomentum => 0.68,
            _ => 0.6,
        };
        return baseConf * Math.Clamp(state.Confidence + 0.2, 0.2, 1);
    }
}
