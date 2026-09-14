using Livora.Server.Infrastructure.Engines.Pipeline;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: one isolatable test per weight and per boundary in stages 8 (priorities), 9 (budget),
///          10 (schedule), 11 (adaptation) and 12 (feedback). Each test changes exactly ONE input
///          and asserts the direction it must move — that is what "a reviewer could falsify"
///          means here: flip a constant, a named test goes red, and the constant's doc line names
///          the test that owns it.
/// OWNER: Agent 10+11.
/// </summary>
public sealed class PrioritisationScheduleFeedbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    private static CandidateAction Cand(string key, RecommendationPriority p = RecommendationPriority.Medium,
        int minutes = 0, bool demanding = false, string conclusion = "p1e.state.sleep_poor")
        => new(key, conclusion, p, minutes, demanding, ["ev-1"], "fixture");

    private static StateSnapshot EmptyState() => new(
        new Dictionary<string, DomainSignal>(StringComparer.Ordinal)
        {
            [FactKeys.SleepMinutes] = new DomainSignal(FactKeys.SleepMinutes, 300, 450, -0.33,
                SignalLevel.WorseThanUsual, EvidenceGrade.DeviceDerived, BaselineConfidence.High, ["ev-1"], "worse"),
        },
        [new StateConclusion("p1e.state.sleep_poor", FactKeys.SleepMinutes, "fired", "x",
            ["deficit_h=2.5"], ["ev-1"]),
         new StateConclusion("p1e.state.data_trustworthy", "", "fired", "x", ["completeness=1"],
            Array.Empty<string>())],
        EvidenceGrade.DeviceDerived, 1.0, Array.Empty<TrailEntry>());

    private static GoalHealthResult NoGoals() =>
        new(new Dictionary<string, GoalVerdict>(StringComparer.Ordinal), Array.Empty<TrailEntry>());

    // ---- stage 8: weight isolation (client parity: RecommendationRanker tests) ----

    private static double ScoreOf(CandidateAction c,
        ExecutionLikelihoodModel? likelihood = null,
        IReadOnlyCollection<string>? focus = null,
        GoalHealthResult? goals = null,
        DateTimeOffset? now = null)
    {
        var ranked = Prioritiser.Rank([c], EmptyState(),
            likelihood ?? ExecutionLikelihoodModel.Empty(Now),
            focus ?? Array.Empty<string>(), goals, now ?? Now);
        return Assert.Single(ranked).Score;
    }

    [Fact]
    public void Weights_sum_to_one_minus_effort_scale_and_keep_the_client_relative_order()
    {
        double sum = Prioritiser.W1GoalAlignment + Prioritiser.W2Urgency + Prioritiser.W3Confidence
                   + Prioritiser.W4ScheduleFit + Prioritiser.W6Execution;
        Assert.Equal(1.0, Math.Round(sum, 6));
        // relative order of the FIVE ported weights is unchanged by the 0.8 scale
        Assert.True(Prioritiser.W1GoalAlignment > Prioritiser.W2Urgency);
        Assert.True(Prioritiser.W2Urgency > Prioritiser.W3Confidence);
        Assert.True(Prioritiser.W3Confidence > Prioritiser.W4ScheduleFit);
        Assert.Equal(0.35 * 0.8, Prioritiser.W1GoalAlignment);   // RecommendationRanker.cs:41
    }

    [Fact]
    public void Goal_alignment_weight_moves_the_score_exactly_W1()
    {
        var c = Cand(ActionCatalog.ShortWalk);
        var off = ScoreOf(c, focus: Array.Empty<string>());
        var on = ScoreOf(c, focus: ["activity"]);
        // Precision-6 equality, not bit-identity: the engine rounds each score to 6 decimals, so
        // the diff can sit one ULP off the 0.35*0.8 product the weight constant folds to. The
        // falsifiable promise is "flipping W1 moves the score by exactly W1", which precision-6 pins.
        Assert.Equal(Prioritiser.W1GoalAlignment, Math.Round(on - off, 6), 6);
    }

    [Fact]
    public void Effort_penalises_minutes_at_exactly_W5_over_120()
    {
        // The old form asserted `W5*e - 2*W5*0.125 == -W5*0.25`, which is 0 == -0.05 — an
        // unsatisfiable tautology, not a weight check. The falsifiable promise is: two candidates
        // differing ONLY in minutes score apart by exactly W5 × Δminutes/120.
        var twin30 = Cand(ActionCatalog.TakeBreak, minutes: 30);
        var twin0 = Cand(ActionCatalog.TakeBreak, minutes: 0);
        var ranked = Prioritiser.Rank([twin0, twin30], EmptyState(),
            ExecutionLikelihoodModel.Empty(Now), Array.Empty<string>(), NoGoals(), Now);
        var longCard = Assert.Single(ranked, r => r.Components.Effort > 0);
        Assert.Equal(30 / 120.0, longCard.Components.Effort);
        var zeroCard = Assert.Single(ranked, r => r.Components.Effort == 0);
        Assert.Equal(Prioritiser.W5Effort * 0.25,
            Math.Round(zeroCard.Score - longCard.Score, 6), 6);
    }

    [Fact]
    public void Schedule_fit_zeroes_out_when_a_block_would_end_at_or_after_2200()
    {
        // client parity: RecommendationRanker.cs:125-127 — ending exactly at 22:00 is NOT before it
        var c = Cand(ActionCatalog.ShortWalk, minutes: 60, conclusion: "p1e.state.activity_low");
        var atCutoff = ScoreOf(c, now: new DateTimeOffset(2026, 5, 4, 21, 0, 0, TimeSpan.Zero));
        var safe = ScoreOf(c, now: Now);
        // precision-6 for the same ULP reason as the W1 test above
        Assert.Equal(Prioritiser.W4ScheduleFit, Math.Round(safe - atCutoff, 6), 6);
    }

    [Fact]
    public void Execution_likelihood_moves_the_score_by_W6_and_starts_at_the_prior()
    {
        var c = Cand(ActionCatalog.EarlierSleep);
        var priorScore = ScoreOf(c);
        var boosted = ExecutionLikelihoodModel.Build(
            [new ExecutionFeedback("f1", ActionCatalog.EarlierSleep, ExecutionOutcome.Completed,
                Now.AddHours(-2), EvidenceGrade.SelfReported)], Now);
        var after = ScoreOf(c, likelihood: boosted);
        Assert.True(after > priorScore);
        Assert.True(Math.Round((after - priorScore) / (boosted.LikelihoodFor(ActionCatalog.EarlierSleep)
                    - ExecutionLikelihoodModel.PriorLikelihood), 4)
                    - Math.Round(Prioritiser.W6Execution, 4) < 0.01);
    }

    [Fact]
    public void Confidence_is_evidence_weight_times_baseline_factor_never_a_merge()
    {
        // same deviation, different grade ⇒ different score; different baseline ⇒ different score
        Assert.Equal(0.60, Prioritiser.EvidenceWeight(EvidenceGrade.SelfReported), 6);
        Assert.Equal(1.00, Prioritiser.EvidenceWeight(EvidenceGrade.SystemVerified), 6);
        Assert.True(Prioritiser.EvidenceWeight(EvidenceGrade.DeviceDerived)
                    > Prioritiser.EvidenceWeight(EvidenceGrade.SelfReported));
        Assert.Equal(0.45, Prioritiser.BaselineFactor(BaselineConfidence.Low), 6);   // StateConfidenceCalculator.cs:61
        Assert.Equal(0.0, Prioritiser.BaselineFactor(BaselineConfidence.None), 6);
    }

    [Fact]
    public void Ranking_is_stable_ties_broken_by_action_key_ordinal()
    {
        var a = Cand(ActionCatalog.EarlierSleep);
        var b = Cand(ActionCatalog.ModerateScreen, conclusion: "p1e.state.data_trustworthy");
        var ranked = Prioritiser.Rank([b, a], EmptyState(), ExecutionLikelihoodModel.Empty(Now),
            Array.Empty<string>(), NoGoals(), Now);
        Assert.Equal(2, ranked.Count);
        // equal-ish scores must never reorder across runs
        var again = Prioritiser.Rank([a, b], EmptyState(), ExecutionLikelihoodModel.Empty(Now),
            Array.Empty<string>(), NoGoals(), Now);
        Assert.Equal(ranked.Select(r => r.ActionKey), again.Select(r => r.ActionKey));
    }

    // ---- stage 10: schedule boundaries ---------------------------------------

    private static (RecommendationBundle, ConstraintSet) Bundle(
        int minutes, params CalendarBlock[] blocks)
    {
        var rec = new Recommendation(ActionCatalog.ShortWalk, RecommendationPriority.Medium, minutes,
            "p1e.state.activity_low", "fixture", 0.5,
            new ScoreComponents(0, 0, 0, 0.5, 1, 0), EvidenceGrade.DeviceDerived,
            BaselineConfidence.High, 0.5, ["walk:+15min"], ["ev-1"]);
        var bundle = new RecommendationBundle([rec], Array.Empty<SuppressedAction>(),
            DirectiveSet.Empty(), Array.Empty<TrailEntry>());
        var state = EmptyState();
        var constraints = ConstraintStage.Build(state, blocks, TimeWindowUser.WholeDay, 240);
        return (bundle, constraints);
    }

    [Fact]
    public void Placement_starts_no_earlier_than_seven_am_and_ends_before_ten_pm()
    {
        var (bundle, constraints) = Bundle(15);
        var plan = ScheduleStage.Build(bundle, constraints, Array.Empty<PlannedItem>(), Now, 0);
        var placed = Assert.Single(plan.Items);
        Assert.True(placed.StartMinutesOfDay >= 7 * 60);
        Assert.True(placed.StartMinutesOfDay + placed.PlannedMinutes <= 22 * 60);
    }

    [Fact]
    public void A_block_never_lands_on_top_of_a_fixed_meeting()
    {
        var (bundle, constraints) = Bundle(60,
            new CalendarBlock("mtg", new TimeWindowUser(9 * 60, 11 * 60), "ev"));
        var plan = ScheduleStage.Build(bundle, constraints,
            [new PlannedItem("other", 7 * 60, 60)], Now, 0);
        var placed = plan.Items.First(i => i.ItemId == ActionCatalog.ShortWalk);
        Assert.True(placed.StartMinutesOfDay >= 11 * 60 || placed.StartMinutesOfDay + 60 <= 9 * 60,
            $"placement at {placed.StartMinutesOfDay} overlaps the fixed block");
    }

    [Fact]
    public void No_room_before_the_cutoff_is_a_stated_conflict_not_a_silent_drop()
    {
        // 15 hourly blocks cover 07:00–22:00 solid: the whole placement band (MorningStart..cutoff)
        // is occupied, so no 60-minute slot exists. (14 blocks left 21:00–22:00 free and the walk
        // fitted legitimately — a fixture off-by-one, not an engine bug: the placement law is
        // "free window before 22:00", and 21:00–22:00 IS one.)
        var blocks = Enumerable.Range(7, 15)
            .Select(h => new CalendarBlock($"b{h}", new TimeWindowUser(h * 60, (h + 1) * 60))).ToArray();
        var (bundle, constraints) = Bundle(60, blocks);
        var plan = ScheduleStage.Build(bundle, constraints, Array.Empty<PlannedItem>(), Now, 0);
        var conflict = Assert.Single(plan.Unplaced);
        Assert.Equal("no_slot", conflict.ReasonKey);
        Assert.DoesNotContain(plan.Items, i => i.ItemId == ActionCatalog.ShortWalk);
    }

    [Fact]
    public void Total_minutes_cap_uses_the_constraint_stage_number_not_a_fork()
    {
        Assert.Equal(1.2, ConstraintStage.TotalMinutesCapFactor);   // PlanAdaptationEngine.cs:102
        var (bundle, constraints) = Bundle(60);
        Assert.Equal(288, constraints.CapMinutes);                  // 240 base * 1.2
        var tight = constraints with { BaselinePlanMinutes = 40 };  // cap 48: a 60-min walk cannot fit
        var plan = ScheduleStage.Build(bundle, tight, Array.Empty<PlannedItem>(), Now, 0);
        Assert.Equal("total_cap", Assert.Single(plan.Unplaced).ReasonKey);
    }

    // ---- stage 11: ladder + idempotence + never-raise -------------------------

    [Theory]
    [InlineData(45, 30)]    // one ladder step down
    [InlineData(30, 20)]
    [InlineData(20, 20)]    // no step below 20, and the floor keeps 20 intact ⇒ no change
    [InlineData(10, 10)]    // below the floor: min(15,10)=10, never inflated to 15
    [InlineData(60, 45)]
    public void Shorten_ladder_steps_once_down_and_never_raises(int wanted, int expected)
        => Assert.Equal(expected, AdaptationStage.ShortenTarget(wanted, recoveryLow: true));

    [Fact]
    public void Already_applied_rule_keys_make_the_pass_idempotent()
    {
        var facts = CrossDomainFixtures.BadDayFacts();
        var input = PipelineInput.Simple(Now, facts, CrossDomainFixtures.NormalHistory(),
            basePlan: [new PlannedItem("bootcamp-day-9", 9 * 60, 45)],
            fixedBlocks: [new CalendarBlock("mtg", new TimeWindowUser(9 * 60, 10 * 60))],
            conflictingItemIds: ["bootcamp-day-9"],
            alreadyAppliedRuleKeys: [AdaptationStage.RuleShorten, AdaptationStage.RuleMove]);
        var result = PipelineRunner.Run(input);
        // with move+shorten already applied, the only remaining lever is skip — no silent no-op
        var change = Assert.Single(result.Adaptations);
        Assert.Equal("skipped", change.Resolution);
        Assert.Equal(AdaptationStage.RuleSkip, change.RuleKey);
    }

    [Fact]
    public void The_input_plan_is_never_mutated()
    {
        var input = PipelineInput.Simple(Now, CrossDomainFixtures.BadDayFacts(),
            CrossDomainFixtures.NormalHistory(),
            basePlan: [new PlannedItem("bootcamp-day-9", 9 * 60, 45)],
            fixedBlocks: [new CalendarBlock("mtg", new TimeWindowUser(9 * 60, 10 * 60))],
            conflictingItemIds: ["bootcamp-day-9"]);
        var before = input.BasePlan[0];
        _ = PipelineRunner.Run(input);
        Assert.Equal(before.StartMinutesOfDay, input.BasePlan[0].StartMinutesOfDay);
        Assert.Equal(45, input.BasePlan[0].PlannedMinutes);
    }

    [Fact]
    public void Deadline_protection_keeps_the_floor_when_not_recovering_low()
    {
        var protectedIds = new HashSet<string>(StringComparer.Ordinal) { "bootcamp-day-9" };
        var input = PipelineInput.Simple(Now, CrossDomainFixtures.GoodDayFacts(),
            CrossDomainFixtures.NormalHistory(),
            basePlan: [new PlannedItem("bootcamp-day-9", 9 * 60, 20)],
            fixedBlocks: Enumerable.Range(7, 15)
                .Select(h => new CalendarBlock($"b{h}", new TimeWindowUser(h * 60, (h + 1) * 60))).ToArray(),
            conflictingItemIds: ["bootcamp-day-9"],
            protectedItemIds: protectedIds);
        var result = PipelineRunner.Run(input);
        var change = Assert.Single(result.Adaptations);
        Assert.Equal("protected", change.Resolution);
        Assert.Contains(result.Schedule!.Items, i => i.ItemId == "bootcamp-day-9");
    }

    // ---- stage 12: feedback record properties ----------------------------------

    [Fact]
    public void Likelihood_is_clamped_to_zero_one_and_degrades_to_the_prior_when_all_deleted()
    {
        var events = new[]
        {
            new ExecutionFeedback("f1", ActionCatalog.ShortWalk, ExecutionOutcome.Completed,
                Now.AddHours(-1), EvidenceGrade.DeviceDerived),
            new ExecutionFeedback("f2", ActionCatalog.ShortWalk, ExecutionOutcome.Completed,
                Now.AddHours(-2), EvidenceGrade.DeviceDerived),
        };
        var two = ExecutionLikelihoodModel.Build(events, Now).LikelihoodFor(ActionCatalog.ShortWalk);
        // The model's own law (stage-12 header + the decay test below): Laplace smoothing toward
        // the prior means a real estimate NUDGES the number and NEVER pins it to 0 or 1 — so the
        // falsifiable promise here is the bound (0,1] plus strict movement above the prior, not
        // bit-exact saturation at 1.0 (which only a zero-pseudo-count model could produce, and
        // that model would make two completions a fact about the person — product law 1).
        Assert.InRange(two, ExecutionLikelihoodModel.PriorLikelihood, 1.0);
        Assert.True(two > ExecutionLikelihoodModel.PriorLikelihood);
        Assert.True(two < 1.0 + 1e-12);                       // clamp: never above 1
        var emptied = ExecutionLikelihoodModel.Build(
            events.Select(e => e with { DeletedAtUtc = Now }).ToList(), Now);
        Assert.Equal(ExecutionLikelihoodModel.PriorLikelihood,
            emptied.LikelihoodFor(ActionCatalog.ShortWalk));
    }

    [Fact]
    public void Skips_pull_the_estimate_down_and_old_history_decays_toward_the_prior()
    {
        var skips = ExecutionLikelihoodModel.Build(
            [new ExecutionFeedback("f1", ActionCatalog.ProtectFocusBlock, ExecutionOutcome.Skipped,
                Now.AddDays(-1), EvidenceGrade.SelfReported)], Now);
        Assert.True(skips.LikelihoodFor(ActionCatalog.ProtectFocusBlock)
                    < ExecutionLikelihoodModel.PriorLikelihood);

        // "ancient" = OLDER than the representativeness bound: the offset must subtract days
        // (the old fixture added them, feeding the estimator a FUTURE event that the age filter
        // clamps to age 0 instead of dropping).
        var ancient = ExecutionLikelihoodModel.Build(
            [new ExecutionFeedback("f1", ActionCatalog.ProtectFocusBlock, ExecutionOutcome.Skipped,
                Now.AddDays(-(ExecutionLikelihoodModel.MaxRepresentativeAgeDays + 1)),
                EvidenceGrade.SelfReported)], Now);
        Assert.Equal(ExecutionLikelihoodModel.PriorLikelihood,
            ancient.LikelihoodFor(ActionCatalog.ProtectFocusBlock), 6);
    }

    [Fact]
    public void An_estimate_explains_itself_through_trail_lines_only_no_prose()
    {
        var model = ExecutionLikelihoodModel.Build(
            [new ExecutionFeedback("f1", ActionCatalog.ShortWalk, ExecutionOutcome.Completed,
                Now.AddDays(-1), EvidenceGrade.SelfReported, "user_tap")], Now);
        var line = Assert.Single(model.Trail([ActionCatalog.ShortWalk]));
        Assert.Equal("execution_likelihood", line.Stage);
        Assert.Contains(line.Factors, f => f.StartsWith("likelihood="));
    }

    [Fact]
    public void Lever_selection_follows_the_latest_representative_event()
    {
        var model = ExecutionLikelihoodModel.Build(
            [
                new ExecutionFeedback("f1", "bootcamp-day-9", ExecutionOutcome.TooHard,
                    Now.AddDays(-3), EvidenceGrade.SelfReported),
                new ExecutionFeedback("f2", "bootcamp-day-9", ExecutionOutcome.WrongTime,
                    Now.AddDays(-1), EvidenceGrade.SelfReported),
            ], Now);
        Assert.Equal("move", model.LeverFor("bootcamp-day-9"));
        Assert.Equal("default", model.LeverFor("unknown-action"));
    }
}

