using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.State;
using LIVORA.Application.State;

namespace LIVORA.Tests.Tests;

public class BaselineServiceTests
{
    [Fact]
    public void FromSamples_LessThanThreeDays_ConfidenceNone()
    {
        var b = Baseline.FromSamples(Metrics.SleepMinutes, new List<double> { 400, 420 });
        Assert.Equal(BaselineConfidence.None, b.Confidence);
    }

    [Fact]
    public void FromSamples_SevenDays_LowOrMediumConfidence_NotHigh()
    {
        var samples = Enumerable.Range(0, 7).Select(i => 450.0).ToList();
        var b = Baseline.FromSamples(Metrics.SleepMinutes, samples);
        Assert.True(b.Confidence == BaselineConfidence.Low || b.Confidence == BaselineConfidence.Medium);
    }

    [Fact]
    public void FromSamples_FourteenDays_HighConfidence()
    {
        var samples = Enumerable.Range(0, 14).Select(i => 460.0).ToList();
        var b = Baseline.FromSamples(Metrics.SleepMinutes, samples);
        Assert.Equal(BaselineConfidence.High, b.Confidence);
        Assert.Equal(460.0, b.Value, 3);
    }

    [Fact]
    public void FromSamples_Empty_ReturnsNone_WithoutThrowing()
    {
        var b = Baseline.FromSamples("x", Array.Empty<double>());
        Assert.Equal(BaselineConfidence.None, b.Confidence);
        Assert.Equal(0, b.SampleDays);
    }

    [Fact]
    public void FromSamples_AveragesCorrectly()
    {
        var b = Baseline.FromSamples(Metrics.Steps, new double[] { 5000, 7000 });
        Assert.Equal(6000, b.Value, 3);
    }
}

public class TrendServiceTests
{
    private readonly TrendService _trends = new();

    [Fact]
    public void Compute_TooFewSamples_InsufficientData()
        => Assert.Equal(TrendDirection.InsufficientData, _trends.Compute(new List<double> { 1, 2, 3 }, true));

    [Fact]
    public void Compute_Improving_HigherIsBetter()
        => Assert.Equal(TrendDirection.Improving, _trends.Compute(new List<double> { 5, 5, 5, 8, 8, 8 }, true));

    [Fact]
    public void Compute_Declining_HigherIsBetter()
        => Assert.Equal(TrendDirection.Declining, _trends.Compute(new List<double> { 8, 8, 8, 5, 5, 5 }, true));

    [Fact]
    public void Compute_Stable_WithinBand()
        => Assert.Equal(TrendDirection.Stable, _trends.Compute(new List<double> { 7, 7.1, 6.9, 7.05, 7.0, 7.1 }, true));

    [Fact]
    public void Compute_StressFalling_CountsAsImproving()
        => Assert.Equal(TrendDirection.Improving, _trends.Compute(new List<double> { 0.8, 0.8, 0.8, 0.4, 0.4, 0.4 }, higherIsBetter: false));
}

public class MetricStateTests
{
    private static MetricState Make(double value, double baseline, BaselineConfidence conf = BaselineConfidence.High, bool higherBetter = true)
        => new()
        {
            MetricKey = "m",
            Value = value,
            BaselineValue = baseline,
            BaselineConfidence = conf,
            RelativeDeviation = (value - baseline) / baseline,
            HigherIsBetter = higherBetter,
            Quality = DataQuality.Complete,
        };

    [Fact]
    public void Level_SlightlyBelowBaseline_IsNormal_NotPanic()
        => Assert.Equal(StateLevel.Normal, Make(450, 460).Level); // -2%

    [Fact]
    public void Level_WellBelowBaseline_BelowBaseline()
        => Assert.Equal(StateLevel.BelowBaseline, Make(360, 460).Level); // -21%

    [Fact]
    public void Level_NoBaseline_Unknown()
    {
        var m = new MetricState { MetricKey = "m", Value = 400 };
        Assert.Equal(StateLevel.Unknown, m.Level);
    }

    [Fact]
    public void SignedBadness_StressAbove_IsPositive()
    {
        var stress = Make(0.8, 0.5, higherBetter: false);
        Assert.True(stress.SignedBadness > 0, "high stress must read as worse-than-baseline");
    }

    [Fact]
    public void SignedBadness_SleepAbove_IsNegative()
    {
        var sleep = Make(500, 450, higherBetter: true);
        Assert.True(sleep.SignedBadness < 0, "more sleep than baseline reads as better");
    }
}
