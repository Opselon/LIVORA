using LIVORA.Domain.Enums;

namespace LIVORA.Domain.Models.Planning;
/// <summary>
/// One scheduled block in today's plan. Semantic action + intensity; text lives in localization.
/// </summary>
public sealed class PlanItem
{
    public RecommendationActionKind Action { get; init; }
    public required string TitleKey { get; init; }
    public string? DetailKey { get; init; }
    public object[] DetailArgs { get; init; } = Array.Empty<object>();
    public int PlannedMinutes { get; init; }
    /// <summary>Base minutes before adaptation; if != PlannedMinutes the item was resized.</summary>
    public int BaseMinutes { get; init; }
    public TimeSpan? PreferredWindowStart { get; init; }
    public TimeSpan? PreferredWindowEnd { get; init; }
    public bool WasAdapted => PlannedMinutes != BaseMinutes;
    /// <summary>Which rule adapted this item (for the explanation layer + tests).</summary>
    public string? AdaptedByRule { get; init; }
    /// <summary>Anchored to a domain goal/habit/program id, or null (free recommendation).</summary>
    public string? LinkedId { get; init; }
    public RecommendationCategory Category { get; init; }
}

/// <summary>
/// The day's plan assembled from state + goals + habits + active program + recommendations.
/// Deterministic: same inputs -> same plan (unless AI rewrite is explicitly enabled).
/// </summary>
public sealed class DailyPlan
{
    public DateTime Date { get; init; }
    public required IReadOnlyList<PlanItem> Items { get; init; }
    /// <summary>Rules that fired during assembly (ids/keys) — drives the change explanation.</summary>
    public IReadOnlyList<string> AdaptationRuleKeys { get; init; } = Array.Empty<string>();
    /// <summary>True when at least one item was resized/moved vs the unadapted plan.</summary>
    public bool WasAdapted => AdaptationRuleKeys.Count > 0;
    public double TotalCommittedMinutes => Items.Sum(i => i.PlannedMinutes);
}

/// <summary>Structured result of one rule evaluation (explainable by construction).</summary>
public sealed class RuleResult
{
    public required string RuleKey { get; init; }
    public required string ConditionKey { get; init; }   // why it fired, e.g. "Rule.Reason.SleepBelowBaseline"
    public object[] ConditionArgs { get; init; } = Array.Empty<object>();
    public RecommendationActionKind Action { get; init; }
    public RecommendationCategory Category { get; init; }
    public RecommendationPriority Priority { get; init; }
    /// <summary>Plan mutations the rule requests, e.g. "exercise:60->30".</summary>
    public IReadOnlyList<string> PlanAdjustments { get; init; } = Array.Empty<string>();
    public double Confidence { get; init; } = 0.8;
}
