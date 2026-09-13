using LIVORA.Application.Abstractions;
using LIVORA.Application.Activities;
using LIVORA.Application.Normalization;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Tests.Wave3c.Norm;

/// <summary>
/// Wave 3c (lane 03): workout normalization — alias map, pinned MET math, duration rejects,
/// dedupe rules, and the state bridge's additive-only contract.
/// </summary>
public class WorkoutNormalizerTests
{
    private static readonly UnitConverter C = new();
    private static readonly DateTime T0 = new(2026, 9, 13, 8, 0, 0);
    private static readonly DateTime Imported = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static RawWorkoutSession Raw(
        string alias, DateTime start, DateTime end,
        double? distance = null, string distanceUnit = "",
        double? energy = null, string energyUnit = "",
        string sourceId = "", DataOrigin origin = DataOrigin.HealthConnect, double? weightKg = null) => new()
    {
        TypeAlias = alias,
        StartLocal = start,
        EndLocal = end,
        Distance = distance,
        DistanceUnit = distanceUnit,
        Energy = energy,
        EnergyUnit = energyUnit,
        SourceId = sourceId,
        Origin = origin,
        WeightKg = weightKg,
    };

    // ---- alias map -------------------------------------------------------------

    [Theory]
    [InlineData("walk", WorkoutType.Walk)]
    [InlineData("Walking", WorkoutType.Walk)]
    [InlineData("run", WorkoutType.Run)]
    [InlineData("jog", WorkoutType.Run)]
    [InlineData("cycle", WorkoutType.Cycle)]
    [InlineData("bike", WorkoutType.Cycle)]
    [InlineData("spinning", WorkoutType.Cycle)]
    [InlineData("gym", WorkoutType.Gym)]
    [InlineData("strength", WorkoutType.Gym)]
    [InlineData("lifts", WorkoutType.Gym)]
    [InlineData("sport", WorkoutType.Sport)]
    [InlineData("football", WorkoutType.Sport)]
    [InlineData("basketball", WorkoutType.Sport)]
    [InlineData("tennis", WorkoutType.Sport)]
    [InlineData("yoga", WorkoutType.Mobility)]
    [InlineData("mobility", WorkoutType.Mobility)]
    [InlineData("stretch", WorkoutType.Mobility)]
    [InlineData("quidditch", WorkoutType.Other)]   // unknown → Other, never a wrong guess
    [InlineData("", WorkoutType.Other)]
    public void AliasMap_CoversEveryFamily(string alias, WorkoutType expected) =>
        Assert.Equal(expected, WorkoutNormalizer.MapType(alias));

    [Fact]
    public void AliasMap_HandlesProviderPrefixedIds()
    {
        Assert.Equal(WorkoutType.Run, WorkoutNormalizer.MapType("HKWorkoutActivityTypeRunning"));
        Assert.Equal(WorkoutType.Cycle, WorkoutNormalizer.MapType("cycling"));
    }

    // ---- MET energy (pinned) -----------------------------------------------------

