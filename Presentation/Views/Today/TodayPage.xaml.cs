using LIVORA.Presentation;
using LIVORA.Presentation.Views.Review;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Today (Wave 3): pull-to-refresh + the pipeline. The weekly review keeps its existing modal
/// entry (button) and gains route navigation through the quick actions; both paths are wired
/// here because this page owns the modal presentation.
/// </summary>
public partial class TodayPage : BaseContentPage
{
    public TodayPage() : base(ServiceHelper.Get<TodayViewModel>())
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (BindingContext is TodayViewModel vm)
            {
                vm.OpenWeeklyReview = async () =>
                {
                    await Navigation.PushModalAsync(new WeeklySummaryPage());
                };
                // Route pushes (log-entry / reminders / updates) go through Shell. MAUI versions
                // disagree on whether an unregistered route throws or fails silently, so the page
                // verifies the navigation actually moved and reports back — the VM then shows the
                // honest "not available yet" line instead of a dead tap.
                vm.OpenRoute = async route =>
                {
                    var shell = Shell.Current;
                    if (shell is null) return false;
                    var before = shell.CurrentState?.Location?.OriginalString ?? string.Empty;
                    try { await shell.GoToAsync(route); }
                    catch { return false; }
                    var after = shell.CurrentState?.Location?.OriginalString ?? string.Empty;
                    return after != before || after.Contains(route, StringComparison.OrdinalIgnoreCase);
                };
                await vm.LoadAsync();
            }
        };
    }
}
