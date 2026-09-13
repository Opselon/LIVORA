using System;
using System.Collections.Generic;
using System.Linq;

namespace LIVORA.Presentation.Responsive;

/// <summary>
/// The app-wide responsive glue (lane 04): attached properties that let a page declare its desktop
/// behaviour once, plus the single hook <see cref="Attach"/> that <c>BaseContentPage</c> calls so
/// every page knows its breakpoint without any per-page code.
///
/// Rules this file is built around (LANES.md §Lane 04):
///   • driven by <c>Page.SizeChanged</c> only — no timers, no polling;
///   • the attached breakpoint value is written ONLY when the width bucket actually changes, so
///     dragging a window edge never thrashes bindings;
///   • <see cref="MaxContentWidthProperty"/> centers the page content on wide windows instead of
///     stretching a paragraph across 1600 px (the "blown-up phone" the client rejected).
///
/// Everything is a real attached <see cref="BindableProperty"/>, so XAML can just write it:
///   <c>resp:AdaptiveLayout.Columns="1,2,3"</c> on a <see cref="ResponsiveGrid"/>,
///   <c>resp:AdaptiveLayout.ColumnSpan="2"</c> on a tile,
///   and bind off <c>AdaptiveLayout.Breakpoint</c> (see <see cref="BreakpointToBoolConverter"/>).
/// </summary>
public static class AdaptiveLayout
{
    /// <summary>Reading measure the desktop layout aims for (LANES.md §Lane 04: 980 effective px).</summary>
    public const double DefaultMaxContentWidth = 980;

    // ---- Breakpoint (attached to the Page; written by Attach, read by everyone) ----------

    public static readonly BindableProperty BreakpointProperty =
        BindableProperty.CreateAttached("Breakpoint", typeof(Breakpoint), typeof(AdaptiveLayout),
            Breakpoint.Narrow);

    public static Breakpoint GetBreakpoint(BindableObject view) => (Breakpoint)view.GetValue(BreakpointProperty);

    internal static void SetBreakpoint(BindableObject view, Breakpoint value) =>
        view.SetValue(BreakpointProperty, value);

    /// <summary>
    /// Breakpoint of the page an element lives in — one answer for grid maths and XAML bindings.
    /// Walks the logical tree so a deep label reports the same bucket its page set; falls back to
    /// the element's own last known width, then to Narrow, so a control not inside a page yet
    /// still lays out safely.
    /// </summary>
    public static Breakpoint BreakpointOf(VisualElement? element)
    {
        for (Element? el = element; el is not null; el = el.Parent)
        {
            if (el is Page page) return GetBreakpoint(page);
            if (el is Window window) return BreakpointScale.FromWidth(window.Width);
        }
        return element is null ? Breakpoint.Narrow : BreakpointScale.FromWidth(element.Width);
    }

    /// <summary>True when the element renders right-to-left (the mirroring flag for layout maths).</summary>
    public static bool IsRightToLeft(VisualElement element) =>
        ((IVisualElementController)element).EffectiveFlowDirection
            .HasFlag(EffectiveFlowDirection.RightToLeft);

    // ---- Columns spec (attached): "narrow,medium,wide", e.g. "1,2,3" ----------------------

    public static readonly BindableProperty ColumnsProperty =
        BindableProperty.CreateAttached("Columns", typeof(string), typeof(AdaptiveLayout),
            defaultValue: null, propertyChanged: (b, _, _) => InvalidateLayoutOf(b));

    public static string? GetColumns(BindableObject view) => (string?)view.GetValue(ColumnsProperty);
    public static void SetColumns(BindableObject view, string? value) => view.SetValue(ColumnsProperty, value);

    /// <summary>Resolve a spec (element's own attached value wins over the fallback string).</summary>
    public static int ResolveColumns(VisualElement element, string? fallbackSpec = null, int fallback = 1) =>
        BreakpointScale.ValueForBucket(GetColumns(element) ?? fallbackSpec, BreakpointOf(element), fallback);

    // ---- Column span (attached; same shape as Grid.ColumnSpan so XAML reads naturally) -----

    public static readonly BindableProperty ColumnSpanProperty =
        BindableProperty.CreateAttached("ColumnSpan", typeof(int), typeof(AdaptiveLayout), 1,
            validateValue: (_, v) => v is int i && i >= 1 && i <= 12,
            propertyChanged: (b, _, _) => InvalidateLayoutOf(b));

