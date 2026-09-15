using LIVORA.Application.Abstractions;
using LIVORA.Application.Cloud;
using LIVORA.Application.Sync;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Infrastructure.Cloud;

/// <summary>
/// WAVE 4 P1-D — the offline sync bridge: the seam between Wave 3c's durable outbox and the cloud,
/// and the object the connector chip renders from. It implements two existing contracts instead of
/// inventing a third layer:
/// <list type="bullet">
///   <item><see cref="ISyncTransport"/> — so <see cref="SyncQueue.DrainAsync"/> can be handed THIS
///     (the DI block replaces the Wave 3c noop registration). When the cloud is not usable it defers
///     to <see cref="NoopSyncTransport"/>, byte for byte: <c>IsConfigured</c> false, push refused,
///     every queue entry keeps <see cref="SyncState.Pending"/>. When it IS usable it delegates to
///     <see cref="CloudSyncTransport"/>, which only reports success on a server-confirmed batch.
///     Either way the queue's own honesty rules are untouched — this class cannot mark anything Synced.</item>
///   <item><see cref="ICloudConnector"/> — the UI-facing surface: status projection, real probe, one
///     sync cycle, sign-out.</item>
/// </list>
///
/// WHY IT NEVER FABRICATES: every field of the status comes from one of four real sources — the
/// options (a URL exists?), the session manager (a session is held?), the queue (counters read from
/// its own state), the state store (what the last real round-trip produced) and the capability
/// snapshot (what the server itself said). Nothing is inferred from "the app is running", and the
/// strongest word the surface can produce (<see cref="CloudConnectorState.Synced"/>) additionally
/// requires a confirmation timestamp the server earned — the state machine refuses to render Synced
/// without it even if every counter looks clean.
///
/// PULL SIDE (<c>GET /sync/changes</c>): Phase 1 records the watermark and COUNTS what came down; it
/// does not write remote changes over local data, because there is no local-apply path yet (that is
/// a Phase-2 lane). The report carries <see cref="CloudSyncCycleReport.RemoteChangesApplied"/> = false
/// so the UI can say "N changes are waiting for the apply path" instead of implying they landed.
///
/// MAUI-free: the composition root passes the concrete services; a MAUI-typed adapter lives under
/// Presentation and holds no logic.
/// </summary>
public sealed class CloudSyncBridge : ISyncTransport, ICloudConnector
{
    /// <summary>Cap on how many change pages one cycle pulls (a phone drain must not become a loop).</summary>
    public const int MaxPagesPerCycle = 5;

    /// <summary>Default <c>limit</c> sent to /sync/changes (§5c; the server may clamp lower).</summary>
    public const int DefaultChangesLimit = 200;

    private readonly ILivoraApiPort _port;
    private readonly ICloudApiOptions _options;
    private readonly ICloudAuthContext? _auth;
    private readonly SyncQueue _queue;
    private readonly CloudConnectorStateStore _state;
    private readonly CloudSyncTransport? _real;
    private readonly NoopSyncTransport _noop = new();
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Func<Task>? _restoreSession;

    private CapabilitySnapshotDto? _capabilities;
    private volatile bool _workInFlight;
    private CloudConnectorStatus _last;

    public CloudSyncBridge(
        ILivoraApiPort port,
        ICloudApiOptions options,
        SyncQueue queue,
        CloudConnectorStateStore stateStore,
        ICloudAuthContext? auth = null,
        CloudSyncTransport? realTransport = null,
        ILogger<CloudSyncBridge>? logger = null,
        TimeProvider? timeProvider = null,
        Func<Task>? restoreSession = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _state = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _auth = auth;
        _real = realTransport;
        _log = logger ?? NullLogger<CloudSyncBridge>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _restoreSession = restoreSession;
        _last = ProjectState(BuildEvidence(_state.ReadRecord()));
    }

    public event Action? StatusChanged;

    /// <summary>Capabilities of the last successful probe (null until this launch has probed).</summary>
    public CapabilitySnapshotDto? LastCapabilities => _capabilities;

    // ============================ ISyncTransport ===============================================

