using Livora.Server.Infrastructure.Engines.Decision;
using Livora.Server.Infrastructure.Persistence;

namespace Livora.Server.Modules.Intelligence;

/// <summary>
/// PURPOSE: the schema-contribution gate for the two engines lanes' entity types. The frozen
///          Phase-1 migration tests run MigrateAsync against the InitialCore snapshot, and EF 10
///          turns any model-vs-snapshot drift into PendingModelChangesWarning -> ERROR. Until the
///          lead's merged Wave4P1Schema migration exists (it is GENERATED from these contributions
///          at integration time), registering them unconditionally would break a frozen test in
///          every lane's gate. So registration is explicit and off by default:
///            "Modules:Intelligence:SchemaContribution": "true"   flips it on.
///          The lead flips it in appsettings when the migration lands; the requests file carries
///          the line. Endpoints that need persistence answer 503 provider_unavailable while the
///          gate is off — degradation is STATED, not faked (Wave 4 §31).
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// INVARIANTS:
///   - EnsureRegistered is idempotent and reads the REAL config value; no environment sniffing
///   - when off, nothing in the static registry references this lane's types (tripwire test)
/// </summary>
public static class EnginesSchemaGate
{
    public const string ConfigKey = "Modules:Intelligence:SchemaContribution";

    public static bool IsEnabled(IConfiguration configuration) =>
        string.Equals(configuration[ConfigKey], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Set by <see cref="EnsureRegistered"/> when THIS registration call lands: the
    /// ledger/pattern endpoints answer provider_unavailable (never a model exception) while schema
    /// contributions are gated off. Read it through <see cref="EnginesSchemaStatus"/> in a handler
    /// — the DI instance is the host's own answer; this static is only the process-wide fact that
    /// the contributions were registered at least once.</summary>
    public static bool Active { get; private set; }

    /// <summary>Per-host view of the gate. A test host that turns the contribution on must not
    /// make a later host (with the key off) believe its ledger tables exist: the old static-only
    /// read leaked across hosts in one process and answered 500 "no such table" where the honest
    /// degradation was 503. Registered as a singleton by both engines modules.</summary>
    public sealed class EnginesSchemaStatus(bool active)
    {
        public bool Active { get; } = active;
    }

    public static EnginesSchemaStatus StatusFor(IConfiguration configuration) =>
        new(IsEnabled(configuration));

    /// <summary>Register BOTH of the lane's contributions (intelligence dismissals + verification
    /// ledger). Safe to call from either module — the registry membership check makes it once-only.</summary>
    public static void EnsureRegistered(IConfiguration configuration, ILogger logger)
    {
        if (!IsEnabled(configuration))
        {
            logger.LogDebug("engines schema contribution gated off ({Key}); ledger endpoints degrade to 503", ConfigKey);
            return;
        }
        Active = true;
        foreach (var contribution in Contributions)
        {
            if (!ModelContributionRegistry.Contributions.Any(c => c.GetType() == contribution.GetType()))
            {
                try { ModelContributionRegistry.Add(contribution); }
                catch (InvalidOperationException)
                {
                    // Frozen without us = a test reset the registry mid-process; the host re-runs
                    // ConfigureServices before Freeze, so the next build lands correct. Log, never throw.
                    logger.LogWarning("schema contribution could not register (registry frozen): {Type}",
                        contribution.GetType().Name);
                }
            }
        }
    }

    /// <summary>The lane's contributions, constructed once per call site (stateless configurators).</summary>
    public static IReadOnlyList<IModelContribution> Contributions =>
    [
        new IntelligenceModelContribution(),
        new VerificationModelContribution(),
    ];
}
