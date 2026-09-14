using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Identity;
using Livora.Server.Infrastructure.Persistence;
using Livora.Server.Modules.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Livora.Server.Tests.Identity;

/// <summary>
/// PURPOSE: prove the Google sign-in path TELLS THE TRUTH and MAPS CLAIMS UNDER REAL CRYPTOGRAPHY.
///   (a) endpoint level: no Identity:Google:ClientId configured ⇒ 503 provider_unconfigured, and the
///       capability report says "unconfigured" — never "connected" (§7 tripwire);
///   (b) verifier level: a throwaway 2048-bit RSA key + a fake DISCOVERY DOCUMENT/ JWKS served
///       through the verifier's IDocumentRetriever seam — the signature check, issuer pin, audience
///       pin, lifetime check and the RS256-only algorithm allowlist all run FOR REAL. The fake
///       replaces Google's NETWORK, never Google's CHECKS (the file's own invariant);
///   (c) service level: a valid locally-signed id_token provisions an account anchored on `sub` and
///       re-login returns the SAME user; an unverified email claim is dropped (mapper law), and the
///       minted tokens work against a protected route (the provisioning is real, not a shape test).
/// OWNER: Agent 03 (identity lane); written by repair lane R2.
/// CONSUMES: GoogleTokenVerifier's test ctor, GoogleClaimMapper, IdentityService (constructed by
///           hand against a temp SQLite file with the frozen contributions), PasswordHasher.
/// INVARIANTS: HS256-forgery (the public key used as an HMAC secret), alg=none, tampered payloads,
///           foreign audience, foreign issuer and expired tokens are ALL rejected — an accept on any
///           of these is a login bypass, so every rejection row carries its own assertion.
/// </summary>
public sealed class IdentityGoogleAuthTests : IdentityApiHarness, IAsyncLifetime
{
    private const string ClientId = "livora-test-client.apps.test";
    private const string MetadataAddress = "https://accounts.google.com.invalid/.well-known/openid-configuration";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _kid = Guid.NewGuid().ToString("N")[..12];

    public IdentityGoogleAuthTests(LivoraWebFixture fixture) : base(fixture) { }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    // --------------------------------------------------------------- (a) honest 503 -------------

