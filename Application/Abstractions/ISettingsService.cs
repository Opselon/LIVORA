using LIVORA.Domain.Enums;

namespace LIVORA.Application.Abstractions;

/// <summary>
/// Lightweight preferences abstraction. Implementations must persist across app restarts.
///
/// WAVE 3 CONTRACT: two members were added (ThemeMode, LastSeenVersion). The orchestrator owns this
/// interface; lanes implement/replace the *service*, never this file.
/// </summary>
public interface ISettingsService
{
    AppLanguage PreferredLanguage { get; set; }

    /// <summary>True when the language was explicitly chosen (or defaulted) at least once.</summary>
    bool LanguageExplicitlySet { get; set; }

    bool OnboardingCompleted { get; set; }

    string? ProfileId { get; set; }

    /// <summary>Requested theme. System means "follow the OS", and that choice must persist.</summary>
    ThemeMode ThemeMode { get; set; }

    /// <summary>Version string the "What's new" sheet has already been shown for (null = never).</summary>
    string? LastSeenVersion { get; set; }
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

