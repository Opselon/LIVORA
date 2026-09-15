using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Tests.Identity;

/// <summary>
/// PURPOSE: prove the §5c auth lifecycle end-to-end through the real host: register → login →
///          refresh (ROTATION) → reuse of a rotated-out token revokes the whole family and is
///          recorded as a theft signal; refresh tokens exist as plaintext in exactly one response
///          and as SHA-256 hashes in the database; logout revokes ONLY its own session; wrong email
///          and wrong password are byte-identical 401s; the lockout ladder escalates
///          401 → 429 → 403 at the configured thresholds; and a minted-but-orphaned token (no
///          session row) is refused by the DB gate even though its signature is perfect.
/// OWNER: Agent 03 (identity lane); written by repair lane R2 — the lane shipped the module with
///          zero tests, which this file (with its siblings) replaces.
/// CONSUMES: IdentityApiHarness → real POST /auth/* verbs + the fixture's SQLite file for the
///           truth-behind-the-response assertions.
/// INVARIANTS TESTED (each maps to an IdentityService doc claim):
///   - rotation is one-shot: the OLD token cannot rotate twice; the family wipe kills the NEW one too
///   - families are independent: a theft on device A never logs out device B's separate login
///   - revocation is state, not signature (gate test) — §5b's IDOR/revocation promise
///   - lockout is keyed by normalized email and applies to unknown emails identically (no oracle)
/// TEST DOUBLES: none beyond the host fixture; no sleeping (the ladder is driven by real repeat
///   attempts at the config defaults; window expiry math is proven in IdentityPureMechanicsTests).
/// </summary>
public sealed class IdentityAuthLifecycleTests : IdentityApiHarness
{
    public IdentityAuthLifecycleTests(LivoraWebFixture fixture) : base(fixture) { }

    // ------------------------------------------------------------------ register/login ----------

    [Fact]
    public async Task Register_answers_the_5c_payload_and_a_second_registration_is_409()
    {
        var email = NewEmail("reg");
        var (status, text) = await PostRawAsync("/api/v1/auth/register",
            JsonSerializer.Serialize(new { email, password = FixturePassphrase }));

        Assert.Equal(HttpStatusCode.Created, status);
        var tokens = ReadTokens(text);

        // duplicate → email_already_registered, and the body is the shared envelope
        var (dupStatus, dupText) = await PostRawAsync("/api/v1/auth/register",
            JsonSerializer.Serialize(new { email, password = FixturePassphrase }));
        Assert.Equal(HttpStatusCode.Conflict, dupStatus);
        Assert.Equal(ProblemCodes.EmailAlreadyRegistered, ProblemOf(dupText).Code);

        // the account really exists in the DB with a hash, never the plaintext
        using var db = Db();
        var user = await db.Users.SingleAsync(u => u.Id == tokens.UserId);
        Assert.NotNull(user.PasswordHash);
        Assert.DoesNotContain(FixturePassphrase, user.PasswordHash);
        Assert.Equal(email, user.NormalizedEmail);
    }

    [Theory]
    [InlineData("", "short")]                  // both fields bad
    [InlineData("not-an-email", "short")]      // email shape + password length
    public async Task Register_with_invalid_fields_answers_400_with_per_field_errors(
        string email, string password)
    {
        var (status, text) = await PostRawAsync("/api/v1/auth/register",
            JsonSerializer.Serialize(new { email, password }));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        var problem = ProblemOf(text);
        Assert.Equal(ProblemCodes.ValidationFailed, problem.Code);
        Assert.NotNull(problem.Errors);
        Assert.True(problem.Errors!.ContainsKey("email"));
        if (password.Length < 10) Assert.True(problem.Errors.ContainsKey("password"));
    }

    [Fact]
    public async Task Register_rejects_an_unknown_locale_but_accepts_the_two_legal_ones()
    {
        var (status, text) = await PostRawAsync("/api/v1/auth/register",
            JsonSerializer.Serialize(new { email = NewEmail("loc"), password = FixturePassphrase, locale = "de" }));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("locale", ProblemOf(text).Errors!.Keys);

        foreach (var locale in new[] { "en", "fa" })
        {
            var (ok, _) = await PostRawAsync("/api/v1/auth/register",
                JsonSerializer.Serialize(new { email = NewEmail("loc"), password = FixturePassphrase, locale }));
            Assert.Equal(HttpStatusCode.Created, ok);
        }
    }

