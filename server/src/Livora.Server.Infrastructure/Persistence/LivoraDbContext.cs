using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Infrastructure.Persistence;

/// <summary>
/// PURPOSE: the single LIVORA database model. Core platform tables live here as first-party
///          entities; every feature domain adds tables through <see cref="IModelContribution"/>
///          instead of editing this file — that is what lets 16 lanes share one schema.
/// OWNER: Agent 01/02 (integration-owned). Lanes never edit it.
/// CONSUMES: entity classes in this folder (core) + contributions from feature folders.
/// PROVIDES: one migration set per provider, FK-enforced, soft-delete aware.
/// INVARIANTS:
///   - every table has a text primary key (GUID "N") so client-minted ids sync across devices
///   - DateTimeOffset stored in UTC; no local-time columns
///   - deletes of user-owned rows are soft (DeletedAtUtc) where the row is evidence/audit-relevant;
///     hard delete happens only through the documented account-deletion path
///   - no health value, token, or payment payload is ever stored in an audit column
///   - concurrency: timestamped rows carry ConcurrencyToken for optimistic sync conflict detection
/// EXTEND: server/src/Livora.Server/Modules/&lt;Feature&gt;/&lt;Feature&gt;ModelContribution.cs
///         implementing IModelContribution (it must call modelBuilder.Entity&lt;&gt;() only for its
///         own types; registering a core type twice fails the model, which is the intended tripwire).
/// </summary>
public sealed class LivoraDbContext : DbContext
{
    public LivoraDbContext(DbContextOptions<LivoraDbContext> options) : base(options) { }

    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<AuthSession> Sessions => Set<AuthSession>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ConnectorState> Connectors => Set<ConnectorState>();
    public DbSet<SyncOperation> SyncOperations => Set<SyncOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ---- core: identity ---------------------------------------------------------------
        modelBuilder.Entity<UserAccount>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320);
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Property(x => x.GoogleSubject).HasMaxLength(200);
            e.HasIndex(x => x.GoogleSubject).IsUnique().HasFilter("\"GoogleSubject\" IS NOT NULL");
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.PrimaryLocale).HasMaxLength(10);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Tier).HasConversion<string>().HasMaxLength(24);
        });

        modelBuilder.Entity<AuthSession>(e =>
        {
            e.ToTable("auth_sessions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.RefreshTokenHash).IsUnique();
            e.Property(x => x.RefreshTokenHash).HasMaxLength(128);
            e.Property(x => x.DeviceLabel).HasMaxLength(160);
            e.HasOne(x => x.User).WithMany(u => u.Sessions).HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- core: auditability (Wave 4 §55: safe audit events, never raw payloads) --------
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.OccurredAtUtc });
            e.HasIndex(x => x.Type);
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Subject).HasMaxLength(160);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.MetadataJson).HasMaxLength(4000);
        });

        // ---- core: truthful connector state (Wave 4 §31: no fake connected states) ---------
        modelBuilder.Entity<ConnectorState>(e =>
        {
            e.ToTable("connector_states");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Provider }).IsUnique();
            e.Property(x => x.Provider).HasMaxLength(48);
            e.Property(x => x.State).HasMaxLength(32);
            e.Property(x => x.Detail).HasMaxLength(500);
            e.Property(x => x.LastErrorCode).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany(u => u.Connectors).HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- core: cloud sync (Wave 4 §21) -------------------------------------------------
        modelBuilder.Entity<SyncOperation>(e =>
        {
            e.ToTable("sync_operations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.EntityType, x.EntityId });
            // Idempotency: a client retry of the same operation must not apply twice.
            e.HasIndex(x => new { x.UserId, x.OperationId }).IsUnique();
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.OperationId).HasMaxLength(80);
            e.Property(x => x.Kind).HasMaxLength(24);
            e.Property(x => x.Outcome).HasMaxLength(24);
            e.Property(x => x.PayloadJson).HasMaxLength(60000);
            e.HasOne(x => x.User).WithMany(u => u.SyncOperations).HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- feature contributions (append-only schema ownership) ---------------------------
        foreach (var c in ModelContributionRegistry.Contributions)
        {
            c.Configure(modelBuilder);
        }
    }
}
