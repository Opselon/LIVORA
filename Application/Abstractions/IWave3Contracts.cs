using LIVORA.Domain.Enums;

namespace LIVORA.Application.Abstractions;

// =============================================================================
// Wave 3 shared contracts.
//
// These are the seams the parallel feature lanes code against. The orchestrator owns this file:
// lanes implement the *services*, they never edit these signatures. The composition root
// (MauiProgram.cs) remains the only place a concrete implementation is named.
//
// Layer rule (unchanged): Domain = pure data, Application = contracts + engines (no MAUI, no IO),
// Infrastructure = platform adapters, Presentation = views + view models.
// =============================================================================

// -----------------------------------------------------------------------------
// Updates (lane 01) — in-app version check against the published release feed.
// -----------------------------------------------------------------------------

/// <summary>Result of comparing the installed app with the published release feed.</summary>
public sealed record UpdateInfo
{
    /// <summary>Machine state — the UI renders this, it never guesses.</summary>
    public UpdateCheckStatus Status { get; init; } = UpdateCheckStatus.Unknown;

    /// <summary>Version of the running build, e.g. "1.2".</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>Latest version reported by the feed. Null unless the feed actually answered.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>Display name of the newest release (from the feed, may be null).</summary>
    public string? LatestName { get; init; }

    /// <summary>Release note lines (plain text from the feed; markdown noise stripped).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Download URLs by platform key: "android","ios","windows","macos","all".</summary>
    public IReadOnlyDictionary<string, string> DownloadLinks { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The release page itself — the fallback when no platform asset exists.</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>True when the running build is newer than the feed (dev/CI) — NOT "up to date".</summary>
    public bool IsNewerThanFeed { get; init; }

    /// <summary>True when the newest published release is a pre-release.</summary>
    public bool IsPrerelease { get; init; }

    /// <summary>When the feed was last successfully consulted (null = never in this install).</summary>
    public DateTime? CheckedAtUtc { get; init; }

    /// <summary>Localized failure reason for Error/RateLimited (already resolved text).</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>True when this answer came from the cached last check rather than the network.</summary>
    public bool FromCache { get; init; }
}

/// <summary>
/// Version comparison logic — pure, deterministic and unit-testable (lane 10 covers it).
/// Handles "1.2", "1.2.0", "1.10", "v1.2", "1.2.3-rc.4" and null without throwing.
/// </summary>
public static class AppVersion
{
    /// <summary>Parses "a.b.c[.d][-suffix]" into a comparable 4-part number; false when unparseable.</summary>
    public static bool TryParse(string? text, out Version value)
    {
        value = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Trim();
        int dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];
        core = core.TrimStart('v', 'V');
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4) return false;
        var nums = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            // Tolerate a trailing non-numeric marker on a part (e.g. "3beta").
            var digits = new string(parts[i].TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, out nums[i])) return false;
        }
        value = new Version(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    /// <summary>
    /// -1 when installed is older than the feed, 0 equal, 1 newer. Missing build/revision parts
    /// compare as 0, so "1.2" == "1.2.0". Unparseable input on either side means "no comparison
    /// possible" (null) — callers must surface that honestly instead of claiming up-to-date.
    /// </summary>
    public static int? Compare(string? installed, string? feed)
    {
        if (!TryParse(installed, out var a) || !TryParse(feed, out var b)) return null;
        return a.CompareTo(b);
    }
}

/// <summary>
/// Reads the published release feed. Implementations must be keyless (the feed is public — no
/// secrets, no auth), offline-safe (never throw; return a status instead), and must never invent a
/// version: <see cref="UpdateInfo.LatestVersion"/> stays null unless the feed actually answered.
/// </summary>
public interface IUpdateService
{
    /// <summary>True when this build can reach a real release feed at all.</summary>
    bool IsFeedConfigured { get; }

    /// <summary>Check for updates. <paramref name="force"/> bypasses the minimum re-check interval.</summary>
    Task<UpdateInfo> CheckAsync(bool force, CancellationToken ct = default);

    /// <summary>Last known answer without touching the network (the Profile badge uses this).</summary>
    Task<UpdateInfo?> PeekCachedAsync(CancellationToken ct = default);
}

// -----------------------------------------------------------------------------
// Manual entry (lane 02) — the user logs real values; origin becomes Manual.
// -----------------------------------------------------------------------------

