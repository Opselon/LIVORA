using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace LIVORA.Infrastructure.Localization;
/// <summary>
/// MAUI Preferences-backed settings (replaces the Wave 1 file-based store). Persists across
/// restarts; the value that drives UI language/direction. All reads/writes are synchronous
/// against the platform secure-ish preferences store — tiny payloads, no file IO hazards.
/// </summary>
public sealed class PreferencesSettingsService : ISettingsService
{
    private const string KeyLanguage = "preferred_language";
    private const string KeyLanguageSet = "language_explicitly_set";
    private const string KeyOnboarding = "onboarding_completed";
    private const string KeyProfileId = "profile_id";
    private const string KeyThemeMode = "theme_mode";
    private const string KeyLastSeenVersion = "last_seen_version";

    private readonly ILogger<PreferencesSettingsService> _logger;

    public PreferencesSettingsService(ILogger<PreferencesSettingsService> logger) => _logger = logger;

    public Task LoadAsync() => Task.CompletedTask; // preferences are always current

    public AppLanguage PreferredLanguage
    {
        get
        {
            var raw = Preferences.Default.Get<string?>(KeyLanguage, null);
            return Enum.TryParse<AppLanguage>(raw, out var l) ? l : AppLanguage.English;
        }
        set => Preferences.Default.Set(KeyLanguage, value.ToString());
    }

    public bool LanguageExplicitlySet
    {
        get => Preferences.Default.Get(KeyLanguageSet, false);
        set => Preferences.Default.Set(KeyLanguageSet, value);
    }

    public bool OnboardingCompleted
    {
        get => Preferences.Default.Get(KeyOnboarding, false);
        set => Preferences.Default.Set(KeyOnboarding, value);
    }

    public string? ProfileId
    {
        get => Preferences.Default.Get<string?>(KeyProfileId, null);
        set => Preferences.Default.Set(KeyProfileId, value);
    }

    // Wave 3: theme choice ("System" must persist as such, not as the currently resolved theme).
    public ThemeMode ThemeMode
    {
        get
        {
            var raw = Preferences.Default.Get<string?>(KeyThemeMode, null);
            return Enum.TryParse<ThemeMode>(raw, out var m) ? m : ThemeMode.System;
        }
        set => Preferences.Default.Set(KeyThemeMode, value.ToString());
    }

    /// <summary>Version the "What's new" sheet was already shown for (null = never shown).</summary>
    public string? LastSeenVersion
    {
        get => Preferences.Default.Get<string?>(KeyLastSeenVersion, null);
        set => Preferences.Default.Set(KeyLastSeenVersion, value);
    }
}
