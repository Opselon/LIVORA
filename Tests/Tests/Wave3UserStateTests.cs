using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.HealthData;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;
using StateMetrics = LIVORA.Domain.Models.State.Metrics;

namespace LIVORA.Tests.Tests;

/// <summary>
/// <see cref="UserStateService"/> is the class every honest claim in the app is derived from, and
/// until Wave 3 it had zero direct coverage. These tests pin the derivation rules the README
/// advertises: focus is derived (never measured), provenance survives derivation, confidence is a
/// chain as strong as its weakest link, and repeated/c Concurrent loads behave as documented.
///
/// "Today" is deliberately the real <see cref="DateTime.Today"/>: the service mixes an injected
/// clock (for GeneratedAt / the provider day) with the process clock inside
/// <c>SleepDaysSinceFresh</c>, so a fixed historical date would make the staleness assertions
/// measure the gap to the real wall clock instead of the fixture.
/// </summary>
public class UserStateServiceTests
{
    private static readonly DateTime Today = DateTime.Today;

    private sealed class DayProvider : IDataProvider
    {
        private readonly Func<DateTime, NormalizedDay?> _select;
        private readonly TimeSpan? _delay;
        public int Calls;

        public DayProvider(Func<DateTime, NormalizedDay?> select, TimeSpan? delay = null)
        {
            _select = select;
            _delay = delay;
        }

        public string Id => "test.provider";
        public LIVORA.Domain.Enums.SourceType SourceType => LIVORA.Domain.Enums.SourceType.Real;
        public ConnectionState State => ConnectionState.Connected;
        public string DisplayNameKey => "Profile.DataSource.Sample";
        public DataOrigin Origin => DataOrigin.Manual;
        public DataSourceCapabilities Capabilities => DataSourceCapabilities.Sleep | DataSourceCapabilities.Steps;

        public async Task<NormalizedDay?> GetNormalizedDayAsync(DateTime date, UserProfile profile, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (_delay is { } d) await Task.Delay(d, ct);
            return _select(date);
        }
    }

    private static NormalizedDay Day(DateTime date,
        double sleep = 450, double quality = 0.8, double stress = 0.4, double energy = 0.7,
        int steps = 8000, DataOrigin origin = DataOrigin.Mock, DataQuality sleepQuality = DataQuality.Complete,
        DateTime? sleepTimestamp = null, DataPoint? rhr = null, DataPoint? hrv = null) => new()
    {
        Date = date,
        Origin = origin,
        SleepMinutes = new DataPoint { Value = sleep, Timestamp = sleepTimestamp ?? date.AddHours(9), Quality = sleepQuality, Origin = origin },
        SleepQuality = new DataPoint { Value = quality, Timestamp = date.AddHours(9), Quality = DataQuality.Complete, Origin = origin },
        SleepConsistency = new DataPoint { Value = 0.8, Timestamp = date.AddHours(9), Quality = DataQuality.Complete, Origin = origin },
        BedtimeMinutesOfDay = new DataPoint { Value = 1380, Timestamp = date.AddHours(9), Quality = DataQuality.Complete, Origin = origin },
        WakeMinutesOfDay = new DataPoint { Value = 420, Timestamp = date.AddHours(9), Quality = DataQuality.Complete, Origin = origin },
        Steps = new DataPoint { Value = steps, Timestamp = date.AddHours(17), Quality = DataQuality.Complete, Origin = origin },
        ActiveMinutes = new DataPoint { Value = 30, Timestamp = date.AddHours(17), Quality = DataQuality.Complete, Origin = origin },
        RecoveryScore = new DataPoint { Value = 0.7, Timestamp = date.AddHours(8), Quality = DataQuality.Complete, Origin = origin },
        Stress = new DataPoint { Value = stress, Timestamp = date.AddHours(17), Quality = DataQuality.Complete, Origin = origin },
        Mood = new DataPoint { Value = 0.7, Timestamp = date.AddHours(17), Quality = DataQuality.Complete, Origin = origin },
        Energy = new DataPoint { Value = energy, Timestamp = date.AddHours(17), Quality = DataQuality.Complete, Origin = origin },
        RestingHeartRate = rhr,
        HrvMs = hrv,
    };

