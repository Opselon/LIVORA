using LIVORA.Application.State.Wave3b;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.State;
using static LIVORA.Tests.Wave3c.Lane04Fixtures;

namespace LIVORA.Tests.Wave3c;

/// <summary>Pinned boundaries + monotonicity — StateConfidenceCalculator (lane 04).</summary>
public class StateConfidenceCalculatorTests
{
    // ---- pinned boundary tests (mandatory) ------------------------------------

    [Fact]
    public void AllMissing_IsNearZero()
    {
        var r = StateConfidenceCalculator.Compute(
            completeness: 0, BaselineConfidence.High, maxStalenessDays: 0, OriginMix.AllDevice);
        Assert.True(r.Score < 0.05, $"all-missing scored {r.Score}");
        Assert.Equal(0, r.Score, 6);   // completeness 0 ⇒ exactly 0 regardless of the rest
    }

    [Fact]
    public void AllMissing_NoBaseline_NoData_IsExactlyZero()
    {
        var r = StateConfidenceCalculator.Compute(0, BaselineConfidence.None, 9, OriginMix.NoData);
        Assert.Equal(0, r.Score, 10);
        Assert.Equal(BaselineConfidence.None, r.ConfidenceLevel);
    }

    [Fact]
    public void Full_Fresh_High_DeviceGrade_IsAtLeast085()
    {
        var device = StateConfidenceCalculator.Compute(1.0, BaselineConfidence.High, 0, OriginMix.AllDevice);
        Assert.True(device.Score >= 0.85, $"device-grade full/fresh/high scored {device.Score}");
        var mixed = StateConfidenceCalculator.Compute(1.0, BaselineConfidence.High, 0, OriginMix.Mixed);
        Assert.True(mixed.Score >= 0.85, $"mixed-origin full/fresh/high scored {mixed.Score}");
        Assert.Equal(BaselineConfidence.High, device.ConfidenceLevel);
    }

    [Theory]
    [InlineData(1, 0.90)]
    [InlineData(2, 0.75)]
    [InlineData(3, 0.60)]
    [InlineData(4, 0.45)]
    [InlineData(5, 0.25)]
    [InlineData(30, 0.25)]
    public void StalenessMultipliers_ArePinned(int days, double expected)
    {
        Assert.Equal(expected, StateConfidenceCalculator.StalenessFactorOf(days), 6);
        // And the multiplier flows through the score one-to-one at full quality:
        var r = StateConfidenceCalculator.Compute(1.0, BaselineConfidence.High, days, OriginMix.AllDevice);
        Assert.Equal(expected, r.Score, 6);
    }

    [Fact]
    public void Staleness_DecaysMonotonicallyOneToFourPlus()
    {
        Assert.True(StateConfidenceCalculator.StalenessFactorOf(1)
            > StateConfidenceCalculator.StalenessFactorOf(2));
        Assert.True(StateConfidenceCalculator.StalenessFactorOf(2)
            > StateConfidenceCalculator.StalenessFactorOf(3));
        Assert.True(StateConfidenceCalculator.StalenessFactorOf(3)
            > StateConfidenceCalculator.StalenessFactorOf(4));
        Assert.True(StateConfidenceCalculator.StalenessFactorOf(4)
            >= StateConfidenceCalculator.StalenessFactorOf(50));
    }

    // ---- monotonicity per factor -------------------------------------------------

    [Fact]
    public void Completeness_Monotonic_NonDecreasing()
    {
        double? prev = null;
        foreach (double c in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var s = StateConfidenceCalculator.Compute(c, BaselineConfidence.High, 0, OriginMix.AllDevice).Score;
            Assert.True(prev is null || s >= prev, $"completeness {c} lowered confidence");
            prev = s;
        }
    }

    [Fact]
    public void WorstBaseline_Monotonic_NoneIsHardZero()
    {
        Assert.Equal(0, StateConfidenceCalculator
            .Compute(1.0, BaselineConfidence.None, 0, OriginMix.AllDevice).Score, 10);
        double? prev = null;
        foreach (var conf in new[] { BaselineConfidence.None, BaselineConfidence.Low,
                                     BaselineConfidence.Medium, BaselineConfidence.High })
        {
            var s = StateConfidenceCalculator.Compute(1.0, conf, 0, OriginMix.AllDevice).Score;
            Assert.True(prev is null || s >= prev!.Value, $"{conf} lowered confidence");
            prev = s;
        }
    }

    [Fact]
    public void ManualOrigin_CapsBelowDeviceGrade()
    {
        var device = StateConfidenceCalculator.Compute(1.0, BaselineConfidence.High, 0, OriginMix.AllDevice).Score;
        var mixed = StateConfidenceCalculator.Compute(1.0, BaselineConfidence.High, 0, OriginMix.Mixed).Score;
        var manual = StateConfidenceCalculator.Compute(1.0, BaselineConfidence.High, 0, OriginMix.AllManual).Score;
        Assert.True(manual < mixed && mixed < device,
            $"origin trust must order manual({manual}) < mixed({mixed}) < device({device})");
        Assert.True(manual < 0.85, "all-manual must stay below the pinned 0.85 device-grade line");
    }

    [Fact]
    public void Determinism_SameInputs_SameScore()
    {
        var a = StateConfidenceCalculator.Compute(0.62, BaselineConfidence.Medium, 2, OriginMix.Mixed);
        var b = StateConfidenceCalculator.Compute(0.62, BaselineConfidence.Medium, 2, OriginMix.Mixed);
        Assert.Equal(a.Score, b.Score);
        Assert.Equal(a, b);   // full record equality — every factor pinned too
    }

    [Theory]
    [InlineData(-3)]   // clock skew must never inflate
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NegativeStaleness_IsClampedToZero(int days)
    {
        var r = StateConfidenceCalculator.Compute(1.0, BaselineConfidence.High, days, OriginMix.AllDevice);
        Assert.Equal(Math.Max(0, days), r.MaxStalenessDays);
        Assert.Equal(StateConfidenceCalculator.StalenessFactorOf(Math.Max(0, days)), r.StalenessFactor, 6);
        Assert.InRange(r.Score, 0, 1);
    }

    [Fact]
    public void Score_NeverEscapesZeroToOne_EvenWithGarbageCompleteness()
    {
        var hi = StateConfidenceCalculator.Compute(42, BaselineConfidence.High, 0, OriginMix.AllDevice);
        Assert.Equal(1.0, hi.Completeness);    // clamped input
        Assert.True(hi.Score <= 1.0 && hi.Score >= 0);
        var neg = StateConfidenceCalculator.Compute(-1, BaselineConfidence.High, 0, OriginMix.AllDevice);
        Assert.Equal(0, neg.Score, 10);
    }

    [Fact]
    public void FactorProduct_IsTheExactScore_AtAnArbitraryPoint()
    {
        var r = StateConfidenceCalculator.Compute(0.8, BaselineConfidence.Medium, 2, OriginMix.AllManual);
        Assert.Equal(0.8 * 0.75 * 0.75 * 0.80, r.Score, 6);
    }
}
