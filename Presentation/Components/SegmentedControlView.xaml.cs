using System.Collections;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Infrastructure.Localization;

namespace LIVORA.Presentation.Components;

/// <summary>
/// SegmentedControlView (lane 05, Wave 3): single-select pill rail used by theme mode,
/// language switching and filter rows (lanes 01/03/06/09 code against §4's name).
///
/// API: <c>ItemsSource</c> of <see cref="SegmentItem"/> (Value + Text or localization Key),
/// <c>SelectedItem</c> (TwoWay bindable), <c>SelectedIndex</c>, <c>SelectionChanged</c>,
/// and an optional <c>Command</c> executed with the new segment's Value.
///
/// Rules baked in:
///  • cells are plain Border+Label built in code (count/labels are data-driven; no
///    ControlTemplate pitfalls, identical behavior on all four heads);
///  • language changes re-resolve <see cref="SegmentItem.Key"/> captions live — the LanguageHook
///    delegate is kept in a field so the weak subscription stays alive for this instance's life;
///  • selection is marked by FILL + bold weight, never color alone (accessibility); brushes go
///    on Border-typed properties, Labels get themed Colors via SetAppThemeColor (§0.7);
///  • equal-width star columns mirror automatically under RTL — the selected segment is marked by
///    fill, not by a directional slide, so EN and fa read identically (§0.6).
/// </summary>
public partial class SegmentedControlView : ContentView
{
    public static readonly BindableProperty ItemsSourceProperty =
        BindableProperty.Create(nameof(ItemsSource), typeof(IList), typeof(SegmentedControlView), null,
            propertyChanged: (b, o, n) => ((SegmentedControlView)b).Rebuild());

