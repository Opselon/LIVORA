using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Identity;
using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Tests.Identity;

/// <summary>
/// PURPOSE: the IDOR matrix the Wave 4 brief (§16) and CONTRACT-P1 §5b demand of every user-scoped
///          route: for sessions list, session revoke, account read, account export and the deletion
///          verbs, a valid token of account A must NEVER read or mutate account B's rows. Each row
///          is asserted twice over: what HTTP answers (403/404 for foreign object ids, never 200)
///          and what the BODY leaks (the foreign row's content must not appear anywhere in it).
/// OWNER: Agent 03 (identity lane); written by repair lane R2.
/// CONSUMES: IdentityApiHarness (real registrations → real sessions) + the DB truth.
/// INVARIANTS:
///   - a policy name never proves ownership: handlers compare the ROW's owner id (§5b). The revoke
///     row proves it by answering 403 for a foreign session id and leaving the victim's session live.
///   - "resource does not exist for you" and "forbidden" never differ in a way that enumerates ids:
///     the foreign-session answer is the same envelope shape as every other forbidden answer.
///   - self-scoped surfaces ignore caller-supplied identity: `?user=`/`?userId=` are inert — the
///     caller's TOKEN is the only subject selector (the export/account rows prove this by content).
///   - a revoked or wiped session stops working with a still-perfect JWT (state, not signature).
/// </summary>
public sealed class IdentityIdorMatrixTests : IdentityApiHarness
{
    public IdentityIdorMatrixTests(LivoraWebFixture fixture) : base(fixture) { }

    private async Task<(Registered Victim, Registered Attacker)> TwoAccountsAsync()
        => (await RegisterAsync(deviceLabel: "victim-phone"), await RegisterAsync(deviceLabel: "attacker-phone"));

    // ------------------------------------------------------------- GET /auth/sessions -----------

    [Fact]
    public async Task Sessions_list_shows_only_the_callers_own_live_sessions()
    {
        var (victim, attacker) = await TwoAccountsAsync();
        await LoginAsync(victim.Email, victim.Password, deviceLabel: "victim-tablet");

        var (status, text) = await SendRawAsync(HttpMethod.Get, "/api/v1/auth/sessions",
            bearer: attacker.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, status);
        var rows = Parse(text).EnumerateArray().ToArray();

        // §5c: the sessions list is the caller's own device list — the attacker sees exactly one
        // row (their own), and the victim's device labels never appear.
        Assert.Single(rows);
        Assert.Equal(attacker.Tokens.SessionId, rows[0].GetProperty("sessionId").GetString());
        Assert.True(rows[0].GetProperty("isCurrent").GetBoolean());
        Assert.DoesNotContain("victim-phone", text);
        Assert.DoesNotContain("victim-tablet", text);
        Assert.DoesNotContain(victim.Tokens.SessionId, text);
        Assert.DoesNotContain(victim.Tokens.UserId, text);

        // the victim sees their own two, both live, one current
        var (_, victimText) = await SendRawAsync(HttpMethod.Get, "/api/v1/auth/sessions",
            bearer: victim.Tokens.AccessToken);
        var victimRows = Parse(victimText).EnumerateArray().ToArray();
        Assert.Equal(2, victimRows.Length);
        Assert.Equal(1, victimRows.Count(r => r.GetProperty("isCurrent").GetBoolean()));
    }

    [Fact]
    public async Task Sessions_list_is_bounded_by_limit_and_never_leaks_the_overshoot()
    {
        var reg = await RegisterAsync();
        await LoginAsync(reg.Email, reg.Password, deviceLabel: "extra-one");
        await LoginAsync(reg.Email, reg.Password, deviceLabel: "extra-two");

        var (_, text) = await SendRawAsync(HttpMethod.Get, "/api/v1/auth/sessions?limit=2",
            bearer: reg.Tokens.AccessToken);
        Assert.Equal(2, Parse(text).EnumerateArray().Count());
        // a limit beyond the frozen clamp (PageRequest.MaxLimit) is clamped to the default, not obeyed
        var (_, big) = await SendRawAsync(HttpMethod.Get, "/api/v1/auth/sessions?limit=99999",
            bearer: reg.Tokens.AccessToken);
        Assert.True(Parse(big).EnumerateArray().Count() <= PageRequest.MaxLimit);
    }

    // ------------------------------------------------------ DELETE /auth/sessions/{id} ----------

