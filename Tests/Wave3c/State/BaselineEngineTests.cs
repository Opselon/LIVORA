using LIVORA.Application.State;
using LIVORA.Application.State.Wave3b;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;
using static LIVORA.Tests.Wave3c.Lane04Fixtures;

namespace LIVORA.Tests.Wave3c;

/// <summary>Window gates, statistic choice, refusal honesty — BaselineEngine (lane 04).</summary>
public class BaselineEngineTests
{
    // ---- gate arithmetic -----------------------------------------------------

    [Theory]
    [InlineData(BaselineWindow.Days7, 4)]
    [InlineData(BaselineWindow.Days14, 8)]
    [InlineData(BaselineWindow.Days30, 16)]
    public void Gates_AreTheDocumentedMinimums(BaselineWindow window, int expected) =>
        Assert.Equal(expected, BaselineEngine.MinimumSamples(window));

    [Theory]
    [InlineData(BaselineWindow.Days7, 7)]
    [InlineData(BaselineWindow.Days14, 14)]
    [InlineData(BaselineWindow.Days30, 30)]
    public void Windows_HaveTheDocumentedLengths(BaselineWindow window, int expected) =>
        Assert.Equal(expected, BaselineEngine.WindowDays(window));

    [Fact]
    public void ThirtyDayWindow_FromTwoDaysOfData_Refuses()
    {
        // THE headline honesty test: 2 clean days cannot back a 30-day baseline.
        var history = new List<DailyHistoryRecord> { Day(0), Day(1) };
        var e = BaselineEngine.ComputeWindow(Metrics.SleepMinutes, history, Epoch.AddDays(1), BaselineWindow.Days30);

        Assert.Null(e.WindowUsed);
        Assert.False(e.Usable);
        Assert.Equal(BaselineConfidence.None, e.Baseline.Confidence);
        Assert.True(double.IsNaN(e.Baseline.Value), "a refused baseline must never carry a computed-looking number");
        Assert.True(double.IsNaN(e.Baseline.StdDev));
        Assert.Equal(2, e.SampleDays);   // the honest "how much data exists" number
    }

    [Theory]
    [InlineData(BaselineWindow.Days7, 3)]    // one below the 4-sample gate
    [InlineData(BaselineWindow.Days14, 7)]   // one below the 8-sample gate
    [InlineData(BaselineWindow.Days30, 15)]  // one below the 16-sample gate
    public void ExactlyOneBelowEachGate_Refuses(BaselineWindow window, int days)
    {
        var e = BaselineEngine.ComputeWindow(Metrics.Steps, Flat(days), Epoch.AddDays(days - 1), window);
        Assert.False(e.Usable);
        Assert.True(double.IsNaN(e.Baseline.Value));
        Assert.Equal(days, e.SampleDays);
    }

    [Theory]
    [InlineData(BaselineWindow.Days7, 4)]
    [InlineData(BaselineWindow.Days14, 8)]
    [InlineData(BaselineWindow.Days30, 16)]
    public void ExactlyAtEachGate_Computes(BaselineWindow window, int days)
    {
        var e = BaselineEngine.ComputeWindow(Metrics.Steps, Flat(days), Epoch.AddDays(days - 1), window);
        Assert.True(e.Usable);
        Assert.Equal(window, e.WindowUsed);
        Assert.Equal(days, e.SampleDays);
        Assert.False(double.IsNaN(e.Baseline.Value));
    }

    [Fact]
    public void LearningCurveConfidence_MatchesFromSamplesBands()
    {
        var e4 = BaselineEngine.ComputeWindow(Metrics.Steps, Flat(4), Epoch.AddDays(3), BaselineWindow.Days7);
        var e7 = BaselineEngine.ComputeWindow(Metrics.Steps, Flat(7), Epoch.AddDays(6), BaselineWindow.Days7);
        var e14 = BaselineEngine.ComputeWindow(Metrics.Steps, Flat(14), Epoch.AddDays(13), BaselineWindow.Days14);
        Assert.Equal(BaselineConfidence.Low, e4.Baseline.Confidence);
        Assert.Equal(BaselineConfidence.Medium, e7.Baseline.Confidence);   // 7–13 samples → Medium (FromSamples parity)
        Assert.Equal(BaselineConfidence.High, e14.Baseline.Confidence);
    }

