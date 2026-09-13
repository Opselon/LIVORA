namespace LIVORA.Infrastructure.Localization;

/// <summary>
/// Resolves the font family per active language. English uses bundled OpenSans;
/// Persian uses bundled Vazirmatn (SIL OFL 1.1, full Arabic-script + Latin coverage) so Persian
/// text never falls back to a Latin-only font.
///
/// HONEST MAPPING (wave-3 audit, lane 05): only OpenSans Regular + Semibold are shipped, so
/// LatinBold deliberately maps to OpenSansSemibold — no fake-bold synthesis, no unlicensed
/// font added. Latin headings therefore read "semibold" weight; Vazirmatn ships a true Bold.
/// The type scale compensates with size/leading rather than pretending there is a heavier face.
/// </summary>
public static class FontFamilies
{
    public const string LatinBody = "OpenSansRegular";
    public const string LatinMedium = "OpenSansSemibold";
    public const string LatinBold = "OpenSansSemibold";

    public const string PersianBody = "VazirmatnRegular";
    public const string PersianMedium = "VazirmatnSemiBold";
    public const string PersianBold = "VazirmatnBold";
}
