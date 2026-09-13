using System.Text.RegularExpressions;
using LIVORA.Application.Patterns;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;
using static LIVORA.Tests.Wave3c.Lane04Fixtures;

namespace LIVORA.Tests.Wave3c;

/// <summary>Detector-by-detector firing, silence on noise, gate refusal, and the
/// HARD no-causal-language sweep — PatternEngine (lane 04).</summary>
public class PatternEngineTests
{
    private static DateTime AsOf(int day) => Epoch.AddDays(day);

    private static PatternFinding? Finding(PatternScanResult r, PatternKind kind) =>
        r.Findings.FirstOrDefault(f => f.Kind == kind);

    private static PatternInput Input(
        IReadOnlyList<DailyHistoryRecord> history, IReadOnlyList<Habit>? habits = null,
        IReadOnlyList<GoalProgressSample>? goals = null, int asDay = 27)
        => new(AsOf(asDay), history, habits ?? Array.Empty<Habit>(), goals ?? Array.Empty<GoalProgressSample>());

    // ---- global gate ---------------------------------------------------------

    [Fact]
    public void FewerThan21Days_EveryKindRefuses()
    {
        var r = PatternEngine.Scan(Input(Flat(20), asDay: 19));
        Assert.Empty(r.Findings);
        Assert.Equal(7, r.InsufficientKinds.Count);
        Assert.All((PatternKind[])Enum.GetValues(typeof(PatternKind)),
            k => Assert.Contains(k, r.InsufficientKinds));
    }

    [Fact]
    public void FlatNoise_FiresNothing()
    {
        var r = PatternEngine.Scan(Input(Flat(28)));
        Assert.Empty(r.Findings);   // no pattern may be imagined out of steady data
    }

    // ---- 1) LateSleepRecurring -------------------------------------------------

    private static List<DailyHistoryRecord> LateSleepFixture() =>
        Enumerable.Range(0, 28).Select(i => Day(i, bedtime: i >= 24 ? 1500 : 1380)).ToList();

    [Fact]
    public void LateSleep_FiresOnHandBuiltFixture()
    {
        var r = PatternEngine.Scan(Input(LateSleepFixture()));
        var f = Finding(r, PatternKind.LateSleepRecurring);
        Assert.NotNull(f);
        Assert.Equal(7, f!.SampleCount);                       // the 7-night window was observed
        Assert.Equal(Epoch.AddDays(21), f.DateFrom.Date);
        Assert.Equal(Epoch.AddDays(27), f.DateTo.Date);
        var ev = f.EvidenceKeys.First(k => k.Key == "pattern.evidence.late-nights");
        Assert.Equal(new object[] { 4, 7, 60 }, ev.Args.ToArray());   // 4 late of 7, +60 min
    }

    [Fact]
    public void LateSleep_SilentWhenOnlyTwoNightsLate()
    {
        var hist = Enumerable.Range(0, 28).Select(i => Day(i, bedtime: i >= 26 ? 1500 : 1380)).ToList();
        Assert.Null(Finding(PatternEngine.Scan(Input(hist)), PatternKind.LateSleepRecurring));
    }

    [Fact]
    public void LateSleep_RefusesWhenARecentNightIsUnrecorded()
    {
        var hist = LateSleepFixture();
        hist[24].BedtimeMinutesOfDay = 0;   // 6 of 7 nights → below the 7-night gate
        var r = PatternEngine.Scan(Input(hist));
        Assert.Null(Finding(r, PatternKind.LateSleepRecurring));
        Assert.Contains(PatternKind.LateSleepRecurring, r.InsufficientKinds);
    }

    // ---- 2) WeekdayActivityDip ----------------------------------------------------

    [Fact]
    public void WeekdayDip_FiresOnFridayCrashAndNamesTheWeekday()
    {
        var hist = Enumerable.Range(0, 28).Select(i =>
            Day(i, steps: AsOf(i).DayOfWeek == DayOfWeek.Friday ? 3000 : 8000)).ToList();
        var f = Finding(PatternEngine.Scan(Input(hist)), PatternKind.WeekdayActivityDip);
        Assert.NotNull(f);
        var ev = f!.EvidenceKeys.First(k => k.Key == "pattern.evidence.weekday-dip");
        Assert.Equal((int)DayOfWeek.Friday, ev.Args[0]);
        Assert.Equal(4, f.SampleCount);                          // 4 Fridays observed
    }

    [Fact]
    public void WeekdayDip_RefusesBelowFourSamplesPerWeekday()
    {
        // Dip exists but each weekday only appears ~2 times → gate (4) refuses.
        var hist = Enumerable.Range(0, 22).Select(i => Day(i,
            steps: i == 3 || i == 10 ? 500 : 8000)).ToList();
        var r = PatternEngine.Scan(Input(hist, asDay: 21));
        Assert.True(r.InsufficientKinds.Contains(PatternKind.WeekdayActivityDip)
                    || Finding(r, PatternKind.WeekdayActivityDip) is null);
    }