    public static readonly BindableProperty SelectedItemProperty =
        BindableProperty.Create(nameof(SelectedItem), typeof(object), typeof(SegmentedControlView), null,
            BindingMode.TwoWay,
            propertyChanged: (b, o, n) => ((SegmentedControlView)b).OnSelectedItemChanged());

    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(SegmentedControlView), null);

    /// <summary>Segment collection (IList so a plain List&lt;SegmentItem&gt; binds directly).</summary>
    public IList? ItemsSource
    {
        get => (IList?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>The selected <see cref="SegmentItem"/>. TwoWay-bindable from a view model.</summary>
    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public int SelectedIndex
    {
        get
        {
            var items = ItemsSource;
            var sel = SelectedItem;
            if (items is null || sel is null) return -1;
            for (int i = 0; i < items.Count; i++)
                if (Equals(items[i], sel)) return i;
            return -1;
        }
    }

    /// <summary>Raised whenever the resolved index changes (tap or programmatic selection).</summary>
    public event EventHandler<int>? SelectedIndexChanged;

    /// <summary>Raised with the new segment's Value (the machine tag a VM switches on).</summary>
    public event EventHandler<object?>? SelectionChanged;

    // Field-kept so LanguageHook's WEAK list can still see this instance's handler.
    private Action? _languageHandler;
    private readonly List<Action> _captionRefreshers = new();

    public SegmentedControlView()
    {
        InitializeComponent();
        _languageHandler = OnLanguageChanged;
        LanguageHook.Subscribe(_languageHandler);
        Rebuild();
    }

    private void OnLanguageChanged() => Dispatcher.Dispatch(RefreshCaptions);

    private void Rebuild()
    {
        Segments.Children.Clear();
        Segments.ColumnDefinitions.Clear();
        _captionRefreshers.Clear();

        var items = ItemsSource;
        if (items is null || items.Count == 0) return;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not SegmentItem item) continue;
            Segments.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var border = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 13 },
                Padding = new Thickness(10, 9),
                MinimumHeightRequest = 38,
                StrokeThickness = 0,
                BindingContext = item,
            };
            border.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Select(item)) });

            var label = new Label { Text = ResolveCaption(item) };
            // Prefer the §2 SegmentText style (size/weight/alignment/truncation from the design
            // system); keep hand-set defaults only as the resource-less fallback (unit tests,
            // early inflation) so the control never renders unstyled.
            if (Microsoft.Maui.Controls.Application.Current?.Resources is { } res
                && res.TryGetValue("SegmentText", out var segStyleObj) && segStyleObj is Style segStyle)
            {
                label.Style = segStyle;
            }
            else
            {
                label.FontSize = 13;
                label.HorizontalOptions = LayoutOptions.Center;
                label.VerticalOptions = LayoutOptions.Center;
                label.HorizontalTextAlignment = TextAlignment.Center;
                label.VerticalTextAlignment = TextAlignment.Center;
                label.LineBreakMode = LineBreakMode.TailTruncation;
                label.MaxLines = 1;
            }
            border.Content = label;
            Segments.Children.Add(border);
            Grid.SetColumn(border, i);
            _captionRefreshers.Add(() => label.Text = ResolveCaption(item));
        }
        Paint();
    }

    /// <summary>Re-resolve Key-based captions after a language switch (no structural rebuild).</summary>
    private void RefreshCaptions()
    {
        foreach (var refresh in _captionRefreshers) refresh();
    }

    private static string ResolveCaption(SegmentItem item)
    {
        if (!string.IsNullOrEmpty(item.Key))
        {
            var loc = ServiceHelper.TryGet<ILocalizationService>();
            if (loc is not null) return loc[item.Key];
        }
        return item.Text ?? string.Empty;
    }

    /// <summary>Select <paramref name="item"/> and announce it (the tap path).</summary>
    public void Select(SegmentItem item)
    {
        if (item is null) return;
        bool changed = !Equals(SelectedItem, item);
        SelectedItem = item;
        if (changed)
        {
            SelectionChanged?.Invoke(this, item.Value);
            if (Command?.CanExecute(item.Value) == true)
                Command.Execute(item.Value);
        }
    }

    private void OnSelectedItemChanged()
    {
        Paint();
        SelectedIndexChanged?.Invoke(this, SelectedIndex);
    }

    private void Paint()
    {
        var sel = SelectedItem;
        foreach (var child in Segments.Children)
        {
            if (child is not Border border || border.BindingContext is not SegmentItem item) continue;
            bool on = Equals(item, sel);
            border.Background = on ? Brush("AccentBrush") : new SolidColorBrush(Colors.Transparent);
            if (border.Content is Label label)
            {
                label.SetAppThemeColor(Label.TextColorProperty,
                    on ? ColorKey("TextOnAccent") : ColorKey("TextSecondary"),
                    on ? ColorKey("TextOnAccent") : ColorKey("TextSecondaryDark"));
                label.FontAttributes = on ? FontAttributes.Bold : FontAttributes.None;
            }
        }
    }

    private static SolidColorBrush Brush(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources is { } res
        && res.TryGetValue(key, out var v) && v is SolidColorBrush b ? b : new SolidColorBrush(Colors.Transparent);

    private static Color ColorKey(string key)
    {
        if (Microsoft.Maui.Controls.Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var v) && v is Color c) return c;
        // Fallbacks == LivoraColors.xaml v2 (never Transparent: invisible labels are worse than
        // off-brand ones). The three keys above are all this control reads.
        return Color.FromArgb(key switch
        {
            "TextOnAccent" => "#FFFFFF",
            "TextSecondaryDark" => "#A5A29B",
            _ => "#67655F",
        });
    }
}

/// <summary>One segment: machine <see cref="Value"/> for callers, plus Text or a localization Key.</summary>
public sealed class SegmentItem
{
    public object? Value { get; init; }
    /// <summary>Literal caption (already localized by the caller, e.g. "فارسی").</summary>
    public string? Text { get; init; }
    /// <summary>Localization key resolved live — preferred for static labels (§0.4).</summary>
    public string? Key { get; init; }

    public SegmentItem() { }
    public SegmentItem(object? value, string? text = null, string? key = null)
    {
        Value = value; Text = text; Key = key;
    }
}
