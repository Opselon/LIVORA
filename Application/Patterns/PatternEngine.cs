using LIVORA.Application.State;
using LIVORA.Application.State.Wave3b;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Patterns;

/// <summary>One localization key plus the numeric arguments its template consumes
/// ({"pattern.evidence.late-nights", [3, 7]} renders as "3 of the last 7 nights").
/// Keys are machine-readable dotted identifiers — never display prose.</summary>
public sealed record PatternEvidenceKey(string Key, IReadOnlyList<object> Args)
{
    public PatternEvidenceKey(string key) : this(key, Array.Empty<object>()) { }
}

/// <summary>
/// A single observed behavioral pattern. HARD RULE (product law): a finding describes
/// co-occurrence and deviation from the user's OWN baseline — it never claims, implies or
/// localizes causation, and it is never a diagnosis. Every evidence key must start with
/// "pattern.observed." or "pattern.evidence." (enforced by the no-causal-language sweep in
/// Tests/Wave3c/State/PatternEngineTests).
/// </summary>
public sealed record PatternFinding(
    PatternKind Kind,
    double Confidence,
    int SampleCount,
    DateTime DateFrom,
    DateTime DateTo,
    IReadOnlyList<PatternEvidenceKey> EvidenceKeys)
{
    /// <summary>Direction when the pattern is about a trend (WorkoutConsistency); otherwise
    /// InsufficientData — absence of direction is honest, not a bug.</summary>
    public TrendDirection Trend { get; init; } = TrendDirection.InsufficientData;
}

/// <summary>One goal-progress observation, fed to the stagnation detector. ProgressValue and
/// Deadline ride along so the detector stays pure (no repository round-trip per sample).</summary>
public sealed record GoalProgressSample(string GoalId, DateTime Date, double ProgressValue, DateTime? Deadline);

/// <summary>Everything a pattern scan reads. Pure data in — findings out.</summary>
public sealed record PatternInput(
    DateTime AsOf,
    IReadOnlyList<DailyHistoryRecord> History,
    IReadOnlyList<Habit> Habits,
    IReadOnlyList<GoalProgressSample> GoalProgress)
{
    public static PatternInput Empty(DateTime asOf) => new PatternInput(
        asOf,
        new List<DailyHistoryRecord>(),
        new List<Habit>(),
        new List<GoalProgressSample>());
}

/// <summary>Scan outcome: findings plus the kinds that REFUSED for lack of samples.
/// Refusal is explicit data, not a silent omission — the UI can say "learning" instead of
/// pretending the pattern is absent.</summary>
public sealed record PatternScanResult(
    IReadOnlyList<PatternFinding> Findings,
    IReadOnlyList<PatternKind> InsufficientKinds)
{
    public static PatternScanResult AllInsufficient() =>
        new(Array.Empty<PatternFinding>(), Enum.GetValues<PatternKind>());
}

/// <summary>
/// Behavioral pattern engine (Wave 3c lane 04) — pure, deterministic, no IO, no wall clock.
/// Requires at least <see cref="MinHistoryDays"/> distinct days of history; below that every
/// kind is reported as Insufficient instead of half-guessed.
/// </summary>
public static class PatternEngine
{
    /// <summary>Global input gate for ANY pattern.</summary>
    public const int MinHistoryDays = 21;

    // ---- per-detector gates (pinned by tests) --------------------------------
    public const int GateLateSleepNights = 7;      // needs 7 of the last 7 nights recorded
    public const int LateSleepNightsToFire = 3;    // ≥3 late nights in that window
    public const double LateSleepThresholdMinutes = 60;   // bedtime > baseline + 60 min

    public const int GateWeekdaySamples = 4;       // ≥4 observations of that weekday
    public const double WeekdayDipThreshold = -0.20;      // −20% steps vs personal mean

    public const int GateFocusPairs = 8;           // ≥8 poor-sleep→next-day pairs
    public const double PoorSleepDeviation = -0.15;       // sleep below −15% of baseline
    public const double FocusCoOccurrenceToFire = 0.50;   // more often than not

