using Livora.Server.Infrastructure.Engines.Decision;

namespace Livora.Server.Modules.Intelligence;

/// <summary>
/// PURPOSE: the wire shapes for the intelligence surface. Every response number is paired with
///          the fact ids it traces to — a client that cannot show the trace cannot show the number.
///          camelCase (the shared serializer default; Wave 4 P1 §5).
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// INVARIANTS:
///   - enums cross the wire as stable lowercase strings (provenance/trust/priority), never numbers
///   - there is no bool "verified" field anywhere in this module's output, by design
///   - refusal arrays are part of the SUCCESS shape: what the engine did not say travels with
///     what it did say
/// </summary>
public sealed record FactView(
    string FactId, string MetricKey, double Value, string Unit, string Provenance,
    double Confidence, string SourceRef, DateTime AsOfDateUtc, string Quality,
    IReadOnlyList<string> DerivedFrom);

public sealed record MetricView(
    string MetricKey, double? Value, double? BaselineValue, string BaselineConfidence,
    int BaselineSamples, double? RelativeDeviation, string Level, bool HigherIsBetter,
    string Quality, string Provenance, double Confidence, string FactId, string BaselineFactId);

public sealed record StateView(
    DateTime AsOfUtc, IReadOnlyList<MetricView> Metrics, IReadOnlyList<FactView> Facts,
    double DataCompleteness, double Confidence, int DaysSinceFreshSleep,
    IReadOnlyList<string> Assumptions);

public sealed record RuleHitView(
    string RuleKey, string ReasonKey, IReadOnlyList<object> ReasonArgs, string Action,
    string Category, string Priority, IReadOnlyList<string> PlanAdjustments,
    double Confidence, IReadOnlyList<string> EvidenceFactIds);

public sealed record PlacementView(int StartMinutesOfDay, int DurationMinutes, IReadOnlyList<string> AdaptationIds);

public sealed record PlanItemViewDto(
    string ItemId, string Kind, string Action, string Category, string Priority, string Flexibility,
    int DurationMinutes, PlacementView? Placement, string ReasonKey, IReadOnlyList<object> ReasonArgs,
    IReadOnlyList<string> EvidenceFactIds, string SourceRuleKey, double Confidence);

public sealed record AdaptationViewDto(
    string AdaptationId, string TargetBlockId, string Verb, string ReasonKey,
    IReadOnlyList<object> ReasonArgs, IReadOnlyList<string> EvidenceFactIds,
    string SourceRuleKey, double Confidence,
    int OriginalStartMinutes, int OriginalDurationMinutes, int NewStartMinutes, int NewDurationMinutes);

public sealed record DecisionResponse(
    string DecisionId, DateTime GeneratedAtUtc, StateView State,
    IReadOnlyList<RuleHitView> RulesFired, IReadOnlyList<PlanItemViewDto> PlanItems,
    IReadOnlyList<AdaptationViewDto> Adaptations, IReadOnlyList<PlanItemViewDto> Recommendations,
    IReadOnlyList<string> Refusals,
    /// <summary>How this decision was produced — always deterministic; the honesty field.</summary>
    string GeneratedBy);

public sealed record WeeklyReviewResponse(
    bool Available, string? RefusalReason, int DaysPresentInWindow, DateTime WeekStartUtc,
    DateTime WeekEndUtc, string SleepTrend, string ActivityTrend, string RecoveryTrend,
    string StressTrend, double HabitConsistency, int StreakDays,
    IReadOnlyList<string> ImprovementKeys, IReadOnlyList<string> DeclineKeys,
    string FocusKey, IReadOnlyList<object> FocusArgs, string Confidence, string GeneratedBy);

public sealed record PatternView(
    string PatternId, string Kind, double Confidence, int SampleCount,
    DateTime DateFromUtc, DateTime DateToUtc, string Trend,
    IReadOnlyList<PatternEvidenceView> EvidenceKeys, IReadOnlyList<string> EvidenceFactIds);

public sealed record PatternEvidenceView(string Key, IReadOnlyList<object> Args);

public sealed record PatternScanResponse(
    IReadOnlyList<PatternView> Findings, IReadOnlyList<string> InsufficientKinds,
    IReadOnlyList<string> DismissedPatternIds, int DistinctHistoryDays, string GeneratedBy);

public sealed record ExplanationRequest(
    /// <summary>Keys + args exactly as a decision emitted them; unknown keys render visibly.</summary>
    IReadOnlyList<ExplanationKeyDto> Items, string? Locale = "en");

public sealed record ExplanationKeyDto(string Key, IReadOnlyList<object>? Args = null);

