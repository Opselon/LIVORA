using LIVORA.Application.Abstractions;
using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Pushed program page (route <c>bootcamp-detail?id=…</c>). Reachable both by ctor (DI) and by
/// route: the <see cref="QueryPropertyAttribute"/> writes the id onto the ViewModel before the
/// first load, and the VM then reads the program from the shared repository — never a copy of it.
/// </summary>
[QueryProperty(nameof(ProgramId), "id")]
public partial class BootcampDetailPage : BaseContentPage
{
    private bool _firstLoadDone;

    public BootcampDetailPage() : base(ServiceHelper.Get<BootcampDetailViewModel>())
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (BindingContext is BootcampDetailViewModel vm)
            {
                await vm.LoadAsync();
                _firstLoadDone = true;
            }
        };
    }

    /// <summary>
    /// Route bridge. Shell applies query properties either before or after the first load
    /// depending on version, so a late write only reloads when the page already loaded once —
    /// that keeps a fresh push to a single repository round-trip.
    /// </summary>
    public string? ProgramId
    {
        get => (BindingContext as BootcampDetailViewModel)?.ProgramId;
        set
        {
            if (BindingContext is not BootcampDetailViewModel vm) return;
            if (vm.ProgramId == value && _firstLoadDone) return;
            vm.ProgramId = value;
            if (_firstLoadDone)
                MainThread.BeginInvokeOnMainThread(async () => await vm.LoadAsync());
        }
    }
}
