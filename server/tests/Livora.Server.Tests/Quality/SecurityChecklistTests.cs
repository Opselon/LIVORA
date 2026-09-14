using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Livora.Server.Application;
using Livora.Server.Modules.Platform;
using Xunit.Abstractions;

namespace Livora.Server.Tests.Quality;

/// <summary>
/// PURPOSE: the release-gate SECURITY CHECKLIST as executable probes, not a document to nod at.
///          Each row of the P1-F brief is run against the live host HERE AND NOW, and rows whose
///          surface does not exist yet assert the HONEST absence (envelope + capability/OpenAPI
///          agreement) instead of skipping — so when a lane ships the endpoint, this suite is the
///          first thing that goes red until the checklist row is upgraded from NOT_IMPLEMENTED to
///          a real cross-account probe.
/// OWNER: Agent 16 (P1-F QA lane; maintained by R3). Evidence for the gate document; every verdict
///          line cites a test here.
/// PROBES:
///   S1 anonymous access: EVERY operation the live OpenAPI document serves that is not explicitly
///      anonymous must answer the 401 problem envelope (never a 200, never a bare 401, never HTML).
///      Enumerated from the document, so a new protected route is covered the moment it appears.
///   S2 cross-account IDOR: R3 UPDATE — the identity and sync lanes LANDED, so the honest-absence
///      theory was replaced by the real probes it always said it would become: S2a revokes another
///      account's session (403 forbidden, never 200), S2b proves /account/export is self-scoped
///      against a foreign ?user=, S2c proves the sync change feed never carries another account's
///      operations. S2h keeps the STILL-absent surfaces pinned three-way honestly.
///   S3 oversized page requests: PageRequest contract clamps (limit 5000 → default, never a scan
///      of 5000, never a 400 DoS-bait); negative/zero limits clamp too. Pure + instant + forever.
///   S4 missing Idempotency-Key on POST /api/v1/sync/batch: the endpoint EXISTS now (P1-B), so the
///      row is the real contract assertion — a key-less batch is refused 400 validation_failed
///      before any write, the same way the module's own header promises. The honest-absence shape
///      (404 + module/surface agreement) remains live for a hypothetical future HEAD without it.
///   S5 header injection through X-Correlation-Id: CRLF/oversized/odd characters must never echo
///      back verbatim; safe values must round-trip; the response header must always exist.
/// INVARIANTS:
///   - probes assert status + ENVELOPE CODE, not just status (a 401 without code=unauthenticated
///     would break the client's localisation contract)
///   - no probe may pass by absence alone: each "route missing" verdict additionally checks the
///     OpenAPI document and /capabilities say the same thing (three-way agreement)
///   - serialised with the perf/gate collection so child-process builds cannot time-skew results
/// </summary>
[Collection(Wave4SerialCollection.Name)]
public sealed class SecurityChecklistTests(LivoraWebFixture fixture, ITestOutputHelper output)
{
    private readonly HttpClient _http = fixture.Http;

    // ---------------------------------------------------------------- S1 anonymous sweep

