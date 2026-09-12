using LIVORA.Application.Abstractions;
using LIVORA.Application.Insights;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Honesty gate for the weekly look-back: LIVORA says nothing it cannot support with
/// persisted history. Fewer than 3 closed days => no summary at all.
/// </summary>
public class WeeklySummaryServiceTests
{
    private static readonly DateTime Today = new(2026, 9, 11);
    // The closed window the service looks at: 2026-09-04 .. 2026-09-10 (yesterday back 7 days).
    private static readonly DateTime WeekStart = new(2026, 9, 4);
    private static readonly DateTime PriorStart = new(2026, 8, 28);

    private static WeeklySummaryService Service(
        IEnumerable<DailyHistoryRecord>? history = null,
        IEnumerable<Habit>? habits = null,
        IEnumerable<Goal>? goals = null) =>
        new(
            new FakeHistoryRepository(history ?? Array.Empty<DailyHistoryRecord>()),
            new InMemoryRepo<Habit>(habits ?? Array.Empty<Habit>()),
            new InMemoryRepo<Goal>(goals ?? Array.Empty<Goal>()),
            new TrendService(),
            new FakeClock(Today));

    private static DailyHistoryRecord Rec(DateTime date, double sleep = 450, int steps = 8000,
        double recovery = 0.7, double stress = 0.4) => new()
        {
            Date = date,
            Origin = nameof(DataOrigin.Mock),
            Completeness = 1,
            SleepMinutes = sleep,
            SleepQuality = 0.8,
            SleepConsistency = 0.8,
            BedtimeMinutesOfDay = 1380,
            Steps = steps,
            ActiveMinutes = 30,
            RecoveryScore = recovery,
            Stress = stress,
            Mood = 0.7,
            Energy = 0.7,
        };

    /// <summary>Seven closed days, each domain shape overridable per day index.</summary>
    private static List<DailyHistoryRecord> Week(
        Func<int, double>? sleep = null, Func<int, double>? stress = null,
        Func<int, double>? recovery = null, Func<int, int>? steps = null) =>
        Enumerable.Range(0, 7).Select(i => Rec(
            WeekStart.AddDays(i),
            sleep: sleep?.Invoke(i) ?? 450,
            steps: steps?.Invoke(i) ?? 8000,
            recovery: recovery?.Invoke(i) ?? 0.7,
            stress: stress?.Invoke(i) ?? 0.4)).ToList();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FewerThanThreeClosedDays_SaysNothing(int days)
    {
        var history = Enumerable.Range(0, days).Select(i => Rec(WeekStart.AddDays(i))).ToList();
        var summary = await Service(history).BuildLastWeekAsync();
        Assert.Null(summary);   // never invent a trend out of a couple of days
    }

    [Fact]
    public async Task DaysOutsideTheWindow_DoNotCountTowardHonestyGate()
    {
        // Five older days from the prior week are still "not enough data" for last week.
        var history = Enumerable.Range(0, 5).Select(i => Rec(PriorStart.AddDays(i))).ToList();
        Assert.Null(await Service(history).BuildLastWeekAsync());
    }

    [Fact]
    public async Task ThreeClosedDays_ReturnsSummary_ButLabelsConfidenceLow()
    {
        var history = Enumerable.Range(0, 3).Select(i => Rec(WeekStart.AddDays(i))).ToList();
        var s = await Service(history).BuildLastWeekAsync();

        Assert.NotNull(s);
        Assert.Equal(BaselineConfidence.Low, s!.Confidence);
        Assert.Equal(WeekStart, s.WeekStart);
        Assert.Equal(Today.AddDays(-1), s.WeekEnd);   // the window is calendar-fixed, not data-shaped
        // Three samples cannot support a trend claim: every direction must stay InsufficientData.
        Assert.Equal(TrendDirection.InsufficientData, s.SleepTrend);
        Assert.Equal(TrendDirection.InsufficientData, s.ActivityTrend);
        Assert.Equal(TrendDirection.InsufficientData, s.RecoveryTrend);
        Assert.Equal(TrendDirection.InsufficientData, s.StressTrend);
        Assert.Empty(s.ImprovementKeys);   // ...and therefore no bullets either
        Assert.Empty(s.DeclineKeys);
    }

    [Fact]
    public async Task SixClosedDays_IsMedium_SevenPlusSevenPriorIsHigh()
    {
        var six = Enumerable.Range(0, 6).Select(i => Rec(WeekStart.AddDays(i))).ToList();
        var partial = await Service(six).BuildLastWeekAsync();
        Assert.Equal(BaselineConfidence.Medium, partial!.Confidence);

        var full = Week();
        full.AddRange(Enumerable.Range(0, 7).Select(i => Rec(PriorStart.AddDays(i))));
        var strong = await Service(full).BuildLastWeekAsync();
        Assert.Equal(BaselineConfidence.High, strong!.Confidence);
    }