    public const int GateHabitDays = 10;           // ≥10 expected days to judge a habit
    public const double HabitClusterMissRate = 0.60;      // a weekday ≥60% missed

    public const int GateWorkoutWeeks = 6;         // 6 full-enough weeks of activity
    public const int WorkoutActiveMinutesFloor = 30;      // a "workout day" threshold

    public const int GateStagnationDays = 10;      // progress flat ≥10 days
    public const int StagnationDeadlineWindowDays = 30;   // deadline inside 30 days matters

    public const int GateCouplingPairs = 14;       // ≥14 lag-1 pairs
    public const double CouplingMinMagnitude = 0.30;      // |r| worth reporting

    // ---- public entry ---------------------------------------------------------

    public static PatternScanResult Scan(PatternInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var history = input.History
            .Where(r => r.Date.Date <= input.AsOf.Date)
            .OrderBy(r => r.Date.Date)
            .ToList();
        int distinctDays = history.Select(r => r.Date.Date).Distinct().Count();
        if (distinctDays < MinHistoryDays)
            return PatternScanResult.AllInsufficient();

        var findings = new List<PatternFinding>();
        var insufficient = new List<PatternKind>();

        TryLateSleepRecurring(input, history, findings, insufficient);
        TryWeekdayActivityDip(input, history, findings, insufficient);
        TryFocusAfterPoorSleep(input, history, findings, insufficient);
        TryHabitFailureWindow(input, history, findings, insufficient);
        TryWorkoutConsistency(input, history, findings, insufficient);
        TryGoalStagnation(input, history, findings, insufficient);
        TryRecoveryActivityCoupling(input, history, findings, insufficient);

        findings.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        return new PatternScanResult(findings, insufficient);
    }

    // ---- 1) LateSleepRecurring -------------------------------------------------

    private static void TryLateSleepRecurring(PatternInput input, List<DailyHistoryRecord> history,
        List<PatternFinding> findings, List<PatternKind> insufficient)
    {
        const PatternKind kind = PatternKind.LateSleepRecurring;
        // Bedtime baseline (longitudinal, honest gate) — no baseline, no reference for "late".
        var entry = BaselineEngine.ComputeWindow(Metrics.BedtimeMinutes, history, input.AsOf, BaselineWindow.Days14);
        if (entry is not { Usable: true }) { insufficient.Add(kind); return; }
        double basePostNoon = ToPostNoon(entry.Baseline.Value);

        var lastSeven = history
            .Where(r => r.Date.Date > input.AsOf.Date.AddDays(-GateLateSleepNights) && r.Date.Date <= input.AsOf.Date)
            .Where(r => r.BedtimeMinutesOfDay > 0 && !double.IsNaN(r.BedtimeMinutesOfDay))
            .ToList();
        // Missing bedtimes inside the last 7 nights = the window itself is unobservable.
        if (lastSeven.Count < GateLateSleepNights) { insufficient.Add(kind); return; }

        var lateNights = lastSeven
            .Where(r => ToPostNoon(r.BedtimeMinutesOfDay) > basePostNoon + LateSleepThresholdMinutes)
            .ToList();
        int late = lateNights.Count;
        if (late < LateSleepNightsToFire) return;   // below the fire threshold is NOT a pattern

        findings.Add(new PatternFinding(
            kind,
            ConfidenceFrom(late / (double)GateLateSleepNights / 0.60, lastSeven.Count, GateLateSleepNights),
            lastSeven.Count,
            lastSeven[0].Date.Date, lastSeven[^1].Date.Date,
            new[]
            {
                new PatternEvidenceKey("pattern.observed.late-sleep"),
                new PatternEvidenceKey("pattern.evidence.late-nights",
                    new object[] { late, lastSeven.Count, (int)LateSleepThresholdMinutes }),
            }));
    }

    /// <summary>Bedtimes before noon belong to the previous night's sleep session — map to the
    /// post-noon scale (same idea as BaselineService.WrapAround / BaselineEngine.WrapBedtime).</summary>
    private static double ToPostNoon(double minutesOfDay) => BaselineEngine.WrapBedtime(minutesOfDay);

