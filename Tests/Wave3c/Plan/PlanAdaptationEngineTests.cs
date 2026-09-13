using LIVORA.Application.Planning.Adaptive;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using static LIVORA.Tests.Wave3c.Plan.Wave3cFixtures;

namespace LIVORA.Tests.Wave3c.Plan;

/// <summary>
/// Lane 05: the adaptive plan engine. Ladder arithmetic, floors, gates, precedence,
/// idempotence, determinism and the never-over-1.2x cap.
/// </summary>
public class PlanAdaptationEngineTests
{
    private static readonly PlanAdaptationEngine Engine = new();
    private static readonly DateTime Now = Wave3cFixtures.Now;

    private static PlanItem At(DailyPlan p, RecommendationCategory cat) =>
        p.Items.First(i => i.Category == cat);

    // ---- Scenario A: normal day => zero adaptations ------------------------

    [Fact]
    public void ScenarioA_NormalDay_ProducesZeroAdaptations()
    {
        var plan = StandardPlan();
        var before = PlanFingerprint(plan);

        var result = Engine.Adapt(plan, State(), Now);

        Assert.Empty(result.Adaptations);
        Assert.False(result.Changed);
        Assert.Equal(before, PlanFingerprint(result.Plan));   // byte-equal for a normal day
    }

    // ---- wave3b.recovery-low: ladder exact numbers -------------------------

    [Fact]
    public void RecoveryLow_FirstLadderStep_60Becomes45()
    {
        // recovery 0.5 vs baseline 0.7, High confidence => BelowBaseline (-28.6%).
        var result = Engine.Adapt(StandardPlan(), State(recoveryValue: 0.5), Now);

        var program = At(result.Plan, RecommendationCategory.Program);
        Assert.Equal(60, program.BaseMinutes);
        Assert.Equal(45, program.PlannedMinutes);   // ladder step 1
        Assert.Equal(PlanAdaptationRuleKeys.RecoveryLow, program.AdaptedByRule);
        Assert.Contains(PlanAdaptationRuleKeys.RecoveryLow, result.Plan.AdaptationRuleKeys);
    }

    [Fact]
    public void RecoveryLow_RespectsCurrentValue_NeverRaises()
    {
        // Activity block 40 < 45: the ladder step must NOT raise it.
        var result = Engine.Adapt(StandardPlan(), State(recoveryValue: 0.5), Now);

        var activity = At(result.Plan, RecommendationCategory.Activity);
        Assert.Equal(40, activity.PlannedMinutes);
        Assert.False(activity.WasAdapted);
    }

    [Fact]
    public void RecoveryLow_AddsOneRecoveryItem_15Minutes()
    {
        var result = Engine.Adapt(StandardPlan(), State(recoveryValue: 0.5), Now);

        var recovery = Assert.Single(result.Plan.Items,
            i => i.Category == RecommendationCategory.Recovery);
        Assert.Equal(15, recovery.BaseMinutes);
        Assert.Equal(15, recovery.PlannedMinutes);
        Assert.Contains(result.Adaptations, a => a.ChangeKey == "Plan.Change.AddRecovery");
    }

    [Fact]
    public void RecoveryLow_EvidenceCarriesSignedBadnessPercent()
    {
        // 0.5 vs 0.7 baseline, higher-is-better => signed badness +28.57% => 29.
        var result = Engine.Adapt(StandardPlan(), State(recoveryValue: 0.5), Now);

        var shrink = Assert.Single(result.Adaptations,
            a => a.ChangeKey == "Plan.Change.ShrinkExercise");
        Assert.Equal("Plan.Evidence.RecoveryBelowBaseline", shrink.EvidenceKey);
        Assert.Equal(29, shrink.EvidenceArgs[0]);
        Assert.Equal(60, shrink.ChangeArgs[0]);     // base
        Assert.Equal(45, shrink.ChangeArgs[1]);     // new
        Assert.Equal(PlanAdaptationRuleKeys.RecoveryLow, shrink.SourceRuleKey);
    }

