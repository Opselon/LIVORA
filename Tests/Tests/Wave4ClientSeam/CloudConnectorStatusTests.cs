using LIVORA.Application.Abstractions;
using LIVORA.Application.Cloud;
using LIVORA.Application.Sync;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Cloud;
using LIVORA.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Tests.Tests.Wave4ClientSeam;

/// <summary>
/// The bridge + connector state machine: the rules that decide what a user is allowed to read on the
/// cloud chip. Every assertion here is about a WORD the UI would otherwise be free to invent —
/// "synced", "connected", "verified" — and the evidence that alone earns it.
/// </summary>
public class CloudConnectorStateMachineTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    private static CloudConnectorEvidence Ev(
        bool configured = true, bool attempted = true, bool reached = true, bool session = true,
        int pending = 0, int conflicts = 0, string? error = null, DateTimeOffset? confirmed = null,
        bool goodAnswer = true, CapabilitySnapshotDto? caps = null) =>
        new()
        {
            Configured = configured,
            ProbeAttempted = attempted,
            LastAttemptReachedServer = reached,
            HasSession = session,
            PendingCount = pending,
            ConflictCount = conflicts,
            LastErrorReasonKey = error,
            LastProbeAtUtc = attempted ? T : null,
            // "the host answered well" is what a successful attempt means, so the helper derives it;
            // the CONFIRMATION timestamp is only ever what the test hands in — earned, never assumed.
            LastGoodAnswerAtUtc = goodAnswer && attempted && reached && error is null ? T : null,
            LastConfirmedAtUtc = confirmed,
            Capabilities = caps,
        };

    [Fact]
    public void NoBaseUrl_IsUnconfigured_EvenWithASessionAndACleanQueue()
    {
        var s = CloudConnectorStateMachine.Evaluate(Ev(configured: false, session: true));
        Assert.Equal(CloudConnectorState.Unconfigured, s.State);
        Assert.Equal("Cloud.Connector.State.Unconfigured", s.StateReasonKey);
        Assert.False(s.IsLinkUp);
        Assert.False(s.ConfirmedByServer);
    }

    [Fact]
    public void ConfiguredButNeverAttempted_IsUnverified_NotConnected()
    {
        var s = CloudConnectorStateMachine.Evaluate(Ev(attempted: false));
        Assert.Equal(CloudConnectorState.Unverified, s.State);
        Assert.Equal("Cloud.Connector.State.Unverified", s.StateReasonKey);
        Assert.Null(s.LastProbeAtUtc);
    }

    [Fact]
    public void LastAttemptNeverReachedTheHost_IsOffline_NotFailed()
    {
        var s = CloudConnectorStateMachine.Evaluate(Ev(reached: false, error: "Api.Error.network"));
        Assert.Equal(CloudConnectorState.Offline, s.State);
        Assert.Equal("Cloud.Connector.State.Offline", s.StateReasonKey);
        Assert.False(s.IsLinkUp);
    }

    [Fact]
    public void ReachableWithoutASession_IsSignedOut_AndNeverClaimsSynced()
    {
        var s = CloudConnectorStateMachine.Evaluate(Ev(session: false));
        Assert.Equal(CloudConnectorState.SignedOut, s.State);
        Assert.False(s.ConfirmedByServer);
    }

    [Fact]
    public void AnAnsweredError_UsesTheRecordedReasonKeyDirectly()
    {
        var s = CloudConnectorStateMachine.Evaluate(Ev(error: "Api.Error.rate_limited"));
        Assert.Equal(CloudConnectorState.Failed, s.State);
        Assert.Equal("Api.Error.rate_limited", s.StateReasonKey);   // the code's own key, not a generic word
    }

    [Fact]
    public void ConflictsOutrankPendingCounts_BecauseTheyBlockTheQueue()
    {
        var s = CloudConnectorStateMachine.Evaluate(Ev(pending: 3, conflicts: 2, error: null));
        Assert.Equal(CloudConnectorState.Conflict, s.State);
        Assert.Equal("Cloud.Connector.Reason.Conflicts", s.StateReasonKey);
        Assert.Equal(new object[] { 2 }, s.ReasonArgs);
    }

    [Fact]
    public void QueuedWorkIsNeverCalledSynced_EvenWhenTheTunnelWorks()
    {
        var s = CloudConnectorStateMachine.Evaluate(Ev(pending: 4, confirmed: T));
        Assert.Equal(CloudConnectorState.PendingLocal, s.State);
        Assert.Equal("Cloud.Connector.Reason.Pending", s.StateReasonKey);
        Assert.Equal(4, s.PendingCount);
        Assert.True(s.IsLinkUp);                       // the link is fine...
        Assert.NotEqual(CloudConnectorState.Synced, s.State);   // ...and that is not the same sentence
    }

    [Fact]
    public void AServerThatSaysDegraded_IsReportedAsDegraded_NotOk()
    {
        var caps = new CapabilitySnapshotDto(T, "v1",
            new[] { new CapabilityEntryDto("identity", "degraded", "detail", null) }, null, null);
        var s = CloudConnectorStateMachine.Evaluate(Ev(caps: caps, confirmed: T));
        Assert.Equal(CloudConnectorState.Degraded, s.State);
        Assert.Equal("Cloud.Connector.State.Degraded", s.StateReasonKey);
        Assert.False(s.ConfirmedByServer);   // never promoted past the server's own word
    }

    [Fact]
    public void OnlyAConfirmedCleanCycleReachesSynced()
    {
        var caps = new CapabilitySnapshotDto(T, "v1",
            new[] { new CapabilityEntryDto("identity", "ok", "d", null) },
            new CapabilityEntryDto("database", "ok", "d", null), Array.Empty<string>());
        var s = CloudConnectorStateMachine.Evaluate(Ev(caps: caps, confirmed: T));
        Assert.Equal(CloudConnectorState.Synced, s.State);
        Assert.True(s.ConfirmedByServer);
        Assert.Equal("synced", s.StateTag);
    }

    [Fact]
    public void SyncedWithoutAGoodAnswerTimestamp_CollapsesToUnverified()
    {
        // The one structural guard in the projector: a clean queue is not proof the server ever
        // answered this device. "In sync" without a real answer is an inference, not a measurement.
        var s = CloudConnectorStateMachine.Evaluate(Ev(goodAnswer: false));
        Assert.Equal(CloudConnectorState.Unverified, s.State);
        Assert.False(s.ConfirmedByServer);
    }

    [Fact]
    public void AProbeAnswerEarnsSynced_ButNeverTheConfirmedTimestamp()
    {
        // The two facts stay two facts: the host responded (so "in sync" is honest for a clean
        // queue), but it never confirmed queued work, so ConfirmedByServer stays false and the
        // "server confirmed at …" line has nothing to render.
        var s = CloudConnectorStateMachine.Evaluate(Ev(confirmed: null));
        Assert.Equal(CloudConnectorState.Synced, s.State);
        Assert.Null(s.LastConfirmedAtUtc);
        Assert.False(s.ConfirmedByServer);
    }

    [Fact]
    public void WorkingOverlay_NeverMasksUnconfiguredOrOffline()
    {
        var unconf = CloudConnectorStateMachine.EvaluateWithWork(Ev(configured: false), workInFlight: true);
        Assert.Equal(CloudConnectorState.Unconfigured, unconf.State);

        var offline = CloudConnectorStateMachine.EvaluateWithWork(Ev(reached: false), workInFlight: true);
        Assert.Equal(CloudConnectorState.Offline, offline.State);

        // The overlay may only paint over a state the queue has proven; pending counts survive it.
        var working = CloudConnectorStateMachine.EvaluateWithWork(Ev(pending: 2, confirmed: T), workInFlight: true);
        Assert.Equal(CloudConnectorState.Working, working.State);
        Assert.Equal("Cloud.Connector.State.Working", working.StateReasonKey);
        // The overlay must not lose the counts it is drawing on top of.
        Assert.Equal(2, working.PendingCount);
    }

    [Fact]
    public void EveryStateReasonKey_IsADottedKeyThatShipsInBothLanguages()
    {
        var keys = new[]
        {
            CloudConnectorStateMachine.KeyUnconfigured, CloudConnectorStateMachine.KeyUnverified,
            CloudConnectorStateMachine.KeyOffline, CloudConnectorStateMachine.KeySignedOut,
            CloudConnectorStateMachine.KeyWorking, CloudConnectorStateMachine.KeyFailed,
            CloudConnectorStateMachine.KeyConflict, CloudConnectorStateMachine.KeyPending,
            CloudConnectorStateMachine.KeyDegraded, CloudConnectorStateMachine.KeySynced,
            CloudConnectorStateMachine.KeyConflictCount, CloudConnectorStateMachine.KeyPendingCount,
        };
        foreach (var k in keys)
        {
            Assert.StartsWith("Cloud.", k, StringComparison.Ordinal);
            Assert.DoesNotContain(' ', k);
        }
    }

    [Fact]
    public void NotOkFollowsTheServerStringsExactly_AndToleratesTheUnknown()
    {
        Assert.False(CloudConnectorStateMachine.AnyModuleNotOk(null));   // never fetched != degraded
        Assert.False(CloudConnectorStateMachine.AnyModuleNotOk(
            new CapabilitySnapshotDto(T, "v1", new[] { new CapabilityEntryDto("sync", "OK", "d", null) }, null, null)));
        foreach (var state in new[] { "degraded", "unconfigured", "not_implemented", "whatever_new" })
            Assert.True(CloudConnectorStateMachine.AnyModuleNotOk(
                new CapabilitySnapshotDto(T, "v1", new[] { new CapabilityEntryDto("sync", state, "d", null) }, null, null)),
                state + " must not be promoted to ok");
    }
}

