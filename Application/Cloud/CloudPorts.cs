namespace LIVORA.Application.Cloud;

/// <summary>
/// WAVE 4 P1-D — the seams the cloud layer needs beyond the API port itself. Each one here exists
/// because at least two real implementations can occupy it (or because it breaks a cycle), never as
/// ceremony: the contract law (§0.6) forbids empty indirection.
/// </summary>

/// <summary>
/// Read-side view of the cloud configuration. The composition root decides where the base URL comes
/// from (a persisted advanced-settings file, a build-time default); the seam only asks whether one
/// exists, because that single fact is what flips the whole product between <c>Unconfigured</c> and
/// everything else. A configured URL is NOT a verified backend — verification needs a real round-trip.
/// </summary>
public interface ICloudApiOptions
{
    /// <summary>True when an absolute http(s) base URL is present.</summary>
    bool IsConfigured { get; }

    /// <summary>The normalized base URL (no trailing slash), or null when unconfigured.</summary>
    string? BaseUrl { get; }

    /// <summary>Per-request timeout the port applies (a value, so tests can pin it).</summary>
    TimeSpan Timeout { get; }

    /// <summary>Machine tag of where the URL came from ("none" | "store" | "build-default").</summary>
    string SourceLabel { get; }
}

/// <summary>
/// The token store: the only place a session's bearer + refresh tokens may live. Implementations
/// persist through a platform-protected facility (<c>ISecureStorageService</c>), never a plain file,
/// and never log what they are handed.
/// </summary>
public interface ICloudTokenStore
{
    /// <summary>Load the stored session, or null when this device holds none.</summary>
    Task<CloudSession?> ReadAsync(CancellationToken ct = default);

    /// <summary>Persist a session (overwrite). Called on login/register and after every rotation.</summary>
    Task WriteAsync(CloudSession session, CancellationToken ct = default);

    /// <summary>Erase every credential this store holds (logout, revoked family, account deletion).</summary>
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>
    /// True only when the backing store provides a real OS at-rest boundary. The UI reads this and
    /// says "encrypted" nowhere else — the Wave 3c rule for the token store, unchanged.
    /// </summary>
    bool IsOsBacked { get; }
}

/// <summary>
/// The device's cloud session: what the token store holds and what the auth context exposes. The
/// token strings are never surfaced to the UI, never placed in an exception message, and never
/// written to a log line (privacy law §0.5); <see cref="RefreshTokenFingerprint"/> is the only
/// derivable identifier, and it is a one-way hash so a log line cannot be replayed as a credential.
/// </summary>
public sealed record CloudSession
{
    public required string UserId { get; init; }
    public required string SessionId { get; init; }
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>When this device received the session (client-side fact, not the server's claim).</summary>
    public DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>True when the bearer is at/past its expiry (or inside the safety margin).</summary>
    public bool IsAccessExpired(DateTimeOffset nowUtc, TimeSpan margin) =>
        nowUtc + margin >= ExpiresAtUtc;

    /// <summary>
    /// Short one-way tag of the refresh token — for diagnostics that must correlate a rotation
    /// without ever printing a credential. 12 hex chars of SHA-256; not a secret, not searchable.
    /// <c>[JsonIgnore]</c>: a get-only property would otherwise be serialized into the stored blob,
    /// and the at-rest shape must stay exactly the session's own fields.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RefreshTokenFingerprint => CloudSession.Fingerprint(RefreshToken);

    public static string Fingerprint(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(refreshToken));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }
}

/// <summary>
/// The seam the API port uses to authorize itself — deliberately small and one-directional so the
/// session manager can depend on the port while the port depends only on this. A port that needs a
/// token does not get to decide how the session is stored or when it rotates.
/// </summary>
public interface ICloudAuthContext
{
    /// <summary>
    /// True when this object KNOWS a session exists (in memory). It is a fast read of the last
    /// resolved state — it starts false before the store has been read and never guesses true;
    /// surfaces that must be exact await <see cref="GetAccessTokenAsync"/> instead.
    /// </summary>
    bool HasSession { get; }

    /// <summary>
    /// The current bearer token (loading the stored session on first use), or null when this device
    /// holds none. May be an expired bearer — deciding that is the server's job and the port's 401
    /// path, not a guess made here. Never logged, never echoed into an error body.
    /// </summary>
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Rotate the access token using the stored refresh token. True when a fresh bearer is now
    /// available. A false result means the session is gone (the implementation clears it itself) —
    /// callers must not retry in a loop.
    /// </summary>
    Task<bool> TryRefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Called by the port when the server rejected the bearer (§5c 401 codes). The implementation
    /// decides: a rejected-but-refreshable session is refreshed, a revoked/expired family is dropped
    /// and the reason recorded. This never throws.
    /// </summary>
    void HandleAuthRejected(string? code);

    /// <summary>Raised whenever the session changed (sign-in, rotation, sign-out) so status surfaces re-render.</summary>
    event Action? SessionChanged;
}

/// <summary>
/// Where a queued local change finds the bytes to upload. Wave 3c's <c>SyncQueue</c> stores only
/// hash + size by design, so pushing content needs a store-side reader; wiring one is a real
/// capability and its absence must keep the transport <b>not configured</b> rather than push an
/// empty payload and mark the row synced (the failure mode the honesty rules single out).
/// </summary>
public interface ICloudSyncPayloadSource
{
    /// <summary>Machine tag ("local-catalog" | "none").</summary>
    string Label { get; }

