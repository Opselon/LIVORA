using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.HealthData;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.History;

namespace LIVORA.Infrastructure.Persistence;
/// <summary>
/// Wave 3 (lane 02): persistence for the user's self-reported days + the write-side hook that
/// pushes a manual entry THROUGH the data pipeline instead of parking it in a dead-end file.
///
/// Storage: one JSON list file (<see cref="EntriesFileName"/>) via the shared
/// <see cref="JsonFileStore"/> (injected — the store's optional directory override is what makes
/// this class testable against a temp dir). Corrupt or empty files read back as "no entries",
/// exactly like the rest of persistence.
///
/// Flow-through (the honest-pipeline contract):
/// <list type="bullet">
///   <item>Read side: <see cref="ManualOverlayProvider"/> composes this service with the sample
///     feed, so UserStateService → MetricStates → RuleEngine → recommendations all see the user's
///     values per day with Manual provenance.</item>
///   <item>Write side: <see cref="SaveAsync"/> / <see cref="DeleteAsync"/> also refresh that day's
///     row in <see cref="IHistoryRepository"/> (health fields only — habit/goal/insight fields are
///     preserved) so the 28-day baselines and the weekly review move with what the user actually
///     logged, and revert cleanly when the entry is deleted.</item>
/// </list>
///
/// Honesty: every stored value keeps <see cref="DataOrigin.Manual"/> (the record itself carries
/// the tag on disk) and is labeled self-reported downstream — never presented as measured.
/// File IO stays synchronous inside JsonFileStore by the same Phase-1 rationale documented there.
/// </summary>
public sealed class ManualEntryStore : IManualEntryService
{
    /// <summary>The on-disk filename. Public so the privacy inventory/wipe can name the real file.</summary>
    public const string EntriesFileName = "livora_manual_entries.json";

    private readonly JsonFileStore _store;
    private readonly Func<IDataProvider> _provider;
    private readonly Func<IHistoryRepository> _history;
    private readonly SessionState _session;
    private readonly IDateTimeProvider _clock;

    /// <summary>Load-modify-save is serialized so two saves on different threads cannot lose one.
    /// (JsonFileStore's own lock only covers a single read/write, not the whole mutation.)</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="store">Injected, not constructed: the production composition root builds it
    /// against <c>FileSystem.AppDataDirectory</c>; tests pass a temp-directory instance.</param>
    /// <param name="provider">Lazy factory for the registered <see cref="IDataProvider"/> (today:
    /// ManualOverlayProvider) — resolved at save time, never at construction time, because the
    /// provider depends on THIS service through IManualEntryService; an eager dependency would
    /// close a DI cycle. Re-overlaying the provider's already-merged answer with the same record
    /// is idempotent (manual wins on manual, identical values), so no bypass and no double-count.
    /// The composition root registers the Func (see lane 02 APPEND block).</param>
    /// <param name="history">Lazy factory for the history repository — same cycle consideration as
    /// <paramref name="provider"/>: the repository is only resolved during SyncHistoryAsync.</param>
    public ManualEntryStore(
        JsonFileStore store,
        Func<IDataProvider> provider,
        Func<IHistoryRepository> history,
        SessionState session,
        IDateTimeProvider clock)
    {
        _store = store;
        _provider = provider;
        _history = history;
        _session = session;
        _clock = clock;
    }

    // ---- IManualEntryService --------------------------------------------------

