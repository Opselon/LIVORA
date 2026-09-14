using Microsoft.Extensions.Configuration;

namespace Livora.Server.Modules;

/// <summary>
/// PURPOSE: the self-registration seam for backend feature modules. A lane adds files ONLY inside
///          its own folder and the host scanner finds them; nobody edits the composition root.
///          That is the whole integration contract, and it is why 16 lanes can touch one repo.
/// OWNER: Agent 01 (contract, frozen at Wave 4 P0). Lanes implement, they do not amend.
/// CONSUMES: <see cref="ModuleSeed"/> (services phase), <see cref="FlivoraEndpointContext"/> (route phase).
/// PROVIDES: a module key, endpoint mapping, and an honest dependency probe.
/// INVARIANTS:
///   - <see cref="Report"/> must state the REAL state of external dependencies. "Not configured" is
///     <see cref="DependencyState.Unconfigured"/> — never Ok, never "connected".
///   - A module with no configuration still maps its endpoints and answers truthfully; it must not
///     throw at startup, and it must not fake success.
///   - Every route path MUST begin with /api/v1 (enforced at runtime by the endpoint context).
///   - Service registration happens only in <see cref="ConfigureServices"/> (before Build);
///     endpoints only in <see cref="MapEndpoints"/> (after Build). Mixing them is a host crash.
/// EXTEND: create server/src/Livora.Server/Modules/&lt;Feature&gt;/&lt;Feature&gt;Module.cs.
/// </summary>
public interface IFlivoraModule
{
    /// <summary>Stable, lowercase, unique key, e.g. "identity", "marketplace". Used in /healthz + capabilities.</summary>
    string Key { get; }

    /// <summary>Phase 1 (pre-Build): register this module's services. Default: nothing to register.</summary>
    void ConfigureServices(ModuleSeed seed) { }

    /// <summary>Phase 2 (post-Build): map /api/v1 endpoints. Default: no surface (health-only module).</summary>
    void MapEndpoints(FlivoraEndpointContext ctx) { }

    /// <summary>Live, honest dependency report. Called per request by the capability endpoint.</summary>
    ModuleHealth Report();
}

/// <summary>Phase-1 input: the only thing a module may touch before the provider is built.</summary>
public sealed record ModuleSeed(
    Microsoft.Extensions.DependencyInjection.IServiceCollection Services,
    IConfiguration Configuration,
    ILogger Logger);

/// <summary>Phase-2 input: route building. The version prefix is not negotiable.</summary>
public sealed class FlivoraEndpointContext
{
    /// <summary>Every module route is mounted under here.</summary>
    public const string VersionPrefix = "/api/v1";

    public FlivoraEndpointContext(IEndpointRouteBuilder app, IConfiguration configuration, ILogger logger)
    {
        App = app;
        Configuration = configuration;
        Logger = logger;
    }

    public IEndpointRouteBuilder App { get; }
    public IConfiguration Configuration { get; }
    public ILogger Logger { get; }

    /// <summary>Group for a module: always /api/v1/&lt;segment&gt;, never a bare path.</summary>
    public RouteGroupBuilder MapVersionedGroup(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("Module route segment is required.", nameof(segment));
        if (segment.StartsWith('/'))
            throw new ArgumentException($"Route segment '{segment}' must not start with '/'.", nameof(segment));
        return App.MapGroup($"{VersionPrefix}/{segment}");
    }
}

/// <summary>Truthful availability states — the product law "no fake connected states", in enum form.</summary>
public enum DependencyState
{
    /// <summary>Working: verified by an actual call/probe, not by the presence of a config value.</summary>
    Ok = 0,
    /// <summary>Configured but the dependency did not answer (network/provider failure).</summary>
    Degraded = 1,
    /// <summary>No configuration present. Absent and honest — not broken, and not "connected".</summary>
    Unconfigured = 2,
    /// <summary>Deliberately not implemented in this wave.</summary>
    NotImplemented = 3,
}

/// <summary>A module's self-report, surfaced by /api/v1/platform/capabilities and /healthz?deep=1.</summary>
/// <param name="Key">Module key.</param>
/// <param name="State">Truthful availability.</param>
/// <param name="Detail">Short human/ops text; no secrets, no raw user data.</param>
/// <param name="Capabilities">Optional stable feature flags the client can switch on (e.g. "google_oauth":"unconfigured").</param>
public sealed record ModuleHealth(
    string Key,
    DependencyState State,
    string Detail,
    IReadOnlyDictionary<string, string>? Capabilities = null);
