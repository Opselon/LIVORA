namespace Livora.Server.Infrastructure.Engines.Decision;

// ============================================================================
// PURPOSE: the vocabulary of the server-side deterministic engines. These are the
//          client enums RESTATEMENTs (Domain/Enums/*) — the server assembly must not
//          reference MAUI-client assemblies, so the shapes are copied deliberately and
//          pinned against drift by Engines/NumericRulesParityTests.cs (which reads the
//          client source files and compares member names + numeric constants verbatim).
// OWNER: Agent 10+11 (lane w4-p1e-engines).
// CONSUMES: nothing (BCL only) — pure code, no EF, no HTTP, no clock.
// PROVIDES: enums + the evidence fact type every engine output traces back to.
// INVARIANTS:
//   - member ORDER and NAMES mirror the client enums exactly (persisted as strings; a
//     renamed member is a breaking change in both directions).
//   - EngineProvenance answers the product-law question "where did this number come from"
//     for EVERY value the pipeline emits: observed|inferred|user-provided|assumed.
//   - VerificationTrust levels are distinct rungs, not synonyms: the ladder never collapses
//     to a generic "verified" (each verdict carries the EXACT rung it reached).
// ============================================================================

/// <summary>Where one value physically came from. Mirrors nothing client-side directly —
/// this is the provenance axis the Wave 4 evidence model needs (DataOrigin is the trust axis).</summary>
public enum EngineProvenance
{
    /// <summary>Measured by an instrument/provider: a real reading of the world.</summary>
    Observed = 0,
    /// <summary>Computed from other facts by a deterministic rule (labelled, never hidden).</summary>
    Inferred = 1,
    /// <summary>The user typed/tapped it: true, but self-reported.</summary>
    UserProvided = 2,
    /// <summary>A placeholder the engine had to assume to keep computing — lowest trust,
    /// and any output that relied on it is flagged.</summary>
    Assumed = 3,
}

/// <summary>Trust rung of a verification claim. Distinct levels NEVER collapse.
/// Higher value = stronger evidence lineage.</summary>
public enum VerificationTrust
{
    /// <summary>The user says so. Honest, but uncorroborated.</summary>
    SelfReported = 0,
    /// <summary>A device the user carries recorded it (sensor/watch/phone).</summary>
    DeviceDerived = 1,
    /// <summary>An external provider account reported it (Health Connect, Garmin, ...).</summary>
    ProviderDerived = 2,
    /// <summary>The system itself performed a check (delivery probe, signature, DB invariant).</summary>
    SystemVerified = 3,
    /// <summary>A staff human reviewed the evidence and ruled.</summary>
    HumanReviewed = 4,
}

/// <summary>How much history backs a baseline. Mirrors LIVORA.Domain.Enums.BaselineConfidence.</summary>
public enum EngineBaselineConfidence
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>Trend direction over a window. Mirrors TrendDirection — InsufficientData prevents
/// fake conclusions from tiny windows (client Application/State/TrendService.cs).</summary>
public enum EngineTrend
{
    InsufficientData = 0,
    Stable = 1,
    Improving = 2,
    Declining = 3,
}

/// <summary>Derived level vs the user's OWN baseline. Mirrors StateLevel.</summary>
public enum EngineLevel
{
    Unknown = 0,
    BelowBaseline = 1,
    Normal = 2,
    AboveBaseline = 3,
}

/// <summary>Data quality of one reading. Mirrors DataQuality.</summary>
public enum EngineQuality
{
    Missing = 0,
    Complete = 1,
    Estimated = 2,
    Stale = 3,
    Invalid = 4,
}

/// <summary>Priority ladder. Mirrors RecommendationPriority (Optional..Critical).</summary>
public enum EnginePriority
{
    Optional = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// <summary>Recommendation categories. Mirrors RecommendationCategory.</summary>
public enum EngineCategory
{
    Sleep = 0,
    Activity = 1,
    Recovery = 2,
    Focus = 3,
    Stress = 4,
    Habit = 5,
    Goal = 6,
    Program = 7,
    General = 8,
}

/// <summary>Typed actions. Mirrors RecommendationActionKind (same names, same order for the
/// shared subset) plus the plan-placement verbs the server fusion engine needs
/// (MoveWorkout/ShortenWorkout/SkipWorkout/NoExtraDemand) which have no client literal.</summary>
public enum EngineAction
{
    None = 0,
    ReduceTrainingIntensity = 1,
    ShortWalk = 2,
    ProtectFocusBlocks = 3,
    EarlierBedtime = 4,
    KeepRoutine = 5,
    Hydrate = 6,
    MorningLight = 7,
    TakeBreak = 8,
    ModerateScreenTime = 9,
    CompleteHabit = 10,
    AdvanceGoal = 11,
    DoProgramDay = 12,
    ReviewWeeklySummary = 13,
    WindDownBeforeBed = 14,
    NapBriefly = 15,
    // ---- server-side plan placement (no client literal; appended, never renumbered) ----
    MoveWorkout = 16,
    ShortenWorkout = 17,
    SkipWorkout = 18,
    NoExtraDemand = 19,
}

/// <summary>How much a plan item may move without breaking its purpose. Product law: every
/// recommended item says how flexible it is, so the user (and the planner) can negotiate.</summary>
public enum EngineFlexibility
{
    /// <summary>Non-negotiable for the day (safety guardrail).</summary>
    Fixed = 0,
    /// <summary>Can move in time, not in content.</summary>
    Movable = 1,
    /// <summary>Can be shortened down to a documented floor.</summary>
    Shortenable = 2,
    /// <summary>First thing cut when the day has no room.</summary>
    Droppable = 3,
    /// <summary>Beneficial, not asked-for effort; user's call.</summary>
    Optional = 4,
}

/// <summary>
/// One traceable number. Every value a decision emits references at least one MetricFact,
/// and every MetricFact references the observation row or request field it came from.
/// That chain is the "never fabricate" contract made mechanical (see NumericRules and the
/// evidence-trace tests): a number with no fact reference is a bug, caught by sweep tests.
/// </summary>
public sealed record MetricFact(
    string FactId,
    string MetricKey,
    double Value,
    string Unit,
    EngineProvenance Provenance,
    double Confidence,
    string SourceRef,
    DateTime AsOfDateUtc,
    EngineQuality Quality = EngineQuality.Complete)
{
    /// <summary>A derived fact (deviation, deficit hours…) is Inferred and names its parents.</summary>
    public IReadOnlyList<string> DerivedFrom { get; init; } = Array.Empty<string>();
}
