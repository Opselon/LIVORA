using LIVORA.Presentation;

namespace LIVORA.Presentation.Views.Review;

/// <summary>
/// Weekly review (issue #1). Reachable TWO ways and both must work:
/// <list type="bullet">
///   <item>modal — Today's "Review this week" button (existing flow, unchanged behaviour);</item>
///   <item>pushed — route <c>review</c> (Today's quick action + Profile's entry, via APPEND).</item>
/// </list>
/// Close therefore pops whichever stack it actually sits in, never guessing.
/// </summary>
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

    /// <summary>Pops the modal if opened modally, otherwise pops the pushed navigation stack.</summary>
    private void Close()
    {
        if (Navigation.ModalStack.Count > 0) { _ = Navigation.PopModalAsync(); return; }
        if (Navigation.NavigationStack.Count > 1) _ = Navigation.PopAsync();
    }
}
