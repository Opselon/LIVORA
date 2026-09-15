using Livora.Server.Infrastructure.Engines.Decision;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: pin the rule layer against the client RuleEngine's R1..R7 semantics (same keys, same
///          thresholds, same priority ladder, same deterministic ordering), plus the two declared
///          server-only rules. Every assertion quotes the client's number, not a re-derived one.
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// </summary>
public sealed class EngineRuleTests
{
    private static IReadOnlyList<RuleHit> Fire(DecisionInput input)
        => EngineRuleEvaluator.Evaluate(EngineStateComputer.Compute(input), input);

    [Fact]
    public void SleepDebt_fires_at_1_5_hours_below_personal_baseline_and_goes_high_at_2_5()
    {
        // 470 baseline, 330 today => 2.33h: fires Medium (the client's High ceiling is 2.5h).
        var hits = Fire(EngineFixtures.SleepDeprived());
        var debt = Assert.Single(hits, h => h.RuleKey == EngineRuleEvaluator.R1SleepDebt);
        Assert.Equal(EnginePriority.Medium, debt.Priority);
        Assert.Equal(2.333, debt.ReasonArgs[0] is double d ? Math.Round(d, 3) : 0);

        // 2.6h deficit crosses the client's 2.5h High threshold.
        var deeper = EngineFixtures.SleepDeprived() with
        { Today = EngineFixtures.SleepDeprived().Today with { SleepMinutes = 314 } };
        var deep = Assert.Single(Fire(deeper), h => h.RuleKey == EngineRuleEvaluator.R1SleepDebt);
        Assert.Equal(EnginePriority.High, deep.Priority);
    }

    [Fact]
    public void SleepDebt_also_asks_for_intensity_reduction_as_a_separate_rule()
    {
        var hits = Fire(EngineFixtures.SleepDeprived());
        Assert.Contains(hits, h => h.RuleKey == EngineRuleEvaluator.R1bSleepDebtReduceIntensity
                                   && h.Action == EngineAction.ReduceTrainingIntensity
                                   && h.PlanAdjustments.Contains("exercise:*0.5"));
    }

    [Fact]
    public void Low_recovery_fires_on_the_absolute_floor_or_a_big_drop_vs_baseline()
    {
        var floor = Fire(EngineFixtures.HighMeetingLoad());     // recovery 0.42 < 0.55
        Assert.Contains(floor, h => h.RuleKey == EngineRuleEvaluator.R2LowRecovery);

        var drop = EngineFixtures.NormalUser() with
        { Today = EngineFixtures.NormalUser().Today with { RecoveryScore = 0.60 } }; // 0.75 base, -20%
        Assert.Contains(Fire(drop), h => h.RuleKey == EngineRuleEvaluator.R2LowRecovery);

        var fine = Fire(EngineFixtures.NormalUser());           // 0.77, no drop
        Assert.DoesNotContain(fine, h => h.RuleKey == EngineRuleEvaluator.R2LowRecovery);
    }

    [Fact]
    public void High_stress_fires_two_rules_with_the_clients_priority_split()
    {
        var hits = Fire(EngineFixtures.HighMeetingLoad());      // stress 0.70 > 0.65, not > 0.8
        var main = Assert.Single(hits, h => h.RuleKey == EngineRuleEvaluator.R3HighStress);
        Assert.Equal(EnginePriority.Medium, main.Priority);
        Assert.Contains(hits, h => h.RuleKey == EngineRuleEvaluator.R3bHighStressScreens
                                   && h.Action == EngineAction.ModerateScreenTime);

        var severe = EngineFixtures.HighMeetingLoad() with
        { Today = EngineFixtures.HighMeetingLoad().Today with { Stress = 0.85 } };
        Assert.Equal(EnginePriority.High,
            Assert.Single(Fire(severe), h => h.RuleKey == EngineRuleEvaluator.R3HighStress).Priority);
    }

    [Fact]
    public void Activity_deficit_needs_both_the_55_percent_rule_and_recovery_not_being_low()
    {
        var depleted = Fire(EngineFixtures.HighMeetingLoad());  // steps 3200 vs 9000 base, rec 0.42 < 0.55
        // client guard: rec?.Value >= RecoveryBelow — recovery IS low, so R4 must not pile on
        Assert.DoesNotContain(depleted, h => h.RuleKey == EngineRuleEvaluator.R4ActivityDeficit);

        var onlySteps = EngineFixtures.NormalUser() with
        { Today = EngineFixtures.NormalUser().Today with { Steps = 3_200, RecoveryScore = 0.7 } };
        Assert.Contains(Fire(onlySteps), h => h.RuleKey == EngineRuleEvaluator.R4ActivityDeficit);
    }

