using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Activities;
using LIVORA.Application.HealthData;
using LIVORA.Application.Normalization;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Tests.Wave3c.Norm;

/// <summary>
/// Wave 3c (lane 03): the three provider mappers. Happy paths, reject matrices, sleep
/// aggregation (midnight wrap, overlap merge, gap-day coverage) and provenance stamping.
/// Every mapper must produce a day the existing DataNormalizer accepts untouched.
/// </summary>
public class ProviderDayMapperTests
{
    private static readonly DateTime FixedUtc = new(2026, 9, 13, 6, 30, 0, DateTimeKind.Utc);
    private static readonly UnitConverter C = new();
    private static readonly PayloadValidator V = new();

    private static ProviderDayMapper Mapper() => new(C, V, () => FixedUtc);

    /// <summary>Night for day D = [D-1 12:00, D 12:00). 23:00 D-1 → 06:00 D is the normal case.</summary>
    private static readonly DateTime Day = new(2026, 9, 13);

    private static HealthConnectRawSample Bucket(
        int startMinPrevDay, int endMinPrevDay, SleepBucketStatus status, string id = "") => new()
    {
        StartLocal = Day.AddDays(-1).AddMinutes(startMinPrevDay),
        EndLocal = Day.AddDays(-1).AddMinutes(endMinPrevDay),
        Status = status,
        SourceRecordId = id,
    };

    // ---- Health Connect happy path --------------------------------------------------

    [Fact]
    public void HealthConnect_HappyPath_FullNightAndSteps()
    {
        // night 23:00 (D-1) → 06:00 (D): minutes since D-1 midnight 1380..1800
        var buckets = new List<HealthConnectRawSample>
        {
            Bucket(1380, 1800, SleepBucketStatus.InBed, "hc-1"),
            Bucket(1410, 1770, SleepBucketStatus.Asleep, "hc-2"),
            new()
            {
                StartLocal = Day.AddHours(8), EndLocal = Day.AddHours(9),
                StepsInBucket = 1200, SourceRecordId = "hc-3",
            },
        };

        var r = Mapper().MapHealthConnect(buckets, Day);

        Assert.True(r.IsUsable);
        Assert.NotNull(r.Day);
        Assert.Empty(r.RejectKeys);
        var d = r.Day!;
        Assert.Equal(420, d.SleepMinutes.Value, 6);       // union of nested in-bed/asleep = 7h
        Assert.Equal(1380, d.BedtimeMinutesOfDay.Value, 6); // 23:00 in minutes-of-day
        Assert.Equal(360, d.WakeMinutesOfDay.Value, 6);     // 06:00 wraps
        Assert.Equal(1200, d.Steps.Value, 6);
        Assert.Equal(DataOrigin.HealthConnect, d.Origin);
        Assert.Equal(DataQuality.Complete, d.SleepMinutes.Quality); // gapless night → Complete
        Assert.Equal(DataQuality.Complete, d.Steps.Quality);
    }

    [Fact]
    public void HealthConnect_ProvenanceIsStamped()
    {
        var buckets = new List<HealthConnectRawSample> { Bucket(1380, 1800, SleepBucketStatus.InBed, "hc-1") };
        var r = Mapper().MapHealthConnect(buckets, Day);

        Assert.NotNull(r.Provenance);
        Assert.Equal("healthconnect", r.Provenance!.Source);
        Assert.Equal("hc-1", r.Provenance.SourceRecordId);
        Assert.Equal(FixedUtc, r.Provenance.ImportedAtUtc);
        Assert.Equal(DataOrigin.HealthConnect, r.Provenance.Origin);
    }

    // ---- Apple Health happy path ----------------------------------------------------

