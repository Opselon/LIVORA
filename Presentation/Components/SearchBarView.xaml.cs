using System.Windows.Input;

namespace LIVORA.Presentation.Components;

/// <summary>
/// Reusable search field wrapper (lane 09; shared with lanes 07/08). Plain MAUI SearchBar inside
/// a token-styled border. Hosts bind <see cref="Text"/> (two-way), a localized
/// <see cref="Placeholder"/>, and an optional <see cref="SearchCommand"/> that fires when the
/// keyboard search key is pressed. No strings live here.
/// </summary>
public partial class SearchBarView : ContentView
{
    public SearchBarView()
    {
        InitializeComponent();
        BindingContext = this;
    }

    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(nameof(Text), typeof(string), typeof(SearchBarView), string.Empty);

    public static readonly BindableProperty PlaceholderProperty =
        BindableProperty.Create(nameof(Placeholder), typeof(string), typeof(SearchBarView), string.Empty);

    public static readonly BindableProperty SearchCommandProperty =
        BindableProperty.Create(nameof(SearchCommand), typeof(ICommand), typeof(SearchBarView), null);

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>Executed with the current <see cref="Text"/> when the search key is pressed.</summary>
    public ICommand? SearchCommand
    {
        get => (ICommand?)GetValue(SearchCommandProperty);
        set => SetValue(SearchCommandProperty, value);
    }

    private void OnSearchButtonPressed(object? sender, EventArgs e)
    {
        if (SearchCommand?.CanExecute(Text) == true) SearchCommand.Execute(Text);
    }
}
