using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.State.Wave3b;

/// <summary>
/// Pure longitudinal projector (Wave 3c lane 04): history + optional today + profile +
/// a BaselineSet → PersonalState. No IO, no clock, no events — the existing (frozen)
/// UserStateService keeps owning the live pipeline; this is the deterministic core the
/// Wave 3b UI paths and tests exercise directly, and the shape UserStateService can be
/// pointed at once its call sites adopt BaselineEngine.
///
/// Honesty rules baked in:
///  • A metric with no value today is Quality.Missing with Value = NaN and Level Unknown.
///    It is NEVER zero-filled and never given a fabricated stand-in value.
///  • A refused baseline (BaselineEngine gate) propagates as BaselineConfidence.None and
///    RelativeDeviation = null — no comparison is drawn against a NaN baseline.
///  • Focus is explicitly derived (IsDerived = true) from energy/stress/sleep per the
///    documented formula below; no direct focus measurement exists.
/// </summary>
public static class PersonalStateProjector
{
    /// <summary>Documented focus weights — the ONLY source of truth for the Wave 3b formula.
    /// focus = energy·0.40 + (1 − stress)·0.35 + sleepNorm·0.25, all terms in 0..1.</summary>
    public const double FocusWeightEnergy = 0.40;
    public const double FocusWeightStress = 0.35;   // applied to (1 - stress)
    public const double FocusWeightSleep = 0.25;

    /// <summary>Days value marking "no fresh data at all" (parity with UserStateService's sentinel).</summary>
    public const int NoDataSentinelDays = 99;

