using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;
using StateMetrics = LIVORA.Domain.Models.State.Metrics;

namespace LIVORA.Tests.Wave3c.Plan;

/// <summary>
/// Lane-05 fixtures: synthetic PersonalState + DailyPlan shapes for the adaptive engine and the
/// ranker. Self-contained (the base suite's StateFactory is internal to another namespace and has
/// different knobs). Levels are derived from (Value − Baseline)/Baseline exactly like production,
/// so every fixture is honest about what the engine sees.
/// </summary>
internal static class Wave3cFixtures
{
    /// <summary>Monday 2026-09-21 10:00 — a mid-morning "now" with evening room left.</summary>
    public static readonly DateTime Now = new(2026, 9, 21, 10, 0, 0);

    // ---- metric builder --------------------------------------------------------------

    internal static MetricState Metric(string key, double value, double baseline,
        bool higherBetter = true,
        BaselineConfidence confidence = BaselineConfidence.High,
        DataQuality quality = DataQuality.Complete) => new()
    {
        MetricKey = key,
        Value = value,
        BaselineValue = baseline > 0 ? baseline : null,
        BaselineConfidence = baseline > 0 ? confidence : BaselineConfidence.None,
        RelativeDeviation = baseline > 0 ? (value - baseline) / baseline : null,
        HigherIsBetter = higherBetter,
        Quality = quality,
    };

    /// <summary>
    /// Builds a PersonalState. Defaults = a calm NORMAL day (Scenario A): every metric exactly at
    /// baseline with High confidence and a fresh feed — no rule may fire from this alone.
    /// </summary>
    internal static PersonalState State(
        double recoveryValue = 0.70, double recoveryBaseline = 0.70,
        BaselineConfidence recoveryConfidence = BaselineConfidence.High,
        double sleepMinutes = 450, double sleepBaseline = 450,
        BaselineConfidence sleepConfidence = BaselineConfidence.High,
        double steps = 8000, double stepsBaseline = 8000,
        BaselineConfidence stepsConfidence = BaselineConfidence.High,
        int sleepDaysSinceFreshData = 0,
        double stateConfidence = 0.85)
    {
        var m = new Dictionary<string, MetricState>
        {
            [StateMetrics.RecoveryScore] = Metric(StateMetrics.RecoveryScore, recoveryValue, recoveryBaseline,
                confidence: recoveryConfidence),
            [StateMetrics.SleepMinutes] = Metric(StateMetrics.SleepMinutes, sleepMinutes, sleepBaseline,
                confidence: sleepConfidence),
            [StateMetrics.Steps] = Metric(StateMetrics.Steps, steps, stepsBaseline,
                confidence: stepsConfidence),
            [StateMetrics.Stress] = Metric(StateMetrics.Stress, 0.4, 0.4, higherBetter: false),
            [StateMetrics.SleepQuality] = Metric(StateMetrics.SleepQuality, 0.8, 0.8),
            [StateMetrics.ActiveMinutes] = Metric(StateMetrics.ActiveMinutes, 30, 30),
            [StateMetrics.Mood] = Metric(StateMetrics.Mood, 0.7, 0.7),
            [StateMetrics.Energy] = Metric(StateMetrics.Energy, 0.7, 0.7),
        };
        return new PersonalState
        {
            GeneratedAt = Now,
            Confidence = stateConfidence,
            DataCompleteness = 1,
            Sleep = new SleepState
            {
                Duration = m[StateMetrics.SleepMinutes],
                Quality = m[StateMetrics.SleepQuality],
                Consistency = Metric("c", 0.8, 0.8),
                Bedtime = Metric("b", 1380, 1380),
                DaysSinceFreshData = sleepDaysSinceFreshData,
            },
            Activity = new DailyActivityState
            {
                Steps = m[StateMetrics.Steps],
                ActiveMinutes = m[StateMetrics.ActiveMinutes],
            },
            Recovery = new RecoveryState { Score = m[StateMetrics.RecoveryScore] },
            Wellness = new WellnessState2
            {
                Stress = m[StateMetrics.Stress],
                Mood = m[StateMetrics.Mood],
                Energy = m[StateMetrics.Energy],
            },
            Focus = new FocusState { Estimated = Metric("fe", 0.7, 0.7) },
            Habits = new HabitStateSnapshot { HabitId = "", Name = "" },
            Metrics = m,
        };
    }

    // ---- plan builder ------------------------------------------------------------------

