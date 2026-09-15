using Livora.Server.Infrastructure.Persistence;

namespace Livora.Server.Infrastructure.Engines.Pipeline.Persistence;

/// <summary>
/// PURPOSE: the persisted side of the evidence model — one row per piece of evidence, one row per
///          feedback event, one row per stored assessment. The ENGINE is pure (Engines/Pipeline);
///          these are the rows it reads and writes. Deliberately two separate tables rather than a
///          generic "verification" blob, because collapsing grade and status into one column is the
///          exact failure this lane exists to prevent: the ladder must remain queryable.
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// CONSUMES: nothing (entity declarations only).
/// PROVIDES: entity types for <c>EnginesModelContribution</c>; the lead generates the migration.
/// INVARIANTS (contract §4 conventions):
///   - text GUID "N" keys, UTC *AtUtc columns, explicit HasMaxLength, soft delete on evidence and
///     feedback rows (account deletion is the only hard delete)
///   - NO raw health value is stored as prose: <see cref="Value"/> is the number the claim was
///     made about, in the declared unit, and <see cref="SubjectKey"/> is a machine key, not text
///   - grade and status are stored as STRINGS with distinct vocabularies (self_reported …
///     human_reviewed / unattested … provider_unconfigured) so a report can never join them into
///     a single "verified" flag by accident
///   - an idempotency unique index backs feedback replay (a retried client tap must not double-count
///     into the execution-likelihood estimate)
/// EXTEND: this file is the lane's only entity list; the contribution maps them.
/// </summary>
public sealed class EvidenceRecordRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    /// <summary>Machine key of the thing being attested ("sleep.minutes", "claim:workout-45m").</summary>
    public string SubjectKey { get; set; } = "";
    /// <summary>Which metric the value belongs to; "" for non-numeric attestations.</summary>
    public string MetricKey { get; set; } = "";
    /// <summary>EvidenceGrade token — one of self_reported|device_derived|provider_derived|system_verified|human_reviewed.</summary>
    public string Grade { get; set; } = "";
    /// <summary>The observed value, null when the record attests something not numeric.</summary>
    public double? Value { get; set; }
    /// <summary>Machine unit token ("minutes","steps"). Never display text.</summary>
    public string Unit { get; set; } = "";
    /// <summary>Independence family: two rows sharing it are ONE source wearing two hats.</summary>
    public string SourceFamily { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    /// <summary>True when this row feeds a recompute aggregation; false when it IS the assertion.</summary>
    public bool IsSourceFact { get; set; }
    public bool IsAggregate { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Withdrawal: excluded from every assessment, kept for audit until account deletion.</summary>
    public DateTimeOffset? DeletedAtUtc { get; set; }
}

/// <summary>
/// One execution-feedback event (accepted / completed / skipped / too_hard / wrong_time). The
/// row is an ESTIMATE INPUT, not a character record: it is revisable (a newer row simply is
/// newer), explainable (every derived estimate names the rows it read), and deletable
/// (<see cref="DeletedAtUtc"/> removes it from the model with no other effect).
/// <see cref="IdempotencyKey"/> makes a client retry a no-op.
/// </summary>
public sealed class ExecutionFeedbackRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string ActionKey { get; set; } = "";
    /// <summary>accepted|completed|skipped|too_hard|wrong_time.</summary>
    public string Outcome { get; set; } = "";
    /// <summary>How we know the outcome (a tap is self_reported; a watch-detected walk is device_derived).</summary>
    public string EvidenceGrade { get; set; } = "";
    public string? SourceLabel { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string IdempotencyKey { get; set; } = "";
    public DateTimeOffset? DeletedAtUtc { get; set; }
}

/// <summary>
/// A stored assessment: the two-axis verdict plus the subject it was about. Nothing in the schema
/// can express "verified = true" — the columns are a grade, a status, and an expiry; a consumer
/// that wants a boolean has to write the rule itself, in the open.
/// </summary>
public sealed class VerificationAssessmentRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string SubjectKey { get; set; } = "";
    public string Grade { get; set; } = "";
    public string Status { get; set; } = "";
    /// <summary>Machine rule trail (ids + rule keys + numeric factors). No free prose, no health text.</summary>
    public string TrailJson { get; set; } = "[]";
    public DateTimeOffset AssessedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAtUtc { get; set; }
}
