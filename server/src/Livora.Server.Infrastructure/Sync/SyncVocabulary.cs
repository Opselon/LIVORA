namespace Livora.Server.Infrastructure.Sync;

/// <summary>
/// PURPOSE: the one place the sync lane names its outcome and kind values, so the frozen core
///          table (sync_operations.Outcome), this lane's feed table, the API DTOs and the tests
///          cannot drift into four different spellings of "applied".
/// OWNER: Agent 02 (platform/sync lane).
/// CONSUMES: nothing.
/// PROVIDES: SyncOutcomes / SyncKinds / SyncConflictReasons constants.
/// INVARIANTS:
///   - outcome values are exactly the four the frozen P1 contract §5c promises the client:
///     applied | conflict | rejected | duplicate. The client switches on them; renaming one is a
///     breaking change.
///   - only <see cref="SyncOutcomes.Applied"/> ever writes a row into the change feed; the other
///     three are decisions recorded in the operation log and nothing more. That is what keeps
///     GET /changes a stream of things that actually happened.
/// </summary>
public static class SyncOutcomes
{
    public const string Applied = "applied";
    public const string Conflict = "conflict";
    public const string Rejected = "rejected";
    public const string Duplicate = "duplicate";
}

/// <summary>The three write intents a client may express (frozen contract §5c).</summary>
public static class SyncKinds
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";

    public static bool IsKnown(string? kind) =>
        kind is Create or Update or Delete;
}

/// <summary>
/// Machine-readable reasons inside a conflict/rejected result. Stable strings: they are the only
/// way the client can tell "your base is stale" from "this already exists" without parsing prose,
/// and both must render in en + fa from the client's localisation table.
/// </summary>
public static class SyncConflictReasons
{
    /// <summary>baseRevision does not match the entity's current server revision.</summary>
    public const string StaleBase = "stale_base";
    /// <summary>create for an entity the server already holds.</summary>
    public const string AlreadyExists = "already_exists";
    /// <summary>Payload failed shape/size validation — the operation was never applied.</summary>
    public const string InvalidShape = "invalid_shape";
    /// <summary>Payload exceeded the stored size bound.</summary>
    public const string PayloadTooLarge = "payload_too_large";
    /// <summary>The same operationId appeared twice inside one batch.</summary>
    public const string DuplicateInBatch = "duplicate_in_batch";
    /// <summary>More operations than the configured per-batch ceiling — the service-level gate for
    /// direct (non-HTTP) callers; the HTTP edge answers 400 validation_failed before this fires.</summary>
    public const string BatchTooLarge = "batch_too_large";
}