    /// <summary>True only when the real transport can complete and confirm a push.</summary>
    public bool IsConfigured => _options.IsConfigured && _real is { IsConfigured: true };

    public string GatewayLabel => IsConfigured ? CloudSyncTransport.Label : _noop.GatewayLabel;

    public Task<SyncPushResult> PushAsync(IReadOnlyList<SyncEnvelope> batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!IsConfigured) return _noop.PushAsync(batch, ct);   // honest no-op: entries stay Pending
        return _real!.PushAsync(batch, ct);
    }

    // ============================ ICloudConnector ==============================================

    public CloudConnectorStatus Status => _last;

    public async Task<CloudConnectorStatus> RefreshStatusAsync(CancellationToken ct = default)
    {
        await EnsureSessionAsync(ct).ConfigureAwait(false);
        var record = _state.ReadRecord();
        var status = await ProjectAsync(record, workInFlight: _workInFlight, ct).ConfigureAwait(false);
        Publish(status);
        return status;
    }

    /// <summary>
    /// The one call that can move the surface off Unverified: a real <c>GET /platform/capabilities</c>.
    /// A network failure is recorded as Offline evidence (not swallowed), and no exception escapes.
    /// </summary>
    public Task<CloudConnectorStatus> ProbeAsync(CancellationToken ct = default) => ProbeCoreAsync(ct);

    private async Task<CloudConnectorStatus> ProbeCoreAsync(CancellationToken ct)
    {
        _workInFlight = true;
        try
        {
            var res = await _port.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            if (res.Ok && res.Value is not null)
            {
                _capabilities = res.Value;
                _state.RecordAttempt(succeeded: true, code: null, reasonKey: null,
                    correlationId: res.CorrelationId, httpStatus: res.Status);
            }
            else
            {
                _capabilities = null;
                _state.RecordAttempt(succeeded: false, code: res.Code, reasonKey: res.ErrorReasonKey,
                    correlationId: res.CorrelationId, httpStatus: res.Status);
            }
        }
        finally
        {
            _workInFlight = false;
        }
        var after = await ProjectAsync(_state.ReadRecord(), workInFlight: false, ct).ConfigureAwait(false);
        Publish(after);
        return after;
    }

    /// <summary>
    /// One honest cycle: push everything Pending (through the queue, which owns the state machine)
    /// and pull one bounded run of changes. The report is assembled from the queue's own
    /// <see cref="DrainReport"/> and the server's own batch/change bodies.
    /// </summary>
    public async Task<CloudSyncCycleReport> SyncNowAsync(CancellationToken ct = default)
    {
        await EnsureSessionAsync(ct).ConfigureAwait(false);
        _workInFlight = true;
        try
        {
            var pendingAtStart = await _queue.PendingCountAsync(ct).ConfigureAwait(false);
            var before = _state.ReadCursor();
            int applied = 0, conflicted = 0, rejected = 0, duplicate = 0, batches = 0;
            string? failureCode = null, failureKey = null, correlationId = null;
            bool reached = false;
            bool confirmed = false;

            if (IsConfigured && pendingAtStart > 0)
            {
                var drain = await _queue.DrainAsync(this, ct).ConfigureAwait(false);
                batches = drain.TransportConfigured ? Math.Max(1, (int)Math.Ceiling(drain.Pushed / (double)SyncQueue.BatchSize)) : 0;
                applied = drain.Synced;
                conflicted = drain.Conflicted;
                // TransportConfigured only says the seam WAS usable; whether the host answered is the
                // transport's own measured fact. Inferring reachability from configuration is exactly
                // how an offline device ends up labelled Failed instead of Offline.
                reached |= _real?.LastAttemptReachedServer ?? false;
                confirmed = drain.TransportConfigured && drain.Synced > 0 && drain.Conflicted == 0 && drain.FailedBatches == 0;
                if (drain.FailedBatches > 0) { failureCode = drain.ErrorCategory; failureKey = "Cloud.Sync.Reason.PushRefused"; }
            }

            // Pull: a session is required (§5c authenticated route); the watermark keeps it incremental.
            int pulled = 0;
            long? after = before.LatestRevision;
            if (_options.IsConfigured && _auth is { HasSession: true })
            {
                var page = await _port.GetSyncChangesAsync(before.LatestRevision, DefaultChangesLimit, ct)
                    .ConfigureAwait(false);
                if (page.Ok && page.Value is not null)
                {
                    reached = true;
                    pulled = page.Value.Changes?.Count ?? 0;
                    after = page.Value.LatestRevision;
                    _state.SaveCursor(page.Value.LatestRevision);
                    // A change page is a GOOD ANSWER (the record's LastGoodAnswerAtUtc is set because
                    // the cycle succeeded) but it is NOT a confirmation of this device's queued work,
                    // so `confirmed` stays exactly as the push left it. The UI's "server confirmed at"
                    // line reads only the confirmation stamp and would otherwise print a lie.
                }
                else if (page.Status > 0)
                {
                    reached = true;
                    failureCode ??= page.Code;
                    failureKey ??= page.ErrorReasonKey;
                    correlationId ??= page.CorrelationId;
                }
                else
                {
                    failureCode ??= page.Code;
                    failureKey ??= page.ErrorReasonKey;
                }
            }

            // One record write per cycle: whatever the attempt proved (or failed to prove). The
            // httpStatus here is a boolean dressed as a status — >0 means "an HTTP answer arrived",
            // which is the only distinction the state machine reads from it (Offline vs Failed).
            _state.RecordAttempt(
                succeeded: failureKey is null,
                code: failureCode,
                reasonKey: failureKey,
                correlationId: correlationId,
                httpStatus: reached ? 200 : 0,
                confirmedOperations: confirmed);

            var report = new CloudSyncCycleReport
            {
                Skipped = !IsConfigured && pulled == 0,
                SkipReasonKey = SkippedReasonKey(),
                PendingAtStart = pendingAtStart,
                Applied = applied,
                Conflicted = conflicted,
                Rejected = rejected,
                Duplicate = duplicate,
                Batches = batches,
                ChangesPulled = pulled,
                RemoteChangesApplied = false,      // Phase 1 has no local-apply path — see class docs
                WatermarkBefore = before.LatestRevision,
                WatermarkAfter = after,
                FailureReasonKey = failureKey,
                FailureCode = failureCode,
                CorrelationId = correlationId,
                ReachedServer = reached,
            };
            _log.LogInformation("cloud-sync cycle pushed={Applied} conflicts={Conflicted} pulled={Pulled} reached={Reached} code={Code}",
                applied, conflicted, pulled, reached, failureCode ?? "-");
            return report;
        }
        finally
        {
            _workInFlight = false;
            Publish(await ProjectAsync(_state.ReadRecord(), workInFlight: false, ct).ConfigureAwait(false));
        }

        static bool failureReached(string? code) => code is not null;
    }

    /// <summary>
    /// Sign-out (delegates to the session manager, which refuses to clear local state until the server
    /// answered). On success the connector record and cursor are wiped: a signed-out device must not
    /// keep claiming "last confirmed".
    /// </summary>
    public async Task<LivoraApiResult<bool>> SignOutAsync(CancellationToken ct = default)
    {
        if (_auth is not CloudSessionManager manager)
            return LivoraApiResult<bool>.TransportFailure(LivoraApiTransportCodes.NotSignedIn);

        var res = await manager.SignOutAsync(ct).ConfigureAwait(false);
        if (res.Ok) _state.Reset();
        Publish(await ProjectAsync(_state.ReadRecord(), workInFlight: false, ct).ConfigureAwait(false));
        return res;
    }

    // ============================ evidence + projection =======================================

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_restoreSession is not null)
        {
            try { await _restoreSession().ConfigureAwait(false); }
            catch (Exception) { /* a broken store reads as signed out, never a crash */ }
        }
        else if (_auth is CloudSessionManager m)
        {
            try { await m.RestoreAsync(ct).ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private CloudConnectorEvidence BuildEvidence(CloudConnectorRecord record)
    {
        int pending = SafeCount(() => _queue.PendingCountAsync(CancellationToken.None).GetAwaiter().GetResult());
        int conflicts = SafeCount(() => _queue.ConflictCountAsync(CancellationToken.None).GetAwaiter().GetResult());
        return new CloudConnectorEvidence
        {
            Configured = _options.IsConfigured,
            ProbeAttempted = record.LastAttemptAtUtc is not null,
            LastAttemptReachedServer = record.LastAttemptReachedServer,
            HasSession = _auth?.HasSession ?? false,
            PendingCount = pending,
            ConflictCount = conflicts,
            LastErrorReasonKey = record.LastErrorReasonKey,
            LastProbeAtUtc = record.LastAttemptAtUtc,
            LastGoodAnswerAtUtc = record.LastGoodAnswerAtUtc,
            LastConfirmedAtUtc = record.LastConfirmedAtUtc,
            Capabilities = _capabilities,
            LastCorrelationId = record.LastCorrelationId,
            TransportLabel = GatewayLabel,
        };
    }

    /// <summary>
    /// The async-safe path (queue counters are Task-returning; the ctor cannot await). The synchronous
    /// <see cref="BuildEvidence"/> is only used for the initial projection, before any drain has run.
    /// </summary>
    private async Task<CloudConnectorStatus> ProjectAsync(CloudConnectorRecord record, bool workInFlight, CancellationToken ct)
    {
        var evidence = new CloudConnectorEvidence
        {
            Configured = _options.IsConfigured,
            ProbeAttempted = record.LastAttemptAtUtc is not null,
            LastAttemptReachedServer = record.LastAttemptReachedServer,
            HasSession = _auth?.HasSession ?? false,
            PendingCount = await _queue.PendingCountAsync(ct).ConfigureAwait(false),
            ConflictCount = await _queue.ConflictCountAsync(ct).ConfigureAwait(false),
            LastErrorReasonKey = record.LastErrorReasonKey,
            LastProbeAtUtc = record.LastAttemptAtUtc,
            LastGoodAnswerAtUtc = record.LastGoodAnswerAtUtc,
            LastConfirmedAtUtc = record.LastConfirmedAtUtc,
            Capabilities = _capabilities,
            LastCorrelationId = record.LastCorrelationId,
            TransportLabel = GatewayLabel,
        };
        return CloudConnectorStateMachine.EvaluateWithWork(evidence, workInFlight);
    }

    private static CloudConnectorStatus ProjectState(CloudConnectorEvidence evidence) =>
        CloudConnectorStateMachine.Evaluate(evidence);

    private void Publish(CloudConnectorStatus status)
    {
        // Value comparison, not reference: a page that repaints on every probe (identical verdict)
        // is a flicker and a wake-up storm; a page that misses a real change is a lie. The record's
        // own equality covers the scalars; the argument array is compared by content.
        if (_last is not null && SameProjection(_last, status)) return;
        _last = status;
        StatusChanged?.Invoke();
    }

    private static bool SameProjection(CloudConnectorStatus a, CloudConnectorStatus b) =>
        a.State == b.State
        && a.StateReasonKey == b.StateReasonKey
        && a.PendingCount == b.PendingCount
        && a.ConflictCount == b.ConflictCount
        && a.LastProbeAtUtc == b.LastProbeAtUtc
        && a.LastGoodAnswerAtUtc == b.LastGoodAnswerAtUtc
        && a.LastConfirmedAtUtc == b.LastConfirmedAtUtc
        && a.LastCorrelationId == b.LastCorrelationId
        && a.TransportLabel == b.TransportLabel
        && a.ReasonArgs.Count == b.ReasonArgs.Count
        && !a.ReasonArgs.Where((v, i) => !Equals(v, b.ReasonArgs[i])).Any();

    private string? SkippedReasonKey() => _options.IsConfigured switch
    {
        false => "Cloud.Sync.Reason.NotConfigured",
        true when _auth is null || !_auth.HasSession => "Cloud.Sync.Reason.NoSession",
        true when _real is null || !_real.IsConfigured => "Cloud.Sync.Reason.NoPayloadSource",
        _ => null,
    };

    private static int SafeCount(Func<int> read)
    {
        try { return read(); }
        catch (Exception) { return 0; }   // an unreadable queue must not fabricate counts; 0 = "no evidence of work"
    }
}
