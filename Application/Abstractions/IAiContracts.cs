using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.State;
using LIVORA.Domain.Models;

namespace LIVORA.Application.Abstractions;

// =============================================================================
// Wave 3 (master) — AI orchestration + safety contracts (AGENT 09 / AGENT 10 seam).
// Owned by the Integration Lead. The AI is a phrasing/insight layer over
// deterministic facts; it never supplies measurements. All of this is MAUI-free.
// =============================================================================

/// <summary>
/// The complete, MINIMAL context an AI call may receive. Built only by IContextBuilder —
/// nobody hand-assembles prompts elsewhere. Every field is derived/approved, never raw history.
/// </summary>
public sealed class IntelligenceContext
{
    /// <summary>Stable id to correlate provider logs without user data (diagnostics only).</summary>
    public required string CorrelationId { get; init; }
    public required string LanguageCode { get; init; }          // "en" | "fa"
    public required PersonalState State { get; init; }
    public required UserProfile Profile { get; init; }
    public required IReadOnlyList<Recommendation> DeterministicRecommendations { get; init; }
    /// <summary>Short goal/habit titles the AI may reference (never whole stores).</summary>
    public required IReadOnlyList<string> ActiveGoalTitles { get; init; }
    /// <summary>Baseline-vs-now deltas in human units, pre-computed deterministically.</summary>
    public required IReadOnlyList<StateDeltaFact> StateFacts { get; init; }
}

/// <summary>One deterministic fact the AI is allowed to talk about (value already computed).</summary>
public sealed record StateDeltaFact(string MetricKey, double Current, double? Baseline, string Unit, string FactKey, object[] FactArgs);

/// <summary>Builds the minimal context for one call. Consent + privacy filtering happen INSIDE.</summary>
public interface IContextBuilder
{
    Task<IntelligenceContext> BuildAsync(
        PersonalState state, UserProfile profile,
        IReadOnlyList<Recommendation> deterministicRecommendations,
        CancellationToken ct = default);
}

/// <summary>
/// Structured AI response. Deliberately string-light: keys + args only, so every rendered
/// sentence passes through localization. Numbers may appear ONLY inside FactsToVerify and are
/// re-checked against the context by the validator before anything is shown.
/// </summary>
public sealed class AiInsightResponse
{
    public string HeadlineKey { get; init; } = string.Empty;
    /// <summary>When true the headline is provider free-text (must still pass safety checks).</summary>
    public bool HeadlineIsProviderText { get; init; }
    public string BodyKey { get; init; } = string.Empty;
    public bool BodyIsProviderText { get; init; }
    public string? ProviderText { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<NumericClaim> Claims { get; init; } = Array.Empty<NumericClaim>();
    /// <summary>Actions the AI proposes — validator must map these onto allowed action kinds.</summary>
    public IReadOnlyList<string> ProposedActionKinds { get; init; } = Array.Empty<string>();
    /// <summary>Raw provider payload for diagnostics ONLY after secret/PII redaction.</summary>
    public string? RawForDiagnostics { get; init; }
}

/// <summary>One number the AI claims. The validator re-derives each against the context.</summary>
public sealed record NumericClaim(string MetricKey, double Value, string Unit);

/// <summary>Provider-neutral transport for one structured completion. No UI may touch this.</summary>
public interface IIntelligenceChatProvider
{
    /// <summary>False until the endpoint is configured AND reachable at least once.</summary>
    bool IsConfigured { get; }
    AiProviderKind Kind { get; }
    /// <summary>Display name for honesty labels ("AI: external endpoint", "AI: mock").</summary>
    string ProviderLabel { get; }
    /// <summary>
    /// Returns null on ANY transport failure (timeout, HTTP error, malformed body).
    /// Implementations must never throw into the app and never log prompt contents.
    /// </summary>
    Task<string?> CompleteStructuredAsync(
        string systemPrompt, string userJson, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>Registry of available providers; the orchestrator asks for one that works.</summary>
public interface IIntelligenceProviderRegistry
{
    IReadOnlyList<IIntelligenceChatProvider> Providers { get; }
    /// <summary>Best currently usable provider honoring settings + consent, or null = rules only.</summary>
    IIntelligenceChatProvider? PickEffective();
}

/// <summary>Verdict of the safety layer on one AI response.</summary>
public sealed class AiValidationResult
{
    public bool Accepted { get; init; }
    /// <summary>Machine-readable rejection reasons (never raw provider text).</summary>
    public IReadOnlyList<string> RejectionReasons { get; init; } = Array.Empty<string>();
    /// <summary>Cleaned response safe to render (claims re-derived or dropped).</summary>
    public AiInsightResponse? Safe { get; init; }
}

/// <summary>The defense layer (AGENT 10): assume the AI output is wrong until proven safe.</summary>
public interface IAiOutputValidator
{
    AiValidationResult Validate(AiInsightResponse? response, IntelligenceContext context);
}
