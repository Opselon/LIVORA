using System.Windows.Input;
namespace LIVORA.Presentation.Components;

/// <summary>
/// StatChip (lane 05, Wave 3): a compact value+label pill for dashboards, filter rows and
/// per-metric badges (lanes 01/03/06/09 use it for status chips). One place decides the tone
/// language so a "caution" chip means the same thing on every page.
///
/// API (all BindableProperty, all live): <c>Text</c> (label), <c>Value</c>, <c>Tone</c>,
/// <c>Glyph</c>, <c>Command</c>/<c>CommandParameter</c>, <c>Selectable</c>.
///
/// Token discipline (§0.7): fills/strokes are brushes and are assigned to Border-typed
/// properties only; the two Labels get plain Colors installed with SetAppThemeColor, so a theme
/// flip re-resolves them without any brush ever touching a Color property. Named styles are used
/// for type/spacing; FontFamily stays untouched (BaseContentPage's language walk owns it).
/// </summary>
public partial class StatChip : ContentView
{
    public enum ChipTone { Neutral, Tint, Accent, Positive, Caution, Negative }

    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(StatChip), string.Empty,
            propertyChanged: (b, o, n) => ((StatChip)b).TextLabel.Text = (string?)n ?? string.Empty);

    public static readonly BindableProperty ValueProperty =
        BindableProperty.Create(nameof(Value), typeof(string), typeof(StatChip), null,
            propertyChanged: (b, o, n) => ((StatChip)b).OnValueChanged((string?)n));

    public static readonly BindableProperty ToneProperty =
        BindableProperty.Create(nameof(Tone), typeof(ChipTone), typeof(StatChip), ChipTone.Neutral,
            propertyChanged: (b, o, n) => ((StatChip)b).ApplyTone((ChipTone)n));

    public static readonly BindableProperty GlyphProperty =
        BindableProperty.Create(nameof(Glyph), typeof(ImageSource), typeof(StatChip), null,
            propertyChanged: (b, o, n) => ((StatChip)b).OnGlyphChanged((ImageSource?)n));

    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(StatChip), null,
            propertyChanged: (b, o, n) => ((StatChip)b).Root.Command = (ICommand?)n);

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(StatChip), null,
            propertyChanged: (b, o, n) => ((StatChip)b).Root.CommandParameter = n);

    /// <summary>True = the chip reacts (press state + command). False = pure display.</summary>
    public static readonly BindableProperty SelectableProperty =
        BindableProperty.Create(nameof(Selectable), typeof(bool), typeof(StatChip), true,
            propertyChanged: (b, o, n) => ((StatChip)b).Root.InvokeOnTap = (bool)n);

    public string Text
    {
        get => (string?)GetValue(TextProperty) ?? string.Empty;
        set => SetValue(TextProperty, value);
    }

    public string? Value
    {
        get => (string?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ChipTone Tone
    {
        get => (ChipTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public ImageSource? Glyph
    {
        get => (ImageSource?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public bool Selectable
    {
        get => (bool)GetValue(SelectableProperty);
        set => SetValue(SelectableProperty, value);
    }

    public StatChip()
    {
        InitializeComponent();
        // The value label is optional: hide its row entirely when unset so dense rows stay aligned.
        ValueLabel.IsVisible = false;
        Root.InvokeOnTap = Selectable;
        ApplyTone(Tone);
    }

    private void OnValueChanged(string? value)
    {
        ValueLabel.Text = value ?? string.Empty;
        ValueLabel.IsVisible = !string.IsNullOrEmpty(value);
    }

    private void OnGlyphChanged(ImageSource? source)
    {
        Icon.Source = source;
        Icon.IsVisible = source is not null;
    }

    // Token helpers with hex fallbacks == LivoraColors.xaml v2 values (dictionary always wins;
    // a fallback only protects against an unload-order surprise and can never render invisible).
    private static readonly Dictionary<string, string> _hex = new(StringComparer.Ordinal)
    {
        ["TextOnAccent"] = "#FFFFFF", ["TextSecondary"] = "#67655F", ["TextSecondaryDark"] = "#A5A29B",
        ["AccentText"] = "#2C5D53", ["PositiveDark"] = "#7CBBA9", ["CautionText"] = "#8A5F0D",
        ["CautionDark"] = "#E9B44E", ["NegativeText"] = "#8F3B2C", ["NegativeDark"] = "#E58773",
    };

    private void ApplyTone(ChipTone tone)
    {
        // fill = Border.Background (brush), stroke = Border.Stroke (brush),
        // text = Label.TextColor (Color via SetAppThemeColor) — never the other way round.
        switch (tone)
        {
            case ChipTone.Tint:
                Root.Background = Brush("AccentSoftBrush");
                Root.Stroke = Brush("OverlayBrush");
                Root.StrokeThickness = 1;
                Paint(Label1, "TextSecondary", "TextSecondaryDark");
                Paint(Label2, "TextSecondary", "TextSecondaryDark");
                break;
            case ChipTone.Accent:
                Root.Background = Brush("AccentBrush");
                Root.StrokeThickness = 0;
                Paint(Label1, "TextOnAccent", "TextOnAccent");
                Paint(Label2, "TextOnAccent", "TextOnAccent");
                break;
            case ChipTone.Positive:
                Root.Background = Brush("PositiveSoftBrush");
                Root.StrokeThickness = 0;
                Paint(Label1, "AccentText", "PositiveDark");
                Paint(Label2, "AccentText", "PositiveDark");
                break;
            case ChipTone.Caution:
                Root.Background = Brush("CautionSoftBrush");
                Root.StrokeThickness = 0;
                Paint(Label1, "CautionText", "CautionDark");
                Paint(Label2, "CautionText", "CautionDark");
                break;
            case ChipTone.Negative:
                Root.Background = Brush("NegativeSoftBrush");
                Root.StrokeThickness = 0;
                Paint(Label1, "NegativeText", "NegativeDark");
                Paint(Label2, "NegativeText", "NegativeDark");
                break;
            default: // Neutral — the page-default chip
                Root.Background = Brush("OverlayBrush");
                Root.StrokeThickness = 0;
                Paint(Label1, "TextSecondary", "TextSecondaryDark");
                Paint(Label2, "TextSecondary", "TextSecondaryDark");
                break;
        }
    }

    private void Paint(Label label, string lightKey, string darkKey) =>
        label.SetAppThemeColor(Label.TextColorProperty, ColorKey(lightKey), ColorKey(darkKey));

    private static SolidColorBrush Brush(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources is { } res
        && res.TryGetValue(key, out var v) && v is SolidColorBrush b ? b : new SolidColorBrush(Colors.Transparent);

    private static Color ColorKey(string key)
    {
        if (Microsoft.Maui.Controls.Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var v) && v is Color c) return c;
        return Color.FromArgb(_hex.TryGetValue(key, out var hex) ? hex : "#67655F");
    }

    // Convenience accessors so ApplyTone reads without Name collisions.
    private Label Label1 => ValueLabel;
    private Label Label2 => TextLabel;
}
