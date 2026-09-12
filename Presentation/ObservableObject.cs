using System.ComponentModel;
using System.Runtime.CompilerServices;
using LIVORA.Application.Abstractions;
using LIVORA.Infrastructure.Localization;

namespace LIVORA.Presentation;
/// <summary>
/// Base ViewModel: property change + localized-string helpers + app-wide language glue.
/// On language change: FlowDirection and FontFamily are re-raised so every bound page mirrors
/// and re-types itself instantly, and OnLanguageChanged lets each VM re-resolve its strings.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private ILocalizationService? _loc;
    protected ILocalizationService Loc => _loc ??= ServiceHelper.Get<ILocalizationService>();

    /// <summary>Resolves a key now; VMs re-resolve on language change via OnLanguageChanged.</summary>
    protected string L(string key, params object[] args) => args.Length == 0 ? Loc[key] : Loc.T(key, args);

    // ---- Global language glue (bound by every page root) ----------------

    public FlowDirection FlowDirection => Loc.IsRightToLeft
        ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string AppFont => Loc.BodyFontFamily;

    protected virtual void OnLanguageChanged() { }

    public void SubscribeLanguage()
    {
        Loc.LanguageChanged += () =>
        {
            Raise(nameof(FlowDirection));
            Raise(nameof(AppFont));
            OnLanguageChanged();
        };
    }

    public void RefreshLocalized() => OnLanguageChanged();
}

/// <summary>Static service locator bridge for MAUI DI resolution from pages/helpers.</summary>
public static class ServiceHelper
{
    private static IServiceProvider? _services;
    public static void Initialize(IServiceProvider services) => _services = services;
    public static T Get<T>() where T : notnull =>
        (T?)_services?.GetService(typeof(T)) ?? throw new InvalidOperationException($"Service {typeof(T).Name} not registered");

    public static T? TryGet<T>() where T : notnull
        => _services is null ? default : (T?)_services.GetService(typeof(T));
}
