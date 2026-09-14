using Livora.Server.Infrastructure.Engines.Pipeline;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: falsifiable tests for stages 3 (baseline), 4 (state), 5 (patterns) and 6 (goals) —
///          the "what is THIS person's normal, and what does today say" layers. Every boundary in
///          the header comments is pinned: change the constant without changing the test and this
///          file goes red.
/// OWNER: Agent 10+11.
/// </summary>
public sealed class BaselineStatePatternTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime AsOf = new(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc);

    private static List<HistoryDay> SleepHistory(double minutes, int days, double step = 0)
        => Enumerable.Range(0, days)
            .Select(i => new HistoryDay(
                new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                SleepMinutes: minutes + i * step))
            .ToList();

    // ---- stage 3: baseline honesty -------------------------------------------

    [Fact]
    public void Two_weeks_of_history_earn_a_High_30_day_baseline()
    {
        var set = BaselineStage.Compute(SleepHistory(450, 30), AsOf);
        var sleep = set.Find(FactKeys.SleepMinutes)!;
        Assert.True(sleep.Usable);
        Assert.Equal(BaselineWindow.Days30, sleep.WindowUsed);
        Assert.Equal(BaselineConfidence.High, sleep.Confidence);
        Assert.Equal(450, sleep.Value, 3);
        Assert.Equal(30, sleep.SampleDays);
    }

    [Fact]
    public void Four_days_of_history_refuse_every_window_and_report_how_little_exists()
    {
        var set = BaselineStage.Compute(SleepHistory(400, 4), AsOf);
        var sleep = set.Find(FactKeys.SleepMinutes)!;
        Assert.False(sleep.Usable);
        Assert.True(double.IsNaN(sleep.Value));       // NEVER a computed-looking number
        Assert.Null(sleep.WindowUsed);
        Assert.Equal(BaselineConfidence.None, sleep.Confidence);
        Assert.Equal(4, sleep.SampleDays);            // the honest "how little" number
    }

    [Theory]
    [InlineData(4, false)]   // below the 7d density gate (5) — refuses
    [InlineData(5, true)]    // exactly at the gate
    [InlineData(7, true)]    // the whole week observed
    public void Density_gate_boundary_is_pinned(int days, bool usable)
    {
        // As-of 2026-04-10: the 7-day window (04-04 … 04-10) is observable from day one, so only
        // the DENSITY gate can refuse here — coverage is satisfied by construction.
        var rows = SleepHistory(450, days);
        var set = BaselineStage.Compute(rows, new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(usable, set.Find(FactKeys.SleepMinutes)!.Usable);
    }

    [Fact]
    public void A_seven_day_window_can_never_claim_High_confidence()
    {
        // 21 rows but all clustered in the last 7 days: Days30/Days14 fail coverage, Days7 passes
        var dense = Enumerable.Range(0, 7).SelectMany(_ => Enumerable.Range(0, 3))
            .Take(7)
            .Select(i => new HistoryDay(new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                SleepMinutes: 450))
            .ToList();
        var set = BaselineStage.Compute(dense, new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc));
        var sleep = set.Find(FactKeys.SleepMinutes)!;
        Assert.Equal(BaselineWindow.Days7, sleep.WindowUsed);
        Assert.True(sleep.Confidence <= BaselineConfidence.Low,
            "a week of history is a week of knowing — the window ceiling must cap it");
    }

    [Fact]
    public void One_two_hour_night_cannot_drag_a_recency_weighted_median_baseline()
    {
        var rows = SleepHistory(450, 29);
        rows.Add(new HistoryDay(rows[rows.Count - 1].DateUtc.AddDays(1), SleepMinutes: 120));  // an outlier night
        var set = BaselineStage.Compute(rows, AsOf);
        Assert.Equal(450, set.Find(FactKeys.SleepMinutes)!.Value, 0);   // median, not mean

        // sanity on the same rows: the client's mean would have been dragged down
        double mean = rows.Average(r => r.SleepMinutes!.Value);
        Assert.True(mean < 450);
    }

    [Fact]
    public void Bedtime_across_midnight_wraps_before_averaging_and_unwraps_after()
    {
        // 23:50 and 00:40 are 50 minutes apart, not 1430: the wrap must make them neighbours.
        var rows = Enumerable.Range(0, 30).Select(i => new HistoryDay(
            new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
            BedtimeMinutesOfDay: i % 2 == 0 ? 23 * 60 + 50 : 0 * 60 + 40)).ToList();
        var set = BaselineStage.Compute(rows, AsOf);
        var bedtime = set.Find(FactKeys.BedtimeMinutesOfDay)!;
        Assert.True(bedtime.Usable);
        Assert.InRange(bedtime.Value, 23 * 60 + 45, 24 * 60 + 45);  // post-wrap scale unwrapped
    }

    [Fact]
    public void Deviation_helper_refuses_to_compare_against_an_unusable_baseline()
    {
        var refused = new MetricBaseline(FactKeys.Steps, double.NaN, double.NaN, 2, null,
            BaselineConfidence.None, true, "insufficient_samples_or_coverage");
        Assert.Null(refused.DeviationOf(5000));
        var earned = new MetricBaseline(FactKeys.Steps, 10_000, 500, 20, BaselineWindow.Days30,
            BaselineConfidence.High, true, null);
        Assert.Equal(-0.5, earned.DeviationOf(5000));
    }

    [Fact]
    public void Worst_confidence_is_the_weakest_link_over_the_keys_a_rule_used()
    {
        var set = BaselineStage.Compute(SleepHistory(450, 30), AsOf);   // sleep only
        Assert.Equal(BaselineConfidence.None,
            set.WorstConfidence([FactKeys.SleepMinutes, FactKeys.Steps]));
    }

    // ---- stage 4: state polarity + conclusion keys ----------------------------

    private static StateSnapshot StateWith(
        double sleepMinutes, double baselineSleep = 450,
        double meetings = 60, double screen = 180, double steps = 8000, double recovery = 0.72,
        double stress = 0.4, BaselineConfidence conf = BaselineConfidence.High)
    {
        var facts = new FactSet(
            [
                Fact.Of(FactKeys.SleepMinutes, sleepMinutes, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-9)),
                Fact.Of(FactKeys.MeetingMinutes, meetings, "minutes", EvidenceGrade.ProviderDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.ScreenMinutes, screen, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.Steps, steps, "steps", EvidenceGrade.DeviceDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.RecoveryScore, recovery, "fraction", EvidenceGrade.DeviceDerived, Now.AddHours(-8)),
                Fact.Of(FactKeys.Stress, stress, "fraction", EvidenceGrade.DeviceDerived, Now.AddHours(-8)),
            ], Now);
        var quality = DataQualityStage.Assess(facts, DataQualityStage.ExpectedKeysFor(DecisionProfile.DailyPlan));
        var baselines = Baselines(sleepMinutes: baselineSleep, steps: 8000, recovery: 0.72,
            screen: 180, meetings: 60, conf: conf);
        return StateStage.Derive(facts, quality, baselines);
    }

    private static BaselineResult Baselines(
        double sleepMinutes, double steps, double recovery, double screen, double meetings,
        BaselineConfidence conf)
    {
        var window = conf switch
        {
            BaselineConfidence.High => BaselineWindow.Days30,
            BaselineConfidence.Medium => BaselineWindow.Days14,
            _ => BaselineWindow.Days7,
        };
        var dict = new Dictionary<string, MetricBaseline>(StringComparer.Ordinal);
        void Add(string key, double value, bool hib = true) =>
            dict[key] = new MetricBaseline(key, value, 10, 20, window, conf, hib, null);
        Add(FactKeys.SleepMinutes, sleepMinutes);
        Add(FactKeys.Steps, steps);
        Add(FactKeys.RecoveryScore, recovery);
        Add(FactKeys.ScreenMinutes, screen, hib: false);
        Add(FactKeys.MeetingMinutes, meetings, hib: false);
        Add(FactKeys.Stress, 0.4, hib: false);
        return new BaselineResult(AsOf, dict, Array.Empty<TrailEntry>());
    }

    [Theory]
    [InlineData(455, false)]   // +1% — noise, inside the ±12% band
    [InlineData(395, false)]   // 55 min deficit — under the 1.5 h rule
    [InlineData(361, false)]   // 89 min = 1.483 h — still under
    [InlineData(360, true)]    // exactly 90 min = 1.5 h — RuleEngine.cs:31 uses >=, so it FIRES
    [InlineData(350, true)]    // 1.67 h
    public void Sleep_deficit_boundary_follows_the_ported_ruleengine_number(
        double sleepMinutes, bool expectedFired)
        => Assert.Equal(expectedFired, StateWith(sleepMinutes).Fired("p1e.state.sleep_poor"));

    [Fact]
    public void A_higher_is_worse_signal_above_its_band_reads_as_high_not_better()
    {
        var state = StateWith(450, meetings: 210);   // baseline 60, +250%
        Assert.Equal(SignalLevel.High, state.Signal(FactKeys.MeetingMinutes).Level);
        Assert.True(state.Fired("p1e.state.meeting_load_high"));
    }

    [Fact]
    public void Meeting_load_absolute_floor_and_relative_band_are_alternative_triggers()
    {
        Assert.True(StateWith(450, meetings: 185).Fired("p1e.state.meeting_load_high")); // ≥180 absolute
        Assert.True(StateWith(450, meetings: 82).Fired("p1e.state.meeting_load_high"));  // +36.7% ≥ +35%
        Assert.False(StateWith(450, meetings: 80).Fired("p1e.state.meeting_load_high")); // +33% and <180
    }

    [Fact]
    public void Recovery_relative_dip_and_absolute_floor_both_count()
    {
        Assert.True(StateWith(450, recovery: 0.50).Fired("p1e.state.recovery_low"));  // < 0.55
        Assert.True(StateWith(450, recovery: 0.58).Fired("p1e.state.recovery_low"));  // -19% ≤ -18%
        Assert.False(StateWith(450, recovery: 0.66).Fired("p1e.state.recovery_low")); // -11%, 0.66
    }

    [Fact]
    public void Missing_signal_is_named_missing_not_zero()
    {
        var facts = new FactSet(
            [
                Fact.Of(FactKeys.SleepMinutes, 450, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-9)),
                Fact.Of(FactKeys.MeetingMinutes, 60, "minutes", EvidenceGrade.ProviderDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.ScreenMinutes, 180, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.Steps, 8000, "steps", EvidenceGrade.DeviceDerived, Now.AddHours(-1)),
                // recovery absent entirely
            ], Now);
        var quality = DataQualityStage.Assess(facts, DataQualityStage.ExpectedKeysFor(DecisionProfile.DailyPlan));
        var state = StateStage.Derive(facts, quality,
            Baselines(450, 8000, 0.72, 180, 60, BaselineConfidence.High));
        Assert.Equal(SignalLevel.Unknown, state.Signal(FactKeys.RecoveryScore).Level);
        Assert.False(state.Fired("p1e.state.recovery_low"));
        Assert.Equal("missing", state.Signal(FactKeys.RecoveryScore).Verdict);
    }

    [Fact]
    public void Conclusion_carries_the_grade_of_its_own_signal_and_the_ceiling_is_the_best_of_them()
    {
        // sleep self-reported, everything else device-grade
        var facts = new FactSet(
            [
                Fact.Of(FactKeys.SleepMinutes, 300, "minutes", EvidenceGrade.SelfReported, Now.AddHours(-9)),
                Fact.Of(FactKeys.MeetingMinutes, 200, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.ScreenMinutes, 200, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.Steps, 8000, "steps", EvidenceGrade.DeviceDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.RecoveryScore, 0.7, "fraction", EvidenceGrade.DeviceDerived, Now.AddHours(-8)),
            ], Now);
        var quality = DataQualityStage.Assess(facts, DataQualityStage.ExpectedKeysFor(DecisionProfile.DailyPlan));
        var state = StateStage.Derive(facts, quality, Baselines(450, 8000, 0.72, 180, 60, BaselineConfidence.High));
        Assert.Equal(EvidenceGrade.SelfReported, state.GradeFor("p1e.state.sleep_poor"));
        Assert.Equal(EvidenceGrade.DeviceDerived, state.GradeFor("p1e.state.meeting_load_high"));
        Assert.Equal(EvidenceGrade.DeviceDerived, state.EvidenceCeiling);  // ceiling of the set
    }

    [Fact]
    public void Every_conclusion_trail_line_carries_a_fired_or_clear_verdict_and_factors()
    {
        var state = StateWith(350);
        var vocabulary = new[] { "fired", "clear", "worse", "normal", "better", "missing", "no_baseline" };
        Assert.All(state.Trail.Where(t => t.Stage == "state"),
            t => Assert.Contains(t.Verdict, vocabulary));
        Assert.Contains(state.Trail, t => t.RuleKey == "p1e.state.sleep_poor" && t.Verdict == "fired");
    }

    // ---- stage 5: patterns ----------------------------------------------------

    private static List<HistoryDay> FullHistory(int days, double steps = 8000, double sleep = 450,
        double bedtime = 1350)
        => Enumerable.Range(0, days).Select(i => new HistoryDay(
            new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
            SleepMinutes: sleep, BedtimeMinutesOfDay: bedtime, Steps: steps)).ToList();

    [Fact]
    public void Below_21_days_nothing_is_reported_at_all()
    {
        var hist = FullHistory(14);
        var set = BaselineStage.Compute(hist, AsOf);
        Assert.Empty(PatternStage.Assess(hist, set, AsOf));
    }

    [Fact]
    public void Three_late_nights_in_the_last_seven_fire_the_recurring_late_sleep_pattern()
    {
        var hist = FullHistory(31);   // 04-04 … 05-04: exactly 7 nights in the check window
        for (int i = 0; i < 3; i++)
        {
            int late = hist.Count - 1 - i;
            hist[late] = hist[late] with { BedtimeMinutesOfDay = 23 * 60 + 59 };   // > baseline+60
        }
        var set = BaselineStage.Compute(hist, AsOf);
        var findings = PatternStage.Assess(hist, set, AsOf);
        Assert.Contains(findings, f => f.Kind == PatternKind.LateSleepRecurring && f.Samples >= 3);
    }

    [Fact]
    public void Two_late_nights_do_not_fire()
    {
        var hist = FullHistory(31);
        hist[hist.Count - 1] = hist[hist.Count - 1] with { BedtimeMinutesOfDay = 23 * 60 + 59 };
        hist[hist.Count - 2] = hist[hist.Count - 2] with { BedtimeMinutesOfDay = 23 * 60 + 59 };
        var set = BaselineStage.Compute(hist, AsOf);
        Assert.DoesNotContain(PatternStage.Assess(hist, set, AsOf),
            f => f.Kind == PatternKind.LateSleepRecurring);
    }

    [Fact]
    public void Sunday_collapsing_to_half_steps_fires_the_weekday_dip()
    {
        var hist = FullHistory(35);   // 5 Sundays ≥ GateWeekdaySamples
        for (int i = 0; i < hist.Count; i++)
            if (hist[i].DateUtc.DayOfWeek == DayOfWeek.Sunday)
                hist[i] = hist[i] with { Steps = 3000 };     // far below the personal steps baseline
        var set = BaselineStage.Compute(hist, AsOf);
        var dip = PatternStage.Assess(hist, set, AsOf)
            .FirstOrDefault(f => f.Kind == PatternKind.WeekdayActivityDip);
        Assert.NotNull(dip);
        Assert.Contains("Sunday", dip!.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Poor_sleep_days_repeatedly_followed_by_low_activity_fire_the_coupling()
    {
        var hist = FullHistory(30);
        for (int i = 1; i < hist.Count; i += 2)
        {
            hist[i - 1] = hist[i - 1] with { SleepMinutes = 330 };   // poor night
            hist[i] = hist[i] with { Steps = 3000 };                 // low next day (>12% below)
        }
        var set = BaselineStage.Compute(hist, AsOf);
        Assert.Contains(PatternStage.Assess(hist, set, AsOf),
            f => f.Kind == PatternKind.LowActivityAfterPoorSleep && f.Samples >= PatternStage.GateCouplingPairs);
    }

    // ---- stage 6: goals --------------------------------------------------------

    [Fact]
    public void Deadline_risk_and_stall_use_the_ported_client_numbers()
    {
        Assert.Equal(30, GoalStage.DeadlineRiskDays);          // PlanAdaptationEngine.cs:100
        Assert.Equal(0.5, GoalStage.DeadlineRiskFractionCeiling); // :101
        Assert.Equal(10, GoalStage.StagnationDays);            // PatternEngine.cs:95

        var goals = GoalStage.Assess(
            [
                new GoalInput("g-risk", "program", 0.2, Now.AddDays(10), 2),
                new GoalInput("g-ok", "program", 0.8, Now.AddDays(10), 2),
                new GoalInput("g-stalled", "habit", 0.3, Now.AddDays(90), 12),
            ], AsOf);
        Assert.Equal("at_risk", goals.ById["g-risk"].Verdict);
        Assert.Equal("on_track", goals.ById["g-ok"].Verdict);
        Assert.Equal("stalled", goals.ById["g-stalled"].Verdict);
        Assert.Equal(2, goals.AtRisk.Count);
    }

    [Fact]
    public void Unknown_progress_refuses_to_a_verdict_instead_of_coercing_zero()
    {
        var goals = GoalStage.Assess([new GoalInput("g", "goal", double.NaN, Now.AddDays(5), null)], AsOf);
        Assert.Equal("unknown", goals.ById["g"].Verdict);
    }
}
