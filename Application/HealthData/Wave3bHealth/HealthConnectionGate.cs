namespace LIVORA.Application.HealthData.Wave3bHealth;

using LIVORA.Domain.Enums;

// Wave 3b (lane 02) — the read/connection gate.
//
// The provider (Infrastructure/HealthProviders/HealthConnectProvider.cs) is a thin adapter whose
// every platform-dependent decision routes through this class, so "is this head allowed to claim
// a connection, capabilities, or data?" is answered — and tested — in the pure layer that the
// plain-net10.0 test project compiles. One rule, one place: an unsupported head can never derive
// its way to Connected or to a non-null day.

/// <summary>
/// Pure decision table over (probe, permission). No state, no I/O: identical inputs always produce
/// identical verdicts, which is what makes the honesty invariants in
/// <c>Wave3bHealthProviderTests</c> testable rather than aspirational.
/// </summary>
public static class HealthConnectionGate
{
    /// <summary>
    /// Connection identity for the provider's <see cref="Abstractions.IDataSource.State"/>. Rules:
    /// a transport crash is Error (distinct from absent); a Ready + Granted platform is Connected;
    /// everything else — not installed, not permitted, no integration — is Disconnected.
    /// </summary>
    public static ConnectionState ConnectionState(BridgeProbeResult probe, PermissionState permission)
    {
        // Fully qualified: the member is named ConnectionState too, so a bare `ConnectionState.Error`
        // binds to this method (CS0119), not to the enum type.
        if (probe.ErrorCategory == BridgeErrorCategory.TransportFailure) return Domain.Enums.ConnectionState.Error;
        if (probe.Readable && permission == PermissionState.Granted) return Domain.Enums.ConnectionState.Connected;
        return Domain.Enums.ConnectionState.Disconnected;
    }

    /// <summary>
    /// Advertised capabilities: the three concepts, and only when the platform is actually present.
    /// NeedsPermission still advertises — the platform supports the concept, and the grant is a
    /// separate gate the UI renders on its own row (§0.4: never hide a capability behind a prompt).
    /// </summary>
    public static DataSourceCapabilities Capabilities(BridgeProbeResult probe) =>
        probe.PlatformPresent ? HealthCapabilityMap.HealthConnectAdvertised : DataSourceCapabilities.None;

    /// <summary>
    /// The single read gate: Ready platform AND a current grant. False means the provider must
    /// return null from GetNormalizedDayAsync — not an empty day, not zeros, not the mock feed.
    /// </summary>
    public static bool AllowsRead(BridgeProbeResult probe, PermissionState permission) =>
        probe.Readable && permission == PermissionState.Granted;
}
