using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.Activities;

/// <summary>
/// Wave 3b (lane 03): what one calendar day of workout sessions adds up to. Unknowns are null,
/// never 0 — a day of gym sessions with no distance reported has DistanceMeters == null, which
/// the UI must render as "no data", not as "0 m".
/// </summary>
public sealed record DailyWorkoutSummary
{
    /// <summary>Calendar day the summary covers (midnight-local, like NormalizedDay.Date).</summary>
    public required DateTime Date { get; init; }
    /// <summary>Sum of usable session durations (minutes, 0 when the day has no sessions).</summary>
    public required double TotalMinutes { get; init; }
    /// <summary>Sum of reported distances, or null when NO session reported one.</summary>
    public double? DistanceMeters { get; init; }
    /// <summary>Sum of reported/estimated energy, or null when NO session reported one.</summary>
    public double? EnergyKcal { get; init; }
    /// <summary>True when at least one session that contributed to <see cref="EnergyKcal"/> was
    /// WorkoutQuality.Estimated — the UI must then present the kcal figure as an estimate.</summary>
    public bool EnergyWasEstimated { get; init; }
    /// <summary>How many sessions were counted (sample count travels with every number — §0.5).</summary>
    public required int SessionCount { get; init; }
    /// <summary>Type with the most minutes; null when the day is empty. Ties break by first seen.</summary>
    public WorkoutType? DominantType { get; init; }
    /// <summary>Single origin when every counted session shares one; null when the day mixes sources.</summary>
    public DataOrigin? Origin { get; init; }
}

/// <summary>
/// Wave 3b (lane 03): pure aggregation from raw <see cref="WorkoutSession"/> records to one
/// <see cref="DailyWorkoutSummary"/> per calendar day. No IO, no clock, no MAUI — the grouping
/// key is StartLocal.Date, so a session belongs to the day it started (documented convention;
/// an overnight session counts to its start day, same as bedtime attribution elsewhere).
///
/// Honesty rules encoded here (§0.5, no fabricated analytics):
/// <list type="bullet">
///   <item>Sessions with a non-positive or NaN duration are NOT counted (a broken record must not
///     inflate a day).</item>
///   <item>Distance/energy sum only the sessions that actually reported them, and go null (never 0)
///     when none did.</item>
///   <item>Energy from Estimated-quality sessions still sums, but flips EnergyWasEstimated so the
///     blended figure is disclosed as partly estimated. Suspect-quality energy never contributes.</item>
///   <item>Suspect duration still counts as active minutes (the session happened; its extras are
///     what is untrustworthy).</item>
/// </list>
/// </summary>
public static class WorkoutAggregator
{
    /// <summary>Sum a single day's sessions. Null/empty input yields an honest zero-count summary.</summary>
    public static DailyWorkoutSummary Summarize(DateTime date, IReadOnlyList<WorkoutSession>? sessions)
    {
        var day = date.Date;
        if (sessions is null || sessions.Count == 0)
        {
            return new DailyWorkoutSummary
            {
                Date = day, TotalMinutes = 0, SessionCount = 0,
                DistanceMeters = null, EnergyKcal = null, EnergyWasEstimated = false,
                DominantType = null, Origin = null,
            };
        }

        double totalMinutes = 0;
        double? distance = null;
        double? energy = null;
        bool energyEstimated = false;
        int counted = 0;
        DataOrigin? origin = null;
        bool mixedOrigin = false;
        var minutesByType = new Dictionary<WorkoutType, double>();
        var typeOrder = new List<WorkoutType>();

        foreach (var s in sessions)
        {
            double minutes = s.Duration.TotalMinutes;
            if (double.IsNaN(minutes) || minutes <= 0) continue; // broken record — skip, don't launder

            counted++;
            totalMinutes += minutes;

            if (s.DistanceMeters is { } d && !double.IsNaN(d) && d > 0)
                distance = (distance ?? 0) + d;

            if (s.Quality != WorkoutQuality.Suspect &&
                s.EnergyKcal is { } e && !double.IsNaN(e) && e > 0)
            {
                energy = (energy ?? 0) + e;
                if (s.Quality == WorkoutQuality.Estimated) energyEstimated = true;
            }

            if (origin is null) origin = s.Provenance.Origin;
            else if (origin != s.Provenance.Origin) mixedOrigin = true;

            if (!minutesByType.TryGetValue(s.Type, out var m))
            {
                minutesByType[s.Type] = minutes;
                typeOrder.Add(s.Type);
            }
            else minutesByType[s.Type] = m + minutes;
        }

        WorkoutType? dominant = null;
        if (counted > 0)
        {
            double best = -1;
            foreach (var t in typeOrder) // first-seen wins ties (deterministic on insertion order)
            {
                if (minutesByType[t] > best) { best = minutesByType[t]; dominant = t; }
            }
        }

        return new DailyWorkoutSummary
        {
            Date = day,
            TotalMinutes = totalMinutes,
            DistanceMeters = distance is { } dm ? Math.Round(dm) : null,
            EnergyKcal = energy is { } ek ? Math.Round(ek) : null,
            EnergyWasEstimated = energyEstimated,
            SessionCount = counted,
            DominantType = dominant,
            Origin = mixedOrigin ? null : origin,
        };
    }

    /// <summary>Group sessions by their start-local calendar day and summarize each group.
    /// Empty days in the range are absent — callers fill "nothing" honestly.</summary>
    public static IReadOnlyDictionary<DateTime, DailyWorkoutSummary> SummarizeByDay(
        IEnumerable<WorkoutSession>? sessions)
    {
        var result = new SortedDictionary<DateTime, DailyWorkoutSummary>();
        if (sessions is null) return result;

        var groups = new Dictionary<DateTime, List<WorkoutSession>>();
        foreach (var s in sessions)
        {
            if (s.StartLocal == default) continue; // no timestamp -> no calendar day -> ungroupable
            var day = s.StartLocal.Date;
            if (!groups.TryGetValue(day, out var list)) groups[day] = list = new List<WorkoutSession>();
            list.Add(s);
        }

        foreach (var (day, list) in groups) result[day] = Summarize(day, list);
        return result;
    }
}
