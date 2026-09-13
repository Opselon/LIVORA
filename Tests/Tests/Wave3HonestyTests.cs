using LIVORA.Application.Abstractions;
using LIVORA.Application.HealthData;
using LIVORA.Application.Insights;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;
using StateMetrics = LIVORA.Domain.Models.State.Metrics;

namespace LIVORA.Tests.Tests;

/// <summary>
/// The catalogue of every (key, arg-count) pair the pure layers can emit. Hand-maintained on
/// purpose: it is the *contract* between the engines and the two resx files. When a lane adds a rule
/// that speaks a new key, the catalogue has to grow — and then
/// <see cref="Wave3ResxIntegrityTests"/>, <see cref="Wave3HonestyTests"/> and
/// <see cref="ApplicationPurityTests"/> fail until the key exists in BOTH languages with matching
/// placeholders. That is what makes "localize every user-facing string" enforceable instead of a
/// code-review plea.
/// </summary>
internal static class Wave3EmissionCatalog
{
    /// <summary>key -> number of format args the engines actually pass. Must exist in EN and FA.</summary>
    public static IReadOnlyDictionary<string, int> KeysWithArgCounts() => new Dictionary<string, int>
    {
        // RuleEngine condition keys
        ["Rule.Reason.SleepBelowBaseline"] = 1,      // deficit hours
        ["Rule.Reason.RecoveryBelowBaseline"] = 1,   // recovery value (template may ignore it)
        ["Rule.Reason.StressAboveUsual"] = 1,        // stress value
        ["Rule.Reason.StepsBelowBaseline"] = 0,
        ["Rule.Reason.AllNearBaseline"] = 0,
        ["Rule.Reason.HabitStreakAtRisk"] = 2,       // habit name, streak length
        ["Rule.Reason.SleepDataStale"] = 1,          // days old

        // RecommendationService
        ["Rec.KeepRoutine"] = 0,
        ["Benefit.SleepRecovery"] = 0,
        ["Benefit.PreserveRecovery"] = 0,
        ["Benefit.GentleMovement"] = 0,
        ["Benefit.StressRelease"] = 0,
        ["Benefit.BetterSleepOnset"] = 0,
        ["Benefit.StreakMomentum"] = 0,
        ["Benefit.Stability"] = 0,
        ["Benefit.General"] = 0,

        // DailyPlanService skeleton + adaptation grammar
        ["Plan.Item.Habit"] = 1,
        ["Plan.Item.FocusBlocks"] = 1,
        ["Plan.NotAdapted"] = 0,
        ["Plan.Adapted.Sleep"] = 0,
        ["Plan.Adapted.Recovery"] = 0,
        ["Plan.Adapted.Stress"] = 0,
        ["Plan.Adapted.Activity"] = 0,
        ["Plan.Adapted.Multiple"] = 0,
        ["Plan.Adapted.Generic"] = 0,

        // SampleIntelligenceProvider interpretation
        ["Insight.Reason.SleepHours"] = 2,
        ["Insight.Reason.RecoveryScore"] = 2,
        ["Insight.Reason.PositiveStreak"] = 1,
        ["Insight.Reason.Balanced"] = 0,
        ["Insight.Reason.HighStress"] = 0,

        // WeeklySummaryService
        ["Weekly.Up.Sleep"] = 0, ["Weekly.Down.Sleep"] = 0,
        ["Weekly.Up.Activity"] = 0, ["Weekly.Down.Activity"] = 0,
        ["Weekly.Up.Recovery"] = 0, ["Weekly.Down.Recovery"] = 0,
        ["Weekly.Up.Stress"] = 0, ["Weekly.Down.Stress"] = 0,
        ["Weekly.Focus.Stress"] = 0, ["Weekly.Focus.Sleep"] = 0,
        ["Weekly.Focus.Momentum"] = 1, ["Weekly.Focus.OneHabit"] = 0,

        // LocalizationService's own templates
        ["Format.DurationFullFa"] = 2,
        ["Format.DurationMinutesFa"] = 1,

        // SampleHealthProvider / privacy inventory / demo seed
        ["Profile.DataSource.Sample"] = 0,
        ["Privacy.Origin.Mock"] = 0,
        ["Privacy.Origin.Manual"] = 0,
        ["Seed.Goal.Exercise"] = 0, ["Seed.Goal.Sleep"] = 0, ["Seed.Goal.Reading"] = 0,
        ["Seed.Habit.MorningWalk"] = 0, ["Seed.Habit.Reading"] = 0, ["Seed.Habit.Stretching"] = 0,
    };

