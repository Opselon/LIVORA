using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.State;
/// <summary>
/// THE user-state engine (Wave 2 core): provider data -> normalized -> compared against the
/// user's personal baseline -> derived PersonalState. UI and rules consume the derived state;
/// nothing downstream touches raw provider values directly.
/// </summary>
public sealed class UserStateService : IUserStateService
{
    private readonly IDataProvider _provider;
    private readonly IDataNormalizer _normalizer;
    private readonly IBaselineService _baselines;
    private readonly IHistoryRepository _history;
    private readonly SessionState _session;
    private readonly IDateTimeProvider _clock;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Goal> _goals;
    private readonly ITrendService _trends;

    public event Action<PersonalState>? StateUpdated;

    private PersonalState? _last;
    private DataRefreshMode _lastMode;

    // ---- In-flight coalescing -------------------------------------------------
    // A language switch fires LoadAsync on several VMs at once and every one of them walks the
    // whole pipeline (history ensure-load -> provider day -> baselines -> 12 metric states ->
    // habit/goal snapshots). While a compute for a given mode is still running, another request
    // for the SAME mode joins it instead of starting a second identical walk: same mode => same
    // inputs => the same PersonalState, so joining cannot change any result. Different modes are
    // never merged — mode is what gates the cheap InitialLoad re-use below, and mixing it across
    // modes would change how much later calls recompute. (No caller passes a non-default
    // CancellationToken today; a joined caller shares the starter's token semantics.)
    // In Phase 2 every stage completes synchronously, so this coalesces nothing yet — it is the
    // guard that keeps the stampede safe the day a real async provider or DB replaces the mocks.
    private readonly object _computeGate = new();
    private DataRefreshMode _inFlightMode;
    private TaskCompletionSource<PersonalState>? _inFlight;

    public UserStateService(
        IDataProvider provider,
        IDataNormalizer normalizer,
        IBaselineService baselines,
        IHistoryRepository history,
        SessionState session,
        IDateTimeProvider clock,
        IRepository<Habit> habits,
        IRepository<Goal> goals,
        ITrendService trends)
    {
        _provider = provider;
        _normalizer = normalizer;
        _baselines = baselines;
        _history = history;
        _session = session;
        _clock = clock;
        _habits = habits;
        _goals = goals;
        _trends = trends;
    }

    public Task<PersonalState> GetStateAsync(DataRefreshMode mode, CancellationToken ct = default)
    {
        // Cheap de-dupe: manual/resume refresh recomputes; repeated InitialLoad reuses.
        if (_last is not null && mode == DataRefreshMode.InitialLoad && _lastMode == mode
            && _last.GeneratedAt.Date == _clock.Today)
            return Task.FromResult(_last);

        Task<PersonalState> shared;
        lock (_computeGate)
            shared = _inFlightMode == mode && _inFlight is { } inflight
                ? inflight.Task
                : StartLocked(mode, ct);
        return shared;
    }

    /// <summary>
    /// Registers the in-flight compute BEFORE its first await, so callers that arrive while the
    /// pipeline is running join it instead of starting a second identical walk. The completion-time
    /// _last/_lastMode stamping stays exactly where the original code had it (end of ComputeAsync),
    /// so error paths behave as before.
    /// </summary>
    private Task<PersonalState> StartLocked(DataRefreshMode mode, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PersonalState>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlight = tcs;
        _inFlightMode = mode;
        _ = CompleteComputeAsync(tcs, mode, ct);
        return tcs.Task;
    }

