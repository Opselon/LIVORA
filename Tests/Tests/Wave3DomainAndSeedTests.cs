using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Goal/Habit arithmetic — specifically the parts a Persian-speaking client notices first: the week
/// starts on Saturday, streaks count from the latest completion, and double-taps are idempotent.
/// </summary>
public class Wave3DomainMathTests
{
    private static DateTime Sat(int year, int month, int day) => new(year, month, day);

    // ---- Saturday week start ---------------------------------------------

    [Theory]
    // 2026-09-12 is a Saturday: it must be the FIRST day of its own week.
    [InlineData(2026, 9, 12, DayOfWeek.Saturday)]
    [InlineData(2026, 9, 13, DayOfWeek.Sunday)]
    [InlineData(2026, 9, 18, DayOfWeek.Friday)]
    public void StartOfWeek_SaturdayIsTheAnchor(int y, int m, int d, DayOfWeek dow)
    {
        var date = Sat(y, m, d);
        Assert.Equal(dow, date.DayOfWeek);
        var start = Habit.StartOfWeek(date);
        Assert.Equal(DayOfWeek.Saturday, start.DayOfWeek);
        Assert.True(start <= date && date < start.AddDays(7));
    }

    [Fact]
    public void StartOfWeek_FridayBelongsToTheWeekStartedTheDayBefore()
    {
        // 2026-09-11 is a Friday; the Saturday-to-Friday week started 2026-09-05.
        Assert.Equal(new DateTime(2026, 9, 5), Habit.StartOfWeek(new DateTime(2026, 9, 11)));
    }

    [Fact]
    public void StartOfWeek_IsTimeOfDayInsensitive()
    {
        var morning = new DateTime(2026, 9, 12, 0, 5, 0);
        var late = new DateTime(2026, 9, 12, 23, 55, 0);
        Assert.Equal(Habit.StartOfWeek(morning), Habit.StartOfWeek(late));
    }

    [Fact]
    public void CompletionsThisWeek_RespectsTheSaturdayBoundary()
    {
        var h = new Habit();
        h.Complete(new DateTime(2026, 9, 5));   // Saturday -> inside
        h.Complete(new DateTime(2026, 9, 11));  // Friday   -> inside (same Sat-week)
        h.Complete(new DateTime(2026, 9, 4));   // Friday of the PREVIOUS week -> outside
        h.Complete(new DateTime(2026, 9, 12));  // next Saturday -> outside
        Assert.Equal(2, h.CompletionsThisWeek(new DateTime(2026, 9, 8)));
    }

    [Fact]
    public void Streak_SurvivesADayWithTimeButNotADayWithout()
    {
        var h = new Habit();
        h.Complete(new DateTime(2026, 9, 1, 23, 59, 0));
        h.Complete(new DateTime(2026, 9, 2, 0, 1, 0));
        Assert.Equal(2, h.CurrentStreak);   // same calendar-day semantics, not 24h windows
    }

    [Fact]
    public void Streak_ZeroWhenNothingCompleted()
        => Assert.Equal(0, new Habit().CurrentStreak);

    [Fact]
    public void Streak_CompletionsAddedOutOfOrderStillCount()
    {
        var h = new Habit();
        h.Complete(new DateTime(2026, 9, 3));
        h.Complete(new DateTime(2026, 9, 1));
        h.Complete(new DateTime(2026, 9, 2));
        Assert.Equal(3, h.CurrentStreak);
    }

    [Fact]
    public void Uncomplete_BreaksTheStreak()
    {
        var h = new Habit();
        for (int i = 0; i < 4; i++) h.Complete(new DateTime(2026, 9, 1).AddDays(i));
        h.Uncomplete(new DateTime(2026, 9, 3));
        Assert.Equal(1, h.CurrentStreak);   // only 09-04 stands alone now
    }

    [Fact]
    public void IsCompletedOn_IgnoresTimeOfDay()
    {
        var h = new Habit();
        h.Complete(new DateTime(2026, 9, 3, 7, 0, 0));
        Assert.True(h.IsCompletedOn(new DateTime(2026, 9, 3, 22, 0, 0)));
        Assert.False(h.IsCompletedOn(new DateTime(2026, 9, 2)));
    }

