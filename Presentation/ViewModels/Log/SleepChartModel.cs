using System;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Presentation;

// =============================================================================
// Lane 03 — the paint model for SleepTrendChart. The ViewModel owns ALL text
// (localized labels, Persian digits via IFormatService); the Skia view draws
// only what it is handed. That keeps the chart honest with the live language:
// when the language flips, the VM rebuilds the model and the view invalidates.
// =============================================================================

/// <summary>One night in the window. Null minutes = no data (a gap, never a fabricated zero).</summary>
public sealed record SleepChartPoint(DateTime Date, int? SleepMinutes, DataOrigin Origin, bool IsToday);

/// <summary>
/// One horizontal reference line with its pre-formatted axis labels: the full localized form
/// ("8h" / "۸ ساعت") and a digits-only short form ("8" / "۸") the view falls back to when the
/// gutter is too narrow for the unit. Digits still follow the active locale — the short form is
/// never the long one truncated.
/// </summary>
public sealed record SleepChartGridLine(double Minutes, string Label, string ShortLabel);

/// <summary>
/// A 14-night sleep series plus the user's personal baseline, ready to paint. Immutable snapshot;
/// the view never mutates it and never invents a value that isn't in <see cref="Points"/>.
/// </summary>
public sealed class SleepChartModel
{
    public SleepChartModel(
        IReadOnlyList<SleepChartPoint> points,
        double? baselineMinutes,
        IReadOnlyList<SleepChartGridLine> gridLines,
        double axisMaxMinutes,
        IReadOnlyList<string> dayLabels,
        string placeholderText)
    {
        Points = points;
        BaselineMinutes = baselineMinutes;
        GridLines = gridLines;
        AxisMaxMinutes = axisMaxMinutes;
        DayLabels = dayLabels;
        PlaceholderText = placeholderText;
    }

    public IReadOnlyList<SleepChartPoint> Points { get; }
    /// <summary>The user's own sleep baseline in minutes (null while still learning — never fabricated).</summary>
    public double? BaselineMinutes { get; }
    /// <summary>Horizontal reference lines above the axis floor, ascending.</summary>
    public IReadOnlyList<SleepChartGridLine> GridLines { get; }
    public double AxisMaxMinutes { get; }
    /// <summary>Pre-localized x label per point (ShortDate: "Sep 5" / "۵ مهر").</summary>
    public IReadOnlyList<string> DayLabels { get; }
    /// <summary>Localized one-line fallback the view paints when its drawing code faults.</summary>
    public string PlaceholderText { get; }

    public bool HasData => Points.Any(p => p.SleepMinutes is > 0);

    public static SleepChartModel Empty(string placeholderText) => new(
        Array.Empty<SleepChartPoint>(), null, Array.Empty<SleepChartGridLine>(), 0, Array.Empty<string>(), placeholderText);

    /// <summary>
    /// Computes a clean axis: whole hours only, never below 10h so a typical night doesn't touch
    /// the ceiling, and at most five reference lines so labels never collide. Whole hours matter
    /// for more than tidiness: a localized duration ("۷ ساعت و ۳۰ دقیقه") is far too wide for the
    /// axis gutter, while an hour label always fits. The wording arrives through callbacks —
    /// this layer never builds prose; the VM supplies the localized hour label plus its
    /// locale-digit short form (both formatted through IFormatService, so fa gets Persian digits).
    /// </summary>
    public static SleepChartModel Build(
        IReadOnlyList<SleepChartPoint> points,
        IReadOnlyList<string> dayLabels,
        double? baselineMinutes,
        Func<int, string> hourLabel,
        Func<int, string> hourDigits,
        string placeholderText)
    {
        double dataMax = Math.Max(baselineMinutes ?? 0d,
            points.Count == 0 ? 0d : points.Max(p => (double)(p.SleepMinutes ?? 0)));
        int hours = Math.Max(10, (int)Math.Ceiling(dataMax / 60d));          // ≥ 10h, whole hours
        int step = Math.Max(1, (int)Math.Ceiling(hours / 5d));                // ≤ 5 grid lines

        var grid = new List<SleepChartGridLine>(6);
        for (int h = step; h <= hours; h += step)
            grid.Add(new SleepChartGridLine(h * 60d, hourLabel(h), hourDigits(h)));

        return new SleepChartModel(points, baselineMinutes, grid, hours * 60d, dayLabels, placeholderText);
    }
}
