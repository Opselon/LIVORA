using LIVORA.Application.Abstractions;
using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Week progress block (lane 08) hosted by the weekly review page. It owns its own view-model —
/// resolved from DI — so the host only drops the view into the layout and (optionally) calls
/// <see cref="RefreshAsync"/> when its own reload cycle runs. The VM keeps the honesty rule: with
/// fewer than three days of history the bars stay hidden and the "not enough history" note shows.
/// </summary>
public partial class WeekProgressView : ContentView
{
    private readonly WeekProgressViewModel _vm;

    public WeekProgressView()
    {
        _vm = ServiceHelper.Get<WeekProgressViewModel>();
        BindingContext = _vm;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    /// <summary>Rebuild the bars for the week containing <paramref name="referenceDay"/> (default: today).</summary>
    public Task RefreshAsync(DateTime? referenceDay = null) => _vm.LoadAsync(referenceDay ?? DateTime.Today);
}
