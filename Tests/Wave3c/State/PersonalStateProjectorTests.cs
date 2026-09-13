using LIVORA.Application.State;
using LIVORA.Application.State.Wave3b;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;
using static LIVORA.Tests.Wave3c.Lane04Fixtures;

namespace LIVORA.Tests.Wave3c;

/// <summary>Projection honesty + focus formula — PersonalStateProjector (lane 04).</summary>
public class PersonalStateProjectorTests
{
    private static readonly DateTime Today = new(2026, 9, 22);

    private static NormalizedDay FullDay(DataOrigin origin = DataOrigin.HealthConnect,
        double sleep = 450, double bedtime = 1380, double energy = 0.8, double stress = 0.2,
        double steps = 8000, double active = 30, double recovery = 0.7,
        double mood = 0.7, double quality = 0.75, double consistency = 0.8) => new()
    {
        Date = Today,
        Origin = origin,
        SleepMinutes = P(sleep, origin),
        SleepQuality = P(quality, origin),
        SleepConsistency = P(consistency, origin),
        BedtimeMinutesOfDay = P(bedtime, origin),
        WakeMinutesOfDay = P(540, origin),
        Steps = P(steps, origin),
        ActiveMinutes = P(active, origin),
        RecoveryScore = P(recovery, origin),
        Stress = P(stress, origin),
        Mood = P(mood, origin),
        Energy = P(energy, origin),
    };

    private static DataPoint P(double v, DataOrigin origin, DateTime? ts = null, DataQuality q = DataQuality.Complete) =>
        new() { Value = v, Timestamp = ts ?? Today, Origin = origin, Quality = q };

    private static PersonalState ProjectFull(NormalizedDay? today = null, BaselineSet? baselines = null,
        IReadOnlyList<DailyHistoryRecord>? history = null, bool noToday = false) =>
        PersonalStateProjector.Project(
            history ?? Flat(20),
            noToday ? null : today ?? FullDay(),
            new UserProfile(),
            baselines ?? AllHighSet(),
            trends: new TrendService());

    // ---- missing-data honesty (mandatory) ---------------------------------------

    [Fact]
    public void MissingToday_MetricsAreNaN_Missing_Unknown_NoZeroFills()
    {
        var state = ProjectFull(noToday: true);
        Assert.NotEmpty(state.Metrics);
        foreach (var m in state.Metrics.Values)
        {
            Assert.True(double.IsNaN(m.Value), $"{m.MetricKey} must be NaN, not zero-filled");
            Assert.NotEqual(0.0, m.Value);                 // a 0.0 here is exactly the lie
            Assert.Equal(DataQuality.Missing, m.Quality);
            Assert.Equal(StateLevel.Unknown, m.Level);     // never BelowBaseline-from-absence
            Assert.Null(m.RelativeDeviation);
        }
        Assert.Equal(0, state.DataCompleteness);
    }

    [Fact]
    public void SingleMissingField_OnlyThatMetricDegrades()
    {
        var day = FullDay();
        var broken = new NormalizedDay
        {
            Date = day.Date, Origin = day.Origin,
            SleepMinutes = DataPoint.Missing(Today),       // only sleep duration is absent
            SleepQuality = day.SleepQuality, SleepConsistency = day.SleepConsistency,
            BedtimeMinutesOfDay = day.BedtimeMinutesOfDay, WakeMinutesOfDay = day.WakeMinutesOfDay,
            Steps = day.Steps, ActiveMinutes = day.ActiveMinutes, RecoveryScore = day.RecoveryScore,
            Stress = day.Stress, Mood = day.Mood, Energy = day.Energy,
        };
        var state = ProjectFull(today: broken);
        Assert.True(double.IsNaN(state.Sleep.Duration.Value));
        Assert.Equal(StateLevel.Unknown, state.Sleep.Duration.Level);
        Assert.False(double.IsNaN(state.Wellness.Energy.Value)); // the rest survived honestly
        Assert.NotEqual(StateLevel.Unknown, state.Wellness.Energy.Level);
    }