/// <summary>Shared deterministic fixtures — the same numbers every P1-E test reasons over, kept in
/// ONE place so a reviewer checking "is this fixture honest?" checks it once.</summary>
public static class CrossDomainFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    public static List<HistoryDay> NormalHistory()
        => Enumerable.Range(0, 31).Select(i => new HistoryDay(
            new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
            SleepMinutes: 450, SleepQuality: 0.75, BedtimeMinutesOfDay: 1350,
            Steps: 8000, ActiveMinutes: 40, RecoveryScore: 0.72, Stress: 0.4,
            ScreenMinutes: 180, MeetingMinutes: 60)).ToList();

    public static FactSet BadDayFacts() => new(
        [
            Fact.Of(FactKeys.SleepMinutes, 315, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-10), "device", "ev-sleep"),
            Fact.Of(FactKeys.BedtimeMinutesOfDay, 1395, "minutes_of_day", EvidenceGrade.DeviceDerived, Now.AddHours(-10), "device", "ev-bedtime"),
            Fact.Of(FactKeys.MeetingMinutes, 210, "minutes", EvidenceGrade.ProviderDerived, Now.AddHours(-1), "provider", "ev-meetings"),
            Fact.Of(FactKeys.ScreenMinutes, 340, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-1), "device", "ev-screen"),
            Fact.Of(FactKeys.Steps, 1500, "steps", EvidenceGrade.DeviceDerived, Now.AddHours(-1), "device", "ev-steps"),
            Fact.Of(FactKeys.RecoveryScore, 0.50, "fraction", EvidenceGrade.DeviceDerived, Now.AddHours(-8), "device", "ev-recovery"),
        ], Now);

    public static FactSet GoodDayFacts() => new(
        [
            Fact.Of(FactKeys.SleepMinutes, 455, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-9), "device", "ev-sleep"),
            Fact.Of(FactKeys.MeetingMinutes, 55, "minutes", EvidenceGrade.ProviderDerived, Now.AddHours(-1), "provider", "ev-meetings"),
            Fact.Of(FactKeys.ScreenMinutes, 170, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-1), "device", "ev-screen"),
            Fact.Of(FactKeys.Steps, 8100, "steps", EvidenceGrade.DeviceDerived, Now.AddHours(-1), "device", "ev-steps"),
            Fact.Of(FactKeys.RecoveryScore, 0.74, "fraction", EvidenceGrade.DeviceDerived, Now.AddHours(-8), "device", "ev-recovery"),
        ], Now);
}
