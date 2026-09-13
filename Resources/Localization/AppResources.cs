namespace LIVORA.Resources.Localization;

/// <summary>
/// Hand-written resource accessor (no VS designer generator dependency — works in plain dotnet CLI builds).
/// Base name matches the manifest name of Resources/Localization/AppResources.resx.
/// </summary>
public static class AppResources
{
    private static readonly System.Resources.ResourceManager _rm =
        new("LIVORA.Resources.Localization.AppResources", typeof(AppResources).Assembly);

    public static System.Resources.ResourceManager ResourceManager => _rm;

    /// <summary>Current UI culture used for lookups. Null = invariant (English neutral).</summary>
    public static System.Globalization.CultureInfo? Culture { get; set; }

    public static string Get(string key, System.Globalization.CultureInfo? culture = null)
        => _rm.GetString(key, culture ?? Culture) ?? $"[{key}]";

    // ---- Named accessors for keys consumed from C# outside the localization service ----

    /// <summary>Title of the global unhandled-exception alert (App.xaml.cs).</summary>
    public static string ErrorDialog_Title => Get(nameof(ErrorDialog_Title));

    /// <summary>Body of the global unhandled-exception alert (App.xaml.cs).</summary>
    public static string ErrorDialog_Body => Get(nameof(ErrorDialog_Body));
}