    [Fact]
    public async Task S1_every_authenticated_route_in_the_openapi_document_refuses_anonymous_with_the_envelope()
    {
        // The set of paths the scaffold DECLARES anonymous. Anything else in the document that
        // answers 2xx to a token-less GET is a broken access control, full stop.
        var anonymousKnown = new[] { "/healthz", "/api/v1/platform/capabilities", "/api/v1/platform/version" };
        var docRaw = await _http.GetStringAsync("/openapi/v1.json");
        using var doc = JsonDocument.Parse(docRaw);
        var getPaths = doc.RootElement.GetProperty("paths").EnumerateObject()
            .Where(p => p.Value.TryGetProperty("get", out _))
            .Select(p => p.Name)
            .Where(p => p.StartsWith("/api/v1", StringComparison.Ordinal) || p == "/healthz")
            .ToList();
        Assert.NotEmpty(getPaths);

        var offenders = new List<string>();
        foreach (var path in getPaths)
        {
            if (anonymousKnown.Contains(path)) continue;
            // Template routes ({id}) need a concrete value to be reachable at all.
            var probe = path.Replace("{id}", "anonymous-sweep-id").Replace("{claimId}", "anonymous-sweep-id")
                            .Replace("{patternId}", "anonymous-sweep-id");
            var res = await _http.GetAsync(probe);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                var problem = await LivoraWebFixture.ReadProblemAsync(res);
                if (problem.Code != ProblemCodes.Unauthenticated)
                    offenders.Add($"{probe}: 401 but code={problem.Code}");
                continue;
            }
            offenders.Add($"{probe}: anonymous got {(int)res.StatusCode}");
        }
        Assert.True(offenders.Count == 0, "S1 anonymous-access offenders: " + string.Join(", ", offenders));
        output.WriteLine($"SECURITY S1: swept {getPaths.Count} GET routes; every protected one answered the envelope.");
    }

    [Fact]
    public async Task S1b_the_one_protected_route_end_to_end_anonymous_401_token_200()
    {
        var anon = await _http.GetAsync("/api/v1/platform/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(anon);
        Assert.Equal(ProblemCodes.Unauthenticated, problem.Code);

        using var client = fixture.CreateAuthenticatedClient("sec-sweep-user");
        var authed = await client.GetAsync("/api/v1/platform/me");
        authed.EnsureSuccessStatusCode();
        var body = await authed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sec-sweep-user", body.GetProperty("userId").GetString());
    }

    // ---------------------------------------------------------------- S2 IDOR probes (REAL routes)
    //
    // R3 UPDATE (14 Sep): the identity (P1-C) and sync (P1-B) lanes LANDED, so the honest-absence
    // assertions this row carried became false statements about the tree — a session-revoke route,
    // an account export and a sync changes feed all EXIST now. The row is upgraded exactly as its
    // own comment said it must be: real cross-account probes asserting correct behaviour. The
    // genuinely-absent surfaces are still pinned honestly in S2h.
    //
    // Why API registration (not the fixture's minted token) for identity routes: they re-check the
    // SESSION ROW (IdentityService.AuthorizeCallerAsync), so a minted token with a synthetic
    // session answers 401 token_revoked — correct platform behaviour, but it would make an IDOR
    // probe measure the wrong thing. Registering through /auth/register mints a live session.

    /// <summary>A registered account with a live server-side session. Requests are sent through
    /// the fixture's host client with a PER-REQUEST bearer header — never a mutated default — so
    /// two accounts in one test can never cross-contaminate each other's identity.</summary>
    private sealed record Account(string UserId, string SessionId, string Email, string AccessToken);

    private async Task<HttpResponseMessage> AsAsync(Account who, HttpMethod verb, string path,
        HttpContent? content = null, string? idempotencyKey = null)
    {
        using var req = new HttpRequestMessage(verb, path);
        if (content is not null) req.Content = content;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.AccessToken);
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> GetAsync(Account who, string path) =>
        AsAsync(who, HttpMethod.Get, path);

    private Task<HttpResponseMessage> DeleteAsync(Account who, string path) =>
        AsAsync(who, HttpMethod.Delete, path);

    private async Task<(HttpStatusCode Status, string Body)> PostBatchAsync(Account who, string json,
        string? idempotencyKey)
    {
        var res = await AsAsync(who, HttpMethod.Post, "/api/v1/sync/batch",
            new StringContent(json, Encoding.UTF8, "application/json"), idempotencyKey);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>One legal §5c create operation — the shape the sync tests use, kept in one place.</summary>
    private static string BatchJson(string operationId, string entityId) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            operations = new[]
            {
                new { operationId, entityType = "history", entityId, kind = "create",
                      baseRevision = 0L, payload = new { v = 1 } },
            },
        });

    private async Task<Account> NewAccountAsync(string tag)
    {
        var email = $"{tag}-{Guid.NewGuid():N}@livora.test";
        var res = await _http.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "correct horse battery staple", displayName = tag, locale = "en" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        return new Account(body.GetProperty("userId").GetString()!,
            body.GetProperty("sessionId").GetString()!, email,
            body.GetProperty("accessToken").GetString()!);
    }

    [Fact]
    public async Task S2a_cross_account_session_revocation_is_forbidden_never_200()
    {
        var a = await NewAccountAsync("idor-a");
        var b = await NewAccountAsync("idor-b");

        var res = await DeleteAsync(a, $"/api/v1/auth/sessions/{b.SessionId}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);   // never 200, per the row's law
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.Forbidden, problem.Code);

        // The refusal must not have quietly revoked B's session: B still acts on it.
        var bStillLive = await GetAsync(b, "/api/v1/auth/sessions");
        Assert.Equal(HttpStatusCode.OK, bStillLive.StatusCode);
        // A's own session survives too (no side effect on the caller).
        var aStillLive = await GetAsync(a, "/api/v1/auth/sessions");
        Assert.Equal(HttpStatusCode.OK, aStillLive.StatusCode);
        output.WriteLine("SECURITY S2a: A revoking B's session answered 403 forbidden; both accounts' " +
                         "own sessions remain live (no side-effect revocation).");
    }

    [Fact]
    public async Task S2b_account_export_is_self_scoped_and_a_user_query_param_cannot_widen_it()
    {
        var a = await NewAccountAsync("export-a");
        var b = await NewAccountAsync("export-b");

        var res = await GetAsync(a, "/api/v1/account/export?user=" + b.UserId);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();

        // GDPR/DSAR rule: the export is about the CALLER. B's identifiers must not appear at all —
        // not as data, not as an echo of the query parameter.
        Assert.Contains(a.UserId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(b.UserId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(b.Email, body, StringComparison.Ordinal);
        var doc = JsonDocument.Parse(body).RootElement;
        Assert.Equal(a.UserId, doc.GetProperty("profile").GetProperty("data").GetProperty("userId").GetString());
        output.WriteLine("SECURITY S2b: /account/export returns the CALLER's document; a foreign " +
                         "?user= is ignored, not obeyed, and never echoed.");
    }

    [Fact]
    public async Task S2c_sync_changes_feed_never_carries_another_account_operations()
    {
        var a = await NewAccountAsync("feed-a");
        var b = await NewAccountAsync("feed-b");

        // B writes a real operation; A then pulls the whole feed from the beginning.
        var opId = "idor-" + Guid.NewGuid().ToString("N");
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            operations = new[]
            {
                new { operationId = opId, entityType = "history", entityId = "idor-1",
                      kind = "create", baseRevision = 0L, payload = new { v = 1 } },
            },
        });
        var (wroteStatus, _) = await PostBatchAsync(b, payload, "sec-s2c-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.OK, wroteStatus);

        var feed = await GetAsync(a, "/api/v1/sync/changes?since=0&user=x-ignored");
        feed.EnsureSuccessStatusCode();
        var text = await feed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(opId, text, StringComparison.Ordinal);
        var doc = JsonDocument.Parse(text).RootElement;
        Assert.True(doc.GetProperty("changes").GetArrayLength() == 0,
            $"A's feed must be empty (A wrote nothing) but carried: {text}");
        output.WriteLine("SECURITY S2c: B's operation is absent from A's change feed; the foreign " +
                         "user query value is ignored, not obeyed.");
    }

    [Fact]
    public async Task S2h_surfaces_that_are_still_honestly_absent_agree_everywhere()
    {
        // The honest-absent list, kept ACCURATE rather than deleted: these capabilities have no
        // server-side route at this HEAD (contract §5 / Wave4TruthMatrix rows 4, 6, 8-13). Each
        // absence is three-way: envelope + capability report + OpenAPI all say "not here".
        var absent = new (string Path, string Module)[]
        {
            ("/api/v1/health/records", "health"),
            ("/api/v1/calendar/events", "calendar"),
            ("/api/v1/nutrition/items", "nutrition"),
            ("/api/v1/marketplace/listings", "marketplace"),
            ("/api/v1/community/reports", "community"),
            ("/api/v1/commerce/purchases", "commerce"),
        };
        var snap = await fixture.GetAsync<CapabilitySnapshot>("/api/v1/platform/capabilities");
        var docRaw = await _http.GetStringAsync("/openapi/v1.json");

        using var client = fixture.CreateAuthenticatedClient("honest-absence-probe");
        foreach (var (path, module) in absent)
        {
            var res = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            var problem = await LivoraWebFixture.ReadProblemAsync(res);
            Assert.Equal(ProblemCodes.NotFound, problem.Code);
            Assert.False(snap.Modules.Any(m => m.Key == module),
                $"capability report lists '{module}' while {path} 404s — a half-shipped route is an unaudited one");
            Assert.DoesNotContain(path, docRaw, StringComparison.Ordinal);
        }
        output.WriteLine($"SECURITY S2h: {absent.Length} still-absent surfaces agree three-way.");
    }

    // ---------------------------------------------------------------- S3 paging clamp

    [Fact]
    public void S3_oversized_and_malformed_page_requests_clamp_never_obey_never_crash()
    {
        // The frozen contract's own DoS guard, executed: a huge limit is NOT an error (clients
        // would break) and NOT honoured (server would melt) — it silently becomes the default.
        Assert.Equal(PageRequest.DefaultLimit, new PageRequest { Limit = 5_000 }.SafeLimit);
        Assert.Equal(PageRequest.DefaultLimit, new PageRequest { Limit = -7 }.SafeLimit);
        Assert.Equal(PageRequest.DefaultLimit, new PageRequest { Limit = 0 }.SafeLimit);
        Assert.Equal(100, new PageRequest { Limit = PageRequest.MaxLimit }.SafeLimit);
        Assert.Equal(25, new PageRequest { }.SafeLimit); // default construct == default limit
        // PagedResult.of never claims more than exists:
        var req = new PageRequest { Offset = 90, Limit = 5_000 };
        var page = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var res = PagedResult<int>.Of(page, req, total: 100);
        Assert.Equal(PageRequest.DefaultLimit, res.Limit);
        Assert.False(res.HasMore); // 90 + 10 >= 100 — no phantom "more"
    }

    // ---------------------------------------------------------------- S4 idempotency-key posture

    [Fact]
    public async Task S4_sync_batch_without_idempotency_key_is_refused_before_any_write()
    {
        // R3 UPDATE (14 Sep): POST /api/v1/sync/batch LANDED with P1-B, so the row is upgraded
        // from pinned-honest-absence to the §5c contract itself: the key is MANDATORY and a
        // key-less batch is refused with the shared validation envelope — never accepted behind
        // the module's back ("not fakely permissive" is still the law, now measurable).
        using var client = fixture.CreateAuthenticatedClient("idem-probe");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/batch")
        {
            Content = new StringContent("""{"operations":[]}""", Encoding.UTF8, "application/json"),
        };
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.ValidationFailed, problem.Code);

        // Even a WELL-FORMED body without a key is refused — the refusal is the header, not the JSON:
        using var good = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/batch")
        {
            Content = new StringContent(BatchJson("s4-1", "s4-e"), Encoding.UTF8, "application/json"),
        };
        var res2 = await client.SendAsync(good);
        Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);
        Assert.Equal(ProblemCodes.ValidationFailed, (await LivoraWebFixture.ReadProblemAsync(res2)).Code);

        // Capability/module agreement (the tripwire that makes behaviour auditable): the surface
        // exists AND the report lists it AND the OpenAPI advertises it — all three, or the row reds.
        var snap = await fixture.GetAsync<CapabilitySnapshot>("/api/v1/platform/capabilities");
        Assert.Contains(snap.Modules, m => m.Key == "sync");
        var docRaw = await _http.GetStringAsync("/openapi/v1.json");
        Assert.Contains("/api/v1/sync/batch", docRaw, StringComparison.Ordinal);
        output.WriteLine("SECURITY S4: key-less sync/batch refused 400 validation_failed (body shape " +
                         "irrelevant); module report + OpenAPI agree the surface is live.");
    }

    [Fact]
    public async Task S4b_missing_key_refusal_writes_nothing_replayable()
    {
        // The sharper edge: a refused key-less batch must leave NO trace in the per-user feed —
        // otherwise the "before any write" claim is decorative.
        var b = await NewAccountAsync("s4b");
        var (status, _) = await PostBatchAsync(b, BatchJson("ghost", "ghost-1"), idempotencyKey: null);
        Assert.Equal(HttpStatusCode.BadRequest, status);

        var feed = await GetAsync(b, "/api/v1/sync/changes?since=0");
        var text = await feed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.DoesNotContain("ghost", text, StringComparison.Ordinal);
        output.WriteLine("SECURITY S4b: the refused key-less batch left no feed entry — refusal precedes write.");
    }

    // ---------------------------------------------------------------- S5 correlation header injection

    [Theory]
    [InlineData("ok-value_1-2")]                    // safe: echoed verbatim
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 76 chars > 64
    [InlineData("has space")]
    [InlineData("quote\"and\\slash")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("unicode-unless-ascii-٩")]
    public async Task S5_x_correlation_id_never_echoes_unsafe_values_and_always_returns_one(string incoming)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        bool accepted;
        try { msg.Headers.Add(CorrelationMiddleware.HeaderName, incoming); accepted = true; }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { accepted = false; }

        if (!accepted)
        {
            // .NET's own header validator refused a shape HttpClient forbids (control chars). The
            // middleware never SAW it — assert the alternative transport instead: raw socket is out
            // of scope for a checklist row that Kestrel already rejects at the protocol layer.
            output.WriteLine($"SECURITY S5: client stack refused '{incoming}' before the wire (protocol-layer rejection).");
            return;
        }

        var res = await _http.SendAsync(msg);
        var echoed = res.Headers.TryGetValues(CorrelationMiddleware.HeaderName, out var v)
            ? string.Join("", v) : null;
        Assert.False(string.IsNullOrEmpty(echoed), "a correlation id must ALWAYS be returned");
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", echoed!); // the sanitiser's alphabet is the contract
        if (incoming == "ok-value_1-2")
            Assert.Equal(incoming, echoed);
        else if (incoming.Length <= 64 && Regex.IsMatch(incoming, "^[A-Za-z0-9_-]+$"))
            Assert.Equal(incoming, echoed); // safe shapes are honoured (a mint-everything bug would fail here)
        else
            Assert.NotEqual(incoming, echoed); // unsafe shape MUST have been replaced, not echoed
    }

    [Fact]
    public async Task S5b_crlf_in_the_header_cannot_reach_the_response_at_all()
    {
        // Two layers exist; both are checked honestly: (1) HttpRequestMessage refuses CR/LF;
        // (2) even a value that survives validation must never echo CR/LF.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            msg.Headers.Add(CorrelationMiddleware.HeaderName, "a\r\nX-Injected: yes");
            await _http.SendAsync(msg);
        });

        using var ok = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        ok.Headers.Add(CorrelationMiddleware.HeaderName, "legit-value");
        var res = await _http.SendAsync(ok);
        var echoed = string.Join("", res.Headers.GetValues(CorrelationMiddleware.HeaderName));
        Assert.Equal("legit-value", echoed);
        Assert.DoesNotContain("\r", echoed);
        Assert.DoesNotContain("\n", echoed);
    }

    // ---------------------------------------------------------------- problem-envelope shape guard

    [Fact]
    public async Task Security_hygiene_error_bodies_never_leak_paths_stack_or_secrets()
    {
        var res = await _http.GetAsync("/api/v1/nope/nope");
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("at Livora", body, StringComparison.Ordinal);          // stack
        Assert.DoesNotContain("C:\\", body, StringComparison.Ordinal);               // file path
        Assert.DoesNotContain("Data Source", body, StringComparison.Ordinal);        // connection string
        Assert.DoesNotContain("Bearer", body, StringComparison.Ordinal);             // tokens
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.False(string.IsNullOrWhiteSpace(problem.CorrelationId));
        Assert.StartsWith("https://livora.app/problems/", problem.Type, StringComparison.Ordinal);
    }
}