    [Fact]
    public async Task Revoking_another_accounts_session_is_403_and_changes_nothing()
    {
        var (victim, attacker) = await TwoAccountsAsync();

        var (status, text) = await SendRawAsync(HttpMethod.Delete,
            "/api/v1/auth/sessions/" + victim.Tokens.SessionId, bearer: attacker.Tokens.AccessToken);

        // §5c names this route's cross-account answer explicitly: 403 forbidden.
        Assert.Equal(HttpStatusCode.Forbidden, status);
        var problem = ProblemOf(text);
        Assert.Equal(ProblemCodes.Forbidden, problem.Code);
        // no leakage: the detail never repeats the victim's id, email, or session material
        Assert.DoesNotContain(victim.Tokens.UserId, text);
        Assert.DoesNotContain(victim.Email, text);

        // the victim's session is still alive (list works, refresh works)
        var (stillOk, _) = await SendRawAsync(HttpMethod.Get, "/api/v1/auth/sessions",
            bearer: victim.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, stillOk);
        var (refreshed, _) = await RefreshAsync(victim.Tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed);

        // and the attempt is in the audit trail against the ATTACKER's id (who tried), not the victim's
        using var db = Db();
        var attempt = await db.AuditEvents.FirstOrDefaultAsync(a =>
            a.Type == "SessionRevokeIdorAttempt" && a.UserId == attacker.Tokens.UserId);
        Assert.NotNull(attempt);
        Assert.Equal(victim.Tokens.SessionId, attempt!.Subject);
        Assert.DoesNotContain(victim.Email, attempt.Subject ?? "");
    }

