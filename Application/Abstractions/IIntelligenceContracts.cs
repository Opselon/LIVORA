using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Abstractions;
/// <summary>Deterministic rules engine — the safety layer under any AI interpretation.</summary>
public interface IRuleEngine
{
    IReadOnlyList<RuleResult> Evaluate(PersonalState state, UserProfile profile,
        IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits, DateTime now);
}

/// <summary>Structured, explainable recommendation output.</summary>
public interface IRecommendationService
{
    IReadOnlyList<Recommendation> BuildRecommendations(
        PersonalState state,
        UserProfile profile,
        IReadOnlyList<Goal> goals,
        IReadOnlyList<Habit> habits,
        DailyPlan? plan,
        DateTime now);
}

/// <summary>Builds today's plan from state + goals + habits + program; adapts intensity.</summary>
public interface IDailyPlanService
{
    Task<DailyPlan> BuildPlanAsync(PersonalState state, UserProfile profile,
        IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits, DateTime now, CancellationToken ct = default);

    /// <summary>Why the plan differs from the baseline schedule (localization key + args).</summary>
    string AdaptationReasonKey(DailyPlan plan);
}

/// <summary>Provider-agnostic interpretation layer. Structured facts come from deterministic
/// services; an AI provider may only rewrite phrasing within rules — never invent measurements.</summary>
public interface IIntelligenceProvider
{
    /// <summary>True when this provider may participate (e.g. real API configured).</summary>
    bool IsAvailable { get; }
    DataOrigin Origin { get; }
    Task<InsightInterpretation> InterpretAsync(
        PersonalState state,
        IReadOnlyList<Recommendation> deterministicRecommendations,
        UserProfile profile,
        CancellationToken ct = default);
}

/// <summary>What the interpretation layer may add: phrasing over structured truth, nothing more.</summary>
public sealed class InsightInterpretation
{
    /// <summary>Localization key for the headline.</summary>
    public required string HeadlineKey { get; init; }
    public required string BodyKey { get; init; }
    public object[] BodyArgs { get; init; } = Array.Empty<object>();
    public InsightPriority Priority { get; init; }
    public double Confidence { get; init; }
    /// <summary>Overrides must pass rule-engine validation before they may apply.</summary>
    public IReadOnlyList<Recommendation> SuggestedRecommendationOverrides { get; init; } = Array.Empty<Recommendation>();
}

/// <summary>Weekly review (look-back) service.</summary>
public interface IWeeklySummaryService
{
    Task<WeeklySummary?> BuildLastWeekAsync(CancellationToken ct = default);
}
