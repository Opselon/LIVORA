using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;
public partial class ProfilePage : BaseContentPage
{
    public ProfilePage() : base(ServiceHelper.Get<ProfileViewModel>())
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (BindingContext is ProfileViewModel vm) await vm.LoadAsync();
        };
    }
}