    /// <summary>True when this source can actually produce payloads (the false case disables push).</summary>
    bool CanProvidePayloads { get; }

    /// <summary>
    /// Full JSON of one entity, or null when it no longer exists locally (a deleted row: the queue
    /// entry is dropped rather than pushed as an empty object).
    /// </summary>
    Task<string?> GetPayloadAsync(string entityKind, string entityId, CancellationToken ct = default);
}

/// <summary>What one drain cycle actually did. Every number came from a real request or a real queue.</summary>
public sealed record CloudSyncCycleReport
{
    /// <summary>Nothing moved and the seam says why (no URL, no session, no payload source).</summary>
    public bool Skipped { get; init; }

    /// <summary>Localization key explaining a skip (never prose; ships in both languages).</summary>
    public string? SkipReasonKey { get; init; }

    public int PendingAtStart { get; init; }
    public int Applied { get; init; }
    public int Conflicted { get; init; }
    public int Rejected { get; init; }
    public int Duplicate { get; init; }
    public int Batches { get; init; }

    /// <summary>Changes pulled from the server (recorded, not applied — see <see cref="RemoteChangesApplied"/>).</summary>
    public int ChangesPulled { get; init; }

    /// <summary>
    /// Always false in Phase 1: the client has no local-apply path yet, so pulled changes are
    /// counted and reported, never written over the user's data. A merge that flips this without
    /// implementing apply would silently destroy local edits.
    /// </summary>
    public bool RemoteChangesApplied { get; init; }

    public long? WatermarkBefore { get; init; }
    public long? WatermarkAfter { get; init; }

    /// <summary>Code/reason of the first failure this cycle hit (null = nothing failed).</summary>
    public string? FailureReasonKey { get; init; }
    public string? FailureCode { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>True when the cycle proved the tunnel (a real HTTP answer) even if it moved nothing.</summary>
    public bool ReachedServer { get; init; }

    /// <summary>The honest one-liner for "did this cycle change anything?": false = nothing moved.</summary>
    public bool MovedAnything => Applied + Conflicted + Rejected + Duplicate + ChangesPulled > 0;
}

/// <summary>
/// The UI-facing cloud seam: the status the connector chip renders, the probe that updates it, and
/// the two user actions (sync now / sign out). Implementing it is the bridge in Infrastructure/Cloud;
/// no ViewModel may reach past this to an <c>HttpClient</c> (§6).
/// </summary>
public interface ICloudConnector
{
    /// <summary>The last projected status (pure read of recorded evidence — computes nothing new).</summary>
    CloudConnectorStatus Status { get; }

    /// <summary>Recompute the status from the live stores (queue counts, session, config, capabilities).</summary>
    Task<CloudConnectorStatus> RefreshStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Real round-trip probe: <c>GET /platform/capabilities</c>. This is the only path that may move
    /// the surface out of <c>Unverified</c>, and it can fail exactly like any other request.
    /// </summary>
    Task<CloudConnectorStatus> ProbeAsync(CancellationToken ct = default);

    /// <summary>Drain the queue and pull changes once. Returns what actually happened.</summary>
    Task<CloudSyncCycleReport> SyncNowAsync(CancellationToken ct = default);

    /// <summary>Sign this device out (server first: local state only clears on a real answer).</summary>
    Task<LivoraApiResult<bool>> SignOutAsync(CancellationToken ct = default);

    /// <summary>Raised after any state that the status projection reads has changed.</summary>
    event Action? StatusChanged;
}

/// <summary>
/// Persisted watermark of the incremental pull (<c>GET /sync/changes?since=</c>). A file, not memory,
/// so a restart does not re-pull the world — and a cursor that was never written reads as
/// "never synced", which is what keeps the first status honest.
/// </summary>
public sealed record CloudSyncCursor
{
    /// <summary>Latest revision the server confirmed and this device recorded (null = never pulled).</summary>
    public long? LatestRevision { get; init; }

    /// <summary>UTC stamp of the last successful pull (null = never).</summary>
    public DateTimeOffset? LastPullAtUtc { get; init; }

    /// <summary>True when this device has ever completed a pull — the pull path's "never" state.</summary>
    public bool HasPulled => LatestRevision is not null;
}

/// <summary>
/// Recorded history of the connector: the facts the state machine projects, persisted so a restart
/// cannot turn "the server confirmed at 14:02" into "never verified" (which would be its own lie).
/// </summary>
public sealed record CloudConnectorRecord
{
    /// <summary>UTC stamp of the last request that received an HTTP answer (any status).</summary>
    public DateTimeOffset? LastAttemptAtUtc { get; init; }

    /// <summary>True when that last attempt got an answer (vs. never leaving the device).</summary>
    public bool LastAttemptReachedServer { get; init; }

    /// <summary>Localization key of the last failure (null when the last attempt was fine).</summary>
    public string? LastErrorReasonKey { get; init; }

    /// <summary>Correlation id of the last failed attempt (never the server's prose).</summary>
    public string? LastCorrelationId { get; init; }

    /// <summary>UTC stamp of the last attempt the server answered successfully (any endpoint).</summary>
    public DateTimeOffset? LastGoodAnswerAtUtc { get; init; }

    /// <summary>UTC stamp of the last time the server CONFIRMED queued operations (/sync/batch).</summary>
    public DateTimeOffset? LastConfirmedAtUtc { get; init; }

    /// <summary>The §5c machine code of the last failure (kept for the diagnostics sheet).</summary>
    public string? LastCode { get; init; }
}
