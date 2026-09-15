using LIVORA.Application.Cloud;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Infrastructure.Cloud;

/// <summary>
/// WAVE 4 P1-D — the session manager: the one writer of <see cref="CloudTokenStore"/> content and
/// the <see cref="ICloudAuthContext"/> the API port authorizes itself from.
///
/// IT OWNS EXACTLY THESE DECISIONS (and nothing else):
/// <list type="bullet">
///   <item><b>sign in</b> — email/password, register, or a Google id-token: call the port, and the
///     store is written ONLY from a real 200/201 body. A failed login clears nothing (an existing
///     session from another login attempt is not this attempt's to erase) and produces the §5c code
///     so the UI localizes "wrong credentials" vs "locked" vs "rate limited" differently;</item>
///   <item><b>rotation</b> — §5c rotates the refresh token on every refresh. <see cref="TryRefreshAsync"/>
///     serializes refreshes behind one gate (N expiring requests must not mint N sessions and orphan
///     N-1 refresh tokens — a rotated-out token replayed by a racing request is a THEFT signal
///     server-side, so a client race here would lock the user's own account out);</item>
///   <item><b>sign out</b> — server first. Only a real 204 clears the local session; an offline
///     sign-out reports the failure and KEEPS the session (dropping it locally would strand the
///     server-side session with no way to revoke it, and would show "signed out" while the account
///     still has an active session — §0.1 in both directions);</item>
///   <item><b>theft/revocation</b> — when the server answers token_revoked (or token_expired on a
///     refresh), the family is gone: the local session is erased, the reason is recorded, and the
///     reason key is what the connector renders.</item>
/// </list>
/// Access tokens never leave this class except through <see cref="AccessToken"/> (read by the port at
/// request-build time). No method returns a token to the UI, and no log line here carries one —
/// the only session-derived string this class exposes anywhere is the one-way fingerprint.
/// </summary>
public sealed class CloudSessionManager : ICloudAuthContext
{
    /// <summary>Refresh this long before the bearer actually expires, so a request never starts on a dead token.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly ILivoraApiPort _port;
    private readonly CloudTokenStore _store;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CloudSession? _cached;
    private string? _lastSignOutReasonKey;

    public CloudSessionManager(
        ILivoraApiPort port,
        CloudTokenStore store,
        ILogger<CloudSessionManager>? logger = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = logger ?? NullLogger<CloudSessionManager>.Instance;
    }

    public event Action? SessionChanged;

    public bool HasSession => Cached() is not null;

    /// <summary>
    /// The bearer the port attaches. Resolves the stored session on first use so a request issued
    /// before the composition root awaited <see cref="RestoreAsync"/> still carries the right token
    /// (and an honest null when there is none).
    /// </summary>
    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        => Task.FromResult(Cached()?.AccessToken);

    /// <summary>Machine tag of why the last sign-out refused to complete (null = nothing pending).</summary>
    public string? LastSignOutReasonKey => _lastSignOutReasonKey;

