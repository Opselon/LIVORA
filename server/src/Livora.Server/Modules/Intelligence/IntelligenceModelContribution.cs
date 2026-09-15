using Livora.Server.Infrastructure.Engines.Decision;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Modules.Intelligence;

/// <summary>Paged row of the dismissal list (stable shape; the client pages with PageRequest).</summary>
public sealed record DismissalView(string PatternId, string Kind, DateTimeOffset CreatedAtUtc);

/// <summary>Budget + locale safety for the explanation port (the renderer refuses to be a DoS).</summary>
public static class ExplanationLimits
{
    public const int MaxItemsPerCall = 50;

    /// <summary>Only "en" and "fa" exist; anything else degrades to en (and the response says which).</summary>
    public static string SafeLocale(string? requested) =>
        string.Equals(requested, "fa", StringComparison.OrdinalIgnoreCase) ? "fa" : "en";
}

/// <summary>
/// PURPOSE: attach the intelligence lane's entity types to the shared LivoraDbContext WITHOUT
///          editing it — the append-only schema seam (IModelContribution). The lead's merged
///          Wave4P1Schema migration generates from these; this lane ships no migration file.
/// OWNER: Agent 10+11. Configure() touches ONLY this lane's own types (the model-builder throws
///        on a core type, which is the intended tripwire).
/// </summary>
public sealed class IntelligenceModelContribution : Livora.Server.Infrastructure.Persistence.IModelContribution
{
    public const string Key = "intelligence";

    public void Configure(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PatternDismissalRecord>(e =>
        {
            e.ToTable("pattern_dismissals");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.PatternId).HasMaxLength(160).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(48);
            e.Property(x => x.DeletedAtUtc);
            // A user dismisses a given pattern once: the DB enforces what the adapter checks.
            e.HasIndex(x => new { x.UserId, x.PatternId }).IsUnique();
            e.HasIndex(x => x.UserId);
        });
    }
}

/// <summary>Same seam for the verification ledger (declared here so ONE registration call per
/// module covers both of the lane's entity sets; verification module maps the same instances).</summary>
public sealed class VerificationModelContribution : Livora.Server.Infrastructure.Persistence.IModelContribution
{
    public const string Key = "verification";

    public void Configure(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Livora.Server.Infrastructure.Engines.Decision.Verification.VerificationClaimRecord>(e =>
        {
            e.ToTable("verification_claims");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ClaimId).HasMaxLength(80).IsRequired();
            e.Property(x => x.ClaimType).HasMaxLength(32);
            e.Property(x => x.SourceKind).HasMaxLength(24);
            e.Property(x => x.MetricKey).HasMaxLength(64);
            e.Property(x => x.Unit).HasMaxLength(16);
            e.Property(x => x.SourceRef).HasMaxLength(160);
            e.Property(x => x.TrustLevel).HasMaxLength(24);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.AttemptedRulesJson).HasMaxLength(4000);
            e.Property(x => x.ContributingRuleKeysJson).HasMaxLength(600);
            e.Property(x => x.RefusalReason).HasMaxLength(120);
            e.Property(x => x.ReviewerId).HasMaxLength(64);
            e.Property(x => x.ReviewDecision).HasMaxLength(16);
            // Idempotency: a client re-posting the same claim id updates, never forks a second verdict.
            e.HasIndex(x => new { x.UserId, x.ClaimId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            e.HasMany(x => x.Evidence).WithOne(x => x.Claim!)
                .HasForeignKey(x => x.ClaimRowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Livora.Server.Infrastructure.Engines.Decision.Verification.VerificationEvidenceRecord>(e =>
        {
            e.ToTable("verification_evidence");
            e.HasKey(x => x.Id);
            e.Property(x => x.EvidenceId).HasMaxLength(80).IsRequired();
            e.Property(x => x.SourceKind).HasMaxLength(24);
            e.Property(x => x.MetricKey).HasMaxLength(64);
            e.Property(x => x.Unit).HasMaxLength(16);
            e.Property(x => x.SourceRef).HasMaxLength(160);
            e.HasIndex(x => new { x.ClaimRowId, x.EvidenceId }).IsUnique();
        });
    }
}
