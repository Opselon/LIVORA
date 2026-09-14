using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Persistence;
using Livora.Server.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Livora.Server.Modules.Sync;

/// <summary>
/// PURPOSE: the cloud-sync foundation for Wave 4 — the two endpoints frozen in CONTRACT-P1 §5c:
///   <code>POST /api/v1/sync/batch</code>   idempotent, revision-checked, per-user ordered writes
///   <code>GET  /api/v1/sync/changes</code> the per-user change feed a second device pulls
///          + <code>GET /api/v1/sync/operations</code> — a paginated diagnostic view of the
///          operation log (additive to §5c, PagedResult in/out per §5; it exists so support can
///          answer "what did the server decide about operation X" without reading payload content).
/// OWNER: Agent 02 (lane w4-p1b-platform).
/// CONSUMES: <see cref="SyncBatchService"/> (the rules), <see cref="LivoraDbContext"/>, the
///           pre-wired auth seam (Platform/LivoraAuth.cs — §5b: policy + ctx.User.UserId(), never a
///           hand-written 401), <see cref="Problems"/> for every error.
/// PROVIDES: routes, service registration, the model contribution, and an honest <see cref="Report"/>.
/// INVARIANTS (the contract, in code):
///   - every route requires <see cref="Policies.SignedIn"/> and every query is filtered by the
///     CALLER's user id: per-user authorization is a WHERE clause, not a policy name (§5b IDOR gate).
///   - Idempotency-Key is mandatory on the batch. A replay of the same key + same canonical body
///     returns the ORIGINAL 200 bytes; same key + different body answers
///     409 idempotency_key_reuse_mismatch. The decision is enforced by a unique index, not a check.
///   - optimistic revision conflict: baseRevision != server entity head ⇒ outcome "conflict" with a
///     detail naming BOTH revisions. Newer server data is never silently overwritten.
///   - offline-safe ordering: applied operations take strictly increasing per-user revisions in
///     array order, and only applied operations enter the feed, so any device pulling
///     since=rev sees the same total order.
///   - batch/operation/payload size limits are read from Sync:* config and clamped down, never up
///     (Wave 4 §52); over-limit answers 400 validation_failed BEFORE any row is written.
///   - the response carries a correlation id (the platform middleware echoes X-Correlation-Id on
///     every response, including this one — asserted by the lane's tests).
///   - the module's Report() never claims Ok: sync state is only as good as the database probe, and
///     a real external sync provider (device-side) is out of scope here — see <see cref="Report"/>.
/// </summary>
public sealed class SyncModule : IFlivoraModule
{
    public const string ModuleKey = "sync";
    public const string RouteGroup = "sync";

    public string Key => ModuleKey;

    public void ConfigureServices(ModuleSeed seed)
    {
        // Persistence: our tables ride the shared model through the contribution seam (§4).
        // A failure here means the sync tables are NOT in the model — record it honestly rather
        // than mapping endpoints that would 500 on the first request.
        if (!SyncModelContribution.EnsureRegistered())
        {
            seed.Logger.LogError(
                "sync model contribution NOT registered: /api/v1/sync persistence is unavailable " +
                "on this host (the capability report says so, endpoints still map)");
        }

        var limits = SyncLimits.From(seed.Configuration);
        seed.Services.AddSingleton(limits);
        seed.Services.AddScoped<SyncBatchService>();
    }

    public void MapEndpoints(FlivoraEndpointContext ctx)
    {
        var group = ctx.MapVersionedGroup(RouteGroup);

        // ---------------------------------------------------------------- POST /sync/batch ----
        group.MapPost("/batch", async (
            HttpContext http,
            SyncBatchService service,
            SyncLimits limits,
            CancellationToken ct) =>
        {
            var log = http.RequestServices.GetRequiredService<ILogger<SyncModule>>();
            var correlationId = http.GetCorrelationId();
            var userId = http.User.UserId();
            var sessionId = http.User.SessionId();

            // §5b: the policy guarantees a token; a missing uid claim is a malformed token, and
            // unauthenticated is the honest code (the envelope comes from Problems, not a literal).
            if (string.IsNullOrWhiteSpace(userId))
                return Problems.Of(http, ProblemCodes.Unauthenticated, "A valid access token is required.");

            // Idempotency-Key: mandatory. No key means the client opted out of replay safety, and
            // silently accepting it would make every "at-most-once" claim in this file false.
            var idempotencyKey = http.Request.Headers[SyncLimits.IdempotencyKeyHeader].ToString();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    "The Idempotency-Key header is required for sync batches.");
            if (idempotencyKey.Length > SyncLimits.MaxIdempotencyKeyLength)
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    $"Idempotency-Key must be at most {SyncLimits.MaxIdempotencyKeyLength} characters.");
            if (!IsSafeKeyValue(idempotencyKey))
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    "Idempotency-Key may contain only letters, digits, '-' and '_'.");

