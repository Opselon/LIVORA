using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.State;
using StateMetrics = LIVORA.Domain.Models.State.Metrics;

namespace LIVORA.Application.Reminders;

/// <summary>
/// One reminder the engine decided is worth firing right now. Pure data: text lives in
/// localization (TextKey + args) — never prose built here.
/// </summary>
public sealed record ReminderCandidate(
    string Kind,
    string? TargetId,
    string TextKey,
    object[] TextArgs,
    DateTime FireAt);

/// <summary>
/// Deterministic reminder decision engine (lane 09). Consumes the frozen
/// <see cref="ReminderSetting"/> list + an already-fired map and answers: what is worth
/// notifying RIGHT NOW, with per-day dedupe so the same reminder can never nag twice the
/// same day.
///
/// Thresholds deliberately MIRROR <c>RuleEngine</c> instead of forking a second definition:
/// <list type="bullet">
///   <item>Habit at-risk = RuleEngine R6 (streak ≥ 3, not completed today, after 18:00).</item>
///   <item>Wind-down = RuleEngine R1 High branch (sleep deficit vs personal baseline ≥ 2.5 h,
///     complete quality, usable baseline). No baseline → no claim, no reminder.</item>
///   <item>Log nudge = no manual entry today after 21:00 (caller supplies the
///     IManualEntryService-derived flag so this file stays IO-free and testable).</item>
///   <item>Bootcamp pending = enrolled program with today's day not completed.</item>
/// </list>
/// </summary>
public static class ReminderEngine
{
    // Machine tags persisted in ReminderSetting.Kind — never renamed, only added to.
    public const string KindHabit = "habit";
    public const string KindBootcamp = "bootcamp";
    public const string KindLog = "log";
    public const string KindWinddown = "winddown";

    /// <summary>The built-in reminder kinds the Reminders page exposes, in display order.</summary>
    public static readonly string[] BuiltInKinds = { KindHabit, KindBootcamp, KindLog, KindWinddown };

    // Mirrored RuleEngine constants (kept as explicit numbers so a RuleEngine refactor that
    // changes semantics cannot silently change reminders; lane 10's tests pin both sides).
    public const int HabitRiskHour = 18;      // RuleEngine R6 gate: now.Hour >= 18
    public const int HabitRiskStreak = 3;     // RuleEngine R6: CurrentStreak >= 3
    public const int LogNudgeHour = 21;       // product rule: never nag about logging before 21:00
    public const double HighSleepDebtHours = 2.5; // RuleEngine R1 High priority threshold

    /// <summary>Default schedule for a built-in kind (used when seeding settings).</summary>
    public static TimeSpan DefaultTimeFor(string kind) => kind switch
    {
        KindHabit => new TimeSpan(18, 30, 0),
        KindBootcamp => new TimeSpan(19, 0, 0),
        KindLog => new TimeSpan(21, 15, 0),
        KindWinddown => new TimeSpan(21, 45, 0),
        _ => new TimeSpan(20, 0, 0),
    };

    /// <summary>Canonical notification body key for a kind (settings store this; the engine
    /// re-checks the semantic gate before treating the reminder as still true).</summary>
    public static string TextKeyFor(string kind) => kind switch
    {
        KindHabit => "Reminders.Text.Habit",
        KindBootcamp => "Reminders.Text.Bootcamp",
        KindLog => "Reminders.Text.Log",
        KindWinddown => "Reminders.Text.Winddown",
        _ => "Reminders.Text.Bootcamp",
    };

    /// <summary>Stable dedupe key for the fired map: one kind+target can fire once per local day.</summary>
    public static string FireKey(string kind, string? targetId) =>
        kind + "|" + (string.IsNullOrEmpty(targetId) ? "*" : targetId);

    public static string FireKey(ReminderSetting s) => FireKey(s.Kind, s.TargetId);

    /// <summary>True when the 7-bit mask (bit0 = Sunday, <see cref="DayOfWeek"/> order) includes the day.</summary>
    public static bool DayMatches(int daysMask, DayOfWeek dow) => (daysMask & (1 << (int)dow)) != 0;

    /// <summary>Next local occurrence at TimeOfDay on a masked day, strictly after <paramref name="now"/>,
    /// searched up to 7 days ahead (null = never within the week, e.g. an all-days-off mask).</summary>
    public static DateTime? NextOccurrence(ReminderSetting s, DateTime now)
    {
        for (int i = 0; i <= 7; i++)
        {
            var day = now.Date.AddDays(i);
            if (!DayMatches(s.DaysMask, day.DayOfWeek)) continue;
            var at = day + s.TimeOfDay;
            if (at > now) return at;
        }
        return null;
    }

    /// <summary>True when this exact key already fired on the given local day (per-day dedupe).</summary>
    public static bool AlreadyFiredToday(
        IReadOnlyDictionary<string, DateTime>? firedDays, string key, DateTime now) =>
        firedDays is not null &&
        firedDays.TryGetValue(key, out var d) && d.Date == now.Date;

    /// <summary>RuleEngine R6 semantics, verbatim: streak ≥3, nothing logged today, after 18:00.</summary>
    public static bool HabitAtRisk(Habit h, DateTime now) =>
        h.CurrentStreak >= HabitRiskStreak && !h.IsCompletedOn(now.Date) && now.Hour >= HabitRiskHour;

