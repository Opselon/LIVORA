using LIVORA.Application.Abstractions;
using LIVORA.Application.State;
using LIVORA.Application.State.Wave3b;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Wave 3b lane 05 — windowed 7/14/30 baselines, sample gates, state-confidence model and the
/// primary-window selector. The suite pins the honesty contract (insufficient data => null, never
/// a fake number), the layering contract (identical math to the 28-day engine on identical
/// samples), and determinism of the pure layers.
/// </summary>
public class Wave3bBaselineTests
{
    // ---- helpers -----------------------------------------------------------

    private static DailyHistoryRecord Day(int daysAgo, double sleep = 450, double? rhr = 60, double? hrv = 55) =>
        new()
        {
            Date = DateTime.Today.AddDays(-daysAgo),
            Origin = nameof(DataOrigin.Manual),
            Completeness = 1,
            SleepMinutes = sleep,
            SleepQuality = 0.8,
            SleepConsistency = 0.8,
            BedtimeMinutesOfDay = 1380,
            Steps = 8000,
            ActiveMinutes = 30,
            RecoveryScore = 0.7,
            RestingHeartRate = rhr,
            HrvMs = hrv,
            Stress = 0.9,
            Mood = 0.7,
            Energy = 0.4,
        };

    /// <summary>Contiguous real days covering ago..0 (inclusive), i.e. ago+1 records.</summary>
    private static FakeHistoryRepository History(int ago) =>
        new(Enumerable.Range(0, ago + 1).Select(d => Day(d)));

    /// <summary>Explicit set of real days (days-ago), for gap shapes coverage cannot fake.</summary>
    private static FakeHistoryRepository HistoryDays(params int[] daysAgo) =>
        new(daysAgo.Select(d => Day(d)));

    private static Baseline Bl(string metric, BaselineConfidence confidence) =>
        new() { MetricKey = metric, Value = 450, StdDev = 10, SampleDays = confidence switch
        {
            BaselineConfidence.High => 20, BaselineConfidence.Medium => 10,
            BaselineConfidence.Low => 5, _ => 0,
        }, Confidence = confidence };

    private static WindowedBaselineResult Synth(string metric,
        Baseline? w7, Baseline? w14, Baseline? w30,
        int n7 = 30, int n14 = 30, int n30 = 30) => new()
        {
            MetricKey = metric,
            ByWindow = new Dictionary<BaselineWindow, Baseline?>
            {
                [BaselineWindow.Days7] = w7,
                [BaselineWindow.Days14] = w14,
                [BaselineWindow.Days30] = w30,
            },
            SampleDaysByWindow = new Dictionary<BaselineWindow, int>
            {
                [BaselineWindow.Days7] = n7,
                [BaselineWindow.Days14] = n14,
                [BaselineWindow.Days30] = n30,
            },
        };

    private static Task<Dictionary<string, WindowedBaselineResult>> Run(IHistoryRepository history) =>
        new WindowedBaselineService(history).GetAllAsync();

    // ---- gate table (documented honesty) -----------------------------------

    [Fact]
    public void GateTable_MatchesDocumentedMinimums()
    {
        Assert.Equal(5, WindowedBaselineService.MinimumSamplesFor(BaselineWindow.Days7));
        Assert.Equal(10, WindowedBaselineService.MinimumSamplesFor(BaselineWindow.Days14));
        Assert.Equal(21, WindowedBaselineService.MinimumSamplesFor(BaselineWindow.Days30));
    }

