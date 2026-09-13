namespace LIVORA.Application.HealthData.Wave3bHealth;

using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

// Wave 3b (lane 02) — the "extra seam" the lane prompt allows only if trivial. It is trivial:
// an enumerable list of health providers with a pick-by-readiness rule, mirroring the shape of
// IIntelligenceProviderRegistry (the existing pattern this reuses instead of reinventing).
//
// Why it exists at all: HealthConnectProvider must NOT replace the composition root's
// IDataProvider (ManualOverlayProvider is what the whole pipeline resolves today), so the
// health-connection surface needs its own enumeration for the Settings connection row. The gate
// logic stays shared: readiness is decided by HealthConnectionGate, so the registry can never
// disagree with the provider's own State.

/// <summary>Everything the UI needs to render one health connection row: name, status, capabilities.</summary>
public sealed record HealthProviderDescriptor(
    string Id,
    string DisplayNameKey,
    ConnectionState State,
    DataSourceCapabilities Capabilities,
    // StatusKey  — provider-level status localization key from the probe (never prose, §0.6)
    // StatusArgs — args for StatusKey
    // CanReadNow — true when this connection can deliver a day today (gate, not claim)
    string StatusKey,
    IReadOnlyList<object> StatusArgs,
    bool CanReadNow)
{
    /// <summary>The row is honest filler, not a data source: it must never be presented as active.</summary>
    public bool IsPlaceholder => Capabilities == DataSourceCapabilities.None && !CanReadNow;
}

/// <summary>Registration shape: a provider plus the bridge whose probe describes its status.</summary>
public sealed record HealthProviderRegistration(
    IDataProvider Provider,
    BridgeProbeResult Probe,
    PermissionState Permission,
    // CanReadNow — the read gate result for this provider right now
    bool CanReadNow);

/// <summary>The seam: which health integrations exist, and which one can serve data now.</summary>
public interface IHealthDataProviderRegistry
{
    IReadOnlyList<HealthProviderRegistration> Providers { get; }
    /// <summary>Renderable descriptors in registration order (deterministic UI).</summary>
    IReadOnlyList<HealthProviderDescriptor> Describe();
    /// <summary>The first registration that can read now, or null = no real health feed yet.</summary>
    HealthProviderRegistration? PickReadable();
}

/// <summary>
/// In-memory registry. Deterministic and dumb on purpose: it stores what the composition root hands
/// it and derives each descriptor from the probe + gate, so a "connected" row cannot be cached into
/// a lie after the platform goes away.
/// </summary>
public sealed class HealthDataProviderRegistry : IHealthDataProviderRegistry
{
    private readonly List<HealthProviderRegistration>? _fixed;
    private readonly Func<IReadOnlyList<HealthProviderRegistration>>? _live;

    public HealthDataProviderRegistry() : this(Array.Empty<HealthProviderRegistration>()) { }

    public HealthDataProviderRegistry(IEnumerable<HealthProviderRegistration> providers) =>
        _fixed = (providers ?? Array.Empty<HealthProviderRegistration>()).ToList();

    private HealthDataProviderRegistry(Func<IReadOnlyList<HealthProviderRegistration>> live)
    {
        _live = live;
        _fixed = null;
    }

    /// <summary>
    /// A registry that re-derives its entries on every read. This is what the composition root uses:
    /// a probe and a permission grant both change during a session (the user installs or denies in
    /// the OS), and a frozen snapshot would keep showing the boot-time verdict.
    /// </summary>
    public static HealthDataProviderRegistry Live(Func<IReadOnlyList<HealthProviderRegistration>> factory) =>
        new(factory ?? throw new ArgumentNullException(nameof(factory)));

    public IReadOnlyList<HealthProviderRegistration> Providers =>
        _live is not null ? _live() : _fixed ?? (IReadOnlyList<HealthProviderRegistration>)Array.Empty<HealthProviderRegistration>();

    public IReadOnlyList<HealthProviderDescriptor> Describe() => Providers.Select(Describe).ToList();

    public HealthProviderRegistration? PickReadable() => Providers.FirstOrDefault(p => p.CanReadNow);

    /// <summary>One registration -> one row. Status key comes from the probe ladder, so "Not connected" / "Needs
    /// permission" / "Not installed" all render through existing keys.</summary>
    public static HealthProviderDescriptor Describe(HealthProviderRegistration r) => new(
        r.Provider.Id,
        r.Provider.DisplayNameKey,
        r.Provider.State,
        r.Provider.Capabilities,
        HealthCapabilityMap.ProbeStatusKey(r.Probe),
        HealthCapabilityMap.ProbeStatusArgs(r.Probe),
        r.CanReadNow);

    /// <summary>
    /// Convenience for the composition root: build the registration for a
    /// <see cref="Infrastructure.HealthProviders.HealthConnectProvider"/>-shaped bridge+flow pair,
    /// probing live so the descriptor matches what the provider can actually serve right now.
    /// </summary>
    public static HealthProviderRegistration For(
        IDataProvider provider, IHealthPlatformBridge bridge, PermissionState permission)
    {
        var probe = bridge.Probe();
        return new HealthProviderRegistration(
            provider, probe, permission, HealthConnectionGate.AllowsRead(probe, permission));
    }
}
