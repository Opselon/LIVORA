using System.Diagnostics;
using Livora.Server;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Persistence;
using Livora.Server.Modules;

// ============================================================================
// LIVORA cloud backend — composition root (Wave 4, P0 scaffold).
// PURPOSE: host + cross-cutting platform concerns only. Feature behaviour lives in modules.
// OWNER: Agent 02 (Cloud Backend & API Platform Engineer). Feature lanes MUST NOT edit this file;
//        they add server/src/Livora.Server/Modules/<Feature>/ and the scanner picks them up.
// CONSUMES: configuration (env + appsettings), LivoraDbContext.
// PROVIDES: /api/v1 surface, correlation IDs, RFC-9457 problem envelope, OpenAPI, /healthz.
// INVARIANTS:
//   - boots with ZERO external providers configured (local SQLite is the default)
//   - a module that throws in any phase is recorded as degraded and the host keeps serving
//   - no secret value is ever logged; only whether one is present
//   - unknown /api/v1/* answers the shared problem envelope, never an empty 404
//   - /healthz only claims dependency health when an actual probe ran (see PlatformModule)
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddOpenApi();
// Auth is pre-wired by the lead so no lane invents its own token plumbing (see Platform/LivoraAuth.cs).
builder.Services.AddLivoraAuthentication(builder.Configuration);
// FlivoraActivity.Source is self-contained; no global ActivityListener registry needed here.

// ---- persistence: provider switch here, the model lives in Infrastructure ---------------------
var dbProvider = builder.Configuration["Database:Provider"] ?? "sqlite";
builder.Services.AddLivoraDbContext(dbProvider, builder.Configuration.GetConnectionString("Livora"));

// ---- modules: discover once, use twice (services pre-Build, endpoints post-Build) -------------
var modules = FlivoraModuleScanner.Discover(typeof(Program).Assembly);
var registry = new ModuleRegistry(modules);
// The registry is a service so modules can expose the capability surface without a static.
builder.Services.AddSingleton(registry);

using var bootLoggers = LoggerFactory.Create(b => b.AddConsole());
registry.ConfigureServices(new ModuleSeed(builder.Services, builder.Configuration,
    bootLoggers.CreateLogger("livora.modules.services")));
// Model contributions are frozen here: every module has registered by now, and the first DbContext
// build must see a stable list (EF caches the model per provider, so late additions would be random).
Livora.Server.Infrastructure.Persistence.ModelContributionRegistry.Freeze();

var app = builder.Build();

app.UseFlivoraCorrelation();
app.UseFlivoraProblemDetails();
// Auth envelope must wrap the auth handlers so a bare 401/403 becomes the shared problem body.
app.UseMiddleware<AuthEnvelopeMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/v1.json");
}

// Migrate on start only when explicitly asked: a broken migration must not black out a live host.
if (string.Equals(app.Configuration["Database:ApplyMigrationsOnStart"], "true", StringComparison.OrdinalIgnoreCase))
{
    await app.Services.ApplyLivoraMigrationsAsync(app.Lifetime.ApplicationStopping);
}
// Test/dev hosts may build the schema from the model directly (no migration files). Never the
// deployment path - documented in the persistence extension.
if (string.Equals(app.Configuration["Database:EnsureCreatedOnStart"], "true", StringComparison.OrdinalIgnoreCase))
{
    await app.Services.EnsureLivoraSchemaAsync();
}

var endpointCtx = new FlivoraEndpointContext(app, app.Configuration,
    bootLoggers.CreateLogger("livora.modules.endpoints"));
var mappingFailures = registry.MapEndpoints(endpointCtx);

// ---- liveness: process up. Dependency truth is in the deep report (probed, not assumed). -------
app.MapGet("/healthz", async (HttpContext ctx) =>
{
    // Parsed by hand on purpose: a missing query value must mean "liveness only", never a 400.
    var deep = string.Equals(ctx.Request.Query["deep"], "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(ctx.Request.Query["deep"], "true", StringComparison.OrdinalIgnoreCase);
    if (!deep)
    {
        return Results.Json(new
        {
            status = "ok",
            service = "livora-server",
            version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            correlationId = ctx.GetCorrelationId(),
        });
    }

    var reports = registry.Reports();
    var blocked = reports.Where(r => r.State is DependencyState.Degraded).ToList();
    return Results.Json(new
    {
        status = mappingFailures.Count == 0 && blocked.Count == 0 ? "ok" : "degraded",
        service = "livora-server",
        database = dbProvider,
        modules = reports.Select(r => new
        {
            key = r.Key,
            state = r.State.ToString().ToLowerInvariant(),
            detail = r.Detail,
        }),
        endpointMappingFailures = mappingFailures,
    });
}).AllowAnonymous();

// ---- the honest 404 inside the version prefix --------------------------------------------------
app.MapFallback("/api/v1/{**path}", (HttpContext ctx) =>
{
    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    return ctx.Response.WriteAsJsonAsync(new ApiProblem(
        Type: "https://livora.app/problems/not_found",
        Title: "not_found",
        Status: 404,
        Detail: "This endpoint does not exist. See /api/v1/platform/capabilities and /openapi/v1.json.",
        Instance: ctx.Request.Path.Value ?? "/api/v1",
        Code: ProblemCodes.NotFound,
        CorrelationId: ctx.GetCorrelationId()));
}).AllowAnonymous();

app.Logger.LogInformation(
    "LIVORA server starting: modules={Modules} database={Database} mappingFailures={Failures}",
    string.Join(",", modules.Select(m => m.Key)), dbProvider, mappingFailures.Count);

await app.RunAsync();

// Test host entry point (WebApplicationFactory<Program>). Must stay `public partial`.
public partial class Program { }
