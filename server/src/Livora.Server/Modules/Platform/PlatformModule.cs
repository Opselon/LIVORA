using Livora.Server.Application;
using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Modules.Platform;

/// <summary>
/// PURPOSE: the platform surface every client needs before any feature exists — version info, the
///          truthful capability report, and a real (not assumed) dependency probe. It also serves as
///          the worked example of the module seam for every other lane.
/// OWNER: Agent 02.
/// CONSUMES: <see cref="ModuleRegistry"/> (resolved from DI, never a static), <see cref="LivoraDbContext"/>.
/// PROVIDES: GET /api/v1/platform/health-deep, GET /api/v1/platform/capabilities.
/// INVARIANTS:
///   - <c>database</c> state comes from an ACTUAL query. An unreachable DB reports degraded; it can
///     never report ok because "a connection string exists".
///   - capability output lists every module with its live self-report — no allow-list, no caching.
///   - this module never reports Ok for another module's dependency.
/// </summary>
public sealed class PlatformModule : IFlivoraModule
{
    public const string ModuleKey = "platform";

    public string Key => ModuleKey;

    public void MapEndpoints(FlivoraEndpointContext ctx)
    {
        var group = ctx.MapVersionedGroup("platform");

        group.MapGet("/capabilities", async (HttpContext http, ModuleRegistry registry,
            LivoraDbContext db, CancellationToken ct) =>
        {
            var dbReport = await DatabaseProbe.ProbeAsync(db, ct);
            var reports = registry.Reports().ToList();

            return Results.Ok(new CapabilitySnapshot(
                ServerTimeUtc: DateTimeOffset.UtcNow,
                ApiVersion: "v1",
                // Truthful states only: a client renders "not configured" from this, never guesses.
                Modules: reports.Select(r => new CapabilityEntry(r.Key,
                    r.State.ToString().ToLowerInvariant(), r.Detail,
                    r.Capabilities ?? new Dictionary<string, string>())).ToArray(),
                Database: new CapabilityEntry("database",
                    dbReport.State.ToString().ToLowerInvariant(), dbReport.Detail,
                    new Dictionary<string, string> { ["provider"] = dbReport.Provider }),
                // A module that failed to map routes at startup is a real outage, disclosed here.
                EndpointMappingFailures: registry.StartupFailures));
        });

        group.MapGet("/version", () => Results.Ok(new
        {
            service = "livora-server",
            version = typeof(PlatformModule).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            apiVersion = "v1",
        }));

        // The smallest protected endpoint on purpose: it lets every lane verify the auth contract
        // (who am I, which roles, and what an anonymous call actually gets back) without needing a
        // feature module to exist first. Identity semantics come from Platform/LivoraAuth.cs.
        group.MapGet("/me", (HttpContext http) =>
        {
            var user = http.User;
            return Results.Ok(new
            {
                userId = user.UserId(),
                sessionId = user.SessionId(),
                roles = user.RolesOf(),
                isStaff = user.IsStaff(),
            });
        }).RequireAuthorization(Policies.SignedIn);
    }

    public ModuleHealth Report() => new(ModuleKey, DependencyState.Ok,
        "platform surface always available when the host is up");
}

/// <summary>The body the client's connector/status screens are built from.</summary>
public sealed record CapabilitySnapshot(
    DateTimeOffset ServerTimeUtc,
    string ApiVersion,
    IReadOnlyList<CapabilityEntry> Modules,
    CapabilityEntry Database,
    IReadOnlyList<string> EndpointMappingFailures);

/// <summary>One dependency's honest state. <c>state</c> ∈ ok|degraded|unconfigured|not_implemented.</summary>
public sealed record CapabilityEntry(
    string Key,
    string State,
    string Detail,
    IReadOnlyDictionary<string, string> Capabilities);

/// <summary>
/// PURPOSE: probe the database by asking it a question. This is the only sanctioned way a module may
///          claim a dependency is healthy (Wave 4 rule D: no fake "connected").
/// OWNER: Agent 02.
/// INVARIANTS: any exception or non-success converts to <see cref="DependencyState.Degraded"/> with
///             the exception TYPE name only — never the message (it can carry a file path or DSN).
/// </summary>
public static class DatabaseProbe
{
    public static async Task<(DependencyState State, string Detail, string Provider)> ProbeAsync(
        LivoraDbContext db, CancellationToken ct)
    {
        var provider = db.Database.ProviderName ?? "unknown";
        try
        {
            var canConnect = await db.Database.CanConnectAsync(ct);
            return canConnect
                ? (DependencyState.Ok, "connected", ProviderName(provider))
                : (DependencyState.Degraded, "configured but not reachable", ProviderName(provider));
        }
        catch (Exception ex)
        {
            return (DependencyState.Degraded, $"probe failed: {ex.GetType().Name}", ProviderName(provider));
        }
    }

    private static string ProviderName(string full) => full[(full.LastIndexOf('.') + 1)..];
}