    [Fact]
    public void RecoveryLow_ConfidenceIsMinOfUsedInputs_AndCarriesEvidenceConfidence()
    {
        // Medium baseline (tier 0.75) vs state confidence 0.85 => min = 0.75.
        var state = State(recoveryValue: 0.5, recoveryConfidence: BaselineConfidence.Medium,
            stateConfidence: 0.85);
        var result = Engine.Adapt(StandardPlan(), state, Now);

        var shrink = Assert.Single(result.Adaptations,
            a => a.ChangeKey == "Plan.Change.ShrinkExercise");
        Assert.Equal(0.75, shrink.Confidence, 3);
        Assert.Equal(BaselineConfidence.Medium, shrink.EvidenceConfidence);
    }

    // ---- wave3b.high-activity-yesterday ------------------------------------

    [Fact]
    public void HighActivity_Alone_OnlyAddsRecoveryAwareNote_NoMinutesChange()
    {
        // steps 10500 vs 8000 = +31.25% >= 25%.
        var state = State(steps: 10500);
        var plan = StandardPlan();

        var result = Engine.Adapt(plan, state, Now);

        Assert.Equal(ItemsFingerprint(plan), ItemsFingerprint(result.Plan)); // note only
        var note = Assert.Single(result.Adaptations);
        Assert.Equal("Plan.Change.RecoveryAware", note.ChangeKey);
        Assert.Equal("Plan.Evidence.HighActivityYesterday", note.EvidenceKey);
        Assert.Equal(PlanAdaptationRuleKeys.HighActivity, note.SourceRuleKey);
        Assert.Equal(31, note.EvidenceArgs[0]);
    }

    [Fact]
    public void HighActivity_WithRecoveryLow_PermitsSecondLadderStep_30()
    {
        var state = State(recoveryValue: 0.5, steps: 10500);
        var result = Engine.Adapt(StandardPlan(), state, Now);

        Assert.Equal(30, At(result.Plan, RecommendationCategory.Program).PlannedMinutes);
        Assert.Equal(30, At(result.Plan, RecommendationCategory.Activity).PlannedMinutes);
        Assert.Contains(result.Adaptations, a => a.ChangeKey == "Plan.Change.RecoveryAware");
    }

    [Fact]
    public void HighActivity_BelowTwentyFivePercent_DoesNotFire()
    {
        var state = State(recoveryValue: 0.5, steps: 9600);  // +20% < +25%
        var result = Engine.Adapt(StandardPlan(), state, Now);

        Assert.DoesNotContain(result.Adaptations, a => a.SourceRuleKey == PlanAdaptationRuleKeys.HighActivity);
        Assert.Equal(45, At(result.Plan, RecommendationCategory.Program).PlannedMinutes); // first step only
    }

    // ---- wave3b.sleep-low ----------------------------------------------------

    [Fact]
    public void ScenarioB_PoorSleep_FiresSleepRuleShiftsEarliestFocusClampedAtSeven()
    {
        // 300 min vs 450 baseline = -33% <= -15%, High confidence. Focus starts 08:00.
        var state = State(sleepMinutes: 300);
        var result = Engine.Adapt(StandardPlan(), state, Now);

        var focus = At(result.Plan, RecommendationCategory.Focus);
        Assert.Equal(new TimeSpan(7, 0, 0), focus.PreferredWindowStart);  // 2h earlier clamped to 07:00
        var shift = Assert.Single(result.Adaptations, a => a.ChangeKey == "Plan.Change.ShiftFocusEarlier");
        Assert.Equal(60, shift.ChangeArgs[0]);                             // moved 60 min (not 120)
        Assert.Equal("Plan.Evidence.SleepBelowBaseline", shift.EvidenceKey);
        Assert.Equal(33, shift.EvidenceArgs[0]);                           // signed badness +33%
        Assert.Contains(PlanAdaptationRuleKeys.SleepLow, result.Plan.AdaptationRuleKeys);
    }

