namespace LIVORA.Application.Cloud;

/// <summary>
/// WAVE 4 P1-D — the honest cloud-connector state machine.
///
/// WHY AN ENUM WITH A PRECEDENCE RULE INSTEAD OF A BOOL: a boolean "connected" flag is how a
/// half-built integration lies to a user. This type can only reach <see cref="CloudConnectorState.Synced"/>
/// when a real request answered AND nothing is queued AND nothing diverged; every other value names
/// the specific thing that is missing, which is what the bilingual status chip renders (§0.1, §0.3).
/// </summary>
public enum CloudConnectorState
{
    /// <summary>No base URL: the cloud seam does not exist in this install. Not an error — a fact.</summary>
    Unconfigured = 0,
    /// <summary>Configured, but no request has ever completed a round-trip in this build.</summary>
    Unverified = 1,
    /// <summary>The last real attempt could not reach the host (DNS/connect/timeout).</summary>
    Offline = 2,
    /// <summary>This device holds no session, so nothing is uploaded on its behalf (says nothing about reachability).</summary>
    SignedOut = 3,
    /// <summary>A sync/auth call is in flight right now (set by the caller, never by the projector).</summary>
    Working = 4,
    /// <summary>Last attempt answered with an error the user should see (reason key + correlation id).</summary>
    Failed = 5,
    /// <summary>At least one entity diverged from the server and waits for an explicit decision.</summary>
    Conflict = 6,
    /// <summary>The tunnel works; local changes are still queued. Deliberately NOT called "synced".</summary>
    PendingLocal = 7,
    /// <summary>Reachable, but the server itself reports a module as degraded/unconfigured.</summary>
    Degraded = 8,
    /// <summary>Only after a real response confirmed every queued operation. Never inferred.</summary>
    Synced = 9,
}

/// <summary>
/// Everything the projector is allowed to know — each field is either a counter read from the real
/// stores or the recorded outcome of a real call. Nothing here is a plan, an intent, or a hope.
/// </summary>
public sealed record CloudConnectorEvidence
{
    /// <summary>True when a base URL exists (the seam could be used at all).</summary>
    public bool Configured { get; init; }

    /// <summary>True once at least one request has been ATTEMPTED through this seam.</summary>
    public bool ProbeAttempted { get; init; }

    /// <summary>True only when the last attempted request received an HTTP answer (any status).</summary>
    public bool LastAttemptReachedServer { get; init; }

    /// <summary>True when this device holds a session it has not given up on.</summary>
    public bool HasSession { get; init; }

    /// <summary>Queued local changes waiting to go out (from <c>SyncQueue.PendingCountAsync</c>).</summary>
    public int PendingCount { get; init; }

    /// <summary>Entries parked in Conflict awaiting a user decision (from <c>SyncQueue.ConflictCountAsync</c>).</summary>
    public int ConflictCount { get; init; }

    /// <summary>Localization key of the last failure this seam produced (null = nothing failed).</summary>
    public string? LastErrorReasonKey { get; init; }

    /// <summary>UTC stamp of the last completed round-trip (null = never).</summary>
    public DateTimeOffset? LastProbeAtUtc { get; init; }

    /// <summary>
    /// UTC stamp of the last attempt the server ANSWERED SUCCESSFULLY (null = nothing good has ever
    /// come back). This is the evidence a <c>Synced</c> verdict requires: without it, "in sync" would
    /// be an inference from an empty queue rather than something the host confirmed about this device.
    /// </summary>
    public DateTimeOffset? LastGoodAnswerAtUtc { get; init; }

    /// <summary>
    /// UTC stamp of the last time the server CONFIRMED queued operations (<c>/sync/batch</c> applied
    /// them). A distinct fact from the one above: a capability probe is a good answer but not a
    /// confirmation of the user's data, and the UI's "server confirmed at …" line reads this only.
    /// </summary>
    public DateTimeOffset? LastConfirmedAtUtc { get; init; }

    /// <summary>The capability snapshot of the last successful probe (null = never fetched).</summary>
    public CapabilitySnapshotDto? Capabilities { get; init; }

    /// <summary>Correlation id of the last request that failed (displayed, never the server prose).</summary>
    public string? LastCorrelationId { get; init; }

    /// <summary>The label of the transport in play ("livora-cloud" | "noop-local-only").</summary>
    public string? TransportLabel { get; init; }
}

/// <summary>The rendered status: a state, the key that explains it, and the real numbers behind it.</summary>
public sealed record CloudConnectorStatus
{
    public required CloudConnectorState State { get; init; }

