using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Application.Planning;

/// <summary>
/// Day-content templates per program category (lane 08).
///
/// Why this exists: enrollment used to invent one identical "20-minute walk" for every day of a
/// program, so the 30-day reading plan was 30 walks — a fake-complete calendar. A template here is
/// a deterministic function of (Category, DayNumber) built only from plan keys that already ship in
/// both languages (Bootcamp.Plan.*), so the same program always produces the same calendar, the
/// rotation is explainable, and tests can pin it without any UI.
///
/// Rotation design: a 3- or 4-day activity cycle per category, plus one weekly "gentle swap" that
/// replaces an intense day (workout / deep work) with a restorative one. The swap is decided from
/// the RESOLVED shape (not from cycle arithmetic), so it can never claim a rest day the cycle did
/// not schedule, and the whole calendar stays reproducible from (Category, DayNumber) alone.
/// </summary>
public static class BootcampTemplates
{
    /// <summary>One planned day of content: semantic keys + target minutes. Contains no display text.</summary>
    public sealed record PlanShape(string TitleKey, string DescriptionKey, int Minutes);

    static readonly PlanShape Walk = new("Bootcamp.Plan.Walk", "Bootcamp.Plan.Walk.Desc", 20);
    static readonly PlanShape Workout = new("Bootcamp.Plan.Workout", "Bootcamp.Plan.Workout.Desc", 30);
    static readonly PlanShape WindDown = new("Bootcamp.Plan.WindDown", "Bootcamp.Plan.WindDown.Desc", 20);
    static readonly PlanShape Meditation = new("Bootcamp.Plan.Meditation", "Bootcamp.Plan.Meditation.Desc", 10);
    static readonly PlanShape Reading = new("Bootcamp.Plan.Reading", "Bootcamp.Plan.Reading.Desc", 20);
    static readonly PlanShape Stretch = new("Bootcamp.Plan.Stretch", "Bootcamp.Plan.Stretch.Desc", 15);
    static readonly PlanShape DeepWork = new("Bootcamp.Plan.DeepWork", "Bootcamp.Plan.DeepWork.Desc", 40);

    static bool IsIntense(PlanShape s) =>
        s.TitleKey == Workout.TitleKey || s.TitleKey == DeepWork.TitleKey;

    /// <summary>Deterministic template for one day. dayNumber is 1-based; non-positive returns null.</summary>
    public static PlanShape? ShapeFor(BootcampCategory category, int dayNumber)
    {
        if (dayNumber <= 0) return null;
        var baseShape = category switch
        {
            BootcampCategory.Sleep => (dayNumber % 3) switch { 1 => Walk, 2 => Workout, _ => Meditation },
            BootcampCategory.Fitness => (dayNumber % 4) switch { 1 => Walk, 3 => Workout, _ => Meditation },
            BootcampCategory.Learning => (dayNumber % 4) switch { 1 => Reading, 3 => DeepWork, _ => Meditation },
            BootcampCategory.Focus => (dayNumber % 4) switch { 1 => DeepWork, 2 => Meditation, 3 => Reading, _ => Workout },
            BootcampCategory.Mindfulness => (dayNumber % 4) switch { 1 => Meditation, 2 => Stretch, 3 => Walk, _ => Workout },
            // Habit (and any future category): the pattern the demo catalog already ships (i % 3).
            _ => (dayNumber % 3) switch { 1 => Walk, 2 => Workout, _ => WindDown },
        };

        // Every 7th day protects recovery: an intense day becomes restorative (sleep programs lean
        // evening, everything else stretches). Already-gentle days keep their own content.
        if (dayNumber % 7 == 0 && IsIntense(baseShape))
            return category == BootcampCategory.Sleep ? WindDown : Stretch;

        return baseShape;
    }

    /// <summary>Build the stored day list for a program from its own category template.</summary>
    public static List<BootcampDay> Materialize(Bootcamp b)
    {
        var days = new List<BootcampDay>(Math.Max(b.DurationDays, 0));
        for (int i = 1; i <= b.DurationDays; i++)
        {
            var shape = ShapeFor(b.Category, i);
            if (shape is null) continue;
            days.Add(new BootcampDay
            {
                DayNumber = i,
                PlanTitleKey = shape.TitleKey,
                PlanDescriptionKey = shape.DescriptionKey,
                TargetMinutes = shape.Minutes,
            });
        }
        return days;
    }
}