    // ---- windowing / downgrade ------------------------------------------------

    [Fact]
    public void WhenMaxWindowFailsTheLargestEarnedWindowWins()
    {
        // 10 days of data cannot feed Days30 (gate 16) but earns Days14 (gate 8).
        var set = BaselineEngine.Compute(Flat(10), Epoch.AddDays(9));
        var e = set.Entry(Metrics.Steps)!;
        Assert.Equal(BaselineWindow.Days14, e.WindowUsed);
        Assert.Equal(10, e.SampleDays);
    }

    [Fact]
    public void RecordsOutsideTheWindowAreIgnored()
    {
        var history = Flat(10);
        history.Add(Day(100, steps: 999_999));  // far future — must not leak into any window
        var e = BaselineEngine.ComputeWindow(Metrics.Steps, history, Epoch.AddDays(9), BaselineWindow.Days7);
        Assert.Equal(8000, e.Baseline.Value);
    }

    // ---- statistics -------------------------------------------------------------

    [Fact]
    public void OutlierNight_MedianMetricBeatsMean_OnTheSameFixture()
    {
        // Six normal nights (450 min) + one wrecked 120-min night, newest-last.
        // Mean would sink to 402.86; the recency-weighted median must stay at 450.
        var history = new List<DailyHistoryRecord>
        {
            Day(0, sleep: 450), Day(1, sleep: 450), Day(2, sleep: 450),
            Day(3, sleep: 450), Day(4, sleep: 450), Day(5, sleep: 450),
            Day(6, sleep: 120),
        };
        var e = BaselineEngine.ComputeWindow(Metrics.SleepMinutes, history, Epoch.AddDays(6), BaselineWindow.Days7);
        Assert.True(e.Usable);
        Assert.Equal(450, e.Baseline.Value, 6);                       // median wins
        Assert.Equal((450.0 * 6 + 120) / 7, 402.857142857, 4);        // the mean it REFUSED to be
    }

    [Fact]
    public void RecencyWeightedMedian_DoubleWeightsTheNewestHalf()
    {
        // 4 samples: [100,100, 1,1] chronological — newest half (1,1) carries ×2.
        // Plain median = (1+100)/2 = 50.5; plain mean = 50.25; recent-weighted median = 1.
        Assert.Equal(1, BaselineEngine.RecencyWeightedMedian(new double[] { 100, 100, 1, 1 }), 6);
        Assert.Equal(100, BaselineEngine.RecencyWeightedMedian(new double[] { 1, 1, 100, 100 }), 6);
    }

    [Fact]
    public void MeanMetrics_GetThePlainMean_NotAMedian()
    {
        // Steps is a "mean otherwise" metric even with the same outlier shape.
        var history = new List<DailyHistoryRecord>
        {
            Day(0, steps: 8000), Day(1, steps: 8000), Day(2, steps: 8000),
            Day(3, steps: 8000), Day(4, steps: 10),
        };
        var e = BaselineEngine.ComputeWindow(Metrics.Steps, history, Epoch.AddDays(4), BaselineWindow.Days7);
        Assert.Equal(32010 / 5.0, e.Baseline.Value, 6);   // 6402 — the mean, outliers and all
    }

    [Fact]
    public void Bedtime_AveragesAcrossMidnight_AndUnwrapsViaBaselineService()
    {
        // Nights: 23:20, 23:30, 00:20(→next morning), all in a 7-day window.
        // Post-noon scale: 1400, 1410, 1460 → recency-weighted median picks 1410 → unwrap → 1410.
        var history = new List<DailyHistoryRecord>
        {
            Day(0, bedtime: 1400), Day(1, bedtime: 1410), Day(2, bedtime: 20),
        };
        var e = BaselineEngine.ComputeWindow(Metrics.BedtimeMinutes, history, Epoch.AddDays(2), BaselineWindow.Days7);
        // Gate: only 3 samples in the week — refuses. Widen to 8 so Days7 (gate 4) is met:
        var bigger = new List<DailyHistoryRecord>
        {
            Day(0, bedtime: 1400), Day(1, bedtime: 1400), Day(2, bedtime: 1400),
            Day(3, bedtime: 1400), Day(4, bedtime: 1410), Day(5, bedtime: 1460),
            Day(6, bedtime: 20),   // 00:20 — must NOT drag the average toward 20
        };
        Assert.False(e.Usable);
        var e2 = BaselineEngine.ComputeWindow(Metrics.BedtimeMinutes, bigger, Epoch.AddDays(6), BaselineWindow.Days7);
        Assert.True(e2.Usable);
        Assert.Equal(1410, e2.Baseline.Value, 6);
        Assert.Equal(BaselineService.UnwrapBedtime(1410), e2.Baseline.Value, 6);
        Assert.True(e2.Baseline.Value > 12 * 60);   // post-noon night scale, not a naive mean pulled toward noon
    }