    private async Task CompleteComputeAsync(TaskCompletionSource<PersonalState> tcs, DataRefreshMode mode, CancellationToken ct)
    {
        PersonalState? result = null;
        try
        {
            result = await ComputeAsync(mode, ct);
            tcs.TrySetResult(result);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            lock (_computeGate)
                if (ReferenceEquals(_inFlight, tcs))
                {
                    _inFlight = null;
                    _inFlightMode = default;
                }
            // A failed compute must not leave an exception task that swallows later retries:
            // nothing else observes tcs.Task when ComputeAsync throws into a fire-and-forget VM
            // reload that itself has no catch, so mark it observed to avoid UnobservedTaskException
            // escalations on the finalizer.
            if (result is null)
                _ = tcs.Task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    private async Task<PersonalState> ComputeAsync(DataRefreshMode mode, CancellationToken ct)
    {
        var profile = _session.CurrentProfile;
        var today = _clock.Today;

        await _history.EnsureLoadedAsync(profile, today);
        var baselines = await _baselines.GetBaselinesAsync(ct);

        var raw = await _provider.GetNormalizedDayAsync(today, profile, ct) ?? EmptyDay(today);
        var day = _normalizer.Normalize(raw, _clock.Now);

        var sleepFresh = SleepDaysSinceFresh(day);

        var metrics = new Dictionary<string, MetricState>
        {
            [Metrics.SleepMinutes] = Metric(Metrics.SleepMinutes, day.SleepMinutes, baselines, higherIsBetter: true),
            [Metrics.SleepQuality] = Metric(Metrics.SleepQuality, day.SleepQuality, baselines, higherIsBetter: true),
            [Metrics.SleepConsistency] = Metric(Metrics.SleepConsistency, day.SleepConsistency, baselines, higherIsBetter: true),
            [Metrics.BedtimeMinutes] = Metric(Metrics.BedtimeMinutes, day.BedtimeMinutesOfDay, baselines, higherIsBetter: true),
            [Metrics.Steps] = Metric(Metrics.Steps, day.Steps, baselines, higherIsBetter: true),
            [Metrics.ActiveMinutes] = Metric(Metrics.ActiveMinutes, day.ActiveMinutes, baselines, higherIsBetter: true),
            [Metrics.RecoveryScore] = Metric(Metrics.RecoveryScore, day.RecoveryScore, baselines, higherIsBetter: true),
            [Metrics.Stress] = Metric(Metrics.Stress, day.Stress, baselines, higherIsBetter: false),
            [Metrics.Mood] = Metric(Metrics.Mood, day.Mood, baselines, higherIsBetter: true),
            [Metrics.Energy] = Metric(Metrics.Energy, day.Energy, baselines, higherIsBetter: true),
        };

        if (day.RestingHeartRate is { } rhr)
            metrics[Metrics.RestingHeartRate] = Metric(Metrics.RestingHeartRate, rhr, baselines, higherIsBetter: false);
        if (day.HrvMs is { } hrv)
            metrics[Metrics.HrvMs] = Metric(Metrics.HrvMs, hrv, baselines, higherIsBetter: true);

        // Focus estimate: derived (no direct measurement exists in Phase 2) — label it honestly.
        var focusVal = 0.45 * SafeVal(day.Energy) + 0.35 * (1 - SafeVal(day.Stress)) + 0.20 * SafeVal(day.SleepQuality);
        metrics[Metrics.FocusEstimate] = new MetricState
        {
            MetricKey = Metrics.FocusEstimate,
            Value = focusVal,
            BaselineValue = null, // no personal history for a derived signal yet
            BaselineConfidence = BaselineConfidence.None,
            Quality = DataQuality.Estimated,
            Origin = day.Origin,
        };

        var focusState = new FocusState
        {
            Estimated = metrics[Metrics.FocusEstimate],
            IsDerived = true,
        };

        // Habit + goal snapshots for intelligence/UI
        var habits = await _habits.GetAllAsync();
        var habitSnaps = habits.Select(h => SnapshotHabit(h, today)).ToList();
        var goals = (await _goals.GetAllAsync()).Where(g => !g.IsArchived).ToList();
        var goalSnaps = goals.Select(g => new GoalStateSnapshot
        {
            GoalId = g.Id, Name = g.Name, Fraction = g.Fraction, Status = g.Status,
        }).ToList();

        var state = new PersonalState
        {
            GeneratedAt = _clock.Now,
            Confidence = ComputeConfidence(metrics),
            DataCompleteness = day.Completeness(),
            Sleep = new SleepState
            {
                Duration = metrics[Metrics.SleepMinutes],
                Quality = metrics[Metrics.SleepQuality],
                Consistency = metrics[Metrics.SleepConsistency],
                Bedtime = metrics[Metrics.BedtimeMinutes],
                DaysSinceFreshData = sleepFresh,
            },
            Activity = new DailyActivityState
            {
                Steps = metrics[Metrics.Steps],
                ActiveMinutes = metrics[Metrics.ActiveMinutes],
            },
            Recovery = new RecoveryState
            {
                Score = metrics[Metrics.RecoveryScore],
                RestingHeartRate = metrics.GetValueOrDefault(Metrics.RestingHeartRate),
                Hrv = metrics.GetValueOrDefault(Metrics.HrvMs),
            },
            Wellness = new WellnessState2
            {
                Stress = metrics[Metrics.Stress],
                Mood = metrics[Metrics.Mood],
                Energy = metrics[Metrics.Energy],
            },
            Focus = focusState,
            Habits = habitSnaps.FirstOrDefault() ?? new HabitStateSnapshot { HabitId = "", Name = "" },
            HabitSnapshots = habitSnaps,
            GoalSnapshots = goalSnaps,
            Metrics = metrics,
        };

        _last = state;
        _lastMode = mode;
        StateUpdated?.Invoke(state);
        return state;
    }

    private static NormalizedDay EmptyDay(DateTime day) => new()
    {
        Date = day,
        Origin = DataOrigin.Mock,
        SleepMinutes = DataPoint.Missing(day),
        SleepQuality = DataPoint.Missing(day),
        SleepConsistency = DataPoint.Missing(day),
        BedtimeMinutesOfDay = DataPoint.Missing(day),
        WakeMinutesOfDay = DataPoint.Missing(day),
        Steps = DataPoint.Missing(day),
        ActiveMinutes = DataPoint.Missing(day),
        RecoveryScore = DataPoint.Missing(day),
        Stress = DataPoint.Missing(day),
        Mood = DataPoint.Missing(day),
        Energy = DataPoint.Missing(day),
    };

    private static double SafeVal(DataPoint p) => p.Quality is DataQuality.Complete or DataQuality.Estimated ? p.Value : double.NaN;

    private HabitStateSnapshot SnapshotHabit(Habit h, DateTime today)
    {
        var cutoff30 = today.AddDays(-30);
        var last30 = h.Completions.Where(d => d.Date > cutoff30).ToList();
        double successRate = last30.Count / 30.0;

        // Best window: time-of-day histogram from the completion log (needs >=5 points to claim a pattern).
        TimeSpan? window = null;
        if (h.CompletionLog.Count >= 5)
        {
            var hist = h.CompletionLog
                .GroupBy(d => (int)(d.TimeOfDay.TotalHours / 3))
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (hist is not null && hist.Count() >= Math.Max(3, h.CompletionLog.Count * 0.3))
                window = TimeSpan.FromHours(hist.Key * 3);
        }

        var weekCounts = Enumerable.Range(0, 4)
            .Select(w => h.Completions.Count(d => d.Date > today.AddDays(-7 * (w + 1)) && d.Date <= today.AddDays(-7 * w)))
            .Reverse()
            .Select(c => (double)c)
            .ToList();

        return new HabitStateSnapshot
        {
            HabitId = h.Id,
            Name = h.Name,
            Streak = h.CurrentStreak,
            SuccessRate30d = Math.Clamp(successRate, 0, 1),
            BestCompletionWindow = window,
            ConsistencyTrend = _trends.Compute(weekCounts, higherIsBetter: true),
        };
    }

    private static MetricState Metric(string key, DataPoint p, IReadOnlyDictionary<string, Baseline> baselines, bool higherIsBetter)
    {
        Baseline? b = baselines.TryGetValue(key, out var bb) && bb.Confidence != BaselineConfidence.None ? bb : null;
        double? dev = b is null or { Value: <= 0 } ? null : (p.Value - b.Value) / b.Value;
        return new MetricState
        {
            MetricKey = key,
            Value = p.Value,
            BaselineValue = b?.Value,
            BaselineConfidence = b?.Confidence ?? BaselineConfidence.None,
            RelativeDeviation = double.IsNaN(dev ?? double.NaN) ? null : dev,
            HigherIsBetter = higherIsBetter,
            Quality = p.Quality,
            Origin = p.Origin,
        };
    }

    private static int SleepDaysSinceFresh(NormalizedDay day)
    {
        var p = day.SleepMinutes;
        if (p.Quality is DataQuality.Missing or DataQuality.Invalid) return 99;
        var age = DateTime.Today - p.Timestamp.Date;
        return Math.Max(0, (int)age.TotalDays);
    }

    private static double ComputeConfidence(IReadOnlyDictionary<string, MetricState> metrics)
    {
        // Honest confidence: how many domains have a usable baseline AND a complete value today.
        var usable = metrics.Values.Count(m => m.Quality == DataQuality.Complete && m.BaselineConfidence >= BaselineConfidence.Low);
        return Math.Clamp(usable / (double)Math.Max(metrics.Count - 1, 1), 0, 1);
    }
}
