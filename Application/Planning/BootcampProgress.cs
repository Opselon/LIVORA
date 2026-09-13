using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;

namespace LIVORA.Application.Planning;

/// <summary>Display state of one calendar cell — vocabulary only, never text.</summary>
public enum BootcampDayState
{
    Upcoming,
    Today,
    AdaptedToday,
    Completed,
    CompletedToday,
}

/// <summary>
/// One day of the program as the UI should draw it. Built from real persisted flags
/// (IsCompleted / IsAdapted) plus the pointer, never from an assumption.
/// </summary>
public sealed class BootcampDayCell
{
    public required int DayNumber { get; init; }
    public required string PlanTitleKey { get; init; }
    public required string PlanDescriptionKey { get; init; }
    public required int TargetMinutes { get; init; }
    public required BootcampDayState State { get; init; }
    /// <summary>True when a rule actually touched this day (today's adaptation or a stored mark).</summary>
    public required bool WasAdapted { get; init; }

    public bool IsCompleted => State is BootcampDayState.Completed or BootcampDayState.CompletedToday;
    public bool IsToday => State is BootcampDayState.Today or BootcampDayState.AdaptedToday or BootcampDayState.CompletedToday;
    public bool IsAdapted => WasAdapted;

    /// <summary>Localization key for the cell's status word ("Bootcamp.Day.Completed", …).</summary>
    public string StatusKey =>
        IsToday && WasAdapted ? "Bootcamp.Day.Adapted"
        : IsCompleted ? "Bootcamp.Day.Completed"
        : IsToday ? "Bootcamp.Day.Today"
        : "Bootcamp.Day.Upcoming";
}

/// <summary>Everything the program screens say about progress. Immutable, computed, explainable.</summary>
public sealed class BootcampProgressSnapshot
{
    public required IReadOnlyList<BootcampDayCell> Cells { get; init; }
    public required int TotalDays { get; init; }
    public required int CompletedDays { get; init; }
    public required int CurrentDay { get; init; }
    public required int DaysRemaining { get; init; }
    public required int AdaptedDays { get; init; }
    /// <summary>Consecutive completed days counting back from today (a miss today does not break it).</summary>
    public required int StreakDays { get; init; }
    /// <summary>Honest completion: done-days / total-days (NOT the day pointer).</summary>
    public required double CompletionFraction { get; init; }
    public required bool NotStarted { get; init; }
    public required bool Finished { get; init; }
    /// <summary>Program days already played (1..pointer); 0 = no pace evidence at all.</summary>
    public required int DaysPlayed { get; init; }
    /// <summary>Days completed per program day played; 0 = unknown pace.</summary>
    public required double PacePerDay { get; init; }
    /// <summary>Projected last day at the current pace; null when LIVORA would be guessing.</summary>
    public DateTime? ProjectedFinishDate { get; init; }
    public required int NextMilestoneDay { get; init; }
    public required int NextMilestonePercent { get; init; }
    public required int PlannedMinutesRemaining { get; init; }

    /// <summary>"Bootcamp.Status.NotStarted" | ".InProgress" | ".Completed" — driven by real
    /// completed-day counts, never by the pointer alone.</summary>
    public string StatusKey => Finished ? "Bootcamp.Status.Completed"
        : CompletedDays == 0 ? "Bootcamp.Status.NotStarted"
        : "Bootcamp.Status.InProgress";


    /// <summary>The cell for a day number, or null (never throws on a partial calendar).</summary>
    public BootcampDayCell? Cell(int dayNumber) => Cells.FirstOrDefault(c => c.DayNumber == dayNumber);

    /// <summary>Today's cell, if the program is inside its range.</summary>
    public BootcampDayCell? TodayCell => Cell(CurrentDay);
}

/// <summary>
/// Pure progress math for adaptive programs (lane 08) + the single place a program's
/// enrollment/day-completion may change.
///
/// Two product rules the rest of the app depends on:
///  - Progress comes from <see cref="BootcampDay.IsCompleted"/> days, never from the day pointer,
///    so a skipped day is visible instead of being counted as done.
///  - A projection or a streak is only stated when the data supports it: with no pace evidence
///    <see cref="BootcampProgressSnapshot.ProjectedFinishDate"/> is null and the UI says so.
///
/// Domain is frozen (shared with the JSON store + tests), so the mutations that used to sit inside
/// view-models live here as static mutators and are called by every screen. MAUI-free on purpose:
/// the test project compiles this file.
/// </summary>
public static class BootcampProgress
{
    /// <summary>Milestone grid: quarter, half, three-quarter, finish.</summary>
    static readonly int[] MilestonePercents = { 25, 50, 75, 100 };

