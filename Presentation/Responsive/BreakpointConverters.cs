using System;
using System.Globalization;

namespace LIVORA.Presentation.Responsive;

/// <summary>
/// Value converters that make the attached <see cref="AdaptiveLayout.BreakpointProperty"/> usable
/// straight from XAML (registered in App.xaml as <c>BpToBool</c> / <c>BpToInt</c>):
///
///   IsVisible="{Binding Source={RelativeSource AncestorType={x:Type Page}},
///                        Path=(resp:AdaptiveLayout.Breakpoint),
///                        Converter={StaticResource BpToBool}, ConverterParameter=Wide}"
///   Grid.ColumnSpan="{Binding Source={RelativeSource AncestorType={x:Type Page}},
///                        Path=(resp:AdaptiveLayout.Breakpoint),
///                        Converter={StaticResource BpToInt}, ConverterParameter=1,2,3}"
///
/// Pure stateless functions of (breakpoint, parameter) — no polling, no events: the binding
/// re-fires whenever the page's breakpoint value changes.
/// </summary>
public sealed class BreakpointToBoolConverter : IValueConverter
{
    /// <summary>
    /// Parameter = comma-separated buckets that mean "true" (e.g. "Wide" or "Medium,Wide").
    /// Unknown parameter or value → false (hide the opt-in decoration rather than show it wrong).
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Breakpoint bp || parameter is not string spec) return false;
        foreach (var part in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<Breakpoint>(part, ignoreCase: true, out var want) && want == bp)
                return true;
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Parameter = per-bucket numbers "narrow,medium,wide" (1–3 values, collapsed like
/// <see cref="BreakpointScale.ValueForBucket"/>). Malformed input returns 1 so a typo can never
/// produce a zero-span cell.
/// </summary>
public sealed class BreakpointToIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Breakpoint bp && parameter is string spec
            ? BreakpointScale.ValueForBucket(spec, bp, 1)
            : 1;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
