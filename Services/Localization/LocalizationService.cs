using System.Globalization;
using LIVORA.Core.Enums;
using LIVORA.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LIVORA.Services.Localization;

/// <summary>
/// Central localization service. Resolves keys from AppResources.resx (English, neutral) and
/// AppResources.fa.resx (Persian), manages flow direction, per-language fonts and the formatting
/// culture, and supports live switching without an app restart.
/// </summary>
public sealed class LocalizationService : ILocalizationService, IFormatService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<LocalizationService> _logger;
    private readonly CultureInfo _en = CultureInfo.GetCultureInfo("en-US");
    private readonly CultureInfo _fa = CultureInfo.GetCultureInfo("fa-IR");

    public event Action? LanguageChanged;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService(ISettingsService settings, ILogger<LocalizationService> logger)
    {
        _settings = settings;
        _logger = logger;

        // First launch: follow the device language when it is en/fa, otherwise English.
        if (!_settings.LanguageExplicitlySet)
        {
            var device = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            _settings.PreferredLanguage = device == "fa" ? AppLanguage.Persian : AppLanguage.English;
            _settings.LanguageExplicitlySet = true;
        }

        ApplyLanguage(_settings.PreferredLanguage, raiseChange: false);
    }

    public AppLanguage CurrentLanguage { get; private set; }
    public string CultureName => FormatCulture.Name;
    public bool IsRightToLeft { get; private set; }
    public CultureInfo FormatCulture { get; private set; } = _en;

    // ---- Font resolution -------------------------------------------------

    public static string BodyFont => CurrentLanguage == AppLanguage.Persian
        ? FontFamilies.PersianBody : FontFamilies.LatinBody;

    public static string MediumFont => CurrentLanguage == AppLanguage.Persian
        ? FontFamilies.PersianMedium : FontFamilies.LatinMedium;

    public static string BoldFont => CurrentLanguage == AppLanguage.Persian
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
        // ResourceManager invariant fallback handles missing keys; empty string means truly missing.
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
        IsRightToLeft = language == AppLanguage.Persian;
        FormatCulture = language == AppLanguage.Persian ? _fa : _en;
        AppResources.Culture = language == AppLanguage.Persian ? _fa : _en;

        // FlowDirection is applied app-wide by App.ApplyFlowDirection().
        if (raiseChange)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
            LanguageChanged?.Invoke();
        }
    }

    // ---- Locale-aware formatting (IFormatService) ------------------------

    private static string LocalDigits(string input, CultureInfo culture)
    {
        if (culture.LCID == _FaLcid)
        {
            var map = culture.NumberFormat.NativeDigits;
            var sb = new System.Text.StringBuilder(input.Length);
            foreach (var ch in input)
                sb.Append(char.IsAsciiDigit(ch) ? map[ch - '0'] : ch);
            return sb.ToString();
        }
        return input;
    }

    private const int _FaLcid = 1065; // fa-IR

    public string LongDate(DateTime date)
    {
        if (FormatCulture.LCID == _FaLcid)
        {
            var pc = new CultureInfo("fa-IR");
            // On .NET, fa-IR default calendar is Persian (Jalali). Use ICU if available.
            var text = date.ToString("dddd d MMMM", pc);
            return LocalDigits(text, pc);
        }
        return date.ToString("dddd, MMMM d", _en);
    }

    public string ShortDate(DateTime date)
    {
        if (FormatCulture.LCID == _FaLcid)
        {
            var pc = new CultureInfo("fa-IR");
            return LocalDigits(date.ToString("d MMMM", pc), pc);
        }
        return date.ToString("MMM d", _en);
    }

    public string Time(TimeSpan time)
    {
        var t = DateOnly.MinValue.Add(time).ToTimeSpan();
        var s = new DateTime(1, 1, 1).Add(t).ToString("HH:mm", CultureInfo.InvariantCulture);
        return LocalDigits(s, FormatCulture);
    }

    public string Duration(double hours, double minutes)
    {
        int h = (int)hours;
        int m = (int)Math.Round(minutes);
        if (m >= 60) { h += m / 60; m %= 60; }
        if (FormatCulture.LCID == _FaLcid)
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
        return FormatCulture.LCID == _FaLcid ? $"٪{s}" : $"{s}%";
    }

    public string TodayLabel() => this["Common.Today"];

    public string TranslateEnum(Enum value)
    {
        var key = $"Enum.{value.GetType().Name}.{value}";
        return Lookup(key);
    }

    public string TranslateBool(bool value) => Lookup(value ? "Common.Yes" : "Common.No");

    public string StatusColor(double fraction) => fraction >= 1 ? "Positive"
        : fraction >= 0.6 ? "Accent" : "Caution";
}
