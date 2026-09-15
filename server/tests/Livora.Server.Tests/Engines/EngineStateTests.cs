using Livora.Server.Infrastructure.Engines.Decision;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: pin the server state engine against the CLIENT semantics (Application/State/*):
///          baselines (28-day window, 3/7/14 confidence ladder), trend math (5 samples, 6% band),
///          ±12% level band, focus derivation (0.45/0.35/0.20 UserStateService weights), honest
///          nulls and provenance. Every vector below is hand-computed from the client source so a
///          server-side drift in ANY constant fails a test with the client's number in the message.
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// </summary>
public sealed class EngineStateTests
{
    private static StateSnapshot Compute(DecisionInput input) => EngineStateComputer.Compute(input);

    [Fact]
    public void Baseline_needs_three_clean_samples_or_refuses_confidence_none()
    {
        var twoDays = EngineFixtures.Beginner() with { History = EngineFixtures.SleepHistory(nights: 2) };
        var state = Compute(twoDays);
        var sleep = state.Metrics[EngineStateComputer.EngineMetrics.SleepMinutes];
        Assert.Equal(EngineBaselineConfidence.None, sleep.BaselineConfidence);
        Assert.Null(sleep.RelativeDeviation);
        Assert.Equal(EngineLevel.Unknown, sleep.Level);   // no fake level from two nights
    }

    [Theory]
    [InlineData(3, EngineBaselineConfidence.Low)]      // < 7  => Low (early learning)
    [InlineData(7, EngineBaselineConfidence.Medium)]   // < 14 => Medium
    [InlineData(14, EngineBaselineConfidence.High)]    // >=14 => High (2+ weeks)
    public void Baseline_confidence_ladder_matches_client_FromSamples(int nights, EngineBaselineConfidence expected)
    {
        var input = EngineFixtures.NormalUser() with
        { History = EngineFixtures.SleepHistory(nights: nights) };
        var state = Compute(input);
        Assert.Equal(expected, state.Metrics[EngineStateComputer.EngineMetrics.SleepMinutes].BaselineConfidence);
    }

    [Fact]
    public void Baseline_window_is_28_days_of_the_users_own_history()
    {
        // 40 nights: only the nearest 28-window may count (days strictly before today, > asOf-28).
        var far = Enumerable.Range(1, 40).Select(i => new EngineDayRecord(
            EngineFixtures.AsOf.AddDays(-i), SleepMinutes: i <= 28 ? 100 : 1000)).ToList();
        var input = EngineFixtures.NormalUser() with { History = far };
        var state = Compute(input);
        var sleep = state.Metrics[EngineStateComputer.EngineMetrics.SleepMinutes];
        Assert.Equal(100d, sleep.BaselineValue!.Value, 3);        // the 1000s sit outside the 28-day window
        Assert.Equal(27, sleep.BaselineSamples);            // strict > today-28, exactly the client's edge
    }

    [Fact]
    public void Deviation_and_level_use_the_clients_band_and_formula()
    {
        // baseline 470, today 330 => dev = (330-470)/470 = -0.29787... => BelowBaseline (beyond -12%)
        var state = Compute(EngineFixtures.SleepDeprived());
        var sleep = state.Metrics[EngineStateComputer.EngineMetrics.SleepMinutes];
        Assert.Equal(-0.297872, sleep.RelativeDeviation!.Value, 5);
        Assert.Equal(EngineLevel.BelowBaseline, sleep.Level);
        // exactly the band edge is still Normal in the client (±12% is noise)
        Assert.Equal(EngineLevel.Normal, NumericRules.LevelFor(-NumericRules.LevelBand));
        Assert.Equal(EngineLevel.BelowBaseline, NumericRules.LevelFor(-NumericRules.LevelBand - 0.001));
    }

    [Fact]
    public void Bedtime_baseline_wraps_across_midnight_like_the_client()
    {
        // 23:50 (1430) and 00:40 (40) are 50 minutes apart, not 1390: the wrap scale must prove it.
        var history = new List<EngineDayRecord>
        {
            new(EngineFixtures.AsOf.AddDays(-2), BedtimeMinutesOfDay: 1_430),
            new(EngineFixtures.AsOf.AddDays(-3), BedtimeMinutesOfDay: 40),
            new(EngineFixtures.AsOf.AddDays(-4), BedtimeMinutesOfDay: 1_420),
            new(EngineFixtures.AsOf.AddDays(-5), BedtimeMinutesOfDay: 50),
            new(EngineFixtures.AsOf.AddDays(-6), BedtimeMinutesOfDay: 1_425),
            new(EngineFixtures.AsOf.AddDays(-7), BedtimeMinutesOfDay: 45),
            new(EngineFixtures.AsOf.AddDays(-8), BedtimeMinutesOfDay: 1_428),
        };
        var input = EngineFixtures.NormalUser() with { History = history };
        var state = Compute(input);
        var bedtime = state.Metrics[EngineStateComputer.EngineMetrics.BedtimeMinutes];
        Assert.NotNull(bedtime.BaselineValue);
        // wrapped mean of {1430,1480,1420,1490,1425,1485,1428} ≈ 1452.57 => unwrapped ≈ 12.57 min past midnight
        Assert.Equal(11.14, bedtime.BaselineValue!.Value, 2);
    }

    [Fact]
    public void Missing_reading_stays_null_and_drops_completeness_never_zero()
    {
        var input = EngineFixtures.NormalUser() with { Today = new EngineDayRecord(EngineFixtures.AsOf) };
        var state = Compute(input);
        var sleep = state.Metrics[EngineStateComputer.EngineMetrics.SleepMinutes];
        Assert.Null(sleep.Value);
        Assert.Equal(EngineQuality.Missing, sleep.Quality);
        Assert.Equal(0, sleep.Confidence);
        Assert.Equal(0, state.DataCompleteness, 2);         // completeness counts TODAY's readings; none exist
    }

    [Fact]
    public void Focus_is_labeled_derived_from_energy_stress_sleepquality_client_formula()
    {
        var input = EngineFixtures.NormalUser();
        var state = Compute(input);
        var focus = state.Metrics[EngineStateComputer.EngineMetrics.FocusEstimate];
        // 0.45*0.72 + 0.35*(1-0.32) + 0.20*0.78 = 0.324 + 0.238 + 0.156 = 0.718
        Assert.Equal(0.718, focus.Value!.Value, 3);
        Assert.Equal(EngineProvenance.Inferred, focus.Provenance);
        Assert.Equal(EngineQuality.Estimated, focus.Quality);
        Assert.Null(focus.BaselineValue);
        Assert.Equal(EngineBaselineConfidence.None, focus.BaselineConfidence);
        var fact = Assert.Single(state.Facts, f => f.FactId == focus.FactId);
        Assert.Equal(3, fact.DerivedFrom.Count);          // full parentage, not a floating number
    }

    [Fact]
    public void Mood_and_energy_are_user_provided_even_when_row_says_observed()
    {
        // Honest labelling: no instrument measures mood; the record tag cannot launder it.
        var state = Compute(EngineFixtures.NormalUser());
        Assert.Equal(EngineProvenance.UserProvided, state.Metrics[EngineStateComputer.EngineMetrics.Mood].Provenance);
        Assert.Equal(EngineProvenance.UserProvided, state.Metrics[EngineStateComputer.EngineMetrics.Energy].Provenance);
        Assert.Equal(EngineProvenance.Observed, state.Metrics[EngineStateComputer.EngineMetrics.Steps].Provenance);
    }

    [Fact]
    public void Freshness_counts_days_since_last_sleep_record_stale_when_feed_ageing()
    {
        var partial = Compute(EngineFixtures.PartialData());
        Assert.Equal(42, partial.DaysSinceFreshSleep);    // sleep history row nearest today sits 42 days back

        var noSleepEver = Compute(EngineFixtures.NoData());
        Assert.Equal(99, noSleepEver.DaysSinceFreshSleep); // client sentinel: no feed at all
    }

    [Fact]
    public void Every_baseline_citation_gets_its_own_fact_with_lineage()
    {
        var state = Compute(EngineFixtures.SleepDeprived());
        var sleep = state.Metrics[EngineStateComputer.EngineMetrics.SleepMinutes];
        Assert.False(string.IsNullOrEmpty(sleep.BaselineFactId));
        var fact = Assert.Single(state.Facts, f => f.FactId == sleep.BaselineFactId);
        Assert.Equal(EngineProvenance.Inferred, fact.Provenance);
        Assert.Contains("window=28d", fact.SourceRef);    // the window that earned it is in the trace
        Assert.Contains("samples=14", fact.SourceRef);
    }

    [Fact]
    public void Trend_engine_refuses_under_five_samples_and_applies_the_six_percent_band()
    {
        Assert.Equal(EngineTrend.InsufficientData, NumericRules.ClassifyTrend([1, 2, 3, 4], true));
        Assert.Equal(EngineTrend.Stable, NumericRules.ClassifyTrend([100, 100, 100, 101, 100, 100], true)); // ~0.7%
        Assert.Equal(EngineTrend.Improving, NumericRules.ClassifyTrend([100, 100, 100, 120, 120, 120], true)); // +20%
        Assert.Equal(EngineTrend.Declining, NumericRules.ClassifyTrend([100, 100, 100, 120, 120, 120], false));
        // NaN-tolerant like the client: NaN entries are dropped before the count gate
        Assert.Equal(EngineTrend.Stable, NumericRules.ClassifyTrend([100, double.NaN, 100, 102, 100, 100], true));
    }

    [Fact]
    public void Signed_badness_respects_metric_polarity_client_rule()
    {
        Assert.Equal(0.2, NumericRules.SignedBadness(0.2, higherIsBetter: false), 5);   // stress above = bad
        Assert.Equal(-0.2, NumericRules.SignedBadness(0.2, higherIsBetter: true), 5);   // sleep above = good
        Assert.Equal(0, NumericRules.SignedBadness(null, true));
    }

    [Fact]
    public void Pipeline_is_deterministic_same_input_equal_output()
    {
        var input = EngineFixtures.HighMeetingLoad();
        var a = new DecisionPipeline().Run(input);
        var b = new DecisionPipeline().Run(input);
        Assert.Equal(a.DecisionId, b.DecisionId);
        Assert.Equal(a.RulesFired.Count, b.RulesFired.Count);
        Assert.Equal(a.PlanItems.Count, b.PlanItems.Count);
        for (int i = 0; i < a.RulesFired.Count; i++)
        {
            Assert.Equal(a.RulesFired[i].RuleKey, b.RulesFired[i].RuleKey);
            Assert.Equal(a.RulesFired[i].Confidence, b.RulesFired[i].Confidence);
        }
    }
}
