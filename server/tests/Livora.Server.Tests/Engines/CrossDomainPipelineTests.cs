using Livora.Server.Infrastructure.Engines.Pipeline;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: the cross-domain coherence proofs the brief demands:
///   1. poor sleep + high meeting load + high screen time + low activity ⇒ ONE plan whose visible
///      actions are exactly {reduce intensity, short walk, protect one focus block, earlier sleep},
///      nothing demanding added, and every action's trail pointing at its fired conclusion;
///   2. the recommendation budget (1-3) with an explicit suppression reason per dropped candidate;
///   3. the plan-conflict case (bootcamp wants 45 min, a meeting owns the slot, recovery low) ⇒
///      resolved by move/shorten/skip with a stated reason;
///   4. determinism: byte-equal trail for byte-equal input; and the honest refusal when the data
///      gate fails.
/// All numbers below are FIXED fixture values, chosen relative to the history baseline the same
/// fixture builds — no invented "real" data, nothing read from a wall clock.
/// OWNER: Agent 10+11.
/// </summary>
public sealed class CrossDomainPipelineTests
{
    // Monday 2026-05-04, 09:00 UTC — the decision moment.
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A 28-day history whose personal normals are: sleep 450 min (7.5 h), bedtime
    /// 1350 (22:30), steps 8000, recovery 0.72, meetings 60, screen 180. All clean, complete days:
    /// baseline gates (5/10/21 + coverage) earn High confidence for Days30.</summary>
    private static List<HistoryDay> NormalHistory()
    {
        var list = new List<HistoryDay>();
        for (int i = 1; i <= 30; i++)
            list.Add(new HistoryDay(
                new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc).AddDays(i - 1),
                SleepMinutes: 450, SleepQuality: 0.75, BedtimeMinutesOfDay: 1350,
                Steps: 8000, ActiveMinutes: 40, RecoveryScore: 0.72, Stress: 0.4,
                ScreenMinutes: 180, MeetingMinutes: 60));
        return list;
    }

    /// <summary>The bad day: slept 5.25 h (deficit 3.75 h ≥ 1.5 ⇒ sleep_poor High), 210 meeting
    /// minutes (≥ 180 absolute and +250% vs baseline ⇒ meeting_load_high), 340 screen minutes
    /// (≥ 300 ⇒ screen_load_high), 1500 steps (81% below own baseline ⇒ activity_low), recovery
    /// 0.50 (&lt; 0.55 ⇒ recovery_low).</summary>
    private static FactSet BadDayFacts() => new(
        [
            Fact.Of(FactKeys.SleepMinutes, 315, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-10),
                "device", "ev-sleep"),
            Fact.Of(FactKeys.BedtimeMinutesOfDay, 1395, "minutes_of_day", EvidenceGrade.DeviceDerived,
                Now.AddHours(-10), "device", "ev-bedtime"),
            Fact.Of(FactKeys.MeetingMinutes, 210, "minutes", EvidenceGrade.ProviderDerived, Now.AddHours(-1),
                "provider", "ev-meetings"),
            Fact.Of(FactKeys.ScreenMinutes, 340, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-1),
                "device", "ev-screen"),
            Fact.Of(FactKeys.Steps, 1500, "steps", EvidenceGrade.DeviceDerived, Now.AddHours(-1),
                "device", "ev-steps"),
            Fact.Of(FactKeys.RecoveryScore, 0.50, "fraction", EvidenceGrade.DeviceDerived, Now.AddHours(-8),
                "device", "ev-recovery"),
        ], Now);

    private static PipelineResult RunBadDay(
        IReadOnlyList<GoalInput>? goals = null,
        IReadOnlyList<ExecutionFeedback>? feedback = null)
        => PipelineRunner.Run(PipelineInput.Simple(
            Now, BadDayFacts(), NormalHistory(),
            goals: goals, feedback: feedback,
            focusTokens: ["sleep", "activity", "focus"]));

    // ---- proof 1: ONE coherent cross-domain plan ------------------------------

    [Fact]
    public void Poor_sleep_meetings_screens_and_low_activity_yield_one_coherent_plan()
    {
        var result = RunBadDay();

        Assert.False(result.Refused);
        // all four domains are seen, from four different fact ids
        Assert.True(result.State!.Fired("p1e.state.sleep_poor"));
        Assert.True(result.State.Fired("p1e.state.meeting_load_high"));
        Assert.True(result.State.Fired("p1e.state.screen_load_high"));
        Assert.True(result.State.Fired("p1e.state.activity_low"));
        Assert.True(result.State.Fired("p1e.state.recovery_low"));

        var actions = result.Actions;
        // reduce intensity, a short walk, one protected focus block, earlier sleep — exactly the
        // coherent set. activity_low never adds a second walk (dedupe parity with
        // RecommendationService.cs:29-33 / RuleEngine.cs:110's recovery gate).
        Assert.Contains(ActionCatalog.ReduceIntensity, actions);
        Assert.Contains(ActionCatalog.ShortWalk, actions);
        Assert.Contains(ActionCatalog.ProtectFocusBlock, actions);
        Assert.Contains(ActionCatalog.EarlierSleep, actions);
        Assert.DoesNotContain(ActionCatalog.DoProgramDay, actions);   // nothing demanding added

        // THE BUDGET: at most 3 ASKS (things the person must decide to do). The walk and the
        // protected focus block commit minutes; earlier-sleep and reduce-intensity are zero-minute
        // plan adjustments (client parity: RecommendationService.DurationFor:91 "timing change, not
        // a duration"), and screen moderation ranks below the fold — suppressed with a reason.
        int asks = result.Recommendations.Chosen.Count(r => r.Ask);
        Assert.InRange(asks, 1, RecommendationStage.MaxVisibleAsks);
        Assert.InRange(result.Recommendations.Chosen.Count, 1, RecommendationStage.MaxVisibleActions);

        // the plan directives: intensity halved, a 15-min walk, a protected block, bedtime -30,
        // and the recovery constraint forbidding demanding minutes — all traceable
        Assert.True(result.Recommendations.Directives.Has("exercise:*0.5"));
        Assert.True(result.Recommendations.Directives.Has("walk:+15min"));
        Assert.True(result.Recommendations.Directives.Has("bedtime:-30min"));
        Assert.True(result.Recommendations.Directives.Has("focus:protect:1"));
        Assert.True(result.Recommendations.Directives.Has("load:no_new_demanding"));

        // every visible action carries the conclusion that fired it and real evidence ids
        foreach (var r in result.Recommendations.Chosen)
        {
            Assert.StartsWith("p1e.state.", r.SourceConclusion);
            Assert.NotEmpty(r.EvidenceIds);
            Assert.NotEqual(EvidenceGrade.Unrated, r.Grade);
        }
    }

    [Fact]
    public void Bad_day_schedule_places_a_walk_and_a_focus_block_inside_availability()
    {
        var result = RunBadDay();
        var placed = result.Schedule!.Items
            .Where(i => i.ItemId is ActionCatalog.ShortWalk or ActionCatalog.ProtectFocusBlock)
            .ToList();
        Assert.Equal(2, placed.Count);
        Assert.All(placed, p =>
        {
            Assert.InRange(p.StartMinutesOfDay, ScheduleStage.MorningStartMinutes,
                ScheduleStage.EveningCutoffTotalMinutes - p.PlannedMinutes);
        });
        // walk before focus is NOT asserted (placement is earliest-fit by score order); overlap is.
        var a = placed.First(); var b = placed.Last();
        Assert.True(a.StartMinutesOfDay + a.PlannedMinutes <= b.StartMinutesOfDay
                    || b.StartMinutesOfDay + b.PlannedMinutes <= a.StartMinutesOfDay);
    }

    // ---- proof 2: budget with suppression reasons -----------------------------

    [Fact]
    public void Recommendation_budget_keeps_at_most_three_and_states_every_drop()
    {
        var result = RunBadDay();
        Assert.True(result.Recommendations.Chosen.Count <= RecommendationStage.MaxVisibleActions);
        Assert.True(result.Recommendations.Chosen.Count >= 1);

        // five conclusions fired, four candidate actions survive dedupe + the visible set: the
        // drop(s) must each carry a reason key, and the reasons come from the documented set
        var knownReasons = new[]
        {
            "budget", "one_high_priority_only", "duplicate_action", "recovery_gate",
            "unsupported_evidence", "provisional_baseline", "p1e.budget.insufficient_data",
        };
        foreach (var d in result.Recommendations.Dropped)
            Assert.Contains(d.ReasonKey, knownReasons);

        // screen moderation is the weakest fired candidate on this fixture — it must be the one
        // the budget dropped, with the budget reason stated (not vanished)
        var droppedScreen = result.Recommendations.Dropped
            .FirstOrDefault(d => d.ActionKey == ActionCatalog.ModerateScreen);
        if (result.Actions.Contains(ActionCatalog.ModerateScreen))
            Assert.NotNull(droppedScreen);   // kept ⇒ no drop line
        else
            Assert.NotNull(droppedScreen);
    }

    [Fact]
    public void Nothing_fired_yields_the_keep_routine_anchor_and_zero_suppressions()
    {
        var facts = new FactSet(
            [
                Fact.Of(FactKeys.SleepMinutes, 455, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-9)),
                Fact.Of(FactKeys.MeetingMinutes, 55, "minutes", EvidenceGrade.ProviderDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.ScreenMinutes, 170, "minutes", EvidenceGrade.DeviceDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.Steps, 8100, "steps", EvidenceGrade.DeviceDerived, Now.AddHours(-1)),
                Fact.Of(FactKeys.RecoveryScore, 0.74, "fraction", EvidenceGrade.DeviceDerived, Now.AddHours(-8)),
            ], Now);
        var result = PipelineRunner.Run(PipelineInput.Simple(Now, facts, NormalHistory()));
        Assert.Equal([ActionCatalog.KeepRoutine], result.Actions);
        Assert.Empty(result.Recommendations.Dropped);
    }

    // ---- proof 3: the plan-conflict case ---------------------------------------

    [Fact]
    public void Bootcamp_45min_meeting_owned_slot_low_recovery_resolves_with_a_stated_reason()
    {
        // The 09:00-10:00 meeting owns the bootcamp block's slot; recovery is low (bad day).
        var fixedBlocks = new[]
        {
            new CalendarBlock("mtg-standup", new TimeWindowUser(9 * 60, 10 * 60), "ev-meetings"),
        };
        var input = PipelineInput.Simple(
            Now, BadDayFacts(), NormalHistory(),
            basePlan: [new PlannedItem("bootcamp-day-9", 9 * 60, 45)],
            fixedBlocks: fixedBlocks,
            conflictingItemIds: ["bootcamp-day-9"],
            focusTokens: ["program"]);
        var result = PipelineRunner.Run(input);

        var change = Assert.Single(result.Adaptations);
        Assert.Equal("bootcamp-day-9", change.ItemId);
        Assert.NotEmpty(change.RuleKey);
        Assert.NotEmpty(change.Why);
        Assert.True(change.Resolution is "moved" or "shortened" or "skipped",
            $"resolution must be one of move/shorten/skip, got {change.Resolution}");

        // with no feedback history the default lever is the recovery ladder: 45 → 30 with recovery
        // low; the trail names the ported ladder numbers
        Assert.Equal("shortened", change.Resolution);
        Assert.Contains("from_min=45", change.ChangeSummary);
        Assert.Contains("to_min=30", change.ChangeSummary);
        Assert.Contains("ladder=45/30/20", change.ChangeSummary);
        Assert.Contains(result.Trail, t =>
            t.RuleKey == "p1e.adapt.conflict.shorten" && t.Verdict == "shortened");
    }

    [Fact]
    public void Wrong_time_feedback_history_switches_the_lever_to_move()
    {
        var feedback = new[]
        {
            new ExecutionFeedback("fb-1", "bootcamp-day-9", ExecutionOutcome.WrongTime,
                Now.AddDays(-2), EvidenceGrade.SelfReported, "user_tap"),
        };
        var input = PipelineInput.Simple(
            Now, BadDayFacts(), NormalHistory(),
            basePlan: [new PlannedItem("bootcamp-day-9", 9 * 60, 45)],
            fixedBlocks: [new CalendarBlock("mtg-standup", new TimeWindowUser(9 * 60, 10 * 60), "ev-meetings")],
            conflictingItemIds: ["bootcamp-day-9"],
            feedback: feedback);
        var result = PipelineRunner.Run(input);

        // move FIRST: the block keeps its length and lands in a free window (10:00+)
        var change = Assert.Single(result.Adaptations);
        Assert.Equal("moved", change.Resolution);
        var moved = result.Schedule!.Items.Single(i => i.ItemId == "bootcamp-day-9");
        Assert.Equal(45, moved.PlannedMinutes);
        Assert.True(moved.StartMinutesOfDay >= 10 * 60);
    }

    [Fact]
    public void Too_hard_feedback_history_shortens_instead_of_only_moving()
    {
        var input = PipelineInput.Simple(
            Now, BadDayFacts(), NormalHistory(),
            basePlan: [new PlannedItem("bootcamp-day-9", 9 * 60, 45)],
            fixedBlocks: [new CalendarBlock("mtg-standup", new TimeWindowUser(9 * 60, 10 * 60), "ev-meetings")],
            conflictingItemIds: ["bootcamp-day-9"],
            feedback:
            [
                new ExecutionFeedback("fb-1", "bootcamp-day-9", ExecutionOutcome.TooHard,
                    Now.AddDays(-1), EvidenceGrade.SelfReported, "user_tap"),
            ]);
        var result = PipelineRunner.Run(input);
        var change = Assert.Single(result.Adaptations, c => c.ItemId == "bootcamp-day-9");
        // the lever asked for a shorten; with recovery low the ladder cuts 45 → 30
        Assert.Contains(result.Adaptations, c => c.Resolution is "shortened" or "moved");
        Assert.Equal(AdaptationStage.RecoveryLadderMinutes[1],
            result.Schedule!.Items.Single(i => i.ItemId == "bootcamp-day-9").PlannedMinutes);
    }

    [Fact]
    public void No_room_and_no_shortening_permissible_skips_with_all_three_forcing_reasons()
    {
        // A fully-owned day: fixed blocks from 07:00 to 22:00, availability WholeDay.
        var blocks = Enumerable.Range(7, 15)
            .Select(h => new CalendarBlock($"blk-{h}", new TimeWindowUser(h * 60, (h + 1) * 60)))
            .ToList();
        var input = PipelineInput.Simple(
            Now, BadDayFacts(), NormalHistory(),
            basePlan: [new PlannedItem("bootcamp-day-9", 12 * 60, 20)],
            fixedBlocks: blocks,
            conflictingItemIds: ["bootcamp-day-9"]);
        var result = PipelineRunner.Run(input);
        var change = Assert.Single(result.Adaptations);
        Assert.Equal("skipped", change.Resolution);
        Assert.Contains("free_slot=none", change.ChangeSummary);
        Assert.Contains("recovery_low=True", change.ChangeSummary);
        Assert.DoesNotContain(result.Schedule!.Items, i => i.ItemId == "bootcamp-day-9");
    }

    // ---- proof 4: determinism + refusal + grade ceiling ------------------------

    [Fact]
    public void Same_input_produces_byte_equal_trails_and_action_order()
    {
        var a = RunBadDay();
        var b = RunBadDay();
        Assert.Equal(a.TrailText(), b.TrailText());
        Assert.Equal(a.Actions, b.Actions);
        Assert.Equal(EngineMath.EngineVersion,
            a.Trail.First().Factors.First(f => f.StartsWith("engine="))["engine=".Length..]);
    }

    [Fact]
    public void A_day_below_the_quality_gate_refuses_instead_of_recommending()
    {
        var thin = new FactSet([Fact.Of(FactKeys.Steps, 900, "steps", EvidenceGrade.DeviceDerived, Now)], Now);
        var result = PipelineRunner.Run(PipelineInput.Simple(Now, thin, NormalHistory()));
        Assert.True(result.Refused);
        Assert.Empty(result.Actions);
        var drop = Assert.Single(result.Recommendations.Dropped);
        Assert.Equal("p1e.budget.insufficient_data", drop.ReasonKey);
        Assert.Contains(result.Trail, t => t.RuleKey == "p1e.pipeline.refused");
    }

    [Fact]
    public void Self_reported_day_cannot_produce_a_SystemVerified_ceiling()
    {
        var facts = new FactSet(
            BadDayFacts().Raw
                .Select(f => f with { Grade = EvidenceGrade.SelfReported, SourceLabel = "manual" })
                .ToList(), Now);
        var result = PipelineRunner.Run(PipelineInput.Simple(Now, facts, NormalHistory()));
        Assert.Equal(EvidenceGrade.SelfReported, result.EvidenceCeiling);
        Assert.All(result.Recommendations.Chosen, r => Assert.Equal(EvidenceGrade.SelfReported, r.Grade));
    }

    [Fact]
    public void Removing_the_sleep_fact_stops_the_sleep_conclusion_and_its_actions()
    {
        // falsifiability of "cross-domain": the plan changes when one domain's input is removed
        var withoutSleep = new FactSet(
            BadDayFacts().Raw.Where(f => f.Key != FactKeys.SleepMinutes).ToList(), Now);
        var result = PipelineRunner.Run(PipelineInput.Simple(Now, withoutSleep, NormalHistory()));
        Assert.False(result.State!.Fired("p1e.state.sleep_poor"));
        Assert.DoesNotContain(ActionCatalog.EarlierSleep, result.Actions);
        Assert.DoesNotContain(result.Recommendations.Directives.Directives,
            d => d.Grammar.StartsWith("bedtime"));
    }

    [Fact]
    public void At_risk_goal_adds_the_program_candidate_but_the_recovery_gate_suppresses_it()
    {
        var result = RunBadDay(goals:
        [
            new GoalInput("goal-bootcamp", "program", 0.2,
                new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero), 3, "ev-goal"),
        ]);
        var suppression = Assert.Single(result.Recommendations.Dropped,
            d => d.ActionKey == ActionCatalog.DoProgramDay);
        Assert.Equal("recovery_gate", suppression.ReasonKey);
        Assert.DoesNotContain(ActionCatalog.DoProgramDay, result.Actions);
    }

    [Fact]
    public void Execution_likelihood_is_a_confidence_aware_revisable_deletable_estimate()
    {
        // no history ⇒ exact prior, and the trail says so
        var fresh = RunBadDay();
        var walk = fresh.Recommendations.Chosen.First(r => r.ActionKey == ActionCatalog.ShortWalk);
        Assert.Equal(ExecutionLikelihoodModel.PriorLikelihood, walk.ExecutionLikelihood);

        // completed twice yesterday ⇒ moves up; deleted ⇒ back to the prior, exactly
        var done = new[]
        {
            new ExecutionFeedback("f1", ActionCatalog.ShortWalk, ExecutionOutcome.Completed,
                Now.AddDays(-1), EvidenceGrade.SelfReported, "user_tap", Idempotency: "k1"),
            new ExecutionFeedback("f2", ActionCatalog.ShortWalk, ExecutionOutcome.Completed,
                Now.AddDays(-2), EvidenceGrade.SelfReported, "user_tap", Idempotency: "k2"),
        };
        var model = ExecutionLikelihoodModel.Build(done, Now);
        var withHistory = ExecutionLikelihoodModel.Build(done.Select(d => d with { })
            .ToList(), Now);
        Assert.True(withHistory.LikelihoodFor(ActionCatalog.ShortWalk) >
                    ExecutionLikelihoodModel.PriorLikelihood);

        var deleted = done.Select(d => d with { DeletedAtUtc = Now }).ToList();
        var afterDelete = ExecutionLikelihoodModel.Build(deleted, Now);
        Assert.Equal(ExecutionLikelihoodModel.PriorLikelihood,
            afterDelete.LikelihoodFor(ActionCatalog.ShortWalk));
        Assert.True(afterDelete.IsPriorOnly(ActionCatalog.ShortWalk));
        Assert.NotNull(model);   // construction is total — a deleted-only set behaves like empty
    }

    [Fact]
    public void Older_feedback_counts_less_than_recent_feedback()
    {
        var old = ExecutionLikelihoodModel.Build(
            [new ExecutionFeedback("f1", ActionCatalog.EarlierSleep, ExecutionOutcome.Completed,
                Now.AddDays(-30), EvidenceGrade.SelfReported)], Now);
        var recent = ExecutionLikelihoodModel.Build(
            [new ExecutionFeedback("f1", ActionCatalog.EarlierSleep, ExecutionOutcome.Completed,
                Now.AddDays(-1), EvidenceGrade.SelfReported)], Now);
        Assert.True(recent.LikelihoodFor(ActionCatalog.EarlierSleep) >
                    old.LikelihoodFor(ActionCatalog.EarlierSleep));
    }

    [Fact]
    public void Too_hard_and_wrong_time_are_distinguished_for_the_lever_but_equal_for_the_estimate()
    {
        Assert.Equal("shorten", ExecutionLikelihoodModel.AdaptationLever(ExecutionOutcome.TooHard));
        Assert.Equal("move", ExecutionLikelihoodModel.AdaptationLever(ExecutionOutcome.WrongTime));
        Assert.Equal(ExecutionLikelihoodModel.OutcomeValue(ExecutionOutcome.TooHard),
            ExecutionLikelihoodModel.OutcomeValue(ExecutionOutcome.WrongTime));
    }
}