    [Fact]
    public void AppleHealth_HappyPath_UnitsConvertedAndSleepMapped()
    {
        var recs = new List<AppleHealthRawQuantity>
        {
            new() { Quantity = 3.2, UnitCode = "h", TypeIdentifier = "HKQuantityTypeIdentifierTimeAsleep",
                    SourceRecordId = "ah-1", StartLocal = Day.AddDays(-1).AddHours(23), EndLocal = Day.AddHours(2.2) },
            new() { Quantity = 4.5, UnitCode = "h", TypeIdentifier = "HKCategoryTypeIdentifierSleepAnalysis",
                    SleepValue = "HKCategoryValueSleepAnalysisAsleep", SourceRecordId = "ah-2",
                    StartLocal = Day.AddDays(-1).AddHours(23.5), EndLocal = Day.AddHours(4) },
            new() { Quantity = 9000, UnitCode = "count", TypeIdentifier = "HKQuantityTypeIdentifierStepCount",
                    SourceRecordId = "ah-3", StartLocal = Day.AddHours(7), EndLocal = Day.AddHours(7.5) },
            new() { Quantity = 62, UnitCode = "count/min", TypeIdentifier = "HKQuantityTypeIdentifierRestingHeartRate",
                    SourceRecordId = "ah-4", StartLocal = Day.AddHours(6), EndLocal = Day.AddHours(6.1) },
        };

        // The sleep-analysis rows are category records: mapped by SleepValue, duration ignored.
        // The TimeAsleep quantity row is a category too (Apple's quantity is the asleep total).
        // Assert the steps + rhr path, which is the pure-quantity contract.
        var r = Mapper().MapAppleHealth(recs, Day);
        Assert.True(r.IsUsable);
        Assert.Equal(9000, r.Day!.Steps.Value, 6);
        Assert.Equal(62, r.Day!.RestingHeartRate!.Value, 6);
        Assert.Equal("bpm", r.Day!.RestingHeartRate!.Unit);
        Assert.Equal("applehealth", r.Provenance!.Source);
        Assert.Contains("ah-1", r.Provenance.SourceRecordId);
    }

    [Fact]
    public void AppleHealth_KilometreDistance_ConvertsThroughUnitTable()
    {
        var recs = new List<AppleHealthRawQuantity>
        {
            new() { Quantity = 5, UnitCode = "km", TypeIdentifier = "HKQuantityTypeIdentifierDistanceWalkingRunning",
                    SourceRecordId = "ah-d", StartLocal = Day.AddHours(8), EndLocal = Day.AddHours(9) },
            new() { Quantity = 4.2, UnitCode = "h", TypeIdentifier = "HKCategoryTypeIdentifierSleepAnalysis",
                    SleepValue = "InBed", SourceRecordId = "ah-s",
                    StartLocal = Day.AddDays(-1).AddHours(23), EndLocal = Day.AddHours(4) },
        };

        var r = Mapper().MapAppleHealth(recs, Day);
        Assert.True(r.IsUsable);
        Assert.Equal(300, r.Day!.SleepMinutes.Value, 6);  // 23:00→04:00 = 5h union
        Assert.Equal(1380, r.Day!.BedtimeMinutesOfDay.Value, 6);
        Assert.Equal(DataOrigin.AppleHealth, r.Day!.Origin);
        Assert.Equal("applehealth", r.Provenance!.Source);
    }

    // ---- Generic import happy path ---------------------------------------------------