    [Fact]
    public async Task MetricKeys_MirrorThe28DayEnginesExactKeySet()
    {
        // The lane may not edit BaselineService.cs, so the 12-metric list is mirrored here;
        // this pin breaks the build-time contract drift if the engine ever changes its set.
        var engine = await new BaselineService(History(3)).GetBaselinesAsync();
        Assert.Equal(
            engine.Keys.OrderBy(k => k, StringComparer.Ordinal),
            WindowedBaselineService.MetricKeys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(12, WindowedBaselineService.MetricKeys.Count);
    }

    // ---- sample-gate behavior (the mission's core honesty) ------------------

    [Fact]
    public async Task TwoDayHistory_EveryWindowForEveryMetric_IsNull()
    {
        // "no 30-day baseline from 2 days of data" — and not from 2 days of ANY window:
        // 2 < 5, so even the 7-day gate fails. Null everywhere, never a fake number.
        var all = await Run(History(1)); // 2 records
        foreach (var metric in WindowedBaselineService.MetricKeys)
        {
            var r = all[metric];
            foreach (var w in Wave3bWindows.Declared)
            {
                Assert.Null(r[w]);
                Assert.True(WindowedBaselineService.MinimumSamplesFor(w) > r.SampleDays(w));
            }
        }
    }

    [Fact]
    public async Task TwentyFiveDayHistory_ThirtyDayWindowNull_FourteenDayPresent()
    {
        var all = await Run(History(24)); // 25 real days: density would pass (25>=21),
        var sleep = all[Metrics.SleepMinutes];
        Assert.Null(sleep[BaselineWindow.Days30]);    // but coverage fails — history never spans 30d
        Assert.NotNull(sleep[BaselineWindow.Days14]); // 14 real days >= 10
        Assert.NotNull(sleep[BaselineWindow.Days7]);
    }

    [Fact]
    public async Task ThirtyOneDayHistory_ThirtyDayWindowAppears()
    {
        var all = await Run(History(30)); // 31 real days on the books
        var sleep = all[Metrics.SleepMinutes];
        Assert.NotNull(sleep[BaselineWindow.Days30]);
        // The window is the LAST 30 calendar days (today-29..today), same strict-edge semantics
        // as the 28-day engine: the 31st-oldest record sits outside it.
        Assert.Equal(30, sleep.SampleDays(BaselineWindow.Days30));
        Assert.Equal(BaselineConfidence.High, sleep[BaselineWindow.Days30]!.Confidence); // 30 >= 14 samples
    }

    [Fact]
    public async Task DensityGateBoundary_FourRecordsFailFiveRecordsPass_SevenDayWindow()
    {
        // Both sets span the full week (oldest = today-6) so coverage passes either way —
        // only the density gate differs: 4 < 5 fails, 5 >= 5 passes.
        var four = await Run(HistoryDays(0, 3, 5, 6));
        Assert.Null(four[Metrics.Steps][BaselineWindow.Days7]);
        Assert.Equal(4, four[Metrics.Steps].SampleDays(BaselineWindow.Days7));

        var five = await Run(HistoryDays(0, 2, 4, 5, 6));
        Assert.NotNull(five[Metrics.Steps][BaselineWindow.Days7]);
        Assert.Equal(5, five[Metrics.Steps].SampleDays(BaselineWindow.Days7));
    }

    [Fact]
    public async Task CoverageGate_HistorySpanningTwentyFiveDays_NullsThirtyWindowDespiteDensity()
    {
        // Documented two-gate shape: coverage alone can null a window whose density passes.
        // History from 24..0 spans 25 days: the 30-window counts 25 REAL days (>=21 density)
        // but the calendar never reached day 30 -> null; the 14-window passes both gates.
        var all = await Run(History(24));
        var sleep = all[Metrics.SleepMinutes];
        Assert.Equal(25, sleep.SampleDays(BaselineWindow.Days30)); // enough REAL days counted...
        Assert.Null(sleep[BaselineWindow.Days30]);                 // ...yet coverage nulls the window.
        Assert.NotNull(sleep[BaselineWindow.Days14]);
    }

    [Fact]
    public async Task SparseOptionals_CountOnlyDaysWithARealValue_PerMetric()
    {
        // RHR/HRV are nullable. With 7 records but only 5 carrying values, the 7-day gate
        // (>=5) is met EXACTLY for those metrics — while always-present steps still see 7.
        // The density gate counts valid samples per metric, never rows.
        var records = Enumerable.Range(0, 7)
            .Select(d => Day(d, rhr: d < 5 ? 60 : null, hrv: d < 5 ? 55 : null))
            .ToList();
        var all = await Run(new FakeHistoryRepository(records));
        Assert.Equal(5, all[Metrics.HrvMs].SampleDays(BaselineWindow.Days7));
        Assert.NotNull(all[Metrics.HrvMs][BaselineWindow.Days7]);
        Assert.NotNull(all[Metrics.RestingHeartRate][BaselineWindow.Days7]);
        Assert.Equal(7, all[Metrics.Steps].SampleDays(BaselineWindow.Days7));

        // One value fewer and the gate fails: 4 < 5 -> null, not a thinner baseline.
        var sparse = await Run(new FakeHistoryRepository(Enumerable.Range(0, 7)
            .Select(d => Day(d, rhr: d < 4 ? 60 : null, hrv: d < 4 ? 55 : null)).ToList()));
        Assert.Equal(4, sparse[Metrics.HrvMs].SampleDays(BaselineWindow.Days7));
        Assert.Null(sparse[Metrics.HrvMs][BaselineWindow.Days7]);
    }

    // ---- layering: identical math to the existing engine ---------------------

    [Fact]
    public async Task WindowedBaselines_AreIdenticalToFromSamples_OnTheSameSamples()
    {
        // 7-day window over exactly 7 records: every metric must equal what the engine's static
        // FromSamples produces from the same values (Value/StdDev/Confidence pinned).
        var history = History(6);
        var all = await Run(history);
        var records = await history.GetAllAsync();
        foreach (var metric in WindowedBaselineService.MetricKeys)
        {
            var samples = WindowedBaselineService.SamplesFor(metric, records);
            var expected = Baseline.FromSamples(metric, samples);
            var actual = all[metric][BaselineWindow.Days7]!;
            Assert.Equal(expected.Value, actual.Value, 9);
            Assert.Equal(expected.StdDev, actual.StdDev, 9);
            Assert.Equal(expected.Confidence, actual.Confidence);
            Assert.Equal(expected.SampleDays, actual.SampleDays);
        }
    }

    [Fact]
    public async Task WindowedThirtyDayWindow_MatchesThe28DayEngine_OnStableHistory()
    {
        // Layering check: with a fully-covered, stable history, the 30-day window and the shipped
        // 28-day engine must agree on Value/StdDev/Confidence (the engine's window is strictly
        // 28 calendar days, so its sample count differs by the two oldest records; equality here
        // is on the derived statistics over the user's steady history, which is what consumers see).
        var history = History(29); // 30 records: coverage passes for the 30-window
        var engine = await new BaselineService(history).GetBaselinesAsync();
        var all = await Run(history);
        foreach (var metric in WindowedBaselineService.MetricKeys)
        {
            var b28 = engine[metric];
            var b30 = all[metric][BaselineWindow.Days30]!;
            Assert.Equal(b28.Value, b30.Value, 9);
            Assert.Equal(b28.StdDev, b30.StdDev, 9);
            Assert.Equal(b28.Confidence, b30.Confidence);
        }
    }

    [Fact]
    public async Task BedtimeWrapAround_MatchesTheEngine_AndUnwrapSharesItsStatic()
    {
        // Mixed late-night (1430) and post-midnight (40) bedtimes: naive averaging would land
        // near 735 (noon) — nonsense. The wrap-around math must keep the average near midnight.
        var records = Enumerable.Range(0, 7).Select(d => new DailyHistoryRecord
        {
            Date = DateTime.Today.AddDays(-d), Origin = nameof(DataOrigin.Manual), Completeness = 1,
            SleepMinutes = 450, SleepQuality = .8, SleepConsistency = .8,
            BedtimeMinutesOfDay = d % 2 == 0 ? 1430 : 40,
            Steps = 8000, ActiveMinutes = 30, RecoveryScore = .7,
            RestingHeartRate = 60, HrvMs = 55, Stress = .9, Mood = .7, Energy = .4,
        }).ToList();
        var history = new FakeHistoryRepository(records);

        var engine = await new BaselineService(history).GetBaselinesAsync();
        var all = await Run(history);
        var expected = engine[Metrics.BedtimeMinutes];
        var actual = all[Metrics.BedtimeMinutes][BaselineWindow.Days7]!;
        Assert.Equal(expected.Value, actual.Value, 9);
        Assert.Equal(expected.StdDev, actual.StdDev, 9);
        // Unwrap path is the SAME static the engine exposes (one rule, not two).
        Assert.Equal(BaselineService.UnwrapBedtime(actual.Value), WindowedBaselineService.UnwrapBedtime(actual.Value), 9);
        Assert.InRange(WindowedBaselineService.UnwrapBedtime(actual.Value), 0, 1440);
        Assert.True(WindowedBaselineService.UnwrapBedtime(actual.Value) >= 1380
            || WindowedBaselineService.UnwrapBedtime(actual.Value) <= 90);
    }

    [Fact]
    public async Task Service_IsDeterministic_AndCachesPerDay()
    {
        var svc = new WindowedBaselineService(History(27));
        var a = await svc.GetAllAsync();
        var b = await svc.GetAllAsync();
        Assert.Same(a, b); // day-cache: same instance across calls until tomorrow
        var fresh = await new WindowedBaselineService(History(27)).GetAllAsync();
        foreach (var metric in WindowedBaselineService.MetricKeys)
        foreach (var w in Wave3bWindows.Declared)
            Assert.Equal(a[metric][w]?.Value ?? double.NaN, fresh[metric][w]?.Value ?? double.NaN);
    }

    // ---- primary-window selector --------------------------------------------

    [Fact]
    public void Selector_PrefersHighestTrustworthyWindow_NoFallbackTag()
    {
        var sel = new PrimaryWindowSelector().Select(
            Synth(Metrics.SleepMinutes, Bl(Metrics.SleepMinutes, BaselineConfidence.Medium),
                Bl(Metrics.SleepMinutes, BaselineConfidence.High), Bl(Metrics.SleepMinutes, BaselineConfidence.High)));
        Assert.Equal(BaselineWindow.Days30, sel.Window);
        Assert.Empty(sel.FallbackTags);
        Assert.Null(sel.NullReason);
    }

    [Fact]
    public void Selector_TagsStepDown_WhenThirtyDayWindowFails()
    {
        var sel = new PrimaryWindowSelector().Select(
            Synth(Metrics.Steps, Bl(Metrics.Steps, BaselineConfidence.Low),
                Bl(Metrics.Steps, BaselineConfidence.Medium), null));
        Assert.Equal(BaselineWindow.Days14, sel.Window);
        Assert.Contains("fallback:30->14", sel.FallbackTags);
    }

    [Fact]
    public void Selector_OnlySevenDayWindow_FallsBackStraightToSeven()
    {
        var sel = new PrimaryWindowSelector().Select(
            Synth(Metrics.Steps, Bl(Metrics.Steps, BaselineConfidence.Low), null, null));
        Assert.Equal(BaselineWindow.Days7, sel.Window);
        Assert.Contains("fallback:30->7", sel.FallbackTags);
    }

    [Fact]
    public void Selector_NoTrustworthyWindow_NullPlusReason()
    {
        var sel = new PrimaryWindowSelector().Select(
            Synth(Metrics.Steps, null, null, null, n7: 2, n14: 2, n30: 2));
        Assert.Null(sel.Window);
        Assert.Null(sel.Baseline);
        Assert.Equal("no_trustworthy_window", sel.NullReason);
    }

    [Fact]
    public async Task Selector_RealFourteenDayPrimary_WhenHistoryOnlyCoversFourteenDays()
    {
        // 15 real days: 30-window fails coverage -> the selector must step down to 14 and say so.
        var all = await Run(History(14));
        var sel = new PrimaryWindowSelector().Select(all[Metrics.SleepMinutes]);
        Assert.Equal(BaselineWindow.Days14, sel.Window);
        Assert.Contains("fallback:30->14", sel.FallbackTags);
    }

    // ---- state-confidence model ----------------------------------------------

    [Fact]
    public async Task Confidence_MissingData_DrivesShortageTagsAndNoneLevel()
    {
        var all = await Run(History(1)); // 2 days: every density gate fails
        var info = new StateConfidenceModel().Evaluate(all, completeness: 0.42);
        Assert.Equal(BaselineConfidence.None, info.Level);
        Assert.Contains($"{Metrics.SleepMinutes}:window30:{Wave3bTags.InsufficientSamples}", info.Drivers);
        Assert.Contains($"{Metrics.Steps}:window7:{Wave3bTags.InsufficientSamples}", info.Drivers);
        Assert.Contains($"{Metrics.Mood}:{Wave3bTags.NoTrustworthyWindow}", info.Drivers);
        Assert.Contains("completeness:0.42", info.Drivers);
        Assert.DoesNotContain(Wave3bTags.CompletenessWeakestLink, info.Drivers); // metrics already None
    }

    [Fact]
    public void Confidence_CompletenessIsTheWeakestLink()
    {
        var metricA = Synth(Metrics.SleepMinutes, Bl(Metrics.SleepMinutes, BaselineConfidence.High),
            Bl(Metrics.SleepMinutes, BaselineConfidence.High), Bl(Metrics.SleepMinutes, BaselineConfidence.High));
        var metricB = Synth(Metrics.Steps, Bl(Metrics.Steps, BaselineConfidence.High),
            Bl(Metrics.Steps, BaselineConfidence.High), Bl(Metrics.Steps, BaselineConfidence.High));
        var baselines = new Dictionary<string, WindowedBaselineResult>
        {
            [Metrics.SleepMinutes] = metricA,
            [Metrics.Steps] = metricB,
        };
        var info = new StateConfidenceModel().Evaluate(baselines, completeness: 0.6);
        Assert.Equal(BaselineConfidence.Medium, info.Level); // both metrics High, completeness drags
        Assert.Contains(Wave3bTags.CompletenessWeakestLink, info.Drivers);
        Assert.DoesNotContain(Wave3bTags.NoTrustworthyWindow, info.Drivers);
    }

    [Fact]
    public void Confidence_WeakestMetricCapsTheChain_EvenWithFullCompleteness()
    {
        var strong = Synth(Metrics.SleepMinutes, Bl(Metrics.SleepMinutes, BaselineConfidence.High),
            Bl(Metrics.SleepMinutes, BaselineConfidence.High), Bl(Metrics.SleepMinutes, BaselineConfidence.High));
        // Steps: only the 7-day window trusted (Low); 14/30 lack samples -> shortage tags,
        // but the metric DOES have a trustworthy window, so its link is Low, not None.
        var weak = Synth(Metrics.Steps, Bl(Metrics.Steps, BaselineConfidence.Low), null, null, n7: 7, n14: 5, n30: 5);
        var info = new StateConfidenceModel().Evaluate(
            new Dictionary<string, WindowedBaselineResult>
            {
                [Metrics.SleepMinutes] = strong,
                [Metrics.Steps] = weak,
            }, completeness: 1.0);
        Assert.Equal(BaselineConfidence.Low, info.Level); // weakest link, not the average
        Assert.DoesNotContain($"{Metrics.Steps}:{Wave3bTags.NoTrustworthyWindow}", info.Drivers);
        Assert.Contains($"{Metrics.Steps}:window14:{Wave3bTags.InsufficientSamples}", info.Drivers);
        Assert.Contains($"{Metrics.Steps}:window30:{Wave3bTags.InsufficientSamples}", info.Drivers);
    }

    [Fact]
    public void Confidence_EmptyInput_IsNoneWithNoMetricsTag()
    {
        var info = new StateConfidenceModel().Evaluate(
            new Dictionary<string, WindowedBaselineResult>(), completeness: 0.9);
        Assert.Equal(BaselineConfidence.None, info.Level);
        Assert.Contains(Wave3bTags.NoMetrics, info.Drivers);
    }

    [Theory]
    [InlineData(1.0, BaselineConfidence.High)]
    [InlineData(0.8, BaselineConfidence.High)]      // inclusive threshold
    [InlineData(0.79, BaselineConfidence.Medium)]
    [InlineData(0.5, BaselineConfidence.Medium)]
    [InlineData(0.49, BaselineConfidence.Low)]
    [InlineData(0.25, BaselineConfidence.Low)]
    [InlineData(0.24, BaselineConfidence.None)]
    [InlineData(0.0, BaselineConfidence.None)]
    [InlineData(-1.0, BaselineConfidence.None)]      // clamped
    [InlineData(2.0, BaselineConfidence.High)]       // clamped
    public void Confidence_CompletenessLadder(double completeness, BaselineConfidence expected) =>
        Assert.Equal(expected, StateConfidenceModel.LevelFromCompleteness(completeness));

    [Fact]
    public async Task Confidence_IsDeterministic_SameInputsIdenticalDrivers()
    {
        var all = await Run(History(24));
        var model = new StateConfidenceModel();
        var first = model.Evaluate(all, 0.42);
        var second = model.Evaluate(all, 0.42);
        Assert.Equal(first.Level, second.Level);
        Assert.Equal(first.Drivers, second.Drivers); // order included — machine tags, stable grammar
    }
}