    // ---- 2) WeekdayActivityDip --------------------------------------------------

    private static void TryWeekdayActivityDip(PatternInput input, List<DailyHistoryRecord> history,
        List<PatternFinding> findings, List<PatternKind> insufficient)
    {
        const PatternKind kind = PatternKind.WeekdayActivityDip;
        var withSteps = history.Where(r => r.Steps > 0).ToList();
        if (withSteps.Count == 0) { insufficient.Add(kind); return; }
        double overall = withSteps.Average(r => (double)r.Steps);
        if (overall <= 0) { insufficient.Add(kind); return; }

        var groups = withSteps
            .GroupBy(r => (int)r.Date.DayOfWeek)
            .Select(g => new
            {
                Weekday = g.Key,
                N = g.Count(),
                Mean = g.Average(r => (double)r.Steps),
                First = g.Min(r => r.Date.Date),
                Last = g.Max(r => r.Date.Date),
            })
            .ToList();

        var qualifying = groups.Where(g => g.N >= GateWeekdaySamples).ToList();
        if (qualifying.Count == 0) { insufficient.Add(kind); return; }

        var worst = qualifying
            .OrderBy(g => (g.Mean - overall) / overall)
            .First();
        double dev = (worst.Mean - overall) / overall;
        if (dev > WeekdayDipThreshold) return;   // no weekday dips enough

        int pctBelow = (int)Math.Round(Math.Abs(dev) * 100, MidpointRounding.AwayFromZero);
        findings.Add(new PatternFinding(
            kind,
            ConfidenceFrom(Math.Abs(dev) / 0.40, worst.N, GateWeekdaySamples),
            worst.N,
            worst.First, worst.Last,
            new[]
            {
                new PatternEvidenceKey("pattern.observed.weekday-dip"),
                new PatternEvidenceKey("pattern.evidence.weekday-dip",
                    new object[] { worst.Weekday, pctBelow, worst.N }),
            }));
    }

    // ---- 3) FocusAfterPoorSleep ---------------------------------------------------

    private static void TryFocusAfterPoorSleep(PatternInput input, List<DailyHistoryRecord> history,
        List<PatternFinding> findings, List<PatternKind> insufficient)
    {
        const PatternKind kind = PatternKind.FocusAfterPoorSleep;
        var sleepEntry = BaselineEngine.ComputeWindow(Metrics.SleepMinutes, history, input.AsOf, BaselineWindow.Days30);
        if (sleepEntry is not { Usable: true }) { insufficient.Add(kind); return; }
        double sleepBase = sleepEntry.Baseline.Value;
        if (sleepBase <= 0) { insufficient.Add(kind); return; }

        // Day-indexed focus series (same derivation the projector documents): sleepNorm uses
        // the person's own sleep baseline as the need reference.
        var byDate = history.GroupBy(r => r.Date.Date).ToDictionary(g => g.Key, g => g.Last());
        double FocusOf(DateTime day) =>
            byDate.TryGetValue(day, out var r)
                ? PersonalStateProjector.FocusValue(r.Energy, r.Stress, Math.Clamp(r.SleepMinutes / sleepBase, 0, 1))
                : double.NaN;
        var focusSeries = byDate.Keys.OrderBy(d => d)
            .Select(FocusOf).Where(v => !double.IsNaN(v)).ToList();
        if (focusSeries.Count < GateFocusPairs) return;   // not enough focus signal to judge
        double focusMedian = Median(focusSeries);

        var dates = byDate.Keys.OrderBy(d => d).ToList();
        int pairs = 0, cooccur = 0;
        DateTime first = default, last = default;
        for (int i = 0; i + 1 < dates.Count; i++)
        {
            var night = byDate[dates[i]];
            var next = byDate[dates[i + 1]];
            bool consecutive = (dates[i + 1].Date - dates[i].Date).TotalDays == 1;
            if (!consecutive) continue;
            double sleepDev = (night.SleepMinutes - sleepBase) / sleepBase;
            if (sleepDev >= PoorSleepDeviation) continue;            // not a poor-sleep night
            double focusNext = FocusOf(next.Date);
            if (double.IsNaN(focusNext)) continue;                   // no next-day signal
            pairs++;
            if (first == default) first = dates[i];
            last = dates[i + 1];
            if (focusNext < focusMedian) cooccur++;
        }
        if (pairs < GateFocusPairs) { insufficient.Add(kind); return; }

        double rate = cooccur / (double)pairs;
        if (rate < FocusCoOccurrenceToFire) return;   // association not stronger than chance

        int ratePct = (int)Math.Round(rate * 100, MidpointRounding.AwayFromZero);
        findings.Add(new PatternFinding(
            kind,
            ConfidenceFrom(rate / 0.80, pairs, GateFocusPairs),
            pairs,
            first, last,
            new[]
            {
                new PatternEvidenceKey("pattern.observed.poor-sleep-focus"),
                new PatternEvidenceKey("pattern.evidence.poor-sleep-focus",
                    new object[] { ratePct, pairs, cooccur }),
            }));
    }

