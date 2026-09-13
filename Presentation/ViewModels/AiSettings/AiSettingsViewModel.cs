using System.ComponentModel;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Presentation;

/// <summary>
/// Wave 3c (lane 07) — AI settings surface: gateway status (exactly what
/// <see cref="IGatewayConfigService.GetPublicStatusAsync"/> reports — never more), the master
/// enable switch, an opt-in user key override, the four consent rows with three-state honesty,
/// the insecure-transport warning, and the static (honest) cloud-account card.
///
/// Honesty contract carried from the brief:
///  - "Verified" is only ever shown when the REAL status carries a LastVerifiedUtc; the formatted
///    date tells WHEN it was proven, and no probe means "Not verified" — never a hopeful word.
///  - The TEST-CONNECTION row uses the same static-delegate seam as AppShell.LogTabContent: the
///    integrator wires <see cref="ConnectionTester"/> to lane 02's provider probe in the
///    composition root. With no tester registered the row says so and the button is HIDDEN —
///    a hidden button cannot be tapped into a dead nothing.
///  - Cloud account is a READ-ONLY status card. No sign-in form is rendered at all: a form that
///    cannot talk to a backend is a fake, and fakes are product law violations.
///
/// Services resolve lazily through ServiceHelper.TryGet (the SettingsViewModel pattern): a wave-3
/// capability that is not registered in this build renders "not available" rows, never crashes
/// and never dead-buttons.
/// </summary>
public sealed class AiSettingsViewModel : ObservableObject
{
    /// <summary>
    /// INTEGRATOR HOOK — wire this in the composition root to lane 02's real provider probe
    /// (a live round-trip against the gateway; MUST NOT log the key, and any probe text it shows
    /// goes through localization keys, not raw provider prose):
    ///
    ///     AiSettingsViewModel.ConnectionTester = ct => ProbeGatewayAsync(ct);
    ///
    /// Left null (the default in this build until the probe merges), the Test-connection row
    /// renders an honest "no tester registered in this build" note and hides its button. The
    /// delegate must return true only after a REAL successful round-trip.
    /// </summary>
    public static Func<CancellationToken, Task<bool>>? ConnectionTester { get; set; }

    private readonly ILocalizationService _loc;
    private IFormatService? _format;
    private IGatewayConfigService? _gateway;
    private IConsentService? _consent;
    private ICloudAuthService? _cloud;

    private IFormatService? Fmt => _format ??= ServiceHelper.TryGet<IFormatService>();
    private IGatewayConfigService? Gateway => _gateway ??= ServiceHelper.TryGet<IGatewayConfigService>();
    private IConsentService? Consent => _consent ??= ServiceHelper.TryGet<IConsentService>();
    private ICloudAuthService? Cloud => _cloud ??= ServiceHelper.TryGet<ICloudAuthService>();

    public AiSettingsViewModel(ILocalizationService loc)
    {
        _loc = loc;
        SubscribeLanguage();

        TestConnectionCommand = new Command(async () => await TestConnectionAsync(),
            () => ConnectionTester is not null);
        SaveKeyCommand = new Command(async () => await SaveKeyAsync());
        ClearKeyCommand = new Command(async () => await ClearKeyAsync());
        BackCommand = new Command(async () => await GoBackAsync());

        ConsentRows = new List<ConsentRowViewModel>
        {
            new(this, ConsentCategory.AiProcessing, "AiSettings.Consent.AiProcessing", "AiSettings.Consent.AiProcessingNote"),
            new(this, ConsentCategory.HealthData, "AiSettings.Consent.HealthData", "AiSettings.Consent.HealthDataNote"),
            new(this, ConsentCategory.ActivityData, "AiSettings.Consent.ActivityData", "AiSettings.Consent.ActivityDataNote"),
            new(this, ConsentCategory.Notifications, "AiSettings.Consent.Notifications", "AiSettings.Consent.NotificationsNote"),
        };
    }

