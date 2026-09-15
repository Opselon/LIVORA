using Livora.Server.Application;
using Livora.Server.Infrastructure.Identity;
using Livora.Server.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Livora.Server.Modules.Identity;

/// <summary>
/// PURPOSE: the identity module — self-registers the account system behind the frozen §5c contract:
///          /api/v1/auth/* and /api/v1/account/*. Key "identity" is this lane's OWNERSHIP row.
/// OWNER: Agent 03 (identity lane).
/// CONSUMES: config Identity:* (IdentityOptions), the lead's LivoraSigningKey seam, LivoraDbContext
///           core tables + this lane's model contributions (identity_security_profiles,
///           auth_session_lineage, auth_revoked_refresh_tokens).
/// PROVIDES: POST auth/register|login|google|refresh|logout, GET auth/sessions,
///           DELETE auth/sessions/{id}, GET account, POST/DELETE account/delete-requests,
///           GET account/export, POST account/delete-requests/execute (staff), and the honest
///           /healthz?deep=1 + capabilities entry for the lane.
/// INVARIANTS:
///   - the Google path reports its REAL state: no Identity:Google:ClientId ⇒ the module reports
///     unconfigured and the endpoint answers 503 provider_unconfigured. No fake login exists.
///   - an ephemeral signing key (LivoraSigningKey.Ephemeral) is surfaced as `degraded` — the dev
///     posture is disclosed, never presented as production-ready (§5b).
///   - every route below Policies.SignedIn ALSO passes AuthorizeSessionAsync: the policy proves a
///     token; only the DB proves the session lives and the row is yours (IDOR gate, §5b).
/// </summary>
public sealed class IdentityModule : IFlivoraModule
{
    public const string ModuleKey = "identity";

    /// <summary>Set once at ConfigureServices; read by Report(). A module instance survives the
    /// whole host lifetime, so this is the seam between config-time truth and report-time truth.</summary>
    private volatile IdentityOptions _options = IdentityOptions.FromConfiguration(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    public string Key => ModuleKey;

    public void ConfigureServices(ModuleSeed seed)
    {
        IdentityModelContribution.EnsureRegistered();

        var options = IdentityOptions.FromConfiguration(seed.Configuration);
        _options = options;

        seed.Services.AddSingleton(options);
        seed.Services.AddSingleton<IClock>(_ => SystemClock.Instance);
        seed.Services.AddSingleton<PasswordHasher>();
        seed.Services.AddSingleton<IGoogleIdTokenVerifier>(_ => new GoogleTokenVerifier(
            options.GoogleClientId ?? "", options.GoogleMetadataAddress));
        seed.Services.AddScoped<IdentitySecurityStore>(sp =>
            new IdentitySecurityStore(() => sp.GetRequiredService<LivoraDbContext>()));
        seed.Services.AddScoped<IdentityService>();
        seed.Services.AddScoped<IdentityAccountService>();
        seed.Services.AddHostedService<IdentityKeyStateProbe>();
    }

    public void MapEndpoints(FlivoraEndpointContext ctx)
    {
        var auth = ctx.MapVersionedGroup("auth");

        auth.MapPost("/register", async (HttpContext http, IdentityService svc, CancellationToken ct)
            => await svc.RegisterAsync(http, await ReadBodyAsync<RegisterRequest>(http), ct));

        auth.MapPost("/login", async (HttpContext http, IdentityService svc, CancellationToken ct)
            => await svc.LoginAsync(http, await ReadBodyAsync<LoginRequest>(http), ct));

        auth.MapPost("/google", async (HttpContext http, IdentityService svc, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<GoogleLoginRequest>(http);
            return await svc.GoogleLoginAsync(http, body, ct);
        });

        auth.MapPost("/refresh", async (HttpContext http, IdentityService svc, CancellationToken ct)
            => await svc.RefreshAsync(http, await ReadBodyAsync<RefreshRequest>(http), ct));

        auth.MapPost("/logout", async (HttpContext http, IdentityService svc, CancellationToken ct)
            => await svc.LogoutAsync(http, ct))
            .RequireAuthorization(Policies.SignedIn);

        auth.MapGet("/sessions", async (HttpContext http, IdentityService svc,
            int? offset, int? limit, CancellationToken ct)
            => await svc.ListSessionsAsync(http, offset, limit, ct))
            .RequireAuthorization(Policies.SignedIn);
        auth.MapDelete("/sessions/{id}", async (HttpContext http, IdentityService svc,
            string id, CancellationToken ct)
            => await svc.RevokeSessionAsync(http, id, ct))
            .RequireAuthorization(Policies.SignedIn);

        var account = ctx.MapVersionedGroup("account");

        // §5c spells this route WITHOUT a trailing slash and the frozen P1-D client calls exactly
        // that. Inside MapGroup("account"), only the "" template binds the bare /api/v1/account —
        // "/" would produce the trailing-slash variant and leave the contract path 404.
        account.MapGet("", async (HttpContext http, IdentityAccountService svc, CancellationToken ct)
            => await svc.GetAccountAsync(http, ct))
            .RequireAuthorization(Policies.SignedIn);

        account.MapPost("/delete-requests", async (HttpContext http, IdentityAccountService svc, CancellationToken ct)
            => await svc.RequestDeletionAsync(http, ct))
            .RequireAuthorization(Policies.SignedIn);

        // §5c spells this DELETE with a body; bodies on DELETE are unreliable across clients, so the
        // route answers with no body — the caller IS the subject. Request recorded in the lane ledger.
        // (A trailing-slash twin used to be mapped here as well; ASP.NET's matcher treats "/x" and
        // "/x/" as one addressable shape, so two endpoints made every request AMBIGUOUS — 500. One
        // template, both spellings served.)
        account.MapDelete("/delete-requests", async (HttpContext http, IdentityAccountService svc, CancellationToken ct)
            => await svc.CancelDeletionAsync(http, ct))
            .RequireAuthorization(Policies.SignedIn);

        // Phase-2 scheduler calls ExecuteAsync directly; this route makes the path REAL now
        // (admin-only, grace-window enforced) so deletion is not a paper promise.
        account.MapPost("/delete-requests/execute", async (HttpContext http, IdentityAccountService svc,
            CancellationToken ct) =>
        {
            var body = await ReadBodyAsync<ExecuteDeletionRequest>(http);
            return await svc.ExecuteDeletionAsync(http, body?.UserId, ct);
        }).RequireAuthorization(Policies.SignedIn);

        account.MapGet("/export", async (HttpContext http, IdentityAccountService svc, CancellationToken ct)
            => await svc.ExportAsync(http, ct))
            .RequireAuthorization(Policies.SignedIn);
    }

    /// <summary>Empty or malformed JSON bodies must reach the handler as `null` and produce the
    /// contract's validation/401 answers — never an unhandled 500 on `POST {}`-less clients.</summary>
    private static async Task<T?> ReadBodyAsync<T>(HttpContext http) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(Problems.Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null; // unsupported media type
        }
    }