    // ---- 4) HabitFailureWindow -----------------------------------------------------

    private static void TryHabitFailureWindow(PatternInput input, List<DailyHistoryRecord> history,
        List<PatternFinding> findings, List<PatternKind> insufficient)
    {
        const PatternKind kind = PatternKind.HabitFailureWindow;
        var expectedDates = history.Select(r => r.Date.Date).Distinct().OrderBy(d => d).ToList();
        if (expectedDates.Count < GateHabitDays) { insufficient.Add(kind); return; }

        var habits = input.Habits;
        // No habit data supplied = nothing observable — an honest refusal, not a silent pass.
        if (habits.Count == 0) { insufficient.Add(kind); return; }

        PatternFinding? best = null;
        foreach (var h in habits)
        {
            var done = h.Completions.Select(d => d.Date).ToHashSet();
            var missed = expectedDates.Where(d => !done.Contains(d)).ToList();
            if (missed.Count == 0) continue;

            // Cluster by weekday: the weekday whose days are missed far more than the habit overall.
            var perWeekday = missed.GroupBy(d => (int)d.DayOfWeek)
                .Select(g => (Weekday: g.Key, Count: g.Count()))
                .OrderByDescending(t => t.Count)
                .First();
            int expectedOnThatWeekday = expectedDates.Count(d => (int)d.DayOfWeek == perWeekday.Weekday);
            double clusterRate = expectedOnThatWeekday == 0 ? 0 : perWeekday.Count / (double)expectedOnThatWeekday;
            if (expectedOnThatWeekday < 2 || clusterRate < HabitClusterMissRate) continue;

            int pct = (int)Math.Round(clusterRate * 100, MidpointRounding.AwayFromZero);
            var finding = new PatternFinding(
                kind,
                ConfidenceFrom(clusterRate / 0.85, expectedDates.Count, GateHabitDays),
                expectedDates.Count,
                expectedDates[0], expectedDates[^1],
                new[]
                {
                    new PatternEvidenceKey("pattern.observed.habit-window"),
                    new PatternEvidenceKey("pattern.evidence.habit-window",
                        new object[] { perWeekday.Weekday, perWeekday.Count, expectedOnThatWeekday, pct }),
                });
            if (best is null || finding.Confidence > best.Confidence) best = finding;
        }
        if (best is not null) findings.Add(best);
    }

    // ---- 5) WorkoutConsistency -------------------------------------------------------

