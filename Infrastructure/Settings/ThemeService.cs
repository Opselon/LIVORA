using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LIVORA.Infrastructure.Settings;

/// <summary>
/// MAUI adapter for <see cref="IThemeService"/> (lane 05, Wave 3).
///
/// The contract speaks DOMAIN enums (<see cref="ThemeMode"/> / <see cref="AppThemeKind"/>) so the
/// Application layer and the plain-net10.0 test project never touch Microsoft.Maui types — THIS
/// class is the single mapping point between the domain kinds and the platform
/// (<c>Microsoft.Maui.ApplicationModel.AppTheme</c> + <c>Application.UserAppTheme</c>).
///
/// Semantics:
///  • <see cref="Mode"/> is the user's CHOICE and is persisted through <see cref="ISettingsService"/>
///    the instant it changes ("System" persists as System — never as whatever the OS currently is).
///  • <see cref="ResolvedTheme"/> is what is actually painted: System resolves against the OS's
///    requested theme (light when the platform has not answered yet — MAUI's own default).
///  • <see cref="Apply"/> pushes the choice into the platform (UserAppTheme; Unspecified for
///    System so the OS keeps driving) and is safe to call before/after window creation. It also
///    (once) attaches to Application.RequestedThemeChanged so a live OS switch re-resolves and
///    re-raises <see cref="ThemeChanged"/> while the mode stays System.
///  • <see cref="ThemeChanged"/> fires ONLY when the RESOLVED kind actually flips — switching
///    Light→System while the OS is light is a no-op announcement-wise (subscribers avoid redraws).
///
/// Lane 04 additionally forwards RequestedThemeChanged from App.xaml.cs; both paths converge on
/// <see cref="NotifyOsThemeChanged"/>, which is idempotent, so the double wiring is harmless.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<ThemeService> _logger;
    private ThemeMode _mode;
    private AppThemeKind _resolved;
    private bool _osHooked;

    public ThemeService(ISettingsService settings, ILogger<ThemeService> logger)
    {
        _settings = settings;
        _logger = logger;
        _mode = settings.ThemeMode;
        _resolved = ThemeResolution.Resolve(_mode, OsIsDark());
    }

    public ThemeMode Mode => _mode;
    public AppThemeKind ResolvedTheme => _resolved;
    public bool IsDark => _resolved == AppThemeKind.Dark;

    public event Action<AppThemeKind>? ThemeChanged;

    public void SetMode(ThemeMode mode)
    {
        // Persist first: if the process dies mid-apply the user's choice survives.
        _settings.ThemeMode = mode;
        _mode = mode;
        Apply();
    }

    public void Apply()
    {
        HookOsChanges();
        var app = Microsoft.Maui.Controls.Application.Current;
        if (app is null)
        {
            // Pre-startup call: nothing to push yet; the constructor-time resolution still holds.
            UpdateResolved(ThemeResolution.Resolve(_mode, OsIsDark()));
            return;
        }

        var platform = _mode switch
        {
            ThemeMode.Light => Microsoft.Maui.ApplicationModel.AppTheme.Light,
            ThemeMode.Dark => Microsoft.Maui.ApplicationModel.AppTheme.Dark,
            _ => Microsoft.Maui.ApplicationModel.AppTheme.Unspecified, // let the OS drive
        };
        try
        {
            app.UserAppTheme = platform;
        }
        catch (Exception ex)
        {
            // A platform that rejects the write must not take the app down; report + keep resolving.
            _logger.LogWarning(ex, "UserAppTheme write refused (mode {Mode})", _mode);
        }
        UpdateResolved(ThemeResolution.Resolve(_mode, OsIsDark()));
    }

    /// <summary>Called by the OS-theme hook (and safely by lane 04's forwarding).</summary>
    public void NotifyOsThemeChanged()
    {
        if (_mode != ThemeMode.System) return; // an explicit user choice ignores the OS flip
        UpdateResolved(ThemeResolution.Resolve(_mode, OsIsDark()));
    }

    private void HookOsChanges()
    {
        if (_osHooked) return;
        var app = Microsoft.Maui.Controls.Application.Current;
        if (app is null) return; // hook on the first Apply that runs after the app exists
        app.RequestedThemeChanged += (_, _) => NotifyOsThemeChanged();
        _osHooked = true;
    }

    /// <summary>OS says dark? Unspecified (no platform answer yet) counts as light — MAUI's own default.</summary>
    private static bool OsIsDark()
        => Microsoft.Maui.Controls.Application.Current?.RequestedTheme
           == Microsoft.Maui.ApplicationModel.AppTheme.Dark;

    private void UpdateResolved(AppThemeKind next)
    {
        if (_resolved == next) return;
        _resolved = next;
        try
        {
            ThemeChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThemeChanged subscriber threw");
        }
    }
}
