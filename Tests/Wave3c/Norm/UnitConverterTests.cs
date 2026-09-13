using LIVORA.Application.Abstractions;
using LIVORA.Application.Normalization;

namespace LIVORA.Tests.Wave3c.Norm;

/// <summary>
/// Wave 3c (lane 03): the deterministic unit table. Every row of the spec's conversion tables
/// pinned, unknown units loud, and round-trips proven stable (property tests, 1e-9).
/// </summary>
public class UnitConverterTests
{
    private static readonly UnitConverter C = new();

    // ---- time rows → canonical minutes -------------------------------------

    [Theory]
    [InlineData("sleep.minutes", 120, "s", 2)]
    [InlineData("sleep.minutes", 120, "sec", 2)]
    [InlineData("sleep.minutes", 120, "seconds", 2)]
    [InlineData("sleep.minutes", 42, "min", 42)]
    [InlineData("sleep.minutes", 42, "minutes", 42)]
    [InlineData("sleep.minutes", 7, "h", 420)]
    [InlineData("sleep.minutes", 7.5, "hours", 450)]
    [InlineData("sleep", 3, "h", 180)]
    [InlineData("activity.minutes", 90, "s", 1.5)]
    [InlineData("duration", 3600, "seconds", 60)]
    public void TimeRows_ConvertToMinutes(string metric, double value, string unit, double expected) =>
        Assert.Equal(expected, C.ToCanonical(metric, value, unit), 9);

    // ---- distance rows → meters ---------------------------------------------

    [Theory]
    [InlineData(5, "m", 5)]
    [InlineData(5, "meters", 5)]
    [InlineData(5, "km", 5000)]
    [InlineData(2.5, "kilometers", 2500)]
    [InlineData(5, "mi", 8046.72)]
    [InlineData(1, "miles", 1609.344)]
    [InlineData(5, "yd", 4.572)]
    [InlineData(100, "yards", 91.44)]
    [InlineData(5, "ft", 1.524)]
    [InlineData(10, "feet", 3.048)]
    public void DistanceRows_ConvertToMeters(double value, string unit, double expected) =>
        Assert.Equal(expected, C.ToCanonical("distance", value, unit), 9);

    // ---- energy rows → kcal (nutrition "cal" == kcal: documented alias) ------

    [Theory]
    [InlineData(520, "cal", 520)]
    [InlineData(520, "kcal", 520)]
    [InlineData(418.4, "kj", 100)]
    [InlineData(2092, "kilojoules", 500)]
    public void EnergyRows_ConvertToKcal(double value, string unit, double expected) =>
        Assert.Equal(expected, C.ToCanonical("energy", value, unit), 9);

    [Fact]
    public void Energy_Cal_IsKcalByConvention()
    {
        // The nutrition-Calorie convention: 1 "cal" on a fitness feed is 1 kcal, not 1/1000.
        Assert.Equal(100, C.ToCanonical("activity.calories", 100, "cal"), 9);
    }

    // ---- ratio rows → 0..1 ----------------------------------------------------

    [Theory]
    [InlineData(0.5, "ratio", 0.5)]
    [InlineData(50, "pct", 0.5)]
    [InlineData(100, "%", 1)]
    [InlineData(0.9, "score01", 0.9)]
    public void RatioRows_ConvertToFraction(double value, string unit, double expected) =>
        Assert.Equal(expected, C.ToCanonical("sleep.quality", value, unit), 9);

    // ---- counters / heart ------------------------------------------------------

    [Theory]
    [InlineData("activity.steps", 8000, "steps", "steps")]
    [InlineData("steps", 8000, "count", "steps")]
    [InlineData("recovery.rhr", 58, "bpm", "bpm")]
    [InlineData("recovery.rhr", 58, "count/min", "bpm")]
    [InlineData("recovery.hrv", 42, "ms", "ms")]
    public void CountAndHeartRows_AreCanonicalPassthrough(string metric, double value, string unit, string canonical)
    {
        Assert.Equal(value, C.ToCanonical(metric, value, unit), 9);
        Assert.Equal(canonical, C.CanonicalUnit(metric));
    }

    // ---- canonical units per family --------------------------------------------

