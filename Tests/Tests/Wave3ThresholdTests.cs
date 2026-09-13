using LIVORA.Application.Abstractions;
using LIVORA.Application.HealthData;
using LIVORA.Application.Insights;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;
using StateMetrics = LIVORA.Domain.Models.State.Metrics;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Boundary conditions of the thresholds the whole honesty story is written as constants. Each
/// existing suite tests a rule "on" or "off"; these pin the exact edge, so changing a constant is a
/// visible decision with a failing test attached rather than a silent product change.
/// </summary>
public class Wave3ThresholdBoundaryTests
{
    // ---- TrendService -----------------------------------------------------

    private static readonly TrendService Trends = new();

    [Fact]
    public void Trend_ExactlyMinimumSamples_IsClassified_NotInsufficient()
        => Assert.Equal(TrendDirection.Improving, Trends.Compute(new double[] { 5, 5, 6, 7, 7 }, higherIsBetter: true));

    [Fact]
    public void Trend_JustBelowMinimum_IsInsufficientData()
        => Assert.Equal(TrendDirection.InsufficientData, Trends.Compute(new double[] { 5, 5, 7, 7 }, true));

    [Fact]
    public void Trend_NaNEntries_AreDroppedBeforeTheCountCheck()
    {
        // Five values of which two are NaN must NOT be classified from three — the count is taken
        // after filtering, so the honest answer here is "not enough data".
        var withNaN = new double[] { 5, double.NaN, 6, double.NaN, 9 };
        Assert.Equal(TrendDirection.InsufficientData, Trends.Compute(withNaN, true));
    }

    [Theory]
    [InlineData(0.059, TrendDirection.Stable)]      // inside the 6% band
    [InlineData(0.060, TrendDirection.Improving)]   // exactly at the band: the test is `<`, so 6% is a real move
    [InlineData(0.061, TrendDirection.Improving)]   // outside it
    [InlineData(-0.061, TrendDirection.Declining)]
    public void Trend_NoiseBandIsSixPercent(double swing, TrendDirection expected)
    {
        // 6 samples split 3/3, so the measured change IS the swing.
        var values = new[] { 100.0, 100.0, 100.0, 100.0 * (1 + swing), 100.0 * (1 + swing), 100.0 * (1 + swing) };
        Assert.Equal(expected, Trends.Compute(values, higherIsBetter: true));
    }

    [Fact]
    public void Trend_FlatAtZero_IsStable_NotDivisionByZero()
    {
        // scale = Max(|first|, 1e-6) exists precisely so an all-zero window cannot divide by zero.
        Assert.Equal(TrendDirection.Stable, Trends.Compute(new double[] { 0, 0, 0, 0, 0 }, true));
    }

    [Fact]
    public void Trend_ZeroToSomething_RegistersAsImproving()
        => Assert.Equal(TrendDirection.Improving, Trends.Compute(new double[] { 0, 0, 0, 50, 50 }, true));

    [Fact]
    public void Trend_OddSampleCount_SplitsWithoutLeakingTheMiddlePointIntoBothHalves()
    {
        // 7 samples: half = 3, first 3 vs last 4 (Skip(half)) — the middle sample belongs to the
        // second half exactly once. Pinned because a refactor to "middle inclusive both sides"
        // would silently flatten every trend.
        Assert.Equal(TrendDirection.Improving, Trends.Compute(new double[] { 5, 5, 5, 5, 9, 9, 9 }, true));
    }

    // ---- DataNormalizer sanity ranges ------------------------------------

    private static readonly DateTime Now = new(2026, 9, 11, 18, 0, 0);
    private static readonly DataNormalizer Normalizer = new();

    private static DataPoint Pt(double v, string unit = "score01", DateTime? ts = null, DataQuality q = DataQuality.Complete) =>
        new() { Value = v, Unit = unit, Timestamp = ts ?? Now.Date.AddHours(9), Quality = q };

    private static NormalizedDay Raw(DataPoint? stress = null, DataPoint? rhr = null, DataPoint? hrv = null,
        DataPoint? active = null, DataPoint? bedtime = null) => new()
    {
        Date = Now.Date,
        Origin = DataOrigin.Mock,
        SleepMinutes = Pt(450, "minutes"),
        SleepQuality = Pt(0.8),
        SleepConsistency = Pt(0.8),
        BedtimeMinutesOfDay = bedtime ?? Pt(1380, "minutesOfDay"),
        WakeMinutesOfDay = Pt(420, "minutesOfDay"),
        Steps = Pt(8000, "steps"),
        ActiveMinutes = active ?? Pt(35, "minutes"),
        RecoveryScore = Pt(0.7),
        RestingHeartRate = rhr,
        HrvMs = hrv,
        Stress = stress ?? Pt(0.4),
        Mood = Pt(0.7),
        Energy = Pt(0.7),
    };

