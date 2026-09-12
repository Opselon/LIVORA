using LIVORA.Presentation;

namespace LIVORA.Presentation.Views.Review;

public partial class WeeklySummaryPage : BaseContentPage
{
    public WeeklySummaryPage() : base(ServiceHelper.Get<WeeklySummaryViewModel>())
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (BindingContext is WeeklySummaryViewModel vm)
            {
                vm.CloseRequested += Close;
                await vm.LoadAsync();
            }
        };
    }

    /// <summary>Opened modally from Today; pops itself.</summary>
    private void Close()
    {
        if (Navigation.ModalStack.Count > 0) _ = Navigation.PopModalAsync();
    }
}
