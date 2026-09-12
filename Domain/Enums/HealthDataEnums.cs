namespace LIVORA.Domain.Enums;

// Provenance and quality contracts for ingested health data.

/// <summary>What a data provider can actually deliver. Providers advertise, never over-claim.</summary>
[Flags]
public enum DataSourceCapabilities
{
    None = 0,
    Sleep = 1 << 0,
    Steps = 1 << 1,
    ActiveMinutes = 1 << 2,
    HeartRate = 1 << 3,
    HRV = 1 << 4,
    Recovery = 1 << 5,
    Workout = 1 << 6,
    Calories = 1 << 7,
    ScreenTime = 1 << 8,
    Calendar = 1 << 9,
    Wellness = 1 << 10,
}

/// <summary>Where a datapoint physically originated. Determines trust + UI labeling.
/// Only Mock/Manual exist today; the rest are contract slots for real integrations.</summary>
public enum DataOrigin
{
    Mock,
    Manual,
    Imported,
    AppleHealth,
    HealthConnect,
    Garmin,
    Fitbit,
    SamsungHealth,
    WearOs,
    Calendar,
    ScreenTime,
}

/// <summary>Trust level of an individual measurement (not of the provider overall).</summary>
public enum DataQuality
{
    /// <summary>No value exists.</summary>
    Missing,
    /// <summary>Fresh, measured value.</summary>
    Complete,
    /// <summary>Derived/estimated from other signals.</summary>
    Estimated,
    /// <summary>Real value, but older than its acceptable freshness window.</summary>
    Stale,
    /// <summary>Parsed but failed sanity checks — must be excluded from baselines.</summary>
    Invalid,
}
