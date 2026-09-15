using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Engines.Decision.Verification;

namespace Livora.Server.Modules.Verification;

/// <summary>
/// PURPOSE: wire shapes for the evidence/verification surface. There is deliberately NO boolean
///          "verified" field: every answer names the exact trust rung (self_reported |
///          device_derived | provider_derived | system_verified | human_reviewed) plus every rule
///          that spoke. Collapsing the ladder into a bool is the failure mode this module exists
///          to prevent, so the DTO cannot express it.
/// OWNER: Agent 10+11 (lane w4-p1e-engines). camelCase on the wire (shared serializer default).
/// </summary>
public sealed record EvidenceDto(
    string EvidenceId, string SourceKind, string MetricKey, double Value, string Unit,
    string SourceRef, DateTime ObservedAtUtc);

public sealed record VerifyClaimRequest(
    /// <summary>Client-minted stable id: re-posting the same claimId UPSERTS, never forks a verdict.</summary>
    string ClaimId,
    string ClaimType,          // health_metric | activity_log | habit_completion | manual_entry
    string SourceKind,         // self_reported | device | provider | system_probe
    string? MetricKey = null,
    double? ClaimedValue = null,
    string? Unit = null,
    string SourceRef = "",
    /// <summary>Injected now — the verdict must be reproducible; the engine never reads a wall clock.</summary>
    DateTime? AsOfUtc = null,
    IReadOnlyList<EvidenceDto>? Evidence = null,
    /// <summary>Optional explicit rule; unknown key => 503 verification_rule_unknown (never silent).</summary>
    string? RuleKey = null);

public sealed record RuleOutcomeDto(string RuleKey, string StatusToken, string Detail);

public sealed record VerdictResponse(
    string ClaimId, string ClaimType, string TrustLevel, string Status, double Confidence,
    IReadOnlyList<RuleOutcomeDto> AttemptedRules, IReadOnlyList<string> ContributingRuleKeys,
    IReadOnlyList<string> EvidenceFactIds, string? RefusalReason, DateTime GeneratedAtUtc,
    string GeneratedBy);

public sealed record ReviewRequest(string Decision, string? Comment = null);

/// <summary>Validation + mapping at the seam (the ladder math lives in the pure engine).</summary>
public static class VerificationBoundary
{
    public const int MaxEvidencePerClaim = 20;

    public static IReadOnlyDictionary<string, string[]>? ValidationErrors(VerifyClaimRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.ClaimId) || r.ClaimId.Length > 80)
            errors["claimId"] = ["claimId is required (max 80 chars)"];
        if (!ClaimTypes.All.Contains(r.ClaimType))
            errors["claimType"] = [$"claimType must be one of: {string.Join(", ", ClaimTypes.All)}"];
        if (!ClaimSources.All.Contains(r.SourceKind))
            errors["sourceKind"] = [$"sourceKind must be one of: {string.Join(", ", ClaimSources.All)}"];
        if (r.Evidence?.Count > MaxEvidencePerClaim)
            errors["evidence"] = [$"at most {MaxEvidencePerClaim} evidence rows per claim"];
        if (r.Evidence is { } ev)
        {
            foreach (var bad in ev.Where(e => string.IsNullOrWhiteSpace(e.EvidenceId)
                                              || string.IsNullOrWhiteSpace(e.SourceKind)
                                              || string.IsNullOrWhiteSpace(e.MetricKey)))
                errors[$"evidence.{bad.EvidenceId}"] = ["evidence requires evidenceId, sourceKind and metricKey"];
        }
        return errors.Count == 0 ? null : errors;
    }

    public static VerificationRequest ToEngine(this VerifyClaimRequest r, DateTime asOf,
        bool callerIsStaff, string? reviewerId, string? reviewDecision) => new(
        ClaimId: r.ClaimId.Trim(),
        ClaimType: r.ClaimType,
        SourceKind: r.SourceKind,
        MetricKey: r.MetricKey,
        ClaimedValue: r.ClaimedValue,
        Unit: r.Unit,
        SourceRef: r.SourceRef ?? "",
        AsOfUtc: r.AsOfUtc ?? asOf,
        Evidence: (r.Evidence ?? []).Select(e => new EvidenceFact(
            e.EvidenceId, e.SourceKind, e.MetricKey, e.Value, e.Unit, e.SourceRef, e.ObservedAtUtc)).ToList(),
        RuleKey: r.RuleKey,
        ReviewerId: reviewerId,
        CallerIsStaff: callerIsStaff,
        ReviewDecision: reviewDecision);

    public static VerdictResponse ToResponse(this VerificationVerdict v) => new(
        v.ClaimId, v.ClaimType, v.TrustLevel, v.Status, v.Confidence,
        v.AttemptedRules.Select(o => new RuleOutcomeDto(o.RuleKey, o.StatusToken, o.Detail)).ToList(),
        v.ContributingRuleKeys, v.EvidenceFactIds, v.RefusalReason, v.GeneratedAtUtc,
        GeneratedBy: "deterministic_verification_engine");
}
