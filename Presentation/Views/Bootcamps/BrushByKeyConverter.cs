using System.Globalization;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Maps a *Brush token key (e.g. "AccentSoftBrush") to the SolidColorBrush registered under that
/// key in the app resources, so a data-driven state (day-cell completed/today/adapted) can pick a
/// BRUSH-typed property (Border.Background / Border.Stroke) without a converter per state.
///
/// Why it lives next to the page that needs it: the shared Components folder belongs to another
/// lane, and the rule "a *Brush key may only be touched by brush-typed properties" is enforced
/// here by construction — this converter returns null for anything that is not a brush, so a
/// Color-typed property bound through it stays unset instead of logging a conversion failure.
/// </summary>
public sealed class BrushByKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return null;
        if (Microsoft.Maui.Controls.Application.Current?.Resources is not { } resources) return null;
        return resources.TryGetValue(key, out var res) && res is Brush brush ? brush : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