    public string Title => L("AiSettings.Title");
    public string StatusHeader => L("AiSettings.Status.Header");
    public string AdvancedNote => L("AiSettings.AdvancedNote");
    public string EnableLabel => L("AiSettings.Enable");
    public string EnableNote => L("AiSettings.EnableNote");
    public string TestConnectionLabel => L("AiSettings.TestConnection");
    public string TestUnavailableNote => L("AiSettings.TestUnavailableNote");
    public string KeyHeader => L("AiSettings.Key.Header");
    public string KeyNote => L("AiSettings.Key.Note");
    public string KeyPlaceholder => L("AiSettings.Key.Placeholder");
    public string KeySaveLabel => L("Common.Save");
    public string KeyClearLabel => L("AiSettings.Key.Clear");
    public string ConsentHeader => L("AiSettings.Consent.Header");
    public string ConsentNote => L("AiSettings.Consent.Note");
    public string CloudHeader => L("AiSettings.Cloud.Header");
    public string NotAvailable => L("Common.NotAvailable");
    public string BackText => L("Common.Back");
    public string FailedMessage => L("AiSettings.Failed");

    // ---- Gateway status (what GetPublicStatusAsync said, nothing more) ----------

    private bool _hasStatus;
    public bool HasGatewayService => Gateway is not null;
    public bool HasStatus => _hasStatus;
    public bool NoGatewayNoteVisible => Gateway is null;

    private bool _isConfigured;
    private bool _isEnabled;
    private bool _insecure;
    private string _model = string.Empty;
    private string _keySource = string.Empty;
    private DateTime? _lastVerifiedUtc;
    private bool _lastProbeOk;

    public bool IsConfigured => _isConfigured;
    public bool IsInsecureTransport => _insecure;
    /// <summary>Machine tag from the contract (e.g. "coding") — shown verbatim, never localized.</summary>
    public string ModelText => _model.Length == 0 ? L("Common.NotAvailable") : _model;
    /// <summary>Key origin: mapped to a key for the three documented values, else verbatim.</summary>
    public string KeySourceText => MapKeySource(_keySource);
    public bool ConfiguredChipVisible => _hasStatus;
    public string ConfiguredText => L(_isConfigured ? "AiSettings.Status.Configured" : "AiSettings.Status.NotConfigured");
    public string VerificationText => _lastVerifiedUtc is { } utc && _lastProbeOk
        ? L("AiSettings.Status.VerifiedAt", Fmt?.LongDate(utc.ToLocalTime()) ?? string.Empty,
            Fmt?.Time(utc.ToLocalTime().TimeOfDay) ?? string.Empty)
        : L("AiSettings.Status.NotVerified");

    private static string MapKeySource(string raw) => raw switch
    {
        "Built-in (obfuscated)" => T("AiSettings.KeySource.BuiltIn"),
        "Your own key" => T("AiSettings.KeySource.User"),
        "None" => T("AiSettings.KeySource.None"),
        // Unknown machine value: show it verbatim rather than guess a meaning for it.
        var other when other.Length > 0 => other,
        _ => T("Common.NotAvailable"),
    };

    // ---- Enable switch -----------------------------------------------------------

