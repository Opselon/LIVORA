using LIVORA.Application.Abstractions;

namespace LIVORA.Application.Normalization;

/// <summary>
/// Wave 3c (lane 03): the deterministic unit table. Every provider value crosses this gate before
/// it enters business logic, so the app speaks exactly one set of canonical units:
/// <list type="bullet">
///   <item>time-ish metrics (sleep minutes, active minutes, bedtime/wake, durations) → <c>minutes</c></item>
///   <item>step counters → <c>steps</c> (the count canonical)</item>
///   <item>heart rate → <c>bpm</c></item>
///   <item>HRV → <c>ms</c></item>
///   <item>scores/ratios → <c>ratio</c> (0..1)</item>
///   <item>distance → <c>meters</c></item>
///   <item>energy → <c>kcal</c></item>
/// </list>
///
/// Purity contract: no clock, no culture, no IO, no mutation of inputs. Same arguments always
/// return the same result — conversion is a lookup times an exact rational factor, so round-trips
/// (ToCanonical then ToSource) are stable within float noise (asserted &lt; 1e-9 in the suite).
///
/// Units are matched case-insensitively after trim. A unit is only ever valid inside its own
/// family: <c>min</c> converts time, never distance. Anything unmapped throws
/// <see cref="UnitConversionException"/> — adapters must catch it and surface the
/// <c>Norm.Reject.Unit</c> key, never guess a unit.
///
/// Nutrition note (documented deliberately): food labels say "Calories" (capital C) meaning
/// kilocalories; provider feeds frequently send energy as bare <c>cal</c> while meaning kcal.
/// Both <c>cal</c> and <c>kcal</c> therefore map to canonical kcal with factor 1. A feed that
/// really means small calories does not exist in Health Connect/Apple Health payloads — if one
/// ever appears, it needs its own unit token, not a silent re-factor of <c>cal</c>.
/// </summary>
public sealed class UnitConverter : IUnitConverter
{
    // Canonical unit keys — the exact strings from IUnitConverter's contract comment.
    public const string Minutes = "minutes";
    public const string Steps = "steps";
    public const string Bpm = "bpm";
    public const string Ms = "ms";
    public const string Ratio = "ratio";
    public const string Meters = "meters";
    public const string Kcal = "kcal";

    /// <summary>Exact SI mile factor. 1 mi = 1609.344 m by definition.</summary>
    public const double MetersPerMile = 1609.344;
    public const double MetersPerYard = 0.9144;
    public const double MetersPerFoot = 0.3048;
    /// <summary>Thermochemical: 1 kcal = 4.184 kJ exactly.</summary>
    public const double KilojoulesPerKcal = 4.184;

