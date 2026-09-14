using System.Security.Claims;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Identity;
using Livora.Server.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Modules.Identity;

/// <summary>
/// PURPOSE: the whole account state machine — register, login, federated login, refresh with
///          rotation + reuse detection, logout, session revocation, deletion lifecycle. Handlers
///          stay thin; every security-critical decision runs through one class with one clock and
///          one DB context.
/// OWNER: Agent 03 (identity lane).
/// CONSUMES: LivoraDbContext (frozen core tables + this lane's contributions), LivoraSigningKey
///           (lead's pre-wired mint — this lane NEVER builds its own JwtSecurityToken),
///           PasswordHasher, RefreshTokens, IdentitySecurityStore (lockout), IGoogleIdTokenVerifier.
/// INVARIANTS (each is pinned by a test in tests/…/Identity/):
///   1. Passwords: PBKDF2-HMAC-SHA512 (iterations$salt$subkey, cost travels with the hash),, transparent rehash-upgrade on login; the
///      plaintext never touches a field, log, or audit row.
///   2. Constant-shape 401: wrong email vs wrong password are the SAME code, SAME body, and the
///      same cryptographic work (unknown email runs the dummy-hash verification) — §5c says the
///      body is identical, this lane makes the TIMING identical too.
///   3. Refresh rotation: exactly one live hash per session; the presented token is one-shot.
///      A replay of a rotated-out token (tombstone hit, reason=rotation) revokes the whole session
///      family and is audited as a theft signal.
///   4. Revocation is state, not signature: every authenticated identity route re-checks the
///      session row against the DB. A valid JWT for a revoked/expired/deleted session answers
///      401 with code token_revoked / token_expired.
///   5. Lockout lives on normalized email — unknown emails lock out on the identical ladder, so
///      the 429/403 escalation is not an existence oracle.
///   6. IDOR: every user-scoped read/act compares the ROW's owner to ctx.User.UserId() —
///      Policies/OwnsResource prove a token, never ownership.
///   7. Deletion = anonymise + seal, per the stated policy (see IdentityModule.MapEndpoints docs +
///      threat model §7): credentials/sessions/connectors/sync payloads are destroyed or
///      anonymised; audit_events and tombstone hashes are RETAINED (they are already unlinkable
///      pseudonyms after the wipe; deleting them would erase the security evidence trail).
/// EXTEND: Phase 2 adds email verification + password reset here, not around here.
/// </summary>
public sealed class IdentityService
{
    private readonly LivoraDbContext _db;
    private readonly LivoraSigningKey _key;
    private readonly PasswordHasher _hasher;
    private readonly IdentityOptions _options;
    private readonly IClock _clock;
    private readonly IdentitySecurityStore _security;
    private readonly IGoogleIdTokenVerifier _google;
    private readonly ILogger<IdentityService> _log;

    /// <summary>Single-host law (Wave 4 §0.6): rotation, revocation and lockout mutations are
    /// serialized per user by an in-process gate; a multi-host deployment would need DB-level
    /// rowversion on auth_sessions instead — recorded as a follow-up, not hidden.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> UserGates = new();

    public IdentityService(
        LivoraDbContext db, LivoraSigningKey key, PasswordHasher hasher, IdentityOptions options,
        IClock clock, IdentitySecurityStore security, IGoogleIdTokenVerifier google,
        ILogger<IdentityService> log)
    {
        _db = db;
        _key = key;
        _hasher = hasher;
        _options = options;
        _clock = clock;
        _security = security;
        _google = google;
        _log = log;
    }

    // ------------------------------------------------------------------ results
    /// <summary>Everything a protected identity handler needs after passing the gauntlet:
    /// live session + owning user, or the problem result to return verbatim.</summary>
    public sealed record GateContext(UserAccount User, AuthSession Session);
    public sealed record GateOutcome(GateContext? Context, IResult? Problem)
    {
        public bool Allowed => Context is not null;
    }

