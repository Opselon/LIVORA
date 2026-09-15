using Livora.Server.Infrastructure.Engines.Decision;

namespace Livora.Server.Infrastructure.Engines.Decision.Explanation;

/// <summary>
/// PURPOSE: the explanation port. AI is NOT the source of truth anywhere in LIVORA: the engines
///          above decide state/baseline/trend/priority/plan/verification, and this port only ever
///          RENDERED what they computed. Wave 4 P1 ships exactly one implementation — the
///          deterministic template renderer — and NO code path in the server calls an AI provider.
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// INVARIANTS:
///   - the result says HOW it was generated ("deterministic_template") — never a silent "ai"
///   - the renderer can only speak keys the engines emitted; unknown keys show as [missing:key],
///     they are never invented into prose (fabricated explanation is the same lie as fabricated data)
///   - a Phase-2 AI provider would implement this port and MUST still pass AiSafetyValidator-style
///     checks (no new numbers, keys only) — the port shape makes that the only way to plug in.
/// </summary>
public interface IExplanationProvider
{
    /// <summary>True when this provider may participate. The deterministic renderer: always true.</summary>
    bool IsAvailable { get; }
    /// <summary>"deterministic_template" | "external_llm" — honesty label on every answer.</summary>
    string GeneratedBy { get; }
    ExplanationResult Explain(IReadOnlyList<PlanItemView> items, string locale, CancellationToken ct = default);
}

/// <summary>One rendered line: still structured (key kept beside the text) so the client can
/// re-localize without trusting our prose.</summary>
public sealed record ExplanationLine(string Key, IReadOnlyList<object> Args, string Text, bool KeyKnown);

public sealed record ExplanationResult(
    string GeneratedBy,
    string Locale,
    IReadOnlyList<ExplanationLine> Lines);

/// <summary>
/// PURPOSE: the only registered provider in Phase 1: the deterministic template renderer, in
///          en + fa, zero external calls. (Product law satisfied by construction: there is no AI
///          call to fail, so ai_unavailable can only be returned for a FUTURE provider port —
///          and the endpoint answers it honestly, never with a disguised fallback.)
/// </summary>
public sealed class DeterministicExplanationProvider : IExplanationProvider
{
    public bool IsAvailable => true;
    public string GeneratedBy => DeterministicExplanationRenderer.GeneratedBy;

    public ExplanationResult Explain(IReadOnlyList<PlanItemView> items, string locale, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var lines = items.Select(i => new ExplanationLine(
            i.ReasonKey, i.ReasonArgs,
            DeterministicExplanationRenderer.Render(i.ReasonKey, i.ReasonArgs, locale),
            DeterministicExplanationRenderer.Knows(i.ReasonKey, locale))).ToList();
        return new ExplanationResult(GeneratedBy, locale, lines);
    }
}
