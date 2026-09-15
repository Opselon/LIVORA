using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Infrastructure.Sync;

/// <summary>
/// PURPOSE: registers the sync lane's tables on the shared model without editing the frozen
///          LivoraDbContext — the append-only schema mechanism (CONTRACT-P1 §4). The lead turns
///          these types into the ONE Wave4P1Schema migration at merge (request line: migration-ready).
/// OWNER: Agent 02 (lane w4-p1b-platform).
/// CONSUMES: the entity classes in this folder — and ONLY those; configuring a core type here would
///           break the model, which is the intended tripwire.
/// PROVIDES: two tables: sync_batch_records, sync_change_records.
/// INVARIANTS:
///   - unique index (UserId, IdempotencyKey): the DATABASE decides key reuse — at-most-one
///     processing of a key is not merely an application check-then-write
///   - unique index (UserId, ServerRevision): the per-user change feed is strictly ordered with no
///     duplicated cursor positions; a concurrent batch that raced into the same slot loses on this
///     index and retries, which is the offline-safe ordering guarantee
///   - every user-owned table cascades on account deletion; rows are insert-only for the sync lane
///   - explicit HasMaxLength everywhere (§4 convention; keeps the merge-time migration honest)
/// EXTEND: wired from SyncModule.ConfigureServices via SyncModelContribution.EnsureRegistered() —
///         exactly one instance may ever reach the registry (double-configure breaks the model).
/// </summary>
public sealed class SyncModelContribution : IModelContribution
{
    /// <summary>Process-wide gate: the frozen registry list is not thread-safe and parallel test
    /// hosts must not append the same contribution twice.</summary>
    private static readonly object RegistrationGate = new();
    private static bool _registered;

    /// <summary>Idempotent, freeze-safe registration for module startup. Returns true when the
    /// contribution is present in the registry (registered here or by an earlier host boot);
    /// false only when neither is true — the caller must then refuse to claim the tables exist.</summary>
    public static bool EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (_registered) return true;
            try
            {
                ModelContributionRegistry.Add(new SyncModelContribution());
                _registered = true;
                return true;
            }
            catch (InvalidOperationException)
            {
                // The registry froze before we got there (test-order dependent). Still legal only
                // if a SyncModelContribution is inside — a host that froze without it means the
                // sync tables were never in that model.
                _registered = ModelContributionRegistry.Contributions
                    .Any(c => c.GetType() == typeof(SyncModelContribution));
                return _registered;
            }
        }
    }

    /// <summary>True when this lane's tables are part of the built model (asserted by tests so no
    /// one can claim sync persistence that was never wired).</summary>
    public static bool Registered
    {
        get
        {
            lock (RegistrationGate)
            {
                return ModelContributionRegistry.Contributions.Any(c => c.GetType() == typeof(SyncModelContribution));
            }
        }
    }

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SyncBatchRecord>(e =>
        {
            e.ToTable("sync_batch_records");
            e.HasKey(x => x.Id);
            // The idempotency guarantee lives in the schema, not in a racy application check.
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.IdempotencyKey).HasMaxLength(80);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.ResponseJson).HasMaxLength(200_000);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SyncChangeRecord>(e =>
        {
            e.ToTable("sync_change_records");
            e.HasKey(x => x.Id);
            // Monotonic feed cursor: two applied operations can never share a position.
            e.HasIndex(x => new { x.UserId, x.ServerRevision }).IsUnique();
            // The entity-head read (Max ResultRevision per entity) rides this index.
            e.HasIndex(x => new { x.UserId, x.EntityType, x.EntityId });
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.OperationId).HasMaxLength(80);
            e.Property(x => x.BatchKey).HasMaxLength(80);
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.EntityId).HasMaxLength(160);
            e.Property(x => x.Kind).HasMaxLength(24);
            e.Property(x => x.Outcome).HasMaxLength(24);
            e.Property(x => x.PayloadJson).HasMaxLength(60_000);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