    public static int GetColumnSpan(BindableObject view) => (int)view.GetValue(ColumnSpanProperty);
    public static void SetColumnSpan(BindableObject view, int value) => view.SetValue(ColumnSpanProperty, value);

    /// <summary>Span of a layout child (an IView that is not a BindableObject spans one column).</summary>
    public static int GetColumnSpan(IView view) => view is BindableObject b ? GetColumnSpan(b) : 1;

    // ---- MaxContentWidth (attached to the Page): the desktop reading measure ----------------
    // Default-on: EVERY page centers its content at 980 px on wide windows (the finished-goodly
    // bar). A page opts out with resp:AdaptiveLayout.MaxContentWidth="0" (any value ≤ 0 means
    // "let me stretch, I know what I'm doing" — e.g. a future true multi-pane desktop page).

    public static readonly BindableProperty MaxContentWidthProperty =
        BindableProperty.CreateAttached("MaxContentWidth", typeof(double), typeof(AdaptiveLayout),
            DefaultMaxContentWidth,
            propertyChanged: (b, _, _) =>
            {
                if (b is Page p && p.Width > 0) ReapplyCentering(p, force: true);
            });

    public static double GetMaxContentWidth(BindableObject view) => (double)view.GetValue(MaxContentWidthProperty);
    public static void SetMaxContentWidth(BindableObject view, double value) => view.SetValue(MaxContentWidthProperty, value);

    // ---- The per-page hook ------------------------------------------------------------

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Page, PageState> _pages = new();

    /// <summary>
    /// Start watching a page's width: publishes <see cref="BreakpointProperty"/> (bucket changes
    /// only) and keeps the reading measure applied. Called exactly once from
    /// <c>BaseContentPage</c>'s constructor — the one-line APPEND hook lane 04 ships to lane 06;
    /// repeated calls are ignored. State is weak per page, so no window outlives its bookkeeping.
    /// </summary>
    public static void Attach(Page page)
    {
        var state = _pages.GetValue(page, _ => new PageState());
        if (state.Hooked) return;
        state.Hooked = true;

        page.SizeChanged += (_, _) => OnPageSizeChanged(page);
    }

    private static void OnPageSizeChanged(Page page)
    {
        double width = page.Width;
        if (double.IsNaN(width) || width <= 0) return;

        var bucket = BreakpointScale.FromWidth(width);
        if (GetBreakpoint(page) != bucket)
        {
            SetBreakpoint(page, bucket);      // bucket-only writes: bindings fire on real changes
            ((IView)page).InvalidateMeasure(); // the subtree re-reads its column maths once
        }

        ReapplyCentering(page, force: false);
    }

    private static void ReapplyCentering(Page page, bool force)
    {
        var state = _pages.GetValue(page, _ => new PageState());
        var max = GetMaxContentWidth(page);
        double width = page.Width;
        if (double.IsNaN(width) || width <= 0) return;

        if (!state.CapturedBaseline)
        {
            // Capture on the first real layout pass — after XAML finished authoring Padding, so a
            // page that DOES set its own padding keeps it; we only ever add/remove our gutter on
            // top of the authored value.
            state.AuthoredPadding = page.Padding;
            state.CapturedBaseline = true;
        }

        double side = max > 0 && width > max ? (width - max) / 2 : 0;
        if (Math.Abs(state.LastSide - side) < 0.5 && !force) return;
        state.LastSide = side;

        var b = state.AuthoredPadding;
        page.Padding = new Thickness(b.Left + side, b.Top, b.Right + side, b.Bottom);
    }

    private static void InvalidateLayoutOf(object bindable)
    {
        // Re-run the measure pass for whoever consumes attached specs. IView.InvalidateMeasure is
        // the public surface (protected on VisualElement); the parent is nudged too so a spec on
        // a leaf still re-waters its owning container's maths.
        if (bindable is IView view) view.InvalidateMeasure();
        if (bindable is VisualElement { Parent: IView parent }) parent.InvalidateMeasure();
    }

    private sealed class PageState
    {
        public bool Hooked;
        public bool CapturedBaseline;
        public Thickness AuthoredPadding;
        public double LastSide = double.NaN;
    }
}
