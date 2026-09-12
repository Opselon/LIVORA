using System.Globalization;

namespace LIVORA.Presentation.Components;

/// <summary>True when the bound string is non-empty (used for optional detail rows).</summary>
public sealed class IsNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Trim().Length > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
