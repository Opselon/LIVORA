using LIVORA.Presentation;
using LIVORA.Presentation.Views.Review;

namespace LIVORA.Presentation.Views;

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
                await vm.LoadAsync();
            }
        };
    }
}
