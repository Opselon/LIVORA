using LIVORA.Application.Insights;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;
using StateMetrics = LIVORA.Domain.Models.State.Metrics;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Wave 2 plan assembly: the skeleton (program day + habit prompts + focus blocks) must come
/// from state, and every resize must be traceable to a rule key.
/// </summary>
public class DailyPlanServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0);

    private static DailyPlanService Service(params Bootcamp[] bootcamps) =>
        new(new RuleEngine(), new InMemoryRepo<Bootcamp>(bootcamps));

    private static Bootcamp Enrolled(int targetMinutes = 40, int dayNumber = 3) => new()
    {
        Id = "camp-1",
        TitleKey = "Bootcamp.SleepFirst.Title",
        DurationDays = 21,
        CurrentDay = dayNumber,
        IsEnrolled = true,
        Days = Enumerable.Range(1, 21).Select(d => new BootcampDay
        {
            DayNumber = d,
            PlanTitleKey = "Bootcamp.Plan.Workout",
            PlanDescriptionKey = "Bootcamp.Plan.Workout.Desc",
            TargetMinutes = targetMinutes,
        }).ToList(),
    };

    [Fact]
    public async Task EnrolledBootcamp_ContributesProgramItem()
    {
        var plan = await Service(Enrolled()).BuildPlanAsync(
            StateFactory.State(), new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        var program = Assert.Single(plan.Items, i => i.Category == RecommendationCategory.Program);
        Assert.Equal(RecommendationActionKind.DoProgramDay, program.Action);
        Assert.Equal("Bootcamp.Plan.Workout", program.TitleKey);
        Assert.Equal("Bootcamp.Plan.Workout.Desc", program.DetailKey);
        Assert.Equal(40, program.BaseMinutes);
        Assert.Equal(40, program.PlannedMinutes);
        Assert.Equal("camp-1", program.LinkedId);
        Assert.False(program.WasAdapted);
    }

    [Fact]
    public async Task NotEnrolled_NoProgramItem()
    {
        var camp = Enrolled();
        camp.IsEnrolled = false;
        var plan = await Service(camp).BuildPlanAsync(
            StateFactory.State(), new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.DoesNotContain(plan.Items, i => i.Category == RecommendationCategory.Program);
    }

    [Fact]
    public async Task ProgramWithoutCurrentDay_ContributesNothing()
    {
        var camp = Enrolled();
        camp.CurrentDay = 0; // before day 1 -> Today is null
        var plan = await Service(camp).BuildPlanAsync(
            StateFactory.State(), new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.DoesNotContain(plan.Items, i => i.Category == RecommendationCategory.Program);
    }

    [Fact]
    public async Task LowRecovery_ResizesExerciseThroughGrammar()
    {
        // "exercise:*0.5" from Rule.LowRecoveryReduce must halve the program day, keep the base,
        // and name the rule that did it.
        var state = StateFactory.State(recovery: 0.4);
        var plan = await Service(Enrolled(targetMinutes: 40)).BuildPlanAsync(
            state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        var program = Assert.Single(plan.Items, i => i.Category == RecommendationCategory.Program);
        Assert.Equal(40, program.BaseMinutes);
        Assert.Equal(20, program.PlannedMinutes);
        Assert.True(program.PlannedMinutes < program.BaseMinutes);
        Assert.True(program.WasAdapted);
        Assert.Equal("Rule.LowRecoveryReduce", program.AdaptedByRule);

        Assert.True(plan.WasAdapted);
        Assert.Contains("Rule.LowRecoveryReduce", plan.AdaptationRuleKeys);
        Assert.Contains(plan.Items, i => i.Category == RecommendationCategory.Recovery && i.PlannedMinutes == 15);
        Assert.Equal("Plan.Adapted.Recovery", Service().AdaptationReasonKey(plan));
    }

    [Fact]
    public async Task SleepDebt_ResizesProgramAndAddsEarlierBedtime()
    {
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450); // 2h deficit
        var svc = Service(Enrolled(targetMinutes: 50));
        var plan = await svc.BuildPlanAsync(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        var program = Assert.Single(plan.Items, i => i.Category == RecommendationCategory.Program);
        Assert.Equal(50, program.BaseMinutes);
        Assert.Equal(25, program.PlannedMinutes);           // exercise:*0.5 grammar applied
        Assert.Contains(plan.Items, i => i.Action == RecommendationActionKind.EarlierBedtime);
        Assert.Contains("Rule.SleepDebt", plan.AdaptationRuleKeys);
        Assert.Contains("Rule.SleepDebtReduceIntensity", plan.AdaptationRuleKeys);
        // Two rules moved the plan: the honest explanation says "multiple", never picks one at random.
        Assert.Equal("Plan.Adapted.Multiple", new DailyPlanService(new RuleEngine(), new InMemoryRepo<Bootcamp>()).AdaptationReasonKey(plan));
    }

    [Fact]
    public async Task CalmDay_PlanIsNotAdapted()
    {
        var plan = await Service(Enrolled()).BuildPlanAsync(
            StateFactory.State(sleepMin: 455, sleepBase: 450, recovery: 0.72, stress: 0.35),
            new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        Assert.False(plan.WasAdapted);
        Assert.Empty(plan.AdaptationRuleKeys);                 // nothing resized => nothing to explain
        Assert.All(plan.Items, i => Assert.False(i.WasAdapted)); // every item still at base minutes
        Assert.Equal("Plan.NotAdapted", Service().AdaptationReasonKey(plan));
    }

    [Fact]
    public async Task AdaptedItem_AlwaysNamesItsRule()
    {
        // Explainability invariant: no silent resize. If minutes changed, the rule key exists.
        foreach (var state in new[]
        {
            StateFactory.State(recovery: 0.4),
            StateFactory.State(sleepMin: 330, sleepBase: 450),
            StateFactory.State(stress: 0.8),
        })
        {
            var plan = await Service(Enrolled()).BuildPlanAsync(
                state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
            Assert.All(plan.Items.Where(i => i.WasAdapted), i =>
            {
                Assert.False(string.IsNullOrEmpty(i.AdaptedByRule));
                Assert.Contains(i.AdaptedByRule!, plan.AdaptationRuleKeys);
            });
        }
    }

    [Fact]
    public async Task HighStress_FewerFocusBlocks_AndStillExplainsItself()
    {
        var calm = await Service().BuildPlanAsync(
            StateFactory.State(stress: 0.3), new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        var stressed = await Service().BuildPlanAsync(
            StateFactory.State(stress: 0.8), new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        var calmFocus = Assert.Single(calm.Items, i => i.Category == RecommendationCategory.Focus);
        var stressedFocus = Assert.Single(stressed.Items, i => i.Category == RecommendationCategory.Focus);

        Assert.Equal(150, calmFocus.BaseMinutes);              // 3 blocks x 50
        Assert.Equal(100, stressedFocus.BaseMinutes);          // 2 blocks x 50 (skeleton is state-scaled)
        Assert.True(stressedFocus.PlannedMinutes < stressedFocus.BaseMinutes); // "focus:-1block" on top
        Assert.Equal(75, stressedFocus.PlannedMinutes);
        Assert.Contains("Rule.HighStress", stressed.AdaptationRuleKeys);
    }

    [Fact]
    public async Task PendingHabits_BecomeScheduledPrompts_CappedAtTwo()
    {
        var habits = Enumerable.Range(1, 5).Select(i => new Habit { Id = $"h{i}", Name = $"H{i}" }).ToList();
        var plan = await Service().BuildPlanAsync(
            StateFactory.State(), new UserProfile(), Array.Empty<Goal>(), habits, Now);

        var prompts = plan.Items.Where(i => i.Category == RecommendationCategory.Habit).ToList();
        Assert.Equal(2, prompts.Count);
        Assert.All(prompts, p =>
        {
            Assert.Equal(RecommendationActionKind.CompleteHabit, p.Action);
            Assert.Equal(10, p.PlannedMinutes);
            Assert.False(string.IsNullOrEmpty(p.LinkedId));
        });
    }

    [Fact]
    public async Task CompletedHabits_AreNotPromptedAgain()
    {
        var done = new Habit { Id = "h1", Name = "Walk" };
        done.Complete(Now);
        var plan = await Service().BuildPlanAsync(
            StateFactory.State(), new UserProfile(), Array.Empty<Goal>(), new[] { done }, Now);
        Assert.DoesNotContain(plan.Items, i => i.Category == RecommendationCategory.Habit);
    }

    [Fact]
    public async Task HabitBestWindow_IsCarriedIntoThePlanItem()
    {
        var snap = new HabitStateSnapshot
        {
            HabitId = "h1", Name = "Walk", Streak = 2, BestCompletionWindow = new TimeSpan(7, 0, 0),
        };
        var state = StateFactory.State().WithHabitSnapshots(snap);
        var plan = await Service().BuildPlanAsync(
            state, new UserProfile(), Array.Empty<Goal>(), new[] { new Habit { Id = "h1", Name = "Walk" } }, Now);

        var prompt = Assert.Single(plan.Items, i => i.Category == RecommendationCategory.Habit);
        Assert.Equal(new TimeSpan(7, 0, 0), prompt.PreferredWindowStart);
    }

    [Fact]
    public async Task SameInputs_ProduceTheSamePlan()
    {
        var svc = Service(Enrolled());
        var state = StateFactory.State(recovery: 0.4, stress: 0.7, sleepMin: 380, sleepBase: 450);
        var a = await svc.BuildPlanAsync(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        var b = await svc.BuildPlanAsync(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        Assert.Equal(a.Items.Select(i => (i.TitleKey, i.BaseMinutes, i.PlannedMinutes, i.AdaptedByRule)),
                     b.Items.Select(i => (i.TitleKey, i.BaseMinutes, i.PlannedMinutes, i.AdaptedByRule)));
        Assert.Equal(a.AdaptationRuleKeys, b.AdaptationRuleKeys);
        Assert.Equal(a.TotalCommittedMinutes, b.TotalCommittedMinutes);
    }

    [Fact]
    public async Task StaleAndOldFeed_PlanStandsUnadapted_AndOnlyTheGuardFires()
    {
        // A sleep metric marked Stale (feed stopped) must not be laundered into
        // "go to bed earlier / train less" advice; only the honest stale guard may speak.
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450,
            sleepQuality: DataQuality.Stale, sleepDaysSinceFreshData: 5);
        var plan = await Service(Enrolled()).BuildPlanAsync(
            state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        Assert.DoesNotContain(plan.Items, i => i.Action == RecommendationActionKind.EarlierBedtime);
        Assert.DoesNotContain(plan.AdaptationRuleKeys, k => k.StartsWith("Rule.SleepDebt"));
        var program = Assert.Single(plan.Items, i => i.Category == RecommendationCategory.Program);
        Assert.Equal(program.BaseMinutes, program.PlannedMinutes);   // untouched
    }

    [Fact]
    public async Task UnknownState_PlanIsStillBuilt_AndNotAdapted()
    {
        // No baseline (Level Unknown) => no rule dares to resize. The day's skeleton stands.
        var state = StateFactory.State(sleepMin: 420, sleepBase: 0);
        Assert.Equal(StateLevel.Unknown, state.Metrics[StateMetrics.SleepMinutes].Level);

        var plan = await Service(Enrolled()).BuildPlanAsync(
            state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.NotEmpty(plan.Items);
        Assert.False(plan.WasAdapted);
        Assert.All(plan.Items, i => Assert.Equal(i.BaseMinutes, i.PlannedMinutes));
    }

    [Fact]
    public void AdaptationReasonKey_MultipleRules_SaysMultiple()
    {
        var svc = Service();
        var plan = new DailyPlan
        {
            Date = Now.Date,
            Items = Array.Empty<PlanItem>(),
            AdaptationRuleKeys = new[] { "Rule.LowRecoveryReduce", "Rule.HighStress" },
        };
        Assert.Equal("Plan.Adapted.Multiple", svc.AdaptationReasonKey(plan));
    }
}

/// <summary>Confidence + staleness gates on the rule engine itself.</summary>
public class RuleEngineHonestyTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0);
    private readonly RuleEngine _engine = new();

    private static UserProfile Profile() => new();

    [Theory]
    [InlineData(BaselineConfidence.Low, 0.6)]
    [InlineData(BaselineConfidence.Medium, 0.8)]
    [InlineData(BaselineConfidence.High, 0.9)]
    public void SleepDebt_FiresForAnyUsableBaseline_AndConfidenceTracksIt(BaselineConfidence conf, double expected)
    {
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, sleepConfidence: conf);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        var debt = Assert.Single(fired, r => r.RuleKey == "Rule.SleepDebt");
        Assert.Equal(expected, debt.Confidence);
        Assert.NotEmpty(debt.PlanAdjustments);
    }

    [Fact]
    public void SleepDebt_NoBaselineConfidence_DoesNotFire()
    {
        // "Confidence.None" means "we do not know your normal" — there is no deficit claim to make,
        // so the engine must stay silent instead of acting on a value with no certainty.
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, sleepConfidence: BaselineConfidence.None);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.SleepDebt");
        Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.SleepDebtReduceIntensity");
    }

    [Fact]
    public void StaleSleepFeed_FiresStaleSleepGuard()
    {
        var state = StateFactory.State(sleepDaysSinceFreshData: 5);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        var stale = Assert.Single(fired, r => r.RuleKey == "Rule.StaleSleep");
        Assert.Equal(5, Assert.Single(stale.ConditionArgs));
        Assert.Equal(RecommendationActionKind.None, stale.Action);   // guard, not nagging
        Assert.Empty(stale.PlanAdjustments);                          // never resizes the plan
        Assert.Equal(RecommendationPriority.Optional, stale.Priority);
    }

    [Fact]
    public void FreshSleepFeed_StaleGuardSilent()
    {
        foreach (var days in new[] { 0, 1, 2 })
        {
            var state = StateFactory.State(sleepDaysSinceFreshData: days);
            var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
            Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.StaleSleep");
        }
    }

    [Theory]
    [InlineData(DataQuality.Stale)]
    [InlineData(DataQuality.Invalid)]
    [InlineData(DataQuality.Missing)]
    public void NonCompleteSleep_NeverDrivesActionRules(DataQuality quality)
    {
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, sleepQuality: quality);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.SleepDebt");
        Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.SleepDebtReduceIntensity");
        Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.PositiveMomentum");
    }

    [Theory]
    [InlineData(DataQuality.Stale)]
    [InlineData(DataQuality.Invalid)]
    public void NonCompleteRecovery_NeverDrivesActionRules(DataQuality quality)
    {
        var state = StateFactory.State(recovery: 0.4, recoveryQuality: quality);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.LowRecoveryReduce");
    }

    [Fact]
    public void EveryFiredRule_IsTraceableAndExplained()
    {
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, recovery: 0.4, stress: 0.8, sleepDaysSinceFreshData: 4);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.NotEmpty(fired);
        Assert.All(fired, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.RuleKey));
            Assert.False(string.IsNullOrEmpty(r.ConditionKey));   // the "why" is structured, always
            Assert.True(r.Confidence > 0 && r.Confidence <= 1.0); // never zero-certainty, never overclaim
        });
    }

    [Fact]
    public void PriorityOrdering_IsStable()
    {
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, recovery: 0.4, stress: 0.9);
        var fired = _engine.Evaluate(state, Profile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        var priorities = fired.Select(r => (int)r.Priority).ToList();
        Assert.Equal(priorities.OrderByDescending(x => x), priorities);
    }
}
