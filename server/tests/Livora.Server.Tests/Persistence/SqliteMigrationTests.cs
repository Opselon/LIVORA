using Livora.Server.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Tests.Persistence;

/// <summary>
/// PURPOSE: prove the backend schema is real — the EF migration actually creates working tables on
///          SQLite (the default provider), with FK enforcement and the unique constraints that the
///          sync/identity lanes depend on. A model that has never been migrated is not a schema.
/// OWNER: Agent 02 (platform) / Agent 16 (gate).
/// INVARIANTS: each test gets its OWN temporary SQLite file (parallel-safe) which is deleted after;
///             no test uses the in-memory provider for schema assertions (it validates nothing).
/// </summary>
public sealed class SqliteMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "livora-schema-" + Guid.NewGuid().ToString("N")[..8]);

    private string DbPath => Path.Combine(_dir, "test.db");
    private string ConnectionString => $"Data Source={DbPath}";

    private LivoraDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<LivoraDbContext>()
            .UseSqlite(ConnectionString)
            .AddInterceptors(new SqliteConnectionInterceptor())
            .Options;
        return new LivoraDbContext(options);
    }

    [Fact]
    public async Task InitialCore_migration_creates_every_core_table()
    {
        Directory.CreateDirectory(_dir);
        using var db = NewContext();
        await db.Database.MigrateAsync();

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        var names = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        }

        Assert.Contains("users", names);
        Assert.Contains("auth_sessions", names);
        Assert.Contains("audit_events", names);
        Assert.Contains("connector_states", names);
        Assert.Contains("sync_operations", names);
    }

    [Fact]
    public async Task Foreign_keys_are_enforced_so_orphans_cannot_be_written()
    {
        Directory.CreateDirectory(_dir);
        using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
            // A session with no matching user must be refused — SQLite needs the pragma, which the
            // connection interceptor supplies; if the interceptor is removed this test goes red.
            db.Sessions.Add(new AuthSession
            {
                UserId = "does-not-exist",
                RefreshTokenHash = new string('a', 64),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Sync_operation_idempotency_key_is_unique_per_user()
    {
        Directory.CreateDirectory(_dir);
        using var db = NewContext();
        await db.Database.MigrateAsync();

        var user = new UserAccount { Email = "dup@test.local", NormalizedEmail = "DUP@TEST.LOCAL" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.SyncOperations.Add(new SyncOperation
        {
            UserId = user.Id, OperationId = "op-1", EntityType = "goal", EntityId = "g1",
        });
        await db.SaveChangesAsync();

        db.SyncOperations.Add(new SyncOperation
        {
            UserId = user.Id, OperationId = "op-1", EntityType = "goal", EntityId = "g2",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        // After a failed batch EF keeps the rejected entity tracked; drop it or the next save just
        // replays the same violation (this is a real EF behaviour every lane must know).
        db.ChangeTracker.Clear();

        // A different user may reuse the same operation id: uniqueness is per account, not global.
        var other = new UserAccount { Email = "other@test.local", NormalizedEmail = "OTHER@TEST.LOCAL" };
        db.Users.Add(other);
        await db.SaveChangesAsync();
        db.SyncOperations.Add(new SyncOperation
        {
            UserId = other.Id, OperationId = "op-1", EntityType = "goal", EntityId = "g9",
        });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.SyncOperations.CountAsync());
    }

    [Fact]
    public async Task Normalized_email_unique_prevents_duplicate_accounts()
    {
        Directory.CreateDirectory(_dir);
        using var db = NewContext();
        await db.Database.MigrateAsync();

        db.Users.Add(new UserAccount { Email = "a@b.co", NormalizedEmail = "A@B.CO" });
        await db.SaveChangesAsync();
        db.Users.Add(new UserAccount { Email = "a@b.co", NormalizedEmail = "A@B.CO" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Federated_account_may_have_no_email_but_google_subject_is_unique()
    {
        Directory.CreateDirectory(_dir);
        using var db = NewContext();
        await db.Database.MigrateAsync();

        db.Users.Add(new UserAccount { GoogleSubject = "google-sub-1" });
        db.Users.Add(new UserAccount { GoogleSubject = "google-sub-2" });
        await db.SaveChangesAsync();

        db.Users.Add(new UserAccount { GoogleSubject = "google-sub-1" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); Directory.Delete(_dir, recursive: true); }
        catch { /* temp cleanup is best-effort; never fail a test for it */ }
    }
}