/// <summary>
/// Read-side view over a program's calendar. Older installs persisted enrollment without day
/// content (or with the old uniform walk placeholder), so display fills in the deterministic
/// template instead of mutating the aggregate. Two invariants:
///  - a stored day with real content is shown exactly as stored (seeded/user content is never re-invented);
///  - legacy uniform-placeholder days are repaired to their template, keeping completion + adaptation marks.
/// Everything returned is a fresh object, so the UI can never write through the view by accident.
/// </summary>
public static class BootcampDayDisplay
{
    /// <summary>The key the pre-Wave-3 enrollment fallback used for every single day.</summary>
    public const string LegacyFallbackPlanKey = "Bootcamp.Plan.Walk";

    /// <summary>
    /// The calendar as displayed: Count == DurationDays whenever DurationDays &gt; 0.
    /// Pure — the bootcamp is never modified. When a program was persisted with a day pointer past
    /// day 1 but without day records (older builds), the days BEHIND the pointer count as done:
    /// that is the only history those installs carry, and the alternative is a 0%-done program the
    /// user has demonstrably been playing.
    /// </summary>
    public static IReadOnlyList<BootcampDay> BuildView(Bootcamp b)
    {
        if (b.DurationDays <= 0) return Array.Empty<BootcampDay>();
        var stored = b.Days ?? new List<BootcampDay>();
        bool legacy = stored.Count == 0 || IsLegacyPlaceholder(stored, b.DurationDays);
        bool pointerHistory = stored.Count == 0 && b.CurrentDay > 1; // nothing stored, but the pointer moved

        var result = new List<BootcampDay>(b.DurationDays);
        for (int i = 1; i <= b.DurationDays; i++)
        {
            var day = stored.FirstOrDefault(d => d.DayNumber == i);
            var shape = BootcampTemplates.ShapeFor(b.Category, i);
            if (day is null)
            {
                if (shape is null) continue;
                result.Add(Clone(i, shape.TitleKey, shape.DescriptionKey, shape.Minutes,
                    pointerHistory && i < b.CurrentDay, false));
                continue;
            }
            if (shape is not null && (legacy || string.IsNullOrWhiteSpace(day.PlanTitleKey)))
                result.Add(Clone(i, shape.TitleKey, shape.DescriptionKey, shape.Minutes, day.IsCompleted, day.IsAdapted));
            else
                result.Add(Clone(i, day.PlanTitleKey,
                    string.IsNullOrWhiteSpace(day.PlanDescriptionKey) ? day.PlanTitleKey + ".Desc" : day.PlanDescriptionKey,
                    day.TargetMinutes, day.IsCompleted, day.IsAdapted));
        }
        return result;
    }

    static BootcampDay Clone(int dayNumber, string titleKey, string descKey, int minutes, bool completed, bool adapted)
        => new()
        {
            DayNumber = dayNumber,
            PlanTitleKey = titleKey,
            PlanDescriptionKey = descKey,
            TargetMinutes = minutes,
            IsCompleted = completed,
            IsAdapted = adapted,
        };

    /// <summary>
    /// The plan key this day would carry WITHOUT today's adaptation (localization key, never prose) —
    /// the honest "original plan" line when a rule touched the day. Falls back to the category
    /// template when the calendar is not materialized yet.
    /// </summary>
    public static string OriginalPlanKey(Bootcamp b, int dayNumber)
    {
        var stored = b.Days?.FirstOrDefault(d => d.DayNumber == dayNumber);
        if (stored is not null && !string.IsNullOrWhiteSpace(stored.PlanTitleKey) &&
            !IsLegacyPlaceholder(b.Days!, b.DurationDays))
            return stored.PlanTitleKey;
        return BootcampTemplates.ShapeFor(b.Category, dayNumber)?.TitleKey ?? string.Empty;
    }

    /// <summary>True when every stored day carries the same fallback content (the old bug's shape).</summary>
    public static bool IsLegacyPlaceholder(IReadOnlyList<BootcampDay> days, int duration)
        => days.Count >= Math.Min(duration, 2)
           && days.All(d => string.Equals(d.PlanTitleKey, LegacyFallbackPlanKey, StringComparison.Ordinal));
}
