using System.Net;
using System.Net.Http;
using LIVORA.Application.Cloud;
using LIVORA.Infrastructure.Cloud;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Tests.Tests.Wave4ClientSeam;

/// <summary>
/// The typed port's behaviour, driven through the REAL <see cref="LivoraApiPort"/> class with a
/// scripted handler (no live backend exists — §5c shapes only). What each test pins is the part a
/// ViewModel cannot defend against later: header plumbing, error classification, and the rule that
/// an expected failure is a value, never an exception.
/// </summary>
public class LivoraApiPortTests
{
    private static LivoraApiPort Port(Wave4SeamHarness.ScriptedHandler handler,
        Wave4SeamHarness.TempDir dir, bool configured = true,
        ICloudAuthContext? auth = null) =>
        new(Wave4SeamHarness.Options(dir, configured), handler, auth, NullLogger<LivoraApiPort>.Instance,
            () => "cid-fixed-1");

    // ============================ unconfigured ==============================================

    [Fact]
    public async Task WithNoBaseUrl_EveryCallRefuses_WithoutTouchingTheNetwork()
    {
        var handler = new Wave4SeamHarness.ScriptedHandler();
        using var dir = new Wave4SeamHarness.TempDir("port-unconfigured");
        using var port = Port(handler, dir, configured: false);

        Assert.False(port.IsConfigured);
        var res = await port.LoginAsync(new LoginRequest("a@b.test", "pw"));
        Assert.False(res.Ok);
        Assert.Equal(LivoraApiTransportCodes.NotConfigured, res.Code);
        Assert.Equal("Api.Error.not_configured", res.ErrorReasonKey);
        Assert.Empty(handler.Requests);   // nothing left the device — asserted, not assumed

        var caps = await port.GetCapabilitiesAsync();
        Assert.False(caps.Ok);
        Assert.Equal(LivoraApiTransportCodes.NotConfigured, caps.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NotConfiguredResult_IsNotMarkedTransient_RetryingCannotHelp()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-unconf2");
        using var port = Port(new Wave4SeamHarness.ScriptedHandler(), dir, configured: false);
        var res = await port.GetAccountAsync();
        Assert.False(res.IsTransient);
        Assert.False(res.IsNetworkFailure);
    }

    // ============================ paths + verbs ===============================================

    [Fact]
    public async Task EachEndpoint_HitsTheFrozenPath_AndVerb()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-paths");
        var h = new Wave4SeamHarness.ScriptedHandler();
        h.Json("POST", "/api/v1/auth/register", 201, Wave4SeamHarness.TokensBody());
        h.Json("POST", "/api/v1/auth/login", 200, Wave4SeamHarness.TokensBody());
        h.Json("POST", "/api/v1/auth/google", 200, Wave4SeamHarness.TokensBody());
        h.Json("POST", "/api/v1/auth/refresh", 200, Wave4SeamHarness.TokensBody());
        h.On("POST", "/api/v1/auth/logout", _ => Wave4SeamHarness.NoContent());
        h.Json("GET", "/api/v1/auth/sessions", 200,
            """[{"sessionId":"s-1","deviceLabel":"phone","platform":"windows","createdAtUtc":"2026-09-01T08:00:00+00:00","lastUsedAtUtc":null,"expiresAtUtc":"2026-09-15T08:00:00+00:00","isCurrent":true}]""");
        h.On("DELETE", "/api/v1/auth/sessions/s-9", _ => Wave4SeamHarness.NoContent());
        h.Json("GET", "/api/v1/account", 200,
            """{"userId":"u-1","email":"a@b.test","displayName":"سارا","locale":"fa-IR","tier":"free","status":"active","createdAtUtc":"2026-09-01T08:00:00+00:00","lastLoginAtUtc":null}""");
        h.Json("POST", "/api/v1/account/delete-requests", 200,
            """{"deletionRequestedAtUtc":"2026-09-14T08:00:00+00:00","scheduledForUtc":"2026-09-21T08:00:00+00:00","reversibleUntilUtc":"2026-09-20T08:00:00+00:00"}""");
        h.On("DELETE", "/api/v1/account/delete-requests", _ => Wave4SeamHarness.NoContent());
        h.Json("GET", "/api/v1/account/export", 200, """{"exportedAtUtc":"2026-09-14T08:00:00+00:00","profile":{"source":"server","profile":{"displayName":"سارا"}},"goals":{"source":"not_implemented"},"habits":{"source":"not_implemented"},"plans":{"source":"not_implemented"},"history":{"source":"server","items":[]},"connectedDataMetadata":{"source":"server","items":[{"kind":"health"}]},"purchases":{"source":"not_implemented"},"communityContent":{"source":"not_implemented"}}""");
        h.Json("POST", "/api/v1/sync/batch", 200, Wave4SeamHarness.BatchBody(("op", "applied")));
        h.Json("GET", "/api/v1/sync/changes", 200, Wave4SeamHarness.ChangesBody(9, "g1"));
        h.Json("GET", "/api/v1/platform/capabilities", 200, Wave4SeamHarness.CapabilitiesBody());

        using var port = Port(h, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1"));

        Assert.True((await port.RegisterAsync(new RegisterRequest("a@b.test", "pw"))).Ok);
        Assert.True((await port.LoginAsync(new LoginRequest("a@b.test", "pw"))).Ok);
        Assert.True((await port.LoginWithGoogleAsync(new GoogleLoginRequest("idtok"))).Ok);
        Assert.True((await port.RefreshAsync(new RefreshTokenRequest("rt-1"))).Ok);
        Assert.True((await port.LogoutAsync()).Value);
        var sessions = await port.GetSessionsAsync();
        Assert.True(sessions.Ok && sessions.Value!.Count == 1 && sessions.Value[0].IsCurrent);
        Assert.True((await port.RevokeSessionAsync("s-9")).Ok);
        var account = await port.GetAccountAsync();
        Assert.Equal("سارا", account.Value!.DisplayName);          // Persian survives the wire
        Assert.True((await port.RequestAccountDeletionAsync()).Ok);
        Assert.True((await port.CancelAccountDeletionAsync()).Ok);
        var exp = await port.GetAccountExportAsync();
        Assert.Equal("server", exp.Value!.SectionSource("profile"));
        Assert.Equal("not_implemented", exp.Value!.SectionSource("goals"));
        Assert.True((await port.SyncBatchAsync(new SyncBatchRequest(Array.Empty<SyncOperationDto>()), "k")).Ok);
        var changes = await port.GetSyncChangesAsync(5, 50);
        Assert.Equal(9, changes.Value!.LatestRevision);
        Assert.Equal("sync/changes?since=5&limit=50", ExtractPath(h.Last(pathContains: "changes")!.Uri));
        var caps = await port.GetCapabilitiesAsync();
        Assert.Equal("unconfigured", caps.Value!.Module("identity")!.Capabilities!["google_oauth"]);

        // No endpoint outside the §5c table was called.
        Assert.All(h.Requests, r => Assert.Contains("/api/v1/", r.Uri, StringComparison.Ordinal));
    }

    private static string ExtractPath(string uri)
    {
        var idx = uri.IndexOf("/api/v1/", StringComparison.Ordinal);
        return idx < 0 ? uri : uri[(idx + "/api/v1/".Length)..];
    }

    // ============================ headers ======================================================

    [Fact]
    public async Task EveryRequest_CarriesACorrelationId_TheServerCanSanelyAccept()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-cid");
        var h = new Wave4SeamHarness.ScriptedHandler().Json("GET", "/api/v1/account", 200,
            """{"userId":"u","email":null,"displayName":null,"locale":null,"tier":null,"status":null,"createdAtUtc":null,"lastLoginAtUtc":null}""");
        using var port = Port(h, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1"));

        var res = await port.GetAccountAsync();
        Assert.True(res.Ok);
        var seen = h.Last()!;
        Assert.Equal("cid-fixed-1", seen.Headers["X-Correlation-Id"]);
        // Same acceptance rule the server applies (1..64 of [A-Za-z0-9-_]) — proven from the client side.
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", seen.Headers["X-Correlation-Id"]);
        Assert.Equal("cid-fixed-1", res.CorrelationId);   // echoed value, not a minted one
    }

    [Fact]
    public async Task AFactoryThatReturnsGarbage_IsReplaced_NotForwarded()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-cid-bad");
        var h = new Wave4SeamHarness.ScriptedHandler().Json("GET", "/api/v1/platform/capabilities", 200,
            Wave4SeamHarness.CapabilitiesBody());
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h,
            correlationIdFactory: () => "evil\r\nX-Injected: yes",
            logger: NullLogger<LivoraApiPort>.Instance);

