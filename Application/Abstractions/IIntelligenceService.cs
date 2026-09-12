using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Abstractions;
/// <summary>
/// Wave 2 intelligence orchestration: composes UserState + rules + recommendations + provider
/// interpretation into the DailyInsight the UI shows. Deterministic core; the provider is a
/// swappable phrasing layer (see IIntelligenceProvider).
/// </summary>
public interface IIntelligenceService
{
    Task<DailyInsight> GenerateDailyInsightAsync(
        PersonalState state, UserProfile profile, DailyPlan plan,
        IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits, CancellationToken ct = default);

    /// <summary>Structured explanation keys (localization-ready) for one recommendation.</summary>
    string ExplainRecommendationKey(Recommendation recommendation);
}
