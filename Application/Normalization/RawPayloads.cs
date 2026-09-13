namespace LIVORA.Application.Normalization;

// =============================================================================
// Wave 3c (lane 03) — raw payload shapes, frozen at the adapter edge.
//
// These mirror what the real feeds hand over, field names and all. They are
// deliberately untyped-ish (strings for units/statuses): the whole point of the
// mapper is to turn "unknown to LIVORA" into "canonical NormalizedDay + reject
// keys" — so the raw side must be able to carry nonsense (a 36h sleep bucket,
// a unit of "L") without the model refusing to hold it.
//
// No logic lives here. Validation/conversion is ProviderDayMapper's job.
// =============================================================================

/// <summary>Health Connect status bucket kinds (the subset LIVORA consumes).</summary>
public enum SleepBucketStatus
{
    Unknown = 0,
    /// <summary>Recorded "in bed" — presence, not necessarily sleep.</summary>
    InBed,
    /// <summary>Recorded "out of bed".</summary>
    OutOfBed,
    /// <summary>Recorded asleep (device-sleep-stage composite).</summary>
    Asleep,
}

/// <summary>
/// One Health Connect sleep-stages bucket: [StartUtc, EndUtc) with a status. Steps arrive as
/// a separate per-bucket counter record (same shape family — bucket start/end + a count).
/// </summary>
public sealed record HealthConnectRawSample
{
    /// <summary>Bucket start (local clock as delivered by the device feed).</summary>
    public required DateTime StartLocal { get; init; }
    /// <summary>Bucket end — may equal start for instantaneous records.</summary>
    public required DateTime EndLocal { get; init; }
    /// <summary>"in-bed" / "out-of-bed" / "asleep" (raw record; Unknown when unparseable).</summary>
    public SleepBucketStatus Status { get; init; } = SleepBucketStatus.Unknown;
    /// <summary>Steps recorded inside this bucket (null = not a steps record).</summary>
    public double? StepsInBucket { get; init; }
    /// <summary>Active minutes recorded inside this bucket (null = not an activity record).</summary>
    public double? ActiveMinutesInBucket { get; init; }
    /// <summary>Stable record id inside Health Connect, when known.</summary>
    public string SourceRecordId { get; init; } = string.Empty;
    /// <summary>Unit the bucket value arrived in ("" = canonical default for the field: minutes/count).</summary>
    public string Unit { get; init; } = string.Empty;

    public static SleepBucketStatus ParseStatus(string? raw)
    {
        var s = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return SleepBucketStatus.Unknown;
        // exact tokens first, then Apple's value-suffix style ("HKCategoryValueSleepAnalysisInBed")
        switch (s)
        {
            case "in-bed": case "inbed": case "in_bed": return SleepBucketStatus.InBed;
            case "out-of-bed": case "outofbed": case "out_of_bed": return SleepBucketStatus.OutOfBed;
            case "asleep": case "sleep": case "sleeping": return SleepBucketStatus.Asleep;
        }
        var letters = new string(s.Where(char.IsLetterOrDigit).ToArray());
        if (letters.Contains("outofbed", StringComparison.Ordinal)) return SleepBucketStatus.OutOfBed;
        if (letters.Contains("inbed", StringComparison.Ordinal)) return SleepBucketStatus.InBed;
        if (letters.Contains("asleep", StringComparison.Ordinal)) return SleepBucketStatus.Asleep;
        return SleepBucketStatus.Unknown;
    }
}

/// <summary>
/// One Apple Health quantity/category record. Apple ships unit strings on the record itself
/// ("min", "count", "ms", "count/min", "kcal", "km", ...), so the mapper must convert before
/// it validates — a "3.2 h" sleep record is 192 minutes, not 3.2 of anything.
/// </summary>
public sealed record AppleHealthRawQuantity
{
    public required double Quantity { get; init; }
    /// <summary>Raw unit code string as exported (never assumed canonical).</summary>
    public required string UnitCode { get; init; }
    /// <summary>e.g. "HKQuantityTypeIdentifierStepCount", "HKCategoryTypeIdentifierSleepAnalysis:InBed".</summary>
    public required string TypeIdentifier { get; init; }
    /// <summary>Apple's stable record id — feeds Provenance.SourceRecordId for traceability.</summary>
    public string SourceRecordId { get; init; } = string.Empty;
    public required DateTime StartLocal { get; init; }
    public required DateTime EndLocal { get; init; }

    /// <summary>Category value when this is a sleep-analysis record ("InBed"/"OutOfBed"/"Asleep"); null otherwise.</summary>
    public string? SleepValue { get; init; }
}

/// <summary>A value+unit pair as it appears in a free-form import/export file.</summary>
public sealed record RawMetricValue(double Value, string Unit);

/// <summary>
/// The generic day: metric key → value+unit, exactly the shape of a user's own JSON export
/// (and of any future provider we have no dedicated mapper for). Everything is optional —
/// absence is honest; the mapper marks missing fields Missing, never zero.
/// </summary>
public sealed record GenericRawDay
{
    /// <summary>Calendar day the numbers belong to.</summary>
    public required DateTime Date { get; init; }
    /// <summary>metric key → (value, unit). Unknown metric/unit pairs survive here; the
    /// converter + validator decide what to do with them.</summary>
    public required IReadOnlyDictionary<string, RawMetricValue> Metrics { get; init; }
    /// <summary>Sleep segments if the export carries them (else sleep.minutes alone is used).</summary>
    public IReadOnlyList<HealthConnectRawSample> SleepBuckets { get; init; } =
        Array.Empty<HealthConnectRawSample>();
    /// <summary>Stable id of the imported record/day, when the export has one.</summary>
    public string SourceRecordId { get; init; } = string.Empty;
}