    /// <summary>Load the stored session into memory (composition root calls this at startup; safe to call twice).</summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        _cached = await _store.ReadAsync(ct).ConfigureAwait(false);
        if (_cached is not null) SessionChanged?.Invoke();
    }

    // ============================ sign-in paths ================================================

    public async Task<CloudAuthOutcome> SignInWithPasswordAsync(
        string email, string password, string? deviceLabel = null, string? platform = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(password);
        var res = await _port.LoginAsync(new LoginRequest(email, password, deviceLabel, platform), ct)
            .ConfigureAwait(false);
        return await CommitAsync(res, "login", ct).ConfigureAwait(false);
    }

    public async Task<CloudAuthOutcome> RegisterAsync(
        string email, string password, string? displayName = null, string? locale = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(password);
        var res = await _port.RegisterAsync(new RegisterRequest(email, password, displayName, locale), ct)
            .ConfigureAwait(false);
        return await CommitAsync(res, "register", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Google sign-in: the client supplies an ID token it obtained from the platform flow. With no
    /// <c>Identity:Google:ClientId</c> on the host the server answers 503 <c>provider_unconfigured</c>
    /// (§5c) — that code reaches the UI as "Google sign-in is not configured on this server yet",
    /// never as a generic failure and never as a success.
    /// </summary>
    public async Task<CloudAuthOutcome> SignInWithGoogleAsync(
        string idToken, string? deviceLabel = null, string? platform = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);
        var res = await _port.LoginWithGoogleAsync(new GoogleLoginRequest(idToken, deviceLabel, platform), ct)
            .ConfigureAwait(false);
        return await CommitAsync(res, "google", ct).ConfigureAwait(false);
    }

    private async Task<CloudAuthOutcome> CommitAsync(
        LivoraApiResult<AuthTokensDto> res, string via, CancellationToken ct)
    {
        if (!res.Ok || res.Value is null)
            return CloudAuthOutcome.Failed(res.Code, res.ErrorReasonKey, res.CorrelationId, res.FieldErrors);

        var tokens = res.Value;
        var session = new CloudSession
        {
            UserId = tokens.UserId,
            SessionId = tokens.SessionId,
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            ExpiresAtUtc = tokens.ExpiresAtUtc,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
        };
        await _store.WriteAsync(session, ct).ConfigureAwait(false);
        _cached = session;
        _lastSignOutReasonKey = null;
        // cid + uid only; the tokens themselves are never logged even in structured form (§0.5).
        _log.LogInformation("cloud-auth via={Via} cid={CorrelationId} uid={UserId} session=opened",
            via, res.CorrelationId, tokens.UserId);
        SessionChanged?.Invoke();
        return CloudAuthOutcome.Success(session);
    }

    // ============================ rotation =======================================================

    /// <summary>
    /// The port calls this when the server rejected the bearer. Serialized so concurrent 401s share
    /// ONE refresh: the winner stores the rotated pair, the losers find a fresh bearer already cached
    /// and re-send without a second call. A failed refresh clears the session (the family is revoked
    /// or expired server-side — there is nothing to retry against) and returns false.
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = _cached ??= await _store.ReadAsync(ct).ConfigureAwait(false);
            if (session is null) return false;

            // Another waiter may have rotated while this one queued; a fresher bearer means done.
            if (!session.IsAccessExpired(DateTimeOffset.UtcNow, RefreshMargin)) return true;

            var res = await _port.RefreshAsync(new RefreshTokenRequest(session.RefreshToken), ct)
                .ConfigureAwait(false);
            if (!res.Ok || res.Value is null)
            {
                var revoked = res.Code is LivoraApiCodes.TokenRevoked or LivoraApiCodes.TokenExpired
                              or LivoraApiCodes.InvalidCredentials;
                if (revoked)
                {
                    _log.LogInformation("cloud-auth cid={CorrelationId} session=dropped code={Code}",
                        res.CorrelationId, res.Code);
                    await DropLocalAsync(ct).ConfigureAwait(false);
                }
                // A network failure keeps the session: the family may be alive and the next attempt
                // (or the next app launch) can refresh. Deleting local state on a dropped packet
                // would sign the user out because their train went through a tunnel.
                return false;
            }

            var rotated = session with
            {
                UserId = res.Value.UserId,
                SessionId = res.Value.SessionId,
                AccessToken = res.Value.AccessToken,
                RefreshToken = res.Value.RefreshToken,   // ROTATED — the old value must not survive on disk
                ExpiresAtUtc = res.Value.ExpiresAtUtc,
                ReceivedAtUtc = DateTimeOffset.UtcNow,
            };
            await _store.WriteAsync(rotated, ct).ConfigureAwait(false);
            _cached = rotated;
            _log.LogInformation("cloud-auth cid={CorrelationId} session=rotated fp={Fingerprint}",
                res.CorrelationId, rotated.RefreshTokenFingerprint);   // one-way tag, never the token
            SessionChanged?.Invoke();
            return true;
        }
        finally { _refreshGate.Release(); }
    }

    public void HandleAuthRejected(string? code)
    {
        // token_revoked on a bearer (not just on refresh) means the family is gone server-side.
        if (code is LivoraApiCodes.TokenRevoked or LivoraApiCodes.AccountLocked)
            _ = DropLocalAsync(CancellationToken.None);
    }

    // ============================ sign-out =======================================================

    /// <summary>
    /// Logout = server first (§5c revokes THIS session only). Local state is erased only after the
    /// server confirmed with 204; offline, the session is kept and the reason is surfaced, because
    /// pretending sign-out succeeded would hide an open session from a user who believes they closed it.
    /// <paramref name="forceLocalSignOut"/> is the explicit escape hatch the UI offers after that
    /// message ("forget this device anyway") — a user decision, never an automatic path.
    /// </summary>
    public async Task<LivoraApiResult<bool>> SignOutAsync(CancellationToken ct = default, bool forceLocalSignOut = false)
    {
        if (_cached is null && await _store.ReadAsync(ct).ConfigureAwait(false) is null)
        {
            _lastSignOutReasonKey = null;
            return LivoraApiResult<bool>.Success(true, status: 200);   // already signed out is signed out
        }

        var res = await _port.LogoutAsync(ct).ConfigureAwait(false);
        if (res.Ok)
        {
            await DropLocalAsync(ct).ConfigureAwait(false);
            _lastSignOutReasonKey = null;
            _log.LogInformation("cloud-auth cid={CorrelationId} session=closed", res.CorrelationId);
            return res;
        }

        if (forceLocalSignOut)
        {
            await DropLocalAsync(ct).ConfigureAwait(false);
            _lastSignOutReasonKey = "Cloud.Auth.SignOut.ForcedLocally";
            return res;
        }

        _lastSignOutReasonKey = res.ErrorReasonKey;
        return res;
    }

    private async Task DropLocalAsync(CancellationToken ct)
    {
        await _store.ClearAsync(ct).ConfigureAwait(false);
        _cached = null;
        SessionChanged?.Invoke();
    }

    private CloudSession? Cached()
    {
        // Lazy first read so the composition root does not have to await RestoreAsync before the
        // port can authorize its first request (startup ordering across lanes is a crash waiting
        // to happen). ReadAsync is cheap and cached afterwards.
        if (_cached is not null) return _cached;
        try
        {
            _cached = _store.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception) { _cached = null; }
        return _cached;
    }
}

