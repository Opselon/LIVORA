using LIVORA.Application.Abstractions;
using LIVORA.Application.Cloud;
using LIVORA.Application.Sync;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Cloud;
using LIVORA.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Tests.Tests.Wave4ClientSeam;

/// <summary>
/// The offline→cloud bridge, driven through the REAL Wave 3c <see cref="SyncQueue"/> (journal, meta
/// sidecar, FIFO cap and state machine included) with a scripted wire behind it. These are the tests
/// that make "we never fake a sync" checkable: every one of them asserts what the queue's own
/// bookkeeping says after a cycle, not what a report claims.
/// </summary>
public class CloudSyncTransportTests
{
    private static CloudSyncTransport Transport(
        LivoraApiPort port, ICloudApiOptions options, Wave4SeamHarness.ScriptedAuth? auth,
        Wave4SeamHarness.MapPayloadSource? payloads) =>
        new(port, options, auth, payloads, NullLogger<CloudSyncTransport>.Instance);

    // ============================ not configured = no push ======================================

    [Theory]
    [InlineData(false, true, true, "Cloud.Sync.Reason.NotConfigured")]    // no base URL
    [InlineData(true, false, true, "Cloud.Sync.Reason.NoSession")]        // no session
    [InlineData(true, true, false, "Cloud.Sync.Reason.NoPayloadSource")]  // no readable payloads
    public async Task MissingAnyPrecondition_RefusesThePush_AndEveryEntryStaysPending(
        bool configured, bool hasSession, bool hasPayloads, string expectedReasonKey)
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-precondition");
        var h = new Wave4SeamHarness.ScriptedHandler();
        var options = Wave4SeamHarness.Options(dir, configured);
        var auth = new Wave4SeamHarness.ScriptedAuth(hasSession ? "at-1" : null);
        var payloads = new Wave4SeamHarness.MapPayloadSource { Available = hasPayloads };
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "g1", meta: meta);

        Assert.False(transport.IsConfigured);
        Assert.Equal(expectedReasonKey, transport.ReasonKey);

        var drain = await queue.DrainAsync(transport);
        Assert.False(drain.TransportConfigured);          // the queue's own honest verdict
        Assert.True(drain.IsHonestNoOp);
        Assert.Equal(0, drain.Synced);
        Assert.Equal(1, await queue.PendingCountAsync());
        Assert.Equal(0, await queue.SyncedCountAsync());
        Assert.Equal(SyncState.Pending, (await meta.GetAsync("goal", "g1")).SyncState);
        Assert.Equal(0, h.CallCount());                   // and nothing left the device
    }

    [Fact]
    public async Task TheBridgeDefersToTheWave3cNoopTransport_WhenTheCloudIsUnusable()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-noop");
        var options = Wave4SeamHarness.Options(dir, configured: false);
        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "g1", meta: meta);
        var h = new Wave4SeamHarness.ScriptedHandler();
        using var port = new LivoraApiPort(options, h, null, NullLogger<LivoraApiPort>.Instance);
        var bridge = new CloudSyncBridge(port, options, queue, Wave4SeamHarness.StateStore(dir));

        Assert.False(bridge.IsConfigured);
        Assert.Equal(NoopSyncTransport.Label, bridge.GatewayLabel);   // same tag the Wave 3c build shipped
        var report = await queue.DrainAsync(bridge);
        Assert.False(report.TransportConfigured);
        Assert.Equal("Sync.Reason.TransportNotConfigured", report.ReasonKey);   // unchanged Wave 3c wording
        Assert.Equal(1, await queue.PendingCountAsync());
        Assert.Equal(0, h.CallCount());
    }

    // ============================ a real push ====================================================

    [Fact]
    public async Task AppliedResults_MoveTheQueueToSynced_WithTheFrozenBatchBody()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-applied");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1","title":"پیاده‌روی"}""");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var h = new Wave4SeamHarness.ScriptedHandler();
        h.On("POST", "/api/v1/sync/batch", Wave4SeamHarness.BatchEcho("applied"));
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        var envelope = Wave4SeamHarness.Enqueue(queue, "goal", "g1", version: 3, meta: meta);

        Assert.True(transport.IsConfigured);
        var drain = await queue.DrainAsync(transport);

        Assert.True(drain.TransportConfigured);
        Assert.Equal(1, drain.Synced);
        Assert.Equal(0, await queue.PendingCountAsync());
        Assert.Equal(1, await queue.SyncedCountAsync());
        Assert.Equal(SyncState.Synced, (await meta.GetAsync("goal", "g1")).SyncState);   // the UI reads the meta

        // Wire body: exactly §5c's operation shape, with the real payload bytes from the source.
        var body = h.Last(pathContains: "sync/batch")!.Body!;
        Assert.Contains("{\"operationId\":", body, StringComparison.Ordinal);
        Assert.Contains("\"entityType\":\"goal\"", body, StringComparison.Ordinal);
        Assert.Contains("\"entityId\":\"g1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"update\"", body, StringComparison.Ordinal);
        Assert.Contains("\"baseRevision\":3", body, StringComparison.Ordinal);
        Assert.Contains("پیاده‌روی", body, StringComparison.Ordinal);   // Persian content survives UTF-8
        Assert.Contains("Bearer at-1", h.Last(pathContains: "sync/batch")!.Headers["Authorization"], StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(h.Last(pathContains: "sync/batch")!.Headers["Idempotency-Key"]));
        _ = envelope;
    }

    [Fact]
    public async Task AConflictOutcome_MovesTheBatchToConflict_AndNothingResolvesItself()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-conflict");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("POST", "/api/v1/sync/batch", Wave4SeamHarness.BatchEcho("conflict",
                conflictJson: """{"kind":"version_conflict","remoteRevision":9}"""));
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "g1", meta: meta);
        var drain = await queue.DrainAsync(transport);

        Assert.Equal(1, drain.Conflicted);
        Assert.Equal(0, drain.Synced);
        Assert.Equal(1, await queue.ConflictCountAsync());
        Assert.Equal(0, await queue.SyncedCountAsync());
        Assert.Equal(SyncState.Conflict, (await meta.GetAsync("goal", "g1")).SyncState);

        // A second drain must NOT re-push it: divergence waits for a human, not a retry loop.
        var again = await queue.DrainAsync(transport);
        Assert.Equal(0, again.Pushed);
        Assert.Equal(1, h.CallCount("POST", "sync/batch"));
        Assert.Equal(1, await queue.ConflictCountAsync());

        // Only an explicit user decision moves it.
        Assert.True(await queue.ResolveConflictAsync(EntityMeta.KeyOf("goal", "g1"), keepLocal: true));
        Assert.Equal(1, await queue.PendingCountAsync());
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("already-synced-but-server-invented-a-word")]
    public async Task AnyOutcomeThatIsNotAppliedOrDuplicate_KeepsTheEntriesPending(string outcome)
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-" + outcome.Length);
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("POST", "/api/v1/sync/batch", Wave4SeamHarness.BatchEcho(outcome));
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "g1", meta: meta);
        var drain = await queue.DrainAsync(transport);

        Assert.Equal(0, drain.Synced);
        Assert.Equal(1, await queue.PendingCountAsync());      // refused, not renamed to "synced"
        Assert.Equal(SyncState.Pending, (await meta.GetAsync("goal", "g1")).SyncState);
    }

    [Fact]
    public async Task ADuplicateOutcome_CountsAsTheServerHavingTheContent()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-duplicate");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("POST", "/api/v1/sync/batch", Wave4SeamHarness.BatchEcho("duplicate", revision: 4));
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "g1", meta: meta);
        await queue.DrainAsync(transport);
        Assert.Equal(SyncState.Synced, (await meta.GetAsync("goal", "g1")).SyncState);   // the server HAS it
    }

    [Fact]
    public async Task AShortResultListIsNotTreatedAsConfirmationOfTheWholeBatch()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-short");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        payloads.Put("goal", "g2", """{"id":"g2"}""");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        // Answer ONLY the first submitted operation: one row for a two-operation batch.
        var h = new Wave4SeamHarness.ScriptedHandler().On("POST", "/api/v1/sync/batch", req =>
        {
            var text = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var first = doc.RootElement.GetProperty("operations")[0].GetProperty("operationId").GetString();
            var row = "{\"operationId\":\"" + first + "\",\"outcome\":\"applied\",\"resultRevision\":8,\"conflict\":null}";
            return Wave4SeamHarness.Response(200, "{\"results\":[" + row + "],\"serverTimeUtc\":\"2026-09-14T08:00:00+00:00\"}");
        });
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "g1", meta: meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "g2");

        var drain = await queue.DrainAsync(transport);
        Assert.Equal(0, drain.Synced);
        Assert.Equal(CloudSyncTransport.CategoryShortResults, drain.ErrorCategory);
        Assert.Equal(2, await queue.PendingCountAsync());
        Assert.Equal(0, await queue.SyncedCountAsync());
    }

    [Fact]
    public async Task AFailedRequest_KeepsTheBatchPending_AndClassifiesByServerCode()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-fail");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Problem("POST", "/api/v1/sync/batch", 409, LivoraApiCodes.IdempotencyKeyReuseMismatch);
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "g1", meta: meta);
        var drain = await queue.DrainAsync(transport);

        Assert.Equal(1, drain.FailedBatches);
        Assert.Contains(LivoraApiCodes.IdempotencyKeyReuseMismatch, drain.ErrorCategory, StringComparison.Ordinal);
        Assert.Equal(1, await queue.PendingCountAsync());
        Assert.Equal(SyncState.Pending, (await meta.GetAsync("goal", "g1")).SyncState);
    }

    // ============================ payloads =======================================================

    [Fact]
    public async Task AnEntityThatCannotBeRead_IsNeverUploadedAsAnEmptyObject()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-missing");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "present", """{"id":"present"}""");
        // "gone" intentionally NOT in the source: the queue row exists, the entity does not.
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("POST", "/api/v1/sync/batch", Wave4SeamHarness.BatchEcho("applied"));
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "present");
        Wave4SeamHarness.Enqueue(queue, "goal", "gone");
        await queue.DrainAsync(transport);

        var body = h.Last(pathContains: "sync/batch")!.Body!;
        Assert.Contains("\"present\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"gone\"", body, StringComparison.Ordinal);   // no phantom row created
        Assert.DoesNotContain("\"payload\":{}", body, StringComparison.Ordinal);
        Assert.Equal(1, transport.LastSkippedMissingPayload);
        Assert.Equal(1, transport.LastSubmitted);
    }

    [Fact]
    public async Task WhenNothingIsReadable_TheBatchIsRefused_NotSilentlyConsumed()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-none-readable");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var h = new Wave4SeamHarness.ScriptedHandler();
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "vanished");
        var drain = await queue.DrainAsync(transport);

        Assert.Equal(0, drain.Pushed);
        Assert.Equal(1, await queue.PendingCountAsync());
        Assert.Equal(0, h.CallCount());   // no request built from an empty operation list
    }

    [Fact]
    public async Task CorruptLocalJson_IsNotUploaded_EvenThoughTheQueueRowExists()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-corrupt");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", "{ not json ");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var h = new Wave4SeamHarness.ScriptedHandler();
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        Wave4SeamHarness.Enqueue(queue, "goal", "g1", meta: meta);
        await queue.DrainAsync(transport);
        Assert.Equal(0, h.CallCount());
        Assert.Equal(1, await queue.PendingCountAsync());
    }

    // ============================ idempotency (§5c) ==============================================

    [Fact]
    public async Task TheSameQueuedContent_ProducesTheSameIdempotencyKey_ChangedContentAnother()
    {
        using var dir = new Wave4SeamHarness.TempDir("sync-idem");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("POST", "/api/v1/sync/batch", Wave4SeamHarness.BatchEcho("applied"));
        var options = Wave4SeamHarness.Options(dir);
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var transport = Transport(port, options, auth, payloads);

        using var queue = Wave4SeamHarness.Queue(dir, out _);
        Wave4SeamHarness.Enqueue(queue, "goal", "g1", version: 1);
        await transport.PushAsync(await queue.SnapshotPendingAsync());
        string first = h.Last(pathContains: "sync/batch")!.Headers["Idempotency-Key"];

        await transport.PushAsync(await queue.SnapshotPendingAsync());
        Assert.Equal(first, h.Last(pathContains: "sync/batch")!.Headers["Idempotency-Key"]);   // replay-safe

        // A newer local edit is a DIFFERENT operation: no key collision with the older content.
        await queue.EnqueueAsync("goal", "g1", 2, new string('a', 64), 10);
        await transport.PushAsync(await queue.SnapshotPendingAsync());
        Assert.NotEqual(first, h.Last(pathContains: "sync/batch")!.Headers["Idempotency-Key"]);
    }

    [Fact]
    public void IdempotencyKey_DependsOnTheOperationIdSetOnly_AndIsHex()
    {
        static string KeyOf(params string[] ids) =>
            CloudSyncTransport.IdempotencyKeyFor(ids.Select(i => new SyncOperationDto(i, "goal", i, "update", null, null)).ToList());

        Assert.Matches("^[0-9a-f]{64}$", KeyOf("a", "b"));
        Assert.Equal(KeyOf("a", "b"), KeyOf("b", "a"));            // order-insensitive: the SET is the identity
        Assert.NotEqual(KeyOf("a", "b"), KeyOf("a", "c"));         // any content change flips the key
    }

    [Fact]
    public void OperationId_IsDeterministic_AndTiesTheReplayToTheVersionAndHash()
    {
        var env = new SyncEnvelope
        {
            EntityKind = "goal", EntityId = "g1", LocalState = SyncState.Pending,
            LocalVersion = 4, PayloadHash = new string('d', 64),
        };
        Assert.Equal("goal:g1@4#" + new string('d', 12), CloudSyncTransport.OperationIdOf(env));
        Assert.Equal(CloudSyncTransport.OperationIdOf(env), CloudSyncTransport.OperationIdOf(env));
        Assert.DoesNotContain(new string('d', 20), CloudSyncTransport.OperationIdOf(env), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFrozenKindVocabularyIsEverSent()
    {
        Assert.True(SyncOperationKinds.IsKnown(SyncOperationKinds.Create));
        Assert.True(SyncOperationKinds.IsKnown(SyncOperationKinds.Update));
        Assert.True(SyncOperationKinds.IsKnown(SyncOperationKinds.Delete));
        Assert.False(SyncOperationKinds.IsKnown("upsert"));
        var env = new SyncEnvelope { EntityKind = "goal", EntityId = "g", LocalState = SyncState.Pending, LocalVersion = 1, PayloadHash = "h" };
        Assert.Equal(SyncOperationKinds.Update, CloudSyncTransport.KindFor(env));   // never an invented kind
    }
}
