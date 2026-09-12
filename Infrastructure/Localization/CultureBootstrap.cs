using System.Globalization;

namespace LIVORA.Infrastructure.Localization;

/// <summary>
/// Culture bootstrap: resolves the app's formatting cultures with a safe fallback chain for
/// platforms where the ICU fa-IR data is trimmed (Android/iOS sometimes are). Never throws —
/// on such platforms CultureInfo.GetCultureInfo can fail, and a throwing static initializer
/// would take the whole app down. Fallback order: full tag ("fa-IR") -> language-only tag
/// ("fa", keeps Persian digits/Jalali wherever the platform still has them) -> invariant.
/// </summary>
public static class CultureBootstrap
{
    public static CultureInfo English { get; } = TryCulture("en-US", "en");
    public static CultureInfo Persian { get; } = TryCulture("fa-IR", "fa");

    private static CultureInfo TryCulture(string fullName, string parentName)
    {
        try { return CultureInfo.GetCultureInfo(fullName); }
        catch (CultureNotFoundException) { }
        try { return CultureInfo.GetCultureInfo(parentName); }
        catch (CultureNotFoundException)
        {
            // ICU data trimmed on this platform: fall back to invariant so formatting never crashes.
            return CultureInfo.InvariantCulture;
        }
    }
}
