using System.Globalization;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LIVORA.Infrastructure.Localization;
/// <summary>
/// Central localization service. Resolves keys from AppResources.resx (English, neutral) and
/// AppResources.fa.resx (Persian), manages flow direction, per-language fonts and the formatting
/// culture, and supports live switching without an app restart.
/// </summary>
public sealed class LocalizationService : ILocalizationService, IFormatService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<LocalizationService> _logger;
    // Resolved through CultureBootstrap: on trimmed-ICU platforms (Android) CultureInfo.GetCultureInfo
    // can throw, and a throwing static initializer would take the whole app down on first touch.
    // The UI language (resource lookup + RTL) never depends on these objects — see ApplyLanguage.
    private static readonly CultureInfo _en = CultureBootstrap.English;
    private static readonly CultureInfo _fa = CultureBootstrap.Persian;

    // Persian LCIDs: fa-IR (regional, the normal case) and fa (neutral) — the latter only appears
    // when CultureBootstrap had to fall back one level because the platform trimmed fa-IR data.
    // Anything else (e.g. invariant, LCID 127) means this device has no Persian ICU data at all.
    private const int _FaLcid = 1065; // fa-IR
    private const int _FaNeutralLcid = 41; // fa
    private static bool IsPersian(CultureInfo culture) => culture.LCID is _FaLcid or _FaNeutralLcid;

    public event Action? LanguageChanged;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService(ISettingsService settings, ILogger<LocalizationService> logger)
    {
        _settings = settings;
        _logger = logger;

        // First launch: LIVORA defaults to Persian (product decision); user can switch anytime.
        if (!_settings.LanguageExplicitlySet)
        {
            _settings.PreferredLanguage = AppLanguage.Persian;
            _settings.LanguageExplicitlySet = true;
        }

        ApplyLanguage(_settings.PreferredLanguage, raiseChange: false);
    }

    public AppLanguage CurrentLanguage { get; private set; }
    public string CultureName => FormatCulture.Name;
    public bool IsRightToLeft { get; private set; }
    public CultureInfo FormatCulture { get; private set; } = _en;

    // ---- Font resolution (instance: follows the active language) ----------------

    public string BodyFontFamily => CurrentLanguage == AppLanguage.Persian
        ? FontFamilies.PersianBody : FontFamilies.LatinBody;

    public string MediumFontFamily => CurrentLanguage == AppLanguage.Persian
        ? FontFamilies.PersianMedium : FontFamilies.LatinMedium;

    public string BoldFontFamily => CurrentLanguage == AppLanguage.Persian
        ? FontFamilies.PersianBold : FontFamilies.LatinBold;

    // ---- Lookup ----------------------------------------------------------

    public string this[string key] => Lookup(key);

    public string T(string key, params object[] args)
    {
        var format = Lookup(key);
        if (args is { Length: > 0 })
        {
            try { return string.Format(FormatCulture, format, args); }
            catch (FormatException)
            {
                _logger.LogWarning("Bad format for key {Key}: {Format}", key, format);
                return format;
            }
        }
        return format;
    }

    private static string Lookup(string key)
    {
        // ResourceManager invariant fallback handles missing keys; missing shows [key] for debugging.
        var value = Resources.Localization.AppResources.Get(key);
        return value;
    }

    public void SetLanguage(AppLanguage language)
    {
        if (language == CurrentLanguage) return;
        _settings.PreferredLanguage = language;
        _settings.LanguageExplicitlySet = true;
        ApplyLanguage(language, raiseChange: true);
    }

    private void ApplyLanguage(AppLanguage language, bool raiseChange)
    {
        CurrentLanguage = language;
        // RTL follows the CHOSEN language, never whether the platform ships ICU data, so a trimmed
        // device still mirrors the layout. Resource lookup keys off the culture NAME: as long as
        // CultureBootstrap resolved at least neutral "fa", the Persian satellite still loads; if
        // even that is unavailable it degrades to the invariant (English) resources instead of
        // throwing — the UI language is a resource concern, not an ICU concern.
        IsRightToLeft = language == AppLanguage.Persian;
        FormatCulture = language == AppLanguage.Persian ? _fa : _en;
        Resources.Localization.AppResources.Culture = FormatCulture;
        // Culture-aware formatting for StringFormat bindings etc.
        try { CultureInfo.CurrentCulture = FormatCulture; CultureInfo.CurrentUICulture = FormatCulture; }
        catch (CultureNotFoundException) { /* fall back to whatever the device supports */ }

        // FlowDirection is applied app-wide by App.ApplyFlowDirection().
        if (raiseChange)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
            LanguageHook.NotifyLanguageChanged();
            LanguageChanged?.Invoke();
        }
    }

    // ---- Locale-aware formatting (IFormatService) ------------------------

    private static string LocalDigits(string input, CultureInfo culture)
    {
        if (IsPersian(culture))
        {
            var map = culture.NumberFormat.NativeDigits;
            var sb = new System.Text.StringBuilder(input.Length);
            foreach (var ch in input)
                sb.Append(char.IsAsciiDigit(ch) ? map[ch - '0'] : ch);
            return sb.ToString();
        }
        return input;
    }

    public string LongDate(DateTime date)
    {
        if (IsPersian(FormatCulture))
        {
            // fa-IR/fa default calendar is Persian (Jalali) when ICU data is present.
            return LocalDigits(date.ToString("dddd d MMMM", _fa), _fa);
        }
        return date.ToString("dddd, MMMM d", _en);
    }

    public string ShortDate(DateTime date)
    {
        if (IsPersian(FormatCulture))
            return LocalDigits(date.ToString("d MMMM", _fa), _fa);
        return date.ToString("MMM d", _en);
    }

    public string Time(TimeSpan time)
    {
        var t = TimeSpan.FromHours(time.TotalHours);
        var s = new DateTime(1, 1, 1).Add(t).ToString("HH:mm", CultureInfo.InvariantCulture);
        return LocalDigits(s, FormatCulture);
    }

    public string Duration(double hours, double minutes)
    {
        int h = (int)hours;
        int m = (int)Math.Round(minutes);
        if (m >= 60) { h += m / 60; m %= 60; }
        if (IsPersian(FormatCulture))
        {
            if (h == 0) return T("Format.DurationMinutesFa", LocalDigits(m.ToString(_en), FormatCulture));
            return T("Format.DurationFullFa", LocalDigits(h.ToString(_en), FormatCulture), LocalDigits(m.ToString(_en), FormatCulture));
        }
        return m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    public string DurationFromMinutes(int totalMinutes)
        => Duration(totalMinutes / 60.0, totalMinutes % 60.0);

    public string Number(long value)
        => LocalDigits(value.ToString("N0", _en), FormatCulture);

    public string Percent(double fraction)
    {
        var pct = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
        var s = LocalDigits(pct.ToString(_en), FormatCulture);
        return IsPersian(FormatCulture) ? $"٪{s}" : $"{s}%";
    }
}
