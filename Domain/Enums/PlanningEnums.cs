namespace LIVORA.Domain.Enums;

// Vocabulary for daily planning and recommendations.

/// <summary>Recommendation priority ladder. Most items must stay Medium/Low.</summary>
public enum RecommendationPriority
{
    Optional = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// <summary>Structured recommendation category for filtering/aggregation.</summary>
public enum RecommendationCategory
{
    Sleep,
    Activity,
    Recovery,
    Focus,
    Stress,
    Habit,
    Goal,
    Program,
    General,
}

/// <summary>A typed action a recommendation asks the user to take (semantic; localized at display).</summary>
public enum RecommendationActionKind
{
    None,
    ReduceTrainingIntensity,
    ShortWalk,
    ProtectFocusBlocks,
    EarlierBedtime,
    KeepRoutine,
    Hydrate,
    MorningLight,
    TakeBreak,
    ModerateScreenTime,
    CompleteHabit,
    AdvanceGoal,
    DoProgramDay,
    ReviewWeeklySummary,
    WindDownBeforeBed,
    NapBriefly,
}

/// <summary>Why/how data may be refreshed. No aggressive background polling in Phase 2.</summary>
public enum DataRefreshMode
{
    InitialLoad,
    ManualRefresh,
    Resume,
    ProviderSync,
}
