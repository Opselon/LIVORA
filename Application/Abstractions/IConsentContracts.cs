using LIVORA.Domain.Enums;

namespace LIVORA.Application.Abstractions;

// =============================================================================
// Wave 3 (master) — consent, secure storage, sync abstraction (AGENT 11 / 12 seam).
// Owned by the Integration Lead. MAUI-free.
// =============================================================================

/// <summary>
/// Explicit, revocable consent per data category. Default = Denied. Consent is checked at
/// every boundary that could move data (AI calls, provider sync, notifications).
/// </summary>
public interface IConsentService
{
    ConsentDecision Get(ConsentCategory category);
    Task SetAsync(ConsentCategory category, ConsentDecision decision, CancellationToken ct = default);
    /// <summary>Snapshot of every category — drives the privacy screen.</summary>
    Task<IReadOnlyDictionary<ConsentCategory, ConsentDecision>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Revoke everything data-related (connection revoke + wipe trigger point).</summary>
    Task RevokeAllAsync(CancellationToken ct = default);
}

/// <summary>
/// Encrypted-at-rest key/value seam for small secrets (provider tokens, gateway config).
/// Implementations must not write plaintext anywhere, including logs and temp files.
/// </summary>
public interface ISecureStorageService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// ABSTRACTION ONLY — no cloud backend exists yet and this wave must not pretend otherwise.
/// A real transport will be registered in a future wave; until then the composed sync queue
/// drains nowhere and reports SyncState.Pending, which the UI must show honestly.
/// </summary>
public interface ISyncTransport
{
    /// <summary>False for the built-in NoopSyncTransport — the UI must reflect that.</summary>
    bool IsConfigured { get; }
    string GatewayLabel { get; }
    Task<SyncPushResult> PushAsync(IReadOnlyList<SyncEnvelope> batch, CancellationToken ct = default);
}

/// <summary>One local change queued for the (future) backend.</summary>
public sealed class SyncEnvelope
{
    public required string EntityKind { get; init; }
    public required string EntityId { get; init; }
    public required SyncState LocalState { get; init; }
    public required long LocalVersion { get; init; }
    /// <summary>SHA-256 of the payload — lets a future backend detect conflicts without content in logs.</summary>
    public required string PayloadHash { get; init; }
    public DateTime ChangedAtUtc { get; init; }
}

/// <summary>Outcome of one push attempt. No silent partials.</summary>
public sealed class SyncPushResult
{
    public required bool Success { get; init; }
    public required IReadOnlyList<ConflictKind> Conflicts { get; init; } = Array.Empty<ConflictKind>();
    public string? ErrorCategory { get; init; }
}