    /// <summary>Keys that label data as sample/mock — the disclosure surface of the product law.</summary>
    public static readonly string[] MockLabelKeys =
    {
        "Today.SampleDataNote",
        "Health.SourceMock",
        "Profile.DataSource.Sample",
        "Profile.DataSource.Sample.Status",
        "Profile.DataSource.RealComingSoon",
        "Profile.Status.MockActive",
        "Profile.Status.NotConnected",
        "Privacy.Origin.Mock",
        "Onboarding.Finish.Note",
    };

    /// <summary>The rules plus the condition key each one is allowed to speak through.</summary>
    public static readonly (string RuleKey, string ConditionKey)[] Rules =
    {
        ("Rule.SleepDebt", "Rule.Reason.SleepBelowBaseline"),
        ("Rule.SleepDebtReduceIntensity", "Rule.Reason.SleepBelowBaseline"),
        ("Rule.LowRecoveryReduce", "Rule.Reason.RecoveryBelowBaseline"),
        ("Rule.HighStress", "Rule.Reason.StressAboveUsual"),
        ("Rule.HighStressScreens", "Rule.Reason.StressAboveUsual"),
        ("Rule.ActivityDeficit", "Rule.Reason.StepsBelowBaseline"),
        ("Rule.PositiveMomentum", "Rule.Reason.AllNearBaseline"),
        ("Rule.HabitAtRisk", "Rule.Reason.HabitStreakAtRisk"),
        ("Rule.StaleSleep", "Rule.Reason.SleepDataStale"),
    };
}