    [Fact]
    public async Task Login_issues_a_fresh_session_per_device_and_the_payload_matches_5c()
    {
        var reg = await RegisterAsync();
        var (status, text) = await PostRawAsync("/api/v1/auth/login",
            JsonSerializer.Serialize(new
            {
                email = reg.Email, password = reg.Password,
                deviceLabel = "pixel-7", platform = "android",
            }));

        Assert.Equal(HttpStatusCode.OK, status);
        var tokens = ReadTokens(text);
        Assert.Equal(reg.Tokens.UserId, tokens.UserId);      // same account
        Assert.NotEqual(reg.Tokens.SessionId, tokens.SessionId); // new session

        // deviceLabel/platform persisted — the sessions list is built from these rows
        using var db = Db();
        var session = await db.Sessions.SingleAsync(s => s.Id == tokens.SessionId);
        Assert.Equal("pixel-7", session.DeviceLabel);
        Assert.Equal("android", session.ClientPlatform);
    }

    // ---------------------------------------------------------- constant-shape 401 (no oracle) --

    [Fact]
    public async Task Wrong_password_and_unknown_email_produce_byte_identical_401_bodies()
    {
        var reg = await RegisterAsync();
        var unknown = NewEmail("ghost");

        var (s1, b1) = await PostRawAsync("/api/v1/auth/login",
            JsonSerializer.Serialize(new { email = reg.Email, password = WrongPassphrase }));
        var (s2, b2) = await PostRawAsync("/api/v1/auth/login",
            JsonSerializer.Serialize(new { email = unknown, password = WrongPassphrase }));

        Assert.Equal(HttpStatusCode.Unauthorized, s1);
        Assert.Equal(HttpStatusCode.Unauthorized, s2);
        Assert.Equal(ProblemCodes.InvalidCredentials, ProblemOf(b1).Code);

        // Byte-identical except the per-response correlation id (which identifies the RESPONSE, not
        // the answer — masking it is the honest reading of "identical body" in §5c).
        var m1 = MaskCorrelation(b1);
        var m2 = MaskCorrelation(b2);
        Assert.Equal(m1, m2);
        Assert.NotEqual(b1, b2); // and the ids themselves ARE distinct: real responses, not a stub
    }

