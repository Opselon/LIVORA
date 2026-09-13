using LIVORA.Application.Abstractions;
using LIVORA.Application.HealthData;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.Activities;

/// <summary>Outcome of the provenance-carrying merge overload — see <see cref="WorkoutContributionMerger"/>.</summary>
public sealed record WorkoutMergeResult
{
    /// <summary>The day to keep (the SAME instance as the input when nothing was merged).</summary>
    public required NormalizedDay Day { get; init; }
    /// <summary>The provenance to keep: stamped with the workout marker when applied, verbatim when not.</summary>
    public required Provenance Provenance { get; init; }
    /// <summary>True when ActiveMinutes actually changed. Callers that persist provenance must only
    /// persist it when this is true (a no-op merge must not relabel a day).</summary>
    public required bool Applied { get; init; }
}

/// <summary>
/// Wave 3b (lane 03): the pure provenance merge between a provider day and that day's workout
/// summary — the established <see cref="ManualMerge"/> overlay pattern applied to activity. No IO,
/// no clock, no MAUI; returns the SAME day instance when nothing would change (callers may compare
/// by reference).
///
/// Merge rule (documented once, pinned by tests):
/// <list type="bullet">
///   <item><description>ActiveMinutes increases by the day's workout minutes ONLY when the day's
///     existing usable value is LOWER than those minutes (the provider under-counted activity).
///     When the provider already reports at least the workout minutes it presumably saw the
///     session, and the day is returned untouched — which is also what makes the day-only merge
///     idempotent: after one merge the value is ≥ the workout minutes, so a second merge with the
///     same summary is a no-op and never double-counts.</description></item>
///   <item><description>A Manual base is the user's word and is never inflated by non-manual
///     workout data (same product law as ManualMerge) — unless the workout summary is itself
///     Manual, i.e. the user's own logged workouts.</description></item>
///   <item><description>When the day has NO usable ActiveMinutes (Missing/NaN/Invalid), the workout
///     minutes become the day's value carrying the summary's origin (Mock stays Mock — never
///     laundered into "measured"). A day whose sessions mix origins collapses to Mock, the
///     least-trust family.</description></item>
///   <item><description>Everything else — Steps, all Sleep fields, Recovery, RHR, HRV, Stress,
///     Mood, Energy and the day's own Origin — is copied verbatim. Workouts only ever speak about
///     active minutes.</description></item>
///   <item><description>The merged value clamps to <see cref="ManualMerge.ActiveMinutesMax"/>, the
///     same window DataNormalizer enforces, so a merged day never becomes Invalid downstream.</description></item>
///   <item><description>The merged point is Quality=Estimated with confidence capped at 0.9: a
///     blended figure is a derived value, never "device-measured", even when both halves were.</description></item>
/// </list>
///
/// Provenance marker convention (documented once — the "how do I know this day was merged" answer):
/// <see cref="DataPoint"/> carries Origin/Quality/Confidence but no record id, so the workout
/// contribution is marked at the record level. Any <see cref="Provenance"/> for a day whose
/// ActiveMinutes was merged MUST go through <see cref="StampWorkout"/>, which appends exactly one
/// <c>"+workout"</c> segment to <c>Provenance.SourceRecordId</c>
/// ("healthconnect:day:2026-09-10" → "healthconnect:day:2026-09-10+workout").
/// <see cref="Overlay(NormalizedDay,DailyWorkoutSummary,Provenance)"/> refuses to merge a
/// provenance that already carries the segment, so re-running the pipeline over an already-merged
/// day can never stack markers or minutes — the marker is the durable idempotence key for the
/// production path, and is checked before the value comparison.
/// </summary>
public static class WorkoutContributionMerger
{
    /// <summary>Suffix segment marking "this record's ActiveMinutes includes a workout contribution".</summary>
    public const string WorkoutSegment = "+workout";

