using LIVORA.Application.Abstractions;
using LIVORA.Domain.Constants;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Infrastructure.Persistence;
/// <summary>
/// Seeds first-launch demo content (goals, habits, one enrolled bootcamp + catalog).
/// All names are USER-FACING DATA, seeded in the active language at first launch.
/// Seed only happens when the store is empty.
/// </summary>
public sealed class DemoDataSeeder
{
    private readonly IRepository<Goal> _goals;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Bootcamp> _bootcamps;
    private readonly ISettingsService _settings;
    private readonly Func<string, string> _localizer; // l(key)

    public DemoDataSeeder(
        IRepository<Goal> goals,
        IRepository<Habit> habits,
        IRepository<Bootcamp> bootcamps,
        ISettingsService settings,
        Func<string, string> localizer)
    {
        _goals = goals;
        _habits = habits;
        _bootcamps = bootcamps;
        _settings = settings;
        _localizer = localizer;
    }

    public async Task SeedIfEmptyAsync(DateTime today)
    {
        // Gate on the one-shot marker, NOT on emptiness: a user who deletes their own goals has an
        // legitimately empty store, and an emptiness gate re-created the entire sample set behind
        // their back on the next launch (goals + habits + bootcamps, each with fresh Guid ids, so the
        // upsert-by-id repository could not collapse the duplicates — 12 bootcamps over 6 TitleKeys).
        if (_settings.DemoDataSeeded) return;
        if (!await _goals.IsEmptyAsync()) { _settings.DemoDataSeeded = true; return; }

        var l = _localizer;
        var goals = new List<Goal>
        {
            new()
            {
                Name = l("Seed.Goal.Exercise"), NameKey = "Seed.Goal.Exercise",
                Category = GoalCategory.Fitness,
                TargetValue = 4, ProgressValue = 2, Period = GoalPeriod.Week, Unit = GoalUnit.Sessions,
            },
            new()
            {
                Name = l("Seed.Goal.Sleep"), NameKey = "Seed.Goal.Sleep",
                Category = GoalCategory.Sleep,
                TargetValue = 5, ProgressValue = 3, Period = GoalPeriod.Week, Unit = GoalUnit.Sessions,
            },
            new()
            {
                Name = l("Seed.Goal.Reading"), NameKey = "Seed.Goal.Reading",
                Category = GoalCategory.Learning,
                TargetValue = 7, ProgressValue = 4, Period = GoalPeriod.Week, Unit = GoalUnit.Sessions,
            },
        };
        foreach (var g in goals) await _goals.SaveAsync(g);

        var habits = new List<Habit>
        {
            new()
            {
                Name = l("Seed.Habit.MorningWalk"), NameKey = "Seed.Habit.MorningWalk",
                Frequency = HabitFrequencyKind.Daily,
                Completions = Enumerable.Range(0, 5).Select(i => today.AddDays(-i)).ToList(),
            },
            new()
            {
                Name = l("Seed.Habit.Reading"), NameKey = "Seed.Habit.Reading",
                Frequency = HabitFrequencyKind.Daily,
                Completions = Enumerable.Range(0, 3).Select(i => today.AddDays(-i)).ToList(),
            },
            new()
            {
                Name = l("Seed.Habit.Stretching"), NameKey = "Seed.Habit.Stretching",
                Frequency = HabitFrequencyKind.TimesPerWeek, TimesPerWeek = 4,
                Completions = new List<DateTime> { today.AddDays(-1), today.AddDays(-3) },
            },
        };
        foreach (var h in habits) await _habits.SaveAsync(h);

        // Idempotent by TitleKey: every Bootcamp gets a fresh Guid here, so an upsert-by-id store
        // cannot collapse a second seeding pass (a pre-Wave-3 install that already holds the
        // catalog would otherwise end up with 12 bootcamps over 6 TitleKeys).
        var existingTitles = (await _bootcamps.GetAllAsync()).Select(b => b.TitleKey).ToHashSet();
        foreach (var b in CreateBootcampCatalog(l))
            if (existingTitles.Add(b.TitleKey)) await _bootcamps.SaveAsync(b);

        _settings.DemoDataSeeded = true;
    }

    public static List<Bootcamp> CreateBootcampCatalog(Func<string, string> l)
    {
        var catalog = new List<Bootcamp>
        {
            Make(l, "Bootcamp.Title.EarlyRising", "Bootcamp.Desc.EarlyRising", BootcampCategory.Sleep, 21, BootcampDifficulty.Intermediate),
            Make(l, "Bootcamp.Title.BetterSleep", "Bootcamp.Desc.BetterSleep", BootcampCategory.Sleep, 14, BootcampDifficulty.Beginner),
            Make(l, "Bootcamp.Title.FitnessStart", "Bootcamp.Desc.FitnessStart", BootcampCategory.Fitness, 30, BootcampDifficulty.Beginner),
            Make(l, "Bootcamp.Title.DigitalDetox", "Bootcamp.Desc.DigitalDetox", BootcampCategory.Focus, 7, BootcampDifficulty.Beginner),
            Make(l, "Bootcamp.Title.Reading", "Bootcamp.Desc.Reading", BootcampCategory.Learning, 30, BootcampDifficulty.Beginner),
            Make(l, "Bootcamp.Title.Focus", "Bootcamp.Desc.Focus", BootcampCategory.Focus, 21, BootcampDifficulty.Intermediate),
        };

        // Enroll the first one at day 8 with a simple plan so adaptation is visible immediately.
        var early = catalog[0];
        early.IsEnrolled = true;
        early.CurrentDay = 8;
        early.Days = Enumerable.Range(1, 21).Select(i => new BootcampDay
        {
            DayNumber = i,
            PlanTitleKey = i % 3 == 0 ? "Bootcamp.Plan.Workout" : i % 3 == 1 ? "Bootcamp.Plan.Walk" : "Bootcamp.Plan.WindDown",
            PlanDescriptionKey = i % 3 == 0 ? "Bootcamp.Plan.Workout.Desc" : i % 3 == 1 ? "Bootcamp.Plan.Walk.Desc" : "Bootcamp.Plan.WindDown.Desc",
            TargetMinutes = i % 3 == 0 ? 30 : 20,
            IsCompleted = i < 8,
        }).ToList();

        return catalog;
    }

    private static Bootcamp Make(Func<string, string> l, string titleKey, string descKey, BootcampCategory cat, int days, BootcampDifficulty diff) => new()
    {
        TitleKey = titleKey,
        DescriptionKey = descKey,
        Category = cat,
        DurationDays = days,
        Difficulty = diff,
        CreatorName = "LIVORA",
    };
}
