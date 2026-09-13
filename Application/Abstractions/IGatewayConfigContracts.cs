using LIVORA.Domain.Enums;

namespace LIVORA.Application.Abstractions;

// =============================================================================
// Wave 3c (master) — AI gateway configuration + UI message DTOs.
// Owned by the Integration Lead. This is the SINGLE seam for the external AI
// endpoint: lanes consume it, nobody builds a competing config/secret path.
// MAUI-free by construction (compiles into the plain-net10.0 test project).
// =============================================================================

/// <summary>
/// Resolved gateway configuration the transport actually uses. Produced ONLY by
/// IGatewayConfigService — UI never sees the raw key, logs never see it either.
/// </summary>
public sealed record GatewayConfig
{
    public required string BaseUrl { get; init; }        // e.g. "http://sub.legoten.com:4455/v1"
    public required string ApiKey { get; init; }         // DECRYPTED at use time only — never log, never persist plaintext
    public required string Model { get; init; }          // routed model tag (server may map to a real model)
    public bool Enabled { get; init; }                   // master user switch (off until the user opts in)
    public bool RequireConsent { get; init; } = true;    // AiProcessing consent must be granted to actually call
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Where the answer came from: Embedded-obfuscated | UserEntered | NotConfigured.</summary>
    public string SourceLabel { get; init; } = "None";
    /// <summary>True when transport is plain HTTP — UI must warn honestly (see honesty rules).</summary>
    public bool IsInsecureTransport { get; init; }
}

/// <summary>
/// Loads/decrypts the gateway config. Implementations (lane 02) own the at-rest protection:
/// embedded key material is obfuscated (never plaintext in source), and the platform store
/// (DPAPI/Keystore where available) wraps user-entered keys. Obfuscation ≠ encryption
/// boundary — the ADR says so, the UI must never claim "unhackable".
/// </summary>
public interface IGatewayConfigService
{
    Task<GatewayConfig?> GetEffectiveAsync(CancellationToken ct = default);
    /// <summary>Persist a user-provided key (encrypted at rest). Null/empty removes the override.</summary>
    Task SetUserKeyOverrideAsync(string? apiKey, CancellationToken ct = default);
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
    /// <summary>Safe for UI/logs: base URL + model + source label + no key material whatsoever.</summary>
    Task<GatewayPublicStatus> GetPublicStatusAsync(CancellationToken ct = default);
}

/// <summary>What the UI may display about the AI connection. Carries NO secrets by construction.</summary>
public sealed record GatewayPublicStatus
{
    public required bool IsConfigured { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool UsesInsecureTransport { get; init; }
    public required string Model { get; init; }
    public required string KeySource { get; init; }      // "Built-in (obfuscated)" | "Your own key" | "None"
    /// <summary>Last successful health probe (UTC) — null = never proven reachable from this install.</summary>
    public DateTime? LastVerifiedUtc { get; init; }
    /// <summary>True only after a REAL round-trip test succeeded (Settings shows Connected on this).</summary>
    public bool LastProbeOk { get; init; }
}

// -----------------------------------------------------------------------------
// Structured UI prompts ("JSON prompts for user UI")
// -----------------------------------------------------------------------------

/// <summary>
/// A machine-readable UI message the intelligence layer emits INSTEAD of raw prose:
/// title/body localize-keys (or validated provider text) + typed actions. The UI renders
/// this DTO; it never string-concatenates AI output into layout. All text passes through
/// localization unless <see cref="IsProviderFreeText"/> AND the safety validator cleared it.
/// </summary>
public sealed record UiMessage
{
    public required string Id { get; init; }
    public required UiMessageKind Kind { get; init; }
    public string TitleKey { get; init; } = string.Empty;
    public object[] TitleArgs { get; init; } = Array.Empty<object>();
    public string BodyKey { get; init; } = string.Empty;
    public object[] BodyArgs { get; init; } = Array.Empty<object>();
    /// <summary>When true, BodyText is provider free-text already cleared by the validator.</summary>
    public bool IsProviderFreeText { get; init; }
    public string? BodyText { get; init; }
    public IReadOnlyList<UiAction> Actions { get; init; } = Array.Empty<UiAction>();
    /// <summary>Honesty: who produced this — rules or AI (and AI source label).</summary>
    public IntelligenceSource ProducedBy { get; init; } = IntelligenceSource.RulesOnly;
    public string? ProvenanceLabel { get; init; }
}

public enum UiMessageKind { Greeting, StateChange, Explanation, PlanSummary, Nudge, Warning, EmptyState }

/// <summary>A typed action button. Command names map onto registered VM commands — never URLs from the AI.</summary>
public sealed record UiAction(string CommandName, string LabelKey, object[] LabelArgs = default!, string? TargetId = null);
