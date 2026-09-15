namespace Livora.Server.Infrastructure.Engines.Decision;

// ============================================================================
// PURPOSE: input/output shapes of the deterministic decision pipeline. Pure data —
//          no EF, no HTTP, no ASP.NET types. The HTTP module maps its DTOs onto these.
// OWNER: Agent 10+11 (lane w4-p1e-engines).
// INVARIANTS:
//   - every input number arrives with a source tag; absence is null, never 0
//   - every output number carries the fact ids it traces to (the "never fabricate" chain)
// ============================================================================

/// <summary>One day of history as the caller knows it. Null fields are genuinely absent.</summary>
public sealed record EngineDayRecord(
    DateTime DateUtc,
    double? SleepMinutes = null,
    double? SleepQuality = null,        // 0..1
    double? SleepConsistency = null,    // 0..1
    double? BedtimeMinutesOfDay = null,
    double? WakeMinutesOfDay = null,
    double? Steps = null,
    double? ActiveMinutes = null,
    double? RecoveryScore = null,       // 0..1
    double? Stress = null,              // 0..1 (lower is better)
    double? Mood = null,                // 0..1
    double? Energy = null,              // 0..1
    /// <summary>Where THIS record's numbers came from. Default observed (a real reading).</summary>
    EngineProvenance Provenance = EngineProvenance.Observed,
    IReadOnlyList<string>? CompletedHabitIds = null);

/// <summary>A calendar block the planner must respect (a workout, a meeting, a focus block).</summary>
public sealed record EngineCalendarBlock(
    string BlockId,
    string Kind,                 // "workout" | "meeting" | "focus" | "other" (lowercase)
    string Title,                // free text from the user's calendar — never re-labelled by us
    int StartMinutesOfDay,
    int EndMinutesOfDay);

/// <summary>A goal, as context for the fusion engine. Fraction 0..1, deadline optional.</summary>
public sealed record EngineGoal(
    string GoalId,
    string Name,
    string Category,             // "fitness" | "sleep" | "focus" | ...
    double Fraction,
    DateTime? DeadlineUtc = null);

/// <summary>A habit with just enough signal for the at-risk rule (streak + done-today).</summary>
public sealed record EngineHabit(
    string HabitId,
    string Name,
    int Streak,
    bool CompletedToday);

/// <summary>One goal-progress observation for the stagnation detector.</summary>
public sealed record EngineGoalProgressSample(
    string GoalId, DateTime DateUtc, double ProgressValue, DateTime? DeadlineUtc);

/// <summary>Everything the decision pipeline reads. Pure data in — decision out.</summary>
public sealed record DecisionInput(
    DateTime AsOfUtc,                                  // injected "now" — tests fix it
    EngineDayRecord Today,
    IReadOnlyList<EngineDayRecord> History,            // strictly BEFORE AsOfUtc day
    IReadOnlyList<EngineCalendarBlock> Calendar,
    IReadOnlyList<EngineGoal> Goals,
    IReadOnlyList<EngineHabit> Habits,
    IReadOnlyList<EngineGoalProgressSample> GoalProgress,
    /// <summary>Total calendar meeting minutes today (derived from Calendar by the caller or given).</summary>
    int? MeetingMinutesToday = null,
    double? ScreenTimeMinutesToday = null,
    int MaxRecommendations = NumericRules.MaxTotalRecommendations);

/// <summary>One metric after baseline comparison, with its fact id.</summary>
public sealed record DerivedMetric(
    string MetricKey,
    double? Value,
    double? BaselineValue,
    EngineBaselineConfidence BaselineConfidence,
    int BaselineSamples,
    double? RelativeDeviation,
    EngineLevel Level,
    bool HigherIsBetter,
    EngineQuality Quality,
    EngineProvenance Provenance,
    double Confidence,
    string FactId)
{
    /// <summary>Fact id of the BASELINE itself (a 28-day personal average is a computed number too —
    /// a claim like "below your baseline" cites BOTH the reading and the baseline. "" when no baseline).</summary>
    public string BaselineFactId { get; init; } = "";
}

/// <summary>The server-side mirror of the client's PersonalState: honest, complete, traceable.</summary>
public sealed record StateSnapshot(
    DateTime AsOfUtc,
    IReadOnlyDictionary<string, DerivedMetric> Metrics,
    IReadOnlyList<MetricFact> Facts,
    double DataCompleteness,       // 0..1 share of expected readings that exist
    double Confidence,             // weakest-link confidence (UserStateService parity)
    int DaysSinceFreshSleep,
    IReadOnlyList<string> Assumptions); // human-readable "what we had to assume", may be empty