/// <summary>
/// The product-law suite: every user-visible claim the pure layers make must (a) be a localization
/// key rather than prose, (b) resolve in both languages, and (c) never present sample data as
/// measured. This is the file a future lane breaks first when it shortcuts honesty.
/// </summary>
public class Wave3HonestyTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 10, 0, 0);

    // ---- keys the engines emit really exist ------------------------------

    [Fact]
    public void EveryCataloguedKey_ExistsInBothLanguages()
    {
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;

        var missing = Wave3EmissionCatalog.KeysWithArgCounts().Keys
            .Where(k => !pair.En.ContainsKey(k) || !pair.Fa.ContainsKey(k))
            .ToList();
        Assert.True(missing.Count == 0,
            $"engines emit keys with no translation ({missing.Count}): {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryCataloguedKey_ConsumesEveryArgumentTheEnginePasses()
    {
        // An engine that passes 2 args into a 1-placeholder template is harmless, but the reverse —
        // a template with {1} and an engine passing one arg — shows the user a literal "{1}".
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;

        var offenders = new List<string>();
        foreach (var (key, args) in Wave3EmissionCatalog.KeysWithArgCounts())
        {
            if (!pair.En.TryGetValue(key, out var en)) continue;
            foreach (var (label, text) in new[] { ("EN", en), ("FA", pair.Fa.TryGetValue(key, out var f) ? f : "") })
            {
                var ph = Wave3Harness.Placeholders(text);
                if (ph.Any(i => i >= args))
                    offenders.Add($"{label} {key}: template uses {string.Join(",", ph)} but the engine passes {args} arg(s)");
            }
        }
        Assert.True(offenders.Count == 0, string.Join(" | ", offenders));
    }

    [Fact]
    public void EveryRecommendationAction_HasATextKeyInBothLanguages()
    {
        // RecommendationService builds "Rec.{Action}" by interpolation, so a new
        // RecommendationActionKind can silently produce an untranslated "[Rec.NapBriefly]".
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;

        foreach (RecommendationActionKind action in Enum.GetValues<RecommendationActionKind>())
        {
            var key = $"Rec.{action}";
            Assert.True(pair.En.ContainsKey(key), $"missing {key} in EN");
            Assert.True(pair.Fa.ContainsKey(key), $"missing {key} in FA");
        }
    }

    [Fact]
    public void EveryRuleAndInsightTopic_IsExplainableInBothLanguages()
    {
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;

        foreach (var (ruleKey, conditionKey) in Wave3EmissionCatalog.Rules)
        {
            Assert.True(pair.En.ContainsKey(conditionKey), $"{conditionKey} missing (EN) for {ruleKey}");
            Assert.True(pair.Fa.ContainsKey(conditionKey), $"{conditionKey} missing (FA) for {ruleKey}");
            var why = "Rule.Why." + ruleKey;
            Assert.True(pair.En.ContainsKey(why), $"missing {why} (EN)");
            Assert.True(pair.Fa.ContainsKey(why), $"missing {why} (FA)");
        }
        foreach (InsightTopic topic in Enum.GetValues<InsightTopic>())
        {
            Assert.True(pair.En.ContainsKey($"Insight.Title.{topic}"), $"Insight.Title.{topic} missing (EN)");
            Assert.True(pair.Fa.ContainsKey($"Insight.Title.{topic}"), $"Insight.Title.{topic} missing (FA)");
        }
    }

    // ---- mock data is always labeled -------------------------------------

    [Fact]
    public void EveryMockDisclosureKey_ExistsInBothLanguages()
    {
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;

        foreach (var key in Wave3EmissionCatalog.MockLabelKeys)
        {
            Assert.True(pair.En.ContainsKey(key), $"honesty key {key} is missing from EN");
            Assert.True(pair.Fa.ContainsKey(key), $"honesty key {key} is missing from FA");
            Assert.False(string.IsNullOrWhiteSpace(pair.En[key]), $"{key} has no English text");
            Assert.False(string.IsNullOrWhiteSpace(pair.Fa[key]), $"{key} has no Persian text");
        }
    }

    [Fact]
    public void SampleDataDisclosure_NamesItselfInBothLanguages()
    {
        // If someone rewrites Today.SampleDataNote into something friendlier that no longer says
        // "sample", the sentence is still valid English and the lie still ships. Pin the word.
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;

        Assert.Contains("sample", pair.En["Today.SampleDataNote"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("نمونه", pair.Fa["Today.SampleDataNote"]);
        Assert.Contains("sample", pair.En["Health.SourceMock"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("نمونه", pair.Fa["Health.SourceMock"]);
        Assert.Contains("sample", pair.En["Profile.Status.MockActive"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("نمونه", pair.Fa["Profile.Status.MockActive"]);
    }

    [Fact]
    public void MockOriginLabel_NeverReadsLikeRealData()
    {
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;

        Assert.DoesNotContain("real", pair.En["Privacy.Origin.Mock"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("measured", pair.En["Privacy.Origin.Mock"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("entered", pair.En["Privacy.Origin.Manual"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderDay_IsLabeledMock_EveryFieldOfIt()
    {
        var day = await new SampleHealthProvider().GetNormalizedDayAsync(Now, new UserProfile { Id = "u" });
        Assert.NotNull(day);
        Assert.Equal(DataOrigin.Mock, day!.Origin);
        Assert.Equal(ConnectionState.Mock, day.Origin == DataOrigin.Mock ? ConnectionState.Mock : ConnectionState.Connected);

        var points = new[]
        {
            day.SleepMinutes, day.SleepQuality, day.SleepConsistency, day.BedtimeMinutesOfDay,
            day.WakeMinutesOfDay, day.Steps, day.ActiveMinutes, day.RecoveryScore, day.Stress,
            day.Mood, day.Energy,
        };
        Assert.All(points, p => Assert.Equal(DataOrigin.Mock, p.Origin));
        Assert.Equal(1.0, day.Completeness(), 6);   // a full mock day must claim a full mock day
    }

    [Fact]
    public void Completeness_OnlyCountsCompleteOrEstimated()
    {
        var complete = new DataPoint { Value = 1, Timestamp = Now, Quality = DataQuality.Complete };
        var estimated = new DataPoint { Value = 1, Timestamp = Now, Quality = DataQuality.Estimated };
        var missing = DataPoint.Missing(Now);
        var stale = new DataPoint { Value = 400, Timestamp = Now.AddDays(-9), Quality = DataQuality.Stale };
        var invalid = new DataPoint { Value = 400, Timestamp = Now, Quality = DataQuality.Invalid };

        var day = new NormalizedDay
        {
            Date = Now.Date, Origin = DataOrigin.Manual,
            SleepMinutes = missing, SleepQuality = stale, SleepConsistency = complete,
            BedtimeMinutesOfDay = invalid, WakeMinutesOfDay = complete, Steps = complete,
            ActiveMinutes = estimated, RecoveryScore = invalid, Stress = missing,
            Mood = complete, Energy = stale,
        };
        // Completeness() samples 8 fields: counted here are Steps, ActiveMinutes (Estimated) and
        // Mood. SleepConsistency/WakeMinutes/Bedtime are not part of the denominator at all —
        // which is why a day can be 100% "complete" with no bedtime. Pinned deliberately.
        Assert.Equal(3.0 / 8.0, day.Completeness(), 6);
    }

    [Fact]
    public void Completeness_AllMissing_IsExactlyZero()
    {
        var m = DataPoint.Missing(Now);
        var day = new NormalizedDay
        {
            Date = Now.Date, Origin = DataOrigin.Mock,
            SleepMinutes = m, SleepQuality = m, SleepConsistency = m, BedtimeMinutesOfDay = m,
            WakeMinutesOfDay = m, Steps = m, ActiveMinutes = m, RecoveryScore = m,
            Stress = m, Mood = m, Energy = m,
        };
        Assert.Equal(0, day.Completeness(), 6);
    }

    // ---- dead band + confidence gates ------------------------------------

    [Theory]
    [InlineData(0.119, StateLevel.Normal)]        // just inside the ±12% noise band
    [InlineData(0.12, StateLevel.Normal)]         // boundary itself is normal, not above
    [InlineData(0.121, StateLevel.AboveBaseline)]
    [InlineData(-0.119, StateLevel.Normal)]
    [InlineData(-0.121, StateLevel.BelowBaseline)]
    [InlineData(0.0, StateLevel.Normal)]
    public void MetricLevel_DeadBandIsTwelvePercent(double deviation, StateLevel expected)
    {
        var m = new MetricState
        {
            MetricKey = StateMetrics.SleepMinutes,
            Value = 450 * (1 + deviation),
            BaselineValue = 450,
            BaselineConfidence = BaselineConfidence.High,
            RelativeDeviation = deviation,
            Quality = DataQuality.Complete,
        };
        Assert.Equal(expected, m.Level);
    }

    [Theory]
    [InlineData(DataQuality.Missing)]
    [InlineData(DataQuality.Invalid)]
    public void MetricLevel_UnusableQuality_IsUnknown_EvenWithABigDeviation(DataQuality quality)
    {
        var m = new MetricState
        {
            MetricKey = "x", Value = 100, BaselineValue = 500,
            RelativeDeviation = -0.8, Quality = quality,
        };
        Assert.Equal(StateLevel.Unknown, m.Level);
    }

    [Fact]
    public void MetricLevel_StaleDataStillLevels_ButRulesRefuseToActOnIt()
    {
        // Documented split: Level only refuses Missing/Invalid; the action gate lives in RuleEngine
        // (which requires Quality == Complete). Both halves must hold or stale data drives advice.
        var m = new MetricState
        {
            MetricKey = "x", Value = 330, BaselineValue = 450,
            RelativeDeviation = -0.267, Quality = DataQuality.Stale,
        };
        Assert.Equal(StateLevel.BelowBaseline, m.Level);

        var fired = new RuleEngine().Evaluate(
            StateFactory.State(sleepMin: 330, sleepBase: 450, sleepQuality: DataQuality.Stale),
            new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.DoesNotContain(fired, r => r.RuleKey.StartsWith("Rule.SleepDebt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.58, false)]     // above the 0.55 floor and only 17% below the 0.7 baseline: silent
    [InlineData(0.549, true)]     // the absolute floor
    [InlineData(0.4, true)]       // deep enough to also cross the -18% relative gate
    public void LowRecovery_FiresOnTheDocumentedSideOfItsFloor(double recovery, bool expectFire)
    {
        var fired = new RuleEngine().Evaluate(StateFactory.State(recovery: recovery),
            new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.Equal(expectFire, fired.Any(r => r.RuleKey == "Rule.LowRecoveryReduce"));
    }

    [Theory]
    [InlineData(0.65, false)]     // HighStress fires on "> 0.65"
    [InlineData(0.651, true)]
    [InlineData(0.81, true)]
    public void HighStress_FiresOnTheDocumentedSideOfItsCeiling(double stress, bool expectFire)
    {
        var fired = new RuleEngine().Evaluate(StateFactory.State(stress: stress),
            new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        Assert.Equal(expectFire, fired.Any(r => r.RuleKey == "Rule.HighStress"));
    }

    [Theory]
    [InlineData(90, true)]        // exactly 1.5h below baseline fires
    [InlineData(89, false)]       // 89 minutes is under the deficit threshold
    public void SleepDebt_FiresOnTheDocumentedDeficitBoundary(int deficitMinutes, bool expectFire)
    {
        var state = StateFactory.State(sleepMin: 450 - deficitMinutes, sleepBase: 450);
        var fired = new RuleEngine().Evaluate(state, new UserProfile(), Array.Empty<Goal>(),
            Array.Empty<Habit>(), Now);
        Assert.Equal(expectFire, fired.Any(r => r.RuleKey == "Rule.SleepDebt"));
    }

    [Fact]
    public void SleepDebt_PriorityFlipsAtTwoAndAHalfHours()
    {
        var moderate = new RuleEngine().Evaluate(StateFactory.State(sleepMin: 330, sleepBase: 450),
            new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);
        var severe = new RuleEngine().Evaluate(StateFactory.State(sleepMin: 300, sleepBase: 450),
            new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), Now);

        var m = Assert.Single(moderate, r => r.RuleKey == "Rule.SleepDebt");
        var s = Assert.Single(severe, r => r.RuleKey == "Rule.SleepDebt");
        Assert.Equal(RecommendationPriority.Medium, m.Priority);
        Assert.Equal(RecommendationPriority.High, s.Priority);
    }

    [Theory]
    [InlineData(17, false)]       // HabitAtRisk is an 18:00 rule — before that it is still a day
    [InlineData(18, true)]
    public void HabitAtRisk_BoundaryIsExactlyEighteen(int hour, bool expectFire)
    {
        var habit = new Habit { Id = "h1", Name = "Walk" };
        for (int i = 1; i <= 3; i++) habit.Complete(Now.AddDays(-i));   // streak of 3 (>= 3)

        var fired = new RuleEngine().Evaluate(StateFactory.State(), new UserProfile(),
            Array.Empty<Goal>(), new[] { habit }, Now.Date.AddHours(hour));
        Assert.Equal(expectFire, fired.Any(r => r.RuleKey == "Rule.HabitAtRisk"));
    }

    [Fact]
    public void HabitAtRisk_StreakOfTwoIsNotAtRisk_CompletedTodayIsNeverAtRisk()
    {
        var shortStreak = new Habit { Id = "h1", Name = "Walk" };
        for (int i = 1; i <= 2; i++) shortStreak.Complete(Now.AddDays(-i));
        var done = new Habit { Id = "h2", Name = "Read" };
        for (int i = 0; i <= 4; i++) done.Complete(Now.AddDays(-i));    // includes today

        var fired = new RuleEngine().Evaluate(StateFactory.State(), new UserProfile(),
            Array.Empty<Goal>(), new[] { shortStreak, done }, Now.Date.AddHours(21));
        Assert.DoesNotContain(fired, r => r.RuleKey == "Rule.HabitAtRisk");
    }

    [Fact]
    public void ActivityDeficit_RequiresBothTheStepFloorAndUsableRecovery()
    {
        // steps < 55% of baseline AND recovery >= 0.55 — a tired person is not told to walk.
        var low = StateFactory.State(steps: 3000, stepsBase: 8000, recovery: 0.7);
        var fired = new RuleEngine().Evaluate(low, new UserProfile(), Array.Empty<Goal>(),
            Array.Empty<Habit>(), Now);
        Assert.Contains(fired, r => r.RuleKey == "Rule.ActivityDeficit");

        var tired = StateFactory.State(steps: 3000, stepsBase: 8000, recovery: 0.4);
        var tiredFired = new RuleEngine().Evaluate(tired, new UserProfile(), Array.Empty<Goal>(),
            Array.Empty<Habit>(), Now);
        Assert.DoesNotContain(tiredFired, r => r.RuleKey == "Rule.ActivityDeficit");
    }

    [Fact]
    public void Confidence_NeverEscapesZeroToOne()
    {
        var state = StateFactory.State(sleepMin: 330, sleepBase: 450, recovery: 0.2, stress: 0.99);
        var recs = new RecommendationService(new RuleEngine()).BuildRecommendations(
            state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), null, Now);
        Assert.All(recs, r => Assert.InRange(r.Confidence, 0, 1));

        var fired = new RuleEngine().Evaluate(state, new UserProfile(), Array.Empty<Goal>(),
            Array.Empty<Habit>(), Now);
        Assert.All(fired, r => Assert.InRange(r.Confidence, 0.0001, 1));
    }

    [Fact]
    public async Task IntelligenceInterpretation_ReportsMockOrigin_AndNeverFabricatesOverrides()
    {
        var provider = new Infrastructure.IntelligenceProviders.SampleIntelligenceProvider(new RuleEngine());
        Assert.Equal(DataOrigin.Mock, provider.Origin);

        var interp = await provider.InterpretAsync(
            StateFactory.State(sleepMin: 330, sleepBase: 450), Array.Empty<Recommendation>(), new UserProfile());

        Assert.StartsWith("Insight.Title.", interp.HeadlineKey);
        Assert.StartsWith("Insight.Reason.", interp.BodyKey);
        Assert.InRange(interp.Confidence, 0, 1);
        Assert.Empty(interp.SuggestedRecommendationOverrides);  // the mock may not invent actions
    }

    [Fact]
    public async Task DailyInsight_SourceIsMock_AndCarriesOnlyKeys()
    {
        var svc = new IntelligenceOrchestrator(
            new RuleEngine(),
            new RecommendationService(new RuleEngine()),
            new Infrastructure.IntelligenceProviders.SampleIntelligenceProvider(new RuleEngine()),
            new DailyPlanService(new RuleEngine(), new InMemoryRepo<Bootcamp>()));

        var state = StateFactory.State(sleepMin: 380, sleepBase: 450);
        var insight = await svc.GenerateDailyInsightAsync(state, new UserProfile(),
            new DailyPlan { Date = Now.Date, Items = Array.Empty<PlanItem>() },
            Array.Empty<Goal>(), Array.Empty<Habit>());

        Assert.Equal(SourceType.Mock, insight.Source);
        Assert.StartsWith("Insight.", insight.TitleKey);
        Assert.StartsWith("Insight.", insight.SummaryKey);
        Assert.All(insight.RelatedMetricKeys, k => Assert.StartsWith("Health.", k));
    }

    [Fact]
    public void RelatedMetricLabels_NeverInventAChip()
    {
        // An unmapped metric must yield "" (filtered out upstream) rather than a made-up label.
        Assert.Equal("Health.Recovery", InsightMetricLabelsProbe.For(StateMetrics.RestingHeartRate));
        Assert.Equal("", InsightMetricLabelsProbe.For(StateMetrics.FocusEstimate));
        Assert.Equal("", InsightMetricLabelsProbe.For("nonsense.key"));
    }

    [Fact]
    public void PlanItemKeys_AreAllKnownResources()
    {
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;

        var svc = new DailyPlanService(new RuleEngine(), new InMemoryRepo<Bootcamp>(new[]
        {
            new Bootcamp
            {
                Id = "c1", IsEnrolled = true, CurrentDay = 1, DurationDays = 3,
                Days = new List<BootcampDay>
                {
                    new()
                    {
                        DayNumber = 1, PlanTitleKey = "Bootcamp.Plan.Workout",
                        PlanDescriptionKey = "Bootcamp.Plan.Workout.Desc", TargetMinutes = 30,
                    },
                },
            },
        }));
        var plan = svc.BuildPlanAsync(
            StateFactory.State(sleepMin: 330, sleepBase: 450, recovery: 0.3, stress: 0.9),
            new UserProfile(), Array.Empty<Goal>(), new[] { new Habit { Id = "h1", Name = "Walk" } }, Now)
            .GetAwaiter().GetResult();

        Assert.NotEmpty(plan.Items);
        foreach (var item in plan.Items)
        {
            Assert.True(pair.En.ContainsKey(item.TitleKey), $"plan item emits untranslated key {item.TitleKey}");
            Assert.True(pair.Fa.ContainsKey(item.TitleKey), $"plan item {item.TitleKey} has no FA value");
            if (item.DetailKey is { Length: > 0 } detail)
                Assert.True(pair.En.ContainsKey(detail), $"plan detail emits untranslated key {detail}");
        }
    }

    /// <summary>Reflective bridge to the internal label mapper (internal on purpose).</summary>
    private static class InsightMetricLabelsProbe
    {
        private static readonly System.Reflection.MethodInfo? Map = typeof(IntelligenceOrchestrator)
            .Assembly.GetType("LIVORA.Application.Insights.InsightMetricLabels")?
            .GetMethod("For", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        public static string For(string metricKey)
        {
            Assert.NotNull(Map);   // if the mapper is renamed/deleted, that is a contract break
            return (string)Map!.Invoke(null, new object[] { metricKey })!;
        }
    }
}