/// <summary>The bridge end-to-end, with the real queue and a scripted wire behind the port.</summary>
public class CloudSyncBridgeTests
{
    [Fact]
    public async Task FreshInstall_ReportsUnconfigured_AndNothingWasProbed()
    {
        using var dir = new Wave4SeamHarness.TempDir("bridge-fresh");
        var h = new Wave4SeamHarness.ScriptedHandler();
        var options = Wave4SeamHarness.Options(dir, configured: false);
        using var port = new LivoraApiPort(options, h, null, NullLogger<LivoraApiPort>.Instance);
        using var queue = Wave4SeamHarness.Queue(dir, out _);
        var bridge = new CloudSyncBridge(port, options, queue, Wave4SeamHarness.StateStore(dir));

        var status = await bridge.RefreshStatusAsync();
        Assert.Equal(CloudConnectorState.Unconfigured, status.State);
        Assert.Equal("Cloud.Connector.State.Unconfigured", status.StateReasonKey);
        Assert.Null(status.LastProbeAtUtc);
        Assert.Equal(0, h.CallCount());
        Assert.Equal(NoopSyncTransport.Label, bridge.GatewayLabel);

        var report = await bridge.SyncNowAsync();
        Assert.True(report.Skipped);
        Assert.Equal("Cloud.Sync.Reason.NotConfigured", report.SkipReasonKey);
        Assert.False(report.MovedAnything);
        Assert.Equal(0, h.CallCount());   // still nothing sent
    }