    [Fact]
    public async Task Unknown_email_locks_out_on_the_same_ladder_as_a_real_account()
    {
        // Threat model T-03: the escalation carries no existence information. Drive ONE past the
        // warn threshold for a never-registered address and read the shape back.
        var ghost = NewEmail("ghost-ladder");
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 6; i++)
        {
            var (status, text) = await PostRawAsync("/api/v1/auth/login",
                JsonSerializer.Serialize(new { email = ghost, password = OtherWrongPassphrase }));
            Assert.Contains(status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests });
            last = status;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last); // same 429 rung a real account would hit
    }

    private string MaskCorrelation(string body)
    {
        var problem = ProblemOf(body);
        Assert.False(string.IsNullOrWhiteSpace(problem.CorrelationId));
        return body.Replace(problem.CorrelationId, "<correlation-id>");
    }

    // ------------------------------------------------------------- rotation + reuse detection ---

    [Fact]
    public async Task Refresh_rotates_the_token_and_stores_only_the_new_hash()
    {
        var reg = await RegisterAsync();
        var (status, text) = await RefreshAsync(reg.Tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, status);
        var rotated = ReadTokens(text);

        Assert.Equal(reg.Tokens.UserId, rotated.UserId);
        Assert.Equal(reg.Tokens.SessionId, rotated.SessionId);       // same session, new material
        Assert.NotEqual(reg.Tokens.RefreshToken, rotated.RefreshToken);
        Assert.NotEqual(reg.Tokens.AccessToken, rotated.AccessToken);

        using var db = Db();
        var session = await db.Sessions.SingleAsync(s => s.Id == reg.Tokens.SessionId);
        Assert.Equal(Sha256Hex(rotated.RefreshToken), session.RefreshTokenHash);
        Assert.NotEqual(Sha256Hex(reg.Tokens.RefreshToken), session.RefreshTokenHash);
        // the rotated-out token is tombstoned — that is what makes a replay recognisable
        var tombstone = await db.Set<RevokedRefreshToken>()
            .SingleAsync(t => t.TokenHash == Sha256Hex(reg.Tokens.RefreshToken));
        Assert.Equal("rotation", tombstone.Reason);
        Assert.Equal(session.UserId, tombstone.UserId);
    }

    [Fact]
    public async Task Replaying_a_rotated_out_token_revokes_the_family_and_survives_on_the_other_login()
    {
        var stolen = await RegisterAsync(deviceLabel: "stolen-phone");
        var safe = await LoginAsync(stolen.Email, stolen.Password, deviceLabel: "my-phone");

        // attacker's device rotates once (as any client would)...
        var (_, first) = await RefreshAsync(stolen.Tokens.RefreshToken);
        var rotated = ReadTokens(first);

        // ...the legitimate rotation of the SAME login happens next; the attacker replays the
        // token they had held. That replay is the theft signal.
        var (replayStatus, replayText) = await RefreshAsync(stolen.Tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replayStatus);
        Assert.Equal(ProblemCodes.TokenRevoked, ProblemOf(replayText).Code);

        // family wipe: even the CURRENT token of the stolen family is now dead
        var (deadStatus, deadText) = await RefreshAsync(rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, deadStatus);
        Assert.Equal(ProblemCodes.TokenRevoked, ProblemOf(deadText).Code);

        // independent login of the same account survives untouched
        var (safeStatus, safeText) = await RefreshAsync(safe.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, safeStatus);

        using var db = Db();
        var wiped = await db.Sessions.SingleAsync(s => s.Id == stolen.Tokens.SessionId);
        Assert.NotNull(wiped.RevokedAtUtc);
        Assert.Equal("token_reuse_detected", wiped.RevokedReason);
        var survivor = await db.Sessions.SingleAsync(s => s.Id == safe.SessionId);
        Assert.Null(survivor.RevokedAtUtc);

        // theft signal in the audit trail (product law 5: ids and severity only, no content)
        var theft = await db.AuditEvents.Where(a => a.Type == "RefreshTokenReuseDetected")
            .OrderByDescending(a => a.OccurredAtUtc).FirstOrDefaultAsync();
        Assert.NotNull(theft);
        Assert.Equal(stolen.Tokens.UserId, theft!.UserId);
        Assert.Contains("theft_signal", theft.MetadataJson);

        // and the wiped family's live hash was tombstoned as family_wipe, so ITS replay must not
        // cascade a second wipe of anything else
        var wipeTombstone = await db.Set<RevokedRefreshToken>()
            .FirstOrDefaultAsync(t => t.SessionId == stolen.Tokens.SessionId
                                   && t.Reason == "family_wipe");
        Assert.NotNull(wipeTombstone);
    }

    [Fact]
    public async Task Forged_refresh_token_is_token_revoked_never_a_500()
    {
        var (status, text) = await RefreshAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(ProblemCodes.TokenRevoked, ProblemOf(text).Code);
    }

    // ------------------------------------------------------------------- logout scope -----------

    [Fact]
    public async Task Logout_revokes_only_its_own_session_and_kills_its_refresh_token()
    {
        var a = await RegisterAsync(deviceLabel: "A");
        var b = await LoginAsync(a.Email, a.Password, deviceLabel: "B");

        var (outStatus, _) = await PostRawAsync("/api/v1/auth/logout", "{}", bearer: a.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, outStatus);

        // A's bearer now fails at the DB gate even though the JWT itself is still well-formed
        var (aStatus, aText) = await SendRawAsync(HttpMethod.Get, "/api/v1/auth/sessions",
            bearer: a.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, aStatus);
        Assert.Equal(ProblemCodes.TokenRevoked, ProblemOf(aText).Code);

        // B is untouched
        var (bStatus, _) = await SendRawAsync(HttpMethod.Get, "/api/v1/auth/sessions", bearer: b.AccessToken);
        Assert.Equal(HttpStatusCode.OK, bStatus);

        // and A's refresh token is tombstoned as logout — replaying it fails but is NOT a theft wipe
        var (rStatus, rText) = await RefreshAsync(a.Tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rStatus);
        Assert.Equal(ProblemCodes.TokenRevoked, ProblemOf(rText).Code);
        var (stillAlive, _) = await RefreshAsync(b.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, stillAlive);

        using var db = Db();
        var sessionA = await db.Sessions.FirstOrDefaultAsync(s => s.Id == a.Tokens.SessionId);
        if (sessionA is not null)
            Assert.Equal("logout", sessionA.RevokedReason);
    }

    // ------------------------------------------------------------------- lockout ladder ---------

    [Fact]
    public async Task Lockout_escalates_at_the_configured_thresholds_and_blocks_the_right_password()
    {
        // The ladder's numbers come from the SAME source the service reads (IdentityOptions with an
        // empty Identity: section — the fixture host configures no lockout overrides):
        // warn-after 5 (429 from the 6th), max-attempts 10 (403 from the 11th). No magic numbers.
        var options = IdentityOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var email = NewEmail("ladder");
        var reg = await RegisterAsync(email: email);

        for (var attempt = 1; attempt <= options.LockoutMaxAttempts + 1; attempt++)
        {
            var (status, text) = await PostRawAsync("/api/v1/auth/login",
                JsonSerializer.Serialize(new { email, password = "wrong-" + FixturePassphrase }));
            var expected = attempt <= options.LockoutWarnAfterFailures ? HttpStatusCode.Unauthorized
                         : attempt <= options.LockoutMaxAttempts ? HttpStatusCode.TooManyRequests
                         : HttpStatusCode.Forbidden;
            Assert.Equal(expected, status);
            var code = ProblemOf(text).Code;
            if (status == HttpStatusCode.Forbidden) Assert.Equal(ProblemCodes.AccountLocked, code);
            else if (status == HttpStatusCode.TooManyRequests) Assert.Equal(ProblemCodes.RateLimited, code);
            else Assert.Equal(ProblemCodes.InvalidCredentials, code);
        }

        // Even the RIGHT password is refused while locked — lockout is a state, not a suggestion.
        var (lockedStatus, lockedText) = await PostRawAsync("/api/v1/auth/login",
            JsonSerializer.Serialize(new { email, password = reg.Password }));
        Assert.Equal(HttpStatusCode.Forbidden, lockedStatus);
        Assert.Equal(ProblemCodes.AccountLocked, ProblemOf(lockedText).Code);

        using var db = Db();
        var profile = await db.Set<IdentitySecurityProfile>()
            .SingleAsync(p => p.NormalizedEmail == email);
        Assert.True(profile.FailedAttempts > options.LockoutMaxAttempts);
        Assert.NotNull(profile.LockoutUntilUtc);
    }

    // -------------------------------------------------------------- revocation is state ---------

    [Fact]
    public async Task A_validly_signed_token_without_a_session_row_is_refused_by_every_identity_route()
    {
        // The §5b promise: Policies.SignedIn proves a token; only the DB proves the session lives.
        // MintAccessToken (the fixture seam) creates NO session — the gate must answer token_revoked.
        var orphan = Fixture.MintAccessToken("no-such-account-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var path in new[]
                 {
                     "/api/v1/auth/sessions", "/api/v1/account", "/api/v1/account/export",
                 })
        {
            var (status, text) = await SendRawAsync(HttpMethod.Get, path, bearer: orphan);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal(ProblemCodes.TokenRevoked, ProblemOf(text).Code);
        }
    }

    [Fact]
    public async Task Anonymous_reaches_neither_side_of_the_contract_and_gets_the_shared_envelope()
    {
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/v1/auth/sessions"),
                     (HttpMethod.Get, "/api/v1/account"),
                     (HttpMethod.Get, "/api/v1/account/export"),
                     (HttpMethod.Post, "/api/v1/auth/logout"),
                     (HttpMethod.Delete, "/api/v1/auth/sessions/whatever"),
                     (HttpMethod.Post, "/api/v1/account/delete-requests"),
                     (HttpMethod.Delete, "/api/v1/account/delete-requests"),
                 })
        {
            var (status, text) = await SendRawAsync(method, path, body: method == HttpMethod.Post ? "{}" : null);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal(ProblemCodes.Unauthenticated, ProblemOf(text).Code);
        }
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