    [Fact]
    public void SleepLow_EarliestBlockWins_UnclampedShiftIs120Minutes()
    {
        // Two focus blocks: earliest at 10:00 shifts to 08:00 (full 2h).
        var plan = new DailyPlan
        {
            Date = Now.Date,
            Items = new List<PlanItem>
            {
                Item(RecommendationCategory.Focus, 100, action: RecommendationActionKind.ProtectFocusBlocks,
                    start: new TimeSpan(12, 0, 0)),
                Item(RecommendationCategory.Focus, 100, action: RecommendationActionKind.ProtectFocusBlocks,
                    start: new TimeSpan(10, 0, 0)),
            },
        };
        var result = Engine.Adapt(plan, State(sleepMinutes: 300), Now);

        var shift = Assert.Single(result.Adaptations, a => a.ChangeKey == "Plan.Change.ShiftFocusEarlier");
        Assert.Equal(120, shift.ChangeArgs[0]);
        Assert.Equal(new TimeSpan(8, 0, 0), result.Plan.Items[1].PreferredWindowStart);
        Assert.Equal(new TimeSpan(12, 0, 0), result.Plan.Items[0].PreferredWindowStart); // later one untouched
    }

    [Fact]
    public void SleepLow_AddsOneWindDownItem_20MinutesEvening()
    {
        var result = Engine.Adapt(StandardPlan(), State(sleepMinutes: 300), Now);

        var wind = Assert.Single(result.Plan.Items,
            i => i.Action == RecommendationActionKind.WindDownBeforeBed);
        Assert.Equal(20, wind.BaseMinutes);
        Assert.Equal(20, wind.PlannedMinutes);
        Assert.Equal(new TimeSpan(21, 30, 0), wind.PreferredWindowStart);
        Assert.Contains(result.Adaptations, a => a.ChangeKey == "Plan.Change.AddWindDown");
    }

    [Fact]
    public void SleepLow_MildDeficit_UnderFifteenPercent_DoesNotFire()
    {
        // 405 vs 450 = -10% => below the -15% trigger (even though Level says BelowBaseline).
        var result = Engine.Adapt(StandardPlan(), State(sleepMinutes: 405), Now);
        Assert.DoesNotContain(result.Adaptations,
            a => a.SourceRuleKey == PlanAdaptationRuleKeys.SleepLow);
    }

    // ---- wave3b.stale-data ----------------------------------------------------

    [Fact]
    public void StaleData_NoteOnlyNoIntensityChange_Confidence030()
    {
        var plan = StandardPlan();
        var before = ItemsFingerprint(plan);

        var result = Engine.Adapt(plan, State(sleepDaysSinceFreshData: 2), Now);

        Assert.Equal(before, ItemsFingerprint(result.Plan));       // NO intensity change
        Assert.Equal(plan.TotalCommittedMinutes, result.Plan.TotalCommittedMinutes);
        var note = Assert.Single(result.Adaptations);
        Assert.Equal("Plan.Change.KeptStale", note.ChangeKey);
        Assert.Equal("Plan.Evidence.StaleData", note.EvidenceKey);
        Assert.Equal(PlanAdaptationRuleKeys.StaleData, note.SourceRuleKey);
        Assert.Equal(0.3, note.Confidence, 5);
        Assert.Equal(2, note.EvidenceArgs[0]);
    }

    [Fact]
    public void StaleData_OneDayFreshGap_DoesNotFire()
    {
        var result = Engine.Adapt(StandardPlan(), State(sleepDaysSinceFreshData: 1), Now);
        Assert.DoesNotContain(result.Adaptations,
            a => a.SourceRuleKey == PlanAdaptationRuleKeys.StaleData);
    }

    // ---- wave3b.deadline-risk: 15-min floor, push-back precedence -------------

