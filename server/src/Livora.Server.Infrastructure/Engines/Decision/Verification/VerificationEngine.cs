namespace Livora.Server.Infrastructure.Engines.Decision.Verification;

// ============================================================================
// PURPOSE: the evidence & verification model — pure, deterministic, no EF, no HTTP.
//          Trust is a LADDER of five distinct rungs (VerificationTrust); a verdict always
//          names the EXACT rung it earned plus the rules that tried. There is deliberately
//          NO boolean "IsVerified" anywhere in this design: collapsing five different
//          evidence lineages into one word is the failure mode this lane exists to prevent.
// OWNER: Agent 10+11 (lane w4-p1e-engines).
// ============================================================================

/// <summary>The subject a verification claim is about (stable snake_case strings on the wire).</summary>
public static class ClaimTypes
{
    public const string HealthMetric = "health_metric";       // "I slept 6h", "my watch says 8000 steps"
    public const string ActivityLog = "activity_log";         // a logged workout/session
    public const string HabitCompletion = "habit_completion"; // "I did the habit today"
    public const string ManualEntry = "manual_entry";         // any typed-in value

    public static readonly IReadOnlyList<string> All =
        [HealthMetric, ActivityLog, HabitCompletion, ManualEntry];
}

/// <summary>Where the claim's data physically came from (input side of the ladder).</summary>
public static class ClaimSources
{
    public const string SelfReported = "self_reported";
    public const string Device = "device";
    public const string Provider = "provider";
    public const string SystemProbe = "system_probe";

    public static readonly IReadOnlyList<string> All = [SelfReported, Device, Provider, SystemProbe];
}

/// <summary>One independent observation offered as evidence for a claim.</summary>
public sealed record EvidenceFact(
    string EvidenceId,
    string SourceKind,          // ClaimSources value
    string MetricKey,
    double Value,
    string Unit,
    string SourceRef,           // "garmin:session-123" | "watch:step-count" | "manual:ios" ...
    DateTime ObservedAtUtc);

/// <summary>The request the engine verifies: a claim + the evidence set to check it against.</summary>
public sealed record VerificationRequest(
    string ClaimId,
    string ClaimType,
    string SourceKind,          // ClaimSources: where the CLAIM itself came from
    string? MetricKey,
    double? ClaimedValue,
    string? Unit,
    string SourceRef,
    DateTime AsOfUtc,           // injected now — determinism
    IReadOnlyList<EvidenceFact> Evidence,
    /// <summary>Explicit rule key to apply, or null = auto (apply every rule that fits).</summary>
    string? RuleKey = null,
    /// <summary>Staff reviewer id (only honoured when the caller IS staff; the handler enforces
    /// that with the policy — the engine trusts the handler's authenticated flag below).</summary>
    string? ReviewerId = null,
    bool CallerIsStaff = false,
    string? ReviewDecision = null); // "confirm" | "reject"

/// <summary>Outcome of one rule's attempt to establish a claim.</summary>
public sealed record RuleOutcome(string RuleKey, string Result, string Detail);

/// <summary>The verdict: one rung of the ladder + every rule that tried. Never a bare bool.</summary>
public sealed record VerificationVerdict(
    string ClaimId,
    string ClaimType,
    /// <summary>VerificationTrust name, snake_case ("self_reported" | "device_derived" | ...).</summary>
    string TrustLevel,
    /// <summary>"accepted" | "implausible" | "insufficient_evidence" | "rejected" | "overridden".</summary>
    string Status,
    double Confidence,
    IReadOnlyList<RuleOutcome> AttemptedRules,
    /// <summary>The rule whose rung the verdict actually carries (may be more than one tie).</summary>
    IReadOnlyList<string> ContributingRuleKeys,
    IReadOnlyList<string> EvidenceFactIds,
    /// <summary>Machine reason when the verdict is not accepted ("value_out_of_range", ...).</summary>
    string? RefusalReason,
    DateTime GeneratedAtUtc);

