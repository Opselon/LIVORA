using System.Reflection;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Models;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Minimal in-memory IRepository fake — no Moq needed (the pure layers must stay testable
/// with stdlib only). Upsert-by-Id semantics match the JSON store's behavior.
/// </summary>
public sealed class InMemoryRepo<T> : IRepository<T> where T : class
{
    private readonly List<T> _items = new();

    public InMemoryRepo() { }
    public InMemoryRepo(IEnumerable<T> items) => _items.AddRange(items);

    public Task<IReadOnlyList<T>> GetAllAsync() => Task.FromResult<IReadOnlyList<T>>(_items.ToList());

    public Task<T?> GetAsync(string id) =>
        Task.FromResult<T?>(_items.FirstOrDefault(i => IdOf(i) == id));

    public Task SaveAsync(T item)
    {
        var id = IdOf(item);
        int idx = _items.FindIndex(i => IdOf(i) == id);
        if (idx >= 0) _items[idx] = item;
        else _items.Add(item);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        _items.RemoveAll(i => IdOf(i) == id);
        return Task.CompletedTask;
    }

    public Task<bool> IsEmptyAsync() => Task.FromResult(_items.Count == 0);

    private static string? IdOf(T item) =>
        typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) as string;
}

internal static class StateCloning
{
    /// <summary>PersonalState is init-only, so tests derive variants by copying with overrides.</summary>
    public static LIVORA.Domain.Models.State.PersonalState WithHabitSnapshots(
        this LIVORA.Domain.Models.State.PersonalState s,
        params LIVORA.Domain.Models.State.HabitStateSnapshot[] snapshots) => new()
        {
            GeneratedAt = s.GeneratedAt,
            Confidence = s.Confidence,
            DataCompleteness = s.DataCompleteness,
            Sleep = s.Sleep,
            Activity = s.Activity,
            Recovery = s.Recovery,
            Wellness = s.Wellness,
            Focus = s.Focus,
            Habits = s.Habits,
            HabitSnapshots = snapshots,
            GoalSnapshots = s.GoalSnapshots,
            Metrics = s.Metrics,
        };
}

/// <summary>Fixed-clock fake: every test that reads a wall clock pins it explicitly.</summary>
public sealed class FakeClock : IDateTimeProvider
{
    public FakeClock(DateTime today) { Today = today; Now = today.AddHours(10); }
    public DateTime Today { get; }
    public DateTime Now { get; }
}

/// <summary>In-memory history for the weekly-summary honesty tests.</summary>
public sealed class FakeHistoryRepository : IHistoryRepository
{
    private readonly List<Domain.Models.History.DailyHistoryRecord> _records = new();

    public FakeHistoryRepository() { }
    public FakeHistoryRepository(IEnumerable<Domain.Models.History.DailyHistoryRecord> records) => _records.AddRange(records);

    public Task<IReadOnlyList<Domain.Models.History.DailyHistoryRecord>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<Domain.Models.History.DailyHistoryRecord>>(_records.ToList());

    public Task UpsertAsync(Domain.Models.History.DailyHistoryRecord record)
    {
        _records.RemoveAll(r => r.Date == record.Date);
        _records.Add(record);
        return Task.CompletedTask;
    }

    public Task EnsureLoadedAsync(UserProfile profile, DateTime today) => Task.CompletedTask;
    public Task RecordInsightAsync(DateTime date, Domain.Enums.InsightTopic topic, Domain.Enums.RecommendationPriority priority) => Task.CompletedTask;
    public Task RecordHabitCompletionAsync(DateTime date, string habitId, bool completed) => Task.CompletedTask;
    public Task RecordGoalProgressAsync(DateTime date, string goalId) => Task.CompletedTask;
}