    // ---- Goal arithmetic --------------------------------------------------

    [Theory]
    [InlineData(10, 6, GoalStatus.OnTrack)]      // 60% exactly => OnTrack
    [InlineData(10, 5, GoalStatus.AtRisk)]       // 50%
    [InlineData(10, 3, GoalStatus.AtRisk)]       // 30% exactly => AtRisk, not Behind
    [InlineData(10, 2, GoalStatus.Behind)]       // 20%
    [InlineData(10, 10, GoalStatus.Completed)]
    [InlineData(10, 12, GoalStatus.Completed)]
    [InlineData(0, 5, GoalStatus.Behind)]        // zero target => Fraction 0, never NaN/Infinity
    public void GoalStatus_BoundariesAreInclusive(double target, double progress, GoalStatus expected)
        => Assert.Equal(expected, new Goal { TargetValue = target, ProgressValue = progress }.Status);

    [Fact]
    public void GoalFraction_NeverGoesNegativeOrAboveOne()
    {
        Assert.Equal(0, new Goal { TargetValue = 10, ProgressValue = -50 }.Fraction);
        Assert.Equal(1, new Goal { TargetValue = 10, ProgressValue = 1000 }.Fraction);
        Assert.Equal(0, new Goal { TargetValue = -1, ProgressValue = 5 }.Fraction);
    }

    [Fact]
    public void Goal_DefaultMeasurementIsManualCounter_NotASilentMetricLink()
    {
        var g = new Goal();
        Assert.Equal(GoalMeasurement.ManualCounter, g.Measurement);
        Assert.Null(g.MetricKey);
        Assert.False(g.IsArchived);
        Assert.Equal(GoalPeriod.Week, g.Period);
        Assert.Equal(GoalUnit.Sessions, g.Unit);
        Assert.Equal(1, g.FrequencyPerPeriod);
    }

    [Fact]
    public void Habit_DefaultsAreDailySevenTimes_PerHabitFrequencyContract()
    {
        var h = new Habit();
        Assert.Equal(HabitFrequencyKind.Daily, h.Frequency);
        Assert.Equal(7, h.TimesPerWeek);
        Assert.Empty(h.Completions);
        Assert.Empty(h.CompletionLog);
    }

    [Fact]
    public void GoalAndHabit_IdAreUniquePerInstance()
    {
        Assert.NotEqual(new Goal().Id, new Goal().Id);
        Assert.NotEqual(new Habit().Id, new Habit().Id);
        Assert.DoesNotContain('-', new Goal().Id);   // "N" format: stable for file keys
    }

    // ---- Bootcamp ---------------------------------------------------------

    [Fact]
    public void Bootcamp_TodayIsNullBeforeDayOneAndAfterTheLastDay()
    {
        var b = new Bootcamp
        {
            DurationDays = 3,
            Days = Enumerable.Range(1, 3).Select(i => new BootcampDay { DayNumber = i }).ToList(),
        };
        b.CurrentDay = 0;
        Assert.Null(b.Today);
        b.CurrentDay = 4;
        Assert.Null(b.Today);
        b.CurrentDay = 3;
        Assert.Equal(3, b.Today!.DayNumber);
    }

    [Theory]
    [InlineData(0, 21, 0)]
    [InlineData(1, 21, 0.047619047619)]
    [InlineData(21, 21, 1)]
    [InlineData(30, 21, 1)]      // clamped: a corrupt CurrentDay never renders 143%
    [InlineData(-3, 21, 0)]      // ...and a corrupt negative never renders -14%
    [InlineData(5, 0, 0)]        // zero-length program => 0, not DivideByZero
    public void Bootcamp_CompletionFraction_IsAlwaysZeroToOne(int current, int duration, double expected)
        => Assert.Equal(expected, new Bootcamp { CurrentDay = current, DurationDays = duration }.CompletionFraction, 6);