/// <summary>What a verification rule must answer. Rules are pure functions of the request.</summary>
public interface IVerificationRule
{
    /// <summary>Stable key, e.g. "provider_derived.account_report". Unknown key => 503 code.</summary>
    string RuleKey { get; }
    /// <summary>The rung this rule can ESTABLISH (its own lineage — never "generic verified").</summary>
    VerificationTrust Establishes { get; }
    /// <summary>Claim types this rule applies to (empty = all).</summary>
    IReadOnlyList<string> AppliesTo { get; }
    /// <summary>Evaluate: return null when the rule cannot speak to this claim at all.</summary>
    RuleOutcome? Evaluate(VerificationRequest request);
}

/// <summary>
/// PURPOSE: the deterministic trust ladder + verdict engine. The rung a verdict carries is decided
///          by the STRONGEST rule that actually established the claim, and cross-source agreement
///          is a REAL computation (independent evidence within <see cref="AgreementToleranceRatio"/>
///          of the claim raises self/device claims to SystemVerified) — never a label someone set.
/// INVARIANTS:
///   - five rungs stay distinct; the verdict always names the exact rung + every rule attempted
///   - stale evidence (observed older than <see cref="FreshnessHours"/> before AsOf) cannot lift
///     a claim: the freshness check is what makes SystemVerified mean "checked NOW", not "filed once"
///   - implausible values (outside a wide physical range) are REJECTED with a machine reason even
///     at the self-reported rung: honesty includes refusing nonsense, politely and by number
///   - confidence per rung is fixed (ConfidenceFor) so identical claims verify identically
///   - human review outranks everything but requires a staff-authenticated caller (handler-side
///     policy; the engine records CallerIsStaff/ReviewerId from the authenticated principal only)
/// </summary>
public static class VerificationEngine
{
    // ---- named constants (tests + docs read these) ------------------------------------------
    public const double AgreementToleranceRatio = 0.05;   // evidence within 5% of the claim = agreement
    public const double AgreementFloorAbsolute = 25;      // ...or within 25 absolute units (steps!)
    public const int FreshnessHours = 48;                 // evidence older than this cannot lift trust
    public const int MinCrossSourceAgreement = 1;         // one independent agreeing source suffices

    /// <summary>Physical plausibility windows per metric family — wide on purpose: this rejects
    /// 3000-hour sleep logs, not healthy variation. Unit-normalized values (ratio) are 0..1.</summary>
    public static IReadOnlyDictionary<string, (double Min, double Max)> PlausibilityRanges { get; } =
        new Dictionary<string, (double, double)>(StringComparer.Ordinal)
        {
            ["sleep.minutes"] = (0, 1_200),      // 20h/day is the human outlier ceiling
            ["sleep.quality"] = (0, 1),
            ["sleep.consistency"] = (0, 1),
            ["activity.steps"] = (0, 150_000),
            ["activity.minutes"] = (0, 1_440),
            ["recovery.score"] = (0, 1),
            ["wellness.stress"] = (0, 1),
            ["wellness.mood"] = (0, 1),
            ["wellness.energy"] = (0, 1),
            ["screen.minutes"] = (0, 1_440),
            ["calendar.meeting-minutes"] = (0, 1_440),
        };

    public static double ConfidenceFor(VerificationTrust trust) => trust switch
    {
        VerificationTrust.SelfReported => 0.5,
        VerificationTrust.DeviceDerived => 0.75,
        VerificationTrust.ProviderDerived => 0.85,
        VerificationTrust.SystemVerified => 0.95,
        VerificationTrust.HumanReviewed => 1.0,
        _ => 0.0,
    };

    /// <summary>The rules in ladder order (weakest lineage first; auto mode takes the strongest
    /// accepted). The registry (DI) and the endpoint list come from this single source.</summary>
    public static IReadOnlyList<IVerificationRule> DefaultRules { get; } =
        [
            new SelfReportedPlausibilityRule(),
            new DeviceDerivedSignalRule(),
            new ProviderDerivedAccountRule(),
            new SystemVerifiedCrossSourceRule(),
            new HumanReviewedOverrideRule(),
        ];

