using LIVORA.Presentation;

namespace LIVORA.Presentation.Views.Log;

/// <summary>
/// The shared daily check-in editor view (lane 03). Its BindingContext is the
/// <see cref="LogEntryViewModel"/> it is given — either the Log tab's own editor
/// instance or the one the pushed log-entry route resolves, so both flows render
/// the exact same implementation.
/// </summary>
public partial class CheckInView : ContentView
{
    public CheckInView()
    {
        InitializeComponent();
    }

    /// <summary>Hosts call this right after construction (and again on language-driven rebuilds).</summary>
    public void BindEditor(LogEntryViewModel editor) => BindingContext = editor;
}
