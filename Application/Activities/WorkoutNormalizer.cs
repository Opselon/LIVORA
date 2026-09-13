using LIVORA.Application.Abstractions;
using LIVORA.Application.Normalization;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.Activities;

// =============================================================================
// Wave 3c (lane 03) — the workout normalization engine.
//
// Raw session (whatever shape the feed used) → canonical WorkoutSession:
//   alias→WorkoutType mapping, unit conversion for distance/energy, MET-based energy
//   estimation when the feed gave none, and dedupe. Rejects are localization KEYS
//   (Activity.Reject.* / Norm.Reject.*) — never prose.
//
// MET table is a fixed published constant per activity family (compendium of
// physical activity values, rounded); kcal = MET × hours × kg at the session's
// weight. Estimated energy is marked WorkoutQuality.Estimated — an estimate is
// labeled as one everywhere downstream.
// =============================================================================

/// <summary>
/// One raw workout record as a provider/export hands it over. Units are strings because feeds
/// disagree (km vs mi, cal vs kcal); empty unit = the value is already canonical (meters/kcal).
/// </summary>
public sealed record RawWorkoutSession
{
    /// <summary>Provider's own activity label ("walk", "HKWorkoutActivityTypeRunning", ...).</summary>
    public required string TypeAlias { get; init; }
    public required DateTime StartLocal { get; init; }
    public required DateTime EndLocal { get; init; }
    /// <summary>Distance in <see cref="DistanceUnit"/> (null = the source has no distance — honest unknown).</summary>
    public double? Distance { get; init; }
    public string DistanceUnit { get; init; } = string.Empty;
    /// <summary>Energy in <see cref="EnergyUnit"/> (null = not reported → MET estimate kicks in).</summary>
    public double? Energy { get; init; }
    public string EnergyUnit { get; init; } = string.Empty;
    /// <summary>0..1 relative intensity when the source exposes one.</summary>
    public double? Intensity { get; init; }
    /// <summary>Stable id inside the source; empty = source has none (dedupe falls back to time).</summary>
    public string SourceId { get; init; } = string.Empty;
    public DataOrigin Origin { get; init; } = DataOrigin.HealthConnect;
    /// <summary>User body mass for MET math. Null → default (70 kg) with the Estimated flag set.</summary>
    public double? WeightKg { get; init; }
}

/// <summary>Verdict for one raw session: session (null when rejected) + reject keys + flags.</summary>
public sealed record WorkoutMapResult(
    WorkoutSession? Session,
    IReadOnlyList<string> RejectKeys,
    bool UsedDefaultWeight);

/// <summary>Verdict of a dedupe pass: survivors + how many duplicates were folded away.</summary>
public sealed record WorkoutDedupeResult(IReadOnlyList<WorkoutSession> Sessions, int DuplicatesRemoved);

/// <summary>
/// The pure session normalizer. Same input → same WorkoutSession, byte for byte: no clock,
/// no culture-dependent parsing, no IO. The ingest timestamp is injected as an argument.
/// </summary>
public static class WorkoutNormalizer
{
    /// <summary>Body mass assumed when the user's weight is unknown. Documented, flagged Estimated.</summary>
    public const double DefaultWeightKg = 70;

    /// <summary>Dedupe window: same family + starts within this many seconds = same session record.</summary>
    public const int DedupeWindowSeconds = 60;

    /// <summary>MET (metabolic equivalent) per canonical family — fixed published constants.</summary>
    public static IReadOnlyDictionary<WorkoutType, double> MetTable { get; } =
        new Dictionary<WorkoutType, double>
        {
            [WorkoutType.Walk] = 3.5,
            [WorkoutType.Run] = 9.8,
            [WorkoutType.Cycle] = 7.5,
            [WorkoutType.Gym] = 5.0,
            [WorkoutType.Sport] = 8.0,
            [WorkoutType.Mobility] = 3.0,
            // Other: no defensible single MET — energy stays null instead of a guessed number.
        };