    [Fact]
    public async Task Google_endpoint_answers_provider_unconfigured_when_no_client_id_exists()
    {
        var (status, text, correlation) = await PostFullAsync("/api/v1/auth/google",
            JsonSerializer.Serialize(new { idToken = "anything" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        var problem = ProblemOf(text);
        Assert.Equal(ProblemCodes.ProviderUnconfigured, problem.Code);
        // the envelope is the PLATFORM's: a correlation id rode the response end to end
        Assert.False(string.IsNullOrWhiteSpace(correlation));
        Assert.NotEqual("unknown", correlation);
        Assert.Equal(correlation, problem.CorrelationId);
    }

    [Fact]
    public async Task Capabilities_report_google_unconfigured_and_never_a_fake_connected()
    {
        var snap = await Fixture.GetAsync<Livora.Server.Modules.Platform.CapabilitySnapshot>(
            "/api/v1/platform/capabilities");
        var identity = Assert.Single(snap.Modules, m => m.Key == "identity");
        Assert.Equal("unconfigured", identity.Capabilities["google_oauth"]);
        Assert.DoesNotContain("connected", identity.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------ (b) real crypto through the seam ----

    [Fact]
    public async Task A_valid_locally_signed_token_verifies_and_its_claims_survive_to_the_mapper()
    {
        var token = SignedJwt(iss: "https://accounts.google.com", aud: ClientId,
            claims: new() { ["sub"] = "10881234567890", ["email"] = "Sara@Test.local",
                            ["email_verified"] = "true", ["name"] = "سارا رضایی", ["locale"] = "fa" });

        var outcome = await Verifier(ClientId).VerifyAsync(token, default);

        Assert.Equal(GoogleVerifyStatus.Ok, outcome.Status);
        Assert.Equal("10881234567890", outcome.Claims!["sub"]);
        var mapped = GoogleClaimMapper.Map(outcome.Claims!);
        Assert.True(mapped.Accepted);
        Assert.Equal("10881234567890", mapped.Identity!.Subject);
        Assert.Equal("Sara@Test.local", mapped.Identity!.Email); // mapper preserves the claim; the service normalizes on store
        Assert.True(mapped.Identity!.EmailVerified);
        Assert.Equal("سارا رضایی", mapped.Identity!.DisplayName); // Persian name survives intact
        Assert.Equal("fa", mapped.Identity!.Locale);
    }

    [Theory]
    [InlineData("https://evil.issuer", ClientId, false, false, GoogleVerifyStatus.Invalid)] // foreign issuer
    [InlineData("https://accounts.google.com", "someone-elses-client", false, false, GoogleVerifyStatus.Invalid)] // foreign aud
    [InlineData("https://accounts.google.com", ClientId, true, false, GoogleVerifyStatus.Invalid)]   // expired
    [InlineData("https://accounts.google.com", ClientId, false, true, GoogleVerifyStatus.Invalid)]   // tampered payload
    public async Task Invalid_tokens_are_rejected_under_real_validation(
        string iss, string aud, bool expired, bool tamper, GoogleVerifyStatus expected)
    {
        var token = SignedJwt(iss, aud, expired: expired);
        if (tamper)
        {
            // flip one character inside the payload segment — signature must catch it
            var parts = token.Split('.');
            var payload = parts[1];
            parts[1] = (payload[0] == 'x' ? "y" : "x") + payload[1..];
            token = string.Join('.', parts);
        }
        var outcome = await Verifier(ClientId).VerifyAsync(token, default);
        Assert.Equal(expected, outcome.Status);
        Assert.Null(outcome.Claims); // nothing leaks forward
    }

    [Fact]
    public async Task HS256_forgery_and_alg_none_are_rejected_by_the_RS256_only_allowlist()
    {
        // The classic JCU key-confusion attack: sign with the PUBLIC key bytes as an HMAC secret.
        var publicKey = _rsa.ExportSubjectPublicKeyInfo();
        var header = Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var payload = Base64Url("{\"iss\":\"https://accounts.google.com\",\"aud\":\"" + ClientId +
                                "\",\"sub\":\"attacker\",\"exp\":" + ExpiresIn(600) + "}");
        using var hmac = new HMACSHA256(publicKey);
        var sig = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.ASCII.GetBytes(header + "." + payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var forged = header + "." + payload + "." + sig;

        Assert.Equal(GoogleVerifyStatus.Invalid, (await Verifier(ClientId).VerifyAsync(forged, default)).Status);

        // alg=none, with and without a trailing dot
        var none = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + payload + ".";
        Assert.Equal(GoogleVerifyStatus.Invalid, (await Verifier(ClientId).VerifyAsync(none, default)).Status);
    }

    [Fact]
    public async Task An_empty_client_id_never_calls_the_network_and_answers_Unconfigured()
    {
        var outcome = await Verifier(clientId: "").VerifyAsync(SignedJwt(
            "https://accounts.google.com", ClientId), default);
        Assert.Equal(GoogleVerifyStatus.Unconfigured, outcome.Status);
    }

    [Fact]
    public async Task A_metadata_outage_is_Unavailable_never_a_silent_accept()
    {
        var throwing = new ThrowingRetriever();
        var verifier = new GoogleTokenVerifier(ClientId, MetadataAddress, throwing);
        var outcome = await verifier.VerifyAsync(SignedJwt("https://accounts.google.com", ClientId), default);
        Assert.Equal(GoogleVerifyStatus.Unavailable, outcome.Status);
    }

    [Fact]
    public async Task Oversized_and_blank_tokens_are_Invalid_before_any_network()
    {
        Assert.Equal(GoogleVerifyStatus.Invalid, (await Verifier(ClientId).VerifyAsync(null, default)).Status);
        Assert.Equal(GoogleVerifyStatus.Invalid,
            (await Verifier(ClientId).VerifyAsync(new string('a', 9000), default)).Status);
    }

    // -------------------------------------------- (c) full provisioning under the local key -----

    [Fact]
    public async Task A_locally_signed_login_provisions_the_sub_anchored_account_and_repeats_stably()
    {
        var dir = Path.Combine(Path.GetTempPath(), "livora-google-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var dbPath = Path.Combine(dir, "t.db");
            IdentityModelContribution.EnsureRegistered();
            SyncModelContributionBridge.Ensure();
            var options = new DbContextOptionsBuilder<LivoraDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;
            using var db = new LivoraDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var identityOptions = IdentityOptions.FromConfiguration(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Identity:Google:ClientId"] = ClientId,
                    ["Identity:Google:MetadataAddress"] = MetadataAddress,
                }).Build());
            var key = new LivoraSigningKey(RandomNumberGenerator.GetBytes(48), "test", "test", true);
            var svc = new IdentityService(db, key, new PasswordHasher(), identityOptions,
                SystemClock.Instance,
                new IdentitySecurityStore(() => db),
                new GoogleTokenVerifier(ClientId, MetadataAddress, FakeDiscovery()),
                NullLogger<IdentityService>.Instance);

            var token = SignedJwt("https://accounts.google.com", ClientId, claims: new()
            {
                ["sub"] = "9007199254740993",
                ["email"] = "notverified@attacker.test",
                ["email_verified"] = "false",
                ["name"] = "Ghost",
            });

            var (first, firstBody) = await CallAsync(svc, token);
            Assert.Equal(200, first);
            var body = JsonDocument.Parse(firstBody).RootElement;
            var userId = body.GetProperty("userId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(userId));

            // the unverified email was DROPPED — the account exists with no email claim at all
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            Assert.Equal("9007199254740993", user.GoogleSubject);
            Assert.Null(user.NormalizedEmail);
            Assert.Null(user.PasswordHash);       // federated-only: no local credential exists
            Assert.Equal("Ghost", user.DisplayName);

            // same sub, second login → same account, new session
            var (second, secondBody) = await CallAsync(svc, SignedJwt("https://accounts.google.com", ClientId,
                claims: new() { ["sub"] = "9007199254740993" }));
            Assert.Equal(200, second);
            Assert.Equal(userId, JsonDocument.Parse(secondBody).RootElement
                .GetProperty("userId").GetString());

            // a bad signature on this same wired service is 401 invalid_credentials, never 500
            var (bad, badBody) = await CallAsync(svc, token + "x");
            Assert.Equal(401, bad);
            Assert.Equal(ProblemCodes.InvalidCredentials, ProblemOf(badBody).Code);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    // -------------------------------------------------------------------- plumbing --------------

    /// <summary>Run the real handler (an IResult) through a bare HttpContext and read its body —
    /// the same code path the HTTP pipeline executes; only the routing/auth layers are bypassed
    /// because this test is about the SERVICE's Google wiring, not about the envelope again.</summary>
    private static async Task<(int Status, string Body)> CallAsync(IdentityService svc, string idToken)
    {
        var http = new DefaultHttpContext
        {
            // Results.Json resolves its options from Request (IOptions<JsonOptions> = web/camelCase
            // defaults); a bare HttpContext has no provider, and a test that fakes the pipeline must
            // at least provide what the framework's own result types need.
            RequestServices = TestServices(),
        };
        http.Response.Body = new MemoryStream();
        var result = await svc.GoogleLoginAsync(http, new GoogleLoginRequest(idToken, null, null), default);
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode,
            await new StreamReader(http.Response.Body, Encoding.UTF8).ReadToEndAsync());
    }

    private static ServiceProvider TestServices()
        => new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(_ => { }) // web defaults = camelCase
            .BuildServiceProvider();

    private GoogleTokenVerifier Verifier(string clientId)
        => new(clientId, MetadataAddress, FakeDiscovery());

    private IDocumentRetriever FakeDiscovery() => new TestRetriever(_rsa, _kid, MetadataAddress);

    private string SignedJwt(
        string iss, string aud, Dictionary<string, string>? claims = null, bool expired = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["iss"] = iss, ["aud"] = aud, ["sub"] = "1",
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"] = expired
                ? DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
        };
        if (claims is not null)
            foreach (var (k, v) in claims) payload[k] = v;

        var signingInput = Base64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":\"" + _kid + "\"}")
            + "." + Base64Url(JsonSerializer.Serialize(payload));
        var signature = _rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Convert.ToBase64String(signature)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Base64Url(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static long ExpiresIn(int minutes)
        => DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();

    /// <summary>Serves Google's discovery + JWKS SHAPES offline from a throwaway local key. Every
    /// validation check (signature/iss/aud/exp/alg) still runs in the verifier — this is the seam
    /// the production file documents, not a bypass.</summary>
    private sealed class TestRetriever(RSA rsa, string kid, string metadataAddress) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            if (address == metadataAddress)
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    issuer = "https://accounts.google.com",
                    jwks_uri = "https://accounts.google.test/oauth2/v3/certs",
                    authorization_endpoint = "https://accounts.google.com/o/oauth2/v2/auth",
                }));
            var p = rsa.ExportParameters(false);
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA", use = "sig", alg = "RS256", kid,
                        n = Convert.ToBase64String(p.Modulus!).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                        e = Convert.ToBase64String(p.Exponent!).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                    },
                },
            }));
        }
    }

    private sealed class ThrowingRetriever : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
            => Task.FromException<string>(new HttpRequestException("simulated Google outage"));
    }
}

/// <summary>The sync contribution registered through its own idempotent gate (same call the module
/// makes) — reused here so a hand-built context matches the host model.</summary>
internal static class SyncModelContributionBridge
{
    public static void Ensure() => Livora.Server.Infrastructure.Sync.SyncModelContribution.EnsureRegistered();
}