    private static (UserStateService Svc, SessionState Session) Build(
        IDataProvider provider, IHistoryRepository history, IRepository<Habit>? habits = null,
        IRepository<Goal>? goals = null)
    {
        var session = new SessionState { CurrentProfile = new UserProfile { Id = "user-1" } };
        var svc = new UserStateService(
            provider,
            new DataNormalizer(),
            new BaselineService(history),
            history,
            session,
            new FakeClock(Today),
            habits ?? new InMemoryRepo<Habit>(),
            goals ?? new InMemoryRepo<Goal>(),
            new TrendService());
        return (svc, session);
    }

    private static IHistoryRepository History(int days = 20) =>
        new FakeHistoryRepository(Enumerable.Range(1, days).Select(i => new DailyHistoryRecord
        {
            Date = Today.AddDays(-i),
            Origin = nameof(DataOrigin.Mock),
            Completeness = 1,
            SleepMinutes = 450,
            SleepQuality = 0.8,
            SleepConsistency = 0.8,
            BedtimeMinutesOfDay = 1380,
            Steps = 8000,
            ActiveMinutes = 30,
            RecoveryScore = 0.7,
            Stress = 0.4,
            Mood = 0.7,
            Energy = 0.7,
        }));

    // ---- focus honesty ----------------------------------------------------

    [Fact]
    public async Task Focus_IsLabeledDerived_NeverMeasured()
    {
        var (svc, _) = Build(new DayProvider(d => Day(d)), History());
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);

