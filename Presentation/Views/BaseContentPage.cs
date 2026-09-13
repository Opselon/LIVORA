using LIVORA.Presentation;
using Microsoft.Maui.Graphics; // IBrush (Border.Background is IBrush-typed; §0.7)

namespace LIVORA.Presentation.Views;
/// <summary>
/// Base page (non-generic — MAUI XAML does not support generic base types): resolves its
/// ViewModel from DI via the constructor and keeps FlowDirection + FontFamily in sync with
/// the active language, including content added dynamically (template rebuilds).
/// </summary>
public abstract class BaseContentPage : ContentPage
{
    protected BaseContentPage(ObservableObject vm)
    {
        BindingContext = vm;

        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ObservableObject.FlowDirection))
                FlowDirection = vm.FlowDirection;
            else if (e.PropertyName == nameof(ObservableObject.AppFont))
                ApplyFont(vm.AppFont);
        };

        FlowDirection = vm.FlowDirection;
        Loaded += (_, _) => ApplyFont(vm.AppFont);

        // Wave 3 responsiveness: cheap bucket check on every size event (no timers, no polling).
        // Recompute only when the wide/narrow decision actually flips, never per pixel.
        SizeChanged += (_, _) => ApplyResponsiveWidth();
    }

    // ---- Wide-window content centering (lane 04 hook surface) -----------------

    /// <summary>
    /// Set <c>views:BaseContentPage.MaxContentWidth="980"</c> on a page to clamp + center its
    /// content when the window is genuinely wide (desktop), instead of stretching text edge to
    /// edge. 0 (default) = page opts out entirely.
    /// </summary>
    public static readonly BindableProperty MaxContentWidthProperty =
        BindableProperty.CreateAttached("MaxContentWidth", typeof(double), typeof(BaseContentPage), 0d);

    public static double GetMaxContentWidth(BindableObject v) => (double)v.GetValue(MaxContentWidthProperty);
    public static void SetMaxContentWidth(BindableObject v, double value) => v.SetValue(MaxContentWidthProperty, value);

    private bool? _isWideLayout;

    private void ApplyResponsiveWidth()
    {
        double max = GetMaxContentWidth(this);
        if (max <= 0 || Content is null) return;
        if (double.IsNaN(Width) || Width <= 0) return;

        // +160dp of breathing room keeps split-view tablets at full bleed; only true desktop
        // windows enter the centered mode.
        bool wide = Width >= max + 160;
        if (wide == _isWideLayout) return; // recompute only on a real bucket change
        _isWideLayout = wide;

        var target = (VisualElement?)_busyHost ?? Content as VisualElement;
        if (target is null) return;
        target.WidthRequest = wide ? max : -1;
        if (target is View v) // HorizontalOptions is a View property, not VisualElement
            v.HorizontalOptions = wide ? LayoutOptions.Center : LayoutOptions.Fill;
    }

    // ---- Busy overlay (VMs drive it via the Bindable, no page references) -----

    /// <summary>
    /// Bind a page to its VM's busy flag: <c>views:BaseContentPage.IsLoading="{Binding IsBusy}"</c>.
    /// While loading, the content is dimmed, input-transparent, and a centered spinner rides on top.
    /// A page whose VM never sets it behaves exactly like before (the overlay is built on first true).
    /// Named IsLoading (not IsBusy) so it can't be confused with Shell's ContentPage.IsBusy.
    /// </summary>
    public static readonly BindableProperty IsLoadingProperty =
        BindableProperty.Create(nameof(IsLoading), typeof(bool), typeof(BaseContentPage), false,
            propertyChanged: (b, o, n) => ((BaseContentPage)b).SetLoading((bool)n));

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    private Grid? _busyHost;
    private VisualElement? _pageContent;
    private Border? _busyScrim;
    private ActivityIndicator? _busySpinner;

    /// <summary>Idempotent busy-state helper: builds the overlay lazily, reuses it after.</summary>
    protected void SetLoading(bool loading)
    {
        if (loading && _busyHost is null)
        {
            if (Content is not VisualElement original) return; // nothing to guard yet
            _pageContent = original;
            Content = null;

            _busySpinner = new ActivityIndicator
            {
                IsRunning = true,
                IsVisible = false,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                HeightRequest = 44,
                WidthRequest = 44,
                Color = Theme.Accent,
            };
            // Token brush when resolvable; the black fallback is a resource-failure safety net,
            // not a design choice. Opacity keeps it a wash, not a wall.
            // MAUI brushes are Microsoft.Maui.Controls.Brush (IBrush is a WinUI-only type).
            Brush scrimBrush = new SolidColorBrush(Colors.Black);
            if (Microsoft.Maui.Controls.Application.Current?.Resources is ResourceDictionary appRes
                && appRes.TryGetValue("OverlayBrush", out var resObj) && resObj is Brush b)
                scrimBrush = b;
            var scrim = new Border
            {
                Background = scrimBrush,
                Opacity = 0.55,
                StrokeThickness = 0,
                Content = _busySpinner,
            };
            _busyScrim = scrim;

            _busyHost = new Grid();
            _busyHost.Children.Add(_pageContent);
            _busyHost.Children.Add(scrim);
            Content = _busyHost;
            ApplyFont((BindingContext as ObservableObject)?.AppFont ?? string.Empty);
            _isWideLayout = null; // the wrap changed the layout root — let the width hook re-apply
            ApplyResponsiveWidth();
        }

        if (_busyHost is null || _pageContent is null || _busyScrim is null || _busySpinner is null) return;
        _busyScrim.IsVisible = loading;
        _busySpinner.IsVisible = loading;
        _busySpinner.IsRunning = loading;
        _pageContent.InputTransparent = loading;
    }

    /// <summary>Applies the active language's font across the content tree.</summary>
    protected void ApplyFont(string family)
    {
        if (Content is null) return;
        StyleElement(Content, family);
        if (Content is VisualElement root) Walk(root, family);
    }

    private static void Walk(VisualElement el, string family)
    {
        switch (el)
        {
            case Layout layout:
                foreach (var child in layout.Children.OfType<VisualElement>())
                {
                    StyleElement(child, family);
                    Walk(child, family);
                }
                break;
            case Border border when border.Content is VisualElement bc:
                StyleElement(bc, family);
                Walk(bc, family);
                break;
            case ScrollView sv when sv.Content is VisualElement sc:
                StyleElement(sc, family);
                Walk(sc, family);
                break;
            case ContentView cv when cv.Content is VisualElement cc:
                StyleElement(cc, family);
                Walk(cc, family);
                break;
            // CollectionView items are realized as logical children of the control (they are not
            // a Layout), so the case above misses them. Only materialized items are walked —
            // recycled items get the font via inherited-property defaults when realized.
            case CollectionView cvw:
                foreach (var item in ((IElementController)cvw).LogicalChildren.OfType<VisualElement>())
                {
                    StyleElement(item, family);
                    Walk(item, family);
                }
                break;
            // Note: BindableLayout-attached panels realize their items directly into the host
            // Layout.Children, so the Layout case already covers them — no extra branch needed.
        }
    }

    private static void StyleElement(object element, string family)
    {
        switch (element)
        {
            case Label l when !GetFontFamilyOverride(l): l.FontFamily = family; break;
            case Button b: b.FontFamily = family; break;
            case Entry e: e.FontFamily = family; break;
            case Editor ed: ed.FontFamily = family; break;
        }
    }

    /// <summary>Set FontFamilyOverride="True" on a Label to keep its own font (e.g. brand wordmark).</summary>
    public static readonly BindableProperty FontFamilyOverrideProperty =
        BindableProperty.CreateAttached("FontFamilyOverride", typeof(bool), typeof(BaseContentPage), false);

    public static bool GetFontFamilyOverride(BindableObject v) => (bool)v.GetValue(FontFamilyOverrideProperty);
    public static void SetFontFamilyOverride(BindableObject v, bool value) => v.SetValue(FontFamilyOverrideProperty, value);
}
