using LIVORA.Application.Abstractions;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;

namespace LIVORA.Tests.Tests;

/// <summary>
/// <see cref="ProgramAdapter"/> had zero coverage before Wave 3 even though it is the seam that
/// keeps "what Today recommends" and "what the bootcamp actually schedules" from disagreeing.
/// The invariant that matters: an adaptation is only ever claimed when a REAL rule fired an
/// "exercise:" adjustment — and a non-intense day is never touched, because swapping a wind-down
/// for a walk would be a lie dressed up as care.
/// </summary>
public class ProgramAdapterTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0);
    private static readonly RuleEngine Rules = new();

    private static ProgramAdapter Sut() => new(Rules);

    private static Bootcamp Camp(string todayPlanKey) => new()
    {
        Id = "camp",
        CurrentDay = 2,
        DurationDays = 21,
        Days = Enumerable.Range(1, 21).Select(i => new BootcampDay
        {
            DayNumber = i,
            PlanTitleKey = i == 2 ? todayPlanKey : "Bootcamp.Plan.Walk",
            PlanDescriptionKey = "Bootcamp.Plan.Workout.Desc",
            TargetMinutes = 30,
        }).ToList(),
    };

    private static (BootcampDay, string?) Adapt(string planKey, double recovery = 0.7, double sleepMin = 450)
    {
        var camp = Camp(planKey);
        return Sut().AdaptDay(camp, camp.Today!,
            StateFactory.State(recovery: recovery, sleepMin: sleepMin, sleepBase: 450),
            new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
    }

    [Fact]
    public void IntenseDay_WithLowRecovery_IsAdaptedToALightWalk()
    {
        var (day, ruleKey) = Adapt("Bootcamp.Plan.Workout", recovery: 0.4);

        Assert.True(day.IsAdapted);
        Assert.Equal("Bootcamp.Plan.LightWalk", day.PlanTitleKey);
        Assert.Equal("Bootcamp.Plan.LightWalk.Desc", day.PlanDescriptionKey);
        Assert.Equal(10, day.TargetMinutes);
        Assert.Equal("Rule.LowRecoveryReduce", ruleKey);   // named, not implied
    }

    [Fact]
    public void IntenseDay_WithSleepDebt_IsAdaptedThroughTheSameRuleEngine()
    {
        var (day, ruleKey) = Adapt("Bootcamp.Plan.Workout", sleepMin: 330);
        Assert.True(day.IsAdapted);
        Assert.NotNull(ruleKey);
        Assert.StartsWith("Rule.", ruleKey!);
    }

    [Fact]
    public void CalmDay_IntenseSession_IsLeftAlone()
    {
        // Nothing fired => the planned day is returned by reference and no rule key is invented.
        var (day, ruleKey) = Adapt("Bootcamp.Plan.Workout", recovery: 0.72, sleepMin: 455);
        Assert.Null(ruleKey);
        Assert.False(day.IsAdapted);
        Assert.Equal("Bootcamp.Plan.Workout", day.PlanTitleKey);
        Assert.Equal(30, day.TargetMinutes);
    }

    [Theory]
    [InlineData("Bootcamp.Plan.Walk")]
    [InlineData("Bootcamp.Plan.WindDown")]
    [InlineData("Bootcamp.Plan.Reading")]
    public void NonIntenseDay_IsNeverAdapted_EvenOnATerribleDay(string planKey)
    {
        // Only "Bootcamp.Plan.Workout" is intensity-keyed. Downgrading a wind-down "for recovery"
        // would be the adapter claiming credit for an adaptation no rule asked for.
        var (day, ruleKey) = Adapt(planKey, recovery: 0.2);
        Assert.Null(ruleKey);
        Assert.False(day.IsAdapted);
        Assert.Equal(planKey, day.PlanTitleKey);
    }

    [Fact]
    public void CompletionState_SurvivesAdaptation()
    {
        var camp = Camp("Bootcamp.Plan.Workout");
        camp.Today!.IsCompleted = true;
        var (day, _) = Sut().AdaptDay(camp, camp.Today,
            StateFactory.State(recovery: 0.4), new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        Assert.True(day.IsCompleted,     // done is done, even if the shape changed
            "adapting a finished day must not silently un-finish it");
        Assert.Equal(2, day.DayNumber);
    }

    [Theory]
    [InlineData(0.4, true)]      // low recovery fires the exercise adjustment
    [InlineData(0.72, false)]    // a normal day must not be adapted
    public void AdapterAndRuleEngine_NeverDisagree(double recovery, bool expectAdaptation)
    {
        // The single-source-of-truth claim, stated exactly: the adapter may only adapt when a rule
        // that emits an "exercise:" adjustment actually fired — and never the other way round.
        var state = StateFactory.State(recovery: recovery, sleepMin: 455, sleepBase: 450);
        var exerciseRules = Rules.Evaluate(state, new UserProfile(), Array.Empty<Goal>(),
                Array.Empty<Habit>(), Now)
            .Where(r => r.PlanAdjustments.Any(a => a.StartsWith("exercise:", StringComparison.Ordinal)))
            .Select(r => r.RuleKey)
            .ToList();

        var camp = Camp("Bootcamp.Plan.Workout");
        var (day, ruleKey) = Sut().AdaptDay(camp, camp.Today!, state, new UserProfile(),
            Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        Assert.Equal(expectAdaptation, day.IsAdapted);
        Assert.Equal(exerciseRules.Count > 0, expectAdaptation);
        if (ruleKey is not null) Assert.Contains(ruleKey, exerciseRules);
    }

    [Fact]
    public void Adaptation_NeverInventedWhenNoRuleFired()
    {
        // The dangerous direction: a "we made today easier for you" message with no rule behind it.
        var state = StateFactory.State(sleepMin: 455, sleepBase: 450, recovery: 0.72, stress: 0.35);
        var camp = Camp("Bootcamp.Plan.Workout");
        var (day, ruleKey) = Sut().AdaptDay(camp, camp.Today!, state, new UserProfile(),
            Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        Assert.Null(ruleKey);
        Assert.False(day.IsAdapted);
        Assert.Same(camp.Today, day);   // literally the planned day, untouched
    }

    [Fact]
    public void AdaptedDay_EmitsOnlyKeys()
    {
        var (day, _) = Adapt("Bootcamp.Plan.Workout", recovery: 0.3);
        Assert.DoesNotContain(' ', day.PlanTitleKey);
        Assert.DoesNotContain(' ', day.PlanDescriptionKey);

        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;
        Assert.True(pair.En.ContainsKey(day.PlanTitleKey));
        Assert.True(pair.Fa.ContainsKey(day.PlanDescriptionKey));
    }
}

/// <summary>
/// The plan grammar ("exercise:*0.5" / "walk:+15min" / "focus:-1block" / "winddown:+30min") is a
/// machine protocol between RuleEngine and DailyPlanService. These tests cover the clauses the
/// existing suite does not: the walk/ winddown inserters, the Add-dedupe, and the invariant that a
/// rule with no adjustments may not appear in the explanation.
/// </summary>
public class DailyPlanGrammarTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0);

    private static DailyPlanService Sut(params Bootcamp[] camps) =>
        new(new RuleEngine(), new InMemoryRepo<Bootcamp>(camps));

    private static Bootcamp WorkoutCamp(int minutes = 40) => new()
    {
        Id = "c1", IsEnrolled = true, CurrentDay = 1, DurationDays = 5,
        Days = Enumerable.Range(1, 5).Select(i => new BootcampDay
        {
            DayNumber = i, PlanTitleKey = "Bootcamp.Plan.Workout",
            PlanDescriptionKey = "Bootcamp.Plan.Workout.Desc", TargetMinutes = minutes,
        }).ToList(),
    };

    [Fact]
    public async Task HighStress_AddsWalkAndWindDown_Items()
    {
        // Rule.HighStress carries "focus:-1block, recovery:+10min"; Rule.HighStressScreens carries
        // "winddown:+30min"; Rule.ActivityDeficit carries "walk:+15min". Combine them all.
        var plan = await Sut().BuildPlanAsync(
            StateFactory.State(stress: 0.85, steps: 1000, stepsBase: 8000, recovery: 0.7),
            new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        Assert.Contains(plan.Items, i => i.Action == RecommendationActionKind.ShortWalk && i.PlannedMinutes == 15);
        Assert.Contains(plan.Items, i => i.Action == RecommendationActionKind.WindDownBeforeBed && i.PlannedMinutes == 30);
        Assert.Contains(plan.Items, i => i.Category == RecommendationCategory.Recovery && i.PlannedMinutes == 10);
    }

    [Fact]
    public async Task SameAdjustmentTwice_AddsTheItemOnce()
    {
        // Both sleep rules emit "exercise:*0.5" and the stress pair emits overlapping recovery
        // blocks; Add() dedupes by (action, category, title) — no double-booking.
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, stress: 0.9, recovery: 0.3);
        var svc = Sut();
        var plan = await svc.BuildPlanAsync(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        Assert.Equal(1, plan.Items.Count(i => i.Category == RecommendationCategory.Recovery));
        Assert.Equal(1, plan.Items.Count(i => i.Action == RecommendationActionKind.EarlierBedtime));
    }

    [Fact]
    public async Task TotalCommittedMinutes_IsTheSumOfPlannedMinutes()
    {
        var plan = await Sut(WorkoutCamp()).BuildPlanAsync(
            StateFactory.State(recovery: 0.4), new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.Equal(plan.Items.Sum(i => i.PlannedMinutes), plan.TotalCommittedMinutes);
    }

    [Fact]
    public async Task ScaleDown_NeverInventsMinutes_AboveTheBase()
    {
        var plan = await Sut(WorkoutCamp(40)).BuildPlanAsync(
            StateFactory.State(recovery: 0.4), new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        var program = Assert.Single(plan.Items, i => i.Category == RecommendationCategory.Program);
        Assert.True(program.PlannedMinutes <= program.BaseMinutes,
            "adaptation may only reduce intensity in Wave 3 — never silently add load");
    }

    [Fact]
    public void AdaptationReasonKey_EachSingleRule_HasItsOwnSentence()
    {
        var svc = Sut();
        string Key(params string[] rules) => svc.AdaptationReasonKey(new DailyPlan
        {
            Date = Now.Date, Items = Array.Empty<PlanItem>(), AdaptationRuleKeys = rules,
        });

        Assert.Equal("Plan.NotAdapted", Key());
        Assert.Equal("Plan.Adapted.Sleep", Key("Rule.SleepDebt"));
        Assert.Equal("Plan.Adapted.Sleep", Key("Rule.SleepDebtReduceIntensity"));
        Assert.Equal("Plan.Adapted.Recovery", Key("Rule.LowRecoveryReduce"));
        Assert.Equal("Plan.Adapted.Stress", Key("Rule.HighStress"));
        Assert.Equal("Plan.Adapted.Stress", Key("Rule.HighStressScreens"));
        Assert.Equal("Plan.Adapted.Activity", Key("Rule.ActivityDeficit"));
        Assert.Equal("Plan.Adapted.Generic", Key("Rule.HabitAtRisk"));      // unknown but single
        Assert.Equal("Plan.Adapted.Generic", Key("Rule.StaleSleep"));       // guard-only
        Assert.Equal("Plan.Adapted.Multiple", Key("Rule.SleepDebt", "Rule.HighStress"));
    }

    [Fact]
    public void AdaptationReasonKey_NeverRepeatsARuleTwiceAsMultiple()
    {
        var svc = Sut();
        // The grammar is only ever handed distinct rule keys (each rule adds once) — but the shape
        // must still be explainable: 2 keys => "Multiple", no silent first-wins.
        var plan = new DailyPlan
        {
            Date = Now.Date,
            Items = Array.Empty<PlanItem>(),
            AdaptationRuleKeys = new[] { "Rule.HighStress", "Rule.HighStressScreens" },
        };
        Assert.Equal("Plan.Adapted.Multiple", svc.AdaptationReasonKey(plan));
    }

    [Theory]
    [InlineData("Plan.NotAdapted")]
    [InlineData("Plan.Adapted.Sleep")]
    [InlineData("Plan.Adapted.Recovery")]
    [InlineData("Plan.Adapted.Stress")]
    [InlineData("Plan.Adapted.Activity")]
    [InlineData("Plan.Adapted.Multiple")]
    [InlineData("Plan.Adapted.Generic")]
    public void EveryExplanationKey_TranslatesInBothLanguages(string key)
    {
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;
        Assert.True(pair.En.ContainsKey(key), $"{key} missing in EN");
        Assert.True(pair.Fa.ContainsKey(key), $"{key} missing in FA");
    }

    [Fact]
    public async Task RecommendationCap_DedupesByAction_NotByRule()
    {
        // Sleep debt fires two rules that both say "reduce intensity"; the user sees ONE card.
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, recovery: 0.3, stress: 0.9);
        var recs = new RecommendationService(new RuleEngine()).BuildRecommendations(
            state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), null, Now);

        Assert.True(recs.Count <= 3);
        Assert.Equal(recs.Count, recs.Select(r => r.ActionKind).Distinct().Count());
        await Task.CompletedTask;
    }

    [Fact]
    public void Recommendation_EmptyDay_StillAnchorsWithKeepRoutine()
    {
        var state = StateFactory.State(sleepMin: 455, sleepBase: 450, recovery: 0.72, stress: 0.35);
        var recs = new RecommendationService(new RuleEngine()).BuildRecommendations(
            state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), null, Now);
        var rec = Assert.Single(recs);
        Assert.Equal("Rec.KeepRoutine", rec.TextKey);
        Assert.Equal("Rule.Reason.AllNearBaseline", rec.ExplainKey);
        Assert.Equal("Rule.PositiveMomentum", rec.ProducedByRule);
    }
}