    [Fact]
    public async Task ProbeAgainstADownServer_LandsOnOffline_AndRecordsTheAttempt()
    {
        using var dir = new Wave4SeamHarness.TempDir("bridge-offline");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("GET", "/api/v1/platform/capabilities", _ => throw new System.Net.Http.HttpRequestException("down"));
        var options = Wave4SeamHarness.Options(dir);
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        using var queue = Wave4SeamHarness.Queue(dir, out _);
        var bridge = new CloudSyncBridge(port, options, queue, Wave4SeamHarness.StateStore(dir), auth);

        var status = await bridge.ProbeAsync();
        Assert.Equal(CloudConnectorState.Offline, status.State);
        Assert.NotNull(status.LastProbeAtUtc);   // the attempt is a fact
        Assert.False(status.IsLinkUp);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task ASuccessfulProbePromotesUnverified_AndRendersTheServersOwnStates()
    {
        using var dir = new Wave4SeamHarness.TempDir("bridge-probe");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Json("GET", "/api/v1/platform/capabilities", 200, Wave4SeamHarness.CapabilitiesBody());
        var options = Wave4SeamHarness.Options(dir);
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        using var queue = Wave4SeamHarness.Queue(dir, out _);
        var bridge = new CloudSyncBridge(port, options, queue, Wave4SeamHarness.StateStore(dir), auth);

        var first = await bridge.RefreshStatusAsync();
        Assert.Equal(CloudConnectorState.Unverified, first.State);   // configured, but unproven

        var probed = await bridge.ProbeAsync();
        // A successful probe is enough to say "in sync" when nothing is queued...
        Assert.Equal(CloudConnectorState.Synced, probed.State);
        // ...but it is NOT a confirmation of queued work, so the badge stays off.
        Assert.False(probed.ConfirmedByServer);
        Assert.NotNull(bridge.LastCapabilities);
        Assert.Equal("unconfigured", bridge.LastCapabilities!.Module("identity")!.Capabilities!["google_oauth"]);
        Assert.Null(probed.LastCorrelationId);   // no failure: nothing to hand support
    }

    [Fact]
    public async Task ADegradedServerIsNeverRenderedAsSynced()
    {
        using var dir = new Wave4SeamHarness.TempDir("bridge-degraded");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Json("GET", "/api/v1/platform/capabilities", 200,
                Wave4SeamHarness.CapabilitiesBody(identityState: "unconfigured"));
        var options = Wave4SeamHarness.Options(dir);
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        using var queue = Wave4SeamHarness.Queue(dir, out _);
        var bridge = new CloudSyncBridge(port, options, queue, Wave4SeamHarness.StateStore(dir), auth);

        var status = await bridge.ProbeAsync();
        Assert.Equal(CloudConnectorState.Degraded, status.State);
        Assert.False(status.ConfirmedByServer);   // a probe is not a confirmation of queued work
    }

    [Fact]
    public async Task AFullCycle_ConfirmsTheQueue_AndTheStatusThenReadsSynced()
    {
        using var dir = new Wave4SeamHarness.TempDir("bridge-cycle");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var options = Wave4SeamHarness.Options(dir);
        var h = new Wave4SeamHarness.ScriptedHandler();
        h.On("POST", "/api/v1/sync/batch", Wave4SeamHarness.BatchEcho("applied"));
        h.Json("GET", "/api/v1/sync/changes", 200, Wave4SeamHarness.ChangesBody(42, "remote-g"));
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var realTransport = new CloudSyncTransport(port, options, auth, payloads, NullLogger<CloudSyncTransport>.Instance);
        var state = Wave4SeamHarness.StateStore(dir);
        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        var bridge = new CloudSyncBridge(port, options, queue, state, auth, realTransport);

        Wave4SeamHarness.Enqueue(queue, "goal", "g1");
        var pre = await bridge.RefreshStatusAsync();
        // Nothing has ever been attempted in this build, so the first honest state to match is
        // Unverified — and the queue's real count still rides along on the status for the rows that
        // render it. "PendingLocal" only appears once an attempt has actually been made.
        Assert.Equal(CloudConnectorState.Unverified, pre.State);
        Assert.Equal(1, pre.PendingCount);

        var report = await bridge.SyncNowAsync();
        Assert.False(report.Skipped);
        Assert.Equal(1, report.Applied);
        Assert.Equal(1, report.ChangesPulled);
        Assert.False(report.RemoteChangesApplied);      // Phase 1 has no local-apply path
        Assert.Equal(42, report.WatermarkAfter);
        Assert.True(report.ReachedServer);
        Assert.True(report.MovedAnything);

        Assert.Equal(0, await queue.PendingCountAsync());
        Assert.Equal(SyncState.Synced, (await meta.GetAsync("goal", "g1")).SyncState);
        Assert.Equal(CloudConnectorState.Synced, bridge.Status.State);
        Assert.NotNull(bridge.Status.LastConfirmedAtUtc);

        // Watermark persisted: a second cycle asks for changes SINCE 42, not from the beginning.
        await bridge.SyncNowAsync();
        Assert.Contains("since=42", h.Last(pathContains: "sync/changes")!.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyQueueCycleRecordsNothingAsConfirmed()
    {
        using var dir = new Wave4SeamHarness.TempDir("bridge-empty-cycle");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var options = Wave4SeamHarness.Options(dir);
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Json("GET", "/api/v1/sync/changes", 200, Wave4SeamHarness.ChangesBody(3));
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var real = new CloudSyncTransport(port, options, auth, payloads, NullLogger<CloudSyncTransport>.Instance);
        var state = Wave4SeamHarness.StateStore(dir);
        using var queue = Wave4SeamHarness.Queue(dir, out _);
        var bridge = new CloudSyncBridge(port, options, queue, state, auth, real);

        var report = await bridge.SyncNowAsync();
        Assert.Equal(0, report.Applied);
        Assert.False(report.MovedAnything);
        // The host answered (so a clean queue honestly reads Synced) but it never confirmed queued
        // work: the two timestamps are two different facts, and only the second lights the badge.
        Assert.Equal(CloudConnectorState.Synced, bridge.Status.State);
        Assert.False(bridge.Status.ConfirmedByServer);
        // No push was attempted at all: no queue rows, no batch request.
        Assert.Equal(0, h.CallCount("POST", "sync/batch"));
    }

    [Fact]
    public async Task SignOutWipesTheConnectorRecord_ANewAccountCannotInheritLastConfirmed()
    {
        using var dir = new Wave4SeamHarness.TempDir("bridge-signout");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        await store.WriteAsync(new CloudSession
        {
            UserId = "u-1", SessionId = "s-1", AccessToken = "at-1", RefreshToken = "rt-1",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
        });
        var options = Wave4SeamHarness.Options(dir);
        var h = new Wave4SeamHarness.ScriptedHandler();
        h.On("POST", "/api/v1/sync/batch", Wave4SeamHarness.BatchEcho("applied"));
        h.Json("GET", "/api/v1/sync/changes", 200, Wave4SeamHarness.ChangesBody(5));
        h.On("POST", "/api/v1/auth/logout", _ => Wave4SeamHarness.NoContent());
        using var port = new LivoraApiPort(options, h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, store, NullLogger<CloudSessionManager>.Instance);
        await auth.RestoreAsync();
        var state = Wave4SeamHarness.StateStore(dir);
        using var queue = Wave4SeamHarness.Queue(dir, out _);
        var real = new CloudSyncTransport(port, options, auth, payloads, NullLogger<CloudSyncTransport>.Instance);
        var bridge = new CloudSyncBridge(port, options, queue, state, auth, real);

        Wave4SeamHarness.Enqueue(queue, "goal", "g1");
        await bridge.SyncNowAsync();
        Assert.NotNull(bridge.Status.LastConfirmedAtUtc);

        var signedOut = await bridge.SignOutAsync();
        Assert.True(signedOut.Ok);
        var after = await bridge.RefreshStatusAsync();
        Assert.Equal(CloudConnectorState.SignedOut, after.State);
        Assert.Null(after.LastConfirmedAtUtc);        // wiped, not carried over
        Assert.Null(state.ReadRecord().LastConfirmedAtUtc);
        Assert.Null(state.ReadCursor().LatestRevision);
    }

    [Fact]
    public async Task OfflineDuringARealCycle_QueuedWorkStaysPending_AndTheChipSaysOffline()
    {
        using var dir = new Wave4SeamHarness.TempDir("bridge-offline-cycle");
        var payloads = new Wave4SeamHarness.MapPayloadSource();
        payloads.Put("goal", "g1", """{"id":"g1"}""");
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        var options = Wave4SeamHarness.Options(dir);
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("POST", "/api/v1/sync/batch", _ => throw new System.Net.Http.HttpRequestException("down"))
            .On("GET", "/api/v1/sync/changes", _ => throw new System.Net.Http.HttpRequestException("down"));
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        var real = new CloudSyncTransport(port, options, auth, payloads, NullLogger<CloudSyncTransport>.Instance);
        var state = Wave4SeamHarness.StateStore(dir);
        using var queue = Wave4SeamHarness.Queue(dir, out var meta);
        var bridge = new CloudSyncBridge(port, options, queue, state, auth, real);

        Wave4SeamHarness.Enqueue(queue, "goal", "g1", meta: meta);
        var report = await bridge.SyncNowAsync();

        Assert.Equal(0, report.Applied);
        Assert.False(report.ReachedServer);
        // The queue classifies a throwing transport by exception type; the bridge reports that
        // category rather than renaming it to its own guess.
        Assert.Equal(LivoraApiTransportCodes.Network, report.FailureCode);   // the port's own transport tag
        Assert.Equal("Cloud.Sync.Reason.PushRefused", report.FailureReasonKey);
        Assert.Equal(1, await queue.PendingCountAsync());                       // nothing consumed
        Assert.Equal(SyncState.Pending, (await meta.GetAsync("goal", "g1")).SyncState);
        Assert.Equal(CloudConnectorState.Offline, bridge.Status.State);
    }

    [Fact]
    public async Task StatusChanged_FiresOnlyWhenTheProjectionActuallyChanged()
    {
        using var dir = new Wave4SeamHarness.TempDir("bridge-events");
        var options = Wave4SeamHarness.Options(dir);
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Json("GET", "/api/v1/platform/capabilities", 200, Wave4SeamHarness.CapabilitiesBody());
        var auth = new Wave4SeamHarness.ScriptedAuth("at-1");
        using var port = new LivoraApiPort(options, h, auth, NullLogger<LivoraApiPort>.Instance);
        using var queue = Wave4SeamHarness.Queue(dir, out _);
        // Fixed clock: two probes at the same recorded instant are the same projection, which is the
        // only way to tell "nothing changed" from "the timestamps moved".
        var bridge = new CloudSyncBridge(port, options, queue,
            Wave4SeamHarness.StateStore(dir, new Wave4SeamHarness.FixedTime()), auth);

        int hits = 0;
        bridge.StatusChanged += () => hits++;
        await bridge.ProbeAsync();
        Assert.Equal(1, hits);
        await bridge.ProbeAsync();          // same verdict twice: no repaint storm
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task TheBridgeNeverMarksAnythingSyncedOnItsOwn_QueueStaysTheOnlyWriter()
    {
        // Structural: the bridge composes SyncQueue and never touches MetaIndex directly.
        var text = Wave4SeamHarness.ReadRepoFile("Infrastructure/Cloud/CloudSyncBridge.cs");
        if (text is null) return;
        Assert.DoesNotContain("SetStateAsync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncState.Synced,", text, StringComparison.Ordinal);
        Assert.Contains("_queue.DrainAsync", text, StringComparison.Ordinal);   // state moves through the queue
    }
}

/// <summary>Where the base URL comes from, and what "configured" may and may not mean.</summary>
public class CloudApiOptionsTests
{
    [Fact]
    public void DefaultIsUnconfigured_NoBuiltInHostIsGuessed()
    {
        using var dir = new Wave4SeamHarness.TempDir("opts-default");
        var o = new CloudApiOptions(dir.Store(CloudApiOptions.CloudDirName));
        Assert.False(o.IsConfigured);
        Assert.Null(o.ResolvedUrl);
        Assert.Equal("absent", o.RejectionReason);
        Assert.Equal("none", o.SourceLabel);
    }

    [Theory]
    [InlineData("https://api.livora.test/", "https://api.livora.test")]
    [InlineData("  https://api.livora.test/base  ", "https://api.livora.test/base")]
    public void AValidHttpsUrl_NormalizesTheTrailingSlash(string input, string expected)
    {
        using var dir = new Wave4SeamHarness.TempDir("opts-ok-" + expected.Length);
        var o = new CloudApiOptions(dir.Store(CloudApiOptions.CloudDirName));
        Assert.Null(o.TrySetBaseUrl(input));
        Assert.Equal(expected, o.ResolvedUrl);
        Assert.True(o.IsConfigured);
        Assert.Equal("store", o.SourceLabel);
    }

    [Theory]
    [InlineData("http://api.livora.test", "scheme")]      // a bearer must not cross cleartext
    [InlineData("api.livora.test", "relative")]
    [InlineData("ftp://api.livora.test", "scheme")]
    public void ARejectedUrl_NeverBecomesConfigured(string input, string expectedReason)
    {
        using var dir = new Wave4SeamHarness.TempDir("opts-reject-" + expectedReason);
        var o = new CloudApiOptions(dir.Store(CloudApiOptions.CloudDirName));
        Assert.Equal(expectedReason, o.TrySetBaseUrl(input));   // the write itself says why it refused
        Assert.False(o.IsConfigured);                            // and no half-configured state survives
        // RejectionReason describes the STORED state, which is still empty after a refused write:
        // "absent". The two channels are different facts and must not be conflated.
        Assert.Equal("absent", o.RejectionReason);
    }

    [Fact]
    public void CleartextLoopback_IsAcceptedOnlyForTheDevHeadThatAsksForIt()
    {
        using var dir = new Wave4SeamHarness.TempDir("opts-loopback");
        var strict = new CloudApiOptions(dir.Store("strict"));
        Assert.Equal("scheme", strict.TrySetBaseUrl("http://localhost:5000"));

        var dev = new CloudApiOptions(dir.Store("dev"), allowInsecureLoopback: true);
        Assert.Null(dev.TrySetBaseUrl("http://localhost:5000"));
        Assert.Equal("http://localhost:5000", dev.ResolvedUrl);
    }

    [Fact]
    public void ClearingTheUrl_ReturnsTheSeamToUnconfigured()
    {
        using var dir = new Wave4SeamHarness.TempDir("opts-clear");
        var o = new CloudApiOptions(dir.Store(CloudApiOptions.CloudDirName));
        o.TrySetBaseUrl("https://api.livora.test");
        Assert.True(o.IsConfigured);
        Assert.Null(o.TrySetBaseUrl("   "));
        Assert.False(o.IsConfigured);
        Assert.Equal("absent", o.RejectionReason);
    }

    [Fact]
    public void CorruptSettingsFile_DegradesToUnconfigured_NotACrash()
    {
        using var dir = new Wave4SeamHarness.TempDir("opts-corrupt");
        var store = dir.Store(CloudApiOptions.CloudDirName);
        store.WriteRawAtomic(CloudApiOptions.SettingsFileName, "{ not json");
        var o = new CloudApiOptions(store);
        Assert.False(o.IsConfigured);
        Assert.Equal(new TimeSpan(0, 0, 20), o.Timeout);   // documented default, not a random value
    }

    [Fact]
    public void TimeoutIsClampedToADocumentedWindow()
    {
        using var dir = new Wave4SeamHarness.TempDir("opts-timeout");
        var o = new CloudApiOptions(dir.Store(CloudApiOptions.CloudDirName));
        o.SetTimeout(TimeSpan.FromSeconds(900));
        Assert.Equal(120, o.Timeout.TotalSeconds);
        o.SetTimeout(TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, o.Timeout.TotalSeconds);
    }

    [Fact]
    public void AThrowingOverrideSource_ReadsUnconfigured_InsteadOfFaultingThePage()
    {
        using var dir = new Wave4SeamHarness.TempDir("opts-throw");
        var o = new CloudApiOptions(dir.Store(CloudApiOptions.CloudDirName),
            overrideUrl: () => throw new IOException("config store gone"));
        Assert.False(o.IsConfigured);
    }

    [Fact]
    public void TheSettingsFileCarriesNoCredentialField()
    {
        var text = Wave4SeamHarness.ReadRepoFile("Infrastructure/Cloud/CloudApiOptions.cs");
        if (text is null) return;
        var body = text[(text.IndexOf("private sealed class SettingsFile", StringComparison.Ordinal))..];
        var fields = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.DoesNotContain("Token", fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", fields, StringComparison.OrdinalIgnoreCase);
    }
}
