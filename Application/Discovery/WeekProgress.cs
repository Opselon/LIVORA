using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Discovery;

/// <summary>
/// The week's data situation. Anything below <see cref="WeekProgress.InsufficientDataDays"/> days
/// of history must be reported as <see cref="WeekDataStatus.InsufficientData"/> — the app is not
/// allowed to render confident bars from a two-day sample.
/// </summary>
public enum WeekDataStatus
{
    InsufficientData,
    Early,
    Solid,
}

/// <summary>
/// One labelled bar. Emission stays key+number only: the row label is a localization key
/// (program titles) or free user text (habit/goal names are data, not keys), and the caption is a
/// key with args — the UI resolves both, so this file can never leak a sentence.
/// </summary>
public sealed class WeekBar
{
    /// <summary>Localization key for the label (null when <see cref="LabelText"/> carries user text).</summary>
    public string? LabelKey { get; init; }
    /// <summary>Free user text (habit/goal names). Empty when the label is keyed.</summary>
    public string LabelText { get; init; } = string.Empty;
    /// <summary>Count caption key ("Common.ThisWeekCount", "WeekProgress.GoalSoFar", "WeekProgress.ProgramDone");
    /// the UI formats <see cref="ValueCount"/>/<see cref="TargetCount"/> through IFormatService.</summary>
    public string CaptionKey { get; init; } = string.Empty;
    public required double Value { get; init; }
    /// <summary>Denominator for the count caption (0 when nothing was expected in this window).</summary>
    public int ValueCount { get; init; }
    public int TargetCount { get; init; }
    /// <summary>Theme color key (Positive/Caution/Negative/Accent) — never a hex value.</summary>
    public required string ColorKey { get; init; }
    /// <summary>True when nothing was expected in this window (a 0% bar would be a lie).</summary>
    public bool NoTarget { get; init; }
}

/// <summary>What the week view shows. Keys + numbers only; no sentences.</summary>
public sealed class WeekProgressResult
{
    public required DateTime WeekStart { get; init; }
    public required DateTime WeekEnd { get; init; }
    /// <summary>Days of the week already elapsed (history can only cover these).</summary>
    public required int ElapsedDays { get; init; }
    /// <summary>Days that actually carry history inside the window.</summary>
    public required int DataDays { get; init; }
    public required WeekDataStatus Status { get; init; }
    /// <summary>0 = not allowed to claim confidence yet.</summary>
    public required double Confidence { get; init; }
    public IReadOnlyList<WeekBar> HabitBars { get; init; } = Array.Empty<WeekBar>();
    public IReadOnlyList<WeekBar> GoalBars { get; init; } = Array.Empty<WeekBar>();
    public IReadOnlyList<WeekBar> ProgramBars { get; init; } = Array.Empty<WeekBar>();
    public int StreakDays { get; init; }
    public int ProgramDaysCompleted { get; init; }
    public IReadOnlyList<string> MetricKeys { get; init; } = Array.Empty<string>();

    public bool HasAnything =>
        HabitBars.Count > 0 || GoalBars.Count > 0 || ProgramBars.Count > 0 || DataDays > 0;

    /// <summary>Honesty gate: below 3 days of data no bar may be presented as a trend.</summary>
    public bool ShowBars => Status != WeekDataStatus.InsufficientData;
}

/// <summary>
/// Week progress (lane 08): habits, goals and program days for one Saturday-start week, computed
/// from persisted history + the completion records that actually exist.
///
/// Honesty is the whole point of this class: with fewer than
/// <see cref="InsufficientDataDays"/> days inside the window it returns
/// <see cref="WeekDataStatus.InsufficientData"/> and a zero confidence instead of drawing bars that
/// would look like a verdict on two days of data. MAUI-free, so the test project compiles it.
/// </summary>
public static class WeekProgress
{
    /// <summary>The product law: never claim week-level confidence from less than this many days.</summary>
    public const int InsufficientDataDays = 3;
    /// <summary>From this many days the picture is considered solid.</summary>
    public const int SolidDataDays = 6;

    /// <summary>Week start = Saturday (the calendar the Persian default uses), via the shared helper.</summary>
    public static DateTime WeekStartOf(DateTime date) => Habit.StartOfWeek(date);

    /// <summary>
    /// Build one week's summary. <paramref name="referenceDay"/> is any day inside the wanted week
    /// (usually today); the window never runs into the future.
    /// </summary>
    public static async Task<WeekProgressResult> BuildAsync(
        IHistoryRepository history,
        IRepository<Habit> habits,
        IRepository<Goal> goals,
        IRepository<Bootcamp> bootcamps,
        DateTime referenceDay,
        CancellationToken ct = default)
    {
        var weekStart = WeekStartOf(referenceDay);
        var weekEnd = weekStart.AddDays(6);

        var historyTask = history.GetAllAsync();
        var habitsTask = habits.GetAllAsync();
        var goalsTask = goals.GetAllAsync();
        var programsTask = bootcamps.GetAllAsync();
        await Task.WhenAll(historyTask, habitsTask, goalsTask, programsTask);
        ct.ThrowIfCancellationRequested();

        return Compute(
            historyTask.Result, habitsTask.Result, goalsTask.Result, programsTask.Result,
            referenceDay, weekStart, weekEnd);
    }