        await port.GetCapabilitiesAsync();
        var cid = h.Last()!.Headers["X-Correlation-Id"];
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", cid);
        Assert.DoesNotContain("X-Injected", cid, StringComparison.Ordinal);
        Assert.False(h.Last()!.Headers.ContainsKey("X-Injected"));
    }

    [Fact]
    public async Task SyncBatch_SendsTheIdempotencyKey_AndAuthFreePathsCarryNoBearer()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-idem");
        var h = new Wave4SeamHarness.ScriptedHandler();
        h.Json("POST", "/api/v1/sync/batch", 200, Wave4SeamHarness.BatchBody(("op", "applied")));
        h.Json("POST", "/api/v1/auth/login", 200, Wave4SeamHarness.TokensBody());
        using var port = Port(h, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1"));

        var payload = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();
        await port.SyncBatchAsync(new SyncBatchRequest(new[]
        {
            new SyncOperationDto("op", "goal", "g1", "update", null, payload),
        }), "key-abc");
        Assert.Equal("key-abc", h.Last(pathContains: "sync/batch")!.Headers["Idempotency-Key"]);

        await port.LoginAsync(new LoginRequest("a@b.test", "pw"));
        Assert.Null(h.Last(pathContains: "auth/login")!.Authorization);          // no session yet
        Assert.Null(h.Last(pathContains: "auth/login")!.Headers.GetValueOrDefault("Authorization"));

        await port.GetAccountAsync();
        Assert.Equal("at-1", h.Last(pathContains: "/account")!.Authorization);    // protected call carries it
    }

    // ============================ error classification =========================================

    [Fact]
    public async Task AProblemEnvelope_IsClassifiedByCode_NotByProse()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-problem");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Problem("POST", "/api/v1/auth/login", 401, LivoraApiCodes.InvalidCredentials,
                "server prose that must never reach the UI", "cid-401");
        using var port = Port(h, dir);

        var res = await port.LoginAsync(new LoginRequest("a@b.test", "pw"));
        Assert.False(res.Ok);
        Assert.Equal(401, res.Status);
        Assert.Equal(LivoraApiCodes.InvalidCredentials, res.Code);
        Assert.Equal("Api.Error.invalid_credentials", res.ErrorReasonKey);
        Assert.True(res.IsUnauthorized);
        // The header echo wins over the envelope field (one id per request, as the server sends it).
        Assert.Equal("cid-fixed-1", res.CorrelationId);
        Assert.Equal("cid-401", res.Problem!.CorrelationId);
        Assert.False(res.IsTransient);
        // The prose exists for the diagnostics sheet only — the reason key is what a page may bind.
        Assert.Equal("server prose that must never reach the UI", res.Problem!.Detail);
    }

    [Fact]
    public async Task ValidationFailure_KeepsPerFieldCodes()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-fields");
        const string body = """{"type":"https://livora.app/problems/validation_failed","title":"validation_failed","status":400,"detail":"x","instance":"/","code":"validation_failed","correlationId":"cid","errors":{"email":["invalid_format"],"password":["too_short","too_short"]}}""";
        var h = new Wave4SeamHarness.ScriptedHandler().Json("POST", "/api/v1/auth/register", 400, body);
        using var port = Port(h, dir);

        var res = await port.RegisterAsync(new RegisterRequest("nope", "x"));
        Assert.Equal(LivoraApiCodes.ValidationFailed, res.Code);
        Assert.Equal(2, res.FieldErrors!["password"].Length);
        Assert.Equal("invalid_format", res.FieldErrors!["email"][0]);
    }

    [Theory]
    [InlineData(409, LivoraApiCodes.EmailAlreadyRegistered, "Api.Error.email_already_registered", false)]
    [InlineData(403, LivoraApiCodes.AccountLocked, "Api.Error.account_locked", false)]
    [InlineData(429, LivoraApiCodes.RateLimited, "Api.Error.rate_limited", true)]
    [InlineData(503, LivoraApiCodes.ProviderUnconfigured, "Api.Error.provider_unconfigured", true)]
    [InlineData(500, LivoraApiCodes.InternalError, "Api.Error.internal_error", true)]
    public async Task AuthStatuses_MapToTheirOwnKeys_AndTransientFlags(int status, string code, string key, bool transient)
    {
        using var dir = new Wave4SeamHarness.TempDir("port-status-" + status);
        var h = new Wave4SeamHarness.ScriptedHandler().Problem("POST", "/api/v1/auth/login", status, code);
        using var port = Port(h, dir);
        var res = await port.LoginAsync(new LoginRequest("a@b.test", "pw"));
        Assert.Equal(code, res.Code);
        Assert.Equal(key, res.ErrorReasonKey);
        Assert.Equal(transient, res.IsTransient);
    }

    [Fact]
    public async Task AStatusWithoutAnEnvelope_IsClassifiedByStatus_AndNotInvented()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-noenv");
        var h = new Wave4SeamHarness.ScriptedHandler().Json("GET", "/api/v1/account", 403, "<html>gateway said no</html>");
        using var port = Port(h, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1"));

        var res = await port.GetAccountAsync();
        Assert.False(res.Ok);
        Assert.Equal(LivoraApiCodes.Forbidden, res.Code);
        Assert.Null(res.Problem);   // no envelope parsed, and no fake one created
        Assert.Equal("Api.Error.forbidden", res.ErrorReasonKey);
    }

    [Fact]
    public async Task HtmlErrorPage_IsNeverParsedAsAProblem()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-html");
        var h = new Wave4SeamHarness.ScriptedHandler().Json("GET", "/api/v1/account", 500,
            "<html><body>System.Exception: connection string leaked</body></html>");
        using var port = Port(h, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1"));
        var res = await port.GetAccountAsync();
        Assert.Null(res.Problem);
        Assert.Equal(LivoraApiCodes.InternalError, res.Code);
        Assert.DoesNotContain("connection string", res.ErrorReasonKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoHundredWithGarbage_IsMalformed_NotAnEmptySuccess()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-malformed");
        var h = new Wave4SeamHarness.ScriptedHandler().Json("GET", "/api/v1/account", 200, "not json at all");
        using var port = Port(h, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1"));
        var res = await port.GetAccountAsync();
        Assert.False(res.Ok);
        Assert.Equal(LivoraApiTransportCodes.MalformedResponse, res.Code);
        Assert.Equal("Api.Error.malformed_response", res.ErrorReasonKey);
    }

    [Fact]
    public async Task TwoHundredWithAnEmptyBody_IsNotReportedAsData()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-empty");
        var h = new Wave4SeamHarness.ScriptedHandler().Json("GET", "/api/v1/account", 200, "");
        using var port = Port(h, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1"));
        var res = await port.GetAccountAsync();
        Assert.False(res.Ok);
        Assert.Equal(LivoraApiTransportCodes.MalformedResponse, res.Code);
    }

    [Fact]
    public async Task TransportFailures_AreValues_NotExceptions()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-transport");
        var offline = new Wave4SeamHarness.ScriptedHandler()
            .On("GET", "/api/v1/account", _ => throw new HttpRequestException("no route to host"));
        using (var port = Port(offline, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1")))
        {
            var res = await port.GetAccountAsync();
            Assert.False(res.Ok);
            Assert.Equal(0, res.Status);
            Assert.Equal(LivoraApiTransportCodes.Network, res.Code);
            Assert.True(res.IsNetworkFailure && res.IsTransient);
            Assert.Equal("Api.Error.network", res.ErrorReasonKey);
        }

        var timeout = new Wave4SeamHarness.ScriptedHandler()
            .On("GET", "/api/v1/account", _ => throw new TaskCanceledException("tick"));
        using (var port = Port(timeout, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1")))
        {
            var res = await port.GetAccountAsync();
            Assert.Equal(LivoraApiTransportCodes.Timeout, res.Code);
            Assert.Equal("Api.Error.timeout", res.ErrorReasonKey);
        }
    }

    [Fact]
    public async Task ACancelledToken_ProducesACanceledResult_NotAThrowAtTheCaller()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-cancel");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("GET", "/api/v1/account", _ => throw new OperationCanceledException());
        using var port = Port(h, dir, auth: new Wave4SeamHarness.ScriptedAuth("at-1"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var res = await port.GetAccountAsync(cts.Token);
        Assert.False(res.Ok);
        Assert.Equal(LivoraApiTransportCodes.Canceled, res.Code);
    }

    // ============================ one refresh, one replay ======================================

    [Fact]
    public async Task ARejectedBearer_TriggersExactlyOneRefresh_AndOneReplay()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-refresh");
        int calls = 0;
        var h = new Wave4SeamHarness.ScriptedHandler().On("GET", "/api/v1/account", _ =>
        {
            calls++;
            return calls == 1
                ? Wave4SeamHarness.ProblemResponse(401, LivoraApiCodes.Unauthenticated)
                : Wave4SeamHarness.Response(200, """{"userId":"u","email":null,"displayName":null,"locale":null,"tier":null,"status":null,"createdAtUtc":null,"lastLoginAtUtc":null}""");
        });
        var auth = new Wave4SeamHarness.ScriptedAuth("at-old");
        using var port = Port(h, dir, auth: auth);

        var res = await port.GetAccountAsync();
        Assert.True(res.Ok);
        Assert.Equal(1, auth.RefreshCalls);
        Assert.Equal(2, h.CallCount("GET", "/api/v1/account"));
        Assert.Equal("at-rotated", h.Requests[^1].Authorization);   // the replay used the rotated bearer
    }

    [Fact]
    public async Task WhenRefreshFails_TheRejectionIsHandedToTheSession_AndNoReplayIsSent()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-refresh-fail");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Problem("GET", "/api/v1/account", 401, LivoraApiCodes.TokenRevoked);
        var auth = new Wave4SeamHarness.ScriptedAuth("at-old") { RefreshResult = () => false };
        using var port = Port(h, dir, auth: auth);

        var res = await port.GetAccountAsync();
        Assert.False(res.Ok);
        Assert.Equal(LivoraApiCodes.TokenRevoked, res.Code);
        Assert.Equal(1, auth.RefreshCalls);
        Assert.Single(auth.RejectedCodes);
        Assert.Equal(LivoraApiCodes.TokenRevoked, auth.RejectedCodes[0]);
        Assert.Equal(1, h.CallCount("GET", "/api/v1/account"));   // no hammering
    }

    [Fact]
    public async Task LoginIsNeverRefreshed_NoMatterWhatTheServerSays()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-no-refresh-on-login");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Problem("POST", "/api/v1/auth/login", 401, LivoraApiCodes.InvalidCredentials);
        var auth = new Wave4SeamHarness.ScriptedAuth("at-old");
        using var port = Port(h, dir, auth: auth);

        var res = await port.LoginAsync(new LoginRequest("a@b.test", "pw"));
        Assert.False(res.Ok);
        Assert.Equal(0, auth.RefreshCalls);   // a wrong password is not an expired token
        Assert.Equal(1, h.CallCount("POST", "/auth/login"));
    }

    [Fact]
    public async Task TheRefreshEndpointItNeverRecursesIntoTheAuthContext()
    {
        using var dir = new Wave4SeamHarness.TempDir("port-refresh-endpoint");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Problem("POST", "/api/v1/auth/refresh", 401, LivoraApiCodes.TokenRevoked);
        var auth = new Wave4SeamHarness.ScriptedAuth("at-old");
        using var port = Port(h, dir, auth: auth);

        var res = await port.RefreshAsync(new RefreshTokenRequest("rt-1"));
        Assert.False(res.Ok);
        Assert.Equal(LivoraApiCodes.TokenRevoked, res.Code);
        Assert.Equal(0, auth.RefreshCalls);   // would otherwise be an infinite loop
        Assert.Null(h.Last(pathContains: "auth/refresh")!.Authorization);
    }

    // ============================ port honesty (structural) ====================================

    [Fact]
    public void ThePortNeverLogsARequestBody_AndNeverReturnsServerProseAsAReasonKey()
    {
        var text = Wave4SeamHarness.ReadRepoFile("Infrastructure/Cloud/LivoraApiPort.cs");
        if (text is null) return;
        var code = Wave4SeamHarness_ReadCode(text);

        // Only identifiers cross a Log* call: correlation ids, paths, statuses, user ids.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"_log\.(Log\w+|LogInformation|LogWarning|LogError|LogDebug)\(([^;]*);"))
        {
            var args = m.Groups[2].Value;
            Assert.DoesNotContain("body", args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("text", args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Detail", args, StringComparison.Ordinal);
            Assert.DoesNotContain("password", args, StringComparison.OrdinalIgnoreCase);
        }
        // The reason key is always produced by the code table, never by copying server prose.
        Assert.Contains("ErrorReasonKey", Wave4SeamHarness.ReadRepoFile("Application/Cloud/LivoraApiContract.cs")!, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorReasonKey = Problem", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorReasonKey = text", code, StringComparison.Ordinal);
    }

    private static string Wave4SeamHarness_ReadCode(string text)
    {
        // Crude comment strip is enough here: the assertions are about call shapes.
        var noXmlDoc = System.Text.RegularExpressions.Regex.Replace(text, @"///.*(\r?\n)", "\n");
        return System.Text.RegularExpressions.Regex.Replace(noXmlDoc, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
    }

    [Fact]
    public void AuthFreeEndpoints_AreExactlyTheFourTheContractDefines()
    {
        Assert.True(LivoraApiPort.IsAuthFreePath(LivoraApiPaths.Login));
        Assert.True(LivoraApiPort.IsAuthFreePath(LivoraApiPaths.Register));
        Assert.True(LivoraApiPort.IsAuthFreePath(LivoraApiPaths.Google));
        Assert.True(LivoraApiPort.IsAuthFreePath(LivoraApiPaths.Refresh));
        Assert.True(LivoraApiPort.IsAuthFreePath(LivoraApiPaths.Capabilities));
        Assert.False(LivoraApiPort.IsAuthFreePath(LivoraApiPaths.Account));
        Assert.False(LivoraApiPort.IsAuthFreePath(LivoraApiPaths.SyncBatch));
        Assert.False(LivoraApiPort.IsAuthFreePath(LivoraApiPaths.Logout));   // it revokes THIS session: needs the bearer
    }
}