    [Fact]
    public void WeekdayDip_SilentWhenDipShallowerThanTwentyPercent()
    {
        var hist = Enumerable.Range(0, 28).Select(i => Day(i,
            steps: AsOf(i).DayOfWeek == DayOfWeek.Friday ? 7000 : 8000)).ToList();   // −14%
        Assert.Null(Finding(PatternEngine.Scan(Input(hist)), PatternKind.WeekdayActivityDip));
    }

    // ---- 3) FocusAfterPoorSleep ------------------------------------------------------

    /// <summary>Nine consecutive poor nights (18–26) whose NEXT days carry the low-energy
    /// aftermath. The 30-day recency-weighted sleep median stays at 450, so the deviation
    /// reference is honest, and 9 ≥ 8 consecutive pairs exist to test co-occurrence.</summary>
    private static List<DailyHistoryRecord> PoorSleepFixture() =>
        Enumerable.Range(0, 28).Select(i =>
        {
            bool poorNight = i >= 18 && i <= 26;
            bool aftermath = i >= 19 && i <= 27;
            return Day(i, sleep: poorNight ? 300 : 450,
                energy: aftermath ? 0.2 : 0.8, stress: aftermath ? 0.8 : 0.2);
        }).ToList();

    [Fact]
    public void FocusAfterPoorSleep_FiresOnHandBuiltFixture()
    {
        var f = Finding(PatternEngine.Scan(Input(PoorSleepFixture())), PatternKind.FocusAfterPoorSleep);
        Assert.NotNull(f);
        Assert.Equal(9, f!.SampleCount);          // 9 consecutive poor→next-day pairs
        var ev = f.EvidenceKeys.First(k => k.Key == "pattern.evidence.poor-sleep-focus");
        Assert.Equal(100, ev.Args[0]);            // every measured pair co-occurred
        Assert.Equal(9, ev.Args[1]);
        Assert.Equal(9, ev.Args[2]);
    }

    [Fact]
    public void FocusAfterPoorSleep_RefusesBelowEightPairs()
    {
        // Only 2 poor nights total → far below the 8-pair gate; silence via refusal.
        var hist = Enumerable.Range(0, 28).Select(i => Day(i, sleep: i is 5 or 12 ? 300 : 450)).ToList();
        var r = PatternEngine.Scan(Input(hist));
        Assert.Null(Finding(r, PatternKind.FocusAfterPoorSleep));
        Assert.Contains(PatternKind.FocusAfterPoorSleep, r.InsufficientKinds);
    }

    [Fact]
    public void FocusAfterPoorSleep_SilentWhenFocusStaysFlatAfterPoorNights()
    {
        // Poor nights exist in volume, but the NEXT day's focus never drops below the median:
        // energy/stress pinned at best on every day → co-occurrence 0% → no association.
        var hist = Enumerable.Range(0, 28).Select(i => Day(i,
            sleep: i % 2 == 1 ? 300 : 450, energy: 1.0, stress: 0.0)).ToList();
        Assert.Null(Finding(PatternEngine.Scan(Input(hist)), PatternKind.FocusAfterPoorSleep));
    }

    // ---- 4) HabitFailureWindow ---------------------------------------------------------

    [Fact]
    public void HabitFailure_FiresOnSaturdayMissCluster()
    {
        var hist = Flat(28);
        var h = new Habit { Id = "h", Name = "Read" };
        foreach (var d in hist)
            if (d.Date.DayOfWeek != DayOfWeek.Saturday) h.Complete(d.Date.AddHours(20));
        var f = Finding(PatternEngine.Scan(Input(hist, habits: new[] { h })), PatternKind.HabitFailureWindow);
        Assert.NotNull(f);
        var ev = f!.EvidenceKeys.First(k => k.Key == "pattern.evidence.habit-window");
        Assert.Equal((int)DayOfWeek.Saturday, ev.Args[0]);
        Assert.Equal(4, ev.Args[1]);   // 4 misses
        Assert.Equal(4, ev.Args[2]);   // of 4 expected Saturdays (100%)
    }

    [Fact]
    public void HabitFailure_RefusesWithoutHabitData()
    {
        var r = PatternEngine.Scan(Input(Flat(28)));   // zero habits supplied
        Assert.Null(Finding(r, PatternKind.HabitFailureWindow));
        Assert.Contains(PatternKind.HabitFailureWindow, r.InsufficientKinds);
    }

