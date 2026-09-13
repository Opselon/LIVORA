namespace LIVORA.Domain.Enums;

/// <summary>
/// Wave 3 (master) enums — intelligence, provenance, sync, consent, activity.
/// Values may be APPENDED (persisted as strings); never renumber or rename existing members.
/// </summary>

/// <summary>Rolling baseline window. Each window has its own minimum-sample gate.</summary>
public enum BaselineWindow
{
    Days7 = 0,
    Days14 = 1,
    Days30 = 2,
}

/// <summary>What kind of intelligence backend is behind a provider (honesty-critical label).</summary>
public enum AiProviderKind
{
    /// <summary>Deterministic template/phrasing — no external call.</summary>
    Mock = 0,
    /// <summary>Real external LLM endpoint (network, may fail).</summary>
    ExternalLlm = 1,
    /// <summary>Future on-device model (contract slot; nothing ships local today).</summary>
    LocalModel = 2,
}

/// <summary>Why an intelligence answer looked the way it did — drives UI labels + QA tests.</summary>
public enum IntelligenceSource
{
    /// <summary>Deterministic rules only; AI never participated.</summary>
    RulesOnly = 0,
    /// <summary>AI phrasing accepted after validation.</summary>
    AiValidated = 1,
    /// <summary>AI was attempted and rejected (invalid/timeout); result is deterministic.</summary>
    AiRejectedFallback = 2,
    /// <summary>AI disabled by user consent or settings.</summary>
    AiDisabledByUser = 3,
}

/// <summary>Lifecycle of a locally stored record vs the (future) backend.</summary>
public enum SyncState
{
    /// <summary>No pending local change.</summary>
    Clean = 0,
    /// <summary>Changed locally, waiting to go out.</summary>
    Pending = 1,
    /// <summary>Confirmed in sync (only real when a gateway ever confirms).</summary>
    Synced = 2,
    /// <summary>Local and remote diverged; needs resolution — never silently overwritten.</summary>
    Conflict = 3,
}

/// <summary>Conflict kind for the (planned) sync layer. Abstraction only in this wave.</summary>
public enum ConflictKind
{
    None = 0,
    LocalChanged = 1,
    RemoteChanged = 2,
    BothChanged = 3,
    Resolved = 4,
}

/// <summary>Consent categories. Explicit opt-in where appropriate; denied by default.</summary>
public enum ConsentCategory
{
    HealthData = 0,
    ActivityData = 1,
    CalendarData = 2,
    Notifications = 3,
    /// <summary>Sending a MINIMAL derived context to an external AI endpoint.</summary>
    AiProcessing = 4,
    Analytics = 5,
}

/// <summary>Consent answers must be distinguishable between never-asked and an explicit no.</summary>
public enum ConsentDecision
{
    /// <summary>The user has never been asked — behave as denied, but UI may prompt.</summary>
    Untouched = 0,
    Denied = 1,
    Granted = 2,
}

/// <summary>Workout families LIVORA normalizes sessions into.</summary>
public enum WorkoutType
{
    Walk = 0,
    Run = 1,
    Cycle = 2,
    Gym = 3,
    Sport = 4,
    Mobility = 5,
    Other = 6,
}

/// <summary>How trustworthy one workout session record is.</summary>
public enum WorkoutQuality
{
    Complete = 0,
    Partial = 1,
    Estimated = 2,
    Suspect = 3,
}

/// <summary>Families of behavioral pattern the pattern engine can report.</summary>
public enum PatternKind
{
    LateSleepRecurring = 0,
    WeekdayActivityDip = 1,
    FocusAfterPoorSleep = 2,
    HabitFailureWindow = 3,
    WorkoutConsistency = 4,
    GoalStagnation = 5,
    RecoveryActivityCoupling = 6,
}
