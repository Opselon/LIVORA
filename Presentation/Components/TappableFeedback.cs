namespace LIVORA.Presentation.Components;

/// <summary>
/// Reusable tap/press micro-interaction language for cards and rows, defined entirely in
/// Components (no new keys in the shared style dictionaries):
///
///   comp:TappableFeedback.Tappable="True"      -> builds a Common VisualStateGroup
///                                                  (Normal / PointerOver / Pressed) on the
///                                                  element with subtle scale + opacity, and
///                                                  drives it from touch + pointer input.
///   comp:TappableFeedback.PressScale="0.985"   -> optional custom press depth (default 0.98).
///
/// Why code and not a style key: MAUI's VisualStateManager on a Border needs states plus a
/// trigger — nothing in Controls fires "Pressed" for a plain Border/row automatically, and
/// TapGestureRecognizer in this MAUI version exposes no touch-down event. So we attach:
///  - a zero-handler TapGestureRecognizer: its Tapped event produces a short press pulse
///    (Pressed -> back after ~110ms). A recognizer with no command/ handler never consumes
///    input, so Buttons inside the element keep working untouched;
///  - a PointerGestureRecognizer: real PointerPressed/Released/Entered/Exited transitions
///    for mouse/pen on desktop (hover rides the same Pressed/Normal states).
/// A brief ZIndex lift keeps the element above its neighbours while pressed. Attach is
/// idempotent, so ItemsStackLayout template rebuilds are safe.
/// </summary>
public sealed class TappableFeedback
{
    public static readonly BindableProperty TappableProperty =
        BindableProperty.CreateAttached("Tappable", typeof(bool), typeof(TappableFeedback), false,
            propertyChanged: OnTappableChanged);

    public static readonly BindableProperty PressScaleProperty =
        BindableProperty.CreateAttached("PressScale", typeof(double), typeof(TappableFeedback), 0.98,
            propertyChanged: OnPressScaleChanged);

    public static bool GetTappable(BindableObject v) => (bool)v.GetValue(TappableProperty);
    public static void SetTappable(BindableObject v, bool value) => v.SetValue(TappableProperty, value);
    public static double GetPressScale(BindableObject v) => (double)v.GetValue(PressScaleProperty);
    public static void SetPressScale(BindableObject v, double value) => v.SetValue(PressScaleProperty, value);

    private static void OnTappableChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not View el) return; // gesture recognizers live on View
        if (newValue is true) Attach(el);
        else Detach(el);
    }

    /// <summary>PressScale can be set after Tappable in XAML — rebuild the states so the
    /// custom depth always wins, regardless of attribute order.</summary>
    private static void OnPressScaleChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is View el && GetTappable(el) && el.GetValue(TapProperty) is not null)
            EnsureStates(el);
    }

    private static void Attach(View el)
    {
        if (el.GetValue(TapProperty) is not null) return; // already attached (idempotent)
        EnsureStates(el);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Pulse(el);

        var pointer = new PointerGestureRecognizer();
        pointer.PointerPressed += (_, _) => SetHeld(el, true);
        pointer.PointerReleased += (_, _) => SetHeld(el, false);
        pointer.PointerExited += (_, _) => SetHeld(el, false);
        pointer.PointerEntered += (_, _) => { el.SetValue(HoverProperty, true); Update(el); };
        pointer.PointerExited += (_, _) => { el.SetValue(HoverProperty, false); Update(el); };

        el.GestureRecognizers.Add(tap);
        el.GestureRecognizers.Add(pointer);
        el.SetValue(TapProperty, tap);
        el.SetValue(PointerProperty, pointer);
    }

    private static void Detach(View el)
    {
        if (el.GetValue(TapProperty) is TapGestureRecognizer tap) el.GestureRecognizers.Remove(tap);
        if (el.GetValue(PointerProperty) is PointerGestureRecognizer pointer) el.GestureRecognizers.Remove(pointer);
        el.ClearValue(TapProperty);
        el.ClearValue(PointerProperty);
    }

    /// <summary>Touch press: enter the Pressed state, release after a short beat.
    /// A token guard makes rapid repeated taps converge on exactly one pending release.</summary>
    private static async void Pulse(View el)
    {
        int token = (int)el.GetValue(PulseTokenProperty) + 1;
        el.SetValue(PulseTokenProperty, token);
        el.SetValue(HeldProperty, true);
        Update(el);
        await Task.Delay(110);
        if (token != (int)el.GetValue(PulseTokenProperty)) return; // superseded by a newer pulse
        el.SetValue(HeldProperty, false);
        Update(el);
    }

    private static void SetHeld(View el, bool held)
    {
        el.SetValue(HeldProperty, held);
        Update(el);
    }

    private static void Update(View el)
    {
        bool held = (bool)el.GetValue(HeldProperty);
        bool hover = !held && (bool)el.GetValue(HoverProperty);
        string state = held ? "Pressed" : hover ? "PointerOver" : "Normal";
        VisualStateManager.GoToState(el, state);
        el.ZIndex = held ? 2 : 0;
    }

    private static void EnsureStates(View el)
    {
        double scale = GetPressScale(el);

        var normal = new VisualState { Name = "Normal" };
        normal.Setters.Add(new Setter { Property = VisualElement.OpacityProperty, Value = 1.0 });
        normal.Setters.Add(new Setter { Property = VisualElement.ScaleProperty, Value = 1.0 });

        var hover = new VisualState { Name = "PointerOver" };
        hover.Setters.Add(new Setter { Property = VisualElement.OpacityProperty, Value = 0.94 });
        hover.Setters.Add(new Setter { Property = VisualElement.ScaleProperty, Value = 1.0 });

        var pressed = new VisualState { Name = "Pressed" };
        pressed.Setters.Add(new Setter { Property = VisualElement.OpacityProperty, Value = 0.88 });
        pressed.Setters.Add(new Setter { Property = VisualElement.ScaleProperty, Value = scale });

        VisualStateManager.SetVisualStateGroups(el, new VisualStateGroupList
        {
            new VisualStateGroup { Name = "Common", States = { normal, hover, pressed } }
        });
    }

    // Private per-element storage: one attachment is enough and re-attach is a no-op.
    private static readonly BindableProperty TapProperty =
        BindableProperty.CreateAttached("TappableTap", typeof(TapGestureRecognizer), typeof(TappableFeedback), null);
    private static readonly BindableProperty PointerProperty =
        BindableProperty.CreateAttached("TappablePointer", typeof(PointerGestureRecognizer), typeof(TappableFeedback), null);
    private static readonly BindableProperty HeldProperty =
        BindableProperty.CreateAttached("TappableHeld", typeof(bool), typeof(TappableFeedback), false);
    private static readonly BindableProperty HoverProperty =
        BindableProperty.CreateAttached("TappableHover", typeof(bool), typeof(TappableFeedback), false);
    private static readonly BindableProperty PulseTokenProperty =
        BindableProperty.CreateAttached("TappablePulseToken", typeof(int), typeof(TappableFeedback), 0);
}
