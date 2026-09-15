namespace Livora.Server.Infrastructure.Sync;

// ============================================================================
// Sync-lane entities — declared by Agent 02 (lane w4-p1b-platform) in this
// folder, NOT in the frozen Persistence/Entities.cs, and wired into the shared
// model through SyncModelContribution (IModelContribution). The lead turns them
// into the ONE Wave4P1Schema migration at merge; no lane writes migrations.
// PURPOSE: the two pieces of state the frozen sync_operations log cannot carry:
//   - SyncBatchRecord: binds (UserId, Idempotency-Key) -> request hash + the
//     ORIGINAL response body, so a replay is byte-identical and a different body
//     under the same key is provably a mismatch (409 idempotency_key_reuse_mismatch).
//   - SyncChangeRecord: the per-user ordered change feed. ServerRevision is the
//     monotonic cursor position (`since`), ResultRevision is the entity's own head
//     after this change — so "what changed next" and "what revision is this entity
//     at" are both answerable from one ordered table.
// INVARIANTS (§4 conventions + §0 product law):
//   - text GUID "N" keys; *AtUtc timestamps; explicit HasMaxLength in the contribution
//   - no tokens, no secrets, no raw health values in audit columns: payload columns hold
//     the user's OWN sync content only; every other column carries ids/counts/enums
//   - insert-only: neither table is updated or deleted by this lane; a delete operation
//     is a feed row with kind="delete" (a tombstone in the stream), never a removal —
//     that is what stops a stale client silently resurrecting deleted data
// ============================================================================

/// <summary>
/// One row per accepted <c>POST /api/v1/sync/batch</c> request, inserted in the SAME transaction as
/// the batch it guards: a committed record always has its response, and a rolled-back batch leaves
/// no record behind, so a retry is still free to process. The unique index (UserId, IdempotencyKey)
/// means the DATABASE decides "this key was already used" — not a check-then-write that two
/// concurrent requests could both pass. <see cref="ResponseJson"/> is the exact text handed back on
/// replay, so the retry sees the original verdict — including the original serverTimeUtc — instead
/// of a re-decision.
/// </summary>
public sealed class SyncBatchRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";

    /// <summary>The client's Idempotency-Key header value. Opaque here; a uuid by convention.</summary>
    public string IdempotencyKey { get; set; } = "";

    /// <summary>SHA-256 (lowercase hex) over canonicalised JSON of the request body (§5c reuse test).</summary>
    public string RequestHash { get; set; } = "";

    /// <summary>The 200 body as serialised the first time — the bytes a replay returns verbatim. For
    /// a stored problem (a version-conflict batch) this holds the code+detail JSON to rebuild.</summary>
    public string ResponseJson { get; set; } = "";

    /// <summary>The HTTP status this record's decision produced (200 success, 409 version_conflict).
    /// A replay must return the SAME status the original did, or idempotency would be a lie.</summary>
    public int ResponseStatus { get; set; } = 200;

    /// <summary>Operation count of the accepted batch (diagnostics; never content).</summary>
    public int OperationCount { get; set; }

    /// <summary>Session that submitted it — revoking a device stops that device, not the account.</summary>
    public string? SessionId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

/// <summary>
/// One applied operation in the per-user change feed, ordered by <see cref="ServerRevision"/>.
/// Only applied operations get a row: a rejected or conflicted write changed nothing, so
/// broadcasting it to the other devices would claim something happened that did not happen.
/// </summary>
public sealed class SyncChangeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";

    /// <summary>Global monotonic position in this user's feed — the <c>since</c> cursor value.</summary>
    public long ServerRevision { get; set; }

    /// <summary>The entity's own revision after this change (1-based; what the next baseRevision must match).</summary>
    public long ResultRevision { get; set; }

    public string OperationId { get; set; } = "";

    /// <summary>The Idempotency-Key of the batch that produced it (traceability, not content).</summary>
    public string BatchKey { get; set; } = "";

    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";

    /// <summary>create | update | delete.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The baseRevision the client claimed — recorded so a conflict is explainable after the fact.</summary>
    public long BaseRevision { get; set; }

    /// <summary>Always <c>applied</c> today; carried so the feed's meaning is explicit per row.</summary>
    public string Outcome { get; set; } = SyncOutcomes.Applied;

    /// <summary>Canonical JSON of the applied payload — the user's own data, nothing derived.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>SHA-256 of the payload: a cheap "did the content really change" for a client storing by hash.</summary>
    public string ContentHash { get; set; } = "";

    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? SessionId { get; set; }
    public string? CorrelationId { get; set; }
}
