using LIVORA.Application.Abstractions;
using LIVORA.Presentation;
using LIVORA.Presentation.Views;

namespace LIVORA;
public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var loc = ServiceHelper.Get<ILocalizationService>();
        var settings = ServiceHelper.Get<ISettingsService>();

        var onboardingDone = settings.OnboardingCompleted;
        var page = onboardingDone ? (Page)new AppShell() : new OnboardingPage();
        // The Shell itself (tabs/flyout chrome) needs the direction set at creation —
        // child pages set their own in BaseContentPage.
        page.FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return new Window(page);
    }

    /// <summary>
    /// Applies the active language's flow direction to the running app (Shell + all open pages).
    /// Called once at startup and again whenever the language changes — no restart required.
    /// </summary>
    public static void ApplyFlowDirection()
    {
        var loc = ServiceHelper.Get<ILocalizationService>();
        var flow = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        if (Microsoft.Maui.Controls.Application.Current?.Windows is { } windows)
        {
            foreach (var w in windows)
            {
                if (w.Page is not null) w.Page.FlowDirection = flow;
            }
        }
        // Defensive: also set the static default so newly opened pages inherit it.
        Microsoft.Maui.Controls.Application.Current?.Resources["AppFlowDirection"] = flow;
    }
}
