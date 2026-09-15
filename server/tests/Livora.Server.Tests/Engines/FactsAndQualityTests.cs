using Livora.Server.Infrastructure.Engines.Pipeline;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: falsifiable tests for stage 1 (facts) + stage 2 (data quality) — the boundary where
///          trust enters the pipeline. Every test names the exact rule it pins, so changing a
///          threshold or the grade ladder without editing a test is a red build.
/// OWNER: Agent 10+11.
/// </summary>
public sealed class FactsAndQualityTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    // ---- stage 1: the grade ladder must never collapse ------------------------

    [Fact]
    public void Evidence_grades_are_a_strict_total_order_and_never_a_boolean()
    {
        Assert.Equal(
            new[]
            {
                EvidenceGrade.SelfReported, EvidenceGrade.DeviceDerived, EvidenceGrade.ProviderDerived,
                EvidenceGrade.SystemVerified, EvidenceGrade.HumanReviewed,
            },
            EvidenceGrades.Ordered);
        for (int i = 1; i < EvidenceGrades.Ordered.Count; i++)
            Assert.True(EvidenceGrades.Ordered[i].Rank() > EvidenceGrades.Ordered[i - 1].Rank(),
                $"{EvidenceGrades.Ordered[i]} must outrank {EvidenceGrades.Ordered[i - 1]}");
    }

    [Theory]
    [InlineData("manual", EvidenceGrade.SelfReported)]
    [InlineData("HealthConnect", EvidenceGrade.DeviceDerived)]
    [InlineData("google_calendar", EvidenceGrade.ProviderDerived)]
    [InlineData("recomputed", EvidenceGrade.SystemVerified)]
    [InlineData("staff_review", EvidenceGrade.HumanReviewed)]
    [InlineData("whatever-someone-typed", EvidenceGrade.Unrated)]
    [InlineData(null, EvidenceGrade.Unrated)]
    public void Source_label_mapping_never_guesses_a_grade(string? label, EvidenceGrade expected)
        => Assert.Equal(expected, EvidenceGrades.FromSourceLabel(label));

    [Fact]
    public void Ceiling_over_a_mixed_set_is_the_strongest_grade_not_a_merge()
    {
        var ceiling = EvidenceGrades.Ceiling(
            [EvidenceGrade.SelfReported, EvidenceGrade.SystemVerified, EvidenceGrade.DeviceDerived]);
        Assert.Equal(EvidenceGrade.SystemVerified, ceiling);
        Assert.Equal(EvidenceGrade.Unrated, EvidenceGrades.Ceiling(Array.Empty<EvidenceGrade>()));
    }

    [Fact]
    public void Self_reported_never_outranks_provider_derived_even_when_fresher()
    {
        var self = Fact.Of(FactKeys.Steps, 12_000, "steps", EvidenceGrade.SelfReported, Now);
        var provider = Fact.Of(FactKeys.Steps, 9_000, "steps", EvidenceGrade.ProviderDerived, Now.AddHours(-3));
        var set = new FactSet([self, provider], Now);
        Assert.Equal(EvidenceGrade.ProviderDerived, set.Find(FactKeys.Steps)!.Grade);
    }

    [Fact]
    public void FactSet_dedupes_by_grade_then_freshness_and_keeps_raw_rows_visible()
    {
        var older = Fact.Of(FactKeys.SleepMinutes, 400, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-2));
        var newer = Fact.Of(FactKeys.SleepMinutes, 390, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-1));
        var set = new FactSet([older, newer], Now);
        Assert.Single(set.Items);
        Assert.Equal(390, set.Find(FactKeys.SleepMinutes)!.Value);
        Assert.Equal(2, set.Raw.Count);   // nothing is silently dropped: the conflict pass needs it
    }

    // ---- stage 2: plausibility / freshness / presence ------------------------

    private static QualityReport Assess(params Fact[] facts)
        => DataQualityStage.Assess(new FactSet(facts, Now), DataQualityStage.ExpectedKeysFor(DecisionProfile.DailyPlan));

    [Fact]
    public void Impossible_value_is_refused_and_named_not_zero_filled()
    {
        var report = Assess(
            Fact.Of(FactKeys.SleepMinutes, 5000, "minutes", EvidenceGrade.DeviceDerived, Now),  // 83 h
            Fact.Of(FactKeys.MeetingMinutes, 60, "minutes", EvidenceGrade.ProviderDerived, Now),
            Fact.Of(FactKeys.ScreenMinutes, 200, "minutes", EvidenceGrade.DeviceDerived, Now),
            Fact.Of(FactKeys.Steps, 8000, "steps", EvidenceGrade.DeviceDerived, Now),
            Fact.Of(FactKeys.RecoveryScore, 0.5, "fraction", EvidenceGrade.DeviceDerived, Now));
        Assert.True(report.Usable.ContainsKey(FactKeys.MeetingMinutes));
        Assert.False(report.Usable.ContainsKey(FactKeys.SleepMinutes));
        Assert.Contains(report.Trail, t => t.RuleKey == "p1e.quality.rejected_plausible_range" && t.Verdict == "demoted");
    }

    [Fact]
    public void Recovery_older_than_its_window_is_stale_but_sleep_may_be_younger_than_a_day()
    {
        var recovery = Fact.Of(FactKeys.RecoveryScore, 0.9, "fraction", EvidenceGrade.DeviceDerived,
            Now.AddHours(-13));                        // > FreshWindowHoursRecovery (12)
        var report = Assess(recovery);
        Assert.False(report.Usable.ContainsKey(FactKeys.RecoveryScore));
        Assert.Contains(report.Trail, t => t.RuleKey == "p1e.quality.stale");
    }

    [Fact]
    public void Clock_skew_cannot_inflate_trust()
    {
        var future = Fact.Of(FactKeys.Steps, 9000, "steps", EvidenceGrade.DeviceDerived, Now.AddHours(5));
        var report = Assess(future);
        Assert.True(report.Usable.ContainsKey(FactKeys.Steps));   // treated as age 0, never negative
        var line = report.Trail.Single(t => t.Factors.Any(f => f.StartsWith("age_h=")));
        Assert.Contains("age_h=0", line.Factors);
    }

    [Fact]
    public void Completeness_below_the_gate_stops_the_pipeline_from_deciding()
    {
        var report = Assess(Fact.Of(FactKeys.Steps, 500, "steps", EvidenceGrade.DeviceDerived, Now));
        Assert.Equal(0.2, Math.Round(report.Completeness, 2));   // 1 of 5 expected keys
        Assert.False(report.CanDecide);
        Assert.Equal(DataQualityStage.MinCompletenessToDecide, 0.5);
    }

    [Fact]
    public void Two_sources_disagreeing_beyond_tolerance_is_recorded_not_averaged()
    {
        var self = Fact.Of(FactKeys.Steps, 4000, "steps", EvidenceGrade.SelfReported, Now,
            "self", "ev-self");
        var device = Fact.Of(FactKeys.Steps, 9000, "steps", EvidenceGrade.SelfReported, Now,
            "watch", "ev-device");     // same grade on purpose: neither supersedes the other
        var report = Assess(self, device,
            Fact.Of(FactKeys.SleepMinutes, 420, "minutes", EvidenceGrade.DeviceDerived, Now),
            Fact.Of(FactKeys.MeetingMinutes, 60, "minutes", EvidenceGrade.ProviderDerived, Now),
            Fact.Of(FactKeys.ScreenMinutes, 200, "minutes", EvidenceGrade.DeviceDerived, Now),
            Fact.Of(FactKeys.RecoveryScore, 0.6, "fraction", EvidenceGrade.DeviceDerived, Now));
        Assert.Single(report.Conflicts);
        Assert.StartsWith("activity.steps:", report.Conflicts[0]);
        Assert.Contains(report.Trail, t => t.RuleKey == "p1e.quality.conflict");
    }

    [Fact]
    public void Absent_expected_keys_are_stated_in_the_trail()
    {
        var report = Assess();
        var absent = report.Trail.Count(t => t.RuleKey == "p1e.quality.absent");
        Assert.Equal(5, absent);   // the whole DailyPlan expected set
        Assert.Equal(0, report.Completeness);
    }

    [Fact]
    public void Trail_entries_render_deterministically_and_carry_evidence_ids()
    {
        var fact = Fact.Of(FactKeys.Steps, 9000, "steps", EvidenceGrade.DeviceDerived, Now, "watch", "ev-1");
        var report = Assess(fact);
        var line = report.Trail.First(t => t.RuleKey == "p1e.quality.ok");
        Assert.Equal(["ev-1"], line.EvidenceIds);
        Assert.Equal("data_quality|p1e.quality.ok|accepted|key=activity.steps,grade=device_derived,age_h=0|ev-1",
            line.Render());
    }
}
