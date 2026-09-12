using System.Globalization;

namespace LIVORA.Presentation.Components;

/// <summary>Maps a theme color key (e.g. "Positive") to its app resource Color.</summary>
public sealed class ColorByKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key) return Theme.Accent;
        var resources = Microsoft.Maui.Controls.Application.Current?.Resources;
        if (resources is not null && resources.TryGetValue(key, out var res) && res is Color c)
            return c;
        return Theme.Accent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
