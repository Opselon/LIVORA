using LIVORA.Application.HealthData;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Wave 2 ingestion honesty: the normalizer must refuse to pass through impossible values,
/// must downgrade old feeds to Stale, and must never invent device signals the provider lacks.
/// </summary>
public class DataNormalizerTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 18, 0, 0);
    private readonly DataNormalizer _sut = new();

    /// <summary>A raw day where every field is plausible and fresh; tests mutate one field at a time.</summary>
    private static NormalizedDay Raw(
        DataPoint? sleep = null, DataPoint? steps = null, DataPoint? recovery = null,
        DataPoint? stress = null, DataPoint? rhr = null, DataPoint? hrv = null) => new()
        {
            Date = Now.Date,
            Origin = DataOrigin.Mock,
            SleepMinutes = sleep ?? Pt(450, "minutes", Now.Date.AddHours(9)),
            SleepQuality = Pt(0.8, "score01", Now.Date.AddHours(9)),
            SleepConsistency = Pt(0.8, "score01", Now.Date.AddHours(9)),
            BedtimeMinutesOfDay = Pt(1380, "minutesOfDay", Now.Date.AddHours(9)),
            WakeMinutesOfDay = Pt(420, "minutesOfDay", Now.Date.AddHours(9)),
            Steps = steps ?? Pt(8000, "steps", Now.Date.AddHours(17)),
            ActiveMinutes = Pt(35, "minutes", Now.Date.AddHours(17)),
            RecoveryScore = recovery ?? Pt(0.7, "score01", Now.Date.AddHours(8)),
            RestingHeartRate = rhr,
            HrvMs = hrv,
            Stress = stress ?? Pt(0.4, "score01", Now.Date.AddHours(17)),
            Mood = Pt(0.7, "score01", Now.Date.AddHours(17)),
            Energy = Pt(0.7, "score01", Now.Date.AddHours(17)),
        };

    private static DataPoint Pt(double v, string unit, DateTime ts, DataQuality q = DataQuality.Complete) => new()
    {
        Value = v, Unit = unit, Timestamp = ts, Origin = DataOrigin.Mock, Quality = q, Confidence = 0.9,
    };

    [Fact]
    public void ImpossibleSleep_FarsBeyondRange_IsInvalid_NotClamped()
    {
        // 2000 min of sleep (33h) is not a person — it must be Invalid, and must keep its raw value
        // (clamping would launder bad sensor data into a believable number).
        var day = _sut.Normalize(Raw(sleep: Pt(2000, "minutes", Now.Date.AddHours(9))), Now);
        Assert.Equal(DataQuality.Invalid, day.SleepMinutes.Quality);
        Assert.Equal(2000, day.SleepMinutes.Value);
        Assert.Equal(0, day.SleepMinutes.Confidence);
    }

    [Theory]
    [InlineData(-100)]      // negative steps: physically impossible
    [InlineData(500_000)]   // a day with half a million steps: sensor garbage
    public void ImpossibleSteps_IsInvalid(double value)
    {
        var day = _sut.Normalize(Raw(steps: Pt(value, "steps", Now.Date.AddHours(17))), Now);
        Assert.Equal(DataQuality.Invalid, day.Steps.Quality);
        Assert.Equal(0, day.Steps.Confidence);
    }

    [Fact]
    public void InvalidData_IsNotCountedAsComplete()
    {
        var day = _sut.Normalize(Raw(steps: Pt(500_000, "steps", Now.Date.AddHours(17))), Now);
        // Completeness() only counts Complete/Estimated -> the invalid field is honestly excluded.
        Assert.True(day.Completeness() < 1.0);
    }

    [Fact]
    public void StaleActivityFeed_MarkedStale_ValuePreserved()
    {
        // ActivityMaxAge = 1 day; a steps reading 3 days old is history, not today.
        var old = Pt(9000, "steps", Now.Date.AddDays(-3).AddHours(17));
        var day = _sut.Normalize(Raw(steps: old), Now);
        Assert.Equal(DataQuality.Stale, day.Steps.Quality);
        Assert.Equal(9000, day.Steps.Value);            // value kept — stale is not missing
        Assert.Equal(old.Timestamp, day.Steps.Timestamp);
    }

    [Fact]
    public void SleepFeed_TwoDaysOld_StillFresh_BeyondTwoDays_Stale()
    {
        var freshish = Pt(430, "minutes", Now.Date.AddDays(-1).AddHours(9));
        Assert.Equal(DataQuality.Complete, _sut.Normalize(Raw(sleep: freshish), Now).SleepMinutes.Quality);

        var tooOld = Pt(430, "minutes", Now.Date.AddDays(-4).AddHours(9));
        Assert.Equal(DataQuality.Stale, _sut.Normalize(Raw(sleep: tooOld), Now).SleepMinutes.Quality);
    }

    [Fact]
    public void WellnessWithinItsOwnWindow_StaysComplete()
    {
        // WellnessMaxAge = 3 days, ActivityMaxAge = 1 — different windows must not be conflated.
        var twoDays = Pt(0.45, "score01", Now.Date.AddDays(-2).AddHours(17));
        var day = _sut.Normalize(Raw(stress: twoDays), Now);
        Assert.Equal(DataQuality.Complete, day.Stress.Quality);
    }

    [Fact]
    public void NaN_MarksMissing_NotZero()
    {
        // A NaN must never leak downstream as 0.0 — 0 is a real (and alarming) value for these metrics.
        var day = _sut.Normalize(Raw(sleep: Pt(double.NaN, "minutes", Now.Date.AddHours(9))), Now);
        Assert.Equal(DataQuality.Missing, day.SleepMinutes.Quality);
        Assert.True(double.IsNaN(day.SleepMinutes.Value));
        Assert.Equal(0, day.SleepMinutes.Confidence);
    }

    [Fact]
    public void AbsentOptionalDeviceSignals_StayAbsent()
    {
        // The mock has no HR/HRV hardware. Normalizing must not manufacture a resting heart rate.
        var day = _sut.Normalize(Raw(), Now);
        Assert.Null(day.RestingHeartRate);
        Assert.Null(day.HrvMs);
    }

    [Fact]
    public void PresentOptionalDeviceSignals_AreStillQualityChecked()
    {
        var day = _sut.Normalize(Raw(
            rhr: Pt(58, "bpm", Now.Date.AddHours(8)),
            hrv: Pt(4000, "ms", Now.Date.AddHours(8))), Now);
        Assert.NotNull(day.RestingHeartRate);
        Assert.Equal(DataQuality.Complete, day.RestingHeartRate!.Quality);
        Assert.Equal(DataQuality.Invalid, day.HrvMs!.Quality);   // 4000ms HRV is not real
    }

    [Fact]
    public void PlausibleFreshDay_IsUnchanged()
    {
        var raw = Raw();
        var day = _sut.Normalize(raw, Now);
        Assert.Equal(DataQuality.Complete, day.SleepMinutes.Quality);
        Assert.Equal(DataQuality.Complete, day.Steps.Quality);
        Assert.Equal(DataQuality.Complete, day.RecoveryScore.Quality);
        Assert.Equal(1.0, day.Completeness());
        Assert.Equal(raw.Date, day.Date);
        Assert.Equal(raw.Origin, day.Origin);
    }
}

