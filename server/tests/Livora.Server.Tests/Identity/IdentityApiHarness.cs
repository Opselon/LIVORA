using System.Text;
using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Tests.Identity;

/// <summary>
/// PURPOSE: the shared plumbing every identity-lane test needs — REAL sign-ups over HTTP (never a
///          minted token pretending to be a session), raw-body posts so response BYTES can be
///          compared, and a second connection to the database file the host ACTUALLY opened, so
///          assertions can check what the DATABASE says (audit truth, session revocation) and not
///          only what HTTP echoed.
/// OWNER: Agent 03 (identity lane); harness added by repair lane R2. Lives in the identity test
///          folder; the frozen fixture owns the host, this file only adds verbs and a reader.
/// CONSUMES: <see cref="LivoraWebFixture"/> exactly as frozen — Http, Json, DbDir.
/// A REAL-PLATFORM FACT the identity tests must state (filed as R-r2-2 in
/// docs/architecture/wave4/requests/r2.md): the fixture injects its per-test connection string via
/// <c>ConfigureAppConfiguration</c>, which runs AFTER <c>Program.cs:31</c> has already read
/// <c>GetConnectionString("Livora")</c> and handed the appsettings default (<c>livora.db</c>) to
/// <c>AddLivoraDbContext</c>. So every fixture in the process shares <c>&lt;test bin&gt;/livora.db</c>
/// instead of its own file under DbDir. We do not touch the frozen fixture; instead <see cref="Db"/>
/// resolves THE FILE THAT ACTUALLY HOLDS THE LIVE SCHEMA by probing the candidates (fixture dir
/// first — correct the moment the lead fixes the fixture — then the bin-dir default), and tests use
/// unique emails/user ids so the shared file stays behaviourally isolated anyway.
/// INVARIANTS:
///   - a "signed-in user" here always comes from POST /auth/register or /auth/login, so every
///     protected-route assertion exercises the real session gate. <c>MintAccessToken</c> creates no
///     session row — tests use it only where the test is ABOUT the missing-session gate.
///   - emails are per-test unique: lockout state is keyed by normalized email; a reused mailbox
///     would couple two tests.
///   - no test sleeps: the lockout ladder is driven by real repeated logins at the config defaults;
///     window/expiry arithmetic is proven in IdentityPureMechanicsTests through the IClock seam.
/// </summary>
public abstract class IdentityApiHarness : LivoraApiTest
{
    private string? _dbPath;

    protected IdentityApiHarness(LivoraWebFixture fixture) : base(fixture) { }

    /// <summary>A mailbox no other test can collide with.</summary>
    protected static string NewEmail(string tag)
        => $"{tag}-{Guid.NewGuid().ToString("N")[..10]}@identity.test";

    protected static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>A bare anonymous client (the shared fixture client — no default Authorization).</summary>
    protected HttpClient Anon() => Fixture.Http;

    // ------------------------------------------------------------------ raw verb helpers --------

    protected async Task<(HttpStatusCode Status, string Text)> PostRawAsync(
        string path, string body, string? bearer = null, string? correlationId = null)
        => await SendRawAsync(HttpMethod.Post, path, body, bearer, correlationId);

    protected async Task<(HttpStatusCode Status, string Text)> SendRawAsync(
        HttpMethod method, string path, string? body = null, string? bearer = null,
        string? correlationId = null)
    {
        var (status, text, _) = await SendFullAsync(method, path, body, bearer, correlationId);
        return (status, text);
    }

    /// <summary>The same call with the X-Correlation-Id response header attached — tests that must
    /// prove the envelope is the PLATFORM's (correlation rides end-to-end) use this.</summary>
    protected async Task<(HttpStatusCode Status, string Text, string? Correlation)> PostFullAsync(
        string path, string body, string? bearer = null)
        => await SendFullAsync(HttpMethod.Post, path, body, bearer, correlationId: null);

    protected async Task<(HttpStatusCode Status, string Text, string? Correlation)> SendFullAsync(
        HttpMethod method, string path, string? body = null, string? bearer = null,
        string? correlationId = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = Json(body);
        if (bearer is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (correlationId is not null) req.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        var res = await Fixture.Http.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync(),
            res.Headers.TryGetValues("X-Correlation-Id", out var v) ? string.Join("", v) : null);
    }

    protected static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>Deserialize the shared problem envelope from a raw response body.</summary>
    protected static ApiProblem ProblemOf(string text)
        => JsonSerializer.Deserialize<ApiProblem>(text, LivoraWebFixture.Json)
           ?? throw new InvalidOperationException("not a problem envelope: " + text);

    // --------------------------------------------------------------------- signed-up users ------

