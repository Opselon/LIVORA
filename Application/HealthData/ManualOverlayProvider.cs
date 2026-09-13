using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;

namespace LIVORA.Application.HealthData;
/// <summary>
/// Wave 3 (lane 02): the composition root's <see cref="IDataProvider"/>. Wraps the sample feed and
/// overlays the user's manual entries per day, so everything downstream — UserStateService's
/// metric states, the rule engine, recommendations, the Today intelligence — consumes real
/// user-reported values with honest <see cref="DataOrigin.Manual"/> provenance, instead of the
/// mock numbers alone.
///
/// Honesty contract:
/// <list type="bullet">
///   <item><see cref="Origin"/> reports <see cref="DataOrigin.Manual"/> when (and only when) the
///     most recent <see cref="GetNormalizedDayAsync"/> actually applied at least one override for
///     that day; otherwise it reports the underlying provider's origin (today: Mock — the wrapped
///     feed is still sample data, and this class never claims otherwise).</item>
///   <item><see cref="Capabilities"/> is the union of what the wrapped feed really delivers and
///     the fields manual entry can honor (sleep, steps, active minutes, wellness) — nothing more.
///     HRV/HeartRate stay unadvertised until a real device feed exists.</item>
///   <item>Provider output stays untouched whenever the day has no manual values.</item>
/// </list>
/// </summary>
public sealed class ManualOverlayProvider : IDataProvider
{
    /// <summary>The fields manual entry can genuinely honor — the overlay's contribution.</summary>
    public const DataSourceCapabilities ManualCapabilities =
        DataSourceCapabilities.Sleep | DataSourceCapabilities.Steps
        | DataSourceCapabilities.ActiveMinutes | DataSourceCapabilities.Wellness;

    private readonly SampleHealthProvider _base;
    private readonly IManualEntryService _manual;

    // Origin of the LAST computed day (see class doc). Volatile: the state pipeline may be read
    // from a different thread than the one that completed the compute.
    private volatile bool _lastDayOverridden;

    public ManualOverlayProvider(SampleHealthProvider baseProvider, IManualEntryService manualEntries)
    {
        _base = baseProvider ?? throw new ArgumentNullException(nameof(baseProvider));
        _manual = manualEntries ?? throw new ArgumentNullException(nameof(manualEntries));
    }

    /// <summary>Same source identity as the wrapped feed: this is not a second connection, it is
    /// that feed plus user input. (The per-day origin is what carries the manual truth.)</summary>
    public string Id => _base.Id;
    public SourceType SourceType => _base.SourceType;   // still Mock — never claim a device
    public ConnectionState State => _base.State;

    /// <summary>Localized name for the connection row: sample data merged with the user's log.</summary>
    public string DisplayNameKey => "Health.DataSource.ManualOverlay";

    public DataOrigin Origin => _lastDayOverridden ? DataOrigin.Manual : _base.Origin;

    public DataSourceCapabilities Capabilities => _base.Capabilities | ManualCapabilities;

    public async Task<NormalizedDay?> GetNormalizedDayAsync(
        DateTime date, UserProfile profile, CancellationToken ct = default)
    {
        var raw = await _base.GetNormalizedDayAsync(date, profile, ct);
        var draft = await _manual.GetForDayAsync(date, ct);
        var record = ToRecord(draft);

        if (!ManualMerge.HasValues(record))
        {
            _lastDayOverridden = false;
            return raw; // no user input for this day: the provider's answer stands verbatim
        }

        // User logged values. With a provider day: field-by-field overlay. Without one:
        // a manual-only day whose unentered fields are honest Missing.
        var merged = raw is null
            ? ManualMerge.ManualOnlyDay(date, record!)
            : ManualMerge.Overlay(raw, record!);

        _lastDayOverridden = !ReferenceEquals(merged, raw);
        return merged;
    }

    /// <summary>Map the service's DTO back to the persisted/domain shape for the merge.
    /// (The store already sanitizes on read; <see cref="ManualMerge.Sanitize"/> here is the
    /// cheap belt for in-memory fakes that never passed through the store.)</summary>
    private static ManualEntryRecord? ToRecord(ManualEntryDraft? draft) => draft is null
        ? null
        : ManualMerge.Sanitize(new ManualEntryRecord
        {
            Date = draft.Date,
            SleepMinutes = draft.SleepMinutes,
            Steps = draft.Steps,
            ActiveMinutes = draft.ActiveMinutes,
            SleepQuality = draft.SleepQuality,
            Mood = draft.Mood,
            Energy = draft.Energy,
            Stress = draft.Stress,
            Note = draft.Note,
            SavedAtUtc = draft.SavedAtUtc,
            Origin = DataOrigin.Manual,
        });
}
