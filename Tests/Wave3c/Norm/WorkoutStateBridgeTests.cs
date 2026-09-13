using LIVORA.Application.Abstractions;
using LIVORA.Application.Activities;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Tests.Wave3c.Norm;

/// <summary>
/// Wave 3c (lane 03): the workout→state seam. The bridge's whole contract is what it refuses
/// to do: never overwrite a provider's active minutes, never touch steps, never mutate.
/// </summary>
public class WorkoutStateBridgeTests
{
    private static readonly DateTime Day = new(2026, 9, 13);
    private static readonly DateTime Imported = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static NormalizedDay DayWith(
        DataQuality activeQuality = DataQuality.Missing,
        double activeValue = double.NaN,
        double steps = 8000) => new()
    {
        Date = Day,
        Origin = DataOrigin.HealthConnect,
        SleepMinutes = Point(420, "minutes"),
        SleepQuality = Point(0.8, "score01"),
        SleepConsistency = Point(0.9, "score01"),
        BedtimeMinutesOfDay = Point(1380, "minutesOfDay"),
        WakeMinutesOfDay = Point(360, "minutesOfDay"),
        Steps = Point(steps, "steps"),
        ActiveMinutes = new DataPoint
        {
            Value = activeValue, Timestamp = Day.AddHours(12), Origin = DataOrigin.HealthConnect,
            Quality = activeQuality, Confidence = activeQuality == DataQuality.Missing ? 0 : 1, Unit = "minutes",
        },
        RecoveryScore = Point(0.7, "score01"),
        Stress = Point(0.3, "score01"),
        Mood = Point(0.7, "score01"),
        Energy = Point(0.6, "score01"),
    };

    private static DataPoint Point(double v, string unit) => new()
    {
        Value = v, Timestamp = Day.AddHours(12), Origin = DataOrigin.HealthConnect,
        Quality = DataQuality.Complete, Confidence = 1, Unit = unit,
    };

    private static WorkoutSession Session(DateTime start, TimeSpan duration) => new()
    {
        Id = "s-" + start.Ticks,
        Type = WorkoutType.Run,
        StartLocal = start,
        EndLocal = start + duration,
        Provenance = new Provenance { Source = "import", SourceRecordId = "x", ImportedAtUtc = Imported, Origin = DataOrigin.Imported },
    };

    // ---- additive-only: the seam opens ONLY when the provider had nothing ------------

    [Fact]
    public void ProviderMissing_ActiveMinutesComeFromSessions()
    {
        var day = DayWith(); // Missing active minutes
        var sessions = new[] { Session(Day.AddHours(7), TimeSpan.FromMinutes(45)) };

        var (result, resolution) = WorkoutStateBridge.ApplyActiveMinutes(day, sessions);

        Assert.Equal(ActiveMinutesSource.Sessions, resolution.Source);
        Assert.Equal(45, result.ActiveMinutes.Value, 6);
        Assert.Equal(DataQuality.Estimated, result.ActiveMinutes.Quality); // derived, labeled
    }

    [Fact]
    public void ProviderZeroMinutes_SeamOpens()
    {
        var day = DayWith(DataQuality.Complete, 0); // counter says "0" — no provider minutes
        var sessions = new[] { Session(Day.AddHours(7), TimeSpan.FromMinutes(30)) };

        var (result, resolution) = WorkoutStateBridge.ApplyActiveMinutes(day, sessions);
        Assert.Equal(ActiveMinutesSource.Sessions, resolution.Source);
        Assert.Equal(30, result.ActiveMinutes.Value, 6);
    }

    [Fact]
    public void ProviderHasMinutes_SessionsNeverStackOnTop()
    {
        var day = DayWith(DataQuality.Complete, 50);
        var sessions = new[]
        {
            Session(Day.AddHours(7), TimeSpan.FromMinutes(45)),
            Session(Day.AddHours(18), TimeSpan.FromMinutes(60)),
        };

        var (result, resolution) = WorkoutStateBridge.ApplyActiveMinutes(day, sessions);

        Assert.Equal(ActiveMinutesSource.Provider, resolution.Source);
        Assert.Equal(50, result.ActiveMinutes.Value, 6);      // NOT 50+105
        Assert.True(resolution.SessionsIgnored);
        Assert.Same(day, result);                              // reference-equal no-op
    }

