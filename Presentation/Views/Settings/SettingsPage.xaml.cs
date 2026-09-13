using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Second-level settings hub (pushed from Profile via route "settings"). Owns the navigation the
/// VM requests — a missing lane-owned route is reported honestly instead of being a dead tap.
/// </summary>
public partial class SettingsPage : BaseContentPage
{
    /// <summary>Shell-route constructor (route "settings" resolves this page parameterless).</summary>
    public SettingsPage() : this(ServiceHelper.Get<SettingsViewModel>()) { }

    /// <summary>DI constructor — the §0.8 shape, used when the container injects the VM.</summary>
    public SettingsPage(SettingsViewModel vm) : base(vm)
    {
        InitializeComponent();
        vm.NavigateRequested += route => _ = NavigateSafelyAsync(vm, route);
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    private async Task NavigateSafelyAsync(SettingsViewModel vm, string route)
    {
        try
        {
            await Shell.Current.GoToAsync(route);
        }
        catch
        {
            await DisplayAlertAsync(vm.Title, vm.UnavailableMessage, Tr("Common.Done"));
        }
    }

    private static string Tr(string key)
    {
        var loc = ServiceHelper.TryGet<LIVORA.Application.Abstractions.ILocalizationService>();
        return loc?[key] ?? key;
    }
}