    public async Task<IReadOnlyList<ManualEntryDraft>> GetEntriesAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var (lo, hi) = from.Date <= to.Date ? (from.Date, to.Date) : (to.Date, from.Date);
        var all = await LoadCleanAsync(ct);
        return all
            .Where(r => r.Date >= lo && r.Date <= hi)
            .OrderBy(r => r.Date)
            .Select(ToDraft)
            .ToList();
    }

    public async Task<ManualEntryDraft?> GetForDayAsync(DateTime date, CancellationToken ct = default)
    {
        var all = await LoadCleanAsync(ct);
        var record = all.FirstOrDefault(r => r.Date == date.Date);
        return record is null ? null : ToDraft(record);
    }

    /// <summary>
    /// Upsert-by-date. An all-empty draft stores nothing and removes any existing entry for that
    /// day ("save with no values" = "no entry", so CountEntriesAsync stays honest). The saved
    /// timestamp is stamped here — callers never set it. Then the day's history row is re-synced
    /// so baselines/trends/weekly review see the same numbers the live pipeline sees.
    /// </summary>
    public async Task SaveAsync(ManualEntryDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var day = draft.Date.Date;
        var record = ManualMerge.Sanitize(new ManualEntryRecord
        {
            Date = day,
            SleepMinutes = draft.SleepMinutes,
            Steps = draft.Steps,
            ActiveMinutes = draft.ActiveMinutes,
            SleepQuality = draft.SleepQuality,
            Mood = draft.Mood,
            Energy = draft.Energy,
            Stress = draft.Stress,
            Note = draft.Note,
            SavedAtUtc = DateTime.UtcNow, // "set by the store, not the caller" (contract)
            Origin = DataOrigin.Manual,
        });

        bool hasValues = ManualMerge.HasValues(record);
        bool changed;
        await _gate.WaitAsync(ct);
        try
        {
            var all = await LoadRawAsync(ct);
            int before = all.Count;
            all.RemoveAll(r => r.Date == day);
            bool existed = all.Count < before;
            changed = existed || hasValues; // empty save on a day with no entry = true no-op
            if (hasValues) all.Add(record);
            if (changed) await PersistAsync(all, ct);
        }
        finally
        {
            _gate.Release();
        }

        // Future-dated drafts (only lane 03's validation can even produce one, and it won't) stay
        // in the manual file for editing but must not pollute history: DailyHistoryStore drops
        // future rows on load anyway. Past/today → sync the history row.
        if (changed && day <= _clock.Today)
            await SyncHistoryAsync(day, hasValues ? record : null, ct);
    }

    /// <summary>Removes the entry and rebuilds that day's history row from the pure provider, so
    /// deleting an entry genuinely undoes its influence on baselines.</summary>
    public async Task DeleteAsync(DateTime date, CancellationToken ct = default)
    {
        var day = date.Date;
        bool existed;
        await _gate.WaitAsync(ct);
        try
        {
            var all = await LoadRawAsync(ct);
            existed = all.RemoveAll(r => r.Date == day) > 0;
            if (existed) await PersistAsync(all, ct);
        }
        finally
        {
            _gate.Release();
        }

        if (existed && day <= _clock.Today)
            await SyncHistoryAsync(day, manual: null, ct);
    }

    /// <summary>Days holding at least one real value — the Today nudge and the reminder engine
    /// ("haven't logged today") key off this, so phantom all-null records must not count.</summary>
    public async Task<int> CountEntriesAsync(CancellationToken ct = default)
    {
        var all = await LoadCleanAsync(ct);
        return all.Count(ManualMerge.HasValues);
    }

    /// <summary>Privacy wipe of the manual store: delete the file and revert every day it had
    /// influenced in history back to pure provider values. Callers must confirm with the user
    /// first (destructive, irreversible) — same contract as IPrivacyService's wipe.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        List<ManualEntryRecord> all;
        await _gate.WaitAsync(ct);
        try
        {
            all = await LoadRawAsync(ct);
            await _store.DeleteFileAsync(EntriesFileName);
        }
        finally
        {
            _gate.Release();
        }

        var today = _clock.Today;
        // Bound the revert pass even for absurd files: history retention caps at 120 days.
        foreach (var record in all.Where(r => r.Date <= today).OrderBy(r => r.Date).Take(200))
            await SyncHistoryAsync(record.Date.Date, manual: null, ct);
    }

    // ---- history flow-through -------------------------------------------------

    /// <summary>
    /// Rebuild the day's <see cref="DailyHistoryRecord"/> health fields from
    /// provider ⊕ <paramref name="manual"/> (null manual = revert to provider-only). Non-health
    /// fields (habit completions, goal advances, bootcamp day, recorded insight) are carried over
    /// untouched — this store must never clobber what other lanes persisted. No-op when the day
    /// has no provider row and the history cache has no row either, or when the merged day carries
    /// NaN health fields (a ManualOnlyDay without a provider feed is a live-day concern only; NaN
    /// cannot round-trip JSON and must never reach the history file).
    /// </summary>
    private async Task SyncHistoryAsync(DateTime day, ManualEntryRecord? manual, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var profile = _session.CurrentProfile;
        // The registered provider (ManualOverlayProvider) already merges this day's stored entry —
        // Save/Delete persisted before we got here — so its answer IS the merged day. Re-overlaying
        // it below with the same record is idempotent; going through the registered provider (never
        // a concrete feed) is what keeps history and live state from ever drifting apart.
        var raw = await _provider().GetNormalizedDayAsync(day, profile, ct);
        var merged = raw is null
            ? (ManualMerge.HasValues(manual) ? ManualMerge.ManualOnlyDay(day, manual!) : null)
            : (ManualMerge.HasValues(manual) ? ManualMerge.Overlay(raw, manual!) : raw);
        if (merged is null) return;

        if (double.IsNaN(merged.SleepMinutes.Value) || double.IsNaN(merged.Steps.Value) ||
            double.IsNaN(merged.ActiveMinutes.Value) || double.IsNaN(merged.RecoveryScore.Value) ||
            double.IsNaN(merged.Stress.Value) || double.IsNaN(merged.Mood.Value) ||
            double.IsNaN(merged.Energy.Value) || double.IsNaN(merged.SleepQuality.Value))
            return;

        var history = await _history().GetAllAsync();
        var existing = history.FirstOrDefault(r => r.Date.Date == day);
        // New rows are only created for real days of the user's own timeline; a row for a day the
        // history engine has never seen (future, or beyond the backfill window) is left to the
        // normal EnsureLoadedAsync path.
        var record = existing ?? new DailyHistoryRecord { Date = day, Origin = merged.Origin.ToString() };

        record.Date = day;
        record.Origin = merged.Origin.ToString();
        record.Completeness = merged.Completeness();
        record.SleepMinutes = merged.SleepMinutes.Value;
        record.SleepQuality = merged.SleepQuality.Value;
        record.SleepConsistency = merged.SleepConsistency.Value;
        record.BedtimeMinutesOfDay = merged.BedtimeMinutesOfDay.Value;
        record.Steps = (int)merged.Steps.Value;
        record.ActiveMinutes = (int)merged.ActiveMinutes.Value;
        record.RecoveryScore = merged.RecoveryScore.Value;
        record.RestingHeartRate = merged.RestingHeartRate?.Value;
        record.HrvMs = merged.HrvMs?.Value;
        record.Stress = merged.Stress.Value;
        record.Mood = merged.Mood.Value;
        record.Energy = merged.Energy.Value;

        await _history().UpsertAsync(record);
    }

    // ---- file helpers -----------------------------------------------------------

    /// <summary>Reads the file and scrubs it: dates normalized, values clamped via
    /// <see cref="ManualMerge.Sanitize"/>, phantom all-null records dropped, duplicates per date
    /// collapsed to the newest save. A corrupt file reads as empty (JsonFileStore guarantees that).
    /// This is what makes yesterday's hand-edited or old-build file harmless today.</summary>
    private async Task<List<ManualEntryRecord>> LoadCleanAsync(CancellationToken ct)
    {
        var records = await LoadRawAsync(ct);
        return Dedupe(records);
    }

    /// <summary>Raw file read (pre-sanitize) — used by mutations that operate on on-disk state.</summary>
    private Task<List<ManualEntryRecord>> LoadRawAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _store.LoadListAsync<ManualEntryRecord>(EntriesFileName);
    }

    private static List<ManualEntryRecord> Dedupe(List<ManualEntryRecord> records)
    {
        var byDate = new Dictionary<DateTime, ManualEntryRecord>();
        foreach (var raw in records)
        {
            if (raw is null) continue;
            var r = ManualMerge.Sanitize(raw);
            if (!ManualMerge.HasValues(r)) continue;
            if (byDate.TryGetValue(r.Date, out var prev) &&
                (prev.SavedAtUtc ?? DateTime.MinValue) >= (r.SavedAtUtc ?? DateTime.MinValue))
                continue;
            byDate[r.Date] = r;
        }
        return byDate.Values.OrderBy(r => r.Date).ToList();
    }

    private Task PersistAsync(List<ManualEntryRecord> records, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ordered = records.OrderBy(r => r.Date).ToList();
        return _store.SaveListAsync(EntriesFileName, ordered);
    }

    private static ManualEntryDraft ToDraft(ManualEntryRecord r) => new()
    {
        Date = r.Date,
        SleepMinutes = r.SleepMinutes,
        Steps = r.Steps,
        ActiveMinutes = r.ActiveMinutes,
        SleepQuality = r.SleepQuality,
        Mood = r.Mood,
        Energy = r.Energy,
        Stress = r.Stress,
        Note = r.Note,
        SavedAtUtc = r.SavedAtUtc,
    };
}
