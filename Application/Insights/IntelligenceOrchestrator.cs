using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Insights;
/// <summary>
/// Orchestrates the Wave 2 intelligence pipeline for the Today card. Combines the rule engine's
/// structural facts with the (mock) provider's interpretation into one DailyInsight for the UI.
/// Deterministic given inputs. Real AI providers can replace the interpretation layer only.
/// </summary>
public sealed class IntelligenceOrchestrator : IIntelligenceService
{
    private readonly IRuleEngine _rules;
    private readonly IRecommendationService _recommendations;
    private readonly IIntelligenceProvider _provider;
    private readonly IDailyPlanService _planService;

    public IntelligenceOrchestrator(
        IRuleEngine rules,
        IRecommendationService recommendations,
        IIntelligenceProvider provider,
        IDailyPlanService planService)
    {
        _rules = rules;
        _recommendations = recommendations;
        _provider = provider;
        _planService = planService;
    }

    public async Task<DailyInsight> GenerateDailyInsightAsync(
        PersonalState state, UserProfile profile, DailyPlan plan,
        IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits, CancellationToken ct = default)
    {
        var recs = _recommendations.BuildRecommendations(state, profile, goals, habits, plan, state.GeneratedAt);
        var interpretation = await _provider.InterpretAsync(state, recs, profile, ct);

        var related = state.Metrics
            .Where(kv => kv.Value.Level == StateLevel.BelowBaseline || kv.Value.Level == StateLevel.AboveBaseline)
            .Select(kv => InsightMetricLabels.For(kv.Key))
            .Where(s => s.Length > 0)
            .Take(3)
            .ToList();

        return new DailyInsight
        {
            TitleKey = interpretation.HeadlineKey,
            SummaryKey = interpretation.BodyKey,
            SummaryArgs = interpretation.BodyArgs,
            ReasonKey = interpretation.BodyKey,
            ReasonArgs = interpretation.BodyArgs,
            Priority = interpretation.Priority,
            Confidence = interpretation.Confidence,
            Recommendations = recs,
            RelatedMetricKeys = related,
            Source = SourceType.Mock,
        };
    }

    public string ExplainRecommendationKey(Recommendation recommendation)
        => string.IsNullOrEmpty(recommendation.ExplainKey) ? recommendation.ReasonKey : recommendation.ExplainKey;
}

/// <summary>Maps metric keys -> localization keys for "related metrics" chips.</summary>
internal static class InsightMetricLabels
{
    public static string For(string metricKey) => metricKey switch
    {
        Metrics.SleepMinutes or Metrics.SleepQuality or Metrics.SleepConsistency => "Health.Sleep",
        Metrics.Steps or Metrics.ActiveMinutes => "Health.Activity",
        Metrics.RecoveryScore or Metrics.RestingHeartRate or Metrics.HrvMs => "Health.Recovery",
        Metrics.Stress or Metrics.Mood or Metrics.Energy => "Health.Wellness",
        _ => "",
    };
}