    /// <summary>Register through the real endpoint; asserts the §5c 201 and returns the token
    /// payload. A broken register therefore fails its dependents loudly, not silently.</summary>
    protected async Task<Registered> RegisterAsync(
        string? email = null, string password = Password,
        string? deviceLabel = null, string? platform = null)
    {
        email ??= NewEmail("user");
        var (status, text) = await PostRawAsync("/api/v1/auth/register",
            JsonSerializer.Serialize(new { email, password, deviceLabel, platform }));
        Assert.Equal(HttpStatusCode.Created, status);
        var tokens = ReadTokens(text);
        return new Registered(email, password, tokens, text);
    }

    /// <summary>Login through the real endpoint (asserts 200).</summary>
    protected async Task<TokenBundle> LoginAsync(
        string email, string password, string? deviceLabel = null, string? platform = null)
    {
        var (status, text) = await PostRawAsync("/api/v1/auth/login",
            JsonSerializer.Serialize(new { email, password, deviceLabel, platform }));
        Assert.Equal(HttpStatusCode.OK, status);
        return ReadTokens(text);
    }

    /// <summary>The §5c token payload read with the shared camelCase options.</summary>
    protected static TokenBundle ReadTokens(string text)
    {
        var doc = Parse(text);
        var tokens = new TokenBundle(
            UserId: doc.GetProperty("userId").GetString()!,
            AccessToken: doc.GetProperty("accessToken").GetString()!,
            RefreshToken: doc.GetProperty("refreshToken").GetString()!,
            ExpiresAtUtc: doc.GetProperty("expiresAtUtc").GetDateTimeOffset(),
            SessionId: doc.GetProperty("sessionId").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.SessionId));
        return tokens;
    }

    /// <summary>POST /auth/refresh as a raw call (the status is the assertion).</summary>
    protected async Task<(HttpStatusCode Status, string Text)> RefreshAsync(string refreshToken)
        => await PostRawAsync("/api/v1/auth/refresh", JsonSerializer.Serialize(new { refreshToken }));

    // ------------------------------------------------------------------------ DB truth ---------

    /// <summary>Open the database FILE THE HOST IS ACTUALLY USING (see class doc — the fixture's
    /// intended per-test file may not be in effect; resolution is by live schema, not by guess).</summary>
    protected LivoraDbContext Db()
    {
        var path = _dbPath ??= ResolveLiveDatabase(Fixture.DbDir);
        var options = new DbContextOptionsBuilder<LivoraDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new SqliteConnectionInterceptor())
            .Options;
        return new LivoraDbContext(options);
    }

    private static string ResolveLiveDatabase(string fixtureDbDir)
    {
        // Candidates in priority order: the fixture's intended per-test file (wins the moment the
        // lead fixes R-r2-2), then the appsettings default resolved the way Microsoft.Data.Sqlite
        // resolves it (base directory, then CWD). A file QUALIFIES only if its schema has `users`.
        var candidates = new[]
        {
            Path.Combine(fixtureDbDir, "fixture.db"),
            Path.Combine(AppContext.BaseDirectory, "livora.db"),
            Path.Combine(Directory.GetCurrentDirectory(), "livora.db"),
        };
        foreach (var c in candidates)
        {
            try
            {
                if (!File.Exists(c)) continue;
                using var conn = new SqliteConnection($"Data Source={c};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='users'";
                if (Convert.ToInt64(cmd.ExecuteScalar()) == 1) return c;
            }
            catch (SqliteException) { /* another candidate may hold it */ }
        }
        throw new InvalidOperationException(
            "identity harness: no candidate database file carries the live schema — looked at " +
            string.Join(", ", candidates));
    }

    /// <summary>Wait (briefly, without sleeping on product timing — this is read-your-write
    /// visibility across two SQLite connections, not an expiry test) until a row becomes visible.</summary>
    protected async Task<T> ReadWhenPresentAsync<T>(Func<LivoraDbContext, Task<T?>> read, int attempts = 40)
    {
        for (var i = 0; i < attempts; i++)
        {
            using (var probe = Db())
            {
                var value = await read(probe);
                if (value is not null) return value;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("the row the HTTP response promised never appeared in the database");
    }

    protected const string Password = "correct-horse-battery-staple-9";

    /// <summary>The §5c token payload (userId, accessToken, refreshToken, expiresAtUtc, sessionId).</summary>
    protected sealed record TokenBundle(
        string UserId, string AccessToken, string RefreshToken,
        DateTimeOffset ExpiresAtUtc, string SessionId);

    protected sealed record Registered(string Email, string Password, TokenBundle Tokens, string Body);
}
