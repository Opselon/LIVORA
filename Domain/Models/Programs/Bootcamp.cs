using LIVORA.Domain.Enums;

namespace LIVORA.Domain.Models;
/// <summary>Adaptive multi-day program. Content is semantic (topic/action keys), localized in the UI.</summary>
public sealed class Bootcamp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TitleKey { get; set; } = string.Empty;
    public string DescriptionKey { get; set; } = string.Empty;
    public BootcampCategory Category { get; set; }
    public int DurationDays { get; set; }
    public BootcampDifficulty Difficulty { get; set; }
    public int CurrentDay { get; set; }
    public bool IsEnrolled { get; set; }
    public List<BootcampDay> Days { get; set; } = new();
    public string CreatorName { get; set; } = "LIVORA";

    // ---- Wave 2: structured program metadata ----
    /// <summary>Goal metric this program moves (Metrics.*), for data-driven progress later.</summary>
    public string? GoalMetricKey { get; set; }
    /// <summary>
    /// Rule keys that may adapt this program's days (deterministic, explainable). Only keys the
    /// RuleEngine actually emits may appear here: RuleEngine's exercise-intensity rules are
    /// Rule.SleepDebtReduceIntensity / Rule.LowRecoveryReduce / Rule.HighStress. A phantom key
    /// here would advertise an adaptation nothing can produce (and has no Rule.Why copy).
    /// </summary>
    public List<string> AdaptationRuleKeys { get; set; } = new() { "Rule.LowRecoveryReduce", "Rule.SleepDebtReduceIntensity", "Rule.HighStress" };

    public double CompletionFraction => DurationDays <= 0 ? 0 : Math.Clamp((double)CurrentDay / DurationDays, 0, 1);

    public BootcampDay? Today => CurrentDay >= 1 && CurrentDay <= Days.Count ? Days[CurrentDay - 1] : null;

    /// <summary>True when today's plan was adapted by the intelligence layer.</summary>
    public bool WasAdaptedToday { get; set; }
}

public sealed class BootcampDay
{
    public int DayNumber { get; set; }
    /// <summary>Localization key for the day's plan title (e.g. "Bootcamp.Plan.Walk").</summary>
    public string PlanTitleKey { get; set; } = string.Empty;
    /// <summary>Localization key for the day's plan description.</summary>
    public string PlanDescriptionKey { get; set; } = string.Empty;
    public int TargetMinutes { get; set; }
    public bool IsAdapted { get; set; }
    public bool IsCompleted { get; set; }
}

public sealed class DailyInsight
{
    public string TitleKey { get; set; } = string.Empty;
    public string SummaryKey { get; set; } = string.Empty;
    /// <summary>Format args for SummaryKey (numbers formatted per locale at display time).</summary>
    public object[] SummaryArgs { get; set; } = Array.Empty<object>();
    public string ReasonKey { get; set; } = string.Empty;
    public object[] ReasonArgs { get; set; } = Array.Empty<object>();
    public InsightTopic Topic { get; set; }
    public InsightPriority Priority { get; set; }
    /// <summary>0..1 how confident the engine is; presented as a qualitative badge.</summary>
    public double Confidence { get; set; }
    public IReadOnlyList<Recommendation> Recommendations { get; set; } = new List<Recommendation>();
    public List<string> RelatedMetricKeys { get; set; } = new();
    public SourceType Source { get; set; } = SourceType.Mock;
}

public sealed class Recommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Localization key for the recommendation sentence.</summary>
    public string TextKey { get; set; } = string.Empty;
    public object[] TextArgs { get; set; } = Array.Empty<object>();
    public string ReasonKey { get; set; } = string.Empty;
    public InsightPriority Priority { get; set; } = InsightPriority.Normal;

    // ---- Wave 2 additions (structured, explainable recommendations) ----
    public RecommendationActionKind ActionKind { get; set; } = RecommendationActionKind.None;
    public RecommendationCategory Category { get; set; } = RecommendationCategory.General;
    /// <summary>Structured "why" — localization key + args, never prose baked into logic.</summary>
    public string ExplainKey { get; set; } = string.Empty;
    public object[] ExplainArgs { get; set; } = Array.Empty<object>();
    /// <summary>Structured expected benefit (key + args).</summary>
    public string ExpectedBenefitKey { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
    /// <summary>Wave 2 priority ladder (finer than InsightPriority).</summary>
    public RecommendationPriority ScoredPriority { get; set; } = RecommendationPriority.Medium;
    /// <summary>0..1 — inherited from the state/confidence that produced it.</summary>
    public double Confidence { get; set; } = 0.8;
    /// <summary>The rule key that produced this recommendation (traceability/tests).</summary>
    public string? ProducedByRule { get; set; }
    public DateTime CreatedAt { get; set; }
}
