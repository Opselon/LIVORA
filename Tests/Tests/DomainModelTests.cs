using LIVORA.Domain.Models;
using LIVORA.Domain.Enums;

namespace LIVORA.Tests.Tests;

public class GoalProgressTests
{
    [Fact]
    public void Fraction_ProportionalProgress()
    {
        var g = new Goal { TargetValue = 4, ProgressValue = 2 };
        Assert.Equal(0.5, g.Fraction, 3);
    }

    [Fact]
    public void Fraction_ClampsAboveTarget()
    {
        var g = new Goal { TargetValue = 4, ProgressValue = 9 };
        Assert.Equal(1.0, g.Fraction, 3);
    }

    [Fact]
    public void Fraction_ZeroTarget_NoDivideByZero()
    {
        var g = new Goal { TargetValue = 0, ProgressValue = 3 };
        Assert.Equal(0.0, g.Fraction, 3);
    }

    [Fact]
    public void Status_Completed_AtTarget()
        => Assert.Equal(GoalStatus.Completed, new Goal { TargetValue = 4, ProgressValue = 4 }.Status);

    [Fact]
    public void Status_OnTrack_At60Percent()
        => Assert.Equal(GoalStatus.OnTrack, new Goal { TargetValue = 5, ProgressValue = 3 }.Status);

    [Fact]
    public void Status_AtRisk_At40Percent()
        => Assert.Equal(GoalStatus.AtRisk, new Goal { TargetValue = 5, ProgressValue = 2 }.Status);

    [Fact]
    public void Status_Behind_At10Percent()
        => Assert.Equal(GoalStatus.Behind, new Goal { TargetValue = 10, ProgressValue = 1 }.Status);
}

public class HabitStreakTests
{
    [Fact]
    public void Streak_ConsecutiveDays_Counted()
    {
        var h = new Habit();
        var start = new DateTime(2026, 9, 1);
        for (int i = 0; i < 5; i++) h.Complete(start.AddDays(i));
        Assert.Equal(5, h.CurrentStreak);
    }

    [Fact]
    public void Streak_BrokenChain_CountsFromLatestRun()
    {
        var h = new Habit();
        var start = new DateTime(2026, 9, 1);
        h.Complete(start);
        h.Complete(start.AddDays(1));
        // gap
        h.Complete(start.AddDays(5));
        h.Complete(start.AddDays(6));
        Assert.Equal(2, h.CurrentStreak);
    }

    [Fact]
    public void DoubleComplete_SameDay_Idempotent()
    {
        var h = new Habit();
        var d = new DateTime(2026, 9, 3, 8, 15, 0);
        h.Complete(d);
        h.Complete(d.AddHours(2));
        Assert.Single(h.Completions);
        Assert.Single(h.CompletionLog);
    }

    [Fact]
    public void CompletionsThisWeek_CountsWithinSevenDays()
    {
        var h = new Habit();
        var monday = new DateTime(2026, 9, 8);
        for (int i = 0; i < 4; i++) h.Complete(monday.AddDays(i));
        h.Complete(monday.AddDays(20)); // outside the week
        Assert.Equal(4, h.CompletionsThisWeek(monday.AddDays(2)));
    }

    [Fact]
    public void Uncomplete_RemovesBothRecords()
    {
        var h = new Habit();
        var d = new DateTime(2026, 9, 3, 9, 0, 0);
        h.Complete(d);
        h.Uncomplete(d);
        Assert.Empty(h.Completions);
        Assert.Empty(h.CompletionLog);
    }
}

public class BootcampProgressTests
{
    [Fact]
    public void CompletionFraction_Day8Of21()
    {
        var b = new Bootcamp { DurationDays = 21, CurrentDay = 8 };
        Assert.Equal(8.0 / 21.0, b.CompletionFraction, 3);
    }

    [Fact]
    public void Today_ReturnsCorrectDayIndex()
    {
        var b = new Bootcamp
        {
            DurationDays = 3,
            CurrentDay = 2,
            Days = new List<BootcampDay>
            {
                new() { DayNumber = 1 }, new() { DayNumber = 2 }, new() { DayNumber = 3 },
            },
        };
        Assert.Equal(2, b.Today?.DayNumber);
    }
}
