using LIVORA.Domain.Enums;

namespace LIVORA.Application.Abstractions;
/// <summary>
/// Lightweight preferences abstraction. Implementations must persist across app restarts.
/// </summary>
public interface ISettingsService
{
    AppLanguage PreferredLanguage { get; set; }

    /// <summary>True when the language was explicitly chosen (or defaulted) at least once.</summary>
    bool LanguageExplicitlySet { get; set; }

    bool OnboardingCompleted { get; set; }

    string? ProfileId { get; set; }
}

public interface IDateTimeProvider
{
    DateTime Today { get; }
    DateTime Now { get; }
}

public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public static readonly SystemDateTimeProvider Instance = new();
    public DateTime Today => DateTime.Today;
    public DateTime Now => DateTime.Now;
}
