using LIVORA.Application.Abstractions;
using LIVORA.Application.HealthData.Wave3bHealth;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Infrastructure.HealthProviders;

/// <summary>
/// Wave 3b (lane 02): the Health Connect <see cref="IDataProvider"/> — a thin adapter over an
/// <see cref="IHealthPlatformBridge"/>, with NO platform API in this class. The only Android-shaped
/// file is <see cref="HealthConnectAndroidBridge"/>, whose probe honestly reports
/// <see cref="BridgeErrorCategory.ApiNotBundled"/> because this wave forbids the Health Connect
/// client NuGet package (§0.10), so the platform call-sites stay behind the bridge.
///
/// MAUI-free on purpose: every head compiles it and the plain net10.0 test project can test it.
///
/// HONESTY CONTRACT (§0.4 — the whole point of this class):
/// • on a head with no Health Connect (Windows today) <see cref="State"/> is Disconnected,
///   <see cref="Capabilities"/> is None and <see cref="GetNormalizedDayAsync"/> returns null:
///   there is no code path from this provider to a fabricated number;
/// • days that ARE produced carry the bridge's attested origin on the day AND on every DataPoint,
///   plus per-field <see cref="Provenance"/> holding the provider record id (<see cref="LastMapping"/>);
/// • it does NOT replace the composition root's IDataProvider — ManualOverlayProvider keeps
///   winning until a real Android read exists (see the MauiProgram APPEND block comment).
/// </summary>
public sealed class HealthConnectProvider : IDataProvider
{
    /// <summary>Stable machine id — persisted and cross-referenced, never display text.</summary>
    public const string SourceId = "healthconnect";

    private readonly IHealthPlatformBridge _bridge;
    private readonly PermissionFlow _flow;
    private readonly IDateTimeProvider _clock;

    // Last computed mapping, kept for the UI/registry to label a displayed day with its records.
    // Volatile for the same reason ManualOverlayProvider._lastDayOverridden is: reads may land on
    // another thread than the compute.
    private volatile HealthDayMapping? _lastMapping;

    /// <param name="bridge">Platform seam. null = the honest default for this head: unsupported
    /// everywhere until a real Android client ships (see <see cref="DefaultBridge"/>).</param>
    /// <param name="permissionFlow">Permission state machine; null = a new flow over <paramref name="bridge"/>.</param>
    /// <param name="clock">Time source stamped onto produced days; null = system time.</param>
    public HealthConnectProvider(
        IHealthPlatformBridge? bridge = null,
        PermissionFlow? permissionFlow = null,
        IDateTimeProvider? clock = null)
    {
        _bridge = bridge ?? DefaultBridge();
        _flow = permissionFlow ?? new PermissionFlow(_bridge);
        _clock = clock ?? SystemDateTimeProvider.Instance;
    }

    /// <summary>The platform's current availability verdict (fresh probe: cheap, no side effect).</summary>
    public BridgeProbeResult Probe => _bridge.Probe();

    /// <summary>The permission machine driving this connection (budget: 2 asks per session by default).</summary>
    public PermissionFlow Permissions => _flow;

    /// <summary>Provenance of the last successful day computation; null until one runs (or any did not).</summary>
    public HealthDayMapping? LastMapping => _lastMapping;

    public string Id => "livora." + SourceId + ".provider";

    /// <summary>
    /// Real, not Mock: this adapter is an integration point, not a sample generator. That is not a
    /// claim of a connected device — <see cref="State"/> is Disconnected whenever the platform
    /// cannot report Ready, and no day is produced in that state.
    /// </summary>
    public SourceType SourceType => SourceType.Real;

    public ConnectionState State => HealthConnectionGate.ConnectionState(Probe, _flow.State);

    public string DisplayNameKey => "Health.Provider.HealthConnect";

    /// <summary>Origin the bridge attests for its rows. A day is only produced with rows behind it.</summary>
    public DataOrigin Origin => _bridge.RowOrigin;

    /// <summary>
    /// What this connection can actually serve right now — derived from the live probe, so an
    /// unsupported head reports <see cref="DataSourceCapabilities.None"/> (never a hopeful
    /// advertisement of a capability nobody implemented).
    /// </summary>
    public DataSourceCapabilities Capabilities => HealthConnectionGate.Capabilities(Probe);

    /// <summary>
    /// The read gate is <see cref="HealthConnectionGate.AllowsRead"/>: a Ready platform AND a
    /// granted permission. Otherwise null — the honest "this day has no Health Connect data",
    /// which lets the pipeline fall back to whatever else it has instead of trusting a zero.
    /// </summary>
    public async Task<NormalizedDay?> GetNormalizedDayAsync(
        DateTime date, UserProfile profile, CancellationToken ct = default)
    {
        var probe = Probe;
        if (!HealthConnectionGate.AllowsRead(probe, _flow.State))
        {
            _lastMapping = null;
            return null;
        }

        var day = date.Date;
        var rows = await _bridge.ReadRowsAsync(day, day.AddDays(1), ct);
        var mapping = HealthDayMapper.MapDay(day, rows.ToList(), _bridge.ProviderId, _bridge.RowOrigin, _clock.Now);
        _lastMapping = mapping;
        return mapping?.Day;
    }

    /// <summary> Per-concept capability disclosure for the connection row in Settings: probe + advertised
    /// capabilities + live permission + (optional) observed rows mapped to localization keys. </summary>
    public IReadOnlyList<CapabilityDisclosure> DiscoverCapabilities() =>
        HealthCapabilityMap.Discover(Probe, Capabilities, _flow.State);

    /// <summary>One entry point for the UI: run the flow and report the attempt (never prompts directly).</summary>
    public Task<PermissionAttempt> EnsurePermissionAsync(CancellationToken ct = default) => _flow.EnsureAsync(ct);

    /// <summary>
    /// The composition root's default seam per head. On Android it is the
    /// <see cref="HealthConnectAndroidBridge"/> shell, whose probe reports ApiNotBundled until the
    /// AndroidX Health Connect client is compiled in; on every other head it is an
    /// <see cref="UnsupportedHealthPlatformBridge"/>, so the provider is honest everywhere.
    /// </summary>
    public static IHealthPlatformBridge DefaultBridge() =>
#if ANDROID
        new HealthConnectAndroidBridge();
#else
        new UnsupportedHealthPlatformBridge(
            BridgeErrorCategory.PlatformUnsupported,
            UnsupportedHealthPlatformBridge.DefaultMachineTag);
#endif
}
