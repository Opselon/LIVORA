using Livora.Server.Application;

namespace Livora.Server.Tests.Platform;

/// <summary>
/// PURPOSE: prove the pre-wired auth contract BEFORE any identity lane code exists — a protected
///          endpoint must reject anonymous callers with the shared envelope, accept a token minted
///          by the host key, and expose exactly the claims the contract promises.
/// OWNER: lead (platform scaffold gate). Agent 03 extends, does not replace, these.
/// INVARIANTS: no test here guesses at JWT internals; tokens come from the host's own signing key
///             via the fixture, which is what makes them a real end-to-end assertion.
/// </summary>
public sealed class AuthContractTests : LivoraApiTest
{
    public AuthContractTests(LivoraWebFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Protected_endpoint_without_token_returns_problem_envelope_not_bare_401()
    {
        var res = await Fixture.Http.GetAsync("/api/v1/platform/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.Unauthenticated, problem.Code);
        Assert.Equal(401, problem.Status);
        Assert.NotEqual("unknown", problem.CorrelationId);
        Assert.StartsWith("application/json", res.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Protected_endpoint_with_tampered_token_is_rejected()
    {
        var token = Fixture.MintAccessToken("user-1");
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/platform/me");
        req.Headers.Add("Authorization", "Bearer " + token[..^4] + "AAAA");
        var res = await Fixture.Http.SendAsync(req); // never dispose the shared fixture client
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_with_valid_token_exposes_contract_claims()
    {
        var client = Fixture.CreateAuthenticatedClient("user-42", [Roles.User, Roles.Creator]);
        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/platform/me");

        Assert.Equal("user-42", body.GetProperty("userId").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("sessionId").GetString()));
        var roles = body.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains(Roles.Creator, roles);
        Assert.False(body.GetProperty("isStaff").GetBoolean()); // creator is not staff
    }

    [Fact]
    public async Task Staff_flag_reflects_moderator_and_admin_roles_only()
    {
        var mod = await Fixture.CreateAuthenticatedClient("u-mod", [Roles.User, Roles.Moderator])
            .GetFromJsonAsync<JsonElement>("/api/v1/platform/me");
        var admin = await Fixture.CreateAuthenticatedClient("u-admin", [Roles.Admin])
            .GetFromJsonAsync<JsonElement>("/api/v1/platform/me");
        var plain = await Fixture.CreateAuthenticatedClient("u-plain")
            .GetFromJsonAsync<JsonElement>("/api/v1/platform/me");

        Assert.True(mod.GetProperty("isStaff").GetBoolean());
        Assert.True(admin.GetProperty("isStaff").GetBoolean());
        Assert.False(plain.GetProperty("isStaff").GetBoolean());
    }
}
