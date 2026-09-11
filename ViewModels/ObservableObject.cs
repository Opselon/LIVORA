using System.ComponentModel;
using System.Runtime.CompilerServices;
using LIVORA.Core.Interfaces;
using LIVORA.Services.Localization;

namespace LIVORA.ViewModels;

/// <summary>Base ViewModel: property change + localized-string helpers that refresh on language change.</summary>
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

    /// <summary>Resolves a key now; call RefreshLocalized() on language change to re-resolve.</summary>
    protected string L(string key, params object[] args) => args.Length == 0 ? Loc[key] : Loc.T(key, args);

    protected virtual void OnLanguageChanged() { }

    public void RefreshLocalized() => OnLanguageChanged();

    public void SubscribeLanguage()
    {
        var loc = Loc;
        loc.LanguageChanged += OnLanguageChanged;
    }
}

/// <summary>Static service locator bridge for XAML-era construction (MAUI DI).</summary>
public static class ServiceHelper
{
    private static IServiceProvider? _services;
    public static void Initialize(IServiceProvider services) => _services = services;
    public static T Get<T>() where T : notnull =>
        _services?.GetService<T>() ?? throw new InvalidOperationException($"Service {typeof(T).Name} not registered");
}