    /// <summary>
    /// unit → multiplicative factor toward the canonical unit, per family. A value is canonical
    /// when it passes through with factor 1.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, double>> Families =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Minutes] = Table(
                ("ms", 1d / 60_000d), ("msec", 1d / 60_000d), ("millisecond", 1d / 60_000d), ("milliseconds", 1d / 60_000d),
                ("s", 1d / 60d), ("sec", 1d / 60d), ("secs", 1d / 60d), ("second", 1d / 60d), ("seconds", 1d / 60d),
                ("min", 1d), ("mins", 1d), ("minute", 1d), ("minutes", 1d),
                ("h", 60d), ("hr", 60d), ("hrs", 60d), ("hour", 60d), ("hours", 60d)),
            [Steps] = Table(
                ("steps", 1d), ("step", 1d), ("count", 1d), ("counts", 1d)),
            [Bpm] = Table(
                ("bpm", 1d), ("beatsperminute", 1d), ("/min", 1d), ("count/min", 1d), ("countpermin", 1d)),
            [Ms] = Table(
                ("ms", 1d), ("msec", 1d), ("millisecond", 1d), ("milliseconds", 1d)),
            [Ratio] = Table(
                // 'score01' is the unit hint DataPoints already carry for 0..1 fields today.
                ("ratio", 1d), ("score01", 1d), ("unitless", 1d),
                ("pct", 1d / 100d), ("percent", 1d / 100d), ("%", 1d / 100d)),
            [Meters] = Table(
                ("m", 1d), ("meter", 1d), ("meters", 1d),
                ("km", 1_000d), ("kilometer", 1_000d), ("kilometers", 1_000d),
                ("mi", MetersPerMile), ("mile", MetersPerMile), ("miles", MetersPerMile),
                ("yd", MetersPerYard), ("yard", MetersPerYard), ("yards", MetersPerYard),
                ("ft", MetersPerFoot), ("foot", MetersPerFoot), ("feet", MetersPerFoot)),
            [Kcal] = Table(
                // "cal" = nutrition Calorie = kcal (see class doc). Small-calorie feeds are not a
                // thing in these providers; a real one would need its own token.
                ("cal", 1d), ("kcal", 1d),
                ("kj", 1d / KilojoulesPerKcal), ("kilojoule", 1d / KilojoulesPerKcal), ("kilojoules", 1d / KilojoulesPerKcal)),
        };

    /// <summary>metric key → canonical family. Keys mirror Domain Metrics constants plus the short
    /// aliases raw provider payloads use (so adapters can pass what they received unchanged).</summary>
    private static readonly Dictionary<string, string> MetricFamilies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Sleep / activity time → minutes
            ["sleep.minutes"] = Minutes,
            ["sleep.bedtime"] = Minutes,
            ["sleep.wake"] = Minutes,
            ["activity.minutes"] = Minutes,
            ["workout.duration"] = Minutes,
            ["sleep"] = Minutes,
            ["bedtime"] = Minutes,
            ["wake"] = Minutes,
            ["active"] = Minutes,
            ["duration"] = Minutes,
            ["time"] = Minutes,
            // Counters → steps
            ["activity.steps"] = Steps,
            ["steps"] = Steps,
            // Heart → bpm
            ["recovery.rhr"] = Bpm,
            ["rhr"] = Bpm,
            ["bpm"] = Bpm,
            // HRV → ms
            ["recovery.hrv"] = Ms,
            ["hrv"] = Ms,
            // 0..1 scores → ratio
            ["sleep.quality"] = Ratio,
            ["sleep.consistency"] = Ratio,
            ["recovery.score"] = Ratio,
            ["wellness.stress"] = Ratio,
            ["wellness.mood"] = Ratio,
            ["wellness.energy"] = Ratio,
            ["focus.estimate"] = Ratio,
            ["quality"] = Ratio,
            ["consistency"] = Ratio,
            ["recovery"] = Ratio,
            ["stress"] = Ratio,
            ["mood"] = Ratio,
            ["energy_score"] = Ratio,
            // Distance → meters
            ["distance"] = Meters,
            ["activity.distance"] = Meters,
            ["workout.distance"] = Meters,
            // Energy → kcal
            ["energy"] = Kcal,
            ["activity.energy"] = Kcal,
            ["activity.calories"] = Kcal,
            ["workout.energy"] = Kcal,
            ["workout.calories"] = Kcal,
            ["calories"] = Kcal,
        };

    public string CanonicalUnit(string metricKey)
    {
        var family = FamilyOf(metricKey);
        return family;
    }

    public double ToCanonical(string metricKey, double value, string sourceUnit)
    {
        var factor = FactorFor(metricKey, sourceUnit);
        return value * factor;
    }

    /// <summary>
    /// Inverse of <see cref="ToCanonical"/> on the concrete class (the frozen interface does not
    /// carry it). Used for display/export math and by the round-trip property tests.
    /// </summary>
    public double ToSource(string metricKey, double canonicalValue, string targetUnit)
    {
        var factor = FactorFor(metricKey, targetUnit);
        return canonicalValue / factor;
    }

    /// <summary>True when the converter can map <paramref name="sourceUnit"/> for this metric.</summary>
    public bool CanConvert(string metricKey, string sourceUnit)
    {
        if (sourceUnit is null) return false;
        var unit = sourceUnit.Trim();
        return unit.Length > 0
            && Families.TryGetValue(FamilyOf(metricKey), out var table)
            && table.ContainsKey(unit);
    }

    private static double FactorFor(string metricKey, string sourceUnit)
    {
        var family = FamilyOf(metricKey);
        var unit = (sourceUnit ?? string.Empty).Trim();
        if (unit.Length == 0 || !Families[family].TryGetValue(unit, out var factor))
            throw new UnitConversionException(metricKey, sourceUnit ?? string.Empty);
        return factor;
    }

    private static string FamilyOf(string metricKey)
    {
        var key = (metricKey ?? string.Empty).Trim();
        if (key.Length > 0 && MetricFamilies.TryGetValue(key, out var family)) return family;
        throw new UnitConversionException(metricKey ?? string.Empty, string.Empty, "unknown metric");
    }

    private static Dictionary<string, double> Table(params (string Unit, double Factor)[] rows) =>
        rows.ToDictionary(r => r.Unit, r => r.Factor, StringComparer.OrdinalIgnoreCase);
}
