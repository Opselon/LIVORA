using System.Globalization;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Infrastructure.Security;

/// <summary>
/// The local consent register (wave 3c, lane 01) — the frozen <see cref="IConsentService"/> seam
/// backed by ONE json file under the app data dir. MAUI-free by construction: it talks to
/// <see cref="LocalJsonStore"/> (injected directory), so it compiles and is tested in the plain
/// <c>net10.0</c> head exactly as it runs on device.
///
/// Three-state semantics, which is the whole point of the enum and of this class:
/// <list type="bullet">
///   <item><b>Untouched</b> — never asked. Reads as a missing row. Behaviour is deny-everything
///     (callers must treat <c>!= Granted</c> as no), but the UI is allowed to ASK. Collapsing this
///     into "Denied" would make the ask-a-question screen impossible to build honestly.</item>
///   <item><b>Denied</b> — an explicit no. Persisted as a row. Never re-asked by an automatic flow.</item>
///   <item><b>Granted</b> — an explicit yes, revocable at any time.</item>
/// </list>
///
/// <see cref="RevokeAllAsync"/> does NOT delete the rows: a withdrawal is a decision the user made,
/// so every category is written as <b>Denied</b>, not back to "never asked". That keeps
/// "we never revoked anything" from becoming a lie after the fact, and it is what the (future)
/// wipe trigger keys off.
///
/// Nothing here leaves the device: no network call exists in this class, no value other than a
/// category + decision + UTC stamp is ever written, and no logging is performed at all (a consent
/// record is itself personal data — the file is the only copy).
/// </summary>
public sealed class ConsentStore : IConsentService
{
    /// <summary>File owned by this service (also exported through the data catalog as kind "consents").</summary>
    public const string FileName = "livora_consents.json";

    private readonly LocalJsonStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();

    /// <summary>On-disk shape. Enums serialize as strings (shared store options) so a rename of the
    /// numeric value can never silently flip a decision.</summary>
    public sealed class ConsentEntry
    {
        public ConsentCategory Category { get; set; }
        public ConsentDecision Decision { get; set; }
        /// <summary>ISO-8601 UTC instant the decision was recorded (not the read instant).</summary>
        public string UpdatedAtUtc { get; set; } = string.Empty;
    }

    public ConsentStore(LocalJsonStore store, Func<DateTime>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Synchronous by contract on the interface: an absent row is Untouched, never a guess.</summary>
    public ConsentDecision Get(ConsentCategory category)
    {
        lock (_gate)
        {
            var entry = Load().FirstOrDefault(e => e.Category == category);
            return entry is null ? ConsentDecision.Untouched : entry.Decision;
        }
    }

    public Task SetAsync(ConsentCategory category, ConsentDecision decision, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        if (!Enum.IsDefined(decision))
            throw new ArgumentOutOfRangeException(nameof(decision));
        if (decision == ConsentDecision.Untouched)
            // "I have decided that I was never asked" is nonsense: Untouched means NO ROW exists.
            // Callers who want the never-asked state back must not have asked in the first place.
            throw new ArgumentOutOfRangeException(nameof(decision),
                "Untouched is the never-asked state and cannot be set explicitly.");

        Mutate(list =>
        {
            var idx = list.FindIndex(e => e.Category == category);
            var entry = new ConsentEntry
            {
                Category = category,
                Decision = decision,
                UpdatedAtUtc = _utcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            };
            if (idx >= 0) list[idx] = entry;
            else list.Add(entry);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Snapshot for the privacy screen: an entry for EVERY category, so the UI renders six rows
    /// whether or not the user has ever been asked (never a silently missing row).
    /// </summary>
    public Task<IReadOnlyDictionary<ConsentCategory, ConsentDecision>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var rows = Load();
            var map = new Dictionary<ConsentCategory, ConsentDecision>();
            foreach (ConsentCategory category in Enum.GetValues<ConsentCategory>())
                map[category] = rows.FirstOrDefault(e => e.Category == category)?.Decision
                                ?? ConsentDecision.Untouched;
            return Task.FromResult<IReadOnlyDictionary<ConsentCategory, ConsentDecision>>(map);
        }
    }

    /// <summary>Explicit withdrawal of everything — rows written as Denied (see class comment).</summary>
    public Task RevokeAllAsync(CancellationToken ct = default)
    {
        var stamp = _utcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        Mutate(list =>
        {
            list.Clear();
            foreach (ConsentCategory category in Enum.GetValues<ConsentCategory>())
                list.Add(new ConsentEntry { Category = category, Decision = ConsentDecision.Denied, UpdatedAtUtc = stamp });
        });
        return Task.CompletedTask;
    }

    // ---- helpers ---------------------------------------------------------------

    private List<ConsentEntry> Load() => _store.LoadList<ConsentEntry>(FileName);

    private void Mutate(Action<List<ConsentEntry>> mutate)
    {
        lock (_gate)
        {
            var list = Load();
            mutate(list);
            _store.SaveList(FileName, list);
        }
    }
}