    [Theory]
    [InlineData(25, DataQuality.Complete)]     // inclusive lower bound of the RHR window
    [InlineData(150, DataQuality.Complete)]    // inclusive upper bound
    [InlineData(24, DataQuality.Invalid)]
    [InlineData(151, DataQuality.Invalid)]
    public void Normalizer_HeartRateWindow_IsInclusive(double value, DataQuality expected)
    {
        var day = Normalizer.Normalize(Raw(rhr: Pt(value, "bpm")), Now);
        Assert.Equal(expected, day.RestingHeartRate!.Quality);
    }

    [Theory]
    [InlineData(5, DataQuality.Complete)]
    [InlineData(300, DataQuality.Complete)]
    [InlineData(4, DataQuality.Invalid)]
    [InlineData(301, DataQuality.Invalid)]
    public void Normalizer_HrvWindow_IsInclusive(double value, DataQuality expected)
    {
        var day = Normalizer.Normalize(Raw(hrv: Pt(value, "ms")), Now);
        Assert.Equal(expected, day.HrvMs!.Quality);
    }

    [Theory]
    [InlineData(960, DataQuality.Complete)]    // 16h is the ceiling and the check is `> max`
    [InlineData(961, DataQuality.Invalid)]
    [InlineData(0, DataQuality.Complete)]
    [InlineData(-1, DataQuality.Invalid)]
    public void Normalizer_ActiveMinutesCeiling_IsSixteenHours(double value, DataQuality expected)
    {
        var day = Normalizer.Normalize(Raw(active: Pt(value, "minutes")), Now);
        Assert.Equal(expected, day.ActiveMinutes.Quality);
    }

    [Theory]
    [InlineData(0, DataQuality.Complete)]
    [InlineData(1440, DataQuality.Complete)]
    [InlineData(1441, DataQuality.Invalid)]
    public void Normalizer_BedtimeSpansExactlyOneClockFace(double value, DataQuality expected)
    {
        var day = Normalizer.Normalize(Raw(bedtime: Pt(value, "minutesOfDay")), Now);
        Assert.Equal(expected, day.BedtimeMinutesOfDay.Quality);
    }

    [Theory]
    [InlineData(1.0, DataQuality.Complete)]
    [InlineData(1.001, DataQuality.Invalid)]
    public void Normalizer_FractionScoresRejectJustOverOne(double value, DataQuality expected)
    {
        var day = Normalizer.Normalize(Raw(stress: Pt(value)), Now);
        Assert.Equal(expected, day.Stress.Quality);
    }

    [Fact]
    public void Normalizer_StalenessIsMeasuredAgainstTheCalendarDay_NotTheClock()
    {
        // Fix() compares against now.Date, so the boundary is whole days, not hours: a reading
        // timestamped inside the 2-day sleep window is Complete, one day beyond it is Stale.
        var inside = Pt(430, "minutes", ts: Now.Date.AddDays(-2).AddHours(1));
        var outside = Pt(430, "minutes", ts: Now.Date.AddDays(-3).AddHours(1));
        Assert.Equal(DataQuality.Complete, inside.WithMaxAge(Now.Date, DataNormalizer.SleepMaxAge).Quality);
        Assert.Equal(DataQuality.Stale, outside.WithMaxAge(Now.Date, DataNormalizer.SleepMaxAge).Quality);
    }

    [Fact]
    public void Normalizer_KeepsOriginAndDateOfTheRawDay()
    {
        // Provenance must not be laundered by normalization: Manual in, Manual out.
        var raw = new NormalizedDay
        {
            Date = Now.Date, Origin = DataOrigin.Manual,
            SleepMinutes = new DataPoint { Value = 400, Timestamp = Now.Date, Origin = DataOrigin.Manual },
        };
        var day = Normalizer.Normalize(raw, Now);
        Assert.Equal(DataOrigin.Manual, day.Origin);
        Assert.Equal(DataOrigin.Manual, day.SleepMinutes.Origin);
        Assert.Equal(raw.Date, day.Date);
    }

    // ---- RecommendationService cap ---------------------------------------

    [Fact]
    public void Recommendations_NeverExceedOneHighPriority()
    {
        var svc = new RecommendationService(new RuleEngine());
        var state = StateFactory.State(sleepMin: 300, sleepBase: 450, recovery: 0.3, stress: 0.95);
        var recs = svc.BuildRecommendations(state, new UserProfile(), Array.Empty<Goal>(),
            Array.Empty<Habit>(), null, new DateTime(2026, 9, 11, 20, 0, 0));

        Assert.True(recs.Count(r => r.ScoredPriority >= RecommendationPriority.High) <= 1);
        Assert.True(recs.Count <= 3, $"cap of 3 broken: {recs.Count}");
    }

