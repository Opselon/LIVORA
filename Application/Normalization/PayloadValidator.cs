using LIVORA.Application.Abstractions;

namespace LIVORA.Application.Normalization;

// =============================================================================
// Wave 3c (lane 03) — payload validation at the provider edge.
//
// Physical ranges, on CANONICAL values (mappers convert units first, then validate):
//   sleep minutes 0..1440 · bedtime/wake 0..1439 · steps 0..200_000 ·
//   active 0..1440 · bpm 20..250 · hrv 1..500 · scores/ratios 0..1.
// NaN / ±Infinity are never data. Negative values are never data.
// Cross-field: a window's endLocal must be after or equal to startLocal.
//
// Everything beyond those walls is PLAUSIBILITY, not physics: 150k steps in a day
// is under the wall but almost certainly a duplicated feed. Those come back as
// warn-level keys (Norm.Warn.*) — the day still ships and baseline math keeps the
// value; only the physical walls produce hard rejects (Norm.Reject.*).
//
// Output contract (product law): KEYS ONLY, never prose. The UI resolves them
// through AppResources; tests assert the key, not a sentence.
// =============================================================================

/// <summary>Full validator verdict: per-field hard rejects plus warn-level plausibility flags.</summary>
public sealed record ValidationOutcome(
    IReadOnlyDictionary<string, string> RejectsByField,
    IReadOnlyList<string> WarnKeys)
{
    public static ValidationOutcome Clean { get; } =
        new(new Dictionary<string, string>(), Array.Empty<string>());

    /// <summary>No field rejected = the payload may proceed to normalization unchanged.</summary>
    public bool IsValid => RejectsByField.Count == 0;

    /// <summary>Flat reject-key list (deduplicated, stable order) for interface/keys consumers.</summary>
    public IReadOnlyList<string> RejectKeys =>
        RejectsByField.Values.Distinct(StringComparer.Ordinal).ToList();

    public static ValidationOutcome Combine(IEnumerable<ValidationOutcome> outcomes)
    {
        var rejects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warns = new List<string>();
        foreach (var o in outcomes)
        {
            foreach (var (k, v) in o.RejectsByField) rejects[k] = v;
            warns.AddRange(o.WarnKeys);
        }
        return new ValidationOutcome(rejects, warns);
    }
}

/// <summary>
/// The provider-edge gate. Pure: same bundle in → same verdict out, no clock, no IO.
/// Metric keys follow <c>Domain.Models.State.Metrics</c> plus the short aliases the raw
/// payloads use; unknown keys are not checked here (the converter rejects those).
/// </summary>
public sealed class PayloadValidator : IPayloadValidator
{
    // ---- physical walls (from the Wave 3c spec sheet) ----------------------
    public const double SleepMinutesMax = 1440;      // a 36h feed is broken, not a nap
    public const double TimeOfDayMax = 1439;         // minutes-of-day: 0..23:59
    public const double StepsMax = 200_000;          // world-record-adjacent; beyond = counter bug
    public const double ActiveMinutesMax = 1440;     // can't be active more minutes than the day
    public const double BpmMin = 20, BpmMax = 250;   // living-human bracket
    public const double HrvMin = 1, HrvMax = 500;    // ms; SDNN beyond this is an artifact
    public const double RatioMin = 0, RatioMax = 1;  // scores are 0..1 fractions by definition
    /// <summary>Physical ceiling for one day of distance: 200 km (an ultra + change).</summary>
    public const double DistanceMaxMeters = 200_000;
    /// <summary>Physical ceiling for one day of exercise energy: 20 000 kcal.</summary>
    public const double EnergyMaxKcal = 20_000;

    // ---- plausibility thresholds (warn, never reject) -----------------------
    public const double StepsPlausibleMax = 120_000; // above this: probably double-counted
    public const double SleepPlausibleMax = 1200;    // 20h: under the wall, still implausible
    public const double RhrPlausibleMax = 150;       // resting HR at workout ceiling = suspicious
    public const double HrvPlausibleMax = 300;       // chronic SDNN above this is rare/device noise

    /// <summary>
    /// Interface seam: reject keys for the sleep family only (empty list = payload valid).
    /// Use <see cref="ValidateDay"/> for the full per-field verdict including warns.
    /// </summary>
    public IReadOnlyList<string> ValidateSleep(NormalizedFieldBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var outcome = ValidateFields(
            bundle.Values.Where(kv => kv.Key.StartsWith("sleep.", StringComparison.OrdinalIgnoreCase)));
        return outcome.RejectKeys;
    }

