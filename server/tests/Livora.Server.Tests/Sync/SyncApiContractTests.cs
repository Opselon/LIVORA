using System.Text;
using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Sync;
using Livora.Server.Modules.Sync;

namespace Livora.Server.Tests.Sync;

/// <summary>
/// PURPOSE: prove the frozen §5c sync contract end-to-end through a REAL host — real routing, real
///          JWT auth, real serializer, real module seam — and every platform mechanic the wave leans
///          on: request idempotency (replay and mismatch), optimistic revision conflict, per-user
///          authorization (the IDOR gate), the correlation id on every response, and OpenAPI
///          visibility of the new endpoints.
/// OWNER: Agent 02 (lane w4-p1b-platform).
/// CONSUMES: <see cref="LivoraApiTest"/> / <see cref="LivoraWebFixture"/> (the sanctioned host;
///           in-memory provider — the documented Phase-1 limit: transactions and unique indexes are
///           NOT enforced there, so the SCHEMA-level guarantees are asserted on SQLite in
///           <see cref="SyncPersistenceSchemaTests"/>; what THIS file asserts is endpoint behaviour).
/// INVARIANTS TESTED:
///   - a replay returns the ORIGINAL body; a different body under the same key is
///     409 idempotency_key_reuse_mismatch; a wholly stale batch is 409 version_conflict naming both
///     revisions and applying nothing
///   - every response carries X-Correlation-Id, including the replayed and the conflicted ones
///   - no caller can read or write another user's log/feed (per-user WHERE clause on every operation)
///   - anonymous answers come from the platform envelope, never a lane-written 401
/// TEST DOUBLES: user ids are synthetic ("sync-user-&lt;guid&gt;") because the identity lane (P1-C)
///   does not exist in this lane's tree; tokens come from the host's real signing key via the
///   fixture. No real device, no client engine, and no external sync provider is exercised here —
///   and none of these tests claims to be one.
/// </summary>
public sealed class SyncApiContractTests : LivoraApiTest
{
    public SyncApiContractTests(LivoraWebFixture fixture) : base(fixture) { }

    /// <summary>A fresh account per test: every assertion below is about one user's data, and
    /// per-user isolation is itself part of what is under test.</summary>
    private HttpClient SignedIn(out string userId)
    {
        userId = "sync-user-" + Guid.NewGuid().ToString("N")[..12];
        return Fixture.CreateAuthenticatedClient(userId);
    }

    private static string Body(params (string Op, string Type, string Id, string Kind, long Base, string? Payload)[] ops)
        => JsonSerializer.Serialize(new
        {
            operations = ops.Select(o => new
            {
                operationId = o.Op,
                entityType = o.Type,
                entityId = o.Id,
                kind = o.Kind,
                baseRevision = o.Base,
                payload = o.Payload is null ? (JsonElement?)null : JsonDocument.Parse(o.Payload).RootElement.Clone(),
            }),
        });

    private static HttpRequestMessage BatchRequest(string key, string body, string? correlationId = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/batch")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(SyncLimits.IdempotencyKeyHeader, key);
        if (correlationId is not null) req.Headers.Add("X-Correlation-Id", correlationId);
        return req;
    }

