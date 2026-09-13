using System.Globalization;

namespace LIVORA.Presentation.Components;

/// <summary>Inverts a boolean. Lets a view show "idle-only" states (empty placeholders)
/// without adding view-model surface: IsVisible="{Binding IsBusy, Converter={StaticResource Not}}".
/// Declared in page-level Resources where used — never in the shared app dictionary.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