    [Fact]
    public void DeadlineRisk_ProtectsLinkedItemTo15MinuteFloor()
    {
        // Program day (linked goal-workout) already at 10 min; goal due in 10 days at 30%.
        var plan = StandardPlan(programBase: 60, programPlanned: 10);
        var goals = new[] { WorkoutGoal(Now.AddDays(10), fraction: 0.3) };

        var result = new PlanAdaptationEngine(goals).Adapt(plan, State(), Now);

        var program = At(result.Plan, RecommendationCategory.Program);
        Assert.Equal(15, program.PlannedMinutes);                 // floor 15 exact
        Assert.Equal(60, program.BaseMinutes);                    // never above base
        var protect = Assert.Single(result.Adaptations);
        Assert.Equal("Plan.Change.ProtectDeadlineItem", protect.ChangeKey);
        Assert.Equal(15, protect.ChangeArgs[0]);
        Assert.Equal("Plan.Evidence.DeadlineRisk", protect.EvidenceKey);
        Assert.Equal(10, protect.EvidenceArgs[0]);                // days left
        Assert.Equal(30, protect.EvidenceArgs[1]);                // fraction percent
        Assert.Equal(PlanAdaptationRuleKeys.DeadlineRisk, protect.SourceRuleKey);
    }

    [Fact]
    public void DeadlineRisk_DeadlineFarOrFractionHigh_ProtectsNothing()
    {
        var plan = StandardPlan(programBase: 60, programPlanned: 10);
        var far = new[] { WorkoutGoal(Now.AddDays(45), fraction: 0.3) };
        var done = new[] { WorkoutGoal(Now.AddDays(10), fraction: 0.6) };

        Assert.Empty(new PlanAdaptationEngine(far).Adapt(plan, State(), Now).Adaptations);
        Assert.Empty(new PlanAdaptationEngine(done).Adapt(plan, State(), Now).Adaptations);
    }

    [Fact]
    public void DeadlineRisk_Precedence_RunsAfterShrinkAndPushesItBack()
    {
        // Shrink runs first but refuses to go below the floor; protection then pushes the
        // sub-floor result UP to exactly 15 — documented precedence: protection outranks reduction.
        var plan = StandardPlan(programBase: 60, programPlanned: 14);
        var goals = new[] { WorkoutGoal(Now.AddDays(5), fraction: 0.1) };

        var result = new PlanAdaptationEngine(goals).Adapt(plan, State(recoveryValue: 0.5), Now);

        Assert.Equal(15, At(result.Plan, RecommendationCategory.Program).PlannedMinutes);
        Assert.Contains(result.Adaptations, a => a.SourceRuleKey == PlanAdaptationRuleKeys.RecoveryLow);
        Assert.Contains(result.Adaptations, a => a.SourceRuleKey == PlanAdaptationRuleKeys.DeadlineRisk);
        // Recovery ordering: every shrink adaptation precedes every protection adaptation.
        var ruleOrder = result.Adaptations.Select(a => a.SourceRuleKey).ToList();
        Assert.True(ruleOrder.IndexOf(PlanAdaptationRuleKeys.RecoveryLow)
                    < ruleOrder.IndexOf(PlanAdaptationRuleKeys.DeadlineRisk));
    }

    [Fact]
    public void DeadlineRisk_FloorNeverExceedsBase()
    {
        // Linked item with base 10 (< floor 15): protection must leave it at base, not inflate.
        var plan = StandardPlan(programBase: 10, programPlanned: 10);
        var goals = new[] { WorkoutGoal(Now.AddDays(5), fraction: 0.1) };

        var result = new PlanAdaptationEngine(goals).Adapt(plan, State(), Now);

        Assert.Equal(10, At(result.Plan, RecommendationCategory.Program).PlannedMinutes);
        Assert.DoesNotContain(result.Adaptations,
            a => a.SourceRuleKey == PlanAdaptationRuleKeys.DeadlineRisk);
    }

    // ---- baseline gating -------------------------------------------------------

    [Fact]
    public void InsufficientBaseline_Low_Confidence_BlocksEveryRule()
    {
        // Everything looks terrible — but each metric carries a LOW-confidence baseline.
        var state = State(
            recoveryValue: 0.30, recoveryConfidence: BaselineConfidence.Low,
            sleepMinutes: 240, sleepConfidence: BaselineConfidence.Low,
            steps: 15000, stepsConfidence: BaselineConfidence.Low);

        var result = Engine.Adapt(StandardPlan(), state, Now);

        Assert.Empty(result.Adaptations);
        Assert.False(result.Changed);
    }

