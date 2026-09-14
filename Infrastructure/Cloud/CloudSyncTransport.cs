using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Cloud;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Infrastructure.Cloud;

/// <summary>
/// WAVE 4 P1-D — the first REAL <see cref="ISyncTransport"/>: it pushes the durable outbox that Wave
/// 3c's <see cref="LIVORA.Application.Sync.SyncQueue"/> has been accumulating through the typed API
/// port and <see cref="LivoraApiPort"/>'s <c>POST /sync/batch</c>.
///
/// It composes the existing queue rather than forking it: the queue owns durability, FIFO caps, the
/// journal and the state transitions, and this class owns only the wire call and the mapping back to
/// <see cref="SyncPushResult"/>. Every honesty rule the queue documents still holds verbatim:
/// <list type="bullet">
///   <item><b>not configured = no push.</b> <see cref="IsConfigured"/> requires all three real
///     preconditions — a base URL, a session this device holds, and a payload source that can actually
///     produce entity bytes. Any one missing and this transport reports false, so the queue's
///     <c>DrainAsync</c> is a visible no-op and the entries stay <see cref="SyncState.Pending"/>
///     instead of being silently consumed. That is the whole point of Wave 3c's design and the one
///     thing this lane must not quietly break.</item>
///   <item><b>no payload, no operation.</b> The queue stores hash + size only (§ durability rule), so
///     content has to be re-read from the local store at push time. An entity that cannot be read is
///     NEVER sent as an empty object — an empty payload applied server-side would erase a user's data
///     and mark it synced. It is dropped from the batch and counted.</item>
///   <item><b>success is per-batch and provable.</b> <see cref="SyncPushResult.Success"/> is true only
///     when the HTTP call answered 2xx AND the server returned one result per submitted operation AND
///     every result outcome was <c>applied</c> or <c>duplicate</c>. Anything else — a short result
///     list, an unknown outcome string, a 4xx/5xx, a transport failure — leaves the batch Pending so
///     the next drain retries it.</item>
///   <item><b>conflict is reported, never resolved.</b> A <c>conflict</c> outcome comes back as
///     <see cref="ConflictKind.BothChanged"/>; the frozen contract carries no entity id, so the queue
///     keeps its documented whole-batch reading and the entries sit in Conflict until the user decides
///     (product law §0.2 — deterministic code and a human, not a retry loop).</item>
/// </list>
///
/// IDEMPOTENCY (§5c): the batch's <c>Idempotency-Key</c> is derived, not random —
/// <c>sha256(sorted operationIds)</c>. It therefore "matches the per-operation operationId set" by
/// construction, a content-identical retry (the classic "the response was lost but the server applied
/// it") replays the ORIGINAL result instead of double-applying, and any change to the batch's content
/// produces a different key rather than colliding into a 409. Operation ids are equally deterministic:
/// <c>kind:id@localVersion#payloadHash[..12]</c>, so a re-queued newer edit is a different operation
/// and an unchanged retry is the same one.
/// </summary>
public sealed class CloudSyncTransport : ISyncTransport
{
    /// <summary>Machine tag this transport reports as its gateway label.</summary>
    public const string Label = "livora-cloud";

    /// <summary>Error categories this transport produces (machine tags; the UI maps them to keys).</summary>
    public const string CategoryPayloadMissing = "payload-missing";
    public const string CategoryShortResults = "short-result-list";
    /// <summary>The server answered for a different operation set than we submitted.</summary>
    public const string CategoryResultMismatch = "result-mismatch";
    public const string CategoryUnknownOutcome = "unknown-outcome";
    public const string CategoryNoOperations = "no-operations";

    private readonly ILivoraApiPort _port;
    private readonly ICloudApiOptions _options;
    private readonly ICloudAuthContext? _auth;
    private readonly ICloudSyncPayloadSource? _payloads;
    private readonly ILogger _log;

    /// <summary>Operations accepted per request (the queue's own BatchSize is 200; the server may cap lower).</summary>
    public const int MaxOperationsPerBatch = 200;

    public CloudSyncTransport(
        ILivoraApiPort port,
        ICloudApiOptions options,
        ICloudAuthContext? auth = null,
        ICloudSyncPayloadSource? payloadSource = null,
        ILogger<CloudSyncTransport>? logger = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auth = auth;
        _payloads = payloadSource;
        _log = logger ?? NullLogger<CloudSyncTransport>.Instance;
    }

    /// <summary>True when a push could actually be completed and confirmed (see class docs).</summary>
    public bool IsConfigured =>
        _options.IsConfigured
        && _auth is not null
        && _auth.HasSession
        && _payloads is { CanProvidePayloads: true };

    public string GatewayLabel => IsConfigured ? Label : NoopSyncTransport.Label;