        Assert.True(state.Focus.IsDerived, "no focus sensor exists — the state must say so");
        Assert.Equal(DataQuality.Estimated, state.Focus.Estimated.Quality);
        Assert.Equal(BaselineConfidence.None, state.Focus.Estimated.BaselineConfidence);
        Assert.Null(state.Focus.Estimated.BaselineValue);   // no personal history for a derived signal
        Assert.Equal(StateLevel.Unknown, state.Focus.Estimated.Level);
        Assert.True(state.Metrics.ContainsKey(StateMetrics.FocusEstimate));
    }

    [Fact]
    public async Task Focus_EstimateIsTheDocumentedWeightedBlend()
    {
        // 0.45*energy + 0.35*(1-stress) + 0.20*sleepQuality — pinned so a future "tweak" is a
        // deliberate decision with a failing test, not an accident.
        var (svc, _) = Build(new DayProvider(d => Day(d, energy: 0.6, stress: 0.2, quality: 0.8)), History());
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);
        Assert.Equal(0.45 * 0.6 + 0.35 * (1 - 0.2) + 0.20 * 0.8, state.Focus.Estimated.Value, 6);
    }

    [Fact]
    public async Task Focus_NeverEscapesItsZeroToOneRange_WhateverTheInputs()
    {
        foreach (var (energy, stress, quality) in new[] { (0.0, 1.0, 0.0), (1.0, 0.0, 1.0), (0.5, 0.5, 0.5) })
        {
            var (svc, _) = Build(new DayProvider(d => Day(d, energy: energy, stress: stress, quality: quality)), History());
            var state = await svc.GetStateAsync(DataRefreshMode.ManualRefresh);
            Assert.InRange(state.Focus.Estimated.Value, 0, 1);
        }
    }

    // ---- provenance propagation ------------------------------------------

    [Fact]
    public async Task MetricProvenance_TravelsFromProviderToState()
    {
        // The honesty model depends on origin surviving derivation: if a Manual day quietly became
        // Mock, every "self-reported" label in the UI would be lying.
        var (svc, _) = Build(new DayProvider(d => Day(d, origin: DataOrigin.Manual)), History());
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);

        Assert.Equal(DataOrigin.Manual, state.Metrics[StateMetrics.SleepMinutes].Origin);
        Assert.Equal(DataOrigin.Manual, state.Metrics[StateMetrics.Steps].Origin);
        Assert.Equal(DataOrigin.Manual, state.Focus.Estimated.Origin);
    }

    [Fact]
    public async Task MissingProviderDay_DoesNotFabricateValues()
    {
        // Provider returns null for the day: the engine falls back to an empty day whose
        // completeness is 0 — never to a plausible-looking zero (0 steps is a real, alarming value).
        var (svc, _) = Build(new DayProvider(_ => null), History(days: 0));
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);

        Assert.Equal(0, state.DataCompleteness);
        Assert.True(double.IsNaN(state.Metrics[StateMetrics.SleepMinutes].Value));
        Assert.Equal(DataQuality.Missing, state.Metrics[StateMetrics.SleepMinutes].Quality);
        Assert.Equal(StateLevel.Unknown, state.Metrics[StateMetrics.SleepMinutes].Level);
        Assert.Equal(0, state.Confidence);   // nothing usable => zero confidence, honestly
    }

    [Fact]
    public async Task DeviationAgainstBaseline_IsRelativeNotAbsolute()
    {
        var (svc, _) = Build(new DayProvider(d => Day(d, sleep: 360)), History());
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);
        var sleep = state.Metrics[StateMetrics.SleepMinutes];

        Assert.Equal(450, sleep.BaselineValue!.Value, 3);
        Assert.Equal((360 - 450) / 450.0, sleep.RelativeDeviation!.Value, 4);
        Assert.Equal(StateLevel.BelowBaseline, sleep.Level);
    }

    [Fact]
    public async Task InvertedPolarity_IsRespectedForStress()
    {
        // Stress above baseline is bad. If HigherIsBetter leaked through as true, every stress
        // spike would render as an improvement.
        var (svc, _) = Build(new DayProvider(d => Day(d, stress: 0.9)), History());
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);
        var stress = state.Metrics[StateMetrics.Stress];

        Assert.False(stress.HigherIsBetter);
        Assert.True(stress.SignedBadness > 0);
        Assert.Equal(StateLevel.AboveBaseline, stress.Level);
    }

    [Fact]
    public async Task OptionalDeviceSignals_AreAbsentNotZero()
    {
        var (svc, _) = Build(new DayProvider(d => Day(d)), History());
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);

        Assert.False(state.Metrics.ContainsKey(StateMetrics.RestingHeartRate));
        Assert.False(state.Metrics.ContainsKey(StateMetrics.HrvMs));
        Assert.Null(state.Recovery.RestingHeartRate);
        Assert.Null(state.Recovery.Hrv);
    }

    [Fact]
    public async Task PresentDeviceSignals_AreDerivedWithTheRightPolarity()
    {
        var (svc, _) = Build(new DayProvider(d => Day(d,
            rhr: new DataPoint { Value = 52, Timestamp = d.AddHours(7), Quality = DataQuality.Complete },
            hrv: new DataPoint { Value = 60, Timestamp = d.AddHours(7), Quality = DataQuality.Complete })),
            History(days: 0));
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);

        Assert.True(state.Metrics.ContainsKey(StateMetrics.RestingHeartRate));
        Assert.False(state.Metrics[StateMetrics.RestingHeartRate].HigherIsBetter); // lower RHR is better
        Assert.True(state.Metrics[StateMetrics.HrvMs].HigherIsBetter);             // higher HRV is better
    }

    // ---- confidence -------------------------------------------------------

    [Fact]
    public async Task Confidence_RisesWithBaselineCoverage_AndStaysInRange()
    {
        var noHistory = await Build(new DayProvider(d => Day(d)), History(days: 0)).Svc
            .GetStateAsync(DataRefreshMode.InitialLoad);
        var withHistory = await Build(new DayProvider(d => Day(d)), History(days: 20)).Svc
            .GetStateAsync(DataRefreshMode.ManualRefresh);

        Assert.Equal(0, noHistory.Confidence);
        Assert.True(withHistory.Confidence > noHistory.Confidence);
        Assert.InRange(withHistory.Confidence, 0, 1);
    }

    [Fact]
    public async Task Confidence_DoesNotRiseWhenTheFeedGoesStale()
    {
        // Same numbers, but the sleep reading is 6 days old: the normalizer downgrades it to Stale
        // and confidence must not stay as high as a fresh day's.
        var fresh = await Build(new DayProvider(d => Day(d)), History(days: 20)).Svc
            .GetStateAsync(DataRefreshMode.InitialLoad);
        var stale = await Build(new DayProvider(d => Day(d, sleepTimestamp: Today.AddDays(-6).AddHours(9))), History(days: 20)).Svc
            .GetStateAsync(DataRefreshMode.ManualRefresh);

        Assert.Equal(6, stale.Sleep.DaysSinceFreshData);
        Assert.Equal(DataQuality.Stale, stale.Metrics[StateMetrics.SleepMinutes].Quality);
        Assert.True(stale.Confidence < fresh.Confidence, "stale data may not claim the same confidence");
    }

    // ---- snapshots --------------------------------------------------------

    [Fact]
    public async Task HabitAndGoalSnapshots_AreDerivedFromTheRepositories()
    {
        var walk = new Habit { Id = "h1", Name = "Walk" };
        for (int i = 0; i < 5; i++) walk.Complete(Today.AddDays(-i).AddHours(7));
        var goals = new InMemoryRepo<Goal>(new[]
        {
            new Goal { Id = "g1", Name = "Sleep", TargetValue = 10, ProgressValue = 5 },
            new Goal { Id = "g2", Name = "Archived", TargetValue = 10, ProgressValue = 10, IsArchived = true },
        });

        var (svc, _) = Build(new DayProvider(d => Day(d)), History(), new InMemoryRepo<Habit>(new[] { walk }), goals);
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);

        Assert.Single(state.HabitSnapshots);
        Assert.Equal(5, state.HabitSnapshots[0].Streak);
        Assert.Equal(5 / 30.0, state.HabitSnapshots[0].SuccessRate30d, 4);
        Assert.NotNull(state.HabitSnapshots[0].BestCompletionWindow);

        // Archived goals are excluded: counting them would inflate "how are you doing".
        var snap = Assert.Single(state.GoalSnapshots);
        Assert.Equal("g1", snap.GoalId);
        Assert.Equal(0.5, snap.Fraction, 3);
    }

    [Fact]
    public async Task StateUpdated_FiresOncePerCompute()
    {
        var (svc, _) = Build(new DayProvider(d => Day(d)), History());
        var fired = 0;
        svc.StateUpdated += _ => fired++;

        await svc.GetStateAsync(DataRefreshMode.ManualRefresh);
        await svc.GetStateAsync(DataRefreshMode.ManualRefresh);
        Assert.Equal(2, fired);
    }

    [Fact]
    public async Task RepeatedInitialLoad_ReusesTheCachedState()
    {
        // Documented de-dupe: same mode, same day => no second walk through the pipeline.
        var provider = new DayProvider(d => Day(d));
        var (svc, _) = Build(provider, History());

        var a = await svc.GetStateAsync(DataRefreshMode.InitialLoad);
        var b = await svc.GetStateAsync(DataRefreshMode.InitialLoad);
        Assert.Same(a, b);
        Assert.Equal(1, provider.Calls);

        await svc.GetStateAsync(DataRefreshMode.ManualRefresh);
        Assert.Equal(2, provider.Calls);   // an explicit refresh must actually refresh
    }

    [Fact]
    public async Task ConcurrentSameModeRequests_FromAnotherThread_CoalesceIntoOneCompute()
    {
        // The in-flight guard exists so a language switch (which fires LoadAsync on every open VM)
        // walks the pipeline once. Two facts make this test deliberately thread-pinned: the starter
        // arms the reentrancy guard for ITS OWN thread, so a second caller on the same thread must
        // compute inline (see the next test) — only a caller from another thread may join.
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var provider = new SignallingProvider(d => Day(d), entered, release);
        var (svc, _) = Build(provider, History());

        PersonalState? first = null;
        var starter = new Thread(async () =>
        {
            first = await svc.GetStateAsync(DataRefreshMode.Resume);
        });
        starter.IsBackground = true;
        starter.Start();

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "provider never entered");
        var joinedTask = svc.GetStateAsync(DataRefreshMode.Resume);   // this thread may join
        release.Set();
        starter.Join(TimeSpan.FromSeconds(5));
        var joined = await joinedTask;

        Assert.Equal(1, provider.Calls);
        Assert.NotNull(first);
        Assert.Same(first, joined);
    }

    [Fact]
    public async Task ReentrantCallFromInsideACompute_CompletesInsteadOfDeadlocking()
    {
        // The hazard the guard's comment names: a synchronous StateUpdated subscriber (the real app
        // has several VMs) that asks for state again while a compute is in flight on its own stack
        // must NOT be handed the task it is itself blocking on. It gets a fresh inline compute
        // instead — exactly the pre-guard sequential behavior, so it cannot hang.
        var (svc, _) = Build(new DayProvider(d => Day(d)), History());
        Task<PersonalState>? nested = null;
        var reentered = false;
        svc.StateUpdated += _ =>
        {
            if (reentered) return;                    // once: the nested compute raises it too
            reentered = true;
            nested = svc.GetStateAsync(DataRefreshMode.Resume);
        };

        var outer = await svc.GetStateAsync(DataRefreshMode.InitialLoad);
        Assert.NotNull(outer);
        Assert.NotNull(nested);

        var winner = await Task.WhenAny(nested!, Task.Delay(2000));
        Assert.Same(nested!, winner);                       // completed => no self-deadlock
        Assert.NotSame(outer, await nested!);               // computed inline, not joined
    }

    private sealed class SignallingProvider : IDataProvider
    {
        private readonly SampleHealthProvider _inner = new();
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        public int Calls;

        public SignallingProvider(Func<DateTime, NormalizedDay?> _, ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public string Id => "test.provider";
        public LIVORA.Domain.Enums.SourceType SourceType => LIVORA.Domain.Enums.SourceType.Real;
        public ConnectionState State => ConnectionState.Connected;
        public string DisplayNameKey => "Profile.DataSource.Sample";
        public DataOrigin Origin => DataOrigin.Manual;
        public DataSourceCapabilities Capabilities => DataSourceCapabilities.Sleep;

        public Task<NormalizedDay?> GetNormalizedDayAsync(DateTime date, UserProfile profile, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(5));
            return _inner.GetNormalizedDayAsync(date, profile, ct);
        }
    }


    [Fact]
    public async Task GeneratedAtAndCompleteness_AreCarriedOntoTheState()
    {
        var (svc, _) = Build(new DayProvider(d => Day(d)), History());
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);

        Assert.Equal(Today.AddHours(10), state.GeneratedAt);
        Assert.Equal(1.0, state.DataCompleteness, 6);
        Assert.Equal(0, state.Sleep.DaysSinceFreshData);
    }

    [Fact]
    public async Task StaleGuard_IsVisibleToRulesThroughTheRealPipeline()
    {
        // End-to-end honesty: a 6-day-old sleep feed must surface the stale rule and never the
        // "go to bed earlier" advice that a naive reading of the number would produce.
        var (svc, _) = Build(new DayProvider(d => Day(d, sleep: 330, sleepTimestamp: Today.AddDays(-6).AddHours(9))), History());
        var state = await svc.GetStateAsync(DataRefreshMode.InitialLoad);
        var fired = new Application.Rules.RuleEngine().Evaluate(state, new UserProfile(),
            Array.Empty<Goal>(), Array.Empty<Habit>(), Today.AddHours(19));

        Assert.Contains(fired, r => r.RuleKey == "Rule.StaleSleep");
        Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.SleepDebt");
    }
}

