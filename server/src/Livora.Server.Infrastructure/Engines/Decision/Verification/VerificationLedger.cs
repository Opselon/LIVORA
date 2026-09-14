using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Infrastructure.Engines.Decision.Verification;

// ============================================================================
// PURPOSE: the persisted evidence ledger — claims, their verdicts, and the evidence rows a
//          verdict was computed from. This is the thin adapter side of the verification engine;
//          the ladder math lives in VerificationEngine.cs (pure). A "verified" claim in the DB
//          is only ever a ROW RECORDING which rung was earned — there is no bool column, by design.
// OWNER: Agent 10+11 (lane w4-p1e-engines). Attached to the model by the verification module's
//        IModelContribution (LivoraDbContext is frozen; the lead generates the migration at merge).
// INVARIANTS:
//   - text GUID("N") keys; *AtUtc UTC columns; explicit max lengths; soft delete (evidence rows
//     are audit-relevant)
//   - unique (user, claim id): posting the same claim twice UPSERTS the verdict, it never forks
//   - the trust/status strings come from VerificationEngine.TrustName — one vocabulary, no drift
// ============================================================================

/// <summary>One verification claim + its current verdict (the ledger row the API reads back).</summary>
public sealed class VerificationClaimRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";

    /// <summary>Client-minted stable claim id (unique per user — the idempotency key).</summary>
    public string ClaimId { get; set; } = "";
    public string ClaimType { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string? MetricKey { get; set; }
    public double? ClaimedValue { get; set; }
    public string? Unit { get; set; }
    public string SourceRef { get; set; } = "";
    public DateTimeOffset AsOfUtc { get; set; }

    /// <summary>self_reported | device_derived | provider_derived | system_verified | human_reviewed.</summary>
    public string TrustLevel { get; set; } = "";
    /// <summary>accepted | implausible | insufficient_evidence | rejected | overridden.</summary>
    public string Status { get; set; } = "";
    public double Confidence { get; set; }
    /// <summary>JSON array of {ruleKey,result,detail} for every rule that SPOKE (audit trail).</summary>
    public string AttemptedRulesJson { get; set; } = "[]";
    /// <summary>JSON array of rule keys whose rung the verdict carries.</summary>
    public string ContributingRuleKeysJson { get; set; } = "[]";
    public string? RefusalReason { get; set; }

    /// <summary>Staff review (only written through the Moderator-guarded path).</summary>
    public string? ReviewerId { get; set; }
    public string? ReviewDecision { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Hard delete only via the account-deletion path; evidence is audit-relevant.</summary>
    public DateTimeOffset? DeletedAtUtc { get; set; }

    public List<VerificationEvidenceRecord> Evidence { get; set; } = [];
}

/// <summary>One evidence row the engine cross-checked. Kept as data, never as prose.</summary>
public sealed class VerificationEvidenceRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClaimRowId { get; set; } = "";
    public VerificationClaimRecord? Claim { get; set; }

    /// <summary>Engine-side stable id inside the claim's evidence set (unique per claim row).</summary>
    public string EvidenceId { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string MetricKey { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public DateTimeOffset ObservedAtUtc { get; set; }
}

/// <summary>
/// EF adapter over the shared context (Set&lt;T&gt;() access — the DbContext file is frozen).
/// No ladder logic here: verdicts come from the pure engine; this only round-trips rows.
/// </summary>
public sealed class EfVerificationLedger(DbContext db)
{
    private DbSet<VerificationClaimRecord> Claims => db.Set<VerificationClaimRecord>();
    private DbSet<VerificationEvidenceRecord> Evidence => db.Set<VerificationEvidenceRecord>();

    public Task<VerificationClaimRecord?> FindAsync(string userId, string claimId, CancellationToken ct = default)
        => Claims.Include(c => c.Evidence)
            .SingleOrDefaultAsync(c => c.UserId == userId && c.ClaimId == claimId && c.DeletedAtUtc == null, ct);

    /// <summary>Owner-agnostic lookup for the staff review path — the handler checked the
    /// Moderator policy, and it still compares row.UserId for the non-staff 403 branch.</summary>
    public Task<VerificationClaimRecord?> FindAnyAsync(string claimId, CancellationToken ct = default)
        => Claims.Include(c => c.Evidence)
            .SingleOrDefaultAsync(c => c.ClaimId == claimId && c.DeletedAtUtc == null, ct);

    public async Task<(IReadOnlyList<VerificationClaimRecord> Items, long Total)> ListAsync(
        string userId, int offset, int limit, CancellationToken ct = default)
    {
        var q = Claims.Where(c => c.UserId == userId && c.DeletedAtUtc == null);
        long total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Skip(offset).Take(limit).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>Replace the verdict + evidence set for a claim id (idempotent per user+claimId).</summary>
    public async Task UpsertAsync(VerificationClaimRecord record, CancellationToken ct = default)
    {
        var existing = await Claims.Include(c => c.Evidence)
            .SingleOrDefaultAsync(c => c.UserId == record.UserId && c.ClaimId == record.ClaimId, ct);
        if (existing is null)
        {
            Claims.Add(record);
        }
        else
        {
            Evidence.RemoveRange(existing.Evidence);
            existing.ClaimType = record.ClaimType;
            existing.SourceKind = record.SourceKind;
            existing.MetricKey = record.MetricKey;
            existing.ClaimedValue = record.ClaimedValue;
            existing.Unit = record.Unit;
            existing.SourceRef = record.SourceRef;
            existing.AsOfUtc = record.AsOfUtc;
            existing.TrustLevel = record.TrustLevel;
            existing.Status = record.Status;
            existing.Confidence = record.Confidence;
            existing.AttemptedRulesJson = record.AttemptedRulesJson;
            existing.ContributingRuleKeysJson = record.ContributingRuleKeysJson;
            existing.RefusalReason = record.RefusalReason;
            existing.ReviewerId = record.ReviewerId;
            existing.ReviewDecision = record.ReviewDecision;
            existing.ReviewedAtUtc = record.ReviewedAtUtc;
            existing.UpdatedAtUtc = record.UpdatedAtUtc;
            foreach (var e in record.Evidence) e.ClaimRowId = existing.Id;
            Evidence.AddRange(record.Evidence);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SoftDeleteAsync(VerificationClaimRecord record, CancellationToken ct = default)
    {
        record.DeletedAtUtc = DateTimeOffset.UtcNow;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
