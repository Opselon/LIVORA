using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.HealthData.Wave3bHealth;

/// <summary>
/// One mapped day plus its trace metadata. Immutable value object: mapping is a pure function of
/// (rows, origin, import time), so two calls with the same inputs are byte-identical and a test can
/// assert the whole day, provenance included, without a clock or a platform.
/// </summary>
public sealed record HealthDayMapping(
    NormalizedDay Day,
    // DayProvenance    — aggregate provenance (SourceRecordId joins every contributing record)
    // RowProvenance   — canonical metric key -> one Provenance per raw row that fed that field
    // Diagnostics     — machine tags for rejected rows; empty = nothing dropped
    Provenance DayProvenance,
    IReadOnlyDictionary<string, IReadOnlyList<Provenance>> RowProvenance,
    IReadOnlyList<string> Diagnostics);

/// <summary>Pure row -> day mapper.</summary>
public static class HealthDayMapper
{
    /// <summary>Normalized-day timestamp semantics, same convention as the sample provider: a day's values were
    /// measured across that calendar day.</summary>
    public static DateTime DayTimestamp(DateTime day) => day.Date.AddHours(12);

    /// <summary>Confidence of a summed device record. 1.0 only for Complete (measured) data.</summary>
    public const double MeasuredConfidence = 1.0;
    public const double EstimatedConfidence = 0.75;

