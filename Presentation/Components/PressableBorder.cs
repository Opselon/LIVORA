using System.Windows.Input;
namespace LIVORA.Presentation.Components;

/// <summary>
/// Tap-to-act Border (Wave 3, lane 05): <c>Command</c> + <c>CommandParameter</c> with the same
/// press/hover micro-interaction language as <see cref="TappableFeedback"/> — shared state names
/// (Common: Normal / PointerOver / Pressed), shared depth defaults — so cards wrapped in either
/// behave identically. Complements TappableFeedback (which is pure feedback, no command):
/// use PressableBorder where the element IS the button; use TappableFeedback where a page just
/// wants pulse + hover on an element that already has its own handlers.
///
/// Why gestures in code: TapGestureRecognizer exposes no touch-down event, so a touch "press"
/// is a short pulse (Pressed → Normal after ~110 ms) while desktop pointer input gets true
/// press/hold/hover via PointerGestureRecognizer — exactly TappableFeedback's proven scheme.
///
/// Color/state policy: only Opacity/Scale/ZIndex are animated (VisualElement-typed, no brushes
/// on Color properties), and the command gate is checked before executing — a dead command
/// dims the whole element (0.55) and ignores input, it never fakes success.
///
/// Children that handle their own input (a Button inside) still win the touch first on every
/// platform; the border's recognizer only fires on bare-element taps.
/// </summary>
public class PressableBorder : Border
{
    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(PressableBorder), null,
            propertyChanged: OnCommandChanged);

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(PressableBorder), null);

    public static readonly BindableProperty PressScaleProperty =
        BindableProperty.Create(nameof(PressScale), typeof(double), typeof(PressableBorder), 0.98,
            propertyChanged: (b, o, n) => ((PressableBorder)b).EnsureStates());

    /// <summary>False = visual press feedback only, never executes the command (pure card polish
    /// on a Border that has its own handlers elsewhere). Default true = tap-to-act.</summary>
    public static readonly BindableProperty InvokeOnTapProperty =
        BindableProperty.Create(nameof(InvokeOnTap), typeof(bool), typeof(PressableBorder), true);

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public double PressScale
    {
        get => (double)GetValue(PressScaleProperty);
        set => SetValue(PressScaleProperty, value);
    }

    public bool InvokeOnTap
    {
        get => (bool)GetValue(InvokeOnTapProperty);
        set => SetValue(InvokeOnTapProperty, value);
    }

    private bool _held;
    private bool _hover;
    private bool _disabled;
    private int _pulseToken;
    private TapGestureRecognizer? _tap;
    private PointerGestureRecognizer? _pointer;

    public PressableBorder()
    {
        EnsureStates();

        _tap = new TapGestureRecognizer();
        _tap.Tapped += (_, _) => OnTapped();
        _pointer = new PointerGestureRecognizer();
        _pointer.PointerPressed += (_, _) => SetHeld(true);
        _pointer.PointerReleased += (_, _) => SetHeld(false);
        _pointer.PointerExited += (_, _) => SetHeld(false);
        _pointer.PointerEntered += (_, _) => { _hover = true; Update(); };
        _pointer.PointerExited += (_, _) => { _hover = false; Update(); };
        GestureRecognizers.Add(_tap);
        GestureRecognizers.Add(_pointer);

        UpdateCommandGate();
    }

    /// <summary>Execute + pulse, exposed so templates/behaviors can drive it programmatically.</summary>
    public void Invoke()
    {
        Pulse();
        var cmd = Command;
        if (InvokeOnTap && cmd?.CanExecute(CommandParameter) == true)
            cmd.Execute(CommandParameter);
    }

    private void OnTapped()
    {
        var cmd = Command;
        // Feedback only when the action would actually happen — a dead command must not look alive.
        if (cmd is not null && !cmd.CanExecute(CommandParameter)) return;
        Invoke();
    }

    private static void OnCommandChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var self = (PressableBorder)bindable;
        if (oldValue is ICommand oldCmd) oldCmd.CanExecuteChanged -= self.OnCanExecuteChanged;
        if (newValue is ICommand newCmd) newCmd.CanExecuteChanged += self.OnCanExecuteChanged;
        self.UpdateCommandGate();
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) => UpdateCommandGate();

    private void UpdateCommandGate()
    {
        var cmd = Command;
        bool live = cmd is null || cmd.CanExecute(CommandParameter);
        InputTransparent = !live;
        // Dead command rides its own visual state instead of poking Opacity directly: a local
        // value would fight every VSM transition (Normal/Pressed both set Opacity).
        _disabled = !live;
        Update();
    }

    private async void Pulse()
    {
        int token = ++_pulseToken;
        _held = true;
        Update();
        await Task.Delay(110);
        if (token != _pulseToken) return; // a newer pulse owns the release
        _held = false;
        Update();
    }

    private void SetHeld(bool held)
    {
        _held = held;
        Update();
    }

    private void Update()
    {
        string state = _disabled ? "Disabled"
            : _held ? "Pressed"
            : _hover ? "PointerOver" : "Normal";
        VisualStateManager.GoToState(this, state);
        ZIndex = _held ? 2 : 0;
    }

    private void EnsureStates()
    {
        double scale = PressScale;

        var normal = new VisualState { Name = "Normal" };
        normal.Setters.Add(new Setter { Property = VisualElement.OpacityProperty, Value = 1.0 });
        normal.Setters.Add(new Setter { Property = VisualElement.ScaleProperty, Value = 1.0 });

        var hover = new VisualState { Name = "PointerOver" };
        hover.Setters.Add(new Setter { Property = VisualElement.OpacityProperty, Value = 0.94 });
        hover.Setters.Add(new Setter { Property = VisualElement.ScaleProperty, Value = 1.0 });

        var pressed = new VisualState { Name = "Pressed" };
        pressed.Setters.Add(new Setter { Property = VisualElement.OpacityProperty, Value = 0.88 });
        pressed.Setters.Add(new Setter { Property = VisualElement.ScaleProperty, Value = scale });

        var disabled = new VisualState { Name = "Disabled" };
        disabled.Setters.Add(new Setter { Property = VisualElement.OpacityProperty, Value = 0.55 });
        disabled.Setters.Add(new Setter { Property = VisualElement.ScaleProperty, Value = 1.0 });

        VisualStateManager.SetVisualStateGroups(this, new VisualStateGroupList
        {
            new VisualStateGroup { Name = "Common", States = { normal, hover, pressed, disabled } }
        });
    }
}
