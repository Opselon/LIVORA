using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Log tab (lane 03). The check-in editor is the shared <see cref="CheckInView"/>
/// bound to the tab VM's own <c>Editor</c> instance, so tapping a recent row or the
/// Today chip drives exactly the same editor the pushed <c>log-entry</c> route shows.
/// </summary>
public partial class LogPage : BaseContentPage
{
    public LogPage() : base(ServiceHelper.Get<LogViewModel>())
    {
        InitializeComponent();

        if (BindingContext is LogViewModel vm)
        {
            Editor.BindEditor(vm.Editor);
            // Tap-to-edit (or the empty-state CTA) must bring the editor into view, or the row
            // tap reads as a dead control on a page this long.
            vm.EditorFocusRequested += () => _ = Scroller.ScrollToAsync(Editor, ScrollToPosition.Start, true);
            Loaded += async (_, _) =>
            {
                await vm.LoadAsync();
                // The chart paints device-independent units; make sure it has one pass
                // after the tab realized at its final size.
                SleepChart.RefreshChart();
            };
        }
    }
}
