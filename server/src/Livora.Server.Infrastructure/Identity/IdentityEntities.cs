using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Infrastructure.Identity;

// ============================================================================
// Identity-lane additions (P1-C) ON TOP of the frozen core tables.
//
// WHY A SECOND CONTEXT — and the reproducible platform fact behind it:
// CONTRACT-P1 §4 says "one LivoraDbContext, extend via IModelContribution".
// Executed evidence in this worktree (2026-09-14): ANY contribution registered
// before a MigrateAsync makes the FROZEN Persistence/SqliteMigrationTests throw
//   InvalidOperationException: PendingModelChangesWarning (model vs the frozen
//   InitialCore snapshot) — 5/21 server tests red, deterministic across three runs.
// Lanes may not write migrations (§4) and may not touch that test (out of scope),
// so the shared-model contribution path is blocked for the entire lane phase.
// The identity state that must persist (lockout, rotation families, tombstones)
// therefore lives in IdentitySideDbContext below: the SAME database (same
// ConnectionStrings:Livora, same SqliteConnectionInterceptor), its own model.
// IdentityModelContribution is shipped UNREGISTERED so the lead's integration-time
// Wave4P1Schema pass can fold these types into the shared model by flipping ONE
// line (requests/p1c.md R-p1c-1 records this with the repro).
// ============================================================================

/// <summary>
/// Per-email brute-force state for the login path. Keyed by NORMALIZED EMAIL — not by user id —
/// on purpose: a nonexistent account and a real one accumulate failures identically, so the
/// 429/403 escalation ladder is not an oracle for "is this email registered?" (threat model T-03).
/// </summary>
public sealed class IdentitySecurityProfile
{
    /// <summary>PK = normalized (trim + lowercase, invariant) email address.</summary>
    public string NormalizedEmail { get; set; } = "";
    /// <summary>Set once the email resolves to a real account; denormalised convenience for the
    /// deletion path, which must destroy lockout state keyed by a personal email.</summary>
    public string? UserId { get; set; }
    public int FailedAttempts { get; set; }
    /// <summary>Start of the current sliding failure window; the counter resets when it ages out.</summary>
    public DateTimeOffset? WindowStartedAtUtc { get; set; }
    public DateTimeOffset? LastFailureAtUtc { get; set; }
    /// <summary>Hard lockout: logins for this email are refused until this instant.</summary>
    public DateTimeOffset? LockoutUntilUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// PURPOSE: which login lineage a session belongs to. The frozen <c>auth_sessions</c> table cannot
///          gain columns, so the identity lane tracks the rotation FAMILY here: a fresh login seeds
///          a family (family id = the seeding session id); rotations continue in the same family.
///          A replay of a rotated-out token (theft signal) kills the whole family — the attacker
///          keeps nothing — while the user's OTHER, independent logins survive untouched.
/// OWNER: Agent 03 (identity lane).
/// </summary>
public sealed class AuthSessionLineage
{
    public string SessionId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string UserId { get; set; } = "";
}

/// <summary>
/// PURPOSE: the tombstone of every refresh-token hash that leaves the live set (rotation, logout,
///          user revoke, family wipe, account deletion). This is what makes REUSE DETECTABLE: a
///          rotated-out token matches no live session, and without this table it would be
///          indistinguishable from a forged string. With it, "was valid, presented again after
///          rotation" is a fact — the contract's theft signal (§5c note 2).
/// INVARIANTS:
///   - <see cref="Reason"/> ∈ rotation | logout | revoked_by_user | family_wipe | account_deleted
///     (fixed vocabulary in RevokedRefreshReasons, host side)
///   - only <c>rotation</c> tombstones trigger family-wide revocation when replayed; replays after
///     a deliberate logout just fail (threat model T-05/T-06 pin the semantics)
///   - the hash is SHA-256 hex of the plaintext token — the same transform live rows use. No
///     plaintext, ever. Rows are RETAINED after account deletion: a hash over 288 random bits is a
///     pre-hash non-secret, and dropping it would disable reuse detection for tokens an attacker
///     might still hold (the deletion policy states this explicitly).
/// </summary>
public sealed class RevokedRefreshToken
{
    public string TokenHash { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string FamilyId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset RevokedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// PURPOSE: the schema definition of the identity lane's tables. Shared by BOTH paths so there is
///          one schema truth: <see cref="IdentitySideDbContext"/> (what runs today) and
///          <see cref="IdentityModelContribution"/> (what the lead folds into the shared model at
///          integration, per R-p1c-1).
/// CONVENTIONS (§4 honored): text keys, UTC DateTimeOffset, explicit HasMaxLength, unique indexes
///          where idempotency matters (TokenHash is the natural key — the same rotated-out hash can
///          be replayed a hundred times and must not create a hundred rows: inserts are guarded by
///          the caller's upsert-or-ignore pattern, and the unique PK is the backstop).
/// </summary>
public static class IdentitySchema
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdentitySecurityProfile>(e =>
        {
            e.ToTable("identity_security_profiles");
            e.HasKey(x => x.NormalizedEmail);
            e.Property(x => x.NormalizedEmail).HasMaxLength(320);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<AuthSessionLineage>(e =>
        {
            e.ToTable("auth_session_lineage");
            e.HasKey(x => x.SessionId);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.FamilyId).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<RevokedRefreshToken>(e =>
        {
            e.ToTable("auth_revoked_refresh_tokens");
            e.HasKey(x => x.TokenHash);
            e.Property(x => x.TokenHash).HasMaxLength(128);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.FamilyId).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.Reason).HasMaxLength(32);
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.UserId);
        });
    }
}

/// <summary>
/// PURPOSE: the identity lane's persistence slice — SAME database as LivoraDbContext (same
///          connection string, same SQLite interceptor for FK/WAL), separate model, so the frozen
///          core migration snapshot keeps matching. See the banner at the top of this file for why
///          the shared-contribution path is blocked in the lane phase (R-p1c-1).
/// OWNER: Agent 03 (identity lane).
/// INVARIANTS: no FKs declared to the core tables — cross-context referential integrity is
///           enforced by the application (the deletion path explicitly cleans these tables), and
///           the ids are app-minted GUIDs. The lead's merge fold-in (R-p1c-1) can restore FKs.
/// </summary>
public sealed class IdentitySideDbContext : DbContext
{
    public IdentitySideDbContext(DbContextOptions<IdentitySideDbContext> options) : base(options) { }