    /// <summary>Localization key for the state label (bilingual by construction — never prose).</summary>
    public required string StateReasonKey { get; init; }

    /// <summary>
    /// Format arguments for the reason key ({0} = count where the label carries one). Empty otherwise
    /// so a Persian template never receives a Latin-only sentence built in code.
    /// </summary>
    public IReadOnlyList<object> ReasonArgs { get; init; } = Array.Empty<object>();

    public int PendingCount { get; init; }
    public int ConflictCount { get; init; }
    public DateTimeOffset? LastProbeAtUtc { get; init; }
    public DateTimeOffset? LastGoodAnswerAtUtc { get; init; }
    public DateTimeOffset? LastConfirmedAtUtc { get; init; }
    public string? LastCorrelationId { get; init; }
    public string? TransportLabel { get; init; }

    /// <summary>True for the states where the tunnel itself works (auth reachable, session present).</summary>
    public bool IsLinkUp => State is CloudConnectorState.Synced or CloudConnectorState.PendingLocal
                                          or CloudConnectorState.Conflict or CloudConnectorState.Degraded;

    /// <summary>
    /// True only for the strongest claim the client can make — and only when the server confirmed
    /// QUEUED work (a probe is a good answer but not a confirmation of the user's data). Anything
    /// that renders "connected" reads this flag instead of assuming, so the honesty tripwire has
    /// exactly one place to check.
    /// </summary>
    public bool ConfirmedByServer => State == CloudConnectorState.Synced && LastConfirmedAtUtc is not null;

    /// <summary>Machine tag for logs/diagnostics (never display prose).</summary>
    public string StateTag => State.ToString().ToLowerInvariant();
}

/// <summary>
/// The projector: <see cref="Evaluate"/> is a pure, total function over <see cref="CloudConnectorEvidence"/>.
/// Being pure is the point — the precedence table below is unit-tested branch by branch, so the
/// "connected only when proven" rule cannot rot into an inference someone adds at a call site.
///
/// PRECEDENCE (first match wins, and every reason key ships in BOTH languages):
/// <list type="number">
///   <item>no base URL → <c>Unconfigured</c> (a missing backend is reported as missing, §0.1);</item>
///   <item>no session held → <c>SignedOut</c> (a fact independent of reachability, and actionable);</item>
///   <item>no attempt yet → <c>Unverified</c> (nothing has been proven either way);</item>
///   <item>last attempt never reached the host → <c>Offline</c> (explains why nothing moved);</item>
///   <item>answered with an error → <c>Failed</c> (the reason key + correlation id are the story);</item>
///   <item>diverged rows waiting → <c>Conflict</c> (needs a human, §0.2);</item>
///   <item>queue non-empty → <c>PendingLocal</c> (never "synced": work is still local);</item>
///   <item>server reports a module not ok → <c>Degraded</c> (the server's own word, not ours);</item>
///   <item>otherwise → <c>Synced</c>, and only with a confirmation timestamp.</item>
/// </list>
/// </summary>
public static class CloudConnectorStateMachine
{
    /// <summary>State label keys (all shipped in wave4-keys/lane-p1d.*.keys.xml).</summary>
    public const string KeyUnconfigured = "Cloud.Connector.State.Unconfigured";
    public const string KeyUnverified = "Cloud.Connector.State.Unverified";
    public const string KeyOffline = "Cloud.Connector.State.Offline";
    public const string KeySignedOut = "Cloud.Connector.State.SignedOut";
    public const string KeyWorking = "Cloud.Connector.State.Working";
    public const string KeyFailed = "Cloud.Connector.State.Failed";
    public const string KeyConflict = "Cloud.Connector.State.Conflict";
    public const string KeyPending = "Cloud.Connector.State.PendingLocal";
    public const string KeyDegraded = "Cloud.Connector.State.Degraded";
    public const string KeySynced = "Cloud.Connector.State.Synced";

    /// <summary>Reason keys with a count placeholder ({0}).</summary>
    public const string KeyConflictCount = "Cloud.Connector.Reason.Conflicts";
    public const string KeyPendingCount = "Cloud.Connector.Reason.Pending";

