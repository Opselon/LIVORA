using System.Reflection;
using Livora.Server.Infrastructure.Engines.Pipeline;

namespace Livora.Server.Tests.Verification;

/// <summary>
/// PURPOSE: falsifiable tests for the evidence model — the lane's core promise that LIVORA never
///          collapses SelfReported / DeviceDerived / ProviderDerived / SystemVerified /
///          HumanReviewed into one generic "verified". These tests would fail loudly if anyone
///          added a boolean "IsVerified" or merged the two axes.
/// OWNER: Agent 10+11.
/// </summary>
public sealed class EvidenceModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    private static EvidenceRecord Rec(string id, string grade, double? value,
        string family, bool source = false, bool aggregate = false, DateTimeOffset? deleted = null)
        => new(id, "claim:meeting-minutes", FactKeys.MeetingMinutes,
            EvidenceGrades.FromSourceLabel(grade), value, family, Now.AddHours(-2),
            IsSourceFact: source, IsAggregate: aggregate, DeletedAtUtc: deleted);

    // ---- the ladder stays a ladder -------------------------------------------

    [Fact]
    public void Grade_ladder_is_strict_and_SelfReported_never_equals_verified()
    {
        Assert.True(EvidenceGrade.SelfReported.Rank() < EvidenceGrade.DeviceDerived.Rank());
        Assert.True(EvidenceGrade.DeviceDerived.Rank() < EvidenceGrade.ProviderDerived.Rank());
        Assert.True(EvidenceGrade.ProviderDerived.Rank() < EvidenceGrade.SystemVerified.Rank());
        Assert.True(EvidenceGrade.SystemVerified.Rank() < EvidenceGrade.HumanReviewed.Rank());
        Assert.Equal("self_reported", EvidenceGrade.SelfReported.Token());
        Assert.False(P1eVerificationEngine.ClearsEntitlementBar(EvidenceGrade.SelfReported));
        Assert.False(P1eVerificationEngine.ClearsEntitlementBar(EvidenceGrade.DeviceDerived));
        Assert.False(P1eVerificationEngine.ClearsEntitlementBar(EvidenceGrade.ProviderDerived));
        Assert.True(P1eVerificationEngine.ClearsEntitlementBar(EvidenceGrade.SystemVerified));
        Assert.True(P1eVerificationEngine.ClearsEntitlementBar(EvidenceGrade.HumanReviewed));
    }

    [Fact]
    public void There_is_no_boolean_verified_anywhere_in_the_pipeline_namespace()
    {
        // The tripwire: if anyone adds `bool Verified` / `IsVerified` to any output type in this
        // lane's namespace, this reflection sweep fails — that is the whole point of the lane.
        var asm = typeof(EvidenceGrade).Assembly;
        var offenders = asm.GetTypes()
            .Where(t => t.Namespace is { } ns && ns.StartsWith(
                "Livora.Server.Infrastructure.Engines.Pipeline", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                              .Select(p => (t, p)))
            .Where(x => x.p.Name is "Verified" or "IsVerified")
            .Select(x => $"{x.t.Name}.{x.p.Name}")
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Assessment_exposes_grade_and_status_as_two_separate_tokens()
    {
        var a = P1eVerificationEngine.Assess(new VerificationRequest(
            "claim:meeting-minutes", null, [Rec("e1", "manual", 60, "phone")]), Now);
        Assert.Equal("self_reported", a.GradeToken);
        Assert.Equal("provisional", a.StatusToken);
        Assert.Equal(EvidenceGrade.SelfReported, a.Grade);
        Assert.Equal(VerificationStatus.Provisional, a.Status);
    }

    // ---- withdrawal is total for decisions ------------------------------------

    [Fact]
    public void Withdrawn_evidence_leaves_only_Unattested_behind()
    {
        var a = P1eVerificationEngine.Assess(new VerificationRequest(
            "claim:meeting-minutes", null,
            [Rec("e1", "device_derived", 60, "watch", deleted: Now.AddHours(-1))]), Now);
        Assert.Equal(VerificationStatus.Withdrawn, a.Status);
        Assert.Equal(EvidenceGrade.Unrated, a.Grade);
        Assert.Empty(a.LiveEvidenceIds);
        Assert.Contains(a.Trail, t => t.RuleKey == "p1e.verify.withdrawal" && t.Verdict == "withdrawn");
    }

    // ---- expiry preserves the grade -------------------------------------------

    [Fact]
    public void Expired_record_keeps_its_grade_and_loses_its_present_tense()
    {
        var old = Rec("e1", "system_verified", 60, "server",
            deleted: null) with { OccurredAtUtc = Now.AddDays(-5) };
        var a = P1eVerificationEngine.Assess(
            new VerificationRequest("claim:meeting-minutes", null, [old]), Now);
        Assert.Equal(VerificationStatus.Expired, a.Status);
        Assert.Equal(P1eVerificationEngine.EvidenceFreshnessDays, 2);
        // the trail line names the kept grade, so history can show HOW strong the claim once was
        Assert.Contains(a.Trail, t => t.RuleKey == "p1e.verify.freshness"
                                      && t.Factors.Contains("grade_kept=system_verified"));
    }

    // ---- recompute mints SystemVerified, nothing else can ----------------------

    [Fact]
    public void Recompute_from_two_independent_device_rows_mints_SystemVerified()
    {
        var rows = new[]
        {
            Rec("e1", "device_derived", 90, "watch", source: true),
            Rec("e2", "device_derived", 30, "phone", source: true),
            Rec("e3", "device_derived", 120, "calendar", aggregate: true),
        };
        var a = P1eVerificationEngine.Assess(
            new VerificationRequest("claim:meeting-minutes", 120, rows), Now);
        Assert.Equal(VerificationStatus.Corroborated, a.Status);
        Assert.Equal(EvidenceGrade.SystemVerified, a.Grade);
        Assert.Contains(a.Trail, t => t.RuleKey == "p1e.verify.recompute" && t.Verdict == "reproduced");
    }

    [Fact]
    public void Self_reported_inputs_cannot_be_recomputed_into_SystemVerified()
    {
        // The grade of the SOURCES gates the recompute: two manual rows do not become proof.
        var rows = new[]
        {
            Rec("e1", "manual", 90, "phone", source: true),
            Rec("e2", "manual", 30, "notes", source: true),
            Rec("e3", "manual", 120, "phone", aggregate: true),   // the numbers add up — but the
            // grade of the SOURCES gates the recompute: manual components cannot mint proof
        };
        var a = P1eVerificationEngine.Assess(
            new VerificationRequest("claim:meeting-minutes", 120, rows), Now);
        Assert.NotEqual(EvidenceGrade.SystemVerified, a.Grade);
        Assert.Equal(VerificationStatus.Provisional, a.Status);
        Assert.Contains(a.Trail, t => t.RuleKey == "p1e.verify.recompute" && t.Verdict == "insufficient_sources");
    }

    [Fact]
    public void A_wrong_assertion_is_recorded_as_mismatch_not_rounded_into_agreement()
    {
        var rows = new[]
        {
            Rec("e1", "device_derived", 90, "watch", source: true),
            Rec("e2", "device_derived", 30, "phone", source: true),
            Rec("e3", "device_derived", 150, "calendar", aggregate: true),   // claims 150, sums 120
        };
        var a = P1eVerificationEngine.Assess(
            new VerificationRequest("claim:meeting-minutes", 150, rows), Now);
        Assert.Contains(a.Trail, t => t.RuleKey == "p1e.verify.recompute" && t.Verdict == "mismatch");
        Assert.NotEqual(VerificationStatus.Corroborated, a.Status);
    }

    [Fact]
    public void Two_same_grade_independent_sources_disagreeing_is_Contradicted_not_averaged()
    {
        var rows = new[]
        {
            Rec("e1", "device_derived", 300, "watch"),
            Rec("e2", "device_derived", 120, "phone"),
        };
        var a = P1eVerificationEngine.Assess(
            new VerificationRequest("claim:meeting-minutes", null, rows), Now);
        Assert.Equal(VerificationStatus.Contradicted, a.Status);
        Assert.Contains(a.Trail, t => t.RuleKey == "p1e.verify.contradiction");
    }

    [Fact]
    public void Stronger_grade_supersedes_rather_than_contradicts()
    {
        var rows = new[]
        {
            Rec("e1", "self_reported", 300, "notes"),
            Rec("e2", "device_derived", 120, "watch"),
        };
        var a = P1eVerificationEngine.Assess(
            new VerificationRequest("claim:meeting-minutes", null, rows), Now);
        Assert.NotEqual(VerificationStatus.Contradicted, a.Status);
        Assert.Equal(EvidenceGrade.DeviceDerived, a.Grade);
    }

    // ---- provider truth ---------------------------------------------------------

    [Fact]
    public void An_unsupported_rule_key_is_refused_loudly_not_forgiven()
    {
        // Unknown verification rules are a 503-class problem (ProblemCodes.
        // VerificationRuleUnknown), never a silent "no rule matched so it is fine".
        var ex = Assert.Throws<VerificationRuleUnknownException>(
            () => P1eVerificationEngine.AssessRule("p1e.verify.nonexistent", null, Now));
        Assert.Contains("p1e.verify.nonexistent", ex.RuleKey);
    }

    [Fact]
    public void A_provider_derived_claim_without_a_provider_receipt_is_unproven_not_verified()
    {
        var prev = P1eProviderReceiptVerifier.GoogleCalendarConfig;
        try
        {
            P1eProviderReceiptVerifier.GoogleCalendarConfig = null;   // no secrets exist (Wave 4 §3)
            var a = P1eVerificationEngine.Assess(new VerificationRequest(
                "claim:meeting-minutes", null,
                [Rec("e1", "provider_derived", 120, "google_calendar")],
                Provider: P1eProviderReceiptVerifier.GoogleCalendarProvider), Now);
            Assert.Equal(VerificationStatus.ProviderUnconfigured, a.Status);
            // the grade the row carries stays provider_derived: unproven is a STATUS, not a
            // downgrade of where the information came from — the two axes never merge
            Assert.Equal(EvidenceGrade.ProviderDerived, a.Grade);
            Assert.DoesNotContain("verified", a.StatusToken);
            Assert.Contains(a.Trail, t =>
                t.RuleKey == "p1e.verify.provider_receipt" && t.Factors.Contains("configured=False"));
        }
        finally { P1eProviderReceiptVerifier.GoogleCalendarConfig = prev; }
    }

    [Fact]
    public void Provider_config_is_an_input_not_an_assumption()
    {
        var prev = P1eProviderReceiptVerifier.GoogleCalendarConfig;
        try
        {
            P1eProviderReceiptVerifier.GoogleCalendarConfig = null;
            var r1 = P1eProviderReceiptVerifier.Evaluate(
                new VerificationRequest("s", null, Array.Empty<EvidenceRecord>(),
                    Provider: P1eProviderReceiptVerifier.GoogleCalendarProvider), Now);
            Assert.NotNull(r1);
            Assert.Equal("unconfigured", r1!.Value.Verdict);
            Assert.False(r1.Value.Configured);

            P1eProviderReceiptVerifier.GoogleCalendarConfig = (true, true);
            var r2 = P1eProviderReceiptVerifier.Evaluate(
                new VerificationRequest("s", null, Array.Empty<EvidenceRecord>(),
                    Provider: P1eProviderReceiptVerifier.GoogleCalendarProvider), Now);
            // configured but no probe has run: still NOT a pass (a "connected" claim needs a probe)
            Assert.Equal("no_probe_result_yet", r2!.Value.Verdict);
            Assert.True(r2.Value.Configured);

            var none = P1eProviderReceiptVerifier.Evaluate(
                new VerificationRequest("s", null, Array.Empty<EvidenceRecord>()), Now);
            Assert.Null(none);   // no provider asked = no receipt consulted
        }
        finally { P1eProviderReceiptVerifier.GoogleCalendarConfig = prev; }
    }

    [Fact]
    public void Self_reported_only_claims_never_produce_a_verified_looking_status()
    {
        var a = P1eVerificationEngine.Assess(new VerificationRequest(
            "claim:weight", null, [Rec("e1", "manual", 78, "phone")]), Now);
        Assert.Equal(VerificationStatus.Provisional, a.Status);
        Assert.DoesNotContain("corroborat", a.StatusToken);
        Assert.DoesNotContain("verified", a.GradeToken);   // self_reported != system_verified, by name
    }

    [Fact]
    public void Absence_of_evidence_is_a_state_of_its_own_never_a_zero_score()
    {
        var a = P1eVerificationEngine.Assess(
            new VerificationRequest("claim:x", null, Array.Empty<EvidenceRecord>()), Now);
        Assert.Equal(VerificationStatus.Unattested, a.Status);
        Assert.Equal("unrated", a.GradeToken);
        Assert.DoesNotContain(a.StatusToken, new[] { "corroborated", "contradicted" });
    }

    [Fact]
    public void Trail_is_deterministic_for_the_same_evidence_in_any_input_order()
    {
        var rows = new[]
        {
            Rec("e2", "device_derived", 30, "phone", source: true),
            Rec("e1", "device_derived", 90, "watch", source: true),
            Rec("e3", "device_derived", 120, "calendar", aggregate: true),
        };
        var a = P1eVerificationEngine.Assess(
            new VerificationRequest("claim:meeting-minutes", 120, rows), Now);
        var b = P1eVerificationEngine.Assess(
            new VerificationRequest("claim:meeting-minutes", 120, rows.Reverse().ToList()), Now);
        Assert.Equal(string.Join("\n", a.Trail.Select(t => t.Render())),
                     string.Join("\n", b.Trail.Select(t => t.Render())));
    }
}
