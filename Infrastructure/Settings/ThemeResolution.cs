using LIVORA.Domain.Enums;

namespace LIVORA.Infrastructure.Settings;

/// <summary>
/// The pure (MAUI-free) half of theme resolution, split out of <see cref="ThemeService"/> so the
/// plain-net10.0 test project can link this file and pin the mapping matrix (lane 10).
/// The adapter only adds platform plumbing on top: persisting <see cref="ThemeMode"/>, writing
/// UserAppTheme, and reacting to RequestedThemeChanged.
/// </summary>
public static class ThemeResolution
{
    /// <summary>
    /// Resolve what the app should actually paint. <paramref name="osIsDark"/> is "the OS says
    /// dark" — false covers both 'light' and 'platform has not answered yet' (MAUI's own default
    /// for RequestedTheme before a window exists is light, so Unspecified → Light, never a guess).
    /// </summary>
    public static AppThemeKind Resolve(ThemeMode mode, bool osIsDark) => mode switch
    {
        ThemeMode.Light => AppThemeKind.Light,
        ThemeMode.Dark => AppThemeKind.Dark,
        _ => osIsDark ? AppThemeKind.Dark : AppThemeKind.Light,
    };

    /// <summary>True when the resolved kind is dark (what IsDark reports to VMs).</summary>
    public static bool IsDark(ThemeMode mode, bool osIsDark) => Resolve(mode, osIsDark) == AppThemeKind.Dark;
}