    public static BootcampProgressSnapshot Compute(Bootcamp b, DateTime today, BootcampDay? todayOverride = null)
    {
        var view = BootcampDayDisplay.BuildView(b);
        int total = b.DurationDays > 0 ? b.DurationDays : view.Count;
        int pointer = Math.Clamp(b.CurrentDay, 0, Math.Max(total, 1));

        var cells = new List<BootcampDayCell>(view.Count);
        foreach (var d in view)
        {
            var day = d;
            bool adaptedMark = d.IsAdapted;
            bool isToday = total > 0 && d.DayNumber == pointer;
            if (isToday && todayOverride is not null)
            {
                day = todayOverride;
                adaptedMark = todayOverride.IsAdapted;
            }
            var state =
                day.IsCompleted && isToday ? BootcampDayState.CompletedToday :
                day.IsCompleted ? BootcampDayState.Completed :
                isToday && adaptedMark ? BootcampDayState.AdaptedToday :
                isToday ? BootcampDayState.Today :
                BootcampDayState.Upcoming;
            cells.Add(new BootcampDayCell
            {
                DayNumber = day.DayNumber,
                PlanTitleKey = day.PlanTitleKey,
                PlanDescriptionKey = day.PlanDescriptionKey,
                TargetMinutes = day.TargetMinutes,
                State = state,
                WasAdapted = adaptedMark,
            });
        }

        int completed = cells.Count(c => c.IsCompleted);
        int adapted = cells.Count(c => c.WasAdapted);
        int remaining = Math.Max(0, total - completed);
        double fraction = total <= 0 ? 0 : Math.Clamp((double)completed / total, 0, 1);
        bool finished = total > 0 && completed >= total;

        // Streak: the run of completed days ending today, tolerating "today not done yet".
        int streak = 0;
        int idx = pointer >= 1 ? pointer - 1 : cells.Count - 1;
        for (int i = idx; i >= 0; i--)
        {
            if (cells[i].IsCompleted) streak++;
            else if (i == idx) continue; // today is still open — the streak is not broken yet
            else break;
        }

        // Pace: completed days over program days already played (every day up to the pointer has
        // been "released" to the user). Zero played days => zero evidence => no projection, and the
        // UI must say "no estimate yet" rather than extrapolate from nothing.
        int played = total <= 0 ? 0 : Math.Clamp(pointer, 0, total);
        double pace = played > 0 ? (double)completed / played : 0;

        bool todayOpen = pointer >= 1 && cells.FirstOrDefault(c => c.DayNumber == pointer)?.IsCompleted == false;
        DateTime? projected = null;
        if (total > 0 && !finished && pace > 0)
        {
            // Program day (pointer + k) lands on today + k, so the k-th future completion is the
            // projected finish. n days of play at this pace close n * pace program days.
            int neededProgramDays = (int)Math.Ceiling(remaining / pace);
            int offset = todayOpen ? Math.Max(0, neededProgramDays - 1) : neededProgramDays;
            projected = today.Date.AddDays(offset);
        }

        int nextMilestoneDay = 0, nextMilestonePercent = 0;
        foreach (int pct in MilestonePercents)
        {
            int dayNumber = (int)Math.Ceiling(total * pct / 100.0);
            if (dayNumber <= 0) continue;
            if (completed < dayNumber) { nextMilestoneDay = dayNumber; nextMilestonePercent = pct; break; }
        }

        return new BootcampProgressSnapshot
        {
            Cells = cells,
            TotalDays = total,
            CompletedDays = completed,
            CurrentDay = pointer,
            DaysRemaining = remaining,
            AdaptedDays = adapted,
            StreakDays = streak,
            CompletionFraction = fraction,
            NotStarted = completed == 0 && pointer <= 1,
            Finished = finished,
            DaysPlayed = played,
            PacePerDay = pace,
            ProjectedFinishDate = projected,
            NextMilestoneDay = nextMilestoneDay,
            NextMilestonePercent = nextMilestonePercent,
            PlannedMinutesRemaining = cells.Where(c => !c.IsCompleted).Sum(c => c.TargetMinutes),
        };
    }

