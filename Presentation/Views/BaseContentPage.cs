using LIVORA.Presentation;

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