    /// <summary>Alias → family. Matched case-insensitively after trim; substrings for provider-prefixed ids.</summary>
    private static readonly (string Alias, WorkoutType Type)[] AliasMap =
    {
        ("walk", WorkoutType.Walk),
        ("walking", WorkoutType.Walk),
        ("run", WorkoutType.Run),
        ("jog", WorkoutType.Run),
        ("cycle", WorkoutType.Cycle),
        ("cycling", WorkoutType.Cycle),
        ("bike", WorkoutType.Cycle),
        ("biking", WorkoutType.Cycle),
        ("spinning", WorkoutType.Cycle),
        ("gym", WorkoutType.Gym),
        ("strength", WorkoutType.Gym),
        ("lifts", WorkoutType.Gym),
        ("weighttraining", WorkoutType.Gym),
        ("sport", WorkoutType.Sport),
        ("football", WorkoutType.Sport),
        ("soccer", WorkoutType.Sport),
        ("basketball", WorkoutType.Sport),
        ("tennis", WorkoutType.Sport),
        ("yoga", WorkoutType.Mobility),
        ("mobility", WorkoutType.Mobility),
        ("stretch", WorkoutType.Mobility),
        ("stretching", WorkoutType.Mobility),
    };

    /// <summary>The canonical family for a provider alias. Unknown → Other (never a wrong guess).</summary>
    public static WorkoutType MapType(string? typeAlias)
    {
        var a = (typeAlias ?? string.Empty).Trim().ToLowerInvariant();
        if (a.Length == 0) return WorkoutType.Other;
        foreach (var (alias, type) in AliasMap)
            if (a == alias || a.EndsWith(alias, StringComparison.Ordinal) ||
                a.Contains(alias, StringComparison.Ordinal))
                return type;
        return WorkoutType.Other;
    }

    /// <summary>kcal = MET × hours × kg. Null when the family has no published constant (Other).</summary>
    public static double? EstimateEnergyKcal(WorkoutType type, TimeSpan duration, double weightKg)
    {
        if (!MetTable.TryGetValue(type, out var met)) return null;
        var hours = duration.TotalMinutes / 60.0;
        if (hours <= 0) return null;
        return met * hours * weightKg;
    }

    /// <summary>
    /// Normalize one raw record. Rules, in order:
    /// 1) end must be after start (non-positive duration → <c>Activity.Reject.Duration</c>, session dropped);
    /// 2) distance/energy convert through the unit table (bad unit → reject key, field dropped to null —
    ///    unknown stays unknown, it is never zero);
    /// 3) energy: reported wins; otherwise MET estimate (quality Estimated);
    /// 4) weight: record's own → caller's override → 70 default (UsedDefaultWeight + Estimated).
    /// </summary>
    public static WorkoutMapResult Normalize(
        RawWorkoutSession raw,
        IUnitConverter converter,
        DateTime importedAtUtc,
        double? weightKgOverride = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(converter);
        var rejects = new List<string>();

        if (raw.EndLocal <= raw.StartLocal)
        {
            rejects.Add("Activity.Reject.Duration");
            return new WorkoutMapResult(null, rejects, false);
        }

        var weightKg = raw.WeightKg ?? weightKgOverride;
        var usedDefaultWeight = false;
        if (weightKg is null or <= 0)
        {
            weightKg = DefaultWeightKg;
            usedDefaultWeight = true;
        }

        var type = MapType(raw.TypeAlias);
        double? distanceMeters = null;
        double? energyKcal = null;
        var estimated = false;

        if (raw.Distance is { } d)
        {
            distanceMeters = ConvertField(converter, "distance", d, raw.DistanceUnit, rejects);
            if (distanceMeters is { } dm && dm < 0)
            {
                rejects.Add("Norm.Reject.Negative");
                distanceMeters = null;
            }
        }
        if (raw.Energy is { } e)
        {
            energyKcal = ConvertField(converter, "energy", e, raw.EnergyUnit, rejects);
            if (energyKcal is { } ek)
            {
                if (ek < 0) { rejects.Add("Norm.Reject.Negative"); energyKcal = null; }
                else if (double.IsNaN(ek) || double.IsInfinity(ek))
                { rejects.Add("Norm.Reject.NotFinite"); energyKcal = null; }
            }
        }
        if (energyKcal is null)
        {
            var est = EstimateEnergyKcal(type, raw.EndLocal - raw.StartLocal, weightKg.Value);
            if (est is { } metKcal && !double.IsNaN(metKcal) && !double.IsInfinity(metKcal))
            {
                energyKcal = metKcal;
                estimated = true;
            }
        }

        double? intensity = raw.Intensity is { } i && !double.IsNaN(i) && i >= 0 && i <= 1 ? i : null;
        if (raw.Intensity is { } ri && intensity is null && !double.IsNaN(ri))
            rejects.Add("Norm.Reject.Ratio");

        var quality = estimated || usedDefaultWeight
            ? WorkoutQuality.Estimated
            : (distanceMeters is null && raw.Distance is not null) || (energyKcal is null && raw.Energy is not null)
                ? WorkoutQuality.Partial   // a reported field was dropped by validation
                : WorkoutQuality.Complete;

        var session = new WorkoutSession
        {
            Id = raw.SourceId.Length > 0
                ? raw.SourceId
                : $"w-{raw.StartLocal.Ticks:x}-{type.ToString().ToLowerInvariant()}",
            Type = type,
            StartLocal = raw.StartLocal,
            EndLocal = raw.EndLocal,
            DistanceMeters = distanceMeters,
            EnergyKcal = energyKcal,
            Intensity = intensity,
            Quality = quality,
            Provenance = new Provenance
            {
                Source = OriginMachineId(raw.Origin),
                SourceRecordId = raw.SourceId,
                ImportedAtUtc = importedAtUtc,
                Origin = raw.Origin,
            },
        };
        return new WorkoutMapResult(session, rejects, usedDefaultWeight);
    }