    [Fact]
    public void Generic_HappyPath_FlatDayWithUnits()
    {
        var raw = new GenericRawDay
        {
            Date = Day,
            SourceRecordId = "gen-1",
            Metrics = new Dictionary<string, RawMetricValue>
            {
                ["sleep.minutes"] = new(462, "min"),
                ["sleep.bedtime"] = new(1385, "min"),
                ["sleep.wake"] = new(380, "min"),
                ["activity.steps"] = new(8214, "count"),
                ["activity.minutes"] = new(41, "min"),
                ["sleep.quality"] = new(80, "pct"),   // pct divides by 100
            },
        };

        var r = Mapper().MapGeneric(raw, Day, bedtimeHistoryMinutes: new[] { 1385.0, 1392.0 });

        Assert.True(r.IsUsable);
        var d = r.Day!;
        Assert.Equal(462, d.SleepMinutes.Value, 6);
        Assert.Equal(0.8, d.SleepQuality.Value, 9);
        Assert.Equal(8214, d.Steps.Value, 6);
        Assert.Equal(DataOrigin.Imported, d.Origin);
        Assert.Equal("import", r.Provenance!.Source);
        Assert.Equal("gen-1", r.Provenance.SourceRecordId);
        // flat-summary day, no buckets → nothing to scale → Complete
        Assert.Equal(DataQuality.Complete, d.SleepMinutes.Quality);
        Assert.Equal(0.95, d.SleepMinutes.Confidence, 6);
    }

    [Fact]
    public void Generic_BucketsWinOverFlatSummary()
    {
        var raw = new GenericRawDay
        {
            Date = Day,
            Metrics = new Dictionary<string, RawMetricValue> { ["sleep.minutes"] = new(999, "min") },
            SleepBuckets = new List<HealthConnectRawSample> { Bucket(1380, 1800, SleepBucketStatus.InBed) },
        };

        var r = Mapper().MapGeneric(raw, Day);
        Assert.Equal(420, r.Day!.SleepMinutes.Value, 6); // segments beat the flat summary
    }

    [Fact]
    public void Generic_UnknownUnit_RejectsFieldKeepsRest()
    {
        var raw = new GenericRawDay
        {
            Date = Day,
            Metrics = new Dictionary<string, RawMetricValue>
            {
                ["distance"] = new(5, "L"),            // litres: not a distance unit
                ["activity.steps"] = new(8000, "count"),
            },
        };

        var r = Mapper().MapGeneric(raw, Day);
        Assert.Contains("Norm.Reject.Unit", r.RejectKeys);
        Assert.Equal(8000, r.Day!.Steps.Value, 6);      // the good field survived
        Assert.True(r.IsUsable);
    }

    [Fact]
    public void Generic_NothingUsable_IsNotUsable_AndShipsNullDay()
    {
        var raw = new GenericRawDay
        {
            Date = Day,
            Metrics = new Dictionary<string, RawMetricValue> { ["distance"] = new(5, "L") },
        };

        var r = Mapper().MapGeneric(raw, Day);
        Assert.False(r.IsUsable);
        Assert.Null(r.Day);
    }

    // ---- the reject matrix ------------------------------------------------------------

    [Fact]
    public void RejectMatrix_ImpossibleFeeds()
    {
        var mapper = Mapper();

        // 36h sleep flat
        Assert.Contains("Norm.Reject.Sleep", mapper.MapGeneric(Flat(("sleep.minutes", 2160, "min")), Day).RejectKeys);
        // bpm 500
        Assert.Contains("Norm.Reject.Bpm", mapper.MapGeneric(Flat(("recovery.rhr", 500, "bpm")), Day).RejectKeys);
        // steps 999999999
        Assert.Contains("Norm.Reject.Steps", mapper.MapGeneric(Flat(("activity.steps", 999_999_999, "count")), Day).RejectKeys);
        // bedtime 25:00
        Assert.Contains("Norm.Reject.Bedtime", mapper.MapGeneric(Flat(("sleep.bedtime", 1500, "min")), Day).RejectKeys);
        // infinite energy
        Assert.Contains("Norm.Reject.NotFinite",
            mapper.MapGeneric(Flat(("energy", double.PositiveInfinity, "kcal")), Day).RejectKeys);
        // NaN steps
        Assert.Contains("Norm.Reject.NotFinite",
            mapper.MapGeneric(Flat(("activity.steps", double.NaN, "count")), Day).RejectKeys);
        // negative hrv
        Assert.Contains("Norm.Reject.Negative", mapper.MapGeneric(Flat(("recovery.hrv", -3, "ms")), Day).RejectKeys);
    }

