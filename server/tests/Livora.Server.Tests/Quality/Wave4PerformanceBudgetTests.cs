using System.Diagnostics;
using Livora.Server.Application;
using Livora.Server.Modules.Platform;
using Livora.Server.Tests;
using Xunit.Abstractions;

namespace Livora.Server.Tests.Quality;

/// <summary>
/// PURPOSE: Wave 4 performance budgets measured against the REAL host (WebApplicationFactory
///          in-process TestServer), so the numbers in the docs are reproducible receipts, not
///          estimates.
/// OWNER: Agent 16 (QA/performance).
/// BUDGETS (this lane's brief):
///   - p95 &lt; 50 ms for /healthz over 200 sequential requests
///   - p95 &lt; 50 ms for /api/v1/platform/capabilities over 200 sequential requests
///   - p95 &lt; 150 ms for a 50-operation POST /api/v1/sync/batch — ONLY if the sync module exists
///     at this HEAD. When it does not, the test asserts the ENDPOINT TRUTH instead (the honest
///     not_found envelope + the OpenAPI document agreeing) and records NOT_IMPLEMENTED in its
///     output. A silent return-skip would hide whether the surface exists at all.
/// MEASUREMENT HONESTY (stated in the report, repeated here): in-process TestServer numbers are
///          NOT production numbers — no network stack, no TLS, no disk contention, no cold JIT on
///          a shared CI runner. They prove the code path has no algorithmic blow-up and give a
///          regression tripwire; capacity planning needs a deployed environment.
/// INVARIANTS:
///   - serialised with the gate harness via Wave4SerialCollection: a dotnet build competing for
///     the same cores inflates latency measurements (measured — FLAKE-STORAGE-BUDGET.md), and a
///     latency test that races a build is a number nobody can reproduce
///   - single isolated run per budget, SEQUENTIAL requests (a flood's p95 measures the scheduler)
///   - warm-up requests first: JIT and first-request DI/EF model build are setup cost, not steady state
///   - every sample must be a SUCCESS response — a 500 measured at 2 ms is not a data point
///   - every number goes to the test output so docs quote measured values only
/// LIMITS (stated, not hidden): other xunit collections still run in parallel with this one (the
///          runner config that would disable that belongs to the shared project, not to a lane).
///          The 50 ms budget carries ~20-50x headroom over the measured ~1-3 ms; if a future CI
///          box shows inflation, the fix is the serial step proposed in CI-SERVER-JOB.md, not a
///          wider budget.
/// </summary>
[Collection(Wave4SerialCollection.Name)]
public sealed class Wave4PerformanceBudgetTests(LivoraWebFixture fixture, ITestOutputHelper output)
{
    private const int Requests = 200;

    [Fact]
    public async Task Healthz_p95_under_50ms_over_200_sequential_requests()
    {
        await AssertLatencyBudget("/healthz", maxP95Ms: 50);
    }

    [Fact]
    public async Task Capabilities_p95_under_50ms_over_200_sequential_requests()
    {
        // this endpoint runs a live EF probe per request, so it is the honest slow surface
        await AssertLatencyBudget("/api/v1/platform/capabilities", maxP95Ms: 50);
    }

    /// <summary>
    /// R3 UPDATE (14 Sep): P1-B landed the sync module at HEAD 1c7bd50, so the NOT_IMPLEMENTED
    /// branch below is now the path that WOULD be a lie if taken — the live measurement runs
    /// instead. Measured at this HEAD (in-process TestServer, per-fixture SQLite file DB, 50
    /// fresh `create` operations per batch, every batch a distinct Idempotency-Key so no sample is
    /// a cheap replay): p50 ≈ 38 ms, p95 ≈ 56 ms, max ≈ 58 ms. The budget stays the contract's
    /// 150 ms — that is ~2.7x headroom over the measured p95, a stated margin, not a number
    /// picked to pass. The endpoint's real shape is also pinned here (auth required, key
    /// mandatory, replay byte-equal) because the module/surface agreement is what makes the
    /// measured number mean anything.
    /// </summary>
    [Fact]
    public async Task Sync_batch_p95_under_150ms_or_endpoint_honestly_absent()
    {
        var snap = await fixture.GetAsync<CapabilitySnapshot>("/api/v1/platform/capabilities");
        var listsSync = snap.Modules.Any(m => m.Key == "sync");

        // The probe must be SIGNED IN: an anonymous call answers 401 (the policy, correctly), and
        // reading that as "the module and the surface disagree" was this test's own bug — the
        // original shape pre-dated the sync lane landing and assumed a 404 or an open 400.
        using var client = fixture.CreateAuthenticatedClient("perf-sync-batch-user");
        var probe = await PostBatchAsync(client, operations: 1);
        var honestAbsence = probe.StatusCode == HttpStatusCode.NotFound;

        if (honestAbsence)
        {
            // NOT_IMPLEMENTED path, kept live: if a future HEAD removes the module, the absence
            // must be three-way honest (route + capability report + OpenAPI), never a silent skip.
            output.WriteLine("BUDGET sync-batch: NOT_IMPLEMENTED at this HEAD " +
                             "(route answers the scaffold's honest not_found envelope).");
            Assert.False(listsSync,
                "the capability report lists a 'sync' module while POST /api/v1/sync/batch answers " +
                "not_found — a reported module with no surface is exactly what tripwires forbid");
            var docRaw = await fixture.Http.GetStringAsync("/openapi/v1.json");
            using var doc = JsonDocument.Parse(docRaw);
            var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
            Assert.DoesNotContain(paths, p => p.Contains("/sync/batch", StringComparison.OrdinalIgnoreCase));
            return;
        }

        // Surface/report agreement, in the direction that matters now: the module is listed AND
        // the surface answers success.
        Assert.True(listsSync,
            "POST /api/v1/sync/batch works but the capability report does not list 'sync' — " +
            "an undeclared live surface is unaudited");
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);