    public DbSet<IdentitySecurityProfile> SecurityProfiles => Set<IdentitySecurityProfile>();
    public DbSet<AuthSessionLineage> SessionLineage => Set<AuthSessionLineage>();
    public DbSet<RevokedRefreshToken> RevokedTokens => Set<RevokedRefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        IdentitySchema.Configure(modelBuilder);
    }
}

/// <summary>
/// The shared-model fold-in vehicle for integration time. Deliberately NOT registered by the lane
/// (registering it deterministically breaks the frozen SqliteMigrationTests — see file banner and
/// R-p1c-1). The lead calls <see cref="EnsureRegistered"/> once from the module (or rewrites these
/// entity types directly into Wave4P1Schema) and flips IdentitySideDbContext away.
/// </summary>
public sealed class IdentityModelContribution : IModelContribution
{
    private static readonly object RegistrationGate = new();
    private static bool _registered;

    /// <summary>Idempotent, freeze-safe registration into the shared registry; true when the
    /// contribution is (now) inside it. The lane never calls this today; it exists so the merge
    /// fold-in is a one-line change with the same schema body.</summary>
    public static bool EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (_registered) return true;
            try
            {
                ModelContributionRegistry.Add(new IdentityModelContribution());
                _registered = true;
                return true;
            }
            catch (InvalidOperationException)
            {
                _registered = Registered;
                return _registered;
            }
        }
    }

    public static bool Registered
    {
        get
        {
            lock (RegistrationGate)
            {
                return ModelContributionRegistry.Contributions.Any(c => c is IdentityModelContribution);
            }
        }
    }

    public void Configure(ModelBuilder modelBuilder) => IdentitySchema.Configure(modelBuilder);
}