    [Fact]
    public void Bootcamp_DefaultAdaptationRuleKeys_NameRealRules()
    {
        var pair = Wave3Harness.ReadBoth();
        var b = new Bootcamp();
        Assert.NotEmpty(b.AdaptationRuleKeys);
        Assert.All(b.AdaptationRuleKeys, k => Assert.StartsWith("Rule.", k));
        Assert.Equal("LIVORA", b.CreatorName);   // the honest creator: no fake coach persona
    }
}

/// <summary>
/// DemoDataSeeder must seed once, seed in the ACTIVE language, and never re-seed over data the
/// user has (Wave 3 makes goals/habits editable — a second seed would silently resurrect deleted
/// sample content).
/// </summary>
public class DemoDataSeederWave3Tests
{
    private static readonly DateTime Today = new(2026, 9, 11);

    private static string L(string key) => key;   // identity localizer: the keys are the assertions

    /// <summary>In-memory stand-in for the one-shot seeding flag (Wave 3 preference).</summary>
    private sealed class SeedingSettings : LIVORA.Application.Abstractions.ISettingsService
    {
        public LIVORA.Domain.Enums.AppLanguage PreferredLanguage { get; set; }
        public bool LanguageExplicitlySet { get; set; }
        public bool OnboardingCompleted { get; set; }
        public string? ProfileId { get; set; }
        public LIVORA.Domain.Enums.ThemeMode ThemeMode { get; set; }
        public string? LastSeenVersion { get; set; }
        public bool DemoDataSeeded { get; set; }
    }

    private static (DemoDataSeeder Seeder, InMemoryRepo<Goal> Goals, InMemoryRepo<Habit> Habits,
                    InMemoryRepo<Bootcamp> Camps, SeedingSettings Settings) Build(SeedingSettings? settings = null)
    {
        var goals = new InMemoryRepo<Goal>();
        var habits = new InMemoryRepo<Habit>();
        var camps = new InMemoryRepo<Bootcamp>();
        var s = settings ?? new SeedingSettings();
        return (new DemoDataSeeder(goals, habits, camps, s, L), goals, habits, camps, s);
    }

    [Fact]
    public async Task Seeds_ThreeGoals_ThreeHabits_SixPrograms()
    {
        var (seeder, goals, habits, camps, _) = Build();
        await seeder.SeedIfEmptyAsync(Today);

        Assert.Equal(3, (await goals.GetAllAsync()).Count);
        Assert.Equal(3, (await habits.GetAllAsync()).Count);
        Assert.Equal(6, (await camps.GetAllAsync()).Count);
    }

    [Fact]
    public async Task SecondRun_IsANoOp_WhenTheStoreIsNotEmpty()
    {
        var (seeder, goals, habits, camps, _) = Build();
        await seeder.SeedIfEmptyAsync(Today);
        var goalIds = (await goals.GetAllAsync()).Select(g => g.Id).ToList();
        var campCount = (await camps.GetAllAsync()).Count;

        await seeder.SeedIfEmptyAsync(Today.AddDays(1));   // next launch
        Assert.Equal(goalIds, (await goals.GetAllAsync()).Select(g => g.Id).ToList());
        Assert.Equal(campCount, (await camps.GetAllAsync()).Count);
    }

    [Fact]
    public async Task DeletingHabitsOrProgramsAlone_NeverResurrectsThem()
    {
        // The gate is exactly "goals empty". Deleting the demo habits (which Wave 3 lets users do)
        // must not bring them back on the next launch.
        var (seeder, goals, habits, camps, _) = Build();
        await seeder.SeedIfEmptyAsync(Today);
        foreach (var h in await habits.GetAllAsync()) await habits.DeleteAsync(h.Id);

        await seeder.SeedIfEmptyAsync(Today.AddDays(1));
        Assert.Empty(await habits.GetAllAsync());                 // user intent respected
        Assert.Equal(3, (await goals.GetAllAsync()).Count);       // no double-seed
    }

