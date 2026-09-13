using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Sync;
using LIVORA.Domain.Enums;

namespace LIVORA.Tests.Wave3c;

/// <summary>
/// Wave 3c (lane 06): the metadata sidecar + the durable outbox queue. Every honesty claim in the
/// task sits here: legacy files read quiet (Version 0 / Clean), only a write bumps to Pending,
/// <c>Synced</c> appears only after a transport really succeeded, a conflict needs an explicit human
/// decision, and the cap drops transport-buffer records — never user data.
/// </summary>
public class MetaAndSyncQueueTests : IAsyncLifetime
{
    private readonly Scavenger _temp = new();
    private string _dir = "";

    public Task InitializeAsync()
    {
        _dir = _temp.Dir("metaqueue");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _temp.DisposeAsync();

    private MetaIndex NewIndex(string name = "idx.json") => new(_dir, name);

    // ---- meta: legacy default + bump -------------------------------------------

    [Fact]
    public async Task Meta_AbsentReadsAsLegacyVersionZeroClean()
    {
        var meta = NewIndex("absent.json");
        var m = await meta.GetAsync("goals", "g-1");

        Assert.Equal(0, m.Version);
        Assert.Equal(SyncState.Clean, m.SyncState);
        Assert.True(m.IsAbsent);
        // Honesty: no row was invented on disk for a read.
        Assert.False(File.Exists(meta.FilePath));
    }

    [Fact]
    public async Task Meta_BumpStampsUpdatedAtAdvancesVersionAndMarksPending()
    {
        var meta = NewIndex("bump.json");
        var t0 = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

        var first = await meta.BumpAsync("goals", "g-1", t0);
        Assert.Equal(1, first.Version);
        Assert.Equal(SyncState.Pending, first.SyncState);
        Assert.Equal(t0, first.UpdatedAtUtc);
        Assert.Equal(t0, first.CreatedAtUtc);

        var second = await meta.BumpAsync("goals", "g-1", t0.AddMinutes(5));
        Assert.Equal(2, second.Version);
        Assert.Equal(t0.AddMinutes(5), second.UpdatedAtUtc);
        Assert.Equal(t0, second.CreatedAtUtc);   // creation stamp survives a later edit
    }

    [Fact]
    public async Task Meta_ReloadFromDiskKeepsBumpedRows()
    {
        var meta = NewIndex("reload.json");
        await meta.BumpAsync("habits", "h-9", new DateTime(2026, 5, 5, 8, 0, 0, DateTimeKind.Utc));

        var reopened = NewIndex("reload.json");
        var m = await reopened.GetAsync("habits", "h-9");
        Assert.Equal(1, m.Version);
        Assert.Equal(SyncState.Pending, m.SyncState);
    }

    [Fact]
    public async Task Meta_KeysAreNamespacedPerKind()
    {
        var meta = NewIndex("ns.json");
        await meta.BumpAsync("goals", "same-id");
        await meta.BumpAsync("habits", "same-id");
        await meta.BumpAsync("goals", "same-id");

        Assert.Equal(2, (await meta.GetAsync("goals", "same-id")).Version);
        Assert.Equal(1, (await meta.GetAsync("habits", "same-id")).Version);
        Assert.Equal(2, await meta.CountAsync());
    }

    [Fact]
    public async Task Meta_SetStateOnNeverBumpedRowDoesNotInventMetadata()
    {
        var meta = NewIndex("noinvent.json");
        var back = await meta.SetStateAsync("goals", "legacy-1", SyncState.Clean);

        Assert.True(back.IsAbsent);
        Assert.Equal(0, await meta.CountAsync());
    }

    [Fact]
    public async Task Meta_CorruptIndexQuarantinesAndDegradesToEmpty()
    {
        var meta = NewIndex("corrupt.json");
        await meta.BumpAsync("goals", "g-1");
        File.WriteAllText(meta.FilePath, "{ this is not json");

        var reopened = NewIndex("corrupt.json");
        var m = await reopened.GetAsync("goals", "g-1");

        Assert.True(m.IsAbsent);
        var quarantined = Directory.GetFiles(_dir, "corrupt.json.corrupt-*");
        Assert.NotEmpty(quarantined);
        // The unreadable bytes survive for forensics — a quarantine must not be a rewrite.
        Assert.Equal("{ this is not json", File.ReadAllText(quarantined[0]));
    }

    // ---- queue: enqueue / FIFO / cap -------------------------------------------

    [Fact]
    public async Task Queue_EnqueueIsFifoAndCountsPending()
    {
        using var q = new SyncQueue(_temp.Dir("q-fifo"));
        for (int i = 0; i < 3; i++)
            await q.EnqueueAsync(Wave3cSync.Envelope("goals", $"g{i}", 1));

        Assert.Equal(3, await q.PendingCountAsync());
        var snap = await q.SnapshotPendingAsync();
        Assert.Equal(new[] { "goals:g0", "goals:g1", "goals:g2" },
            snap.Select(e => EntityMeta.KeyOf(e.EntityKind, e.EntityId)).ToArray());
    }

    [Fact]
    public async Task Queue_CapDropsOldestAndCountsDrops()
    {
        const int small = SyncQueue.Capacity; // documented cap
        Assert.Equal(10_000, small);

        using var q = new SyncQueue(_temp.Dir("q-cap"));
        for (int i = 0; i < small + 5; i++)
            await q.EnqueueAsync(Wave3cSync.Envelope("history", $"d{i}", 1));

        Assert.Equal(small, await q.CountAsync());
        Assert.Equal(5, await q.DroppedCountAsync());
        // The OLDEST five queued records left, the newest stayed (FIFO, entity data untouched).
        var snapshot = await q.SnapshotPendingAsync();
        Assert.DoesNotContain(snapshot, e => e.EntityId == "d0");
        Assert.Contains(snapshot, e => e.EntityId == "d10004");
    }

    [Fact]
    public async Task Queue_CapEvictionsAreNotResurrectedByRestart()
    {
        var dir = _temp.Dir("q-cap-replay");
        using (var q = new SyncQueue(dir))
        {
            for (int i = 0; i < SyncQueue.Capacity + 3; i++)
                await q.EnqueueAsync(Wave3cSync.Envelope("history", $"r{i}", 1));
            q.Dispose();
        }
        using var reopened = new SyncQueue(dir);
        Assert.Equal(SyncQueue.Capacity, await reopened.CountAsync());
        var snapshot = await reopened.SnapshotPendingAsync();
        Assert.DoesNotContain(snapshot, e => e.EntityId == "r0");
        Assert.Contains(snapshot, e => e.EntityId == "r10002");
        Assert.Equal(3, await reopened.DroppedCountAsync());
    }

    [Fact]
    public async Task Queue_DropCounterSurvivesRestart()
    {
        var dir = _temp.Dir("q-drop");
        using (var q = new SyncQueue(dir))
        {
            for (int i = 0; i < SyncQueue.Capacity + 2; i++)
                await q.EnqueueAsync(Wave3cSync.Envelope("history", $"x{i}", 1));
            Assert.Equal(2, await q.DroppedCountAsync());
        }
        using (var reopened = new SyncQueue(dir))
        {
            Assert.Equal(2, await reopened.DroppedCountAsync());
            Assert.Equal(SyncQueue.Capacity, await reopened.CountAsync());
        }
    }

    [Fact]
    public async Task Queue_NeverStoresRawPayload_OnlyHashAndSize()
    {
        var dir = _temp.Dir("q-payload");
        var canary = "Persian note: امروز حالت خوب بود";
        using (var q = new SyncQueue(dir))
        {
            await q.EnqueueAsync(new SyncEnvelope
            {
                EntityKind = "goals",
                EntityId = "g-1",
                LocalState = SyncState.Pending,
                LocalVersion = 1,
                PayloadHash = CanonicalJson.Sha256HexOfCanonical(CanonicalJson.Serialize(new { Name = canary })),
                ChangedAtUtc = DateTime.UtcNow,
            }, payloadBytes: Encoding.UTF8.GetByteCount(canary));

            // Inspect the LIVE journal (appender still open) — the payload must not be in it.
            await q.FlushAsync(); // one enqueue rides the buffer; land it before reading
            var journal = string.Join('\n', await q.ReadJournalLinesAsync());
            Assert.DoesNotContain(canary, journal);
            Assert.DoesNotContain("حالت", journal);
            Assert.Contains("\"hash\"", journal.ToLowerInvariant());
        }

        using var reopened = RequeueFrom(dir);
        var envelope = (await reopened.SnapshotPendingAsync()).Single();
        Assert.Equal(64, envelope.PayloadHash.Length); // SHA-256 hex
        Assert.Matches("^[0-9a-f]{64}$", envelope.PayloadHash);
    }

    private static SyncQueue RequeueFrom(string dir) => new(dir);

    [Fact]
    public async Task Queue_ReplayRestoresPendingEntries()
    {
        var dir = _temp.Dir("q-replay");
        using (var q = new SyncQueue(dir))
        {
            await q.EnqueueAsync(Wave3cSync.Envelope("bootcamps", "b-1", 3));
            q.Dispose();
        }
        using var reopened = new SyncQueue(dir);
        var snap = await reopened.SnapshotPendingAsync();
        var env = Assert.Single(snap);
        Assert.Equal("bootcamps", env.EntityKind);
        Assert.Equal(3, env.LocalVersion);
        Assert.Equal(SyncState.Pending, env.LocalState);
    }

    // ---- queue: drain honesty ---------------------------------------------------

    [Fact]
    public async Task Drain_NoTransport_KeepsEverythingPendingAndReportsWhy()
    {
        using var q = new SyncQueue(_temp.Dir("q-noop"));
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-a", 1));
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-b", 2));

        var report = await q.DrainAsync(FakeSyncTransport.NotConfigured());

        Assert.False(report.TransportConfigured);
        Assert.True(report.IsHonestNoOp);
        Assert.Equal("Sync.Reason.TransportNotConfigured", report.ReasonKey);
        Assert.Equal(0, report.Pushed);
        Assert.Equal(0, report.Synced);
        Assert.Equal(2, report.RemainedPending);
        Assert.Equal(2, await q.PendingCountAsync());
    }

    [Fact]
    public async Task Drain_NullTransport_KeepsEverythingPending()
    {
        using var q = new SyncQueue(_temp.Dir("q-null"));
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-a", 1));
        var report = await q.DrainAsync(transport: null);

        Assert.True(report.IsHonestNoOp);
        Assert.Equal(1, await q.PendingCountAsync());
        Assert.Equal(0, await q.SyncedCountAsync());
    }

    [Fact]
    public async Task Drain_ConfiguredSuccess_MarksSyncedEverywhere()
    {
        var meta = NewIndex("drain-ok.json");
        using var q = new SyncQueue(_temp.Dir("q-ok"), meta);
        await meta.BumpAsync("goals", "g-1");
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-1", 1));

        var transport = FakeSyncTransport.AlwaysSucceeds();
        var report = await q.DrainAsync(transport);

        Assert.True(report.TransportConfigured);
        Assert.Equal(1, report.Synced);
        Assert.Equal(0, report.RemainedPending);
        Assert.Equal(1, transport.PushedBatches.Count);
        Assert.Equal(0, await q.PendingCountAsync());
        Assert.Equal(1, await q.SyncedCountAsync());
        // The sidecar tells the same story the queue does.
        Assert.Equal(SyncState.Synced, (await meta.GetAsync("goals", "g-1")).SyncState);
    }

    [Fact]
    public async Task Drain_PushesInBatchesOf200()
    {
        using var q = new SyncQueue(_temp.Dir("q-batch"));
        for (int i = 0; i < 450; i++)
            await q.EnqueueAsync(Wave3cSync.Envelope("history", $"d{i}", 1));

        var transport = FakeSyncTransport.AlwaysSucceeds();
        await q.DrainAsync(transport);

        Assert.Equal(3, transport.PushedBatches.Count);
        Assert.Equal(new[] { 200, 200, 50 }, transport.PushedBatches.Select(b => b.Count).ToArray());
        Assert.Equal(450, await q.SyncedCountAsync());
    }

    [Fact]
    public async Task Drain_FailingTransport_LeavesEntriesPending()
    {
        using var q = new SyncQueue(_temp.Dir("q-fail"));
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-1", 1));

        var report = await q.DrainAsync(FakeSyncTransport.Failing("network"));

        Assert.Equal(1, report.FailedBatches);
        Assert.Equal("network", report.ErrorCategory);
        Assert.Equal(0, report.Synced);
        Assert.Equal(1, await q.PendingCountAsync());
        Assert.Equal(1, report.RemainedPending);
    }

    [Fact]
    public async Task Drain_ConflictResult_MovesEntriesToConflict()
    {
        var meta = NewIndex("drain-conflict.json");
        using var q = new SyncQueue(_temp.Dir("q-conflict"), meta);
        await meta.BumpAsync("goals", "g-1");
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-1", 1));

        var report = await q.DrainAsync(
            FakeSyncTransport.ConflictsOn(_ => true, ConflictKind.BothChanged));

        Assert.Equal(1, report.Conflicted);
        Assert.Equal(0, report.Synced);
        Assert.Equal(1, await q.ConflictCountAsync());
        Assert.Equal(0, await q.PendingCountAsync());
        Assert.Equal(SyncState.Conflict, (await meta.GetAsync("goals", "g-1")).SyncState);
    }

    [Fact]
    public async Task Conflict_StaysUntouchedUntilExplicitResolve()
    {
        var meta = NewIndex("conflict-idle.json");
        var dir = _temp.Dir("q-conflict-idle");
        using var q = new SyncQueue(dir, meta);
        await meta.BumpAsync("goals", "g-1");
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-1", 1));
        await q.DrainAsync(FakeSyncTransport.ConflictsOn(_ => true, ConflictKind.LocalChanged));

        // A second drain must NOT re-push or self-resolve: divergence needs a human.
        var second = FakeSyncTransport.AlwaysSucceeds();
        var report = await q.DrainAsync(second);
        Assert.Equal(0, second.PushedEnvelopes);
        Assert.Equal(0, report.Pushed);
        Assert.Equal(1, await q.ConflictCountAsync());
        Assert.Equal(SyncState.Conflict, (await meta.GetAsync("goals", "g-1")).SyncState);

        // Restart: still Conflict — the state is durable, not an in-memory accident.
        q.Dispose();
        using var reopened = new SyncQueue(dir, NewIndex("conflict-idle.json"));
        Assert.Equal(1, await reopened.ConflictCountAsync());
        Assert.Equal(SyncState.Conflict, (await reopened.StateOfAsync("goals:g-1")).GetValueOrDefault());
    }

    [Fact]
    public async Task ResolveConflict_KeepLocal_ArmsARePush()
    {
        var meta = NewIndex("resolve-local.json");
        using var q = new SyncQueue(_temp.Dir("q-resolve-local"), meta);
        await meta.BumpAsync("goals", "g-1");
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-1", 1));
        await q.DrainAsync(FakeSyncTransport.ConflictsOn(_ => true, ConflictKind.LocalChanged));

        Assert.True(await q.ResolveConflictAsync("goals:g-1", keepLocal: true));

        Assert.Equal(0, await q.ConflictCountAsync());
        Assert.Equal(1, await q.PendingCountAsync());
        Assert.Equal(SyncState.Pending, (await meta.GetAsync("goals", "g-1")).SyncState);

        var transport = FakeSyncTransport.AlwaysSucceeds();
        await q.DrainAsync(transport);
        Assert.Equal(1, transport.PushedEnvelopes);
        Assert.Equal(1, await q.SyncedCountAsync());
    }

    [Fact]
    public async Task ResolveConflict_KeepRemote_StopsPushingAndClearsQueueRow()
    {
        var meta = NewIndex("resolve-remote.json");
        using var q = new SyncQueue(_temp.Dir("q-resolve-remote"), meta);
        await meta.BumpAsync("goals", "g-1");
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-1", 1));
        await q.DrainAsync(FakeSyncTransport.ConflictsOn(_ => true, ConflictKind.RemoteChanged));

        Assert.True(await q.ResolveConflictAsync("goals:g-1", keepLocal: false));

        Assert.Equal(0, await q.CountAsync());
        Assert.Equal(SyncState.Clean, (await meta.GetAsync("goals", "g-1")).SyncState);
        // Version untouched: a transport attempt is not a local edit.
        Assert.Equal(1, (await meta.GetAsync("goals", "g-1")).Version);
        var transport = FakeSyncTransport.AlwaysSucceeds();
        await q.DrainAsync(transport);
        Assert.Equal(0, transport.PushedEnvelopes);
    }

    [Fact]
    public async Task ResolveConflict_OnNonConflictingKey_IsRefused()
    {
        using var q = new SyncQueue(_temp.Dir("q-resolve-refuse"));
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-1", 1));

        Assert.False(await q.ResolveConflictAsync("goals:g-1", keepLocal: true));
        Assert.False(await q.ResolveConflictAsync("goals:missing", keepLocal: true));
        Assert.Equal(1, await q.PendingCountAsync());
    }

    [Fact]
    public async Task Queue_Clear_ErasesJournalAndCounters()
    {
        var dir = _temp.Dir("q-clear");
        using var q = new SyncQueue(dir);
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-1", 1));
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-2", 1));

        await q.ClearAsync();
        Assert.Equal(0, await q.CountAsync());
        Assert.Equal(0, await q.PendingCountAsync());
        q.Dispose();

        Assert.False(File.Exists(Path.Combine(dir, SyncQueue.JournalFileName)));
        Assert.False(File.Exists(Path.Combine(dir, SyncQueue.JournalFileName) + ".bak"));
    }

    [Fact]
    public async Task Queue_FlushAsync_LandsBufferedEnqueuesOnDisk()
    {
        var dir = _temp.Dir("q-flush");
        var q = new SyncQueue(dir);
        await q.EnqueueAsync(Wave3cSync.Envelope("goals", "g-1", 1));
        await q.FlushAsync();

        Assert.True(File.Exists(Path.Combine(dir, SyncQueue.JournalFileName)));
        var lines = await q.ReadJournalLinesAsync();
        Assert.Contains(lines, l => l.Contains("g-1"));
        q.Dispose();
    }

    // ---- canonical hash determinism ---------------------------------------------

    [Fact]
    public void CanonicalHash_IsInvariantToKeyOrderAndWhitespace()
    {
        var a = CanonicalJson.Sha256HexOfCanonical("""{"b":2,"a":1,"nested":{"y":"two","x":[1,2]}}""");
        var b = CanonicalJson.Sha256HexOfCanonical("""{ "a" : 1 , "b" : 2 , "nested" : { "x" : [ 1 , 2 ] , "y" : "two" } }""");
        var c = CanonicalJson.Sha256HexOfCanonical("""{"nested":{"x":[1,2],"y":"two"},"a":1,"b":2}""");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void CanonicalHash_DiffersWhenContentDiffers()
    {
        var one = CanonicalJson.Sha256HexOfCanonical("""{"id":"g-1","name":"Sleep"}""");
        var two = CanonicalJson.Sha256HexOfCanonical("""{"id":"g-1","name":"Sport"}""");
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void CanonicalHash_MatchesSha256OfCanonicalBytes()
    {
        const string canonical = """{"a":1,"b":"x"}""";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        // Key order in the input must not change the digest of the canonical form.
        Assert.Equal(expected, CanonicalJson.Sha256HexOfCanonical("""{"b":"x","a":1}"""));
    }

    [Fact]
    public void CanonicalSerialize_SortsPropertiesByNameRegardlessOfDeclarationOrder()
    {
        var json = CanonicalJson.Serialize(new SampleModel { Zebra = 1, Alpha = "a" });
        Assert.Equal("""{"Alpha":"a","Zebra":1}""", json);
    }

    private sealed class SampleModel
    {
        public int Zebra { get; set; }
        public string Alpha { get; set; } = "";
    }
}
