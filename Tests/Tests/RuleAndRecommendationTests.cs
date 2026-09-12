using LIVORA.Application.Rules;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;
using StateMetrics = LIVORA.Domain.Models.State.Metrics;

namespace LIVORA.Tests.Tests;

/// <summary>Builds a synthetic PersonalState for deterministic rule tests.</summary>
internal static class StateFactory
{
    public static PersonalState State(
        double sleepMin = 450, double sleepBase = 450,
        double recovery = 0.7, double stress = 0.4, double steps = 8000, double stepsBase = 8000,
        BaselineConfidence sleepConfidence = BaselineConfidence.High,
        DataQuality sleepQuality = DataQuality.Complete,
        DataQuality recoveryQuality = DataQuality.Complete,
        int sleepDaysSinceFreshData = 0)
    {
        var m = new Dictionary<string, MetricState>
        {
            [StateMetrics.SleepMinutes] = Metric(StateMetrics.SleepMinutes, sleepMin, sleepBase, higherBetter: true,
                confidence: sleepConfidence, quality: sleepQuality),
            [StateMetrics.Steps] = Metric(StateMetrics.Steps, steps, stepsBase, higherBetter: true),
            [StateMetrics.RecoveryScore] = Metric(StateMetrics.RecoveryScore, recovery, 0.7, quality: recoveryQuality),
            [StateMetrics.Stress] = Metric(StateMetrics.Stress, stress, 0.4, higherBetter: false),
        };
        return new PersonalState
        {
            GeneratedAt = new DateTime(2026, 9, 11, 10, 0, 0),
            Confidence = 0.8,
            DataCompleteness = 1,
            Sleep = new SleepState
            {
                Duration = m[StateMetrics.SleepMinutes],
                Quality = Metric("q", 0.8, 0.8),
                Consistency = Metric("c", 0.8, 0.8),
                Bedtime = Metric("b", 1380, 1380),
                DaysSinceFreshData = sleepDaysSinceFreshData,
            },
            Activity = new DailyActivityState { Steps = m[StateMetrics.Steps], ActiveMinutes = Metric("am", 30, 30) },
            Recovery = new RecoveryState { Score = m[StateMetrics.RecoveryScore] },
            Wellness = new WellnessState2 { Stress = m[StateMetrics.Stress], Mood = Metric("md", 0.7, 0.7), Energy = Metric("en", 0.7, 0.7) },
            Focus = new FocusState { Estimated = Metric("fe", 0.7, 0) },
            Habits = new HabitStateSnapshot { HabitId = "", Name = "" },
            Metrics = m,
        };
    }

    private static MetricState Metric(string key, double value, double baseline, bool higherBetter = true,
        BaselineConfidence confidence = BaselineConfidence.High, DataQuality quality = DataQuality.Complete) => new()
    {
        MetricKey = key,
        Value = value,
        BaselineValue = baseline > 0 ? baseline : null,
        BaselineConfidence = baseline > 0 ? confidence : BaselineConfidence.None,
        RelativeDeviation = baseline > 0 ? (value - baseline) / baseline : null,
        HigherIsBetter = higherBetter,
        Quality = quality,
    };
}

public class RuleEngineTests
{
    private readonly RuleEngine _engine = new();
    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0);

    private static UserProfile Profile() => new();

    [Fact]
    public void SleepDeficit_FiresEarlierBedtime()
    {
        // 6h sleep vs 7.5h baseline = 90 min deficit (at the threshold -> fires)
        var state = StateFactory.State(sleepMin: 6 * 60, sleepBase: 7.5 * 60);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.Contains(fired, r => r.RuleKey == "Rule.SleepDebt");
        Assert.Contains(fired, r => r.RuleKey == "Rule.SleepDebtReduceIntensity");
    }

    [Fact]
    public void SmallSleepDelta_DoesNotFire()
    {
        var state = StateFactory.State(sleepMin: 440, sleepBase: 450);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.SleepDebt");
    }

    [Fact]
    public void HighStress_FiresBreakAndScreens()
    {
        var state = StateFactory.State(stress: 0.8);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.Contains(fired, r => r.RuleKey == "Rule.HighStress");
        Assert.Contains(fired, r => r.Action == RecommendationActionKind.ModerateScreenTime);
    }

    [Fact]
    public void LowRecovery_FiresGentleMovement()
    {
        var state = StateFactory.State(recovery: 0.4);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.Contains(fired, r => r.RuleKey == "Rule.LowRecoveryReduce");
    }

    [Fact]
    public void GreatDay_ProducesPositiveMomentumOnly()
    {
        var state = StateFactory.State(sleepMin: 455, sleepBase: 450, recovery: 0.72, stress: 0.35);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.All(fired, r => Assert.Equal("Rule.PositiveMomentum", r.RuleKey));
    }

    [Fact]
    public void HabitAtRisk_EveningOnly()
    {
        var habit = new Habit { Name = "Walk" };
        for (int i = 1; i <= 4; i++) habit.Complete(Now.AddDays(-i)); // streak of 4, none today
        var state = StateFactory.State();

        var morning = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), new[] { habit }, Now.Date.AddHours(9));
        Assert.DoesNotContain(morning, r => r.RuleKey == "Rule.HabitAtRisk");

        var evening = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), new[] { habit }, Now.Date.AddHours(19));
        Assert.Contains(evening, r => r.RuleKey == "Rule.HabitAtRisk");
    }

    [Fact]
    public void Determinism_SameInputsSameOrdering()
    {
        var state = StateFactory.State(sleepMin: 380, sleepBase: 450, stress: 0.7);
        var a = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        var b = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.Equal(a.Select(r => r.RuleKey), b.Select(r => r.RuleKey));
    }
}

public class RecommendationServiceTests
{
    [Fact]
    public void Caps_VisibleRecommendations()
    {
        var svc = new Application.Planning.RecommendationService(new RuleEngine());
        // Triple-bad day: sleep debt + low recovery + high stress => must still cap at 3 shown.
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, recovery: 0.35, stress: 0.8);
        var recs = svc.BuildRecommendations(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), null, Now);
        Assert.True(recs.Count <= 3, $"surfaced {recs.Count} recommendations — overwhelm guard broken");
    }

    [Fact]
    public void EveryRecommendation_IsExplainable()
    {
        var svc = new Application.Planning.RecommendationService(new RuleEngine());
        var state = StateFactory.State(sleepMin: 380, sleepBase: 450);
        var recs = svc.BuildRecommendations(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), null, Now);
        Assert.All(recs, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.TextKey));
            Assert.False(string.IsNullOrEmpty(r.ExplainKey));       // structured "why" mandatory
            Assert.False(string.IsNullOrEmpty(r.ProducedByRule));   // traceability mandatory
        });
    }

    [Fact]
    public void AtMostOneHighPriority()
    {
        var svc = new Application.Planning.RecommendationService(new RuleEngine());
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, recovery: 0.35, stress: 0.8);
        var recs = svc.BuildRecommendations(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), null, Now);
        Assert.True(recs.Count(r => r.ScoredPriority >= RecommendationPriority.High) <= 1);
    }

    [Fact]
    public void PerfectDay_KeepsRoutineAnchor()
    {
        var svc = new Application.Planning.RecommendationService(new RuleEngine());
        var state = StateFactory.State(sleepMin: 455, sleepBase: 450, recovery: 0.72, stress: 0.35);
        var recs = svc.BuildRecommendations(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), null, Now);
        Assert.Single(recs);
        Assert.Equal(RecommendationActionKind.KeepRoutine, recs[0].ActionKind);
    }

    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0);
}
