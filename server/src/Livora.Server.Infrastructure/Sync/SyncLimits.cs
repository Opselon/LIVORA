using Microsoft.Extensions.Configuration;

namespace Livora.Server.Infrastructure.Sync;

/// <summary>
/// PURPOSE: the sync lane's bounds in one place, read from the <c>Sync:*</c> config section. The
///          module validates at the HTTP edge, the service re-checks at the row level: the service
///          is also callable directly (jobs, tests, Phase-2 lanes), so both layers must stand alone.
/// OWNER: Agent 02 (lane w4-p1b-platform).
/// CONSUMES: <see cref="IConfiguration"/> keys (all optional — defaults are the shipped bounds):
///   Sync:MaxOperationsPerBatch   default 200   (hard floor 10, ceiling 200 — config clamps DOWN only)
///   Sync:MaxChangesLimit         default 100   (ceiling 500 — the page size a client may ask for)
///   Sync:MaxIdempotencyKeyLength default 80    (pinned to the sync_idempotency index width)
/// PROVIDES: <see cref="Default"/> for direct construction, <see cref="From"/> for host startup.
/// INVARIANTS (Wave 4 §52 — an unbounded request is a DoS and a cost leak):
///   - values are CLAMPED DOWN, never up: raising a ceiling above the hard bound requires a code
///     change and a review, not a config edit on a production box
///   - the payload size bound is NOT configurable: it equals the frozen sync_operations.PayloadJson
///     column width (60k chars, SyncBatchService.MaxPayloadChars) — config cannot outgrow the schema
///   - header name + charset rules are constants here so no other file spells "Idempotency-Key"
///     by hand (a typo would silently disable the mandatory-key gate)
/// </summary>
public sealed record SyncLimits
{
    /// <summary>The frozen §5c header carrying the request-level idempotency key.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Index-width bound for the key column — longer keys cannot be stored, so refuse early.</summary>
    public const int MaxIdempotencyKeyLength = 80;

    /// <summary>Config cannot shrink the batch below a usable floor; below this the host was misconfigured.</summary>
    public const int MinOperationsPerBatchFloor = 10;

    /// <summary>Hard ceiling: 200 ops already covers a generous offline day and bounds one transaction.</summary>
    public const int MaxOperationsPerBatchCeiling = 200;

    public const int DefaultChangesLimit = 100;
    public const int MinChangesLimit = 1;
    public const int MaxChangesLimitCeiling = 500;

    public int MaxOperationsPerBatch { get; init; } = MaxOperationsPerBatchCeiling;
    public int MaxChangesLimit { get; init; } = DefaultChangesLimit;

    /// <summary>Shipped defaults — what a host with no Sync: section gets.</summary>
    public static SyncLimits Default { get; } = new();

    /// <summary>
    /// Read + clamp. A non-numeric or absurd value falls back to the default rather than throwing:
    /// a typo in config must degrade to a tighter limit, not black out the whole host (law 3).
    /// </summary>
    public static SyncLimits From(IConfiguration configuration)
    {
        var ops = Clamp(configuration, "Sync:MaxOperationsPerBatch",
            MaxOperationsPerBatchCeiling, MinOperationsPerBatchFloor, MaxOperationsPerBatchCeiling);
        var changes = Clamp(configuration, "Sync:MaxChangesLimit",
            DefaultChangesLimit, MinChangesLimit, MaxChangesLimitCeiling);
        return new SyncLimits { MaxOperationsPerBatch = ops, MaxChangesLimit = changes };
    }

    private static int Clamp(IConfiguration configuration, string key, int fallback, int min, int max)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw.Trim(), out var value)) return fallback;
        return Math.Clamp(value, min, max);
    }
}