            // The body is hashed over RAW text — reading it once keeps the reuse detector exactly
            // aligned with what the client actually sent.
            string rawBody;
            using (var reader = new StreamReader(http.Request.Body))
            {
                rawBody = await reader.ReadToEndAsync(ct);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawBody) ? "{}" : rawBody);
            }
            catch (JsonException)
            {
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    "Request body must be valid JSON of the shape {\"operations\":[...]}.");
            }

            SyncBatchRequest? request;
            try
            {
                request = doc.RootElement.Deserialize<SyncBatchRequest>(SyncCanonicalJson.Options);
            }
            catch (JsonException ex)
            {
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    $"Request body does not match the sync batch contract ({ex.GetType().Name}).");
            }
            finally
            {
                doc.Dispose();
            }

            var operations = request?.Operations;
            if (operations is null || operations.Count == 0)
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    "At least one operation is required.",
                    fieldErrors: new Dictionary<string, string[]>
                    {
                        ["operations"] = ["operations is required and must not be empty"],
                    });

            if (operations.Count > limits.MaxOperationsPerBatch)
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    $"A batch may carry at most {limits.MaxOperationsPerBatch} operations.");

            // Shape/size problems are per-operation verdicts inside a 200 (outcome=rejected) — the
            // only batch-level gates are the ones above, so one bad op cannot fail a whole device day.
            var requestHash = SyncCanonicalJson.Sha256Hex(HashSource(rawBody));

            var result = await service.ApplyAsync(
                userId, sessionId, correlationId, idempotencyKey, request!, requestHash, ct);

            switch (result.Status)
            {
                case SyncBatchStatus.KeyMismatch:
                    log.LogWarning(
                        "sync idempotency key reused with a different body: user={UserId} key={Key} " +
                        "correlationId={CorrelationId}", userId, idempotencyKey, correlationId);
                    return Problems.Of(http, ProblemCodes.IdempotencyKeyReuseMismatch,
                        "This Idempotency-Key was already used with a different request body. " +
                        "Retry with the original body, or mint a new key.");

                case SyncBatchStatus.VersionConflict:
                    // Optimistic conflict: nothing was applied — the detail names both revisions.
                    // The 409 is ALSO stored under the key (status+code+detail), so a replay of the
                    // same body re-answers the same conflict, and a replay of a different body is
                    // still the mismatch above. Sync never silently overwrites newer user data.
                    log.LogInformation(
                        "sync batch answered version_conflict: user={UserId} key={Key} " +
                        "correlationId={CorrelationId}", userId, idempotencyKey, correlationId);
                    return Problems.Of(http, ProblemCodes.VersionConflict,
                        result.ProblemDetail ?? "The batch conflicts with newer server data.");

                case SyncBatchStatus.Replay:
                    // The ORIGINAL answer, byte-for-byte and status-for-status. A replayed problem
                    // rebuilds the shared envelope (fresh correlation id: it identifies this
                    // response); a replayed 200 returns the stored bytes verbatim.
                    log.LogInformation(
                        "sync batch replayed from stored result: user={UserId} key={Key} " +
                        "correlationId={CorrelationId}", userId, idempotencyKey, correlationId);
                    if (result.ResponseStatus != StatusCodes.Status200OK)
                    {
                        var replayCode = result.ProblemCode == ProblemCodes.VersionConflict
                            ? ProblemCodes.VersionConflict
                            : ProblemCodes.Conflict;
                        return Problems.Of(http, replayCode,
                            result.ProblemDetail ?? "Replayed batch conflict; nothing was applied.",
                            status: result.ResponseStatus);
                    }
                    return Results.Content(result.ResponseJson ?? "{}",
                        "application/json; charset=utf-8", statusCode: StatusCodes.Status200OK);

                default:
                    log.LogInformation(
                        "sync batch processed: user={UserId} operations={Count} correlationId={CorrelationId}",
                        userId, operations.Count, correlationId);
                    return Results.Content(result.ResponseJson ?? "{}",
                        "application/json; charset=utf-8", statusCode: StatusCodes.Status200OK);
            }
        })
        .RequireAuthorization(Policies.SignedIn)
        .Produces<SyncBatchResponse>(StatusCodes.Status200OK)
        .Produces<ApiProblem>(StatusCodes.Status400BadRequest)
        .Produces<ApiProblem>(StatusCodes.Status409Conflict)
        .WithTags("sync");

        // --------------------------------------------------------------- GET /sync/changes ----
        group.MapGet("/changes", async (
            HttpContext http,
            SyncBatchService service,
            SyncLimits limits,
            CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (string.IsNullOrWhiteSpace(userId))
                return Problems.Of(http, ProblemCodes.Unauthenticated, "A valid access token is required.");

            // Parsed by hand: a missing/!legal cursor must not become a 500 or a silent full dump.
            var sinceRaw = http.Request.Query["since"].ToString();
            long since = 0;
            if (!string.IsNullOrWhiteSpace(sinceRaw))
            {
                if (!long.TryParse(sinceRaw, out since) || since < 0)
                    return Problems.Of(http, ProblemCodes.ValidationFailed,
                        "'since' must be a non-negative revision from a previous /sync/changes response.");
            }

            var limit = limits.MaxChangesLimit;
            var limitRaw = http.Request.Query["limit"].ToString();
            if (!string.IsNullOrWhiteSpace(limitRaw)
                && (!int.TryParse(limitRaw, out limit)
                    || limit < SyncLimits.MinChangesLimit
                    || limit > limits.MaxChangesLimit))
            {
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    $"'limit' must be between {SyncLimits.MinChangesLimit} and {limits.MaxChangesLimit}.");
            }

            // The service filters by userId in its WHERE clause: no other account's feed is
            // addressable, whatever the caller asks for (IDOR gate — §5b).
            var feed = await service.GetChangesAsync(userId, since, limit, ct);
            return Results.Ok(feed);
        })
        .RequireAuthorization(Policies.SignedIn)
        .Produces<SyncChangesResponse>(StatusCodes.Status200OK)
        .Produces<ApiProblem>(StatusCodes.Status400BadRequest)
        .WithTags("sync");

        // ------------------------------------------------------------- GET /sync/operations ----
        // Additive diagnostic surface (not in §5c): "did the server already see this operationId",
        // answered with decisions and ids only — payload content never leaves through here.
        // Query values are bound as primitives and folded into a PageRequest by hand: an illegal
        // offset/limit must clamp (PageRequest.SafeLimit), not become a framework 400 outside the
        // shared envelope.
        group.MapGet("/operations", async (
            HttpContext http,
            LivoraDbContext db,
            CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (string.IsNullOrWhiteSpace(userId))
                return Problems.Of(http, ProblemCodes.Unauthenticated, "A valid access token is required.");

            // Bound as raw strings on purpose: a non-nullable int parameter would make a MISSING
            // query value a framework 400 outside the shared envelope. Absent = defaults, illegal
            // = the platform clamp, exactly like PageRequest promises.
            var offset = TryReadInt(http.Request.Query["offset"], out var rawOffset) ? Math.Max(0, rawOffset) : 0;
            var limit = TryReadInt(http.Request.Query["limit"], out var rawLimit) ? rawLimit : PageRequest.DefaultLimit;
            var needle = http.Request.Query["q"].ToString().Trim();

            var page = new PageRequest
            {
                Offset = offset,
                Limit = limit,          // SafeLimit clamps a crazy value instead of erroring (§5)
                Query = needle.Length > 0 ? needle : null,
            };

            var query = db.SyncOperations.Where(o => o.UserId == userId);
            if (needle.Length > 0)
            {
                query = query.Where(o => o.OperationId.StartsWith(needle)
                                      || o.EntityId.StartsWith(needle)
                                      || o.EntityType.StartsWith(needle));
            }

            var take = page.SafeLimit;
            var total = await query.CountAsync(ct);
            var rows = await query
                .OrderByDescending(o => o.ReceivedAtUtc).ThenBy(o => o.OperationId)
                .Skip(page.Offset).Take(take)
                .ToListAsync(ct);

            var items = rows.Select(o => new SyncOperationView(
                o.OperationId, o.EntityType, o.EntityId, o.Kind, o.BaseRevision, o.ResultRevision,
                o.Outcome, o.ConflictDetail, o.ReceivedAtUtc)).ToArray();

            return Results.Ok(PagedResult<SyncOperationView>.Of(items, page, total));
        })
        .RequireAuthorization(Policies.SignedIn)
        .Produces<PagedResult<SyncOperationView>>(StatusCodes.Status200OK)
        .WithTags("sync");
    }

    /// <summary>
    /// TRUTHFUL REPORT — the sync lane has no external provider to probe; its dependency is the
    /// database, and this module does not run a probe per call (PlatformModule owns that surface).
    /// So the honest answer about readiness of the *persistence* is "depends on the model
    /// registration", and anything beyond that is reported as what it is:
    ///   - contribution registered  -> Degraded: "endpoints mapped; database health is reported by
    ///     /api/v1/platform/capabilities; device-side sync (the client engine, lane P1-D) is not
    ///     implemented here". Never Ok: no probe ran in this module, and product law 1 forbids
    ///     claiming a dependency is healthy from a config value or from "the routes exist".
    ///   - contribution missing     -> Degraded as well, with the reason, because the endpoints
    ///     would fail at the first query and the client must be able to see why.
    /// </summary>
    public ModuleHealth Report()
    {
        var wired = SyncModelContribution.Registered;
        return new ModuleHealth(
            ModuleKey,
            DependencyState.Degraded,
            wired
                ? "batch/changes endpoints mapped; persistence contribution registered; " +
                  "database health is probed by /api/v1/platform/capabilities, not claimed here"
                : "model contribution NOT registered: sync tables are absent from this host's " +
                  "EF model, so persistence calls will fail",
            new Dictionary<string, string>
            {
                ["endpoints"] = "mapped",
                ["persistence"] = wired ? "contribution_registered" : "missing",
                ["idempotency"] = "database_enforced",
                // The client-side sync engine is lane P1-D's job; saying "implemented" here would
                // be exactly the fake-green claim the contract forbids.
                ["device_sync_engine"] = "not_implemented_in_this_lane",
                ["schema_migration"] = "pending_lead_wave4p1schema",
            });
    }

    /// <summary>Query values bind as raw strings on purpose (a missing value must mean "default",
    /// not a framework 400 outside the shared envelope); this parses one leniently.</summary>
    private static bool TryReadInt(string? raw, out int value)
        => int.TryParse(raw, out value);

    /// <summary>Key charset gate: opaque ids only, so a header value can never carry a CR/LF, a
    /// quote or an emoji into an index key or a log line.</summary>
    private static bool IsSafeKeyValue(string value)
        => value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Canonical text for hashing. Re-serialising through the canonicaliser (rather than hashing the
    /// raw bytes) makes a whitespace/property-order retry of the SAME logical request hash equal,
    /// which is the whole point: a client retrying with a reformatted body must not be punished
    /// with a bogus 409.
    /// </summary>
    private static string HashSource(string rawBody)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawBody) ? "{}" : rawBody);
        return SyncCanonicalJson.Canonicalize(doc.RootElement);
    }
}

/// <summary>Defaults for values the frozen contract leaves open.</summary>
internal static class SyncContractDefaults
{
    /// <summary>Feed page size when the client does not ask (must stay inside §5c bounds).</summary>
    public const int ChangesLimit = 100;
}
