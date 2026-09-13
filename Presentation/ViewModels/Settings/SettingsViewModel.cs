using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Presentation;

/// <summary>
/// Second-level Settings page: the places-to-tune things that are not identity. Notification
/// grant (with a real RequestGrantAsync button that reports exactly what the OS answered, and
/// "Open system settings" via AppInfo.ShowSettingsUI when denied), theme mode, language,
/// check-for-updates, reminders shortcut, the sample-data disclosure, and About (version +
/// "What's new" replay through ISettingsService.LastSeenVersion).
///
/// Every row either works or says it does not: Wave 3 services (lanes 01/05/09) resolve lazily
/// through ServiceHelper.TryGet, so a not-yet-registered capability renders an honest
/// "not available in this build" instead of a dead button.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    // WAVE3B-SETTINGS-VM: lane APPEND blocks add services/properties/commands here.
    // WAVE3B-SETTINGS-VM-END

    private readonly ILocalizationService _loc;
    private readonly ISettingsService _settings;

    private IThemeService? _theme;
    private IReminderService? _reminders;

    private IThemeService? ThemeSvc => _theme ??= ServiceHelper.TryGet<IThemeService>();
    private IReminderService? ReminderSvc => _reminders ??= ServiceHelper.TryGet<IReminderService>();

    public SettingsViewModel(
        ILocalizationService loc,
        ISettingsService settings)
    {
        _loc = loc;
        _settings = settings;
        SubscribeLanguage();

        // Same banner sourcing as Profile: DI first (lane 01), direct construction as the
        // standalone fallback (see lane 06 NOTES for the merge rule).
        Banner = ServiceHelper.TryGet<UpdateBannerViewModel>() ?? new UpdateBannerViewModel();

        SetEnglishCommand = new Command(() => SetLanguage(AppLanguage.English));
        SetPersianCommand = new Command(() => SetLanguage(AppLanguage.Persian));
        SetThemeSystemCommand = new Command(() => SetTheme(ThemeMode.System));
        SetThemeLightCommand = new Command(() => SetTheme(ThemeMode.Light));
        SetThemeDarkCommand = new Command(() => SetTheme(ThemeMode.Dark));
        RequestGrantCommand = new Command(async () => await RequestGrantAsync());
        OpenSystemSettingsCommand = new Command(OpenSystemSettings);
        OpenRemindersCommand = new Command(() => NavigateRequested?.Invoke("reminders"));
        OpenUpdatesCommand = new Command(() => NavigateRequested?.Invoke("updates"));
        CheckNowCommand = new Command(async () => await Banner.RefreshAsync(force: true));
        WhatsNewCommand = new Command(ShowWhatsNew);

        BackCommand = new Command(async () => await GoBackAsync());
    }

    /// <summary>Lane 01's banner VM reused for the "check for updates" row (§Lane 06).</summary>
    public UpdateBannerViewModel Banner { get; }

    /// <summary>Route navigation is executed by the page (routes belong to other lanes).</summary>
    public event Action<string>? NavigateRequested;

    public Command SetEnglishCommand { get; }
    public Command SetPersianCommand { get; }
    public Command SetThemeSystemCommand { get; }
    public Command SetThemeLightCommand { get; }
    public Command SetThemeDarkCommand { get; }
    public Command RequestGrantCommand { get; }
    public Command OpenSystemSettingsCommand { get; }
    public Command OpenRemindersCommand { get; }
    public Command OpenUpdatesCommand { get; }
    public Command CheckNowCommand { get; }
    public Command WhatsNewCommand { get; }
    public Command BackCommand { get; }

    // ---- Labels -----------------------------------------------------------------

    public string Title => L("Settings.Title");
    public string NotificationsHeader => L("Settings.Notifications");
    public string NotificationsNote => L("Settings.NotificationsNote");
    /// <summary>Grant chip: the OS answer, "not available in this build", or "unknown".</summary>
    public string GrantText => ReminderSvc is null
        ? NotAvailable
        : _grantText.Length > 0 ? _grantText : L("Notification.State.Unknown");
    public string GrantRequestText => L("Notification.Grant");
    public string GrantOpenSettingsText => L("Notification.OpenSystemSettings");
    /// <summary>"Open system settings" only makes sense after a real denial.</summary>
    public bool ShowOpenSettingsButton => _grantState == NotificationGrantState.Denied;
    public bool HasReminderService => ReminderSvc is not null;
    public string RemindersEntryNote => L("Settings.RemindersNote");
    public string AppearanceHeader => L("Profile.Appearance");
    public string AppearanceNote => L("Settings.AppearanceNote");
    public string ThemeSystemText => L("Theme.System");
    public string ThemeLightText => L("Theme.Light");
    public string ThemeDarkText => L("Theme.Dark");
    public string LanguageHeader => L("Profile.Language");
    public string LanguageNote => L("Profile.LanguageNote");
    public string EnglishText => L("Onboarding.Language.English");
    public string PersianText => L("Onboarding.Language.Persian");
    public string UpdatesHeader => L("Settings.Updates");
    public string UpdatesNote => L("Settings.UpdatesNote");
    public string SampleDataHeader => L("Settings.SampleData");
    public string SampleDataNote => L("Settings.SampleDataNote");
    public string PrivacyEntryNote => L("Settings.PrivacyEntryNote");
    public string AboutHeader => L("Profile.About");
    public string VersionText => L("Profile.Version", AppInfo.Current.VersionString);
    public string WhatsNewText => L("Settings.WhatsNew");
    public string CheckedAtText => Banner.CheckedAtText;
    public string NotAvailable => L("Common.NotAvailable");
    public string OpenText => L("Common.Open");
    public string BackText => L("Common.Back");
    public string UnavailableMessage => L("Profile.NavUnavailable");
    /// <summary>The alert keeps the grant STATE, not its translated text, so a language switch
    /// after the dialog can't leave a stale-language word inside a fresh-language sentence.</summary>
    public string GrantResultMessage => _lastGrantResult is { } s
        ? L("Notification.GrantResult", L(NotificationGrantDisplay.KeyFor(s)))
        : L("Notification.GrantResult", L("Notification.State.Unknown"));

    // ---- Theme state (lane 05) ---------------------------------------------------

    public bool HasThemeControl => ThemeSvc is not null;
    public bool IsThemeSystem => ThemeSvc?.Mode == ThemeMode.System;
    public bool IsThemeLight => ThemeSvc?.Mode == ThemeMode.Light;
    public bool IsThemeDark => ThemeSvc?.Mode == ThemeMode.Dark;
    public Color ThemeSystemBackground => SegmentLook.Background(IsThemeSystem);
    public Color ThemeSystemForeground => SegmentLook.Foreground(IsThemeSystem);
    public Color ThemeLightBackground => SegmentLook.Background(IsThemeLight);
    public Color ThemeLightForeground => SegmentLook.Foreground(IsThemeLight);
    public Color ThemeDarkBackground => SegmentLook.Background(IsThemeDark);
    public Color ThemeDarkForeground => SegmentLook.Foreground(IsThemeDark);
    public string ThemeUnavailableNote => L("Profile.ThemeUnavailable");

    private void SetTheme(ThemeMode mode)
    {
        var svc = ThemeSvc;
        if (svc is null) return;
        svc.SetMode(mode);
        _settings.ThemeMode = mode;
        RaiseTheme();
    }

    private void RaiseTheme()
    {
        Raise(nameof(HasThemeControl));
        Raise(nameof(IsThemeSystem));
        Raise(nameof(IsThemeLight));
        Raise(nameof(IsThemeDark));
        Raise(nameof(ThemeSystemBackground));
        Raise(nameof(ThemeSystemForeground));
        Raise(nameof(ThemeLightBackground));
        Raise(nameof(ThemeLightForeground));
        Raise(nameof(ThemeDarkBackground));
        Raise(nameof(ThemeDarkForeground));
    }

    // ---- Language ---------------------------------------------------------------

    public bool IsEnglish => _loc.CurrentLanguage == AppLanguage.English;
    public bool IsPersian => _loc.CurrentLanguage == AppLanguage.Persian;
    public Color EnglishButtonBackground => SegmentLook.Background(IsEnglish);
    public Color EnglishButtonText => SegmentLook.Foreground(IsEnglish);
    public Color PersianButtonBackground => SegmentLook.Background(IsPersian);
    public Color PersianButtonText => SegmentLook.Foreground(IsPersian);

    private void SetLanguage(AppLanguage lang)
    {
        _loc.SetLanguage(lang);
        App.ApplyFlowDirection();
        RaiseLanguage();
    }

    private void RaiseLanguage()
    {
        Raise(nameof(IsEnglish));
        Raise(nameof(IsPersian));
        Raise(nameof(EnglishButtonBackground));
        Raise(nameof(EnglishButtonText));
        Raise(nameof(PersianButtonBackground));
        Raise(nameof(PersianButtonText));
    }

    // ---- Notification grant (honest, OS-answered) ---------------------------------

    private string _grantText = string.Empty;
    private NotificationGrantState? _grantState;
    private NotificationGrantState? _lastGrantResult; // stored as the STATE, re-resolved per language

    public async Task LoadAsync()
    {
        await RefreshGrantAsync();
        await Banner.LoadAsync();
        RaiseAll();
    }

    private async Task RefreshGrantAsync()
    {
        var rem = ReminderSvc;
        if (rem is null)
        {
            _grantText = string.Empty;
            _grantState = null;
            return;
        }
        try
        {
            _grantState = await rem.GetGrantStateAsync();
            _grantText = L(NotificationGrantDisplay.KeyFor(_grantState.Value));
        }
        catch
        {
            _grantState = null;
            _grantText = L("Profile.Status.CheckFailed");
        }
    }

    /// <summary>Ask the OS; then report exactly the state it returned (never a hopeful "allowed").</summary>
    private async Task RequestGrantAsync()
    {
        var rem = ReminderSvc;
        if (rem is null) return;
        IsBusy = true;
        try
        {
            NotificationGrantState state = await rem.RequestGrantAsync();
            _grantState = state;
            _grantText = L(NotificationGrantDisplay.KeyFor(state));
            _lastGrantResult = state;
            await AlertAsync(Title, GrantResultMessage);
        }
        catch
        {
            await AlertAsync(Title, L("Notification.GrantFailed"));
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(GrantText));
            Raise(nameof(ShowOpenSettingsButton));
        }
    }

    /// <summary>
    /// When the OS denied, the honest next step is its own settings page.
    /// MAUI's ShowSettingsUI() is fire-and-forget (void): it cannot tell us whether the platform
    /// opened anything, so we call it and never claim a result — a throw reports honestly.
    /// </summary>
    private void OpenSystemSettings()
    {
        try
        {
            AppInfo.ShowSettingsUI();
        }
        catch
        {
            _ = AlertAsync(Title, L("Notification.SystemSettingsUnavailable"));
        }
    }

    // ---- What's new replay (LastSeenVersion contract) -----------------------------

    /// <summary>
    /// "What's new" = open the updates page and mark the current build as seen, so lane 01's
    /// first-run banner logic stays consistent with an explicit read. No feed answer is invented:
    /// with no cached update info the row only navigates to the page that can fetch it.
    /// </summary>
    private void ShowWhatsNew()
    {
        _settings.LastSeenVersion = AppInfo.Current.VersionString;
        NavigateRequested?.Invoke("updates");
    }

    /// <summary>Settings is pushed from Profile: pop; if it somehow owns the window, go to the hub.</summary>
    private static async Task GoBackAsync()
    {
        try
        {
            var shell = Shell.Current;
            if (shell is not null)
            {
                if (shell.Navigation.ModalStack.Count > 0) { await shell.Navigation.PopModalAsync(); return; }
                if (shell.Navigation.NavigationStack.Count > 1) { await shell.Navigation.PopAsync(); return; }
                await shell.GoToAsync("../..");
                return;
            }
        }
        catch { /* fall through to the window-root reset below */ }

        if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page is AppShell)
        {
            // Only reached if Settings ever becomes a root page; the hub route is the honest target.
            try { await Shell.Current.GoToAsync("//Profile"); } catch { /* nothing else to try */ }
        }
    }

    private static async Task AlertAsync(string title, string body)
    {
        var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return;
        try
        {
            await page.DisplayAlertAsync(title, body,
                ServiceHelper.TryGet<ILocalizationService>()?["Common.Done"] ?? "OK");
        }
        catch { /* a failed dialog must never escalate */ }
    }

    private bool _isBusy;
    /// <summary>Drives <c>BaseContentPage.IsLoading</c> (dim + spinner + input lock).</summary>
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); Raise(nameof(IsNotBusy)); } }
    public bool IsNotBusy => !_isBusy;

    private void RaiseAll()
    {
        Raise(nameof(GrantText));
        Raise(nameof(ShowOpenSettingsButton));
        Raise(nameof(HasReminderService));
        RaiseTheme();
        RaiseLanguage();
    }

    private static readonly string[] LocalizedProperties =
    {
        nameof(Title), nameof(NotificationsHeader), nameof(NotificationsNote), nameof(GrantText),
        nameof(GrantRequestText), nameof(GrantOpenSettingsText), nameof(RemindersEntryNote),
        nameof(AppearanceHeader), nameof(AppearanceNote), nameof(ThemeSystemText),
        nameof(ThemeLightText), nameof(ThemeDarkText), nameof(ThemeUnavailableNote),
        nameof(LanguageHeader), nameof(LanguageNote), nameof(EnglishText), nameof(PersianText),
        nameof(UpdatesHeader), nameof(UpdatesNote), nameof(SampleDataHeader), nameof(SampleDataNote),
        nameof(PrivacyEntryNote), nameof(AboutHeader), nameof(VersionText), nameof(WhatsNewText),
        nameof(CheckedAtText), nameof(NotAvailable), nameof(OpenText), nameof(BackText),
        nameof(UnavailableMessage), nameof(GrantResultMessage),
    };

    protected override void OnLanguageChanged()
    {
        foreach (var key in LocalizedProperties) Raise(key);
        RaiseAll();
    }
}