    [Fact]
    public void RefusedBaseline_PropagatesNone_AndNoDeviation()
    {
        var set = new BaselineSet(new[] { Refused(Metrics.Steps, 2) });
        var state = ProjectFull(baselines: set);
        var steps = state.Activity.Steps;
        Assert.Equal(8000, steps.Value);                              // the measurement is real
        Assert.Equal(BaselineConfidence.None, steps.BaselineConfidence); // the baseline is not
        Assert.Null(steps.BaselineValue);
        Assert.Null(steps.RelativeDeviation);
        Assert.Equal(StateLevel.Unknown, steps.Level);                // comparison refused ⇒ no level
    }

    // ---- freshness ---------------------------------------------------------------

    [Fact]
    public void DaysSinceFreshData_ComesFromTheNewestRecord()
    {
        var history = new List<DailyHistoryRecord> { Day(0), Day(3) };  // newest = Epoch+3
        Assert.Equal(18, PersonalStateProjector.DaysSinceFreshData(history, Today));   // Sep 22 - Sep 4
        Assert.Equal(0, PersonalStateProjector.DaysSinceFreshData(
            new List<DailyHistoryRecord> { Day(21) }, Today));           // Epoch+21 = today
        Assert.Equal(PersonalStateProjector.NoDataSentinelDays,
            PersonalStateProjector.DaysSinceFreshData(new List<DailyHistoryRecord>(), null));
    }

    // ---- bedtime unwrap (must reuse BaselineService.UnwrapBedtime) -----------------

    [Fact]
    public void Bedtime_AcrossMidnight_DeviationUsesUnwrap_NotNaiveSubtraction()
    {
        // Baseline 1410 (23:30, already post-noon scale). Today 00:20 → wrap to 1460.
        // Correct deviation: +50/1410 ≈ +0.0355 → Normal (inside ±12% band).
        // Naive (20 − 1410)/1410 ≈ −0.986 → would scream BelowBaseline for a 50-min shift.
        var set = new BaselineSet(new[] { Usable(Metrics.BedtimeMinutes, 1410, 14) });
        var state = ProjectFull(today: FullDay(bedtime: 20), baselines: set);
        var bed = state.Sleep.Bedtime;
        Assert.NotNull(bed.RelativeDeviation);
        Assert.Equal(50.0 / 1410.0, bed.RelativeDeviation!.Value, 6);
        Assert.Equal(StateLevel.Normal, bed.Level);
        Assert.True(bed.Value < 60, "reported value stays the user's real 00:20");
    }

    // ---- focus formula (documented + boundaries) ------------------------------------

    [Fact]
    public void FocusFormula_IsTheDocumentedWeightedSum()
    {
        Assert.Equal(0.40, PersonalStateProjector.FocusWeightEnergy);
        Assert.Equal(0.35, PersonalStateProjector.FocusWeightStress);
        Assert.Equal(0.25, PersonalStateProjector.FocusWeightSleep);
        Assert.Equal(1.0, PersonalStateProjector.FocusWeightEnergy
            + PersonalStateProjector.FocusWeightStress + PersonalStateProjector.FocusWeightSleep, 10);
    }

    [Theory]
    [InlineData(0, 1, 0, 0.00)]   // worst energy, max stress, no sleep → hard floor
    [InlineData(1, 0, 1, 1.00)]   // best on every term → hard ceiling
    [InlineData(0.8, 0.2, 1.0, 0.85)]  // 0.32 + 0.28 + 0.25
    [InlineData(0.5, 0.5, 0.5, 0.50)]  // 0.20 + 0.175 + 0.125
    public void FocusBoundaries(double energy, double stress, double sleep, double expected)
        => Assert.Equal(expected, PersonalStateProjector.FocusValue(energy, stress, sleep), 6);

