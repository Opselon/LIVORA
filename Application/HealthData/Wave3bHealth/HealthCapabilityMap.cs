using LIVORA.Domain.Enums;

namespace LIVORA.Application.HealthData.Wave3bHealth;

/// <summary>Per-capability verdict. Sits alongside — never replaces — <see cref="PermissionState"/>: a concept can
/// be platform-supported and still locked behind a grant, or granted and simply absent.</summary>
public enum CapabilityStatus
{
    /// <summary>Delivering data now (advertised by the provider and the bridge is Ready).</summary>
    Available = 0,
    /// <summary>The platform supports it; the user has to grant the permission first.</summary>
    NeedsPermission = 1,
    /// <summary>Health Connect exists but reports no record for this concept in the window.</summary>
    NoData = 2,
    /// <summary>This integration will not deliver this concept (contract slot, honest "not supported").</summary>
    Unsupported = 3,
    /// <summary>The platform integration itself is absent/broken — nothing to grant or read.</summary>
    Unavailable = 4,
}

/// <summary>
/// One row of the capability table. <see cref="SourceRecordIds"/> is what makes the disclosure
/// auditable: an Available verdict without traceable record ids is a bug, and <see
/// cref="HealthCapabilityMap.Diagnostics"/> names it in machine tags.
/// </summary>
public sealed record CapabilityDisclosure(
    HealthMetricConcept Concept,
    DataSourceCapabilities Capability,
    CapabilityStatus Status,
    // (Docs live here rather than on each parameter: XML comments on record positional parameters
    // are not a valid doc target — the compiler warns CS1587 and drops them.)
    //   LabelKey        — concept label key (existing Health.* keys: reuse, not reinvention)
    //   StatusKey       — verdict key (lane-new Health.Cap.* / reused Profile.Status.*)
    //   StatusArgs      — args for StatusKey; a resolvable label KEY or nothing, never prose
    //   SourceRecordIds — provider record ids behind the verdict (empty unless Available)
    string LabelKey,
    string StatusKey,
    IReadOnlyList<object> StatusArgs,
    IReadOnlyList<string> SourceRecordIds)
{
    public bool IsAvailable => Status == CapabilityStatus.Available;
}

/// <summary>Pure capability table: same inputs, same output (no clock, no RNG, no platform call).</summary>
public static class HealthCapabilityMap
{
    /// <summary>The three concepts this wave reads, in stable display order.</summary>
    public static readonly HealthMetricConcept[] SupportedConcepts =
    {
        HealthMetricConcept.SleepMinutes,
        HealthMetricConcept.Steps,
        HealthMetricConcept.ActiveMinutes,
    };

    /// <summary>Concept -> the capability bit a provider must advertise to serve it.</summary>
    public static DataSourceCapabilities CapabilityFor(HealthMetricConcept concept) => concept switch
    {
        HealthMetricConcept.SleepMinutes => DataSourceCapabilities.Sleep,
        HealthMetricConcept.Steps => DataSourceCapabilities.Steps,
        HealthMetricConcept.ActiveMinutes => DataSourceCapabilities.ActiveMinutes,
        _ => DataSourceCapabilities.None,
    };

    /// <summary>Dot-free, space-free machine identifier for a concept (diagnostics, never display).</summary>
    public static string ConceptTag(HealthMetricConcept concept) => concept switch
    {
        HealthMetricConcept.SleepMinutes => "sleep-minutes",
        HealthMetricConcept.Steps => "steps",
        HealthMetricConcept.ActiveMinutes => "active-minutes",
        _ => "unknown",
    };

    /// <summary>Canonical metric key (matches <see cref="Domain.Models.State.Metrics"/>) for a concept.</summary>
    public static string MetricKey(HealthMetricConcept concept) => concept switch
    {
        HealthMetricConcept.SleepMinutes => Domain.Models.State.Metrics.SleepMinutes,
        HealthMetricConcept.Steps => Domain.Models.State.Metrics.Steps,
        HealthMetricConcept.ActiveMinutes => Domain.Models.State.Metrics.ActiveMinutes,
        _ => string.Empty,
    };

    /// <summary>Concept -> the existing resx label key that already names it in both languages.</summary>
    public static string LabelKey(HealthMetricConcept concept) => concept switch
    {
        HealthMetricConcept.SleepMinutes => "Health.Sleep",
        HealthMetricConcept.Steps => "Health.Steps",
        HealthMetricConcept.ActiveMinutes => "Health.ActiveMinutes",
        _ => "Health.NotAvailable",
    };