    /// <summary>
    /// Apply <paramref name="summary"/> over <paramref name="day"/> following the merge rule above.
    /// Returns <paramref name="day"/> unchanged (same reference) when there is nothing to merge.
    /// Prefer the provenance-carrying overload when the day has a record id to keep honest about.
    /// </summary>
    public static NormalizedDay Overlay(NormalizedDay day, DailyWorkoutSummary? summary)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (!Applies(day, summary)) return day;
        return Merged(day, summary!);
    }

    /// <summary>
    /// Provenance-carrying merge: the day plus the provenance its record is stored under. The
    /// returned <see cref="WorkoutMergeResult.Provenance"/> carries exactly one "+workout" segment
    /// when (and only when) the merge applied; when the day already carries the marker nothing is
    /// merged at all, so a second pass is a strict no-op for value AND provenance.
    /// </summary>
    public static WorkoutMergeResult Overlay(NormalizedDay day, DailyWorkoutSummary? summary, Provenance provenance)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(provenance);

        // The marker is the idempotence key: an already-merged record is never merged again.
        if (IsStamped(provenance))
            return new WorkoutMergeResult { Day = day, Provenance = provenance, Applied = false };

        if (!Applies(day, summary))
            return new WorkoutMergeResult { Day = day, Provenance = provenance, Applied = false };

        return new WorkoutMergeResult
        {
            Day = Merged(day, summary!),
            Provenance = StampWorkout(provenance),
            Applied = true,
        };
    }

    /// <summary>True when <see cref="Overlay(NormalizedDay,DailyWorkoutSummary)"/> would change the day.</summary>
    public static bool Applies(NormalizedDay day, DailyWorkoutSummary? summary)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (summary is null || summary.SessionCount <= 0 || !(summary.TotalMinutes > 0)
            || double.IsNaN(summary.TotalMinutes))
            return false;

        var basePoint = day.ActiveMinutes;
        var baseValue = UsableValue(basePoint);

        // Manual base: the user's word wins unless the workout data is the user's word too.
        if (baseValue is not null && basePoint!.Origin == DataOrigin.Manual && summary.Origin != DataOrigin.Manual)
            return false;

        // Only ever raise when the provider under-counted the workout (idempotence guard).
        // Compared against the CLAMPED contribution so a merge that could not change the value
        // never returns a fresh instance.
        var minutes = Math.Clamp(summary.TotalMinutes, 0, ManualMerge.ActiveMinutesMax);
        return baseValue is null || baseValue.Value < minutes;
    }

    /// <summary>
    /// The documented provenance marker: append one "+workout" segment to the record id (see the
    /// class summary). Idempotent — a provenance that already ends with the segment is returned
    /// as-is, so re-merging can never stack "+workout+workout".
    /// </summary>
    public static Provenance StampWorkout(Provenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return IsStamped(provenance)
            ? provenance
            : provenance with { SourceRecordId = provenance.SourceRecordId + WorkoutSegment };
    }

    /// <summary>True when a provenance already carries the workout marker.</summary>
    public static bool IsStamped(Provenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return provenance.SourceRecordId.EndsWith(WorkoutSegment, StringComparison.Ordinal);
    }

    /// <summary>Build the merged day. Only called when Applies() holds.</summary>
    private static NormalizedDay Merged(NormalizedDay day, DailyWorkoutSummary s)
    {
        var basePoint = day.ActiveMinutes;
        var baseValue = UsableValue(basePoint); // null = genuinely unknown (missing/NaN/Invalid)
        var minutes = Math.Clamp(s.TotalMinutes, 0, ManualMerge.ActiveMinutesMax);

        // Unknown base → the workout minutes ARE the day's value (fill the hole, honestly labeled).
        // Known-but-lower base → raise it by the workout minutes (the provider under-counted).
        var newValue = baseValue is null ? minutes : Math.Clamp(baseValue.Value + minutes, 0, ManualMerge.ActiveMinutesMax);
        var newOrigin = baseValue is null ? (s.Origin ?? DataOrigin.Mock) : basePoint!.Origin;

        return new NormalizedDay
        {
            Date = day.Date,
            Origin = day.Origin,               // workouts do not redefine the day's provider
            SleepMinutes = day.SleepMinutes,   // untouched — never laundered
            SleepQuality = day.SleepQuality,
            SleepConsistency = day.SleepConsistency,
            BedtimeMinutesOfDay = day.BedtimeMinutesOfDay,
            WakeMinutesOfDay = day.WakeMinutesOfDay,
            Steps = day.Steps,                 // untouched — step counts are not workout minutes
            ActiveMinutes = new DataPoint
            {
                Value = newValue,
                Timestamp = basePoint is not null && basePoint.Timestamp.Date == day.Date.Date
                    ? basePoint.Timestamp
                    : day.Date.Date.AddHours(12),
                Origin = newOrigin,
                Quality = DataQuality.Estimated,
                Confidence = Math.Round(Math.Clamp(Math.Min(baseValue is null ? 0.9 : basePoint!.Confidence, 0.9), 0, 1), 3),
                Unit = string.IsNullOrEmpty(basePoint?.Unit) ? "minutes" : basePoint!.Unit,
            },
            RecoveryScore = day.RecoveryScore,
            RestingHeartRate = day.RestingHeartRate,
            HrvMs = day.HrvMs,
            Stress = day.Stress,
            Mood = day.Mood,
            Energy = day.Energy,
        };
    }

    private static double? UsableValue(DataPoint? p) =>
        p is null || double.IsNaN(p.Value) || p.Value < 0 ||
        p.Quality is DataQuality.Missing or DataQuality.Invalid
            ? null
            : p.Value;
}
