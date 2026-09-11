namespace LIVORA.Core.Models;

/// <summary>A single day's measured sleep. Duration in machine-readable minutes; quality as 0..1.</summary>
public sealed class SleepData
{
    public DateTime Date { get; set; }
    public double DurationMinutes { get; set; }
    /// <summary>0..1, 1 = best. Localized presentation ("خواب خوب") is derived, never stored.</summary>
    public double Quality { get; set; }
    public DateTime Bedtime { get; set; }
    public DateTime WakeTime { get; set; }
    /// <summary>0..1 consistency of schedule over recent days.</summary>
    public double Consistency { get; set; }
}

public sealed class ActivityData
{
    public DateTime Date { get; set; }
    public int Steps { get; set; }
    public int ActiveMinutes { get; set; }
    /// <summary>0..1 relative to the user's target load for the day.</summary>
    public double LoadEstimate { get; set; }
}

public sealed class RecoveryData
{
    public DateTime Date { get; set; }
    /// <summary>0..1 readiness score.</summary>
    public double RecoveryScore { get; set; }
    /// <summary>Placeholder until a real provider supplies RHR.</summary>
    public double? RestingHeartRate { get; set; }
    /// <summary>Placeholder until a real provider supplies HRV (ms).</summary>
    public double? HrvMilliseconds { get; set; }
}

public sealed class WellnessState
{
    public DateTime Date { get; set; }
    /// <summary>0..1, 1 = very stressed.</summary>
    public double Stress { get; set; }
    /// <summary>0..1, 1 = great mood.</summary>
    public double Mood { get; set; }
    /// <summary>0..1, 1 = high energy.</summary>
    public double Energy { get; set; }
}

/// <summary>Everything the intelligence layer needs about one day. Language-independent by design.</summary>
public sealed class HealthSnapshot
{
    public DateTime Date { get; set; }
    public SleepData Sleep { get; set; } = new();
    public ActivityData Activity { get; set; } = new();
    public RecoveryData Recovery { get; set; } = new();
    public WellnessState Wellness { get; set; } = new();
    public SourceType Source { get; set; } = SourceType.Mock;
}

public sealed class DailyState
{
    public DateTime Date { get; set; }
    public HealthSnapshot Health { get; set; } = new();
    /// <summary>0..1 composite readiness for today.</summary>
    public double DailyScore { get; set; }
    public IReadOnlyList<Goal> Goals { get; set; } = Array.Empty<Goal>();
    public IReadOnlyList<Habit> Habits { get; set; } = Array.Empty<Habit>();
}
