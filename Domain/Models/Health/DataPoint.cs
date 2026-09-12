using LIVORA.Domain.Enums;

namespace LIVORA.Domain.Models.Health;
/// <summary>
/// Raw-vs-derived contract: a DataPoint is a single MEASUREMENT with provenance.
/// It carries the value and honest metadata; interpretation happens downstream
/// in derived-state objects (PersonalState domains), never here.
/// </summary>
public sealed class DataPoint
{
    public double Value { get; init; }
    public DateTime Timestamp { get; init; }
    public DataOrigin Origin { get; init; } = DataOrigin.Mock;
    public DataQuality Quality { get; init; } = DataQuality.Complete;
    public bool IsEstimated => Quality == DataQuality.Estimated;
    /// <summary>0..1 measurement confidence. 1 only for device-measured Complete data.</summary>
    public double Confidence { get; init; } = 1.0;
    /// <summary>Unit hint ("minutes", "ms", "bpm", "steps") — machine-readable, not display text.</summary>
    public string Unit { get; init; } = string.Empty;

    public static DataPoint Missing(DateTime ts, DataOrigin origin = DataOrigin.Mock) => new()
    {
        Value = double.NaN, Timestamp = ts, Origin = origin, Quality = DataQuality.Missing, Confidence = 0,
    };

    /// <summary>Stale = real value but older than maxAge. Intelligence must treat it as history, not now.</summary>
    public DataPoint WithMaxAge(DateTime now, TimeSpan maxAge) =>
        Quality == DataQuality.Complete && now - Timestamp > maxAge
            ? new DataPoint { Value = Value, Timestamp = Timestamp, Origin = Origin, Quality = DataQuality.Stale, Confidence = Confidence, Unit = Unit }
            : this;
}

/// <summary>One normalized day produced by a provider adapter. All providers (mock or real)
/// must map into exactly this shape — the UI never sees provider-specific fields.</summary>
public sealed class NormalizedDay
{
    public DateTime Date { get; init; }
    public required DataOrigin Origin { get; init; }

    public DataPoint SleepMinutes { get; init; } = null!;
    public DataPoint SleepQuality { get; init; } = null!;      // 0..1
    public DataPoint SleepConsistency { get; init; } = null!;  // 0..1
    public DataPoint BedtimeMinutesOfDay { get; init; } = null!;
    public DataPoint WakeMinutesOfDay { get; init; } = null!;

    public DataPoint Steps { get; init; } = null!;
    public DataPoint ActiveMinutes { get; init; } = null!;

    public DataPoint RecoveryScore { get; init; } = null!;     // 0..1
    public DataPoint? RestingHeartRate { get; init; }
    public DataPoint? HrvMs { get; init; }

    public DataPoint Stress { get; init; } = null!;            // 0..1
    public DataPoint Mood { get; init; } = null!;              // 0..1
    public DataPoint Energy { get; init; } = null!;            // 0..1

    /// <summary>Fraction 0..1 of expected fields that are present + non-invalid.</summary>
    public double Completeness()
    {
        var pts = new[]
        {
            SleepMinutes, SleepQuality, Steps, ActiveMinutes, RecoveryScore, Stress, Mood, Energy,
        };
        return pts.Count(p => p is { Quality: DataQuality.Complete or DataQuality.Estimated }) / (double)pts.Length;
    }
}