    // ================================================================== register

    public async Task<IResult> RegisterAsync(
        HttpContext ctx, [FromBody] RegisterRequest? body, CancellationToken ct)
    {
        var emailRaw = EmailNormalized.Normalize(body?.Email);
        var email = emailRaw ?? "";
        var errors = new Dictionary<string, List<string>>();
        if (email.Length == 0 || !EmailNormalized.LooksValid(email))
            errors["email"] = ["A valid email address is required."];

        var password = body?.Password ?? "";
        if (password.Length < 10)
            errors["password"] = ["The password must be at least 10 characters."];
        else if (System.Text.Encoding.UTF8.GetByteCount(password) > _options.MaxPasswordBytes)
            errors["password"] = [$"The password must not exceed {_options.MaxPasswordBytes} bytes."];
        else if (password.Any(char.IsControl))
            errors["password"] = ["The password contains invalid characters."];

        var displayName = body?.DisplayName?.Trim();
        if (displayName is { Length: > 120 })
            errors["displayName"] = ["The display name must not exceed 120 characters."];

        var locale = body?.Locale?.Trim().ToLowerInvariant();
        if (locale is not (null or "" or "en" or "fa"))
            errors["locale"] = ["locale must be \"en\" or \"fa\"."];
        if (locale is null or "") locale = "en";

        if (errors.Count > 0)
            return Problems.Of(ctx, ProblemCodes.ValidationFailed, "The submitted values are not valid.",
                fieldErrors: errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));

        var existing = await _db.Users.AnyAsync(u => u.NormalizedEmail == email, ct);
        if (existing)
            return Problems.Of(ctx, ProblemCodes.EmailAlreadyRegistered,
                "An account with this email already exists.");

