using Livora.Server.Infrastructure.Engines.Decision;
using Livora.Server.Infrastructure.Engines.Decision.Verification;
using Livora.Server.Modules.Intelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: prove the lane's schema contributions produce a REAL, working SQLite schema and the
///          EF adapters round-trip honestly — via EnsureCreated on a private probe context (the
///          contract's Phase-1 rule: lanes prove their model slice this way; the lead generates
///          the ONE Wave4P1Schema migration from these contributions at merge, so this repo gets
///          no migration file from me).
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// INVARIANTS: each test gets its own temp SQLite file (parallel-safe), FK/unique behaviour is
///             asserted against the actual database, not a mock.
/// </summary>
public sealed class EnginePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "livora-engines-" + Guid.NewGuid().ToString("N")[..8]);

    private string ConnectionString => $"Data Source={Path.Combine(_dir, "probe.db")}";

    /// <summary>Probe context: the shared core tables are NOT needed here — only this lane's
    /// contributions, applied exactly as the module would apply them.</summary>
    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var contribution in EnginesSchemaGate.Contributions)
                contribution.Configure(modelBuilder);
        }
    }

    private ProbeDbContext NewContext()
    {
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<ProbeDbContext>().UseSqlite(ConnectionString).Options;
        return new ProbeDbContext(options);
    }

    [Fact]
    public async Task Contributions_create_working_tables_with_the_documented_unique_keys()
    {
        using var db = NewContext();
        await db.Database.EnsureCreatedAsync();

        var store = new EfPatternDismissalStore(db);
        await store.AddAsync("user-a", "pat:LateSleepRecurring:2026-02-01..2026-03-10", "LateSleepRecurring");
        await store.AddAsync("user-a", "pat:LateSleepRecurring:2026-02-01..2026-03-10", "LateSleepRecurring"); // idempotent
        var listed = await store.ListAsync("user-a");
        Assert.Single(listed);
        Assert.Equal("pat:LateSleepRecurring:2026-02-01..2026-03-10", listed[0].PatternId);
    }

    [Fact]
    public async Task A_dismissed_pattern_returns_when_the_dismissal_is_deleted()
    {
        // Product law: patterns are confidence-aware, REVISABLE and DELETABLE — deletion of the
        // dismissal must restore the finding, so the store has to support remove for real.
        using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        var store = new EfPatternDismissalStore(db);

        await store.AddAsync("u", "pat:X:2026-01-01..2026-01-31", "X");
        Assert.Single(await store.ListAsync("u"));
        Assert.True(await store.RemoveAsync("u", "pat:X:2026-01-01..2026-01-31"));
        Assert.Empty(await store.ListAsync("u"));
        Assert.False(await store.RemoveAsync("u", "pat:never-dismissed"));   // honest false, no throw
    }

    [Fact]
    public async Task Dismissals_are_scoped_per_user_idor_line_holds_in_the_store()
    {
        using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        var store = new EfPatternDismissalStore(db);

        await store.AddAsync("alice", "pat:Secret:2026-01-01..2026-01-02", "Secret");
        Assert.Empty(await store.ListAsync("bob"));
        Assert.False(await store.RemoveAsync("bob", "pat:Secret:2026-01-01..2026-01-02"));
    }

    [Fact]
    public async Task Unique_user_claim_index_enforces_upsert_never_forked_verdicts()
    {
        using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        var ledger = new EfVerificationLedger(db);

        var first = Claim("claim-1", "system_verified", "accepted");
        await ledger.UpsertAsync(first);
        var again = Claim("claim-1", "self_reported", "implausible");
        await ledger.UpsertAsync(again);           // replaces, does not fork

        var row = await ledger.FindAsync("user-1", "claim-1");
        Assert.NotNull(row);
        Assert.Equal("self_reported", row!.TrustLevel);   // the LATEST verdict is what survives
        Assert.Equal("implausible", row.Status);
        Assert.Equal(1, await db.Set<VerificationClaimRecord>().CountAsync());
        Assert.Single(row.Evidence);
    }

    [Fact]
    public async Task Ledger_list_and_find_are_scoped_to_the_owner()
    {
        using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        var ledger = new EfVerificationLedger(db);

        await ledger.UpsertAsync(Claim("mine", "device_derived", "accepted", userId: "user-1"));
        await ledger.UpsertAsync(Claim("theirs", "device_derived", "accepted", userId: "user-2"));

        var (items, total) = await ledger.ListAsync("user-1", offset: 0, limit: 10);
        Assert.Equal(1, total);
        Assert.Equal("mine", Assert.Single(items).ClaimId);
        Assert.Null(await ledger.FindAsync("user-1", "theirs"));   // IDOR: not found, not 500
    }

    [Fact]
    public async Task Soft_delete_hides_a_claim_from_reads_without_destroying_the_row()
    {
        using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        var ledger = new EfVerificationLedger(db);

        var claim = Claim("gone", "provider_derived", "accepted");
        await ledger.UpsertAsync(claim);
        var stored = await ledger.FindAsync("user-1", "gone");
        await ledger.SoftDeleteAsync(stored!);

        Assert.Null(await ledger.FindAsync("user-1", "gone"));
        Assert.Equal(1, await db.Set<VerificationClaimRecord>().CountAsync());   // row remains (audit)
    }

    [Fact]
    public void Gate_is_off_by_default_and_the_contributions_are_inert_until_flipped()
    {
        // The migration tripwire in the frozen persistence tests proves why: contributions must
        // not join the model before the lead's Wave4P1Schema exists. Default = off, explicitly.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        Assert.False(EnginesSchemaGate.IsEnabled(config));

        var on = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Modules:Intelligence:SchemaContribution"] = "true",
        }).Build();
        Assert.True(EnginesSchemaGate.IsEnabled(on));
    }

    [Fact]
    public void Contributions_declare_exactly_the_lanes_three_tables()
    {
        var builder = new ModelBuilder();
        foreach (var c in EnginesSchemaGate.Contributions) c.Configure(builder);
        var tables = builder.Model.GetEntityTypes()
            .Select(t => t.GetTableName()).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(["pattern_dismissals", "verification_claims", "verification_evidence"], tables);
    }

    private static VerificationClaimRecord Claim(string claimId, string trust, string status,
        string userId = "user-1") => new()
    {
        UserId = userId,
        ClaimId = claimId,
        ClaimType = ClaimTypes.HealthMetric,
        SourceKind = ClaimSources.Device,
        MetricKey = "activity.steps",
        ClaimedValue = 8_000,
        Unit = "steps",
        SourceRef = "watch:x",
        TrustLevel = trust,
        Status = status,
        Confidence = 0.75,
        Evidence =
        [
            new VerificationEvidenceRecord
            {
                ClaimRowId = claimId, EvidenceId = "e-1", SourceKind = ClaimSources.Device,
                MetricKey = "activity.steps", Value = 8_000, Unit = "steps", SourceRef = "watch:x",
                ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            },
        ],
    };

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); Directory.Delete(_dir, recursive: true); }
        catch { /* temp cleanup is best-effort; never fail a test for it */ }
    }
}
