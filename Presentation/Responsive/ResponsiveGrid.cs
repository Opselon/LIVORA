using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Maui.Layouts;

namespace LIVORA.Presentation.Responsive;

/// <summary>
/// A tile grid that reflows to the window width: one column on a phone, two on a tablet-sized
/// window, three-plus on a desktop — with per-tile spanning and real RTL mirroring.
///
/// Why a layout class instead of a XAML <c>Grid</c>: a Grid would need its
/// <c>ColumnDefinitions</c> (and every child's <c>Grid.Column</c>) rewritten at every breakpoint
/// change — imperative XAML surgery in every page. Here the page declares a spec
/// (<c>resp:AdaptiveLayout.Columns="1,2,3"</c> on the grid itself, or <c>ItemWidth</c>) and the
/// container owns the rest.
///
/// Layout contract (the shape MAUI's built-in managers follow, implemented directly so it is
/// deterministic):
///   • tiles are measured against the slot width, never the whole row, so a phone card cannot
///     stretch to 1200 px (the "blown-up phone" the client rejected);
///   • a row's height is the tallest tile measured in it;
///   • Fill tiles take the slot (plus span extra); Start/Center/End keep their desired size and
///     align inside the slot; the horizontal axis mirrors in RTL while the logical order
///     (first child = reading-start) holds in both directions;
///   • margins are honored and a spanning tile never overlaps its neighbours.
///
/// No timers, no polling: all work happens inside the normal measure/arrange pass; the only extra
/// trigger is an invalidation when the width *bucket* changes (AdaptiveLayout pokes
/// <c>InvalidateMeasure</c> through the attached-property change handler).
/// </summary>
public class ResponsiveGrid : Layout
{
    /// <summary>Target tile width the automatic column count aims for (cards read well at ~260–340 px).</summary>
    public const double DefaultItemWidth = 300;

    /// <summary>Hard ceiling — six columns of anything is a spreadsheet, not a wellness app.</summary>
    public const int MaxColumnCount = 6;

    /// <summary>Default gap between tiles; matches the Wave 3 CardSpacing token's meaning.</summary>
    public const double DefaultSpacing = 14;

    /// <summary>Gap between tiles on both axes (single number so wrapped rows breathe evenly).</summary>
    public double Spacing { get; set; } = DefaultSpacing;

    /// <summary>
    /// Target tile width for automatic column selection. Ignored when a columns spec
    /// (<see cref="ColumnsSpecProperty"/> or the attached <c>AdaptiveLayout.Columns</c>) is set;
    /// 0 forces a single column.
    /// </summary>
    public double ItemWidth { get; set; } = DefaultItemWidth;

    /// <summary>
    /// Per-breakpoint column count as <c>"narrow,medium,wide"</c> — see <see cref="BreakpointScale"/>
    /// for the exact effective-px bands (&lt;700 / 700–999 / ≥1000). Example: <c>"1,2,3"</c>.
    /// One or two values collapse as in <see cref="BreakpointScale.ValueForBucket"/>.
    /// Equivalent to setting the attached <c>AdaptiveLayout.Columns</c> on this element —
    /// whichever is present wins in this order: ColumnsSpec, attached spec.
    /// </summary>
    public string? ColumnsSpec
    {
        get => (string?)GetValue(ColumnsSpecProperty);
        set => SetValue(ColumnsSpecProperty, value);
    }

    public static readonly BindableProperty ColumnsSpecProperty =
        BindableProperty.Create(nameof(ColumnsSpec), typeof(string), typeof(ResponsiveGrid), null,
            propertyChanged: (b, _, _) => ((IView)b).InvalidateMeasure());

    /// <summary>Column count the last layout pass actually used. Observable, so pages and tests can assert it.</summary>
    public int UsedColumns => (int)GetValue(UsedColumnsProperty);

    public static readonly BindableProperty UsedColumnsProperty =
        BindableProperty.Create(nameof(UsedColumns), typeof(int), typeof(ResponsiveGrid), 1);

    /// <inheritdoc/>
    protected override ILayoutManager CreateLayoutManager() => new ResponsiveGridManager(this);

    /// <summary>
    /// Column count for an inner width: spec wins (direct, then attached), otherwise auto-fit to
    /// <see cref="ItemWidth"/> (phones stay at one column unless a spec opts in). Pure function of
    /// the visible inputs — deterministic for lane 10's tests.
    /// </summary>
    public int ResolveColumns(double innerWidth)
    {
        var bucket = BreakpointScale.FromWidth(innerWidth);
        int cols;
        var spec = ColumnsSpec ?? AdaptiveLayout.GetColumns(this);
        if (!string.IsNullOrWhiteSpace(spec))
            cols = BreakpointScale.ValueForBucket(spec, bucket, 1);
        else if (ItemWidth > 0 && innerWidth > 0)
        {
            double gap = Math.Max(0, Spacing);
            cols = (int)Math.Floor((innerWidth + gap) / (ItemWidth + gap));
            if (bucket == Breakpoint.Narrow) cols = Math.Min(cols, 1);
        }
        else
            cols = 1;
        return Math.Clamp(cols, 1, MaxColumnCount);
    }