    [Theory]
    [InlineData(1.5, 0, 1)]      // out-of-range inputs cannot push focus above 1
    [InlineData(-1, 2, 0)]       // nor below 0
    public void Focus_ClampedToUnitInterval(double energy, double stress, double sleep)
    {
        var v = PersonalStateProjector.FocusValue(energy, stress, sleep);
        Assert.InRange(v, 0, 1);
    }

    [Theory]
    [InlineData(double.NaN, 0.2, 1.0)]
    [InlineData(0.8, double.NaN, 1.0)]
    [InlineData(0.8, 0.2, double.NaN)]
    public void Focus_AnyMissingInput_IsNaN_NeverAFabricatedMidpoint(double e, double s, double sn)
        => Assert.True(double.IsNaN(PersonalStateProjector.FocusValue(e, s, sn)));

    [Fact]
    public void Focus_IsDerivedAndEstimated()
    {
        var state = ProjectFull();
        Assert.True(state.Focus.IsDerived);
        Assert.Equal(DataQuality.Estimated, state.Focus.Estimated.Quality);
        Assert.Equal(0.85, state.Focus.Estimated.Value, 6);   // energy .8, stress .2, sleepNorm 1
        Assert.Null(state.Focus.Estimated.BaselineValue);     // no personal focus history claimed
    }

    [Fact]
    public void Focus_MissingEnergy_MetricReadsMissingNotZero()
    {
        var day = FullDay(energy: double.NaN);
        var withNaN = new NormalizedDay
        {
            Date = day.Date, Origin = day.Origin,
            SleepMinutes = day.SleepMinutes, SleepQuality = day.SleepQuality,
            SleepConsistency = day.SleepConsistency, BedtimeMinutesOfDay = day.BedtimeMinutesOfDay,
            WakeMinutesOfDay = day.WakeMinutesOfDay, Steps = day.Steps, ActiveMinutes = day.ActiveMinutes,
            RecoveryScore = day.RecoveryScore, Stress = day.Stress, Mood = day.Mood,
            Energy = DataPoint.Missing(Today),
        };
        var state = ProjectFull(today: withNaN);
        Assert.True(double.IsNaN(state.Focus.Estimated.Value));
        Assert.Equal(DataQuality.Missing, state.Focus.Estimated.Quality);
    }

    // ---- snapshots + overall confidence ---------------------------------------------

    [Fact]
    public void HabitAndGoalSnapshots_ComeFromPureParameters()
    {
        var habit = new Habit { Id = "h1", Name = "Walk" };
        habit.Complete(Today.AddDays(-2));
        habit.Complete(Today.AddDays(-1));
        var goal = new Goal { Id = "g1", Name = "Read", TargetValue = 10, ProgressValue = 4 };
        var state = PersonalStateProjector.Project(Flat(20), FullDay(), new UserProfile(), AllHighSet(),
            new[] { habit }, new[] { goal }, new TrendService());
        Assert.Single(state.HabitSnapshots);
        Assert.Equal("h1", state.Habits.HabitId);
        Assert.Equal(2, state.HabitSnapshots[0].Streak);
        Assert.Equal(2 / 30.0, state.HabitSnapshots[0].SuccessRate30d, 6);
        Assert.Single(state.GoalSnapshots);
        Assert.Equal(0.4, state.GoalSnapshots[0].Fraction, 6);
        Assert.Equal(GoalStatus.AtRisk, state.GoalSnapshots[0].Status);   // 0.3 ≤ f < 0.6
    }

    [Fact]
    public void FreshDeviceGradeFullDay_HighConfidence_FullAndFreshPinsAboveFloor()
    {
        var state = ProjectFull();
        Assert.Equal(0, state.Sleep.DaysSinceFreshData);
        Assert.True(state.Confidence >= 0.85, $"full+fresh+high projected {state.Confidence}");
    }

    [Fact]
    public void ManualOnlyDay_ConfidenceStaysBelowDeviceGrade()
    {
        var device = ProjectFull();
        var manual = ProjectFull(today: FullDay(origin: DataOrigin.Manual));
        Assert.True(manual.Confidence < device.Confidence,
            "a manual-only day must never read as confident as a device day");
    }
}