    /// <summary>Pure core — separated from the repository reads so tests can drive it directly.</summary>
    public static WeekProgressResult Compute(
        IReadOnlyList<DailyHistoryRecord> history,
        IReadOnlyList<Habit> habits,
        IReadOnlyList<Goal> goals,
        IReadOnlyList<Bootcamp> bootcamps,
        DateTime referenceDay,
        DateTime weekStart,
        DateTime weekEnd)
    {
        var today = referenceDay.Date;
        // A week still in progress cannot be judged on days that have not happened yet.
        int elapsedDays = Math.Clamp((today - weekStart).Days + 1, 0, 7);
        var window = history
            .Where(r => r.Date.Date >= weekStart && r.Date.Date <= weekEnd && r.Date.Date <= today)
            .ToList();
        int dataDays = window.Count;

        var status = dataDays < InsufficientDataDays
            ? WeekDataStatus.InsufficientData
            : dataDays >= SolidDataDays ? WeekDataStatus.Solid : WeekDataStatus.Early;
        // Confidence grows only with evidence, and never claims certainty at full week.
        double confidence = status == WeekDataStatus.InsufficientData ? 0 : Math.Min(1.0, dataDays / 7.0);

        int maxBars = 4;
        var habitBars = new List<WeekBar>();
        foreach (var h in habits.Where(h => !string.IsNullOrWhiteSpace(h.Name)))
        {
            int done = h.Completions.Count(c => c.Date >= weekStart && c.Date <= weekEnd && c.Date <= today);
            int expected = h.Frequency switch
            {
                HabitFrequencyKind.Daily => elapsedDays,
                HabitFrequencyKind.Weekdays => WeekdayBudget(weekStart, elapsedDays),
                _ => Math.Clamp(h.TimesPerWeek <= 0 ? 3 : h.TimesPerWeek, 1, 7),
            };
            expected = Math.Max(expected, 0);
            bool noTarget = expected == 0;
            double value = noTarget ? 0 : Math.Clamp((double)done / expected, 0, 1);
            habitBars.Add(new WeekBar
            {
                LabelText = h.Name,
                CaptionKey = "Common.ThisWeekCount",
                Value = value,
                ValueCount = done,
                TargetCount = expected,
                NoTarget = noTarget,
                ColorKey = noTarget || value == 0 ? "Accent" : value >= 0.99 ? "Positive" : value >= 0.5 ? "Accent" : "Caution",
            });
        }
        if (habitBars.Count > maxBars)
            habitBars = habitBars.OrderByDescending(b => b.Value).Take(maxBars)
                .OrderBy(b => b.LabelText, StringComparer.Ordinal).ToList();

        var goalBars = new List<WeekBar>();
        foreach (var g in goals.Where(g => !g.IsArchived))
        {
            // Goal progress is the goal's own period total (Goal.Fraction) — the bar never pretends
            // to have re-derived it from history it does not hold.
            goalBars.Add(new WeekBar
            {
                LabelText = g.Name,
                CaptionKey = "WeekProgress.GoalSoFar",
                Value = Math.Clamp(g.Fraction, 0, 1),
                ValueCount = (int)Math.Round(g.ProgressValue),
                TargetCount = (int)Math.Round(g.TargetValue),
                NoTarget = g.TargetValue <= 0,
                ColorKey = g.Status switch
                {
                    GoalStatus.Completed => "Positive",
                    GoalStatus.OnTrack => "Positive",
                    GoalStatus.AtRisk => "Caution",
                    _ => "Negative",
                },
            });
        }
        if (goalBars.Count > maxBars)
            goalBars = goalBars.OrderByDescending(b => b.Value).Take(maxBars)
                .OrderBy(b => b.LabelText, StringComparer.Ordinal).ToList();

        var programBars = new List<WeekBar>();
        int programDaysCompleted = 0;
        foreach (var b in bootcamps.Where(b => b.IsEnrolled && b.DurationDays > 0))
        {
            // Program bars show real calendar progress (completed days / total days) — not a
            // week-slice estimate, because nothing records WHEN each program day was closed.
            var view = LIVORA.Application.Planning.BootcampDayDisplay.BuildView(b);
            int done = view.Count(d => d.IsCompleted);
            int total = b.DurationDays;
            double value = total <= 0 ? 0 : Math.Clamp((double)done / total, 0, 1);
            programDaysCompleted += done;
            programBars.Add(new WeekBar
            {
                LabelKey = b.TitleKey,
                CaptionKey = "WeekProgress.ProgramDone",
                Value = value,
                ValueCount = done,
                TargetCount = total,
                NoTarget = total <= 0,
                ColorKey = value >= 0.99 ? "Positive" : value > 0 ? "Accent" : "Caution",
            });
        }

        var metricKeys = new List<string>();
        if (dataDays >= InsufficientDataDays)
        {
            if (window.Any(r => r.SleepMinutes > 0)) metricKeys.Add(Metrics.SleepMinutes);
            if (window.Any(r => r.Steps > 0)) metricKeys.Add(Metrics.Steps);
            if (window.Any(r => r.RecoveryScore > 0)) metricKeys.Add(Metrics.RecoveryScore);
            if (window.Any(r => r.Stress > 0)) metricKeys.Add(Metrics.Stress);
        }

        return new WeekProgressResult
        {
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            ElapsedDays = elapsedDays,
            DataDays = dataDays,
            Status = status,
            Confidence = confidence,
            HabitBars = habitBars,
            GoalBars = goalBars,
            ProgramBars = programBars,
            StreakDays = habits.Select(h => h.CurrentStreak).DefaultIfEmpty(0).Max(),
            ProgramDaysCompleted = programDaysCompleted,
            MetricKeys = metricKeys,
        };
    }

    /// <summary>Weekdays (Sat–Thu) inside the first <paramref name="elapsedDays"/> days of the week.</summary>
    static int WeekdayBudget(DateTime weekStart, int elapsedDays)
    {
        int count = 0;
        for (int i = 0; i < Math.Clamp(elapsedDays, 0, 7); i++)
        {
            var d = weekStart.AddDays(i).DayOfWeek;
            if (d != DayOfWeek.Friday && d != DayOfWeek.Saturday) count++;
        }
        return count;
    }
}