    /// <summary>
    /// Why a push is not possible right now, as a localization key (both languages ship). Read by the
    /// connector surface; the queue's own <c>DrainReport.ReasonKey</c> stays what Wave 3c defined.
    /// Null = the transport is usable and has nothing to apologise for.
    /// </summary>
    public string? ReasonKey
    {
        get
        {
            if (!_options.IsConfigured) return "Cloud.Sync.Reason.NotConfigured";
            if (_auth is null || !_auth.HasSession) return "Cloud.Sync.Reason.NoSession";
            if (_payloads is not { CanProvidePayloads: true }) return "Cloud.Sync.Reason.NoPayloadSource";
            return null;
        }
    }

    /// <summary>Counters of the last push attempt (diagnostics; the UI shows them, never prose).</summary>
    public int LastSubmitted { get; private set; }
    public int LastSkippedMissingPayload { get; private set; }

    /// <summary>
    /// Whether the LAST push got an HTTP answer at all. Reachability is a separate fact from
    /// configuration — the bridge must not infer "the server answered" from "we were configured to
    /// send", because that is precisely how an Offline device ends up labelled Failed.
    /// </summary>
    public bool LastAttemptReachedServer { get; private set; }

    public async Task<SyncPushResult> PushAsync(
        IReadOnlyList<SyncEnvelope> batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ct.ThrowIfCancellationRequested();

        if (!IsConfigured)
        {
            // Refuse without touching the network. A queued entry keeps Pending — no discard, no lie.
            LastAttemptReachedServer = false;
            return Fail("not-configured");
        }

        var operations = new List<SyncOperationDto>(batch.Count);
        int missing = 0;
        foreach (var envelope in batch)
        {
            ct.ThrowIfCancellationRequested();
            var payloadJson = await _payloads!.GetPayloadAsync(envelope.EntityKind, envelope.EntityId, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                missing++;
                continue;   // entity gone locally: nothing to send, and never an empty object
            }

            JsonElement payload;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                payload = doc.RootElement.Clone();
            }
            catch (JsonException) { missing++; continue; }   // unreadable store content is not data to upload

            operations.Add(new SyncOperationDto(
                OperationId: OperationIdOf(envelope),
                EntityType: envelope.EntityKind,
                EntityId: envelope.EntityId,
                Kind: KindFor(envelope),
                BaseRevision: envelope.LocalVersion == 0 ? null : envelope.LocalVersion,
                Payload: payload));

            if (operations.Count >= MaxOperationsPerBatch) break;
        }

        LastSubmitted = operations.Count;
        LastSkippedMissingPayload = missing;

        if (operations.Count == 0)
        {
            // Nothing to push because nothing was readable. Failure, not success: the entries stay
            // Pending and the bridge prunes the vanished ones (it can see them by key).
            return Fail(missing > 0 ? CategoryPayloadMissing : CategoryNoOperations);
        }

        var idempotencyKey = IdempotencyKeyFor(operations);
        var res = await _port.SyncBatchAsync(new SyncBatchRequest(operations), idempotencyKey, ct)
            .ConfigureAwait(false);
        LastAttemptReachedServer = res.Status > 0;   // an HTTP answer, whatever the status

        if (!res.Ok || res.Value is null)
            return Fail(res.Code ?? "unknown");

        var results = res.Value.Results ?? Array.Empty<SyncOperationResultDto>();
        if (results.Count < operations.Count)
        {
            // The server answered for fewer operations than we submitted. Marking the batch synced
            // would claim confirmation for operations nobody confirmed.
            return Fail(CategoryShortResults, results.Count, idempotencyKey, res.CorrelationId);
        }

        // Strict pairing: every operation WE submitted must be answered by id. A result set that
        // covers other operations is not confirmation of ours, however well-formed it is.
        var byId = new Dictionary<string, SyncOperationResultDto>(StringComparer.Ordinal);
        foreach (var r in results)
            byId[r.OperationId ?? ""] = r;
        if (operations.Any(o => !byId.ContainsKey(o.OperationId)))
            return Fail(CategoryResultMismatch, results.Count, idempotencyKey, res.CorrelationId);

        var conflicts = new List<ConflictKind>();
        foreach (var op in operations)
        {
            var r = byId[op.OperationId];
            switch (r.Outcome)
            {
                case SyncOutcomes.Applied:
                case SyncOutcomes.Duplicate:
                    break;                                    // both mean "the server has this"
                case SyncOutcomes.Conflict:
                    conflicts.Add(ConflictKind.BothChanged);  // frozen shape carries no id — see class docs
                    break;
                case SyncOutcomes.Rejected:
                    return Fail(CategoryUnknownOutcome + ":" + LivoraApiCodes.ValidationFailed,
                        results.Count, idempotencyKey, res.CorrelationId);
                default:
                    // An outcome string this build does not know is NOT silently treated as applied.
                    return Fail(CategoryUnknownOutcome, results.Count, idempotencyKey, res.CorrelationId);
            }
        }

