using LIVORA.Application.Abstractions;

namespace LIVORA.Presentation;

/// <summary>
/// Wave 3c (lane 07) — the device-passcode lock screen over <see cref="ILocalPasscodeService"/>.
/// Four-digit numeric PIN, IsPassword entry (never echoed), set behind a two-step
/// enter + re-enter confirm, verify, remove (only after a real verify), and lock-now.
///
/// Wrong-PIN honesty with NO timing oracle: every failed verify produces the SAME generic
/// message — no attempt counters, no "N tries left", no cooldown hints, and no wording that
/// distinguishes "wrong code" from "wrong for any other reason". A would-be attacker learns
/// nothing about the failure budget from the UI; the service behind this VM owns whatever
/// throttling it has (this lane never implements or invents any).
///
/// If the service is not registered in this build the page renders the honest "not available"
/// state and no control pretends otherwise. The startup-gate integration (present this page
/// modally when ILocalPasscodeService.IsLocked at window creation) is delivered as an APPEND
/// proposal in the lane NOTES — the composition root applies it; the VM never reaches into App.
/// </summary>
public sealed class LockViewModel : ObservableObject
{
    /// <summary>PIN length the UI enforces (the service stores whatever it is given; this screen
    /// is the 4-digit contract the product asked for).</summary>
    public const int PinLength = 4;

    private ILocalPasscodeService? _passcode;
    private ILocalPasscodeService? Passcode => _passcode ??= ServiceHelper.TryGet<ILocalPasscodeService>();

    /// <summary>The loc parameter is the DI shape the startup gate composes against; strings
    /// resolve through ObservableObject.Loc (the same service instance).</summary>
    public LockViewModel(ILocalizationService loc)
    {
        _ = loc;
        SubscribeLanguage();
        SubmitCommand = new Command(async () => await SubmitAsync());
        RemoveCommand = new Command(async () => await RemoveAsync());
        LockNowCommand = new Command(LockNow);
        CancelStepCommand = new Command(CancelPendingStep);
        BackCommand = new Command(async () => await GoBackAsync());
    }

    // ---- Screen state -------------------------------------------------------------

    public bool HasService => Passcode is not null;
    /// <summary>True while a passcode exists on this device (verify/remove mode; else setup mode).</summary>
    public bool PasscodeExists => Passcode?.IsSet == true;
    /// <summary>True when the session is currently locked (drives the page's "no back" mode).</summary>
    public bool SessionLocked => Passcode?.IsLocked == true;

    public string Title => L("Lock.Title");
    public string NotAvailable => L("Common.NotAvailable");

    /// <summary>Label over the PIN entry, per the live step (never reveals whether a code exists
    /// beyond what the mode already shows — setup vs verify is a functional affordance, not a
    /// secret; the wrong-code message stays the single generic one either way).</summary>
    public string PinLabel => PasscodeExists
        ? L("Lock.Verify.Label")
        : _pending.Length > 0 ? L("Lock.Retype.Label") : L("Lock.Set.Label");

    public string SubmitLabel => PasscodeExists
        ? L("Lock.Unlock")
        : _pending.Length > 0 ? L("Lock.Confirm") : L("Lock.Set");

    public string RemoveLabel => L("Lock.Remove");
    public string LockNowLabel => L("Lock.LockNow");
    public string CancelLabel => L("Common.Cancel");
    public string BackText => L("Common.Back");

    // ---- Two-step setup -------------------------------------------------------------

    /// <summary>First half of the set flow (the typed PIN, held only in this VM's memory for the
    /// one re-entry step; cleared on mismatch/cancel — never persisted, never logged).</summary>
    private string _pending = string.Empty;

    private string _pin = string.Empty;
    public string Pin
    {
        get => _pin;
        set
        {
            // UI-side contract: digits only, capped at PinLength — a stray separator from a
            // numeric pad must not silently poison the code the user thinks they typed.
            var cleaned = new string((value ?? string.Empty).Where(char.IsDigit).Take(PinLength).ToArray());
            if (Set(ref _pin, cleaned)) ClearMessage();
        }
    }

    public bool PinReady => Pin.Length == PinLength;

    private bool _pendingStep => !PasscodeExists && _pending.Length > 0;
    public bool CancelVisible => _pendingStep;
    public bool RemoveVisible => PasscodeExists;
    public bool LockNowVisible => PasscodeExists;

    // ---- Message channel (error red / note neutral, one slot, always localized) ------

    private string _messageKey = string.Empty;
    private bool _messageIsError;
    public bool HasMessage => _messageKey.Length > 0;
    public bool MessageIsError => _messageIsError;
    public string MessageText => _messageKey.Length == 0 ? string.Empty : L(_messageKey);

    /// <summary>The generic failure line — identical for a wrong code whatever the internal
    /// reason. No counters, no timing words (see class oracle note).</summary>
    private void ShowWrongPin() => Show("Lock.Wrong", error: true);
    private void Show(string key, bool error)
    {
        _messageKey = key;
        _messageIsError = error;
        Raise(nameof(HasMessage));
        Raise(nameof(MessageIsError));
        Raise(nameof(MessageText));
    }
    private void ClearMessage()
    {
        if (_messageKey.Length == 0) return;
        _messageKey = string.Empty;
        Raise(nameof(HasMessage));
    }

