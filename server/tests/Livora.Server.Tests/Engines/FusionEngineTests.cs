using Livora.Server.Infrastructure.Engines.Decision;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: the product-law scenarios for the cross-domain fusion engine: ONE coherent plan,
///          a hard recommendation budget, explained conflict resolution (move/shorten/skip),
///          honest refusals for thin history, and the never-fabricate evidence sweep over every
///          number the plan quotes.
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// </summary>
public sealed class FusionEngineTests
{
    private static DecisionOutput Run(DecisionInput input) => new DecisionPipeline().Run(input);

    [Fact]
    public void The_fusion_scenario_yields_one_coherent_plan_covering_every_pressure()
    {
        var output = Run(EngineFixtures.HighMeetingLoad());

        var actions = output.Recommendations;
        var all = output.PlanItems;

        // reduce intensity (placement), a walk, protect one focus block, no extra demanding task,
        // hydration, earlier sleep — the six beats of the coherent story.
        Assert.Contains(all, i => i.Action == EngineAction.ShortenWorkout && i.Kind == "placement");
        Assert.Contains(actions, i => i.Action == EngineAction.ShortWalk && i.DurationMinutes == 20);
        Assert.Contains(all, i => i.Action == EngineAction.ProtectFocusBlocks && i.Kind == "guardrail");
        Assert.Contains(all, i => i.Action == EngineAction.NoExtraDemand);
        Assert.Contains(all, i => i.Action == EngineAction.Hydrate);
        Assert.Contains(actions, i => i.Action == EngineAction.EarlierBedtime);

        // and it stays a plan, not a pamphlet: budget 1 high + 2 medium/low visible actions
        Assert.InRange(actions.Count, 1, NumericRules.MaxTotalRecommendations);
        Assert.True(actions.Count(a => (int)a.Priority >= (int)EnginePriority.High)
                    <= NumericRules.MaxHighPriorityRecommendations);
    }

