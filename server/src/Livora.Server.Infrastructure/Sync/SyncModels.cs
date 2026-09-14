using System.Text.Json;

namespace Livora.Server.Infrastructure.Sync;

// ============================================================================
// The sync lane's wire shapes (frozen P1 contract §5c). Plain records, no
// ASP.NET: the host module binds them and the service returns them, so the same types are
// assertable from a unit test without a host and serialisable with the shared camelCase options.
// OWNER: Agent 02.
// ============================================================================

/// <summary>One client operation inside <see cref="SyncBatchRequest"/>. Mirrors §5c field names.</summary>
public sealed record SyncOperationInput
{
    /// <summary>Client-minted id, unique per account. The operation-level idempotency key.</summary>
    public string OperationId { get; init; } = "";
    /// <summary>"goal" | "habit" | "plan_item" | "manual_entry" | ... (shape-checked, never trusted).</summary>
    public string EntityType { get; init; } = "";
    public string EntityId { get; init; } = "";
    /// <summary>"create" | "update" | "delete".</summary>
    public string Kind { get; init; } = SyncKinds.Update;
    /// <summary>The revision the client believes it is editing. 0 for a first-ever create.</summary>
    public long BaseRevision { get; init; }
    /// <summary>Arbitrary client payload. Stored verbatim after size checks; never interpreted here.</summary>
    public JsonElement? Payload { get; init; }
}

/// <summary>Body of <c>POST /api/v1/sync/batch</c>.</summary>
public sealed record SyncBatchRequest
{
    public IReadOnlyList<SyncOperationInput> Operations { get; init; } = [];
}

/// <summary>
/// Extra detail carried by a non-applied result. The contract fixes only <c>conflict?</c>; the
/// reason codes inside it are this lane's stable vocabulary (SyncConflictReasons).
/// </summary>
public sealed record SyncConflictInfo(
    string Reason,
    long BaseRevision,
    long ServerEntityRevision,
    long ServerRevision);

/// <summary>Per-operation verdict: outcome is one of applied|conflict|rejected|duplicate (§5c).</summary>
public sealed record SyncOperationResult(
    string OperationId,
    string Outcome,
    long ResultRevision,
    SyncConflictInfo? Conflict = null);

/// <summary>Body of a successful batch call, and exactly what a replay hands back.</summary>
public sealed record SyncBatchResponse(
    IReadOnlyList<SyncOperationResult> Results,
    DateTimeOffset ServerTimeUtc);

/// <summary>One feed entry handed out by <c>GET /api/v1/sync/changes</c>.</summary>
public sealed record SyncChangeView(
    long ServerRevision,
    long EntityRevision,
    string EntityType,
    string EntityId,
    string Kind,
    string OperationId,
    string ContentHash,
    DateTimeOffset CreatedAtUtc,
    JsonElement Payload);

/// <summary>Cursor-shaped feed response (§5c): not offset paging, so not a PagedResult.</summary>
public sealed record SyncChangesResponse(
    IReadOnlyList<SyncChangeView> Changes,
    long LatestRevision,
    bool HasMore);

/// <summary>
/// Row of <c>GET /api/v1/sync/operations</c> — the diagnostic view of the operation log. Payload
/// content is deliberately absent: the audit view is about decisions, not health values (law 5).
/// </summary>
public sealed record SyncOperationView(
    string OperationId,
    string EntityType,
    string EntityId,
    string Kind,
    long BaseRevision,
    long ResultRevision,
    string Outcome,
    string? ConflictReason,
    DateTimeOffset ReceivedAtUtc);
