using LIVORA.Presentation;
using LIVORA.Presentation.Views;

namespace LIVORA.Presentation.Views;

/// <summary>Reminders (route `reminders`, lane 09). Reached from Today's quick actions and from
/// Profile's notifications entry. Loaded once per visit; pull data is cheap and re-synced.</summary>
public partial class RemindersPage : BaseContentPage
{
    public RemindersPage() : base(ServiceHelper.Get<RemindersViewModel>())
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (BindingContext is RemindersViewModel vm) await vm.LoadAsync();
        };
    }
}
