using LIVORA.Application.Abstractions;
using LIVORA.Application.HealthData;
using LIVORA.Domain.Constants;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.History;

namespace LIVORA.Infrastructure.Persistence;
/// <summary>
/// Wave 2 durable history. Backfills a rolling window of the past (deterministic mock) days so
/// baselines and trends have data from day one; then appends each real day as it completes.
/// Stored as one JSON file via the same store the rest of persistence uses.
///
/// WRITE AMPLIFICATION — what was fixed and what deliberately was not:
/// Every mutation used to serialize and rewrite the WHOLE file (~25 records, ~15KB) even when the
/// mutation changed nothing: the cold-start path persisted unconditionally after a backfill that
/// usually adds zero records (the file already holds them), and re-recording a habit/goal/insight
/// that was already in that state rewrote it again. All of that is gone, so every write left on
/// disk carries real content.
/// A debounce timer was considered and rejected: JsonFileStore writes synchronously on purpose, so
/// a delayed persist would trade a guaranteed write for a possible lost one if Android kills the
/// process in the window, and the flush hooks that would make it safe (Application.Sleeping /
/// WindowClosing) live in App.xaml.cs, outside this layer. With the no-op writes removed, the
/// remaining rate is at most one write per user action — which is exactly the durability we want.
/// Revisit coalescing only when a real provider/DB makes per-record writes expensive (Phase 3).
/// </summary>
public sealed class DailyHistoryStore : IHistoryRepository
{
    private readonly JsonFileStore _store;
    private readonly SampleHealthProvider _provider;
    private readonly ISettingsService _settings;
    private const int BackfillDays = 24; // > 14 so baselines can reach High confidence quickly in demo
    private const int RetainedDays = 120; // cap retained history: ample for baselines + weekly reviews

    public DailyHistoryStore(JsonFileStore store, SampleHealthProvider provider, ISettingsService settings)
    {
        _store = store;
        _provider = provider;
        _settings = settings;
    }

    public Task<IReadOnlyList<DailyHistoryRecord>> GetAllAsync() => Task.FromResult<IReadOnlyList<DailyHistoryRecord>>(Cache);

    private List<DailyHistoryRecord> Cache { get; } = new();
    private bool _loaded;
    private DateTime _cacheDay = DateTime.MinValue;

    public async Task EnsureLoadedAsync(UserProfile profile, DateTime today)
    {
        if (_loaded && _cacheDay == today) return;

        Cache.Clear();
        var existing = await _store.LoadObjectAsync<HistoryFile>(AppConstants.HistoryFile);
        if (existing is not null) Cache.AddRange(existing.Records);
        // clock moved backwards: dropping future records IS a content change, so the file must shrink too
        bool dirty = Cache.RemoveAll(r => r.Date.Date > today) > 0;

        _loaded = true;
        _cacheDay = today;

        // Backfill missing history (mock provider is deterministic, so this is reproducible).
        var have = Cache.Select(r => r.Date.Date).ToHashSet();
        for (int i = BackfillDays; i >= 1; i--)
        {
            var day = today.AddDays(-i);
            if (have.Contains(day)) continue;
            var raw = await _provider.GetNormalizedDayAsync(day, profile);
            if (raw is null) continue;
            Cache.Add(ToRecord(raw));
            dirty = true;
        }

        // Today's provisional record (updated in place as the day evolves).
        var todayRaw = await _provider.GetNormalizedDayAsync(today, profile);
        if (todayRaw is not null && !Cache.Any(r => r.Date == today))
        {
            Cache.Add(ToRecord(todayRaw));
            dirty = true;
        }

        // The old code always ordered + rewrote the file here. Order is an in-memory concern and
        // cheap (one sort of ~25 items); the rewrite only belongs to a real content change.
        Cache.Sort((a, b) => a.Date.CompareTo(b.Date));
        if (dirty) await PersistAsync();
    }

    public async Task UpsertAsync(DailyHistoryRecord record)
    {
        var idx = Cache.FindIndex(r => r.Date == record.Date.Date);
        if (idx >= 0) Cache[idx] = record;
        else Cache.Add(record);
        Cache.Sort((a, b) => a.Date.CompareTo(b.Date));
        await PersistAsync();
    }

    /// <summary>Record what intelligence surfaced today (reproducible explanations later).</summary>
    public async Task RecordInsightAsync(DateTime date, InsightTopic topic, RecommendationPriority priority)
    {
        var rec = Cache.FirstOrDefault(r => r.Date == date.Date);
        if (rec is null) return;
        if (rec.InsightTopic == topic && rec.InsightPriority == priority) return; // already recorded
        rec.InsightTopic = topic;
        rec.InsightPriority = priority;
        await PersistAsync();
    }

    public async Task RecordHabitCompletionAsync(DateTime date, string habitId, bool completed)
    {
        var rec = Cache.FirstOrDefault(r => r.Date == date.Date);
        if (rec is null) return;
        // Persist only when the toggle actually moved the record. Tapping a habit that is already
        // in the requested state used to rewrite the whole history file on top of a full reload.
        bool changed;
        if (completed)
        {
            changed = !rec.CompletedHabitIds.Contains(habitId);
            if (changed) rec.CompletedHabitIds.Add(habitId);
        }
        else
        {
            changed = rec.CompletedHabitIds.Remove(habitId);
        }
        if (!changed) return;
        await PersistAsync();
    }

    public async Task RecordGoalProgressAsync(DateTime date, string goalId)
    {
        var rec = Cache.FirstOrDefault(r => r.Date == date.Date);
        if (rec is null) return;
        if (!rec.AdvancedGoalIds.Contains(goalId)) rec.AdvancedGoalIds.Add(goalId);
        else return; // nothing new -> no rewrite
        await PersistAsync();
    }

    private async Task PersistAsync()
    {
        // Cap retained history (RetainedDays is ample for baselines + weekly reviews). Both
        // orderings and the two list copies only matter once the cap bites or the list is out of
        // order; callers already sort, so the common path skips 2 LINQ sorts + 2 allocations.
        if (Cache.Count > RetainedDays || !IsAscending(Cache))
        {
            var capped = Cache.OrderByDescending(r => r.Date).Take(RetainedDays).OrderBy(r => r.Date).ToList();
            Cache.Clear();
            Cache.AddRange(capped);
        }
        await _store.SaveObjectAsync(AppConstants.HistoryFile, new HistoryFile { Records = Cache });
    }

    private static bool IsAscending(List<DailyHistoryRecord> records)
    {
        for (int i = 1; i < records.Count; i++)
            if (records[i - 1].Date.CompareTo(records[i].Date) > 0) return false;
        return true;
    }

    private static DailyHistoryRecord ToRecord(NormalizedDay d) => new()
    {
        Date = d.Date,
        Origin = d.Origin.ToString(),
        Completeness = d.Completeness(),
        SleepMinutes = d.SleepMinutes.Value,
        SleepQuality = d.SleepQuality.Value,
        SleepConsistency = d.SleepConsistency.Value,
        BedtimeMinutesOfDay = d.BedtimeMinutesOfDay.Value,
        Steps = (int)d.Steps.Value,
        ActiveMinutes = (int)d.ActiveMinutes.Value,
        RecoveryScore = d.RecoveryScore.Value,
        RestingHeartRate = d.RestingHeartRate?.Value,
        HrvMs = d.HrvMs?.Value,
        Stress = d.Stress.Value,
        Mood = d.Mood.Value,
        Energy = d.Energy.Value,
    };

    private sealed class HistoryFile
    {
        public List<DailyHistoryRecord> Records { get; set; } = new();
    }
}
