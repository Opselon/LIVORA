using Livora.Server.Infrastructure.Engines.Decision;
using Livora.Server.Infrastructure.Engines.Decision.Explanation;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: pin the weekly look-back refusal law (the client WeeklySummaryService rule the whole
///          lane mirrors: under 3 days say NOTHING), the trend ladder, and the deterministic
///          explanation port (en + fa + visible [missing:key] + Persian digits, zero AI calls).
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// </summary>
public sealed class WeeklyAndExplanationTests
{
    private static EngineDayRecord Day(int back, double sleep, double steps = 9_000,
        double stress = 0.4, double recovery = 0.7) => new(
        EngineFixtures.AsOf.AddDays(-back), SleepMinutes: sleep, Steps: steps,
        RecoveryScore: recovery, Stress: stress, SleepQuality: 0.7, Energy: 0.6,
        Mood: 0.65, ActiveMinutes: 40);

    [Fact]
    public void Weekly_review_says_nothing_below_three_days_and_says_why()
    {
        var history = new[] { Day(2, 400), Day(3, 410) };
        var input = EngineFixtures.NormalUser() with { History = history };

        var r = EngineWeeklyReview.Review(input);
        Assert.False(r.Available);
        Assert.Equal("insufficient_data", r.RefusalReason);
        Assert.Equal(2, r.DaysPresentInWindow);
        Assert.Equal(EngineTrend.InsufficientData, r.SleepTrend);
        Assert.Empty(r.ImprovementKeys);
        Assert.Empty(r.DeclineKeys);
    }

    [Fact]
    public void Weekly_review_reads_a_declining_sleep_week_and_aims_focus_there()
    {
        // The week's OLDEST days had MORE sleep (back=7 -> 554, back=1 -> 482): declining toward now.
        var history = Enumerable.Range(1, 7).Select(i => Day(i, 470 + i * 12)).ToList();
        var r = EngineWeeklyReview.Review(EngineFixtures.NormalUser() with { History = history });

        Assert.True(r.Available);
        Assert.Equal(7, r.DaysPresentInWindow);
        Assert.Equal(EngineTrend.Declining, r.SleepTrend);
        Assert.Contains("Weekly.Down.Sleep", r.DeclineKeys);
        Assert.Equal("Weekly.Focus.Sleep", r.FocusKey);
        // 7 this week + 0 prior: NOT High (two full weeks earn that) — the client ladder, verbatim.
        Assert.Equal(EngineBaselineConfidence.Medium, r.Confidence);
    }

    [Fact]
    public void Flat_weeks_are_stable_and_confident_only_with_two_full_weeks()
    {
        var history = Enumerable.Range(1, 14).Select(i => Day(i, 450)).ToList();
        var r = EngineWeeklyReview.Review(EngineFixtures.NormalUser() with { History = history });
        Assert.True(r.Available);
        Assert.Equal(EngineTrend.Stable, r.SleepTrend);
        Assert.Equal(EngineTrend.Stable, r.ActivityTrend);
        Assert.Equal(EngineBaselineConfidence.High, r.Confidence);   // 7 + 7 => High (client rule)
        Assert.Empty(r.DeclineKeys);
    }

    [Fact]
    public void Stress_declining_counts_as_improving_not_worsening()
    {
        // stress rising over the week = WORSE (inverted polarity, client higherIsBetter=false)
        var history = Enumerable.Range(1, 7).Select(i => Day(8 - i, 450, stress: 0.3 + i * 0.02)).ToList();
        var r = EngineWeeklyReview.Review(EngineFixtures.NormalUser() with { History = history });
        Assert.Equal(EngineTrend.Declining, r.StressTrend);          // declining wellness
        Assert.Contains("Weekly.Down.Stress", r.DeclineKeys);
        Assert.Equal("Weekly.Focus.Stress", r.FocusKey);             // stress outranks sleep in the ladder
    }

    // ---- explanation port: deterministic, bilingual, never invented --------------------------

    [Fact]
    public void Renderer_knows_every_key_the_engines_can_emit()
    {
        foreach (var input in new[] { EngineFixtures.HighMeetingLoad(), EngineFixtures.SleepDeprived(),
                                      EngineFixtures.NormalUser(), EngineFixtures.Premium() })
        {
            var output = new DecisionPipeline().Run(input);
            foreach (var item in output.PlanItems)
                Assert.True(DeterministicExplanationRenderer.Knows(item.ReasonKey),
                    $"engine emitted a key with no template: {item.ReasonKey}");
            foreach (var adaptation in output.Adaptations)
                Assert.True(DeterministicExplanationRenderer.Knows(adaptation.ReasonKey),
                    $"adaptation emitted a key with no template: {adaptation.ReasonKey}");
        }
    }

    [Fact]
    public void Every_known_english_template_renders_with_its_arguments()
    {
        foreach (var key in DeterministicExplanationRenderer.KnownKeys)
        {
            var text = DeterministicExplanationRenderer.Render(key, ArgumentsFor(key), "en");
            Assert.DoesNotContain("[missing:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);   // no unrendered placeholders
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    [Fact]
    public void Every_key_exists_in_persian_and_numbers_localize_to_persian_digits()
    {
        foreach (var key in DeterministicExplanationRenderer.KnownKeys)
        {
            Assert.True(DeterministicExplanationRenderer.Knows(key, "fa"), $"no fa template for {key}");
            var args = ArgumentsFor(key);
            var text = DeterministicExplanationRenderer.Render(key, args, "fa");
            Assert.DoesNotContain("[missing:", text, StringComparison.Ordinal);
            bool numeric = args.Any(a => a is double or int);
            if (numeric)
            {
                Assert.Matches("[۰-۹]", text);                    // Persian digits present
                Assert.DoesNotMatch("[0-9]", text);              // and no Latin digits leak through
            }
        }
    }

    [Fact]
    public void Unknown_key_is_visible_never_invented()
    {
        Assert.Equal("[missing:No.Such.Key]", DeterministicExplanationRenderer.Render("No.Such.Key", []));
    }

    [Fact]
    public void Explanation_port_labels_itself_deterministic()
    {
        IExplanationProvider provider = new DeterministicExplanationProvider();
        var output = new DecisionPipeline().Run(EngineFixtures.HighMeetingLoad());
        var result = provider.Explain(output.PlanItems, "en");
        Assert.Equal("deterministic_template", result.GeneratedBy);   // wire-stable honesty label
        Assert.True(provider.IsAvailable);
        Assert.Equal(output.PlanItems.Count, result.Lines.Count);
        Assert.All(result.Lines, l => Assert.True(l.KeyKnown));
    }

    private static IReadOnlyList<object> ArgumentsFor(string key) => key switch
    {
        "Rule.Reason.SleepBelowBaseline" => [2.3],
        "Rule.Reason.RecoveryBelowBaseline" => [44],
        "Rule.Reason.StressAboveUsual" => [0.72],           // template carries {0} — arity must match
        "Rule.Reason.StepsBelowBaseline" => [3_200.0, 9_000.0],
        "Rule.Reason.HabitStreakAtRisk" => ["Walk", 4],
        "Rule.Reason.SleepDataStale" => [3],
        "Rule.Reason.MeetingLoadHigh" => [300],
        "Rule.Reason.ScreenTimeHigh" => [320],
        "Plan.Evidence.RecoveryBelowBaseline" => [44],
        "Plan.Change.MoveWorkout" => [90, 60],
        "Plan.Change.ShrinkExercise" => [60, 30, 44],
        "Plan.Change.SkipWorkout" => [60, 44],
        "Fusion.ProtectOneFocusBlock" => [300, 1],
        "Fusion.NoExtraDemandToday" => [300],
        "Rec.CompleteHabit" => ["Walk", 4],                 // the engine emits it with habit+streak (DeterministicExplanationRenderer.cs:44)
        "Weekly.Focus.Momentum" => [5],
        "pattern.evidence.late-nights" => [3, 7, 60],
        "pattern.evidence.weekday-dip" => [0, 47, 6],
        "pattern.evidence.poor-sleep-focus" => [66, 9, 6],
        "pattern.evidence.goal-stagnant" => [12, 14],
        _ => Array.Empty<object>(),
    };
}
