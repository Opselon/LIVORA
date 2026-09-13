using LIVORA.Application.Abstractions;
using LIVORA.Application.Normalization;
using LIVORA.Domain.Enums;

namespace LIVORA.Tests.Wave3c.Norm;

/// <summary>
/// Wave 3c (lane 03): the provider-edge gate. Physical walls reject, plausibility warns,
/// and every verdict is a localization KEY — never prose.
/// </summary>
public class PayloadValidatorTests
{
    private static readonly PayloadValidator V = new();
    private static readonly Provenance Prov = new()
    {
        Source = "healthconnect",
        ImportedAtUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        Origin = DataOrigin.HealthConnect,
    };

    private static NormalizedFieldBundle Bundle(params (string Key, double? Value)[] fields) => new()
    {
        Date = new DateTime(2026, 9, 1),
        Values = fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal),
        Provenance = Prov,
    };

    // ---- happy path -------------------------------------------------------------

    [Fact]
    public void RealisticDay_IsValid_WithNoKeys()
    {
        var outcome = V.ValidateDay(Bundle(
            ("sleep.minutes", 462), ("sleep.bedtime", 1385), ("sleep.wake", 380),
            ("activity.steps", 8214), ("activity.minutes", 41),
            ("recovery.rhr", 58), ("recovery.hrv", 42.5),
            ("sleep.quality", 0.8), ("wellness.stress", 0.3), ("wellness.mood", 0.7),
            ("wellness.energy", 0.6), ("recovery.score", 0.75), ("sleep.consistency", 0.9)));
        Assert.True(outcome.IsValid);
        Assert.Empty(outcome.WarnKeys);
    }

    [Fact]
    public void NullFields_AreHonestAbsence_NotInvalid()
    {
        var outcome = V.ValidateDay(Bundle(("sleep.minutes", null), ("activity.steps", null)));
        Assert.True(outcome.IsValid);
    }

    [Fact]
    public void BoundaryValues_AreAccepted()
    {
        var outcome = V.ValidateDay(Bundle(
            ("sleep.minutes", 1440), ("sleep.bedtime", 1439), ("sleep.wake", 0),
            ("activity.steps", 200_000), ("activity.minutes", 1440),
            ("recovery.rhr", 20), ("recovery.hrv", 500),
            ("sleep.quality", 0), ("wellness.mood", 1)));
        Assert.True(outcome.IsValid, string.Join(",", outcome.RejectKeys));
    }

    // ---- the reject matrix --------------------------------------------------------

    [Fact]
    public void Sleep_ThirtySixHours_Rejects()
    {
        var outcome = V.ValidateDay(Bundle(("sleep.minutes", 36 * 60)));
        Assert.Contains("Norm.Reject.Sleep", outcome.RejectKeys);
    }

    [Fact]
    public void Hrv_Negative_Rejects()
    {
        var outcome = V.ValidateDay(Bundle(("recovery.hrv", -12)));
        Assert.Contains("Norm.Reject.Negative", outcome.RejectKeys);
    }

    [Fact]
    public void Bpm_500_Rejects()
    {
        var outcome = V.ValidateDay(Bundle(("recovery.rhr", 500)));
        Assert.Contains("Norm.Reject.Bpm", outcome.RejectKeys);
    }

    [Fact]
    public void Steps_NaN_Rejects()
    {
        var outcome = V.ValidateDay(Bundle(("activity.steps", double.NaN)));
        Assert.Contains("Norm.Reject.NotFinite", outcome.RejectKeys);
    }

    [Fact]
    public void Steps_999999999_Rejects()
    {
        var outcome = V.ValidateDay(Bundle(("activity.steps", 999_999_999)));
        Assert.Contains("Norm.Reject.Steps", outcome.RejectKeys);
    }

    [Fact]
    public void Bedtime_25Hours_Rejects()
    {
        // 25:00 as minutes-of-day = 1500 — outside 0..1439.
        var outcome = V.ValidateDay(Bundle(("sleep.bedtime", 25 * 60)));
        Assert.Contains("Norm.Reject.Bedtime", outcome.RejectKeys);
    }

    [Fact]
    public void Energy_Infinite_Rejects()
    {
        var outcome = V.ValidateDay(Bundle(("energy", double.PositiveInfinity)));
        Assert.Contains("Norm.Reject.NotFinite", outcome.RejectKeys);
    }

    [Fact]
    public void Sleep_Negative_Rejects()
    {
        var outcome = V.ValidateDay(Bundle(("sleep.minutes", -5)));
        Assert.Contains("Norm.Reject.Negative", outcome.RejectKeys);
    }

    [Fact]
    public void Ratio_AboveOne_Rejects()
    {
        var outcome = V.ValidateDay(Bundle(("wellness.stress", 1.4)));
        Assert.Contains("Norm.Reject.Ratio", outcome.RejectKeys);
    }

    [Fact]
    public void ActiveMinutes_OverDay_Rejects()
    {
        var outcome = V.ValidateDay(Bundle(("activity.minutes", 1441)));
        Assert.Contains("Norm.Reject.Active", outcome.RejectKeys);
    }

    [Fact]
    public void ReversedWindow_Rejects_CrossField()
    {
        // endLocal strictly before startLocal — the shared cross-field rule.
        var start = new DateTime(2026, 9, 1, 23, 0, 0);
        var end = new DateTime(2026, 9, 1, 22, 0, 0);
        Assert.Equal("Norm.Reject.TimeReversed", PayloadValidator.ValidateWindow(start, end));
        Assert.Null(PayloadValidator.ValidateWindow(start, start)); // equal is legal
        Assert.Null(PayloadValidator.ValidateWindow(start, end.AddHours(2)));
    }

    [Fact]
    public void ValidateSleep_InterfaceSeam_OnlyReportsSleepFamily()
    {
        var keys = V.ValidateSleep(Bundle(("sleep.minutes", 3000), ("activity.steps", 999_999_999)));
        Assert.Contains("Norm.Reject.Sleep", keys);
        Assert.DoesNotContain("Norm.Reject.Steps", keys); // out of the seam's scope
    }

    // ---- plausibility = warn, never reject ------------------------------------------

    [Fact]
    public void ImplausibleButLegal_Steps_WarnsWithoutRejecting()
    {
        var outcome = V.ValidateDay(Bundle(("activity.steps", 150_000))); // legal wall, dubious life
        Assert.True(outcome.IsValid);
        Assert.Contains("Norm.Warn.StepsHigh", outcome.WarnKeys);
    }

    [Fact]
    public void ImplausibleButLegal_Sleep_WarnsWithoutRejecting()
    {
        var outcome = V.ValidateDay(Bundle(("sleep.minutes", 1300)));
        Assert.True(outcome.IsValid);
        Assert.Contains("Norm.Warn.SleepHigh", outcome.WarnKeys);
    }

    [Fact]
    public void Plausibility_Pairs_NeverProduceRejectKeys()
    {
        var outcome = PayloadValidator.Plausibility("recovery.rhr", 160);
        Assert.Empty(outcome.RejectKeys);
        Assert.Contains("Norm.Warn.RhrHigh", outcome.WarnKeys);
    }

    [Fact]
    public void AllRejectKeys_AreLocalizationKeys()
    {
        var outcome = V.ValidateDay(Bundle(
            ("sleep.minutes", double.NaN), ("activity.steps", 999_999_999),
            ("recovery.hrv", -1), ("wellness.mood", 7)));
        Assert.All(outcome.RejectKeys, k =>
        {
            Assert.StartsWith("Norm.Reject.", k);
            Assert.DoesNotContain(' ', k);
        });
        Assert.All(outcome.WarnKeys, k => Assert.StartsWith("Norm.Warn.", k));
    }

    [Fact]
    public void UnknownMetrics_AreNotThisGatesBusiness()
    {
        var outcome = V.ValidateDay(Bundle(("bogus.metric", 1e9)));
        Assert.True(outcome.IsValid); // converter, not validator, owns unknown metrics
    }

    [Fact]
    public void Validator_ImplementsTheFrozenInterface() =>
        Assert.IsAssignableFrom<IPayloadValidator>(V);
}