/// <summary>A single user-supplied set of measurements for one day (null = not provided).</summary>
public sealed record ManualEntryDraft
{
    public required DateTime Date { get; init; }
    public int? SleepMinutes { get; init; }
    public int? Steps { get; init; }
    public int? ActiveMinutes { get; init; }
    /// <summary>0..1 self-reported sleep quality.</summary>
    public double? SleepQuality { get; init; }
    /// <summary>0..1 self-reported mood.</summary>
    public double? Mood { get; init; }
    /// <summary>0..1 self-reported energy.</summary>
    public double? Energy { get; init; }
    /// <summary>0..1 self-reported stress.</summary>
    public double? Stress { get; init; }
    public string? Note { get; init; }
    /// <summary>When this entry was last saved (UTC). Set by the store, not the caller.</summary>
    public DateTime? SavedAtUtc { get; init; }

    public bool IsEmpty =>
        SleepMinutes is null && Steps is null && ActiveMinutes is null &&
        SleepQuality is null && Mood is null && Energy is null && Stress is null;
}

/// <summary>
/// Stores user-entered values and makes them override provider data for that day.
/// Honesty rule: entries remain <see cref="DataOrigin.Manual"/> forever and are labeled
/// self-reported — never presented as measured.
/// </summary>
public interface IManualEntryService
{
    Task<IReadOnlyList<ManualEntryDraft>> GetEntriesAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task<ManualEntryDraft?> GetForDayAsync(DateTime date, CancellationToken ct = default);
    Task SaveAsync(ManualEntryDraft draft, CancellationToken ct = default);
    Task DeleteAsync(DateTime date, CancellationToken ct = default);
    /// <summary>Days holding at least one manual value (privacy inventory + Today nudge).</summary>
    Task<int> CountEntriesAsync(CancellationToken ct = default);
    /// <summary>Clear every stored entry (privacy wipe).</summary>
    Task ClearAsync(CancellationToken ct = default);
}

// -----------------------------------------------------------------------------
// Reminders (lane 09) — local notifications behind an abstraction.
// -----------------------------------------------------------------------------

/// <summary>What a reminder is about. Values persist as strings — never rename, only add.</summary>
public sealed record ReminderSetting
{
    public required string Id { get; init; }
    /// <summary>"habit", "bootcamp", "log", "winddown" — machine tag; localized by key in the UI.</summary>
    public required string Kind { get; init; }
    /// <summary>Optional id of the habit/goal/program this reminder is bound to.</summary>
    public string? TargetId { get; init; }
    public bool Enabled { get; init; } = true;
    public TimeSpan TimeOfDay { get; init; } = new(20, 0, 0);
    /// <summary>7-bit weekday mask, bit0 = Sunday (same order as <see cref="DayOfWeek"/>).</summary>
    public int DaysMask { get; init; } = 0b1111111;
    /// <summary>Localization key for the notification body.</summary>
    public required string TextKey { get; init; }
    public object[] TextArgs { get; init; } = Array.Empty<object>();
}

/// <summary>
/// Local notification scheduling. The Wave 2 honesty rule still applies: never present a
/// notification as delivered when the platform blocked it — surface
/// <see cref="NotificationGrantState"/> instead.
/// </summary>
public interface IReminderService
{
    Task<IReadOnlyList<ReminderSetting>> GetRemindersAsync(CancellationToken ct = default);
    Task SaveAsync(ReminderSetting reminder, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Whether the OS currently allows notifications (never assume true).</summary>
    Task<NotificationGrantState> GetGrantStateAsync(CancellationToken ct = default);
    /// <summary>Asks the platform and returns the resulting state. Safe to call repeatedly.</summary>
    Task<NotificationGrantState> RequestGrantAsync(CancellationToken ct = default);
    /// <summary>Push all enabled reminders to the platform scheduler (idempotent).</summary>
    Task SyncAsync(CancellationToken ct = default);
    Task ClearAllAsync(CancellationToken ct = default);
    /// <summary>Schedule a one-off test notification <paramref name="delay"/> from now.</summary>
    Task<bool> ScheduleTestAsync(TimeSpan delay, string textKey, CancellationToken ct = default);
}

// -----------------------------------------------------------------------------
// Appearance (lane 05) — theme mode beyond the OS default.
// -----------------------------------------------------------------------------

/// <summary>Theme mode abstraction so view models never touch Application.Current directly.
/// Deliberately free of Microsoft.Maui types: this file is compiled into the plain-net10.0 test
/// project, so the contract speaks in domain enums and the platform adapter maps them.</summary>
public interface IThemeService
{
    ThemeMode Mode { get; }
    /// <summary>The theme actually in effect after resolving System against the OS setting.</summary>
    AppThemeKind ResolvedTheme { get; }
    bool IsDark { get; }
    void SetMode(ThemeMode mode);
    event Action<AppThemeKind>? ThemeChanged;
    /// <summary>Push the persisted mode into the platform (call once at window creation).</summary>
    void Apply();
}
