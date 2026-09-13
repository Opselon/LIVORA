using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Pushed one-day check-in (route <c>log-entry</c>, optionally <c>?date=yyyy-MM-dd</c>).
/// Resolved by ctor through DI and driven by query args through
/// <see cref="LogEntryViewModel.ApplyQueryAttributes"/>; the editor is the exact same
/// <see cref="CheckInView"/> the Log tab embeds, so both flows share one implementation.
/// </summary>
public partial class LogEntryPage : BaseContentPage
{
    public LogEntryPage(LogEntryViewModel vm) : base(vm)
    {
        InitializeComponent();
        Editor.BindEditor(vm);
        vm.CloseRequested += Pop;
        Loaded += async (_, _) => await vm.LoadForDateAsync(vm.Date);
        Unloaded += (_, _) => vm.CloseRequested -= Pop;
    }

    private void Pop()
    {
        if (Navigation.NavigationStack.Count > 1) _ = Navigation.PopAsync();
    }
}
