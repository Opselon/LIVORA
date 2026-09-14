using Livora.Server.Infrastructure.Engines.Decision;
using Livora.Server.Infrastructure.Engines.Decision.Verification;

namespace Livora.Server.Tests.Verification;

/// <summary>
/// PURPOSE: pin the five-rung trust ladder, its refusal paths, and the rule registry — with the
///          product law that matters most: the rungs never collapse into one generic "verified".
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// </summary>
public sealed class VerificationLadderTests
{
    private static readonly DateTime AsOf = new(2026, 3, 16, 18, 0, 0, DateTimeKind.Utc);

    private static EvidenceFact Watch(string id = "e-watch", double value = 8_000,
        DateTime? observedAt = null) => new(id, ClaimSources.Device, "activity.steps",
        value, "steps", "watch:liliow", observedAt ?? AsOf.AddHours(-2));

    [Fact]
    public void The_ladder_has_five_distinct_rungs_and_a_confidence_for_each()
    {
        var rungs = Enum.GetValues<VerificationTrust>();
        Assert.Equal(5, rungs.Length);
        Assert.Equal(rungs.Select(VerificationEngine.TrustName).Distinct().Count(), rungs.Length);
        var confidences = rungs.Select(VerificationEngine.ConfidenceFor).ToList();
        Assert.True(confidences.Zip(confidences.Skip(1), (a, b) => a < b).All(x => x),
            "confidence must climb strictly with the rung");
    }

