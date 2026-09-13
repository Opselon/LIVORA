using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Habit editor, reachable by DI ctor (blank habit, cadence suggested from the profile) and by
/// Shell route <c>habit-editor?mode=new</c> / <c>habit-editor?id=&lt;habitId&gt;</c>.
/// </summary>
public partial class HabitEditorPage : BaseContentPage, IQueryAttributable
{
    private readonly HabitEditorViewModel _vm;

    public HabitEditorPage() : base(ServiceHelper.Get<HabitEditorViewModel>())
    {
        _vm = (HabitEditorViewModel)BindingContext;
        InitializeComponent();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in query) args[k] = v?.ToString() ?? string.Empty;
        _ = _vm.ApplyQueryAsync(args);
    }
}