    /// <summary>
    /// Project a PersonalState. <paramref name="today"/> may be null (no signal yet today) —
    /// every metric then reads Missing/NaN. <paramref name="history"/> feeds the freshness
    /// marker; baselines come precomputed from BaselineEngine.
    /// </summary>
    public static PersonalState Project(
        IReadOnlyList<DailyHistoryRecord> history,
        NormalizedDay? today,
        UserProfile profile,
        BaselineSet baselines,
        IReadOnlyList<Habit>? habits = null,
        IReadOnlyList<Goal>? goals = null,
        ITrendService? trends = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(baselines);
        trends ??= new TrendService();   // pure + deterministic — safe default, still injectable

        var origin = today?.Origin ?? DataOrigin.Mock;
        var metrics = new Dictionary<string, MetricState>
        {
            [Metrics.SleepMinutes] = FromDay(Metrics.SleepMinutes, today?.SleepMinutes, baselines, higherIsBetter: true, origin, wrap: false),
            [Metrics.SleepQuality] = FromDay(Metrics.SleepQuality, today?.SleepQuality, baselines, higherIsBetter: true, origin, wrap: false),
            [Metrics.SleepConsistency] = FromDay(Metrics.SleepConsistency, today?.SleepConsistency, baselines, higherIsBetter: true, origin, wrap: false),
            [Metrics.BedtimeMinutes] = FromDay(Metrics.BedtimeMinutes, today?.BedtimeMinutesOfDay, baselines, higherIsBetter: true, origin, wrap: true),
            [Metrics.Steps] = FromDay(Metrics.Steps, today?.Steps, baselines, higherIsBetter: true, origin, wrap: false),
            [Metrics.ActiveMinutes] = FromDay(Metrics.ActiveMinutes, today?.ActiveMinutes, baselines, higherIsBetter: true, origin, wrap: false),
            [Metrics.RecoveryScore] = FromDay(Metrics.RecoveryScore, today?.RecoveryScore, baselines, higherIsBetter: true, origin, wrap: false),
            [Metrics.Stress] = FromDay(Metrics.Stress, today?.Stress, baselines, higherIsBetter: false, origin, wrap: false),
            [Metrics.Mood] = FromDay(Metrics.Mood, today?.Mood, baselines, higherIsBetter: true, origin, wrap: false),
            [Metrics.Energy] = FromDay(Metrics.Energy, today?.Energy, baselines, higherIsBetter: true, origin, wrap: false),
        };
        if (today?.RestingHeartRate is { } rhr)
            metrics[Metrics.RestingHeartRate] = FromDay(Metrics.RestingHeartRate, rhr, baselines, higherIsBetter: false, origin, wrap: false);
        else
            metrics[Metrics.RestingHeartRate] = Missing(Metrics.RestingHeartRate, higherIsBetter: false, origin);
        if (today?.HrvMs is { } hrv)
            metrics[Metrics.HrvMs] = FromDay(Metrics.HrvMs, hrv, baselines, higherIsBetter: true, origin, wrap: false);
        else
            metrics[Metrics.HrvMs] = Missing(Metrics.HrvMs, higherIsBetter: true, origin);

        var sleepNorm = SleepNorm(today, profile, baselines);
        var focusValue = FocusValue(
            UsableValue(today?.Energy), UsableValue(today?.Stress), sleepNorm);
        metrics[Metrics.FocusEstimate] = new MetricState
        {
            MetricKey = Metrics.FocusEstimate,
            Value = focusValue,
            BaselineValue = null,   // derived signal has no personal history yet — honest null
            BaselineConfidence = BaselineConfidence.None,
            Quality = double.IsNaN(focusValue) ? DataQuality.Missing : DataQuality.Estimated,
            Origin = origin,
            HigherIsBetter = true,
        };
        var focusState = new FocusState { Estimated = metrics[Metrics.FocusEstimate], IsDerived = true };

        int staleness = DaysSinceFreshData(history, today?.Date, (today?.Completeness() ?? 0) > 0);

        var habitSnaps = (habits ?? Array.Empty<Habit>())
            .Select(h => SnapshotHabit(h, AsOfDate(today, history), trends))
            .ToList();
        var goalSnaps = (goals ?? Array.Empty<Goal>())
            .Where(g => !g.IsArchived)
            .Select(g => new GoalStateSnapshot { GoalId = g.Id, Name = g.Name, Fraction = g.Fraction, Status = g.Status })
            .ToList();

        double completeness = today?.Completeness() ?? 0;
        var usedKeys = new[]
        {
            Metrics.SleepMinutes, Metrics.SleepQuality, Metrics.Steps, Metrics.ActiveMinutes,
            Metrics.RecoveryScore, Metrics.Stress, Metrics.Mood, Metrics.Energy,
        };
        var worst = BaselineConfidence.High;
        foreach (var k in usedKeys)
        {
            var c = metrics[k].Quality is DataQuality.Missing or DataQuality.Invalid
                ? BaselineConfidence.None
                : metrics[k].BaselineConfidence;
            if (c < worst) worst = c;
        }
        var mix = OriginMixOf(today, completeness);
        var confidence = StateConfidenceCalculator.Compute(completeness, worst, staleness, mix).Score;

        return new PersonalState
        {
            GeneratedAt = AsOfDate(today, history),
            Confidence = confidence,
            DataCompleteness = completeness,
            Sleep = new SleepState
            {
                Duration = metrics[Metrics.SleepMinutes],
                Quality = metrics[Metrics.SleepQuality],
                Consistency = metrics[Metrics.SleepConsistency],
                Bedtime = metrics[Metrics.BedtimeMinutes],
                DaysSinceFreshData = staleness,
            },
            Activity = new DailyActivityState
            {
                Steps = metrics[Metrics.Steps],
                ActiveMinutes = metrics[Metrics.ActiveMinutes],
            },
            Recovery = new RecoveryState
            {
                Score = metrics[Metrics.RecoveryScore],
                RestingHeartRate = metrics[Metrics.RestingHeartRate],
                Hrv = metrics[Metrics.HrvMs],
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
    }

    // ---- metric plumbing ------------------------------------------------------

    private static MetricState Missing(string key, bool higherIsBetter, DataOrigin origin) => new()
    {
        MetricKey = key,
        Value = double.NaN,          // NEVER 0 — a zero here would read as a real measurement
        BaselineValue = null,
        BaselineConfidence = BaselineConfidence.None,
        RelativeDeviation = null,
        HigherIsBetter = higherIsBetter,
        Quality = DataQuality.Missing,
        Origin = origin,
    };

    private static MetricState FromDay(
        string key, DataPoint? point, BaselineSet baselines,
        bool higherIsBetter, DataOrigin origin, bool wrap)
    {
        if (point is null || point.Quality is DataQuality.Missing or DataQuality.Invalid
            || double.IsNaN(point.Value) || (wrap && point.Value <= 0))
            return Missing(key, higherIsBetter, origin);

        var entry = baselines.Entry(key);
        Baseline? b = entry is { Usable: true } ? entry.Baseline : null;

        double? deviation = null;
        if (b is not null && b.Value > 0)
        {
            if (wrap)
            {
                // Cross-midnight comparison happens in post-noon space, reusing the ONE
                // unwrap implementation the frozen BaselineService owns (no forked math).
                double wrappedToday = BaselineEngine.WrapBedtime(point.Value);
                double wrappedBase = BaselineService.UnwrapBedtime(b.Value) < 12 * 60
                    ? BaselineService.UnwrapBedtime(b.Value) + 1440
                    : BaselineService.UnwrapBedtime(b.Value);
                if (!double.IsNaN(wrappedToday) && wrappedBase > 0)
                    deviation = (wrappedToday - wrappedBase) / wrappedBase;
            }
            else
            {
                deviation = (point.Value - b.Value) / b.Value;
            }
        }

        return new MetricState
        {
            MetricKey = key,
            Value = point.Value,
            BaselineValue = b?.Value,
            BaselineConfidence = b?.Confidence ?? BaselineConfidence.None,
            RelativeDeviation = deviation is null || double.IsNaN(deviation.Value) ? null : deviation,
            HigherIsBetter = higherIsBetter,
            Quality = point.Quality,
            Origin = point.Origin,
        };
    }

    // ---- focus (documented derivation) ----------------------------------------

    /// <summary>Sleep term of the focus formula: today's sleep minutes normalized against
    /// the profile's needed sleep (preferred window, clamped 6–10h) or the user's own sleep
    /// baseline when one exists. NaN when no sleep data — the formula then yields NaN focus.</summary>
    public static double SleepNorm(NormalizedDay? today, UserProfile profile, BaselineSet baselines)
    {
        double minutes = UsableValue(today?.SleepMinutes);
        if (double.IsNaN(minutes)) return double.NaN;
        double need = baselines.Value(Metrics.SleepMinutes) ?? NeededSleepMinutes(profile);
        if (need <= 0 || double.IsNaN(need)) need = NeededSleepMinutes(profile);
        return Math.Clamp(minutes / need, 0, 1);
    }

    private static double NeededSleepMinutes(UserProfile profile)
    {
        double window = (profile.PreferredWakeTime - profile.PreferredBedtime).TotalMinutes;
        if (window <= 0) window += 24 * 60;   // bedtime after wake time = overnight window
        return Math.Clamp(window, 6 * 60, 10 * 60);
    }

    /// <summary>
    /// THE focus estimate: energy·0.40 + (1 − stress)·0.35 + sleepNorm·0.25 (each term 0..1,
    /// total 0..1). Any input missing ⇒ NaN (Missing quality downstream) — never a silent 0,
    /// because a fabricated mid-range focus would read like a measurement.
    /// </summary>
    public static double FocusValue(double energy, double stress, double sleepNorm)
    {
        if (double.IsNaN(energy) || double.IsNaN(stress) || double.IsNaN(sleepNorm))
            return double.NaN;
        return Math.Clamp(
            FocusWeightEnergy * energy
            + FocusWeightStress * (1 - stress)
            + FocusWeightSleep * sleepNorm,
            0, 1);
    }

    private static double UsableValue(DataPoint? p) =>
        p is null || p.Quality is DataQuality.Missing or DataQuality.Invalid ? double.NaN : p.Value;

    // ---- freshness / origin -----------------------------------------------------

    /// <summary>Days between the reference day and the NEWEST record (a today-day that
    /// actually carries data counts as a record). 99 sentinel when nothing exists at all
    /// (mirrors UserStateService's stale marker without pretending to know an age).</summary>
    public static int DaysSinceFreshData(IReadOnlyList<DailyHistoryRecord> history, DateTime? todayDate, bool todayHasData = false)
    {
        var candidates = history.Select(r => r.Date.Date).ToList();
        if (todayDate is { } t && todayHasData) candidates.Add(t.Date);
        if (candidates.Count == 0) return NoDataSentinelDays;
        var newest = candidates.Max();
        var asOf = todayDate?.Date ?? newest;
        return Math.Max(0, (int)(asOf - newest).TotalDays);
    }

    private static DateTime AsOfDate(NormalizedDay? today, IReadOnlyList<DailyHistoryRecord> history) =>
        today?.Date ?? history.OrderByDescending(r => r.Date).FirstOrDefault()?.Date ?? DateTime.MinValue;

    private static OriginMix OriginMixOf(NormalizedDay? today, double completeness)
    {
        if (today is null || completeness <= 0) return OriginMix.NoData;
        return today.Origin == DataOrigin.Manual ? OriginMix.AllManual : OriginMix.AllDevice;
    }

    // ---- habit/goal snapshots (pure parameters in, snapshots out) ----------------

    private static HabitStateSnapshot SnapshotHabit(Habit h, DateTime asOf, ITrendService trends)
    {
        var cutoff30 = asOf.AddDays(-30);
        double successRate = h.Completions.Count(d => d.Date > cutoff30 && d.Date <= asOf) / 30.0;

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
            .Select(w => h.Completions.Count(d => d.Date > asOf.AddDays(-7 * (w + 1)) && d.Date <= asOf.AddDays(-7 * w)))
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
            ConsistencyTrend = trends.Compute(weekCounts, higherIsBetter: true),
        };
    }
}