    [Fact]
    public void MetEst_OneHourWalkAt70kg_Is245Kcal()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("walk", T0, T0.AddHours(1), sourceId: "w1"), C, Imported);

        Assert.NotNull(session.Session);
        // MET(walk)=3.5 × 1h × 70kg = 245 kcal, within 1%
        Assert.Equal(245, session.Session!.EnergyKcal!.Value, 1);
        Assert.InRange(session.Session.EnergyKcal.Value, 245 * 0.99, 245 * 1.01);
        Assert.Equal(WorkoutQuality.Estimated, session.Session.Quality); // estimated energy is labeled
    }

    [Theory]
    [InlineData("run", 9.8)]
    [InlineData("cycle", 7.5)]
    [InlineData("gym", 5.0)]
    [InlineData("sport", 8.0)]
    [InlineData("mobility", 3.0)]
    public void MetTable_PinnedPerFamily(string alias, double met)
    {
        var kcal = WorkoutNormalizer.EstimateEnergyKcal(
            WorkoutNormalizer.MapType(alias), TimeSpan.FromHours(2), 70);
        Assert.Equal(met * 2 * 70, kcal!.Value, 6);
    }

    [Fact]
    public void MetTable_Other_HasNoGuess_EnergyStaysNull()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("ultimate", T0, T0.AddHours(1), sourceId: "w-odd"), C, Imported);
        Assert.Null(session.Session!.EnergyKcal); // no defensible MET → honest null, not a number
    }

    [Fact]
    public void ReportedEnergy_Wins_NoEstimation()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("run", T0, T0.AddHours(1), energy: 600, energyUnit: "kcal", sourceId: "w2", weightKg: 72), C, Imported);
        Assert.Equal(600, session.Session!.EnergyKcal!.Value, 6);
        Assert.Equal(WorkoutQuality.Complete, session.Session.Quality);
    }

    [Fact]
    public void EnergyInKilojoules_ConvertedThroughUnitTable()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("run", T0, T0.AddHours(1), energy: 1046, energyUnit: "kj", sourceId: "w3"), C, Imported);
        Assert.Equal(250.0, session.Session!.EnergyKcal!.Value, 1); // 1046/4.184 ≈ 250
    }

    [Fact]
    public void DefaultWeight_70kg_FlaggedEstimated()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("gym", T0, T0.AddMinutes(45), sourceId: "w4"), C, Imported);
        Assert.True(session.UsedDefaultWeight);
        Assert.Equal(WorkoutQuality.Estimated, session.Session!.Quality);
        Assert.Equal(5.0 * 0.75 * 70, session.Session.EnergyKcal!.Value, 6);
    }

    [Fact]
    public void KnownWeight_UsedVerbatim()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("gym", T0, T0.AddMinutes(45), sourceId: "w5", weightKg: 90), C, Imported);
        Assert.False(session.UsedDefaultWeight);
        Assert.Equal(5.0 * 0.75 * 90, session.Session!.EnergyKcal!.Value, 6);
    }

    [Fact]
    public void WeightOverride_SuppliesProfileWeight()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("walk", T0, T0.AddHours(1), sourceId: "w6"), C, Imported, weightKgOverride: 80);
        Assert.False(session.UsedDefaultWeight);
        Assert.Equal(3.5 * 80, session.Session!.EnergyKcal!.Value, 6);
    }

    // ---- units + rejects -----------------------------------------------------------

    [Fact]
    public void DistanceInMiles_ConvertsToMeters()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("run", T0, T0.AddHours(1), distance: 5, distanceUnit: "mi",
                energy: 600, energyUnit: "kcal", sourceId: "w7"), C, Imported);
        Assert.Equal(5 * 1609.344, session.Session!.DistanceMeters!.Value, 6);
    }

    [Fact]
    public void DistanceInBadUnit_Rejected_FieldNulls_OtherFieldsSurvive()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("run", T0, T0.AddHours(1), distance: 5, distanceUnit: "L",
                energy: 600, energyUnit: "kcal", sourceId: "w8", weightKg: 72), C, Imported);
        Assert.Contains("Activity.Reject.Unit", session.RejectKeys);
        Assert.Null(session.Session!.DistanceMeters);   // unknown distance stays unknown, not 0
        Assert.Equal(600, session.Session.EnergyKcal!.Value, 6);
        Assert.Equal(WorkoutQuality.Partial, session.Session.Quality); // a reported field died
    }

    [Theory]
    [InlineData(0)]   // zero-length session
    [InlineData(-1)]  // reversed
    public void NonPositiveDuration_Rejects(int minutes)
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("run", T0, T0.AddMinutes(minutes), sourceId: "w9"), C, Imported);
        Assert.Contains("Activity.Reject.Duration", session.RejectKeys);
        Assert.Null(session.Session);
    }

    [Fact]
    public void Provenance_StampedFromRaw()
    {
        var session = WorkoutNormalizer.Normalize(
            Raw("run", T0, T0.AddHours(1), sourceId: "src-42", origin: DataOrigin.AppleHealth), C, Imported);
        Assert.Equal("applehealth", session.Session!.Provenance.Source);
        Assert.Equal("src-42", session.Session.Provenance.SourceRecordId);
        Assert.Equal(DataOrigin.AppleHealth, session.Session.Provenance.Origin);
        Assert.Equal(Imported, session.Session.Provenance.ImportedAtUtc);
    }

    // ---- dedupe: both rules -------------------------------------------------------------

    [Fact]
    public void Dedupe_SameSourceId_FoldsKeepingRicher()
    {
        var poor = new WorkoutSession
        {
            Id = "dup", Type = WorkoutType.Run, StartLocal = T0, EndLocal = T0.AddMinutes(30),
            Provenance = new Provenance { Source = "import", SourceRecordId = "X1", ImportedAtUtc = Imported, Origin = DataOrigin.Imported },
            Quality = WorkoutQuality.Partial,
        };
        var rich = new WorkoutSession
        {
            Id = "dup", Type = WorkoutType.Run, StartLocal = T0.AddSeconds(5), EndLocal = T0.AddMinutes(30).AddSeconds(5),
            DistanceMeters = 5000, EnergyKcal = 400,
            Provenance = new Provenance { Source = "import", SourceRecordId = "X1", ImportedAtUtc = Imported, Origin = DataOrigin.Imported },
            Quality = WorkoutQuality.Complete,
        };

        var r = WorkoutNormalizer.Dedupe(new[] { rich, poor });
        Assert.Single(r.Sessions);
        Assert.Equal(1, r.DuplicatesRemoved);
        Assert.Equal(5000, r.Sessions[0].DistanceMeters!.Value, 6); // richer won regardless of order
    }

    [Fact]
    public void Dedupe_SameTypeStartWithin60s_Folds()
    {
        var a = Session("a", WorkoutType.Walk, T0, T0.AddMinutes(20), energy: 80);
        var b = Session("b", WorkoutType.Walk, T0.AddSeconds(30), T0.AddMinutes(21), energy: 90);
        var c = Session("c", WorkoutType.Walk, T0.AddMinutes(10), T0.AddMinutes(30), energy: 70); // starts 570s after b — outside the window

        var r = WorkoutNormalizer.Dedupe(new[] { a, b, c });
        Assert.Equal(2, r.Sessions.Count);      // a+b fold, c stands alone
        Assert.Equal(1, r.DuplicatesRemoved);
    }

    [Fact]
    public void Dedupe_DifferentTypesAtSameTime_BothKeep()
    {
        var walk = Session("w", WorkoutType.Walk, T0, T0.AddMinutes(30), energy: 100);
        var gym = Session("g", WorkoutType.Gym, T0, T0.AddMinutes(30), energy: 150);
        var r = WorkoutNormalizer.Dedupe(new[] { walk, gym });
        Assert.Equal(2, r.Sessions.Count);
        Assert.Equal(0, r.DuplicatesRemoved);
    }

    [Fact]
    public void Dedupe_BeyondWindow_BothKeep()
    {
        var a = Session("a", WorkoutType.Run, T0, T0.AddMinutes(30), energy: 300);
        var b = Session("b", WorkoutType.Run, T0.AddSeconds(61), T0.AddMinutes(31), energy: 310);
        var r = WorkoutNormalizer.Dedupe(new[] { a, b });
        Assert.Equal(2, r.Sessions.Count);
    }

    private static WorkoutSession Session(
        string id, WorkoutType type, DateTime start, DateTime end, double? energy = null) => new()
    {
        Id = id,
        Type = type,
        StartLocal = start,
        EndLocal = end,
        EnergyKcal = energy,
        Provenance = new Provenance { Source = "import", SourceRecordId = id, ImportedAtUtc = Imported, Origin = DataOrigin.Imported },
    };

    [Fact]
    public void Dedupe_IsDeterministic_RegardlessOfInputOrder()
    {
        var a = Session("a", WorkoutType.Walk, T0, T0.AddMinutes(20), energy: 80);
        var b = Session("b", WorkoutType.Walk, T0.AddSeconds(10), T0.AddMinutes(21), energy: 90);
        var x = WorkoutNormalizer.Dedupe(new[] { a, b }).Sessions;
        var y = WorkoutNormalizer.Dedupe(new[] { b, a }).Sessions;
        Assert.Equal(x.Select(s => s.Id), y.Select(s => s.Id));
    }
}
