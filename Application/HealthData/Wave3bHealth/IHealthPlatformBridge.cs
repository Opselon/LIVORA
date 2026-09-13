using LIVORA.Domain.Enums;

namespace LIVORA.Application.HealthData.Wave3bHealth;

/// <summary>Machine identity of the Health Connect integration (persisted, never display text).</summary>
public static class HealthConnectSource
{
    /// <summary>Provenance.Source id every Health Connect record carries.</summary>
    public const string Id = "healthconnect";

    /// <summary>Every fake record id starts with this — the mock tell.</summary>
    public const string FakeRecordIdPrefix = "fake";
}

/// <summary>What an availability probe found. Ordered worst→best; never use as a UI score.</summary>
public enum BridgeAvailability
{
    /// <summary>No integration on this head (or the client API is not compiled in).</summary>
    Unavailable = 0,
    /// <summary>Platform exists but the provider app (Health Connect) is not installed.</summary>
    NotInstalled = 1,
    /// <summary>Installed, but reading needs a permission the user has not granted.</summary>
    NeedsPermission = 2,
    /// <summary>Ready: reads will return real rows.</summary>
    Ready = 3,
}

/// <summary>Why a probe came back unusable. Machine categories; the UI sentence is a resx key.</summary>
public enum BridgeErrorCategory
{
    None = 0,
    /// <summary>This platform head has no Health Connect implementation (Windows/iOS/mock).</summary>
    PlatformUnsupported = 1,
    /// <summary>Foundation only: Android client call-sites not compiled into this build.</summary>
    ApiNotBundled = 2,
    /// <summary>The platform call failed (transport/service crash). Distinct from "not installed".</summary>
    TransportFailure = 3,
    /// <summary>The OS refuses the request (policy/restrictions) — asking again will not help.</summary>
    UserRestricted = 4,
}

/// <summary>
/// Result of one probe. Immutable; <see cref="MachineTag"/> is a dot/space-free diagnostic
/// identifier for logs and tests, never a string rendered raw to a user (§0.6).
/// </summary>
public sealed record BridgeProbeResult(
    BridgeAvailability Availability,
    BridgeErrorCategory ErrorCategory = BridgeErrorCategory.None,
    string? MachineTag = null)
{
    /// <summary>Platform exists and could serve data once permitted.</summary>
    public bool PlatformPresent =>
        Availability is BridgeAvailability.NeedsPermission or BridgeAvailability.Ready;

    /// <summary>Only true when reads are expected to return real rows right now.</summary>
    public bool Readable => Availability == BridgeAvailability.Ready;

    /// <summary>Localization key for the provider-level status line (never prose, §0.6).</summary>
    public string StatusKey => HealthCapabilityMap.ProbeStatusKey(this);

    /// <summary>Format args for StatusKey.</summary>
    public object[] StatusArgs => HealthCapabilityMap.ProbeStatusArgs(this);

    public static BridgeProbeResult Ready() => new(BridgeAvailability.Ready);

    public static BridgeProbeResult Unavailable(
        BridgeErrorCategory category = BridgeErrorCategory.PlatformUnsupported,
        string? machineTag = null) => new(BridgeAvailability.Unavailable, category, machineTag);
}

/// <summary>
/// The three concepts this wave reads (sleep minutes, steps, active minutes). An enum, not free
/// metric keys: a typo cannot silently address an unimplemented concept, and the capability map can
/// enumerate every slot it has to give a verdict for.
/// </summary>
public enum HealthMetricConcept
{
    SleepMinutes = 0,
    Steps = 1,
    ActiveMinutes = 2,
}

/// <summary>
/// One raw provider record before normalization: date, value and sourceRecordId, plus the concept
/// it belongs to and the trust family the platform attests for it. Nothing is clamped or averaged
/// here — that is the mapper's job, so a bad row stays visible as a bad row.
/// </summary>
public sealed record HealthRawRow(
    DateTime Date,
    double Value,
    string SourceRecordId,
    HealthMetricConcept Concept,
    DataOrigin Origin = DataOrigin.HealthConnect,
    bool Estimated = false)
{
    /// <summary>Calendar day the row belongs to (the mapping is per-day).</summary>
    public DateTime Day => Date.Date;

    /// <summary>
    /// Rows the mapper accepts: a finite value and a non-empty record id to trace back to. A row
    /// without an id cannot be audited, so it is rejected rather than silently trusted.
    /// </summary>
    public bool IsTraceable =>
        !double.IsNaN(Value) && !double.IsInfinity(Value) && !string.IsNullOrEmpty(SourceRecordId);
}

