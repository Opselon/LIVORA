using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Wave 3c (lane 07) — AI settings page. Pushed via the "ai-settings" shell route (the lane's
/// APPEND block registers it next to the other second-level pages). Owns nothing beyond binding:
/// every row is the VM resolving a real service or honestly reporting that this build has none.
///
/// Animation: the cards start hidden (opacity 0, set here in the constructor — before the first
/// paint, so nothing flashes) and fade in ONCE per page instance through <see cref="Wave3cMotion"/>
/// (140 ms each, 20 ms stagger). No repeating animation and no periodic timer exists in this file
/// or in its VM — grep proof in the lane NOTES — so there is nothing to tear down on Disappearing:
/// the entrance is finite, and the fade is skipped entirely if a card set already played.
/// </summary>
public partial class AiSettingsPage : BaseContentPage
{
    private bool _entrancePlayed;

    /// <summary>Shell-route constructor (routes resolve the page parameterless).</summary>
    public AiSettingsPage() : this(ServiceHelper.Get<AiSettingsViewModel>()) { }

    /// <summary>DI constructor — the §0.8 shape, used when the container injects the VM.</summary>
    public AiSettingsPage(AiSettingsViewModel vm) : base(vm)
    {
        InitializeComponent();

        var cards = Cards();
        foreach (var c in cards) c.Opacity = 0; // the fade owns the reveal; no pre-fade flash

        Loaded += async (_, _) =>
        {
            try
            {
                await vm.LoadAsync();
            }
            finally
            {
                // The reveal is unconditional: a slow or failing load must never strand the page
                // invisible. LoadAsync itself reports honestly, so what shows is still the truth.
                if (!_entrancePlayed)
                {
                    _entrancePlayed = true;
                    await Wave3cMotion.StaggerInAsync(cards);
                }
            }
        };
    }

    private VisualElement[] Cards() => new VisualElement[]
    {
        HeaderCard, StatusCard, InsecureCard, KeyCard, ConsentCard, CloudCard,
    };
}
