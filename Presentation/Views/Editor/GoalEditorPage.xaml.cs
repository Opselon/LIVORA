using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Goal editor, reachable two ways (lane 07 spec): by DI ctor — <c>new GoalEditorPage()</c> opens a
/// blank goal pre-filled from the profile's focus areas — and by Shell route
/// <c>goal-editor?mode=new</c> / <c>goal-editor?id=&lt;goalId&gt;</c>, where Shell hands the query
/// to <see cref="ApplyQueryAttributes"/> and the VM loads the stored goal by id.
/// </summary>
public partial class GoalEditorPage : BaseContentPage, IQueryAttributable
{
    private readonly GoalEditorViewModel _vm;

    public GoalEditorPage() : base(ServiceHelper.Get<GoalEditorViewModel>())
    {
        _vm = (GoalEditorViewModel)BindingContext;
        InitializeComponent();
    }

    /// <summary>Shell calls this right after constructing the page for a routed navigation —
    /// before it is ever shown, so the load starts here rather than racing OnAppearing.</summary>
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in query) args[k] = v?.ToString() ?? string.Empty;
        _ = _vm.ApplyQueryAsync(args);
    }
}
