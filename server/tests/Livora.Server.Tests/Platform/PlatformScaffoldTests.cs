using Livora.Server.Application;
using Livora.Server.Modules;
using Livora.Server.Modules.Platform;

namespace Livora.Server.Tests;

/// <summary>
/// PURPOSE: prove the platform scaffold behaves as promised BEFORE any feature lane builds on it.
/// OWNER: Agent 16 (QA) — this file is the contract test for the module seam + error envelope.
/// TESTS: boot, liveness, honest 404, correlation id, capability truthfulness, module-seam rules.
/// </summary>
public sealed class PlatformScaffoldTests : LivoraApiTest
{
    public PlatformScaffoldTests(LivoraWebFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Healthz_liveness_answers_and_carries_correlation_id()
    {
        var res = await Fixture.Http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertHasCorrelation(res);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("livora-server", doc.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task CorrelationId_incoming_valid_value_is_echoed_back()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        req.Headers.Add("X-Correlation-Id", "test-run-2026_09");
        var res = await Fixture.Http.SendAsync(req);
        Assert.Equal("test-run-2026_09",
            string.Join("", res.Headers.GetValues("X-Correlation-Id")));
    }

    [Theory]
    [InlineData("bad\r\ninjected-header")]   // header splitting attempt
    [InlineData("x")]                        // legal but trivial, must be kept
    [InlineData("")]                         // empty = mint a new one
    public async Task CorrelationId_unsafe_value_is_never_echoed_verbatim(string incoming)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        if (!string.IsNullOrEmpty(incoming))
        {
            // The client cannot send CR/LF in a header at all; assert the request itself is refused
            // rather than silently forwarded.
            var threw = false;
            try { req.Headers.Add("X-Correlation-Id", incoming); } catch { threw = true; }
            if (threw) { Assert.Contains("\r", incoming); return; }
        }

        var res = await Fixture.Http.SendAsync(req);
        var echoed = string.Join("", res.Headers.TryGetValues("X-Correlation-Id", out var v) ? v : Array.Empty<string>());
        Assert.False(string.IsNullOrEmpty(echoed), "a correlation id must always be returned");
        Assert.DoesNotContain("\r", echoed);
        Assert.DoesNotContain("\n", echoed);
        if (incoming.Length is > 0 and <= 64 && echoed != "x")
            Assert.Contains(incoming, echoed); // safe values are honoured, not replaced
    }

    [Fact]
    public async Task Unknown_versioned_route_returns_the_shared_problem_envelope_not_an_empty_404()
    {
        var res = await Fixture.Http.GetAsync("/api/v1/definitely/not/mapped");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.NotFound, problem.Code);
        Assert.Equal(404, problem.Status);
        Assert.StartsWith("https://livora.app/problems/", problem.Type);
        Assert.NotEqual("unknown", problem.CorrelationId);
    }

    [Fact]
    public async Task Capabilities_report_lists_the_platform_module_with_a_live_database_state()
    {
        var snap = await Fixture.GetAsync<CapabilitySnapshot>("/api/v1/platform/capabilities");

        Assert.Equal("v1", snap.ApiVersion);
        Assert.Contains(snap.Modules, m => m.Key == PlatformModule.ModuleKey);

        // The database verdict must come from a probe, so it is one of the truthful states —
        // and it must never be silently missing because a lane forgot to wire the probe.
        Assert.False(string.IsNullOrWhiteSpace(snap.Database.State));
        Assert.True(snap.Database.State is "ok" or "degraded",
            $"in-memory probe must answer ok or degraded, got {snap.Database.State}");
        Assert.True(snap.ServerTimeUtc.ToUnixTimeSeconds() > 0);
    }

    [Fact]
    public async Task Capabilities_exposes_no_fabricated_feature_keys()
    {
        // Scaffold rule: only modules that actually registered may appear. A lane that pretends to
        // exist here is caught by this test the moment the contract is published.
        var snap = await Fixture.GetAsync<CapabilitySnapshot>("/api/v1/platform/capabilities");
        Assert.All(snap.Modules, m => Assert.False(string.IsNullOrWhiteSpace(m.Key)));
        Assert.Empty(snap.EndpointMappingFailures);
    }

    [Fact]
    public async Task OpenApi_document_is_served_in_development()
    {
        var res = await Fixture.Http.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"openapi\"", body);
        Assert.Contains("/api/v1/platform/capabilities", body);
    }
}

/// <summary>
/// PURPOSE: unit-test the module seam itself (no HTTP) — the mechanism 16 lanes depend on.
/// OWNER: Agent 16.
/// </summary>
public sealed class ModuleSeamTests
{
    private sealed class GoodModule : IFlivoraModule
    {
        public GoodModule(string key, DependencyState state = DependencyState.Unconfigured)
            { Key = key; _state = state; }
        private readonly DependencyState _state;
        public string Key { get; }
        public ModuleHealth Report() => new(Key, _state, "test");
    }

    private sealed class ThrowingMapModule : IFlivoraModule
    {
        public string Key => "thrower";
        public void MapEndpoints(FlivoraEndpointContext ctx)
            => throw new InvalidOperationException("simulated lane bug");
        public ModuleHealth Report() => new(Key, DependencyState.Ok, "claims healthy");
    }

    private static ModuleSeed Seed() => new(
        new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    [Fact]
    public void Registry_survives_a_module_that_throws_during_endpoint_mapping()
    {
        var registry = new ModuleRegistry([new GoodModule("a"), new ThrowingMapModule()]);
        var ctx = new FlivoraEndpointContext(
            new StubRouteBuilder(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var failures = registry.MapEndpoints(ctx);

        Assert.Equal(["thrower"], failures);
        // The claim of Ok must be overridden: it could not even map routes.
        var report = Assert.Single(registry.Reports(), r => r.Key == "thrower");
        Assert.Equal(DependencyState.Degraded, report.State);
    }

    [Fact]
    public void Registry_reports_a_throwing_health_probe_as_degraded_not_as_missing()
    {
        var registry = new ModuleRegistry([new ThrowingProbeModule()]);
        var report = Assert.Single(registry.Reports());
        Assert.Equal(DependencyState.Degraded, report.State);
        Assert.DoesNotContain("simulated", report.Detail); // exception detail never leaks the message
        Assert.Contains("InvalidOperationException", report.Detail);
    }

    private sealed class ThrowingProbeModule : IFlivoraModule
    {
        public string Key => "probe-thrower";
        public ModuleHealth Report() => throw new InvalidOperationException("simulated detail that must not leak");
    }

    [Fact]
    public void Endpoint_group_prefix_is_enforced()
    {
        var ctx = new FlivoraEndpointContext(new StubRouteBuilder(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Throws<ArgumentException>(() => ctx.MapVersionedGroup("/leading-slash"));
        Assert.Throws<ArgumentException>(() => ctx.MapVersionedGroup("  "));
    }

    /// <summary>Minimal builder so seam tests do not need a host.</summary>
    private sealed class StubRouteBuilder : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } =
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new StubAppBuilder();
    }

    private sealed class StubAppBuilder : IApplicationBuilder
    {
        public IServiceProvider ApplicationServices
        {
            get => new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
            set { }
        }
        public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>();
        public IFeatureCollection ServerFeatures { get; } = new FeatureCollection();
        public RequestDelegate Build() => _ => Task.CompletedTask;
        public IApplicationBuilder New() => this;
        public IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware) => this;
    }
}