    [Fact]
    public async Task EmptiedGoalStore_DoesNotReseed_OnceSeededFlagIsSet()
    {
        // Wave 3 fix (previously pinned as a hazard): seeding is gated on an explicit one-shot
        // preference, not on "the goal store is empty". A user who deletes their own goals must not
        // get the whole demo set re-created behind their back on next launch — and because every
        // Bootcamp gets a fresh Guid, an emptiness gate produced 12 bootcamps over 6 TitleKeys.
        var (seeder, goals, habits, camps, settings) = Build();
        await seeder.SeedIfEmptyAsync(Today);
        Assert.True(settings.DemoDataSeeded);
        foreach (var g in await goals.GetAllAsync()) await goals.DeleteAsync(g.Id);

        // Same stores, new seeder instance, same settings object (what a relaunch does).
        var again = new DemoDataSeeder(goals, habits, camps, settings, L);
        await again.SeedIfEmptyAsync(Today);
        Assert.Empty(await goals.GetAllAsync());
        Assert.Equal(3, (await habits.GetAllAsync()).Count);      // not duplicated
        Assert.Equal(6, (await camps.GetAllAsync()).Count);       // not duplicated
    }

    [Fact]
    public async Task CatalogSeeding_IsIdempotentByTitleKey_ForInstallsWithoutTheFlag()
    {
        // Second guard for installs that already hold the catalog but predate the flag: an empty
        // goal store with an existing bootcamp catalog must not double the programs.
        var goals = new InMemoryRepo<Goal>();
        var habits = new InMemoryRepo<Habit>();
        var camps = new InMemoryRepo<Bootcamp>();
        var s = new SeedingSettings();
        await new DemoDataSeeder(goals, habits, camps, s, L).SeedIfEmptyAsync(Today);
        await goals.DeleteAsync((await goals.GetAllAsync()).First().Id);
        s.DemoDataSeeded = false;                                  // simulate the pre-fix flag state
        await new DemoDataSeeder(goals, habits, camps, s, L).SeedIfEmptyAsync(Today);
        Assert.Equal(6, (await camps.GetAllAsync()).Count);
        Assert.Equal(3, (await habits.GetAllAsync()).Count);
    }

    [Fact]
    public async Task SeededNames_ComeFromTheInjectedLocalizer_InActiveLanguage()
    {
        var localizerCalled = new List<string>();
        var settings = new SeedingSettings();
        var seeder = new DemoDataSeeder(new InMemoryRepo<Goal>(), new InMemoryRepo<Habit>(),
            new InMemoryRepo<Bootcamp>(), settings, k => { localizerCalled.Add(k); return "XX:" + k; });
        var goals = new InMemoryRepo<Goal>();
        seeder = new DemoDataSeeder(goals, new InMemoryRepo<Habit>(), new InMemoryRepo<Bootcamp>(),
            settings, k => { localizerCalled.Add(k); return "XX:" + k; });

        await seeder.SeedIfEmptyAsync(Today);
        var seeded = await goals.GetAllAsync();
        Assert.All(seeded, g => Assert.StartsWith("XX:", g.Name));
        Assert.Contains("Seed.Goal.Exercise", localizerCalled);
    }

    [Fact]
    public async Task SeededHabits_HaveCompletionsGroundedInToday()
    {
        var (seeder, _, habits, _, _) = Build();
        await seeder.SeedIfEmptyAsync(Today);
        var walk = (await habits.GetAllAsync()).First(h => h.Name == "Seed.Habit.MorningWalk");

        Assert.Equal(5, walk.CurrentStreak);
        Assert.True(walk.IsCompletedOn(Today));
        Assert.All(walk.Completions, d => Assert.True(d.Date <= Today && d.Date >= Today.AddDays(-5)));
    }

