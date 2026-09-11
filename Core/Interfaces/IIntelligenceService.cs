using LIVORA.Core.Enums;
using LIVORA.Core.Models;

namespace LIVORA.Core.Interfaces;

/// <summary>
/// Provider-independent intelligence abstraction. The UI never knows which provider
/// (mock, local model, cloud AI) produced an insight. Intelligence output is expressed as
/// semantic topics/actions plus parameters — never as localized display text.
/// </summary>
public interface IIntelligenceService
{
    Task<DailyInsight> GenerateDailyInsightAsync(DailyState state, UserProfile profile, IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits);

    IReadOnlyList<Recommendation> GenerateRecommendations(DailyState state, UserProfile profile);

    /// <summary>Adapts a bootcamp day to the user's current state. Phase 1: rule-based, mock data.</summary>
    BootcampDay AdaptProgramDay(Bootcamp bootcamp, BootcampDay plannedDay, DailyState state);

    /// <summary>Localization key explaining why a recommendation was made.</summary>
    string ExplainRecommendationKey(Recommendation recommendation);
}