/// <summary>SampleHealthProvider must be reproducible: same (date, profile) => same day.</summary>
public class SampleHealthProviderTests
{
    private static readonly DateTime Day = new(2026, 9, 11);
    private readonly SampleHealthProvider _sut = new();

    private static UserProfile Profile() => new() { Id = "user-fixed-1", ActivityLevel = ActivityLevel.Moderate };

    [Fact]
    public async Task SameDateAndProfile_ProduceIdenticalDays()
    {
        var a = await _sut.GetNormalizedDayAsync(Day, Profile());
        var b = await _sut.GetNormalizedDayAsync(Day, Profile());

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Date, b.Date);
        Assert.Equal(a.Origin, b.Origin);
        Assert.Equal(a.SleepMinutes.Value, b.SleepMinutes.Value);
        Assert.Equal(a.SleepQuality.Value, b.SleepQuality.Value);
        Assert.Equal(a.Steps.Value, b.Steps.Value);
        Assert.Equal(a.ActiveMinutes.Value, b.ActiveMinutes.Value);
        Assert.Equal(a.RecoveryScore.Value, b.RecoveryScore.Value);
        Assert.Equal(a.Stress.Value, b.Stress.Value);
        Assert.Equal(a.Mood.Value, b.Mood.Value);
        Assert.Equal(a.Energy.Value, b.Energy.Value);
        Assert.Equal(a.BedtimeMinutesOfDay.Value, b.BedtimeMinutesOfDay.Value);
    }

    [Fact]
    public async Task DifferentProfiles_DoNotCollide()
    {
        // Determinism is per-user — two users must not share the same "history".
        var x = await _sut.GetNormalizedDayAsync(Day, new UserProfile { Id = "user-x" });
        var y = await _sut.GetNormalizedDayAsync(Day, new UserProfile { Id = "user-y" });
        Assert.NotEqual(x!.Stress.Value, y!.Stress.Value);
    }

    [Fact]
    public async Task DifferentDates_Differ()
    {
        var profile = Profile();   // one instance: only the date may change here
        var d1 = await _sut.GetNormalizedDayAsync(Day, profile);
        var d2 = await _sut.GetNormalizedDayAsync(Day.AddDays(1), profile);
        Assert.NotEqual(d1!.SleepMinutes.Value, d2!.SleepMinutes.Value);
    }

    [Fact]
    public async Task MockProvider_DoesNotFabricateDeviceSignals()
    {
        var day = await _sut.GetNormalizedDayAsync(Day, Profile());
        Assert.Null(day!.RestingHeartRate);
        Assert.Null(day.HrvMs);
        Assert.Equal(DataOrigin.Mock, day.Origin);   // never claims to be a real device feed
        // Nothing in the day may reference a heart-rate signal, directly or via derived fields.
        Assert.DoesNotContain(new[] { day.RestingHeartRate, day.HrvMs }, p => p is not null);
    }

    [Fact]
    public async Task ProviderOutput_SurvivesNormalization_Unharmed()
    {
        // The canonical mock must emit plausible, fresh values: the normalizer should have nothing to fix.
        var day = await _sut.GetNormalizedDayAsync(Day, Profile());
        var normalized = new DataNormalizer().Normalize(day!, Day.AddHours(18));
        Assert.Equal(1.0, normalized.Completeness());
        Assert.All(new[] { normalized.SleepMinutes, normalized.Steps, normalized.RecoveryScore, normalized.Stress },
            p => Assert.Equal(DataQuality.Complete, p.Quality));
    }

    [Fact]
    public void Provider_HonestAboutCapabilities()
    {
        // Sleep/steps/activity/recovery/wellness yes; heart rate and HRV are not claimed.
        var caps = _sut.Capabilities;
        Assert.True(caps.HasFlag(DataSourceCapabilities.Sleep));
        Assert.True(caps.HasFlag(DataSourceCapabilities.Steps));
        Assert.False(caps.HasFlag(DataSourceCapabilities.HeartRate));
        Assert.False(caps.HasFlag(DataSourceCapabilities.HRV));
        Assert.Equal(DataOrigin.Mock, _sut.Origin);
    }
}

