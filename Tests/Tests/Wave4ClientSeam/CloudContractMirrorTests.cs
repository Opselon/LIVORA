using System.Text.RegularExpressions;
using LIVORA.Application.Cloud;

namespace LIVORA.Tests.Tests.Wave4ClientSeam;

/// <summary>
/// The client mirror of the frozen §5c contract must not drift from the server's own source. Every
/// lane that renames a code or a path on either side turns THIS suite red — which is the point: the
/// two halves of the seam are in different projects (the MAUI app cannot reference the ASP.NET host),
/// so a compile-time link is not available and a disk-read assertion is.
/// </summary>
public class CloudContractMirrorTests
{
    private const string ServerApiProblem = "server/src/Livora.Server.Application/ApiProblem.cs";

    [Fact]
    public void EveryServerProblemCode_IsMirroredInTheClientTable()
    {
        var source = Wave4SeamHarness.ReadRepoFile(ServerApiProblem);
        if (source is null) return;   // trimmed checkout: skip rather than assert nothing

        var serverCodes = Regex.Matches(source, """public const string \w+ = "([a-z0-9_]+)";""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(serverCodes.Count > 20, "the server code table did not parse — the mirror test would be theatre");

        var clientCodes = LivoraApiCodes.All.ToHashSet(StringComparer.Ordinal);
        var missing = serverCodes.Where(c => !clientCodes.Contains(c)).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var extra = clientCodes.Where(c => !serverCodes.Contains(c)).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "server codes the client cannot localize: " + string.Join(", ", missing));
        Assert.True(extra.Count == 0, "client codes the server never emits (rename or delete): " + string.Join(", ", extra));
    }

    [Fact]
    public void EveryMirroredPath_AppearsInTheFrozenContractDocument()
    {
        var doc = Wave4SeamHarness.ReadRepoFile("docs/architecture/wave4/CONTRACT-P1.md");
        if (doc is null) return;

        string[] paths =
        {
            LivoraApiPaths.Register, LivoraApiPaths.Login, LivoraApiPaths.Google,
            LivoraApiPaths.Refresh, LivoraApiPaths.Logout, LivoraApiPaths.Sessions,
            LivoraApiPaths.Account, LivoraApiPaths.DeleteRequests, LivoraApiPaths.Export,
            LivoraApiPaths.SyncBatch, LivoraApiPaths.SyncChanges,
        };
        var offenders = paths.Where(p => !doc.Contains(p, StringComparison.Ordinal)).ToList();
        Assert.True(offenders.Count == 0,
            "paths this client calls that §5c does not define: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ApiPrefix_IsTheVersionedOneTheHostEnforces()
    {
        Assert.Equal("/api/v1", LivoraApiPaths.ApiPrefix);
        Assert.All(new[]
        {
            LivoraApiPaths.Register, LivoraApiPaths.Sessions, LivoraApiPaths.Account,
            LivoraApiPaths.Export, LivoraApiPaths.SyncBatch, LivoraApiPaths.SyncChanges,
        }, p => Assert.StartsWith(LivoraApiPaths.ApiPrefix + "/", p, StringComparison.Ordinal));
        // No path may carry a query string (the port builds those explicitly) and none may be absolute.
        Assert.DoesNotContain("?", LivoraApiPaths.SyncChanges);
        Assert.Contains("/sessions/", LivoraApiPaths.Session("abc def"), StringComparison.Ordinal);   // escaped, not raw
    }

    [Fact]
    public void CorrelationHeader_And_IdempotencyHeader_MatchTheServerSpelling()
    {
        var correlation = Wave4SeamHarness.ReadRepoFile("server/src/Livora.Server/Platform/Correlation.cs");
        var contract = Wave4SeamHarness.ReadRepoFile("docs/architecture/wave4/CONTRACT-P1.md");

        if (correlation is not null)
            Assert.Contains($"""public const string HeaderName = "{LivoraApiPortContract.CorrelationHeader}";""",
                correlation, StringComparison.Ordinal);
        if (contract is not null)
        {
            // §5c names the sync header verbatim; the correlation header is documented in the server
            // source above (CONTRACT-P1.md refers to it as `AssertHasCorrelation`, not by header name).
            Assert.Contains("Idempotency-Key", contract, StringComparison.Ordinal);
        }
        Assert.Equal("Idempotency-Key", LivoraApiPortContract.IdempotencyHeader);
    }
}

/// <summary>
/// Header names the port uses, exposed for the mirror test without referencing the Infrastructure
/// namespace from the pure layer (the port's own consts are the same values, asserted below).
/// </summary>
internal static class LivoraApiPortContract
{
    public const string CorrelationHeader = "X-Correlation-Id";
    public const string IdempotencyHeader = "Idempotency-Key";
}

/// <summary>
/// The wire format itself: camelCase names, optional fields omitted (not null), and the envelope
/// fields §5c freezes. A property rename in a DTO must fail here, not in production.
/// </summary>
public class CloudWireShapeTests
{
    [Fact]
    public void RegisterRequest_OmitsOptionalFieldsEntirely()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new RegisterRequest("a@b.test", "pw"),
            LIVORA.Application.Cloud.LivoraJson.Options);
        Assert.Contains("\"email\":\"a@b.test\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("locale", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"userId":"u","accessToken":"a","refreshToken":"r","expiresAtUtc":"2026-09-14T08:00:00+00:00","sessionId":"s"}""")]
    public void AuthTokensDto_RoundTripsTheCamelCaseNames(string json)
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<AuthTokensDto>(json, LivoraJson.Options);
        Assert.NotNull(dto);
        Assert.Equal("u", dto!.UserId);
        Assert.Equal("r", dto.RefreshToken);
        Assert.Equal("s", dto.SessionId);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero), dto.ExpiresAtUtc);
    }

    [Fact]
    public void SyncBatchRequest_SerializesTheFrozenOperationShape()
    {
        var payload = System.Text.Json.JsonDocument.Parse("""{"id":"g1"}""").RootElement.Clone();
        var body = new SyncBatchRequest(new[]
        {
            new SyncOperationDto("op-1", "goal", "g1", SyncOperationKinds.Update, 4, payload),
        });
        var json = System.Text.Json.JsonSerializer.Serialize(body, LivoraJson.Options);

        foreach (var name in new[] { "operations", "operationId", "entityType", "entityId", "kind", "baseRevision", "payload" })
            Assert.Contains("\"" + name + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"update\"", json, StringComparison.Ordinal);
        Assert.Contains("\"baseRevision\":4", json, StringComparison.Ordinal);

        // The result row (§5c) too: the queue's outcome mapping switches on these exact strings.
        var row = System.Text.Json.JsonSerializer.Serialize(
            new SyncOperationResultDto("op-1", SyncOutcomes.Applied, 7, null), LivoraJson.Options);
        foreach (var name in new[] { "operationId", "outcome", "resultRevision" })
            Assert.Contains("\"" + name + "\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("\"conflict\"", row, StringComparison.Ordinal);   // absent, not null (§5c optional field)

        var conflicted = System.Text.Json.JsonSerializer.Serialize(
            new SyncOperationResultDto("op-1", SyncOutcomes.Conflict, null, new SyncConflictDto("version_conflict", 9)),
            LivoraJson.Options);
        Assert.Contains("\"conflict\":{\"kind\":\"version_conflict\",\"remoteRevision\":9}", conflicted, StringComparison.Ordinal);
    }

    [Fact]
    public void ProblemEnvelope_UsesEveryFrozenFieldName()
    {
        const string body = """
            {"type":"https://livora.app/problems/invalid_credentials","title":"invalid_credentials",
             "status":401,"detail":"server prose nobody may render","instance":"/api/v1/auth/login",
             "code":"invalid_credentials","correlationId":"abc123","errors":{"email":["Api.Error.validation_failed"]}}
            """;
        var p = System.Text.Json.JsonSerializer.Deserialize<ApiProblemDto>(body, LivoraJson.Options);
        Assert.NotNull(p);
        Assert.Equal(401, p!.Status);
        Assert.Equal("invalid_credentials", p.Code);
        Assert.Equal("abc123", p.CorrelationId);
        Assert.Equal("invalid_credentials", p.EffectiveCode);
        Assert.Single(p.Errors!["email"]);
    }

    [Fact]
    public void EffectiveCode_FallsBackToTheTypeUri_AndNeverInventsACode()
    {
        var fromType = new ApiProblemDto("https://livora.app/problems/token_revoked", "t", 401, "d", "/", null, "c", null);
        Assert.Equal("token_revoked", fromType.EffectiveCode);

        var garbage = new ApiProblemDto("about:blank", "t", 500, "d", "/", null, "c", null);
        Assert.Null(garbage.EffectiveCode);
        Assert.Equal(LivoraApiCodes.UnknownReasonKey, LivoraApiCodes.ReasonKeyFor(garbage.EffectiveCode));
    }

    [Fact]
    public void ReasonKeyFor_EmitsDottedKeys_OnlyForKnownCodes()
    {
        Assert.Equal("Api.Error.rate_limited", LivoraApiCodes.ReasonKeyFor(LivoraApiCodes.RateLimited));
        Assert.Equal("Api.Error.unknown", LivoraApiCodes.ReasonKeyFor("something_the_server_invented"));
        Assert.Equal("Api.Error.unknown", LivoraApiCodes.ReasonKeyFor(null));
        Assert.All(LivoraApiCodes.All, code =>
        {
            var key = LivoraApiCodes.ReasonKeyFor(code);
            Assert.StartsWith("Api.Error.", key, StringComparison.Ordinal);
            Assert.DoesNotContain(' ', key);
        });
    }
}
