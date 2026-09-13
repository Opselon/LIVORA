using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.Normalization;

// =============================================================================
// Wave 3c (lane 03) — provider raw payloads → canonical NormalizedDay.
//
// Every mapper runs the same pipeline: convert units → aggregate → validate →
// stamp provenance. A field the provider never sent ships as DataPoint.Missing
// (never a plausible-looking zero); a field that fails a physical wall is dropped
// to Missing and named by a Norm.Reject.* key. Legal-but-implausible values stay
// and carry a Norm.Warn.* key. No prose leaves this layer — keys only.
//
// Day attribution (matches SampleHealthProvider's existing semantics): the day D
// carries the night that ENDS on D — the window [D-1 12:00, D 12:00). A night
// spent 23:00 (D-1) → 06:00 (D) yields bedtime minutes-of-day 1380 and wake 360:
// the midnight wrap is expressed purely in minutes-of-day, same as every other
// producer of BedtimeMinutesOfDay in this codebase. Step/active buckets are day
// counters and clip to the calendar day [D 00:00, D+1 00:00) instead.
//
// Sleep duration = overlap-safe union of in-bed + asleep buckets: sort by start,
// merge overlaps (an asleep segment nested inside its in-bed parent counts ONCE),
// sum, cap at 1440 — a night cannot outlast its window, and a feed that claims it
// did is already rejected by the validator wall on the flat fields.
//
// Coverage is measured per window family as watched-union ÷ the family's own span (first start
// to last end): gaps INSIDE what the feed claims to have watched scale confidence, while a short
// but gapless night is not punished for hours it never claimed. A feed that only reported part of
// the truth carries Quality.Estimated with Confidence scaled by that fraction. A day built from
// flat day-summaries with no buckets at all is Complete — there is nothing to measure coverage
// against.
//
// Consistency = 1 − min(1, stdev(bedtime minutes-of-day)/120) over the caller-
// supplied history of previous nights; ≥2 samples required — one bedtime is not a
// pattern, so a single night ships Missing rather than a fake perfect 1.0.
//
// Pure: no IO, no MAUI; the only clock read is the ingest stamp, injectable via
// the ctor (Provenance.ImportedAtUtc) so tests are deterministic.
// =============================================================================

/// <summary>
/// One mapper verdict: the canonical day (null when nothing usable survived), plus the
/// reject/warn localization keys collected along the way. Emits KEYS only — the UI resolves them.
/// </summary>
public sealed record DayMapResult(
    NormalizedDay? Day,
    IReadOnlyList<string> RejectKeys,
    IReadOnlyList<string> WarnKeys,
    bool IsUsable,
    // The provenance every field of the day traces back to (null-shaped only when the
    // mapper itself failed - in practice always stamped). NormalizedDay is a frozen contract
    // without a provenance slot, so the result carries it for the ingestion log.
    Provenance? Provenance = null);

/// <summary>
/// The three provider mappers (Health Connect, Apple Health, generic import) plus the shared
/// aggregation math. Lanes 04/05 consume <see cref="MapHealthConnect"/>, <see cref="MapAppleHealth"/>
/// and <see cref="MapGeneric"/> behind this one seam.
/// </summary>
public sealed class ProviderDayMapper
{
    /// <summary>Provenance machine ids — the frozen Source strings for the three families.</summary>
    public const string HealthConnectSourceId = "healthconnect";
    public const string AppleHealthSourceId = "applehealth";
    public const string ImportSourceId = "import";

    /// <summary>Minutes in a day — the sleep cap and the coverage denominator.</summary>
    public const double MinutesPerDay = 1440;
    /// <summary>Offset from a day's midnight back to its night window's start (noon of the
    /// previous day): night for day D = [D-midnight − NightWindowOffset, D-midnight + (1440 − offset)).</summary>
    public const double NightWindowOffsetMinutes = 12 * 60;

    /// <summary>Bedtime stdev (minutes) at which consistency floors at 0.</summary>
    public const double ConsistencySpreadCap = 120;

    /// <summary>Confidence of a fully covered, device-measured bucket day.</summary>
    public const double FullCoverageConfidence = 0.95;
    /// <summary>Floor so a barely-touched day still says "we saw something".</summary>
    public const double MinPartialConfidence = 0.05;

