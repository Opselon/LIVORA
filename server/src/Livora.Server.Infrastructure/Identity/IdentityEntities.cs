using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Infrastructure.Identity;

// ============================================================================
// Identity-lane additions (P1-C) ON TOP of the frozen core tables.
//
// CURRENT SHAPE (Wave 4 P1 integration + repair lane R2): these entity types ride
// the SHARED LivoraDbContext through IdentityModelContribution (§4 as written):
//   - design time: the host's HostDesignTimeFactory calls EnsureRegistered() before
//     the lead generated the single Wave4P1Schema migration (tables:
//     identity_security_profiles, auth_session_lineage, auth_revoked_refresh_tokens);
//   - runtime: IdentityModule.ConfigureServices calls EnsureRegistered() before the
//     registry freezes, so the live model, the migration, and every test context
//     agree on one schema (proved by the Identity* tests running against the fixture
//     host's EnsureCreated schema — a second context would have to be created
//     separately and could silently drift; it was deleted for exactly that reason).
// The P1-C lane originally shipped a private IdentitySideDbContext because
// PendingModelChangesWarning made the frozen migration test order-dependent before
// Wave4P1Schema existed (R-p1c-1). That precondition is gone: keeping the side
// context now would be two sources of truth for one schema. Deletion recorded in
// docs/quality/wave4/requests/r2.md (R-r2-3).
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
/// PURPOSE: the schema definition of the identity lane's tables — ONE source of truth, consumed by
///          <see cref="IdentityModelContribution"/>, which the shared LivoraDbContext applies at
///          both design time (the lead's HostDesignTimeFactory) and runtime (IdentityModule).
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
/// THE shared-model vehicle (§4 as written): IdentityModule.ConfigureServices registers it at
/// runtime and the host's design-time factory registers it before the lead generated
/// Wave4P1Schema, so live host, migration, and tests all build ONE model from ONE schema body.
/// </summary>
public sealed class IdentityModelContribution : IModelContribution
{
    private static readonly object RegistrationGate = new();
    private static bool _registered;

    /// <summary>Idempotent, freeze-safe registration into the shared registry; true when the
    /// contribution is (now) inside it. Called from IdentityModule.ConfigureServices (runtime) and
    /// from the host's HostDesignTimeFactory (design time) — both before the registry freezes.</summary>
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