    /// <summary>The capability bits a Health Connect integration can honestly advertise this wave.</summary>
    public static DataSourceCapabilities HealthConnectAdvertised =>
        DataSourceCapabilities.Sleep | DataSourceCapabilities.Steps | DataSourceCapabilities.ActiveMinutes;

    /// <summary>Bits the foundation does NOT deliver yet (device-grade signals stay unadvertised).</summary>
    public static DataSourceCapabilities NotAdvertised =>
        DataSourceCapabilities.HeartRate | DataSourceCapabilities.HRV | DataSourceCapabilities.Recovery
        | DataSourceCapabilities.Wellness | DataSourceCapabilities.Workout | DataSourceCapabilities.Calories
        | DataSourceCapabilities.ScreenTime | DataSourceCapabilities.Calendar;

    /// <summary>Localization key for the whole-connection status line (one shape per availability branch, so the UI
    /// renders the same way in all four).</summary>
    public static string ProbeStatusKey(BridgeProbeResult probe) => probe.Availability switch
    {
        BridgeAvailability.Ready => "Health.Cap.Provider.Connected",
        BridgeAvailability.NotInstalled => "Health.Cap.Provider.NotInstalled",
        BridgeAvailability.NeedsPermission => "Health.Cap.Provider.NeedsPermission",
        _ => "Health.Cap.Provider.Unavailable",
    };

    /// <summary>
    /// Args for <see cref="ProbeStatusKey"/>: always none. The only candidate arg is an error
    /// category — a machine tag no user can read — so it stays in the probe's diagnostics
    /// (<see cref="ProbeDiagnosticTag"/>) instead of becoming a format argument.
    /// </summary>
    public static object[] ProbeStatusArgs(BridgeProbeResult probe) => Array.Empty<object>();

    /// <summary>
    /// Dot-free diagnostic identifier for a probe ("<c>unavailable:api-not-bundled</c>",
    /// "<c>needs-permission</c>", ...). Machine-facing only: it names the failing call-site in a log
    /// without putting anything a user is meant to read into a localization slot (§0.6).
    /// </summary>
    public static string ProbeDiagnosticTag(BridgeProbeResult probe)
    {
        var availability = probe.Availability switch
        {
            BridgeAvailability.Unavailable => "unavailable",
            BridgeAvailability.NotInstalled => "not-installed",
            BridgeAvailability.NeedsPermission => "needs-permission",
            _ => "ready",
        };
        return probe.ErrorCategory == BridgeErrorCategory.None
            ? availability
            : $"{availability}:{ErrorCategoryTag(probe.ErrorCategory)}";
    }

    /// <summary>Machine tag for an error category (feeds ProbeDiagnosticTag; never rendered).</summary>
    public static string ErrorCategoryTag(BridgeErrorCategory category) => category switch
    {
        BridgeErrorCategory.PlatformUnsupported => "platform-unsupported",
        BridgeErrorCategory.ApiNotBundled => "api-not-bundled",
        BridgeErrorCategory.TransportFailure => "transport-failure",
        BridgeErrorCategory.UserRestricted => "user-restricted",
        _ => "none",
    };

    /// <summary>PermissionState -> the lane-new status key that names it (0 args).</summary>
    public static string PermissionStatusKey(PermissionState state) => state switch
    {
        PermissionState.Granted => "Health.Cap.Permission.Granted",
        PermissionState.Denied => "Health.Cap.Permission.Denied",
        PermissionState.UnavailableInPhase => "Health.Cap.Permission.Unavailable",
        _ => "Health.Cap.Permission.NotAsked",
    };

    /// <summary>Builds the per-concept disclosure rows. <paramref name="rowsByConcept"/> — the rows actually read
    /// from the bridge — are the only evidence that can produce Available.</summary>
    public static IReadOnlyList<CapabilityDisclosure> Discover(
        BridgeProbeResult probe,
        DataSourceCapabilities advertised,
        PermissionState permission,
        IReadOnlyDictionary<HealthMetricConcept, IReadOnlyList<HealthRawRow>>? rowsByConcept = null)
    {
        var rows = rowsByConcept ?? EmptyRows();
        var list = new List<CapabilityDisclosure>(SupportedConcepts.Length);
        foreach (var concept in SupportedConcepts)
        {
            var capability = CapabilityFor(concept);
            var ids = rows.TryGetValue(concept, out var found)
                ? found.Where(r => r.IsTraceable).Select(r => r.SourceRecordId).Distinct(StringComparer.Ordinal).ToList()
                : new List<string>();

            var status = Classify(probe, advertised, capability, permission, ids.Count);
            list.Add(new CapabilityDisclosure(
                concept, capability, status, LabelKey(concept),
                StatusKeyFor(status), StatusArgsFor(status, concept), ids));
        }
        return list;
    }