    private readonly IUnitConverter _converter;
    private readonly PayloadValidator _validator;
    private readonly Func<DateTime> _utcNow;

    public ProviderDayMapper(IUnitConverter converter, PayloadValidator validator, Func<DateTime>? utcNow = null)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _utcNow = utcNow ?? StaticUtcNow;
    }

    private static DateTime StaticUtcNow() => DateTime.UtcNow;

    // ------------------------------------------------------------------------
    // Health Connect — sleep stage buckets + step/active buckets for one day
    // ------------------------------------------------------------------------

    /// <summary>
    /// Map day <paramref name="day"/>'s night and day-counter buckets (records outside the
    /// windows contribute nothing — the caller fetches buckets per window).
    /// <paramref name="bedtimeHistoryMinutes"/> is the canonical bedtime minutes-of-day of
    /// previous nights (the consistency input).
    /// </summary>
    public DayMapResult MapHealthConnect(
        IReadOnlyList<HealthConnectRawSample> buckets,
        DateTime day,
        IReadOnlyList<double>? bedtimeHistoryMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var ctx = new MapContext(_converter, day, DataOrigin.HealthConnect, HealthConnectSourceId);

        foreach (var b in buckets)
        {
            ctx.AddId(b.SourceRecordId);

            if (b.StepsInBucket is { } steps)
            {
                if (ctx.CheckDayWindow(b.StartLocal, b.EndLocal) is not { } seg) continue;
                if (!ctx.TryConvert("activity.steps", steps, DefaultUnit(b.Unit, "count"), out var v)) continue;
                ctx.Steps += v;
                ctx.StepsSeen = true;
                ctx.Touch(seg);
                continue;
            }
            if (b.ActiveMinutesInBucket is { } act)
            {
                if (ctx.CheckDayWindow(b.StartLocal, b.EndLocal) is not { } seg2) continue;
                if (!ctx.TryConvert("activity.minutes", act, DefaultUnit(b.Unit, "minutes"), out var v2)) continue;
                ctx.Active += v2;
                ctx.ActiveSeen = true;
                ctx.Touch(seg2);
                continue;
            }
            if (ctx.CheckNightWindow(b.StartLocal, b.EndLocal) is not { } s) continue;
            switch (b.Status)
            {
                case SleepBucketStatus.InBed:
                case SleepBucketStatus.Asleep:
                    ctx.Sleep.Add(s);
                    ctx.TouchNight(s);
                    break;
                case SleepBucketStatus.OutOfBed:
                    ctx.TouchNight(s); // the feed watched this window — coverage counts, sleep doesn't
                    break;
                default:
                    break; // unknown status: carried, never guessed into a category
            }
        }

        return ctx.Finish(_validator, bedtimeHistoryMinutes, _utcNow);
    }

    // ------------------------------------------------------------------------
    // Apple Health — quantity/category records; unit codes trusted only via converter
    // ------------------------------------------------------------------------

    /// <summary>
    /// Map day <paramref name="day"/>'s Apple Health quantity + sleep-analysis records. Every
    /// unit code goes through the converter before summation; an unmappable code rejects that
    /// record (Norm.Reject.Unit) without contaminating the rest of the day.
    /// </summary>
    public DayMapResult MapAppleHealth(
        IReadOnlyList<AppleHealthRawQuantity> records,
        DateTime day,
        IReadOnlyList<double>? bedtimeHistoryMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var ctx = new MapContext(_converter, day, DataOrigin.AppleHealth, AppleHealthSourceId);

        foreach (var r in records)
        {
            ctx.AddId(r.SourceRecordId);
            var type = r.TypeIdentifier;

            if (type.Contains("SleepAnalysis", StringComparison.OrdinalIgnoreCase)
                || type.Contains("TimeAsleep", StringComparison.OrdinalIgnoreCase)
                || type.Contains("TimeInBed", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.CheckNightWindow(r.StartLocal, r.EndLocal) is not { } seg) continue;
                var raw = r.SleepValue
                          ?? (type.Contains("TimeInBed", StringComparison.OrdinalIgnoreCase) ? "in-bed"
                           : type.Contains("TimeAsleep", StringComparison.OrdinalIgnoreCase) ? "asleep"
                           : type.Contains(':') ? type.Split(':')[^1] : type);
                switch (HealthConnectRawSample.ParseStatus(raw))
                {
                    case SleepBucketStatus.InBed:
                    case SleepBucketStatus.Asleep:
                        ctx.Sleep.Add(seg);
                        ctx.TouchNight(seg);
                        break;
                    case SleepBucketStatus.OutOfBed:
                        ctx.TouchNight(seg);
                        break;
                    default:
                        break; // unknown category value — never guessed
                }
                continue;
            }

            string metricKey = type switch
            {
                _ when type.Contains("StepCount", StringComparison.OrdinalIgnoreCase) => "activity.steps",
                _ when type.Contains("Distance", StringComparison.OrdinalIgnoreCase) => "distance",
                _ when type.Contains("EnergyBurned", StringComparison.OrdinalIgnoreCase) => "energy",
                _ when type.Contains("RestingHeartRate", StringComparison.OrdinalIgnoreCase) => "recovery.rhr",
                _ when type.Contains("HeartRateVariability", StringComparison.OrdinalIgnoreCase) => "recovery.hrv",
                _ when type.Contains("TimeInDaylight", StringComparison.OrdinalIgnoreCase) => "activity.minutes",
                _ => string.Empty, // generic HeartRate samples etc.: no daily-field slot — dropped honestly
            };
            if (metricKey.Length == 0) continue;
            if (ctx.CheckDayWindow(r.StartLocal, r.EndLocal) is not { } w) continue;
            if (!ctx.TryConvert(metricKey, r.Quantity, r.UnitCode, out var v)) continue;

            ctx.Touch(w);
            switch (metricKey)
            {
                case "activity.steps": ctx.Steps += v; ctx.StepsSeen = true; break;
                case "energy": ctx.Energy += v; break;
                case "distance": ctx.Distance += v; break; // validated; NormalizedDay has no distance slot — dropped
                case "recovery.rhr": ctx.Rhr = v; break;
                case "recovery.hrv": ctx.Hrv = v; break;
                case "activity.minutes": ctx.Active += v; ctx.ActiveSeen = true; break;
            }
        }

        return ctx.Finish(_validator, bedtimeHistoryMinutes, _utcNow);
    }

    // ------------------------------------------------------------------------
    // Generic day — the free-form import/export shape (metric → value+unit map)
    // ------------------------------------------------------------------------

    /// <summary>
    /// Map a user's own export day. Every entry is converted through the unit table; unmappable
    /// metric/unit combos reject per-field (Norm.Reject.Unit) and never get coerced. When the
    /// export also carries sleep buckets, bucket-aggregated sleep wins over a flat sleep.minutes
    /// (segments beat a summary) — coverage then reflects what the buckets actually watched.
    /// </summary>
    public DayMapResult MapGeneric(
        GenericRawDay raw,
        DateTime day,
        IReadOnlyList<double>? bedtimeHistoryMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var ctx = new MapContext(_converter, day, DataOrigin.Imported, ImportSourceId);
        ctx.AddId(raw.SourceRecordId);

        foreach (var (metric, mv) in raw.Metrics)
        {
            if (!ctx.TryConvert(metric, mv.Value, mv.Unit, out var v)) continue;
            ctx.Flat[metric] = (ctx.Flat.TryGetValue(metric, out var acc) ? acc : 0) + v;
        }

        foreach (var b in raw.SleepBuckets)
        {
            ctx.AddId(b.SourceRecordId);
            if (ctx.CheckNightWindow(b.StartLocal, b.EndLocal) is not { } seg) continue;
            ctx.TouchNight(seg);
            if (b.Status is SleepBucketStatus.InBed or SleepBucketStatus.Asleep) ctx.Sleep.Add(seg);
        }

        return ctx.Finish(_validator, bedtimeHistoryMinutes, _utcNow);
    }

    // ------------------------------------------------------------------------
    // Shared math — all pure
    // ------------------------------------------------------------------------

    /// <summary>
    /// 1 − min(1, stdev(bedtimes)/120). Needs ≥2 samples; one bedtime is not a pattern, so
    /// fewer returns null (the field ships Missing instead of a fake perfect 1.0).
    /// </summary>
    public static double? ConsistencyFromBedtimes(IReadOnlyList<double>? bedtimeMinutes)
    {
        if (bedtimeMinutes is null || bedtimeMinutes.Count < 2) return null;
        var mean = bedtimeMinutes.Average();
        var variance = bedtimeMinutes.Sum(b => (b - mean) * (b - mean)) / bedtimeMinutes.Count;
        var stdev = Math.Sqrt(variance);
        return 1 - Math.Min(1, stdev / ConsistencySpreadCap);
    }

    /// <summary>Minutes-of-day of an instant, wrapped: midnight reads as 0, not 1440.</summary>
    public static double MinutesOfDay(DateTime t)
    {
        var m = t.TimeOfDay.TotalMinutes % MinutesPerDay;
        return m < 0 ? m + MinutesPerDay : m;
    }

    /// <summary>
    /// Union length of [start,end) minute-intervals: sort by start, merge overlaps, sum, cap at
    /// 1440. This is what makes in-bed+asleep double-recording safe — nested/overlapping buckets
    /// count exactly once, and no night can outlast the day it belongs to.
    /// </summary>
    public static double MergeSum(IEnumerable<(double Start, double End)> intervals)
    {
        double total = 0;
        double curStart = double.NaN, curEnd = double.NaN;
        foreach (var (s, e) in intervals.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (!(e > s)) continue;
            if (double.IsNaN(curStart)) { curStart = s; curEnd = e; continue; }
            if (s <= curEnd) { curEnd = Math.Max(curEnd, e); continue; }
            total += curEnd - curStart;
            curStart = s; curEnd = e;
        }
        if (!double.IsNaN(curStart)) total += curEnd - curStart;
        return Math.Clamp(total, 0, MinutesPerDay);
    }

    private static string DefaultUnit(string unit, string canonical) =>
        string.IsNullOrWhiteSpace(unit) ? canonical : unit;

    /// <summary>
    /// Accumulator for one mapping call. Not thread-safe by design — it lives for exactly one
    /// Map* invocation.
    /// </summary>
    private sealed class MapContext
    {
        private readonly IUnitConverter _converter;
        private readonly DateTime _dayStart;    // D 00:00 — calendar day base
        private readonly DateTime _nightStart;  // D-1 12:00 — the night that ENDS on D

        /// <summary>Sleep segments in minutes since the night window opened (0..1440 space).</summary>
        public List<(double Start, double End)> Sleep { get; } = new();
        /// <summary>Watched night windows in night-space minutes — the sleep coverage numerator.</summary>
        public List<(double Start, double End)> TouchedNight { get; } = new();
        /// <summary>Watched day-counter windows in day-space minutes — the counter coverage numerator.</summary>
        public List<(double Start, double End)> TouchedDay { get; } = new();
        public Dictionary<string, double> Flat { get; } = new(StringComparer.OrdinalIgnoreCase);
        public double Steps, Active, Energy, Distance;
        public bool StepsSeen, ActiveSeen;
        public double? Rhr, Hrv;
        public readonly List<string> Rejects = new();
        public readonly List<string> Warns = new();
        private readonly List<string> _ids = new();

        public DataOrigin Origin { get; }
        public string SourceId { get; }

        public MapContext(IUnitConverter converter, DateTime day, DataOrigin origin, string sourceId)
        {
            _converter = converter;
            _dayStart = day.Date;
            _nightStart = _dayStart.AddMinutes(-NightWindowOffsetMinutes);
            Origin = origin;
            SourceId = sourceId;
        }

        public void AddId(string id) { if (!string.IsNullOrEmpty(id)) _ids.Add(id); }
        public void Reject(string key) { Rejects.Add(key); }

        /// <summary>Record a watched day-counter window (day space) — contributes to counter coverage.</summary>
        public void Touch((double, double) daySpaceSeg) => TouchedDay.Add(daySpaceSeg);

        /// <summary>Record a watched night window (night space) — contributes to sleep coverage.</summary>
        public void TouchNight((double, double) nightSpaceSeg) => TouchedNight.Add(nightSpaceSeg);

        /// <summary>Empty unit string = the payload is already canonical (raw feeds that carry
        /// pre-converted numbers). A unit that cannot map rejects with Norm.Reject.Unit.</summary>
        public bool TryConvert(string metricKey, double value, string unit, out double canonical)
        {
            if (string.IsNullOrWhiteSpace(unit))
            {
                canonical = value;
                return true;
            }
            try
            {
                canonical = _converter.ToCanonical(metricKey, value, unit);
                return true;
            }
            catch (UnitConversionException)
            {
                Reject("Norm.Reject.Unit");
                canonical = 0;
                return false;
            }
        }

        /// <summary>Clip to the night window [D-1 12:00, D 12:00), returned in minutes since the
        /// window opened (so 23:00 D-1 → 660). Null when fully outside; reversed = rejected.</summary>
        public (double Start, double End)? CheckNightWindow(DateTime startLocal, DateTime endLocal)
        {
            if (endLocal < startLocal) { Reject("Norm.Reject.TimeReversed"); return null; }
            var lo = _nightStart.Ticks;
            var hi = lo + (long)(MinutesPerDay * TimeSpan.TicksPerMinute);
            var s = Math.Max(startLocal.Ticks, lo);
            var e = Math.Min(endLocal.Ticks, hi);
            if (e <= s) return null; // entirely outside this night's window
            return ((s - lo) / (double)TimeSpan.TicksPerMinute,
                    (e - lo) / (double)TimeSpan.TicksPerMinute);
        }

        /// <summary>Clip to the calendar day [D 00:00, D+1 00:00) in minutes since midnight.
        /// Same reject rules as the night window.</summary>
        public (double Start, double End)? CheckDayWindow(DateTime startLocal, DateTime endLocal)
        {
            if (endLocal < startLocal) { Reject("Norm.Reject.TimeReversed"); return null; }
            var lo = _dayStart.Ticks;
            var hi = lo + (long)(MinutesPerDay * TimeSpan.TicksPerMinute);
            var s = Math.Max(startLocal.Ticks, lo);
            var e = Math.Min(endLocal.Ticks, hi);
            if (e <= s) return null;
            return ((s - lo) / (double)TimeSpan.TicksPerMinute,
                    (e - lo) / (double)TimeSpan.TicksPerMinute);
        }

        public DayMapResult Finish(
            PayloadValidator validator,
            IReadOnlyList<double>? bedtimeHistory,
            Func<DateTime> utcNow)
        {
            // Flat day-summary fields first; bucket aggregation wins where both exist.
            var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in Flat) values[k] = v;

            if (Sleep.Count > 0)
            {
                values["sleep.minutes"] = MergeSum(Sleep);
                values["sleep.bedtime"] = MinutesOfDay(_nightStart.AddMinutes(Sleep.Min(s => s.Start)));
                values["sleep.wake"] = MinutesOfDay(_nightStart.AddMinutes(Sleep.Max(s => s.End)));
            }
            if (StepsSeen) values["activity.steps"] = Steps;      // a real zero is data, not absence
            if (ActiveSeen) values["activity.minutes"] = Active;
            if (Energy > 0) values["energy"] = Energy;
            if (Distance > 0) values["distance"] = Distance;
            if (Rhr is { } r) values["recovery.rhr"] = r;
            if (Hrv is { } h) values["recovery.hrv"] = h;

            var importedAt = utcNow();
            var stamp = Stamp(importedAt);
            var bundle = new NormalizedFieldBundle
            {
                Date = _dayStart,
                Values = values,
                Provenance = stamp,
            };
            var outcome = validator.ValidateDay(bundle);
            Rejects.AddRange(outcome.RejectsByField.Values);
            Warns.AddRange(outcome.WarnKeys);

            // Coverage per window family: watched buckets ÷ the family's own span (first start →
            // last end). Gaps inside what the feed claims to have watched scale the day's
            // confidence; a short but gapless night is not punished for the hours it never
            // claimed. No watched windows in a family → nothing to measure → Complete there.
            double sleepCoverage = SpanCoverage(TouchedNight);
            double counterCoverage = SpanCoverage(TouchedDay);

            // Rejected fields drop to Missing; whatever survived still ships.
            var survivors = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in values)
                if (!outcome.RejectsByField.ContainsKey(k)) survivors[k] = v;

            var usable = survivors.Values.Any(v => v is { } d && !double.IsNaN(d));
            var day = usable ? BuildDay(survivors, bedtimeHistory, sleepCoverage, counterCoverage) : null;
            return new DayMapResult(
                day,
                Rejects.Distinct(StringComparer.Ordinal).ToList(),
                Warns.Distinct(StringComparer.Ordinal).ToList(),
                usable,
                stamp);
        }

        /// <summary>watched union ÷ span, 1.0 when there is nothing watched to measure.</summary>
        private static double SpanCoverage(List<(double Start, double End)> touched)
        {
            if (touched.Count == 0) return 1.0;
            var span = touched.Max(t => t.End) - touched.Min(t => t.Start);
            if (span <= 0) return 1.0;
            return Math.Clamp(MergeSum(touched) / span, 0, 1);
        }

        private Provenance Stamp(DateTime importedAt) => new()
        {
            Source = SourceId,
            SourceRecordId = string.Join("|", _ids.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal)),
            ImportedAtUtc = importedAt,
            Origin = Origin,
        };

        private NormalizedDay BuildDay(
            Dictionary<string, double?> values,
            IReadOnlyList<double>? bedtimeHistory,
            double sleepCoverage,
            double counterCoverage)
        {
            var ts = _dayStart.AddHours(12); // normalized-day timestamp semantics (see SampleHealthProvider)

            DataPoint ForCoverage(double coverage, string key, string unit)
            {
                if (!values.TryGetValue(key, out var v) || v is null || double.IsNaN(v.Value))
                    return DataPoint.Missing(ts, Origin);
                var partial = coverage < 1;
                return new DataPoint
                {
                    Value = v.Value, Timestamp = ts, Origin = Origin,
                    Quality = partial ? DataQuality.Estimated : DataQuality.Complete,
                    Confidence = partial
                        ? Math.Round(Math.Clamp(FullCoverageConfidence * coverage, MinPartialConfidence, FullCoverageConfidence), 4)
                        : FullCoverageConfidence,
                    Unit = unit,
                };
            }

            // Sleep-family fields scale by night coverage; counters by day coverage.
            DataPoint Sleep(string key, string unit) => ForCoverage(sleepCoverage, key, unit);
            DataPoint Counter(string key, string unit) => ForCoverage(counterCoverage, key, unit);

            DataPoint? Optional(string key, string unit, double coverage) =>
                values.TryGetValue(key, out var v) && v is { } d && !double.IsNaN(d)
                    ? ForCoverage(coverage, key, unit)
                    : null;

            var consistency = ConsistencyFromBedtimes(bedtimeHistory);

            return new NormalizedDay
            {
                Date = _dayStart,
                Origin = Origin,
                SleepMinutes = Sleep("sleep.minutes", "minutes"),
                // A bucket feed never reports a 0..1 sleep-quality score; inventing one would be
                // fabrication, so it stays Missing until a provider actually delivers it.
                SleepQuality = Sleep("sleep.quality", "score01"),
                SleepConsistency = consistency is { } c
                    ? new DataPoint { Value = c, Timestamp = ts, Origin = Origin, Quality = DataQuality.Estimated, Confidence = 0.8, Unit = "score01" }
                    : DataPoint.Missing(ts, Origin),
                BedtimeMinutesOfDay = Sleep("sleep.bedtime", "minutesOfDay"),
                WakeMinutesOfDay = Sleep("sleep.wake", "minutesOfDay"),
                Steps = Counter("activity.steps", "steps"),
                ActiveMinutes = Counter("activity.minutes", "minutes"),
                RecoveryScore = Sleep("recovery.score", "score01"),
                RestingHeartRate = Optional("recovery.rhr", "bpm", counterCoverage),
                HrvMs = Optional("recovery.hrv", "ms", counterCoverage),
                Stress = Sleep("wellness.stress", "score01"),
                Mood = Sleep("wellness.mood", "score01"),
                Energy = Sleep("wellness.energy", "score01"),
            };
        }
    }
}