    [Fact]
    public async Task RisingSleep_ReportsImprovementOnly()
    {
        var s = await Service(Week(sleep: i => 360 + i * 20)).BuildLastWeekAsync();
        Assert.NotNull(s);
        Assert.Equal(TrendDirection.Improving, s!.SleepTrend);
        Assert.Equal(new[] { "Weekly.Up.Sleep" }, s.ImprovementKeys);
        Assert.Empty(s.DeclineKeys);
    }

    [Fact]
    public async Task RisingStress_CountsAsDecline_AndBecomesTheFocus()
    {
        // Stress is inverted: going up is getting worse, and it outranks sleep for next week.
        var s = await Service(Week(stress: i => 0.5 + i * 0.02)).BuildLastWeekAsync();
        Assert.NotNull(s);
        Assert.Equal(TrendDirection.Declining, s!.StressTrend);
        Assert.DoesNotContain("Weekly.Up.Stress", s.ImprovementKeys);
        Assert.Contains("Weekly.Down.Stress", s.DeclineKeys);
        Assert.Equal("Weekly.Focus.Stress", s.FocusKey);
    }

    [Fact]
    public async Task FallingSleep_WithoutStress_SleepBecomesTheFocus()
    {
        var s = await Service(Week(sleep: i => 480 - i * 20)).BuildLastWeekAsync();
        Assert.Equal(TrendDirection.Declining, s!.SleepTrend);
        Assert.Contains("Weekly.Down.Sleep", s.DeclineKeys);
        Assert.Equal("Weekly.Focus.Sleep", s.FocusKey);
    }

    [Fact]
    public async Task FlatWeek_NoTrendBullets_FallsBackToHabitAdvice()
    {
        var s = await Service(Week()).BuildLastWeekAsync();
        Assert.NotNull(s);
        Assert.Empty(s!.ImprovementKeys);      // Stable is not an achievement claim
        Assert.Empty(s.DeclineKeys);
        // No habits at all => cannot promise momentum, so it asks for exactly one habit.
        Assert.Equal("Weekly.Focus.OneHabit", s.FocusKey);
    }

    [Fact]
    public async Task StrongHabitConsistency_BecomesMomentumFocus_WithStreakArg()
    {
        var walk = new Habit { Id = "h1", Name = "Walk" };
        for (int i = 0; i < 7; i++) walk.Complete(WeekStart.AddDays(i).AddHours(7));

        var s = await Service(Week(), habits: new[] { walk }).BuildLastWeekAsync();
        Assert.NotNull(s);
        Assert.Equal(1.0, s!.HabitConsistency, 6);
        Assert.Equal("Weekly.Focus.Momentum", s.FocusKey);
        Assert.Equal(7, s.StreakDays);
        Assert.Equal(new object[] { 7 }, s.FocusArgs);   // the single most motivating number
    }

    [Fact]
    public async Task Bullets_OnlyEverUseTheWeeklyPrefixes()
    {
        // Mixed week: sleep + stress improving, activity + recovery declining.
        var history = Week(
            sleep: i => 360 + i * 20,
            steps: i => 10_000 - i * 400,
            stress: i => 0.6 - i * 0.02,
            recovery: i => 0.8 - i * 0.03);

        var s = (await Service(history).BuildLastWeekAsync())!;
        Assert.Equal(new[] { "Weekly.Up.Sleep", "Weekly.Up.Stress" },
            s.ImprovementKeys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "Weekly.Down.Activity", "Weekly.Down.Recovery" },
            s.DeclineKeys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(s.ImprovementKeys, k => Assert.StartsWith("Weekly.Up.", k));
        Assert.All(s.DeclineKeys, k => Assert.StartsWith("Weekly.Down.", k));
        Assert.StartsWith("Weekly.Focus.", s.FocusKey);
    }

    [Fact]
    public async Task GoalFraction_ExcludesArchivedGoals()
    {
        var goals = new[]
        {
            new Goal { Id = "g1", Name = "Sleep 7h", TargetValue = 10, ProgressValue = 5 },          // 50%
            new Goal { Id = "g2", Name = "Old goal", TargetValue = 10, ProgressValue = 10, IsArchived = true },
        };
        var s = await Service(Week(), goals: goals).BuildLastWeekAsync();
        Assert.Equal(0.5, s!.GoalProgressFraction, 6);   // no 100% inflation from archived goals
    }

