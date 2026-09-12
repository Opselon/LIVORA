using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Abstractions;
/// <summary>Repository for persisted daily history — durable user history (Wave 2).</summary>
public interface IHistoryRepository
{
    Task<IReadOnlyList<DailyHistoryRecord>> GetAllAsync();
    Task UpsertAsync(DailyHistoryRecord record);
    Task EnsureLoadedAsync(UserProfile profile, DateTime today);
    Task RecordInsightAsync(DateTime date, InsightTopic topic, RecommendationPriority priority);
    Task RecordHabitCompletionAsync(DateTime date, string habitId, bool completed);
    Task RecordGoalProgressAsync(DateTime date, string goalId);
}

/// <summary>Computes personal baselines from history. Confidence-gated, recalculable.</summary>
public interface IBaselineService
{
    Task<Dictionary<string, Baseline>> GetBaselinesAsync(CancellationToken ct = default);
    Baseline? Get(string metricKey);
}

/// <summary>The user-state engine: raw data -> derived PersonalState.</summary>
public interface IUserStateService
{
    Task<PersonalState> GetStateAsync(DataRefreshMode mode, CancellationToken ct = default);
    event Action<PersonalState>? StateUpdated;
}

/// <summary>Simple, robust trend classification. Never claims certainty from tiny windows.</summary>
public interface ITrendService
{
    TrendDirection Compute(IReadOnlyList<double> values, bool higherIsBetter);
}
