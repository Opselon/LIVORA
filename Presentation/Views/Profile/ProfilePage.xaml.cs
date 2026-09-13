using LIVORA.Presentation;
using LIVORA.Presentation.Views.Review;

namespace LIVORA.Presentation.Views;

/// <summary>
/// The account hub (see ProfileViewModel). The page owns the navigation the VM only asks for:
/// routes belong to other lanes, so a failed GoTo is reported honestly instead of dying as a
/// swallowed exception, and the weekly review keeps the Wave 2 modal flow (pushed from Today).
/// </summary>
public partial class ProfilePage : BaseContentPage
{
    /// <summary>Shell/tab constructor (ShellContent realizes this page parameterless).</summary>
    public ProfilePage() : this(ServiceHelper.Get<ProfileViewModel>()) { }

    /// <summary>DI constructor — the §0.8 shape.</summary>
    public ProfilePage(ProfileViewModel vm) : base(vm)
    {
        InitializeComponent();
        vm.NavigateRequested += route => _ = NavigateSafelyAsync(vm, route);
        vm.WeeklyReviewRequested += OpenWeeklyReview;
        vm.RestartOnboardingRequested += RestartOnboarding;
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    /// <summary>
    /// Go to a lane-owned route; when the owning lane's page isn't registered in this build,
    /// say so (one localized alert) instead of leaving a silent dead tap.
    /// </summary>
    private async Task NavigateSafelyAsync(ProfileViewModel vm, string route)
    {
        try
        {
            await Shell.Current.GoToAsync(route);
        }
        catch
        {
            await DisplayAlertAsync(vm.Title, vm.UnavailableMessage, L("Common.Done"));
        }
    }

    private void OpenWeeklyReview()
    {
        try { _ = Navigation.PushModalAsync(new WeeklySummaryPage()); }
        catch { /* the page failing to construct must not crash the hub */ }
    }

    /// <summary>
    /// After a confirmed wipe + "set up again": replace the window root with onboarding, exactly
    /// like a first launch. The boot seeder re-creates sample content on the next start — the
    /// wipe dialog already said so, and onboarding's own note labels it sample data (§honesty).
    /// </summary>
    private void RestartOnboarding()
    {
        var page = new OnboardingPage();
        page.FlowDirection = FlowDirection;
        if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault() is { } w)
            w.Page = page;
    }

    private static string L(string key)
    {
        var loc = ServiceHelper.TryGet<LIVORA.Application.Abstractions.ILocalizationService>();
        return loc?[key] ?? key;
    }
}
