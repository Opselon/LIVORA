using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.Activities;

// =============================================================================
// Wave 3c (lane 03) — the workout→state seam.
//
// THE contract between the activity pipeline and the state engine (lanes 04/05):
// when a provider day has NO active minutes (missing or zero — the feed simply
// doesn't deliver that field), the day's workout sessions supply them instead.
// When the provider DID report minutes, sessions never add on top — that would
// double-count the very exercise the provider already measured. Steps are never
// touched by this seam: a workout is not 10 000 invisible steps, and the step
// counter stays whatever the provider said (including Missing).
// =============================================================================

/// <summary>The bridge's answer for one day: what active minutes to use, and from where.</summary>
public enum ActiveMinutesSource
{
    /// <summary>Provider reported real minutes — sessions contributed nothing.</summary>
    Provider,
    /// <summary>Provider had nothing; session minutes were used instead.</summary>
    Sessions,
    /// <summary>Neither provider nor any session has minutes — field stays Missing.</summary>
    None,
}

/// <summary>Result of one bridge pass. Immutable; the input day is never mutated.</summary>
public sealed record ActiveMinutesResolution(
    double? ActiveMinutes,
    ActiveMinutesSource Source,
    // True when provider minutes existed (>0) and session minutes were ignored as duplicates.
    bool SessionsIgnored)
{
    public bool HasValue => ActiveMinutes is not null;
}

/// <summary>
/// Pure bridge consumed by the lane 04/05 state engine. Call it once per day with the mapped
/// <see cref="NormalizedDay"/> and that day's deduped sessions; take the returned day (or the
/// same instance when nothing changes) into the state pipeline. No mutation, no clock, no IO:
/// <code>
///   var (day, resolution) = WorkoutStateBridge.ApplyActiveMinutes(mappedDay, sessions);
/// </code>
/// </summary>
public static class WorkoutStateBridge
{
    /// <summary>
    /// The day's active minutes: provider value when it is present and non-zero; otherwise the
    /// sum of session durations (capped at 1440 — minutes in a day). Session-sourced minutes are
    /// Estimated (derived from session windows, not a provider activity counter).
    /// </summary>
    public static ActiveMinutesResolution ResolveActiveMinutes(
        NormalizedDay day,
        IReadOnlyList<WorkoutSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(sessions);

        var provider = day.ActiveMinutes;
        // Any REAL provider value (Complete/Estimated/Stale) above zero closes the seam —
        // Stale is old truth, not missing truth. Only Missing/Invalid/NaN/zero opens it.
        var providerHasMinutes =
            provider is { Quality: DataQuality.Complete or DataQuality.Estimated or DataQuality.Stale } p &&
            !double.IsNaN(p.Value) && p.Value > 0;

        if (providerHasMinutes)
        {
            // Provider already measured activity for this day — sessions would only double it.
            var hasSessions = sessions.Any(s => s.Duration > TimeSpan.Zero);
            return new ActiveMinutesResolution(provider!.Value, ActiveMinutesSource.Provider, hasSessions);
        }

        double total = 0;
        foreach (var s in sessions)
            if (s.Duration > TimeSpan.Zero) total += s.Duration.TotalMinutes;

        if (total <= 0)
            return new ActiveMinutesResolution(null, ActiveMinutesSource.None, false);

        return new ActiveMinutesResolution(
            Math.Min(1440, total), ActiveMinutesSource.Sessions, false);
    }

    /// <summary>
    /// Apply the seam to a whole day: returns a NEW NormalizedDay whose ActiveMinutes comes from
    /// the resolution above. Steps and every other field pass through byte-identical — this
    /// function contributes active minutes and NOTHING else. When the provider value stands, the
    /// same instance is returned (reference-comparable no-op, mirroring ManualMerge's contract).
    /// </summary>
    public static (NormalizedDay Day, ActiveMinutesResolution Resolution) ApplyActiveMinutes(
        NormalizedDay day,
        IReadOnlyList<WorkoutSession> sessions)
    {
        var resolution = ResolveActiveMinutes(day, sessions);
        if (resolution.Source is ActiveMinutesSource.Provider or ActiveMinutesSource.None)
            return (day, resolution); // provider stands, or there is nothing to add — never a mutation

        var ts = day.Date.AddHours(12);
        var origin = resolution.ActiveMinutes is not null ? FindSessionOrigin(sessions) : DataOrigin.Mock;
        var replacement = new DataPoint
        {
            Value = resolution.ActiveMinutes!.Value,
            Timestamp = ts,
            Origin = origin,
            Quality = DataQuality.Estimated, // derived from session windows, honest about it
            Confidence = 0.8,
            Unit = "minutes",
        };
        return (CloneWithActiveMinutes(day, replacement), resolution);
    }

    /// <summary>Origin family of the sessions that supplied the minutes (mixed → the first one).</summary>
    private static DataOrigin FindSessionOrigin(IReadOnlyList<WorkoutSession> sessions) =>
        sessions.FirstOrDefault()?.Provenance.Origin ?? DataOrigin.Mock;

    /// <summary>Copy with one field replaced — the only shape this bridge may produce.</summary>
    private static NormalizedDay CloneWithActiveMinutes(NormalizedDay day, DataPoint activeMinutes) =>
        new()
        {
            Date = day.Date,
            Origin = day.Origin,
            SleepMinutes = day.SleepMinutes,
            SleepQuality = day.SleepQuality,
            SleepConsistency = day.SleepConsistency,
            BedtimeMinutesOfDay = day.BedtimeMinutesOfDay,
            WakeMinutesOfDay = day.WakeMinutesOfDay,
            Steps = day.Steps,           // NEVER touched — see class doc
            ActiveMinutes = activeMinutes,
            RecoveryScore = day.RecoveryScore,
            RestingHeartRate = day.RestingHeartRate,
            HrvMs = day.HrvMs,
            Stress = day.Stress,
            Mood = day.Mood,
            Energy = day.Energy,
        };
}