    public static VerificationVerdict Evaluate(
        VerificationRequest request, IReadOnlyList<IVerificationRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var applicable = (rules ?? DefaultRules).ToList();

        // Explicit rule selection: unknown key is an honest error, not a silent fallback.
        if (request.RuleKey is { Length: > 0 } wanted)
        {
            var rule = applicable.FirstOrDefault(r => string.Equals(r.RuleKey, wanted, StringComparison.Ordinal));
            if (rule is null)
                throw new UnknownVerificationRuleException(wanted);
            applicable = [rule];
        }

        var outcomes = new List<RuleOutcome>();
        var accepted = new List<(IVerificationRule Rule, RuleOutcome Outcome)>();
        foreach (var rule in applicable)
        {
            if (rule.AppliesTo.Count > 0 && !rule.AppliesTo.Contains(request.ClaimType))
            {
                outcomes.Add(new RuleOutcome(rule.RuleKey, "not_applicable", "claim_type_outside_rule_scope"));
                continue;
            }
            var outcome = rule.Evaluate(request);
            if (outcome is null) continue;                    // rule declined to speak at all
            outcomes.Add(outcome);
            if (outcome.Result is "accepted" or "confirmed")
                accepted.Add((rule, outcome));
        }

        var evidenceIds = request.Evidence.Select(e => e.EvidenceId).ToList();
        var humanRule = applicable.FirstOrDefault(r => r.Establishes == VerificationTrust.HumanReviewed);
        bool humanRejected = humanRule is not null
                             && outcomes.Any(o => o.RuleKey == humanRule.RuleKey && o.Result == "rejected");

        // A staff rejection DOWNGRADES at the human rung even when automated rules accepted —
        // the human rung must be able to say no, not only bless (product law: review is a ruling).
        if (humanRejected)
            return new VerificationVerdict(request.ClaimId, request.ClaimType,
                TrustName(VerificationTrust.HumanReviewed), "rejected",
                ConfidenceFor(VerificationTrust.HumanReviewed), outcomes, [humanRule!.RuleKey],
                evidenceIds, "staff_rejected_after_review", request.AsOfUtc);

        // The strongest accepted rung carries the verdict; weaker accepted rules are recorded as
        // contributing evidence, never averaged away.
        var best = accepted.OrderByDescending(a => (int)a.Rule.Establishes)
                          .ThenBy(a => a.Rule.RuleKey, StringComparer.Ordinal)
                          .FirstOrDefault();

        // The base lineage of the claim itself decides the floor when no rule lifts it.
        var floor = SourceFloor(request.SourceKind);

        if (best.Rule is not null)
        {
            var trust = best.Rule.Establishes;
            // An implausibility found anywhere in the attempt set cannot be ignored: a value is
            // implausible even next to agreeing evidence (only a human ruling can overrule physics).
            var implausible = outcomes.FirstOrDefault(o => o.Result == "implausible");
            if (implausible is not null)
                return new VerificationVerdict(request.ClaimId, request.ClaimType, TrustName(trust),
                    "implausible", ConfidenceFor(trust) * 0.5, outcomes, [implausible.RuleKey],
                    evidenceIds, "value_out_of_range", request.AsOfUtc);

            return new VerificationVerdict(
                request.ClaimId, request.ClaimType, TrustName(trust),
                best.Outcome.Result == "confirmed" ? "overridden" : "accepted",
                ConfidenceFor(trust), outcomes,
                accepted.Where(a => (int)a.Rule.Establishes == (int)trust).Select(a => a.Rule.RuleKey).ToList(),
                evidenceIds, RefusalReason: null, request.AsOfUtc);
        }

        var worst = outcomes.FirstOrDefault(o => o.Result is "implausible")
                    ?? outcomes.FirstOrDefault(o => o.Result == "rejected")
                    ?? outcomes.FirstOrDefault(o => o.Result == "insufficient");

        if (worst is not null)
        {
            var status = worst.Result switch
            {
                "implausible" => "implausible",
                "rejected" => "rejected",
                _ => "insufficient_evidence",
            };
            return new VerificationVerdict(request.ClaimId, request.ClaimType, TrustName(floor),
                status, ConfidenceFor(floor) * 0.5, outcomes, Array.Empty<string>(),
                evidenceIds, worst.Detail, request.AsOfUtc);
        }

        // Nothing established it and nothing actively refused: the claim keeps its lineage floor,
        // status honestly unverified.
        return new VerificationVerdict(request.ClaimId, request.ClaimType, TrustName(floor),
            "insufficient_evidence", ConfidenceFor(floor), outcomes, Array.Empty<string>(),
            evidenceIds, "no_rule_established_claim", request.AsOfUtc);
    }

