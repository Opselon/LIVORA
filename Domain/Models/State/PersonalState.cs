using LIVORA.Domain.Enums;

namespace LIVORA.Domain.Models.State;
/// <summary>
/// Derived state for one metric: the raw value PLUS how it compares to THIS user's baseline.
/// "Your normal matters more than generic normal."
/// </summary>
public sealed class MetricState
{
    public required string MetricKey { get; init; }
    public double Value { get; init; }
    public double? BaselineValue { get; init; }
    public BaselineConfidence BaselineConfidence { get; init; } = BaselineConfidence.None;
    /// <summary>(Value - Baseline) / Baseline. Positive = above baseline. Null when baseline unusable.</summary>
    public double? RelativeDeviation { get; init; }
    public bool HigherIsBetter { get; init; } = true;
    public DataQuality Quality { get; init; } = DataQuality.Complete;
    public DataOrigin Origin { get; init; } = DataOrigin.Mock;

    public StateLevel Level
    {
        get
        {
            if (RelativeDeviation is null || Quality is DataQuality.Missing or DataQuality.Invalid) return StateLevel.Unknown;
            // ±12% of personal baseline = still "normal" — small deltas are noise, not signal.
            const double band = 0.12;
            var d = RelativeDeviation.Value;
            return d > band ? StateLevel.AboveBaseline : d < -band ? StateLevel.BelowBaseline : StateLevel.Normal;
        }
    }

    /// <summary>Direction-aware severity: negative = worse than baseline regardless of metric polarity.</summary>
    public double SignedBadness
    {
        get
        {
            if (RelativeDeviation is null) return 0;
            var raw = HigherIsBetter ? -RelativeDeviation.Value : RelativeDeviation.Value;
            return raw; // >0 means worse than baseline
        }
    }
}

/// <summary>Baseline = rolling personal average, confidence-gated. Never fake precision.</summary>
public sealed class Baseline
{
    public required string MetricKey { get; init; }
    public double Value { get; init; }
    public double StdDev { get; init; }
    public int SampleDays { get; init; }
    public BaselineConfidence Confidence { get; init; }

    /// <summary>Minimum honest sample counts before we pretend to know someone's normal.</summary>
    public static Baseline FromSamples(string metricKey, IReadOnlyList<double> samples)
    {
        // Discard nonsense; keep chronological honesty.
        var clean = samples.Where(s => !double.IsNaN(s) && s >= 0).ToList();
        int n = clean.Count;
        var confidence = n switch
        {
            < 3 => BaselineConfidence.None,     // too few to know anything
            < 7 => BaselineConfidence.Low,      // early learning period
            < 14 => BaselineConfidence.Medium,  // provisional personal normal
            _ => BaselineConfidence.High,       // 2+ weeks: real baseline
        };
        if (n == 0) return new Baseline { MetricKey = metricKey, SampleDays = 0, Confidence = BaselineConfidence.None };

        double mean = clean.Average();
        double variance = clean.Sum(x => (x - mean) * (x - mean)) / n;
        return new Baseline { MetricKey = metricKey, Value = mean, StdDev = Math.Sqrt(variance), SampleDays = n, Confidence = confidence };
    }
}

// ---- Domain states (composition, not one flat blob) ----------------------

public sealed class SleepState
{
    public required MetricState Duration { get; init; }
    public required MetricState Quality { get; init; }
    public required MetricState Consistency { get; init; }
    public required MetricState Bedtime { get; init; }
    /// <summary>Days the sleep feed hasn't been updated (stale detection).</summary>
    public int DaysSinceFreshData { get; init; }
}

public sealed class DailyActivityState
{
    public required MetricState Steps { get; init; }
    public required MetricState ActiveMinutes { get; init; }
}

public sealed class RecoveryState
{
    public required MetricState Score { get; init; }
    public MetricState? RestingHeartRate { get; init; }
    public MetricState? Hrv { get; init; }
}

public sealed class WellnessState2
{
    public required MetricState Stress { get; init; }   // lower is better
    public required MetricState Mood { get; init; }
    public required MetricState Energy { get; init; }
}

/// <summary>Wave 2 focus estimate: derived from energy + stress + sleep (honestly labeled derived).</summary>
public sealed class FocusState
{
    public required MetricState Estimated { get; init; }
    /// <summary>True = computed from other signals; no direct measurement exists yet.</summary>
    public bool IsDerived { get; init; } = true;
}

public sealed class HabitStateSnapshot
{
    public required string HabitId { get; init; }
    public required string Name { get; init; }
    public int Streak { get; init; }
    public double SuccessRate30d { get; init; }
    public TimeSpan? BestCompletionWindow { get; init; }
    public TrendDirection ConsistencyTrend { get; init; }
}

public sealed class GoalStateSnapshot
{
    public required string GoalId { get; init; }
    public required string Name { get; init; }
    public double Fraction { get; init; }
    public GoalStatus Status { get; init; }
}

/// <summary>
/// THE central Wave 2 concept: a coherent, typed snapshot of "what is the user's current state".
/// UI and intelligence consume this; neither sees raw provider data.
/// </summary>
public sealed class PersonalState
{
    public DateTime GeneratedAt { get; init; }
    /// <summary>Overall confidence = min(domain confidences) — a chain is as strong as its weakest link.</summary>
    public double Confidence { get; init; }
    /// <summary>0..1 — how much of the expected data actually exists for this state.</summary>
    public double DataCompleteness { get; init; }

    public required SleepState Sleep { get; init; }
    public required DailyActivityState Activity { get; init; }
    public required RecoveryState Recovery { get; init; }
    public required WellnessState2 Wellness { get; init; }
    public required FocusState Focus { get; init; }
    public required HabitStateSnapshot Habits { get; init; }
    public IReadOnlyList<HabitStateSnapshot> HabitSnapshots { get; init; } = Array.Empty<HabitStateSnapshot>();
    public IReadOnlyList<GoalStateSnapshot> GoalSnapshots { get; init; } = Array.Empty<GoalStateSnapshot>();

    /// <summary>Flat lookup of every derived metric for rules and tests.</summary>
    public IReadOnlyDictionary<string, MetricState> Metrics { get; init; } = new Dictionary<string, MetricState>();
}

/// <summary>Metric keys — single source of truth so rules/tests never typo strings apart.</summary>
public static class Metrics
{
    public const string SleepMinutes = "sleep.minutes";
    public const string SleepQuality = "sleep.quality";
    public const string SleepConsistency = "sleep.consistency";
    public const string BedtimeMinutes = "sleep.bedtime";
    public const string Steps = "activity.steps";
    public const string ActiveMinutes = "activity.minutes";
    public const string RecoveryScore = "recovery.score";
    public const string RestingHeartRate = "recovery.rhr";
    public const string HrvMs = "recovery.hrv";
    public const string Stress = "wellness.stress";
    public const string Mood = "wellness.mood";
    public const string Energy = "wellness.energy";
    public const string FocusEstimate = "focus.estimate";
}
