namespace LIVORA.Services.Localization;

/// <summary>
/// Resolves the font family per active language. English uses Segoe UI (system, no shipping cost);
/// Persian uses bundled Vazirmatn (SIL OFL 1.1, full Arabic-script + Latin coverage) so Persian
/// text never falls back to a Latin-only font.
/// </summary>
public static class FontFamilies
{
    public const string LatinBody = "SegoeUI";
    public const string LatinMedium = "SegoeUI";
    public const string LatinBold = "SegoeUIBold";

    public const string PersianBody = "VazirmatnRegular";
    public const string PersianMedium = "VazirmatnSemiBold";
    public const string PersianBold = "VazirmatnBold";
}