    [Fact]
    public void BadLookingTwoDayHistory_ProducesZeroAdaptations()
    {
        // The two-day-history scenario: <3 samples => BaselineConfidence.None => no metric is
        // allowed to reshape the day, no matter how bad the numbers look.
        var state = State(
            recoveryValue: 0.35, recoveryConfidence: BaselineConfidence.None,
            sleepMinutes: 250, sleepConfidence: BaselineConfidence.None,
            steps: 200, stepsConfidence: BaselineConfidence.None);
        var plan = StandardPlan();

        var result = Engine.Adapt(plan, state, Now);

        Assert.Empty(result.Adaptations);
        Assert.Equal(PlanFingerprint(plan), PlanFingerprint(result.Plan));
    }

    // ---- invariants --------------------------------------------------------------

    [Fact]
    public void CommittedMinutesCap_AdditionThatWouldBreak1Point2x_IsSkipped()
    {
        // Base total 100 => cap 120. Focus planned at 112 already; a 15-min recovery add (127)
        // would break the cap => skipped entirely.
        var plan = new DailyPlan
        {
            Date = Now.Date,
            Items = new List<PlanItem>
            {
                Item(RecommendationCategory.Focus, 100, plannedMin: 112,
                    action: RecommendationActionKind.ProtectFocusBlocks, start: new TimeSpan(8, 0, 0)),
            },
        };

        var result = Engine.Adapt(plan, State(recoveryValue: 0.5), Now);

        Assert.DoesNotContain(result.Adaptations, a => a.ChangeKey == "Plan.Change.AddRecovery");
        Assert.True(result.Plan.TotalCommittedMinutes <= 100 * 1.2 + 1e-9);
    }

    [Fact]
    public void CommittedMinutesCap_WindDownRespectsTheCap()
    {
        // Base 100: focus shift only (no minutes), wind-down +20 => 120 == cap, allowed exactly.
        var plan = new DailyPlan
        {
            Date = Now.Date,
            Items = new List<PlanItem>
            {
                Item(RecommendationCategory.Focus, 100, action: RecommendationActionKind.ProtectFocusBlocks,
                    start: new TimeSpan(8, 0, 0)),
            },
        };
        var result = Engine.Adapt(plan, State(sleepMinutes: 300), Now);

        Assert.Equal(120, result.Plan.TotalCommittedMinutes);   // exactly base * 1.2, never above
        Assert.Single(result.Plan.Items, i => i.Action == RecommendationActionKind.WindDownBeforeBed);
    }

    [Fact]
    public void Idempotent_AdaptingAnAdaptedPlanAddsNothing()
    {
        var once = Engine.Adapt(StandardPlan(), State(recoveryValue: 0.5, steps: 10500), Now);
        var fingerprintAfterFirst = PlanFingerprint(once.Plan);

        var twice = Engine.Adapt(once.Plan, State(recoveryValue: 0.5, steps: 10500), Now);

        Assert.Empty(twice.Adaptations);
        Assert.False(twice.Changed);
        Assert.Equal(fingerprintAfterFirst, PlanFingerprint(twice.Plan));   // deep-equal
    }

    [Fact]
    public void Deterministic_SameInputsTwice_GiveIdenticalFingerprintsAndIds()
    {
        var state = State(recoveryValue: 0.5, sleepMinutes: 300, steps: 10500);
        var a = Engine.Adapt(StandardPlan(), state, Now);
        var b = Engine.Adapt(StandardPlan(), state, Now);

        Assert.Equal(PlanFingerprint(a.Plan), PlanFingerprint(b.Plan));
        Assert.Equal(a.Adaptations.Select(x => x.Id), b.Adaptations.Select(x => x.Id));
    }