/// <summary>
/// BaselineService specifics the whole derivation depends on: the wrap-around bedtime rule, the
/// confidence gates, the 28-day window and the "sync accessor stays silent" contract.
/// </summary>
public class BaselineServiceWave3Tests
{
    private static readonly DateTime Today = DateTime.Today;

    private static DailyHistoryRecord Rec(DateTime date, double bedtimeMinutes, double sleep = 450) => new()
    {
        Date = date, Origin = nameof(DataOrigin.Mock), Completeness = 1,
        SleepMinutes = sleep, SleepQuality = 0.8, SleepConsistency = 0.8,
        BedtimeMinutesOfDay = bedtimeMinutes, Steps = 8000, ActiveMinutes = 30,
        RecoveryScore = 0.7, Stress = 0.4, Mood = 0.7, Energy = 0.7,
    };

    private static BaselineService Service(IEnumerable<DailyHistoryRecord> records) =>
        new(new FakeHistoryRepository(records));

    [Fact]
    public async Task Bedtime_AcrossMidnight_AveragesToTheNightNotToNoon()
    {
        // 23:50 (1430) and 00:40 (40) averaged naively is 735 = 12:15 — "you go to bed at noon".
        // Wrap-around shifts early-morning bedtimes onto the previous night before averaging.
        var records = new[]
        {
            Rec(Today.AddDays(-1), 1430), Rec(Today.AddDays(-2), 1430),
            Rec(Today.AddDays(-3), 40), Rec(Today.AddDays(-4), 40),
        };
        var baselines = await Service(records).GetBaselinesAsync();
        var bedtime = baselines[StateMetrics.BedtimeMinutes];

        Assert.Equal(4, bedtime.SampleDays);
        Assert.Equal(1455, bedtime.Value, 3);                          // 00:15 on the shifted scale
        Assert.Equal(15, BaselineService.UnwrapBedtime(bedtime.Value), 3);
    }

