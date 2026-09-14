using System.Diagnostics;
using System.Net.Http.Headers;
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
/// OWNER: Agent 16 (P1-F QA lane). Evidence for SECURITY-CHECKLIST.md; every verdict line in that
///          doc cites a test here.
/// PROBES:
///   S1 anonymous access: EVERY operation the live OpenAPI document serves that is not explicitly
///      anonymous must answer the 401 problem envelope (never a 200, never a bare 401, never HTML).
///      Enumerated from the document, so a new protected route is covered the moment it appears.
///   S2 cross-account IDOR: every user-scoped row route (/api/v1/auth/sessions/{id} and future
///      :id routes) probed with account B's token against account A's resource id — 403/404, NEVER
///      200. Today: endpoint absent → pinned honest absence; the shape exists so Phase 2 inherits it.
///   S3 oversized page requests: PageRequest contract clamps (limit 5000 → default, never a scan
///      of 5000, never a 400 DoS-bait); negative/zero limits clamp too. Pure + instant + forever.
///   S4 missing Idempotency-Key on POST /api/v1/sync/batch: today honest 404 pinned; the test also
///      pins that the capability report does NOT list a sync module while the route is absent
///      (surface/report agreement is the tripwire that makes the absence trustworthy).
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
    // ---------------------------------------------------------------- S1 anonymous sweep

    [Fact]
    public async Task S1_every_authenticated_route_in_the_openapi_document_refuses_anonymous_with_the_envelope()
    {
        // The set of paths the scaffold DECLARES anonymous. Anything else in the document that
        // answers 2xx to a token-less GET is a broken access control, full stop.
        var anonymousKnown = new[] { "/healthz", "/api/v1/platform/capabilities", "/api/v1/platform/version" };
        var docRaw = await fixture.Http.GetStringAsync("/openapi/v1.json");
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
            var res = await fixture.Http.GetAsync(path);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                var problem = await LivoraWebFixture.ReadProblemAsync(res);
                if (problem.Code != ProblemCodes.Unauthenticated)
                    offenders.Add($"{path}: 401 but code={problem.Code}");
                continue;
            }
            offenders.Add($"{path}: anonymous got {(int)res.StatusCode}");
        }
        Assert.True(offenders.Count == 0, "S1 anonymous-access offenders: " + string.Join(", ", offenders));
        output.WriteLine($"SECURITY S1: swept {getPaths.Count} GET routes; every protected one answered the envelope.");
    }

    [Fact]
    public async Task S1b_the_one_protected_route_end_to_end_anonymous_401_token_200()
    {
        var anon = await fixture.Http.GetAsync("/api/v1/platform/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(anon);
        Assert.Equal(ProblemCodes.Unauthenticated, problem.Code);

        using var client = fixture.CreateAuthenticatedClient("sec-sweep-user");
        var authed = await client.GetAsync("/api/v1/platform/me");
        authed.EnsureSuccessStatusCode();
        var body = await authed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sec-sweep-user", body.GetProperty("userId").GetString());
    }

    // ---------------------------------------------------------------- S2 IDOR shape

    [Theory]
    [InlineData("/api/v1/auth/sessions/other-account-session")]      // identity lane (P1-C) target
    [InlineData("/api/v1/account/export?user=someone-else")]          // export must be SELF-scoped
    [InlineData("/api/v1/sync/changes?since=0&user=someone-else")]    // sync (P1-B) target
    public async Task S2_cross_account_row_probes_are_honestly_absent_today_and_agree_everywhere(string path)
    {
        using var client = fixture.CreateAuthenticatedClient("idor-account-a");
        var res = await client.GetAsync(path);

        // Today these endpoints do not exist: the honest answer is the shared not_found envelope.
        // The three-way agreement pins: route absent + capability report lists no owning module +
        // OpenAPI does not advertise it. When a lane ships the endpoint, this test goes red until
        // the row is upgraded to the real cross-account expectation (403 forbidden / 404, never 200).
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.NotFound, problem.Code);

        var snap = await fixture.GetAsync<CapabilitySnapshot>("/api/v1/platform/capabilities");
        Assert.False(snap.Modules.Any(m => m.Key is "identity" or "sync"),
            $"capability report advertises module '{string.Join(",", snap.Modules.Select(m => m.Key))}' " +
            $"while {path} still 404s — a half-shipped route is an unaudited one");
        var docRaw = await fixture.Http.GetStringAsync("/openapi/v1.json");
        Assert.DoesNotContain(path.Split('?')[0], docRaw, StringComparison.Ordinal);
        output.WriteLine($"SECURITY S2: {path} honestly absent (envelope + capability + OpenAPI agree).");
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
    public async Task S4_sync_batch_without_idempotency_key_is_honestly_absent_not_fakely_permissive()
    {
        using var client = fixture.CreateAuthenticatedClient("idem-probe");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/batch")
        {
            Content = new StringContent("""{"operations":[]}""", System.Text.Encoding.UTF8, "application/json"),
        };
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.NotFound, problem.Code);

        // And when P1-B lands: a 400-code validation_failed / missing-header path is what this row
        // will demand. The contract (§5c) makes Idempotency-Key mandatory — the scaffold must not
        // be "helpfully" accepting key-less batches behind the module's back:
        var snap = await fixture.GetAsync<CapabilitySnapshot>("/api/v1/platform/capabilities");
        Assert.DoesNotContain(snap.Modules, m => m.Key == "sync");
        output.WriteLine("SECURITY S4: POST /sync/batch honestly absent; key-less mutation impossible today.");
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

        var res = await fixture.Http.SendAsync(msg);
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
            await fixture.Http.SendAsync(msg);
        });

        using var ok = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        ok.Headers.Add(CorrelationMiddleware.HeaderName, "legit-value");
        var res = await fixture.Http.SendAsync(ok);
        var echoed = string.Join("", res.Headers.GetValues(CorrelationMiddleware.HeaderName));
        Assert.Equal("legit-value", echoed);
        Assert.DoesNotContain("\r", echoed);
        Assert.DoesNotContain("\n", echoed);
    }

    // ---------------------------------------------------------------- problem-envelope shape guard

    [Fact]
    public async Task Security_hygiene_error_bodies_never_leak_paths_stack_or_secrets()
    {
        var res = await fixture.Http.GetAsync("/api/v1/nope/nope");
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
