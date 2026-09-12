using LIVORA.Domain.Enums;

namespace LIVORA.Domain.Models;
public sealed class Goal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public GoalCategory Category { get; set; }
    public double TargetValue { get; set; }
    public double ProgressValue { get; set; }
    public GoalPeriod Period { get; set; } = GoalPeriod.Week;
    public GoalUnit Unit { get; set; } = GoalUnit.Sessions;
    public DateTime? Deadline { get; set; }
    public bool IsArchived { get; set; }

    // ---- Wave 2: goal intelligence ----
    /// <summary>How progress is measured once real data flows (default stays manual, honest).</summary>
    public GoalMeasurement Measurement { get; set; } = GoalMeasurement.ManualCounter;
    /// <summary>Metric key (Metrics.*), e.g. sleep goal -> "sleep.minutes". Null for manual goals.</summary>
    public string? MetricKey { get; set; }
    /// <summary>Free semantic strategy note key (localized) — how the plan is arranged to hit the goal.</summary>
    public string? StrategyKey { get; set; }
    public int FrequencyPerPeriod { get; set; } = 1;

    public double Fraction => TargetValue <= 0 ? 0 : Math.Clamp(ProgressValue / TargetValue, 0, 1);

    public GoalStatus Status
    {
        get
        {
            if (Fraction >= 1) return GoalStatus.Completed;
            return Fraction >= 0.6 ? GoalStatus.OnTrack
                 : Fraction >= 0.3 ? GoalStatus.AtRisk
                 : GoalStatus.Behind;
        }
    }
}

public sealed class Habit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public HabitFrequencyKind Frequency { get; set; } = HabitFrequencyKind.Daily;
    public int TimesPerWeek { get; set; } = 7;
    public List<DateTime> Completions { get; set; } = new();

    /// <summary>Wave 2: precise completion timestamps (time-of-day aware analytics).</summary>
    public List<DateTime> CompletionLog { get; set; } = new();

    /// <summary>Log a completion with real time (also keeps legacy Completions in sync).</summary>
    public void Complete(DateTime when)
    {
        if (Completions.Any(d => d.Date == when.Date)) return;
        Completions.Add(when.Date);
        CompletionLog.Add(when);
    }

    public void Uncomplete(DateTime day)
    {
        Completions.RemoveAll(d => d.Date == day.Date);
        CompletionLog.RemoveAll(d => d.Date == day.Date);
    }

    /// <summary>Consecutive days completed counting back from the most recent completion.</summary>
    public int CurrentStreak
    {
        get
        {
            if (Completions.Count == 0) return 0;
            var days = Completions.Select(d => d.Date).Distinct().OrderByDescending(d => d).ToList();
            int streak = 1;
            for (int i = 1; i < days.Count; i++)
            {
                if ((days[i - 1] - days[i]).Days == 1) streak++;
                else break;
            }
            return streak;
        }
    }

    public int CompletionsThisWeek(DateTime reference)
    {
        var start = StartOfWeek(reference);
        return Completions.Count(c => c.Date >= start && c.Date < start.AddDays(7));
    }

    public bool IsCompletedOn(DateTime date) => Completions.Any(c => c.Date == date.Date);

    public static DateTime StartOfWeek(DateTime date)
    {
        // Week starts Saturday (used in Iran) — align with localized calendars later if needed.
        int diff = ((int)date.DayOfWeek + 1) % 7;
        return date.Date.AddDays(-diff);
    }
}
