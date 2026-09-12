namespace LIVORA.Domain.Enums;

/// <summary>Languages supported by LIVORA in Phase 1. Persian is a first-class language.</summary>
public enum AppLanguage
{
    English = 0,
    Persian = 1,
}

public enum SourceType
{
    Mock,
    Real,
}

public enum ConnectionState
{
    Mock,
    Connected,
    Disconnected,
    Error,
}

public enum GoalCategory
{
    Fitness,
    Sleep,
    Mindfulness,
    Learning,
    ScreenTime,
    Focus,
    Nutrition,
    Custom,
}

public enum GoalPeriod
{
    Day,
    Week,
}

public enum GoalUnit
{
    Sessions,
    Minutes,
    Hours,
    Steps,
}

public enum GoalStatus
{
    OnTrack,
    AtRisk,
    Behind,
    Completed,
}

public enum HabitFrequencyKind
{
    Daily,
    Weekdays,
    TimesPerWeek,
}

public enum BootcampCategory
{
    Sleep,
    Fitness,
    Mindfulness,
    Learning,
    Focus,
    Habit,
}

public enum BootcampDifficulty
{
    Beginner,
    Intermediate,
    Advanced,
}

public enum InsightPriority
{
    Low,
    Normal,
    High,
}

/// <summary>Semantic topics the intelligence engine can report on for a given day.</summary>
public enum InsightTopic
{
    BalancedDay,
    SleepDebt,
    LowRecovery,
    PositiveMomentum,
    HighStress,
}

public enum ActivityLevel
{
    Sedentary,
    Light,
    Moderate,
    Active,
    VeryActive,
}