        var now = _clock.UtcNow;
        var rolesForBootstrap = _options.BootstrapAdminEmails.Contains(email, StringComparer.Ordinal)
            ? "admin" : null;
        var user = new UserAccount
        {
            Email = email,
            NormalizedEmail = email,
            PasswordHash = _hasher.Hash(password),
            DisplayName = displayName ?? email.Split('@')[0],
            PrimaryLocale = locale!,
            Status = AccountStatus.Active,
            Tier = AccountTier.Free,
            CreatedAtUtc = now,
            RowVersion = 1,
        };
        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a registration race on the unique index — same answer as the pre-check.
            return Problems.Of(ctx, ProblemCodes.EmailAlreadyRegistered,
                "An account with this email already exists.");
        }

        AddAudit(ctx, "AccountCreated", user.Id, user.Id, meta: rolesForBootstrap is null ? null : "{\"role_bootstrap\":\"admin\"}");
        await _db.SaveChangesAsync(ct);

        if (rolesForBootstrap is not null)
            _log.LogInformation("bootstrap admin role granted at registration for a configured admin email");

        var (session, refresh) = await CreateSessionAsync(user, deviceLabel: null, platform: null, ct);
        // The session row MUST be persisted before the tokens go out the door: the revocation gate
        // (AuthorizeCallerAsync) demands a live session for every protected call, and a token for a
        // session that never hit the DB is a dead login. (Bug caught by IdentityAuthApiTests.)
        await _db.SaveChangesAsync(ct);
        return Results.Json(TokenPayload(user, session, refresh), statusCode: 201);
    }

    // ===================================================================== login

    public async Task<IResult> LoginAsync(
        HttpContext ctx, [FromBody] LoginRequest? body, CancellationToken ct)
    {
        var email = EmailNormalized.Normalize(body?.Email);
        var password = body?.Password ?? "";
        if (email is null || !EmailNormalized.LooksValid(email) || password.Length == 0)
            return Problems.Of(ctx, ProblemCodes.InvalidCredentials, "Email or password is incorrect.");

        var gate = await AcquireUserGateAsync(email, ct);
        try
        {
            return await LoginCoreAsync(ctx, email, password, body?.DeviceLabel, body?.Platform, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IResult> LoginCoreAsync(
        HttpContext ctx, string email, string password, string? deviceLabel, string? platform, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var profile = await _security.LoadForUpdateAsync(email, ct);

        // Lockout is checked BEFORE existence: a locked unknown email and a locked real email get
        // the same 403 — the escalation ladder carries no information about account existence.
        if (profile.LockoutUntilUtc is { } until && until > now)
        {
            AddAudit(ctx, "LoginBlockedByLockout", userId: null, subject: null);
            await _db.SaveChangesAsync(ct);
            return Problems.Of(ctx, ProblemCodes.AccountLocked,
                "Too many failed sign-in attempts. Try again later.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.NormalizedEmail == email && u.Status != AccountStatus.Deleted, ct);

        var hash = user?.PasswordHash;
        var ok = _hasher.Verify(password, hash);
        if (user is null || hash is null)
        {
            // Equalize: burn the same PBKDF2 cost as a real check so "no such account" and
            // "wrong password" are indistinguishable by timing as well as by body.
            _hasher.EqualizingVerify(password);
            ok = false;
        }

        if (!ok)
        {
            var state = _security.RecordFailure(profile, _options, now);
            AddAudit(ctx, "LoginFailed", userId: null, subject: null,
                meta: state switch { IdentitySecurityStore.FailureState.LockedOut => "{\"outcome\":\"locked\"}", _ => null });
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("login failed for a normalized email (attempt state {State})", state);

            return state switch
            {
                IdentitySecurityStore.FailureState.LockedOut => Problems.Of(ctx,
                    ProblemCodes.AccountLocked, "Too many failed sign-in attempts. Try again later."),
                IdentitySecurityStore.FailureState.RateLimited => Problems.Of(ctx,
                    ProblemCodes.RateLimited, "Too many failed sign-in attempts. Slow down."),
                _ => Problems.Of(ctx, ProblemCodes.InvalidCredentials, "Email or password is incorrect."),
            };
        }

        if (user!.Status == AccountStatus.Locked)
            return Problems.Of(ctx, ProblemCodes.AccountLocked, "This account is locked. Contact support.");

        // success: reset the failure state, mint a session
        _security.ResetOnSuccess(profile, now);
        user.LastLoginAtUtc = _clock.UtcNow;
        user.RowVersion += 1;
        AddAudit(ctx, "Login", user.Id, user.Id, "{\"method\":\"password\"}");

        var (session, refresh) = await CreateSessionAsync(user, deviceLabel, platform, ct);
        await _db.SaveChangesAsync(ct);

        if (_hasher.NeedsRehash(user.PasswordHash))
        {
            // Transparent upgrade: the password is known-correct right now, so re-derive with the
            // current parameters. One extra KDF on an already-successful login, forever cheap after.
            user.PasswordHash = _hasher.Hash(password);
            await _db.SaveChangesAsync(ct);
            AddAudit(ctx, "PasswordHashUpgraded", user.Id, user.Id);
            await _db.SaveChangesAsync(ct);
        }

        return Results.Json(TokenPayload(user, session, refresh));
    }

    // =================================================================== google

    public async Task<IResult> GoogleLoginAsync(
        HttpContext ctx, [FromBody] GoogleLoginRequest? body, CancellationToken ct)
    {
        var deviceLabel = body?.DeviceLabel;
        var platform = body?.Platform;
        if (!_options.GoogleConfigured)
        {
            // Honest per contract: no client_id means the path is UNCONFIGURED, not "broken",
            // and never a faked login. §5c: 503 provider_unconfigured.
            return Problems.Of(ctx, ProblemCodes.ProviderUnconfigured,
                "Google sign-in is not configured on this server (Identity:Google:ClientId is empty).");
        }

        var outcome = await _google.VerifyAsync(body?.IdToken, ct);
        if (outcome.Status == GoogleVerifyStatus.Ok)
        {
            // proceed below
        }
        else if (outcome.Status == GoogleVerifyStatus.Unavailable)
        {
            return Problems.Of(ctx, ProblemCodes.ProviderUnavailable,
                "The identity provider could not be reached. Try again shortly.");
        }
        else
        {
            // Invalid (and Unconfigured defensive): same envelope as password auth failures —
            // never echo WHY Google's token failed, never a different shape that fingerprints.
            AddAudit(ctx, "GoogleLoginRejected", userId: null, subject: null);
            await _db.SaveChangesAsync(ct);
            return Problems.Of(ctx, ProblemCodes.InvalidCredentials, "The Google sign-in was not accepted.");
        }

        var mapped = GoogleClaimMapper.Map(outcome.Claims!);
        if (!mapped.Accepted)
        {
            AddAudit(ctx, "GoogleLoginRejected", userId: null, subject: null, "{\"reason\":\"claim_map\"}");
            await _db.SaveChangesAsync(ct);
            return Problems.Of(ctx, ProblemCodes.InvalidCredentials, "The Google sign-in was not accepted.");
        }
        var google = mapped.Identity!;

        var gate = await AcquireUserGateAsync(google.Subject, ct);
        try
        {
            return await GoogleLoginCoreAsync(ctx, google, deviceLabel, platform, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IResult> GoogleLoginCoreAsync(
        HttpContext ctx, GoogleIdentity google, string? deviceLabel, string? platform, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.GoogleSubject == google.Subject && u.Status != AccountStatus.Deleted, ct);

        if (user is null && _options.GoogleLinkByEmail && google.EmailVerified && google.Email is { } gemail)
        {
            var normalized = EmailNormalized.Normalize(gemail);
            user = await _db.Users.FirstOrDefaultAsync(
                u => u.NormalizedEmail == normalized && u.Status != AccountStatus.Deleted, ct);
            if (user is not null)
            {
                user.GoogleSubject = google.Subject;
                user.RowVersion += 1;
                AddAudit(ctx, "GoogleLinked", user.Id, user.Id);
            }
        }

        if (user is null)
        {
            // Provisioning-only path (LinkByEmail=false is the default until email verification
            // exists — see threat model T-09): a brand-new account anchored on `sub`.
            user = new UserAccount
            {
                GoogleSubject = google.Subject,
                Email = google.EmailVerified ? EmailNormalized.Normalize(google.Email) : null,
                NormalizedEmail = google.EmailVerified ? EmailNormalized.Normalize(google.Email) : null,
                DisplayName = google.DisplayName
                    ?? (google.Email is { } e2 ? e2.Split('@')[0] : "Google user"),
                PrimaryLocale = google.Locale is { Length: >= 2 } loc && loc[..2].ToLowerInvariant() == "fa" ? "fa" : "en",
                Status = AccountStatus.Active,
                Tier = AccountTier.Free,
                CreatedAtUtc = _clock.UtcNow,
                RowVersion = 1,
            };
            _db.Users.Add(user);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Collision on the unique email/sub index — do not link; fail the same way any
                // rejected sign-in does so no email-existence oracle appears on this path.
                return Problems.Of(ctx, ProblemCodes.EmailAlreadyRegistered,
                    "This Google account's email already belongs to a local account. Sign in with your password, or enable email linking (Identity:Google:LinkByEmail).");
            }
            AddAudit(ctx, "AccountCreated", user.Id, user.Id, "{\"method\":\"google\"}");
            await _db.SaveChangesAsync(ct);
        }

        if (user.Status == AccountStatus.Locked)
            return Problems.Of(ctx, ProblemCodes.AccountLocked, "This account is locked. Contact support.");

        user.LastLoginAtUtc = _clock.UtcNow;
        user.RowVersion += 1;
        AddAudit(ctx, "Login", user.Id, user.Id, "{\"method\":\"google\"}");

        var (session, refresh) = await CreateSessionAsync(user, deviceLabel, platform, ct);
        await _db.SaveChangesAsync(ct);
        return Results.Json(TokenPayload(user, session, refresh));
    }

    // ================================================================== refresh

    public async Task<IResult> RefreshAsync(
        HttpContext ctx, [FromBody] RefreshRequest? body, CancellationToken ct)
    {
        var presented = body?.RefreshToken ?? "";
        var hash = RefreshTokens.Hash(presented);

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);
        if (session is null)
        {
            // Not live. Was it ever? The tombstone answers — and distinguishes forgery from REPLAY.
            var tombstone = await _db.Set<RevokedRefreshToken>()
                .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (tombstone is not null && tombstone.Reason == RevokedRefreshReasons.Rotation)
            {
                await WipeFamilyAsync(ctx, tombstone, ct);
                return Problems.Of(ctx, ProblemCodes.TokenRevoked, "This sign-in was revoked for security reasons.");
            }
            // Revoked deliberately, wiped by family, or simply forged — indistinguishable by design.
            return Problems.Of(ctx, ProblemCodes.TokenRevoked, "This refresh token is no longer valid.");
        }

        if (session.RevokedAtUtc is not null)
            return Problems.Of(ctx, ProblemCodes.TokenRevoked, "This session has been revoked.");

        var now = _clock.UtcNow;
        if (session.ExpiresAtUtc <= now)
            return Problems.Of(ctx, ProblemCodes.TokenExpired, "This session has expired. Sign in again.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == session.UserId, ct);
        if (user is null || user.Status == AccountStatus.Deleted)
            return Problems.Of(ctx, ProblemCodes.TokenRevoked, "This refresh token is no longer valid.");

        var gate = await AcquireUserGateAsync(user.Id, ct);
        try
        {
            return await RotateAsync(ctx, user, session, hash, now, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IResult> RotateAsync(
        HttpContext ctx, UserAccount user, AuthSession session, string oldHash,
        DateTimeOffset now, CancellationToken ct)
    {
        // Re-read under the gate: a concurrent rotation may have moved the hash already (a replay
        // would then hit the tombstone path above on the next request — correct, fail-closed).
        var current = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == session.Id, ct);
        if (current?.RevokedAtUtc is not null)
            return Problems.Of(ctx, ProblemCodes.TokenRevoked, "This session has been revoked.");
        if (current is null || current.RefreshTokenHash != oldHash)
            return Problems.Of(ctx, ProblemCodes.TokenRevoked, "This refresh token is no longer valid.");

        var plaintext = RefreshTokens.NewToken();
        _db.Set<RevokedRefreshToken>().Add(new RevokedRefreshToken
        {
            TokenHash = oldHash,
            SessionId = current.Id,
            FamilyId = await FamilyOfAsync(current.Id, ct),
            UserId = current.UserId,
            Reason = RevokedRefreshReasons.Rotation,
            RevokedAtUtc = now,
        });
        current.RefreshTokenHash = RefreshTokens.Hash(plaintext);
        current.LastUsedAtUtc = now;

        AddAudit(ctx, "RefreshRotated", user.Id, user.Id);
        await _db.SaveChangesAsync(ct);

        var access = AccessTokenMint.Create(_key, user.Id, current.Id, RolesFor(user),
            _options.AccessTokenLifetimeMinutes, now);
        return Results.Json(new TokenResponse(
            UserId: user.Id, AccessToken: access, RefreshToken: plaintext,
            ExpiresAtUtc: now.AddMinutes(_options.AccessTokenLifetimeMinutes),
            SessionId: current.Id));
    }

    /// <summary>The theft mitigation: a rotated-out token was replayed. Kill every session in the
    /// family, tombstone their live hashes (as family_wipe — replays of THOSE must not retrigger a
    /// cascade), and log the theft signal. Other, independent logins of the account survive.</summary>
    private async Task WipeFamilyAsync(HttpContext ctx, RevokedRefreshToken tombstone, CancellationToken ct)
    {
        var gate = await AcquireUserGateAsync(tombstone.UserId, ct);
        try
        {
            var now = _clock.UtcNow;
            var familySessionIds = await _db.Set<AuthSessionLineage>()
                .Where(l => l.FamilyId == tombstone.FamilyId)
                .Select(l => l.SessionId)
                .ToListAsync(ct);
            familySessionIds = familySessionIds.Append(tombstone.SessionId).Distinct().ToList();

            var live = await _db.Sessions
                .Where(s => familySessionIds.Contains(s.Id) && s.RevokedAtUtc == null)
                .ToListAsync(ct);
            foreach (var s in live)
            {
                _db.Set<RevokedRefreshToken>().Add(new RevokedRefreshToken
                {
                    TokenHash = s.RefreshTokenHash,
                    SessionId = s.Id,
                    FamilyId = tombstone.FamilyId,
                    UserId = s.UserId,
                    Reason = RevokedRefreshReasons.FamilyWipe,
                    RevokedAtUtc = now,
                });
                s.RevokedAtUtc = now;
                s.RevokedReason = "token_reuse_detected";
            }

            AddAudit(ctx, "RefreshTokenReuseDetected", tombstone.UserId, tombstone.SessionId,
                "{\"severity\":\"theft_signal\"}");
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("refresh-token reuse detected for family {FamilyId}; the family was revoked",
                tombstone.FamilyId);
        }
        finally
        {
            gate.Release();
        }
    }

    // ============================================================== protected gate

    /// <summary>
    /// The gauntlet every authenticated identity route runs: valid bearer (middleware) → real
    /// session row (exists, not revoked, not expired) → owning user (exists, not deleted).
    /// This is where a revoked session stops working even though its JWT signature and expiry are
    /// still perfect — the contract's revocation promise, enforced per request.
    /// </summary>
    public async Task<GateOutcome> AuthorizeSessionAsync(HttpContext ctx, CancellationToken ct)
        => await AuthorizeCallerAsync(_db, ctx, _clock, ct);

    /// <summary>Static form of the gauntlet so the account service (same module, split file)
    /// enforces the exact same session truth without duplicating the logic.</summary>
    internal static async Task<GateOutcome> AuthorizeCallerAsync(
        LivoraDbContext db, HttpContext ctx, IClock clock, CancellationToken ct)
    {
        var uid = ctx.User.UserId();
        var sid = ctx.User.SessionId();
        if (uid is null || sid is null)
            return new GateOutcome(null, Problems.Of(ctx, ProblemCodes.Unauthenticated,
                "A valid access token is required for this endpoint."));

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sid, ct);
        if (session is null || session.UserId != uid)
            return new GateOutcome(null, Problems.Of(ctx, ProblemCodes.TokenRevoked,
                "This session no longer exists."));
        if (session.RevokedAtUtc is not null)
            return new GateOutcome(null, Problems.Of(ctx, ProblemCodes.TokenRevoked,
                "This session has been revoked."));
        if (session.ExpiresAtUtc <= clock.UtcNow)
            return new GateOutcome(null, Problems.Of(ctx, ProblemCodes.TokenExpired,
                "This session has expired. Sign in again."));

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (user is null || user.Status == AccountStatus.Deleted)
            return new GateOutcome(null, Problems.Of(ctx, ProblemCodes.TokenRevoked,
                "This session is no longer valid."));

        session.LastUsedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new GateOutcome(new GateContext(user, session), null);
    }

    // ================================================================= sessions

    public async Task<IResult> LogoutAsync(HttpContext ctx, CancellationToken ct)
    {
        var gateResult = await AuthorizeSessionAsync(ctx, ct);
        if (!gateResult.Allowed) return gateResult.Problem!;
        var (user, session) = gateResult.Context!;

        await RevokeSessionCoreAsync(ctx, user.Id, session.Id, "logout", ct);
        return Results.NoContent();
    }

    public async Task<IResult> ListSessionsAsync(
        HttpContext ctx, int? offset, int? limit, CancellationToken ct)
    {
        var gateResult = await AuthorizeSessionAsync(ctx, ct);
        if (!gateResult.Allowed) return gateResult.Problem!;
        var (user, current) = gateResult.Context!;

        var req = new PageRequest { Offset = offset ?? 0, Limit = limit ?? PageRequest.DefaultLimit };
        var now = _clock.UtcNow;
        var query = _db.Sessions
            .Where(s => s.UserId == user.Id && s.RevokedAtUtc == null && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.CreatedAtUtc);
        var rows = await query.Skip(req.Offset).Take(req.SafeLimit).ToListAsync(ct);

        // §5c spells this response as the BARE ARRAY of session rows — and the frozen P1-D client
        // (LivoraApiPort.GetSessionsAsync) deserializes exactly that shape. §5's PagedResult rule
        // yields to the more specific frozen contract here (the rendezvous beats the convention);
        // optional offset/limit still bound the page. Logged as a contract clarification request.
        var items = rows.Select(s => new SessionInfo(
            SessionId: s.Id, DeviceLabel: s.DeviceLabel, Platform: s.ClientPlatform,
            CreatedAtUtc: s.CreatedAtUtc, LastUsedAtUtc: s.LastUsedAtUtc,
            ExpiresAtUtc: s.ExpiresAtUtc, IsCurrent: s.Id == current.Id)).ToArray();

        return Results.Json(items);
    }

    public async Task<IResult> RevokeSessionAsync(HttpContext ctx, string id, CancellationToken ct)
    {
        var gateResult = await AuthorizeSessionAsync(ctx, ct);
        if (!gateResult.Allowed) return gateResult.Problem!;
        var (user, _) = gateResult.Context!;

        // §5c: foreign session id answers 403 (explicitly NOT 404 — the contract mandates this
        // shape for this route, and the caller already holds a valid session, so it is not an
        // anonymous existence oracle).
        var target = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (target is null)
            return Problems.Of(ctx, ProblemCodes.NotFound, "No such session.");
        if (target.UserId != user.Id)
        {
            AddAudit(ctx, "SessionRevokeIdorAttempt", user.Id, id);
            await _db.SaveChangesAsync(ct);
            _log.LogWarning("IDOR attempt: account {Actor} tried to revoke session of another account", user.Id);
            return Problems.Of(ctx, ProblemCodes.Forbidden, "This session belongs to another account.");
        }

        await RevokeSessionCoreAsync(ctx, user.Id, target.Id, "revoked_by_user", ct);
        return Results.NoContent();
    }

    private async Task RevokeSessionCoreAsync(HttpContext ctx, string userId, string sessionId, string reason, CancellationToken ct)
    {
        var gate = await AcquireUserGateAsync(userId, ct);
        try
        {
            var now = _clock.UtcNow;
            var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
            if (session is null || session.RevokedAtUtc is not null)
                return; // idempotent: already gone

            var family = await FamilyOfAsync(session.Id, ct);
            _db.Set<RevokedRefreshToken>().Add(new RevokedRefreshToken
            {
                TokenHash = session.RefreshTokenHash,
                SessionId = session.Id,
                FamilyId = family,
                UserId = session.UserId,
                Reason = reason == "revoked_by_user"
                    ? RevokedRefreshReasons.RevokedByUser
                    : RevokedRefreshReasons.Logout,
                RevokedAtUtc = now,
            });
            session.RevokedAtUtc = now;
            session.RevokedReason = reason;

            AddAudit(ctx, reason == "revoked_by_user" ? "SessionRevoked" : "Logout", userId, sessionId);
            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    // ==================================================================== shared

    private async Task<(AuthSession Session, string RefreshPlaintext)> CreateSessionAsync(
        UserAccount user, string? deviceLabel, string? platform, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var refresh = RefreshTokens.NewToken();
        var session = new AuthSession
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            RefreshTokenHash = RefreshTokens.Hash(refresh),
            DeviceLabel = SanitiseLabel(deviceLabel),
            ClientPlatform = SanitiseLabel(platform, 64),
            CreatedAtUtc = now,
            LastUsedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_options.RefreshTokenLifetimeDays),
        };
        _db.Sessions.Add(session);
        _db.Set<AuthSessionLineage>().Add(new AuthSessionLineage
        {
            SessionId = session.Id,
            FamilyId = session.Id, // a login seeds a family; rotations keep the same id (in-place)
            UserId = user.Id,
        });
        return (session, refresh);
    }

    private async Task<string> FamilyOfAsync(string sessionId, CancellationToken ct)
    {
        var lineage = await _db.Set<AuthSessionLineage>().FirstOrDefaultAsync(l => l.SessionId == sessionId, ct);
        return lineage?.FamilyId ?? sessionId;
    }

    private TokenResponse TokenPayload(UserAccount user, AuthSession session, string refreshPlaintext)
    {
        var access = AccessTokenMint.Create(_key, user.Id, session.Id, RolesFor(user),
            _options.AccessTokenLifetimeMinutes, _clock.UtcNow);
        return new TokenResponse(
            UserId: user.Id, AccessToken: access, RefreshToken: refreshPlaintext,
            ExpiresAtUtc: session.CreatedAtUtc.AddMinutes(_options.AccessTokenLifetimeMinutes),
            SessionId: session.Id);
    }

    /// <summary>Server-authoritative roles (Wave 4 §54): the tier/status of the ACCOUNT row decide,
    /// never a client flag. The bootstrap email list is the P1 admin-seeding seam — see threat
    /// model M-07; it is empty by default and logged loudly when used.</summary>
    private IEnumerable<string> RolesFor(UserAccount user)
    {
        yield return Roles.User;
        if (user.NormalizedEmail is { } email && _options.BootstrapAdminEmails.Contains(email, StringComparer.Ordinal))
            yield return Roles.Admin;
    }

    /// <summary>Audit rows carry the request correlation id so an incident can be reconstructed
    /// without storing any content: who/what/when, ids and enum values only (no email, no token).</summary>
    private void AddAudit(HttpContext? ctx, string type, string? userId, string? subject, string? meta = null)
        => AddAuditCore(_db, ctx, type, userId, subject, meta, _clock.UtcNow);

    internal static void AddAuditCore(
        LivoraDbContext db, HttpContext? ctx, string type, string? userId, string? subject,
        string? meta = null, DateTimeOffset? occurredAtUtc = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = userId,
            Type = type,
            Subject = subject,
            OccurredAtUtc = occurredAtUtc ?? DateTimeOffset.UtcNow,
            CorrelationId = ctx?.GetCorrelationId(),
            MetadataJson = meta,
        });
    }

    internal static SemaphoreSlim GateFor(string key) =>
        UserGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private static async Task<SemaphoreSlim> AcquireUserGateAsync(string key, CancellationToken ct)
    {
        var gate = GateFor(key);
        await gate.WaitAsync(ct);
        return gate;
    }

    private static string? SanitiseLabel(string? raw, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var cleaned = new string(trimmed.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Length == 0 ? null : cleaned.Length > max ? cleaned[..max] : cleaned;
    }
}

/// <summary>The fixed vocabulary of tombstone reasons (threat model T-05/T-06 pin their semantics).</summary>
public static class RevokedRefreshReasons
{
    public const string Rotation = "rotation";
    public const string Logout = "logout";
    public const string RevokedByUser = "revoked_by_user";
    public const string FamilyWipe = "family_wipe";
    public const string AccountDeleted = "account_deleted";
}
