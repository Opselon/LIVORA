using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.HealthData;
/// <summary>
/// Wave 3 (lane 02): the pure merge half of the manual-entry pipeline. Takes whatever a provider
/// produced for a day and lets the user's self-reported values win, field by field. No IO, no
/// MAUI, no clock — trivially deterministic and testable in isolation.
///
/// Provenance rules (product law):
/// <list type="bullet">
///   <item>Every field the user entered becomes Origin=Manual, Quality=Complete, Confidence=1.0 —
///     the user's own word is treated as certain about itself (self-reported, never "measured").</item>
///   <item>Every field the user did NOT enter keeps the provider's provenance untouched
///     (the mock stays Mock/Estimated exactly as it was — a manual sleep entry does not launder
///     mock recovery into "real").</item>
///   <item>The day's overall Origin flips to Manual iff at least one override actually applied.</item>
/// </list>
///
/// Bounds: merged values are clamped into the same validity windows <see cref="DataNormalizer"/>
/// enforces (sleep ≤ 16h, steps ≤ 100 000, active ≤ 16h, scores 0..1). A self-reported figure beyond
/// the cap still reads as the user's best day, not as an Invalid point that baseline math silently
/// drops — the UI's own validation (lane 03's LogEntryRules) is the place that tells the user a
/// value is impossible BEFORE it is stored.
/// </summary>
public static class ManualMerge
{
    /// <summary>Upper bound for self-reported sleep minutes — mirrors DataNormalizer.SleepMaxAge window's range check.</summary>
    public const double SleepMinutesMax = 16 * 60;
    /// <summary>Upper bound for self-reported steps — mirrors DataNormalizer's steps range check.</summary>
    public const double StepsMax = 100_000;
    /// <summary>Upper bound for self-reported active minutes — mirrors DataNormalizer's active-minutes range check.</summary>
    public const double ActiveMinutesMax = 16 * 60;

    /// <summary>True when the record carries at least one usable value. Null, all-null and all-NaN
    /// records never trigger an overlay (no phantom Manual provenance).</summary>
    public static bool HasValues(ManualEntryRecord? record) =>
        record is not null &&
        (Usable(record.SleepMinutes) || Usable(record.Steps) || Usable(record.ActiveMinutes) ||
         Usable(record.SleepQuality) || Usable(record.Mood) || Usable(record.Energy) || Usable(record.Stress));

    /// <summary>
    /// Apply <paramref name="record"/> over <paramref name="day"/>. Returns the same instance when
    /// nothing would change (no values, or only unusable ones) — callers can compare by reference.
    /// </summary>
    public static NormalizedDay Overlay(NormalizedDay day, ManualEntryRecord? record)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (!HasValues(record)) return day;
        var r = record!;

