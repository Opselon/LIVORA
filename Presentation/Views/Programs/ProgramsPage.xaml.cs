using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;
public partial class ProgramsPage : BaseContentPage
{
    public ProgramsPage() : base(ServiceHelper.Get<ProgramsViewModel>())
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (BindingContext is ProgramsViewModel vm) await vm.LoadAsync();
        };
    }
}
