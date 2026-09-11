using LIVORA.Core.Constants;
using LIVORA.Core.Enums;
using LIVORA.Core.Models;

namespace LIVORA.Services.Persistence;

/// <summary>
/// Seeds first-launch demo content (goals, habits, one enrolled bootcamp + catalog).
/// All names are USER-FACING DATA, seeded in the active language at first launch.
/// Seed only happens when the store is empty.
/// </summary>
public sealed class SeedDataService
{
    private readonly IRepository<Goal> _goals;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Bootcamp> _bootcamps;
    private readonly Func<string> _localizer; // l(key)

    public SeedDataService(
        IRepository<Goal> goals,
        IRepository<Habit> habits,
        IRepository<Bootcamp> bootcamps,
        Func<string> localizer)
    {
        _goals = goals;
        _habits = habits;
        _bootcamps = bootcamps;
        _localizer = localizer;
    }

    public async Task SeedIfEmptyAsync(DateTime today)
    {
        if (!await _goals.IsEmptyAsync()) return;

        var l = _localizer;
        var goals = new List<Goal>
        {
            new()
            {
                Name = l("Seed.Goal.Exercise"), Category = GoalCategory.Fitness,
                TargetValue = 4, ProgressValue = 2, Period = GoalPeriod.Week, Unit = GoalUnit.Sessions,
            },
            new()
            {
                Name = l("Seed.Goal.Sleep"), Category = GoalCategory.Sleep,
                TargetValue = 5, ProgressValue = 3, Period = GoalPeriod.Week, Unit = GoalUnit.Sessions,
            },
            new()
            {
                Name = l("Seed.Goal.Reading"), Category = GoalCategory.Learning,
                TargetValue = 7, ProgressValue = 4, Period = GoalPeriod.Week, Unit = GoalUnit.Sessions,
            },
        };
        foreach (var g in goals) await _goals.SaveAsync(g);

        var habits = new List<Habit>
        {
            new()
            {
                Name = l("Seed.Habit.MorningWalk"), Frequency = HabitFrequencyKind.Daily,
                Completions = Enumerable.Range(0, 5).Select(i => today.AddDays(-i)).ToList(),
            },
            new()
            {
                Name = l("Seed.Habit.Reading"), Frequency = HabitFrequencyKind.Daily,
                Completions = Enumerable.Range(0, 3).Select(i => today.AddDays(-i)).ToList(),
            },
            new()
            {
                Name = l("Seed.Habit.Stretching"), Frequency = HabitFrequencyKind.TimesPerWeek, TimesPerWeek = 4,
                Completions = new List<DateTime> { today.AddDays(-1), today.AddDays(-3) },
            },
        };
        foreach (var h in habits) await _habits.SaveAsync(h);

        foreach (var b in CreateBootcampCatalog(l))
            await _bootcamps.SaveAsync(b);
    }

    public static List<Bootcamp> CreateBootcampCatalog(Func<string> l)
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

    private static Bootcamp Make(Func<string> l, string titleKey, string descKey, BootcampCategory cat, int days, BootcampDifficulty diff) => new()
    {
        TitleKey = titleKey,
        DescriptionKey = descKey,
        Category = cat,
        DurationDays = days,
        Difficulty = diff,
        CreatorName = "LIVORA",
    };
}