    private static void TryWorkoutConsistency(PatternInput input, List<DailyHistoryRecord> history,
        List<PatternFinding> findings, List<PatternKind> insufficient)
    {
        const PatternKind kind = PatternKind.WorkoutConsistency;
        // Weeks bucketed Saturday-first (Habit.StartOfWeek convention — the localized calendars
        // align later; keeping ONE convention across the app beats inventing a second one here).
        var weeks = history
            .GroupBy(r => Habit.StartOfWeek(r.Date))
            // Only COMPLETE weeks count: a partial edge week with fewer recorded days would
            // fake a "decline" out of missing data, which is the lie this detector exists to avoid.
            .Where(g => g.Select(r => r.Date.Date).Distinct().Count() >= 7)
            .Where(g => g.Min(r => r.Date.Date) > input.AsOf.Date.AddDays(-7 * (GateWorkoutWeeks + 2)))
            .OrderBy(g => g.Key)
            .ToList();
        if (weeks.Count < GateWorkoutWeeks) { insufficient.Add(kind); return; }
        weeks = weeks.Skip(Math.Max(0, weeks.Count - GateWorkoutWeeks)).ToList();

        var daysPerWeek = weeks
            .Select(g => (double)g.Count(r => r.ActiveMinutes >= WorkoutActiveMinutesFloor))
            .ToList();
        var trend = ClassifyTrend(daysPerWeek, higherIsBetter: true);   // local mirror of TrendService
        if (trend is TrendDirection.InsufficientData) { insufficient.Add(kind); return; }
        if (trend == TrendDirection.Stable) return;   // flat noise: nothing to report

        int workoutDays = (int)daysPerWeek.Sum();
        findings.Add(new PatternFinding(
            kind,
            ConfidenceFrom(0.80, weeks.Count, GateWorkoutWeeks),
            weeks.Count,
            weeks[0].Key, weeks[^1].Key.AddDays(6),
            new[]
            {
                new PatternEvidenceKey("pattern.observed.workout-trend"),
                new PatternEvidenceKey("pattern.evidence.workout-trend",
                    new object[] { weeks.Count, workoutDays }),
            })
        { Trend = trend });
    }

    /// <summary>
    /// LOCAL helper mirroring TrendService.Compute (first-half mean vs second-half mean, 6%
    /// noise band, &lt;5 samples ⇒ InsufficientData). Mirrored on purpose: TrendService is frozen
    /// to edits, the pattern layer is static, and forking the CONSTANTS would be the bug —
    /// so they are referenced from the original, not re-typed. Behavior-identical, test-pinned.
    /// </summary>
    internal static TrendDirection ClassifyTrend(IReadOnlyList<double> values, bool higherIsBetter)
    {
        var v = values.Where(x => !double.IsNaN(x)).ToList();
        if (v.Count < TrendService.MinSamples) return TrendDirection.InsufficientData;

        int half = v.Count / 2;
        double first = v.Take(half).Average();
        double second = v.Skip(half).Average();
        double scale = Math.Max(Math.Abs(first), 1e-6);
        double change = (second - first) / scale;

        if (Math.Abs(change) < TrendService.BandFraction) return TrendDirection.Stable;
        bool improving = higherIsBetter ? change > 0 : change < 0;
        return improving ? TrendDirection.Improving : TrendDirection.Declining;
    }

    // ---- 6) GoalStagnation -----------------------------------------------------------

    private static void TryGoalStagnation(PatternInput input, List<DailyHistoryRecord> history,
        List<PatternFinding> findings, List<PatternKind> insufficient)
    {
        const PatternKind kind = PatternKind.GoalStagnation;
        var samples = input.GoalProgress
            .Where(s => s.Date.Date <= input.AsOf.Date)
            .GroupBy(s => s.GoalId)
            .ToList();
        // No tracked goal samples = nothing observable for THIS run (UI shows "learning").
        if (samples.Count == 0) { insufficient.Add(kind); return; }

        foreach (var group in samples)
        {
            var pts = group.OrderBy(s => s.Date.Date)
                           .GroupBy(s => s.Date.Date).Select(g => g.Last())
                           .ToList();
            if (pts.Count < 2) continue;
            var last = pts[^1];
            if (last.Deadline is null) continue;
            DateTime deadline = last.Deadline.Value;
            int daysToDeadline = (int)(deadline.Date - input.AsOf.Date).TotalDays;
            if (daysToDeadline < 0 || daysToDeadline > StagnationDeadlineWindowDays) continue;

            // Longest run at the tail where progress did not move (flat at the newest value).
            double newestValue = last.ProgressValue;
            int flatFrom = pts.Count - 1;
            while (flatFrom > 0 && pts[flatFrom - 1].ProgressValue == newestValue) flatFrom--;
            int flatDays = (int)(pts[^1].Date.Date - pts[flatFrom].Date.Date).TotalDays + 1;
            if (flatDays < GateStagnationDays) continue;

            findings.Add(new PatternFinding(
                kind,
                ConfidenceFrom(flatDays / 20.0, flatDays, GateStagnationDays),
                flatDays,
                pts[flatFrom].Date.Date, pts[^1].Date.Date,
                new[]
                {
                    new PatternEvidenceKey("pattern.observed.goal-stagnant"),
                    new PatternEvidenceKey("pattern.evidence.goal-stagnant",
                        new object[] { flatDays, daysToDeadline }),
                }));
            return;   // one stagnation report is enough noise for the UI
        }
    }

