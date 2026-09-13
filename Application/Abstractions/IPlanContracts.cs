using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Planning;

namespace LIVORA.Application.Abstractions;

// =============================================================================
// Wave 3b (master) — adaptive planning contracts (AGENT 07 / AGENT 13 seam).
// Owned by the Integration Lead. Deterministic-first: an Adaptation ALWAYS
// carries the evidence that triggered it; the UI renders keys, not prose.
// =============================================================================

/// <summary>
/// One recorded change to a plan: what was altered, why (localization key + args), the
/// evidence reference (rule key / metric fact) and how confident the system is.
/// </summary>
public sealed class Adaptation
{
    public required string Id { get; init; }
    /// <summary>PlanItem index or LinkedId this adaptation touched (null = whole-plan change).</summary>
    public string? TargetItemKey { get; init; }
    /// <summary>e.g. "Reduce workout intensity" (key + args, rendered by UI).</summary>
    public required string ChangeKey { get; init; }
    public object[] ChangeArgs { get; init; } = Array.Empty<object>();
    /// <summary>The WHY, as a localization key (e.g. "Adapt.Evidence.RecoveryBelowBaseline").</summary>
    public required string EvidenceKey { get; init; }
    public object[] EvidenceArgs { get; init; } = Array.Empty<object>();
    /// <summary>Rule/metric id that fired — traceable in tests and diagnostics.</summary>
    public required string SourceRuleKey { get; init; }
    /// <summary>0..1 — inherited from the weakest state input it used.</summary>
    public double Confidence { get; init; } = 0.8;
    /// <summary>Baseline confidence of the evidence (Insufficient adaptations must say so).</summary>
    public BaselineConfidence EvidenceConfidence { get; init; } = BaselineConfidence.Medium;
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Second-pass adaptation over an assembled DailyPlan: takes the deterministic plan plus
/// trends/patterns and returns a mutated copy + the list of adaptations applied.
/// Pure (no IO): callers persist the result. Must be idempotent on identical inputs.
/// </summary>
public interface IPlanAdaptationEngine
{
    AdaptationResult Adapt(DailyPlan plan, PersonalState state, DateTime now);
}

/// <summary>Plan + the adaptations that explain every difference from the input plan.</summary>
public sealed class AdaptationResult
{
    public required DailyPlan Plan { get; init; }
    public required IReadOnlyList<Adaptation> Adaptations { get; init; }
    public bool Changed => Adaptations.Count > 0;
}