    [Fact]
    public void A_manual_log_verifies_at_self_reported_and_never_higher()
    {
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c1", ClaimTypes.ManualEntry, ClaimSources.SelfReported, "sleep.minutes", 420,
            "minutes", "manual:ios", AsOf, []));
        Assert.Equal("self_reported", v.TrustLevel);
        Assert.Equal("accepted", v.Status);
        Assert.Equal(0.5, v.Confidence);
    }

    [Fact]
    public void A_fresh_device_claim_sits_on_the_device_rung_not_the_provider_rung()
    {
        var ev = Watch();
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c2", ClaimTypes.HealthMetric, ClaimSources.Device, "activity.steps", 8_000,
            "steps", "watch:today", AsOf, [ev]));
        Assert.Equal("device_derived", v.TrustLevel);
        Assert.Equal("accepted", v.Status);
        Assert.Contains("device_derived.sensor_signal", v.ContributingRuleKeys);
    }

    [Fact]
    public void A_stale_device_signal_cannot_lift_a_claim_at_all()
    {
        var ev = Watch(observedAt: AsOf.AddDays(-5));
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c3", ClaimTypes.HealthMetric, ClaimSources.Device, "activity.steps", 8_000,
            "steps", "watch:last-week", AsOf, [ev]));
        Assert.Equal("device_derived", v.TrustLevel);      // the claim still SAYS device...
        Assert.Equal("rejected", v.Status);                // ...but the freshness check refused it
        Assert.Equal("device_signal_older_than_freshness_window", v.RefusalReason);
    }

    [Fact]
    public void Provider_claims_verify_at_the_provider_rung_and_need_an_account_reference()
    {
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c4", ClaimTypes.ActivityLog, ClaimSources.Provider, "activity.steps", 8_000,
            "steps", "healthconnect:record-77", AsOf, []));
        Assert.Equal("provider_derived", v.TrustLevel);
        Assert.Equal("accepted", v.Status);

        var anonymous = VerificationEngine.Evaluate(new VerificationRequest(
            "c5", ClaimTypes.ActivityLog, ClaimSources.Provider, "activity.steps", 8_000,
            "steps", "", AsOf, []));
        Assert.Equal("insufficient_evidence", anonymous.Status);
    }

    [Fact]
    public void Cross_source_agreement_is_a_real_computation_not_a_label()
    {
        // Claim typed by the user; a fresh independent device reading agrees within tolerance.
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c6", ClaimTypes.HealthMetric, ClaimSources.SelfReported, "activity.steps", 7_900,
            "steps", "manual:android", AsOf, [Watch()]));
        Assert.Equal("system_verified", v.TrustLevel);
        Assert.Equal("accepted", v.Status);
        Assert.Contains("system_verified.cross_source_agreement", v.AttemptedRules.Select(o => o.RuleKey));
    }

    [Fact]
    public void Disagreement_beyond_tolerance_is_reported_not_rounded_off()
    {
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c7", ClaimTypes.HealthMetric, ClaimSources.SelfReported, "activity.steps", 2_000,
            "steps", "manual:android", AsOf, [Watch()]));
        // The self-report rule still accepts the claim AS self-reported (the user did type it),
        // but the cross-source rule records the disagreement — it never lifts the rung.
        Assert.Equal("self_reported", v.TrustLevel);
        Assert.Equal("accepted", v.Status);
        var cross = Assert.Single(v.AttemptedRules,
            o => o.RuleKey == "system_verified.cross_source_agreement");
        Assert.Equal("rejected", cross.Result);
        Assert.Equal("independent_evidence_disagrees_beyond_tolerance", cross.Detail);
    }

    [Fact]
    public void Same_source_evidence_never_counts_as_cross_check()
    {
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c8", ClaimTypes.HealthMetric, ClaimSources.SelfReported, "activity.steps", 8_000,
            "steps", "manual:android", AsOf,
            [new EvidenceFact("e-manual", ClaimSources.SelfReported, "activity.steps", 8_000,
                "steps", "manual:android", AsOf)]));
        Assert.NotEqual("system_verified", v.TrustLevel);   // agreeing with yourself is not verifying
    }

    [Fact]
    public void Implausible_values_are_refused_by_number_not_by_mood()
    {
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c9", ClaimTypes.ManualEntry, ClaimSources.SelfReported, "sleep.minutes", 9_000,
            "minutes", "manual:ios", AsOf, []));
        Assert.Equal("implausible", v.Status);
        Assert.Equal("value_out_of_range", v.RefusalReason);
        Assert.Equal("self_reported", v.TrustLevel);
    }

    [Fact]
    public void Human_review_is_the_top_rung_and_only_for_staff_authentications()
    {
        var byUser = VerificationEngine.Evaluate(new VerificationRequest(
            "c10", ClaimTypes.HealthMetric, ClaimSources.SelfReported, "activity.steps", 8_000,
            "steps", "manual:ios", AsOf, [], ReviewerId: "u-1", CallerIsStaff: false,
            ReviewDecision: "confirm"));
        Assert.NotEqual("human_reviewed", byUser.TrustLevel);   // a plain user cannot bless themselves

        var byStaff = VerificationEngine.Evaluate(new VerificationRequest(
            "c11", ClaimTypes.HealthMetric, ClaimSources.SelfReported, "activity.steps", 8_000,
            "steps", "manual:ios", AsOf, [], ReviewerId: "mod-1", CallerIsStaff: true,
            ReviewDecision: "confirm"));
        Assert.Equal("human_reviewed", byStaff.TrustLevel);
        Assert.Equal("overridden", byStaff.Status);
        Assert.Equal(1.0, byStaff.Confidence);
    }

    [Fact]
    public void A_human_rejection_downgrades_even_blessed_evidence()
    {
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c12", ClaimTypes.HealthMetric, ClaimSources.Device, "activity.steps", 8_000,
            "steps", "watch:today", AsOf, [Watch()], ReviewerId: "mod-1", CallerIsStaff: true,
            ReviewDecision: "reject"));
        Assert.Equal("human_reviewed", v.TrustLevel);
        Assert.Equal("rejected", v.Status);
        Assert.Equal("staff_rejected_after_review", v.RefusalReason);
    }

    [Fact]
    public void An_unknown_rule_key_is_an_error_never_a_quiet_substitute()
    {
        Assert.Throws<VerificationEngine.UnknownVerificationRuleException>(() =>
            VerificationEngine.Evaluate(new VerificationRequest(
                "c13", ClaimTypes.ManualEntry, ClaimSources.SelfReported, "sleep.minutes", 420,
                "minutes", "manual:ios", AsOf, [], RuleKey: "made.up.rule")));
    }

    [Fact]
    public void A_verdict_always_records_every_rule_that_spoke()
    {
        var v = VerificationEngine.Evaluate(new VerificationRequest(
            "c14", ClaimTypes.HealthMetric, ClaimSources.Provider, "activity.steps", 8_000,
            "steps", "garmin:run-9", AsOf, [Watch()]));
        Assert.NotEmpty(v.AttemptedRules);
        Assert.Contains(v.AttemptedRules, o => o.RuleKey == "provider_derived.account_report");
        Assert.Contains(v.AttemptedRules, o => o.RuleKey == "system_verified.cross_source_agreement");
        Assert.True(v.Confidence > 0);
    }

    [Fact]
    public void Default_rule_set_establishes_exactly_one_rung_per_rule()
    {
        var rungs = VerificationEngine.DefaultRules.Select(r => r.Establishes).ToList();
        Assert.Equal(rungs.Count, rungs.Distinct().Count());     // no two rules share a rung
        Assert.Equal(5, rungs.Count);
        Assert.True(rungs.OrderBy(x => (int)x).SequenceEqual(Enum.GetValues<VerificationTrust>()));
    }
}