    public bool Enabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value || Gateway is null) return;
            _ = SetEnabledSafeAsync(value);
        }
    }

    private async Task SetEnabledSafeAsync(bool value)
    {
        var gw = Gateway;
        if (gw is null) return;
        IsBusy = true;
        try
        {
            await gw.SetEnabledAsync(value);
            _isEnabled = value;
            Raise(nameof(Enabled));
            await RefreshStatusQuietAsync();
        }
        catch { await NotifyAsync(Title, T("AiSettings.Failed")); }
        finally { IsBusy = false; }
    }

    // ---- User key override ---------------------------------------------------------

    private string _keyDraft = string.Empty;
    public string KeyDraft { get => _keyDraft; set => Set(ref _keyDraft, value); }
    public bool KeyActionsVisible => Gateway is not null;

    public Command TestConnectionCommand { get; }
    public Command SaveKeyCommand { get; }
    public Command ClearKeyCommand { get; }

    private async Task SaveKeyAsync()
    {
        var gw = Gateway;
        if (gw is null) return;
        var key = _keyDraft.Trim();
        if (key.Length == 0) { await NotifyAsync(Title, T("AiSettings.Key.Empty")); return; }
        IsBusy = true;
        try
        {
            await gw.SetUserKeyOverrideAsync(key);
            KeyDraft = string.Empty;
            await RefreshStatusQuietAsync();
            await NotifyAsync(Title, T("AiSettings.Key.Saved"));
        }
        catch { await NotifyAsync(Title, T("AiSettings.Failed")); }
        finally { IsBusy = false; }
    }

    private async Task ClearKeyAsync()
    {
        var gw = Gateway;
        if (gw is null) return;
        IsBusy = true;
        try
        {
            await gw.SetUserKeyOverrideAsync(null); // null = back to the built-in (obfuscated) key
            KeyDraft = string.Empty;
            await RefreshStatusQuietAsync();
            await NotifyAsync(Title, T("AiSettings.Key.Cleared"));
        }
        catch { await NotifyAsync(Title, T("AiSettings.Failed")); }
        finally { IsBusy = false; }
    }

    // ---- Test connection (static delegate seam) -------------------------------------

    public bool TestButtonVisible => Gateway is not null && ConnectionTester is not null;
    public bool TestUnavailableVisible => Gateway is not null && ConnectionTester is null;

    private async Task TestConnectionAsync()
    {
        var tester = ConnectionTester;
        if (tester is null || Gateway is null) return; // command gate already hides the button
        IsBusy = true;
        bool ok = false;
        try
        {
            ok = await tester(CancellationToken.None);
        }
        catch { ok = false; }
        finally { IsBusy = false; }

        await RefreshStatusQuietAsync();
        await NotifyAsync(Title, T(ok ? "AiSettings.Test.Ok" : "AiSettings.Test.Fail"));
    }

    // ---- Consent rows ---------------------------------------------------------------

    public IReadOnlyList<ConsentRowViewModel> ConsentRows { get; }
    /// <summary>Named accessors: an index path inside a compiled binding is fragile at merge
    /// time, and the row set is a fixed four — so the XAML binds these instead of [i].</summary>
    public ConsentRowViewModel AiProcessingRow => ConsentRows[0];
    public ConsentRowViewModel HealthDataRow => ConsentRows[1];
    public ConsentRowViewModel ActivityDataRow => ConsentRows[2];
    public ConsentRowViewModel NotificationsRow => ConsentRows[3];
    public bool HasConsentService => Consent is not null;
    public string ConsentUnavailableNote => L("Common.NotAvailable");

    internal async Task SetConsentAsync(ConsentCategory category, ConsentDecision decision)
    {
        var svc = Consent;
        if (svc is null) return;
        try
        {
            await svc.SetAsync(category, decision);
            await RefreshConsentQuietAsync();
        }
        catch { await NotifyAsync(Title, T("AiSettings.Failed")); }
    }

    // ---- Cloud account (read-only, honest) ------------------------------------------

    public bool HasCloudService => Cloud is not null;
    public bool CloudConfigured => Cloud?.IsBackendConfigured == true;
    /// <summary>Chip text: configured / not configured / not available — read from the seam.</summary>
    public string CloudStateText => Cloud is null
        ? L("Common.NotAvailable")
        : L(Cloud.IsBackendConfigured ? "AiSettings.Cloud.Configured" : "AiSettings.Cloud.NotConfigured");
    /// <summary>The service's own reason key, localized; empty when nothing to explain.</summary>
    public string CloudReasonText
    {
        get
        {
            var svc = Cloud;
            if (svc is null || svc.IsBackendConfigured) return string.Empty;
            var key = svc.StatusReasonKey;
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            var value = _loc[key];
            return value == $"[{key}]" ? key : value; // unresolved machine key shown as-is, no crash
        }
    }

    // ---- Load / refresh ---------------------------------------------------------------

    public async Task LoadAsync()
    {
        await RefreshStatusQuietAsync();
        await RefreshConsentQuietAsync();
        RaiseAll();
    }

    private async Task RefreshStatusQuietAsync()
    {
        var gw = Gateway;
        if (gw is null) { _hasStatus = false; return; }
        try
        {
            var s = await gw.GetPublicStatusAsync();
            _hasStatus = true;
            _isConfigured = s.IsConfigured;
            _isEnabled = s.IsEnabled;
            _insecure = s.UsesInsecureTransport;
            _model = s.Model ?? string.Empty;
            _keySource = s.KeySource ?? string.Empty;
            _lastVerifiedUtc = s.LastVerifiedUtc;
            _lastProbeOk = s.LastProbeOk;
        }
        catch
        {
            _hasStatus = false; // unreadable status: say nothing was proven, never show stale green
        }
    }

    private async Task RefreshConsentQuietAsync()
    {
        var svc = Consent;
        if (svc is null) return;
        try
        {
            var all = await svc.GetAllAsync();
            foreach (var row in ConsentRows) row.Apply(all.GetValueOrDefault(row.Category, ConsentDecision.Untouched));
        }
        catch { /* rows keep their last honest state */ }
    }

    // ---- Busy / plumbing -----------------------------------------------------------------

    private bool _isBusy;
    /// <summary>Drives <c>BaseContentPage.IsLoading</c> (dim + spinner + input lock).</summary>
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); Raise(nameof(IsNotBusy)); } }
    public bool IsNotBusy => !_isBusy;

    public Command BackCommand { get; }

    /// <summary>Same pop-first, hub-fallback shape SettingsViewModel uses (second-level page).</summary>
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

    private static string T(string key, params object[] args)
    {
        var loc = ServiceHelper.TryGet<ILocalizationService>();
        if (loc is null) return $"[{key}]";
        return args.Length == 0 ? loc[key] : loc.T(key, args);
    }

    private static async Task NotifyAsync(string title, string body)
    {
        var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return;
        try { await page.DisplayAlertAsync(title, body, T("Common.Done")); }
        catch { /* a failed dialog must never escalate */ }
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(HasGatewayService), nameof(HasStatus), nameof(NoGatewayNoteVisible),
            nameof(IsConfigured), nameof(ConfiguredChipVisible), nameof(ConfiguredText),
            nameof(VerificationText), nameof(ModelText), nameof(KeySourceText), nameof(IsInsecureTransport),
            nameof(Enabled), nameof(KeyActionsVisible),
            nameof(TestButtonVisible), nameof(TestUnavailableVisible),
            nameof(HasConsentService), nameof(HasCloudService), nameof(CloudConfigured),
            nameof(CloudStateText), nameof(CloudReasonText),
        }) Raise(name);
        foreach (var row in ConsentRows) row.RaiseAll();
    }

    private static readonly string[] LocalizedProperties =
    {
        nameof(Title), nameof(StatusHeader), nameof(AdvancedNote), nameof(EnableLabel),
        nameof(EnableNote), nameof(TestConnectionLabel), nameof(TestUnavailableNote),
        nameof(KeyHeader), nameof(KeyNote), nameof(KeyPlaceholder), nameof(KeySaveLabel),
        nameof(KeyClearLabel), nameof(ConsentHeader), nameof(ConsentNote), nameof(CloudHeader),
        nameof(NotAvailable), nameof(BackText), nameof(FailedMessage),
        nameof(ConsentUnavailableNote), nameof(ConfiguredText), nameof(VerificationText),
        nameof(KeySourceText), nameof(CloudStateText), nameof(CloudReasonText),
    };

    protected override void OnLanguageChanged()
    {
        foreach (var key in LocalizedProperties) Raise(key);
        RaiseAll();
    }

    /// <summary>
    /// One consent row. Three-state honesty: <see cref="IsUntouched"/> renders a "never asked"
    /// sub-label — the switch sits off because untouched BEHAVES as denied, but the UI never
    /// claims the user said no. First touch records an explicit Granted/Denied decision.
    /// </summary>
    public sealed class ConsentRowViewModel : INotifyPropertyChanged
    {
        private readonly AiSettingsViewModel _owner;
        public ConsentCategory Category { get; }
        public string TitleKey { get; }
        public string NoteKey { get; }
        private ConsentDecision _decision = ConsentDecision.Untouched;
        private bool _suppress;

        internal ConsentRowViewModel(AiSettingsViewModel owner, ConsentCategory category,
            string titleKey, string noteKey)
        {
            _owner = owner;
            Category = category;
            TitleKey = titleKey;
            NoteKey = noteKey;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public string Title => T(TitleKey);
        public string Note => T(NoteKey);
        public bool IsUntouched => _decision == ConsentDecision.Untouched;
        public string UntouchedLabel => T("AiSettings.Consent.NeverAsked");
        public bool ShowUntouchedLabel => IsUntouched;

        public bool IsToggled
        {
            get => _decision == ConsentDecision.Granted;
            set
            {
                if (_suppress) return;
                var target = value ? ConsentDecision.Granted : ConsentDecision.Denied;
                if (_decision == target) return;
                _decision = target;
                Raise(nameof(IsToggled));
                Raise(nameof(IsUntouched));
                Raise(nameof(ShowUntouchedLabel));
                _ = _owner.SetConsentAsync(Category, target);
            }
        }

        internal void Apply(ConsentDecision decision)
        {
            _suppress = true;
            _decision = decision;
            _suppress = false;
            RaiseAll();
        }

        internal void RaiseAll()
        {
            Raise(nameof(IsToggled));
            Raise(nameof(IsUntouched));
            Raise(nameof(ShowUntouchedLabel));
            Raise(nameof(UntouchedLabel));
            Raise(nameof(Title));
            Raise(nameof(Note));
        }
    }
}