    [Fact]
    public void Catalog_IsDeterministic_AndEveryTitleDescKeyTranslates()
    {
        var first = DemoDataSeeder.CreateBootcampCatalog(L);
        var second = DemoDataSeeder.CreateBootcampCatalog(L);
        Assert.Equal(first.Select(c => c.TitleKey), second.Select(c => c.TitleKey));
        Assert.Equal(first.Select(c => c.DurationDays), second.Select(c => c.DurationDays));

        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;
        foreach (var camp in first)
        {
            Assert.True(pair.En.ContainsKey(camp.TitleKey), $"catalog title {camp.TitleKey} untranslated (EN)");
            Assert.True(pair.Fa.ContainsKey(camp.TitleKey), $"catalog title {camp.TitleKey} untranslated (FA)");
            Assert.True(pair.En.ContainsKey(camp.DescriptionKey));
            Assert.Equal(camp.DurationDays, camp.Days.Count == 0 ? camp.DurationDays : camp.Days.Count);
        }
    }

    [Fact]
    public void EnrolledCatalogProgram_HasADayPerDurationDay_AndAnAdaptableWorkout()
    {
        var camp = DemoDataSeeder.CreateBootcampCatalog(L).First(c => c.IsEnrolled);
        Assert.Equal(21, camp.Days.Count);
        Assert.Equal(8, camp.CurrentDay);
        Assert.NotNull(camp.Today);
        Assert.Equal(7, camp.Days.Count(d => d.IsCompleted));   // days 1..7 done, day 8 is "today"
        Assert.Contains(camp.Days, d => d.PlanTitleKey == "Bootcamp.Plan.Workout");
        Assert.NotEmpty(camp.AdaptationRuleKeys);
    }

    [Fact]
    public async Task SeededBootcamps_AreNotEnrolledExceptTheFlagship()
    {
        var (seeder, _, _, camps, _) = Build();
        await seeder.SeedIfEmptyAsync(Today);
        var all = await camps.GetAllAsync();
        Assert.Single(all, c => c.IsEnrolled);
        foreach (var c in all.Where(c => !c.IsEnrolled))
            Assert.False(c.Days.Any(d => d.IsCompleted),
                "a program the user never joined may not show completed days");
    }
}

/// <summary>
/// DailyHistoryRecord / WeeklySummary shapes the persistence + review screens depend on.
/// Kept separate from the service tests because these are data-contract guarantees.
/// </summary>
public class HistoryRecordShapeTests
{
    [Fact]
    public void DailyHistoryRecord_DefaultsAreEmptyNotSentinelValues()
    {
        var r = new DailyHistoryRecord { Date = new DateTime(2026, 9, 11), Origin = nameof(DataOrigin.Mock) };
        Assert.Empty(r.CompletedHabitIds);
        Assert.Empty(r.AdvancedGoalIds);
        Assert.Null(r.InsightTopic);
        Assert.Null(r.InsightPriority);
        Assert.Null(r.BootcampId);
        Assert.False(r.BootcampDayWasAdapted);
        Assert.Equal(0, r.Steps);   // numeric default; honesty lives in Completeness
    }

    [Fact]
    public void WeeklySummary_FocusDefaultsToSteady_AndKeysAreEmptyLists()
    {
        var s = new WeeklySummary
        {
            WeekStart = new DateTime(2026, 9, 4),
            WeekEnd = new DateTime(2026, 9, 10),
            SleepTrend = TrendDirection.InsufficientData,
            ActivityTrend = TrendDirection.InsufficientData,
            RecoveryTrend = TrendDirection.InsufficientData,
            StressTrend = TrendDirection.InsufficientData,
        };
        Assert.Equal("WeeklySummary.Focus.Steady", s.FocusKey);
        Assert.Empty(s.ImprovementKeys);
        Assert.Empty(s.DeclineKeys);
        Assert.Empty(s.FocusArgs);
        Assert.Equal(BaselineConfidence.None, s.Confidence);
    }

    [Fact]
    public void WeeklySummary_DefaultFocusKey_ExistsInBothLanguages()
    {
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;
        Assert.True(pair.En.ContainsKey("WeeklySummary.Focus.Steady"),
            "the default focus key of a summary must translate, or the review card shows [WeeklySummary...]");
        Assert.True(pair.Fa.ContainsKey("WeeklySummary.Focus.Steady"));
    }
}