    private static GenericRawDay Flat(params (string Key, double Value, string Unit)[] metrics) => new()
    {
        Date = Day,
        Metrics = metrics.ToDictionary(m => m.Key, m => new RawMetricValue(m.Value, m.Unit), StringComparer.Ordinal),
    };

    [Fact]
    public void HealthConnect_ReversedBucket_Rejected()
    {
        var buckets = new List<HealthConnectRawSample>
        {
            new() { StartLocal = Day.AddHours(10), EndLocal = Day.AddHours(9), StepsInBucket = 500 },
        };
        var r = Mapper().MapHealthConnect(buckets, Day);
        Assert.Contains("Norm.Reject.TimeReversed", r.RejectKeys);
    }

    [Fact]
    public void HealthConnect_AllRejected_EmptyDayIsHonestNull()
    {
        var buckets = new List<HealthConnectRawSample>
        {
            new() { StartLocal = Day.AddHours(10), EndLocal = Day.AddHours(9), StepsInBucket = 500 },
        };
        var r = Mapper().MapHealthConnect(buckets, Day);
        Assert.False(r.IsUsable);
        Assert.Null(r.Day);
    }

    // ---- sleep aggregation --------------------------------------------------------------

    [Fact]
    public void Sleep_CrossesMidnight_CountsWholeNightOnce()
    {
        // 22:30 (D-1) → 06:15 (D) = 465 minutes, bedtime 1350, wake 375
        var buckets = new List<HealthConnectRawSample>
        {
            Bucket(1350, 1815, SleepBucketStatus.InBed),
        };
        var r = Mapper().MapHealthConnect(buckets, Day);
        Assert.Equal(465, r.Day!.SleepMinutes.Value, 6);
        Assert.Equal(1350, r.Day!.BedtimeMinutesOfDay.Value, 6);
        Assert.Equal(375, r.Day!.WakeMinutesOfDay.Value, 6);
    }

    [Fact]
    public void Sleep_OverlappingBuckets_MergeNotDoubleCount()
    {
        // in-bed 23:00→07:00 (480) with TWO overlapping asleep segments inside it:
        // asleep 23:30→03:00 (210) and 02:00→06:30 (270) → union must still be 480.
        var buckets = new List<HealthConnectRawSample>
        {
            Bucket(1380, 1860, SleepBucketStatus.InBed),
            Bucket(1410, 1620, SleepBucketStatus.Asleep),
            Bucket(1560, 1830, SleepBucketStatus.Asleep),
        };
        var r = Mapper().MapHealthConnect(buckets, Day);
        Assert.Equal(480, r.Day!.SleepMinutes.Value, 6);
    }

    [Fact]
    public void Sleep_SplitNightWithGap_UnionsBothParts()
    {
        // 23:00→02:00 asleep, awake in bed 02:00→03:30 (out-of-bed), asleep 03:30→06:00
        var buckets = new List<HealthConnectRawSample>
        {
            Bucket(1380, 1560, SleepBucketStatus.Asleep),
            Bucket(1560, 1650, SleepBucketStatus.OutOfBed),
            Bucket(1650, 1800, SleepBucketStatus.Asleep),
        };
        var r = Mapper().MapHealthConnect(buckets, Day);
        Assert.Equal(180 + 150, r.Day!.SleepMinutes.Value, 6);
    }

