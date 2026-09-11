using LIVORA.Core.Enums;

namespace LIVORA.Core.Models;

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
    public List<Recommendation> Recommendations { get; set; } = new();
    public List<string> RelatedMetricKeys { get; set; } = new();
    public SourceType Source { get; set; } = SourceType.Mock;
}

public sealed class Recommendation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RecommendationAction Action { get; set; }
    /// <summary>Localization key for the recommendation sentence.</summary>
    public string TextKey { get; set; } = string.Empty;
    public object[] TextArgs { get; set; } = Array.Empty<object>();
    public string ReasonKey { get; set; } = string.Empty;
    public InsightPriority Priority { get; set; } = InsightPriority.Normal;
}

public sealed class DataSourceInfo
{
    public string Id { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public SourceType SourceType { get; set; }
    public ConnectionState State { get; set; }
}