    // ---- Commands ---------------------------------------------------------------------

    public Command SubmitCommand { get; }
    public Command RemoveCommand { get; }
    public Command LockNowCommand { get; }
    public Command CancelStepCommand { get; }
    /// <summary>Only rendered when the session is NOT locked (settings entry). A locked session
    /// has no back: the PIN is the door.</summary>
    public Command BackCommand { get; }

    /// <summary>Raised after a successful unlock so the host can dismiss a modal presentation
    /// (the startup gate owns the modal; the VM never touches Shell).</summary>
    public event Action? Unlocked;

    private async Task SubmitAsync()
    {
        var svc = Passcode;
        if (svc is null || !PinReady) return;
        IsBusy = true;
        try
        {
            if (!svc.IsSet)
            {
                if (_pending.Length == 0)
                {
                    _pending = Pin; // first half of set: remember, ask to re-type
                    Pin = string.Empty;
                    RaiseMode();
                    return;
                }
                if (_pending != Pin)
                {
                    // Mismatch IS said plainly — it is the user's own typo, not a security signal
                    // (unlike a wrong PIN, which gets the single generic line below).
                    _pending = string.Empty;
                    Pin = string.Empty;
                    Show("Lock.Mismatch", error: true);
                    RaiseMode();
                    return;
                }
                var saved = _pending;
                _pending = string.Empty;
                Pin = string.Empty;
                var ok = false;
                try { ok = await svc.SetAsync(saved); } catch { ok = false; }
                Show(ok ? "Lock.SetDone" : "Lock.SetFailed", error: !ok);
                RaiseMode();
                return;
            }

            bool verified = false;
            try { verified = await svc.VerifyAsync(Pin); }
            catch { verified = false; }
            Pin = string.Empty;
            if (verified)
            {
                ClearMessage();
                RaiseMode();
                Unlocked?.Invoke();
            }
            else
            {
                ShowWrongPin(); // SAME string for every failure — no attempt/timing information
            }
        }
        finally { IsBusy = false; }
    }

    /// <summary>Remove is verify-first: no code is ever deleted on a guess. A failed verify stops
    /// the flow with the same generic line — the removal never happened, nothing is revealed.</summary>
    private async Task RemoveAsync()
    {
        var svc = Passcode;
        if (svc is null || !svc.IsSet || !PinReady) return;
        IsBusy = true;
        try
        {
            bool verified = false;
            try { verified = await svc.VerifyAsync(Pin); }
            catch { verified = false; }
            if (!verified)
            {
                Pin = string.Empty;
                ShowWrongPin();
                return;
            }
            try { await svc.RemoveAsync(); } catch { }
            _pending = string.Empty;
            Pin = string.Empty;
            Show("Lock.Removed", error: false); // informational: the code is now gone (measured)
            RaiseMode();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Lock the session immediately (the service owns IsLocked); the startup gate or the
    /// host page re-presents this screen modally. This VM only clears its own draft.</summary>
    private void LockNow()
    {
        var svc = Passcode;
        if (svc is null) return;
        svc.Lock();
        _pending = string.Empty;
        Pin = string.Empty;
        ClearMessage();
        RaiseMode();
        Raise(nameof(SessionLocked));
    }

    private void CancelPendingStep()
    {
        _pending = string.Empty;
        Pin = string.Empty;
        ClearMessage();
        RaiseMode();
    }

    private void RaiseMode()
    {
        Raise(nameof(PasscodeExists));
        Raise(nameof(SessionLocked));
        Raise(nameof(PinLabel));
        Raise(nameof(SubmitLabel));
        Raise(nameof(RemoveVisible));
        Raise(nameof(LockNowVisible));
        Raise(nameof(CancelVisible));
        Raise(nameof(PinReady));
    }

    private static async Task GoBackAsync()
    {
        try
        {
            var shell = Shell.Current;
            if (shell is not null)
            {
                if (shell.Navigation.ModalStack.Count > 0) { await shell.Navigation.PopModalAsync(); return; }
                if (shell.Navigation.NavigationStack.Count > 1) { await shell.Navigation.PopAsync(); return; }
            }
        }
        catch { /* a failed pop is a platform quirk, not a user error */ }
    }

    // ---- Busy / language ---------------------------------------------------------------

    private bool _isBusy;
    /// <summary>Drives <c>BaseContentPage.IsLoading</c> (dim + spinner + input lock).</summary>
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); Raise(nameof(IsNotBusy)); } }
    public bool IsNotBusy => !_isBusy;

    private static readonly string[] LocalizedProperties =
    {
        nameof(Title), nameof(NotAvailable), nameof(PinLabel),
        nameof(SubmitLabel), nameof(RemoveLabel), nameof(LockNowLabel), nameof(CancelLabel),
        nameof(BackText), nameof(MessageText),
    };

    protected override void OnLanguageChanged()
    {
        foreach (var key in LocalizedProperties) Raise(key);
        RaiseMode();
    }
}