    // ---- 7) RecoveryActivityCoupling ----------------------------------------------------

    private static void TryRecoveryActivityCoupling(PatternInput input, List<DailyHistoryRecord> history,
        List<PatternFinding> findings, List<PatternKind> insufficient)
    {
        const PatternKind kind = PatternKind.RecoveryActivityCoupling;
        var byDate = history.GroupBy(r => r.Date.Date).ToDictionary(g => g.Key, g => g.Last());
        var dates = byDate.Keys.OrderBy(d => d).ToList();

        var xs = new List<double>();  // previous-day activity
        var ys = new List<double>();  // same-index recovery score for today
        DateTime first = default, last = default;
        for (int i = 1; i < dates.Count; i++)
        {
            if ((dates[i] - dates[i - 1]).TotalDays != 1) continue;
            var prev = byDate[dates[i - 1]];
            var today = byDate[dates[i]];
            if (prev.Steps <= 0 || double.IsNaN(today.RecoveryScore)) continue;
            if (first == default) first = dates[i - 1];
            last = dates[i];
            xs.Add(prev.Steps);
            ys.Add(today.RecoveryScore);
        }
        if (xs.Count < GateCouplingPairs) { insufficient.Add(kind); return; }

        double? r = Pearson(xs, ys);
        if (r is null || Math.Abs(r.Value) < CouplingMinMagnitude) return;  // not an association worth naming

        findings.Add(new PatternFinding(
            kind,
            ConfidenceFrom(Math.Abs(r.Value) / 0.60, xs.Count, GateCouplingPairs),
            xs.Count,
            first, last,
            new[]
            {
                new PatternEvidenceKey("pattern.observed.recovery-activity"),
                new PatternEvidenceKey("pattern.evidence.recovery-activity",
                    new object[] { Math.Round(r.Value, 2, MidpointRounding.AwayFromZero), xs.Count }),
            }));
    }

    /// <summary>Plain Pearson r; null when undefined (n&lt;2 or a constant side).</summary>
    public static double? Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count || x.Count < 2) return null;
        int n = x.Count;
        double mx = x.Average(), my = y.Average();
        double sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }
        if (sxx <= 0 || syy <= 0) return null;
        return sxy / Math.Sqrt(sxx * syy);
    }

    // ---- shared plumbing ---------------------------------------------------------------

    /// <summary>
    /// Confidence from effect size × sample-gate ratio, capped: a finding at exactly the gate
    /// with exactly the fire-threshold effect lands at ~50%, and the hard cap keeps ANY finding
    /// below certainty (0.95) — patterns are never as sure as measurements.
    /// </summary>
    public static double ConfidenceFrom(double effectScore, int sampleCount, int gate)
    {
        double effect = Math.Clamp(effectScore, 0, 1);
        double gateRatio = Math.Clamp(gate <= 0 ? 1 : sampleCount / (double)gate, 0, 1);
        return Math.Round(Math.Min(0.95, effect * gateRatio), 2, MidpointRounding.AwayFromZero);
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList();
        int mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2.0;
    }
}
