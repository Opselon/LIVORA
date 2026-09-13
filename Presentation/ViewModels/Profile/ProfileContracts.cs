using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Presentation;

// =============================================================================
// Display rows for ProfilePage's templates — pure display data rebuilt by ProfileViewModel on
// every load/language change (ItemsStackLayout rebuilds are the refresh path). Colors are
// computed from Theme constants exactly like the Wave 2 DataSourceRow did: BoxView.Color and
// Label.TextColor are COLOR-typed properties, so they must never receive resource brushes
// (docs/LANES.md §0.7).
// =============================================================================

/// <summary>
/// One honest row of the connection center. Statuses come from services: sample data is labeled
/// sample, self-reported logs are labeled self-reported, and everything without an implementation
/// says "Not connected" — never a fake green "Connected". A row is only actionable when it
/// carries an Action (no dead buttons).
/// </summary>
public sealed class ConnectionRow
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    /// <summary>caution amber = mock active, positive = real user data, secondary gray = none.</summary>
    public required Color DotColor { get; init; }
    /// <summary>Action button label; empty when the row has no action.</summary>
    public string ActionText { get; init; } = string.Empty;
    /// <summary>Row-level command (e.g. "Add your data" → the Log flow). Null = static row.</summary>
    public Command? Action { get; init; }
    public bool HasAction => Action is not null;
}

/// <summary>
/// One row of the data &amp; privacy inventory. The Wave 2 categories come from IPrivacyService
/// (kept working), extended with the Wave 3 stores (manual entries, update cache, reminders,
/// snoozes) — every row's presence chip is a measured File.Exists against the app data
/// directory, never assumed.
/// </summary>
public sealed class PrivacyRowViewModel
{
    public required string Name { get; init; }
    public required string OriginText { get; init; }
    public required string LocationText { get; init; }
    /// <summary>"Stored on this device" / "Nothing stored yet".</summary>
    public string PresenceText { get; init; } = string.Empty;
    public bool Exists { get; init; } = true;
    /// <summary>False when the category has no known backing file — the chip is hidden, not guessed.</summary>
    public bool ShowPresence { get; init; } = true;
    /// <summary>Raw file name — a technical identifier, deliberately not localized.</summary>
    public string FileNameHint { get; init; } = string.Empty;
    /// <summary>True when Delete-all-local-data removes this file (the whole point: always true here).</summary>
    public bool WipedByDeleteAll { get; init; } = true;

    public Color DotColor => Exists ? Theme.Positive : Theme.TextSecondary;
}

/// <summary>A selectable primary-goal chip (tapping toggles one UserProfile.FocusAreas tag).</summary>
public sealed class GoalChip
{
    public required string Area { get; init; }
    public required string Text { get; init; }
    public required bool Selected { get; init; }
    /// <summary>False when the 3-chip cap is reached; a tap then shows the inline hint instead of failing silently.</summary>
    public required bool Enabled { get; init; }
    /// <summary>Command the chip's button routes through (owned by the VM).</summary>
    public Command? Toggle { get; init; }

    /// <summary>Label + dot color (BoxView.Color / Label.TextColor are COLOR-typed — §0.7).</summary>
    public Color TextColor => Selected ? Theme.Accent : Theme.TextSecondary;
    /// <summary>Border.Background / Border.Stroke are IBrush-typed, so the chip ships brushes —
    /// and only on those two Border properties, exactly as §0.7 requires.</summary>
    public SolidColorBrush ChipFillBrush => Selected
        ? new SolidColorBrush(Color.FromArgb("#333E7C6F"))   // 20% accent wash, theme-agnostic
        : new SolidColorBrush(Colors.Transparent);
    public SolidColorBrush ChipStrokeBrush => Selected
        ? new SolidColorBrush(Theme.Accent)
        : new SolidColorBrush(Color.FromArgb("#8B8880"));
    public double ChipOpacity => Enabled || Selected ? 1.0 : 0.55;
}

/// <summary>Shared mapping: OS notification grant → (key, dot color). One law for Profile + Settings.</summary>
internal static class NotificationGrantDisplay
{
    public static string KeyFor(NotificationGrantState state) => state switch
    {
        NotificationGrantState.Allowed => "Notification.State.Allowed",
        NotificationGrantState.Denied => "Notification.State.Denied",
        NotificationGrantState.NotRequested => "Notification.State.NotRequested",
        NotificationGrantState.SystemManaged => "Notification.State.SystemManaged",
        NotificationGrantState.Unsupported => "Notification.State.Unsupported",
        _ => "Notification.State.Unknown",
    };

    public static Color ColorFor(NotificationGrantState state) => state switch
    {
        NotificationGrantState.Allowed => Theme.Positive,
        NotificationGrantState.Denied => Theme.Negative,
        NotificationGrantState.NotRequested => Theme.Caution,
        _ => Theme.TextSecondary,
    };
}

/// <summary>
/// Segmented-control painting shared by Profile + Settings (language rows, theme rows,
/// update actions): the active segment rides on the brand accent, the idle one on the overlay
/// surface. Both are Colors — Button.BackgroundColor/TextColor must never receive a resource
/// brush (docs/LANES.md §0.7, and the maui-color-brush pitfall that crashes Android renderers).
/// </summary>
internal static class SegmentLook
{
    public static readonly Color Idle = Color.FromArgb("#E7E5E0");

    public static Color Background(bool active) => active ? Theme.Accent : Idle;
    public static Color Foreground(bool active) => active ? Colors.White : Theme.TextSecondary;
}
