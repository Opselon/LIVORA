using System.Windows.Input;

namespace LIVORA.Presentation.Components;

/// <summary>
/// Reusable inline empty state (lane 09; lanes 07/08 build against this name): one honest
/// sentence for "there is nothing here", plus an optional action. All text is supplied by the
/// HOST through the bindable properties (resolved against the page's ViewModel, like any other
/// control); the component itself holds zero strings.
/// </summary>
public partial class EmptyStateView : ContentView
{
    public EmptyStateView()
    {
        InitializeComponent();
        // The visual tree renders THIS view's bindable properties, not the page's VM: re-target
        // the inner panel's BindingContext in code (deterministic across handlers, unlike an
        // x:Reference self-cycle resolved during inflation).
        Body.BindingContext = this;
    }

    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(EmptyStateView), string.Empty);

    public static readonly BindableProperty MessageProperty =
        BindableProperty.Create(nameof(Message), typeof(string), typeof(EmptyStateView), string.Empty);

    public static readonly BindableProperty ActionTextProperty =
        BindableProperty.Create(nameof(ActionText), typeof(string), typeof(EmptyStateView), string.Empty);

    public static readonly BindableProperty ActionCommandProperty =
        BindableProperty.Create(nameof(ActionCommand), typeof(ICommand), typeof(EmptyStateView), null);

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }
}