    [Fact]
    public void DefaultZeroBedtimeCountsAsAbsent_NotMidnight()
    {
        // A date-only record row has BedtimeMinutesOfDay = 0 (never recorded).
        var withZero = new List<DailyHistoryRecord>
        {
            Day(0), Day(1), Day(2, bedtime: 0), Day(3, bedtime: 0), Day(4, bedtime: 0),
        };
        var e = BaselineEngine.ComputeWindow(Metrics.BedtimeMinutes, withZero, Epoch.AddDays(4), BaselineWindow.Days7);
        Assert.False(e.Usable);          // 2 real samples < gate 4 — refused, never midnight-guessed
        Assert.Equal(2, e.SampleDays);
    }

    [Fact]
    public void NaNSamplesAndNegativeValues_AreDiscardedLikeFromSamples()
    {
        var history = Flat(6);
        history.Add(new DailyHistoryRecord { Date = Epoch.AddDays(6), Origin = "x", SleepMinutes = double.NaN, Stress = -5 });
        var sleep = BaselineEngine.ComputeWindow(Metrics.SleepMinutes, history, Epoch.AddDays(6), BaselineWindow.Days7);
        Assert.Equal(6, sleep.SampleDays);                       // NaN night excluded
        var stress = BaselineEngine.ComputeWindow(Metrics.Stress, history, Epoch.AddDays(6), BaselineWindow.Days7);
        Assert.Equal(6, stress.SampleDays);                      // negative excluded (>= 0 rule)
    }

    // ---- result set shape ---------------------------------------------------------

    [Fact]
    public void BaselineSet_CoversEveryKnownMetric_AndLegacyDictionaryKeepsTheSeam()
    {
        var set = BaselineEngine.Compute(Flat(20), Epoch.AddDays(19));
        Assert.All(BaselineEngine.MetricKeys, k => Assert.NotNull(set.Entry(k)));
        var legacy = set.ToLegacyDictionary();
        Assert.Equal(BaselineEngine.MetricKeys.Count, legacy.Count);
        foreach (var k in BaselineEngine.MetricKeys)
            Assert.Same(set.Get(k)!, legacy[k]);                 // same objects — pure delegation
    }

    [Fact]
    public void WorstConfidence_IsNoneWhenAnyUsedMetricRefused()
    {
        var set = new BaselineSet(new[]
        {
            Usable(Metrics.SleepMinutes, 450, 14),
            Refused(Metrics.Steps, 2),
        });
        Assert.Equal(BaselineConfidence.None,
            set.WorstConfidence(new[] { Metrics.SleepMinutes, Metrics.Steps }));
        Assert.Equal(BaselineConfidence.High, set.WorstConfidence(new[] { Metrics.SleepMinutes }));
        // An unknown metric is also None — absence never reads as confidence.
        Assert.Equal(BaselineConfidence.None, set.WorstConfidence(new[] { "not.a.metric" }));
    }

    [Fact]
    public void Refusal_NeverProducesAUsableFlag()
    {
        var set = BaselineEngine.Compute(Flat(2), Epoch.AddDays(1));   // 2 days, all windows fail
        foreach (var key in BaselineEngine.MetricKeys)
        {
            var e = set.Entry(key)!;
            if (e.Usable) throw new Exception($"{key} claimed usable from 2 samples");
            Assert.True(double.IsNaN(e.Baseline.Value));
            Assert.Null(set.Value(key));
            Assert.Equal(BaselineConfidence.None, set.ConfidenceOf(key));
        }
    }
}