    /// <summary>
    /// Groups rows by concept, dropping nothing: callers classify suspicious rows themselves via
    /// <see cref="HealthRawRow.IsTraceable"/>, so an untraceable record is visible in the mapping
    /// diagnostics instead of vanishing silently.
    /// </summary>
    public static IReadOnlyDictionary<HealthMetricConcept, IReadOnlyList<HealthRawRow>> GroupByConcept(
        IEnumerable<HealthRawRow> rows)
    {
        var map = new Dictionary<HealthMetricConcept, List<HealthRawRow>>();
        foreach (var c in HealthCapabilityMap.SupportedConcepts) map[c] = new List<HealthRawRow>();
        foreach (var r in rows)
        {
            if (map.TryGetValue(r.Concept, out var bucket)) bucket.Add(r);
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<HealthRawRow>)kv.Value
            .OrderBy(r => r.Day).ThenBy(r => r.SourceRecordId, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Maps every row into one mapping per calendar day, in date order. Days with no usable row at
    /// all produce no entry — the caller must not synthesize them into empty days.
    /// </summary>
    public static IReadOnlyList<HealthDayMapping> MapRange(
        IEnumerable<HealthRawRow> rows, string providerId, DateTime importedAtUtc, DataOrigin origin)
    {
        var byDay = rows
            .GroupBy(r => r.Day)
            .OrderBy(g => g.Key);
        var result = new List<HealthDayMapping>();
        foreach (var g in byDay)
        {
            var mapped = MapDay(g.Key, g.ToList(), providerId, origin, importedAtUtc);
            if (mapped is not null) result.Add(mapped);
        }
        return result;
    }

    /// <summary>
    /// Maps one day's rows. Returns null when no traceable row exists for the day (or every concept
    /// was rejected), so an absent day stays absent instead of arriving as an empty shell that a
    /// downstream rule would read as "zero sleep".
    /// </summary>
    public static HealthDayMapping? MapDay(
        DateTime day,
        IReadOnlyList<HealthRawRow> rowsForDay,
        string providerId,
        DataOrigin origin,
        DateTime importedAtUtc)
    {
        if (rowsForDay is null || rowsForDay.Count == 0) return null;

        var grouped = GroupByConcept(rowsForDay);
        var ts = DayTimestamp(day);
        var diagnostics = new List<string>();
        var rowProvenance = new Dictionary<string, IReadOnlyList<Provenance>>();
        var allRecordIds = new List<string>();
        var suppliedFields = 0;

        DataPoint? sleep = null, steps = null, active = null;
        foreach (var concept in HealthCapabilityMap.SupportedConcepts)
        {
            var bucket = grouped[concept];
            var usable = bucket.Where(r => r.IsTraceable).ToList();
            foreach (var rejected in bucket.Except(usable))
                diagnostics.Add($"row-dropped-untraceable:{HealthCapabilityMap.ConceptTag(concept)}");

            if (usable.Count == 0)
            {
                rowProvenance[HealthCapabilityMap.MetricKey(concept)] = Array.Empty<Provenance>();
                continue;
            }

            suppliedFields++;
            var sum = usable.Sum(r => r.Value);
            var estimated = usable.Any(r => r.Estimated);
            var ids = usable.Select(r => r.SourceRecordId).ToList();
            foreach (var id in ids) allRecordIds.Add(id);

            rowProvenance[HealthCapabilityMap.MetricKey(concept)] = usable
                .Select(r => new Provenance
                {
                    Source = providerId,
                    SourceRecordId = r.SourceRecordId,
                    ImportedAtUtc = importedAtUtc,
                    Origin = origin,
                })
                .ToList();

            var point = new DataPoint
            {
                Value = sum,
                Timestamp = ts,
                Origin = origin,
                Quality = estimated ? DataQuality.Estimated : DataQuality.Complete,
                Confidence = estimated ? EstimatedConfidence : MeasuredConfidence,
                Unit = UnitFor(concept),
            };
            switch (concept)
            {
                case HealthMetricConcept.SleepMinutes: sleep = point; break;
                case HealthMetricConcept.Steps: steps = point; break;
                case HealthMetricConcept.ActiveMinutes: active = point; break;
            }
        }

        if (suppliedFields == 0) return null;   // nothing traceable: no day, no fabrication

        var normalizedDay = new NormalizedDay
        {
            Date = day.Date,
            Origin = origin,
            SleepMinutes = sleep ?? DataPoint.Missing(ts, origin),
            Steps = steps ?? DataPoint.Missing(ts, origin),
            ActiveMinutes = active ?? DataPoint.Missing(ts, origin),
            // Fields Health Connect does not deliver in this wave. Missing (not zero, not derived),
            // each carrying the day's origin so provenance never reads as "mock".
            SleepQuality = DataPoint.Missing(ts, origin),
            SleepConsistency = DataPoint.Missing(ts, origin),
            BedtimeMinutesOfDay = DataPoint.Missing(ts, origin),
            WakeMinutesOfDay = DataPoint.Missing(ts, origin),
            RecoveryScore = DataPoint.Missing(ts, origin),
            Stress = DataPoint.Missing(ts, origin),
            Mood = DataPoint.Missing(ts, origin),
            Energy = DataPoint.Missing(ts, origin),
            // Nullable device-grade signals: absent, not missing-with-a-value.
            RestingHeartRate = null,
            HrvMs = null,
        };

        var dayProvenance = new Provenance
        {
            Source = providerId,
            SourceRecordId = string.Join("+", allRecordIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)),
            ImportedAtUtc = importedAtUtc,
            Origin = origin,
        };

        return new HealthDayMapping(normalizedDay, dayProvenance, rowProvenance, diagnostics);
    }

    /// <summary>Reads + maps one day straight off a bridge. Null when the platform has nothing.</summary>
    public static async Task<HealthDayMapping?> MapDayAsync(
        IHealthPlatformBridge bridge, DateTime day, DateTime importedAtUtc, CancellationToken ct = default)
    {
        var rows = await bridge.ReadRowsAsync(day.Date, day.Date.AddDays(1), ct);
        return MapDay(day.Date, rows.ToList(), bridge.ProviderId, bridge.RowOrigin, importedAtUtc);
    }

    /// <summary>Canonical unit per concept (machine-readable, matches DataNormalizer's ranges).</summary>
    public static string UnitFor(HealthMetricConcept concept) => concept switch
    {
        HealthMetricConcept.Steps => "steps",
        _ => "minutes",
    };
}
