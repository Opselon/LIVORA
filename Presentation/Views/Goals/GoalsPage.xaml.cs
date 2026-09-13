using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;
public partial class GoalsPage : BaseContentPage
{
    public GoalsPage() : base(ServiceHelper.Get<GoalsViewModel>())
    {
        InitializeComponent();
    }

    /// <summary>
    /// Refresh on every appearance, not just the first: the editors are pushed pages, so coming
    /// back from them never re-fires <c>Loaded</c> and a saved goal/habit would otherwise appear
    /// only after an app restart. <c>OnAppearing</c> is the one hook MAUI raises again on pop.
    /// The load is fire-and-forget because the platform hook is void — nothing blocks the UI
    /// thread and no task is ever awaited with .Result/.Wait().
    /// </summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is GoalsViewModel vm) _ = vm.LoadAsync();
    }
}
