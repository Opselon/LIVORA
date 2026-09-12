using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.Abstractions;
/// <summary>
/// Strengthened source contract. Providers advertise capabilities instead of implementing a
/// god-interface. Only adapters that actually exist ship implementations; the rest are
/// contract slots, honestly reported as unsupported.
/// </summary>
public interface IDataProvider : IDataSource
{
    string DisplayNameKey { get; }
    DataOrigin Origin { get; }
    DataSourceCapabilities Capabilities { get; }

    /// <summary>The single normalized-day fetch all UI/pipelines use. Returns null when the day
    /// isn't available — never fabricates values.</summary>
    Task<NormalizedDay?> GetNormalizedDayAsync(DateTime date, UserProfile profile, CancellationToken ct = default);
}

/// <summary>Normalizes provider output into canonical models + enforces freshness (stale detection).</summary>
public interface IDataNormalizer
{
    NormalizedDay Normalize(NormalizedDay raw, DateTime now);
}