    [Fact]
    public void Recommendations_AreOrderedHighPriorityFirst()
    {
        var svc = new RecommendationService(new RuleEngine());
        var state = StateFactory.State(sleepMin: 300, sleepBase: 450, recovery: 0.3, stress: 0.9);
        var recs = svc.BuildRecommendations(state, new UserProfile(), Array.Empty<Goal>(),
            Array.Empty<Habit>(), null, Now.Date);
        var ranks = recs.Select(r => (int)r.ScoredPriority).ToList();
        Assert.Equal(ranks.OrderByDescending(x => x), ranks);
    }

    [Fact]
    public void Recommendations_CarryTheSameArgCountTheirExplainTemplateNeeds()
    {
        // A rule's ConditionArgs must fit the key it ships with, or the UI renders a literal {0}.
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;

        var engine = new RuleEngine();
        foreach (var state in new[]
        {
            StateFactory.State(sleepMin: 330, sleepBase: 450),
            StateFactory.State(recovery: 0.3),
            StateFactory.State(stress: 0.9),
            StateFactory.State(steps: 500, stepsBase: 8000, recovery: 0.8),
            StateFactory.State(sleepDaysSinceFreshData: 9),
        })
        {
            foreach (var r in engine.Evaluate(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now.Date.AddHours(21)))
            {
                var need = Wave3Harness.Placeholders(pair.En[r.ConditionKey]).Length;
                Assert.True(r.ConditionArgs.Length >= need,
                    $"{r.RuleKey}: {r.ConditionKey} needs {need} args, rule passes {r.ConditionArgs.Length}");
            }
        }
    }

    // ---- WeeklySummaryService sample-size gate ----------------------------

    private static readonly DateTime Today = new(2026, 9, 11);
    private static readonly DateTime WeekStart = new(2026, 9, 4);

    private static DailyHistoryRecord Rec(DateTime date, double sleep = 450, double stress = 0.4) => new()
    {
        Date = date, Origin = nameof(DataOrigin.Mock), Completeness = 1,
        SleepMinutes = sleep, SleepQuality = 0.8, SleepConsistency = 0.8, BedtimeMinutesOfDay = 1380,
        Steps = 8000, ActiveMinutes = 30, RecoveryScore = 0.7, Stress = stress, Mood = 0.7, Energy = 0.7,
    };

    private static WeeklySummaryService Summary(int closedDays, IEnumerable<DailyHistoryRecord>? extra = null)
    {
        var records = Enumerable.Range(0, closedDays).Select(i => Rec(WeekStart.AddDays(i))).ToList();
        if (extra is not null) records.AddRange(extra);
        return new WeeklySummaryService(new FakeHistoryRepository(records), new InMemoryRepo<Habit>(),
            new InMemoryRepo<Goal>(), new TrendService(), new FakeClock(Today));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WeeklySummary_BelowThreeClosedDays_SaysNothingAtAll(int days)
        => Assert.Null(await Summary(days).BuildLastWeekAsync());

    [Fact]
    public async Task WeeklySummary_AtThreeDays_SpeaksButClaimsNoTrend()
    {
        var s = await Summary(3).BuildLastWeekAsync();
        Assert.NotNull(s);
        Assert.Equal(BaselineConfidence.Low, s!.Confidence);
        Assert.Equal(TrendDirection.InsufficientData, s.SleepTrend);
        Assert.Empty(s.ImprovementKeys);
        Assert.Empty(s.DeclineKeys);
        Assert.StartsWith("Weekly.Focus.", s.FocusKey);
    }

    [Fact]
    public async Task WeeklySummary_TodayAndTheWeekBeforeLast_AreNotInTheWindow()
    {
        // A huge sleep value today and a huge one 10 days ago must not move the closed window.
        var extra = new[]
        {
            Rec(Today, sleep: 900),
            Rec(WeekStart.AddDays(-1), sleep: 900),
            Rec(WeekStart.AddDays(7), sleep: 900),   // first day of the CURRENT week
        };
        var clean = await Summary(7).BuildLastWeekAsync();
        var polluted = await Summary(7, extra).BuildLastWeekAsync();

        Assert.NotNull(clean);
        Assert.Equal(clean!.SleepTrend, polluted!.SleepTrend);
        Assert.Equal(clean.Confidence, polluted.Confidence);
        Assert.Equal(clean.FocusKey, polluted.FocusKey);
    }

    [Fact]
    public async Task WeeklySummary_AllStableWeek_ProducesNoBulletsButStillAPlan()
    {
        var flat = Enumerable.Range(0, 7).Select(i => Rec(WeekStart.AddDays(i))).ToList();
        var s = await new WeeklySummaryService(new FakeHistoryRepository(flat), new InMemoryRepo<Habit>(),
            new InMemoryRepo<Goal>(), new TrendService(), new FakeClock(Today)).BuildLastWeekAsync();

        Assert.NotNull(s);
        Assert.Empty(s!.ImprovementKeys);
        Assert.Empty(s.DeclineKeys);
        Assert.Equal(TrendDirection.Stable, s.SleepTrend);
        Assert.Equal("Weekly.Focus.OneHabit", s.FocusKey);   // nothing declining, no habits to protect
    }
}
