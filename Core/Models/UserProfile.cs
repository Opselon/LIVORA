using LIVORA.Core.Enums;

namespace LIVORA.Core.Models;

/// <summary>User profile. All descriptive strings are free user text; categorical values are enums
/// whose presentation is localized in the UI layer — never stored as localized strings.</summary>
public sealed class UserProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ActivityLevel ActivityLevel { get; set; } = ActivityLevel.Moderate;
    public TimeSpan PreferredBedtime { get; set; } = new(23, 0, 0);
    public TimeSpan PreferredWakeTime { get; set; } = new(7, 0, 0);
    /// <summary>Primary focus areas chosen during onboarding (semantic keys, not display text).</summary>
    public IReadOnlyList<string> FocusAreas { get; set; } = Array.Empty<string>();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
