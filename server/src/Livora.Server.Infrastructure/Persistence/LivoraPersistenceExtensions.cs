using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Livora.Server.Infrastructure.Persistence;

/// <summary>
/// PURPOSE: let a feature domain extend the shared model without editing LivoraDbContext — the
///          schema equivalent of the client's module seam.
/// OWNER: Agent 02 (mechanism); each contribution file belongs to its lane.
/// INVARIANTS:
///   - contributions are appended once at startup, then the list is frozen (a mid-request append
///     would produce a silently different model per thread — EF caches the model once)
///   - a contribution may configure ONLY its own entity types; colliding with a core type makes the
///     model invalid, and EF fails loudly at first use, which is the intended tripwire
/// EXTEND: Modules/<Feature>/<Feature>ModelContribution.cs + one line in the host module's
///         ConfigureServices (that one line is the module's only schema touch point).
/// </summary>
public interface IModelContribution
{
    void Configure(ModelBuilder modelBuilder);
}

/// <summary>Append-only registry with freeze-on-first-build semantics.</summary>
public static class ModelContributionRegistry
{
    private static readonly List<IModelContribution> _pending = [];
    private static bool _frozen;

    public static IReadOnlyList<IModelContribution> Contributions { get; private set; } = [];

    public static void Add(IModelContribution contribution)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                $"Model contributions are frozen after the first DbContext build; " +
                $"{contribution.GetType().Name} tried to register too late.");
        }
        _pending.Add(contribution);
        Contributions = _pending.ToArray();
    }

    /// <summary>Called by the DbContext factory once the host has registered every module.</summary>
    public static void Freeze()
    {
        _frozen = true;
        Contributions = _pending.ToArray();
    }

    /// <summary>Test-only: return to a clean, unfrozen state (parallel fixtures must not leak model
    /// contributions into each other). Production code never calls this.</summary>
    public static void ResetForTests()
    {
        _frozen = false;
        _pending.Clear();
        Contributions = [];
    }
}

/// <summary>
/// PURPOSE: the provider switch. SQLite is the zero-dependency default so the API and its tests run
///          anywhere; Postgres is the deployment target with the SAME model and migration set.
/// OWNER: Agent 02.
/// INVARIANTS:
///   - SQLite: foreign keys enforced per connection, WAL on, busy timeout set (single-writer skew)
///   - migrations are applied explicitly (CLI or startup flag), never EnsureCreated on a real DB
///   - no provider-specific branching above this file: the model must work on both
/// </summary>
public static class LivoraPersistenceExtensions
{
    /// <summary>
    /// PURPOSE: the DateTimeOffset storage convention (UTC unix-ms on every provider) shared by the
    ///          host context AND lane probe contexts (test/dev contexts that apply contributions
    ///          directly without the host). SQLite cannot ORDER BY a DateTimeOffset TEXT column;
    ///          INTEGER millis sorts and compares natively. Keep every model that maps LIVORA
    ///          entities calling this from ConfigureConventions.
    /// OWNER: lead (moved from LivoraDbContext during Wave 4 P1 integration).
    /// </summary>
    public static void ApplyLivoraConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UnixMillisUtcConverter>();

    /// <summary>Converter used by <see cref="ApplyLivoraConventions"/>; public so lane probe
    /// contexts can reference the exact same storage semantics.</summary>
    public sealed class UnixMillisUtcConverter : Microsoft.EntityFrameworkCore.Storage.ValueConversion
                                     .ValueConverter<DateTimeOffset, long>
    {
        public UnixMillisUtcConverter()
            : base(v => v.ToUnixTimeMilliseconds(),
                   v => DateTimeOffset.FromUnixTimeMilliseconds(v))
        { }
    }

    public static IServiceCollection AddLivoraDbContext(
        this IServiceCollection services, string provider, string? connectionString)
    {
        services.AddDbContext<LivoraDbContext>(options =>
        {
            switch (provider.ToLowerInvariant())
            {
                case "inmemory":
                    // Tests only: same LINQ surface, no disk, no FK enforcement (documented limit).
                    options.UseInMemoryDatabase("livora-tests");
                    break;
                case "postgres":
                    options.UseNpgsql(Resolve(provider, connectionString));
                    break;
                default:
                    options.UseSqlite(Resolve(provider, connectionString),
                        sqlite => sqlite.CommandTimeout(30));
                    // Foreign keys are OFF by default in SQLite; WAL keeps readers unblocked by the
                    // sync writer. Registered per DbContext instance so pooled connections re-apply it.
                    options.AddInterceptors(new SqliteConnectionInterceptor());
                    break;
            }
        });
        return services;
    }

    private static string Resolve(string provider, string? connectionString) => provider.ToLowerInvariant() switch
    {
        "postgres" => connectionString
            ?? throw new InvalidOperationException("ConnectionStrings:Livora is required when Database:Provider=postgres"),
        _ => connectionString ?? "Data Source=livora.db",
    };

    /// <summary>
    /// Build the schema straight from the model (no migration history). For TEST hosts and the
    /// throwaway dev DB only — deployment uses ApplyLivoraMigrationsAsync. Exists because the
    /// shared API fixture needs real SQLite constraints (unique/FK) that the in-memory provider
    /// does not enforce, without paying migration time per fixture.
    /// </summary>
    public static async Task EnsureLivoraSchemaAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LivoraDbContext>();
        await db.Database.EnsureCreatedAsync(ct);
    }

    /// <summary>
    /// Apply pending migrations at startup (opt-in via Database:ApplyMigrationsOnStart=true).
    /// A migration failure is logged and rethrown — the operator must see it; silently starting
    /// with a stale schema would corrupt data, which is worse than a failed boot.
    /// </summary>
    public static async Task ApplyLivoraMigrationsAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("livora.migrations");
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LivoraDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("database schema up to date");
            return;
        }

        if (db.Database.IsRelational())
        {
            logger.LogInformation("applying {Count} pending migrations: {Names}",
                pending.Count, string.Join(",", pending));
            await db.Database.MigrateAsync(ct);
        }
    }
}