/// <summary>
/// What a sign-in attempt produced. A value, never an exception: the register screen must be able to
/// show "email already registered" / "invalid credentials" / "account locked" / "offline" from one
/// switch over <see cref="Code"/> — each with its own §5c machine code and localization key.
/// </summary>
public sealed class CloudAuthOutcome
{
    public bool Succeeded { get; private init; }
    public CloudSession? Session { get; private init; }
    public string? Code { get; private init; }
    /// <summary>Localization key for the failure (bilingual by key, never server prose).</summary>
    public string ReasonKey { get; private init; } = "";
    public string? CorrelationId { get; private init; }
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; private init; }

    /// <summary>The field-error keys localized per field (validation_failed payload in §5c).</summary>
    public IReadOnlyDictionary<string, string[]>? Errors => FieldErrors;

    public static CloudAuthOutcome Success(CloudSession session) =>
        new() { Succeeded = true, Session = session, ReasonKey = "" };

    public static CloudAuthOutcome Failed(
        string? code, string reasonKey, string? correlationId,
        IReadOnlyDictionary<string, string[]>? fieldErrors) =>
        new()
        {
            Succeeded = false,
            Code = code,
            ReasonKey = reasonKey,
            CorrelationId = correlationId,
            FieldErrors = fieldErrors,
        };
}
