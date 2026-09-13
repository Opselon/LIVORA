namespace LIVORA.Domain.Enums;

// Wave 3 enums. Orchestrator-owned: lanes must not rename or reorder these values —
// string forms are persisted in JSON and in preferences, so renames silently lose data.

/// <summary>Honest outcomes of an in-app version check. Unknown is the safe default.</summary>
public enum UpdateCheckStatus
{
    /// <summary>No check has run yet in this install.</summary>
    Unknown = 0,
    /// <summary>A newer published version exists — the user can act on it.</summary>
    UpdateAvailable = 1,
    /// <summary>The feed answered and this build is the newest published version.</summary>
    UpToDate = 2,
    /// <summary>This build is newer than the feed (dev/CI build) — do not call it "up to date".</summary>
    NewerThanFeed = 3,
    /// <summary>No network / feed unreachable. Distinct from UpToDate on purpose.</summary>
    NoConnection = 4,
    /// <summary>The feed throttled us (HTTP 403/429). Try again later.</summary>
    RateLimited = 5,
    /// <summary>The feed answered but the response could not be understood.</summary>
    Error = 6,
    /// <summary>Updates are not checkable in this install channel (e.g. Store-managed build).</summary>
    Disabled = 7,
}

/// <summary>
/// Real notification permission state. Never inferred: a reminder that was not granted must not be
/// presented as if it will fire.
/// </summary>
public enum NotificationGrantState
{
    Unknown = 0,
    /// <summary>Allowed by the OS — scheduled reminders can fire.</summary>
    Allowed = 1,
    /// <summary>Explicitly denied by the user.</summary>
    Denied = 2,
    /// <summary>Not asked yet (first use / iOS before request).</summary>
    NotRequested = 3,
    /// <summary>The OS decides (notifications managed outside the app); the app cannot enable them.</summary>
    SystemManaged = 4,
    /// <summary>This platform head has no notification implementation.</summary>
    Unsupported = 5,
}

/// <summary>App color scheme choice; System follows the OS and must persist as such.</summary>
public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// Resolved color scheme. A domain enum rather than Microsoft.Maui.Controls.AppTheme so the
/// Application layer (and the plain-net10.0 test project that links it) stays platform-free.
/// </summary>
public enum AppThemeKind
{
    Light = 0,
    Dark = 1,
}
