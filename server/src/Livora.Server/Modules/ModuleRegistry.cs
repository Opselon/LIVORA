using System.Reflection;
using Livora.Server.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Livora.Server.Modules;

/// <summary>
/// PURPOSE: find every feature module in an assembly so lanes never edit the composition root —
///          the same self-registration idea the MAUI client uses for its DI marker regions.
/// OWNER: Agent 02 (frozen mechanism). Lanes supply implementations only.
/// PROVIDES: a deterministic, key-ordered module list.
/// INVARIANTS:
///   - a module is a public, non-abstract, parameterless-constructible <see cref="IFlivoraModule"/>
///   - duplicate <c>Key</c> values fail fast: a silent overwrite would hide a lane ownership clash
///   - ordering by Key makes route registration (and therefore OpenAPI output) reproducible
/// EXTEND: modules live in server/src/Livora.Server/Modules/&lt;Feature&gt;/ in the host assembly.
/// </summary>
public static class FlivoraModuleScanner
{
    public static IReadOnlyList<IFlivoraModule> Discover(params Assembly[] assemblies)
    {
        var found = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition
                        && typeof(IFlivoraModule).IsAssignableFrom(t))
            .Select(t => (IFlivoraModule?)Activator.CreateInstance(t))
            .Where(m => m is not null)
            .Cast<IFlivoraModule>()
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .ToList();

        var dupes = found.GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (dupes.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate LIVORA module keys: {string.Join(", ", dupes)}. Two lanes registered the " +
                "same feature key — resolve module ownership before startup.");
        }

        return found;
    }
}

/// <summary>
/// PURPOSE: run the module lifecycle safely. One lane's bug must degrade one feature, never the
///          whole platform (Wave 4 rule G: safe degradation).
/// OWNER: Agent 02.
/// INVARIANTS:
///   - every phase is individually guarded; a throwing module is recorded, not propagated
///   - <see cref="Reports"/> is LIVE per call (a module's verdict must not go stale in memory)
///   - a module that fails to map endpoints reports <see cref="DependencyState.Degraded"/> with the
///     exception type only — no stack text leaks through the capability surface
/// </summary>
public sealed class ModuleRegistry
{
    private readonly IReadOnlyList<IFlivoraModule> _modules;
    private readonly HashSet<string> _mapFailed = new(StringComparer.OrdinalIgnoreCase);

    public ModuleRegistry(IReadOnlyList<IFlivoraModule> modules) => _modules = modules;

    public IReadOnlyList<IFlivoraModule> Modules => _modules;

    /// <summary>Module keys whose routes failed to map at startup — disclosed, never hidden.</summary>
    public IReadOnlyList<string> StartupFailures { get; private set; } = [];

    /// <summary>Phase 1: service registration (must run before the provider is built).</summary>
    public void ConfigureServices(ModuleSeed seed)
    {
        foreach (var m in _modules)
        {
            try { m.ConfigureServices(seed); }
            catch (Exception ex)
            {
                seed.Logger.LogError(ex, "module {ModuleKey} failed ConfigureServices", m.Key);
            }
        }
    }

    /// <summary>Phase 2: endpoint mapping (after Build). Failures are collected, never thrown.</summary>
    public IReadOnlyList<string> MapEndpoints(FlivoraEndpointContext ctx)
    {
        foreach (var m in _modules)
        {
            try { m.MapEndpoints(ctx); }
            catch (Exception ex)
            {
                _mapFailed.Add(m.Key);
                ctx.Logger.LogError(ex, "module {ModuleKey} failed MapEndpoints", m.Key);
            }
        }
        StartupFailures = _mapFailed.OrderBy(k => k, StringComparer.Ordinal).ToList();
        return StartupFailures;
    }

    /// <summary>Honest capability report for all modules (used by /healthz?deep=1 and capabilities).</summary>
    public IReadOnlyList<ModuleHealth> Reports()
    {
        var list = new List<ModuleHealth>(_modules.Count);
        foreach (var m in _modules)
        {
            ModuleHealth report;
            try { report = m.Report(); }
            catch (Exception ex)
            {
                report = new ModuleHealth(m.Key, DependencyState.Degraded,
                    $"module report threw {ex.GetType().Name}", null);
            }

            // A module that could not even map its routes can never claim Ok.
            if (_mapFailed.Contains(m.Key) && report.State == DependencyState.Ok)
            {
                report = report with
                {
                    State = DependencyState.Degraded,
                    Detail = "endpoint mapping failed at startup; " + report.Detail,
                };
            }
            list.Add(report);
        }
        return list;
    }
}