/// <summary>
/// The fakeable platform seam. Every member is a read except RequestPermissionAsync — the only
/// member allowed to prompt — so the max-asks guard lives on the caller (<see cref="PermissionFlow"/>)
/// and not in each implementation, where one of them would eventually forget it.
/// </summary>
public interface IHealthPlatformBridge
{
    /// <summary>Machine id written into Provenance.Source ("healthconnect").</summary>
    string ProviderId { get; }

    /// <summary>Trust family this bridge attests for its rows (fake = Mock; real = HealthConnect).</summary>
    DataOrigin RowOrigin { get; }

    /// <summary>Never throws, never prompts: reports what the platform can do right now.</summary>
    BridgeProbeResult Probe();

    /// <summary>Current grant without prompting (the OS is asked, not the user).</summary>
    PermissionState QueryPermission();

    /// <summary>The only prompting member. Callers must route through PermissionFlow.</summary>
    Task<PermissionState> RequestPermissionAsync(CancellationToken ct = default);

    /// <summary>
    /// Raw rows for [fromInclusive, toExclusive) across all concepts, in deterministic order.
    /// Empty — never null, never fabricated — when nothing is available.
    /// </summary>
    Task<IReadOnlyList<HealthRawRow>> ReadRowsAsync(
        DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default);
}

/// <summary>
/// The honest "no integration here" bridge — the Windows/iOS/mock default. Every read is empty and
/// every probe unusable: there is no code path in this class that can produce a number, which is
/// what lets the provider above it report unsupported without a platform check of its own.
/// </summary>
public sealed class UnsupportedHealthPlatformBridge : IHealthPlatformBridge
{
    public const string DefaultMachineTag = "healthconnect-not-implemented";

    public UnsupportedHealthPlatformBridge(
        BridgeErrorCategory reason = BridgeErrorCategory.PlatformUnsupported,
        string? machineTag = null)
    {
        Reason = reason;
        MachineTag = string.IsNullOrEmpty(machineTag) ? DefaultMachineTag : machineTag!;
    }

    /// <summary>Why this head is unsupported (so the status line can name the reason).</summary>
    public BridgeErrorCategory Reason { get; }

    public string MachineTag { get; }

    public string ProviderId => HealthConnectSource.Id;

    /// <summary>Irrelevant in practice (no rows are ever returned), but never Mock: this is not a fake.</summary>
    public DataOrigin RowOrigin => DataOrigin.HealthConnect;

    public BridgeProbeResult Probe() => new(BridgeAvailability.Unavailable, Reason, MachineTag);

    public PermissionState QueryPermission() => PermissionState.NotDetermined;

    /// <summary>Refuses to ask: prompting for an integration that cannot deliver is a trust debt.</summary>
    public Task<PermissionState> RequestPermissionAsync(CancellationToken ct = default) =>
        Task.FromResult(PermissionState.NotDetermined);

    public Task<IReadOnlyList<HealthRawRow>> ReadRowsAsync(
        DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HealthRawRow>>(Array.Empty<HealthRawRow>());
}

/// <summary>
/// Deterministic stand-in for a working platform, for tests. NOT a data generator: callers script
/// rows explicitly (or via <see cref="WithDailyRows"/>), and every row is stamped
/// <see cref="DataOrigin.Mock"/> with a <c>fake-</c> record id so mock data can never be mistaken
/// for a platform read — in a test, in a log, or if someone wires it into a build.
/// </summary>
public sealed class FakeHealthPlatformBridge : IHealthPlatformBridge
{
    private readonly List<HealthRawRow> _rows = new();
    private BridgeAvailability _availability;
    private BridgeErrorCategory _errorCategory;

    /// <param name="availability">What Probe reports; default Ready so mapping tests read naturally.</param>
    /// <param name="grantedPermission">What QueryPermission reports.</param>
    /// <param name="requestOutcome">What a prompt answers — default Denied, so a test has to opt in to a grant.</param>
    /// <param name="rowOrigin">Trust family stamped on scripted rows; default Mock so fake data can never read as real.</param>
    /// <param name="machineTag">Diagnostic tag carried by the probe (never rendered).</param>
    public FakeHealthPlatformBridge(
        BridgeAvailability availability = BridgeAvailability.Ready,
        PermissionState grantedPermission = PermissionState.NotDetermined,
        PermissionState requestOutcome = PermissionState.Denied,
        DataOrigin rowOrigin = DataOrigin.Mock,
        string? machineTag = null)
    {
        _availability = availability;
        // Only an Unavailable probe carries a category; a present platform (NeedsPermission or
        // Ready) has no error to report.
        _errorCategory = availability == BridgeAvailability.Unavailable
            ? BridgeErrorCategory.ApiNotBundled
            : BridgeErrorCategory.None;
        QueryOutcome = grantedPermission;
        RequestOutcome = requestOutcome;
        RowOrigin = rowOrigin;
        MachineTag = machineTag;
    }

