using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;
public partial class OnboardingPage : BaseContentPage
{
    public OnboardingPage() : base(ServiceHelper.Get<OnboardingViewModel>())
    {
        InitializeComponent();
    }
}