    [Theory]
    [InlineData(1480, 40)]
    [InlineData(1430, 1430)]
    [InlineData(1440, 0)]
    [InlineData(2880, 1440)]
    public void UnwrapBedtime_ReturnsToTheClockFace(double shifted, double expected)
        => Assert.Equal(expected, BaselineService.UnwrapBedtime(shifted), 3);

    [Fact]
    public async Task SyncAccessor_IsSilentUntilTheFirstAsyncLoad()
    {
        var svc = Service(Enumerable.Range(1, 15).Select(i => Rec(Today.AddDays(-i), 1380)));
        Assert.Null(svc.Get(StateMetrics.SleepMinutes));      // no async load yet => no claim to make
        await svc.GetBaselinesAsync();
        Assert.NotNull(svc.Get(StateMetrics.SleepMinutes));
    }

    [Fact]
    public async Task ConfidenceNoneMetrics_AreHiddenFromTheSyncAccessor()
    {
        // Two days of history: FromSamples says None, and Get() must not hand that to a rule that
        // would then act on a "baseline" nobody has.
        var svc = Service(new[] { Rec(Today.AddDays(-1), 1380), Rec(Today.AddDays(-2), 1380) });
        var all = await svc.GetBaselinesAsync();
        Assert.Equal(BaselineConfidence.None, all[StateMetrics.SleepMinutes].Confidence);
        Assert.Null(svc.Get(StateMetrics.SleepMinutes));
    }

