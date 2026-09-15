using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Infrastructure.Persistence;

// ============================================================================
// Core platform entities — the tables that must exist before any feature lane lands.
// PURPOSE: identity, sessions, audit, connector truth, sync. Owner: Agent 02 (+03 for identity).
// INVARIANTS (shared):
//   - text GUID keys ("N") so a client can mint an id offline and sync it later
//   - *Utc suffix on every timestamp; all stored in UTC
//   - no raw health values, tokens, secrets or payment data in any column here
// ============================================================================

/// <summary>A LIVORA account. Local credentials are optional (Google-only accounts have no hash).</summary>
public sealed class UserAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Null when the account is federated-only (Google sign-in, no password).</summary>
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }

    /// <summary>PBKDF2 "iterations$salt$subkey" — never a plaintext or bare hash of the password.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Google <c>sub</c> claim. Unique per account; the real anchor for federated login.</summary>
    public string? GoogleSubject { get; set; }

    public string? AppleSubject { get; set; }
    public string DisplayName { get; set; } = "";
    /// <summary>"en" | "fa" — drives localisation of server-authored text.</summary>
    public string PrimaryLocale { get; set; } = "en";
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    /// <summary>Server-authoritative tier. Never read from the client (Wave 4 §54).</summary>
    public AccountTier Tier { get; set; } = AccountTier.Free;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAtUtc { get; set; }
    /// <summary>Deletion is a scheduled, reversible-while-pending state, not an instant wipe.</summary>
    public DateTimeOffset? DeletionRequestedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    /// <summary>Opaque counter bumped on every mutation; the sync engine compares it, not the clock.</summary>
    public long RowVersion { get; set; }

    public List<AuthSession> Sessions { get; set; } = [];
    public List<ConnectorState> Connectors { get; set; } = [];
    public List<SyncOperation> SyncOperations { get; set; } = [];
}

public enum AccountStatus { Active = 0, Locked = 1, DeletionPending = 2, Deleted = 3 }
public enum AccountTier { Free = 0, Premium = 1, Pro = 2, Creator = 3 }

/// <summary>A signed-in device/session. The refresh token is stored only as a hash.</summary>
public sealed class AuthSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public UserAccount? User { get; set; }

    public string RefreshTokenHash { get; set; } = "";
    public string? DeviceLabel { get; set; }
    /// <summary>IP-less on purpose: only coarse, non-sensitive session metadata.</summary>
    public string? ClientPlatform { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset LastUsedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAtUtc { get; set; }
    /// <summary>Why a session died — distinguishes user logout from theft-mitigation rotation reuse.</summary>
    public string? RevokedReason { get; set; }
}

/// <summary>
/// A safe audit event: who/what/when, with a bounded metadata bag. Raw payloads never go here.
/// </summary>
public sealed class AuditEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Null for anonymous events (registration, failed login attempts keyed by subject).</summary>
    public string? UserId { get; set; }
    /// <summary>e.g. AccountCreated, Login, ProviderConnected, HealthSync, PurchaseConfirmed.</summary>
    public string Type { get; set; } = "";
    /// <summary>Stable identifier of the affected object (id or key), never personal content.</summary>
    public string? Subject { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; set; }
    /// <summary>Small JSON object; values must be ids/counts/enums, validated before write.</summary>
    public string? MetadataJson { get; set; }
}

/// <summary>
/// The persisted truth about a third-party connection (health provider, calendar, payments, AI
/// gateway). This is the table that makes "connected" a fact rather than a UI claim.
/// </summary>
public sealed class ConnectorState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public UserAccount? User { get; set; }

    /// <summary>"healthconnect" | "applehealth" | "googlecalendar" | "screentime" | "garmin" ...</summary>
    public string Provider { get; set; } = "";
    /// <summary>ok | unconfigured | permission_required | disconnected | degraded | unavailable | partial</summary>
    public string State { get; set; } = "unconfigured";
    public string Detail { get; set; } = "";
    /// <summary>Data may be usable but old — the client must show a stale badge, not hide it.</summary>
    public DateTimeOffset? LastSyncAtUtc { get; set; }
    public DateTimeOffset? DataAsOfUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One idempotent, ordered sync operation from a client. Insert-only: the log is the source of
/// truth for "what has the server already applied", which is how a retried offline batch is
/// absorbed without double-applying (Wave 4 §21).
/// </summary>
public sealed class SyncOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public UserAccount? User { get; set; }

    /// <summary>Client-minted id, unique per user — the idempotency key.</summary>
    public string OperationId { get; set; } = "";
    /// <summary>"goal" | "habit" | "plan_item" | "manual_entry" ...</summary>
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    /// <summary>create | update | delete.</summary>
    public string Kind { get; set; } = "update";
    /// <summary>Client's base revision; a mismatch against the stored revision is a conflict.</summary>
    public long BaseRevision { get; set; }
    public long ResultRevision { get; set; }
    public string PayloadJson { get; set; } = "{}";
    /// <summary>applied | conflict | rejected | duplicate.</summary>
    public string Outcome { get; set; } = "applied";
    public string? ConflictDetail { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Session that submitted it — lets a revocation stop an in-flight device, not the user.</summary>
    public string? SessionId { get; set; }
}
