using LIVORA.Domain.Enums;

namespace LIVORA.Domain.Models.History;
/// <summary>
/// Persisted daily history record — answers "how have I been doing over 7/30 days?"
/// without reconstructing from screens. Stores normalized day + the insight that was shown,
/// so explanations remain reproducible after the fact.
/// </summary>
public sealed class DailyHistoryRecord
{
    public DateTime Date { get; set; }
    public required string Origin { get; set; }
    public double Completeness { get; set; }

    public double SleepMinutes { get; set; }
    public double SleepQuality { get; set; }
    public double SleepConsistency { get; set; }
    public double BedtimeMinutesOfDay { get; set; }
    public int Steps { get; set; }
    public int ActiveMinutes { get; set; }
    public double RecoveryScore { get; set; }
    public double? RestingHeartRate { get; set; }
    public double? HrvMs { get; set; }
    public double Stress { get; set; }
    public double Mood { get; set; }
    public double Energy { get; set; }

    /// <summary>Snapshot of the plan that was active that day (kept small — day number per program).</summary>
    public List<string> CompletedHabitIds { get; set; } = new();
    public List<string> AdvancedGoalIds { get; set; } = new();
    public string? BootcampId { get; set; }
    public int? BootcampDayNumber { get; set; }
    public bool BootcampDayWasAdapted { get; set; }

    /// <summary>What intelligence said that day (topic/priority only — text lives in resources).</summary>
    public InsightTopic? InsightTopic { get; set; }
    public RecommendationPriority? InsightPriority { get; set; }
}

/// <summary>Weekly review output — Wave 2's first "look back" experience.</summary>
public sealed class WeeklySummary
{
    public DateTime WeekStart { get; init; }
    public DateTime WeekEnd { get; init; }
    public TrendDirection SleepTrend { get; init; }
    public TrendDirection ActivityTrend { get; init; }
    public TrendDirection RecoveryTrend { get; init; }
    public TrendDirection StressTrend { get; init; }   // inverted: lower stress = improving
    public double HabitConsistency { get; init; }
    public double GoalProgressFraction { get; init; }
    public int StreakDays { get; init; }
    /// <summary>Localization keys for improvement/decline bullets (structured, not UI strings).</summary>
    public List<string> ImprovementKeys { get; init; } = new();
    public List<string> DeclineKeys { get; init; } = new();
    /// <summary>The single most important thing for next week (key + args).</summary>
    public string FocusKey { get; init; } = "WeeklySummary.Focus.Steady";
    public object[] FocusArgs { get; init; } = Array.Empty<object>();
    /// <summary>Confidence label for the whole summary (history size aware).</summary>
    public BaselineConfidence Confidence { get; init; }
}
