namespace Livora.Server.Infrastructure.Engines.Decision;

/// <summary>
/// PURPOSE: the named fixtures the whole lane's tests and the self-check speak. Every scenario is
///          a fixed, hand-built data set with a fixed as-of instant — determinism by construction
///          (no DateTime.UtcNow anywhere in this namespace). Vectors are SHARED with the client
///          semantic-parity test: the same numbers, computed both sides, must agree.
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// INVARIANTS:
///   - AsOf is always 2026-03-16T18:00:00Z (a Monday evening — after the 18:00 habit-risk hour)
///   - history series are built from explicit daily values, never random
///   - NoData/PartialData fixtures exist so the refusal paths are as tested as the happy paths
/// </summary>
public static class EngineFixtures
{
    public static readonly DateTime AsOf = new(2026, 3, 16, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>A sleep baseline worth having: 10 nights of ~7h50 (470 min).</summary>
    public static IReadOnlyList<EngineDayRecord> SleepHistory(double minutes = 470, int nights = 14,
        double steps = 9000, double recovery = 0.75, double stress = 0.35, double energy = 0.7,
        double sleepQuality = 0.75, double bedtime = 1_380 /* 23:00 */)
        => Enumerable.Range(1, nights)
            .Select(i => new EngineDayRecord(AsOf.AddDays(-i - 1),
                SleepMinutes: minutes, SleepQuality: sleepQuality, SleepConsistency: 0.8,
                BedtimeMinutesOfDay: bedtime, Steps: steps, ActiveMinutes: 45,
                RecoveryScore: recovery, Stress: stress, Mood: 0.7, Energy: energy))
            .ToList();

    /// <summary>The five healthy days the momentum rule expects.</summary>
    public static DecisionInput NormalUser() => new(
        AsOf,
        Today: new EngineDayRecord(AsOf, SleepMinutes: 465, SleepQuality: 0.78, SleepConsistency: 0.82,
            BedtimeMinutesOfDay: 1_375, Steps: 9_200, ActiveMinutes: 50, RecoveryScore: 0.77,
            Stress: 0.32, Mood: 0.75, Energy: 0.72),
        History: SleepHistory(),
        Calendar: [], Goals: [], Habits: [new("h-water", "Water", 5, CompletedToday: true)],
        GoalProgress: []);

    /// <summary>Tonight: 5h30 of sleep against a ~7h50 baseline => 2.33h deficit (R1 fires High).</summary>
    public static DecisionInput SleepDeprived() => new(
        AsOf,
        Today: new EngineDayRecord(AsOf, SleepMinutes: 330, SleepQuality: 0.45, SleepConsistency: 0.5,
            BedtimeMinutesOfDay: 1_430 /* 23:50 */, Steps: 6_800, ActiveMinutes: 25, RecoveryScore: 0.60,
            Stress: 0.55, Mood: 0.5, Energy: 0.4),
        History: SleepHistory(),
        Calendar: [], Goals: [], Habits: [], GoalProgress: []);

    /// <summary>The product-law fusion scenario: poor sleep + high meeting load + high screen time
    /// + low activity + a fitness goal + a scheduled workout that collides with meetings.</summary>
    public static DecisionInput HighMeetingLoad() => new(
        AsOf,
        Today: new EngineDayRecord(AsOf, SleepMinutes: 330, SleepQuality: 0.45, SleepConsistency: 0.5,
            BedtimeMinutesOfDay: 1_430, Steps: 3_200, ActiveMinutes: 10, RecoveryScore: 0.42,
            Stress: 0.7, Mood: 0.45, Energy: 0.35),
        History: SleepHistory(),
        Calendar:
        [
            new("m-1", "meeting", "Standup", 9 * 60, 9 * 60 + 30),
            new("m-2", "meeting", "Planning", 10 * 60, 12 * 60),
            new("m-3", "meeting", "Review", 13 * 60, 15 * 60 + 30),   // 4h of meetings => S1
            new("w-1", "workout", "Strength class", 17 * 60 + 30, 19 * 60),
            new("f-1", "focus", "Deep work", 15 * 60 + 30, 16 * 60 + 20),
        ],
        Goals: [new("g-fitness", "Run a 10k", "fitness", 0.3, AsOf.AddDays(20))],
        Habits: [new("h-walk", "Walk", 4, CompletedToday: false)],
        GoalProgress: [new("g-fitness", AsOf.AddDays(-3), 0.3, AsOf.AddDays(20))],
        ScreenTimeMinutesToday: 320);

    /// <summary>Steps way above the personal baseline; recovery also fine (the client's ladder-2 path).</summary>
    public static DecisionInput HighlyActive() => new(
        AsOf,
        Today: new EngineDayRecord(AsOf, SleepMinutes: 470, SleepQuality: 0.8, SleepConsistency: 0.85,
            BedtimeMinutesOfDay: 1_350, Steps: 18_500, ActiveMinutes: 120, RecoveryScore: 0.8,
            Stress: 0.3, Mood: 0.8, Energy: 0.8),
        History: SleepHistory(steps: 9_000),
        Calendar: [], Goals: [], Habits: [], GoalProgress: []);

    /// <summary>3 days of history only: every baseline refuses (None), nothing may reshape the day.</summary>
    public static DecisionInput Beginner() => new(
        AsOf,
        Today: new EngineDayRecord(AsOf, SleepMinutes: 300, SleepQuality: 0.4, SleepConsistency: 0.4,
            BedtimeMinutesOfDay: 1_440, Steps: 4_000, ActiveMinutes: 15, RecoveryScore: 0.5,
            Stress: 0.6, Mood: 0.5, Energy: 0.45),
        History:
        [
            new(AsOf.AddDays(-2), SleepMinutes: 380, Steps: 6_000, RecoveryScore: 0.6, Stress: 0.5,
                SleepQuality: 0.6, Energy: 0.55, Mood: 0.6, ActiveMinutes: 20),
            new(AsOf.AddDays(-3), SleepMinutes: 360, Steps: 5_500, RecoveryScore: 0.58, Stress: 0.55,
                SleepQuality: 0.55, Energy: 0.5, Mood: 0.55, ActiveMinutes: 18),
            new(AsOf.AddDays(-4), SleepMinutes: 370, Steps: 6_200, RecoveryScore: 0.62, Stress: 0.5,
                SleepQuality: 0.6, Energy: 0.55, Mood: 0.6, ActiveMinutes: 22),
        ],
        Calendar: [new("w-1", "workout", "Gym", 18 * 60 + 30, 19 * 60 + 30)],
        Goals: [], Habits: [], GoalProgress: []);

    /// <summary>40 days of rich history + 6 full weeks of workouts: pattern gates become reachable.</summary>
    public static DecisionInput LongTerm()
    {
        var history = Enumerable.Range(1, 40).Select(i =>
        {
            var d = AsOf.AddDays(-i);
            // A late bedtime every Mon/Wed/Fri-ish cadence: a real recurring pattern to catch.
            bool late = (int)d.DayOfWeek is 1 or 3 or 5;
            return new EngineDayRecord(d,
                SleepMinutes: 460, SleepQuality: 0.72, SleepConsistency: 0.7,
                BedtimeMinutesOfDay: late ? 1_470 /* 00:30 */ : 1_350 /* 22:30 */,
                Steps: (int)d.DayOfWeek == 0 ? 4_200 : 9_000,     // Sunday dips
                ActiveMinutes: 45, RecoveryScore: 0.74, Stress: 0.4, Mood: 0.7, Energy: 0.68);
        }).ToList();
        return new DecisionInput(AsOf,
            Today: new EngineDayRecord(AsOf, SleepMinutes: 455, SleepQuality: 0.72, SleepConsistency: 0.7,
                BedtimeMinutesOfDay: 1_360, Steps: 8_900, ActiveMinutes: 44, RecoveryScore: 0.73,
                Stress: 0.4, Mood: 0.7, Energy: 0.66),
            History: history,
            Calendar: [], Goals: [new("g-read", "Read 12 books", "learning", 0.2, AsOf.AddDays(14))],
            Habits: [],
            GoalProgress: Enumerable.Range(0, 12)
                .Select(i => new EngineGoalProgressSample("g-read", AsOf.AddDays(-i), 0.2, AsOf.AddDays(14)))
                .ToList());
    }

    /// <summary>Nothing at all today, nothing in history: the pipeline must refuse, not guess.</summary>
    public static DecisionInput NoData() => new(
        AsOf,
        Today: new EngineDayRecord(AsOf),
        History: [], Calendar: [], Goals: [], Habits: [], GoalProgress: []);

    /// <summary>Half of today missing + 40-day stale sleep feed: the stale guard speaks, nothing else.</summary>
    public static DecisionInput PartialData() => new(
        AsOf,
        Today: new EngineDayRecord(AsOf, Steps: 5_000, ActiveMinutes: 20),
        History: SleepHistory(nights: 14).Select(r => r with { DateUtc = r.DateUtc.AddDays(-40) }).ToList(),
        Calendar: [], Goals: [], Habits: [], GoalProgress: []);

    /// <summary>Offline device sync: history is 5 days old across the board (freshness refusals).</summary>
    public static DecisionInput Offline() => new(
        AsOf,
        Today: new EngineDayRecord(AsOf),
        History: SleepHistory(nights: 10).Select(r => r with { DateUtc = r.DateUtc.AddDays(-5) }).ToList(),
        Calendar: [], Goals: [], Habits: [], GoalProgress: []);

    /// <summary>Premium posture fixture (entitlement is another lane; this just carries the same
    /// healthy shape with a program day scheduled — used by the plan-conflict suite).</summary>
    public static DecisionInput Premium() => new(
        AsOf,
        Today: new EngineDayRecord(AsOf, SleepMinutes: 420, SleepQuality: 0.7, SleepConsistency: 0.75,
            BedtimeMinutesOfDay: 1_395, Steps: 9_500, ActiveMinutes: 55, RecoveryScore: 0.7,
            Stress: 0.4, Mood: 0.75, Energy: 0.7),
        History: SleepHistory(),
        Calendar: [new("w-1", "workout", "Bootcamp day 12", 18 * 60 + 30, 19 * 60 + 30)],
        Goals: [new("g-bootcamp", "Finish bootcamp", "fitness", 0.6)],
        Habits: [], GoalProgress: []);

    /// <summary>Creator-account posture: same engine semantics (verification ladder is tier-blind);
    /// present so tests pin that entitlement never changes a deterministic verdict.</summary>
    public static DecisionInput Creator() => NormalUser();
}
