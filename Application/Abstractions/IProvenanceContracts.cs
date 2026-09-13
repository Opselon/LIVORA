using LIVORA.Domain.Enums;

namespace LIVORA.Application.Abstractions;

// =============================================================================
// Wave 3 (master) — provenance & activity contracts.
// Owned by the Integration Lead (AGENT 01). Lanes implement; they never edit.
// MAUI-free by construction: this compiles into the plain-net10.0 test project.
// =============================================================================

/// <summary>
/// Trace metadata for one ingested record: where it came from and when. Every normalized
/// payload must be able to answer this — "no fake data presented as real" depends on it.
/// </summary>
public sealed record Provenance
{
    /// <summary>Stable machine id of the source ("healthconnect", "sample", "manual", ...).</summary>
    public required string Source { get; init; }
    /// <summary>Id of the record inside the source (empty when the source has no stable id).</summary>
    public string SourceRecordId { get; init; } = string.Empty;
    /// <summary>When LIVORA ingested it (UTC). Never null — ingestion always has a time.</summary>
    public DateTime ImportedAtUtc { get; init; }
    /// <summary>Trust family — mirrors DataOrigin; kept here so provenance stands alone.</summary>
    public DataOrigin Origin { get; init; } = DataOrigin.Mock;
}

/// <summary>
/// Deterministic unit conversion. Canonical units are fixed per metric family; provider units
/// must never leak into business logic. Conversions must be pure and repeatable.
/// </summary>
public interface IUnitConverter
{
    /// <summary>Convert to the canonical unit for <paramref name="metricKey"/>. Throws on unknown unit.</summary>
    double ToCanonical(string metricKey, double value, string sourceUnit);
    /// <summary>Canonical unit key for a metric ("minutes", "steps", "bpm", "ms", "ratio", "meters", "kcal").</summary>
    string CanonicalUnit(string metricKey);
}

/// <summary>Validates raw provider payloads BEFORE normalization. Rejects impossible values.</summary>
public interface IPayloadValidator
{
    /// <summary>A human-readable (localization-keyed) verdict per field: empty list = payload valid.</summary>
    IReadOnlyList<string> ValidateSleep(NormalizedFieldBundle bundle);
}

/// <summary>Flat bag of raw provider numbers keyed by metric key, used at the validation seam.</summary>
public sealed class NormalizedFieldBundle
{
    public required DateTime Date { get; init; }
    public required IReadOnlyDictionary<string, double?> Values { get; init; }
    public required Provenance Provenance { get; init; }
}

// -----------------------------------------------------------------------------
// Activity & workouts (AGENT 03 domain)
// -----------------------------------------------------------------------------

/// <summary>
/// One workout session after provider mapping. Times are local-to-user like every other model;
/// energy/distance may be null = genuinely unknown (never 0 for unknown).
/// </summary>
public sealed class WorkoutSession
{
    public required string Id { get; init; }
    public required WorkoutType Type { get; init; }
    public required DateTime StartLocal { get; init; }
    public required DateTime EndLocal { get; init; }
    public TimeSpan Duration => EndLocal - StartLocal;
    /// <summary>Meters, when the source reported distance.</summary>
    public double? DistanceMeters { get; init; }
    /// <summary>kcal, when reported or explicitly estimated (see Quality).</summary>
    public double? EnergyKcal { get; init; }
    /// <summary>0..1 relative intensity when the source exposes one.</summary>
    public double? Intensity { get; init; }
    public required Provenance Provenance { get; init; }
    public WorkoutQuality Quality { get; init; } = WorkoutQuality.Complete;
}

/// <summary>Source of workout sessions. Providers advertise support; absence is honest.</summary>
public interface IWorkoutSource
{
    DataSourceCapabilities Capabilities { get; }
    ConnectionState State { get; }
    Task<IReadOnlyList<WorkoutSession>> GetSessionsAsync(
        DateTime from, DateTime to, CancellationToken ct = default);
}