    [Fact]
    public void HabitFailure_SilentOnEvenCompletion()
    {
        var hist = Flat(28);
        var h = new Habit { Id = "h", Name = "Read" };
        foreach (var d in hist) h.Complete(d.Date.AddHours(20));
        Assert.Null(Finding(PatternEngine.Scan(Input(hist, habits: new[] { h })), PatternKind.HabitFailureWindow));
    }

    // ---- 5) WorkoutConsistency -----------------------------------------------------------

    [Fact]
    public void WorkoutTrend_FiresImprovingOnSixWeekRamp()
    {
        // Only COMPLETE-data weeks are judged; the ramp starts at the fourth full week.
        var hist = Enumerable.Range(0, 49).Select(i => Day(i, active: i < 28 ? 0 : 45)).ToList();
        var f = Finding(PatternEngine.Scan(Input(hist, asDay: 48)), PatternKind.WorkoutConsistency);
        Assert.NotNull(f);
        Assert.Equal(TrendDirection.Improving, f!.Trend);
        Assert.Equal(6, f.SampleCount);   // six complete weeks classified
    }

    [Fact]
    public void WorkoutTrend_SilentOnStableWeeks()
    {
        var r = PatternEngine.Scan(Input(Flat(49), asDay: 48));   // 30 active daily → six flat weeks
        Assert.Null(Finding(r, PatternKind.WorkoutConsistency));
    }

    [Fact]
    public void WorkoutTrend_RefusesBelowSixWeeks()
    {
        var hist = Enumerable.Range(0, 28).Select(i => Day(i, active: i < 14 ? 0 : 45)).ToList();
        var r = PatternEngine.Scan(Input(hist, asDay: 27));
        Assert.Contains(PatternKind.WorkoutConsistency, r.InsufficientKinds);
    }

    // ---- 6) GoalStagnation -----------------------------------------------------------------

    [Fact]
    public void GoalStagnation_FiresWhenProgressFlatNearDeadline()
    {
        var samples = Enumerable.Range(0, 28)
            .Select(i => new GoalProgressSample("g1", AsOf(i), 3, AsOf(40)))
            .ToList();
        var f = Finding(PatternEngine.Scan(Input(Flat(28), goals: samples)), PatternKind.GoalStagnation);
        Assert.NotNull(f);
        var ev = f!.EvidenceKeys.First(k => k.Key == "pattern.evidence.goal-stagnant");
        Assert.Equal(28, ev.Args[0]);   // flat for all 28 recorded days
        Assert.Equal(13, ev.Args[1]);   // Sep 1 + 40 = Oct 11; as-of Sep 28 → 13 days
    }

    [Fact]
    public void GoalStagnation_SilentWhenProgressMovesOrDeadlineIsFar()
    {
        var moving = Enumerable.Range(0, 28).Select(i => new GoalProgressSample("g1", AsOf(i), i % 5, AsOf(40))).ToList();
        Assert.Null(Finding(PatternEngine.Scan(Input(Flat(28), goals: moving)), PatternKind.GoalStagnation));

        var far = Enumerable.Range(0, 28).Select(i => new GoalProgressSample("g1", AsOf(i), 3, AsOf(120))).ToList();
        Assert.Null(Finding(PatternEngine.Scan(Input(Flat(28), goals: far)), PatternKind.GoalStagnation));
    }

    [Fact]
    public void GoalStagnation_RefusesWithoutGoalSamples()
    {
        var r = PatternEngine.Scan(Input(Flat(28)));
        Assert.Contains(PatternKind.GoalStagnation, r.InsufficientKinds);
    }

    // ---- 7) RecoveryActivityCoupling ----------------------------------------------------------

    /// <summary>recovery on day i tracks the PREVIOUS day's steps exactly → lag-1 r = 1.</summary>
    private static List<DailyHistoryRecord> CouplingFixture()
    {
        var steps = Enumerable.Range(0, 28).Select(i => 2000 + (i * 137 % 5) * 1500).ToList();
        return Enumerable.Range(0, 28).Select(i =>
        {
            double prev = i == 0 ? 5000 : steps[i - 1];
            return Day(i, steps: steps[i], recovery: 0.2 + prev / 10000.0);
        }).ToList();
    }

    [Fact]
    public void RecoveryCoupling_FiresOnStrongPositiveLagOneFixture()
    {
        var f = Finding(PatternEngine.Scan(Input(CouplingFixture())), PatternKind.RecoveryActivityCoupling);
        Assert.NotNull(f);
        var ev = f!.EvidenceKeys.First(k => k.Key == "pattern.evidence.recovery-activity");
        var r = Assert.IsType<double>(ev.Args[0]);
        Assert.True(r > 0.3, $"reported r={r} should be positive and above the 0.30 floor");
        Assert.True(f.Confidence <= 0.95, "coupling confidence must stay capped below certainty");
    }