    [Fact]
    public void Every_item_carries_reason_evidence_priority_and_flexibility()
    {
        foreach (var item in Run(EngineFixtures.HighMeetingLoad()).PlanItems)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.ReasonKey));
            Assert.True(item.ReasonKey.StartsWith("Rule.Reason.", StringComparison.Ordinal)
                        || item.ReasonKey.StartsWith("Plan.Evidence.", StringComparison.Ordinal)
                        || item.ReasonKey.StartsWith("Fusion.", StringComparison.Ordinal),
                $"reason key must be a structured engine key, got {item.ReasonKey}");
            Assert.False(string.IsNullOrEmpty(item.SourceRuleKey));
            Assert.InRange(item.Confidence, 0.0, 1.0);
            Assert.True(Enum.IsDefined(item.Flexibility));       // every item declares its flexibility
            Assert.True(Enum.IsDefined(item.Priority));          // and its priority
            Assert.NotEmpty(item.EvidenceFactIds);
        }
    }

    [Fact]
    public void A_colliding_workout_with_low_recovery_moves_shortens_or_skips_with_explanation()
    {
        // workout 17:30-19:00 overlaps the review meeting window? (no overlap here) -> use a real collision:
        var input = EngineFixtures.HighMeetingLoad() with
        {
            Calendar =
            [
                new("m-1", "meeting", "Standup", 9 * 60, 9 * 60 + 30),
                new("m-2", "meeting", "Planning", 10 * 60, 12 * 60),
                new("m-3", "meeting", "Review", 13 * 60 + 30, 17 * 60),   // ends exactly at the workout
                new("w-1", "workout", "Strength class", 15 * 60, 17 * 60), // buried INSIDE meetings
                new("f-1", "focus", "Deep work", 17 * 60 + 30, 18 * 60 + 20),
            ],
        };
        var output = Run(input);
        var collision = output.PlanItems.FirstOrDefault(i => i.Kind == "placement");
        Assert.NotNull(collision);
        Assert.True(collision!.Action is EngineAction.MoveWorkout or EngineAction.ShortenWorkout or EngineAction.SkipWorkout,
            "a colliding workout with low recovery must be moved/shortened/skipped, never left or blindly appended");
        Assert.NotEmpty(collision.EvidenceFactIds);

        var adaptation = Assert.Single(output.Adaptations);
        Assert.Equal("w-1", adaptation.TargetBlockId);
        Assert.False(string.IsNullOrWhiteSpace(adaptation.ReasonKey));
        // never a blind append: either the block moves to a genuinely free slot or its minutes shrink
        if (adaptation.Verb == "move")
        {
            Assert.DoesNotContain(input.Calendar
                    .Where(c => c.BlockId != "w-1"),
                c => c.StartMinutesOfDay < adaptation.NewStartMinutes + adaptation.NewDurationMinutes
                     && adaptation.NewStartMinutes < c.EndMinutesOfDay);
            Assert.Equal(adaptation.NewDurationMinutes, adaptation.OriginalDurationMinutes); // move keeps content
        }
    }

    [Fact]
    public void Skipping_is_said_out_loud_when_no_keepable_core_fits()
    {
        var input = EngineFixtures.HighMeetingLoad() with
        {
            Calendar =
            [
                new("m-all", "meeting", "Offsite", 6 * 60, 21 * 60),          // the whole day booked
                new("w-1", "workout", "Long session", 10 * 60, 13 * 60),      // 180 min inside the booking
            ],
        };
        var output = Run(input);
        var skip = Assert.Single(output.Adaptations);
        Assert.Equal("skip", skip.Verb);
        Assert.Equal(0, skip.NewDurationMinutes);
        Assert.Contains(output.PlanItems, i => i.Action == EngineAction.SkipWorkout
            && i.ReasonKey.StartsWith("Plan.Evidence.", StringComparison.Ordinal));
    }

    [Fact]
    public void No_workout_in_calendar_means_no_fake_conflict_resolution()
    {
        var output = Run(EngineFixtures.SleepDeprived());   // no calendar at all
        Assert.Empty(output.Adaptations);
        Assert.DoesNotContain(output.PlanItems, i => i.Kind == "placement");
    }

    [Fact]
    public void Beginner_history_refuses_to_reshape_a_workout_for_two_bad_days()
    {
        var output = Run(EngineFixtures.Beginner());   // 3 history days => baseline None (<3 => None; 3 => Low)
        Assert.DoesNotContain(output.Adaptations, a => a.SourceRuleKey is EngineRuleEvaluator.R2LowRecovery);
        // The refusal is EXPLICIT in the record (recovery 0.5 is below the 0.55 floor, so the rule
        // still fires as a walk/recommendation — only the intensity RESHAPE is gated by baseline).
        Assert.Contains(output.Refusals, r => r.StartsWith("intensity-change-refused", StringComparison.Ordinal));
        Assert.DoesNotContain(output.PlanItems, i => i.Kind == "placement");
    }

    [Fact]
    public void Nothing_at_all_says_nothing()
    {
        var output = Run(EngineFixtures.NoData());
        Assert.Equal("insufficient-data", Assert.Single(output.Refusals));
        Assert.Empty(output.Recommendations);
        Assert.Empty(output.PlanItems);
        Assert.Empty(output.Adaptations);
    }

    [Fact]
    public void A_good_day_asks_for_exactly_one_quiet_thing()
    {
        var output = Run(EngineFixtures.NormalUser());
        var action = Assert.Single(output.Recommendations);
        Assert.Equal(EngineAction.KeepRoutine, action.Action);
        Assert.Equal(EnginePriority.Low, action.Priority);
        Assert.Equal(EngineFlexibility.Optional, action.Flexibility);
    }

    [Fact]
    public void Committed_minutes_never_exceed_the_cap_even_when_adding_a_walk()
    {
        var input = EngineFixtures.HighMeetingLoad();
        var output = Run(input);
        int baseCommitted = input.Calendar.Where(c => c.Kind is "workout" or "focus")
            .Sum(c => c.EndMinutesOfDay - c.StartMinutesOfDay);
        int planned = output.PlanItems.Where(i => i.Placement is not null)
            .Sum(i => i.Placement!.DurationMinutes);
        Assert.True(planned <= baseCommitted * NumericRules.TotalMinutesCapFactor + 1e-9,
            $"planned {planned} exceeded 1.2x of {baseCommitted}");
    }

    [Fact]
    public void Every_number_in_the_output_traces_to_a_fact_in_the_state()
    {
        // the never-fabricate sweep, across the fusion scenarios that exist
        foreach (var input in new[]
                 { EngineFixtures.HighMeetingLoad(), EngineFixtures.SleepDeprived(),
                   EngineFixtures.NormalUser(), EngineFixtures.Premium() })
        {
            var output = Run(input);
            var factIds = output.State.Facts.Select(f => f.FactId).ToHashSet(StringComparer.Ordinal);
            foreach (var item in output.PlanItems)
            {
                foreach (var id in item.EvidenceFactIds)
                    Assert.Contains(id, factIds);
                if (item.ReasonArgs.OfType<double>().Any())
                    Assert.NotEmpty(item.EvidenceFactIds);
            }
            foreach (var adaptation in output.Adaptations)
                foreach (var id in adaptation.EvidenceFactIds)
                    Assert.Contains(id, factIds);
        }
    }

    [Fact]
    public void Placements_keep_their_adaptation_backreferences()
    {
        var output = Run(EngineFixtures.HighMeetingLoad());
        var placement = Assert.Single(output.PlanItems, i => i.Kind == "placement");
        Assert.NotNull(placement.Placement);
        var adaptationId = Assert.Single(placement.Placement!.AdaptationIds);
        Assert.Contains(output.Adaptations, a => a.AdaptationId == adaptationId);
    }

    [Fact]
    public void Recommendations_are_the_actions_only_never_guardrails_or_placements()
    {
        var output = Run(EngineFixtures.HighMeetingLoad());
        Assert.All(output.Recommendations, r => Assert.Equal("action", r.Kind));
        Assert.Contains(output.PlanItems, i => i.Kind == "guardrail");
    }
}