    /// <summary>Wind-down gate: sleep deficit vs a usable personal baseline ≥ 2.5 h (RuleEngine R1 "High").</summary>
    public static bool WinddownDue(PersonalState? state)
    {
        if (state is null) return false;
        var sleep = state.Metrics.GetValueOrDefault(StateMetrics.SleepMinutes);
        if (sleep is not { BaselineValue: > 0, BaselineConfidence: not BaselineConfidence.None }) return false;
        if (sleep.Quality != DataQuality.Complete) return false;
        return (sleep.BaselineValue.Value - sleep.Value) / 60.0 >= HighSleepDebtHours;
    }

    /// <summary>A day of an enrolled program is still open.</summary>
    public static bool BootcampPending(IReadOnlyList<Bootcamp> bootcamps) =>
        bootcamps.Any(b => b.IsEnrolled && b.Today is { IsCompleted: false });

    /// <summary>
    /// Evaluate every enabled setting against the current world. A candidate appears only when
    /// BOTH the schedule (mask + time reached) and the semantic gate for its kind are true, and
    /// it has not already fired today. Output is keys + args only.
    /// </summary>
    /// <param name="settings">Persisted reminder settings (built-in kinds + habit-bound rows).</param>
    /// <param name="firedDays">key (<see cref="FireKey(ReminderSetting)"/>) → local day it last fired.</param>
    /// <param name="state">Current derived state (may be null before first load — then only non-state gates fire).</param>
    /// <param name="loggedToday">Caller-provided: a manual entry exists for today (IManualEntryService).</param>
    public static IReadOnlyList<ReminderCandidate> Evaluate(
        IReadOnlyList<ReminderSetting> settings,
        IReadOnlyDictionary<string, DateTime>? firedDays,
        PersonalState? state,
        IReadOnlyList<Habit> habits,
        IReadOnlyList<Bootcamp> bootcamps,
        bool loggedToday,
        DateTime now)
    {
        var outList = new List<ReminderCandidate>();
        foreach (var s in settings)
        {
            if (!s.Enabled) continue;
            var due = DueTimePassed(s, now);
            if (!due.HasValue) continue;

            switch (s.Kind)
            {
                case KindHabit:
                {
                    var atRisk = habits.Where(h => HabitAtRisk(h, now)).ToList();
                    if (atRisk.Count == 0) continue;
                    // Habit-bound settings carry a TargetId: match it; kind-global settings fan out.
                    var targets = string.IsNullOrEmpty(s.TargetId)
                        ? atRisk
                        : atRisk.Where(h => h.Id == s.TargetId).ToList();
                    foreach (var h in targets)
                    {
                        var key = FireKey(KindHabit, h.Id);
                        if (AlreadyFiredToday(firedDays, key, now)) continue;
                        outList.Add(new ReminderCandidate(KindHabit, h.Id,
                            // Reuse the rule engine's own bilingual reason sentence (same argument
                            // order: habit name, streak) instead of forking a parallel wording key.
                            "Rule.Reason.HabitStreakAtRisk",
                            new object[] { h.Name, h.CurrentStreak }, due!.Value));
                    }
                    break;
                }
                case KindWinddown:
                {
                    if (!WinddownDue(state)) continue;
                    var key = FireKey(s);
                    if (AlreadyFiredToday(firedDays, key, now)) continue;
                    outList.Add(new ReminderCandidate(KindWinddown, s.TargetId,
                        "Reminders.Text.Winddown", Array.Empty<object>(), due!.Value));
                    break;
                }
                case KindLog:
                {
                    if (loggedToday || now.Hour < LogNudgeHour) continue;
                    var key = FireKey(s);
                    if (AlreadyFiredToday(firedDays, key, now)) continue;
                    outList.Add(new ReminderCandidate(KindLog, s.TargetId,
                        "Reminders.Text.Log", Array.Empty<object>(), due!.Value));
                    break;
                }
                case KindBootcamp:
                {
                    if (!BootcampPending(bootcamps)) continue;
                    var key = FireKey(s);
                    if (AlreadyFiredToday(firedDays, key, now)) continue;
                    outList.Add(new ReminderCandidate(KindBootcamp, s.TargetId,
                        "Reminders.Text.Bootcamp", Array.Empty<object>(), due!.Value));
                    break;
                }
                // Unknown kinds are inert — a persisted string from a future version never fires
                // something this build cannot explain.
            }
        }
        // A global "habit" setting and lane 07's habit-bound rows can name the SAME habit in the
        // same pass; one notification per (kind,target,day) — the fired map can only remember one
        // answer per key anyway.
        return outList
            .GroupBy(c => FireKey(c.Kind, c.TargetId), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(c => c.FireAt)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.TargetId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The setting's occurrence for TODAY if its time has passed and today is in the mask;
    /// null otherwise (weekday not in the mask, or the time hasn't arrived yet).</summary>
    private static DateTime? DueTimePassed(ReminderSetting s, DateTime now)
    {
        if (!DayMatches(s.DaysMask, now.DayOfWeek)) return null;
        var at = now.Date + s.TimeOfDay;
        return at <= now ? at : null;
    }
}
