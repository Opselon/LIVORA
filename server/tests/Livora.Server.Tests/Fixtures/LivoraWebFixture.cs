using Livora.Server;
using Livora.Server.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace Livora.Server.Tests;

/// <summary>
/// PURPOSE: the one and only way a Wave 4 backend test talks to the API — a real host, real
///          routing, real middleware, zero external dependencies.
/// OWNER: Agent 16 (shared fixture). Lanes may USE it, never modify it (modify = every lane's
///          assumptions break at once).
/// CONSUMES: <see cref="Program"/> from the host assembly.
/// PROVIDES: an HttpClient with correlation plumbing exercised, per-test isolated in-memory DB,
///           and JSON helpers that read the SHARED camelCase options.
/// INVARIANTS:
///   - Database:Provider=inmemory — a test never touches a file DB, so parallel runs cannot collide
///   - no test may claim a provider works; capability state is asserted as it truly is
///   - Environment=Development so /openapi is mapped and the OpenAPI document is itself a gate
/// EXTEND: add a fixture member only if several lanes need it; single-lane helpers go in the lane.
/// </summary>
public sealed class LivoraWebFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Http { get; private set; } = null!;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    // A per-fixture SQLite FILE, not the fake in-memory provider: sync/identity
                    // lanes assert real unique-index/FK behaviour, which the in-memory provider
                    // silently does not enforce. File lives in a temp dir, deleted on dispose.
                    ["Database:Provider"] = "sqlite",
                    ["Database:EnsureCreatedOnStart"] = "true",
                    ["Database:ApplyMigrationsOnStart"] = "false",
                    ["ConnectionStrings:Livora"] = $"Data Source={Path.Combine(DbDir, "fixture.db")}",
                }));
        });
        Directory.CreateDirectory(DbDir);
        Http = _factory.CreateClient();
        return Task.CompletedTask;
    }

    /// <summary>Isolated temp DB dir for THIS fixture instance (parallel-safe: never shared).</summary>
    public string DbDir { get; } = Path.Combine(Path.GetTempPath(),
        "livora-fixture-" + Guid.NewGuid().ToString("N")[..10]);

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(DbDir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        await Task.CompletedTask;
    }

    /// <summary>GET that must succeed and deserialise to <typeparamref name="T"/>.</summary>
    public async Task<T> GetAsync<T>(string path)
    {
        var res = await Http.GetAsync(path);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    /// <summary>Read the shared problem envelope from any non-success response.</summary>
    public static async Task<ApiProblem> ReadProblemAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<ApiProblem>(body, Json);
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Code),
            $"a problem response must carry a machine code, got body: {body}");
        return problem;
    }

    /// <summary>
    /// Mint a real access token through the host's own signing key, so an auth test proves the
    /// actual contract (claim names, validation parameters) instead of a hand-written JWT guess.
    /// Also guarantees the user row EXISTS: the fixture DB enforces real SQLite FKs (the whole
    /// point of using a file DB), and lane tests mint tokens for synthetic ids without going
    /// through registration first. Sync-over-async is not needed: SaveChanges() is synchronous EF.
    /// </summary>
    public string MintAccessToken(string userId, IEnumerable<string>? roles = null, string? sessionId = null)
    {
        var key = _factory.Services.GetRequiredService<LivoraSigningKey>();
        EnsureUserRow(userId);
        return AccessTokenMint.Create(key, userId, sessionId ?? Guid.NewGuid().ToString("N"),
            roles ?? [Roles.User]);
    }

    private void EnsureUserRow(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Livora.Server.Infrastructure.Persistence.LivoraDbContext>();
        if (!db.Users.Any(u => u.Id == userId))
        {
            db.Users.Add(new Livora.Server.Infrastructure.Persistence.UserAccount { Id = userId });
            db.SaveChanges();
        }
    }

    /// <summary>A client that sends <c>Authorization: Bearer …</c> on every request.</summary>
    public HttpClient CreateAuthenticatedClient(string userId, IEnumerable<string>? roles = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MintAccessToken(userId, roles));
        return client;
    }
}

/// <summary>Base class so every lane test gets the same real-host fixture semantics.</summary>
public abstract class LivoraApiTest : IClassFixture<LivoraWebFixture>
{
    protected LivoraApiTest(LivoraWebFixture fixture) => Fixture = fixture;

    protected LivoraWebFixture Fixture { get; }

    /// <summary>The one thing every Wave 4 endpoint must do: hand back a correlation id.</summary>
    protected static void AssertHasCorrelation(HttpResponseMessage res)
    {
        var echoed = res.Headers.TryGetValues(CorrelationMiddleware.HeaderName, out var values)
            ? string.Join("", values)
            : null;
        Assert.False(string.IsNullOrWhiteSpace(echoed), "response is missing the correlation id header");
    }
}
