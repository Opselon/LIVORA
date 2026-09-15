using System.Diagnostics;
using System.Text.Json;
using Livora.Server.Infrastructure.Observability;
using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Livora.Server.Infrastructure.Sync;

/// <summary>What <see cref="SyncBatchService.ApplyAsync"/> decided at the request level.</summary>
public enum SyncBatchStatus
{
    /// <summary>Fresh batch processed; <see cref="SyncBatchApplyResult.ResponseJson"/> is the exact
    /// text that went out and the same text every replay of this key hands back.</summary>
    Applied = 0,
    /// <summary>Same Idempotency-Key + same canonical request body — hand back the ORIGINAL stored
    /// result (<see cref="SyncBatchApplyResult.ResponseStatus"/> says which status it was).</summary>
    Replay = 1,
    /// <summary>Same Idempotency-Key, different body — §5c: 409 idempotency_key_reuse_mismatch.</summary>
    KeyMismatch = 2,
    /// <summary>The batch was wholly stale (every operation came back <c>conflict</c>): nothing was
    /// applied, and the edge answers 409 version_conflict naming both revisions. See class remarks
    /// for how this reconciles §5c's per-operation <c>conflict</c> outcome with the platform rule
    /// "an optimistic conflict is a 409, never a silent overwrite of newer user data".</summary>
    VersionConflict = 3,
}

/// <summary>
/// Result of a batch call. For Applied and for the success case of Replay, <see cref="ResponseJson"/>
/// is the exact wire text, so a replay is byte-identical to the original by construction. For the
/// problem cases (VersionConflict now, Replay-of-a-problem later) the result carries code + detail
/// and the edge rebuilds the shared envelope — with THIS request's correlation id, because the id
/// identifies the response, not the decision.
/// </summary>
public sealed record SyncBatchApplyResult(
    SyncBatchStatus Status,
    string? ResponseJson = null,
    int ResponseStatus = 200,
    string? ProblemCode = null,
    string? ProblemDetail = null,
    SyncOperationResult? Conflict = null);

