using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;
public partial class HealthPage : BaseContentPage
{
    public HealthPage() : base(ServiceHelper.Get<HealthViewModel>())
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (BindingContext is HealthViewModel vm) await vm.LoadAsync();
        };
    }
}