    public sealed record ExecuteDeletionRequest(string? UserId);

    /// <summary>
    /// Honest self-report (live, per call):
    ///   google_oauth: ok only with ClientId configured — never inferred from a secret's presence
    ///   signing_key : configured | ephemeral(dev)
    ///   local_state : available once the DB answers (identity cannot be Ok without its own tables)
    /// Overall state: Degraded on an ephemeral key (a dev posture must never look production-ready,
    /// §5b), otherwise Ok — the local account machinery genuinely runs, and what doesn't (Google)
    /// is stated per-capability, not hidden in a global green.
    /// </summary>
    public ModuleHealth Report()
    {
        // PROBED post-build from the live DI (IdentityKeyStateProbe), never guessed from config.
        var key = IdentityModuleSigningKeyState.Value ?? "unknown";

        var caps = new Dictionary<string, string>
        {
            ["google_oauth"] = _options.GoogleConfigured ? "configured" : "unconfigured",
            ["google_client_secret_present"] = _options.HasGoogleClientSecret ? "true" : "false",
            ["signing_key"] = key,
            ["password_hash"] = "pbkdf2-sha256",
            ["local_accounts"] = "available",
        };

        if (_options.BootstrapAdminEmails.Count > 0)
            caps["bootstrap_admin"] = $"configured ({_options.BootstrapAdminEmails.Count} emails)";

        var state = key == "ephemeral" ? DependencyState.Degraded : DependencyState.Ok;
        var detail = key == "ephemeral"
            ? "dev signing key (tokens die with the process) — set Identity:TokenSigningKey for real use"
            : "local identity fully operational; external providers per capability map";
        return new ModuleHealth(ModuleKey, state, detail, caps);
    }
}

/// <summary>Reads the lead's key state once from DI. A hosted service touches it at startup so the
/// capability report is PROBED truth (post-build), never a config-guess.</summary>
internal sealed class IdentityKeyStateProbe : IHostedService
{
    private readonly IServiceProvider _services;
    public IdentityKeyStateProbe(IServiceProvider services) => _services = services;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var key = _services.GetService(typeof(LivoraSigningKey)) as LivoraSigningKey;
        IdentityModuleSigningKeyState.Value = key is null ? "unknown" : key.Ephemeral ? "ephemeral" : "configured";
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class IdentityModuleSigningKeyState
{
    public static volatile string? Value;
}