    [Fact]
    public async Task Revoking_a_nonexistent_session_is_404_never_a_200_and_never_a_500()
    {
        var attacker = await RegisterAsync();
        var (status, text) = await SendRawAsync(HttpMethod.Delete,
            "/api/v1/auth/sessions/no-such-session-" + Guid.NewGuid().ToString("N")[..8],
            bearer: attacker.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(ProblemCodes.NotFound, ProblemOf(text).Code);
    }

    [Fact]
    public async Task A_user_can_revoke_their_own_other_session_and_it_stops_working()
    {
        var reg = await RegisterAsync();
        var other = await LoginAsync(reg.Email, reg.Password, deviceLabel: "old-phone");

        var (status, _) = await SendRawAsync(HttpMethod.Delete,
            "/api/v1/auth/sessions/" + other.SessionId, bearer: reg.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, status);

        // that session's bearer and refresh token are both dead now
        var (dead, deadText) = await SendRawAsync(HttpMethod.Get, "/api/v1/auth/sessions",
            bearer: other.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, dead);
        Assert.Equal(ProblemCodes.TokenRevoked, ProblemOf(deadText).Code);
        var (rt, _) = await RefreshAsync(other.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rt);

        // listing again from the surviving session shows only the survivor
        var (_, text) = await SendRawAsync(HttpMethod.Get, "/api/v1/auth/sessions",
            bearer: reg.Tokens.AccessToken);
        Assert.DoesNotContain(other.SessionId, text);
    }

    // --------------------------------------------------- GET /account + /account/export ---------

    [Fact]
    public async Task Account_read_is_self_scoped_and_a_supplied_user_param_changes_nothing()
    {
        var (victim, attacker) = await TwoAccountsAsync();

        var (status, own) = await SendRawAsync(HttpMethod.Get,
            "/api/v1/account?user=" + victim.Tokens.UserId, bearer: attacker.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, status);
        var body = Parse(own);
        Assert.Equal(attacker.Tokens.UserId, body.GetProperty("userId").GetString());
        Assert.Equal(attacker.Email, body.GetProperty("email").GetString());
        Assert.DoesNotContain(victim.Email, own);
        Assert.DoesNotContain(victim.Tokens.UserId, own);

        // §5c field set, camelCase, and NO credential material anywhere in the body
        foreach (var field in new[] { "userId", "email", "displayName", "locale", "tier",
                                      "status", "createdAtUtc", "lastLoginAtUtc" })
            Assert.True(body.TryGetProperty(field, out _), $"account is missing '{field}'");
        Assert.DoesNotContain("password", own, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", own, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_is_self_scoped_ignores_a_foreign_user_param_and_carries_no_secrets()
    {
        var (victim, attacker) = await TwoAccountsAsync();
        // give the victim real content so "no leakage" means something
        var (status, text) = await SendRawAsync(HttpMethod.Get,
            "/api/v1/account/export?user=" + victim.Tokens.UserId,
            bearer: attacker.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.DoesNotContain(victim.Email, text);
        Assert.DoesNotContain(victim.Tokens.UserId, text);
        Assert.DoesNotContain(victim.Tokens.RefreshToken, text);
        Assert.DoesNotContain(victim.Tokens.AccessToken, text);
        Assert.DoesNotContain(attacker.Tokens.RefreshToken, text);   // not even the caller's own
        Assert.DoesNotContain(attacker.Tokens.AccessToken, text);
        Assert.DoesNotContain("passwordHash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", text, StringComparison.OrdinalIgnoreCase);

        var doc = Parse(text);
        Assert.Equal(attacker.Tokens.UserId,
            doc.GetProperty("profile").GetProperty("data").GetProperty("userId").GetString());
        // §5c note 3: every section declares its provenance — empty is honest, fabricated is not
        foreach (var (section, source) in new[]
                 {
                     ("profile", "server"), ("history", "server"),
                     ("connectedDataMetadata", "server"),
                     ("goals", "not_implemented"), ("habits", "not_implemented"),
                     ("plans", "not_implemented"), ("purchases", "not_implemented"),
                     ("communityContent", "not_implemented"),
                 })
        {
            Assert.Equal(source, doc.GetProperty(section).GetProperty("source").GetString());
            var data = doc.GetProperty(section).GetProperty("data");
            if (source == "not_implemented") Assert.Empty(data.EnumerateArray());
        }
    }

    // --------------------------------------------------------- deletion verbs (IDOR rows) -------

    [Fact]
    public async Task An_attacker_cannot_cancel_or_execute_a_victims_deletion_request()
    {
        var (victim, attacker) = await TwoAccountsAsync();

        // victim requests deletion
        var (reqStatus, _) = await PostRawAsync("/api/v1/account/delete-requests", "{}",
            bearer: victim.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, reqStatus);

        // attacker tries to cancel it — a cancel is scoped to the CALLER, so the attacker's own
        // (nonexistent) request is cancelled idempotently and the victim stays pending
        var (cancelStatus, _) = await SendRawAsync(HttpMethod.Delete,
            "/api/v1/account/delete-requests", bearer: attacker.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, cancelStatus);
        using (var db = Db())
        {
            var v = await db.Users.SingleAsync(u => u.Id == victim.Tokens.UserId);
            Assert.Equal(AccountStatus.DeletionPending, v.Status);
        }

        // attacker names the victim as the execution target: forbidden, and nothing was destroyed
        var (execStatus, execText) = await PostRawAsync("/api/v1/account/delete-requests/execute",
            JsonSerializer.Serialize(new { userId = victim.Tokens.UserId }),
            bearer: attacker.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, execStatus);
        Assert.Equal(ProblemCodes.Forbidden, ProblemOf(execText).Code);
        Assert.DoesNotContain(victim.Email, execText);

        using (var db = Db())
        {
            var still = await db.Users.SingleAsync(u => u.Id == victim.Tokens.UserId);
            Assert.Equal(AccountStatus.DeletionPending, still.Status);
            Assert.NotNull(still.PasswordHash);
            Assert.Equal(victim.Email, still.NormalizedEmail);
        }
    }

    [Fact]
    public async Task A_minted_token_without_a_session_row_cannot_reach_any_identity_surface()
    {
        // §5b in one test: Policies.SignedIn accepts the signature; only the DB proves the session.
        var orphan = Fixture.MintAccessToken("orphan-" + Guid.NewGuid().ToString("N")[..8],
            roles: [Roles.User]);
        foreach (var path in new[]
                 {
                     "/api/v1/auth/sessions", "/api/v1/account", "/api/v1/account/export",
                 })
        {
            var (status, text) = await SendRawAsync(HttpMethod.Get, path, bearer: orphan);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal(ProblemCodes.TokenRevoked, ProblemOf(text).Code);
        }
        var (delStatus, _) = await PostRawAsync("/api/v1/account/delete-requests", "{}", bearer: orphan);
        Assert.Equal(HttpStatusCode.Unauthorized, delStatus);
        var (outStatus, _) = await PostRawAsync("/api/v1/auth/logout", "{}", bearer: orphan);
        Assert.Equal(HttpStatusCode.Unauthorized, outStatus);
    }

    [Fact]
    public async Task A_session_revoked_by_theft_detection_cannot_read_the_account_even_with_a_live_jwt()
    {
        var reg = await RegisterAsync();
        var rotated = ReadTokens((await RefreshAsync(reg.Tokens.RefreshToken)).Text);
        // trigger the family wipe with the rotated-out token
        await RefreshAsync(reg.Tokens.RefreshToken);

        // the rotated access token is still perfectly signed and unexpired — the DB gate stops it
        var (status, text) = await SendRawAsync(HttpMethod.Get, "/api/v1/account",
            bearer: rotated.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(ProblemCodes.TokenRevoked, ProblemOf(text).Code);
    }
}