    [Fact]
    public async Task Today_IsNotPartOfLastWeek()
    {
        // Today (09-11) is still open: a wild record for it must not enter the closed window.
        var closed = Week();
        var withToday = closed.Concat(new[] { Rec(Today, sleep: 1440, steps: 99_999, stress: 1) }).ToList();

        var clean = await Service(closed).BuildLastWeekAsync();
        var polluted = await Service(withToday).BuildLastWeekAsync();

        Assert.NotNull(clean);
        Assert.Equal(clean!.SleepTrend, polluted!.SleepTrend);
        Assert.Equal(clean.ImprovementKeys, polluted.ImprovementKeys);
        Assert.Equal(clean.DeclineKeys, polluted.DeclineKeys);
        Assert.Equal(clean.FocusKey, polluted.FocusKey);
        Assert.Equal(clean.Confidence, polluted.Confidence);
        Assert.Equal(clean.WeekEnd, polluted.WeekEnd);
    }

    [Fact]
    public async Task SameHistory_ProducesTheSameSummary()
    {
        var a = await Service(Week(sleep: i => 360 + i * 20)).BuildLastWeekAsync();
        var b = await Service(Week(sleep: i => 360 + i * 20)).BuildLastWeekAsync();
        Assert.Equal(a!.SleepTrend, b!.SleepTrend);
        Assert.Equal(a.ImprovementKeys, b.ImprovementKeys);
        Assert.Equal(a.DeclineKeys, b.DeclineKeys);
        Assert.Equal(a.FocusKey, b.FocusKey);
        Assert.Equal(a.Confidence, b.Confidence);
    }
}

/// <summary>
/// Topic selection: the mock intelligence provider maps deterministic rule facts to one semantic
/// topic, and must downgrade to "balanced day" when the underlying feed is stale.
/// </summary>
public class IntelligenceTopicSelectionTests
{
    private static UserProfile Profile() => new();

    private static InsightInterpretation Interpret(PersonalState state) =>
        new Infrastructure.IntelligenceProviders.SampleIntelligenceProvider(new RuleEngine())
            .InterpretAsync(state, Array.Empty<Recommendation>(), Profile())
            .GetAwaiter().GetResult();

    [Theory]
    [InlineData(true, false, false, InsightTopic.SleepDebt)]      // big sleep deficit only
    [InlineData(false, true, false, InsightTopic.LowRecovery)]
    [InlineData(false, false, true, InsightTopic.HighStress)]
    public void TopFiringRule_DrivesTheTopic(bool sleepDebt, bool lowRecovery, bool stress, InsightTopic expected)
    {
        var state = StateFactory.State(
            sleepMin: sleepDebt ? 330 : 455, sleepBase: 450,
            recovery: lowRecovery ? 0.4 : 0.72,
            stress: stress ? 0.8 : 0.35);

        var interp = Interpret(state);
        Assert.Equal($"Insight.Title.{expected}", interp.HeadlineKey);
        Assert.True(interp.Confidence > 0 && interp.Confidence <= 1, $"confidence {interp.Confidence} out of range");
    }

    [Fact]
    public void GreatDay_SelectsPositiveMomentum_NotAnIntervention()
    {
        var state = StateFactory.State(sleepMin: 455, sleepBase: 450, recovery: 0.72, stress: 0.35)
            .WithHabitSnapshots(new HabitStateSnapshot { HabitId = "h1", Name = "Walk", Streak = 5 });
        var interp = Interpret(state);
        Assert.Equal("Insight.Title.PositiveMomentum", interp.HeadlineKey);
        Assert.Equal(InsightPriority.Normal, interp.Priority);   // nothing to alarm about
        Assert.Equal(new object[] { 5 }, interp.BodyArgs);       // the streak is the honest reason
    }

    [Fact]
    public void NoRulesAtAll_FallsBackToBalancedDay()
    {
        // Recovery inside the noise band, no baseline, stress below ceiling => nothing may be claimed.
        var state = StateFactory.State(sleepMin: 420, sleepBase: 0, recovery: 0.6, stress: 0.6);
        var interp = Interpret(state);
        Assert.Equal("Insight.Title.BalancedDay", interp.HeadlineKey);
        Assert.Equal("Insight.Reason.Balanced", interp.BodyKey);
        Assert.Equal(InsightPriority.Normal, interp.Priority);
    }

    [Fact]
    public void StaleFeed_OverridesConfidentTopicToBalancedDay()
    {
        // Same sleep debt, but the feed stopped 6 days ago: no headline certainty allowed.
        var fresh = Interpret(StateFactory.State(sleepMin: 330, sleepBase: 450));
        var stale = Interpret(StateFactory.State(sleepMin: 330, sleepBase: 450, sleepDaysSinceFreshData: 6));

        Assert.Equal("Insight.Title.SleepDebt", fresh.HeadlineKey);
        Assert.Equal("Insight.Title.BalancedDay", stale.HeadlineKey);
        Assert.Equal("Insight.Reason.Balanced", stale.BodyKey);
        Assert.True(stale.Confidence < fresh.Confidence);
    }