    /// <summary>Scriptable probe state (tests flip this to walk the whole capability ladder).</summary>
    public BridgeAvailability Availability
    {
        get => _availability;
        set => _availability = value;
    }

    public BridgeErrorCategory ErrorCategory
    {
        get => _errorCategory;
        set => _errorCategory = value;
    }

    public string? MachineTag { get; set; }

    /// <summary>What the OS reports without prompting.</summary>
    public PermissionState QueryOutcome { get; set; }

    /// <summary>What a prompt answers. Set to Granted to script an accepted permission.</summary>
    public PermissionState RequestOutcome { get; set; }

    public string ProviderId => HealthConnectSource.Id;

    public DataOrigin RowOrigin { get; }

    /// <summary>How many times the code under test actually tried to prompt (the loop oracle).</summary>
    public int PermissionRequestCount { get; private set; }

    /// <summary>How many reads were attempted (loop/short-circuit evidence).</summary>
    public int ReadAttemptCount { get; private set; }

    /// <summary>The scripted rows in insertion order.</summary>
    public IReadOnlyList<HealthRawRow> Rows => _rows;

    public FakeHealthPlatformBridge AddRow(HealthRawRow row)
    {
        _rows.Add(row);
        return this;
    }

    public FakeHealthPlatformBridge AddRow(
        DateTime day, HealthMetricConcept concept, double value, string? sourceRecordId = null) =>
        AddRow(new HealthRawRow(
            day.Date, value,
            sourceRecordId ?? RecordId(day.Date, concept),
            concept, RowOrigin));

    /// <summary>
    /// Appends a deterministic run of consecutive days: arithmetic on the day index, no RNG, so a
    /// mapping assertion written today still passes next month.
    /// </summary>
    public FakeHealthPlatformBridge WithDailyRows(
        DateTime firstDay,
        int days,
        double sleepMinutes = 465,
        double steps = 8200,
        double activeMinutes = 38,
        IReadOnlyCollection<DateTime>? omitDays = null)
    {
        var omit = omitDays?.Select(d => d.Date).ToHashSet() ?? new HashSet<DateTime>();
        for (var i = 0; i < days; i++)
        {
            var day = firstDay.Date.AddDays(i);
            if (omit.Contains(day)) continue;
            AddRow(day, HealthMetricConcept.SleepMinutes, sleepMinutes + (i * 7) % 31);
            AddRow(day, HealthMetricConcept.Steps, steps + (i * 137) % 900);
            AddRow(day, HealthMetricConcept.ActiveMinutes, activeMinutes + (i % 9));
        }
        return this;
    }

    /// <summary>Stable, readable fake record id ("fake-20260911-steps") — the mock tell.</summary>
    public static string RecordId(DateTime day, HealthMetricConcept concept) =>
        $"{HealthConnectSource.FakeRecordIdPrefix}-{day.Date:yyyyMMdd}-{HealthCapabilityMap.ConceptTag(concept)}";

    public BridgeProbeResult Probe() => new(_availability, _errorCategory, MachineTag);

    public PermissionState QueryPermission() => QueryOutcome;

    public Task<PermissionState> RequestPermissionAsync(CancellationToken ct = default)
    {
        PermissionRequestCount++;
        return Task.FromResult(RequestOutcome);
    }

    public Task<IReadOnlyList<HealthRawRow>> ReadRowsAsync(
        DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
    {
        ReadAttemptCount++;
        // Unusable platform => no rows, even if a test scripted some. Reads follow the probe.
        if (_availability != BridgeAvailability.Ready)
            return Task.FromResult<IReadOnlyList<HealthRawRow>>(Array.Empty<HealthRawRow>());

        var from = fromInclusive.Date;
        var to = toExclusive.Date;
        var selected = _rows
            .Where(r => r.Day >= from && r.Day < to)
            .OrderBy(r => r.Day)
            .ThenBy(r => r.Concept)
            .ThenBy(r => r.SourceRecordId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<HealthRawRow>>(selected);
    }
}