        return new NormalizedDay
        {
            Date = day.Date,
            Origin = DataOrigin.Manual, // at least one override applied (guaranteed by HasValues)
            SleepMinutes = r.SleepMinutes is { } sleep && Usable(sleep)
                ? Manual(day.SleepMinutes, Clamped(sleep, 0, SleepMinutesMax), day, "minutes")
                : day.SleepMinutes,
            SleepQuality = OverwriteScore(day.SleepQuality, r.SleepQuality, day, "score01"),
            // Not offered by manual entry — provider provenance is kept verbatim.
            SleepConsistency = day.SleepConsistency,
            BedtimeMinutesOfDay = day.BedtimeMinutesOfDay,
            WakeMinutesOfDay = day.WakeMinutesOfDay,
            Steps = r.Steps is { } steps && Usable(steps)
                ? Manual(day.Steps, Clamped(steps, 0, StepsMax), day, "steps")
                : day.Steps,
            ActiveMinutes = r.ActiveMinutes is { } active && Usable(active)
                ? Manual(day.ActiveMinutes, Clamped(active, 0, ActiveMinutesMax), day, "minutes")
                : day.ActiveMinutes,
            RecoveryScore = day.RecoveryScore,
            RestingHeartRate = day.RestingHeartRate,
            HrvMs = day.HrvMs,
            Stress = OverwriteScore(day.Stress, r.Stress, day, "score01"),
            Mood = OverwriteScore(day.Mood, r.Mood, day, "score01"),
            Energy = OverwriteScore(day.Energy, r.Energy, day, "score01"),
        };
    }

    /// <summary>
    /// A day built from manual values alone — used when the underlying provider has nothing for a
    /// date but the user did log one. Unentered fields are honest Missing (origin Manual, because
    /// the day IS a manual day — the Missing marker keeps them out of baselines either way).
    /// </summary>
    public static NormalizedDay ManualOnlyDay(DateTime date, ManualEntryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var day = date.Date;
        var ts = day.AddHours(12); // normalized-day timestamp semantics: measured over that calendar day
        var skeleton = new NormalizedDay
        {
            Date = day,
            Origin = DataOrigin.Manual,
            SleepMinutes = DataPoint.Missing(ts, DataOrigin.Manual),
            SleepQuality = DataPoint.Missing(ts, DataOrigin.Manual),
            SleepConsistency = DataPoint.Missing(ts, DataOrigin.Manual),
            BedtimeMinutesOfDay = DataPoint.Missing(ts, DataOrigin.Manual),
            WakeMinutesOfDay = DataPoint.Missing(ts, DataOrigin.Manual),
            Steps = DataPoint.Missing(ts, DataOrigin.Manual),
            ActiveMinutes = DataPoint.Missing(ts, DataOrigin.Manual),
            RecoveryScore = DataPoint.Missing(ts, DataOrigin.Manual),
            Stress = DataPoint.Missing(ts, DataOrigin.Manual),
            Mood = DataPoint.Missing(ts, DataOrigin.Manual),
            Energy = DataPoint.Missing(ts, DataOrigin.Manual),
        };
        return Overlay(skeleton, record);
    }

    /// <summary>
    /// Scrub a record into the persisted-safe shape: values above the DataNormalizer caps clamp to
    /// the cap, negatives/NaN/unparseable are dropped (a hand-edited "-5 steps" is meaningless and
    /// must not masquerade as a real 0-step day), notes trimmed, date normalized to midnight.
    /// Used by the store on write AND on read, so files hand-edited or saved by an older build
    /// cannot smuggle Invalid points into the pipeline.
    /// </summary>
    public static ManualEntryRecord Sanitize(ManualEntryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ManualEntryRecord
        {
            Date = record.Date.Date,
            SleepMinutes = Usable(record.SleepMinutes)
                ? (int)Clamped(record.SleepMinutes!.Value, 0, SleepMinutesMax) : null,
            Steps = Usable(record.Steps) ? (int)Clamped(record.Steps!.Value, 0, StepsMax) : null,
            ActiveMinutes = Usable(record.ActiveMinutes)
                ? (int)Clamped(record.ActiveMinutes!.Value, 0, ActiveMinutesMax) : null,
            SleepQuality = Usable(record.SleepQuality) ? Clamped(record.SleepQuality!.Value, 0, 1) : null,
            Mood = Usable(record.Mood) ? Clamped(record.Mood!.Value, 0, 1) : null,
            Energy = Usable(record.Energy) ? Clamped(record.Energy!.Value, 0, 1) : null,
            Stress = Usable(record.Stress) ? Clamped(record.Stress!.Value, 0, 1) : null,
            Note = string.IsNullOrWhiteSpace(record.Note) ? null : record.Note.Trim(),
            SavedAtUtc = record.SavedAtUtc, // store-stamped; a legacy file without a stamp stays null (unknown), never a fake date
            Origin = DataOrigin.Manual, // the only legal value for this record type
        };
    }

    private static DataPoint OverwriteScore(DataPoint basePoint, double? value, NormalizedDay day, string unit) =>
        Usable(value)
            ? Manual(basePoint, Clamped(value!.Value, 0, 1), day, unit)
            : basePoint;

    /// <summary>The manual DataPoint: user's value, self-reported provenance, day-noon timestamp.</summary>
    private static DataPoint Manual(DataPoint? basePoint, double value, NormalizedDay day, string unit)
    {
        // Timestamp: keep the day's convention (midday of the calendar day). A base point's own
        // timestamp is reused only when it already belongs to that day, so freshness windows in
        // DataNormalizer (age vs today) behave identically to provider data.
        var ts = basePoint is not null && basePoint.Timestamp.Date == day.Date.Date
            ? basePoint.Timestamp
            : day.Date.Date.AddHours(12);
        return new DataPoint
        {
            Value = value,
            Timestamp = ts,
            Origin = DataOrigin.Manual,
            Quality = DataQuality.Complete,
            Confidence = 1.0,
            Unit = string.IsNullOrEmpty(basePoint?.Unit) ? unit : basePoint!.Unit,
        };
    }

    private static bool Usable(int? v) => v.HasValue && v.Value >= 0;
    private static bool Usable(double? v) => v.HasValue && !double.IsNaN(v.Value) && !double.IsNegativeInfinity(v.Value) && !double.IsPositiveInfinity(v.Value);

    private static double Clamped(double v, double min, double max) => Math.Clamp(v, min, max);
    private static double Clamped(int v, double min, double max) => Math.Clamp((double)v, min, max);
}