/// <summary>Direct unit coverage of the freshness primitive the normalizer is built on.</summary>
public class DataPointFreshnessTests
{
    private static DataPoint Pt(double v, DateTime ts, DataQuality q = DataQuality.Complete) =>
        new() { Value = v, Timestamp = ts, Unit = "minutes", Quality = q, Confidence = 0.9 };

    [Fact]
    public void WithMaxAge_Fresh_KeepsComplete()
    {
        var ts = new DateTime(2026, 9, 11, 8, 0, 0);
        var p = Pt(450, ts);
        var result = p.WithMaxAge(ts.AddHours(6), TimeSpan.FromDays(2));
        Assert.Equal(DataQuality.Complete, result.Quality);
        Assert.Same(p, result); // untouched points are returned as-is (no needless allocation)
    }

    [Fact]
    public void WithMaxAge_Old_BecomesStale_KeepsValue()
    {
        var ts = new DateTime(2026, 9, 1, 8, 0, 0);
        var p = Pt(450, ts);
        var result = p.WithMaxAge(ts.AddDays(5), TimeSpan.FromDays(2));
        Assert.Equal(DataQuality.Stale, result.Quality);
        Assert.Equal(450, result.Value);
        Assert.Equal(p.Timestamp, result.Timestamp);
        Assert.Equal(p.Origin, result.Origin);
        Assert.Equal(p.Unit, result.Unit);
    }

    [Fact]
    public void WithMaxAge_ExactlyAtBoundary_IsNotStale()
    {
        var ts = new DateTime(2026, 9, 1, 8, 0, 0);
        var p = Pt(450, ts);
        Assert.Equal(DataQuality.Complete, p.WithMaxAge(ts.Add(TimeSpan.FromDays(2)), TimeSpan.FromDays(2)).Quality);
    }

    [Fact]
    public void WithMaxAge_DoesNotResurrectMissingData()
    {
        var ts = new DateTime(2026, 9, 1, 8, 0, 0);
        var missing = DataPoint.Missing(ts);
        Assert.Equal(DataQuality.Missing, missing.WithMaxAge(ts.AddDays(30), TimeSpan.FromDays(1)).Quality);
        Assert.True(double.IsNaN(missing.Value));
        Assert.Equal(0, missing.Confidence);
    }

    [Fact]
    public void WithMaxAge_LeavesInvalidAlone()
    {
        var ts = new DateTime(2026, 9, 1, 8, 0, 0);
        var invalid = Pt(9999, ts, DataQuality.Invalid);
        Assert.Equal(DataQuality.Invalid, invalid.WithMaxAge(ts.AddDays(30), TimeSpan.FromDays(1)).Quality);
    }

    [Fact]
    public void IsEstimated_OnlyForEstimatedQuality()
    {
        var ts = new DateTime(2026, 9, 11, 8, 0, 0);
        Assert.True(Pt(1, ts, DataQuality.Estimated).IsEstimated);
        Assert.False(Pt(1, ts, DataQuality.Complete).IsEstimated);
        Assert.False(Pt(1, ts, DataQuality.Stale).IsEstimated);
    }
}
