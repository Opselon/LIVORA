using LIVORA.Core.Enums;

namespace LIVORA.Core.Interfaces;

/// <summary>
/// Central localization abstraction. All user-facing text in LIVORA flows through this service;
/// user-facing strings must never be hardcoded in XAML, ViewModels or domain logic.
/// </summary>
public interface ILocalizationService : System.ComponentModel.INotifyPropertyChanged
{
    AppLanguage CurrentLanguage { get; }

    /// <summary>BCP-47 style culture name used for formatting (en / fa-IR).</summary>
    string CultureName { get; }

    /// <summary>True when the active language is right-to-left.</summary>
    bool IsRightToLeft { get; }

    /// <summary>Lookup without formatting arguments.</summary>
    string this[string key] { get; }

    /// <summary>Lookup with format arguments, applied in the active culture.</summary>
    string T(string key, params object[] args);

    /// <summary>The culture object used for number/date formatting in the active language.</summary>
    System.Globalization.CultureInfo FormatCulture { get; }

    void SetLanguage(AppLanguage language);

    /// <summary>Raised after the language (and flow direction) changed, so live UI can refresh.</summary>
    event Action? LanguageChanged;
}