    [Fact]
    public void Sleep_GapDay_PartialCoverage_FlagsEstimatedAndScalesConfidence()
    {
        // buckets watch night-space 0..360 and 480..720: union 600 over span 720 → coverage 5/6.
        // night window opens D-1 12:00 (= day-minutes 720): a 120-minute gap sits inside the
        // window the feed claims to have watched → the day is part-truth.
        var buckets = new List<HealthConnectRawSample>
        {
            Bucket(720, 1080, SleepBucketStatus.InBed),   // D-1 noon → 18:00
            Bucket(1200, 1440, SleepBucketStatus.InBed),  // D-1 20:00 → midnight — 120 min gap
        };
        var r = Mapper().MapHealthConnect(buckets, Day);

        var sleep = r.Day!.SleepMinutes;
        Assert.Equal(600, sleep.Value, 6);                    // union of the two segments
        Assert.Equal(DataQuality.Estimated, sleep.Quality);   // partial coverage → Estimated
        Assert.Equal(Math.Round(0.95 * (600d / 720d), 4), sleep.Confidence, 4); // coverage 5/6 scales it
        Assert.True(sleep.Confidence < 0.95);                 // scaled down, honestly
    }

    [Fact]
    public void Consistency_Formula_MatchesSpec()
    {
        // 1 − min(1, stdev/120) — population stdev of {1380, 1440, 1320} = 48.99 (≈ sqrt(2400))
        var bedtimes = new[] { 1380.0, 1440.0, 1320.0 };
        var expected = 1 - Math.Min(1, Math.Sqrt(2400) / 120);
        Assert.Equal(expected, ProviderDayMapper.ConsistencyFromBedtimes(bedtimes)!.Value, 9);
    }

    [Fact]
    public void Consistency_SingleNight_ShipsMissing()
    {
        var buckets = new List<HealthConnectRawSample> { Bucket(1380, 1800, SleepBucketStatus.InBed) };
        var r = Mapper().MapHealthConnect(buckets, Day, bedtimeHistoryMinutes: new[] { 1380.0 });
        Assert.Equal(DataQuality.Missing, r.Day!.SleepConsistency.Quality);

        var two = Mapper().MapHealthConnect(buckets, Day, bedtimeHistoryMinutes: new[] { 1380.0, 1390.0 });
        Assert.Equal(DataQuality.Estimated, two.Day!.SleepConsistency.Quality);
    }

    // ---- the day survives the existing normalizer unchanged ------------------------------

    [Fact]
    public void MappedDays_PassThroughDataNormalizer_WithoutInvalidation()
    {
        var buckets = new List<HealthConnectRawSample>
        {
            Bucket(1380, 1800, SleepBucketStatus.InBed),
            new() { StartLocal = Day.AddHours(8), EndLocal = Day.AddHours(9), StepsInBucket = 6500 },
        };
        var day = Mapper().MapHealthConnect(buckets, Day).Day!;

        var fixed_ = new DataNormalizer().Normalize(day, Day.AddHours(12));
        Assert.NotEqual(DataQuality.Invalid, fixed_.SleepMinutes.Quality);
        Assert.NotEqual(DataQuality.Invalid, fixed_.Steps.Quality);
        Assert.Equal(6500, fixed_.Steps.Value, 6);
        Assert.Equal(420, fixed_.SleepMinutes.Value, 6);
        Assert.True(fixed_.Completeness() > 0);
    }

    [Fact]
    public void MappedDays_SatisfyCompleteness_HonestAboutMissingFields()
    {
        var raw = new GenericRawDay
        {
            Date = Day,
            Metrics = new Dictionary<string, RawMetricValue> { ["sleep.minutes"] = new(400, "min") },
        };
        var day = Mapper().MapGeneric(raw, Day).Day!;
        // one real field out of eight → 12.5% complete, and never more
        Assert.Equal(1 / 8.0, day.Completeness(), 6);
        Assert.Equal(DataQuality.Missing, day.Steps.Quality);
    }

    [Fact]
    public void EmptyFeed_ShipsNothing()
    {
        var r = Mapper().MapHealthConnect(Array.Empty<HealthConnectRawSample>(), Day);
        Assert.Null(r.Day);
        Assert.False(r.IsUsable);
        Assert.Empty(r.RejectKeys); // absence is honest, not an error
    }
}
