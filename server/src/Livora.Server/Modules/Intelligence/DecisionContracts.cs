using Livora.Server.Infrastructure.Engines.Decision;

namespace Livora.Server.Modules.Intelligence;

/// <summary>
/// PURPOSE: the request body for POST /api/v1/intelligence/decision. The SERVER has no health
///          store yet (that is another lane's wave), so the caller supplies the day records it
///          wants reasoned over — and every number in the response traces back to these rows.
///          Bounded on purpose: an unbounded context is a DoS and a cost leak (Wave 4 §52), and
///          the frozen ApiProblem reserves context_budget_exceeded for exactly this refusal.
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// INVARIANTS:
///   - all values optional: absence is null, never 0 (null = "no reading", 0 = "a reading of zero")
///   - history/callendar/habits/goals have hard caps enforced by the handler BEFORE computing
///   - asOfUtc is required: the engine never reads a wall clock the client cannot reproduce
/// </summary>
public sealed record EngineDayDto(
    DateTime DateUtc,
    double? SleepMinutes = null,
    double? SleepQuality = null,
    double? SleepConsistency = null,
    double? BedtimeMinutesOfDay = null,
    double? Steps = null,
    double? ActiveMinutes = null,
    double? RecoveryScore = null,
    double? Stress = null,
    double? Mood = null,
    double? Energy = null,
    /// <summary>observed | inferred | user_provided | assumed (default observed).</summary>
    string? Provenance = null,
    IReadOnlyList<string>? CompletedHabitIds = null);

public sealed record CalendarBlockDto(
    string BlockId, string Kind, string Title, int StartMinutesOfDay, int EndMinutesOfDay);

public sealed record GoalDto(string GoalId, string Name, string Category, double Fraction, DateTime? DeadlineUtc);

public sealed record HabitDto(string HabitId, string Name, int Streak, bool CompletedToday);

public sealed record GoalProgressSampleDto(string GoalId, DateTime DateUtc, double ProgressValue, DateTime? DeadlineUtc);

public sealed record DecisionRequest(
    DateTime AsOfUtc,
    EngineDayDto Today,
    IReadOnlyList<EngineDayDto>? History = null,
    IReadOnlyList<CalendarBlockDto>? Calendar = null,
    IReadOnlyList<GoalDto>? Goals = null,
    IReadOnlyList<HabitDto>? Habits = null,
    IReadOnlyList<GoalProgressSampleDto>? GoalProgress = null,
    int? MeetingMinutesToday = null,
    double? ScreenTimeMinutesToday = null);

/// <summary>Context-budget caps for the decision surface (handler-enforced, machine-coded refusal).</summary>
public static class IntelligenceLimits
{
    public const int MaxHistoryDays = 90;
    public const int MaxCalendarBlocks = 64;
    public const int MaxHabits = 32;
    public const int MaxGoals = 32;
    public const int MaxGoalProgressSamples = 256;

    /// <summary>Violations found in a request (empty = within budget); the handler turns these into
    /// the shared problem envelope — no prose invented here.</summary>
    public static IReadOnlyDictionary<string, string[]>? BudgetViolations(DecisionRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.History?.Count > MaxHistoryDays)
            errors["history"] = [$"history exceeds {MaxHistoryDays} days"];
        if (r.Calendar?.Count > MaxCalendarBlocks)
            errors["calendar"] = [$"calendar exceeds {MaxCalendarBlocks} blocks"];
        if (r.Habits?.Count > MaxHabits)
            errors["habits"] = [$"habits exceed {MaxHabits}"];
        if (r.Goals?.Count > MaxGoals)
            errors["goals"] = [$"goals exceed {MaxGoals}"];
        if (r.GoalProgress?.Count > MaxGoalProgressSamples)
            errors["goalProgress"] = [$"goal progress exceeds {MaxGoalProgressSamples} samples"];
        return errors.Count == 0 ? null : errors;
    }

    /// <summary>Field-level sanity refusals (negative durations, out-of-range ratios).</summary>
    public static IReadOnlyDictionary<string, string[]>? ValidationErrors(DecisionRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.Today is null) errors["today"] = ["a today record is required"];
        if (r.Calendar is { } cal)
        {
            foreach (var b in cal.Where(b => b.StartMinutesOfDay < 0 || b.EndMinutesOfDay > 24 * 60
                                             || b.EndMinutesOfDay <= b.StartMinutesOfDay))
                errors[$"calendar.{b.BlockId}"] = ["block minutes must lie within 0..1440 with end > start"];
        }
        foreach (var g in (r.Goals ?? []).Where(g => g.Fraction is < 0 or > 1))
            errors[$"goal.{g.GoalId}"] = ["fraction must be within 0..1"];
        if (errors.Count == 0) return null;
        return errors;
    }

    // ---- mapping DTOs -> pure engine input (the ONLY conversion boundary) ----------------------
    public static DecisionInput ToInput(this DecisionRequest r) => new(
        AsOfUtc: r.AsOfUtc,
        Today: Map(r.Today),
        History: (r.History ?? []).Select(Map).ToList(),
        Calendar: (r.Calendar ?? []).Select(b => new EngineCalendarBlock(
            b.BlockId, b.Kind.ToLowerInvariant(), b.Title, b.StartMinutesOfDay, b.EndMinutesOfDay)).ToList(),
        Goals: (r.Goals ?? []).Select(g => new EngineGoal(g.GoalId, g.Name, g.Category.ToLowerInvariant(), g.Fraction, g.DeadlineUtc)).ToList(),
        Habits: (r.Habits ?? []).Select(h => new EngineHabit(h.HabitId, h.Name, h.Streak, h.CompletedToday)).ToList(),
        GoalProgress: (r.GoalProgress ?? []).Select(s => new EngineGoalProgressSample(
            s.GoalId, s.DateUtc, s.ProgressValue, s.DeadlineUtc)).ToList(),
        MeetingMinutesToday: r.MeetingMinutesToday,
        ScreenTimeMinutesToday: r.ScreenTimeMinutesToday);

    private static EngineDayRecord Map(EngineDayDto d) => new(
        d.DateUtc, d.SleepMinutes, d.SleepQuality, d.SleepConsistency, d.BedtimeMinutesOfDay,
        null, d.Steps, d.ActiveMinutes, d.RecoveryScore, d.Stress, d.Mood, d.Energy,
        ParseProvenance(d.Provenance), d.CompletedHabitIds);

    public static EngineProvenance ParseProvenance(string? s) => s switch
    {
        null or "" => EngineProvenance.Observed,
        "inferred" => EngineProvenance.Inferred,
        "user_provided" => EngineProvenance.UserProvided,
        "assumed" => EngineProvenance.Assumed,
        _ => EngineProvenance.Observed,
    };
}