/// <summary>One fired rule — the server restatement of the client RuleResult.</summary>
public sealed record RuleHit(
    string RuleKey,
    string ReasonKey,
    IReadOnlyList<object> ReasonArgs,
    EngineAction Action,
    EngineCategory Category,
    EnginePriority Priority,
    IReadOnlyList<string> PlanAdjustments,
    double Confidence,
    IReadOnlyList<string> EvidenceFactIds);

/// <summary>Where an item sits in the day, after conflict resolution.</summary>
public sealed record PlanPlacement(
    int StartMinutesOfDay,
    int DurationMinutes,
    /// <summary>True when the placement moved/shortened/dropped a colliding calendar block.</summary>
    IReadOnlyList<string> AdaptationIds);

/// <summary>One line of the fused plan. WHY is structured (key+args+fact ids), never prose.</summary>
public sealed record PlanItemView(
    string ItemId,
    /// <summary>"action" = asks something of the user (budgeted) · "guardrail" = a hold, not a task
    /// · "placement" = a change to an existing calendar block.</summary>
    string Kind,
    EngineAction Action,
    EngineCategory Category,
    EnginePriority Priority,
    EngineFlexibility Flexibility,
    int DurationMinutes,
    PlanPlacement? Placement,
    string ReasonKey,
    IReadOnlyList<object> ReasonArgs,
    /// <summary>Fact ids every number in ReasonArgs traces to. Non-empty whenever args are numeric.</summary>
    IReadOnlyList<string> EvidenceFactIds,
    string SourceRuleKey,
    double Confidence,
    /// <summary>True when this item made the visible-recommendation budget cut.</summary>
    bool WithinRecommendationBudget);

/// <summary>A recorded plan change with its explanation — move/shorten/skip, never blind.</summary>
public sealed record PlanAdaptationView(
    string AdaptationId,
    string TargetBlockId,
    /// <summary>"move" | "shorten" | "skip".</summary>
    string Verb,
    string ReasonKey,
    IReadOnlyList<object> ReasonArgs,
    IReadOnlyList<string> EvidenceFactIds,
    string SourceRuleKey,
    double Confidence,
    int OriginalStartMinutes,
    int OriginalDurationMinutes,
    int NewStartMinutes,
    int NewDurationMinutes);

/// <summary>The full decision: ONE coherent plan + the evidence chain behind every number.</summary>
public sealed record DecisionOutput(
    string DecisionId,
    DateTime GeneratedAtUtc,
    StateSnapshot State,
    IReadOnlyList<RuleHit> RulesFired,
    IReadOnlyList<PlanItemView> PlanItems,
    IReadOnlyList<PlanAdaptationView> Adaptations,
    /// <summary>The visible "recommend me 1-3 things, not 17" list — budget-capped actions.</summary>
    IReadOnlyList<PlanItemView> Recommendations,
    /// <summary>Explicit refusals: what the engine COULD say but honestly did not, and why.</summary>
    IReadOnlyList<string> Refusals);

/// <summary>Weekly look-back (ports WeeklySummaryService honesty rules).</summary>
public sealed record WeeklyReviewResult(
    bool Available,
    /// <summary>Machine reason when unavailable ("insufficient_data"). Never prose.</summary>
    string? RefusalReason,
    int DaysPresentInWindow,
    DateTime WeekStartUtc,
    DateTime WeekEndUtc,
    EngineTrend SleepTrend,
    EngineTrend ActivityTrend,
    EngineTrend RecoveryTrend,
    EngineTrend StressTrend,
    double HabitConsistency,
    int StreakDays,
    IReadOnlyList<string> ImprovementKeys,
    IReadOnlyList<string> DeclineKeys,
    string FocusKey,
    IReadOnlyList<object> FocusArgs,
    EngineBaselineConfidence Confidence);

/// <summary>A confidence-aware behavioral pattern finding (co-occurrence only — never a diagnosis).</summary>
public sealed record PatternFindingView(
    string PatternId,
    string Kind,                       // PatternKind member name (stable string form)
    double Confidence,
    int SampleCount,
    DateTime DateFromUtc,
    DateTime DateToUtc,
    string Trend,                      // EngineTrend name; "InsufficientData" when directionless
    IReadOnlyList<PatternEvidenceKeyView> EvidenceKeys,
    IReadOnlyList<string> EvidenceFactIds);

/// <summary>Localization key + numeric args ({"pattern.evidence.late-nights", [3, 7]}).</summary>
public sealed record PatternEvidenceKeyView(string Key, IReadOnlyList<object> Args);

/// <summary>Scan output: findings + the kinds that REFUSED for lack of samples (explicit, not silent).</summary>
public sealed record PatternScanOutput(
    IReadOnlyList<PatternFindingView> Findings,
    IReadOnlyList<string> InsufficientKinds,
    int DistinctHistoryDays);