    [Fact]
    public void RecoveryCoupling_RefusesBelowFourteenPairs()
    {
        var hist = Flat(22).ToList();
        for (int i = 2; i < 22; i++) hist[i].Steps = 0;   // kills most prev-day pairs
        var r = PatternEngine.Scan(Input(hist, asDay: 21));
        Assert.Contains(PatternKind.RecoveryActivityCoupling, r.InsufficientKinds);
    }

    [Fact]
    public void RecoveryCoupling_SilentOnConstantRecovery()
    {
        var hist = Enumerable.Range(0, 28).Select(i => Day(i, steps: 2000 + (i * 137 % 5) * 1500)).ToList();
        Assert.Null(Finding(PatternEngine.Scan(Input(hist)), PatternKind.RecoveryActivityCoupling));
    }

    [Theory]
    [InlineData(new double[] { 1, 2, 3, 4, 5 }, new double[] { 2, 4, 6, 8, 10 }, 1.0)]
    [InlineData(new double[] { 1, 2, 3, 4, 5 }, new double[] { 10, 8, 6, 4, 2 }, -1.0)]
    [InlineData(new double[] { 1, 2, 3 }, new double[] { 5, 5, 5 }, -999)]   // constant → null
    public void Pearson_KnownValues(double[] x, double[] y, double expected)
    {
        var r = PatternEngine.Pearson(x, y);
        if (expected == -999) Assert.Null(r);
        else Assert.Equal(expected, r!.Value, 6);
    }

    // ---- confidence plumbing -------------------------------------------------------------

    [Fact]
    public void Confidence_CappedBelowCertainty_AndScalesWithGateRatio()
    {
        Assert.Equal(0.95, PatternEngine.ConfidenceFrom(10, 100, 10), 6);   // hard cap
        Assert.Equal(0.50, PatternEngine.ConfidenceFrom(1.0, 5, 10), 6);    // half of the gate
        Assert.Equal(0.00, PatternEngine.ConfidenceFrom(0, 10, 10), 6);
    }

    // ---- HARD RULE: no causal language, ever ---------------------------------------------

    private static readonly string[] Forbidden =
    {
        "cause", "caus", "because", "leads to", "results in", "result in", "makes me", "reason",
        // Persian causal connectors: علت/باعث/موجب/منجر/نتیجه/زیرا/چون (since·because)
        "علت", "باعث", "موجب", "منجر", "نتیجه", "زیرا", "چون",
    };

    [Fact]
    public void NoCausalLanguage_EveryFindingOfEveryFixture_PassesTheSweep()
    {
        var fixtures = new[]
        {
            Input(LateSleepFixture()),
            Input(Enumerable.Range(0, 28).Select(i => Day(i, steps: AsOf(i).DayOfWeek == DayOfWeek.Friday ? 3000 : 8000)).ToList()),
            Input(PoorSleepFixture()),
            Input(Flat(28), habits: SaturdayMisses()),
            Input(Enumerable.Range(0, 49).Select(i => Day(i, active: i < 28 ? 0 : 45)).ToList(), asDay: 48),
            Input(Flat(28), goals: Enumerable.Range(0, 28).Select(i =>
                new GoalProgressSample("g1", AsOf(i), 3, AsOf(40))).ToList()),
            Input(CouplingFixture()),
        };

        int findingsChecked = 0;
        foreach (var input in fixtures)
        {
            foreach (var f in PatternEngine.Scan(input).Findings)
            {
                findingsChecked++;
                foreach (var ev in f.EvidenceKeys)
                {
                    Assert.True(
                        ev.Key.StartsWith("pattern.observed.", StringComparison.Ordinal) ||
                        ev.Key.StartsWith("pattern.evidence.", StringComparison.Ordinal),
                        $"evidence key '{ev.Key}' escapes the required prefixes");
                    var text = ev.Key + " " + string.Join(",", ev.Args);
                    foreach (var word in Forbidden)
                        Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase),
                            $"forbidden causal token '{word}' in evidence '{text}'");
                    Assert.Matches(new Regex(@"^pattern\.(observed|evidence)\.[a-z0-9.-]+$"), ev.Key);
                }
                // Every kind must carry at least one observed+one evidence key.
                Assert.Contains(f.EvidenceKeys, k => k.Key.StartsWith("pattern.observed.", StringComparison.Ordinal));
                Assert.Contains(f.EvidenceKeys, k => k.Key.StartsWith("pattern.evidence.", StringComparison.Ordinal));
            }
        }
        Assert.True(findingsChecked >= 7, $"sweep only saw {findingsChecked} findings — fixtures broke");
    }

    private static Habit[] SaturdayMisses()
    {
        var h = new Habit { Id = "h", Name = "Read" };
        foreach (var d in Flat(28))
            if (d.Date.DayOfWeek != DayOfWeek.Saturday) h.Complete(d.Date.AddHours(20));
        return new[] { h };
    }
}
