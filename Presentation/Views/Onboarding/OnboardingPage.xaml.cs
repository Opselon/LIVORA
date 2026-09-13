using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// First-run flow. The VM owns the steps and the profile writes; the page owns the window-root
/// swap to the shell once the flow completes (the Wave 2 behavior, moved out of the VM where it
/// belonged to a page/window concern).
/// </summary>
public partial class OnboardingPage : BaseContentPage
{
    public OnboardingPage() : base(ServiceHelper.Get<OnboardingViewModel>())
    {
        InitializeComponent();
        ((OnboardingViewModel)BindingContext).Completed += OpenShell;
    }

    private void OpenShell()
    {
        if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault() is { } w)
            w.Page = new AppShell();
    }
}
