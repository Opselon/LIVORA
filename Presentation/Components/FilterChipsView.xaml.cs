using System.ComponentModel;
using System.Windows.Input;

namespace LIVORA.Presentation.Components;

/// <summary>One chip in a <see cref="FilterChipsView"/> row: a value (machine key), an already
/// localized label, and its selection state. Deliberately tiny and observable so the row can be
/// rebuilt or reused by any lane without inventing its own helper type.</summary>
public sealed class FilterChipItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Value { get; init; }
    public required string Label { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>
/// Reusable horizontal filter chips (lane 09; shared with lanes 07/08). The host owns the items
/// (labels already localized) and receives taps through <see cref="SelectionChangedCommand"/>
/// carrying the tapped <see cref="FilterChipItem"/>. Selection is enforced single-choice here so
/// every lane gets identical behaviour: tapping a chip selects it and clears the others.
/// </summary>
public partial class FilterChipsView : ContentView
{
    public FilterChipsView()
    {
        InitializeComponent();
        BindingContext = this;
    }

    public static readonly BindableProperty ItemsSourceProperty =
        BindableProperty.Create(nameof(ItemsSource), typeof(IReadOnlyList<FilterChipItem>),
            typeof(FilterChipsView), null, propertyChanged: OnItemsSourceChanged);

    public static readonly BindableProperty SelectedValueProperty =
        BindableProperty.Create(nameof(SelectedValue), typeof(string), typeof(FilterChipsView),
            null, BindingMode.TwoWay, propertyChanged: OnSelectedValueChanged);

    public static readonly BindableProperty SelectionChangedCommandProperty =
        BindableProperty.Create(nameof(SelectionChangedCommand), typeof(ICommand),
            typeof(FilterChipsView), null);

    /// <summary>The chips to render. Hosts rebuild this list (or flip IsSelected) as needed.</summary>
    public IReadOnlyList<FilterChipItem>? ItemsSource
    {
        get => (IReadOnlyList<FilterChipItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Machine key of the selected chip (null = none selected).</summary>
    public string? SelectedValue
    {
        get => (string?)GetValue(SelectedValueProperty);
        set => SetValue(SelectedValueProperty, value);
    }

    /// <summary>Executed with the tapped <see cref="FilterChipItem"/> after selection updates.</summary>
    public ICommand? SelectionChangedCommand
    {
        get => (ICommand?)GetValue(SelectionChangedCommandProperty);
        set => SetValue(SelectionChangedCommandProperty, value);
    }

    private static void OnItemsSourceChanged(BindableObject bindable, object oldValue, object newValue)
        => ((FilterChipsView)bindable).SyncSelectionVisuals();

    private static void OnSelectedValueChanged(BindableObject bindable, object oldValue, object newValue)
        => ((FilterChipsView)bindable).SyncSelectionVisuals();

    private void OnChipTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not FilterChipItem chip) return;
        SelectedValue = chip.Value;   // fires SyncSelectionVisuals through the property changed hook
        if (SelectionChangedCommand?.CanExecute(chip) == true) SelectionChangedCommand.Execute(chip);
    }

    /// <summary>Push SelectedValue into the items so the DataTriggers render the selection.</summary>
    private void SyncSelectionVisuals()
    {
        if (ItemsSource is null) return;
        foreach (var item in ItemsSource)
            item.IsSelected = string.Equals(item.Value, SelectedValue, StringComparison.Ordinal);
    }
}