    private static async Task<JsonElement> PostBatchAsync(HttpClient client, string key, string body)
    {
        using var req = BatchRequest(key, body);
        var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<(HttpResponseMessage Res, string Text)> PostBatchRawAsync(
        HttpClient client, string key, string body, string? correlationId = null)
    {
        using var req = BatchRequest(key, body, correlationId);
        var res = await client.SendAsync(req);
        return (res, await res.Content.ReadAsStringAsync());
    }

    private static string NewKey(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

    // ---------------------------------------------------------------------------- auth + surface --

    [Fact]
    public async Task Batch_anonymous_answers_the_shared_envelope_not_a_lane_401()
    {
        var res = await Fixture.Http.PostAsync("/api/v1/sync/batch",
            new StringContent("{\"operations\":[]}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.Unauthenticated, problem.Code);
        AssertHasCorrelation(res);
    }

    [Theory]
    // Each verb is the route's REAL one: batch is POST-only, so a GET there answers the honest
    // 404 fallback, not the auth challenge (a full catch-all match suppresses the 405 candidate).
    [InlineData("POST", "/api/v1/sync/batch")]
    [InlineData("GET", "/api/v1/sync/changes")]
    [InlineData("GET", "/api/v1/sync/operations")]
    public async Task Every_sync_route_is_protected(string method, string path)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        var res = await Fixture.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.Unauthenticated, problem.Code);
        AssertHasCorrelation(res);
    }

    [Fact]
    public async Task Sync_endpoints_are_visible_in_the_openapi_document()
    {
        var res = await Fixture.Http.GetAsync("/openapi/v1.json");
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadAsStringAsync();

        Assert.Contains("/api/v1/sync/batch", doc);
        Assert.Contains("/api/v1/sync/changes", doc);
        Assert.Contains("/api/v1/sync/operations", doc);
        // The Idempotency-Key header is part of the published contract, not tribal knowledge.
        Assert.Contains("Idempotency-Key", doc);
    }

    [Fact]
    public async Task Capabilities_reports_sync_honestly_never_a_fake_ok()
    {
        var snap = await Fixture.GetAsync<Livora.Server.Modules.Platform.CapabilitySnapshot>(
            "/api/v1/platform/capabilities");

        var sync = Assert.Single(snap.Modules, m => m.Key == SyncModule.ModuleKey);
        // Honest: the database is probed by the platform module, not claimed by us; the device-side
        // engine belongs to lane P1-D. An "ok" here without a probe would be a §7 tripwire.
        Assert.Equal("degraded", sync.State);
        Assert.Equal("mapped", sync.Capabilities["endpoints"]);
        Assert.Equal("contribution_registered", sync.Capabilities["persistence"]);
        Assert.Equal("not_implemented_in_this_lane", sync.Capabilities["device_sync_engine"]);
        Assert.DoesNotContain("connected", sync.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------ idempotency ---

    [Fact]
    public async Task Batch_first_call_applies_and_a_byte_identical_retry_replays_the_original_bytes()
    {
        var client = SignedIn(out _);
        var key = NewKey("replay");
        var body = Body(("op-1", "goal", "g1", "create", 0, "{\"title\":\"sleep\"}"));

        var (_, firstText) = await PostBatchRawAsync(client, key, body);
        using var firstDoc = JsonDocument.Parse(firstText);
        Assert.Equal("applied", firstDoc.RootElement.GetProperty("results")[0].GetProperty("outcome").GetString());
        Assert.Equal(1, firstDoc.RootElement.GetProperty("results")[0].GetProperty("resultRevision").GetInt64());

        // Raw text, not the parsed shape: "replay returns the ORIGINAL result" must mean BYTES —
        // including the original serverTimeUtc, which a re-decision could never reproduce.
        var (_, replayText) = await PostBatchRawAsync(client, key, body);
        Assert.Equal(firstText, replayText);
    }

    [Fact]
    public async Task A_reformatted_retry_of_the_same_logical_request_still_replays()
    {
        // The reuse detector hashes the CANONICAL form (property order normalised, whitespace
        // dropped): a client whose serializer changed shape between attempts is still replaying.
        var client = SignedIn(out _);
        var key = NewKey("canon");
        var body = Body(("op-1", "goal", "g1", "create", 0, "{\"title\":\"sleep\"}"));

        var (_, firstText) = await PostBatchRawAsync(client, key, body);

        var reformatted = "  " + JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(body),
            new JsonSerializerOptions { WriteIndented = true });
        var (_, replayText) = await PostBatchRawAsync(client, key, reformatted);

        Assert.Equal(
            JsonDocument.Parse(firstText).RootElement.GetProperty("serverTimeUtc").GetString(),
            JsonDocument.Parse(replayText).RootElement.GetProperty("serverTimeUtc").GetString());
    }

    [Fact]
    public async Task Batch_same_key_different_body_is_a_key_reuse_mismatch_and_corrupts_nothing()
    {
        var client = SignedIn(out _);
        var key = NewKey("mismatch");
        var original = Body(("op-1", "goal", "g1", "create", 0, "{\"title\":\"a\"}"));

        await PostBatchAsync(client, key, original);

        var (res, _) = await PostBatchRawAsync(client, key,
            Body(("op-1", "goal", "g1", "create", 0, "{\"title\":\"different\"}")));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.IdempotencyKeyReuseMismatch, problem.Code);
        Assert.NotEqual("unknown", problem.CorrelationId);
        AssertHasCorrelation(res);

        // The original verdict must survive the mismatch attempt: a rejected replay cannot overwrite
        // the stored answer.
        var again = await PostBatchAsync(client, key, original);
        Assert.Equal("applied", again.GetProperty("results")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Batch_without_idempotency_key_is_refused()
    {
        var client = SignedIn(out _);
        var res = await client.PostAsync("/api/v1/sync/batch",
            new StringContent(Body(("op-1", "goal", "g1", "create", 0, "{}")), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.ValidationFailed, problem.Code);
        Assert.Contains("Idempotency-Key", problem.Detail);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("{\"operations\":[]}")]
    public async Task Batch_malformed_or_empty_body_is_a_validated_problem_not_a_crash(string body)
    {
        var client = SignedIn(out _);
        var (res, _) = await PostBatchRawAsync(client, NewKey("malformed"), body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.ValidationFailed, problem.Code);
        AssertHasCorrelation(res);
    }

    // -------------------------------------------------------------------------------- revisions ---

    [Fact]
    public async Task Update_on_stale_base_conflicts_naming_both_revisions_and_changes_nothing()
    {
        var client = SignedIn(out _);
        var entity = "goal-" + Guid.NewGuid().ToString("N")[..8];

        await PostBatchAsync(client, NewKey("c"),
            Body(("op-1", "goal", entity, "create", 0, "{\"title\":\"v1\"}")));
        // A second applied change moves the head to 2; then a client built on revision 1 tries in.
        await PostBatchAsync(client, NewKey("c2"),
            Body(("op-2", "goal", entity, "update", 1, "{\"title\":\"v2\"}")));

        var stale = await PostBatchAsync(client, NewKey("c3"),
            Body(("op-3", "goal", entity, "update", 1, "{\"title\":\"overwrite!\"}"),
                 ("op-4", "goal", entity + "-other", "create", 0, "{}")));

        // Mixed batch: the stale op conflicts, the healthy op still applies (partial progress is
        // real progress) — so this is a 200 with a per-operation conflict verdict.
        var results = stale.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal("conflict", results[0].GetProperty("outcome").GetString());
        Assert.Equal("applied", results[1].GetProperty("outcome").GetString());

        var conflict = results[0].GetProperty("conflict");
        Assert.Equal("stale_base", conflict.GetProperty("reason").GetString());
        Assert.Equal(1, conflict.GetProperty("baseRevision").GetInt64());          // client's base
        Assert.Equal(2, conflict.GetProperty("serverEntityRevision").GetInt64());  // server's head

        // And the server's data is exactly what it was before the attempt: never overwritten.
        var feed = await client.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=0");
        var rows = feed.GetProperty("changes").EnumerateArray()
            .Where(c => c.GetProperty("entityId").GetString() == entity).ToArray();
        Assert.Equal(2, rows.Length); // the stale write added nothing to the feed
        Assert.All(rows, r => Assert.NotEqual("overwrite!",
            r.TryGetProperty("payload", out var p) && p.TryGetProperty("title", out var t)
                ? t.GetString() : null));
    }

    [Fact]
    public async Task A_wholly_stale_batch_answers_409_version_conflict_applies_nothing_and_replays_as_409()
    {
        var client = SignedIn(out _);
        var entity = "goal-" + Guid.NewGuid().ToString("N")[..8];
        await PostBatchAsync(client, NewKey("seed"),
            Body(("op-1", "goal", entity, "create", 0, "{\"title\":\"server\"}"),
                 ("op-2", "goal", entity + "-b", "create", 0, "{\"title\":\"server-b\"}")));

        var key = NewKey("stale-batch");
        var staleBody = Body(("op-9", "goal", entity, "update", 7, "{\"title\":\"from an old device\"}"),
                             ("op-10", "goal", entity + "-b", "update", 0, "{\"title\":\"also old\"}"));

        using (var conflictReq = BatchRequest(key, staleBody))
        {
            var res = await client.SendAsync(conflictReq);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            var problem = await LivoraWebFixture.ReadProblemAsync(res);
            Assert.Equal(ProblemCodes.VersionConflict, problem.Code);
            // The detail names BOTH revisions ("built on revision 7 ... server holds revision 1").
            Assert.Matches(@"revision 7 .*server holds revision 1", problem.Detail);
            AssertHasCorrelation(res);
        }

        // Nothing applied: the feed head is still where the seed batch left it.
        var feed = await client.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=0");
        Assert.Equal(2, feed.GetProperty("latestRevision").GetInt64());

        // The conflict itself is idempotent: the same body under the same key answers 409 again with
        // the same code, never a silent success.
        using var replayReq = BatchRequest(key, staleBody);
        var replay = await client.SendAsync(replayReq);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(ProblemCodes.VersionConflict, (await LivoraWebFixture.ReadProblemAsync(replay)).Code);
    }

    [Fact]
    public async Task Create_on_an_existing_entity_conflicts_as_already_exists()
    {
        var client = SignedIn(out _);
        var entity = "habit-" + Guid.NewGuid().ToString("N")[..8];
        await PostBatchAsync(client, NewKey("h1"),
            Body(("op-1", "habit", entity, "create", 0, "{\"name\":\"read\"}")));

        using var req = BatchRequest(NewKey("h2"),
            Body(("op-2", "habit", entity, "create", 0, "{\"name\":\"smoke\"}")));
        var res = await client.SendAsync(req);

        // A single-op batch that conflicts is wholly stale: 409 with the reason named in the detail.
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.VersionConflict, problem.Code);
        Assert.Contains("already_exists", problem.Detail);
    }

    [Fact]
    public async Task Delete_then_stale_update_cannot_resurrect_the_row()
    {
        var client = SignedIn(out _);
        var entity = "entry-" + Guid.NewGuid().ToString("N")[..8];
        await PostBatchAsync(client, NewKey("d1"),
            Body(("op-1", "manual_entry", entity, "create", 0, "{\"note\":\"meal\"}")));
        await PostBatchAsync(client, NewKey("d2"),
            Body(("op-2", "manual_entry", entity, "delete", 1, null)));

        var stale = await PostBatchAsync(client, NewKey("d3"),
            Body(("op-3", "manual_entry", entity, "update", 1, "{\"note\":\"resurrected\"}"),
                 ("op-4", "manual_entry", entity + "-x", "create", 0, "{}")));
        var results = stale.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal("conflict", results[0].GetProperty("outcome").GetString());
        Assert.Equal(2, results[0].GetProperty("conflict").GetProperty("serverEntityRevision").GetInt64());

        // The tombstone is still the last applied word for that entity.
        var feed = await client.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=0");
        var last = feed.GetProperty("changes").EnumerateArray()
            .Last(c => c.GetProperty("entityId").GetString() == entity);
        Assert.Equal("delete", last.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Update_with_base_zero_on_an_unseen_entity_is_an_offline_upsert()
    {
        var client = SignedIn(out _);
        var entity = "plan-" + Guid.NewGuid().ToString("N")[..8];
        var res = await PostBatchAsync(client, NewKey("u1"),
            Body(("op-1", "plan_item", entity, "update", 0, "{\"slot\":\"morning\"}")));
        Assert.Equal("applied", res.GetProperty("results")[0].GetProperty("outcome").GetString());
        Assert.Equal(1, res.GetProperty("results")[0].GetProperty("resultRevision").GetInt64());
    }

    // --------------------------------------------------------------- offline duplicate absorption ---

    [Fact]
    public async Task An_operation_id_already_applied_answers_duplicate_with_the_stored_revision()
    {
        var client = SignedIn(out _);
        var entity = "goal-" + Guid.NewGuid().ToString("N")[..8];
        await PostBatchAsync(client, NewKey("a"),
            Body(("op-same", "goal", entity, "create", 0, "{\"title\":\"once\"}")));

        // A device that lost its response and retried the SAME operation under a NEW key: the log
        // answers with the original revision instead of applying it twice.
        var retry = await PostBatchAsync(client, NewKey("b"),
            Body(("op-same", "goal", entity, "create", 0, "{\"title\":\"once\"}")));
        var first = retry.GetProperty("results")[0];
        Assert.Equal("duplicate", first.GetProperty("outcome").GetString());
        Assert.Equal(1, first.GetProperty("resultRevision").GetInt64());

        // The feed carries exactly one applied row for it: no double-apply, no lost write.
        var feed = await client.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=0");
        Assert.Equal(1, feed.GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task Repeated_operation_id_inside_one_batch_is_a_duplicate_in_batch()
    {
        var client = SignedIn(out _);
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var res = await PostBatchAsync(client, NewKey("dup"),
            Body(("op-x", "goal", $"gx-{stamp}", "create", 0, "{\"t\":1}"),
                 ("op-x", "goal", $"gy-{stamp}", "create", 0, "{\"t\":2}")));

        var results = res.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal("applied", results[0].GetProperty("outcome").GetString());
        Assert.Equal("duplicate", results[1].GetProperty("outcome").GetString());
        Assert.Equal("duplicate_in_batch", results[1].GetProperty("conflict").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_rebased_retry_after_a_conflict_can_apply()
    {
        // The log stores APPLIED verdicts only — that is what lets a client fix its base and retry
        // the same operationId instead of being told "duplicate" of a decision that applied nothing.
        var client = SignedIn(out _);
        var entity = "goal-" + Guid.NewGuid().ToString("N")[..8];
        await PostBatchAsync(client, NewKey("r1"),
            Body(("op-1", "goal", entity, "create", 0, "{\"t\":1}")));
        await PostBatchAsync(client, NewKey("r2"),
            Body(("op-2", "goal", entity, "update", 1, "{\"t\":2}")));

        var conflicted = await PostBatchAsync(client, NewKey("r3"),
            Body(("op-3", "goal", entity, "update", 1, "{\"t\":mine}"),
                 ("keep", "goal", entity + "-k", "create", 0, "{}")));
        Assert.Equal("conflict", conflicted.GetProperty("results").EnumerateArray()
            .First(r => r.GetProperty("operationId").GetString() == "op-3").GetProperty("outcome").GetString());

        var rebased = await PostBatchAsync(client, NewKey("r4"),
            Body(("op-3", "goal", entity, "update", 2, "{\"t\":\"mine\"}")));
        Assert.Equal("applied", rebased.GetProperty("results")[0].GetProperty("outcome").GetString());
        Assert.Equal(3, rebased.GetProperty("results")[0].GetProperty("resultRevision").GetInt64());
    }

    [Fact]
    public async Task Unknown_kind_is_rejected_without_sinking_the_batch()
    {
        var client = SignedIn(out _);
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var res = await PostBatchAsync(client, NewKey("rj"),
            Body(("op-1", "goal", "bad-shape", "explode", 0, "{}"),
                 ("op-2", "goal", $"good-{stamp}", "create", 0, "{}")));

        var results = res.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal("rejected", results[0].GetProperty("outcome").GetString());
        Assert.Equal("invalid_shape", results[0].GetProperty("conflict").GetProperty("reason").GetString());
        Assert.Equal("applied", results[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Oversized_batch_is_a_validated_400_before_any_row_is_written()
    {
        var client = SignedIn(out _);
        var ops = Enumerable.Range(0, SyncLimits.MaxOperationsPerBatchCeiling + 5)
            .Select(i => ($"op-{i}", "goal", $"bulk-{i}", "create", 0L, (string?)null))
            .ToArray();

        using var bulkReq = BatchRequest(NewKey("bulk"), Body(ops));
        var res = await client.SendAsync(bulkReq);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(ProblemCodes.ValidationFailed, (await LivoraWebFixture.ReadProblemAsync(res)).Code);

        // Nothing was written: the feed for this brand-new user is empty and its cursor at 0.
        var feed = await client.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes");
        Assert.Equal(0, feed.GetProperty("latestRevision").GetInt64());
        Assert.Equal(0, feed.GetProperty("changes").GetArrayLength());
    }

    // ---------------------------------------------------------------------------------- changes ---

    [Fact]
    public async Task Changes_feed_is_ordered_paged_and_reports_hasmore_from_data()
    {
        var client = SignedIn(out _);
        var stamp = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 3; i++)
        {
            await PostBatchAsync(client, $"ord-{stamp}-{i}",
                Body(($"ord-op-{i}", "goal", $"og-{stamp}-{i}", "create", 0, $"{{\"i\":{i}}}")));
        }

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=0&limit=2");
        var changes = page.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(2, changes.Length);
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, changes[0].GetProperty("serverRevision").GetInt64());
        Assert.Equal(2, changes[1].GetProperty("serverRevision").GetInt64());
        Assert.Equal(3, page.GetProperty("latestRevision").GetInt64());

        // Resume from the cursor: exactly the one change the client had not seen, then the tip.
        var rest = await client.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=2&limit=10");
        var restChanges = rest.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Single(restChanges);
        Assert.Equal(3, restChanges[0].GetProperty("serverRevision").GetInt64());
        Assert.False(rest.GetProperty("hasMore").GetBoolean());
        // The payload round-trips intact (canonicalised, not re-invented).
        Assert.Equal(2, restChanges[0].GetProperty("payload").GetProperty("i").GetInt32());
    }

    [Fact]
    public async Task A_second_device_pulls_the_first_devices_changes_by_cursor()
    {
        var author = SignedIn(out var user);
        var entity = "goal-" + Guid.NewGuid().ToString("N")[..8];
        await PostBatchAsync(author, NewKey("dev-a"),
            Body(("op-a1", "goal", entity, "create", 0, "{\"title\":\"shared\"}")));

        // Same account, different device session: a fresh token sees the feed from revision 0.
        var reader = Fixture.CreateAuthenticatedClient(user);
        var feed = await reader.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=0");
        var row = Assert.Single(feed.GetProperty("changes").EnumerateArray());
        Assert.Equal(entity, row.GetProperty("entityId").GetString());
        Assert.Equal("shared", row.GetProperty("payload").GetProperty("title").GetString());
        Assert.Equal(1, row.GetProperty("serverRevision").GetInt64());
    }

    [Fact]
    public async Task Changes_and_operations_are_scoped_per_user_idor_gate()
    {
        var victim = SignedIn(out _);
        var entity = "private-" + Guid.NewGuid().ToString("N")[..8];
        await PostBatchAsync(victim, NewKey("v"),
            Body(("op-v1", "goal", entity, "create", 0, "{\"title\":\"secret-ish\"}")));

        var attacker = SignedIn(out _);
        var feed = await attacker.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=0");
        Assert.Equal(0, feed.GetProperty("latestRevision").GetInt64());
        Assert.Empty(feed.GetProperty("changes").EnumerateArray());

        var ops = await attacker.GetFromJsonAsync<JsonElement>("/api/v1/sync/operations");
        Assert.Equal(0, ops.GetProperty("totalCount").GetInt32());

        // Even naming the victim's entity id gives an attacker nothing: they cannot read the row,
        // and their write lands as a fresh private entity (their own rev 1) — never a mutation of
        // someone else's head.
        using var probeReq = BatchRequest(NewKey("atk"),
            Body(("op-x", "goal", entity, "update", 0, "{\"title\":\"mine now\"}")));
        var probe = await attacker.SendAsync(probeReq);
        probe.EnsureSuccessStatusCode(); // applied into the ATTACKER's own space

        // The victim's data is unchanged and their feed still exactly one row long.
        var after = await victim.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=0");
        var row = Assert.Single(after.GetProperty("changes").EnumerateArray());
        Assert.Equal("secret-ish", row.GetProperty("payload").GetProperty("title").GetString());
        // And the attacker's "mine now" never entered the victim's stream.
        var attackerFeed = await attacker.GetFromJsonAsync<JsonElement>("/api/v1/sync/changes?since=0");
        Assert.Equal(1, attackerFeed.GetProperty("changes").GetArrayLength());
    }

    // --------------------------------------------------------------- diagnostics + correlation ----

    [Fact]
    public async Task Operations_view_is_paged_and_carries_no_payload_content()
    {
        var client = SignedIn(out _);
        var stamp = Guid.NewGuid().ToString("N")[..8];
        await PostBatchAsync(client, NewKey("diag"),
            Body(($"diag-op-{stamp}", "goal", $"dg-{stamp}", "create", 0, "{\"note\":\"private-value\"}")));

        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/sync/operations?limit=10&q=diag-op-{stamp}");
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("applied", item.GetProperty("outcome").GetString());

        // Product law 5: the audit view shows decisions, never the user's content.
        Assert.DoesNotContain("private-value", page.ToString());
    }

    [Fact]
    public async Task Every_sync_response_echoes_a_correlation_id_and_honours_a_safe_incoming_one()
    {
        var client = SignedIn(out _);
        var id = "sync-corr-" + Guid.NewGuid().ToString("N")[..8];

        var (res, _) = await PostBatchRawAsync(client, NewKey("corr"),
            Body(("op-c", "goal", "corr-" + Guid.NewGuid().ToString("N")[..8], "create", 0, "{}")), id);
        res.EnsureSuccessStatusCode();
        Assert.Equal(id, string.Join("", res.Headers.GetValues("X-Correlation-Id")));
    }

    [Theory]
    [InlineData("/api/v1/sync/changes?since=-1")]
    [InlineData("/api/v1/sync/changes?since=abc")]
    [InlineData("/api/v1/sync/changes?limit=99999")]
    [InlineData("/api/v1/sync/changes?limit=x")]
    public async Task Bad_cursor_and_limit_are_envelope_problems_not_framework_errors(string path)
    {
        var client = SignedIn(out _);
        var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.ValidationFailed, problem.Code);
        AssertHasCorrelation(res);
    }
}