    /// <summary>One layout pass: column count, total content height, each tile's final frame.</summary>
    internal (int Columns, double Height, List<(IView View, Rect Frame)> Frames) Plan(double width)
    {
        var frames = new List<(IView, Rect)>();
        if (width <= 0 || double.IsNaN(width))
            return (1, 0, frames);

        double gap = Math.Max(0, Spacing);
        double inner = Math.Max(0, width - Horizontal(Padding));
        var cells = Children.OfType<IView>()
            .Where(c => c is not VisualElement ve || ve.IsVisible)
            .ToList();
        int cols = ResolveColumns(inner);
        SetValue(UsedColumnsProperty, cols);
        if (cells.Count == 0)
            return (cols, Vertical(Padding), frames);

        double slot = Math.Max(1, (inner - gap * (cols - 1)) / cols);
        bool rtl = AdaptiveLayout.IsRightToLeft(this);

        // Measure every tile at its final width (span included) so the row height is right first time.
        var desired = new Dictionary<IView, (int Span, Size Size)>();
        foreach (var c in cells)
        {
            int span = Math.Clamp(AdaptiveLayout.GetColumnSpan(c), 1, cols);
            double cellW = slot * span + gap * (span - 1);
            desired[c] = (span, c.Measure(cellW, double.PositiveInfinity));
        }

        double y = Padding.Top;
        var row = new List<(IView View, int Start)>();
        int col = 0;

        void Flush()
        {
            if (row.Count == 0) return;
            double rowH = row.Max(r => MeasureHeight(desired[r.View]));
            foreach (var (view, start) in row)
            {
                var (span, d) = desired[view];
                double cellW = slot * span + gap * (span - 1);
                double x = Padding.Left + start * (slot + gap);
                if (rtl) x = width - Padding.Right - start * (slot + gap) - cellW;

                var margin = view.Margin;
                double w = view.HorizontalLayoutAlignment == Microsoft.Maui.Primitives.LayoutAlignment.Fill
                    ? cellW : Math.Min(cellW, double.IsNaN(d.Width) ? cellW : d.Width);
                double hh = view.VerticalLayoutAlignment == Microsoft.Maui.Primitives.LayoutAlignment.Fill
                    ? rowH : (double.IsNaN(d.Height) || d.Height <= 0 ? rowH : d.Height);
                double ax = view.HorizontalLayoutAlignment switch
                {
                    Microsoft.Maui.Primitives.LayoutAlignment.Center => x + (cellW - w) / 2,
                    Microsoft.Maui.Primitives.LayoutAlignment.End => x + cellW - w,
                    _ => x,
                };
                double ay = view.VerticalLayoutAlignment switch
                {
                    Microsoft.Maui.Primitives.LayoutAlignment.Center => y + (rowH - hh) / 2,
                    Microsoft.Maui.Primitives.LayoutAlignment.End => y + rowH - hh,
                    _ => y,
                };
                frames.Add((view, new Rect(ax + margin.Left, ay + margin.Top,
                    Math.Max(0, w - Horizontal(margin)), Math.Max(0, hh - Vertical(margin)))));
            }
            y += rowH + gap;
            row.Clear();
            col = 0;
        }

        foreach (var c in cells)
        {
            var (span, _) = desired[c];
            if (col > 0 && col + span > cols) Flush();
            row.Add((c, col));
            col += span;
            if (col >= cols) Flush();
        }
        Flush();

        // Flush() advanced y past the last row's gap already; drop the trailing gap.
        double height = Math.Max(Vertical(Padding), y - gap);
        return (cols, height, frames);
    }

    private static double MeasureHeight((int Span, Size Size) d) =>
        double.IsNaN(d.Size.Height) || d.Size.Height <= 0 ? 1 : d.Size.Height;

    // Thickness.Horizontal/Vertical are not members on every head's Thickness — spelled locally
    // so the file never depends on which alias set resolved.
    private static double Horizontal(Thickness t) => t.Left + t.Right;
    private static double Vertical(Thickness t) => t.Top + t.Bottom;

    /// <summary>
    /// MAUI's supported custom-layout seam: the geometry lives here so the visual element itself
    /// stays thin. Deterministic: no caching, no lazy invalidation tricks. Derives from the public
    /// <see cref="LayoutManager"/> base (its ctor takes the ILayout it lays out).
    /// </summary>
    private sealed class ResponsiveGridManager : LayoutManager
    {
        private readonly ResponsiveGrid _grid;
        public ResponsiveGridManager(ResponsiveGrid grid) : base(grid) => _grid = grid;

        public override Size Measure(double width, double height)
        {
            var plan = _grid.Plan(width);
            return new Size(Math.Max(0, width), plan.Height);
        }

        public override Size ArrangeChildren(Rect rect)
        {
            var plan = _grid.Plan(rect.Width);
            foreach (var (view, frame) in plan.Frames)
                if (view is VisualElement ve)
                    ve.Arrange(frame);
            return new Size(Math.Max(0, rect.Width), plan.Height);
        }
    }
}