    [Fact]
    public void MultipleSessions_MinutesSum_CappedAtADay()
    {
        var day = DayWith();
        var sessions = new[]
        {
            Session(Day.AddHours(6), TimeSpan.FromMinutes(50)),
            Session(Day.AddHours(12), TimeSpan.FromMinutes(50)),
            Session(Day.AddHours(18), TimeSpan.FromMinutes(50)),
        };
        var (result, _) = WorkoutStateBridge.ApplyActiveMinutes(day, sessions);
        Assert.Equal(150, result.ActiveMinutes.Value, 6);
    }

    [Fact]
    public void NothingAnywhere_FieldStaysMissing()
    {
        var day = DayWith();
        var (result, resolution) = WorkoutStateBridge.ApplyActiveMinutes(day, Array.Empty<WorkoutSession>());
        Assert.Equal(ActiveMinutesSource.None, resolution.Source);
        Assert.Equal(DataQuality.Missing, result.ActiveMinutes.Quality);
        Assert.Same(day, result);
    }

    // ---- never steps -------------------------------------------------------------------

    [Fact]
    public void Steps_AreUntouched_WhenSessionsFillActiveMinutes()
    {
        var day = DayWith(steps: 8000);
        var sessions = new[] { Session(Day.AddHours(7), TimeSpan.FromMinutes(45)) };

        var (result, _) = WorkoutStateBridge.ApplyActiveMinutes(day, sessions);

        Assert.Equal(8000, result.Steps.Value, 6);
        Assert.Same(day.Steps, result.Steps);                  // literally the same DataPoint
    }

    [Fact]
    public void Steps_MissingStaysMissing_EvenWithSessions()
    {
        var day = DayWith().LetCopy(d => new NormalizedDay
        {
            Date = d.Date, Origin = d.Origin, SleepMinutes = d.SleepMinutes, SleepQuality = d.SleepQuality,
            SleepConsistency = d.SleepConsistency, BedtimeMinutesOfDay = d.BedtimeMinutesOfDay,
            WakeMinutesOfDay = d.WakeMinutesOfDay,
            Steps = DataPoint.Missing(Day, DataOrigin.HealthConnect), // provider never reported steps
            ActiveMinutes = d.ActiveMinutes, RecoveryScore = d.RecoveryScore,
            Stress = d.Stress, Mood = d.Mood, Energy = d.Energy,
        });
        var sessions = new[] { Session(Day.AddHours(7), TimeSpan.FromMinutes(45)) };

        var (result, _) = WorkoutStateBridge.ApplyActiveMinutes(day, sessions);
        Assert.Equal(DataQuality.Missing, result.Steps.Quality); // a workout is not 45 steps
        Assert.Equal(45, result.ActiveMinutes.Value, 6);
    }

    // ---- purity ----------------------------------------------------------------------------

    [Fact]
    public void Bridge_NeverMutatesTheInputDay()
    {
        var day = DayWith();
        var before = (day.ActiveMinutes.Quality, day.ActiveMinutes.Value, day.Steps.Value);
        _ = WorkoutStateBridge.ApplyActiveMinutes(day, new[] { Session(Day.AddHours(7), TimeSpan.FromMinutes(45)) });
        Assert.Equal(before, (day.ActiveMinutes.Quality, day.ActiveMinutes.Value, day.Steps.Value));
    }

    [Fact]
    public void Bridge_EveryOtherFieldPassesThroughIdentical()
    {
        var day = DayWith();
        var sessions = new[] { Session(Day.AddHours(7), TimeSpan.FromMinutes(45)) };
        var (result, _) = WorkoutStateBridge.ApplyActiveMinutes(day, sessions);

        Assert.Same(day.SleepMinutes, result.SleepMinutes);
        Assert.Same(day.BedtimeMinutesOfDay, result.BedtimeMinutesOfDay);
        Assert.Same(day.RecoveryScore, result.RecoveryScore);
        Assert.Same(day.Stress, result.Stress);
        Assert.Equal(day.Origin, result.Origin);
        Assert.Equal(day.Date, result.Date);
    }
}

internal static class TestObjectExtensions
{
    /// <summary>Fluent clone-helper (keeps the test bodies readable).</summary>
    public static T LetCopy<T>(this T value, Func<T, T> clone) => clone(value);
}