        // The §5c gates that make the latency number meaningful (not a stub that answers fast):
        // a missing Idempotency-Key is a refusal, and a replay is byte-for-byte the original 200.
        using (var noKey = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/batch")
               { Content = new StringContent("""{"operations":[]}""", System.Text.Encoding.UTF8, "application/json") })
        {
            var refused = await client.SendAsync(noKey);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var code = (await LivoraWebFixture.ReadProblemAsync(refused)).Code;
            Assert.Equal(ProblemCodes.ValidationFailed, code);
        }
        var replayBody = await BatchJson(2);
        var first = await PostBatchWithBodyAsync(client, replayBody);
        var replayed = await PostBatchWithBodyAsync(client, replayBody, reuseLastKey: true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replayed.Content.ReadAsStringAsync());

        for (int warm = 0; warm < 3; warm++)
        {
            var w = await PostBatchAsync(client, 50);
            w.EnsureSuccessStatusCode();
        }

        const int batches = 50;
        var samples = new double[batches];
        for (int i = 0; i < batches; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = await PostBatchAsync(client, 50);
            sw.Stop();
            Assert.Equal(HttpStatusCode.OK, res.StatusCode); // a failing response is not a sample
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        var p95 = Percentile(samples, 0.95);
        Report("POST /api/v1/sync/batch (50 ops)", samples, 150);
        Assert.True(p95 < 150,
            $"sync/batch p95 {p95:F2} ms exceeds the 150 ms budget (max {samples.Max():F2}, n={batches}) " +
            "— in-process TestServer, see class header");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private const string LastKeyMarker = "perf-sync-batch-key";
    private string _lastKey = LastKeyMarker;

    private async Task<HttpResponseMessage> PostBatchAsync(HttpClient client, int operations) =>
        await PostBatchWithBodyAsync(client, await BatchJson(operations));

    private Task<HttpResponseMessage> PostBatchWithBodyAsync(HttpClient client, string body,
        bool reuseLastKey = false)
    {
        if (!reuseLastKey) _lastKey = LastKeyMarker + "-" + Guid.NewGuid().ToString("N");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/batch")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Idempotency-Key", _lastKey);
        return client.SendAsync(req);
    }

    /// <summary>`create` kind + baseRevision 0 is the honest first-write shape; `upsert` is not a
    /// legal §5c kind and the server rejects it per-operation, which would measure the rejection
    /// path instead of the write path.</summary>
    private static Task<string> BatchJson(int operations) => Task.FromResult(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            operations = Enumerable.Range(0, operations).Select(i => new
            {
                operationId = $"gate-{Guid.NewGuid():N}",
                entityType = "history",
                entityId = $"d-{Guid.NewGuid():N}-{i}",
                kind = "create",
                baseRevision = 0L,
                payload = new { v = i },
            }).ToArray(),
        }));


    private async Task AssertLatencyBudget(string path, double maxP95Ms)
    {
        for (int i = 0; i < 10; i++)
        {
            var warm = await fixture.Http.GetAsync(path);
            warm.EnsureSuccessStatusCode();
        }

        var samples = new double[Requests];
        for (int i = 0; i < Requests; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = await fixture.Http.GetAsync(path);
            sw.Stop();
            res.EnsureSuccessStatusCode(); // a failing request is not a latency sample
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Report(path, samples, maxP95Ms);
        var p95 = Percentile(samples, 0.95);
        Assert.True(p95 < maxP95Ms,
            $"{path} p95 {p95:F2} ms exceeds the {maxP95Ms:F0} ms budget (p50 {Percentile(samples, 0.50):F2}, " +
            $"p99 {Percentile(samples, 0.99):F2}, max {samples.Max():F2}, n={Requests}) " +
            "— in-process TestServer, see class header");
    }

    private void Report(string what, double[] samples, double budget) =>
        output.WriteLine($"PERF {what}: p50 {Percentile(samples, 0.50):F2} ms · p95 {Percentile(samples, 0.95):F2} ms · " +
                         $"p99 {Percentile(samples, 0.99):F2} ms · max {samples.Max():F2} ms · " +
                         $"budget {budget:F0} ms · n={samples.Length} (in-process TestServer — NOT production numbers)");

    private static double Percentile(double[] values, double p)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
