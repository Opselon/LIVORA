using System.Globalization;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Activities;
using LIVORA.Application.HealthData;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;
using Microsoft.Extensions.DependencyInjection;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Wave 3b (lane 03): the Activity & Workout integration suite. Pins the four contracts the lane
/// delivers — deterministic mock source, pure aggregator with honest nulls, the provenance merge
/// (raise-only + idempotence + '+workout' marker), and the documented intensity thresholds.
/// Every test here is pure: no IO, no clock beyond injected dates, no MAUI.
/// </summary>
public class Wave3bActivityTests
{
    private static readonly DateTime Ref = new(2026, 9, 13);
    private static readonly DateTime Imported = new(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc);

    private static SampleWorkoutSource Source(int seed = 20260913, int horizon = 60, DateTime? reference = null) =>
        new(seed, horizon, reference ?? Ref, Imported);

    private static WorkoutSession Session(
        WorkoutType type = WorkoutType.Run, int minutes = 30, double? distance = null, double? energy = null,
        WorkoutQuality quality = WorkoutQuality.Complete, DataOrigin origin = DataOrigin.HealthConnect,
        DateTime? start = null, int? lengthMinutes = null) => new()
    {
        Id = $"t-{RecordStub(type, start ?? Ref, minutes)}",
        Type = type,
        StartLocal = start ?? Ref.Date.AddHours(7),
        EndLocal = (start ?? Ref.Date.AddHours(7)).AddMinutes(lengthMinutes ?? minutes),
        DistanceMeters = distance,
        EnergyKcal = energy,
        Quality = quality,
        Provenance = new Provenance
        {
            Source = "healthconnect",
            SourceRecordId = $"hc-{type}-{start ?? Ref:yyyyMMddHHmm}",
            ImportedAtUtc = Imported,
            Origin = origin,
        },
    };

    private static string RecordStub(WorkoutType type, DateTime start, int minutes) => $"{type}-{start:yyyyMMddHHmm}-{minutes}";

    private static DataPoint Point(double value, DataOrigin origin = DataOrigin.Mock,
        DataQuality quality = DataQuality.Complete, double confidence = 1.0, DateTime? ts = null, string unit = "minutes") =>
        new() { Value = value, Timestamp = ts ?? Ref.Date.AddHours(17), Origin = origin, Quality = quality, Confidence = confidence, Unit = unit };

    private static NormalizedDay Day(double? activeMinutes = 10, DataQuality activeQuality = DataQuality.Complete,
        DataOrigin activeOrigin = DataOrigin.Mock) => new()
    {
        Date = Ref.Date,
        Origin = DataOrigin.Mock,
        SleepMinutes = Point(450, unit: "minutes"),
        SleepQuality = Point(0.8, unit: "score01"),
        SleepConsistency = Point(0.8, unit: "score01"),
        BedtimeMinutesOfDay = Point(1380, unit: "minutesOfDay"),
        WakeMinutesOfDay = Point(420, unit: "minutesOfDay"),
        Steps = Point(8000, unit: "steps"),
        ActiveMinutes = activeMinutes is null
            ? DataPoint.Missing(Ref.Date.AddHours(17))
            : Point(activeMinutes.Value, activeOrigin, activeQuality),
        RecoveryScore = Point(0.7, unit: "score01"),
        Stress = Point(0.4, unit: "score01"),
        Mood = Point(0.7, unit: "score01"),
        Energy = Point(0.6, unit: "score01"),
    };

    private static DailyWorkoutSummary Summary(double minutes, int count = 1, DataOrigin? origin = DataOrigin.Mock) => new()
    {
        Date = Ref.Date,
        TotalMinutes = minutes,
        SessionCount = count,
        DominantType = count > 0 ? WorkoutType.Run : null,
        Origin = origin,
    };

    // ------------------------------------------------------------------
    // SampleWorkoutSource — determinism + honest Mock labeling
    // ------------------------------------------------------------------

