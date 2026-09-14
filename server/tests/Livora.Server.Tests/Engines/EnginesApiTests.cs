using Livora.Server.Application;
using Livora.Server.Infrastructure.Engines.Decision;
using Livora.Server.Modules.Intelligence;
using Livora.Server.Modules.Verification;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: exercise the two engine modules through the REAL host: auth, validation, the budget
///          refusal with its machine code, the deterministic decision end-to-end, the staff/IDOR
///          gates on the verification surface, and the honest capabilities entries.
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// INVARIANTS: no test here fakes a provider; the ledger-backed endpoints are asserted in the
///             documented degraded state (schema gate off by default) — presence as a REAL,
///             computed verdict, persistence asserted in EnginePersistenceTests via SQLite.
/// </summary>
public sealed class EnginesApiTests : LivoraApiTest
{
    public EnginesApiTests(LivoraWebFixture fixture) : base(fixture) { }

    private static DecisionRequest SleepDeprivedBody() => new(
        AsOfUtc: new DateTime(2026, 3, 16, 18, 0, 0, DateTimeKind.Utc),
        Today: new EngineDayDto(new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc),
            SleepMinutes: 330, SleepQuality: 0.45, Steps: 6_800, ActiveMinutes: 25,
            RecoveryScore: 0.60, Stress: 0.55, Mood: 0.5, Energy: 0.4),
        History: Enumerable.Range(1, 14).Select(i => new EngineDayDto(
            new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc).AddDays(-i - 1),
            SleepMinutes: 470, SleepQuality: 0.75, Steps: 9_000, ActiveMinutes: 45,
            RecoveryScore: 0.75, Stress: 0.35, Mood: 0.7, Energy: 0.7)).ToList());

    // ---------------- intelligence ----------------------------------------------------------

    [Fact]
    public async Task Decision_endpoint_is_protected_and_answers_the_shared_envelope()
    {
        var res = await Fixture.Http.PostAsJsonAsync("/api/v1/intelligence/decision", SleepDeprivedBody());
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.Unauthenticated, problem.Code);
    }

    [Fact]
    public async Task Decision_computes_deterministically_and_traces_every_citation()
    {
        var client = Fixture.CreateAuthenticatedClient("u-eng-1");
        var res = await client.PostAsJsonAsync("/api/v1/intelligence/decision", SleepDeprivedBody());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertHasCorrelation(res);

        var body = await res.Content.ReadFromJsonAsync<DecisionResponse>(LivoraWebFixture.Json);
        Assert.NotNull(body);
        Assert.Equal("deterministic_engines", body!.GeneratedBy);
        Assert.StartsWith("dec:", body.DecisionId, StringComparison.Ordinal);
        Assert.Contains(body.RulesFired, r => r.RuleKey == "Rule.SleepDebt");
        Assert.InRange(body.Recommendations.Count, 1, NumericRules.MaxTotalRecommendations);
        Assert.All(body.Recommendations, r => Assert.Equal("action", r.Kind));

        // never-fabricate over HTTP: every cited fact id exists in the response's fact ledger
        var factIds = body.State.Facts.Select(f => f.FactId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(factIds, "fact:sleep-minutes:today");
        foreach (var item in body.PlanItems)
            foreach (var id in item.EvidenceFactIds)
                Assert.Contains(id, factIds);
        // provenance vocabulary present on every fact
        Assert.All(body.State.Facts, f => Assert.Contains(f.Provenance,
            ["observed", "inferred", "user_provided", "assumed"]));
    }

    [Fact]
    public async Task Two_equal_requests_produce_the_same_decision_id()
    {
        var client = Fixture.CreateAuthenticatedClient("u-eng-2");
        var a = await client.PostAsJsonAsync("/api/v1/intelligence/decision", SleepDeprivedBody());
        var b = await client.PostAsJsonAsync("/api/v1/intelligence/decision", SleepDeprivedBody());
        var da = (await a.Content.ReadFromJsonAsync<DecisionResponse>(LivoraWebFixture.Json))!;
        var db = (await b.Content.ReadFromJsonAsync<DecisionResponse>(LivoraWebFixture.Json))!;
        Assert.Equal(da.DecisionId, db.DecisionId);
    }

    [Fact]
    public async Task Context_budget_violation_is_a_machine_code_never_a_silent_truncate()
    {
        var client = Fixture.CreateAuthenticatedClient("u-eng-3");
        var big = SleepDeprivedBody() with
        {
            History = Enumerable.Range(1, 200).Select(i => new EngineDayDto(
                new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc).AddDays(-i))).ToList(),
        };
        var res = await client.PostAsJsonAsync("/api/v1/intelligence/decision", big);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.ContextBudgetExceeded, problem.Code);
        Assert.NotNull(problem.Errors);
        Assert.True(problem.Errors!.ContainsKey("history"));
    }

    [Fact]
    public async Task Impossible_calendar_minutes_are_rejected_per_field()
    {
        var client = Fixture.CreateAuthenticatedClient("u-eng-4");
        var body = SleepDeprivedBody() with
        {
            Calendar = [new CalendarBlockDto("w-1", "workout", "Bad", 1_000, 900)],
        };
        var res = await client.PostAsJsonAsync("/api/v1/intelligence/decision", body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.ValidationFailed, problem.Code);
        Assert.True(problem.Errors!.Keys.Any(k => k.StartsWith("calendar", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task No_data_decision_says_nothing_rather_than_guessing()
    {
        var client = Fixture.CreateAuthenticatedClient("u-eng-5");
        var body = new DecisionRequest(
            AsOfUtc: new DateTime(2026, 3, 16, 18, 0, 0, DateTimeKind.Utc),
            Today: new EngineDayDto(new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc)));
        var res = await client.PostAsJsonAsync("/api/v1/intelligence/decision", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);   // silence is a valid answer, not an error
        var view = await res.Content.ReadFromJsonAsync<DecisionResponse>(LivoraWebFixture.Json);
        Assert.Empty(view!.Recommendations);
        Assert.Contains("insufficient-data", view.Refusals);
    }

    [Fact]
    public async Task Weekly_review_refuses_under_three_days_with_a_machine_reason()
    {
        var client = Fixture.CreateAuthenticatedClient("u-eng-6");
        var body = SleepDeprivedBody() with { History = SleepDeprivedBody().History!.Take(2).ToList() };
        var res = await client.PostAsJsonAsync("/api/v1/intelligence/weekly-review", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var view = await res.Content.ReadFromJsonAsync<WeeklyReviewResponse>(LivoraWebFixture.Json);
        Assert.False(view!.Available);
        Assert.Equal("insufficient_data", view.RefusalReason);
    }

    [Fact]
    public async Task Pattern_scan_reports_insufficient_kinds_instead_of_pretending_absence()
    {
        var client = Fixture.CreateAuthenticatedClient("u-eng-7");
        var res = await client.PostAsJsonAsync("/api/v1/intelligence/pattern-scan", SleepDeprivedBody());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var view = await res.Content.ReadFromJsonAsync<PatternScanResponse>(LivoraWebFixture.Json);
        Assert.NotEmpty(view!.InsufficientKinds);          // 14 nights < 21-day gate => refused, loudly
        Assert.Empty(view.Findings);
    }

    [Fact]
    public async Task Explain_answers_deterministically_and_ai_ask_is_honestly_unavailable()
    {
        var client = Fixture.CreateAuthenticatedClient("u-eng-8");
        var res = await client.PostAsJsonAsync("/api/v1/intelligence/explain", new ExplanationRequest(
            [new ExplanationKeyDto("Rule.Reason.SleepBelowBaseline", [2.3])], "fa"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var view = await res.Content.ReadFromJsonAsync<ExplanationResponseDto>(LivoraWebFixture.Json);
        Assert.Equal("deterministic_template", view!.GeneratedBy);
        Assert.Contains('۲', view.Lines[0].Text);           // Persian digits, not a Latin 2
        Assert.True(view.Lines[0].KeyKnown);

        var ai = await client.PostAsync("/api/v1/intelligence/explain-ai", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ai.StatusCode);
        Assert.Equal(ProblemCodes.AiUnavailable, (await LivoraWebFixture.ReadProblemAsync(ai)).Code);
    }

    [Fact]
    public async Task Rules_catalog_lists_every_key_the_engine_can_emit()
    {
        var client = Fixture.CreateAuthenticatedClient("u-eng-9");
        var doc = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/api/v1/intelligence/rules", LivoraWebFixture.Json);
        var keys = doc.GetProperty("ruleEngine").EnumerateArray().Select(e => e.GetString()).ToList();
        foreach (var key in EngineRuleEvaluator.AllRuleKeys)
            Assert.Contains(key, keys);
    }

    [Fact]
    public async Task Dismissal_endpoints_degrade_truthfully_while_the_schema_gate_is_off()
    {
        // The in-memory fixture host has no gate flag => the ledger endpoints must say so with a
        // 503 machine code, never a fake success and never an unhandled exception.
        var client = Fixture.CreateAuthenticatedClient("u-eng-10");
        var put = await client.PutAsync("/api/v1/intelligence/pattern-dismissals/pat:X:2026-01-01..2026-01-02", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, put.StatusCode);
        Assert.Equal(ProblemCodes.ProviderUnavailable, (await LivoraWebFixture.ReadProblemAsync(put)).Code);
    }

    // ---------------- verification ------------------------------------------------------------

    private static VerifyClaimRequest ManualClaim(string id = "claim-api-1") => new(
        ClaimId: id, ClaimType: "manual_entry", SourceKind: "self_reported",
        MetricKey: "sleep.minutes", ClaimedValue: 420, Unit: "minutes", SourceRef: "manual:ios",
        AsOfUtc: new DateTime(2026, 3, 16, 18, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Claim_verification_computes_the_rung_without_any_persistence()
    {
        var client = Fixture.CreateAuthenticatedClient("u-ver-1");
        var res = await client.PostAsJsonAsync("/api/v1/verification/claims", ManualClaim());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var verdict = await res.Content.ReadFromJsonAsync<VerdictResponse>(LivoraWebFixture.Json);
        Assert.Equal("self_reported", verdict!.TrustLevel);
        Assert.Equal("accepted", verdict.Status);
        Assert.Equal("deterministic_verification_engine", verdict.GeneratedBy);
        Assert.NotEmpty(verdict.AttemptedRules);
        Assert.DoesNotContain("verified", verdict.TrustLevel);   // the rung is never the generic word
    }

    [Fact]
    public async Task Cross_source_agreement_verifies_at_system_verified_over_http()
    {
        var client = Fixture.CreateAuthenticatedClient("u-ver-2");
        var claim = ManualClaim("claim-x-1") with
        {
            ClaimType = "health_metric", SourceKind = "self_reported",
            MetricKey = "activity.steps", ClaimedValue = 7_900, Unit = "steps",
            Evidence = [new EvidenceDto("e-watch", "device", "activity.steps", 8_000, "steps",
                "watch:liliow", new DateTime(2026, 3, 16, 16, 0, 0, DateTimeKind.Utc))],
        };
        var res = await client.PostAsJsonAsync("/api/v1/verification/claims", claim);
        var verdict = await res.Content.ReadFromJsonAsync<VerdictResponse>(LivoraWebFixture.Json);
        Assert.Equal("system_verified", verdict!.TrustLevel);
    }

    [Fact]
    public async Task Unknown_rule_key_answers_the_frozen_problem_code()
    {
        var client = Fixture.CreateAuthenticatedClient("u-ver-3");
        var res = await client.PostAsJsonAsync("/api/v1/verification/claims",
            ManualClaim("claim-unknown-rule") with { RuleKey = "made.up.rule" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal(ProblemCodes.VerificationRuleUnknown,
            (await LivoraWebFixture.ReadProblemAsync(res)).Code);
    }

    [Fact]
    public async Task Bad_claim_fields_are_refused_per_field_before_any_computation()
    {
        var client = Fixture.CreateAuthenticatedClient("u-ver-4");
        var res = await client.PostAsJsonAsync("/api/v1/verification/claims",
            ManualClaim() with { ClaimType = "aura_reading", SourceKind = "vibes" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await LivoraWebFixture.ReadProblemAsync(res);
        Assert.Equal(ProblemCodes.ValidationFailed, problem.Code);
        Assert.True(problem.Errors!.ContainsKey("claimType"));
        Assert.True(problem.Errors.ContainsKey("sourceKind"));
    }

    [Fact]
    public async Task Ledger_reads_degrade_truthfully_while_the_schema_gate_is_off()
    {
        var client = Fixture.CreateAuthenticatedClient("u-ver-5");
        var list = await client.GetAsync("/api/v1/verification/claims");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, list.StatusCode);
        Assert.Equal(ProblemCodes.ProviderUnavailable, (await LivoraWebFixture.ReadProblemAsync(list)).Code);
    }

    [Fact]
    public async Task Review_requires_the_staff_policy_not_a_claim_of_staffhood()
    {
        var plain = Fixture.CreateAuthenticatedClient("u-ver-6");
        var res = await plain.PostAsJsonAsync("/api/v1/verification/claims/x/review",
            new ReviewRequest("confirm"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(ProblemCodes.Forbidden, (await LivoraWebFixture.ReadProblemAsync(res)).Code);
    }

    [Fact]
    public async Task Verification_rules_catalog_publishes_the_ladder_with_one_rung_per_rule()
    {
        var client = Fixture.CreateAuthenticatedClient("u-ver-7");
        var rules = await client.GetFromJsonAsync<List<VerificationRuleView>>(
            "/api/v1/verification/rules", LivoraWebFixture.Json);
        Assert.Equal(5, rules!.Count);
        Assert.Equal(rules.Count, rules.Select(r => r.Establishes).Distinct().Count());
        Assert.Contains("human_reviewed", rules.Select(r => r.Establishes));
    }

    // ---------------- capabilities honesty ------------------------------------------------------

    [Fact]
    public async Task Capabilities_lists_both_engine_modules_with_probe_backed_states()
    {
        var snap = await Fixture.GetAsync<global::Livora.Server.Modules.Platform.CapabilitySnapshot>(
            "/api/v1/platform/capabilities");
        var intel = Assert.Single(snap.Modules, m => m.Key == "intelligence");
        var ver = Assert.Single(snap.Modules, m => m.Key == "verification");
        // Ok is allowed ONLY because each module's Report() actually EXECUTES fixed vectors —
        // and the ai_explanation capability must still read not_configured (no provider exists).
        Assert.Equal(IntelligenceModule.ModuleKey, intel.Key);
        Assert.Equal("not_configured", intel.Capabilities["ai_explanation"]);
        Assert.Equal("deterministic", intel.Capabilities["engine"]);
        Assert.True(intel.State is "ok" or "degraded");
        Assert.Equal("self_reported,device_derived,provider_derived,system_verified,human_reviewed",
            ver.Capabilities["trust_levels"]);
        Assert.True(ver.State is "ok" or "degraded");
    }
}
