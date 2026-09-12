using LIVORA.Domain.Enums;

namespace LIVORA.Domain.Models;
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
    public List<string> FocusAreas { get; set; } = new() { "sleep" };
    public bool OnboardingCompleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