        _log.LogInformation("cloud-sync cid={CorrelationId} ops={Ops} skipped={Skipped} conflicts={Conflicts} key={IdempotencyKey}",
            res.CorrelationId, operations.Count, missing, conflicts.Count, idempotencyKey[..Math.Min(12, idempotencyKey.Length)]);

        return new SyncPushResult
        {
            Success = true,
            Conflicts = conflicts,
            ErrorCategory = null,
        };
    }

    /// <summary>
    /// The §5c <c>kind</c> for one envelope. A <see cref="SyncState.Conflict"/> entry that a user
    /// resolved as "keep local" comes back as Pending, so Pending covers both create and update; the
    /// local layer cannot tell them apart without a tombstone, so it sends <c>update</c> — a create
    /// on the server is idempotent by (entityType, entityId) and inventing <c>create</c> is the risk
    /// the mirror test pins against. Deletion never enters the queue (the store removes the row and
    /// the queue entry), so <c>delete</c> stays unused until a real delete path exists (R-P1D-3).
    /// </summary>
    internal static string KindFor(SyncEnvelope envelope) => SyncOperationKinds.Update;

    /// <summary>Deterministic per-operation id (see class docs' idempotency note).</summary>
    internal static string OperationIdOf(SyncEnvelope envelope)
    {
        var hash = envelope.PayloadHash.Length >= 12 ? envelope.PayloadHash[..12] : envelope.PayloadHash;
        return $"{envelope.EntityKind}:{envelope.EntityId}@{envelope.LocalVersion}#{hash}";
    }

    /// <summary>sha256 of the sorted operation ids — stable for identical content, different for anything else.</summary>
    internal static string IdempotencyKeyFor(IReadOnlyList<SyncOperationDto> operations)
    {
        var ids = operations.Select(o => o.OperationId).OrderBy(s => s, StringComparer.Ordinal);
        var joined = string.Join("\n", ids);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    private SyncPushResult Fail(string category, int returned = 0, string? idempotencyKey = null, string? correlationId = null)
    {
        _log.LogInformation("cloud-sync cid={CorrelationId} push=refused category={Category} returned={Returned} key={IdempotencyKey}",
            correlationId ?? "-", category, returned,
            idempotencyKey is null ? "-" : idempotencyKey[..Math.Min(12, idempotencyKey.Length)]);
        return new SyncPushResult
        {
            Success = false,
            Conflicts = Array.Empty<ConflictKind>(),
            ErrorCategory = category,
        };
    }
}

/// <summary>
/// WAVE 4 P1-D — the payload reader that makes real pushes possible: it resolves the entity bytes the
/// queue deliberately never stores, through the ONE existing port for reading stored entities by
/// kind/id (<see cref="ILocalDataCatalogService.ExportEntityAsync"/>). No second data path, no copy:
/// what the advanced-data screen can read is exactly what gets uploaded.
///
/// A kind the catalog does not know, or a row that no longer exists, surfaces as <c>null</c> (the
/// catalog throws <see cref="InvalidOperationException"/> carrying a rejection key) — the transport
/// then refuses to fabricate a payload. If the catalog service is missing from DI entirely, this
/// class reports <see cref="CanProvidePayloads"/> false, which keeps the transport unconfigured and
/// the queue Pending: the honest "sync is idle" state instead of a fake success.
/// </summary>
public sealed class CatalogSyncPayloadSource : ICloudSyncPayloadSource
{
    private readonly Func<ILocalDataCatalogService?> _catalog;

    /// <param name="catalog">Lazy resolver so construction never depends on DI ordering.</param>
    public CatalogSyncPayloadSource(Func<ILocalDataCatalogService?> catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public string Label => Catalog is null ? "none" : "local-catalog";

    public bool CanProvidePayloads => Catalog is not null;

    public async Task<string?> GetPayloadAsync(string entityKind, string entityId, CancellationToken ct = default)
    {
        var catalog = Catalog;
        if (catalog is null) return null;
        try
        {
            var json = await catalog.ExportEntityAsync(entityKind, entityId, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(json) ? null : json;
        }
        catch (InvalidOperationException) { return null; }   // unknown kind / not found / corrupt store
        catch (Exception) { return null; }                   // a broken store is "no payload", never a crash mid-drain
    }

    private ILocalDataCatalogService? Catalog
    {
        get
        {
            try { return _catalog(); }
            catch (Exception) { return null; }
        }
    }
}
