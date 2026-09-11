using LIVORA.Core.Enums;
using LIVORA.Core.Models;

namespace LIVORA.Core.Interfaces;

/// <summary>Base abstraction for any future connected service or device.</summary>
public interface IDataSource
{
    string Id { get; }
    SourceType SourceType { get; }
    ConnectionState State { get; }
}

/// <summary>
/// Health data source abstraction. Phase 1 ships only a mock implementation; future providers
/// (Apple Health, Health Connect, Garmin, ...) plug in behind this interface without UI changes.
/// </summary>
public interface IHealthDataSource : IDataSource
{
    Task<HealthSnapshot?> GetSnapshotAsync(DateTime date, UserProfile profile);
}