    internal static PlanItem Item(
        RecommendationCategory category, int baseMin, int? plannedMin = null,
        RecommendationActionKind action = RecommendationActionKind.None,
        string? linkedId = null, string titleKey = "Plan.Item.FocusBlocks",
        TimeSpan? start = null, TimeSpan? end = null,
        string? adaptedByRule = null) => new()
    {
        Action = action,
        TitleKey = titleKey,
        BaseMinutes = baseMin,
        PlannedMinutes = plannedMin ?? baseMin,
        PreferredWindowStart = start,
        PreferredWindowEnd = end,
        AdaptedByRule = adaptedByRule,
        LinkedId = linkedId,
        Category = category,
    };

    /// <summary>
    /// The standard plan used by the ladder/precedence tests: a 60-min program day (linked to the
    /// workout goal), a 40-min activity block, an 08:00 focus block (250 base) and a habit prompt.
    /// Base total = 60+40+250+10 = 360 → cap = 432 committed minutes.
    /// </summary>
    internal static DailyPlan StandardPlan(
        int programBase = 60, int programPlanned = 60,
        int activityBase = 40, int activityPlanned = 40,
        int focusBase = 250,
        TimeSpan? focusStart = null,
        IReadOnlyList<string>? adaptationRuleKeys = null,
        string programLinkedId = "goal-workout") => new()
    {
        Date = Now.Date,
        Items = new List<PlanItem>
        {
            Item(RecommendationCategory.Program, programBase, programPlanned,
                action: RecommendationActionKind.DoProgramDay, linkedId: programLinkedId,
                titleKey: "Bootcamp.Plan.Workout"),
            Item(RecommendationCategory.Activity, activityBase, activityPlanned,
                action: RecommendationActionKind.ShortWalk, titleKey: "Rec.ShortWalk"),
            Item(RecommendationCategory.Focus, focusBase,
                action: RecommendationActionKind.ProtectFocusBlocks,
                start: focusStart ?? new TimeSpan(8, 0, 0),
                end: (focusStart ?? new TimeSpan(8, 0, 0)) + TimeSpan.FromSeconds(focusBase * 60)),
            Item(RecommendationCategory.Habit, 10,
                action: RecommendationActionKind.CompleteHabit, linkedId: "habit-read",
                titleKey: "Plan.Item.Habit"),
        },
        AdaptationRuleKeys = adaptationRuleKeys ?? Array.Empty<string>(),
    };

    internal static DailyPlan EmptyPlan() => new()
    {
        Date = Now.Date,
        Items = Array.Empty<PlanItem>(),
    };

    // ---- goal builder --------------------------------------------------------------------

    internal static Goal WorkoutGoal(DateTime deadline, double fraction = 0.2) => new()
    {
        Id = "goal-workout",
        Name = "goal-workout",
        TargetValue = 10,
        ProgressValue = fraction * 10,
        Deadline = deadline,
    };

    // ---- recommendation builder (ranker) ---------------------------------------------------

    internal static Recommendation Rec(
        string id,
        RecommendationActionKind action = RecommendationActionKind.ShortWalk,
        RecommendationCategory category = RecommendationCategory.Activity,
        RecommendationPriority priority = RecommendationPriority.Medium,
        int duration = 15,
        double confidence = 0.8,
        DateTime? createdAt = null,
        string? textKey = null,
        object[]? textArgs = null,
        bool timeless = false) => new()
    {
        Id = id,
        ActionKind = action,
        Category = category,
        ScoredPriority = priority,
        DurationMinutes = duration,
        Confidence = confidence,
        // Unique default TextKey per id so unrelated test cards never collide in the ranker's
        // dedupe fingerprint (ActionKind+Category+TextKey+args) unless a test wants that.
        TextKey = textKey ?? "Rec.Test." + id,
        TextArgs = textArgs ?? Array.Empty<object>(),
        CreatedAt = timeless ? default : createdAt ?? Now,
        ProducedByRule = "wave3c.test",
    };

    /// <summary>Item-level fingerprint only (notes may legally add rule keys without touching minutes).</summary>
    internal static string ItemsFingerprint(DailyPlan p) => ItemsBody(p);

    /// <summary>Full structural equality of two plans (deep-equal idempotence checks).</summary>
    internal static string PlanFingerprint(DailyPlan p) =>
        string.Join(";",
            p.Date.ToString("O"),
            string.Join(",", p.AdaptationRuleKeys),
            ItemsBody(p));

    private static string ItemsBody(DailyPlan p) =>
        string.Join("|", p.Items.Select(i => string.Join(",",
            (int)i.Action, (int)i.Category, i.TitleKey, i.DetailKey ?? "",
            string.Join("~", i.DetailArgs.Select(a => a?.ToString() ?? "")),
            i.BaseMinutes, i.PlannedMinutes,
            i.PreferredWindowStart?.ToString() ?? "x",
            i.PreferredWindowEnd?.ToString() ?? "x",
            i.AdaptedByRule ?? "x", i.LinkedId ?? "x")));
}