    private static double? ConvertField(
        IUnitConverter converter, string metricKey, double value, string unit, List<string> rejects)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
        try
        {
            var v = converter.ToCanonical(metricKey, value, unit);
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                rejects.Add("Norm.Reject.NotFinite");
                return null;
            }
            return v;
        }
        catch (UnitConversionException)
        {
            rejects.Add("Activity.Reject.Unit");
            return null;
        }
    }

    /// <summary>
    /// Fold duplicate records: same non-empty sourceId, OR same canonical type with start times
    /// within <see cref="DedupeWindowSeconds"/>. The richer record wins (more real fields);
    /// ties keep the earlier start, then the first seen — fully deterministic.
    /// </summary>
    public static WorkoutDedupeResult Dedupe(IEnumerable<WorkoutSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var ordered = sessions
            .OrderBy(s => s.StartLocal.Ticks)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        var survivors = new List<WorkoutSession>();
        var removed = 0;

        foreach (var candidate in ordered)
        {
            var clashIndex = survivors.FindIndex(s => IsDuplicateOf(s, candidate));
            if (clashIndex < 0) { survivors.Add(candidate); continue; }
            var incumbent = survivors[clashIndex];
            survivors[clashIndex] = Richness(candidate) > Richness(incumbent) ? candidate : incumbent;
            removed++;
        }
        return new WorkoutDedupeResult(survivors, removed);
    }

    /// <summary>True when two already-normalized sessions describe the same activity.</summary>
    public static bool IsDuplicateOf(WorkoutSession a, WorkoutSession b)
    {
        if (!string.IsNullOrEmpty(a.Provenance.SourceRecordId) &&
            string.Equals(a.Provenance.SourceRecordId, b.Provenance.SourceRecordId, StringComparison.Ordinal))
            return true;
        if (a.Type != b.Type) return false;
        var delta = Math.Abs((a.StartLocal - b.StartLocal).TotalSeconds);
        return delta <= DedupeWindowSeconds;
    }

    /// <summary>How much real content a session carries. Deterministic, no float ranking noise.</summary>
    internal static int Richness(WorkoutSession s) =>
        (s.DistanceMeters is not null ? 1 : 0) +
        (s.EnergyKcal is not null ? 1 : 0) +
        (s.Intensity is not null ? 1 : 0) +
        (s.Quality == WorkoutQuality.Complete ? 1 : 0);

    /// <summary>Machine id stamped into Provenance.Source for an origin family.</summary>
    public static string OriginMachineId(DataOrigin origin) => origin switch
    {
        DataOrigin.HealthConnect => "healthconnect",
        DataOrigin.AppleHealth => "applehealth",
        DataOrigin.Imported => "import",
        DataOrigin.Garmin => "garmin",
        DataOrigin.Fitbit => "fitbit",
        DataOrigin.Manual => "manual",
        _ => "unknown",
    };
}
