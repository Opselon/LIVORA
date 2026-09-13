using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.Activities;

/// <summary>
/// Wave 3b (lane 03): pure relative-intensity estimation for a session, 0..1, from type +
/// duration + optional distance — the signals every provider plausibly has. This is an
/// ESTIMATE from the deterministic model below, so it is safe to show as "effort estimate"
/// (Health.Load wording already exists); it must never be labeled as measured intensity.
///
/// Documented thresholds (the whole model, no hidden constants):
/// <list type="number">
///   <item><description>Speed family (Walk/Run/Cycle): when distance is unknown the classifier
///     uses the type's reference pace (Walk 5 km/h, Run 10 km/h, Cycle 20 km/h) and marks the
///     result via <see cref="Classify(WorkoutSession)"/>'s distance-unknown branch. Known speed
///     is converted to km/h and scored against three cutoffs: moderate = 50% of the type's
///     vigorous cutoff, vigorous cutoff = Walk 7 km/h, Run 14 km/h, Cycle 28 km/h. Score =
///     0.15 + 0.80 × (speed / vigorous cutoff), clamped to [0, 1] — so walking at the vigorous
///     cutoff (a fast march) reads as maximal effort for that modality.</description></item>
///   <item><description>Duration taper: sessions at or under 10 minutes score as at most 0.45
///     (short bursts are not sustained intensity), sessions at or over 60 minutes are capped at
///     0.95 (long work accumulates load, it does not maximize instantaneous effort), and
///     0..10 minutes scale a raw score's ceiling linearly with a 0.15 floor.</description></item>
///   <item><description>Gym/Sport/Other (no pace): base 0.45, +0.20 when long (≥45 min),
///     −0.10 when short (≤15 min) → 0.35/0.45/0.65 bands. Sport gets +0.10 over Gym
///     (competitive play skews harder); Other is treated like Gym.</description></item>
///   <item><description>Mobility: fixed 0.25 band (stretching/yoga is gentle by definition).</description></item>
///   <item><description>Quality haircut: Partial and Suspect sessions lose 0.10 (their data is
///     thin, so the model stays conservative); Estimated keeps its value — it is an estimate
///     either way and haircutting it twice would double-penalize.</description></item>
///   <item><description>A session whose duration is not positive has NO definable intensity →
///     null (unknown), never 0.</description></item>
/// </list>
/// Output is always clamped into [0, 1] and rounded to 3 decimals for stable comparisons.
/// </summary>
public static class IntensityClassifier
{
    /// <summary>Reference speeds (m/min) used when a session reports no distance.</summary>
    public const double WalkReferenceSpeedMetersPerMinute = 5000 / 60.0;   // 5 km/h
    public const double RunReferenceSpeedMetersPerMinute = 10000 / 60.0;   // 10 km/h
    public const double CycleReferenceSpeedMetersPerMinute = 20000 / 60.0; // 20 km/h

    /// <summary>Speed (m/min) at which a modality's effort reads as maximal (0.95 after taper).</summary>
    public const double WalkVigorousSpeed = 7000 / 60.0;
    public const double RunVigorousSpeed = 14000 / 60.0;
    public const double CycleVigorousSpeed = 28000 / 60.0;

    public const double ShortSessionMinutes = 10;
    public const double LongSessionMinutes = 45;
    public const double SustainedSessionMinutes = 60;

    private const double SpeedBase = 0.15;
    private const double SpeedSpan = 0.80;

    /// <summary>Convenience overload reading type/duration/distance/quality off a session.</summary>
    public static double? Classify(WorkoutSession? session)
    {
        if (session is null) return null;
        return Classify(session.Type, session.Duration, session.DistanceMeters, session.Quality);
    }

    /// <summary>
    /// 0..1 effort estimate, or null when the session shape gives nothing to estimate from
    /// (non-positive duration). Pure + deterministic: same inputs, same output, no clock.
    /// </summary>
    public static double? Classify(
        WorkoutType type, TimeSpan duration, double? distanceMeters = null,
        WorkoutQuality quality = WorkoutQuality.Complete)
    {
        double minutes = duration.TotalMinutes;
        if (double.IsNaN(minutes) || minutes <= 0) return null;

        double raw = type switch
        {
            WorkoutType.Mobility => 0.25,
            WorkoutType.Gym or WorkoutType.Other => GymBand(minutes),
            WorkoutType.Sport => Math.Min(1, GymBand(minutes) + 0.10),
            _ => SpeedScore(type, minutes, distanceMeters), // Walk / Run / Cycle
        };

        raw = ApplyDurationTaper(raw, minutes);
        raw = quality switch
        {
            WorkoutQuality.Partial or WorkoutQuality.Suspect => raw - 0.10,
            _ => raw,
        };

        return Math.Round(Math.Clamp(raw, 0, 1), 3);
    }

    private static double GymBand(double minutes) =>
        minutes <= ShortSessionMinutes + 5 ? 0.35   // ≤15: short strength/metcon burst
        : minutes >= LongSessionMinutes ? 0.65       // ≥45: long grind
        : 0.45;                                      // in between: standard session

    private static double SpeedScore(WorkoutType type, double minutes, double? distanceMeters)
    {
        double speed = distanceMeters is { } d && !double.IsNaN(d) && d > 0
            ? d / minutes                                          // m/min actually reported
            : type switch
            {
                WorkoutType.Walk => WalkReferenceSpeedMetersPerMinute,
                WorkoutType.Run => RunReferenceSpeedMetersPerMinute,
                _ => CycleReferenceSpeedMetersPerMinute,
            };

        double vigorous = type switch
        {
            WorkoutType.Walk => WalkVigorousSpeed,
            WorkoutType.Run => RunVigorousSpeed,
            _ => CycleVigorousSpeed,
        };

        return SpeedBase + SpeedSpan * (speed / vigorous);
    }

    private static double ApplyDurationTaper(double raw, double minutes)
    {
        if (minutes <= ShortSessionMinutes)
        {
            // Ceiling scales 0.15..0.45 across 0..10 minutes — a 3-minute sprint is not a hard workout.
            double ceiling = 0.15 + (minutes / ShortSessionMinutes) * (0.45 - 0.15);
            return Math.Min(raw, ceiling);
        }
        if (minutes >= SustainedSessionMinutes) return Math.Min(raw, 0.95);
        return raw;
    }
}