    public static VerificationTrust SourceFloor(string sourceKind) => sourceKind switch
    {
        ClaimSources.Device => VerificationTrust.DeviceDerived,
        ClaimSources.Provider => VerificationTrust.ProviderDerived,
        ClaimSources.SystemProbe => VerificationTrust.SystemVerified,
        _ => VerificationTrust.SelfReported,
    };

    public static string TrustName(VerificationTrust trust) => trust switch
    {
        VerificationTrust.SelfReported => "self_reported",
        VerificationTrust.DeviceDerived => "device_derived",
        VerificationTrust.ProviderDerived => "provider_derived",
        VerificationTrust.SystemVerified => "system_verified",
        VerificationTrust.HumanReviewed => "human_reviewed",
        _ => "unknown",
    };

    // ===================== the five rules (one per distinct rung) =====================

    /// <summary>Rung SelfReported: a typed-in report is true-as-told within physical possibility.</summary>
    public sealed class SelfReportedPlausibilityRule : IVerificationRule
    {
        public string RuleKey => "self_reported.manual_log";
        public VerificationTrust Establishes => VerificationTrust.SelfReported;
        public IReadOnlyList<string> AppliesTo => Array.Empty<string>();

        public RuleOutcome? Evaluate(VerificationRequest r)
        {
            if (r.SourceKind != ClaimSources.SelfReported) return null;
            if (r.MetricKey is null || r.ClaimedValue is null)
                return new RuleOutcome(RuleKey, "insufficient", "no_metric_or_value_on_claim");
            if (!PlausibilityRanges.TryGetValue(r.MetricKey, out var range))
                return new RuleOutcome(RuleKey, "accepted", "plausible_unbounded_metric");
            var v = r.ClaimedValue.Value;
            return v >= range.Min && v <= range.Max
                ? new RuleOutcome(RuleKey, "accepted", "value_within_plausible_range")
                : new RuleOutcome(RuleKey, "implausible", "value_out_of_range");
        }
    }

    /// <summary>Rung DeviceDerived: a reading carried by a device the user wears/carries, fresh
    /// enough to be today's body, with a named device source.</summary>
    public sealed class DeviceDerivedSignalRule : IVerificationRule
    {
        public string RuleKey => "device_derived.sensor_signal";
        public VerificationTrust Establishes => VerificationTrust.DeviceDerived;
        public IReadOnlyList<string> AppliesTo => [ClaimTypes.HealthMetric, ClaimTypes.ActivityLog];

        public RuleOutcome? Evaluate(VerificationRequest r)
        {
            if (r.SourceKind != ClaimSources.Device) return null;
            if (string.IsNullOrWhiteSpace(r.SourceRef))
                return new RuleOutcome(RuleKey, "insufficient", "device_claim_without_source_ref");
            bool fresh = (r.AsOfUtc - ObservedOf(r)).TotalHours <= FreshnessHours;
            return fresh
                ? new RuleOutcome(RuleKey, "accepted", "fresh_device_signal")
                : new RuleOutcome(RuleKey, "rejected", "device_signal_older_than_freshness_window");
        }
    }

    /// <summary>Rung ProviderDerived: an external provider account reported it. Needs a provider
    /// name in SourceRef; absence is not a failure of the user, it is absence of the rung.</summary>
    public sealed class ProviderDerivedAccountRule : IVerificationRule
    {
        public string RuleKey => "provider_derived.account_report";
        public VerificationTrust Establishes => VerificationTrust.ProviderDerived;
        public IReadOnlyList<string> AppliesTo => [ClaimTypes.HealthMetric, ClaimTypes.ActivityLog];

