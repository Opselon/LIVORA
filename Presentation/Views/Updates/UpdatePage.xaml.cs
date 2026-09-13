using LIVORA.Presentation;
namespace LIVORA.Presentation.Views;
public partial class UpdatePage : BaseContentPage, IQueryAttributable
{
    public UpdatePage() : base(ServiceHelper.Get<UpdateViewModel>())
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (BindingContext is UpdateViewModel vm) await vm.LoadAsync();
        };
    }
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (BindingContext is UpdateViewModel vm) vm.ApplyQueryAttributes(query);
    }
}