    [Fact]
    public void Momentum_is_the_quiet_anchor_of_a_good_day_and_only_of_a_good_day()
    {
        Assert.Contains(Fire(EngineFixtures.NormalUser()), h => h.RuleKey == EngineRuleEvaluator.R5PositiveMomentum);
        Assert.DoesNotContain(Fire(EngineFixtures.SleepDeprived()), h => h.RuleKey == EngineRuleEvaluator.R5PositiveMomentum);
    }

    [Fact]
    public void Habit_at_risk_needs_a_streak_of_three_nothing_logged_and_evening_clock()
    {
        var evening = EngineFixtures.NormalUser() with
        { Habits = [new EngineHabit("h1", "Meditate", 5, CompletedToday: false)] };
        Assert.Contains(Fire(evening), h => h.RuleKey == EngineRuleEvaluator.R6HabitAtRisk);

        var morning = evening with { AsOfUtc = new DateTime(2026, 3, 16, 9, 0, 0, DateTimeKind.Utc) };
        Assert.DoesNotContain(Fire(morning), h => h.RuleKey == EngineRuleEvaluator.R6HabitAtRisk);

        var done = evening with { Habits = [new EngineHabit("h1", "Meditate", 5, CompletedToday: true)] };
        Assert.DoesNotContain(Fire(done), h => h.RuleKey == EngineRuleEvaluator.R6HabitAtRisk);

        var shortStreak = evening with { Habits = [new EngineHabit("h1", "Meditate", 2, CompletedToday: false)] };
        Assert.DoesNotContain(Fire(shortStreak), h => h.RuleKey == EngineRuleEvaluator.R6HabitAtRisk);
    }

    [Fact]
    public void Stale_feed_is_a_guard_not_a_task_and_asks_for_nothing()
    {
        var hits = Fire(EngineFixtures.PartialData());
        var guard = Assert.Single(hits, h => h.RuleKey == EngineRuleEvaluator.R7StaleSleep);
        Assert.Equal(EngineAction.None, guard.Action);
        Assert.Equal(EnginePriority.Optional, guard.Priority);
        Assert.Empty(guard.PlanAdjustments);   // a missing feed is not a reason to change anyone's day
    }

    [Fact]
    public void Server_rules_measure_meeting_load_and_screen_time_from_the_input()
    {
        var hits = Fire(EngineFixtures.HighMeetingLoad());
        Assert.Equal(300, Assert.Single(hits, h => h.RuleKey == EngineRuleEvaluator.S1MeetingLoad).ReasonArgs[0]);
        Assert.Contains(hits, h => h.RuleKey == EngineRuleEvaluator.S2ScreenTimeHigh);

        var quiet = EngineFixtures.HighMeetingLoad() with
        { Calendar = [], ScreenTimeMinutesToday = null };
        var quietHits = Fire(quiet);
        Assert.DoesNotContain(quietHits, h => h.RuleKey == EngineRuleEvaluator.S1MeetingLoad);
        Assert.DoesNotContain(quietHits, h => h.RuleKey == EngineRuleEvaluator.S2ScreenTimeHigh);
    }

    [Fact]
    public void Ordering_is_total_priority_descending_then_rule_key_ordinal()
    {
        var hits = Fire(EngineFixtures.HighMeetingLoad());
        Assert.True(hits.Count > 3);
        for (int i = 1; i < hits.Count; i++)
        {
            var (prev, next) = (hits[i - 1], hits[i]);
            Assert.True((int)prev.Priority > (int)next.Priority
                || ((int)prev.Priority == (int)next.Priority
                    && string.CompareOrdinal(prev.RuleKey, next.RuleKey) < 0),
                $"ordering broken between {prev.RuleKey} and {next.RuleKey}");
        }
    }

    [Fact]
    public void Every_hit_traces_its_numbers_to_facts_that_exist_in_the_state()
    {
        var input = EngineFixtures.HighMeetingLoad();
        var state = EngineStateComputer.Compute(input);
        var hits = EngineRuleEvaluator.Evaluate(state, input);
        var factIds = state.Facts.Select(f => f.FactId).ToHashSet(StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            Assert.False(string.IsNullOrWhiteSpace(hit.ReasonKey), $"{hit.RuleKey} has no reason key");
            foreach (var id in hit.EvidenceFactIds)
                Assert.Contains(id, factIds);   // never a citation to a number that does not exist
            if (hit.ReasonArgs.OfType<double>().Any())
                Assert.NotEmpty(hit.EvidenceFactIds);   // a numeric claim without evidence is a bug
        }
    }

    [Fact]
    public void No_data_fires_no_action_rules_only_the_honest_stale_guard()
    {
        var input = EngineFixtures.NoData();
        var state = EngineStateComputer.Compute(input);
        var hits = EngineRuleEvaluator.Evaluate(state, input);
        Assert.All(hits, h => Assert.Equal(EngineRuleEvaluator.R7StaleSleep, h.RuleKey));
        Assert.Equal(0.0, state.DataCompleteness);
    }
}