public sealed record ExplanationLineDto(string Key, IReadOnlyList<object> Args, string Text, bool KeyKnown);

public sealed record ExplanationResponseDto(string GeneratedBy, string Locale, IReadOnlyList<ExplanationLineDto> Lines);

/// <summary>Enum → stable wire string (single mapping; tests pin every value). The engines speak
/// enums internally; the wire never speaks numbers for vocabulary that humans localize.</summary>
public static class Wire
{
    public static string Provenance(EngineProvenance p) => p switch
    {
        EngineProvenance.Observed => "observed",
        EngineProvenance.Inferred => "inferred",
        EngineProvenance.UserProvided => "user_provided",
        _ => "assumed",
    };

    public static string Quality(EngineQuality q) => q switch
    {
        EngineQuality.Missing => "missing",
        EngineQuality.Complete => "complete",
        EngineQuality.Estimated => "estimated",
        EngineQuality.Stale => "stale",
        _ => "invalid",
    };

    public static string BaselineConfidence(EngineBaselineConfidence c) => c switch
    {
        EngineBaselineConfidence.High => "high",
        EngineBaselineConfidence.Medium => "medium",
        EngineBaselineConfidence.Low => "low",
        _ => "none",
    };

    public static string Level(EngineLevel l) => l switch
    {
        EngineLevel.BelowBaseline => "below_baseline",
        EngineLevel.Normal => "normal",
        EngineLevel.AboveBaseline => "above_baseline",
        _ => "unknown",
    };

    public static string Priority(EnginePriority p) => p switch
    {
        EnginePriority.Optional => "optional",
        EnginePriority.Low => "low",
        EnginePriority.Medium => "medium",
        EnginePriority.High => "high",
        _ => "critical",
    };

    public static string Action(EngineAction a) => WireName(a);
    public static string Category(EngineCategory c) => WireName(c);
    public static string Flexibility(EngineFlexibility f) => WireName(f);
    public static string Trend(EngineTrend t) => WireName(t);

    private static string WireName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        // PascalCase enum member -> camelCase wire string: one mechanical rule, no per-member tables.
        char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];

    public static DecisionResponse ToResponse(this DecisionOutput o) => new(
        o.DecisionId, o.GeneratedAtUtc,
        State: new StateView(o.State.AsOfUtc,
            o.State.Metrics.Values.OrderBy(k => k.MetricKey, StringComparer.Ordinal)
                .Select(m => new MetricView(m.MetricKey, m.Value, m.BaselineValue,
                    BaselineConfidence(m.BaselineConfidence), m.BaselineSamples, m.RelativeDeviation,
                    Level(m.Level), m.HigherIsBetter, Quality(m.Quality), Provenance(m.Provenance),
                    m.Confidence, m.FactId, m.BaselineFactId)).ToList(),
            o.State.Facts.Select(f => new FactView(f.FactId, f.MetricKey, f.Value, f.Unit,
                Provenance(f.Provenance), f.Confidence, f.SourceRef, f.AsOfDateUtc, Quality(f.Quality),
                f.DerivedFrom)).ToList(),
            o.State.DataCompleteness, o.State.Confidence, o.State.DaysSinceFreshSleep, o.State.Assumptions),
        RulesFired: o.RulesFired.Select(r => new RuleHitView(r.RuleKey, r.ReasonKey, r.ReasonArgs,
            Action(r.Action), Category(r.Category), Priority(r.Priority), r.PlanAdjustments,
            r.Confidence, r.EvidenceFactIds)).ToList(),
        PlanItems: o.PlanItems.Select(Item).ToList(),
        Adaptations: o.Adaptations.Select(a => new AdaptationViewDto(a.AdaptationId, a.TargetBlockId,
            a.Verb, a.ReasonKey, a.ReasonArgs, a.EvidenceFactIds, a.SourceRuleKey, a.Confidence,
            a.OriginalStartMinutes, a.OriginalDurationMinutes, a.NewStartMinutes, a.NewDurationMinutes)).ToList(),
        Recommendations: o.Recommendations.Select(Item).ToList(),
        Refusals: o.Refusals,
        GeneratedBy: "deterministic_engines");

    private static PlanItemViewDto Item(PlanItemView i) => new(i.ItemId, i.Kind, Action(i.Action),
        Category(i.Category), Priority(i.Priority), Flexibility(i.Flexibility), i.DurationMinutes,
        i.Placement is null ? null : new PlacementView(i.Placement.StartMinutesOfDay,
            i.Placement.DurationMinutes, i.Placement.AdaptationIds),
        i.ReasonKey, i.ReasonArgs, i.EvidenceFactIds, i.SourceRuleKey, i.Confidence);
}
