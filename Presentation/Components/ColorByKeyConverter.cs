using System.Globalization;

namespace LIVORA.Presentation.Components;

/// <summary>Maps a theme color key (e.g. "Positive") to its app resource Color.</summary>
public sealed class ColorByKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key) return Theme.Accent;
        var resources = Microsoft.Maui.Controls.Application.Current?.Resources;
        if (resources is not null && resources.TryGetValue(key, out var res))
        {
            // Accept both spellings of a color token: a raw Color or a SolidColorBrush wrapper.
            if (res is Color c) return c;
            if (res is SolidColorBrush brush && brush.Color is not null) return brush.Color;
        }
        return Theme.Accent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