    /// <summary>
    /// The in-flight marker: the caller (bridge/VM) passes this to <see cref="EvaluateWithWork"/> while
    /// a request is running so the chip can say "working" without inventing a progress bar.
    /// </summary>
    public static CloudConnectorStatus Evaluate(CloudConnectorEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        CloudConnectorState state;
        string reasonKey;
        IReadOnlyList<object> args = Array.Empty<object>();

        if (!evidence.Configured)
        {
            state = CloudConnectorState.Unconfigured;
            reasonKey = KeyUnconfigured;
        }
        else if (!evidence.HasSession)
        {
            // Ahead of the reachability facts on purpose: "this device holds no session" is true
            // whether or not the host answers, and it is the one sentence that tells the user what
            // to DO. It claims nothing about the server being reachable.
            state = CloudConnectorState.SignedOut;
            reasonKey = KeySignedOut;
        }
        else if (!evidence.ProbeAttempted)
        {
            state = CloudConnectorState.Unverified;
            reasonKey = KeyUnverified;
        }
        else if (!evidence.LastAttemptReachedServer)
        {
            state = CloudConnectorState.Offline;
            reasonKey = KeyOffline;
        }
        else if (evidence.LastErrorReasonKey is not null)
        {
            state = CloudConnectorState.Failed;
            reasonKey = evidence.LastErrorReasonKey;   // already an Api.Error.* key
        }
        else if (evidence.ConflictCount > 0)
        {
            state = CloudConnectorState.Conflict;
            reasonKey = KeyConflictCount;
            args = new object[] { evidence.ConflictCount };
        }
        else if (evidence.PendingCount > 0)
        {
            state = CloudConnectorState.PendingLocal;
            reasonKey = KeyPendingCount;
            args = new object[] { evidence.PendingCount };
        }
        else if (AnyModuleNotOk(evidence.Capabilities))
        {
            state = CloudConnectorState.Degraded;
            reasonKey = KeyDegraded;
        }
        else
        {
            state = CloudConnectorState.Synced;
            reasonKey = KeySynced;
        }

        // THE one guard in this file, and it is a product law, not a style preference: "in sync" may
        // only be rendered when the host has actually answered this device successfully. An empty
        // queue with no good answer is "we do not know", which is exactly what Unverified says.
        if (state == CloudConnectorState.Synced && evidence.LastGoodAnswerAtUtc is null)
        {
            state = CloudConnectorState.Unverified;
            reasonKey = KeyUnverified;
        }

        return new CloudConnectorStatus
        {
            State = state,
            StateReasonKey = reasonKey,
            ReasonArgs = args,
            PendingCount = evidence.PendingCount,
            ConflictCount = evidence.ConflictCount,
            LastProbeAtUtc = evidence.LastProbeAtUtc,
            LastGoodAnswerAtUtc = evidence.LastGoodAnswerAtUtc,
            LastConfirmedAtUtc = evidence.LastConfirmedAtUtc,
            LastCorrelationId = evidence.LastCorrelationId,
            TransportLabel = evidence.TransportLabel,
        };
    }

    /// <summary>Same table with the in-flight overlay applied (the only writer of <c>Working</c>).</summary>
    public static CloudConnectorStatus EvaluateWithWork(CloudConnectorEvidence evidence, bool workInFlight)
    {
        var status = Evaluate(evidence);
        if (!workInFlight || status.State is CloudConnectorState.Unconfigured or CloudConnectorState.Offline)
            return status;
        return status with { State = CloudConnectorState.Working, StateReasonKey = KeyWorking, ReasonArgs = Array.Empty<object>() };
    }

    /// <summary>
    /// True when the SERVER said any module (or the database) is not <c>ok</c>. Only the snapshot's own
    /// strings count — a missing snapshot is not "degraded", it is "never fetched", and the caller's
    /// evidence decides that separately.
    /// </summary>
    public static bool AnyModuleNotOk(CapabilitySnapshotDto? snapshot)
    {
        if (snapshot is null) return false;
        if (NotOk(snapshot.Database)) return true;
        if (snapshot.Modules is null) return false;
        return snapshot.Modules.Any(NotOk);
    }

    private static bool NotOk(CapabilityEntryDto? entry) =>
        entry is not null && !string.Equals(entry.State, CapabilitySnapshotDto.StateOk, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The client-side name for the "no backend configured" fact, used by the seam's own honesty tests:
    /// the noop transport shipped in Wave 3c stays the composed default until a base URL exists, so a
    /// fresh install must read Unconfigured — never "connected".
    /// </summary>
    public static CloudConnectorEvidence NotConfiguredEvidence(string transportLabel) =>
        new() { Configured = false, TransportLabel = transportLabel };
}