    [Fact]
    public async Task WindowDropsRecordsOlderThan28Days()
    {
        var inside = Enumerable.Range(1, 20).Select(i => Rec(Today.AddDays(-i), 1380, sleep: 400));
        var outside = Enumerable.Range(29, 20).Select(i => Rec(Today.AddDays(-i), 1380, sleep: 900));
        var baselines = await Service(inside.Concat(outside)).GetBaselinesAsync();

        Assert.Equal(20, baselines[StateMetrics.SleepMinutes].SampleDays);
        Assert.Equal(400, baselines[StateMetrics.SleepMinutes].Value, 3);   // the 900s never entered
    }

    [Fact]
    public async Task NegativeSamples_AreExcludedFromBaselines()
    {
        // Valid() filters v >= 0: a negative reading must not drag a mean down.
        var records = Enumerable.Range(1, 8).Select(i => Rec(Today.AddDays(-i), 1380, sleep: i == 1 ? -100 : 450));
        var baselines = await Service(records).GetBaselinesAsync();

        Assert.Equal(7, baselines[StateMetrics.SleepMinutes].SampleDays);
        Assert.Equal(450, baselines[StateMetrics.SleepMinutes].Value, 3);
    }

    [Theory]
    [InlineData(2, BaselineConfidence.None)]
    [InlineData(3, BaselineConfidence.Low)]
    [InlineData(6, BaselineConfidence.Low)]
    [InlineData(7, BaselineConfidence.Medium)]
    [InlineData(13, BaselineConfidence.Medium)]
    [InlineData(14, BaselineConfidence.High)]
    [InlineData(20, BaselineConfidence.High)]
    public async Task ConfidenceGates_MatchTheDocumentedSampleCounts(int days, BaselineConfidence expected)
    {
        var svc = Service(Enumerable.Range(1, days).Select(i => Rec(Today.AddDays(-i), 1380)));
        var baselines = await svc.GetBaselinesAsync();
        Assert.Equal(expected, baselines[StateMetrics.SleepMinutes].Confidence);
    }

    [Fact]
    public async Task BaselinesAreCachedPerDay_AndEveryMetricKeyIsPresent()
    {
        var svc = Service(Enumerable.Range(1, 15).Select(i => Rec(Today.AddDays(-i), 1380)));
        var first = await svc.GetBaselinesAsync();
        var second = await svc.GetBaselinesAsync();
        Assert.Same(first, second);            // same day => cached dictionary

        string[] expectedKeys =
        {
            StateMetrics.SleepMinutes, StateMetrics.SleepQuality, StateMetrics.SleepConsistency,
            StateMetrics.BedtimeMinutes, StateMetrics.Steps, StateMetrics.ActiveMinutes,
            StateMetrics.RecoveryScore, StateMetrics.RestingHeartRate, StateMetrics.HrvMs,
            StateMetrics.Stress, StateMetrics.Mood, StateMetrics.Energy,
        };
        Assert.All(expectedKeys, k => Assert.True(first.ContainsKey(k), $"missing baseline for {k}"));
    }

    [Fact]
    public async Task EmptyHistory_YieldsNoneConfidenceBaselines_NotCrashes()
    {
        var baselines = await Service(Array.Empty<DailyHistoryRecord>()).GetBaselinesAsync();
        var sleep = baselines[StateMetrics.SleepMinutes];
        Assert.Equal(BaselineConfidence.None, sleep.Confidence);
        Assert.Equal(0, sleep.SampleDays);
        Assert.Equal(0, sleep.Value);
    }
}
