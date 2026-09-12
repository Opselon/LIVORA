namespace LIVORA.Domain.Enums;

// Permission model and goal measurement strategy.

/// <summary>Permission slots for future platform integrations (abstraction only in Phase 2).</summary>
public enum AppPermission
{
    Health,
    Activity,
    Notifications,
    Calendar,
    Location,
}

public enum PermissionState
{
    NotDetermined,
    Granted,
    Denied,
    /// <summary>No real provider exists yet for this permission — honest unavailable.</summary>
    UnavailableInPhase,
}

/// <summary>How goal progress is computed once real data flows.</summary>
public enum GoalMeasurement
{
    /// <summary>User/VM increments a counter (Phase 1 compatible).</summary>
    ManualCounter,
    /// <summary>Average of a daily health metric, e.g. sleep duration.</summary>
    DailyMetricAverage,
    /// <summary>Sum of a daily metric over a period, e.g. weekly active minutes.</summary>
    DailyMetricSum,
    /// <summary>Count of days meeting a threshold, e.g. bedtime before 23:30.</summary>
    ThresholdDayCount,
}
