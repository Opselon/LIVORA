using LIVORA.Presentation;

namespace LIVORA.Presentation.Views;

/// <summary>
/// Wave 3c (lane 07) — passcode screen page. Pushed from Settings via the "lock" route, and
/// (per the startup-gate APPEND block in the lane NOTES) presented modally when the session
/// starts locked. The page owns no logic: the VM resolves ILocalPasscodeService and every state
/// on screen is what that service actually reported.
///
/// Animation: finite per-card entrance fade only (Wave3cMotion — 140 ms, 20 ms stagger, once per
/// page instance). Zero System.Threading.Timer / DispatcherTimer usage in this file.
/// </summary>
public partial class LockPage : BaseContentPage
{
    private bool _entrancePlayed;

    /// <summary>Shell-route constructor (routes resolve the page parameterless).</summary>
    public LockPage() : this(ServiceHelper.Get<LockViewModel>()) { }

    /// <summary>DI constructor — the §0.8 shape, used when the container injects the VM.</summary>
    public LockPage(LockViewModel vm) : base(vm)
    {
        InitializeComponent();

        // The startup gate (App.CreateWindow APPEND block) presents this page modally and needs
        // to know when the PIN verified: forward the VM's one-shot event, page-side. Nothing
        // subscribes inside this page, so no leak: subscriber == host, lifetime == host's modal.
        vm.Unlocked += () => UnlockedByPasscode?.Invoke();

        var cards = new VisualElement[] { HeaderCard, UnavailableCard, PinCard };
        foreach (var c in cards) c.Opacity = 0; // the fade owns the reveal; no pre-fade flash

        Loaded += async (_, _) =>
        {
            if (!_entrancePlayed)
            {
                _entrancePlayed = true;
                await Wave3cMotion.StaggerInAsync(cards);
            }
        };
    }

    /// <summary>Raised when <see cref="LockViewModel"/> verified the code (gate dismisses itself).</summary>
    public event Action? UnlockedByPasscode;
}