    // ---- Mutations: the ONE place a program changes (Domain is frozen, so they live here) ----

    /// <summary>
    /// Enroll: materialize this program's own calendar (category template) and start at day 1.
    /// Re-enrolling an in-progress program keeps its real progress instead of resetting it.
    /// </summary>
    public static void Enroll(Bootcamp b)
    {
        EnsureCalendar(b);
        b.IsEnrolled = true;
        if (b.Days.Count(d => d.IsCompleted) == 0 && b.CurrentDay < 1) b.CurrentDay = 1;
        if (b.CurrentDay < 1) b.CurrentDay = 1;
        b.WasAdaptedToday = false;
    }

    /// <summary>
    /// Leave: only the enrollment flag changes. Completed days are real history — deleting them
    /// would erase something the user actually did.
    /// </summary>
    public static void Leave(Bootcamp b) => b.IsEnrolled = false;

    /// <summary>
    /// Complete the current day and move the pointer. Returns the day number that was closed
    /// (0 = nothing to do), so the caller can journal it honestly.
    /// </summary>
    public static int CompleteToday(Bootcamp b, bool adaptedToday)
    {
        EnsureCalendar(b);
        if (b.DurationDays <= 0) return 0;
        if (b.CurrentDay < 1) b.CurrentDay = 1;
        int dayNumber = Math.Min(b.CurrentDay, b.DurationDays);
        var day = b.Days.FirstOrDefault(d => d.DayNumber == dayNumber);
        if (day is null) return 0;
        if (day.IsCompleted && b.CurrentDay >= b.DurationDays) return 0; // already finished
        day.IsCompleted = true;
        day.IsAdapted = adaptedToday;
        b.WasAdaptedToday = adaptedToday;
        b.CurrentDay = Math.Min(dayNumber + 1, b.DurationDays);
        return dayNumber;
    }

    /// <summary>
    /// Make the stored calendar real: empty (or the old uniform "20-min walk for every day") is
    /// replaced by this program's own category template, preserving completion + adaptation marks.
    /// </summary>
    public static void EnsureCalendar(Bootcamp b)
    {
        if (b.DurationDays <= 0) return;
        var stored = b.Days ?? new List<BootcampDay>();
        bool needsRepair = stored.Count == 0
            || stored.Count < b.DurationDays
            || BootcampDayDisplay.IsLegacyPlaceholder(stored, b.DurationDays);
        if (!needsRepair) return;

        var fresh = BootcampTemplates.Materialize(b);
        // A pointer past day 1 with no stored days is the older build's shape: everything behind
        // the pointer was played, so the repaired calendar says so instead of claiming 0% done.
        bool pointerHistory = stored.Count == 0 && b.CurrentDay > 1;
        foreach (var d in fresh)
        {
            var old = stored.FirstOrDefault(s => s.DayNumber == d.DayNumber);
            d.IsCompleted = old?.IsCompleted == true || (pointerHistory && d.DayNumber < b.CurrentDay);
            d.IsAdapted = old?.IsAdapted == true;
        }
        b.Days = fresh;
    }
}

/// <summary>
/// Day-completion journaling for programs: annotates the persisted daily history so the weekly
/// review can count real program days. Both program screens call this instead of carrying their
/// own copy of the rule (single source of truth for "what happened on day N").
/// </summary>
public static class BootcampJournal
{
    /// <summary>
    /// Attach the just-completed program day to today's history record. Does nothing when no
    /// record exists yet — LIVORA never creates a "day" it has no data for.
    /// </summary>
    public static async Task LogDayAsync(
        IHistoryRepository history, Bootcamp b, int dayNumber, bool adaptedToday, DateTime today)
    {
        if (dayNumber <= 0) return;
        var record = (await history.GetAllAsync()).FirstOrDefault(r => r.Date.Date == today.Date);
        if (record is null) return;
        record.BootcampId = b.Id;
        record.BootcampDayNumber = dayNumber;
        record.BootcampDayWasAdapted = adaptedToday;
        await history.UpsertAsync(record);
    }
}