    [Fact]
    public void BaselineConfidence_ChangesClaimedConfidence_NotTheTopic()
    {
        var confident = Interpret(StateFactory.State(sleepMin: 330, sleepBase: 450, sleepConfidence: BaselineConfidence.High));
        var shaky = Interpret(StateFactory.State(sleepMin: 330, sleepBase: 450, sleepConfidence: BaselineConfidence.Low));

        Assert.Equal(confident.HeadlineKey, shaky.HeadlineKey);   // same fact, same topic
        Assert.True(confident.Confidence > shaky.Confidence,
            $"High baseline claimed {confident.Confidence}, Low baseline claimed {shaky.Confidence}");
    }

    [Fact]
    public void SameState_ProducesTheSameInterpretation()
    {
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, stress: 0.75);
        var a = Interpret(state);
        var b = Interpret(state);
        Assert.Equal(a.HeadlineKey, b.HeadlineKey);
        Assert.Equal(a.BodyKey, b.BodyKey);
        Assert.Equal(a.BodyArgs, b.BodyArgs);
        Assert.Equal(a.Priority, b.Priority);
        Assert.Equal(a.Confidence, b.Confidence);
    }

    [Fact]
    public void Interpretation_NeverCarriesRecommendationOverrides()
    {
        // The mock provider may rephrase, but must not smuggle in new actions.
        Assert.Empty(Interpret(StateFactory.State(recovery: 0.3)).SuggestedRecommendationOverrides);
    }

    [Fact]
    public async Task Orchestrator_ComposesRulesRecommendationsAndInterpretation()
    {
        var rules = new RuleEngine();
        var orch = new IntelligenceOrchestrator(
            rules,
            new RecommendationService(rules),
            new Infrastructure.IntelligenceProviders.SampleIntelligenceProvider(rules),
            new DailyPlanService(rules, new InMemoryRepo<Bootcamp>()));

        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, recovery: 0.4, stress: 0.8);
        var plan = new DailyPlan { Date = new DateTime(2026, 9, 11), Items = Array.Empty<PlanItem>() };
        var insight = await orch.GenerateDailyInsightAsync(state, Profile(), plan, Array.Empty<Goal>(), Array.Empty<Habit>());

        Assert.Equal("Insight.Title.HighStress", insight.TitleKey);   // highest-priority rule owns the headline
        Assert.NotEmpty(insight.Recommendations);
        Assert.True(insight.Recommendations.Count <= 3);              // overwhelm guard survives composition
        Assert.Equal(SourceType.Mock, insight.Source);                // honestly labeled: not a real AI call
        Assert.InRange(insight.Confidence, 0, 1);
        Assert.True(insight.RelatedMetricKeys.Count <= 3);
        Assert.All(insight.RelatedMetricKeys, k => Assert.StartsWith("Health.", k));
        Assert.All(insight.Recommendations, r => Assert.False(string.IsNullOrEmpty(r.ExplainKey)));
    }

    [Fact]
    public async Task Orchestrator_ExplainPrefersStructuredKeyThenLegacy()
    {
        var rules = new RuleEngine();
        var orch = new IntelligenceOrchestrator(
            rules,
            new RecommendationService(rules),
            new Infrastructure.IntelligenceProviders.SampleIntelligenceProvider(rules),
            new DailyPlanService(rules, new InMemoryRepo<Bootcamp>()));

        Assert.Equal("Rule.Reason.StressAboveUsual",
            orch.ExplainRecommendationKey(new Recommendation { ExplainKey = "Rule.Reason.StressAboveUsual", ReasonKey = "legacy" }));
        Assert.Equal("Old.Reason", orch.ExplainRecommendationKey(new Recommendation { ReasonKey = "Old.Reason" }));

        // Calm day: the positive anchor must still be explainable.
        var state = StateFactory.State(sleepMin: 455, sleepBase: 450, recovery: 0.72, stress: 0.35);
        var insight = await orch.GenerateDailyInsightAsync(state, Profile(),
            new DailyPlan { Date = state.GeneratedAt.Date, Items = Array.Empty<PlanItem>() },
            Array.Empty<Goal>(), Array.Empty<Habit>());
        Assert.All(insight.Recommendations, r => Assert.False(string.IsNullOrEmpty(orch.ExplainRecommendationKey(r))));
    }
}