    [Theory]
    [InlineData("sleep.minutes", "minutes")]
    [InlineData("activity.minutes", "minutes")]
    [InlineData("activity.steps", "steps")]
    [InlineData("recovery.rhr", "bpm")]
    [InlineData("recovery.hrv", "ms")]
    [InlineData("wellness.mood", "ratio")]
    [InlineData("distance", "meters")]
    [InlineData("energy", "kcal")]
    public void CanonicalUnit_MatchesContractFamilies(string metric, string expected) =>
        Assert.Equal(expected, C.CanonicalUnit(metric));

    // ---- unknown unit / metric: loud, never guessed -----------------------------

    [Theory]
    [InlineData("distance", 5, "L")]          // litres are not a distance unit
    [InlineData("sleep.minutes", 8, "cups")]  // the classic broken joke
    [InlineData("energy", 10, "furlongs")]
    [InlineData("activity.steps", 5, "kg")]
    public void UnknownUnit_Throws(string metric, double value, string unit) =>
        Assert.Throws<UnitConversionException>(() => C.ToCanonical(metric, value, unit));

    [Fact]
    public void UnknownMetric_Throws() =>
        Assert.Throws<UnitConversionException>(() => C.ToCanonical("bogus.metric", 1, "min"));

    [Fact]
    public void EmptyUnit_Throws() =>
        Assert.Throws<UnitConversionException>(() => C.ToCanonical("distance", 1, ""));

    [Fact]
    public void UnitConversionException_CarriesMetricAndUnit()
    {
        var ex = Assert.Throws<UnitConversionException>(() => C.ToCanonical("distance", 1, "L"));
        Assert.Equal("distance", ex.MetricKey);
        Assert.Equal("L", ex.Unit);
    }

    // ---- purity / determinism ----------------------------------------------------

    [Fact]
    public void SameInputs_SameOutput_EveryTime()
    {
        var a = C.ToCanonical("distance", 42.195, "km");
        for (var i = 0; i < 50; i++)
            Assert.Equal(a, C.ToCanonical("distance", 42.195, "km"));
    }

    [Fact]
    public void UnitMatching_IsCaseAndWhitespaceInsensitive()
    {
        Assert.Equal(5000, C.ToCanonical("distance", 5, " KM "), 9);
        Assert.Equal(5, C.ToCanonical("sleep.minutes", 5, "Min"), 9);
        Assert.Equal(300, C.ToCanonical("sleep.minutes", 5, "HR"), 9);
    }

    // ---- round-trip property (spec: within 1e-9) ----------------------------------

    [Theory]
    [InlineData("sleep.minutes", "s")]
    [InlineData("sleep.minutes", "sec")]
    [InlineData("sleep.minutes", "seconds")]
    [InlineData("sleep.minutes", "min")]
    [InlineData("sleep.minutes", "minutes")]
    [InlineData("sleep.minutes", "h")]
    [InlineData("sleep.minutes", "hours")]
    [InlineData("distance", "m")]
    [InlineData("distance", "meters")]
    [InlineData("distance", "km")]
    [InlineData("distance", "kilometers")]
    [InlineData("distance", "mi")]
    [InlineData("distance", "miles")]
    [InlineData("distance", "yd")]
    [InlineData("distance", "yards")]
    [InlineData("distance", "ft")]
    [InlineData("distance", "feet")]
    [InlineData("energy", "cal")]
    [InlineData("energy", "kcal")]
    [InlineData("energy", "kj")]
    [InlineData("energy", "kilojoules")]
    [InlineData("sleep.quality", "ratio")]
    [InlineData("sleep.quality", "pct")]
    [InlineData("activity.steps", "count")]
    [InlineData("recovery.rhr", "bpm")]
    [InlineData("recovery.hrv", "ms")]
    public void RoundTrip_StableWithin1e9(string metric, string unit)
    {
        double[] samples = { 0, 1, 7, 42.195, 316800.5 };
        foreach (var v in samples)
        {
            var canonical = C.ToCanonical(metric, v, unit);
            var back = C.ToSource(metric, canonical, unit);
            Assert.True(Math.Abs(back - v) < 1e-9,
                $"{metric}/{unit}: {v} -> {canonical} -> {back} (drift {Math.Abs(back - v)})");
        }
    }

    [Fact]
    public void Converter_ImplementsTheFrozenInterface()
    {
        Assert.IsAssignableFrom<IUnitConverter>(C);
        Assert.True(C.CanConvert("distance", "mi"));
        Assert.False(C.CanConvert("distance", "L"));
    }
}