    /// <summary>The ladder, ordered by what the user can act on: no platform &gt; not supported by this integration
    /// &gt; no grant &gt; no records &gt; delivering.</summary>
    public static CapabilityStatus Classify(
        BridgeProbeResult probe,
        DataSourceCapabilities advertised,
        DataSourceCapabilities capability,
        PermissionState permission,
        int recordCount)
    {
        if (probe.Availability is BridgeAvailability.Unavailable or BridgeAvailability.NotInstalled)
            return CapabilityStatus.Unavailable;
        if (!advertised.HasFlag(capability))
            return CapabilityStatus.Unsupported;
        if (probe.Availability == BridgeAvailability.NeedsPermission
            || permission is PermissionState.NotDetermined or PermissionState.Denied
                or PermissionState.UnavailableInPhase)
            return CapabilityStatus.NeedsPermission;
        return recordCount > 0 ? CapabilityStatus.Available : CapabilityStatus.NoData;
    }

    /// <summary>Status -> localization key. Existing keys are reused where the meaning already ships.</summary>
    public static string StatusKeyFor(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Available => "Health.Cap.Status.Available",
        CapabilityStatus.NeedsPermission => "Health.Cap.Status.NeedsPermission",
        CapabilityStatus.NoData => "Health.Cap.Status.NoData",
        CapabilityStatus.Unsupported => "Health.Cap.Status.Unsupported",
        _ => "Profile.Status.NotConnected",   // existing Wave 2 line: "Not connected"
    };

    /// <summary>
    /// Args per status: exactly one, and it is the concept's LABEL KEY (never a raw tag, never
    /// prose), so the UI resolves it first and a Persian user reads a translated label instead of
    /// an English metric name embedded in a sentence.
    /// </summary>
    public static IReadOnlyList<object> StatusArgsFor(CapabilityStatus status, HealthMetricConcept concept) =>
        status switch
        {
            CapabilityStatus.Available => Array.Empty<object>(),
            CapabilityStatus.NeedsPermission => new object[] { LabelKey(concept) },
            CapabilityStatus.NoData => new object[] { LabelKey(concept) },
            CapabilityStatus.Unsupported => new object[] { LabelKey(concept) },
            _ => Array.Empty<object>(),
        };

    /// <summary>Placeholder count the status key expects — used by the tests to pin key/arg agreement.</summary>
    public static int StatusArgCount(CapabilityStatus status) => status switch
    {
        CapabilityStatus.NeedsPermission or CapabilityStatus.NoData or CapabilityStatus.Unsupported => 1,
        _ => 0,
    };

    /// <summary>
    /// Machine audit lines (never rendered): an Available verdict with no record id, or a concept
    /// that mixes fake-prefixed and real record ids. An empty list means the disclosure is coherent.
    /// </summary>
    public static IReadOnlyList<string> Diagnostics(IReadOnlyList<CapabilityDisclosure> disclosures)
    {
        var problems = new List<string>();
        foreach (var d in disclosures)
        {
            if (d.Status == CapabilityStatus.Available && d.SourceRecordIds.Count == 0)
                problems.Add($"cap-unproven:{ConceptTag(d.Concept)}");
            if (d.SourceRecordIds.Count > 0)
            {
                var fake = d.SourceRecordIds.Count(id =>
                    id.StartsWith(HealthConnectSource.FakeRecordIdPrefix + "-", StringComparison.Ordinal));
                if (fake > 0 && fake < d.SourceRecordIds.Count)
                    problems.Add($"cap-mixed-record-ids:{ConceptTag(d.Concept)}");
            }
        }
        return problems;
    }

    private static IReadOnlyDictionary<HealthMetricConcept, IReadOnlyList<HealthRawRow>> EmptyRows() =>
        SupportedConcepts.ToDictionary(c => c, _ => (IReadOnlyList<HealthRawRow>)Array.Empty<HealthRawRow>());
}