        public RuleOutcome? Evaluate(VerificationRequest r)
        {
            if (r.SourceKind != ClaimSources.Provider) return null;
            return string.IsNullOrWhiteSpace(r.SourceRef) || !r.SourceRef.Contains(':')
                ? new RuleOutcome(RuleKey, "insufficient", "provider_claim_without_account_reference")
                : new RuleOutcome(RuleKey, "accepted", "provider_account_report_recorded");
        }
    }

    /// <summary>Rung SystemVerified: the system CHECKED — at least one independent evidence row
    /// (different source lineage from the claim) whose value agrees within tolerance and is fresh.
    /// This is a computation over the ledger, never a flag a client can set.</summary>
    public sealed class SystemVerifiedCrossSourceRule : IVerificationRule
    {
        public string RuleKey => "system_verified.cross_source_agreement";
        public VerificationTrust Establishes => VerificationTrust.SystemVerified;
        public IReadOnlyList<string> AppliesTo => Array.Empty<string>();

        public RuleOutcome? Evaluate(VerificationRequest r)
        {
            if (r.MetricKey is null || r.ClaimedValue is null) return null; // nothing to cross-check
            var agreeing = r.Evidence.Where(e =>
                    string.Equals(e.MetricKey, r.MetricKey, StringComparison.Ordinal)
                    && e.SourceKind != r.SourceKind                       // independent lineage only
                    && (r.AsOfUtc - e.ObservedAtUtc).TotalHours <= FreshnessHours
                    && Agrees(r.ClaimedValue.Value, e.Value))
                .ToList();

            if (agreeing.Count >= MinCrossSourceAgreement)
                return new RuleOutcome(RuleKey, "accepted", $"{agreeing.Count} agreeing independent sources");
            bool anyFreshIndependent = r.Evidence.Any(e =>
                string.Equals(e.MetricKey, r.MetricKey, StringComparison.Ordinal)
                && e.SourceKind != r.SourceKind);
            return anyFreshIndependent
                ? new RuleOutcome(RuleKey, "rejected", "independent_evidence_disagrees_beyond_tolerance")
                : new RuleOutcome(RuleKey, "insufficient", "no_independent_evidence_for_metric");
        }
    }

    /// <summary>Rung HumanReviewed: a staff human ruled on the evidence. The handler authenticated
    /// the reviewer (policy Moderator); without that flag the rule cannot speak. A reject decision
    /// makes the verdict "rejected" AT the human rung — a human can downgrade, not just bless.</summary>
    public sealed class HumanReviewedOverrideRule : IVerificationRule
    {
        public string RuleKey => "human_reviewed.staff_decision";
        public VerificationTrust Establishes => VerificationTrust.HumanReviewed;
        public IReadOnlyList<string> AppliesTo => Array.Empty<string>();

        public RuleOutcome? Evaluate(VerificationRequest r)
        {
            if (!r.CallerIsStaff || r.ReviewerId is not { Length: > 0 } || r.ReviewDecision is null)
                return null;
            return r.ReviewDecision switch
            {
                "confirm" => new RuleOutcome(RuleKey, "confirmed", "staff_confirmed_after_review"),
                "reject" => new RuleOutcome(RuleKey, "rejected", "staff_rejected_after_review"),
                _ => new RuleOutcome(RuleKey, "insufficient", "unknown_review_decision"),
            };
        }
    }

    internal static DateTime ObservedOf(VerificationRequest r) =>
        r.Evidence.Count > 0 ? r.Evidence.Max(e => e.ObservedAtUtc) : r.AsOfUtc;

    internal static bool Agrees(double claim, double evidence) =>
        Math.Abs(claim - evidence) <= Math.Max(AgreementFloorAbsolute, Math.Abs(claim) * AgreementToleranceRatio);

    /// <summary>Thrown on an explicit unknown rule key; the HTTP layer converts it to the
    /// 503 problem code verification_rule_unknown (ApiProblem.cs constant — no local literals).</summary>
    public sealed class UnknownVerificationRuleException(string ruleKey) : Exception(
        $"No verification rule is registered under key '{ruleKey}'.");
}
