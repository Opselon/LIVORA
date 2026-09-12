using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;
public partial class GoalsPage : BaseContentPage
{
    public GoalsPage() : base(ServiceHelper.Get<GoalsViewModel>())
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (BindingContext is GoalsViewModel vm) await vm.LoadAsync();
        };
    }
}