    [Fact]
    public async Task SampleWorkoutSource_SameSeed_ProducesIdenticalSessions()
    {
        var a = await Source().GetSessionsAsync(Ref.AddDays(-59), Ref);
        var b = await Source(seed: 20260913).GetSessionsAsync(Ref.AddDays(-59), Ref);

        Assert.NotEmpty(a);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Id, b[i].Id);
            Assert.Equal(a[i].Type, b[i].Type);
            Assert.Equal(a[i].StartLocal, b[i].StartLocal);
            Assert.Equal(a[i].EndLocal, b[i].EndLocal);
            Assert.Equal(a[i].DistanceMeters, b[i].DistanceMeters);
            Assert.Equal(a[i].EnergyKcal, b[i].EnergyKcal);
            Assert.Equal(a[i].Quality, b[i].Quality);
            Assert.Equal(a[i].Provenance.SourceRecordId, b[i].Provenance.SourceRecordId);
        }
    }

    [Fact]
    public async Task SampleWorkoutSource_QueryWindowDoesNotChangeSessionShape()
    {
        // The full-horizon list filtered to a window must equal the window query — sessions are
        // per-day generated, never per-query generated.
        var full = await Source().GetSessionsAsync(Ref.AddDays(-59), Ref);
        var mid = await Source().GetSessionsAsync(Ref.AddDays(-10), Ref.AddDays(-5));
        Assert.Equal(full.Where(s => s.StartLocal.Date >= Ref.AddDays(-10) && s.StartLocal.Date <= Ref.AddDays(-5))
                          .Select(s => s.Id),
                     mid.Select(s => s.Id));
    }

    [Fact]
    public async Task SampleWorkoutSource_DifferentSeed_ProducesDifferentDays()
    {
        var a = await Source(seed: 1).GetSessionsAsync(Ref.AddDays(-59), Ref);
        var b = await Source(seed: 999).GetSessionsAsync(Ref.AddDays(-59), Ref);
        Assert.NotEqual(a.Count, b.Count);
    }

    [Fact]
    public async Task SampleWorkoutSource_CoversTheLast60Days_WithTheDocumentedFamilies()
    {
        var sessions = await Source().GetSessionsAsync(Ref.AddDays(-59), Ref);
        Assert.All(sessions, s => Assert.InRange(s.StartLocal.Date, Ref.AddDays(-59).Date, Ref.Date));
        var families = sessions.Select(s => s.Type).Distinct().ToList();
        Assert.Contains(WorkoutType.Walk, families);
        Assert.Contains(WorkoutType.Run, families);
        Assert.Contains(WorkoutType.Cycle, families);
        Assert.Contains(WorkoutType.Gym, families);
    }

    [Fact]
    public async Task SampleWorkoutSource_EverySession_IsLabeledMock()
    {
        // Product law §0.4: mock stays mock — state, origin, source id, record id.
        var src = Source();
        Assert.Equal(ConnectionState.Mock, src.State);
        Assert.True(src.Capabilities.HasFlag(DataSourceCapabilities.Workout));

        var sessions = await src.GetSessionsAsync(Ref.AddDays(-59), Ref);
        Assert.NotEmpty(sessions);
        Assert.All(sessions, s =>
        {
            Assert.Equal(DataOrigin.Mock, s.Provenance.Origin);
            Assert.Equal(SampleWorkoutSource.SourceId, s.Provenance.Source);
            Assert.StartsWith("sample-", s.Provenance.SourceRecordId, StringComparison.Ordinal);
            Assert.Equal(Imported, s.Provenance.ImportedAtUtc);
            Assert.True(s.EndLocal > s.StartLocal);
            Assert.Null(s.Intensity); // no HR stream in the mock — unknown, never fabricated
        });
    }

    [Fact]
    public async Task SampleWorkoutSource_NeverAttachesExtrasToSuspectOrPartialRecords()
    {
        var sessions = await Source().GetSessionsAsync(Ref.AddDays(-59), Ref);
        Assert.All(sessions, s =>
        {
            if (s.Quality == WorkoutQuality.Suspect || s.Quality == WorkoutQuality.Partial)
                Assert.Null(s.EnergyKcal);
            if (s.Quality == WorkoutQuality.Suspect)
                Assert.Null(s.DistanceMeters);
        });
    }

    [Fact]
    public async Task SampleWorkoutSource_IncludesEstimatedEnergy_SoTheDisclosureFlagGetsExercised()
    {
        var sessions = await Source().GetSessionsAsync(Ref.AddDays(-59), Ref);
        Assert.Contains(sessions, s => s.Quality == WorkoutQuality.Estimated && s.EnergyKcal > 0);
        Assert.Contains(sessions, s => s.Quality == WorkoutQuality.Complete);
    }

    [Fact]
    public async Task SampleWorkoutSource_EmptyOrReversedWindow_ReturnsNothing()
    {
        var src = Source();
        Assert.Empty(await src.GetSessionsAsync(Ref.AddDays(-5), Ref.AddDays(-10)));
        Assert.Empty(await src.GetSessionsAsync(Ref.AddDays(-200), Ref.AddDays(-120)));
    }

    // ------------------------------------------------------------------
    // WorkoutAggregator — honest nulls, estimates flagged, counts travel
    // ------------------------------------------------------------------

    [Fact]
    public void Aggregator_NullAndEmpty_DayIsHonestZeroWithNullUnknowns()
    {
        var nullSummary = WorkoutAggregator.Summarize(Ref, null);
        var emptySummary = WorkoutAggregator.Summarize(Ref, Array.Empty<WorkoutSession>());

        foreach (var s in new[] { nullSummary, emptySummary })
        {
            Assert.Equal(Ref.Date, s.Date);
            Assert.Equal(0, s.TotalMinutes);
            Assert.Equal(0, s.SessionCount);
            Assert.Null(s.DistanceMeters);   // unknown, never 0
            Assert.Null(s.EnergyKcal);
            Assert.False(s.EnergyWasEstimated);
            Assert.Null(s.DominantType);
        }
    }

    [Fact]
    public void Aggregator_NoDistanceReported_LeavesDistanceNull_NotZero()
    {
        var s = WorkoutAggregator.Summarize(Ref, new[]
        {
            Session(WorkoutType.Gym, 30, distance: null, energy: 150),
            Session(WorkoutType.Gym, 20, distance: null, energy: 100),
        });
        Assert.Null(s.DistanceMeters);
        Assert.Equal(250, s.EnergyKcal);
        Assert.Equal(50, s.TotalMinutes);
        Assert.Equal(2, s.SessionCount);
    }

    [Fact]
    public void Aggregator_EstimatedSessionFlipsTheDisclosureFlag()
    {
        var clean = WorkoutAggregator.Summarize(Ref, new[] { Session(minutes: 30, distance: 5000, energy: 300) });
        Assert.False(clean.EnergyWasEstimated);

        var blended = WorkoutAggregator.Summarize(Ref, new[]
        {
            Session(minutes: 30, energy: 300),
            Session(minutes: 20, energy: 200, quality: WorkoutQuality.Estimated, start: Ref.Date.AddHours(18)),
        });
        Assert.True(blended.EnergyWasEstimated);
        Assert.Equal(500, blended.EnergyKcal);
    }

    [Fact]
    public void Aggregator_SuspectEnergyNeverContributes()
    {
        var s = WorkoutAggregator.Summarize(Ref, new[]
        {
            Session(minutes: 30, energy: 999, quality: WorkoutQuality.Suspect),
        });
        Assert.Null(s.EnergyKcal);       // the only energy on the day was suspect → unknown
        Assert.Equal(30, s.TotalMinutes); // duration still counts — the session happened
        Assert.Equal(1, s.SessionCount);
    }

    [Fact]
    public void Aggregator_BrokenDurationsAreNotCounted()
    {
        var s = WorkoutAggregator.Summarize(Ref, new[]
        {
            Session(lengthMinutes: 0),                      // end == start
            Session(start: Ref.Date.AddHours(9), lengthMinutes: -30), // end before start
            Session(minutes: 30),
        });
        Assert.Equal(1, s.SessionCount);
        Assert.Equal(30, s.TotalMinutes);
    }

    [Fact]
    public void Aggregator_DominantTypeIsMostMinutes_FirstSeenBreaksTies()
    {
        var s = WorkoutAggregator.Summarize(Ref, new[]
        {
            Session(WorkoutType.Run, 20),
            Session(WorkoutType.Walk, 45),
            Session(WorkoutType.Cycle, 45, start: Ref.Date.AddHours(18)),
        });
        Assert.Equal(WorkoutType.Walk, s.DominantType); // 45 vs 45: walk seen first

        var single = WorkoutAggregator.Summarize(Ref, new[] { Session(WorkoutType.Gym, 30) });
        Assert.Equal(WorkoutType.Gym, single.DominantType);
    }

    [Fact]
    public void Aggregator_MixedOriginDay_HasNoSingleOrigin()
    {
        var single = WorkoutAggregator.Summarize(Ref, new[] { Session(origin: DataOrigin.HealthConnect) });
        Assert.Equal(DataOrigin.HealthConnect, single.Origin);

        var mixed = WorkoutAggregator.Summarize(Ref, new[]
        {
            Session(origin: DataOrigin.HealthConnect),
            Session(WorkoutType.Walk, 20, origin: DataOrigin.Mock, start: Ref.Date.AddHours(18)),
        });
        Assert.Null(mixed.Origin);
    }

    [Fact]
    public void Aggregator_ByDayGroups_AtStartLocalDate_OvernightToItsStartDay()
    {
        // A record with no timestamp at all is ungroupable (no calendar day to attribute).
        var orphan = new WorkoutSession
        {
            Id = "orphan", Type = WorkoutType.Run, StartLocal = default, EndLocal = default,
            Provenance = new Provenance { Source = "hc", ImportedAtUtc = Imported },
        };
        var byDay = WorkoutAggregator.SummarizeByDay(new[]
        {
            Session(minutes: 30, start: Ref.Date.AddHours(23)),
            Session(WorkoutType.Walk, 20, start: Ref.Date.AddHours(8)),
            Session(WorkoutType.Cycle, 40, start: Ref.AddDays(1).AddHours(1)),
            orphan,
        });
        Assert.Equal(2, byDay.Count);
        Assert.Equal(50, byDay[Ref.Date].TotalMinutes);       // including the 23:00 overnight run
        Assert.Equal(40, byDay[Ref.AddDays(1).Date].TotalMinutes);
        Assert.All(byDay.Values, v => Assert.True(v.SessionCount > 0));
    }

    [Fact]
    public void Aggregator_IsDeterministic_SameInputSameSummary()
    {
        var sessions = new[] { Session(minutes: 30, distance: 5000, energy: 300) };
        Assert.Equal(WorkoutAggregator.Summarize(Ref, sessions), WorkoutAggregator.Summarize(Ref, sessions));
    }

    // ------------------------------------------------------------------
    // WorkoutContributionMerger — raise-only, honest provenance, idempotent
    // ------------------------------------------------------------------

    [Fact]
    public void Merger_UndercountedDay_RaisesActiveMinutesByWorkoutMinutes()
    {
        var day = Day(activeMinutes: 10);
        var merged = WorkoutContributionMerger.Overlay(day, Summary(30));

        Assert.NotSame(day, merged);
        Assert.Equal(40, merged.ActiveMinutes.Value);
        Assert.Equal(DataQuality.Estimated, merged.ActiveMinutes.Quality); // a blend is derived, not measured
        Assert.True(merged.ActiveMinutes.Confidence <= 0.9);
    }

    [Fact]
    public void Merger_Idempotent_MergingTwiceDoesNotDoubleCount()
    {
        var day = Day(activeMinutes: 10);
        var summary = Summary(30);

        var once = WorkoutContributionMerger.Overlay(day, summary);
        var twice = WorkoutContributionMerger.Overlay(once, summary);

        Assert.Same(once, twice);                      // second pass is literally a no-op
        Assert.Equal(40, twice.ActiveMinutes.Value);   // 10+30 — never 10+30+30
    }

    [Fact]
    public void Merger_ProvenanceOverload_MarkerIsTheDurableIdempotenceKey()
    {
        var day = Day(activeMinutes: 10);
        var summary = Summary(30);
        var prov = new Provenance { Source = "healthconnect", SourceRecordId = "hc-day-2026-09-13", ImportedAtUtc = Imported };

        var first = WorkoutContributionMerger.Overlay(day, summary, prov);
        Assert.True(first.Applied);
        Assert.Equal(40, first.Day.ActiveMinutes.Value);
        Assert.EndsWith(WorkoutContributionMerger.WorkoutSegment, first.Provenance.SourceRecordId, StringComparison.Ordinal);

        // Re-run the pipeline over the merged day + its stamped provenance: nothing changes at all.
        var second = WorkoutContributionMerger.Overlay(first.Day, summary, first.Provenance);
        Assert.False(second.Applied);
        Assert.Same(first.Day, second.Day);
        Assert.Same(first.Provenance, second.Provenance);
        Assert.Equal(40, second.Day.ActiveMinutes.Value);

        // And the marker never stacks.
        Assert.Equal(first.Provenance.SourceRecordId,
                     WorkoutContributionMerger.StampWorkout(first.Provenance).SourceRecordId);
        Assert.DoesNotContain("+workout+workout", second.Provenance.SourceRecordId, StringComparison.Ordinal);
    }

    [Fact]
    public void Merger_DayAlreadyCoveringTheWorkout_IsReturnedUntouched()
    {
        var day = Day(activeMinutes: 60);
        Assert.Same(day, WorkoutContributionMerger.Overlay(day, Summary(30)));
        Assert.False(WorkoutContributionMerger.Applies(day, Summary(30)));
    }

    [Fact]
    public void Merger_MissingBase_IsFilledWithWorkoutMinutes_CarryingSummaryOrigin()
    {
        var merged = WorkoutContributionMerger.Overlay(Day(activeMinutes: null), Summary(30, origin: DataOrigin.Mock));
        Assert.Equal(30, merged.ActiveMinutes.Value);
        Assert.Equal(DataOrigin.Mock, merged.ActiveMinutes.Origin); // mock fills the hole AS mock
        Assert.Equal(Ref.Date, merged.ActiveMinutes.Timestamp.Date); // timestamp stays inside the calendar day

        var invalid = WorkoutContributionMerger.Overlay(
            Day(activeMinutes: -5, activeQuality: DataQuality.Invalid), Summary(30, origin: DataOrigin.HealthConnect));
        Assert.Equal(30, invalid.ActiveMinutes.Value);
        Assert.Equal(DataOrigin.HealthConnect, invalid.ActiveMinutes.Origin);
    }

    [Fact]
    public void Merger_ManualBase_IsNeverInflatedByProviderWorkouts()
    {
        var manual = Day(activeMinutes: 5, activeOrigin: DataOrigin.Manual);
        Assert.Same(manual, WorkoutContributionMerger.Overlay(manual, Summary(30, origin: DataOrigin.Mock)));

        // ...but the user's own logged workouts are still the user's word.
        var merged = WorkoutContributionMerger.Overlay(manual, Summary(30, origin: DataOrigin.Manual));
        Assert.Equal(35, merged.ActiveMinutes.Value);
    }

    [Fact]
    public void Merger_ClampedContribution_IsStillIdempotent_AtTheCap()
    {
        // 2000 minutes clamps to the 16h cap; after one merge the base IS the cap, and a second
        // merge with the same over-cap summary must be a strict no-op (never a new instance).
        var day = Day(activeMinutes: null);
        var over = Summary(2000);
        var once = WorkoutContributionMerger.Overlay(day, over);
        Assert.Equal(ManualMerge.ActiveMinutesMax, once.ActiveMinutes.Value);
        Assert.Same(once, WorkoutContributionMerger.Overlay(once, over));
    }

    [Fact]
    public void Merger_TouchesNothingButActiveMinutes()
    {
        var day = Day(activeMinutes: 10);
        var merged = WorkoutContributionMerger.Overlay(day, Summary(30));

        Assert.Same(day.Steps, merged.Steps);
        Assert.Same(day.SleepMinutes, merged.SleepMinutes);
        Assert.Same(day.SleepQuality, merged.SleepQuality);
        Assert.Same(day.SleepConsistency, merged.SleepConsistency);
        Assert.Same(day.BedtimeMinutesOfDay, merged.BedtimeMinutesOfDay);
        Assert.Same(day.WakeMinutesOfDay, merged.WakeMinutesOfDay);
        Assert.Same(day.RecoveryScore, merged.RecoveryScore);
        Assert.Same(day.Stress, merged.Stress);
        Assert.Same(day.Mood, merged.Mood);
        Assert.Same(day.Energy, merged.Energy);
        Assert.Equal(day.Origin, merged.Origin);
        Assert.Equal(day.Date, merged.Date);
    }

    [Fact]
    public void Merger_ClampsToTheNormalizerWindow_NeverProducesAnInvalidDay()
    {
        var merged = WorkoutContributionMerger.Overlay(Day(activeMinutes: null), Summary(2000));
        Assert.Equal(ManualMerge.ActiveMinutesMax, merged.ActiveMinutes.Value);

        var normalizer = new DataNormalizer();
        var fixed_ = normalizer.Normalize(merged, Ref);
        Assert.Equal(DataQuality.Estimated, fixed_.ActiveMinutes.Quality); // not Invalid
    }

    [Fact]
    public void Merger_NullAndEmptySummaries_AreNoOps()
    {
        var day = Day(activeMinutes: 10);
        Assert.Same(day, WorkoutContributionMerger.Overlay(day, null));
        Assert.Same(day, WorkoutContributionMerger.Overlay(day, Summary(0, count: 0)));
        Assert.Same(day, WorkoutContributionMerger.Overlay(day, Summary(999, count: 0))); // no sessions → nothing to merge
        Assert.Throws<ArgumentNullException>(() => WorkoutContributionMerger.Overlay(null!, Summary(30)));
    }

    // ------------------------------------------------------------------
    // IntensityClassifier — every documented boundary pinned
    // ------------------------------------------------------------------

    [Theory]
    // Reference pace (distance unknown) is 5/7 of the vigorous cutoff → 0.15 + 0.8·5/7 ≈ 0.721.
    [InlineData(WorkoutType.Walk, 30, null, "0.721")]
    [InlineData(WorkoutType.Run, 30, null, "0.721")]
    [InlineData(WorkoutType.Cycle, 30, null, "0.721")]
    // Walk at 6 km/h (100 m/min) against the 7 km/h cutoff.
    [InlineData(WorkoutType.Walk, 30, 3000.0, "0.836")]
    // A gentle 3 km/h walk (50 m/min).
    [InlineData(WorkoutType.Walk, 30, 1500.0, "0.493")]
    public void Classifier_SpeedModel_MatchesTheDocumentedFormula(WorkoutType type, int minutes, double? distance, string expected)
    {
        var v = IntensityClassifier.Classify(type, TimeSpan.FromMinutes(minutes), distance);
        Assert.NotNull(v);
        Assert.Equal(expected, v!.Value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Classifier_ShortBurstsAreCeilinged_NotMaximal()
    {
        // 5-minute hard run: raw speed score is >1, taper ceiling at 5 min is 0.15 + 0.5·0.30 = 0.30.
        Assert.Equal(0.30, IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.FromMinutes(5), 1600));
        // Exactly 10 minutes: ceiling 0.45.
        Assert.Equal(0.45, IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.FromMinutes(10), 3000));
        // 11 minutes clears the short-burst ceiling entirely.
        Assert.True(IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.FromMinutes(11), 3000) > 0.45);
    }

    [Fact]
    public void Classifier_LongSessionsAreCappedAt095()
    {
        // 90 minutes of very fast running: raw > 1, duration cap holds it at 0.95.
        Assert.Equal(0.95, IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.FromMinutes(90), 27000));
    }

    [Theory]
    [InlineData(WorkoutType.Mobility, 30, "0.25")]
    [InlineData(WorkoutType.Gym, 10, "0.35")]     // ≤15 short band
    [InlineData(WorkoutType.Gym, 30, "0.45")]     // standard band
    [InlineData(WorkoutType.Gym, 45, "0.65")]     // ≥45 long band
    [InlineData(WorkoutType.Sport, 30, "0.55")]   // gym + 0.10
    [InlineData(WorkoutType.Other, 30, "0.45")]   // other behaves like gym
    public void Classifier_NoPaceFamilies_SitOnTheirDocumentedBands(WorkoutType type, int minutes, string expected)
    {
        Assert.Equal(expected, IntensityClassifier.Classify(type, TimeSpan.FromMinutes(minutes))!.Value.ToString("0.###"));
    }

    [Fact]
    public void Classifier_QualityHaircutIsExactlyOneTenth()
    {
        Assert.Equal(0.35, IntensityClassifier.Classify(WorkoutType.Gym, TimeSpan.FromMinutes(30), null, WorkoutQuality.Partial));
        Assert.Equal(0.35, IntensityClassifier.Classify(WorkoutType.Gym, TimeSpan.FromMinutes(30), null, WorkoutQuality.Suspect));
        // Estimated is an estimate either way — haircutting it again would double-penalize.
        Assert.Equal(0.45, IntensityClassifier.Classify(WorkoutType.Gym, TimeSpan.FromMinutes(30), null, WorkoutQuality.Estimated));
    }

    [Fact]
    public void Classifier_UnknownInputs_ReturnNull_NeverZero()
    {
        Assert.Null(IntensityClassifier.Classify((WorkoutSession?)null));
        Assert.Null(IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.Zero));
        Assert.Null(IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.FromMinutes(-10)));
        // NaN or zero distance means "no distance reported" — the reference pace is used, and the
        // result is NOT treated as a measured 0 m/min.
        Assert.Equal(IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.FromMinutes(30)),
                     IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.FromMinutes(30), double.NaN));
        Assert.Equal(IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.FromMinutes(30)),
                     IntensityClassifier.Classify(WorkoutType.Run, TimeSpan.FromMinutes(30), 0));
    }

    [Fact]
    public void Classifier_AlwaysStaysInUnitRange()
    {
        foreach (WorkoutType type in Enum.GetValues<WorkoutType>())
            foreach (var minutes in new[] { 1, 5, 10, 30, 45, 60, 180 })
                foreach (var distance in new double?[] { null, 1, 100_000 })
                {
                    var v = IntensityClassifier.Classify(type, TimeSpan.FromMinutes(minutes), distance);
                    Assert.NotNull(v);
                    Assert.InRange(v!.Value, 0, 1);
                }
    }

    [Fact]
    public void Classifier_SessionOverload_ReadsTheRecordAndRespectsSourceIntensity()
    {
        Assert.Equal(0.721, IntensityClassifier.Classify(Session(WorkoutType.Run, 30)));
        var withSourceIntensity = new WorkoutSession
        {
            Id = "x", Type = WorkoutType.Gym, StartLocal = Ref, EndLocal = Ref.AddMinutes(30),
            Intensity = 0.8, Provenance = new Provenance { Source = "garmin", ImportedAtUtc = Imported, Origin = DataOrigin.Mock },
        };
        // The classifier models from shape, not from claims; the session's own intensity (null in
        // the mock, provider-reported elsewhere) stays the preferred signal upstream.
        Assert.Equal(0.45, IntensityClassifier.Classify(withSourceIntensity));
    }

    // ------------------------------------------------------------------
    // Composition shape — mirrors the APPEND block destined for MauiProgram.cs
    // ------------------------------------------------------------------

    [Fact]
    public void CompositionShape_WorkoutSource_IsASingletonBehindItsContract()
    {
        // The exact registration line the lane's APPEND block ships (see the report). It cannot
        // land in this copy's MauiProgram (lanes never write shared files), so the container
        // behavior is verified here instead of asserted about.
        var services = new ServiceCollection();
        services.AddSingleton<IWorkoutSource>(sp => new SampleWorkoutSource(seed: 20260913));
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IWorkoutSource>();
        var second = provider.GetRequiredService<IWorkoutSource>();
        Assert.Same(first, second);                              // singleton, as documented
        Assert.IsType<SampleWorkoutSource>(first);
        Assert.Equal(ConnectionState.Mock, first.State);         // honest even through DI
    }

    // ------------------------------------------------------------------
    // End-to-end: sample source -> aggregate -> merge
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_SampleDay_FeedsTheMergerWithoutDoubleCountingAnything()
    {
        var src = Source();
        var sessions = await src.GetSessionsAsync(Ref, Ref);
        var summaries = WorkoutAggregator.SummarizeByDay(sessions);

        var day = Day(activeMinutes: 0);
        if (!summaries.TryGetValue(Ref.Date, out var summary)) return; // no session on the ref day for this seed

        var result = WorkoutContributionMerger.Overlay(
            day, summary, new Provenance { Source = SampleWorkoutSource.SourceId, SourceRecordId = "sample-day", ImportedAtUtc = Imported, Origin = DataOrigin.Mock });

        if (result.Applied)
        {
            Assert.Equal(Math.Clamp(summary.TotalMinutes, 0, ManualMerge.ActiveMinutesMax), result.Day.ActiveMinutes.Value);
            Assert.Equal(DataOrigin.Mock, result.Day.ActiveMinutes.Origin);   // mock filled the hole AS mock
            Assert.True(WorkoutContributionMerger.IsStamped(result.Provenance));

            var again = WorkoutContributionMerger.Overlay(result.Day, summary, result.Provenance);
            Assert.False(again.Applied);
            Assert.Equal(result.Day.ActiveMinutes.Value, again.Day.ActiveMinutes.Value);
        }
    }
}