    [Fact]
    public void InputPlanIsNeverMutated_DeepCloneBeforeChange()
    {
        var plan = StandardPlan();
        var before = PlanFingerprint(plan);
        var itemsBefore = plan.Items.Count;

        Engine.Adapt(plan, State(recoveryValue: 0.5, sleepMinutes: 300, steps: 10500), Now);

        Assert.Equal(before, PlanFingerprint(plan));
        Assert.Equal(itemsBefore, plan.Items.Count);
        Assert.Empty(plan.AdaptationRuleKeys);
    }

    // ---- eight-fixture invariant sweep -----------------------------------------

    /// <summary>Eight fixtures incl. the three named scenarios; no plan may break invariants.</summary>
    public static TheoryData<string> Fixtures() => new()
    {
        "A_normal", "B_poor_sleep", "C_high_activity", "D_recovery_low",
        "E_stale", "F_recovery_plus_activity", "G_insufficient_baseline", "H_deadline_pre_shrunk",
        "I_multi_signal",
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixtureSweep_EveryAdaptation_IsExplainable_AndCapHolds(string fixture)
    {
        var (state, plan, goals) = Fixture(fixture);
        var engine = goals is null ? Engine : new PlanAdaptationEngine(goals);

        var result = engine.Adapt(plan, state, Now);

        Assert.All(result.Adaptations, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.EvidenceKey));
            Assert.False(string.IsNullOrWhiteSpace(a.SourceRuleKey));
            Assert.False(string.IsNullOrWhiteSpace(a.ChangeKey));
            Assert.False(string.IsNullOrWhiteSpace(a.Id));
            Assert.True(a.Confidence > 0, $"{a.SourceRuleKey} Confidence must be > 0");
            Assert.True(PlanAdaptationRuleKeys.All.Contains(a.SourceRuleKey),
                $"unknown rule {a.SourceRuleKey}");
        });
        // Every difference from the input plan must be covered by an adaptation or a rule key.
        Assert.True(result.Plan.TotalCommittedMinutes <= plan.Items.Sum(i => i.BaseMinutes) * 1.2 + 1e-9);

        // Named scenario pins:
        if (fixture == "A_normal") Assert.Empty(result.Adaptations);
        if (fixture == "B_poor_sleep")
        {
            var sleep = result.Adaptations.First(a => a.SourceRuleKey == PlanAdaptationRuleKeys.SleepLow);
            Assert.Equal("Plan.Evidence.SleepBelowBaseline", sleep.EvidenceKey);
        }
        if (fixture == "C_high_activity")
            Assert.Contains(result.Adaptations,
                a => a.ChangeKey == "Plan.Change.RecoveryAware");
    }

    private static (Domain.Models.State.PersonalState, DailyPlan, IReadOnlyList<Goal>?) Fixture(string name) => name switch
    {
        "A_normal" => (State(), StandardPlan(), null),
        "B_poor_sleep" => (State(sleepMinutes: 300), StandardPlan(), null),
        "C_high_activity" => (State(steps: 10500), StandardPlan(), null),
        "D_recovery_low" => (State(recoveryValue: 0.5), StandardPlan(), null),
        "E_stale" => (State(sleepDaysSinceFreshData: 2), StandardPlan(), null),
        "F_recovery_plus_activity" => (State(recoveryValue: 0.5, steps: 10500), StandardPlan(), null),
        "G_insufficient_baseline" => (State(recoveryValue: 0.3, recoveryConfidence: BaselineConfidence.None,
            sleepMinutes: 200, sleepConfidence: BaselineConfidence.None,
            steps: 300, stepsConfidence: BaselineConfidence.None), StandardPlan(), null),
        "H_deadline_pre_shrunk" => (State(recoveryValue: 0.5), StandardPlan(programBase: 60, programPlanned: 12),
            new[] { WorkoutGoal(Now.AddDays(7), fraction: 0.2) }),
        "I_multi_signal" => (State(recoveryValue: 0.5, sleepMinutes: 300, steps: 10500,
            sleepConfidence: BaselineConfidence.Medium), StandardPlan(), null),
        _ => throw new ArgumentOutOfRangeException(name),
    };
}