    /// <summary>Full per-field verdict: every known metric in the bundle, rejects + warns.</summary>
    public ValidationOutcome ValidateDay(NormalizedFieldBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return ValidateFields(bundle.Values);
    }

    /// <summary>
    /// Cross-field time rule shared by day mapping and activity normalization:
    /// end must be after or equal to start. Equal (instantaneous bucket) is legal.
    /// </summary>
    public static string? ValidateWindow(DateTime startLocal, DateTime endLocal) =>
        endLocal < startLocal ? "Norm.Reject.TimeReversed" : null;

    /// <summary>Plausibility warnings for one canonical value. Never returns reject keys.</summary>
    public static ValidationOutcome Plausibility(string metricKey, double canonicalValue)
    {
        var warns = new List<string>();
        switch (metricKey.ToLowerInvariant())
        {
            case "activity.steps" when canonicalValue > StepsPlausibleMax:
                warns.Add("Norm.Warn.StepsHigh");
                break;
            case "sleep.minutes" when canonicalValue > SleepPlausibleMax:
                warns.Add("Norm.Warn.SleepHigh");
                break;
            case "recovery.rhr" when canonicalValue > RhrPlausibleMax:
                warns.Add("Norm.Warn.RhrHigh");
                break;
            case "recovery.hrv" when canonicalValue > HrvPlausibleMax:
                warns.Add("Norm.Warn.HrvHigh");
                break;
        }
        return new ValidationOutcome(new Dictionary<string, string>(), warns);
    }

    private static ValidationOutcome ValidateFields(IEnumerable<KeyValuePair<string, double?>> fields)
    {
        var rejects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warns = new List<string>();

        foreach (var (key, raw) in fields)
        {
            // null = the provider genuinely has nothing for this field — honest, not invalid.
            if (raw is not { } v) continue;
            if (!WallFor(key, out var min, out var max, out var rejectKey))
                continue; // unknown key: not this validator's business (converter rejects it)

            // 1. finite, or it is not a measurement at all
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                rejects[key] = "Norm.Reject.NotFinite";
                continue;
            }
            // 2. negative is never a counter, a sleep length or a score — even where a wall's
            //    minimum is positive, Negative names the real problem.
            if (v < 0)
            {
                rejects[key] = "Norm.Reject.Negative";
                continue;
            }
            // 3. the physical wall
            if (v < min || v > max)
            {
                rejects[key] = rejectKey;
                continue;
            }

            // 4. legal but implausible → warn key; the value stays usable downstream
            warns.AddRange(Plausibility(key, v).WarnKeys);
        }

        return new ValidationOutcome(rejects, warns);
    }

    /// <summary>The physical wall for a metric key + its reject key; false for unknown keys.</summary>
    private static bool WallFor(string key, out double min, out double max, out string rejectKey)
    {
        (min, max, rejectKey) = key.ToLowerInvariant() switch
        {
            "sleep.minutes" => (0, SleepMinutesMax, "Norm.Reject.Sleep"),
            "sleep.bedtime" => (0, TimeOfDayMax, "Norm.Reject.Bedtime"),
            "sleep.wake" => (0, TimeOfDayMax, "Norm.Reject.Wake"),
            "activity.steps" or "steps" => (0, StepsMax, "Norm.Reject.Steps"),
            "activity.minutes" => (0, ActiveMinutesMax, "Norm.Reject.Active"),
            "recovery.rhr" or "bpm" => (BpmMin, BpmMax, "Norm.Reject.Bpm"),
            "recovery.hrv" or "hrv" => (HrvMin, HrvMax, "Norm.Reject.Hrv"),
            "sleep.quality" => (RatioMin, RatioMax, "Norm.Reject.Quality"),
            "sleep.consistency" => (RatioMin, RatioMax, "Norm.Reject.Consistency"),
            "recovery.score" => (RatioMin, RatioMax, "Norm.Reject.Recovery"),
            "wellness.stress" or "wellness.mood" or "wellness.energy" or "focus.estimate"
                => (RatioMin, RatioMax, "Norm.Reject.Ratio"),
            // Distance/energy ceilings are generous physical walls so Infinity/absurd feeds
            // die here instead of poisoning baselines downstream.
            "distance" or "activity.distance" or "workout.distance"
                => (0, DistanceMaxMeters, "Norm.Reject.Distance"),
            "energy" or "activity.energy" or "activity.calories" or "workout.energy"
                or "workout.calories" or "calories"
                => (0, EnergyMaxKcal, "Norm.Reject.Energy"),
            _ => (0, 0, string.Empty),
        };
        return rejectKey.Length > 0;
    }
}
