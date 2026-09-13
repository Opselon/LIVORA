using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Wave 3c (lane 07) — Data Studio page. Pushed via the "data-studio" shell route (APPEND block).
/// The first-entry raw-data warning is owned by the VM's session gate; declining it pops this page
/// through <see cref="DataStudioViewModel.BackRequested"/>, so the user never lands inside an
/// "advanced" screen they did not agree to enter.
///
/// Animation: finite per-card entrance fade only (Wave3cMotion: 140 ms, 20 ms stagger, once per
/// page instance). Zero System.Threading.Timer / DispatcherTimer usage here — nothing to clean up
/// on Disappearing, and no infinite loop runs behind the fade.
/// </summary>
public partial class DataStudioPage : BaseContentPage
{
    private bool _entrancePlayed;

    /// <summary>Shell-route constructor (routes resolve the page parameterless).</summary>
    public DataStudioPage() : this(ServiceHelper.Get<DataStudioViewModel>()) { }

    /// <summary>DI constructor — the §0.8 shape, used when the container injects the VM.</summary>
    public DataStudioPage(DataStudioViewModel vm) : base(vm)
    {
        InitializeComponent();

        // Declining the raw-data warning must back the user out — the gate is a real gate.
        vm.BackRequested += async () =>
        {
            try
            {
                var shell = Shell.Current;
                if (shell is not null && shell.Navigation.NavigationStack.Count > 1)
                    await shell.Navigation.PopAsync();
            }
            catch { /* if the pop fails the page stands, with its honest banner still on top */ }
        };

        var cards = new VisualElement[]
        {
            HeaderCard, BannerCard, UnavailableCard, EntityCard, ExportCard, ImportCard,
        };
        foreach (var c in cards) c.Opacity = 0; // the fade owns the reveal; no pre-fade flash

        Loaded += async (_, _) =>
        {
            var entered = false;
            try
            {
                entered = await vm.LoadAsync();
            }
            finally
            {
                // Reveal only when the user actually stayed inside the gate; if they declined,
                // BackRequested is already popping and a fade on a dying page is noise.
                if (entered && !_entrancePlayed)
                {
                    _entrancePlayed = true;
                    await Wave3cMotion.StaggerInAsync(cards);
                }
                else if (!entered)
                {
                    foreach (var c in cards) c.Opacity = 1; // restore truth for the pop frame
                }
            }
        };
    }
}
