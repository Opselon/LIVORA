using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Infrastructure.Engines.Pipeline.Persistence;

/// <summary>
/// PURPOSE: this lane's schema slice, added through the sanctioned seam — the host's DbContext is
///          frozen, so these three tables appear in the model only when a module calls
///          <see cref="Register"/> from its ConfigureServices (the lead decides which module owns
///          that call at integration time; see docs/architecture/wave4/requests/p1e.md).
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// CONSUMES: <see cref="EvidenceRecordRow"/>, <see cref="ExecutionFeedbackRow"/>,
///           <see cref="VerificationAssessmentRow"/>.
/// PROVIDES: table mappings + indexes; a migration-ready contribution (no lane runs dotnet ef).
/// INVARIANTS:
///   - configures ONLY its own types (a collision with a core type fails the model on purpose)
///   - grade/status/outcome stored as STRINGS from the engine's token vocabularies — there is no
///     boolean column that could collapse the evidence ladder into "verified"
///   - (user, idempotency key) unique on feedback: a client retry cannot double-count an estimate
///   - (user, subject, grade, occurred) index on evidence: the assessment read is per-user, bounded
///   - soft delete everywhere; no health text, only numbers + machine keys
/// EXTEND: the lead's Wave4P1Schema migration picks this up from the merged contributions.
/// </summary>
public sealed class EnginesModelContribution : IModelContribution
{
    public const string EvidenceTable = "engine_evidence";
    public const string FeedbackTable = "engine_feedback";
    public const string AssessmentTable = "engine_verifications";

    /// <summary>The entity types this lane contributes — the "migration-ready" line in the ledger.</summary>
    public static IReadOnlyList<Type> EntityTypes { get; } =
        [typeof(EvidenceRecordRow), typeof(ExecutionFeedbackRow), typeof(VerificationAssessmentRow)];

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EvidenceRecordRow>(e =>
        {
            e.ToTable(EvidenceTable);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.SubjectKey).HasMaxLength(160);
            e.Property(x => x.MetricKey).HasMaxLength(64);
            e.Property(x => x.Grade).HasMaxLength(24);
            e.Property(x => x.Unit).HasMaxLength(24);
            e.Property(x => x.SourceFamily).HasMaxLength(48);
            e.Property(x => x.SourceLabel).HasMaxLength(64);
            e.Property(x => x.Value).HasColumnType("REAL");
            e.HasIndex(x => new { x.UserId, x.SubjectKey, x.OccurredAtUtc });
            // Withdrawal must be idempotent per source row: the same evidence id cannot appear twice.
            e.HasIndex(x => new { x.UserId, x.Id }).IsUnique();
        });

        modelBuilder.Entity<ExecutionFeedbackRow>(e =>
        {
            e.ToTable(FeedbackTable);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.ActionKey).HasMaxLength(64);
            e.Property(x => x.Outcome).HasMaxLength(16);
            e.Property(x => x.EvidenceGrade).HasMaxLength(24);
            e.Property(x => x.SourceLabel).HasMaxLength(64);
            e.Property(x => x.IdempotencyKey).HasMaxLength(80);
            e.HasIndex(x => new { x.UserId, x.ActionKey, x.OccurredAtUtc });
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
        });

        modelBuilder.Entity<VerificationAssessmentRow>(e =>
        {
            e.ToTable(AssessmentTable);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.SubjectKey).HasMaxLength(160);
            e.Property(x => x.Grade).HasMaxLength(24);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.TrailJson).HasMaxLength(8000);
            e.HasIndex(x => new { x.UserId, x.SubjectKey, x.AssessedAtUtc });
        });
    }

    /// <summary>Register once, before the model is frozen (contract: from the module's ConfigureServices).</summary>
    public static void Register()
    {
        if (!ModelContributionRegistry.Contributions.Any(c => c is EnginesModelContribution))
            ModelContributionRegistry.Add(new EnginesModelContribution());
    }
}

/// <summary>
/// PURPOSE: the persistence adapter for evidence + feedback. Deliberately thin (a repository here
/// would be the "ceremonial repository" product law forbids): it maps rows to the engine's records,
/// filters soft-deleted evidence, and enforces owner scoping in the query itself.
/// OWNER: Agent 10+11.
/// INVARIANTS:
///   - EVERY read and write takes a userId and filters by it: a handler that forgets the ownership
///     comparison is caught by EnginePersistenceTests (IDOR gate, contract §5b)
///   - deleted rows never reach the engine (withdrawal is total for decisions, preserved for audit)
///   - the feedback insert is idempotent on (user, idempotencyKey): a replay returns the existing row
/// SIGNATURE NOTE: the ctor takes the base <see cref="DbContext"/> (not LivoraDbContext) so this
///   lane can drive it against an isolated model slice in tests — the frozen
///   ModelContributionRegistry is process-global and mut/reset races a parallel suite, so the
///   slice-context route is the only deterministic way to prove these rows. DI still injects the
///   real LivoraDbContext (an implicit upcast).
/// </summary>
public sealed class EngineEvidenceStore(DbContext db)
{
    private readonly DbContext _db = db;