/// <summary>
/// PURPOSE: the deterministic core of <c>POST /api/v1/sync/batch</c> and <c>GET /api/v1/sync/changes</c>
///          (Wave 4 §21, frozen contract §5c). Three guarantees, each enforced twice — once in this
///          code, once in a unique index of the contributed schema:
///          <list type="number">
///            <item><b>idempotency</b> — (UserId, IdempotencyKey) is unique in sync_batch_records, so
///            the DATABASE decides reuse rather than a check-then-write that two concurrent requests
///            could both pass. Every completed request — 200 <i>or</i> 409 — is stored under its key:
///            a replay returns the original result, and a different body under the same key answers
///            <see cref="SyncBatchStatus.KeyMismatch"/> (edge: 409 idempotency_key_reuse_mismatch).</item>
///            <item><b>optimistic revision conflict</b> — an update/delete whose baseRevision differs
///            from the entity's current head, or a create on an entity the server already holds, comes
///            back <c>conflict</c> naming BOTH revisions (client base + server head). Nothing is ever
///            overwritten: the entity head is echoed unchanged. A batch whose operations ALL conflict
///            is a wholly stale client and the edge answers 409 version_conflict; a mixed batch stays
///            200 with per-operation outcomes, because partial progress is real progress.</item>
///            <item><b>offline-safe ordering</b> — applied operations take strictly increasing
///            per-user ServerRevision values in request-array order, and the unique index
///            (UserId, ServerRevision) makes a duplicated slot impossible rather than merely unlikely.
///            Only applied operations enter the feed, so <c>GET /changes?since=rev</c> is a stream of
///            things that really happened in one total order that every device agrees on.</item>
///          </list>
/// </summary>
/// <remarks>
/// OWNER: Agent 02 (lane w4-p1b-platform).
/// CONSUMES: <see cref="LivoraDbContext"/> (the frozen sync_operations log + this lane's contributed
///           tables), <see cref="SyncCanonicalJson"/> for hashing, <see cref="SyncTelemetry"/> for
///           real counters. PROVIDES: no ASP.NET types — every rule is unit-testable without a host.
/// DECISION ORDER per operation (array order IS apply order):
/// <code>
///   invalid shape or oversized payload                  -> rejected  (current entity head echoed)
///   operationId already APPLIED by an earlier batch     -> duplicate (with the stored applied revision)
///   operationId repeated inside THIS batch (2nd copy)   -> duplicate (duplicate_in_batch)
///   create on an entity the server already holds        -> conflict  (already_exists)
///   update/delete whose baseRevision != entity head     -> conflict  (stale_base)
///   everything else                                     -> applied   (entity head +1, feed slot +1)
/// </code>
/// An update/delete for an entity the server has never seen with baseRevision 0 is an upsert: the
/// client's head really was 0 and 0 is what the server holds (standard offline convergence).
/// A rejected operation is the client's shape bug, not the server's: it must not cost the rest of
/// the batch its apply, and "rejected" is the honest per-operation verdict for it.
/// WHY ONLY APPLIED OPS ARE LOGGED: sync_operations is the durable answer to "have you already done
/// this operationId", which exists to stop a retried offline batch double-applying. A rejected or
/// conflicted verdict did not change anything, so recording it would poison the well: a client that
/// rebases and retries the SAME operationId (same intended edit, correct new baseRevision) must be
/// able to apply, not be told "duplicate" of a decision that applied nothing.
/// WHY A DELETE IS NOT RESURRECTABLE: the entity head is derived from the feed's last applied
/// revision for that (type,id) — a delete is an applied feed row, so any later write built on the
/// pre-delete revision conflicts instead of silently resurrecting the row. A create after a delete
/// answers conflict/already_exists (Phase-1 rule; offline clients upsert or mint a new id — filed as
/// a §5c clarification request for the lead).
/// PROVIDER NOTES (documented limits, asserted by SyncBatchInvariantsTests):
///   - SQLite (CI/local default) and Npgsql (deploy): transactions + unique indexes enforce all
///     three guarantees, including against concurrent requests.
///   - In-memory (the shared LivoraWebFixture provider): EF ignores transactions and does not
///     enforce unique indexes, so the guarantees rest on the decision code alone. That is enough for
///     the fixture's sequential request pattern — and is exactly why the schema-level guarantees are
///     asserted separately on a temporary SQLite file (SyncPersistenceSchemaTests).
/// </remarks>
public sealed class SyncBatchService(
    LivoraDbContext db,
    SyncLimits limits,
    ILogger<SyncBatchService> logger)
{
    /// <summary>Same bound as the frozen sync_operations.PayloadJson column width (60k chars).
    /// Deliberately not configurable: a config edit must never outgrow the schema.</summary>
    public const int MaxPayloadChars = 60_000;

    /// <summary>
    /// An operationId the frozen 80-char log column cannot key (blank or longer) has no addressable
    /// identity in the log. Such an operation is REJECTED at the shape gate — echoed back under the
    /// client's own text but never logged — because inventing a log row keyed on truncated garbage
    /// would make a later retry of a *different* garbage id resolve as a duplicate of it.
    /// </summary>
    public const string UnaddressableOperationId = "unaddressable";

    // ======================================================================================== batch ==

    public async Task<SyncBatchApplyResult> ApplyAsync(
        string userId,
        string? sessionId,
        string? correlationId,
        string idempotencyKey,
        SyncBatchRequest request,
        string requestHash,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var telemetryStatus = SyncTelemetry.BatchOutcomes.Error;
        try
        {
            // At most two passes: a second collision after a full recompute against committed state
            // is no longer a race, it is a logic bug, and it must surface rather than be swallowed.
            for (var attempt = 0; ; attempt++)
            {
                var result = await RunAttemptAsync(userId, sessionId, correlationId, idempotencyKey,
                    request, requestHash, ct);

                if (result is null)
                {
                    if (attempt > 0)
                        throw new InvalidOperationException(
                            "sync batch collided twice against the idempotency/revision indexes; " +
                            "refusing to guess. Retry with a new Idempotency-Key.");
                    logger.LogWarning(
                        "sync batch collided with a concurrent request, recomputing once: " +
                        "user={UserId} key={Key} correlationId={CorrelationId}",
                        userId, idempotencyKey, correlationId);
                    db.ChangeTracker.Clear();
                    continue;
                }

                telemetryStatus = result.Status switch
                {
                    SyncBatchStatus.Applied => SyncTelemetry.BatchOutcomes.Applied,
                    SyncBatchStatus.Replay => SyncTelemetry.BatchOutcomes.Replay,
                    SyncBatchStatus.KeyMismatch => SyncTelemetry.BatchOutcomes.KeyMismatch,
                    _ => SyncTelemetry.BatchOutcomes.VersionConflict,
                };
                return result;
            }
        }
        finally
        {
            SyncTelemetry.RecordBatch(telemetryStatus,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, correlationId);
        }
    }

    private async Task<SyncBatchApplyResult?> RunAttemptAsync(
        string userId, string? sessionId, string? correlationId, string idempotencyKey,
        SyncBatchRequest request, string requestHash, CancellationToken ct)
    {
        // ---- request-level idempotency: the stored record decides, not a check-then-write -------
        var existing = await db.Set<SyncBatchRecord>()
            .FirstOrDefaultAsync(b => b.UserId == userId && b.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
            return Replay(existing, requestHash);

        if (!db.Database.IsRelational())
        {
            // In-memory (the shared fixture): no transactions, no index enforcement — run the same
            // decision code and let it stand on its own logic (see class remarks).
            return await ProcessOnceAsync(userId, sessionId, correlationId, idempotencyKey,
                request, requestHash, ct);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var outcome = await ProcessOnceAsync(userId, sessionId, correlationId, idempotencyKey,
                request, requestHash, ct);

            // Null = the INSERT collided with a concurrent batch on a unique index; leaving the
            // transaction uncommitted discards every row this attempt decided, so nothing half-lands.
            if (outcome is null)
                return null;

            await tx.CommitAsync(ct);
            return outcome;
        });
    }

    private static SyncBatchApplyResult Replay(SyncBatchRecord stored, string requestHash)
    {
        if (stored.RequestHash != requestHash)
            return new SyncBatchApplyResult(SyncBatchStatus.KeyMismatch);

        if (stored.ResponseStatus == StatusCodesOk)
            return new SyncBatchApplyResult(SyncBatchStatus.Replay,
                ResponseJson: stored.ResponseJson, ResponseStatus: StatusCodesOk);

        // A stored problem: same status + code + detail. The correlation id is rebuilt per response
        // by the edge (it identifies THIS response, not the stored decision), so it is not stored.
        var problem = JsonSerializer.Deserialize<StoredProblem>(stored.ResponseJson, SyncCanonicalJson.Options);
        return new SyncBatchApplyResult(SyncBatchStatus.Replay,
            ResponseStatus: stored.ResponseStatus,
            ProblemCode: problem?.Code ?? "conflict",
            ProblemDetail: problem?.Detail ?? "The recorded batch result could not be read.");
    }

    private const int StatusCodesOk = 200;
    private const int StatusCodesConflict = 409;

    /// <summary>The stored body of a replayed problem answer (code + detail only, no per-response fields).</summary>
    private sealed record StoredProblem(string Code, string Detail);

    /// <summary>Null return means "unique-index collision, recompute once against committed state".</summary>
    private async Task<SyncBatchApplyResult?> ProcessOnceAsync(
        string userId, string? sessionId, string? correlationId, string idempotencyKey,
        SyncBatchRequest request, string requestHash, CancellationToken ct)
    {
        var operations = request.Operations ?? [];

        // ---- service-level size gate: the HTTP edge answers 400 before this can fire, but the
        // service is also callable directly (jobs, tests, Phase-2 lanes) and must bound its own work
        // exactly like the contract says (Wave 4 §52) — every op rejected, nothing applied.
        if (operations.Count > limits.MaxOperationsPerBatch)
        {
            var tooBig = operations
                .Select(o => new SyncOperationResult(o.OperationId ?? "", SyncOutcomes.Rejected, 0,
                    new SyncConflictInfo(SyncConflictReasons.BatchTooLarge, o.BaseRevision, 0, 0)))
                .ToArray();
            var bigJson = JsonSerializer.Serialize(new SyncBatchResponse(tooBig, DateTimeOffset.UtcNow),
                SyncCanonicalJson.Options);
            return new SyncBatchApplyResult(SyncBatchStatus.Applied, bigJson);
        }

        // ---- entity heads: derived from the feed itself, so one table remains the source of truth
        var entityTypes = operations.Select(o => o.EntityType ?? "")
            .Distinct(StringComparer.Ordinal).ToArray();
        var headRows = entityTypes.Length == 0
            ? []
            : await db.Set<SyncChangeRecord>()
                .Where(c => c.UserId == userId && entityTypes.Contains(c.EntityType))
                .GroupBy(c => new { c.EntityType, c.EntityId })
                .Select(g => new { g.Key.EntityType, g.Key.EntityId, Revision = g.Max(x => x.ResultRevision) })
                .ToListAsync(ct);
        var head = headRows.ToDictionary(r => (r.EntityType, r.EntityId), r => r.Revision);

        // ---- feed position: the user's current cursor head --------------------------------------
        var feedPosition = await db.Set<SyncChangeRecord>()
            .Where(c => c.UserId == userId)
            .Select(c => (long?)c.ServerRevision).MaxAsync(ct) ?? 0;

        // ---- which of these operationIds has the server already APPLIED? ------------------------
        var opIds = operations.Select(o => o.OperationId ?? "")
            .Where(id => id.Length is > 0 and <= 80)
            .Distinct(StringComparer.Ordinal).ToArray();
        var appliedBefore = opIds.Length == 0
            ? new Dictionary<string, SyncOperation>()
            : await db.SyncOperations
                .Where(o => o.UserId == userId && opIds.Contains(o.OperationId)
                         && o.Outcome == SyncOutcomes.Applied)
                .ToDictionaryAsync(o => o.OperationId, ct);

        var results = new List<SyncOperationResult>(operations.Count);
        var logRows = new List<SyncOperation>();
        var feedRows = new List<SyncChangeRecord>();
        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;

        foreach (var op in operations)
        {
            var opId = op.OperationId ?? "";
            var entityType = op.EntityType ?? "";
            var entityId = op.EntityId ?? "";
            var kind = op.Kind ?? "";
            var key = (entityType, entityId);
            var entityHead = head.GetValueOrDefault(key, 0L);
            var addressable = opId.Length is > 0 and <= 80;
            var logId = addressable ? opId : UnaddressableOperationId;

            var invalid = ShapeProblem(op, opId, entityType, entityId, kind);
            if (invalid is not null)
            {
                Decide(logId, SyncOutcomes.Rejected, entityHead, invalid);
                continue;
            }

            if (appliedBefore.TryGetValue(logId, out var prior))
            {
                // Applied in an earlier batch: reporting the STORED revision is the only honest
                // answer, and it is what absorbs a retried offline batch without double-applying.
                results.Add(new SyncOperationResult(logId, SyncOutcomes.Duplicate, prior.ResultRevision));
                counts[SyncOutcomes.Duplicate] = counts.GetValueOrDefault(SyncOutcomes.Duplicate) + 1;
                continue;
            }

            if (!seenInBatch.Add(logId))
            {
                Decide(logId, SyncOutcomes.Duplicate, entityHead, SyncConflictReasons.DuplicateInBatch);
                continue;
            }

            if (kind == SyncKinds.Create && head.ContainsKey(key))
            {
                Decide(logId, SyncOutcomes.Conflict, entityHead, SyncConflictReasons.AlreadyExists);
                continue;
            }

            if (kind is SyncKinds.Update or SyncKinds.Delete && op.BaseRevision != entityHead)
            {
                // Stale base: the server's newer data stays exactly where it is. Both revisions are
                // named in the detail so the client can rebase without guessing.
                Decide(logId, SyncOutcomes.Conflict, entityHead, SyncConflictReasons.StaleBase);
                continue;
            }

            // Applied (upsert included: an unseen key simply starts at revision 1).
            var resultRevision = entityHead + 1;
            head[key] = resultRevision;
            feedPosition++;
            var payload = PayloadText(op);
            Decide(logId, SyncOutcomes.Applied, resultRevision, null);

            feedRows.Add(new SyncChangeRecord
            {
                UserId = userId,
                OperationId = logId,
                BatchKey = idempotencyKey,
                EntityType = Trunc(entityType, 64),
                EntityId = Trunc(entityId, 160),
                Kind = kind,
                BaseRevision = op.BaseRevision,
                ResultRevision = resultRevision,
                ServerRevision = feedPosition,
                Outcome = SyncOutcomes.Applied,
                PayloadJson = payload,
                ContentHash = SyncCanonicalJson.Sha256Hex(payload),
                CreatedAtUtc = now,
                SessionId = sessionId,
                CorrelationId = correlationId,
            });

            if (addressable)
            {
                // The durable "already done" marker for the next retry of this offline batch.
                logRows.Add(new SyncOperation
                {
                    UserId = userId,
                    OperationId = logId,
                    EntityType = Trunc(entityType, 64),
                    EntityId = Trunc(entityId, 160),
                    Kind = kind,
                    BaseRevision = op.BaseRevision,
                    ResultRevision = resultRevision,
                    Outcome = SyncOutcomes.Applied,
                    ReceivedAtUtc = now,
                    SessionId = sessionId,
                    PayloadJson = payload,
                });
            }

            // One place computes the verdict, its detail, the counters and the log row, so the
            // answer handed back and the state written can never disagree.
            void Decide(string id, string outcome, long revision, string? reason)
            {
                var conflict = reason is null
                    ? null
                    : new SyncConflictInfo(reason, op.BaseRevision, revision, feedPosition);
                results.Add(new SyncOperationResult(id, outcome, revision, conflict));
                counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
            }
        }

        // ---- a wholly stale batch applied nothing: answer 409 version_conflict ------------------
        var allConflict = operations.Count > 0
            && counts.GetValueOrDefault(SyncOutcomes.Conflict) == operations.Count;
        if (allConflict)
        {
            var first = results.First(r => r.Outcome == SyncOutcomes.Conflict);
            var detail = ConflictDetail(first, idempotencyKey);
            var problemJson = JsonSerializer.Serialize(
                new StoredProblem("version_conflict", detail), SyncCanonicalJson.Options);

            var conflictRecord = new SyncBatchRecord
            {
                UserId = userId,
                IdempotencyKey = idempotencyKey,
                RequestHash = requestHash,
                ResponseStatus = StatusCodesConflict,
                ResponseJson = problemJson,
                OperationCount = operations.Count,
                SessionId = sessionId,
                CorrelationId = correlationId,
                ReceivedAtUtc = now,
            };
            db.Set<SyncBatchRecord>().Add(conflictRecord);
            db.AuditEvents.Add(new AuditEvent
            {
                UserId = userId,
                Type = "sync_batch_conflict",
                Subject = idempotencyKey,
                CorrelationId = correlationId,
                // ids and counts only — payload content never enters the audit table (product law 5)
                MetadataJson = JsonSerializer.Serialize(counts, SyncCanonicalJson.Options),
                OccurredAtUtc = now,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsIndexCollision(ex))
            {
                db.ChangeTracker.Clear();
                return null;
            }

            foreach (var (outcome, count) in counts)
                SyncTelemetry.RecordOperations(outcome, count);
            logger.LogInformation(
                "sync batch rejected as version conflict: user={UserId} key={Key} operations={Count} " +
                "correlationId={CorrelationId}", userId, idempotencyKey, operations.Count, correlationId);

            return new SyncBatchApplyResult(SyncBatchStatus.VersionConflict,
                ResponseStatus: StatusCodesConflict, ProblemCode: "version_conflict",
                ProblemDetail: detail, Conflict: first);
        }

        // ---- persist: feed + log + the idempotency record, all in this transaction --------------
        var response = new SyncBatchResponse(results, now);
        var responseJson = JsonSerializer.Serialize(response, SyncCanonicalJson.Options);

        db.SyncOperations.AddRange(logRows);
        db.Set<SyncChangeRecord>().AddRange(feedRows);
        db.Set<SyncBatchRecord>().Add(new SyncBatchRecord
        {
            UserId = userId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            ResponseStatus = StatusCodesOk,
            ResponseJson = responseJson,
            OperationCount = operations.Count,
            SessionId = sessionId,
            CorrelationId = correlationId,
            ReceivedAtUtc = now,
        });
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = userId,
            Type = "sync_batch",
            Subject = idempotencyKey,
            CorrelationId = correlationId,
            // outcome counts only — payload content never enters the audit table (product law 5)
            MetadataJson = JsonSerializer.Serialize(counts, SyncCanonicalJson.Options),
            OccurredAtUtc = now,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsIndexCollision(ex))
        {
            foreach (var (outcome, count) in counts)
                SyncTelemetry.RecordOperations(outcome, count, collided: true);
            db.ChangeTracker.Clear();
            return null; // caller recomputes once against the now-committed state
        }

        foreach (var (outcome, count) in counts)
            SyncTelemetry.RecordOperations(outcome, count);

        logger.LogInformation(
            "sync batch processed: user={UserId} operations={Count} applied={Applied} " +
            "conflicts={Conflicts} rejected={Rejected} duplicates={Duplicates} headRevision={Revision} " +
            "correlationId={CorrelationId}",
            userId, operations.Count,
            counts.GetValueOrDefault(SyncOutcomes.Applied),
            counts.GetValueOrDefault(SyncOutcomes.Conflict),
            counts.GetValueOrDefault(SyncOutcomes.Rejected),
            counts.GetValueOrDefault(SyncOutcomes.Duplicate),
            feedPosition, correlationId);

        return new SyncBatchApplyResult(SyncBatchStatus.Applied, responseJson, StatusCodesOk);
    }

    /// <summary>
    /// The 409 detail: names the first offending operation, its entity, and BOTH revisions — the
    /// client's base and the server's current head — so the client can rebase without guessing.
    /// Ids and numbers only: no payload content, ever (product law 5).
    /// </summary>
    private static string ConflictDetail(SyncOperationResult conflict, string idempotencyKey)
    {
        var info = conflict.Conflict;
        return info is null
            ? $"Sync batch {idempotencyKey} conflicts with newer server data and applied nothing."
            : $"Operation '{conflict.OperationId}' ({info.Reason}): built on revision {info.BaseRevision} " +
              $"but the server holds revision {info.ServerEntityRevision}; nothing was applied. Pull " +
              $"/api/v1/sync/changes?since={info.ServerRevision} and resubmit with a new Idempotency-Key.";
    }

    // ===================================================================================== changes ==

    public async Task<SyncChangesResponse> GetChangesAsync(
        string userId, long since, int limit, CancellationToken ct)
    {
        // limit+1 rows: "hasMore" must come from data, never from arithmetic on a page cut at limit.
        var rows = await db.Set<SyncChangeRecord>()
            .Where(c => c.UserId == userId && c.ServerRevision > since)
            .OrderBy(c => c.ServerRevision)
            .Take(limit + 1)
            .ToListAsync(ct);
        var hasMore = rows.Count > limit;

        // latestRevision is the user's true feed head even when the requested window is empty, so a
        // client sitting at the tip still learns where the tip is.
        var latest = await db.Set<SyncChangeRecord>()
            .Where(c => c.UserId == userId)
            .Select(c => (long?)c.ServerRevision).MaxAsync(ct) ?? 0;

        return new SyncChangesResponse(
            Changes: rows.Take(limit).Select(View).ToArray(),
            LatestRevision: latest,
            HasMore: hasMore);
    }

    // ================================================================================ validation ===

    private string? ShapeProblem(
        SyncOperationInput op, string opId, string entityType, string entityId, string kind)
    {
        // A blank or over-long operationId cannot be keyed in the frozen log (80 chars), so it is
        // not addressable and never gets a durable verdict: reject it, echo the client's own text.
        if (opId.Length is 0 or > 80) return SyncConflictReasons.InvalidShape;
        if (entityType.Length is 0 or > 64) return SyncConflictReasons.InvalidShape;
        if (entityId.Length is 0 or > 160) return SyncConflictReasons.InvalidShape;
        if (!SyncKinds.IsKnown(kind)) return SyncConflictReasons.InvalidShape;
        if (PayloadText(op).Length > MaxPayloadChars) return SyncConflictReasons.PayloadTooLarge;
        return null;
    }

    private static string PayloadText(SyncOperationInput op)
        => op.Payload is { } payload ? SyncCanonicalJson.Canonicalize(payload) : "{}";

    private static string Trunc(string value, int max)
        => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Which DbUpdateExceptions mean "another request won the race" (retry once) versus "our code is
    /// wrong" (surface). Both providers name the constraint in the message; anything else is a real
    /// bug and must not be swallowed into a retry loop.
    /// </summary>
    private static bool IsIndexCollision(DbUpdateException ex)
    {
        var text = ex.InnerException?.Message ?? ex.Message;
        return text.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || text.Contains("duplicated key", StringComparison.OrdinalIgnoreCase);
    }

    private static SyncChangeView View(SyncChangeRecord c) => new(
        c.ServerRevision, c.ResultRevision, c.EntityType, c.EntityId, c.Kind,
        c.OperationId, c.ContentHash, c.CreatedAtUtc,
        JsonDocument.Parse(c.PayloadJson).RootElement.Clone());
}
