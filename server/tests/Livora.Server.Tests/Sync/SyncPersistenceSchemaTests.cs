using Livora.Server.Infrastructure.Persistence;
using Livora.Server.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Tests.Sync;

/// <summary>
/// PURPOSE: prove the sync lane's persistence is REAL on the default provider — the contributed
///          model builds, EnsureCreated creates the tables with the indexes the guarantees rest
///          on, and the unique constraints actually refuse duplicates at the DATABASE level (the
///          shared fixture uses the in-memory provider, which enforces none of this — so without
///          this file the "enforced by a unique index" claims in SyncBatchService would be words).
/// OWNER: Agent 02 (lane w4-p1b-platform).
/// CONSUMES: SyncModelContribution through the real LivoraDbContext; one temp SQLite file per
///           fixture instance, deleted on dispose (parallel-safe; the frozen SqliteMigrationTests
///          use their own files and stay independent of this one).
/// PROVIDES: the §4 proof ("build the model with your contribution and call EnsureCreated on an
///           isolated temp SQLite file") that the contract asks each lane for, and the migration-
///           ready evidence line filed in docs/architecture/wave4/requests/p1b.md.
/// INVARIANTS ASSERTED:
///   - sync_batch_records / sync_change_records exist as tables in the created schema
///   - (UserId, IdempotencyKey) unique: two live inserts of the same key collide — the request-level
///     idempotency guarantee survives a lost application-level race
///   - (UserId, ServerRevision) unique: two feed rows can never share a cursor position
///   - deleting a user cascades both tables (the account-deletion path cannot strand sync data)
/// </summary>
public sealed class SyncPersistenceSchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "livora-sync-schema-" + Guid.NewGuid().ToString("N")[..8]);

    private string ConnectionString => $"Data Source={Path.Combine(_dir, "test.db")}";

    private static LivoraDbContext NewContext(string connectionString)
    {
        SyncModelContribution.EnsureRegistered();
        var options = new DbContextOptionsBuilder<LivoraDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new SqliteConnectionInterceptor())
            .Options;
        return new LivoraDbContext(options);
    }

    private async Task<LivoraDbContext> CreatedAsync()
    {
        Directory.CreateDirectory(_dir);
        var db = NewContext(ConnectionString);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    [Fact]
    public async Task Contributed_entities_are_part_of_the_model_and_created_as_tables()
    {
        using var db = await CreatedAsync();

        Assert.NotNull(db.Model.FindEntityType(typeof(SyncBatchRecord)));
        Assert.NotNull(db.Model.FindEntityType(typeof(SyncChangeRecord)));

        var names = await TableNamesAsync(db);
        Assert.Contains("sync_batch_records", names);
        Assert.Contains("sync_change_records", names);
        // The frozen core tables still build alongside the contribution (no model collision).
        Assert.Contains("sync_operations", names);
    }

    [Fact]
    public async Task Idempotency_key_is_unique_per_user_at_the_database_level()
    {
        using var db = await CreatedAsync();
        var user = new UserAccount { Email = "idem@test.local", NormalizedEmail = "IDEM@TEST.LOCAL" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.Set<SyncBatchRecord>().Add(new SyncBatchRecord
        {
            UserId = user.Id, IdempotencyKey = "key-1", RequestHash = new string('a', 64),
            ResponseJson = "{}",
        });
        await db.SaveChangesAsync();

        db.Set<SyncBatchRecord>().Add(new SyncBatchRecord
        {
            UserId = user.Id, IdempotencyKey = "key-1", RequestHash = new string('b', 64),
            ResponseJson = "{}",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Uniqueness is per account: another user may reuse the same key string.
        var other = new UserAccount { Email = "other@test.local", NormalizedEmail = "OTHER@TEST.LOCAL" };
        db.Users.Add(other);
        await db.SaveChangesAsync();
        db.Set<SyncBatchRecord>().Add(new SyncBatchRecord
        {
            UserId = other.Id, IdempotencyKey = "key-1", RequestHash = new string('c', 64),
            ResponseJson = "{}",
        });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<SyncBatchRecord>().CountAsync());
    }

    [Fact]
    public async Task Feed_cursor_positions_are_unique_per_user_at_the_database_level()
    {
        using var db = await CreatedAsync();
        var user = new UserAccount { Email = "rev@test.local", NormalizedEmail = "REV@TEST.LOCAL" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.Set<SyncChangeRecord>().Add(new SyncChangeRecord
        {
            UserId = user.Id, ServerRevision = 1, ResultRevision = 1, OperationId = "op-1",
            EntityType = "goal", EntityId = "g1", Kind = "create",
        });
        await db.SaveChangesAsync();

        db.Set<SyncChangeRecord>().Add(new SyncChangeRecord
        {
            UserId = user.Id, ServerRevision = 1, ResultRevision = 2, OperationId = "op-2",
            EntityType = "goal", EntityId = "g2", Kind = "create",
        });
        // Two applied operations can never share a cursor slot: the ordering guarantee is schema-
        // enforced, so a concurrent batch that raced to the same revision fails loud, not silent.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Account_deletion_cascades_every_sync_table()
    {
        using var db = await CreatedAsync();
        var user = new UserAccount { Email = "gone@test.local", NormalizedEmail = "GONE@TEST.LOCAL" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.Set<SyncBatchRecord>().Add(new SyncBatchRecord
        {
            UserId = user.Id, IdempotencyKey = "k", RequestHash = new string('d', 64), ResponseJson = "{}",
        });
        db.Set<SyncChangeRecord>().Add(new SyncChangeRecord
        {
            UserId = user.Id, ServerRevision = 1, ResultRevision = 1, OperationId = "op",
            EntityType = "goal", EntityId = "g", Kind = "create",
        });
        db.SyncOperations.Add(new SyncOperation
        {
            UserId = user.Id, OperationId = "op", EntityType = "goal", EntityId = "g",
        });
        await db.SaveChangesAsync();

        db.Users.Remove(user);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.Set<SyncBatchRecord>().CountAsync());
        Assert.Equal(0, await db.Set<SyncChangeRecord>().CountAsync());
        Assert.Equal(0, await db.SyncOperations.CountAsync());
    }

    [Fact]
    public async Task Payload_and_key_widths_match_the_contract_gates()
    {
        using var db = await CreatedAsync();

        var batch = db.Model.FindEntityType(typeof(SyncBatchRecord))!;
        Assert.Equal(80, batch.FindProperty(nameof(SyncBatchRecord.IdempotencyKey))!.GetMaxLength());
        Assert.Equal(64, batch.FindProperty(nameof(SyncBatchRecord.RequestHash))!.GetMaxLength());

        var change = db.Model.FindEntityType(typeof(SyncChangeRecord))!;
        // The service's payload gate (60k) equals the column width: config can never outgrow schema.
        Assert.Equal(SyncBatchService.MaxPayloadChars,
            change.FindProperty(nameof(SyncChangeRecord.PayloadJson))!.GetMaxLength());
        Assert.Equal(80, change.FindProperty(nameof(SyncChangeRecord.OperationId))!.GetMaxLength());
    }

    private static async Task<List<string>> TableNamesAsync(LivoraDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        var names = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        }
        return names;
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); Directory.Delete(_dir, recursive: true); }
        catch { /* temp cleanup is best-effort; never fail a gate for it */ }
    }
}