    public async Task<EvidenceRecordRow> AddEvidenceAsync(
        string userId, EvidenceRecordRow row, CancellationToken ct = default)
    {
        EnsureOwner(userId, row.UserId);
        _db.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>Live evidence for one user, newest first within the assessment window.</summary>
    public async Task<IReadOnlyList<EvidenceRecord>> LiveEvidenceAsync(
        string userId, DateTimeOffset? sinceUtc = null, CancellationToken ct = default)
    {
        var q = _db.Set<EvidenceRecordRow>().Where(e => e.UserId == userId && e.DeletedAtUtc == null);
        if (sinceUtc is { } since) q = q.Where(e => e.OccurredAtUtc >= since);
        var rows = await q.OrderByDescending(e => e.OccurredAtUtc)
                          .ThenBy(e => e.Id, System.StringComparer.Ordinal)
                          .ToListAsync(ct);
        return rows.Select(ToRecord).ToList();
    }

    /// <summary>Withdrawal (soft delete). Returns false when the row belongs to someone else —
    /// which is what keeps an authenticated-but-not-authorised call from erasing evidence.</summary>
    public async Task<bool> WithdrawEvidenceAsync(string userId, string evidenceId, DateTimeOffset atUtc,
        CancellationToken ct = default)
    {
        var row = await _db.Set<EvidenceRecordRow>()
            .FirstOrDefaultAsync(e => e.Id == evidenceId && e.UserId == userId, ct);
        if (row is null || row.DeletedAtUtc is not null) return false;
        row.DeletedAtUtc = atUtc;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Append one feedback event; replay of the same (user, idempotencyKey) returns the
    /// original row unchanged (the estimate must not double-count a retried tap).</summary>
    public async Task<ExecutionFeedbackRow> RecordFeedbackAsync(
        string userId, ExecutionFeedbackRow row, CancellationToken ct = default)
    {
        EnsureOwner(userId, row.UserId);
        var existing = await _db.Set<ExecutionFeedbackRow>()
            .FirstOrDefaultAsync(f => f.UserId == userId && f.IdempotencyKey == row.IdempotencyKey, ct);
        if (existing is not null) return existing;
        _db.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<IReadOnlyList<ExecutionFeedbackRow>> FeedbackHistoryAsync(
        string userId, CancellationToken ct = default)
        => await _db.Set<ExecutionFeedbackRow>()
            .Where(f => f.UserId == userId && f.DeletedAtUtc == null)
            .OrderByDescending(f => f.OccurredAtUtc)
            .ThenBy(f => f.Id, System.StringComparer.Ordinal)
            .ToListAsync(ct);

    /// <summary>The user's right to be forgotten by the estimate: soft-delete every feedback row
    /// for one action (or all of them when <paramref name="actionKey"/> is null).</summary>
    public async Task<int> ForgetFeedbackAsync(string userId, string? actionKey, DateTimeOffset atUtc,
        CancellationToken ct = default)
    {
        var rows = await _db.Set<ExecutionFeedbackRow>()
            .Where(f => f.UserId == userId && f.DeletedAtUtc == null
                        && (actionKey == null || f.ActionKey == actionKey))
            .ToListAsync(ct);
        foreach (var r in rows) r.DeletedAtUtc = atUtc;
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public static IReadOnlyList<ExecutionFeedback> ToEngineModel(IEnumerable<ExecutionFeedbackRow> rows)
        => rows
            .Where(r => r.DeletedAtUtc is null)
            .Select(r => new ExecutionFeedback(
                r.Id, r.ActionKey, ParseOutcome(r.Outcome), r.OccurredAtUtc,
                EvidenceGrades.FromSourceLabel(r.EvidenceGrade), r.SourceLabel, r.DeletedAtUtc))
            .ToList();

    public static EvidenceRecord ToRecord(EvidenceRecordRow r) => new(
        r.Id, r.SubjectKey, r.MetricKey,
        EvidenceGrades.FromSourceLabel(r.Grade), r.Value, r.SourceFamily, r.OccurredAtUtc,
        r.IsSourceFact, r.IsAggregate, r.DeletedAtUtc);

    public static string OutcomeToken(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.Accepted => "accepted",
        ExecutionOutcome.Completed => "completed",
        ExecutionOutcome.Skipped => "skipped",
        ExecutionOutcome.TooHard => "too_hard",
        ExecutionOutcome.WrongTime => "wrong_time",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static ExecutionOutcome ParseOutcome(string token) => token switch
    {
        "accepted" => ExecutionOutcome.Accepted,
        "completed" => ExecutionOutcome.Completed,
        "skipped" => ExecutionOutcome.Skipped,
        "too_hard" => ExecutionOutcome.TooHard,
        "wrong_time" => ExecutionOutcome.WrongTime,
        _ => throw new ArgumentException($"unknown outcome token '{token}'", nameof(token)),
    };

    private static void EnsureOwner(string userId, string rowUserId)
    {
        if (!string.Equals(userId, rowUserId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "cross-user write refused: the handler must pass the authenticated owner id");
    }
}
